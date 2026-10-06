using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace FRPMonitor.Cloud;

internal static class CloudProbe
{
    public static int Run(string output, int seconds) => RunAsync(output, Math.Clamp(seconds, 10, 60)).GetAwaiter().GetResult();
    private static async Task<int> RunAsync(string output, int seconds)
    {
        var monitor = new CloudMonitor(CloudProfile.Load());
        try
        {
            monitor.Start(); await Task.Delay(TimeSpan.FromSeconds(seconds));
            var end = DateTimeOffset.Now;
            var points = monitor.History.Snapshot(TimeSpan.FromMinutes(5), end);
            var frame = monitor.Current;
            var result = frame.Ready && points.Count >= 5 ? "PASS" : "FAIL";
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            File.WriteAllText(output, JsonSerializer.Serialize(new { result, frame.Message, frame.Interfaces, Samples = points.Count, Points = points.Select(p => new { p.Time, UploadMbps = Units.Megabits(p.Upload), DownloadMbps = Units.Megabits(p.Download), p.Seconds }) }, new JsonSerializerOptions { WriteIndented = true }));
            if (points.Count > 0) CloudExporter.Export(Path.ChangeExtension(output, ".xlsx"), points, TimeSpan.FromMinutes(5), end, true);
            return result == "PASS" ? 0 : 1;
        }
        finally { await monitor.StopAsync(); }
    }
}
