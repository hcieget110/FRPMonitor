using System;
using System.Windows;
using System.Windows.Controls;

namespace FRPMonitor;

public sealed class SettingsWindow : Window
{
    public string Names { get; private set; } = "";
    public bool Loopback { get; private set; }
    public bool FloatTopmost { get; private set; }
    public bool MainTopmost { get; private set; }
    public int RateAverageSeconds { get; private set; }
    public bool? StartWithWindows { get; private set; }
    public ConnectionMode ConnectionMode { get; private set; }
    public string EndpointFilters { get; private set; } = "";
    public double FixedScaleMbps { get; private set; }
    public SettingsWindow(Window owner, Settings settings, bool preview = false)
    {
        Owner = owner; Title = "软件设置"; Width = 510; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Icon = AppIcons.WindowIcon;
        Background = Theme.Background; FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI");
        var stack = new StackPanel { Margin = new(26) };
        stack.Children.Add(Theme.Label("监控哪些进程", 20, Theme.Text, FontWeights.SemiBold));
        stack.Children.Add(new TextBlock { Text = "填写进程名，用逗号分隔。进程重启后自动继续跟踪。", Foreground = Theme.Muted, Margin = new(0, 12, 0, 12), TextWrapping = TextWrapping.Wrap });
        var names = new TextBox { Text = settings.ProcessNames, IsReadOnly = true, Padding = new(10), FontSize = 14, Background = Theme.Surface, Foreground = Theme.Muted, BorderBrush = Theme.Border, ToolTip = "点击右侧“编辑”后修改进程名。" };
        var namesRow = new Grid();
        namesRow.ColumnDefinitions.Add(new ColumnDefinition());
        namesRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var editNames = Theme.Button("编辑", (sender, _) =>
        {
            names.IsReadOnly = !names.IsReadOnly;
            names.Foreground = names.IsReadOnly ? Theme.Muted : Theme.Text;
            names.BorderBrush = names.IsReadOnly ? Theme.Border : Theme.Upload;
            ((Button)sender).Content = names.IsReadOnly ? "编辑" : "锁定";
            if (!names.IsReadOnly) { names.Focus(); names.CaretIndex = names.Text.Length; }
        });
        editNames.MinWidth = 86;
        namesRow.Children.Add(names);
        Grid.SetColumn(editNames, 1); namesRow.Children.Add(editNames);
        stack.Children.Add(namesRow);
        var loopback = new CheckBox { Content = "计入本地回环流量（127.0.0.1 / ::1）", IsChecked = settings.IncludeLoopback, Foreground = Theme.Text, Margin = new(0, 22, 0, 10) };
        stack.Children.Add(loopback);
        stack.Children.Add(Theme.Label("连接统计范围", 12, Theme.Muted));
        var connectionMode = new ComboBox { ItemsSource = new[] { "全部进程流量（使用上面的回环开关）", "仅指定连接（隧道 / 本地代理）", "非回环连接 + 指定回环代理" }, SelectedIndex = (int)settings.ConnectionMode, Margin = new(0, 8, 0, 8), Padding = new(6) };
        stack.Children.Add(connectionMode);
        var endpoints = new TextBox { Text = settings.EndpointFilters, Padding = new(8), Background = Theme.Surface, Foreground = Theme.Text, BorderBrush = Theme.Border };
        stack.Children.Add(endpoints);
        stack.Children.Add(new TextBlock { Text = "填写 IP:端口，用逗号分隔，例如 127.0.0.1:7890。也可在主窗口“连接明细”中选择。指定模式按两端地址匹配，不自动识别代理的最终云端线路。", FontSize = 10, Foreground = Theme.Muted, TextWrapping = TextWrapping.Wrap, Margin = new(0, 4, 0, 8) });
        connectionMode.SelectionChanged += (_, _) => { endpoints.IsEnabled = connectionMode.SelectedIndex != 0; loopback.IsEnabled = connectionMode.SelectedIndex == 0; };
        endpoints.IsEnabled = connectionMode.SelectedIndex != 0; loopback.IsEnabled = connectionMode.SelectedIndex == 0;
        var topmost = new CheckBox { Content = "悬浮窗始终置顶（显示在其他软件前面）", IsChecked = settings.FloatTopmost, Foreground = Theme.Text, Margin = new(0, 10, 0, 16) };
        stack.Children.Add(topmost);
        var mainTopmost = new CheckBox { Content = "主窗口始终置顶（显示在其他软件前面）", IsChecked = settings.MainTopmost, Foreground = Theme.Text, Margin = new(0, 0, 0, 16) };
        stack.Children.Add(mainTopmost);
        var startup = new CheckBox { Content = "开机自启（登录 Windows 后自动采集）", Foreground = Theme.Text, Margin = new(0, 0, 0, 8) };
        var startupHint = new TextBlock { Text = "开启后在托盘启动，同时显示悬浮窗。", Foreground = Theme.Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 16) };
        try { startup.IsChecked = preview ? false : StartupService.IsEnabled(); }
        catch (Exception e) { startup.IsEnabled = false; startupHint.Text = "无法读取自启状态：" + e.Message; }
        stack.Children.Add(startup); stack.Children.Add(startupHint);
        stack.Children.Add(Theme.Label("速率显示平均时长（每秒刷新，流量总量不受影响）", 12, Theme.Muted));
        var smoothing = new ComboBox { Margin = new(0, 8, 0, 16), Padding = new(8), ItemsSource = new[] { "1 秒：变化最快", "3 秒：减少波动", "5 秒：更平稳" }, SelectedIndex = settings.RateAverageSeconds == 1 ? 0 : settings.RateAverageSeconds == 5 ? 2 : 1 };
        stack.Children.Add(smoothing);
        stack.Children.Add(Theme.Label("图表纵轴上限（Mb/s；0 为自动）", 12, Theme.Muted));
        var fixedScale = new TextBox { Text = settings.FixedScaleMbps.ToString(System.Globalization.CultureInfo.InvariantCulture), Padding = new(6), Margin = new(0, 6, 0, 4), Background = Theme.Surface, Foreground = Theme.Text, BorderBrush = Theme.Border };
        stack.Children.Add(fixedScale);
        stack.Children.Add(new TextBlock { Text = "自动刻度遇到峰值立即升高，降低前保持 10 秒。固定上限低于实际带宽时，曲线顶部会截断。", Foreground = Theme.Muted, FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 10) });
        stack.Children.Add(new TextBlock { Text = "速率使用小写 b（比特）：1 Mb/s = 1,000,000 b/s。累计流量保留大写 B（字节）。修改监控范围会切换相应历史记录。", Foreground = Theme.Muted, TextWrapping = TextWrapping.Wrap, LineHeight = 22 });
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 22, 0, 0) };
        row.Children.Add(Theme.Button("取消", (_, _) => Close()));
        row.Children.Add(Theme.Button("保存设置", (_, _) =>
        {
            var candidate = new Settings { ProcessNames = names.Text };
            if (candidate.Names.Length == 0 || candidate.Names.Length > 20)
            { System.Windows.MessageBox.Show(this, "请填写 1 到 20 个进程名。", "检查设置"); return; }
            try
            {
                var rules = EndpointRule.ParseAll(endpoints.Text);
                if (connectionMode.SelectedIndex != 0 && rules.Length == 0) throw new FormatException("指定连接模式需要至少一个 IP:端口。");
                if (!double.TryParse(fixedScale.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var scale) || !double.IsFinite(scale) || scale < 0 || scale > 1_000_000)
                    throw new FormatException("纵轴上限填写 0 到 1000000 的数字，0 为自动。");
                ConnectionMode = (ConnectionMode)connectionMode.SelectedIndex; EndpointFilters = string.Join(", ", Array.ConvertAll(rules, rule => rule.ToString())); FixedScaleMbps = scale;
            }
            catch (FormatException e) { System.Windows.MessageBox.Show(this, e.Message, "检查设置"); return; }
            Names = string.Join(", ", candidate.Names); Loopback = loopback.IsChecked == true; FloatTopmost = topmost.IsChecked == true; MainTopmost = mainTopmost.IsChecked == true;
            RateAverageSeconds = smoothing.SelectedIndex == 0 ? 1 : smoothing.SelectedIndex == 2 ? 5 : 3;
            StartWithWindows = startup.IsEnabled ? startup.IsChecked == true : null;
            DialogResult = true;
        }));
        stack.Children.Add(row); Content = new ScrollViewer { Content = stack, MaxHeight = Math.Max(400, SystemParameters.WorkArea.Height - 80), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}
