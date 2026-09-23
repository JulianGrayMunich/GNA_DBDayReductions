#region Imports
using System.Globalization;
using System.Windows;
using System.Windows.Input;
#endregion
namespace GNA_DBDayReductions;
#region Schedule Clock Interface
public partial class MainWindow
{
    private bool _synchronizingClock;
    private void ClockTime_Changed(object? sender, EventArgs e)
    {
        if (ScheduleTimeInput is null) return;
        SynchronizeClockControls();
        if (_initializing) return;
        UpdateScheduleDescription();
        if (!SchedulerClock.IsMouseCaptured) SavePreferences();
    }
    private void SynchronizeClockControls()
    {
        _synchronizingClock = true;
        try
        {
            ScheduleTimeInput.Text = ScheduledLocalTime.ToString(format: "HH:mm", provider: CultureInfo.InvariantCulture);
            ScheduleAm.IsChecked = ScheduledLocalTime.Hour < 12; SchedulePm.IsChecked = ScheduledLocalTime.Hour >= 12;
        }
        finally { _synchronizingClock = false; }
    }
    private void ClockCapture_Lost(object sender, MouseEventArgs e)
    { if (!_initializing) SavePreferences(); }
    private void ClockPeriod_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing || _synchronizingClock) return;
        SchedulerClock.Time = new(hour: ScheduledLocalTime.Hour % 12 + (SchedulePm.IsChecked == true ? 12 : 0), minute: ScheduledLocalTime.Minute);
    }
    private void ScheduleTimeInput_LostFocus(object sender, RoutedEventArgs e) => ApplyClockText();
    private void ScheduleTimeInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { ApplyClockText(); e.Handled = true; }
        else if (e.Key == Key.Escape) { SynchronizeClockControls(); UpdateScheduleDescription(); e.Handled = true; }
    }
    private void ApplyClockText()
    {
        if (_initializing || _synchronizingClock) return;
        if (!TimeOnly.TryParseExact(s: ScheduleTimeInput.Text.Trim(), format: "HH:mm", provider: CultureInfo.InvariantCulture,
            style: DateTimeStyles.None, result: out TimeOnly time) || time.Minute < FirstSelectableMinute)
        {
            SynchronizeClockControls();
            SchedulePreferenceStatus.Text = "Enter a 24-hour time as HH:mm, with minutes 01–59. The previous time has been retained.";
            return;
        }
        SchedulerClock.Time = time; SynchronizeClockControls(); UpdateScheduleDescription(); SavePreferences();
    }
}
#endregion
