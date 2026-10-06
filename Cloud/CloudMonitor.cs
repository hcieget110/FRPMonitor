using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;

namespace FRPMonitor.Cloud;

public sealed record CloudFrame(bool Ready, string Message, DateTimeOffset? Updated, string Interfaces);
public sealed class CloudMonitor
{
    public CloudHistory History { get; } = new();
    private CloudFrame frame = new(false, "未配置云主机 · 点击云主机设置", null, "");
    public CloudFrame Current => Volatile.Read(ref frame);
    private readonly CloudProfile profile;
    private readonly CancellationTokenSource stop = new();
    private Task? worker;
    private readonly object connectionGate = new();
    private SshClient? active;
    public CloudMonitor(CloudProfile profile) { this.profile = profile; if (!profile.Enabled && profile.Host.Length > 0) State(false, "云主机监控已关闭 · 点击设置启用"); }
    private void State(bool ready, string message, DateTimeOffset? updated = null, string interfaces = "") => Volatile.Write(ref frame, new(ready, message, updated, interfaces));
    public void Start() { if (!profile.Enabled) return; worker ??= Task.Run(Run); }
    public async Task StopAsync()
    {
        await Task.Run(() => { stop.Cancel(); lock (connectionGate) { active?.Dispose(); active = null; } }).ConfigureAwait(false);
        if (worker != null) try { await worker.ConfigureAwait(false); } catch (OperationCanceledException) { }
    }
    private async Task Run()
    {
        var retry = 1;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                State(false, "正在连接云主机…"); profile.Validate();
                var password = CloudCredential.Read(profile) ?? throw new InvalidOperationException("请在云主机设置中保存 SSH 密码");
                using var client = new SshClient(profile.Host, profile.Port, profile.User, password);
                client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(8); client.KeepAliveInterval = TimeSpan.FromSeconds(15);
                client.HostKeyReceived += (_, e) => e.CanTrust = profile.Trusts(e.FingerPrintSHA256);
                lock (connectionGate) { if (stop.IsCancellationRequested) return; active = client; }
                client.Connect(); stop.Token.ThrowIfCancellationRequested();
                using var command = client.CreateCommand(CloudLinuxSampler.Command);
                var execution = command.ExecuteAsync(stop.Token);
                _ = execution.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                using var reader = new StreamReader(command.OutputStream);
                long? remoteStart = null; var localStart = DateTimeOffset.Now;
                while (!stop.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(stop.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(6), stop.Token).ConfigureAwait(false);
                    if (line == null) throw new IOException("云端采样已结束，请确认 Linux 主机可运行 python3");
                    var packet = JsonSerializer.Deserialize<CloudPacket>(line) ?? throw new IOException("采样响应为空");
                    var remoteTime = packet.monotonic_ms;
                    if (remoteStart == null) { remoteStart = remoteTime; localStart = DateTimeOffset.Now; }
                    var at = localStart + TimeSpan.FromMilliseconds(remoteTime - remoteStart.Value);
                    if (packet.reset || !double.IsFinite(packet.seconds) || packet.seconds <= 0 || packet.seconds > 2.5) { State(false, "网卡计数重置或采样延迟 · 此间隔留空"); continue; }
                    if (!double.IsFinite(packet.up_bytes) || !double.IsFinite(packet.down_bytes) || packet.up_bytes < 0 || packet.down_bytes < 0) throw new IOException("采样计数无效");
                    History.Add(new(at, packet.up_bytes / packet.seconds, packet.down_bytes / packet.seconds, packet.seconds, 1));
                    State(true, "实时采集中 · " + packet.interfaces + " · 1 秒采样", DateTimeOffset.Now, packet.interfaces); retry = 1;
                }
                await execution.ConfigureAwait(false);
            }
            catch (Exception) when (stop.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                var reason = e is Renci.SshNet.Common.SshAuthenticationException ? "SSH 认证失败，请检查账户与密码"
                    : e is Renci.SshNet.Common.SshConnectionException ? "SSH 连接或主机密钥校验失败"
                    : e is InvalidOperationException or FormatException ? e.Message : e is TimeoutException ? "云端采样超时" : "云主机连接中断";
                State(false, reason + " · " + retry + " 秒后重连");
            }
            finally { lock (connectionGate) { active?.Dispose(); active = null; } }
            try { await Task.Delay(TimeSpan.FromSeconds(retry), stop.Token).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            retry = Math.Min(30, retry * 2);
        }
        State(false, "云端采集已停止");
    }
    private sealed class CloudPacket
    {
        public long monotonic_ms { get; set; }
        public double seconds { get; set; }
        public double up_bytes { get; set; }
        public double down_bytes { get; set; }
        public string interfaces { get; set; } = "";
        public bool reset { get; set; }
    }
}
