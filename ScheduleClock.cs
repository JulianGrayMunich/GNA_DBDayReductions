#region Imports
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
#endregion
namespace GNA_DBDayReductions;
#region Draggable Schedule Clock
public sealed class ScheduleClock : FrameworkElement
{
    private const double FaceSize = 220;
    private const double Centre = FaceSize / 2;
    private const double HourLength = 53;
    private const double MinuteLength = 82;
    private bool _minuteHand;
    public static readonly DependencyProperty TimeProperty = DependencyProperty.Register(name: nameof(Time), propertyType: typeof(TimeOnly), ownerType: typeof(ScheduleClock),
        typeMetadata: new FrameworkPropertyMetadata(defaultValue: new TimeOnly(hour: 0, minute: 1), flags: FrameworkPropertyMetadataOptions.AffectsRender,
            propertyChangedCallback: OnTimeChanged, coerceValueCallback: CoerceTime));
    public TimeOnly Time { get => (TimeOnly)GetValue(dp: TimeProperty); set => SetValue(dp: TimeProperty, value: value); }
    public event EventHandler? TimeChanged;
    public ScheduleClock() { Focusable = true; Cursor = Cursors.Hand; Width = FaceSize; Height = FaceSize; }
    private static object CoerceTime(DependencyObject sender, object value)
    { TimeOnly time = (TimeOnly)value; return new TimeOnly(hour: time.Hour, minute: Math.Max(val1: 1, val2: time.Minute)); }
    private static void OnTimeChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        => ((ScheduleClock)sender).TimeChanged?.Invoke(sender: sender, e: EventArgs.Empty);
    private static Point Tip(double turns, double length)
    {
        double angle = turns * Math.PI * 2;
        return new(x: Centre + Math.Sin(a: angle) * length, y: Centre - Math.Cos(d: angle) * length);
    }
    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext: drawingContext);
        Point centre = new(x: Centre, y: Centre);
        drawingContext.DrawEllipse(brush: Brushes.White, pen: new Pen(brush: Brushes.DarkGray, thickness: 3), center: centre, radiusX: 105, radiusY: 105);
        for (int tick = 0; tick < 60; tick++)
            drawingContext.DrawLine(pen: new Pen(brush: Brushes.Black, thickness: tick % 5 == 0 ? 2 : 1), point0: Tip(turns: tick / 60.0, length: tick % 5 == 0 ? 90 : 95), point1: Tip(turns: tick / 60.0, length: 100));
        for (int hour = 1; hour <= 12; hour++)
        {
            FormattedText text = new(textToFormat: hour.ToString(provider: CultureInfo.InvariantCulture), culture: CultureInfo.InvariantCulture,
                flowDirection: FlowDirection.LeftToRight, typeface: new Typeface(typefaceName: "Arial"), emSize: 16, foreground: Brushes.Black,
                pixelsPerDip: VisualTreeHelper.GetDpi(visual: this).PixelsPerDip);
            Point position = Tip(turns: hour / 12.0, length: 76);
            drawingContext.DrawText(formattedText: text, origin: new Point(x: position.X - text.Width / 2, y: position.Y - text.Height / 2));
        }
        Point hourTip = Tip(turns: (Time.Hour % 12 + Time.Minute / 60.0) / 12, length: HourLength);
        Point minuteTip = Tip(turns: Time.Minute / 60.0, length: MinuteLength);
        drawingContext.DrawLine(pen: new Pen(brush: Brushes.DimGray, thickness: 5), point0: centre, point1: hourTip);
        drawingContext.DrawLine(pen: new Pen(brush: Brushes.SteelBlue, thickness: 3), point0: centre, point1: minuteTip);
        drawingContext.DrawEllipse(brush: Brushes.DimGray, pen: new Pen(brush: Brushes.Black, thickness: 1), center: hourTip, radiusX: 5, radiusY: 5);
        drawingContext.DrawEllipse(brush: Brushes.SteelBlue, pen: null, center: minuteTip, radiusX: 5, radiusY: 5);
        drawingContext.DrawEllipse(brush: Brushes.DimGray, pen: new Pen(brush: Brushes.Black, thickness: 1), center: centre, radiusX: 6, radiusY: 6);
    }
    private static double DistanceToHand(Point point, Point tip)
    {
        Vector hand = tip - new Point(x: Centre, y: Centre);
        Vector cursor = point - new Point(x: Centre, y: Centre);
        double fraction = Math.Clamp(value: Vector.Multiply(vector1: cursor, vector2: hand) / hand.LengthSquared, min: 0, max: 1);
        return (cursor - hand * fraction).Length;
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e: e);
        Point point = e.GetPosition(relativeTo: this);
        if ((point - new Point(x: Centre, y: Centre)).Length < 10) return;
        double hour = DistanceToHand(point: point, tip: Tip(turns: (Time.Hour % 12 + Time.Minute / 60.0) / 12, length: HourLength));
        double minute = DistanceToHand(point: point, tip: Tip(turns: Time.Minute / 60.0, length: MinuteLength));
        if (Math.Min(val1: hour, val2: minute) > 14) return;
        _minuteHand = minute <= hour;
        Focus(); CaptureMouse(); e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e: e);
        if (!IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed) return;
        SetHandPosition(point: e.GetPosition(relativeTo: this), minuteHand: _minuteHand); e.Handled = true;
    }
    internal void SetHandPosition(Point point, bool minuteHand)
    {
        Vector offset = point - new Point(x: Centre, y: Centre);
        if (offset.Length < 10) return;
        double turns = (Math.Atan2(y: offset.X, x: -offset.Y) / (2 * Math.PI) + 1) % 1;
        if (minuteHand)
        {
            int minute = (int)Math.Round(value: turns * 60, mode: MidpointRounding.AwayFromZero) % 60;
            Time = new(hour: Time.Hour, minute: Math.Max(val1: 1, val2: minute));
        }
        else
        {
            int hour = ((int)Math.Round(value: turns * 12 - Time.Minute / 60.0, mode: MidpointRounding.AwayFromZero) + 12) % 12;
            Time = new(hour: hour + (Time.Hour >= 12 ? 12 : 0), minute: Time.Minute);
        }
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e: e);
        if (!IsMouseCaptured) return;
        SetHandPosition(point: e.GetPosition(relativeTo: this), minuteHand: _minuteHand);
        ReleaseMouseCapture(); e.Handled = true;
    }
}
#endregion

