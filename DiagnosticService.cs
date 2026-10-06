using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace FRPMonitor;

public static class DiagnosticService
{
    public static Task ExportAsync(string path, Settings settings, MonitorFrame? frame, History history)
    {
        var now = DateTimeOffset.Now;
        var peaks = history.Peaks(TimeSpan.FromMinutes(5), now);
        var snapshot = new
        {
            Version = AppInfo.Version, GeneratedAt = now, OperatingSystem = Environment.OSVersion.VersionString,
            Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            ProcessNames = settings.Names, settings.IncludeLoopback, settings.ConnectionMode, settings.EndpointFilters,
            settings.RateAverageSeconds, settings.FixedScaleMbps, Collector = frame,
            StorageError = history.StorageError, RecentPeaks = new { UploadBytesPerSecond = peaks.Up, DownloadBytesPerSecond = peaks.Down, RecordedSeconds = peaks.Coverage },
            Coverage = history.Coverage(TimeSpan.FromDays(7), now), Events = history.Timeline.Snapshot(), SettingsWarning = settings.LoadWarning,
            Note = "连接信息来自监控进程的 ETW 事件。代理连接仅显示入口，不能据此证明最终路由或境外节点。"
        };
        return Task.Run(() => Disk.AtomicText(path, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true })));
    }
}
