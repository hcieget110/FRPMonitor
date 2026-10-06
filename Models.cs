using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace FRPMonitor;

public sealed class Settings
{
    public string ProcessNames { get; set; } = "frpc, frps";
    public bool IncludeLoopback { get; set; }
    public bool ShowFloat { get; set; } = true;
    public bool FloatTopmost { get; set; } = true;
    public bool MainTopmost { get; set; }
    public int RateAverageSeconds { get; set; } = 3;
    public ConnectionMode ConnectionMode { get; set; }
    public string EndpointFilters { get; set; } = "";
    public double FixedScaleMbps { get; set; }
    public double FloatLeft { get; set; } = -1;
    public double FloatTop { get; set; } = -1;
    public double FloatOpacity { get; set; } = 0.94;
    public static string DataFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FRPMonitor");
    [JsonIgnore] public string? LoadWarning { get; private set; }
    private bool sourceProtected = true;
    [JsonIgnore]
    public string[] Names => (ProcessNames ?? "").Split(new[] { ',', ';', '，', ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(n => Path.GetFileNameWithoutExtension(n.Trim()).ToLowerInvariant()).Distinct().OrderBy(n => n).ToArray();
    [JsonIgnore]
    public string Scope => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(",", Names) + ":" +
        (ConnectionMode == ConnectionMode.All ? IncludeLoopback.ToString() : ConnectionMode + ":" + string.Join(",", EndpointRule.ParseAll(EndpointFilters).Select(rule => rule.ToString()).OrderBy(rule => rule))))))[..16];
    public static Settings Load(string? folder = null)
    {
        var path = Path.Combine(folder ?? DataFolder, "settings.json");
        if (!File.Exists(path)) return new();
        try
        {
            var result = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? throw new InvalidDataException("设置内容为空");
            if (result.Normalize())
            {
                result.LoadWarning = "部分设置无效，已恢复默认值";
                try { result.LoadWarning += "；原文件保留于 " + Disk.Preserve(path); }
                catch (Exception backup) { result.sourceProtected = false; result.LoadWarning += "；备份失败：" + backup.Message; }
            }
            return result;
        }
        catch (Exception e)
        {
            var result = new Settings { LoadWarning = "设置读取失败：" + e.Message };
            try { result.LoadWarning += "；原文件保留于 " + Disk.Preserve(path); }
            catch (Exception backup) { result.sourceProtected = false; result.LoadWarning += "；备份失败：" + backup.Message; }
            return result;
        }
    }
    public bool Normalize()
    {
        bool changed = false;
        if (Names.Length == 0) { ProcessNames = "frpc, frps"; changed = true; }
        if (EndpointFilters == null) { EndpointFilters = ""; changed = true; }
        if (!Enum.IsDefined(ConnectionMode)) { ConnectionMode = ConnectionMode.All; changed = true; }
        try { var rules = EndpointRule.ParseAll(EndpointFilters); if (ConnectionMode != ConnectionMode.All && rules.Length == 0) throw new FormatException(); }
        catch { EndpointFilters = ""; ConnectionMode = ConnectionMode.All; changed = true; }
        if (RateAverageSeconds is not (1 or 3 or 5)) { RateAverageSeconds = 3; changed = true; }
        if (!double.IsFinite(FixedScaleMbps) || FixedScaleMbps < 0) { FixedScaleMbps = 0; changed = true; }
        if (!double.IsFinite(FloatOpacity) || FloatOpacity < .2 || FloatOpacity > 1) { FloatOpacity = .94; changed = true; }
        if (!double.IsFinite(FloatLeft)) { FloatLeft = -1; changed = true; }
        if (!double.IsFinite(FloatTop)) { FloatTop = -1; changed = true; }
        return changed;
    }
    public void Save()
    { if (!sourceProtected) throw new IOException("无效设置的原文件尚未成功保护，已阻止覆盖"); Disk.AtomicJson(Path.Combine(DataFolder, "settings.json"), this); }
}

public static class Disk
{
    public static string Preserve(string path)
    {
        var copy = path + ".damaged-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
        File.Copy(path, copy, false); return copy;
    }
    public static void AtomicJson<T>(string path, T data)
        => AtomicText(path, JsonSerializer.Serialize(data));
    public static void AtomicText(string path, string content, Encoding? encoding = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try { File.WriteAllText(temp, content, encoding ?? new UTF8Encoding(false)); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

public sealed record TrafficPoint(DateTimeOffset Time, double Upload, double Download, double Seconds = 1, double IntervalSeconds = 1);
public sealed class MinuteBucket
{
    public long UnixMinute { get; set; }
    public long UploadBytes { get; set; }
    public long DownloadBytes { get; set; }
    public double Seconds { get; set; }
    public double? PeakUpload { get; set; }
    public double? PeakDownload { get; set; }
    public double PeakSeconds { get; set; }
    public double? MinimumUpload { get; set; }
    public double? MinimumDownload { get; set; }
    public double MinimumSeconds { get; set; }
    // Known bytes from a delayed drain; their timing cannot be reconstructed reliably.
    public long UncertainUploadBytes { get; set; }
    public long UncertainDownloadBytes { get; set; }
    public int UncertainIntervals { get; set; }
    [JsonIgnore]
    public TrafficPoint Point => new(DateTimeOffset.FromUnixTimeSeconds(UnixMinute * 60), UploadBytes / Math.Max(Seconds, 0.001), DownloadBytes / Math.Max(Seconds, 0.001), Seconds, 60);
}

public sealed class History
{
    private readonly object gate = new();
    private readonly SortedDictionary<long, MinuteBucket> minutes = new();
    private readonly Queue<TrafficPoint> live = new();
    private readonly string path;
    private readonly SemaphoreSlim saveGate = new(1, 1);
    private volatile string? readError, writeError;
    private bool protectedSource = true;
    public string? StorageError => readError == null ? writeError : readError + (writeError == null ? "" : "；" + writeError);
    public EventTimeline Timeline { get; }
    public History(string scope, string? folder = null)
    {
        path = Path.Combine(folder ?? Settings.DataFolder, "history-" + scope + ".json");
        Timeline = new(Path.Combine(folder ?? Settings.DataFolder, "events-" + scope + ".json"));
        try
        {
            if (File.Exists(path))
                LoadBuckets(path);
        }
        catch (Exception e)
        {
            readError = "历史读取失败：" + e.Message; protectedSource = false;
            try { readError += "；已保留原文件 " + Disk.Preserve(path); protectedSource = true; }
            catch (Exception backup) { readError += "；原文件保护失败：" + backup.Message; }
            try { if (File.Exists(path + ".bak")) { LoadBuckets(path + ".bak"); readError += "；已恢复上次备份"; } }
            catch (Exception backup) { readError += "；备份读取失败：" + backup.Message; }
        }
        Prune(DateTimeOffset.UtcNow);
    }
    private void LoadBuckets(string source)
    {
        var loaded = JsonSerializer.Deserialize<List<MinuteBucket>>(File.ReadAllText(source)) ?? throw new InvalidDataException("历史内容为空");
        static bool Rate(double? value) => value == null || double.IsFinite(value.Value) && value >= 0;
        if (loaded.Any(m => m == null || m.UnixMinute < 0 || m.UnixMinute > DateTimeOffset.MaxValue.ToUnixTimeSeconds() / 60 ||
            !double.IsFinite(m.Seconds) || m.Seconds < 0 || m.UploadBytes < 0 || m.DownloadBytes < 0 ||
            !double.IsFinite(m.PeakSeconds) || m.PeakSeconds < 0 || !double.IsFinite(m.MinimumSeconds) || m.MinimumSeconds < 0 ||
            !Rate(m.PeakUpload) || !Rate(m.PeakDownload) || !Rate(m.MinimumUpload) || !Rate(m.MinimumDownload) ||
            m.UncertainUploadBytes < 0 || m.UncertainDownloadBytes < 0 || m.UncertainIntervals < 0) || loaded.Select(m => m.UnixMinute).Distinct().Count() != loaded.Count)
            throw new InvalidDataException("历史数据含无效数值或重复分钟");
        foreach (var bucket in loaded) if (bucket.Seconds > 0 || bucket.UncertainIntervals > 0) minutes[bucket.UnixMinute] = bucket;
    }
    public void Add(DateTimeOffset time, long upload, long download, double seconds)
    {
        lock (gate)
        {
        if (!double.IsFinite(seconds) || seconds <= 0 || upload < 0 || download < 0) return;
        if (seconds > 5)
        {
            var key = time.ToUnixTimeSeconds() / 60;
            if (!minutes.TryGetValue(key, out var delayed)) minutes[key] = delayed = new() { UnixMinute = key };
            delayed.UncertainUploadBytes += upload; delayed.UncertainDownloadBytes += download; delayed.UncertainIntervals++;
            Timeline.Add(time, "采样间隔过长", seconds.ToString("F1") + " 秒；已保留 ↑ " + Units.Bytes(upload) + " ↓ " + Units.Bytes(download) + "，无法还原速率和峰值。", time.AddSeconds(-seconds));
            Prune(time); return;
        }
        live.Enqueue(new(time, upload / seconds, download / seconds, seconds));
        while (live.Count > 0 && live.Peek().Time < time.AddHours(-2)) live.Dequeue();
        // Split intervals at minute boundaries so midnight and minute totals remain correct.
        var cursor = time.AddSeconds(-seconds);
        var remainingUp = upload;
        var remainingDown = download;
        while (cursor < time)
        {
            var key = cursor.ToUnixTimeSeconds() / 60;
            var end = DateTimeOffset.FromUnixTimeSeconds((key + 1) * 60);
            if (end > time) end = time;
            var span = (end - cursor).TotalSeconds;
            var last = end == time;
            var up = last ? remainingUp : (long)Math.Round(upload * span / seconds);
            var down = last ? remainingDown : (long)Math.Round(download * span / seconds);
            if (!minutes.TryGetValue(key, out var bucket)) minutes[key] = bucket = new() { UnixMinute = key };
            bucket.UploadBytes += up; bucket.DownloadBytes += down; bucket.Seconds += span;
            bucket.PeakUpload = Math.Max(bucket.PeakUpload ?? 0, upload / seconds);
            bucket.PeakDownload = Math.Max(bucket.PeakDownload ?? 0, download / seconds);
            bucket.PeakSeconds += span;
            // Constant work per sample; only minute extrema and their coverage are persisted.
            bucket.MinimumUpload = Math.Min(bucket.MinimumUpload ?? double.PositiveInfinity, upload / seconds);
            bucket.MinimumDownload = Math.Min(bucket.MinimumDownload ?? double.PositiveInfinity, download / seconds);
            bucket.MinimumSeconds += span;
            remainingUp -= up; remainingDown -= down;
            cursor = end;
        }
        Prune(time);
        }
    }
    private void Prune(DateTimeOffset now)
    {
        var cutoff = now.AddDays(-7).ToUnixTimeSeconds() / 60;
        foreach (var key in minutes.Keys.TakeWhile(k => k < cutoff).ToArray()) minutes.Remove(key);
    }
    // Export raw samples only. Persisted minute buckets cannot reconstruct per-second history.
    public List<TrafficPoint> ExportSeconds(TimeSpan range, DateTimeOffset end)
    {
        if (range <= TimeSpan.Zero || range > TimeSpan.FromMinutes(5)) throw new ArgumentOutOfRangeException(nameof(range));
        lock (gate) return live.Where(p => p.Time.ToUnixTimeSeconds() > end.ToUnixTimeSeconds() - (long)range.TotalSeconds && p.Time.ToUnixTimeSeconds() <= end.ToUnixTimeSeconds()).ToList();
    }
    public List<TrafficPoint> Points(TimeSpan range, DateTimeOffset now)
    {
        lock (gate)
        {
        var cutoff = now - range;
        // Minute history is also available immediately after restart.
        if (range.TotalHours <= 2 && live.Count > 0)
        {
            var firstLive = live.Peek().Time;
            var earlier = firstLive <= cutoff.AddMinutes(1) ? Enumerable.Empty<TrafficPoint>() : minutes.Values.Where(m =>
            {
                var start = DateTimeOffset.FromUnixTimeSeconds(m.UnixMinute * 60);
                return m.Seconds > 0 && start >= cutoff && start.AddMinutes(1) < firstLive;
            }).Select(m => m.Point);
            return earlier.Concat(live.Where(p => p.Time >= cutoff && p.Time <= now)).ToList();
        }
        return minutes.Values.Where(m =>
        {
            var start = DateTimeOffset.FromUnixTimeSeconds(m.UnixMinute * 60);
            return m.Seconds > 0 && start >= cutoff && start <= now;
        }).Select(m => m.Point).ToList();
        }
    }
    public (long Up, long Down, double Seconds) Totals(TimeSpan range, DateTimeOffset now)
    {
        lock (gate)
        {
        var cutoff = (now - range).ToUnixTimeSeconds() / 60;
        var last = now.ToUnixTimeSeconds() / 60;
        long up = 0, down = 0;
        double seconds = 0;
        foreach (var item in minutes.Values)
        {
            if (item.UnixMinute < cutoff) continue;
            if (item.UnixMinute > last) break;
            up = checked(up + item.UploadBytes + item.UncertainUploadBytes); down = checked(down + item.DownloadBytes + item.UncertainDownloadBytes); seconds += item.Seconds;
        }
        return (up, down, seconds);
        }
    }
    public void Save() => SaveAsync().GetAwaiter().GetResult();
    public Task SaveAsync()
    {
        // Enqueue persistence while holding the snapshot lock, preserving capture order.
        lock (gate) return PersistAsync(minutes.Values.Select(Clone).ToArray());
    }
    private async Task PersistAsync(MinuteBucket[] snapshot)
    {
        await saveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                if (!protectedSource) throw new IOException("损坏的原文件尚未成功备份，已阻止覆盖");
                // Only a validated source may become the recovery backup.
                if (readError == null && File.Exists(path)) File.Copy(path, path + ".bak", true);
                Disk.AtomicJson(path, snapshot);
                Timeline.Save();
            }).ConfigureAwait(false);
            writeError = null;
        }
        catch (Exception e) { writeError = "历史保存失败：" + e.Message; }
        finally { saveGate.Release(); }
    }
    public void Export(string target, TimeSpan range, DateTimeOffset now)
    {
        ExportService.ExportCsv(target, ExportMinutes(range, now));
    }
    public List<MinuteBucket> ExportMinutes(TimeSpan range, DateTimeOffset now)
    {
        lock (gate)
        {
        var first = (now - range).ToUnixTimeSeconds() / 60;
        var last = now.ToUnixTimeSeconds() / 60;
        // Missing minutes stay blank in the workbook and break its lines; recorded zeros remain numeric.
        return Enumerable.Range(0, checked((int)(last - first + 1)))
            .Select(i => minutes.TryGetValue(first + i, out var bucket) ? Clone(bucket)
            : new MinuteBucket { UnixMinute = first + i }).ToList();
        }
    }
    private static MinuteBucket Clone(MinuteBucket bucket) => new()
    { UnixMinute = bucket.UnixMinute, UploadBytes = bucket.UploadBytes, DownloadBytes = bucket.DownloadBytes, Seconds = bucket.Seconds,
        PeakUpload = bucket.PeakUpload, PeakDownload = bucket.PeakDownload, PeakSeconds = bucket.PeakSeconds,
        MinimumUpload = bucket.MinimumUpload, MinimumDownload = bucket.MinimumDownload, MinimumSeconds = bucket.MinimumSeconds,
        UncertainUploadBytes = bucket.UncertainUploadBytes, UncertainDownloadBytes = bucket.UncertainDownloadBytes, UncertainIntervals = bucket.UncertainIntervals };
    public CoverageInfo Coverage(TimeSpan range, DateTimeOffset end)
    {
        lock (gate)
        {
            var first = (end - range).ToUnixTimeSeconds() / 60; var last = end.ToUnixTimeSeconds() / 60;
            double valid = 0, peak = 0, minimum = 0; long uncertainUp = 0, uncertainDown = 0;
            foreach (var m in minutes.Values)
            {
                if (m.UnixMinute < first) continue; if (m.UnixMinute > last) break;
                valid += m.Seconds; peak += m.PeakSeconds; minimum += m.MinimumSeconds;
                uncertainUp += m.UncertainUploadBytes; uncertainDown += m.UncertainDownloadBytes;
            }
            return new(valid, peak, minimum, uncertainUp, uncertainDown);
        }
    }
    public (double? Up, double? Down, double Coverage) Peaks(TimeSpan range, DateTimeOffset end)
    {
        lock (gate)
        {
            var first = (end - range).ToUnixTimeSeconds() / 60; var last = end.ToUnixTimeSeconds() / 60;
            double? up = null, down = null; double coverage = 0;
            foreach (var bucket in minutes.Values)
            {
                if (bucket.UnixMinute < first) continue;
                if (bucket.UnixMinute > last) break;
                if (bucket.PeakUpload.HasValue) up = Math.Max(up ?? 0, bucket.PeakUpload.Value);
                if (bucket.PeakDownload.HasValue) down = Math.Max(down ?? 0, bucket.PeakDownload.Value);
                coverage += bucket.PeakSeconds;
            }
            return (up, down, coverage);
        }
    }
}

public static class Units
{
    public static string Duration(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)) is var span && span.TotalHours >= 1
        ? ((int)span.TotalHours) + "小时" + span.Minutes + "分" : span.TotalMinutes >= 1 ? ((int)span.TotalMinutes) + "分" + span.Seconds + "秒" : seconds.ToString("0.#", CultureInfo.InvariantCulture) + "秒";
    public static string Bytes(double value)
    {
        var units = new[] { "B", "KiB", "MiB", "GiB", "TiB" };
        var i = 0;
        while (value >= 1024 && i < units.Length - 1) { value /= 1024; i++; }
        return value.ToString(i == 0 ? "F0" : "F2", CultureInfo.InvariantCulture) + " " + units[i];
    }
    public static double Megabits(double bytesPerSecond) => bytesPerSecond * 8 / 1_000_000;
    public static string Rate(double bytesPerSecond) => Megabits(bytesPerSecond).ToString("F2", CultureInfo.InvariantCulture) + " Mb/s";
    public static string PointRate(double bytesPerSecond) => Megabits(bytesPerSecond).ToString("0.00", CultureInfo.InvariantCulture) + " Mb/s";
}
