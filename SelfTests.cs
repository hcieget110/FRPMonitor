using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;

namespace FRPMonitor;

public static class SelfTests
{
    private sealed class SyntheticCollector : IMonitorCollector
    {
        private volatile bool fails;
        private readonly System.Threading.CancellationTokenSource stop = new();
        private System.Threading.Tasks.Task? producer;
        public TrafficAccumulator Counters { get; } = new(new[] { "frpc" }, false);
        private volatile bool ready;
        public bool Ready => ready;
        public string? Error => fails ? "synthetic collector failure" : null;
        public long EventsLost => 0;
        public SyntheticCollector(bool fail = false) { fails = fail; }
        public void Fail() => fails = true;
        public void Start()
        {
            if (fails) return;
            Counters.StartProcess(777, "frpc"); ready = true;
            producer = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    while (!stop.IsCancellationRequested)
                    { Counters.Record(777, 1000, true, IPAddress.Parse("192.0.2.1"), IPAddress.Parse("203.0.113.1"), 50000, 443); await System.Threading.Tasks.Task.Delay(20, stop.Token); }
                }
                catch (OperationCanceledException) { }
            });
        }
        public void Dispose() { ready = false; stop.Cancel(); producer?.GetAwaiter().GetResult(); }
    }
    public static int Run(string output)
    {
        var folder = Path.Combine(Path.GetTempPath(), "FRPMonitor-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var passed = new System.Collections.Generic.List<string>();
        void Check(bool condition, string name) { if (!condition) throw new Exception(name); passed.Add(name); }
        try
        {
            var c = new TrafficAccumulator(new[] { "frpc", "frps" }, false);
            c.StartProcess(10, "frpc.exe"); c.StartProcess(20, "frps.exe"); c.StartProcess(30, "browser.exe");
            var remote4 = IPAddress.Parse("203.0.113.2"); var local4 = IPAddress.Parse("192.0.2.2");
            var remote6 = IPAddress.Parse("2001:db8::2"); var local6 = IPAddress.Parse("2001:db8::1");
            c.Record(10, 2048, true, local4, remote4); c.Record(10, 1024, false, remote4, local4);
            c.Record(20, 4096, true, local6, remote6); c.Record(20, 8192, false, remote6, local6);
            c.Record(30, 999999, true, local4, remote4); c.Record(10, 999999, true, IPAddress.Loopback, IPAddress.Loopback);
            c.Record(20, 999999, false, IPAddress.IPv6Loopback, IPAddress.IPv6Loopback);
            var s = c.Drain(2);
            Check(s.UploadBytes == 6144 && s.DownloadBytes == 9216, "IPv4 + IPv6 / multiple target processes / unrelated traffic excluded");
            Check(s.Processes.Count == 2 && s.Processes[0].Upload == 1024, "rates use actual elapsed seconds");
            Check(c.Drain(1).UploadBytes == 0, "sampling drains deltas exactly once");
            c.Record(10, 400, true, local4, remote4); c.StopProcess(10); c.Record(10, 900, true, local4, remote4);
            s = c.Drain(1); Check(s.UploadBytes == 400 && s.Processes.All(p => p.Pid != 10), "exit keeps pending bytes and rejects later events");
            c.StartProcess(40, "frpc.exe"); c.Record(40, 700, false, remote4, local4);
            Check(c.Drain(1).DownloadBytes == 700, "FRP restart is discovered with new PID");
            c.StartProcess(40, "browser.exe"); c.Record(40, 88888, true, local4, remote4);
            Check(c.Drain(1).UploadBytes == 0, "PID reuse by non-target is excluded");
            var loop = new TrafficAccumulator(new[] { "frpc" }, true); loop.StartProcess(50, "frpc"); loop.Record(50, 123, true, IPAddress.Loopback, IPAddress.Loopback);
            Check(loop.Drain(1).UploadBytes == 123, "loopback toggle works");
            Check(Units.Rate(1024) == "0.01 Mb/s" && Units.Rate(800000) == "6.40 Mb/s" && Units.Rate(250000000) == "2000.00 Mb/s" && Units.Bytes(1024) == "1.00 KiB", "all bandwidth displays stay in decimal Mb/s while cumulative traffic remains bytes");
            var smoother = new RateSmoother();
            var slow = new Snapshot(2000, 1000, 2000, 1000, 2, new() { new(10, "frpc", 1000, 500) });
            var fast = new Snapshot(5000, 1000, 7000, 2000, 1, new() { new(10, "frpc", 5000, 1000) });
            smoother.Add(slow, 3); var average = smoother.Add(fast, 3);
            Check(average.UploadBytes == 7000 && average.Seconds == 3 && average.TotalUp == 7000 && Math.Abs(average.Processes[0].Upload - 7000.0 / 3) < 0.001, "moving average weights unequal durations and preserves cumulative totals");
            var after = smoother.Add(new(3000, 900, 10000, 2900, 1, new() { new(10, "frpc", 3000, 900) }), 3);
            Check(after.UploadBytes == 9000 && after.Seconds == 3 && after.TotalUp == 10000, "moving window clips the oldest interval without altering recorded totals");
            Check(smoother.Add(fast, 1) == fast, "one-second mode displays the raw sample");
            var resumed = new Snapshot(100, 100, 10100, 3000, 20, new());
            smoother.Add(slow, 3); smoother.Add(resumed, 3);
            Check(smoother.Add(fast, 3).UploadBytes == fast.UploadBytes, "resume clears pre-suspend speed averages");
            var now = DateTimeOffset.UtcNow;
            var minute = now.ToUnixTimeSeconds() / 60;
            var boundary = DateTimeOffset.FromUnixTimeSeconds(minute * 60);
            var chartPoints = new[] { new TrafficPoint(boundary.AddSeconds(2), 300, 900, 1), new TrafficPoint(boundary, 100, 200, 3) };
            var stats = ChartData.Statistics(chartPoints, true)!;
            Check(stats.Minimum == 100 && stats.Maximum == 300 && stats.Average == 150 && stats.Last == 300,
                "chart statistics weight coverage and select latest by timestamp without counting gaps as zeros");
            Check(ChartData.Statistics(Array.Empty<TrafficPoint>(), false) == null, "empty history has no invented statistics");
            var minutePoints = new[] { new TrafficPoint(boundary, 100, 200, 1, 60), new TrafficPoint(boundary.AddMinutes(1), 800, 300, 1, 60),
                new TrafficPoint(boundary.AddMinutes(2), 300, 900, 1, 60), new TrafficPoint(boundary.AddMinutes(5), 200, 500, 1, 60) };
            var segments = ChartData.Segments(minutePoints, boundary, TimeSpan.FromDays(7), 1);
            Check(segments.Count == 2 && segments[0].Contains(minutePoints[1]) && segments[0].Contains(minutePoints[2]),
                "week rendering preserves both directions' spikes and downtime even within one screen column");
            Check(ChartData.Segments(chartPoints.Append(new TrafficPoint(boundary.AddSeconds(9), 100, 100)), boundary, TimeSpan.FromMinutes(5), 500).Count == 2,
                "second-level history also breaks at missing collection intervals");
            Check(ChartData.ScaleMaximum(6.4) == 10 && ChartData.ScaleMaximum(0) > 0, "bandwidth axes use readable scales with headroom for zero and active traffic");
            var crowded = Enumerable.Range(1, 1800).Select(i => new TrafficPoint(boundary.AddSeconds(i), i % 2 == 0 ? 900 : 100, 0, 1)).ToList();
            var overview = ChartData.TrendSegments(crowded, boundary, TimeSpan.FromMinutes(30), 960).SelectMany(segment => segment).ToArray();
            Check(overview.Length <= 121 && Math.Abs(overview.Sum(point => point.Point.Upload * point.Point.Seconds) - 900000) < 0.00001 && overview.All(point => point.UploadMaximum == 900),
                "long-range display bounds line density while preserving weighted traffic and per-window sample maxima");
            var unequalTrend = ChartData.TrendSegments(new[] { new TrafficPoint(boundary.AddSeconds(3), 100, 0, 3), new TrafficPoint(boundary.AddSeconds(4), 500, 0, 1) }, boundary, TimeSpan.FromMinutes(30), 960).Single().Single();
            Check(unequalTrend.Point.Upload == 200 && unequalTrend.UploadMaximum == 500 && unequalTrend.Point.Seconds == 4,
                "trend averages weight actual valid sampling durations instead of averaging rates equally");
            var crossingTrend = ChartData.TrendSegments(new[] { new TrafficPoint(boundary.AddSeconds(20), 80, 20, 10) }, boundary, TimeSpan.FromMinutes(30), 960).Single();
            Check(crossingTrend.Count == 2 && crossingTrend.Sum(point => point.Point.Seconds) == 10 && crossingTrend.All(point => point.Point.Upload == 80),
                "trend intervals crossing aggregation boundaries split coverage without changing traffic totals");
            var gapTrend = ChartData.TrendSegments(new[] { new TrafficPoint(boundary.AddSeconds(1), 100, 0), new TrafficPoint(boundary.AddSeconds(10), 900, 0) }, boundary, TimeSpan.FromDays(1), 960);
            Check(gapTrend.Count == 2 && gapTrend[0].Last().End < gapTrend[1].First().Start,
                "outages remain separate even when both sides fall inside the same coarse trend window");
            var minuteTrend = ChartData.TrendSegments(new[] { new TrafficPoint(boundary, 200, 0, 15, 60), new TrafficPoint(boundary.AddMinutes(1), 100, 0, 60, 60) }, boundary, TimeSpan.FromDays(1), 960).SelectMany(segment => segment).ToArray();
            Check(minuteTrend.Sum(point => point.Point.Seconds) == 75 && Math.Abs(minuteTrend.Sum(point => point.Point.Upload * point.Point.Seconds) - 9000) < 0.00001,
                "persisted minute data retain valid coverage and weighted rates in longer overview ranges");
            var densityChart = new TrafficChart(); densityChart.Update(crowded, TimeSpan.FromMinutes(5), boundary.AddMinutes(30));
            var fiveMinuteUnchanged = densityChart.AggregationSeconds == 0;
            densityChart.Update(crowded, TimeSpan.FromHours(2), boundary.AddMinutes(30)); densityChart.Compact = true;
            Check(fiveMinuteUnchanged && densityChart.AggregationSeconds == 0 && ChartData.TrendIntervalSeconds(TimeSpan.FromHours(2), 800) > ChartData.TrendIntervalSeconds(TimeSpan.FromHours(2), 1600),
                "five-minute and compact charts keep raw detail while longer plots adapt their resolution to width");
            var h = new History("test", folder);
            h.Add(boundary.AddSeconds(1), 3000, 6000, 3); h.Save();
            var restored = new History("test", folder);
            var total = restored.Totals(TimeSpan.FromDays(1), now.AddSeconds(2));
            Check(total.Up == 3000 && total.Down == 6000 && Math.Abs(total.Seconds - 3) < 0.001, "minute-boundary split preserves exact bytes and coverage after restart");
            Check(restored.Points(TimeSpan.FromDays(1), now.AddSeconds(2)).Count == 2, "day view loads persisted minute buckets");
            restored.Add(now.AddDays(-8), 2000, 2000, 1);
            restored.Add(now.AddDays(-6), 4000, 5000, 1); restored.Add(now, 1000, 1000, 1); restored.Save();
            Check(restored.Totals(TimeSpan.FromDays(7), now.AddSeconds(2)).Up == 8000, "week retains seven days and prunes expired history");
            var before = restored.Totals(TimeSpan.FromDays(7), now.AddSeconds(2)); restored.Add(now, 99999, 99999, 80);
            Check(restored.Totals(TimeSpan.FromDays(7), now.AddSeconds(2)).Up == before.Up + 99999 && restored.Coverage(TimeSpan.FromDays(7), now.AddSeconds(2)).UncertainUploadBytes == 99999,
                "suspend gaps preserve known bytes separately without inventing rates or precise timing");
            var other = new History("other-scope", folder); Check(other.Points(TimeSpan.FromDays(7), now).Count == 0, "scope histories remain isolated");
            var csv = Path.Combine(folder, "export.csv"); restored.Export(csv, TimeSpan.FromDays(7), now.AddSeconds(2));
            var csvLines = File.ReadAllLines(csv);
            var exported = csvLines[1].Split(',');
            var sourceMinute = restored.ExportMinutes(TimeSpan.FromDays(7), now.AddSeconds(2)).First(m => m.Seconds > 0);
            var expectedRate = sourceMinute.UploadBytes / sourceMinute.Seconds * 8 / 1000000;
            Check(csvLines[0].Contains("平均上传Mb每秒") && csvLines.Length >= 3 && Math.Abs(double.Parse(exported[4], System.Globalization.CultureInfo.InvariantCulture) - expectedRate) < 0.000001,
                "CSV bandwidth uses decimal Mb/s and preserves original byte totals and coverage");
            Check(new Settings { ProcessNames = "FRPC.exe，frps;frpc" }.Names.SequenceEqual(new[] { "frpc", "frps" }), "custom names normalize and deduplicate");
            File.WriteAllText(Path.Combine(folder, "history-broken.json"), "broken json");
            Check(new History("broken", folder).StorageError != null, "corrupt history is reported instead of crashing");
            var chartHistory = new History("chart-export", folder);
            chartHistory.Add(boundary.AddSeconds(2), 250000, 125000, 1);
            chartHistory.Add(boundary.AddMinutes(3).AddSeconds(2), 0, 0, 1);
            var exportEnd = boundary.AddMinutes(4);
            var exportedMinutes = chartHistory.ExportMinutes(TimeSpan.FromMinutes(4), exportEnd);
            Check(exportedMinutes.Count == 5 && exportedMinutes[1].Seconds == 0 && exportedMinutes[3].Seconds == 1 && exportedMinutes[3].UploadBytes == 0,
                "chart export pads missing minutes while keeping recorded zero traffic distinct");
            var xlsx = Path.Combine(folder, "charts.xlsx");
            ExcelExporter.Export(xlsx, exportedMinutes, TimeSpan.FromMinutes(4), exportEnd);
            using (var doc = SpreadsheetDocument.Open(xlsx, false))
            {
                var errors = new OpenXmlValidator().Validate(doc).Take(8).ToArray();
                Check(errors.Length == 0, "Excel package conforms to OpenXML" + (errors.Length == 0 ? "" : ": " + string.Join("; ", errors.Select(e => e.Description + " " + e.Path?.XPath))));
                var charts = doc.WorkbookPart!.WorksheetParts.SelectMany(w => w.DrawingsPart?.ChartParts ?? Enumerable.Empty<ChartPart>()).ToArray();
                Check(charts.Length == 2 && charts.All(part => part.ChartSpace!.Descendants<DocumentFormat.OpenXml.Drawing.Charts.LineChartSeries>().Count() == 2),
                    "Excel export contains overview and detailed editable native charts with upload/download series");
                var cache = charts[0].ChartSpace!.Descendants<DocumentFormat.OpenXml.Drawing.Charts.NumberingCache>().First();
                var numericPoints = cache.Elements<DocumentFormat.OpenXml.Drawing.Charts.NumericPoint>().ToArray();
                Check(!numericPoints.Any(p => p.Index!.Value == 1) && numericPoints.Any(p => p.Index!.Value == 3 && p.NumericValue!.Text == "0") && numericPoints.First().NumericValue!.Text == "2",
                    "chart caches retain gaps, real zeros, and decimal Mb/s conversion");
                Check(charts.All(part => part.ChartSpace!.Descendants<DocumentFormat.OpenXml.Drawing.Charts.CategoryAxis>().Single().GetFirstChild<DocumentFormat.OpenXml.Drawing.Charts.AutoLabeled>()!.Val!.Value == false
                    && part.ChartSpace.Descendants<DocumentFormat.OpenXml.Drawing.Charts.StringReference>().All(reference => reference.Formula!.Text.Contains("$G$"))),
                    "minute categories remain distinct and cannot collapse into a daily date axis");
            }
            var weekExport = chartHistory.ExportMinutes(TimeSpan.FromDays(7), exportEnd);
            Check(weekExport.Count == 10081, "full-week Excel export keeps every minute without screen-width reduction");
            using (var zip = System.IO.Compression.ZipFile.OpenRead(xlsx))
                Check(!zip.Entries.Any(entry => entry.FullName.Contains("/media/")), "Excel charts are native editable objects with no embedded picture files");
            var parallelHistory = new History("background-save", folder);
            var saves = new System.Collections.Generic.List<System.Threading.Tasks.Task>();
            for (int i = 1; i <= 10; i++) { parallelHistory.Add(boundary.AddSeconds(10), 100, 50, 1); saves.Add(parallelHistory.SaveAsync()); }
            System.Threading.Tasks.Task.WhenAll(saves).GetAwaiter().GetResult();
            var latestSaved = new History("background-save", folder).Totals(TimeSpan.FromDays(1), boundary.AddMinutes(1));
            Check(latestSaved.Up == 1000 && latestSaved.Down == 500, "queued background history saves retain the latest snapshot without write races");
            var frozenHistory = new History("export-snapshot", folder);
            frozenHistory.Add(boundary.AddSeconds(10), 100, 50, 1);
            var frozenPath = Path.Combine(folder, "snapshot.csv");
            var exportTask = ExportService.ExportAsync(frozenHistory, frozenPath, false, TimeSpan.FromMinutes(5), boundary.AddMinutes(1), false);
            frozenHistory.Add(boundary.AddSeconds(11), 900, 450, 1); exportTask.GetAwaiter().GetResult();
            var frozenRows = File.ReadAllLines(frozenPath).Skip(1).Select(line => line.Split(',')).ToArray();
            Check(frozenRows.Length == 300 && frozenRows.Count(row => row[1].Length > 0) == 1 && double.Parse(frozenRows.Single(row => row[1].Length > 0)[1], System.Globalization.CultureInfo.InvariantCulture) == Units.Megabits(100), "background second export uses an immutable snapshot while live collection continues");
            var badExtensionRejected = false;
            try { ExportService.ExportAsync(frozenHistory, Path.Combine(folder, "wrong.csv"), true, TimeSpan.FromMinutes(5), boundary.AddMinutes(1), false); }
            catch (InvalidOperationException) { badExtensionRejected = true; }
            Check(badExtensionRejected && !File.Exists(Path.Combine(folder, "wrong.csv")), "Excel export cannot silently become CSV because of a filename extension");
            var proxyFilter = new TrafficAccumulator(new[] { "frpc" }, true, ConnectionMode.ExternalAndSelectedLoopback, "127.0.0.1:7890");
            proxyFilter.StartProcess(91, "frpc");
            proxyFilter.Record(91, 100, true, IPAddress.Loopback, IPAddress.Loopback, 51000, 7890);
            proxyFilter.Record(91, 50, false, IPAddress.Loopback, IPAddress.Loopback, 7890, 51000);
            proxyFilter.Record(91, 999, true, IPAddress.Loopback, IPAddress.Loopback, 51001, 22203);
            proxyFilter.Record(91, 200, true, local4, remote4, 51002, 27000);
            var proxySample = proxyFilter.Drain(1);
            Check(proxySample.UploadBytes == 300 && proxySample.DownloadBytes == 50 && proxySample.Connections.Count == 3 && proxySample.Connections.Count(item => !item.Included) == 1,
                "proxy-only loopback selection excludes local business leg while retaining external traffic and excluded connection diagnostics");
            Check(proxySample.Connections.Single(item => item.Remote == "127.0.0.1:7890").Upload == 100 && proxySample.Connections.Single(item => item.Remote == "127.0.0.1:7890").Download == 50,
                "both directions share a connection without merging distinct local ports");
            proxyFilter.Record(91, 17, true, IPAddress.Loopback, IPAddress.Loopback, 51000, 7890, "UDP");
            Check(proxyFilter.Drain(1).Connections.Count(item => item.Remote == "127.0.0.1:7890") == 2,
                "TCP and UDP with the same endpoints remain distinct diagnostic connections");
            var reused = new TrafficAccumulator(new[] { "frpc" }, false); reused.StartProcess(96, "frpc");
            reused.Record(96, 10, true, local4, remote4); reused.StopProcess(96); reused.StartProcess(96, "frpc");
            reused.Record(96, 20, true, local4, remote4);
            Check(reused.Drain(1).UploadBytes == 30 && reused.Drain(1).UploadBytes == 0,
                "rapid target PID reuse preserves pending bytes exactly once");
            var selectedFilter = new TrafficAccumulator(new[] { "frpc" }, false, ConnectionMode.Selected, "[::1]:7890"); selectedFilter.StartProcess(92, "frpc");
            selectedFilter.Record(92, 123, true, IPAddress.IPv6Loopback, IPAddress.IPv6Loopback, 50000, 7890);
            selectedFilter.Record(92, 999, true, local4, remote4, 50001, 443);
            Check(selectedFilter.Drain(1).UploadBytes == 123, "explicit IPv6 endpoints work independently of the legacy loopback switch");
            Check(new Settings { ConnectionMode = ConnectionMode.Selected, EndpointFilters = "127.0.0.1:7890, *:27000" }.Scope ==
                new Settings { ConnectionMode = ConnectionMode.Selected, IncludeLoopback = true, EndpointFilters = "*:27000,127.0.0.1:7890" }.Scope && new Settings().Scope != new Settings { ConnectionMode = ConnectionMode.Selected, EndpointFilters = "127.0.0.1:7890" }.Scope,
                "equivalent endpoint rules share a history scope and changed counting modes remain isolated");
            var peakHistory = new History("sample-peaks", folder);
            for (int i = 1; i <= 60; i++) peakHistory.Add(boundary.AddSeconds(i), i <= 6 ? 2500000 : 0, 0, 1);
            peakHistory.Save();
            var peakBucket = peakHistory.ExportMinutes(TimeSpan.FromMinutes(1), boundary.AddMinutes(1)).First(item => item.Seconds > 0);
            Check(Math.Abs(Units.Megabits(peakBucket.Point.Upload) - 2) < 0.00001 && peakBucket.PeakUpload == 2500000 && peakBucket.PeakSeconds == 60 &&
                new History("sample-peaks", folder).Peaks(TimeSpan.FromMinutes(1), boundary.AddMinutes(1)).Up == 2500000,
                "20 Mb/s six-second burst retains a persisted 20 Mb/s sampled peak and a separate 2 Mb/s minute average");
            File.WriteAllText(Path.Combine(folder, "history-legacy-peaks.json"), JsonSerializer.Serialize(new[] { new { UnixMinute = minute, UploadBytes = 100, DownloadBytes = 50, Seconds = 60 } }));
            var legacy = new History("legacy-peaks", folder);
            Check(legacy.Peaks(TimeSpan.FromDays(1), boundary.AddMinutes(1)).Up == null && legacy.Totals(TimeSpan.FromDays(1), boundary.AddMinutes(1)).Up == 100,
                "legacy history retains totals without inventing a sampled peak");
            var restoredExtrema = new History("sample-peaks", folder).ExportMinutes(TimeSpan.FromMinutes(1), boundary.AddMinutes(1)).First(item => item.Seconds > 0);
            Check(restoredExtrema.MinimumUpload == 0 && restoredExtrema.MinimumDownload == 0 && restoredExtrema.MinimumSeconds == 60,
                "sampled minima include recorded zero traffic and survive snapshots and persistence");
            var lowRates = new History("minimum-rates", folder);
            lowRates.Add(boundary.AddSeconds(1), 100, 80, 1); lowRates.Add(boundary.AddSeconds(3), 40, 0, 2);
            var lowMinute = lowRates.ExportMinutes(TimeSpan.FromMinutes(1), boundary.AddMinutes(1)).First(item => item.Seconds > 0);
            Check(lowMinute.MinimumUpload == 20 && lowMinute.PeakUpload == 100 && lowMinute.MinimumDownload == 0 && lowMinute.MinimumSeconds == 3,
                "online extrema use actual interval rates, not raw bytes or artificial zero-filled gaps");
            File.WriteAllText(Path.Combine(folder, "history-peak-only.json"), JsonSerializer.Serialize(new[] { new { UnixMinute = minute, UploadBytes = 6000, DownloadBytes = 3000, Seconds = 60, PeakUpload = 1000, PeakDownload = 500, PeakSeconds = 60 } }));
            var peakOnly = new History("peak-only", folder);
            var oldMinimum = peakOnly.ExportMinutes(TimeSpan.FromMinutes(1), boundary.AddMinutes(1)).First(item => item.Seconds > 0);
            peakOnly.Add(boundary.AddSeconds(1), 100, 50, 1);
            var partialMinimum = peakOnly.ExportMinutes(TimeSpan.FromMinutes(1), boundary.AddMinutes(1)).First(item => item.Seconds > 0);
            Check(oldMinimum.MinimumUpload == null && partialMinimum.MinimumUpload == 100 && partialMinimum.MinimumSeconds == 1 && partialMinimum.PeakUpload == 1000,
                "upgrading peak-only history preserves prior maxima and records minimum coverage only for new samples");
            var minCsv = Path.Combine(folder, "minimum-coverage.csv");
            ExportService.ExportCsv(minCsv, new[] { restoredExtrema, oldMinimum });
            var minimumRows = File.ReadAllLines(minCsv).Skip(1).Select(line => line.Split(',')).ToArray();
            Check(minimumRows[0][9] == "0.000000" && minimumRows[0][11] == "60.000" && minimumRows[1][9] == "" && minimumRows[1][11] == "",
                "CSV appends minima and coverage without changing prior columns, keeping legacy blanks distinct from zero");
            var extremaXlsx = Path.Combine(folder, "extrema-native.xlsx");
            ExcelExporter.Export(extremaXlsx, new[] { lowMinute, oldMinimum }, TimeSpan.FromMinutes(2), boundary.AddMinutes(1));
            using (var extremaBook = SpreadsheetDocument.Open(extremaXlsx, false))
            {
                var summarySheet = extremaBook.WorkbookPart!.WorksheetParts.Single(part => part.Worksheet!.Descendants<DocumentFormat.OpenXml.Spreadsheet.Cell>().Any(cell => cell.CellReference!.Value == "G5"));
                string? Value(string cellRef) => summarySheet.Worksheet!.Descendants<DocumentFormat.OpenXml.Spreadsheet.Cell>().Single(cell => cell.CellReference!.Value == cellRef).CellValue?.Text;
                Check(Value("F5") == "0.008" && Value("G5") == "0.00016" && !new OpenXmlValidator().Validate(extremaBook).Any(),
                    "Excel summary separates highest and lowest sampled rates from minute averages and excludes unknown legacy minima");
            }
            var splitPath = Path.Combine(folder, "split-native-charts.xlsx");
            var splitMinutes = peakHistory.ExportMinutes(TimeSpan.FromDays(2), boundary.AddMinutes(1));
            ExcelExporter.Export(splitPath, splitMinutes, TimeSpan.FromDays(2), boundary.AddMinutes(1), false, new ExportOptions(true, true));
            using (var split = SpreadsheetDocument.Open(splitPath, false))
            {
                Check(!new OpenXmlValidator().Validate(split).Any(), "daily split and sampled peak charts conform to OpenXML");
                var splitCharts = split.WorkbookPart!.WorksheetParts.SelectMany(sheet => sheet.DrawingsPart?.ChartParts ?? Enumerable.Empty<ChartPart>()).ToArray();
                var references = splitCharts.SelectMany(part => part.ChartSpace!.Descendants<DocumentFormat.OpenXml.Drawing.Charts.NumberReference>()).Select(reference => reference.Formula!.Text).ToArray();
                var secondDayRow = splitMinutes.Select((bucket, index) => (bucket, index)).First(item => item.bucket.Point.Time.LocalDateTime.Date != splitMinutes[0].Point.Time.LocalDateTime.Date).index + 2;
                Check(split.WorkbookPart!.Workbook!.GetFirstChild<DocumentFormat.OpenXml.Spreadsheet.Sheets>()!.ChildElements.Count >= 4 && references.Any(formula => formula.Contains("$H$")) && references.Any(formula => formula.Contains("$B$" + secondDayRow + ":")),
                    "per-day native charts reference their correct global minute row offsets and peak data columns");
            }
            var concurrent = new History("sampling-service", folder); int factories = 0;
            var collectors = new System.Collections.Concurrent.ConcurrentBag<SyntheticCollector>();
            SyntheticCollector? activeCollector = null;
            var service = new MonitoringService(concurrent, new Settings { RateAverageSeconds = 1 }, () =>
            {
                var next = new SyntheticCollector(System.Threading.Interlocked.Increment(ref factories) == 1);
                collectors.Add(next); System.Threading.Volatile.Write(ref activeCollector, next); return next;
            }, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(50));
            service.Start();
            // Deliberately block this thread; the sampling engine must continue independently.
            System.Threading.Thread.Sleep(700);
            Check(service.Current.Ready && service.Current.RetryCount >= 1 && factories >= 2 && concurrent.Points(TimeSpan.FromMinutes(1), DateTimeOffset.Now).Count >= 3,
                "sampling continues during caller/UI blocking and a failed collector recovers with bounded retry");
            var totalBeforeRestart = service.Current.Sample.TotalUp; service.RequestRestart(); System.Threading.Thread.Sleep(400);
            Check(service.Current.Sample.TotalUp >= totalBeforeRestart && service.Current.Ready, "manual collector restart retains session totals while sampling and history continue");
            var beforeFailure = factories; System.Threading.Volatile.Read(ref activeCollector)!.Fail();
            var recovered = System.Threading.SpinWait.SpinUntil(() => System.Threading.Volatile.Read(ref factories) > beforeFailure && service.Current.Ready && service.Current.RetryCount >= 1, 3000);
            Check(recovered, "a running collector reporting an error while still ready is replaced automatically");
            service.StopAsync().GetAwaiter().GetResult();
            Check(new History("sampling-service", folder).Totals(TimeSpan.FromMinutes(1), DateTimeOffset.Now).Up > 0,
                "asynchronous service shutdown saves its final background history snapshot");
            Check(concurrent.Totals(TimeSpan.FromMinutes(1), DateTimeOffset.Now).Up == collectors.Sum(item => item.Counters.Drain(1).TotalUp),
                "manual restart, recovery and shutdown preserve every collected byte in history");
            var midnight = new DateTimeOffset(DateTime.Today, TimeZoneInfo.Local.GetUtcOffset(DateTime.Today));
            var daily = new History("natural-day", folder); daily.Add(midnight.AddSeconds(-1), 999, 0, 1); daily.Add(midnight.AddSeconds(1), 123, 45, 1);
            Check(daily.Totals(TimeSpan.FromSeconds(2), midnight.AddSeconds(2)).Up == 123,
                "natural-day totals exclude the preceding day at local midnight");
            var diagnostics = Path.Combine(folder, "diagnostics.json");
            DiagnosticService.ExportAsync(diagnostics, new Settings { ConnectionMode = ConnectionMode.Selected, EndpointFilters = "127.0.0.1:7890" }, service.Current, peakHistory).GetAwaiter().GetResult();
            using (var diagnostic = JsonDocument.Parse(File.ReadAllText(diagnostics)))
                Check(diagnostic.RootElement.GetProperty("RecentPeaks").ValueKind == JsonValueKind.Object && diagnostic.RootElement.GetProperty("EndpointFilters").GetString() == "127.0.0.1:7890",
                    "diagnostic report includes counting scope and named peak fields without configuration credentials");
            var stress = new History("concurrent-history", folder);
            var writes = System.Threading.Tasks.Task.Run(() => { for (int i = 1; i <= 1000; i++) stress.Add(boundary.AddSeconds(20), 100, 50, 1); });
            var reads = System.Threading.Tasks.Task.Run(() => { for (int i = 1; i <= 50; i++) { stress.Points(TimeSpan.FromDays(1), boundary.AddMinutes(1)); stress.ExportMinutes(TimeSpan.FromDays(1), boundary.AddMinutes(1)); stress.SaveAsync().GetAwaiter().GetResult(); } });
            System.Threading.Tasks.Task.WhenAll(writes, reads).GetAwaiter().GetResult();
            Check(stress.Totals(TimeSpan.FromDays(1), boundary.AddMinutes(1)).Up == 100000, "concurrent history updates, snapshots and persistence preserve exact byte totals");
            var scaleChart = new TrafficChart { HoldScale = true };
            scaleChart.Update(new() { new(boundary, 1000000, 0) }, TimeSpan.FromMinutes(5), boundary);
            scaleChart.Update(new(), TimeSpan.FromMinutes(5), boundary.AddSeconds(3));
            Check(scaleChart.MaximumMbps == 10, "automatic axis holds its upper scale while a recent peak disappears");
            scaleChart.Update(new() { new(boundary, 10000, 0) }, TimeSpan.FromMinutes(30), boundary.AddSeconds(4));
            Check(scaleChart.MaximumMbps == 0.1, "changing view range recalibrates its automatic scale immediately instead of retaining the previous range limit");
            scaleChart.Update(new(), TimeSpan.FromMinutes(5), boundary.AddSeconds(11)); scaleChart.FixedMaximumMbps = 5;
            scaleChart.Update(new() { new(boundary, 10000000, 0) }, TimeSpan.FromMinutes(5), boundary.AddSeconds(12));
            Check(scaleChart.MaximumMbps == 5, "explicit Mb/s axis limit overrides the automatic scale");
            var delayed = new History("delayed", folder);
            delayed.Add(boundary.AddSeconds(10), 6000000, 3000000, 6); delayed.Save();
            var delayedRestored = new History("delayed", folder);
            var delayedRows = delayedRestored.ExportMinutes(TimeSpan.FromMinutes(1), boundary.AddMinutes(1));
            Check(delayedRestored.Totals(TimeSpan.FromMinutes(1), boundary.AddMinutes(1)).Up == 6000000 && delayedRestored.Points(TimeSpan.FromMinutes(1), boundary.AddMinutes(1)).Count == 0 && delayedRestored.Peaks(TimeSpan.FromMinutes(1), boundary.AddMinutes(1)).Up == null,
                "delayed drains survive restart as known bytes without fabricated curves or extrema");
            var delayedCsv = Path.Combine(folder, "delayed.csv"); ExportService.ExportCsv(delayedCsv, delayedRows);
            var delayedFields = File.ReadAllLines(delayedCsv)[1].Split(',');
            Check(delayedFields[4] == "" && delayedFields[12] == "6000000" && delayedFields[14] == "1", "CSV exports uncertain traffic separately and leaves its average rate blank");
            var delayedXlsx = Path.Combine(folder, "delayed.xlsx"); ExcelExporter.Export(delayedXlsx, delayedRows, TimeSpan.FromMinutes(1), boundary.AddMinutes(1));
            using (var workbook = SpreadsheetDocument.Open(delayedXlsx, false))
            {
                var data = workbook.WorkbookPart!.WorksheetParts.First(part => part.Worksheet!.InnerText.Contains("速率不确定上传字节"));
                var raw = data.Worksheet!.Descendants<DocumentFormat.OpenXml.Spreadsheet.Cell>();
                Check(raw.First(cell => cell.CellReference?.Value == "N2").CellValue?.Text == "6000000" && raw.First(cell => cell.CellReference?.Value == "B2").CellValue == null && !new OpenXmlValidator().Validate(workbook).Any(),
                    "native Excel keeps uncertain bytes and blank rates with valid OpenXML schema");
            }
            var recovery = new History("recovery", folder); recovery.Add(boundary.AddSeconds(1), 100, 0, 1); recovery.Save();
            recovery.Add(boundary.AddSeconds(2), 200, 0, 1); recovery.Save();
            var recoveryPath = Path.Combine(folder, "history-recovery.json"); File.WriteAllText(recoveryPath, "{truncated");
            var recoveredHistory = new History("recovery", folder); recoveredHistory.Add(boundary.AddSeconds(3), 50, 0, 1); recoveredHistory.Save();
            Check(Directory.GetFiles(folder, "history-recovery.json.damaged-*").Any(file => File.ReadAllText(file) == "{truncated") && recoveredHistory.StorageError != null && recoveredHistory.Totals(TimeSpan.FromMinutes(1), boundary.AddMinutes(1)).Up == 150,
                "corrupt history is preserved, last backup recovers, and successful saves retain the read warning");
            File.WriteAllText(Path.Combine(folder, "settings.json"), "{\"ProcessNames\":null,\"ConnectionMode\":1,\"EndpointFilters\":\"bad endpoint\",\"RateAverageSeconds\":-3,\"FloatOpacity\":9}");
            var repaired = Settings.Load(folder);
            Check(repaired.Names.Length == 2 && repaired.ConnectionMode == ConnectionMode.All && repaired.RateAverageSeconds == 3 && repaired.FloatOpacity == .94 && repaired.Scope.Length == 16 && repaired.LoadWarning != null && Directory.GetFiles(folder, "settings.json.damaged-*").Length == 1,
                "invalid persisted settings recover safe values and preserve their original file");
            var discovery = new TrafficAccumulator(new[] { "frpc" }, false); discovery.StartProcess(1, "frpc");
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var targetMap = (System.Collections.IDictionary)typeof(TrafficAccumulator).GetField("targets", flags)!.GetValue(discovery)!;
            var captured = (System.Collections.IDictionary)Activator.CreateInstance(targetMap.GetType())!;
            foreach (System.Collections.DictionaryEntry entry in targetMap) captured.Add(entry.Key, entry.Value);
            discovery.StartProcess(2, "frpc");
            typeof(TrafficAccumulator).GetMethod("FinishDiscovery", flags)!.Invoke(discovery, new object[] { captured, new System.Collections.Generic.HashSet<int>() });
            Check(!discovery.IsTarget(1) && discovery.IsTarget(2), "process discovery cannot deactivate a newer ETW process absent from its snapshot");
            var bounded = new TrafficAccumulator(new[] { "frpc" }, false); bounded.StartProcess(1, "frpc");
            for (int port = 1; port <= 2048; port++) bounded.Record(1, 1, true, IPAddress.Parse("192.0.2.1"), IPAddress.Parse("203.0.113.1"), port, 443);
            bounded.Record(1, 1, true, IPAddress.Parse("192.0.2.1"), IPAddress.Parse("203.0.113.1"), 1, 443);
            bounded.Record(1, 1, true, IPAddress.Parse("192.0.2.1"), IPAddress.Parse("203.0.113.1"), 2049, 443);
            var boundedSample = bounded.Drain(1);
            Check(boundedSample.Connections.Count == 2048 && boundedSample.Connections.Any(item => item.Local.EndsWith(":1")) && !boundedSample.Connections.Any(item => item.Local.EndsWith(":2")) && boundedSample.TotalUp == 2050,
                "constant-time connection eviction retains recent connections without losing traffic totals");
            var jobs = new BackgroundOperations(); var completion = new System.Threading.Tasks.TaskCompletionSource();
            var pendingJob = jobs.Run(() => completion.Task); var finished = jobs.FinishAsync();
            bool rejected = false; try { jobs.Run(() => System.Threading.Tasks.Task.CompletedTask); } catch (InvalidOperationException) { rejected = true; }
            Check(!finished.IsCompleted && rejected, "shutdown waits for in-flight exports and rejects new work");
            completion.SetResult(); finished.GetAwaiter().GetResult(); pendingJob.GetAwaiter().GetResult();
            var selection = TrafficChart.SelectionInterval(boundary.AddMinutes(30), TimeSpan.FromMinutes(30), .9, .8);
            Check(selection.End - selection.Start == TimeSpan.FromMinutes(3) && selection.End == boundary.AddMinutes(27), "reverse drag zoom selects the exact anchored time interval");
            densityChart.UseAggregation = false; densityChart.Update(crowded, TimeSpan.FromMinutes(30), boundary.AddMinutes(30));
            Check(densityChart.AggregationSeconds == 0, "long-range detail toggle disables average aggregation");
            var timeline = new EventTimeline(Path.Combine(folder, "timeline-test.json"));
            for (int i = 0; i < 2050; i++) timeline.Add(now.AddSeconds(i), "test", "event");
            timeline.Save(); Check(new EventTimeline(Path.Combine(folder, "timeline-test.json")).Snapshot().Length == 2048 && delayedRestored.Timeline.Snapshot().Any(item => item.Kind == "采样间隔过长") && concurrent.Timeline.Snapshot().Any(item => item.Kind == "采集故障"),
                "bounded persisted timeline records gaps and collector failures");
            var atomicPath = Path.Combine(folder, "atomic.csv"); File.WriteAllText(atomicPath, "original"); bool atomicFailed = false;
            using (var locked = new FileStream(atomicPath, FileMode.Open, FileAccess.Read, FileShare.None))
                try { ExportService.ExportCsv(atomicPath, delayedRows); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { atomicFailed = true; }
            Check(atomicFailed && File.ReadAllText(atomicPath) == "original" && Directory.GetFiles(folder, "atomic.csv.tmp-*").Length == 0,
                "failed atomic CSV replacement leaves the prior export intact and cleans its temporary file");
            var frpRunning = true; int launches = 0;
            var frpControl = new FrpControlService(() => frpRunning, () => { launches++; frpRunning = true; }, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(5));
            Check(frpControl.StartAsync().GetAwaiter().GetResult() == FrpStartResult.AlreadyRunning && launches == 0,
                "FRP start detects an existing client before accessing or running a task");
            frpRunning = false;
            var startOne = frpControl.StartAsync(); var startTwo = frpControl.StartAsync();
            System.Threading.Tasks.Task.WhenAll(startOne, startTwo).GetAwaiter().GetResult();
            Check(launches == 1 && startOne.Result == FrpStartResult.Started && startTwo.Result == FrpStartResult.AlreadyRunning,
                "concurrent FRP start requests launch once and subsequent clicks report already running");
            var missingTask = new FrpControlService(() => false, () => throw new InvalidOperationException("missing task"), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(5));
            bool frpFailed = false;
            try { missingTask.StartAsync().GetAwaiter().GetResult(); } catch (InvalidOperationException e) { frpFailed = e.Message == "missing task"; }
            Check(frpFailed, "FRP launch errors propagate instead of reporting started");
            var exitedFrp = new FrpControlService(() => false, () => { }, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(5));
            frpFailed = false;
            try { exitedFrp.StartAsync().GetAwaiter().GetResult(); } catch (InvalidOperationException e) { frpFailed = e.Message.Contains("未检测到 frpc.exe"); }
            Check(frpFailed, "a queued task without a running client times out without a false started state");
            Cloud.CloudTests.Run(folder, Check);
            File.WriteAllText(output, JsonSerializer.Serialize(new { result = "PASS", count = passed.Count, tests = passed }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception e)
        {
            File.WriteAllText(output, JsonSerializer.Serialize(new { result = "FAIL", error = e.ToString(), passed }, new JsonSerializerOptions { WriteIndented = true })); return 1;
        }
        finally { Directory.Delete(folder, true); }
    }
}
