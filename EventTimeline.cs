using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace FRPMonitor;

public sealed record CoverageInfo(double Seconds, double PeakSeconds, double MinimumSeconds, long UncertainUploadBytes, long UncertainDownloadBytes)
{
    public string Description => "有效采集 " + Units.Duration(Seconds) + " · 秒级最高覆盖 " + Units.Duration(PeakSeconds) + " · 最低覆盖 " + Units.Duration(MinimumSeconds) +
        (UncertainUploadBytes + UncertainDownloadBytes > 0 ? " · 速率不确定流量 ↑ " + Units.Bytes(UncertainUploadBytes) + " ↓ " + Units.Bytes(UncertainDownloadBytes) : "");
}
public sealed record MonitorEvent(DateTimeOffset Time, string Kind, string Detail, DateTimeOffset? Start = null)
{ public string LocalTime => Time.ToLocalTime().ToString("MM/dd HH:mm:ss"); }

public sealed class EventTimeline
{
    private readonly object gate = new();
    private readonly List<MonitorEvent> events = new();
    private readonly string path;
    public string? StorageError { get; private set; }
    private bool protectedSource = true;
    public EventTimeline(string target)
    {
        path = target;
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<List<MonitorEvent>>(File.ReadAllText(path)) ?? throw new InvalidDataException("时间线为空");
                if (loaded.Any(item => item == null || item.Kind == null || item.Detail == null)) throw new InvalidDataException("无效时间线事件");
                events.AddRange(loaded.Where(item => item.Time >= DateTimeOffset.Now.AddDays(-7)).TakeLast(2048));
            }
        }
        catch (Exception e)
        {
            events.Clear(); StorageError = "时间线读取失败：" + e.Message;
            try { Disk.Preserve(path); } catch (Exception backup) { protectedSource = false; StorageError += "；原文件保护失败：" + backup.Message; }
        }
    }
    public void Add(DateTimeOffset time, string kind, string detail, DateTimeOffset? start = null)
    {
        lock (gate)
        {
            events.Add(new(time, kind, detail, start));
            events.RemoveAll(item => item.Time < time.AddDays(-7));
            if (events.Count > 2048) events.RemoveRange(0, events.Count - 2048);
        }
    }
    public MonitorEvent[] Snapshot() { lock (gate) return events.ToArray(); }
    public void Save()
    {
        if (!protectedSource) throw new IOException("时间线原文件保护失败，已阻止覆盖");
        Disk.AtomicJson(path, Snapshot());
    }
}

// The owner awaits these tasks before shutting down, including faulted exports.
public sealed class BackgroundOperations
{
    private readonly HashSet<Task> pending = new();
    private readonly object gate = new();
    private bool closing;
    public Task Run(Func<Task> action)
    {
        lock (gate)
        {
            if (closing) throw new InvalidOperationException("软件正在退出");
            var task = action(); pending.Add(task);
            return Complete(task);
        }
    }
    private async Task Complete(Task task)
    { try { await task; } finally { lock (gate) pending.Remove(task); } }
    public async Task FinishAsync()
    {
        Task[] tasks;
        lock (gate) { closing = true; tasks = pending.ToArray(); }
        try { await Task.WhenAll(tasks); } catch { /* Caller already reports individual errors. */ }
    }
}

public sealed class TimelineWindow : Window
{
    public TimelineWindow(Window owner, EventTimeline timeline, Action<MonitorEvent> locate)
    {
        Owner = owner; Title = "采集事件时间线"; Width = 880; Height = 490;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Icon = AppIcons.WindowIcon;
        Background = Theme.Background; Foreground = Theme.Text;
        var root = new DockPanel { Margin = new(20) };
        var hint = Theme.Label("保留近一周、最多 2048 条事件。双击一条事件，查看对应时间附近的趋势。", 12, Theme.Muted);
        hint.Margin = new(0, 0, 0, 14); DockPanel.SetDock(hint, Dock.Top); root.Children.Add(hint);
        var view = new GridView();
        foreach (var (name, field, width) in new[] { ("时间", "LocalTime", 140), ("事件", "Kind", 140), ("说明", "Detail", 490) })
            view.Columns.Add(new GridViewColumn { Header = name, Width = width, DisplayMemberBinding = new Binding(field) });
        var list = new ListView { View = view, ItemsSource = timeline.Snapshot().Reverse(), Background = Theme.Surface, Foreground = Theme.Text, BorderBrush = Theme.Border };
        list.MouseDoubleClick += (_, _) => { if (list.SelectedItem is MonitorEvent item) { locate(item); Close(); } };
        root.Children.Add(list); Content = root;
    }
}
