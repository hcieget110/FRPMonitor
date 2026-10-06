using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using FRPMonitor.Exporting;

namespace FRPMonitor.Cloud;

internal static class CloudTests
{
    public static void Run(string folder, Action<bool, string> check)
    {
        var end = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.Now.ToUnixTimeSeconds());
        var history = new CloudHistory();
        for (int i = -599; i <= 0; i++) history.Add(new(end.AddSeconds(i), i == 0 ? 0 : 1000, 500));
        var snapshot = history.Snapshot(TimeSpan.FromMinutes(5), end);
        check(snapshot.Count == 300 && history.Snapshot(TimeSpan.FromMinutes(1), end).Count == 60, "cloud history retains only 300 second slots and clips one-minute ranges");
        history.Add(new(end.AddSeconds(1), double.NaN, 0)); history.Add(new(end.AddSeconds(1), 0, 0, 3));
        check(history.Snapshot(TimeSpan.FromMinutes(5), end).Count == 300, "cloud rejects invalid rates and delayed sampling intervals");
        var grid = SecondSeries.Grid(new[] { new TrafficPoint(end.AddSeconds(-1), 0, 0), new TrafficPoint(end, 1, 2, 60, 60) }, TimeSpan.FromMinutes(1), end);
        check(grid.Length == 60 && grid[58]?.Upload == 0 && grid[59] == null && grid[0] == null, "second export distinguishes measured zero, missing seconds, and unrecoverable minute averages");
        var profile = new CloudProfile { Fingerprints = "SHA256:example=" };
        check(profile.Trusts("example") && !profile.Trusts("different") && !System.Text.Json.JsonSerializer.Serialize(profile).Contains("CredentialTarget"), "cloud accepts only pinned SSH host fingerprints and keeps credential lookup out of settings JSON");
        foreach (int minutes in new[] { 1, 5 })
        {
            var path = Path.Combine(folder, "cloud-" + minutes + ".xlsx");
            CloudExporter.Export(path, snapshot, TimeSpan.FromMinutes(minutes), end, true);
            using var book = SpreadsheetDocument.Open(path, false);
            var errors = new OpenXmlValidator().Validate(book).ToArray();
            check(errors.Length == 0, "cloud " + minutes + " minute workbook validates: " + string.Join(";", errors.Take(3).Select(e => e.Description)));
            var data = book.WorkbookPart!.WorksheetParts.Single(p => p.DrawingsPart == null);
            check(data.Worksheet!.Descendants<DocumentFormat.OpenXml.Spreadsheet.Row>().Count() == minutes * 60 + 1, "cloud " + minutes + " minute export includes every second slot");
            var charts = book.WorkbookPart.WorksheetParts.SelectMany(p => p.DrawingsPart?.ChartParts ?? Enumerable.Empty<ChartPart>()).ToArray();
            check(charts.Length == 2 && charts.All(p => p.ChartSpace!.OuterXml.Contains("'秒级数据'!$B$2:$B$" + (minutes * 60 + 1))) && book.WorkbookPart.WorksheetParts.All(p => p.DrawingsPart == null || !p.DrawingsPart.ImageParts.Any()), "cloud " + minutes + " minute charts reference editable sheet values without images");
            var formats = book.WorkbookPart.WorkbookStylesPart!.Stylesheet!.Descendants<DocumentFormat.OpenXml.Spreadsheet.NumberingFormat>();
            check(formats.Single(f => f.NumberFormatId!.Value == 164).FormatCode!.Value == "0.00" && charts.All(p => p.ChartSpace!.Descendants<DocumentFormat.OpenXml.Drawing.Charts.NumberingFormat>().All(f => f.FormatCode!.Value == "0.00")), "cloud " + minutes + " minute sheet, chart axes and point labels display two decimals");
            var graph = book.WorkbookPart.WorksheetParts.Single(p => p.DrawingsPart != null);
            var cells = graph.Worksheet!.Descendants<DocumentFormat.OpenXml.Spreadsheet.Cell>().ToDictionary(cell => cell.CellReference!.Value!);
            check(cells["B5"].StyleIndex!.Value == 6 && cells["B6"].StyleIndex!.Value == 7 && cells["A5"].StyleIndex!.Value == 8 && cells["A6"].StyleIndex!.Value == 9 && cells["A36"].InnerText.Contains("每点数值详细趋势"), "cloud " + minutes + " minute summary uses FRP row styles and has a left-side detailed chart heading");
            XNamespace chartNs = "http://schemas.openxmlformats.org/drawingml/2006/chart";
            var detailed = charts.Select(part => XElement.Parse(part.ChartSpace!.OuterXml)).Single(xml => xml.Element(chartNs + "chart")!.Element(chartNs + "title")!.Value.Contains("详细图"));
            var layout = detailed.Descendants(chartNs + "manualLayout").Single();
            var width = minutes * 60 * 65;
            double Factor(string name) => double.Parse(layout.Element(chartNs + name)!.Attribute("val")!.Value, System.Globalization.CultureInfo.InvariantCulture);
            check(Factor("x") * width < 20 && (Factor("x") + Factor("w")) * width <= 1100, "cloud " + minutes + " minute native detailed title remains in the first horizontal viewport");
            check(data.Worksheet!.Descendants<DocumentFormat.OpenXml.Spreadsheet.Cell>().First(cell => cell.CellReference!.Value == "B2").CellValue!.Text == "0.008", "cloud " + minutes + " minute Excel formatting preserves underlying rate precision");
            var csv = Path.ChangeExtension(path, ".csv"); CloudExporter.Export(csv, snapshot, TimeSpan.FromMinutes(minutes), end, false);
            var csvRows = File.ReadAllLines(csv);
            check(csvRows.Length == minutes * 60 + 1 && csvRows.Skip(1).SelectMany(line => line.Split(',').Skip(1)).All(value => value.Length == 0 || System.Text.RegularExpressions.Regex.IsMatch(value, @"^\d+\.\d{2}$")), "cloud " + minutes + " minute CSV is second-level and uses two decimals");
        }
        var frp = new History("second-export", folder);
        frp.Add(end.AddSeconds(-1), 0, 0, 1); frp.Add(end, 250000, 125000, 1); frp.Save();
        history.Clear();
        check(frp.ExportSeconds(TimeSpan.FromMinutes(5), end).Count == 2, "clearing cloud history does not affect local FRP history");
        var frpPath = Path.Combine(folder, "frp-seconds.xlsx");
        ExportService.ExportAsync(frp, frpPath, true, TimeSpan.FromMinutes(5), end, false).GetAwaiter().GetResult();
        using var frpBook = SpreadsheetDocument.Open(frpPath, false);
        var frpCharts = frpBook.WorkbookPart!.WorksheetParts.SelectMany(p => p.DrawingsPart?.ChartParts ?? Enumerable.Empty<ChartPart>()).ToArray();
        check(new OpenXmlValidator().Validate(frpBook).Count() == 0 && frpCharts.Length == 2 && frpCharts.All(c => c.ChartSpace!.OuterXml.Contains("秒级") && !c.ChartSpace.OuterXml.Contains("分钟平均")), "FRP five-minute export uses native second-level charts rather than minute averages");
        var restored = new History("second-export", folder);
        check(restored.ExportSeconds(TimeSpan.FromMinutes(5), end).Count == 0, "FRP reload never fabricates seconds from persisted minute buckets");
    }
}
