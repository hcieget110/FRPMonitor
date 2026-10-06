using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FRPMonitor;

public static class AppIcons
{
    private static readonly Uri ResourceUri = new("pack://application:,,,/Assets/frp-monitor.ico");
    public static ImageSource WindowIcon { get; } = BitmapFrame.Create(ResourceUri, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
    public static System.Drawing.Icon CreateTrayIcon()
    {
        var resource = System.Windows.Application.GetResourceStream(ResourceUri)!;
        using var stream = resource.Stream;
        using var icon = new System.Drawing.Icon(stream, System.Windows.Forms.SystemInformation.SmallIconSize);
        return (System.Drawing.Icon)icon.Clone();
    }
}
