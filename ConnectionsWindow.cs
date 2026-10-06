using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;

namespace FRPMonitor;

public sealed class ConnectionsWindow : Window
{
    private sealed class Row : INotifyPropertyChanged
    {
        private readonly HashSet<string> selection;
        public ConnectionTraffic Connection { get; private set; }
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Update(ConnectionTraffic connection) { if (Connection == connection) return; Connection = connection; PropertyChanged?.Invoke(this, new(null)); }
        public Row(ConnectionTraffic connection, HashSet<string> chosen) { Connection = connection; selection = chosen; }
        public bool Selected { get => selection.Contains(Connection.Remote); set { if (value) selection.Add(Connection.Remote); else selection.Remove(Connection.Remote); PropertyChanged?.Invoke(this, new(nameof(Selected))); } }
        public string Process => Connection.Name + " · " + Connection.Pid;
        public string Local => Connection.Local;
        public string Remote => Connection.Remote;
        public string Type => Connection.Protocol + (Connection.Loopback ? " 回环" : " 非回环") + (Connection.Included ? " · 已计入" : " · 未计入");
        public string Upload => Units.Rate(Connection.Upload);
        public string Download => Units.Rate(Connection.Download);
    }
    public string SelectedEndpoints { get; private set; } = "";
    public ConnectionsWindow(Window owner, Func<MonitorFrame?> read)
    {
        Owner = owner; Title = "FRP 连接明细与统计范围"; Width = 1020; Height = 560; MaxHeight = SystemParameters.WorkArea.Height - 40;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Icon = AppIcons.WindowIcon;
        Background = Theme.Background; Foreground = Theme.Text; FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI");
        var chosen = new HashSet<string>();
        var root = new DockPanel { Margin = new(20) };
        var hint = new TextBlock { Text = "显示最近 5 分钟内观测到的连接，每秒更新速率，行顺序保持稳定。可暂停或手动排序；勾选远端地址后仅统计所选连接。代理地址代表本机代理入口。", TextWrapping = TextWrapping.Wrap, Foreground = Theme.Muted, Margin = new(0, 0, 0, 12) };
        DockPanel.SetDock(hint, Dock.Top); root.Children.Add(hint);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 12, 0, 0) };
        var pause = new CheckBox { Content = "暂停刷新", Foreground = Theme.Text, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 16, 0) }; buttons.Children.Add(pause);
        buttons.Children.Add(Theme.Button("关闭", (_, _) => Close()));
        buttons.Children.Add(Theme.Button("仅监控所选连接", (_, _) =>
        {
            if (chosen.Count == 0) { System.Windows.MessageBox.Show(this, "请勾选至少一个连接。", "选择连接"); return; }
            SelectedEndpoints = string.Join(", ", chosen.OrderBy(value => value)); DialogResult = true;
        }));
        DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        var view = new GridView();
        var checkbox = new FrameworkElementFactory(typeof(CheckBox)); checkbox.SetBinding(CheckBox.IsCheckedProperty, new Binding("Selected") { Mode = BindingMode.TwoWay });
        view.Columns.Add(new GridViewColumn { Header = "选择", Width = 48, CellTemplate = new DataTemplate { VisualTree = checkbox } });
        foreach (var (header, property, width) in new[] { ("进程 / PID", "Process", 120), ("本地地址", "Local", 180), ("远端地址", "Remote", 180), ("统计状态", "Type", 145), ("上传", "Upload", 110), ("下载", "Download", 110) })
            view.Columns.Add(new GridViewColumn { Header = header, Width = width, DisplayMemberBinding = new Binding(property) });
        var list = new ListView { View = view, Background = Theme.Surface, Foreground = Theme.Text, BorderBrush = Theme.Border };
        var rows = new ObservableCollection<Row>(); list.ItemsSource = rows;
        var indexed = new Dictionary<(int, string, string, string), Row>();
        root.Children.Add(list); Content = root;
        void Refresh()
        {
            if (pause.IsChecked == true) return;
            var connections = read()?.Sample.Connections ?? new();
            var seen = new HashSet<(int, string, string, string)>();
            foreach (var item in connections)
            {
                var key = (item.Pid, item.Protocol, item.Local, item.Remote); seen.Add(key);
                if (indexed.TryGetValue(key, out var row)) row.Update(item);
                else { indexed[key] = row = new(item, chosen); rows.Add(row); }
            }
            foreach (var key in indexed.Keys.Where(key => !seen.Contains(key)).ToArray()) { rows.Remove(indexed[key]); indexed.Remove(key); }
        }
        buttons.Children.Insert(1, Theme.Button("按当前速率排序", (_, _) =>
        {
            var sorted = rows.OrderByDescending(row => row.Connection.Upload + row.Connection.Download).ToArray();
            for (int i = 0; i < sorted.Length; i++) { var index = rows.IndexOf(sorted[i]); if (index != i) rows.Move(index, i); }
        }));
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) }; timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { Refresh(); timer.Start(); }; Closed += (_, _) => timer.Stop();
    }
}
