using System;
using System.Collections.Generic;
using FRPMonitor.Exporting;

namespace FRPMonitor.Cloud;

public static class CloudExporter
{
    public static string FileName(TimeSpan range, DateTimeOffset end, bool excel) => "云主机流量-近" + (int)range.TotalMinutes + "分钟-秒级-" + end.ToLocalTime().ToString("yyyyMMdd-HHmmss") + (excel ? ".xlsx" : ".csv");
    public static void Export(string path, IReadOnlyList<TrafficPoint> snapshot, TimeSpan range, DateTimeOffset end, bool excel)
    {
        if (range != TimeSpan.FromMinutes(1) && range != TimeSpan.FromMinutes(5)) throw new ArgumentOutOfRangeException(nameof(range));
        SecondTrafficExporter.Export(path, snapshot, range, end, excel, "云主机整体带宽", "统计默认路由网卡的全部流量");
    }
}
