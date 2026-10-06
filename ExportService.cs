using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FRPMonitor;

public static class ExportService
{
    public static string RangeLabel(TimeSpan range) => range.TotalDays == 7 ? "近1周"
        : range.TotalDays == 1 ? "近1天"
        : range.TotalHours == 2 ? "近2小时"
        : "近" + range.TotalMinutes.ToString("0.##", CultureInfo.InvariantCulture) + "分钟";

    public static string DefaultFileName(TimeSpan range, DateTimeOffset end, bool excel) =>
        "FRP流量-" + RangeLabel(range) + "-" + end.ToLocalTime().ToString("yyyyMMdd-HHmmss") + (excel ? ".xlsx" : ".csv");

    // The snapshot is captured on the caller's UI thread; workers never enumerate mutable history.
    public static Task ExportAsync(History history, string path, bool excel, TimeSpan range, DateTimeOffset end, bool demo, ExportOptions? options = null)
    {
        var required = excel ? ".xlsx" : ".csv";
        if (!Path.GetExtension(path).Equals(required, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请使用 " + required + " 文件扩展名。");
        var snapshot = history.ExportMinutes(range, end);
        return Task.Run(() => { if (excel) ExcelExporter.Export(path, snapshot, range, end, demo, options); else ExportCsv(path, snapshot); });
    }

    public static void ExportCsv(string path, IEnumerable<MinuteBucket> minutes)
    {
        var sb = new StringBuilder("时间(本机时区),上传字节,下载字节,采集秒数,平均上传Mb每秒,平均下载Mb每秒,上传秒级峰值Mb每秒,下载秒级峰值Mb每秒,峰值有效采集秒数,上传秒级最低Mb每秒,下载秒级最低Mb每秒,最低值有效采集秒数,速率不确定上传字节,速率不确定下载字节,异常间隔次数(按接收分钟归档)\r\n");
        foreach (var m in minutes.Where(m => m.Seconds > 0 || m.UncertainIntervals > 0))
            sb.AppendLine(string.Join(",", m.Point.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz"), m.UploadBytes, m.DownloadBytes,
                m.Seconds.ToString("F3", CultureInfo.InvariantCulture), m.Seconds > 0 ? Units.Megabits(m.Point.Upload).ToString("F6", CultureInfo.InvariantCulture) : "", m.Seconds > 0 ? Units.Megabits(m.Point.Download).ToString("F6", CultureInfo.InvariantCulture) : "",
                m.PeakUpload.HasValue ? Units.Megabits(m.PeakUpload.Value).ToString("F6", CultureInfo.InvariantCulture) : "", m.PeakDownload.HasValue ? Units.Megabits(m.PeakDownload.Value).ToString("F6", CultureInfo.InvariantCulture) : "", m.PeakSeconds.ToString("F3", CultureInfo.InvariantCulture),
                m.MinimumUpload.HasValue ? Units.Megabits(m.MinimumUpload.Value).ToString("F6", CultureInfo.InvariantCulture) : "", m.MinimumDownload.HasValue ? Units.Megabits(m.MinimumDownload.Value).ToString("F6", CultureInfo.InvariantCulture) : "", m.MinimumSeconds > 0 ? m.MinimumSeconds.ToString("F3", CultureInfo.InvariantCulture) : "",
                m.UncertainUploadBytes, m.UncertainDownloadBytes, m.UncertainIntervals));
        Disk.AtomicText(path, sb.ToString(), new UTF8Encoding(true));
    }
}
