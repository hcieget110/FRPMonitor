using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FRPMonitor;

public sealed class ExportCompletedWindow : Window
{
    public ExportCompletedWindow(Window owner, string filePath, bool excel, string? message = null)
    {
        Owner = owner; Title = "导出完成"; Width = 530; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Icon = AppIcons.WindowIcon; Background = Theme.Background; Foreground = Theme.Text;
        FontFamily = new FontFamily("Microsoft YaHei UI");
        var content = new StackPanel { Margin = new(24) };
        content.Children.Add(Theme.Label("导出完成", 20, Theme.Text, FontWeights.SemiBold));
        content.Children.Add(new TextBlock { Text = message ?? (excel ? "已导出可编辑的 Excel 折线图和分钟数据。" : Path.GetExtension(filePath).Equals(".json", StringComparison.OrdinalIgnoreCase) ? "已导出采集状态与连接信息诊断报告。" : "已导出分钟级 CSV 数据与已记录秒级峰值。"), Foreground = Theme.Muted, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 12) });
        content.Children.Add(new TextBlock { Text = Path.GetFileName(filePath), Foreground = Theme.Text, FontSize = 13, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = "打开所在文件夹，并选中刚导出的文件。", Foreground = Theme.Muted, FontSize = 12, Margin = new(0, 8, 0, 18) });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var close = Theme.Button("关闭", (_, _) => Close()); close.IsCancel = true; actions.Children.Add(close);
        var reveal = Theme.Button("打开文件所在位置", (_, _) =>
        {
            try { FileLocation.Reveal(filePath); Close(); }
            catch (Exception e) { System.Windows.MessageBox.Show(this, "文件已导出，但打开所在位置失败：\n" + e.Message, "打开文件位置"); }
        });
        reveal.IsDefault = true; actions.Children.Add(reveal); content.Children.Add(actions); Content = content;
    }
}
