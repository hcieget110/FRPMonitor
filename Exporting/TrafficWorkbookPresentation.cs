using System;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;

namespace FRPMonitor.Exporting;

// Shared presentation only: no knowledge of collectors, credentials, or history stores.
internal static class TrafficWorkbookPresentation
{
    public const string NumberFormat = "0.00";
    public static string CsvNumber(double value) => value.ToString("F2", CultureInfo.InvariantCulture);
    public static string ShortRange(TimeSpan range) => range.TotalSeconds < 60
        ? "近" + range.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture) + "秒"
        : "近" + range.TotalMinutes.ToString("0.##", CultureInfo.InvariantCulture) + "分钟";
    private static readonly XNamespace s = "http://schemas.openxmlformats.org/spreadsheetml/2006/main", c = "http://schemas.openxmlformats.org/drawingml/2006/chart", a = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static XAttribute A(string name, object value) => new(name, value);
    private static XElement S(string name, params object?[] values) => new(s + name, values);
    private static XElement C(string name, params object?[] values) => new(c + name, values);
    private static XElement V(string name, object value) => C(name, A("val", value));

    public static XElement ChartTitle(string text, int width)
    {
        // Keep the native title in the first visible viewport of a horizontally wide chart.
        var wide = width > 1600;
        var layout = wide ? C("layout", C("manualLayout", V("xMode", "factor"), V("yMode", "factor"), V("wMode", "factor"), V("hMode", "factor"),
            V("x", (16.0 / width).ToString("R", CultureInfo.InvariantCulture)), V("y", 0.01), V("w", (800.0 / width).ToString("R", CultureInfo.InvariantCulture)), V("h", 0.08))) : C("layout");
        return C("title", C("tx", C("rich", new XElement(a + "bodyPr"), new XElement(a + "lstStyle"),
            new XElement(a + "p", new XElement(a + "pPr", A("algn", wide ? "l" : "ctr")), new XElement(a + "r", new XElement(a + "rPr", A("lang", "zh-CN"), A("sz", 1200)), new XElement(a + "t", text))))), layout, V("overlay", 0));
    }

    // Style indices are stable for both exporters: normal, header, title, number, time,
    // summary header, upload values, download values, upload label, download label.
    public static XElement Styles(string dateFormat) => S("styleSheet",
        S("numFmts", A("count", 2), S("numFmt", A("numFmtId", 164), A("formatCode", NumberFormat)), S("numFmt", A("numFmtId", 165), A("formatCode", dateFormat))),
        S("fonts", A("count", 7), S("font", S("sz", A("val", 11)), S("name", A("val", "Microsoft YaHei"))),
            S("font", S("b"), S("sz", A("val", 11)), S("color", A("rgb", "FFFFFFFF")), S("name", A("val", "Microsoft YaHei"))),
            S("font", S("b"), S("sz", A("val", 20)), S("name", A("val", "Microsoft YaHei"))),
            Font("FF087F6C", false), Font("FF345FB7", false), Font("FF087F6C", true), Font("FF345FB7", true)),
        S("fills", A("count", 5), S("fill", S("patternFill", A("patternType", "none"))), S("fill", S("patternFill", A("patternType", "gray125"))), Fill("FF1D3048"), Fill("FFEEF9F6"), Fill("FFEFF3FC")),
        S("borders", A("count", 3), S("border", S("left"), S("right"), S("top"), S("bottom"), S("diagonal")), Border("FFD5E0EB"), Border("FFFFFFFF")),
        S("cellStyleXfs", A("count", 1), S("xf", A("numFmtId", 0), A("fontId", 0), A("fillId", 0), A("borderId", 0))),
        S("cellXfs", A("count", 10),
            S("xf", A("numFmtId", 0), A("fontId", 0), A("fillId", 0), A("borderId", 0), A("xfId", 0)),
            SummaryStyle(0, 1, 2, 2),
            S("xf", A("numFmtId", 0), A("fontId", 2), A("fillId", 0), A("borderId", 0), A("xfId", 0), A("applyFont", 1)),
            S("xf", A("numFmtId", 164), A("fontId", 0), A("fillId", 0), A("borderId", 0), A("xfId", 0), A("applyNumberFormat", 1)),
            S("xf", A("numFmtId", 165), A("fontId", 0), A("fillId", 0), A("borderId", 0), A("xfId", 0), A("applyNumberFormat", 1)),
            SummaryStyle(0, 1, 2, 2), SummaryStyle(164, 3, 3, 1), SummaryStyle(164, 4, 4, 1), SummaryStyle(0, 5, 3, 1), SummaryStyle(0, 6, 4, 1)),
        S("cellStyles", A("count", 1), S("cellStyle", A("name", "Normal"), A("xfId", 0), A("builtinId", 0))));
    private static XElement Font(string color, bool bold) => S("font", bold ? S("b") : null, S("sz", A("val", 12)), S("color", A("rgb", color)), S("name", A("val", "Microsoft YaHei")));
    private static XElement Fill(string color) => S("fill", S("patternFill", A("patternType", "solid"), S("fgColor", A("rgb", color)), S("bgColor", A("indexed", 64))));
    private static XElement Border(string color) => S("border", new[] { "left", "right", "top", "bottom" }.Select(edge => S(edge, A("style", "thin"), S("color", A("rgb", color)))), S("diagonal"));
    private static XElement SummaryStyle(int format, int font, int fill, int border) => S("xf", A("numFmtId", format), A("fontId", font), A("fillId", fill), A("borderId", border), A("xfId", 0), A("applyFont", 1), A("applyFill", 1), A("applyBorder", 1), A("applyNumberFormat", 1), A("applyAlignment", 1), S("alignment", A("horizontal", "center"), A("vertical", "center")));
}
