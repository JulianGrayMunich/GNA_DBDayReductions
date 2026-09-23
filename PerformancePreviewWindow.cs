#region Imports
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
#endregion
namespace GNA_DBDayReductions;
#region Performance Image Preview
public sealed class PerformancePreviewWindow : Window
{
    private readonly PerformanceReport _report;
    private readonly IReadOnlyList<PerformanceImagePage> _pages;
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _pageLabel = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(uniformLength: 12) };
    private readonly Button _previous = new() { Content = "Previous", Width = 85 };
    private readonly Button _next = new() { Content = "Next", Width = 85 };
    private int _page;

    public PerformancePreviewWindow(PerformanceReport report, Action<Window, PerformanceReport> save)
    {
        _report = report ?? throw new ArgumentNullException(paramName: nameof(report));
        ArgumentNullException.ThrowIfNull(argument: save);
        _pages = PerformanceImage.Pages(report: report);
        Title = "Performance images — " + report.Request.ProjectName;
        Width = 1000; Height = 850; MinWidth = 550; MinHeight = 450;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        DockPanel panel = new() { Margin = new Thickness(uniformLength: 12) };
        StackPanel actions = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(left: 0, top: 12, right: 0, bottom: 0) };
        Button saveButton = new() { Content = "Save", Width = 85, Margin = new Thickness(left: 24, top: 0, right: 8, bottom: 0) };
        Button close = new() { Content = "Close", Width = 85, IsCancel = true };
        _previous.Click += (_, _) => ShowPage(index: _page - 1);
        _next.Click += (_, _) => ShowPage(index: _page + 1);
        saveButton.Click += (_, _) => save(arg1: this, arg2: _report);
        close.Click += (_, _) => Close();
        actions.Children.Add(element: _previous); actions.Children.Add(element: _pageLabel); actions.Children.Add(element: _next);
        actions.Children.Add(element: saveButton); actions.Children.Add(element: close);
        DockPanel.SetDock(element: actions, dock: Dock.Bottom); panel.Children.Add(element: actions);
        panel.Children.Add(element: _image); Content = panel;
        ShowPage(index: 0);
    }
    private void ShowPage(int index)
    {
        if (index < 0 || index >= _pages.Count) return;
        _image.Source = PerformanceImage.Render(report: _report, page: _pages[index], pageNumber: index + 1, pageCount: _pages.Count);
        _page = index; _pageLabel.Text = $"Page {index + 1} of {_pages.Count} — 250 × 200 mm";
        _previous.IsEnabled = index > 0; _next.IsEnabled = index + 1 < _pages.Count;
    }
}
#endregion
