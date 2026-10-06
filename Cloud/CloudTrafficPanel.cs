using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FRPMonitor.Cloud;

public sealed class CloudTrafficPanel : StackPanel
{
    private readonly TextBlock up, down, total, state;
    private readonly TrafficChart chart;
    public CloudTrafficPanel(Action configureCloud, Action<TimeSpan, bool> exportCloud, bool compact)
    {
        var head = new DockPanel();
        var configure = Theme.Label("设置", compact ? 9 : 11, Theme.Muted); configure.Cursor = Cursors.Hand;
        configure.MouseLeftButtonDown += (_, e) => { configureCloud(); e.Handled = true; }; DockPanel.SetDock(configure, Dock.Right); head.Children.Add(configure);
        head.Children.Add(Theme.Label("◈  云主机 · 整体流量", compact ? 10 : 16, Theme.Text, FontWeights.SemiBold)); Children.Add(head);
        var columns = new Grid { Margin = new(0, compact ? 4 : 10, 0, 0) };
        columns.ColumnDefinitions.Add(new()); columns.ColumnDefinitions.Add(new() { Width = new GridLength(16) }); columns.ColumnDefinitions.Add(new());
        up = Theme.Label("—", compact ? 18 : 25, Theme.Upload, FontWeights.SemiBold); down = Theme.Label("—", compact ? 18 : 25, Theme.Download, FontWeights.SemiBold);
        StackPanel Rate(string label, TextBlock value, Brush color)
        {
            var s = new StackPanel(); s.Children.Add(Theme.Label(label, compact ? 9 : 11, color));
            s.Children.Add(new Viewbox { Child = value, Height = compact ? 24 : 29, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly }); return s;
        }
        columns.Children.Add(Rate("↑ 上传", up, Theme.Upload)); var d = Rate("↓ 下载", down, Theme.Download); Grid.SetColumn(d, 2); columns.Children.Add(d); Children.Add(columns);
        total = Theme.Label("合计 — Mb/s · 1 秒采样", compact ? 10 : 11, Theme.Text); total.Margin = new(0, 2, 0, 2); Children.Add(total);
        chart = new TrafficChart { Compact = compact, Height = compact ? 40 : 120, UseAggregation = false, HoldScale = true, ToolTip = "云主机整体上传 / 下载 · 最近 5 分钟 · 秒级采样 · Mb/s" }; Children.Add(chart);
        state = Theme.Label("未配置云主机", compact ? 9 : 11, Theme.Muted); state.TextTrimming = TextTrimming.CharacterEllipsis; Children.Add(state);
        var exports = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 5, 0, 0) };
        foreach (var minutes in new[] { 1, 5 })
        {
            var button = Theme.Button("导出 " + minutes + " 分钟", (_, _) => exportCloud(TimeSpan.FromMinutes(minutes), true));
            button.Padding = new(compact ? 5 : 8, 3, compact ? 5 : 8, 3); button.FontSize = compact ? 9 : 11;
            button.ToolTip = "秒级数据 + 可编辑 Excel 折线图"; exports.Children.Add(button);
        }
        if (!compact)
        {
            var csv = Theme.Button("CSV 数据", (_, _) => exportCloud(TimeSpan.FromMinutes(5), false)); csv.Padding = new(8, 3, 8, 3); csv.FontSize = 11; exports.Children.Add(csv);
        }
        Children.Add(exports);
    }
    public void Update(CloudFrame frame, List<TrafficPoint> points, DateTimeOffset now)
    {
        var last = points.LastOrDefault(); var ready = frame.Ready && frame.Updated.HasValue && now - frame.Updated.Value < TimeSpan.FromSeconds(4) && last != null;
        up.Text = ready ? Units.Rate(last!.Upload) : "—"; down.Text = ready ? Units.Rate(last!.Download) : "—";
        total.Text = (ready ? "合计 " + Units.Rate(last!.Upload + last.Download) : "合计 — Mb/s") + " · 1 秒采样";
        state.Text = frame.Ready && !ready ? "数据暂未更新 · 等待云端采样" : frame.Message;
        state.ToolTip = state.Text + "\n统计云主机网卡的全部进程流量，不等于单独 FRP 流量。";
        state.Foreground = ready ? Theme.Muted : Theme.Brush("#FFBA80"); chart.Update(points, TimeSpan.FromMinutes(5), now);
    }
}
