#region Imports
using System.Windows;
#endregion
namespace GNA_DBDayReductions;
#region Historic Deletion Interface
public partial class MainWindow
{
    private bool _deleting;
    private CancellationTokenSource? _deletionCancellation;

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_deleting || _reducing || _testing || _updatingProjectDates || _closing) return;
        if (_schedulerActive) { DeletionStatus.Text = "Click Stop on Scheduler before deleting daily data."; return; }
        if (ActiveProject is not ProjectItem project || _projectTimeZone is null || _validatedConnectionString is not string connectionString)
        { DeletionStatus.Text = "Connect to the database and select a project before deleting daily data."; return; }
        RefreshHistoricDates(utcNow: DateTime.UtcNow, resetSelection: false);
        if (!ValidateHistoricRange(startPicker: StartDate, endPicker: EndDate, status: DateRangeStatus, label: "Delete Data")) return;
        DateTime start = StartDate.SelectedDate!.Value.Date, end = EndDate.SelectedDate!.Value.Date;
        ReductionRequest request = new(ProjectId: project.ProjectId, ProjectName: project.ProjectName,
            TimeZoneId: _projectTimeZone.Id, StartDate: start, EndDate: end);
        string warning = $"Project: {project.ProjectName} (ID {project.ProjectId})\nProject-local dates: {start:yyyy-MM-dd} to {end:yyyy-MM-dd}, inclusive.\n\nPermanently delete all Daily-table data and DailyReductionStatistics records for this project and date range?\n\nThis cannot be undone. Epoch readings will be retained.";
        if (MessageBox.Show(owner: this, messageBoxText: warning, caption: "Confirm permanent deletion",
            button: MessageBoxButton.YesNo, icon: MessageBoxImage.Warning, defaultResult: MessageBoxResult.No) != MessageBoxResult.Yes) return;
        _deleting = true;
        _deletionCancellation = CancellationTokenSource.CreateLinkedTokenSource(token: _lifetime.Token);
        _calendarTimer.Stop(); SetReductionControls(); InvalidatePerformance();
        DeletionStatus.Text = "Deleting daily results and statistics...";
        try
        {
            DailyReductionService service = new(options: _configuration.Reduction);
            CancellationToken token = _deletionCancellation.Token;
            DeletionSummary result = await Task.Run(function: () => service.DeleteAsync(connectionString: connectionString, request: request, cancellationToken: token));
            ClearReview();
            DeletionStatus.Text = $"Delete task successfully completed. Deleted {result.DailyRows} Daily rows across {result.Tables} tables and {result.StatisticsRows} statistics rows for {project.ProjectName}, {start:yyyy-MM-dd} to {end:yyyy-MM-dd}, inclusive.";
        }
        catch (OperationCanceledException) { DeletionStatus.Text = "Deletion cancelled. No deletions were committed."; }
        catch (Exception exception) { DeletionStatus.Text = "Deletion failed: " + exception.Message; }
        finally
        {
            _deleting = false; _deletionCancellation.Dispose(); _deletionCancellation = null;
            InvalidatePerformance(); SetReductionControls(); _calendarTimer.Start();
        }
    }
}
#endregion

