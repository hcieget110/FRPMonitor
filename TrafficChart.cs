using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace FRPMonitor;

public sealed class TrafficChart : FrameworkElement
{
    private List<TrafficPoint> points = new();
    private TimeSpan range = TimeSpan.FromMinutes(5);
    private DateTimeOffset now = DateTimeOffset.Now;
    private Point? hover;
    private Point? selectionStart;
    private Rect plotRect;
    public event Action<DateTimeOffset, DateTimeOffset>? RangeSelected;
    public event Action? ResetRequested;
    private List<List<TrafficPoint>>? cachedSegments;
    private List<List<TrendPoint>>? cachedTrendSegments;
    private double cachedWidth;
    private DrawingGroup? cachedDrawing;
    private Size cachedRenderSize;
    private double cachedDpi;
    private bool cachedCompact;
    private double scaleMaximum = ChartData.ScaleMaximum(0);
    public double MaximumMbps => scaleMaximum;
    private DateTimeOffset scaleStableAt;
    public bool HoldScale { get; set; }
    public double FixedMaximumMbps { get; set; }
    public bool Compact { get; set; }
    public bool UseAggregation { get; set; } = true;
    public int AggregationSeconds => !UseAggregation || Compact || range <= TimeSpan.FromMinutes(5) ? 0 : ChartData.TrendIntervalSeconds(range, Math.Max(1, ActualWidth - 71));
    public TrafficChart()
    {
        MouseMove += (_, e) => { hover = e.GetPosition(this); InvalidateVisual(); };
        MouseLeave += (_, _) => { hover = null; InvalidateVisual(); };
        MouseLeftButtonDown += (_, e) =>
        {
            if (Compact || !plotRect.Contains(e.GetPosition(this))) return;
            if (e.ClickCount == 2) { ResetRequested?.Invoke(); e.Handled = true; return; }
            selectionStart = e.GetPosition(this); CaptureMouse(); e.Handled = true;
        };
        MouseLeftButtonUp += (_, e) =>
        {
            if (!selectionStart.HasValue) return;
            var first = selectionStart.Value.X; var last = e.GetPosition(this).X;
            selectionStart = null; ReleaseMouseCapture(); e.Handled = true; InvalidateVisual();
            if (Math.Abs(first - last) < 8) return;
            var interval = SelectionInterval(now, range, (first - plotRect.Left) / plotRect.Width, (last - plotRect.Left) / plotRect.Width);
            if (interval.End - interval.Start >= TimeSpan.FromSeconds(5)) RangeSelected?.Invoke(interval.Start, interval.End);
        };
        LostMouseCapture += (_, _) => { selectionStart = null; InvalidateVisual(); };
    }
    public static (DateTimeOffset Start, DateTimeOffset End) SelectionInterval(DateTimeOffset end, TimeSpan range, double first, double last)
    {
        var a = Math.Clamp(Math.Min(first, last), 0, 1); var b = Math.Clamp(Math.Max(first, last), 0, 1);
        return (end - range + TimeSpan.FromSeconds(range.TotalSeconds * a), end - range + TimeSpan.FromSeconds(range.TotalSeconds * b));
    }
    public void Update(List<TrafficPoint> data, TimeSpan period, DateTimeOffset end)
    {
        var periodChanged = range != period;
        points = data; range = period; now = end; cachedSegments = null; cachedTrendSegments = null; cachedDrawing = null;
        var target = ChartData.ScaleMaximum(points.Count == 0 ? 0 : Units.Megabits(points.Max(p => Math.Max(p.Upload, p.Download))));
        if (FixedMaximumMbps > 0) { scaleMaximum = FixedMaximumMbps; scaleStableAt = end; }
        else if (periodChanged || !HoldScale || target >= scaleMaximum) { scaleMaximum = target; scaleStableAt = end; }
        else if (end - scaleStableAt >= TimeSpan.FromSeconds(10)) { scaleMaximum = target; scaleStableAt = end; }
        InvalidateVisual();
    }
    private FormattedText Text(string text, Brush brush, double size = 11) => new(text,
        CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"),
        size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    private void Label(DrawingContext dc, string text, double x, double y, Brush brush, double size = 11, double align = 0)
    {
        var label = Text(text, brush, size);
        dc.DrawText(label, new(x - label.Width * align, y));
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ActualWidth < 120 || ActualHeight < (Compact ? 16 : 100)) return;
        var compactTimeAxis = Compact && ActualHeight >= 38;
        var left = 57.0;
        if (Compact)
            left = Math.Max(Text(scaleMaximum.ToString("0.##", CultureInfo.InvariantCulture), Theme.Muted, 8).Width,
                Text((scaleMaximum / 2).ToString("0.##", CultureInfo.InvariantCulture), Theme.Muted, 8).Width) + 5;
        var rect = new Rect(left, Compact ? 6 : 23, Math.Max(1, ActualWidth - left - (Compact ? 1 : 14)),
            Math.Max(1, ActualHeight - (Compact ? compactTimeAxis ? 18 : 10 : 60)));
        plotRect = rect;
        var begin = now - range;
        var max = scaleMaximum;
        var size = new Size(ActualWidth, ActualHeight);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (cachedDrawing == null || cachedRenderSize != size || cachedDpi != dpi || cachedCompact != Compact)
        {
            var drawing = new DrawingGroup();
            using (var context = drawing.Open()) DrawBase(context, rect, begin, max);
            drawing.Freeze(); cachedDrawing = drawing;
            cachedRenderSize = size; cachedDpi = dpi; cachedCompact = Compact;
        }
        // Pointer movement only draws the tooltip over the cached curves and axes.
        dc.DrawDrawing(cachedDrawing);
        if (selectionStart.HasValue && hover.HasValue)
        {
            var x1 = Math.Clamp(Math.Min(selectionStart.Value.X, hover.Value.X), rect.Left, rect.Right);
            var x2 = Math.Clamp(Math.Max(selectionStart.Value.X, hover.Value.X), rect.Left, rect.Right);
            dc.DrawRectangle(Theme.Brush("#4054D6BB"), new Pen(Theme.Upload, .8), new Rect(x1, rect.Top, x2 - x1, rect.Height));
        }
        else DrawHover(dc, rect, begin, max);
    }
    private void DrawBase(DrawingContext dc, Rect rect, DateTimeOffset begin, double max)
    {
        if (cachedSegments == null || cachedWidth != rect.Width || cachedCompact != Compact)
        {
            cachedTrendSegments = AggregationSeconds > 0 ? ChartData.TrendSegments(points, begin, range, rect.Width) : null;
            cachedSegments = cachedTrendSegments == null ? ChartData.Segments(points, begin, range, rect.Width) : cachedTrendSegments.Select(segment => segment.Select(point => point.Point).ToList()).ToList();
            cachedWidth = rect.Width;
        }
        var segments = cachedSegments;
        double X(TrafficPoint p) => rect.Left + (p.Time - begin).TotalSeconds / range.TotalSeconds * rect.Width;
        double Y(double value) => rect.Bottom - Units.Megabits(value) / max * rect.Height;
        if (Compact)
        {
            var grid = new Pen(Theme.Brush("#526276"), 0.5) { DashStyle = DashStyles.Dot };
            for (int i = 0; i <= 2; i++)
            {
                var y = rect.Top + i * rect.Height / 2;
                var label = Text((max * (2 - i) / 2).ToString("0.##", CultureInfo.InvariantCulture), Theme.Muted, 8);
                dc.DrawText(label, new Point(rect.Left - 5 - label.Width, y - label.Height / 2));
                if (i < 2) dc.DrawLine(grid, new Point(rect.Left, y), new Point(rect.Right, y));
            }
            dc.DrawLine(new Pen(Theme.Brush("#526276"), 0.5), new Point(rect.Left, rect.Bottom), new Point(rect.Right, rect.Bottom));
            if (ActualHeight >= 38)
            {
                var minutes = range.TotalMinutes;
                Label(dc, minutes.ToString("0.#", CultureInfo.InvariantCulture) + "分钟前", rect.Left, rect.Bottom + 1, Theme.Muted, 8);
                Label(dc, (minutes / 2).ToString("0.#", CultureInfo.InvariantCulture) + "分钟前", rect.Left + rect.Width / 2, rect.Bottom + 1, Theme.Muted, 8, 0.5);
                Label(dc, "现在", rect.Right, rect.Bottom + 1, Theme.Muted, 8, 1);
            }
        }
        if (!Compact)
        {
            dc.DrawRectangle(Theme.Brush("#101B2B"), null, rect);
            var grid = new Pen(Theme.Brush("#344356"), 0.7) { DashStyle = DashStyles.Dot };
            Label(dc, "Mb/s", rect.Left - 9, 0, Theme.Muted, 10, 1);
            for (int i = 0; i <= 4; i++)
            {
                var y = rect.Top + i * rect.Height / 4;
                dc.DrawLine(grid, new(rect.Left, y), new(rect.Right, y));
                Label(dc, (max * (4 - i) / 4).ToString("0.##", CultureInfo.InvariantCulture), rect.Left - 9, y - 7, Theme.Muted, 10, 1);
            }
            var divisions = range.TotalDays >= 2 ? 7 : 6;
            for (int i = 0; i <= divisions; i++)
            {
                var x = rect.Left + rect.Width * i / divisions;
                dc.DrawLine(grid, new(x, rect.Top), new(x, rect.Bottom));
                var time = (begin + TimeSpan.FromTicks(range.Ticks * i / divisions)).ToLocalTime();
                var label = time.ToString(range.TotalDays >= 2 ? "MM/dd" : range.TotalHours >= 24 ? "HH:mm" : "HH:mm:ss");
                Label(dc, label, x, rect.Bottom + 10, Theme.Muted, 10, i == 0 ? 0 : i == divisions ? 1 : 0.5);
            }
            dc.DrawRectangle(null, new Pen(Theme.Brush("#435266"), 0.8), rect);
        }
        dc.PushClip(new RectangleGeometry(rect));
        void DrawSeries(bool upload, SolidColorBrush color)
        {
            var geometry = new StreamGeometry();
            using (var line = geometry.Open())
            {
                foreach (var segment in segments)
                {
                    for (int i = 0; i < segment.Count; i++)
                    {
                        var p = segment[i];
                        var location = new Point(X(p), Y(upload ? p.Upload : p.Download));
                        if (i == 0) line.BeginFigure(location, false, false);
                        else line.LineTo(location, true, false);
                        if (segment.Count == 1) dc.DrawEllipse(color, null, location, 1.6, 1.6);
                    }
                }
            }
            geometry.Freeze();
            dc.DrawGeometry(null, new Pen(color, Compact ? 1.2 : 1.4), geometry);
        }
        DrawSeries(false, Theme.Download); DrawSeries(true, Theme.Upload);
        dc.Pop();
        if (points.Count == 0 && !Compact)
        {
            Label(dc, "等待采集数据", rect.Left + rect.Width / 2, rect.Top + rect.Height / 2 - 18, Theme.Muted, 16, 0.5);
            Label(dc, "启动后积累历史，未采集的时段留空", rect.Left + rect.Width / 2, rect.Top + rect.Height / 2 + 12, Theme.Muted, 12, 0.5);
        }
    }
    private void DrawHover(DrawingContext dc, Rect rect, DateTimeOffset begin, double max)
    {
        double X(TrafficPoint p) => rect.Left + (p.Time - begin).TotalSeconds / range.TotalSeconds * rect.Width;
        double Y(double value) => rect.Bottom - Units.Megabits(value) / max * rect.Height;
        if (hover.HasValue && !Compact && points.Count > 0 && rect.Contains(hover.Value))
        {
            TrendPoint? trend = null;
            if (cachedTrendSegments != null)
            {
                var time = begin + TimeSpan.FromSeconds((hover.Value.X - rect.Left) / rect.Width * range.TotalSeconds);
                trend = cachedTrendSegments.SelectMany(segment => segment).Where(point => point.Start <= time && time <= point.End).MinBy(point => Math.Abs(X(point.Point) - hover.Value.X));
                if (trend == null) return;
            }
            var p = trend?.Point ?? points.MinBy(p => Math.Abs(X(p) - hover.Value.X))!;
            var x = X(p);
            dc.DrawLine(new Pen(Theme.Muted, 0.8) { DashStyle = DashStyles.Dash }, new(x, rect.Top), new(x, rect.Bottom));
            dc.DrawEllipse(Theme.Upload, new Pen(Theme.Background, 1), new(x, Y(p.Upload)), 3.5, 3.5);
            dc.DrawEllipse(Theme.Download, new Pen(Theme.Background, 1), new(x, Y(p.Download)), 3.5, 3.5);
            var uploadLabel = trend == null ? "↑ 上传  " + Units.PointRate(p.Upload) : "↑ 平均 " + Units.PointRate(p.Upload) + "  ·  样本最高 " + Units.PointRate(trend.UploadMaximum);
            var downloadLabel = trend == null ? "↓ 下载  " + Units.PointRate(p.Download) : "↓ 平均 " + Units.PointRate(p.Download) + "  ·  样本最高 " + Units.PointRate(trend.DownloadMaximum);
            var timeLabel = trend == null ? p.Time.ToLocalTime().ToString("MM/dd HH:mm:ss") : trend.Start.ToLocalTime().ToString("MM/dd HH:mm:ss") + " — " + trend.End.ToLocalTime().ToString("HH:mm:ss");
            var popupWidth = Math.Max(206, new[] { Text(uploadLabel, Theme.Upload).Width, Text(downloadLabel, Theme.Download).Width, Text(timeLabel, Theme.Text).Width }.Max() + 24);
            // Put the tooltip on the opposite side so it does not cover the point being inspected.
            var tx = hover.Value.X > rect.Left + rect.Width / 2 ? rect.Left + 8 : Math.Max(rect.Left, rect.Right - popupWidth - 8);
            var ty = Math.Clamp(hover.Value.Y - 86, rect.Top, Math.Max(rect.Top, rect.Bottom - 82));
            dc.DrawRoundedRectangle(Theme.Background, new Pen(Theme.Border, 1), new(tx, ty, popupWidth, 82), 6, 6);
            Label(dc, timeLabel, tx + 12, ty + 9, Theme.Text);
            Label(dc, uploadLabel, tx + 12, ty + 32, Theme.Upload);
            Label(dc, downloadLabel, tx + 12, ty + 55, Theme.Download);
        }
    }
}
