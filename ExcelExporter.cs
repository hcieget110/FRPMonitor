using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;
using S = DocumentFormat.OpenXml.Spreadsheet;
using C = DocumentFormat.OpenXml.Drawing.Charts;
using D = DocumentFormat.OpenXml.Drawing.Spreadsheet;

namespace FRPMonitor;

public static class ExcelExporter
{
    private static readonly XNamespace s = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace c = "http://schemas.openxmlformats.org/drawingml/2006/chart";
    private static readonly XNamespace a = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace xdr = "http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing";
    private static readonly XNamespace r = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static XAttribute A(string name, object value) => new(name, value);
    private static XElement S(string name, params object?[] values) => new(s + name, values);
    private static XElement C(string name, params object?[] values) => new(c + name, values);
    private static XElement V(string name, object value) => C(name, A("val", value));
    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static XElement Cell(string address, object? value, int style = 0)
    {
        if (value == null) return S("c", A("r", address), A("s", style));
        return value is string text ? S("c", A("r", address), A("s", style), A("t", "inlineStr"), S("is", S("t", text)))
            : S("c", A("r", address), A("s", style), S("v", Number(Convert.ToDouble(value, CultureInfo.InvariantCulture))));
    }
    private static XElement Row(int row, params XElement[] cells) => S("row", A("r", row), cells);
    private static XElement Columns(params (int From, int To, int Width)[] columns) => S("cols", columns.Select(col => S("col", A("min", col.From), A("max", col.To), A("width", col.Width), A("customWidth", 1))));

    public static void Export(string path, IReadOnlyList<MinuteBucket> minutes, TimeSpan range, DateTimeOffset end, bool demo = false, ExportOptions? options = null)
    {
        options ??= new();
        if (minutes.Count == 0 || minutes.Count > 10082) throw new InvalidOperationException("请选择最多近一周的历史范围。");
        // Write to a temporary package and replace only after it closes successfully.
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var document = SpreadsheetDocument.Create(temp, DocumentFormat.OpenXml.SpreadsheetDocumentType.Workbook))
            {
                var workbook = document.AddWorkbookPart();
                workbook.Workbook = new S.Workbook();
                var sheets = workbook.Workbook.AppendChild(new S.Sheets());
                var styles = workbook.AddNewPart<WorkbookStylesPart>();
                styles.Stylesheet = new S.Stylesheet(Styles());
                var graphSheet = workbook.AddNewPart<WorksheetPart>();
                var dataSheet = workbook.AddNewPart<WorksheetPart>();
                sheets.Append(new S.Sheet { Id = workbook.GetIdOfPart(graphSheet), SheetId = 1, Name = "带宽折线图" },
                    new S.Sheet { Id = workbook.GetIdOfPart(dataSheet), SheetId = 2, Name = "分钟数据" });
                var data = new List<XElement> { Row(1, Cell("A1", "时间（本机时区）", 1), Cell("B1", "上传分钟平均 Mb/s", 1), Cell("C1", "下载分钟平均 Mb/s", 1),
                    Cell("D1", "上传字节", 1), Cell("E1", "下载字节", 1), Cell("F1", "有效采集秒数", 1), Cell("G1", "图表时间标签", 1),
                    Cell("H1", "上传秒级峰值 Mb/s", 1), Cell("I1", "下载秒级峰值 Mb/s", 1), Cell("J1", "峰值记录秒数", 1),
                    Cell("K1", "上传秒级最低 Mb/s", 1), Cell("L1", "下载秒级最低 Mb/s", 1), Cell("M1", "最低值记录秒数", 1),
                    Cell("N1", "速率不确定上传字节", 1), Cell("O1", "速率不确定下载字节", 1), Cell("P1", "异常间隔次数（接收分钟）", 1)) };
                for (int i = 0; i < minutes.Count; i++)
                {
                    var m = minutes[i]; var row = i + 2; var known = m.Seconds > 0;
                    data.Add(Row(row, Cell("A" + row, m.Point.Time.LocalDateTime.ToOADate(), 4),
                        Cell("B" + row, known ? Units.Megabits(m.Point.Upload) : null, 3), Cell("C" + row, known ? Units.Megabits(m.Point.Download) : null, 3),
                        Cell("D" + row, known ? m.UploadBytes : null), Cell("E" + row, known ? m.DownloadBytes : null), Cell("F" + row, known ? m.Seconds : null, 3),
                        Cell("G" + row, m.Point.Time.ToLocalTime().ToString("MM/dd HH:mm")),
                        Cell("H" + row, m.PeakUpload.HasValue ? Units.Megabits(m.PeakUpload.Value) : null, 3),
                        Cell("I" + row, m.PeakDownload.HasValue ? Units.Megabits(m.PeakDownload.Value) : null, 3), Cell("J" + row, m.PeakSeconds > 0 ? m.PeakSeconds : null, 3),
                        Cell("K" + row, m.MinimumUpload.HasValue ? Units.Megabits(m.MinimumUpload.Value) : null, 3),
                        Cell("L" + row, m.MinimumDownload.HasValue ? Units.Megabits(m.MinimumDownload.Value) : null, 3), Cell("M" + row, m.MinimumSeconds > 0 ? m.MinimumSeconds : null, 3),
                        Cell("N" + row, m.UncertainIntervals > 0 ? m.UncertainUploadBytes : null), Cell("O" + row, m.UncertainIntervals > 0 ? m.UncertainDownloadBytes : null), Cell("P" + row, m.UncertainIntervals > 0 ? m.UncertainIntervals : null)));
                }
                dataSheet.Worksheet = new S.Worksheet(S("worksheet", new XAttribute(XNamespace.Xmlns + "r", r),
                    S("sheetViews", S("sheetView", A("workbookViewId", 0), S("pane", A("ySplit", 1), A("topLeftCell", "A2"), A("activePane", "bottomLeft"), A("state", "frozen")))),
                    S("sheetFormatPr", A("defaultRowHeight", 20)), Columns((1, 1, 27), (2, 3, 24), (4, 5, 22), (6, 6, 20), (7, 7, 22), (8, 9, 24), (10, 10, 20), (11, 12, 24), (13, 13, 20), (14, 16, 30)), S("sheetData", data),
                    S("autoFilter", A("ref", "A1:P" + (minutes.Count + 1)))).ToString());
                var summary = new List<XElement>
                {
                    new XElement(s + "row", A("r", 1), A("ht", 32), A("customHeight", 1), Cell("A1", "FRP 带宽历史折线图" + (demo ? "（合成数据演示）" : ""), 2)),
                    Row(2, Cell("A2", "导出范围：" + ExportService.RangeLabel(range) + "，" + (end - range).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + " 至 " + end.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"))),
                    Row(3, Cell("A3", "单位 Mb/s；分钟平均速率；未采集时段留空，已采集的零流量保留。数据来源：本机 FRPMonitor 历史。")),
                    new XElement(s + "row", A("r", 4), A("ht", 30), A("customHeight", 1),
                        Cell("A4", "方向", 5), Cell("B4", "最低分钟平均", 5), Cell("C4", "加权平均", 5), Cell("D4", "最高分钟平均", 5), Cell("E4", "最新分钟平均", 5), Cell("F4", "已记录秒级最高", 5), Cell("G4", "已记录秒级最低", 5)),
                    Row(7, Cell("A7", "导出软件：" + AppInfo.DisplayName + "。下方为 Excel 原生图表，可点击修改系列、坐标轴、样式和尺寸。")),
                    Row(8, Cell("A8", "秒级最高 / 最低取约 1 秒采样速率，真实零流量参与最低值；未记录的旧数据留空。详细图每点标数，可在 Excel / WPS 中编辑。")),
                    Row(9, Cell("A9", new CoverageInfo(minutes.Sum(m => m.Seconds), minutes.Sum(m => m.PeakSeconds), minutes.Sum(m => m.MinimumSeconds), minutes.Sum(m => m.UncertainUploadBytes), minutes.Sum(m => m.UncertainDownloadBytes)).Description)),
                    Row(10, Cell("A10", "统计按分钟边界；异常间隔的字节在 N / O 列，按收到时的分钟归档，发生时间不确定，不参与平均速率、峰值和最低值。"))
                };
                for (int i = 0; i < 2; i++)
                {
                    var stats = ChartData.Statistics(minutes.Where(m => m.Seconds > 0).Select(m => m.Point), i == 0);
                    var row = i + 5;
                    var valueStyle = i == 0 ? 6 : 7;
                    var peakValues = minutes.Select(m => i == 0 ? m.PeakUpload : m.PeakDownload).Where(value => value.HasValue).ToArray();
                    double? peak = peakValues.Length == 0 ? null : Units.Megabits(peakValues.Max()!.Value);
                    var minimumValues = minutes.Select(m => i == 0 ? m.MinimumUpload : m.MinimumDownload).Where(value => value.HasValue).ToArray();
                    double? minimum = minimumValues.Length == 0 ? null : Units.Megabits(minimumValues.Min()!.Value);
                    summary.Add(new XElement(s + "row", A("r", row), A("ht", 30), A("customHeight", 1),
                        Cell("A" + row, i == 0 ? "上传" : "下载", i == 0 ? 8 : 9), Cell("B" + row, stats == null ? null : Units.Megabits(stats.Minimum), valueStyle),
                        Cell("C" + row, stats == null ? null : Units.Megabits(stats.Average), valueStyle), Cell("D" + row, stats == null ? null : Units.Megabits(stats.Maximum), valueStyle),
                        Cell("E" + row, stats == null ? null : Units.Megabits(stats.Last), valueStyle), Cell("F" + row, peak.HasValue ? peak.Value : "未记录", valueStyle),
                        Cell("G" + row, minimum.HasValue ? minimum.Value : "未记录", valueStyle)));
                }
                var drawing = graphSheet.AddNewPart<DrawingsPart>();
                graphSheet.Worksheet = new S.Worksheet(S("worksheet", new XAttribute(XNamespace.Xmlns + "r", r),
                    S("sheetViews", S("sheetView", A("workbookViewId", 0), A("showGridLines", 0))), S("sheetFormatPr", A("defaultRowHeight", 20)),
                    Columns((1, 1, 14), (2, 7, 21)), S("sheetData", summary.OrderBy(row => (int)row.Attribute("r")!)),
                    S("mergeCells", S("mergeCell", A("ref", "A1:P1")), S("mergeCell", A("ref", "A2:P2")), S("mergeCell", A("ref", "A3:P3")), S("mergeCell", A("ref", "A7:P7")), S("mergeCell", A("ref", "A8:P8")), S("mergeCell", A("ref", "A9:P9")), S("mergeCell", A("ref", "A10:P10"))),
                    S("drawing", new XAttribute(r + "id", graphSheet.GetIdOfPart(drawing)))).ToString());
                var widths = new[] { 1200, Math.Max(1600, minutes.Count * 96) };
                var anchors = new List<XElement>();
                for (int i = 0; i < (options.SplitDays ? 1 : 2); i++)
                {
                    var part = drawing.AddNewPart<ChartPart>();
                    part.ChartSpace = new C.ChartSpace(Chart(minutes, widths[i], ExportService.RangeLabel(range) + (i == 0 ? "上传 / 下载分钟平均 · 概览" : "上传 / 下载分钟平均 · 每点数值详细趋势"), i == 1 || minutes.Count <= 12).ToString());
                    anchors.Add(Anchor(i + 1, i == 0 ? 11 : 33, widths[i], i == 0 ? 390 : 600, drawing.GetIdOfPart(part)));
                }
                if (options.PeakCharts)
                {
                    var part = drawing.AddNewPart<ChartPart>();
                    var width = options.SplitDays ? 1200 : widths[1];
                    part.ChartSpace = new C.ChartSpace(Chart(minutes, width, "已记录秒级峰值（缺失时段留空）", !options.SplitDays, 2, true).ToString());
                    anchors.Add(Anchor(3, options.SplitDays ? 33 : 67, width, 600, drawing.GetIdOfPart(part)));
                }
                drawing.WorksheetDrawing = new D.WorksheetDrawing(new XElement(xdr + "wsDr", new XAttribute(XNamespace.Xmlns + "a", a), new XAttribute(XNamespace.Xmlns + "r", r), anchors).ToString());
                if (options.SplitDays)
                {
                    uint sheetId = 3;
                    foreach (var day in minutes.Select((minute, index) => (Minute: minute, Index: index)).GroupBy(item => item.Minute.Point.Time.LocalDateTime.Date))
                    {
                        var dayMinutes = day.Select(item => item.Minute).ToArray();
                        var daySheet = workbook.AddNewPart<WorksheetPart>();
                        sheets.Append(new S.Sheet { Id = workbook.GetIdOfPart(daySheet), SheetId = sheetId++, Name = day.Key.ToString("MM-dd") + "趋势" });
                        var dayDrawing = daySheet.AddNewPart<DrawingsPart>();
                        daySheet.Worksheet = new S.Worksheet(S("worksheet", new XAttribute(XNamespace.Xmlns + "r", r), S("sheetViews", S("sheetView", A("workbookViewId", 0), A("showGridLines", 0))),
                            S("sheetFormatPr", A("defaultRowHeight", 20)), Columns((1, 1, 40)), S("sheetData", Row(1, Cell("A1", day.Key.ToString("yyyy-MM-dd") + " 带宽趋势", 2)), Row(2, Cell("A2", "单位 Mb/s；分钟平均与秒级峰值分别绘图，全部数据见“分钟数据”。"))),
                            S("drawing", new XAttribute(r + "id", daySheet.GetIdOfPart(dayDrawing)))).ToString());
                        var dayAnchors = new List<XElement>(); var width = Math.Max(1600, dayMinutes.Length * 96);
                        for (int metric = 0; metric < (options.PeakCharts ? 2 : 1); metric++)
                        {
                            var part = dayDrawing.AddNewPart<ChartPart>();
                            part.ChartSpace = new C.ChartSpace(Chart(dayMinutes, width, day.Key.ToString("MM-dd") + (metric == 0 ? " 分钟平均 Mb/s" : " 已记录秒级峰值 Mb/s"), true, day.First().Index + 2, metric == 1).ToString());
                            dayAnchors.Add(Anchor(metric + 1, metric == 0 ? 4 : 38, width, 600, dayDrawing.GetIdOfPart(part)));
                        }
                        dayDrawing.WorksheetDrawing = new D.WorksheetDrawing(new XElement(xdr + "wsDr", new XAttribute(XNamespace.Xmlns + "a", a), new XAttribute(XNamespace.Xmlns + "r", r), dayAnchors).ToString());
                    }
                }
                workbook.Workbook.Save();
            }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static XElement Anchor(int id, int row, int width, int height, string relationship) => new(xdr + "oneCellAnchor",
        new XElement(xdr + "from", new XElement(xdr + "col", 0), new XElement(xdr + "colOff", 0), new XElement(xdr + "row", row), new XElement(xdr + "rowOff", 0)),
        new XElement(xdr + "ext", A("cx", (long)width * 9525), A("cy", (long)height * 9525)),
        new XElement(xdr + "graphicFrame", A("macro", ""), new XElement(xdr + "nvGraphicFramePr", new XElement(xdr + "cNvPr", A("id", id), A("name", "FRP 折线图 " + id)), new XElement(xdr + "cNvGraphicFramePr")),
            new XElement(xdr + "xfrm", new XElement(a + "off", A("x", 0), A("y", 0)), new XElement(a + "ext", A("cx", 0), A("cy", 0))),
            new XElement(a + "graphic", new XElement(a + "graphicData", A("uri", c.NamespaceName), C("chart", new XAttribute(r + "id", relationship))))),
        new XElement(xdr + "clientData"));

    private static XElement Rich(string text) => C("rich", new XElement(a + "bodyPr"), new XElement(a + "lstStyle"),
        new XElement(a + "p", new XElement(a + "r", new XElement(a + "rPr", A("lang", "zh-CN"), A("sz", 1200)), new XElement(a + "t", text))));
    private static XElement Title(string text) => C("title", C("tx", Rich(text)), C("layout"), V("overlay", 0));
    private static XElement Line(string color, int width = 19050) => C("spPr", new XElement(a + "ln", A("w", width), new XElement(a + "solidFill", new XElement(a + "srgbClr", A("val", color))), new XElement(a + "prstDash", A("val", "solid"))));
    private static XElement Chart(IReadOnlyList<MinuteBucket> minutes, int width, string title, bool showPointValues, int firstRow = 2, bool peakSeries = false)
    {
        var lastRow = firstRow + minutes.Count - 1;
        double? Value(MinuteBucket minute, int index) => peakSeries ? index == 0 ? minute.PeakUpload : minute.PeakDownload : minute.Seconds > 0 ? index == 0 ? minute.Point.Upload : minute.Point.Download : null;
        XElement Series(int index, string name, string color, string column)
        {
            var categories = C("strCache", V("ptCount", minutes.Count), minutes.Select((m, i) => C("pt", A("idx", i), C("v", m.Point.Time.ToLocalTime().ToString("MM/dd HH:mm")))));
            var values = C("numCache", C("formatCode", "0.0000"), V("ptCount", minutes.Count), minutes.Select((m, i) => !Value(m, index).HasValue ? null : C("pt", A("idx", i), C("v", Number(Units.Megabits(Value(m, index)!.Value))))));
            var labels = showPointValues ? C("dLbls", C("numFmt", A("formatCode", "0.######"), A("sourceLinked", 0)),
                C("txPr", new XElement(a + "bodyPr", A("rot", 0)), new XElement(a + "lstStyle"),
                    new XElement(a + "p", new XElement(a + "pPr", new XElement(a + "defRPr", A("sz", 1000),
                        new XElement(a + "solidFill", new XElement(a + "srgbClr", A("val", color))))), new XElement(a + "endParaRPr", A("lang", "zh-CN")))),
                V("dLblPos", index == 0 ? "t" : "b"), V("showLegendKey", 0), V("showVal", 1), V("showCatName", 0), V("showSerName", 0), V("showPercent", 0), V("showBubbleSize", 0)) : null;
            var marker = C("marker", V("symbol", showPointValues ? "circle" : "none"), showPointValues ? V("size", 3) : null,
                showPointValues ? C("spPr", new XElement(a + "solidFill", new XElement(a + "srgbClr", A("val", color))),
                    new XElement(a + "ln", new XElement(a + "solidFill", new XElement(a + "srgbClr", A("val", color))))) : null);
            return C("ser", V("idx", index), V("order", index), C("tx", C("v", name)), Line(color), marker, labels,
                C("cat", C("strRef", C("f", "'分钟数据'!$G$" + firstRow + ":$G$" + lastRow), categories)),
                C("val", C("numRef", C("f", "'分钟数据'!$" + column + "$" + firstRow + ":$" + column + "$" + lastRow), values)), V("smooth", 0));
        }
        var skip = Math.Max(1, (int)Math.Ceiling(minutes.Count * 100.0 / width));
        var peak = minutes.Select(m => Units.Megabits(Math.Max(Value(m, 0) ?? 0, Value(m, 1) ?? 0))).DefaultIfEmpty(0).Max();
        var axisFormat = peak > 0 && peak < 0.01 ? "0.000000" : "0.00";
        var plot = C("plotArea", C("layout"), C("lineChart", V("grouping", "standard"), V("varyColors", 0), Series(0, peakSeries ? "上传秒级峰值 Mb/s" : "上传分钟平均 Mb/s", "119B85", peakSeries ? "H" : "B"), Series(1, peakSeries ? "下载秒级峰值 Mb/s" : "下载分钟平均 Mb/s", "5479DC", peakSeries ? "I" : "C"), V("marker", 0), V("smooth", 0), V("axId", 201), V("axId", 202)),
            C("catAx", V("axId", 201), C("scaling", V("orientation", "minMax")), V("delete", 0), V("axPos", "b"), V("majorTickMark", "none"), V("minorTickMark", "none"), V("tickLblPos", "nextTo"), V("crossAx", 202), V("crosses", "autoZero"), V("auto", 0), V("lblAlgn", "ctr"), V("lblOffset", 100), V("tickLblSkip", skip), V("tickMarkSkip", skip)),
            C("valAx", V("axId", 202), C("scaling", V("orientation", "minMax"), V("min", 0)), V("delete", 0), V("axPos", "l"), C("majorGridlines", Line("E2E8EF", 6350)), Title("Mb/s"), C("numFmt", A("formatCode", axisFormat), A("sourceLinked", 0)), V("majorTickMark", "none"), V("minorTickMark", "none"), V("tickLblPos", "nextTo"), V("crossAx", 201), V("crosses", "autoZero"), V("crossBetween", "between")));
        return C("chartSpace", new XAttribute(XNamespace.Xmlns + "a", a), new XAttribute(XNamespace.Xmlns + "r", r), V("date1904", 0), V("lang", "zh-CN"),
            C("chart", Title(title), V("autoTitleDeleted", 0), plot, C("legend", V("legendPos", "b"), C("layout"), V("overlay", 0)), V("plotVisOnly", 1), V("dispBlanksAs", "gap"), V("showDLblsOverMax", 0)));
    }

    private static string Styles() => S("styleSheet",
        S("numFmts", A("count", 2), S("numFmt", A("numFmtId", 164), A("formatCode", "0.0000")), S("numFmt", A("numFmtId", 165), A("formatCode", "yyyy-mm-dd hh:mm"))),
        S("fonts", A("count", 7), S("font", S("sz", A("val", 11)), S("name", A("val", "Microsoft YaHei"))),
            S("font", S("b"), S("sz", A("val", 11)), S("color", A("rgb", "FFFFFFFF")), S("name", A("val", "Microsoft YaHei"))),
            S("font", S("b"), S("sz", A("val", 20)), S("name", A("val", "Microsoft YaHei"))),
            SummaryFont("FF087F6C", false), SummaryFont("FF345FB7", false), SummaryFont("FF087F6C", true), SummaryFont("FF345FB7", true)),
        S("fills", A("count", 5), S("fill", S("patternFill", A("patternType", "none"))), S("fill", S("patternFill", A("patternType", "gray125"))),
            SolidFill("FF1D3048"), SolidFill("FFEEF9F6"), SolidFill("FFEFF3FC")),
        S("borders", A("count", 3), S("border", S("left"), S("right"), S("top"), S("bottom"), S("diagonal")),
            SummaryBorder("FFD5E0EB"), SummaryBorder("FFFFFFFF")),
        S("cellStyleXfs", A("count", 1), S("xf", A("numFmtId", 0), A("fontId", 0), A("fillId", 0), A("borderId", 0))),
        S("cellXfs", A("count", 10),
            S("xf", A("numFmtId", 0), A("fontId", 0), A("fillId", 0), A("borderId", 0), A("xfId", 0)),
            S("xf", A("numFmtId", 0), A("fontId", 1), A("fillId", 2), A("borderId", 0), A("xfId", 0), A("applyFont", 1), A("applyFill", 1)),
            S("xf", A("numFmtId", 0), A("fontId", 2), A("fillId", 0), A("borderId", 0), A("xfId", 0), A("applyFont", 1)),
            S("xf", A("numFmtId", 164), A("fontId", 0), A("fillId", 0), A("borderId", 0), A("xfId", 0), A("applyNumberFormat", 1)),
            S("xf", A("numFmtId", 165), A("fontId", 0), A("fillId", 0), A("borderId", 0), A("xfId", 0), A("applyNumberFormat", 1)),
            SummaryStyle(0, 1, 2, 2), SummaryStyle(164, 3, 3, 1), SummaryStyle(164, 4, 4, 1), SummaryStyle(0, 5, 3, 1), SummaryStyle(0, 6, 4, 1)),
        S("cellStyles", A("count", 1), S("cellStyle", A("name", "Normal"), A("xfId", 0), A("builtinId", 0)))).ToString();

    private static XElement SummaryFont(string color, bool bold) => S("font", bold ? S("b") : null,
        S("sz", A("val", 12)), S("color", A("rgb", color)), S("name", A("val", "Microsoft YaHei")));
    private static XElement SolidFill(string color) => S("fill", S("patternFill", A("patternType", "solid"),
        S("fgColor", A("rgb", color)), S("bgColor", A("indexed", 64))));
    private static XElement SummaryBorder(string color) => S("border",
        new[] { "left", "right", "top", "bottom" }.Select(edge => S(edge, A("style", "thin"), S("color", A("rgb", color)))), S("diagonal"));
    private static XElement SummaryStyle(int format, int font, int fill, int border) => S("xf", A("numFmtId", format),
        A("fontId", font), A("fillId", fill), A("borderId", border), A("xfId", 0), A("applyFont", 1), A("applyFill", 1),
        A("applyBorder", 1), A("applyNumberFormat", 1), A("applyAlignment", 1), S("alignment", A("horizontal", "center"), A("vertical", "center")));
}
