#region Imports
using System.ComponentModel;
using System.Data;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
#endregion
namespace GNA_DBDayReductions;
#region Performance Report Interface
public partial class MainWindow
{
    private PerformanceReport? _performanceReport;
    private CancellationTokenSource? _performanceCancellation;
    private ReductionRequest? _performanceRunRequest;
    private long _performanceGeneration;
    private bool _performanceLoading;

    private ReductionRequest? SelectedPerformanceRequest()
    {
        if (_reducing && _performanceRunRequest is not null) return _performanceRunRequest;
        if (ActiveProject is not ProjectItem project || _projectTimeZone is null
            || ManualStartDate.SelectedDate is not DateTime start || ManualEndDate.SelectedDate is not DateTime end
            || start > end || _latestCompleteDay is null || end > _latestCompleteDay) return null;
        return new(ProjectId: project.ProjectId, ProjectName: project.ProjectName, TimeZoneId: _projectTimeZone.Id, StartDate: start, EndDate: end);
    }
    private void InvalidatePerformance()
    {
        _performanceGeneration++;
        _performanceCancellation?.Cancel();
        _performanceReport = null;
        if (PerformanceFullGrid is null) return;
        PerformanceFullGrid.ItemsSource = null; PerformanceSummaryGrid.ItemsSource = null;
        PerformanceFullHeading.Text = string.Empty; PerformanceSummaryHeading.Text = string.Empty;
        PerformanceFullEmpty.Text = string.Empty; PerformanceSummaryEmpty.Text = string.Empty;
        ViewPerformanceImageButton.IsEnabled = false;
    }
    private async void ReviewTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != ReviewTabs || _initializing || _closing) return;
        ReviewTableName.Visibility = ReviewTabs.SelectedIndex >= 3 ? Visibility.Collapsed : Visibility.Visible;
        if (_performanceLoading) return;
        if (ReviewTabs.SelectedIndex >= 3 && _performanceReport is null) await RefreshPerformanceAsync();
    }
    private async void RefreshPerformanceButton_Click(object sender, RoutedEventArgs e) => await RefreshPerformanceAsync();

    private async Task RefreshPerformanceAsync()
    {
        if (_closing || _deleting) return;
        ReductionRequest? request = SelectedPerformanceRequest();
        string? connectionString = _validatedConnectionString;
        InvalidatePerformance();
        if (request is null || connectionString is null)
        {
            AppendReductionStatus(message: "Select a connected project and a valid Manual Data Reductions date range to view performance.");
            return;
        }
        ReductionPeriod.Text = $"Reduction period (project local): {request.StartDate:yyyy-MM-dd} to {request.EndDate:yyyy-MM-dd}, inclusive";
        long generation = _performanceGeneration;
        CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(token: _lifetime.Token);
        _performanceCancellation = cancellation; _performanceLoading = true;
        RefreshPerformanceFullButton.IsEnabled = false; RefreshPerformanceSummaryButton.IsEnabled = false;
        try
        {
            DailyReductionService service = new(options: _configuration.Reduction);
            PerformanceReport report = await Task.Run(function: () => service.ReadPerformanceAsync(connectionString: connectionString,
                request: request, cancellationToken: cancellation.Token));
            if (_closing || generation != _performanceGeneration || SelectedPerformanceRequest() != request || _validatedConnectionString != connectionString) return;
            DisplayPerformance(report: report);
            AppendReductionStatus(message: $"Performance loaded for {request.ProjectName}, {request.StartDate:yyyy-MM-dd} to {request.EndDate:yyyy-MM-dd}. Committed Pass/Fail days only; unprocessed days are blank.");
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (!_closing && generation == _performanceGeneration) AppendReductionStatus(message: "Performance report failed: " + exception.Message);
        }
        finally
        {
            if (ReferenceEquals(objA: _performanceCancellation, objB: cancellation))
            {
                _performanceCancellation = null; _performanceLoading = false;
                if (!_closing) { RefreshPerformanceFullButton.IsEnabled = true; RefreshPerformanceSummaryButton.IsEnabled = true; }
            }
            cancellation.Dispose();
        }
    }
    private void DisplayPerformance(PerformanceReport report)
    {
        _performanceReport = report;
        PerformanceFullHeading.Text = report.Heading; PerformanceSummaryHeading.Text = report.Heading;
        PerformanceFullEmpty.Text = report.FailingSensors.Count == 0 ? "No fails" : string.Empty;
        PerformanceSummaryEmpty.Text = report.SummaryMessage;
        PerformanceFullGrid.Visibility = report.FailingSensors.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        PerformanceSummaryGrid.ItemsSource = report.Summary;
        DataTable table = new(tableName: "Performance");
        table.Columns.Add(columnName: "SensorType", type: typeof(string)); table.Columns.Add(columnName: "SensorName", type: typeof(string));
        PerformanceFullGrid.Columns.Clear();
        PerformanceFullGrid.Columns.Add(item: new DataGridTextColumn { Header = "Sensor name", Binding = new Binding(path: "SensorName"), Width = 240 });
        for (DateTime day = report.Request.StartDate.Date; day <= report.Request.EndDate.Date; day = day.AddDays(value: 1))
        {
            string column = "D" + day.ToString(format: "yyyyMMdd", provider: CultureInfo.InvariantCulture);
            table.Columns.Add(columnName: column, type: typeof(string));
            Style cell = new(targetType: typeof(DataGridCell));
            cell.Setters.Add(item: new Setter(property: Control.BackgroundProperty, value: Brushes.White));
            cell.Setters.Add(item: new Setter(property: FrameworkElement.ToolTipProperty,
                value: new Binding(path: column) { StringFormat = day.ToString(format: "yyyy-MM-dd", provider: CultureInfo.InvariantCulture) + ": {0}", TargetNullValue = "Unprocessed" }));
            foreach ((string state, Brush colour) in new[] { ("Pass", (Brush)Brushes.Green), ("Fail", (Brush)Brushes.Red) })
            {
                DataTrigger trigger = new() { Binding = new Binding(path: column), Value = state };
                trigger.Setters.Add(item: new Setter(property: Control.BackgroundProperty, value: colour)); cell.Triggers.Add(item: trigger);
            }
            DataTemplate blank = new();
            PerformanceFullGrid.Columns.Add(item: new DataGridTemplateColumn
            {
                Header = day.ToString(format: "dd", provider: CultureInfo.InvariantCulture), Width = 20, MinWidth = 20,
                CellStyle = cell, CellTemplate = blank, CanUserSort = false, ClipboardContentBinding = new Binding(path: column)
            });
        }
        foreach (PerformanceSensor sensor in report.FailingSensors)
        {
            DataRow row = table.NewRow(); row["SensorType"] = sensor.SensorType; row["SensorName"] = sensor.SensorName;
            foreach (KeyValuePair<DateTime, bool> status in sensor.Days)
            {
                string column = "D" + status.Key.ToString(format: "yyyyMMdd", provider: CultureInfo.InvariantCulture);
                if (table.Columns.Contains(name: column)) row[column] = status.Value ? "Pass" : "Fail";
            }
            table.Rows.Add(row: row);
        }
        ICollectionView view = CollectionViewSource.GetDefaultView(source: table.DefaultView);
        view.GroupDescriptions.Add(item: new PropertyGroupDescription(propertyName: "SensorType"));
        PerformanceFullGrid.ItemsSource = view;
        ViewPerformanceImageButton.IsEnabled = true;
    }
    private void ViewPerformanceImageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_performanceReport is not PerformanceReport report) return;
        try
        {
            PerformancePreviewWindow preview = new(report: report, save: SavePerformanceImages) { Owner = this };
            preview.ShowDialog();
        }
        catch (Exception exception) { AppendReductionStatus(message: "Image preview failed: " + exception.Message); }
    }
    private void SavePerformanceImages(Window owner, PerformanceReport report)
    {
        SaveFileDialog dialog = new()
        {
            Title = "Save performance image", Filter = "PNG image (*.png)|*.png", DefaultExt = ".png", AddExtension = true,
            FileName = $"Performance_{report.Request.StartDate:yyyyMMdd}_{report.Request.EndDate:yyyyMMdd}.png"
        };
        if (dialog.ShowDialog(owner: owner) != true) return;
        try
        {
            IReadOnlyList<PerformanceImagePage> pages = PerformanceImage.Pages(report: report);
            string directory = Path.GetDirectoryName(path: dialog.FileName) ?? throw new InvalidOperationException(message: "Select an image folder.");
            string stem = Path.GetFileNameWithoutExtension(path: dialog.FileName);
            List<string> paths = new() { dialog.FileName };
            for (int page = 1; page < pages.Count; page++) paths.Add(item: Path.Combine(path1: directory, path2: $"{stem}_{page + 1:000}.png"));
            if (paths.Skip(count: 1).Any(predicate: File.Exists)
                && MessageBox.Show(owner: owner, messageBoxText: "Additional numbered image files already exist. Replace those files?", caption: "Replace performance images",
                    button: MessageBoxButton.YesNo, icon: MessageBoxImage.Warning, defaultResult: MessageBoxResult.No) != MessageBoxResult.Yes) return;
            for (int page = 0; page < pages.Count; page++)
            {
                BitmapSource bitmap = PerformanceImage.Render(report: report, page: pages[page], pageNumber: page + 1, pageCount: pages.Count);
                PngBitmapEncoder encoder = new(); encoder.Frames.Add(item: BitmapFrame.Create(source: bitmap));
                using FileStream stream = new(path: paths[page], mode: FileMode.Create, access: FileAccess.Write, share: FileShare.None);
                encoder.Save(stream: stream);
            }
            AppendReductionStatus(message: $"Saved {pages.Count} PNG image(s), 250 × 200 mm at 300 DPI: {dialog.FileName}");
        }
        catch (Exception exception) { AppendReductionStatus(message: "Image export failed: " + exception.Message); }
    }
}
#endregion



