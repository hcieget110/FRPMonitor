using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace FRPMonitor;

public static class AppInfo
{
    public static string Version => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "未知";
    public static string DisplayName => "FRP 流量监控 v" + Version;
    public static string ExecutablePath => Environment.ProcessPath ?? Assembly.GetExecutingAssembly().Location;
    public static string Description => DisplayName + "\n运行位置：" + ExecutablePath + "\nExcel 导出：可编辑的原生折线图（.xlsx）";

    public static string DuplicateMessage()
    {
        foreach (var process in Process.GetProcessesByName("FRPMonitor").Where(p => p.Id != Environment.ProcessId))
        {
            using (process)
            {
                var path = QueryPath(process.Id);
                if (path == null) continue;
                string version;
                try { version = FileVersionInfo.GetVersionInfo(path).ProductVersion ?? "未知"; }
                catch { version = "未知"; }
                return "当前正在运行 v" + version + "：\n" + path + "\n\n你打开的是 v" + Version + "。\n请先通过旧版托盘右键菜单「退出」，再打开此版本。";
            }
        }
        return "FRP 流量监控已运行。你打开的是 v" + Version + "。请先从原程序的托盘菜单退出，再打开此版本。";
    }
    private static string? QueryPath(int id)
    {
        var handle = OpenProcess(0x1000, false, id);
        if (handle == IntPtr.Zero) return null;
        try { var buffer = new StringBuilder(32768); var size = buffer.Capacity; return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : null; }
        finally { CloseHandle(handle); }
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int id);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref int size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
