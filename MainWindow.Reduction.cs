#region Imports
using System.Windows;
#endregion
namespace GNA_DBDayReductions;
#region Test Reduction Interface
public partial class MainWindow
{
    private bool _reducing;
    private CancellationTokenSource? _reductionCancellation;

    private async void TestReductionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_deleting || _reducing || _testing || _updatingProjectDates || _closing) return;
        if (_schedulerActive)
        {
            TestStatus.Text = "Click Stop on Scheduler before testing a reduction.";
            return;
        }
        if (ActiveProject is not ProjectItem project || _projectTimeZone is null || _validatedConnectionString is not string connectionString)
        {
            TestStatus.Text = "Connect to the database and select a project before testing.";
            return;
        }
        RefreshHistoricDates(utcNow: DateTime.UtcNow, resetSelection: false);
        if (ManualStartDate.SelectedDate is not DateTime start || ManualEndDate.SelectedDate is not DateTime end || start > end || end > _latestCompleteDay)
        {
            TestStatus.Text = "Select a valid range of completed days in Historic Data / Manual Data Reductions.";
            return;
        }
        ReductionPeriod.Text = $"Reduction period (project local): {start:yyyy-MM-dd} to {end:yyyy-MM-dd}, inclusive";
        string warning = $"Project: {project.ProjectName}\nProject-local dates: {start:yyyy-MM-dd} to {end:yyyy-MM-dd}, inclusive.\n\nThis test writes to the connected database. It will prepare the Daily/statistics columns, then review each day within each table. Next Day advances without writing. Daily means and statistics are written only when you click Commit and Next Table. Cancel stops and retains completed commits.\n\nContinue?";
        if (MessageBox.Show(owner: this, messageBoxText: warning, caption: "Confirm reduction test",
            button: MessageBoxButton.YesNo, icon: MessageBoxImage.Warning, defaultResult: MessageBoxResult.No) != MessageBoxResult.Yes) return;
        ClearReview();
        InvalidatePerformance();
        _reducing = true;
        _reductionCancellation = CancellationTokenSource.CreateLinkedTokenSource(token: _lifetime.Token);
        _calendarTimer.Stop();
        SetReductionControls();
        TestStatus.Text = $"Starting {project.ProjectName}: {start:yyyy-MM-dd} to {end:yyyy-MM-dd}.\n";
        Progress<string> progress = new(handler: message => AppendReductionStatus(message: message));
        try
        {
            DailyReductionService service = new(options: _configuration.Reduction);
            ReductionRequest request = new(ProjectId: project.ProjectId, ProjectName: project.ProjectName,
                TimeZoneId: _projectTimeZone.Id, StartDate: start, EndDate: end);
            _performanceRunRequest = request;
            CancellationToken token = _reductionCancellation.Token;
            ReductionSummary result = await Task.Run(function: () => service.RunAsync(connectionString: connectionString,
                request: request, progress: progress, cancellationToken: token, review: ReviewTableAsync));
            AppendReductionStatus(message: $"Completed: {result.Tables} tables; {result.Rows} daily/statistics rows; Pass {result.Pass}, Fail {result.Fail}.");
        }
        catch (OperationCanceledException)
        {
            AppendReductionStatus(message: "Cancelled. The current uncommitted table was rolled back; completed tables remain. The same range can be rerun.");
        }
        catch (Exception exception)
        {
            AppendReductionStatus(message: "Reduction stopped: " + exception.Message);
        }
        finally
        {
            _reviewDecision = null;
            CommitNextTableButton.IsEnabled = false;
            _reducing = false;
            _reductionCancellation.Dispose();
            _reductionCancellation = null;
            SetReductionControls();
            _calendarTimer.Start();
            await RefreshPerformanceAsync();
        }
    }
    private void CancelReductionButton_Click(object sender, RoutedEventArgs e)
    {
        CommitNextTableButton.IsEnabled = false;
        _reductionCancellation?.Cancel();
        CancelReductionButton.IsEnabled = false;
        if (_manualReducing) ManualReductionStatus.Text = "Cancellation requested; waiting for the current operation to roll back.";
        else AppendReductionStatus(message: "Cancellation requested; waiting for the current operation to roll back.");
    }
    private void AppendReductionStatus(string message)
    {
        if (message.StartsWith(value: "Committed ", comparisonType: StringComparison.Ordinal)) InvalidatePerformance();
        TestStatus.AppendText(textData: message + Environment.NewLine);
        TestStatus.ScrollToEnd();
    }
    private void SetReductionControls()
    {
        ConnectionStringInput.IsEnabled = !_reducing && !_deleting;
        TestConnectionButton.IsEnabled = !_reducing && !_deleting;
        ProjectSelector.IsEnabled = !_reducing && !_deleting && _validatedConnectionString is not null && ProjectSelector.HasItems;
        SelectProjectButton.IsEnabled = ProjectSelector.IsEnabled;
        foreach (System.Windows.Controls.DatePicker picker in HistoricPickers()) picker.IsEnabled = !_reducing && !_deleting && _projectTimeZone is not null;
        TestReductionButton.IsEnabled = !_reducing && !_deleting && !_schedulerActive;
        CancelReductionButton.IsEnabled = _reducing;
        UpdateSchedulerState();
        UpdateProjectDatesAvailability();
    }
}
#endregion





