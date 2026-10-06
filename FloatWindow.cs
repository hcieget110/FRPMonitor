using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FRPMonitor;

public sealed class FloatWindow : Window
{
    private readonly TextBlock up = Theme.Label("—", 18, Theme.Upload, FontWeights.SemiBold);
    private readonly TextBlock down = Theme.Label("—", 18, Theme.Download, FontWeights.SemiBold);
    private readonly TextBlock state = Theme.Label("正在连接采集器", 9, Theme.Muted);
    private readonly TrafficChart chart = new() { Compact = true, Height = 40, ToolTip = "最近 5 分钟 · 每秒刷新 · 上传 / 下载趋势\n纵轴单位 Mb/s，显示最高、中间和 0 刻度" };
    private readonly TextBlock total = Theme.Label("合计 — Mb/s", 10, Theme.Text);
    private readonly MainWindow main;
    private readonly Settings settings;
    private readonly MenuItem topmostItem;
    private readonly List<MenuItem> opacityItems = new();
    public FloatWindow(MainWindow owner, Settings preferences)
    {
        main = owner; settings = preferences;
        FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI");
        Width = 280; Height = 144; Title = "FRP 流量悬浮窗 v" + AppInfo.Version;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = System.Windows.Media.Brushes.Transparent;
        Icon = AppIcons.WindowIcon;
        ShowInTaskbar = false; Topmost = settings.FloatTopmost; ResizeMode = ResizeMode.NoResize;
        Opacity = Math.Clamp(settings.FloatOpacity, 0.4, 1);
        var stack = new StackPanel();
        var head = new DockPanel();
        var hide = Theme.Label("×", 14, Theme.Muted); hide.Cursor = Cursors.Hand;
        hide.MouseLeftButtonDown += (_, e) => { main.ToggleFloat(); e.Handled = true; }; DockPanel.SetDock(hide, Dock.Right); head.Children.Add(hide);
        head.Children.Add(Theme.Label("◈  FRP MONITOR  v" + AppInfo.Version, 10, Theme.Text, FontWeights.SemiBold)); stack.Children.Add(head);
        var columns = new Grid { Margin = new(0, 4, 0, 0), ClipToBounds = true };
        columns.ColumnDefinitions.Add(new()); columns.ColumnDefinitions.Add(new() { Width = new GridLength(16) }); columns.ColumnDefinitions.Add(new());
        Viewbox RateBox(TextBlock value) => new() { Child = value, Height = 24, ClipToBounds = true, Stretch = System.Windows.Media.Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Stretch };
        var us = new StackPanel(); us.Children.Add(Theme.Label("↑ 上传", 9, Theme.Upload)); us.Children.Add(RateBox(up));
        var ds = new StackPanel(); ds.Children.Add(Theme.Label("↓ 下载", 9, Theme.Download)); ds.Children.Add(RateBox(down));
        Grid.SetColumn(ds, 2); columns.Children.Add(us); columns.Children.Add(ds); stack.Children.Add(columns);
        total.Margin = new(0, 2, 0, 1); stack.Children.Add(total); stack.Children.Add(chart); stack.Children.Add(state);
        Content = Theme.Card(stack, new Thickness(12, 9, 12, 8));
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) main.Reveal();
            else { try { DragMove(); SavePosition(); } catch (InvalidOperationException) { } }
        };
        var work = SystemParameters.WorkArea;
        Left = settings.FloatLeft >= 0 ? settings.FloatLeft : work.Right - Width - 24;
        Top = settings.FloatTop >= 0 ? settings.FloatTop : work.Top + 60;
        // Recover visible placement if a previously used display is unplugged.
        if (Left + Width < SystemParameters.VirtualScreenLeft || Left > SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 30 ||
            Top + Height < SystemParameters.VirtualScreenTop || Top > SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 30)
        { Left = work.Right - Width - 24; Top = work.Top + 60; }
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = AppInfo.DisplayName, IsEnabled = false });
        void Item(string title, Action action) { var i = new MenuItem { Header = title }; i.Click += (_, _) => action(); menu.Items.Add(i); }
        Item("打开主窗口", main.Reveal); Item("隐藏悬浮窗", main.ToggleFloat);
        topmostItem = new MenuItem { Header = "悬浮窗始终置顶", IsCheckable = true, IsChecked = settings.FloatTopmost };
        topmostItem.Click += (_, _) => main.SetFloatTopmost(!settings.FloatTopmost);
        menu.Items.Add(topmostItem);
        foreach (var value in new[] { 0.5, 0.7, 0.94 })
        {
            var opacityItem = new MenuItem { Header = "透明度 " + Math.Round(value * 100) + "%", IsCheckable = true, Tag = value };
            opacityItem.Click += (_, _) => SetOpacity(value);
            opacityItems.Add(opacityItem); menu.Items.Add(opacityItem);
        }
        RefreshOpacityChecks();
        menu.Opened += (_, _) => RefreshOpacityChecks();
        Item("退出", main.Quit);
        ContextMenu = menu;
    }
    public void SetTopmost(bool value) { Topmost = value; topmostItem.IsChecked = value; }
    private void RefreshOpacityChecks()
    {
        foreach (var item in opacityItems) item.IsChecked = Math.Abs(Opacity - (double)item.Tag) < 0.000001;
    }
    private void SetOpacity(double value) { Opacity = settings.FloatOpacity = value; RefreshOpacityChecks(); main.SaveSettings(); }
    public void SavePosition() { settings.FloatLeft = Left; settings.FloatTop = Top; main.SaveSettings(); }
    public void Update(Snapshot sample, bool ready, string status, History history)
    {
        up.Text = ready ? Units.Rate(sample.UploadBytes / sample.Seconds) : "—";
        down.Text = ready ? Units.Rate(sample.DownloadBytes / sample.Seconds) : "—";
        total.Text = (ready ? "合计 " + Units.Rate((sample.UploadBytes + sample.DownloadBytes) / sample.Seconds) : "合计 — Mb/s") + "  ·  " + (sample.Seconds > 5 ? "延迟间隔平均" : settings.RateAverageSeconds + " 秒平均");
        state.Text = status; state.ToolTip = status; state.TextTrimming = TextTrimming.CharacterEllipsis;
        state.Foreground = !ready || status.StartsWith("⚠") ? Theme.Brush("#FFBA80") : Theme.Muted;
        chart.FixedMaximumMbps = settings.FixedScaleMbps; chart.HoldScale = true;
        chart.Update(history.Points(TimeSpan.FromMinutes(5), DateTimeOffset.Now), TimeSpan.FromMinutes(5), DateTimeOffset.Now);
    }
}
