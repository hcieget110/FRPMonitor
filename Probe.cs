using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace FRPMonitor;

public static class Probe
{
    public static int Run(string output, int seconds)
    {
        using var collector = new NetworkCollector(new Settings());
        collector.Start();
        var samples = new System.Collections.Generic.List<object>();
        Snapshot? latest = null;
        for (int i = 0; i < Math.Clamp(seconds, 2, 120); i++)
        {
            Thread.Sleep(1000); collector.Counters.RefreshProcesses();
            latest = collector.Counters.Sample();
            samples.Add(new { at = DateTimeOffset.Now, ready = collector.Ready, sample = latest });
            if (collector.Error != null) break;
        }
        var ready = collector.Ready;
        var result = new { result = ready ? "PASS" : "FAIL", collector.Error, lostEvents = ready ? collector.EventsLost : 0, totalUpload = latest?.TotalUp, totalDownload = latest?.TotalDown, samples };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return ready ? 0 : 1;
    }
}
