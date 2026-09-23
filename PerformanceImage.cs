#region Imports
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
#endregion
namespace GNA_DBDayReductions;
#region Performance Image Layout and Rendering
public sealed record PerformanceImageRow(string Type, PerformanceSensor? Sensor);
public sealed record PerformanceImagePage(IReadOnlyList<DateTime> Days, IReadOnlyList<PerformanceImageRow> Rows);
public static class PerformanceImage
{
    public const double WidthMm = 250;
    public const double HeightMm = 200;
    public const double Dpi = 300;
    public const int DaysPerPage = 35;
    public const int RowsPerPage = 34;
    public static int PixelWidth => (int)Math.Round(a: WidthMm / 25.4 * Dpi);
    public static int PixelHeight => (int)Math.Round(a: HeightMm / 25.4 * Dpi);
    private const double Margin = 24;
    private const double NameWidth = 258;
    private const double DayWidth = 18;
    private const double RowHeight = 18;
    private static readonly Typeface Font = new(typefaceName: "Arial");

    public static IReadOnlyList<PerformanceImagePage> Pages(PerformanceReport report)
    {
        List<DateTime> days = new();
        for (DateTime day = report.Request.StartDate.Date; day <= report.Request.EndDate.Date; day = day.AddDays(value: 1)) days.Add(item: day);
        List<IReadOnlyList<PerformanceImageRow>> rowPages = new();
        List<PerformanceImageRow> rows = new(); string? type = null;
        foreach (PerformanceSensor sensor in report.FailingSensors)
        {
            int needed = type == sensor.SensorType ? 1 : 2;
            if (rows.Count + needed > RowsPerPage) { rowPages.Add(item: rows.AsReadOnly()); rows = new(); type = null; }
            if (type != sensor.SensorType) { rows.Add(item: new(Type: sensor.SensorType, Sensor: null)); type = sensor.SensorType; }
            rows.Add(item: new(Type: sensor.SensorType, Sensor: sensor));
        }
        if (rows.Count > 0 || rowPages.Count == 0) rowPages.Add(item: rows.AsReadOnly());
        List<PerformanceImagePage> pages = new();
        if (report.FailingSensors.Count == 0)
        {
            pages.Add(item: new(Days: Array.Empty<DateTime>(), Rows: Array.Empty<PerformanceImageRow>()));
            return pages.AsReadOnly();
        }
        for (int first = 0; first < days.Count; first += DaysPerPage)
        {
            IReadOnlyList<DateTime> block = days.GetRange(index: first, count: Math.Min(val1: DaysPerPage, val2: days.Count - first)).AsReadOnly();
            foreach (IReadOnlyList<PerformanceImageRow> blockRows in rowPages) pages.Add(item: new(Days: block, Rows: blockRows));
        }
        return pages.AsReadOnly();
    }
    private static void Text(DrawingContext context, string value, double x, double y, double width, double size, bool centred = false, bool fitName = false)
    {
        if (fitName)
        {
            FormattedText measured = new(textToFormat: value, culture: CultureInfo.InvariantCulture, flowDirection: FlowDirection.LeftToRight,
                typeface: Font, emSize: size, foreground: Brushes.Black, pixelsPerDip: Dpi / 96);
            if (measured.WidthIncludingTrailingWhitespace > width)
                size *= width / measured.WidthIncludingTrailingWhitespace;
        }
        FormattedText text = new(textToFormat: value, culture: CultureInfo.InvariantCulture, flowDirection: FlowDirection.LeftToRight,
            typeface: Font, emSize: size, foreground: Brushes.Black, pixelsPerDip: Dpi / 96)
        { MaxTextWidth = width, MaxTextHeight = RowHeight, Trimming = TextTrimming.CharacterEllipsis, TextAlignment = centred ? TextAlignment.Center : TextAlignment.Left };
        context.DrawText(formattedText: text, origin: new Point(x: x, y: y));
    }
    public static BitmapSource Render(PerformanceReport report, PerformanceImagePage page, int pageNumber, int pageCount)
    {
        double width = PixelWidth * 96 / Dpi, height = PixelHeight * 96 / Dpi;
        DrawingVisual visual = new();
        using (DrawingContext context = visual.RenderOpen())
        {
            context.DrawRectangle(brush: Brushes.White, pen: null, rectangle: new Rect(x: 0, y: 0, width: width, height: height));
            Text(context: context, value: report.Heading, x: Margin, y: Margin, width: width - 2 * Margin, size: 16);
            if (report.FailingSensors.Count == 0)
                Text(context: context, value: "No fails", x: Margin, y: height / 2, width: width - 2 * Margin, size: 16, centred: true);
            else
            {
                double y = 60, tableWidth = NameWidth + DayWidth * page.Days.Count;
                Pen border = new(brush: Brushes.Gray, thickness: 0.5);
                context.DrawRectangle(brush: Brushes.WhiteSmoke, pen: border, rectangle: new Rect(x: Margin, y: y, width: tableWidth, height: RowHeight));
                Text(context: context, value: "Sensor name", x: Margin + 3, y: y + 2, width: NameWidth - 6, size: 11);
                for (int index = 0; index < page.Days.Count; index++)
                    Text(context: context, value: page.Days[index].ToString(format: "dd", provider: CultureInfo.InvariantCulture),
                        x: Margin + NameWidth + index * DayWidth, y: y + 2, width: DayWidth, size: 10, centred: true);
                y += RowHeight;
                foreach (PerformanceImageRow row in page.Rows)
                {
                    if (row.Sensor is null)
                    {
                        context.DrawRectangle(brush: Brushes.LightGray, pen: border, rectangle: new Rect(x: Margin, y: y, width: tableWidth, height: RowHeight));
                        Text(context: context, value: row.Type, x: Margin + 3, y: y + 2, width: tableWidth - 6, size: 11);
                    }
                    else
                    {
                        context.DrawRectangle(brush: Brushes.White, pen: border, rectangle: new Rect(x: Margin, y: y, width: NameWidth, height: RowHeight));
                        Text(context: context, value: row.Sensor.SensorName, x: Margin + 3, y: y + 2, width: NameWidth - 6, size: 11, fitName: true);
                        for (int index = 0; index < page.Days.Count; index++)
                        {
                            Brush colour = row.Sensor.Days.TryGetValue(key: page.Days[index], value: out bool pass) ? (pass ? Brushes.Green : Brushes.Red) : Brushes.White;
                            context.DrawRectangle(brush: colour, pen: border, rectangle: new Rect(x: Margin + NameWidth + index * DayWidth, y: y, width: DayWidth, height: RowHeight));
                        }
                    }
                    y += RowHeight;
                }
                Text(context: context, value: $"Columns: {page.Days[0]:yyyy-MM-dd} to {page.Days[^1]:yyyy-MM-dd}    Page {pageNumber}/{pageCount}",
                    x: Margin, y: height - 34, width: width - Margin * 2, size: 10);
            }
        }
        // Close the drawing context before rendering the visual.
        RenderTargetBitmap bitmap = new(pixelWidth: PixelWidth, pixelHeight: PixelHeight, dpiX: Dpi, dpiY: Dpi, pixelFormat: PixelFormats.Pbgra32);
        bitmap.Render(visual: visual); bitmap.Freeze(); return bitmap;
    }
}
#endregion


