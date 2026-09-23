#region Imports
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Principal;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
#endregion
namespace GNA_DBDayReductions;
#region Scheduler and Log Folder Interface
public partial class MainWindow
{
    private bool _monitorScheduler;
    private bool _schedulerChanging;
    private bool _schedulerReading;
    private string? _schedulerReadError;
    private SchedulerSnapshot? _schedulerSnapshot;
    private ScheduledReductionProfile? _scheduledProfile;
    private DateTime _lastSchedulerRead;
    private readonly DispatcherTimer _schedulerTimer = new() { Interval = TimeSpan.FromSeconds(value: 1) };

    private void LogFolderButton_Click(object sender, RoutedEventArgs e)
    {
        OpenFolderDialog dialog = new() { Title = "Select system activity log folder", Multiselect = false };
        if (dialog.ShowDialog(owner: this) != true) return;
        try
        {
            SystemActivityLog.EnsureFolder(folder: dialog.FolderName);
            _settings.LogFolder = dialog.FolderName; LogFolderInput.Text = dialog.FolderName; SavePreferences();
            LogFolderStatus.Text = "System log: " + Path.Combine(path1: dialog.FolderName, path2: SystemActivityLog.FileName);
        }
        catch (Exception exception) { LogFolderStatus.Text = "Log folder could not be used: " + exception.Message; }
    }
    private async void SchedulerTimer_Tick(object? sender, EventArgs e)
    {
        if (_closing) return;
        if (!_schedulerChanging && DateTime.UtcNow - _lastSchedulerRead >= TimeSpan.FromSeconds(value: 15)) await RefreshSchedulerAsync(restoreSelection: false);
        UpdateSchedulerState();
    }
    private async Task RefreshSchedulerAsync(bool restoreSelection)
    {
        if (!_monitorScheduler || _schedulerReading || _closing) return;
        _schedulerReading = true;
        try
        {
            string sid = ScheduledReductionProfile.CurrentSid;
            SchedulerSnapshot snapshot = await Task.Run(function: () => { using WindowsScheduler scheduler = new(); return scheduler.Read(sid: sid); });
            if (_closing) return;
            _schedulerSnapshot = snapshot;
            _schedulerActive = snapshot.Enabled || snapshot.Running;
            _scheduledProfile = !_schedulerActive || snapshot.ProfilePath is null ? null : ScheduledReductionProfile.Read(path: snapshot.ProfilePath);
            _schedulerReadError = null;
            if (restoreSelection && _schedulerActive && _scheduledProfile is ScheduledReductionProfile profile)
            {
                _settings.ActiveProjectId = profile.ProjectId;
                _savedConnectionString = profile.ConnectionString();
                ConnectionStringInput.Text = _savedConnectionString;
                SchedulerClock.Time = ProjectSchedule.Time(profile: profile);
                _settings.LogFolder = profile.LogFolder; LogFolderInput.Text = profile.LogFolder;
            }
        }
        catch (Exception exception)
        {
            _schedulerReadError = "Scheduler status could not be verified: " + exception.Message;
            // Unknown scheduler state must never unlock conflicting database writes.
            _schedulerActive = true;
        }
        finally
        {
            _schedulerReading = false; _lastSchedulerRead = DateTime.UtcNow;
            if (!_closing) { UpdateSchedulerState(); ApplySchedulerLocks(); }
        }
    }
    private string SchedulerDescription()
    {
        if (_schedulerChanging) return "Updating Windows Task Scheduler...";
        if (_schedulerReadError is not null) return _schedulerReadError;
        if (_schedulerSnapshot?.Running == true)
            return "Daily reduction is running in the background. " + (_schedulerSnapshot.Enabled ? CountdownDescription() : "Future scheduled runs are stopped; the current run will finish.");
        if (_schedulerActive && _scheduledProfile is not null) return "Reduction software running: " + CountdownDescription();
        return "Reduction software stopped. Next scheduled reduction: not scheduled.";
    }
    private string CountdownDescription()
    {
        ScheduledReductionProfile profile = _scheduledProfile ?? throw new InvalidOperationException(message: "Missing scheduled project.");
        DateTime now = DateTime.UtcNow;
        DateTime next = ProjectSchedule.NextUtc(profile: profile, utcNow: now);
        TimeSpan remaining = next - now;
        DateTime local = TimeZoneInfo.ConvertTimeFromUtc(dateTime: next, destinationTimeZone: TimeZoneInfo.FindSystemTimeZoneById(id: profile.TimeZoneId));
        return $"{profile.ProjectName}; next scheduled reduction at {local:yyyyMMdd HH:mm} (project local). Time remaining: {(int)remaining.TotalHours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}.";
    }
    private void ApplySchedulerLocks()
    {
        bool locked = _schedulerActive || _schedulerChanging || _reducing || _deleting;
        SchedulerClock.IsEnabled = !locked; ScheduleTimeInput.IsEnabled = !locked;
        ScheduleAm.IsEnabled = !locked; SchedulePm.IsEnabled = !locked;
        LogFolderButton.IsEnabled = !locked;
        ConnectionStringInput.IsEnabled = !locked && !_testing;
        TestConnectionButton.IsEnabled = !locked && !_testing;
        ProjectSelector.IsEnabled = !locked && _validatedConnectionString is not null && ProjectSelector.HasItems;
        SelectProjectButton.IsEnabled = ProjectSelector.IsEnabled;
    }
    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_schedulerChanging || _schedulerActive || _reducing || _deleting || _testing || _updatingProjectDates) return;
        ApplyClockText();
        if (ActiveProject is not ProjectItem project || _projectTimeZone is null || _validatedConnectionString is not string connectionString)
        { SchedulerStatus.Text = SchedulePreferenceStatus.Text = "Connect to the database and select a project before starting the scheduler."; return; }
        if (string.IsNullOrWhiteSpace(value: _settings.LogFolder))
        { SchedulerStatus.Text = SchedulePreferenceStatus.Text = "Select the System Log File folder on the Database tab before starting."; return; }
        _schedulerReadError = null;
        _schedulerChanging = true; UpdateSchedulerState(); ApplySchedulerLocks();
        try
        {
            SystemActivityLog.EnsureFolder(folder: _settings.LogFolder);
            ScheduledReductionProfile profile = new()
            {
                OwnerSid = ScheduledReductionProfile.CurrentSid, OwnerName = WindowsIdentity.GetCurrent().Name,
                ProjectId = project.ProjectId, ProjectName = project.ProjectName, TimeZoneId = _projectTimeZone.Id,
                ScheduledTime = ScheduledLocalTime.ToString(format: "HH:mm", provider: CultureInfo.InvariantCulture),
                LogFolder = _settings.LogFolder, ProtectedConnectionString = ScheduledReductionProfile.Protect(connectionString: connectionString),
                Options = _configuration.Reduction, ExecutablePath = Path.ChangeExtension(path: typeof(App).Assembly.Location, extension: ".exe")
            };
            string path = profile.Save();
            int result = await RunSchedulerHelperAsync(mode: "--install-scheduler", argument: path);
            if (result != 0) throw new InvalidOperationException(message: result == 2 ? "Scheduler setup cancelled." : "Scheduler setup failed. See the setup error message.");
            SavePreferences();
        }
        catch (Exception exception) { _schedulerReadError = exception.Message; }
        finally
        {
            string? setupError = _schedulerReadError;
            _schedulerChanging = false;
            await RefreshSchedulerAsync(restoreSelection: false);
            if (setupError is not null) SchedulePreferenceStatus.Text = setupError;
            ApplySchedulerLocks();
        }
    }
    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_schedulerChanging || _reducing || _deleting) return;
        _schedulerReadError = null;
        _schedulerChanging = true; UpdateSchedulerState(); ApplySchedulerLocks();
        try
        {
            int result = await RunSchedulerHelperAsync(mode: "--stop-scheduler", argument: ScheduledReductionProfile.CurrentSid);
            if (result != 0) throw new InvalidOperationException(message: "Scheduler stop was not completed.");
        }
        catch (Exception exception) { _schedulerReadError = exception.Message; }
        finally
        {
            string? stopError = _schedulerReadError;
            _schedulerChanging = false;
            await RefreshSchedulerAsync(restoreSelection: false);
            if (stopError is not null) SchedulePreferenceStatus.Text = stopError;
            ApplySchedulerLocks();
        }
    }
    private static async Task<int> RunSchedulerHelperAsync(string mode, string argument)
    {
        ProcessStartInfo start = new()
        {
            FileName = Path.ChangeExtension(path: typeof(App).Assembly.Location, extension: ".exe"),
            UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory
        };
        start.ArgumentList.Add(item: mode); start.ArgumentList.Add(item: argument);
        using Process process = Process.Start(startInfo: start) ?? throw new InvalidOperationException(message: "Could not launch scheduler setup.");
        await process.WaitForExitAsync(); return process.ExitCode;
    }
}
#endregion


