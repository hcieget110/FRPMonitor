using System;
using System.Collections.Generic;
using System.Linq;

namespace FRPMonitor;

public sealed record SeriesStatistics(double Minimum, double Average, double Maximum, double Last);
public sealed record TrendPoint(DateTimeOffset Start, DateTimeOffset End, TrafficPoint Point, double UploadMaximum, double DownloadMaximum);

public static class ChartData
{
    public static SeriesStatistics? Statistics(IEnumerable<TrafficPoint> points, bool upload)
    {
        double minimum = double.PositiveInfinity, maximum = 0, weighted = 0, seconds = 0, last = 0;
        DateTimeOffset? latest = null;
        foreach (var p in points)
        {
            if (p.Seconds <= 0) continue;
            var value = upload ? p.Upload : p.Download;
            minimum = Math.Min(minimum, value); maximum = Math.Max(maximum, value);
            weighted += value * p.Seconds; seconds += p.Seconds;
            if (!latest.HasValue || p.Time >= latest.Value) { latest = p.Time; last = value; }
        }
        return seconds == 0 ? null : new(minimum, weighted / seconds, maximum, last);
    }

    public static double ScaleMaximum(double peakMegabits)
    {
        var target = Math.Max(0.01, peakMegabits * 1.08);
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(target)));
        return new[] { 1.0, 2, 2.5, 5, 10 }.First(n => n * magnitude >= target) * magnitude;
    }

    public static int TrendIntervalSeconds(TimeSpan range, double width)
    {
        var count = Math.Clamp((int)(width / 8), 24, 200);
        var target = range.TotalSeconds / count;
        return new[] { 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 14400, 28800 }
            .FirstOrDefault(seconds => seconds >= target, 28800);
    }

    // View-only aggregation. Split outages first, then weight each window by valid sampling time.
    // Persistent history, original statistics and exported data never use this reduced series.
    public static List<List<TrendPoint>> TrendSegments(IEnumerable<TrafficPoint> points, DateTimeOffset begin, TimeSpan range, double width)
    {
        var interval = TrendIntervalSeconds(range, width); var end = begin + range;
        var result = new List<List<TrendPoint>>();
        foreach (var segment in Split(points, begin, range))
        {
            var windows = new SortedDictionary<long, TrendWindow>();
            foreach (var point in segment)
            {
                if (point.Seconds <= 0) continue;
                var minute = point.IntervalSeconds >= 60;
                var sampleStart = minute ? point.Time : point.Time.AddSeconds(-point.Seconds);
                var sampleEnd = minute ? point.Time.AddSeconds(point.IntervalSeconds) : point.Time;
                var duration = (sampleEnd - sampleStart).TotalSeconds;
                var cursor = sampleStart < begin ? begin : sampleStart;
                var finish = sampleEnd > end ? end : sampleEnd;
                while (cursor < finish)
                {
                    var key = cursor.ToUnixTimeSeconds() / interval;
                    var windowStart = DateTimeOffset.FromUnixTimeSeconds(key * interval);
                    var windowEnd = windowStart.AddSeconds(interval);
                    var stop = windowEnd < finish ? windowEnd : finish;
                    if (!windows.TryGetValue(key, out var window))
                        windows[key] = window = new() { Start = windowStart < begin ? begin : windowStart, End = windowEnd > end ? end : windowEnd, First = cursor, Last = stop };
                    var seconds = (stop - cursor).TotalSeconds * point.Seconds / duration;
                    window.Up += point.Upload * seconds; window.Down += point.Download * seconds; window.Seconds += seconds;
                    window.UpMaximum = Math.Max(window.UpMaximum, point.Upload); window.DownMaximum = Math.Max(window.DownMaximum, point.Download);
                    if (cursor < window.First) window.First = cursor; if (stop > window.Last) window.Last = stop;
                    cursor = stop;
                }
            }
            var reduced = windows.Values.Where(window => window.Seconds > 0).Select(window => new TrendPoint(window.First, window.Last,
                new(window.First + (window.Last - window.First) / 2, window.Up / window.Seconds, window.Down / window.Seconds, window.Seconds, interval), window.UpMaximum, window.DownMaximum)).ToList();
            if (reduced.Count > 0) result.Add(reduced);
        }
        return result;
    }
    private sealed class TrendWindow
    {
        public DateTimeOffset Start, End, First, Last;
        public double Up, Down, Seconds, UpMaximum, DownMaximum;
    }

    private static List<List<TrafficPoint>> Split(IEnumerable<TrafficPoint> points, DateTimeOffset begin, TimeSpan range)
    {
        var segments = new List<List<TrafficPoint>>(); TrafficPoint? previous = null;
        foreach (var point in points.Where(point => point.Time >= begin && point.Time <= begin + range).OrderBy(point => point.Time))
        {
            var tolerance = previous == null ? 0 : Math.Max(3, 1.5 * Math.Max(Math.Max(previous.IntervalSeconds, point.IntervalSeconds), Math.Max(previous.Seconds, point.Seconds)));
            if (previous == null || (point.Time - previous.Time).TotalSeconds > tolerance) segments.Add(new());
            segments[^1].Add(point); previous = point;
        }
        return segments;
    }

    // Split before reducing points so downtime never turns into a connecting line.
    public static List<List<TrafficPoint>> Segments(IEnumerable<TrafficPoint> points, DateTimeOffset begin, TimeSpan range, double width)
    {
        var segments = Split(points, begin, range);
        // Keep the endpoints and both directions' extrema in each screen column.
        // Averaging by screen width would hide short spikes in day/week views.
        return segments.Select(segment => segment
            .GroupBy(p => (long)((p.Time - begin).TotalSeconds / range.TotalSeconds * Math.Max(1, width)))
            .SelectMany(g => new[] { g.First(), g.MinBy(p => p.Upload)!, g.MaxBy(p => p.Upload)!,
                g.MinBy(p => p.Download)!, g.MaxBy(p => p.Download)!, g.Last() }.Distinct().OrderBy(p => p.Time))
            .ToList()).ToList();
    }
}
