using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace FRPMonitor;

public sealed class MainWindow : Window
{
    private readonly Settings settings;
    private History history;
    private MonitoringService? monitor;
    private readonly bool preview;
    private FloatWindow? floating;
    private Forms.NotifyIcon? tray;
    private System.Drawing.Icon? trayIcon;
    private Forms.ToolStripMenuItem? trayTopmostItem;
    private Forms.ToolStripMenuItem? trayMainTopmostItem;
    private Forms.ToolStripMenuItem? trayFrpStartItem;
    private readonly FrpControlService frpControl = new();
    private readonly Button frpStartButton;
    private bool frpRunning, frpStarting, frpPolling;
    private long lastFrpPoll;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool quitting;
    private readonly BackgroundOperations operations = new();
    private DateTimeOffset? viewEnd;
    private string? settingsError;
    private TimeSpan defaultPeriod = TimeSpan.FromMinutes(5);
    private TimeSpan period = TimeSpan.FromMinutes(5);
    private readonly TrafficChart chart = new() { MinHeight = 145 };
    private readonly TextBlock status = Theme.Label("启动 ETW 采集器…", 12, Theme.Muted);
    private readonly TextBlock up = Theme.Label("—", 31, Theme.Upload, FontWeights.SemiBold);
    private readonly TextBlock down = Theme.Label("—", 31, Theme.Download, FontWeights.SemiBold);
    private readonly TextBlock periodUp = Theme.Label("0 B", 27, Theme.Text, FontWeights.SemiBold);
    private readonly TextBlock periodDown = Theme.Label("0 B", 27, Theme.Text, FontWeights.SemiBold);
    private readonly TextBlock rangeUpTitle = Theme.Label("所选时段 · 累计上传", 12, Theme.Muted);
    private readonly TextBlock rangeDownTitle = Theme.Label("所选时段 · 累计下载", 12, Theme.Muted);
    private readonly TextBlock[,] chartStatistics = new TextBlock[2, 4];
    private readonly TextBlock chartDetail = Theme.Label("", 11, Theme.Muted);
    private readonly TextBlock footer = Theme.Label("", 10, Theme.Muted);
    private readonly TextBlock todaySummary = Theme.Label("", 10, Theme.Muted);
    private readonly TextBlock processSummary = Theme.Label("自动发现 · frpc / frps", 11, Theme.Muted);
    private readonly StackPanel processRows = new();
    private sealed record ProcessControls(int Pid, string Name, TextBlock Upload, TextBlock Download);
    private readonly List<ProcessControls> processControls = new();
    private readonly TextBlock noProcesses = Theme.Label("未发现匹配进程。FRP 启动后将自动开始统计。", 12, Theme.Muted);
    private readonly TextBlock alert = Theme.Label("", 11, Theme.Brush("#FFBA80"));
    private readonly Button floatButton;
    private Snapshot sample = new(0, 0, 0, 0, 1, new());
    private Action? updateRangeColors;
    public MainWindow(Settings preferences, bool isPreview = false, string? previewFolder = null, bool startInTray = false)
    {
        settings = preferences; settings.Normalize(); preview = isPreview;
        settings.ShowFloat = true; // Hiding is temporary; every new launch shows the floating monitor.
        ShowActivated = !startInTray;
        Background = Theme.Background; Foreground = Theme.Text; FontFamily = new FontFamily("Microsoft YaHei UI");
        Icon = AppIcons.WindowIcon;
        Topmost = settings.MainTopmost;
        history = new(settings.Scope, previewFolder);
        Title = AppInfo.DisplayName; Width = 1060; Height = 820; MinWidth = 870; MinHeight = 780;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var root = new Grid { Margin = new(28, 24, 28, 18) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var head = new DockPanel { Margin = new(0, 0, 0, 16) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(Theme.Button("软件设置", (_, _) => Configure()));
        floatButton = Theme.Button(settings.ShowFloat ? "隐藏悬浮窗" : "显示悬浮窗", (_, _) => ToggleFloat()); actions.Children.Add(floatButton);
        actions.Children.Add(Theme.Button("收起到托盘", (_, _) => Hide())); DockPanel.SetDock(actions, Dock.Right); head.Children.Add(actions);
        var identity = new StackPanel();
        var titleLine = new StackPanel { Orientation = Orientation.Horizontal };
        titleLine.Children.Add(Theme.Label("◈  FRP 流量监控", 25, Theme.Text, FontWeights.SemiBold));
        var versionLabel = Theme.Label("v" + AppInfo.Version, 12, Theme.Muted); versionLabel.Margin = new(12, 4, 0, 0); versionLabel.ToolTip = AppInfo.Description;
        titleLine.Children.Add(versionLabel); identity.Children.Add(titleLine);
        status.Margin = new(2, 8, 0, 0); identity.Children.Add(status); head.Children.Add(identity); root.Children.Add(head);
        var cards = new Grid { Margin = new(0, 0, 0, 14) };
        for (int i = 0; i < 4; i++) cards.ColumnDefinitions.Add(new());
        void Stat(int col, TextBlock title, TextBlock value, string hint, TextBlock? secondary = null)
        {
            var s = new StackPanel(); s.Children.Add(title);
            s.Children.Add(new Viewbox { Child = value, Height = 35, Margin = new(0, 8, 0, 6), Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly });
            if (secondary != null) { secondary.Margin = new(0, 0, 0, 6); s.Children.Add(secondary); }
            s.Children.Add(Theme.Label(hint, 10, Theme.Muted));
            var c = Theme.Card(s, new Thickness(18)); c.Margin = new(col == 0 ? 0 : 6, 0, col == 3 ? 0 : 6, 0); Grid.SetColumn(c, col); cards.Children.Add(c);
        }
        Stat(0, Theme.Label("↑ 实时上传", 12, Theme.Upload), up, "比特速率 · 每秒刷新");
        Stat(1, Theme.Label("↓ 实时下载", 12, Theme.Download), down, "比特速率 · TCP + UDP");
        Stat(2, rangeUpTitle, periodUp, "已采集分钟的流量总量");
        Stat(3, rangeDownTitle, periodDown, "已采集分钟的流量总量");
        Grid.SetRow(cards, 1); root.Children.Add(cards);
        var chartContent = new Grid();
        chartContent.RowDefinitions.Add(new() { Height = GridLength.Auto });
        chartContent.RowDefinitions.Add(new() { Height = GridLength.Auto });
        chartContent.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        chartContent.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var chartHead = new DockPanel();
        var legend = new StackPanel { Orientation = Orientation.Horizontal };
        var detail = new CheckBox { Content = "原始细节", Foreground = Theme.Muted, FontSize = 11, Margin = new(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
        detail.Checked += (_, _) => { chart.UseAggregation = false; RefreshView(); };
        detail.Unchecked += (_, _) => { chart.UseAggregation = true; RefreshView(); };
        detail.ToolTip = "关闭平均趋势，显示原始秒／分钟数据；可拖选拥挤区间放大"; legend.Children.Add(detail);
        var reset = Theme.Button("恢复范围", (_, _) => ChangePeriod(defaultPeriod)); reset.Padding = new(6, 2, 6, 2); reset.FontSize = 10; reset.Margin = new(0, 0, 12, 0); legend.Children.Add(reset);
        chart.RangeSelected += (start, end) => { period = end - start; viewEnd = end; updateRangeColors?.Invoke(); RefreshView(); };
        chart.ResetRequested += () => ChangePeriod(defaultPeriod);
        legend.Children.Add(Theme.Label("● 上传", 11, Theme.Upload)); var dl = Theme.Label("● 下载", 11, Theme.Download); dl.Margin = new(20, 0, 0, 0); legend.Children.Add(dl);
        DockPanel.SetDock(legend, Dock.Right); chartHead.Children.Add(legend); chartHead.Children.Add(Theme.Label("带宽历史趋势", 18, Theme.Text, FontWeights.SemiBold)); chartContent.Children.Add(chartHead);
        var controls = new DockPanel { Margin = new(0, 12, 0, 8) };
        var exports = new StackPanel { Orientation = Orientation.Horizontal };
        var excelExport = Theme.Button("导出 Excel 图表", (_, _) => Export(true)); excelExport.ToolTip = "可点击编辑的 Excel 原生折线图，包含概览、大幅详细图和数据";
        var csvExport = Theme.Button("CSV 数据", (_, _) => Export(false)); csvExport.ToolTip = "仅导出表格数据，不含图表";
        exports.Children.Add(excelExport); exports.Children.Add(csvExport); DockPanel.SetDock(exports, Dock.Right); controls.Children.Add(exports);
        var ranges = new StackPanel { Orientation = Orientation.Horizontal };
        var options = new[] { ("5 分钟", TimeSpan.FromMinutes(5)), ("30 分钟", TimeSpan.FromMinutes(30)), ("2 小时", TimeSpan.FromHours(2)), ("近一天", TimeSpan.FromDays(1)), ("近一周", TimeSpan.FromDays(7)) };
        foreach (var (label, span) in options)
        {
            var button = Theme.Button(label, (_, _) => ChangePeriod(span));
            button.Tag = span; button.Margin = new(0, 0, 6, 0); ranges.Children.Add(button);
        }
        controls.Children.Add(ranges); Grid.SetRow(controls, 1); chartContent.Children.Add(controls);
        Grid.SetRow(chart, 2); chartContent.Children.Add(chart);
        var summary = new StackPanel { Margin = new(0, 8, 0, 0) };
        var statistics = new Grid();
        for (int i = 0; i < 5; i++) statistics.ColumnDefinitions.Add(new() { Width = new GridLength(i == 0 ? 1.2 : 1, GridUnitType.Star) });
        for (int i = 0; i < 3; i++) statistics.RowDefinitions.Add(new() { Height = new GridLength(20) });
        var headers = new[] { "监控项", "最小值", "平均值", "最大值", "最新采样" };
        for (int col = 0; col < 5; col++)
        {
            var label = Theme.Label(headers[col], 10, Theme.Muted);
            label.HorizontalAlignment = col == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            Grid.SetColumn(label, col); statistics.Children.Add(label);
        }
        for (int row = 0; row < 2; row++)
        {
            var color = row == 0 ? Theme.Upload : Theme.Download;
            var name = Theme.Label(row == 0 ? "━  FRP 上传" : "━  FRP 下载", 11, color);
            Grid.SetRow(name, row + 1); statistics.Children.Add(name);
            for (int col = 0; col < 4; col++)
            {
                var value = chartStatistics[row, col] = Theme.Label("—", 11, color);
                var cell = new Viewbox { Child = value, Height = 17, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Right };
                Grid.SetRow(cell, row + 1); Grid.SetColumn(cell, col + 1); statistics.Children.Add(cell);
            }
        }
        summary.Children.Add(statistics);
        chartDetail.Margin = new(0, 6, 0, 0); chartDetail.FontSize = 10; chartDetail.TextWrapping = TextWrapping.Wrap;
        summary.Children.Add(chartDetail);
        Grid.SetRow(summary, 3); chartContent.Children.Add(summary);
        var chartCard = Theme.Card(chartContent); Grid.SetRow(chartCard, 2); root.Children.Add(chartCard);
        var processes = new StackPanel();
        var processHead = new DockPanel();
        var processActions = new StackPanel { Orientation = Orientation.Horizontal };
        frpStartButton = Theme.Button("启动 FRP", (_, _) => StartFrp()); frpStartButton.Padding = new(8, 4, 8, 4);
        frpStartButton.ToolTip = "启动任务「开启FRP」。已启动表示检测到 frpc.exe；已运行时点击不会重复启动。";
        processActions.Children.Add(frpStartButton);
        var connectionsButton = Theme.Button("连接明细", (_, _) => ShowConnections()); connectionsButton.Padding = new(8, 4, 8, 4);
        var diagnosticButton = Theme.Button("诊断报告", (_, _) => ExportDiagnostic()); diagnosticButton.Padding = new(8, 4, 8, 4);
        var timelineButton = Theme.Button("事件时间线", (_, _) => ShowTimeline()); timelineButton.Padding = new(8, 4, 8, 4);
        processActions.Children.Add(timelineButton);
        processActions.Children.Add(connectionsButton); processActions.Children.Add(diagnosticButton);
        DockPanel.SetDock(processActions, Dock.Right); processHead.Children.Add(processActions);
        processSummary.MaxWidth = 170; processSummary.TextTrimming = TextTrimming.CharacterEllipsis;
        DockPanel.SetDock(processSummary, Dock.Right); processHead.Children.Add(processSummary);
        processHead.Children.Add(Theme.Label("监控中的进程", 15, Theme.Text, FontWeights.SemiBold)); processes.Children.Add(processHead);
        var scroll = new ScrollViewer { Content = processRows, MaxHeight = 80, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new(0, 8, 0, 0) }; processes.Children.Add(scroll);
        var pc = Theme.Card(processes, new Thickness(20, 15, 20, 15)); pc.Margin = new(0, 18, 0, 14); Grid.SetRow(pc, 3); root.Children.Add(pc);
        var bottom = new StackPanel(); bottom.Children.Add(alert); bottom.Children.Add(todaySummary); bottom.Children.Add(footer); Grid.SetRow(bottom, 4); root.Children.Add(bottom); Content = root;
        // Update range appearance without separate selection state.
        void RangeColors() { foreach (Button b in ranges.Children) { var selected = viewEnd == null && (TimeSpan)b.Tag == period; b.Background = selected ? Theme.Brush("#24483F") : Theme.Brush("#202E43"); b.Foreground = selected ? Theme.Upload : Theme.Muted; } }
        updateRangeColors = RangeColors; RangeColors();
        Closing += OnClosing;
        IsVisibleChanged += (_, _) => { if (IsVisible) RefreshView(); };
        StateChanged += (_, _) => { if (IsVisible && WindowState != WindowState.Minimized) RefreshView(); };
        Loaded += (_, _) =>
        {
            if (preview) { SeedPreview(); return; }
            CreateTray(); StartCollector(); RefreshFrpStatus();
            if (settings.ShowFloat) ShowFloating();
            if (startInTray) Hide();
            timer.Tick += (_, _) => Tick(); timer.Start();
        };
    }
    private void StartCollector() { monitor = new(history, settings); monitor.Start(); }
    private void Tick()
    {
        if (Stopwatch.GetElapsedTime(lastFrpPoll) >= TimeSpan.FromSeconds(2)) RefreshFrpStatus();
        RefreshView();
    }
    private async void RefreshFrpStatus()
    {
        if (preview || quitting || frpPolling || frpStarting) return;
        frpPolling = true; lastFrpPoll = Stopwatch.GetTimestamp();
        try { var running = await System.Threading.Tasks.Task.Run(FrpControlService.IsClientRunning); if (!quitting && !frpStarting) { frpRunning = running; UpdateFrpControls(); } }
        catch (Exception e) { if (!quitting) frpStartButton.ToolTip = "FRP 进程检查失败：" + e.Message; }
        finally { frpPolling = false; }
    }
    private void UpdateFrpControls()
    {
        var text = frpStarting ? "启动中…" : frpRunning ? "已启动" : "启动 FRP";
        frpStartButton.Content = text; frpStartButton.IsEnabled = !frpStarting && !quitting;
        frpStartButton.Foreground = frpRunning ? Theme.Upload : Theme.Text;
        if (trayFrpStartItem != null) { trayFrpStartItem.Text = frpStarting ? "FRP 启动中…" : frpRunning ? "FRP 已启动" : "启动 FRP"; trayFrpStartItem.Enabled = !frpStarting && !quitting; }
    }
    private async void StartFrp()
    {
        if (frpStarting || quitting) return;
        if (preview) { System.Windows.MessageBox.Show(this, "FRP 已启动（演示状态，未操作实际进程）。", "启动 FRP"); return; }
        frpStarting = true; UpdateFrpControls();
        try
        {
            FrpStartResult result = default;
            await operations.Run(async () => { result = await frpControl.StartAsync(); });
            frpRunning = await System.Threading.Tasks.Task.Run(FrpControlService.IsClientRunning);
            if (!quitting) System.Windows.MessageBox.Show(this, frpRunning ? result == FrpStartResult.AlreadyRunning ? "FRP 已启动，无需重复启动。" : "FRP 已启动。" : "FRP 进程已退出，请查看启动日志。", "启动 FRP");
        }
        catch (Exception e) { frpRunning = false; if (!quitting) System.Windows.MessageBox.Show(this, e.Message, "FRP 启动失败"); }
        finally { frpStarting = false; if (!quitting) { UpdateFrpControls(); RefreshFrpStatus(); } }
    }
    private void RefreshView()
    {
        var frame = monitor?.Current;
        if (frame != null) sample = frame.Sample;
        var ready = preview || frame?.Ready == true;
        var state = preview ? "演示预览 · 合成数据" : frame?.Message ?? "正在启动采集…";
        if (changingScope) { ready = false; state = "正在切换统计范围…"; }
        if (exporting) state += " · 正在后台导出";
        chart.FixedMaximumMbps = settings.FixedScaleMbps;
        chart.HoldScale = true;
        // Sampling and persistence continue independently of window visibility.
        // Build only the views that the user can currently see.
        if (IsVisible && WindowState != WindowState.Minimized) RefreshMainView(ready, state, DateTimeOffset.Now);
        if (floating?.IsVisible == true) floating.Update(sample, ready, state, history);
        if (tray != null)
        {
            var trayText = "FRP  ↑ " + (ready ? Units.Rate(sample.UploadBytes / sample.Seconds) : "—") + "  ↓ " + (ready ? Units.Rate(sample.DownloadBytes / sample.Seconds) : "—");
            tray.Text = trayText[..Math.Min(63, trayText.Length)];
        }
    }
    private void RefreshMainView(bool ready, string state, DateTimeOffset now)
    {
        status.Text = state; status.Foreground = ready && !state.StartsWith("⚠") ? Theme.Upload : Theme.Brush("#FFBA80");
        up.Text = ready ? Units.Rate(sample.UploadBytes / sample.Seconds) : "—"; down.Text = ready ? Units.Rate(sample.DownloadBytes / sample.Seconds) : "—";
        var end = viewEnd ?? now;
        var totals = history.Totals(period, end);
        periodUp.Text = Units.Bytes(totals.Up); periodDown.Text = Units.Bytes(totals.Down);
        var rangeLabel = viewEnd.HasValue ? "局部范围" : period.TotalDays == 7 ? "近一周" : period.TotalDays == 1 ? "近一天" : period.TotalHours == 2 ? "近 2 小时" : "近 " + period.TotalMinutes + " 分钟";
        rangeUpTitle.Text = rangeLabel + " · 累计上传"; rangeDownTitle.Text = rangeLabel + " · 累计下载";
        var points = history.Points(period, end); chart.Update(points, period, end);
        for (int row = 0; row < 2; row++)
        {
            var stats = ChartData.Statistics(points, row == 0);
            var values = stats == null ? null : new[] { stats.Minimum, stats.Average, stats.Maximum, stats.Last };
            for (int col = 0; col < 4; col++) chartStatistics[row, col].Text = values == null ? "—" : Units.Rate(values[col]);
        }
        var chartMode = chart.AggregationSeconds == 0 ? "秒／分钟速率" : chart.AggregationSeconds < 60 ? chart.AggregationSeconds + " 秒平均趋势" : chart.AggregationSeconds / 60.0 + " 分钟平均趋势";
        chartDetail.Text = (end - period).ToString("MM/dd HH:mm:ss") + " — " + end.ToString("MM/dd HH:mm:ss") + "  ·  " + chartMode + "  ·  拖选放大，双击恢复；空缺表示未采集\n" + history.Coverage(period, end).Description + "（分钟边界统计）";
        processSummary.Text = string.Join(" / ", settings.Names) + "  ·  自动发现";
        processSummary.ToolTip = processSummary.Text;
        var midnight = new DateTimeOffset(now.Date, TimeZoneInfo.Local.GetUtcOffset(now.Date));
        var today = history.Totals(now - midnight, now);
        var peaks = history.Peaks(period, end);
        todaySummary.Text = "今天 00:00 起 ↑ " + Units.Bytes(today.Up) + " ↓ " + Units.Bytes(today.Down) + "  |  所选范围已记录秒级峰值 ↑ " + (peaks.Up.HasValue ? Units.PointRate(peaks.Up.Value) : "未记录") + " ↓ " + (peaks.Down.HasValue ? Units.PointRate(peaks.Down.Value) : "未记录");
        RefreshProcesses(ready);
        var lost = monitor?.Current.EventsLost ?? 0;
        alert.Text = settingsError ?? settings.LoadWarning ?? history.StorageError ?? history.Timeline.StorageError ?? (lost > 0 ? "ETW 丢失了 " + lost + " 个事件，部分流量可能低估。" : "");
        alert.TextWrapping = TextWrapping.Wrap;
        alert.MaxHeight = 34; alert.ToolTip = alert.Text;
        alert.Visibility = alert.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        var scopeLabel = settings.ConnectionMode == ConnectionMode.Selected ? "仅指定连接" : settings.ConnectionMode == ConnectionMode.ExternalAndSelectedLoopback ? "非回环 + 指定回环" : settings.IncludeLoopback ? "含回环" : "不含回环";
        footer.Text = (ready ? "合计 " + Units.Rate((sample.UploadBytes + sample.DownloadBytes) / sample.Seconds) : "合计 — Mb/s") + " · " + (sample.Seconds > 5 ? "延迟间隔平均" : settings.RateAverageSeconds + " 秒平均") + "  |  本次运行 ↑ " + Units.Bytes(sample.TotalUp) + " ↓ " + Units.Bytes(sample.TotalDown) + "  |  " + scopeLabel + "  |  " + now.ToString("HH:mm:ss") + "  |  关闭窗口后托盘继续采集";
    }
    private void RefreshProcesses(bool ready)
    {
        var changed = processControls.Count != sample.Processes.Count;
        for (int i = 0; !changed && i < processControls.Count; i++)
            changed = processControls[i].Pid != sample.Processes[i].Pid || processControls[i].Name != sample.Processes[i].Name;
        if (changed)
        {
            processControls.Clear(); processRows.Children.Clear();
            foreach (var p in sample.Processes)
            {
                var row = new Grid { Margin = new(0, 7, 0, 3) };
                row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = new GridLength(140) }); row.ColumnDefinitions.Add(new() { Width = new GridLength(165) }); row.ColumnDefinitions.Add(new() { Width = new GridLength(165) });
                TextBlock Cell(int column, string text, Brush color) { var t = Theme.Label(text, 12, color); Grid.SetColumn(t, column); row.Children.Add(t); return t; }
                Cell(0, "●  " + p.Name + ".exe", Theme.Text); Cell(1, "PID  " + p.Pid, Theme.Muted);
                var upload = Cell(2, "", Theme.Upload); var download = Cell(3, "", Theme.Download);
                processControls.Add(new(p.Pid, p.Name, upload, download));
                processRows.Children.Add(row);
            }
        }
        if (sample.Processes.Count == 0 && processRows.Children.Count == 0) processRows.Children.Add(noProcesses);
        for (int i = 0; i < sample.Processes.Count; i++)
        {
            var p = sample.Processes[i]; var controls = processControls[i];
            controls.Upload.Text = "↑  " + (ready ? Units.Rate(p.Upload) : "—");
            controls.Download.Text = "↓  " + (ready ? Units.Rate(p.Download) : "—");
        }
    }
    private void CreateTray()
    {
        trayIcon = AppIcons.CreateTrayIcon();
        tray = new Forms.NotifyIcon { Icon = trayIcon, Visible = true, Text = AppInfo.DisplayName };
        tray.ContextMenuStrip = BuildTrayMenu(); tray.DoubleClick += (_, _) => Dispatcher.Invoke(Reveal);
    }
    private Forms.ContextMenuStrip BuildTrayMenu(bool bindControls = true)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(new Forms.ToolStripMenuItem(AppInfo.DisplayName) { Enabled = false });
        menu.Items.Add("版本与运行位置", null, (_, _) => Dispatcher.Invoke(() => System.Windows.MessageBox.Show(this, AppInfo.Description, AppInfo.DisplayName)));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("打开主窗口", null, (_, _) => Dispatcher.Invoke(Reveal));
        menu.Items.Add("显示 / 隐藏悬浮窗", null, (_, _) => Dispatcher.Invoke(ToggleFloat));
        var frpStart = new Forms.ToolStripMenuItem(frpRunning ? "FRP 已启动" : "启动 FRP");
        frpStart.Click += (_, _) => Dispatcher.Invoke(StartFrp); menu.Items.Add(frpStart);
        if (bindControls) trayFrpStartItem = frpStart;
        var floatPin = new Forms.ToolStripMenuItem("悬浮窗始终置顶") { Checked = settings.FloatTopmost };
        floatPin.Click += (_, _) => Dispatcher.Invoke(() => SetFloatTopmost(!settings.FloatTopmost)); menu.Items.Add(floatPin);
        var mainPin = new Forms.ToolStripMenuItem("主窗口始终置顶") { Checked = settings.MainTopmost };
        mainPin.Click += (_, _) => Dispatcher.Invoke(() => SetMainTopmost(!settings.MainTopmost)); menu.Items.Add(mainPin);
        if (bindControls) { trayTopmostItem = floatPin; trayMainTopmostItem = mainPin; }
        menu.Items.Add("重启采集", null, (_, _) => Dispatcher.Invoke(() => monitor?.RequestRestart()));
        menu.Items.Add("连接明细与统计范围", null, (_, _) => Dispatcher.Invoke(ShowConnections));
        menu.Items.Add("采集事件时间线", null, (_, _) => Dispatcher.Invoke(ShowTimeline));
        menu.Items.Add("导出诊断报告", null, (_, _) => Dispatcher.Invoke(ExportDiagnostic));
        menu.Items.Add(new Forms.ToolStripSeparator()); menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(Quit));
        return menu;
    }
    public string[] PreviewTrayMenu() { using var menu = BuildTrayMenu(false); return menu.Items.Cast<Forms.ToolStripItem>().Select(item => item.Text ?? "").ToArray(); }
    public void Reveal() { Show(); WindowState = WindowState.Normal; RefreshView(); Activate(); }
    private void ShowFloating() { floating ??= new(this, settings); floating.Show(); RefreshView(); }
    public void ToggleFloat()
    {
        settings.ShowFloat = !settings.ShowFloat;
        if (settings.ShowFloat) ShowFloating(); else floating?.Hide();
        floatButton.Content = settings.ShowFloat ? "隐藏悬浮窗" : "显示悬浮窗"; SaveSettings();
    }
    public void SaveSettings()
    {
        if (preview) return;
        try { settings.Save(); settingsError = null; } catch (Exception e) { settingsError = "设置保存失败：" + e.Message; alert.Text = settingsError; alert.Visibility = Visibility.Visible; }
    }
    public void SetFloatTopmost(bool value)
    {
        settings.FloatTopmost = value;
        floating?.SetTopmost(value);
        if (trayTopmostItem != null) trayTopmostItem.Checked = value;
        SaveSettings();
    }
    public void SetMainTopmost(bool value)
    {
        settings.MainTopmost = Topmost = value;
        if (trayMainTopmostItem != null) trayMainTopmostItem.Checked = value;
        SaveSettings();
    }
    private async void Configure()
    {
        if (changingScope || quitting) return;
        var dialog = new SettingsWindow(this, settings, preview);
        if (dialog.ShowDialog() != true) return;
        changingScope = true;
        try
        {
        if (!preview && dialog.StartWithWindows.HasValue)
        {
            try { StartupService.SetEnabled(dialog.StartWithWindows.Value); }
            catch (Exception e) { System.Windows.MessageBox.Show(this, "开机自启设置未保存：" + e.Message, "开机自启"); }
        }
        var oldScope = settings.Scope; settings.ProcessNames = dialog.Names; settings.IncludeLoopback = dialog.Loopback; SetFloatTopmost(dialog.FloatTopmost);
        SetMainTopmost(dialog.MainTopmost);
        settings.ConnectionMode = dialog.ConnectionMode; settings.EndpointFilters = dialog.EndpointFilters; settings.FixedScaleMbps = dialog.FixedScaleMbps;
        settings.RateAverageSeconds = dialog.RateAverageSeconds; monitor?.SetAverageSeconds(settings.RateAverageSeconds); SaveSettings();
        if (settings.Scope != oldScope && !preview)
        {
            if (monitor != null) await monitor.StopAsync();
            history = await System.Threading.Tasks.Task.Run(() => new History(settings.Scope)); sample = new(0, 0, 0, 0, 1, new());
            if (quitting) return;
            StartCollector(); RefreshView();
        }
        RefreshView();
        }
        catch (Exception e) { System.Windows.MessageBox.Show(this, e.Message, "设置应用失败"); }
        finally { changingScope = false; }
    }
    private bool changingScope;
    private async void ShowConnections()
    {
        if (changingScope || quitting) return;
        var dialog = new ConnectionsWindow(this, () => preview ? new MonitorFrame(sample, true, "演示预览", 0, 0, DateTimeOffset.Now) : monitor?.Current);
        if (dialog.ShowDialog() != true) return;
        try
        {
            changingScope = true;
            if (monitor != null) await monitor.StopAsync();
            if (quitting) return;
            settings.ConnectionMode = ConnectionMode.Selected; settings.EndpointFilters = dialog.SelectedEndpoints; SaveSettings();
            if (!preview) { history = await System.Threading.Tasks.Task.Run(() => new History(settings.Scope)); if (quitting) return; sample = new(0, 0, 0, 0, 1, new()); StartCollector(); }
            RefreshView();
        }
        catch (Exception e) { System.Windows.MessageBox.Show(this, e.Message, "切换统计范围失败"); }
        finally { changingScope = false; }
    }
    private async void ExportDiagnostic()
    {
        if (quitting) return;
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "诊断报告 (*.json)|*.json", FileName = "FRP诊断-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json", DefaultExt = ".json", AddExtension = true };
        if (dialog.ShowDialog(this) != true) return;
        try { await operations.Run(() => DiagnosticService.ExportAsync(dialog.FileName, settings, monitor?.Current, history)); if (!quitting) new ExportCompletedWindow(this, dialog.FileName, false).ShowDialog(); }
        catch (Exception e) { if (!quitting) System.Windows.MessageBox.Show(this, e.Message, "诊断导出失败"); }
    }
    private bool exporting;
    private async void Export(bool excel)
    {
        if (quitting) return;
        if (exporting) { System.Windows.MessageBox.Show(this, "正在生成导出文件，请稍候。", "导出中"); return; }
        var choices = new ExportOptionsWindow(this, period, excel, viewEnd);
        if (choices.ShowDialog() != true) return;
        var exportRange = choices.End - choices.Start;
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = excel ? "导出可编辑的 Excel 折线图" : "导出 CSV 数据（不含图表）", Filter = excel ? "Excel 原生折线图 (*.xlsx)|*.xlsx" : "CSV 数据表 (*.csv)|*.csv", DefaultExt = excel ? ".xlsx" : ".csv", AddExtension = true, FileName = "FRP流量-" + choices.FileRangeName + "-" + choices.End.ToString("yyyyMMdd-HHmmss") + (excel ? ".xlsx" : ".csv") };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            exporting = true;
            status.Text = "正在后台生成导出文件…";
            await operations.Run(() => ExportService.ExportAsync(history, dialog.FileName, excel, exportRange, choices.End, preview, choices.Options));
            if (!quitting) new ExportCompletedWindow(this, dialog.FileName, excel).ShowDialog();
        }
        catch (Exception e) { if (!quitting) System.Windows.MessageBox.Show(this, e.Message, "导出失败"); }
        finally { exporting = false; }
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!quitting && !preview) { e.Cancel = true; Hide(); }
    }
    public async void Quit()
    {
        if (quitting) return;
        quitting = true; timer.Stop();
        IsEnabled = false;
        status.Text = "正在保存历史并等待导出完成…";
        try { await operations.FinishAsync(); if (!preview) { if (monitor != null) await monitor.StopAsync(); else await history.SaveAsync(); floating?.SavePosition(); SaveSettings(); } }
        catch (Exception e) { System.Windows.MessageBox.Show(this, e.Message, "退出保存失败"); }
        floating?.Close(); tray?.Dispose(); trayIcon?.Dispose(); System.Windows.Application.Current.Shutdown();
    }
    public void ChangePeriod(TimeSpan span) { period = defaultPeriod = span; viewEnd = null; updateRangeColors?.Invoke(); RefreshView(); }
    private void ShowTimeline()
    {
        if (quitting) return;
        new TimelineWindow(this, history.Timeline, item =>
        {
            viewEnd = item.Time.AddMinutes(2) < DateTimeOffset.Now ? item.Time.AddMinutes(2) : DateTimeOffset.Now;
            period = TimeSpan.FromMinutes(5); updateRangeColors?.Invoke(); Reveal();
        }).ShowDialog();
    }
    public FloatWindow? Floating => floating;
    public void ExportPreview(string path) => ExcelExporter.Export(path, history.ExportMinutes(period, DateTimeOffset.Now), period, DateTimeOffset.Now, true, new ExportOptions(SplitDays: period >= TimeSpan.FromDays(1), PeakCharts: true));
    private void SeedPreview()
    {
        frpRunning = true; UpdateFrpControls();
        var now = DateTimeOffset.Now; var random = new Random(17);
        for (int i = 7 * 24 * 60 - 1; i >= 0; i--)
        {
            if (i <= 5) continue;
            if (i > 30 && i % 1300 > 1200) continue;
            var phase = (7 * 24 * 60 - i) / 45.0;
            var u = (0.15 + Math.Pow(Math.Max(0, Math.Sin(phase)), 4) * 2.4 + random.NextDouble() * 0.3) * 1024 * 1024;
            var d = (0.25 + Math.Pow(Math.Max(0, Math.Cos(phase * 0.9)), 3) * 3.8 + random.NextDouble() * 0.2) * 1024 * 1024;
            history.Add(now.AddMinutes(-i), (long)u, (long)d, 1);
        }
        for (int i = 300; i >= 0; i--)
        {
            var wave = (300 - i) / 24.0;
            history.Add(now.AddSeconds(-i), (long)((0.3 + Math.Pow(Math.Max(0, Math.Sin(wave)), 3) * 1.8) * 1048576),
                (long)((0.6 + Math.Pow(Math.Max(0, Math.Cos(wave * 0.8)), 3) * 3.1) * 1048576), 1);
        }
        sample = new(625623, 29164, 902445000, 1457854200, 1, new() { new(5316, "frpc", 625623, 29164) })
        { Connections = new() {
            new(5316, "frpc", "127.0.0.1:51000", "127.0.0.1:7890", 625623, 29164, 902445000, 1457854200, true, true),
            new(5316, "frpc", "127.0.0.1:51001", "127.0.0.1:22203", 29164, 625623, 1457854200, 902445000, true, false),
            new(5316, "frpc", "192.0.2.10:51002", "203.0.113.20:27000", 125, 80, 1000, 500, false, true) { Protocol = "UDP" }
        } };
        ChangePeriod(TimeSpan.FromDays(7)); if (settings.ShowFloat) ShowFloating();
    }
}
