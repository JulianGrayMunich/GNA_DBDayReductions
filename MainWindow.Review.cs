#region Imports
using System.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
#endregion
namespace GNA_DBDayReductions;
#region Reduction Review Controls
public partial class MainWindow
{
    private TaskCompletionSource<bool>? _reviewDecision;

    private async Task ReviewTableAsync(ReductionReview review, CancellationToken cancellationToken)
    {
        Task decision = await Dispatcher.InvokeAsync(callback: () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReviewTableName.Text = review.SourceTable + " → " + review.TargetTable;
            EpochReadingsGrid.ItemsSource = review.EpochReadings.DefaultView;
            ReductionPassesGrid.ItemsSource = review.Passes.DefaultView;
            DailyResultsGrid.ItemsSource = review.DailyResults.DefaultView;
            ReviewTabs.SelectedIndex = 0;
            _reviewDecision = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
            CommitNextTableButton.IsEnabled = true;
            AppendReductionStatus(message: $"Reviewing {review.SourceTable}: {review.EpochReadings.Rows.Count} readings, {review.DailyResults.Rows.Count} proposed daily rows. Select cells and copy to Excel with Ctrl+C. Commit and Next Table saves this table; Cancel stops without saving it.");
            return _reviewDecision.Task;
        });
        await decision.WaitAsync(cancellationToken: cancellationToken);
    }
    private void CommitNextTableButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_reducing || _reductionCancellation?.IsCancellationRequested != false || _reviewDecision is null) return;
        if (_reviewDecision.TrySetResult(result: true))
        {
            CommitNextTableButton.IsEnabled = false;
            AppendReductionStatus(message: "Commit requested. Rechecking inputs and saving this table...");
        }
    }
    private void CopyReviewSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        DataGrid grid = ReviewTabs.SelectedIndex switch { 1 => ReductionPassesGrid, 2 => DailyResultsGrid, _ => EpochReadingsGrid };
        try
        {
            if (ApplicationCommands.Copy.CanExecute(parameter: null, target: grid))
            {
                ApplicationCommands.Copy.Execute(parameter: null, target: grid);
                AppendReductionStatus(message: "Selected cells copied with column headings. Paste into Excel.");
            }
            else AppendReductionStatus(message: "Select grid cells first. Ctrl+A selects the entire grid.");
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            AppendReductionStatus(message: "The clipboard is busy. Try copying the selection again.");
        }
    }
    private void ReviewGrid_AutoGeneratingColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        if (e.Column is DataGridTextColumn column && column.Binding is Binding binding)
        {
            Type type = Nullable.GetUnderlyingType(nullableType: e.PropertyType) ?? e.PropertyType;
            if (type == typeof(DateTime)) binding.StringFormat = e.PropertyName == "LocalDate" ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm:ss";
            else if (type == typeof(decimal) || type == typeof(double) || type == typeof(float)
                || type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte)) binding.StringFormat = "F4";
            bool integerDisplay = sender == EpochReadingsGrid && e.PropertyName == "PointName_ID"
                || sender == ReductionPassesGrid && e.PropertyName is "PointName_ID" or "Pass" or "Count" or "CandidateCount"
                || sender == DailyResultsGrid && e.PropertyName is "PointName_ID" or "NoOfObservations" or "RetainedCount" or "RejectedCount" or "AcceptedPasses";
            if (integerDisplay) binding.StringFormat = "F0";
            if (e.PropertyName == "RetainedValues") binding.Converter = new RetainedValuesDisplayConverter();
            TextAlignment? alignment = e.PropertyName is "PointName_ID" or "ReadingCount" ? TextAlignment.Center : null;
            if (sender == EpochReadingsGrid && e.PropertyName == "dH") alignment = TextAlignment.Right;
            if (sender == ReductionPassesGrid)
            {
                if (e.PropertyName is "Measurement" or "Pass") alignment = TextAlignment.Center;
                if (e.PropertyName is "Mean" or "StandardDeviation" or "StandardError" or "CandidateCount"
                    or "CandidateMean" or "CandidateStandardDeviation" or "CandidateStandardError" or "TwiceCandidateSE"
                    or "MeanDifference" or "Minimum" or "Maximum") alignment = TextAlignment.Right;
            }
            if (sender == DailyResultsGrid)
            {
                if (e.PropertyName is "NoOfObservations" or "RetainedCount" or "RejectedCount" or "AcceptedPasses" or "Performance")
                    alignment = TextAlignment.Center;
                if (e.PropertyName is "dH" or "OriginalMean" or "MeanDifference") alignment = TextAlignment.Right;
            }
            if (alignment.HasValue)
            {
                Style cellText = new(targetType: typeof(TextBlock));
                cellText.Setters.Add(item: new Setter(property: TextBlock.TextAlignmentProperty, value: alignment.Value));
                cellText.Setters.Add(item: new Setter(property: FrameworkElement.VerticalAlignmentProperty, value: VerticalAlignment.Center));
                column.ElementStyle = cellText;
            }
            if (sender == DailyResultsGrid && e.PropertyName == "MeanDifference") column.Header = "Original Mean - Final Mean";
            binding.TargetNullValue = "NULL";
            column.ClipboardContentBinding = binding;
        }
    }
    private void ClearReview()
    {
        _reviewDecision = null;
        CommitNextTableButton.IsEnabled = false;
        EpochReadingsGrid.ItemsSource = null; ReductionPassesGrid.ItemsSource = null; DailyResultsGrid.ItemsSource = null;
        ReviewTableName.Text = string.Empty;
    }
}
#endregion



