using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;
using S = DocumentFormat.OpenXml.Spreadsheet;
using C = DocumentFormat.OpenXml.Drawing.Charts;
using D = DocumentFormat.OpenXml.Drawing.Spreadsheet;

namespace FRPMonitor.Exporting;

// Stateless native Excel/CSV writer shared by independent monitoring modules.
public static class SecondTrafficExporter
{
    private static readonly XNamespace s = "http://schemas.openxmlformats.org/spreadsheetml/2006/main", c = "http://schemas.openxmlformats.org/drawingml/2006/chart", a = "http://schemas.openxmlformats.org/drawingml/2006/main", d = "http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing", r = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static XAttribute A(string name, object value) => new(name, value);
    private static XElement Sx(string name, params object?[] values) => new(s + name, values);
    private static XElement Cx(string name, params object?[] values) => new(c + name, values);
    private static XElement V(string name, object value) => Cx(name, A("val", value));
    private static string N(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    public static void Export(string path, IReadOnlyList<TrafficPoint> snapshot, TimeSpan range, DateTimeOffset end, bool excel, string title, string scope)
    {
        if (!Path.GetExtension(path).Equals(excel ? ".xlsx" : ".csv", StringComparison.OrdinalIgnoreCase)) throw new FormatException("文件扩展名与导出格式不符。");
        var rows = SecondSeries.Grid(snapshot, range, end);
        if (!rows.Any(p => p != null)) throw new InvalidOperationException("所选时段没有可用的秒级采样数据。");
        if (!excel)
        {
            var text = new StringBuilder("时间(本机时区),上传Mb每秒,下载Mb每秒,实际采样秒数\r\n");
            for (int i = 0; i < rows.Length; i++)
            {
                var point = rows[i]; var time = DateTimeOffset.FromUnixTimeSeconds(end.ToUnixTimeSeconds() - rows.Length + 1 + i);
                text.AppendLine(time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz") + "," + (point == null ? ",," : N(Units.Megabits(point.Upload)) + "," + N(Units.Megabits(point.Download)) + "," + N(point.Seconds)));
            }
            Disk.AtomicText(path, text.ToString(), new UTF8Encoding(true)); return;
        }
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var document = SpreadsheetDocument.Create(temp, DocumentFormat.OpenXml.SpreadsheetDocumentType.Workbook))
            {
                var wb = document.AddWorkbookPart(); wb.Workbook = new S.Workbook(); var sheets = wb.Workbook.AppendChild(new S.Sheets());
                wb.AddNewPart<WorkbookStylesPart>().Stylesheet = new S.Stylesheet(Styles().ToString());
                var graph = wb.AddNewPart<WorksheetPart>(); var data = wb.AddNewPart<WorksheetPart>();
                sheets.Append(new S.Sheet { Id = wb.GetIdOfPart(graph), SheetId = 1, Name = "秒级折线图" }, new S.Sheet { Id = wb.GetIdOfPart(data), SheetId = 2, Name = "秒级数据" });
                var dataRows = new List<XElement> { Row(1, Cell("A1", "时间（本机时区）", 1), Cell("B1", "上传 Mb/s", 1), Cell("C1", "下载 Mb/s", 1), Cell("D1", "实际采样秒数", 1), Cell("E1", "图表秒级时间", 1)) };
                for (int i = 0; i < rows.Length; i++)
                {
                    int row = i + 2; var point = rows[i]; var time = DateTimeOffset.FromUnixTimeSeconds(end.ToUnixTimeSeconds() - rows.Length + 1 + i).ToLocalTime();
                    dataRows.Add(Row(row, Cell("A" + row, time.LocalDateTime.ToOADate(), 3), Cell("B" + row, point == null ? null : Units.Megabits(point.Upload), 2), Cell("C" + row, point == null ? null : Units.Megabits(point.Download), 2), Cell("D" + row, point?.Seconds, 2), Cell("E" + row, time.ToString("HH:mm:ss"))));
                }
                data.Worksheet = new S.Worksheet(Sx("worksheet", Sx("sheetViews", Sx("sheetView", A("workbookViewId", 0), Sx("pane", A("ySplit", 1), A("topLeftCell", "A2"), A("activePane", "bottomLeft"), A("state", "frozen")))), Columns(), Sx("sheetData", dataRows), Sx("autoFilter", A("ref", "A1:E" + (rows.Length + 1)))).ToString());
                var summary = new List<XElement>
                {
                    Row(1, Cell("A1", title + " · 秒级折线图", 1)),
                    Row(2, Cell("A2", "最近 " + (int)range.TotalMinutes + " 分钟；" + (end - range).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + " 至 " + end.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"))),
                    Row(3, Cell("A3", "单位 Mb/s；1 秒采样；" + scope + "。未采集时段留空，真实零流量保留。")),
                    Row(4, Cell("A4", "方向", 1), Cell("B4", "最低 Mb/s", 1), Cell("C4", "加权平均 Mb/s", 1), Cell("D4", "最高 Mb/s", 1), Cell("E4", "最新 Mb/s", 1)),
                    Row(8, Cell("A8", "有效采样 " + rows.Count(p => p != null) + " / " + rows.Length + " 个秒级点。下方为可编辑 Excel 原生折线图。")),
                    Row(9, Cell("A9", "第二张详细图每个有效点显示数值；横向滚动查看。时间精确到秒；不进行分钟聚合。"))
                };
                for (int dir = 0; dir < 2; dir++)
                {
                    var stats = ChartData.Statistics(rows.Where(p => p != null).Select(p => p!), dir == 0)!; int row = 5 + dir;
                    summary.Add(Row(row, Cell("A" + row, dir == 0 ? "上传" : "下载", 2), Cell("B" + row, Units.Megabits(stats.Minimum), 2), Cell("C" + row, Units.Megabits(stats.Average), 2), Cell("D" + row, Units.Megabits(stats.Maximum), 2), Cell("E" + row, Units.Megabits(stats.Last), 2)));
                }
                var drawing = graph.AddNewPart<DrawingsPart>(); var anchors = new List<XElement>();
                for (int i = 0; i < 2; i++)
                {
                    var width = i == 0 ? 1100 : Math.Max(1600, rows.Length * 65); var chart = drawing.AddNewPart<ChartPart>();
                    chart.ChartSpace = new C.ChartSpace(Chart(rows, end, width, i == 1, title).ToString()); anchors.Add(Anchor(i + 1, i == 0 ? 10 : 36, width, 420, drawing.GetIdOfPart(chart)));
                }
                drawing.WorksheetDrawing = new D.WorksheetDrawing(new XElement(d + "wsDr", new XAttribute(XNamespace.Xmlns + "a", a), new XAttribute(XNamespace.Xmlns + "r", r), anchors).ToString());
                graph.Worksheet = new S.Worksheet(Sx("worksheet", new XAttribute(XNamespace.Xmlns + "r", r), Sx("sheetViews", Sx("sheetView", A("workbookViewId", 0), A("showGridLines", 0))), Columns(), Sx("sheetData", summary.OrderBy(row => (int)row.Attribute("r")!)), Sx("mergeCells", new[] { 1, 2, 3, 8, 9 }.Select(i => Sx("mergeCell", A("ref", "A" + i + ":L" + i)))), Sx("drawing", new XAttribute(r + "id", graph.GetIdOfPart(drawing)))).ToString());
                wb.Workbook.Save();
            }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static XElement Cell(string address, object? value, int style = 0) => value == null ? Sx("c", A("r", address), A("s", style)) : value is string text ? Sx("c", A("r", address), A("s", style), A("t", "inlineStr"), Sx("is", Sx("t", text))) : Sx("c", A("r", address), A("s", style), Sx("v", N(Convert.ToDouble(value, CultureInfo.InvariantCulture))));
    private static XElement Row(int index, params XElement[] cells) => Sx("row", A("r", index), A("ht", 25), A("customHeight", 1), cells);
    private static XElement Columns() => Sx("cols", Sx("col", A("min", 1), A("max", 1), A("width", 27), A("customWidth", 1)), Sx("col", A("min", 2), A("max", 5), A("width", 22), A("customWidth", 1)));
    private static XElement Title(string text) => Cx("title", Cx("tx", Cx("rich", new XElement(a + "bodyPr"), new XElement(a + "lstStyle"), new XElement(a + "p", new XElement(a + "r", new XElement(a + "rPr", A("lang", "zh-CN")), new XElement(a + "t", text))))), Cx("layout"), V("overlay", 0));
    private static XElement Line(string color) => Cx("spPr", new XElement(a + "ln", A("w", 19050), new XElement(a + "solidFill", new XElement(a + "srgbClr", A("val", color)))));
    private static XElement Chart(TrafficPoint?[] points, DateTimeOffset end, int width, bool labels, string title)
    {
        XElement Series(int index, string column, string name, string color)
        {
            var cats = Cx("strCache", V("ptCount", points.Length), points.Select((p, i) => Cx("pt", A("idx", i), Cx("v", DateTimeOffset.FromUnixTimeSeconds(end.ToUnixTimeSeconds() - points.Length + 1 + i).ToLocalTime().ToString("HH:mm:ss")))));
            var vals = Cx("numCache", Cx("formatCode", "0.0000"), V("ptCount", points.Length), points.Select((p, i) => p == null ? null : Cx("pt", A("idx", i), Cx("v", N(Units.Megabits(index == 0 ? p.Upload : p.Download))))));
            return Cx("ser", V("idx", index), V("order", index), Cx("tx", Cx("v", name)), Line(color), Cx("marker", V("symbol", labels ? "circle" : "none"), labels ? V("size", 3) : null),
                labels ? Cx("dLbls", Cx("numFmt", A("formatCode", "0.######"), A("sourceLinked", 0)), Cx("txPr", new XElement(a + "bodyPr", A("rot", 0)), new XElement(a + "lstStyle"), new XElement(a + "p", new XElement(a + "pPr", new XElement(a + "defRPr", A("sz", 900))), new XElement(a + "endParaRPr", A("lang", "zh-CN")))), V("dLblPos", index == 0 ? "t" : "b"), V("showLegendKey", 0), V("showVal", 1), V("showCatName", 0), V("showSerName", 0), V("showPercent", 0), V("showBubbleSize", 0)) : null,
                Cx("cat", Cx("strRef", Cx("f", "'秒级数据'!$E$2:$E$" + (points.Length + 1)), cats)), Cx("val", Cx("numRef", Cx("f", "'秒级数据'!$" + column + "$2:$" + column + "$" + (points.Length + 1)), vals)), V("smooth", 0));
        }
        var skip = Math.Max(1, (int)Math.Ceiling(points.Length * 70.0 / width));
        var plot = Cx("plotArea", Cx("layout"), Cx("lineChart", V("grouping", "standard"), V("varyColors", 0), Series(0, "B", "上传 Mb/s", "119B85"), Series(1, "C", "下载 Mb/s", "5479DC"), V("marker", 0), V("smooth", 0), V("axId", 301), V("axId", 302)),
            Cx("catAx", V("axId", 301), Cx("scaling", V("orientation", "minMax")), V("delete", 0), V("axPos", "b"), V("tickLblPos", "nextTo"), V("crossAx", 302), V("crosses", "autoZero"), V("auto", 0), V("lblAlgn", "ctr"), V("lblOffset", 100), V("tickLblSkip", skip), V("tickMarkSkip", skip)),
            Cx("valAx", V("axId", 302), Cx("scaling", V("orientation", "minMax"), V("min", 0)), V("delete", 0), V("axPos", "l"), Cx("majorGridlines", Line("E2E8EF")), Title("Mb/s"), Cx("numFmt", A("formatCode", "0.0000"), A("sourceLinked", 0)), V("tickLblPos", "nextTo"), V("crossAx", 301), V("crosses", "autoZero"), V("crossBetween", "between")));
        return Cx("chartSpace", new XAttribute(XNamespace.Xmlns + "a", a), new XAttribute(XNamespace.Xmlns + "r", r), V("date1904", 0), V("lang", "zh-CN"), Cx("chart", Title(title + " · 秒级带宽 · " + (labels ? "每点数值详细图" : "概览")), V("autoTitleDeleted", 0), plot, Cx("legend", V("legendPos", "b"), Cx("layout"), V("overlay", 0)), V("plotVisOnly", 1), V("dispBlanksAs", "gap"), V("showDLblsOverMax", 0)));
    }
    private static XElement Anchor(int id, int row, int width, int height, string relation) => new(d + "oneCellAnchor", new XElement(d + "from", new XElement(d + "col", 0), new XElement(d + "colOff", 0), new XElement(d + "row", row), new XElement(d + "rowOff", 0)), new XElement(d + "ext", A("cx", (long)width * 9525), A("cy", (long)height * 9525)), new XElement(d + "graphicFrame", A("macro", ""), new XElement(d + "nvGraphicFramePr", new XElement(d + "cNvPr", A("id", id), A("name", "秒级折线图 " + id)), new XElement(d + "cNvGraphicFramePr")), new XElement(d + "xfrm", new XElement(a + "off", A("x", 0), A("y", 0)), new XElement(a + "ext", A("cx", 0), A("cy", 0))), new XElement(a + "graphic", new XElement(a + "graphicData", A("uri", c.NamespaceName), Cx("chart", new XAttribute(r + "id", relation))))), new XElement(d + "clientData"));
    private static XElement Styles() => Sx("styleSheet", Sx("numFmts", A("count", 2), Sx("numFmt", A("numFmtId", 164), A("formatCode", "0.000000")), Sx("numFmt", A("numFmtId", 165), A("formatCode", "yyyy-mm-dd hh:mm:ss"))),
        Sx("fonts", A("count", 2), Sx("font", Sx("sz", A("val", 11)), Sx("name", A("val", "Microsoft YaHei"))), Sx("font", Sx("b"), Sx("sz", A("val", 11)), Sx("color", A("rgb", "FFFFFFFF")), Sx("name", A("val", "Microsoft YaHei")))),
        Sx("fills", A("count", 3), Sx("fill", Sx("patternFill", A("patternType", "none"))), Sx("fill", Sx("patternFill", A("patternType", "gray125"))), Sx("fill", Sx("patternFill", A("patternType", "solid"), Sx("fgColor", A("rgb", "FF1D3048")), Sx("bgColor", A("indexed", 64))))),
        Sx("borders", A("count", 1), Sx("border", Sx("left"), Sx("right"), Sx("top"), Sx("bottom"), Sx("diagonal"))), Sx("cellStyleXfs", A("count", 1), Sx("xf", A("numFmtId", 0), A("fontId", 0), A("fillId", 0), A("borderId", 0))),
        Sx("cellXfs", A("count", 4), Sx("xf", A("numFmtId", 0), A("fontId", 0), A("fillId", 0), A("borderId", 0), A("xfId", 0)), Sx("xf", A("numFmtId", 0), A("fontId", 1), A("fillId", 2), A("borderId", 0), A("xfId", 0), A("applyAlignment", 1), Sx("alignment", A("horizontal", "center"), A("vertical", "center"))), Sx("xf", A("numFmtId", 164), A("fontId", 0), A("fillId", 0), A("borderId", 0), A("xfId", 0), A("applyNumberFormat", 1), A("applyAlignment", 1), Sx("alignment", A("horizontal", "center"), A("vertical", "center"))), Sx("xf", A("numFmtId", 165), A("fontId", 0), A("fillId", 0), A("borderId", 0), A("xfId", 0), A("applyNumberFormat", 1))), Sx("cellStyles", A("count", 1), Sx("cellStyle", A("name", "Normal"), A("xfId", 0), A("builtinId", 0))));
}
