using System;
using System.Windows;
using System.Windows.Controls;

namespace FRPMonitor.Cloud;

public sealed class CloudSettingsWindow : Window
{
    public CloudProfile Profile { get; private set; }
    public CloudSettingsWindow(Window owner, CloudProfile profile, bool preview = false)
    {
        Profile = profile; Owner = owner; Title = "云主机设置"; Width = 490; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Icon = AppIcons.WindowIcon; Background = Theme.Background; FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI");
        var content = new StackPanel { Margin = new(24) };
        content.Children.Add(Theme.Label("云主机整体流量", 19, Theme.Text, FontWeights.SemiBold));
        content.Children.Add(new TextBlock { Text = "SSH 每秒采样，只保留最近 5 分钟。统计默认路由网卡的全部流量；无需 root。云主机需有 python3。", Foreground = Theme.Muted, TextWrapping = TextWrapping.Wrap, Margin = new(0, 10, 0, 15) });
        var enabled = new CheckBox { Content = "启用云主机流量监控", IsChecked = profile.Enabled, Foreground = Theme.Text, Margin = new(0, 0, 0, 12) }; content.Children.Add(enabled);
        TextBox Field(string label, string value)
        {
            content.Children.Add(Theme.Label(label, 11, Theme.Muted));
            var field = new TextBox { Text = value, Padding = new(7), Margin = new(0, 5, 0, 12), Background = Theme.Surface, Foreground = Theme.Text, BorderBrush = Theme.Border }; content.Children.Add(field); return field;
        }
        var host = Field("云主机地址", profile.Host); var port = Field("SSH 端口", profile.Port.ToString()); var user = Field("SSH 用户名", profile.User);
        content.Children.Add(Theme.Label("SSH 密码（留空保留已保存的密码）", 11, Theme.Muted));
        var password = new PasswordBox { Padding = new(7), Background = Theme.Surface, Foreground = Theme.Text, BorderBrush = Theme.Border, Margin = new(0, 5, 0, 12) }; content.Children.Add(password);
        var fingerprints = Field("主机 SHA256 指纹（留空读取本机 known_hosts）", profile.Fingerprints);
        content.Children.Add(new TextBlock { Text = "密码保存在 Windows 凭据管理器，不写入设置、导出文件或公开源码。连接时校验主机指纹。", Foreground = Theme.Muted, TextWrapping = TextWrapping.Wrap, FontSize = 11 });
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 18, 0, 0) };
        row.Children.Add(Theme.Button("取消", (_, _) => Close()));
        row.Children.Add(Theme.Button("保存并连接", async (_, _) =>
        {
            try
            {
                if (!int.TryParse(port.Text, out var number)) throw new FormatException("SSH 端口填写整数。");
                var candidate = new CloudProfile { Enabled = enabled.IsChecked == true, Host = host.Text.Trim(), Port = number, User = user.Text.Trim(), Fingerprints = fingerprints.Text.Trim() };
                var enteredPassword = password.Password;
                IsEnabled = false;
                await System.Threading.Tasks.Task.Run(() =>
                {
                    if (candidate.Fingerprints.Length == 0) candidate.Fingerprints = CloudProfile.KnownFingerprints(candidate.Host, candidate.Port);
                    candidate.Validate();
                    if (!preview)
                    {
                        if (enteredPassword.Length > 0) CloudCredential.Save(candidate, enteredPassword);
                        if (candidate.Enabled && string.IsNullOrEmpty(CloudCredential.Read(candidate))) throw new FormatException("请填写 SSH 密码。");
                        candidate.Save();
                    }
                });
                password.Clear(); Profile = candidate; DialogResult = true;
            }
            catch (Exception e) { System.Windows.MessageBox.Show(this, e.Message, "检查云主机设置"); }
            finally { IsEnabled = true; }
        }));
        content.Children.Add(row); Content = content;
    }
}
