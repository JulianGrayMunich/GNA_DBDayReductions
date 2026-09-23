#region Imports
using System.Windows;
#endregion
namespace GNA_DBDayReductions;
#region Continuous Manual Reduction
public partial class MainWindow
{
    private bool _manualReducing;
    private long _manualRunNumber;

    private async void ManualComputeButton_Click(object sender, RoutedEventArgs e) => await RunManualReductionAsync();

    private async Task RunManualReductionAsync()
    {
        if (_deleting || _reducing || _testing || _updatingProjectDates || _closing) return;
        if (_schedulerActive)
        { ManualReductionStatus.Text = "Click Stop on Scheduler before computing historic data."; return; }
        if (ActiveProject is not ProjectItem project || _projectTimeZone is null || _validatedConnectionString is not string connectionString)
        { ManualReductionStatus.Text = "Connect to the database and select a project before computing historic data."; return; }
        RefreshHistoricDates(utcNow: DateTime.UtcNow, resetSelection: false);
        if (!ValidateHistoricRange(startPicker: ManualStartDate, endPicker: ManualEndDate, status: ManualDateRangeStatus, label: "Manual Data Reductions")) return;
        ReductionRequest request = new(ProjectId: project.ProjectId, ProjectName: project.ProjectName,
            TimeZoneId: _projectTimeZone.Id, StartDate: ManualStartDate.SelectedDate!.Value.Date, EndDate: ManualEndDate.SelectedDate!.Value.Date);
        long run = ++_manualRunNumber;
        _reducing = true; _manualReducing = true;
        _performanceRunRequest = request;
        _reductionCancellation = CancellationTokenSource.CreateLinkedTokenSource(token: _lifetime.Token);
        _calendarTimer.Stop(); ClearReview(); InvalidatePerformance(); SetReductionControls();
        ManualReductionProgress.Value = 0; ManualReductionProgress.Maximum = 1; ManualReductionProgress.IsIndeterminate = true;
        ManualReductionStatus.Text = $"Preparing {project.ProjectName}: {request.StartDate:yyyy-MM-dd} to {request.EndDate:yyyy-MM-dd}, inclusive.";
        Progress<string> messages = new(handler: message =>
        {
            if (!_manualReducing || run != _manualRunNumber) return;
            if (message.StartsWith(value: "Committed ", comparisonType: StringComparison.Ordinal)) InvalidatePerformance();
            ManualReductionStatus.Text = $"{project.ProjectName}, {request.StartDate:yyyy-MM-dd} to {request.EndDate:yyyy-MM-dd}: {message}";
        });
        Progress<ReductionProgress> progress = new(handler: value =>
        {
            if (_manualReducing && run == _manualRunNumber) DisplayManualProgress(progress: value);
        });
        try
        {
            DailyReductionService service = new(options: _configuration.Reduction);
            CancellationToken token = _reductionCancellation.Token;
            ReductionSummary result = await Task.Run(function: () => service.RunAsync(connectionString: connectionString,
                request: request, progress: messages, cancellationToken: token, review: null, tableProgress: progress));
            ManualReductionProgress.Value = 0;
            ManualReductionProgress.ToolTip = "Computation successfully completed";
            ManualReductionStatus.Text = $"Manual data reduction successfully completed for {project.ProjectName}: {request.StartDate:yyyy-MM-dd} to {request.EndDate:yyyy-MM-dd}, inclusive. {result.Tables} tables updated; {result.Rows} Daily results and corresponding statistics; Pass {result.Pass}, Fail {result.Fail}.";
        }
        catch (OperationCanceledException)
        {
            ManualReductionStatus.Text = "Computation cancelled. The current uncommitted table was rolled back; completed tables remain. The selected period can be rerun.";
        }
        catch (Exception exception)
        {
            ManualReductionStatus.Text = "Computation stopped: " + exception.Message + " Completed tables remain committed; the incomplete table was rolled back. The selected period can be rerun.";
        }
        finally
        {
            _manualReducing = false; _reducing = false; ManualReductionProgress.IsIndeterminate = false;
            _reductionCancellation.Dispose(); _reductionCancellation = null;
            InvalidatePerformance(); SetReductionControls(); _calendarTimer.Start();
        }
    }

    private void DisplayManualProgress(ReductionProgress progress)
    {
        ManualReductionProgress.IsIndeterminate = false;
        ManualReductionProgress.Maximum = Math.Max(val1: 1, val2: progress.TotalTables);
        ManualReductionProgress.Value = progress.CompletedTables;
        ManualReductionProgress.ToolTip = $"{progress.CompletedTables} of {progress.TotalTables} tables completed";
    }
}
#endregion

