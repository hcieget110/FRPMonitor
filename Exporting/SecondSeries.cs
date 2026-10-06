using System;
using System.Collections.Generic;

namespace FRPMonitor.Exporting;

// Pure time-slot projection: missing samples remain null; a measured zero remains zero.
public static class SecondSeries
{
    public static TrafficPoint?[] Grid(IReadOnlyList<TrafficPoint> snapshot, TimeSpan range, DateTimeOffset end)
    {
        if (range <= TimeSpan.Zero || range > TimeSpan.FromMinutes(5) || range.TotalSeconds != Math.Floor(range.TotalSeconds))
            throw new ArgumentOutOfRangeException(nameof(range));
        var count = (int)range.TotalSeconds;
        var first = end.ToUnixTimeSeconds() - count + 1;
        var result = new TrafficPoint?[count];
        foreach (var point in snapshot)
        {
            if (point.IntervalSeconds != 1 || !double.IsFinite(point.Seconds) || point.Seconds <= 0 || point.Seconds > 2.5 ||
                !double.IsFinite(point.Upload) || !double.IsFinite(point.Download) || point.Upload < 0 || point.Download < 0) continue;
            var index = point.Time.ToUnixTimeSeconds() - first;
            if (index >= 0 && index < count) result[index] = point;
        }
        return result;
    }
}
