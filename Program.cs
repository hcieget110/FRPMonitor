using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace FRPMonitor;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--self-test") return SelfTests.Run(args[1]);
        if (args.Length >= 2 && args[0] == "--probe") return Probe.Run(args[1], args.Length >= 3 ? int.Parse(args[2]) : 35);
        if (args.Length >= 2 && args[0] == "--startup-test") return StartupService.RunSelfTest(args[1]);
        var preview = args.Length >= 2 && args[0] == "--preview";
        var startup = Array.IndexOf(args, "--startup") >= 0;
        using var mutex = new Mutex(true, preview ? "Local\\FRPMonitor-Preview" : "Local\\FRPMonitor", out var created);
        if (!created) { if (!startup) System.Windows.MessageBox.Show(AppInfo.DuplicateMessage(), AppInfo.DisplayName); return 0; }
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Theme.Install(app);
        app.DispatcherUnhandledException += (_, e) =>
        {
            try { Directory.CreateDirectory(Settings.DataFolder); File.AppendAllText(Path.Combine(Settings.DataFolder, "errors.log"), DateTimeOffset.Now + " " + e.Exception + Environment.NewLine); } catch { }
            System.Windows.MessageBox.Show("程序遇到错误：" + e.Exception.Message, "FRP 流量监控"); e.Handled = true;
        };
        var folder = preview ? Path.GetFullPath(args[1]) : null;
        var window = new MainWindow(preview ? new Settings { ShowFloat = false } : Settings.Load(), preview, folder, startup);
        if (preview)
        {
            Directory.CreateDirectory(folder!);
            window.ContentRendered += (_, _) =>
            {
                var captureTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                var stage = 0;
                SettingsWindow? settingsPreview = null;
                ExportCompletedWindow? exportPreview = null;
                ConnectionsWindow? connectionsPreview = null;
                ExportOptionsWindow? optionsPreview = null;
                captureTimer.Tick += (_, _) =>
                {
                    if (stage == 0) { SaveImage(window, Path.Combine(folder!, "week.png")); window.ExportPreview(Path.Combine(folder!, "week-demo.xlsx")); window.ChangePeriod(TimeSpan.FromDays(1)); }
                    else if (stage == 1) { SaveImage(window, Path.Combine(folder!, "day.png")); window.ExportPreview(Path.Combine(folder!, "day-demo.xlsx")); window.Width = window.MinWidth; window.Height = window.MinHeight; }
                    else if (stage == 2) { SaveImage(window, Path.Combine(folder!, "minimum.png")); window.Width = 1060; window.Height = 820; }
                    else if (stage == 3)
                    {
                        if (window.Floating != null) SaveImage(window.Floating, Path.Combine(folder!, "floating.png"));
                        using (var trayIcon = AppIcons.CreateTrayIcon())
                        using (var bitmap = trayIcon.ToBitmap()) bitmap.Save(Path.Combine(folder!, "tray-icon.png"), System.Drawing.Imaging.ImageFormat.Png);
                        window.SetFloatTopmost(false); window.SetMainTopmost(true);
                        File.WriteAllText(Path.Combine(folder!, "window-state.json"), System.Text.Json.JsonSerializer.Serialize(new { Version = AppInfo.Version, MainTitle = window.Title, TrayMenu = window.PreviewTrayMenu(), MainTopmost = window.Topmost, FloatTopmost = window.Floating?.Topmost, FloatVisibleOnLaunch = window.Floating?.IsVisible,
                            OpacityMenu = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(System.Linq.Enumerable.OfType<System.Windows.Controls.MenuItem>(window.Floating!.ContextMenu!.Items), item => new { Title = item.Header?.ToString(), item.IsCheckable, item.IsChecked })) }));
                        settingsPreview = new SettingsWindow(window, new Settings { FloatTopmost = false, MainTopmost = true }, true); settingsPreview.Show();
                    }
                    else if (stage == 4)
                    {
                        SaveImage(settingsPreview!, Path.Combine(folder!, "settings.png")); settingsPreview!.Close();
                        exportPreview = new ExportCompletedWindow(window, Path.Combine(folder!, "week-demo.xlsx"), true); exportPreview.Show();
                    }
                    else if (stage == 5)
                    {
                        SaveImage(exportPreview!, Path.Combine(folder!, "export-completed.png")); exportPreview!.Close();
                        var demoSample = new Snapshot(100, 50, 1000, 500, 1, new()) { Connections = new() {
                            new(5316, "frpc", "127.0.0.1:51000", "127.0.0.1:7890", 625623, 29164, 1000, 500, true, true),
                            new(5316, "frpc", "127.0.0.1:51001", "127.0.0.1:22203", 29164, 625623, 500, 1000, true, false),
                            new(5316, "frpc", "192.0.2.10:51002", "203.0.113.20:27000", 125, 80, 1000, 500, false, true) { Protocol = "UDP" }
                        } };
                        connectionsPreview = new ConnectionsWindow(window, () => new(demoSample, true, "合成数据演示", 0, 0, DateTimeOffset.Now)); connectionsPreview.Show();
                    }
                    else if (stage == 6)
                    {
                        SaveImage(connectionsPreview!, Path.Combine(folder!, "connections.png")); connectionsPreview!.Close();
                        optionsPreview = new ExportOptionsWindow(window, TimeSpan.FromDays(7), true); optionsPreview.Show();
                    }
                    else
                    {
                        SaveImage(optionsPreview!, Path.Combine(folder!, "export-options.png")); optionsPreview!.Close();
                        captureTimer.Stop(); window.Quit();
                    }
                    stage++;
                };
                captureTimer.Start();
            };
        }
        app.Run(window);
        return 0;
    }
    private static void SaveImage(FrameworkElement element, string path)
    {
        element.UpdateLayout();
        var width = element.ActualWidth; var height = element.ActualHeight;
        if (element is Window window && window.Content is FrameworkElement content)
        { width = content.ActualWidth + content.Margin.Left + content.Margin.Right; height = content.ActualHeight + content.Margin.Top + content.Margin.Bottom; }
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(width), (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32); bmp.Render(element);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bmp)); using var file = File.Create(path); png.Save(file);
    }
}
