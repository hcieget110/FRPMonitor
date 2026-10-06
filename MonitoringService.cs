using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FRPMonitor;

public sealed record MonitorFrame(Snapshot Sample, bool Ready, string Message, long EventsLost, int RetryCount, DateTimeOffset? LastSample);
public sealed class MonitoringService
{
    private readonly History history;
    private readonly Func<IMonitorCollector> factory;
    private readonly TimeSpan interval, retryBase;
    private readonly CancellationTokenSource stop = new();
    private Task? worker;
    private int restartRequested, averageSeconds;
    private MonitorFrame current = new(new(0, 0, 0, 0, 1, new()), false, "正在启动采集…", 0, 0, null);
    public MonitorFrame Current => Volatile.Read(ref current);
    public MonitoringService(History store, Settings settings, Func<IMonitorCollector>? collectorFactory = null, TimeSpan? samplingInterval = null, TimeSpan? retryInterval = null)
    {
        history = store; averageSeconds = settings.RateAverageSeconds;
        var options = new Settings { ProcessNames = settings.ProcessNames, IncludeLoopback = settings.IncludeLoopback, ConnectionMode = settings.ConnectionMode, EndpointFilters = settings.EndpointFilters };
        factory = collectorFactory ?? (() => new NetworkCollector(options));
        interval = samplingInterval ?? TimeSpan.FromSeconds(1); retryBase = retryInterval ?? TimeSpan.FromSeconds(2);
    }
    public void Start() => worker ??= Task.Run(RunAsync);
    public void SetAverageSeconds(int value) => Volatile.Write(ref averageSeconds, value);
    public void RequestRestart() => Interlocked.Exchange(ref restartRequested, 1);
    private async Task RunAsync()
    {
        IMonitorCollector? collector = null;
        var smoother = new RateSmoother(); var previousAverage = averageSeconds;
        var clock = Stopwatch.StartNew();
        var retryAt = TimeSpan.Zero; var started = TimeSpan.Zero;
        var saveAt = TimeSpan.FromSeconds(30);
        var processIds = new HashSet<int>(); long previousLost = 0; bool recovered = false;
        history.Timeline.Add(DateTimeOffset.Now, "监控启动", "后台采集启动");
        var attempts = 0; var failureStreak = 0; long totalUp = 0, totalDown = 0;
        var collectorHasSample = false;
        DateTimeOffset? lastSample = null;
        string failure = "";
        using var timer = new PeriodicTimer(interval);
        try
        {
            do
            {
                var now = DateTimeOffset.Now;
                var elapsed = clock.Elapsed;
                if (Interlocked.Exchange(ref restartRequested, 0) != 0)
                { history.Timeline.Add(now, "重启采集", "用户请求重启采集"); CloseCollector(); retryAt = TimeSpan.Zero; smoother.Clear(); attempts = failureStreak = 0; }
                if (collector == null && elapsed >= retryAt)
                {
                    try { collectorHasSample = false; collector = factory(); started = elapsed; previousLost = 0; recovered = false; collector.Start(); }
                    catch (Exception e) { CloseCollector(); failure = e.Message; ScheduleRetry(); }
                }
                try
                {
                    if (collector?.Ready == true && collector.Error == null && elapsed - started >= interval / 2)
                    {
                        var raw = collector.Counters.Sample();
                        collectorHasSample = true;
                        totalUp += raw.UploadBytes; totalDown += raw.DownloadBytes;
                        raw = raw with { TotalUp = totalUp, TotalDown = totalDown };
                        history.Add(now, raw.UploadBytes, raw.DownloadBytes, raw.Seconds);
                        if (!recovered) { history.Timeline.Add(now, "采集就绪", attempts > 0 ? "采集故障后恢复" : "ETW 已就绪"); recovered = true; }
                        var active = raw.Processes.Select(p => p.Pid).ToHashSet();
                        foreach (var p in raw.Processes.Where(p => !processIds.Contains(p.Pid))) history.Timeline.Add(now, "发现进程", p.Name + " · PID " + p.Pid);
                        foreach (var pid in processIds.Except(active)) history.Timeline.Add(now, "进程退出", "PID " + pid + " 已离开监控范围");
                        processIds = active;
                        var average = Volatile.Read(ref averageSeconds);
                        if (average != previousAverage) { smoother.Clear(); previousAverage = average; }
                        var sample = smoother.Add(raw, average) with { Connections = raw.Connections };
                        lastSample = now;
                        if (elapsed - started > TimeSpan.FromSeconds(30)) failureStreak = 0;
                        var lost = collector.EventsLost;
                        if (lost > previousLost) history.Timeline.Add(now, "事件丢失", "新增丢失 " + (lost - previousLost) + " 个 ETW 事件，流量可能低估");
                        previousLost = lost;
                        var state = raw.Seconds > 5 ? "⚠ 采样延迟 " + raw.Seconds.ToString("F1") + " 秒；显示间隔平均，历史速率留空" : collector.Error != null ? "⚠ " + collector.Error : lost > 0 ? "⚠ 采集丢失 " + lost + " 个事件" : sample.Processes.Count == 0 ? "● FRP 未运行 · 等待进程" : "● 实时采集中 · " + sample.Processes.Count + " 个进程";
                        Volatile.Write(ref current, new(sample, true, state, lost, attempts, lastSample));
                    }
                    else if (collector != null && (collector.Error != null || elapsed - started > TimeSpan.FromSeconds(15)))
                    {
                        failure = collector.Error ?? "采集启动超时"; CloseCollector(); smoother.Clear(); ScheduleRetry();
                    }
                }
                catch (Exception e) { failure = e.Message; CloseCollector(); smoother.Clear(); ScheduleRetry(); }
                if (collector?.Ready != true)
                {
                    var message = collector != null ? "正在启动采集…" : "⚠ " + failure + " · " + Math.Max(0, (int)Math.Ceiling((retryAt - elapsed).TotalSeconds)) + "秒后重试";
                    var prior = Current.Sample;
                    Volatile.Write(ref current, new(new(0, 0, totalUp, totalDown, 1, prior.Processes) { Connections = prior.Connections }, false, message, Current.EventsLost, attempts, lastSample));
                }
                if (elapsed >= saveAt) { _ = history.SaveAsync(); saveAt = elapsed + TimeSpan.FromSeconds(30); }
                void ScheduleRetry()
                { attempts++; failureStreak++; history.Timeline.Add(now, "采集故障", failure); retryAt = clock.Elapsed + TimeSpan.FromMilliseconds(Math.Min(60000, retryBase.TotalMilliseconds * Math.Pow(2, Math.Min(failureStreak - 1, 5)))); }
            } while (await timer.WaitForNextTickAsync(stop.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally { CloseCollector(); history.Timeline.Add(DateTimeOffset.Now, "监控停止", "采集器已关闭，保存历史"); }
        void CloseCollector()
        {
            var closing = collector; collector = null;
            if (closing == null) return;
            // Closing and restarting a collector must not discard its final, undrained bytes.
            var capture = collectorHasSample || closing.Ready && closing.Error == null;
            try { closing.Dispose(); } catch (Exception e) { failure = "停止采集失败：" + e.Message; }
            if (capture)
            {
                var final = closing.Counters.Sample();
                totalUp += final.UploadBytes; totalDown += final.DownloadBytes;
                history.Add(DateTimeOffset.Now, final.UploadBytes, final.DownloadBytes, final.Seconds);
            }
            collectorHasSample = false;
        }
    }
    public async Task StopAsync()
    {
        stop.Cancel();
        try { if (worker != null) await worker.ConfigureAwait(false); }
        finally { await history.SaveAsync().ConfigureAwait(false); }
    }
}
