using System;
using System.Collections.Generic;
using System.Linq;

namespace FRPMonitor.Cloud;

public sealed class CloudHistory
{
    private readonly object gate = new();
    private readonly SortedDictionary<long, TrafficPoint> points = new();
    public void Add(TrafficPoint point)
    {
        if (point.Seconds <= 0 || point.Seconds > 2.5 || !double.IsFinite(point.Seconds) || !double.IsFinite(point.Upload) || !double.IsFinite(point.Download) || point.Upload < 0 || point.Download < 0) return;
        lock (gate) { points[point.Time.ToUnixTimeSeconds()] = point with { Time = DateTimeOffset.FromUnixTimeSeconds(point.Time.ToUnixTimeSeconds()) }; Prune(point.Time); }
    }
    private void Prune(DateTimeOffset now)
    {
        var cutoff = now.ToUnixTimeSeconds() - 300;
        while (points.Count > 0 && (points.First().Key <= cutoff || points.Count > 300)) points.Remove(points.First().Key);
    }
    public List<TrafficPoint> Snapshot(TimeSpan range, DateTimeOffset end)
    {
        if (range <= TimeSpan.Zero || range > TimeSpan.FromMinutes(5)) throw new ArgumentOutOfRangeException(nameof(range));
        lock (gate) { Prune(end); return points.Values.Where(p => p.Time > end.AddSeconds(-range.TotalSeconds) && p.Time <= end).ToList(); }
    }
    public void Clear() { lock (gate) points.Clear(); }
    public static TrafficPoint?[] Seconds(IReadOnlyList<TrafficPoint> snapshot, TimeSpan range, DateTimeOffset end)
    {
        if (range != TimeSpan.FromMinutes(1) && range != TimeSpan.FromMinutes(5)) throw new ArgumentOutOfRangeException(nameof(range));
        return Exporting.SecondSeries.Grid(snapshot, range, end);
    }
}

