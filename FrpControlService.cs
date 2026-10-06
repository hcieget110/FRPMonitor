using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace FRPMonitor;

public enum FrpStartResult { Started, AlreadyRunning }

// Start the existing task so the account, config and proxy-free launcher stay consistent.
public sealed class FrpControlService
{
    public const string TaskName = "开启FRP";
    private readonly SemaphoreSlim startGate = new(1, 1);
    private readonly Func<bool> isRunning;
    private readonly Action launchTask;
    private readonly TimeSpan timeout;
    private readonly TimeSpan pollInterval;

    public FrpControlService() : this(IsClientRunning, LaunchScheduledTask, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(250)) { }
    internal FrpControlService(Func<bool> probe, Action launch, TimeSpan timeout, TimeSpan pollInterval)
    { isRunning = probe; launchTask = launch; this.timeout = timeout; this.pollInterval = pollInterval; }

    public static bool IsClientRunning()
    {
        var processes = Process.GetProcessesByName("frpc");
        // A process snapshot works for SYSTEM clients without opening their handles.
        try { return processes.Length > 0; }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    public async Task<FrpStartResult> StartAsync()
    {
        await startGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (await Task.Run(isRunning).ConfigureAwait(false)) return FrpStartResult.AlreadyRunning;
            await Task.Run(launchTask).ConfigureAwait(false);
            var watch = Stopwatch.StartNew();
            do
            {
                if (await Task.Run(isRunning).ConfigureAwait(false)) return FrpStartResult.Started;
                await Task.Delay(pollInterval).ConfigureAwait(false);
            } while (watch.Elapsed < timeout);
            throw new InvalidOperationException("已请求启动，但未检测到 frpc.exe。请查看任务「" + TaskName + "」和 FRP 启动日志；未将本次操作标记为已启动。");
        }
        finally { startGate.Release(); }
    }

    private sealed class ComScope : IDisposable
    {
        private readonly List<object> objects = new();
        public dynamic Keep(object value) { if (!objects.Exists(o => ReferenceEquals(o, value))) objects.Add(value); return value; }
        public void Dispose()
        { for (int i = objects.Count - 1; i >= 0; i--) if (Marshal.IsComObject(objects[i])) Marshal.FinalReleaseComObject(objects[i]); }
    }
    private static void LaunchScheduledTask()
    {
        using var scope = new ComScope();
        dynamic scheduler = scope.Keep(Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!);
        scheduler.Connect();
        dynamic folder = scope.Keep(scheduler.GetFolder("\\"));
        dynamic task;
        try { task = scope.Keep(folder.GetTask(TaskName)); }
        catch (Exception e) when (e.HResult == unchecked((int)0x80070002) || e.HResult == unchecked((int)0x8004130F))
        { throw new InvalidOperationException("未找到 FRP 启动任务「" + TaskName + "」，请先在任务计划程序中配置该任务。", e); }
        if (!(bool)task.Enabled) throw new InvalidOperationException("FRP 启动任务「" + TaskName + "」已禁用，请先在任务计划程序中启用。");
        int state = (int)task.State;
        // An existing queued/running task may still be creating the process. Wait for it.
        if (state is not (2 or 4)) scope.Keep(task.Run(null));
    }
}
