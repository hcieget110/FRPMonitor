using System;
using System.Collections.Generic;
using System.Linq;

namespace FRPMonitor;

public sealed class RateSmoother
{
    private readonly List<Snapshot> samples = new();
    public void Clear() => samples.Clear();
    public Snapshot Add(Snapshot latest, int windowSeconds)
    {
        if (latest.Seconds > 5) { Clear(); return latest; }
        windowSeconds = Math.Clamp(windowSeconds, 1, 5);
        samples.Add(latest);
        if (windowSeconds == 1) { Clear(); return latest; }
        while (samples.Count > 1 && samples.Skip(1).Sum(s => s.Seconds) >= windowSeconds) samples.RemoveAt(0);
        var duration = Math.Min(windowSeconds, samples.Sum(s => s.Seconds));
        var skipped = Math.Max(0, samples.Sum(s => s.Seconds) - duration);
        double up = 0, down = 0;
        var processUp = new Dictionary<int, double>(); var processDown = new Dictionary<int, double>();
        foreach (var item in samples)
        {
            var included = item.Seconds - Math.Min(skipped, item.Seconds); skipped = Math.Max(0, skipped - item.Seconds);
            var fraction = included / item.Seconds;
            up += item.UploadBytes * fraction; down += item.DownloadBytes * fraction;
            foreach (var p in item.Processes)
            {
                if (!latest.Processes.Any(active => active.Pid == p.Pid && active.Name == p.Name)) continue;
                processUp[p.Pid] = processUp.GetValueOrDefault(p.Pid) + p.Upload * included;
                processDown[p.Pid] = processDown.GetValueOrDefault(p.Pid) + p.Download * included;
            }
        }
        return new((long)Math.Round(up), (long)Math.Round(down), latest.TotalUp, latest.TotalDown, duration,
            latest.Processes.Select(p => new ProcessTraffic(p.Pid, p.Name, processUp.GetValueOrDefault(p.Pid) / duration, processDown.GetValueOrDefault(p.Pid) / duration)).ToList());
    }
}
