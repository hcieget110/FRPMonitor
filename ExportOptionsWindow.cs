using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace FRPMonitor;

public sealed record ExportOptions(bool SplitDays = false, bool PeakCharts = false);
public sealed class ExportOptionsWindow : Window
{
    public DateTimeOffset Start { get; private set; }
    public DateTimeOffset End { get; private set; }
    public ExportOptions Options { get; private set; } = new();
    public string FileRangeName { get; private set; } = "";
    public ExportOptionsWindow(Window owner, TimeSpan selectedRange, bool excel, DateTimeOffset? selectedEnd = null)
    {
        Owner = owner; Title = "选择导出范围"; Width = 540; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ResizeMode = ResizeMode.NoResize; Icon = AppIcons.WindowIcon;
        Background = Theme.Background; Foreground = Theme.Text; FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI");
        var stack = new StackPanel { Margin = new(24) }; Content = stack;
        var preset = new ComboBox { ItemsSource = new[] { "当前范围：" + ExportService.RangeLabel(selectedRange), "今天 00:00 至今", "自定义起止时间" }, SelectedIndex = 0, Padding = new(6), Margin = new(0, 0, 0, 16) };
        stack.Children.Add(preset);
        DatePicker Picker(string title, out TextBox time)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 0, 0, 12) };
            row.Children.Add(Theme.Label(title, 12));
            var date = new DatePicker { Width = 155, Margin = new(16, 0, 10, 0) }; row.Children.Add(date);
            time = new TextBox { Width = 100, Padding = new(6), Background = Theme.Surface, Foreground = Theme.Text }; row.Children.Add(time); stack.Children.Add(row); return date;
        }
        var beginDate = Picker("开始", out var beginTime); var endDate = Picker("结束", out var endTime);
        var filling = false;
        void Fill(DateTime start, DateTime end)
        {
            filling = true;
            try { beginDate.SelectedDate = start.Date; endDate.SelectedDate = end.Date; beginTime.Text = start.ToString("HH:mm:ss"); endTime.Text = end.ToString("HH:mm:ss"); }
            finally { filling = false; }
        }
        var anchor = selectedEnd?.LocalDateTime ?? DateTime.Now;
        Fill(anchor - selectedRange, anchor);
        preset.SelectionChanged += (_, _) => { if (preset.SelectedIndex < 2) Fill(preset.SelectedIndex == 1 ? DateTime.Today : anchor - selectedRange, preset.SelectedIndex == 1 ? DateTime.Now : anchor); };
        void Edited() { if (!filling) preset.SelectedIndex = 2; }
        beginDate.SelectedDateChanged += (_, _) => Edited(); endDate.SelectedDateChanged += (_, _) => Edited();
        beginTime.TextChanged += (_, _) => Edited(); endTime.TextChanged += (_, _) => Edited();
        var split = new CheckBox { Content = "按本机自然日拆分图表（保留全部分钟数据）", Foreground = Theme.Text, IsChecked = selectedRange >= TimeSpan.FromDays(1), Margin = new(0, 4, 0, 10), Visibility = excel ? Visibility.Visible : Visibility.Collapsed };
        var peaks = new CheckBox { Content = "增加秒级峰值原生折线图", Foreground = Theme.Text, IsChecked = true, Margin = new(0, 0, 0, 12), Visibility = excel ? Visibility.Visible : Visibility.Collapsed };
        stack.Children.Add(split); stack.Children.Add(peaks);
        stack.Children.Add(new TextBlock { Text = "范围最多 7 天，按分钟边界选取，边缘最多含额外一分钟。分钟平均与秒级峰值分别展示；旧历史无法还原峰值，保留空缺。日期和时间按本机时区。", TextWrapping = TextWrapping.Wrap, Foreground = Theme.Muted, FontSize = 11 });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 16, 0, 0) };
        buttons.Children.Add(Theme.Button("取消", (_, _) => Close()));
        buttons.Children.Add(Theme.Button("选择保存位置", (_, _) =>
        {
            try
            {
                DateTimeOffset Parse(DatePicker date, TextBox time)
                {
                    if (!date.SelectedDate.HasValue || !TimeSpan.TryParseExact(time.Text, "hh\\:mm\\:ss", CultureInfo.InvariantCulture, out var span) || span.TotalHours >= 24)
                        throw new FormatException("请选择日期，时间填写 HH:mm:ss。");
                    var value = DateTime.SpecifyKind(date.SelectedDate.Value.Date + span, DateTimeKind.Unspecified);
                    if (TimeZoneInfo.Local.IsInvalidTime(value)) throw new FormatException("这个本地时间不存在，请调整时间。");
                    return new DateTimeOffset(value, TimeZoneInfo.Local.GetUtcOffset(value));
                }
                Start = Parse(beginDate, beginTime); End = Parse(endDate, endTime);
                if (End <= Start || End - Start > TimeSpan.FromDays(7) || End > DateTimeOffset.Now.AddSeconds(1)) throw new FormatException("结束时间须晚于开始、不超过当前时间，范围最多 7 天。");
                Options = new(split.IsChecked == true, peaks.IsChecked == true);
                FileRangeName = preset.SelectedIndex == 1 ? "今天" : preset.SelectedIndex == 2 || selectedEnd.HasValue ? "自定义-" + Start.ToString("MMdd-HHmmss") + "至" + End.ToString("MMdd-HHmmss") : ExportService.RangeLabel(selectedRange);
                DialogResult = true;
            }
            catch (FormatException e) { System.Windows.MessageBox.Show(this, e.Message, "检查导出范围"); }
        }));
        stack.Children.Add(buttons);
    }
}
