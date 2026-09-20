#region Imports
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Data.SqlClient;
#endregion

namespace GNA_DBDayReductions;

#region Main Window
public partial class MainWindow : Window
{
    #region Configuration and State
    private const int FirstSelectableMinute = 1;
    private const string SettingsFileName = "user-settings.json";
    private const string ProjectQuery = """
        SELECT [Project_ID], [ProjectName], [TimeZoneId]
        FROM [dbo].[Project]
        WHERE [IsDeleted] = 0
        ORDER BY [ProjectName], [Project_ID];
        """;
    private readonly string _settingsPath;
    private readonly CancellationTokenSource _lifetime = new();
    private ApplicationConfiguration _configuration = new();
    private UserSettings _settings = new();
    private string? _savedConnectionString;
    private string? _validatedConnectionString;
    private bool _initializing = true;
    private bool _testing;
    private bool _closing;
    private bool _loadingProjects;
    private bool _startupConnectionAttempted;
    private TimeZoneInfo? _projectTimeZone;
    private DateTime? _latestCompleteDay;
    private readonly DispatcherTimer _calendarTimer = new() { Interval = TimeSpan.FromMinutes(value: 1) };

    public ProjectItem? ActiveProject { get; private set; }
    public TimeOnly ScheduledLocalTime => new(hour: ScheduledHour.SelectedIndex, minute: ScheduledMinute.SelectedIndex + FirstSelectableMinute);
    #endregion

    #region Startup and Shutdown
    public MainWindow() : this(settingsPath: Path.Combine(
        path1: Environment.GetFolderPath(folder: Environment.SpecialFolder.LocalApplicationData),
        path2: "GNA", path3: "GNA_DBDayReductions", path4: SettingsFileName))
    {
    }

    // An isolated path permits offline verification without changing the operator's preferences.
    internal MainWindow(string settingsPath)
    {
        _settingsPath = settingsPath ?? throw new ArgumentNullException(paramName: nameof(settingsPath));
        InitializeComponent();
        string? startupWarning = LoadConfigurationAndSettings();
        for (int hour = 0; hour < 24; hour++)
        {
            ScheduledHour.Items.Add(newItem: hour.ToString(format: "00", provider: CultureInfo.InvariantCulture));
        }
        for (int minute = FirstSelectableMinute; minute < 60; minute++)
        {
            ScheduledMinute.Items.Add(newItem: minute.ToString(format: "00", provider: CultureInfo.InvariantCulture));
        }
        string timeText = _settings.ScheduledTime ?? _configuration.DefaultScheduledTime;
        if (!TimeOnly.TryParseExact(s: timeText, format: "HH:mm", provider: CultureInfo.InvariantCulture,
            style: DateTimeStyles.None, result: out TimeOnly time))
        {
            time = TimeOnly.ParseExact(s: _configuration.DefaultScheduledTime, format: "HH:mm", provider: CultureInfo.InvariantCulture);
        }
        ScheduledHour.SelectedIndex = time.Hour;
        // Earlier revisions allowed minute 00. Restore that value as the first permitted minute.
        ScheduledMinute.SelectedIndex = Math.Max(val1: time.Minute, val2: FirstSelectableMinute) - FirstSelectableMinute;
        SetHistoricDatesUnavailable();
        _calendarTimer.Tick += CalendarTimer_Tick;
        _calendarTimer.Start();
        ConnectionStringInput.Text = _savedConnectionString ?? string.Empty;
        _initializing = false;
        UpdateScheduleDescription();
        ValidateDateRange();
        SettingsStatus.Text = startupWarning ?? "Ready.";
    }

    private async void Window_ContentRendered(object? sender, EventArgs e)
    {
        if (_startupConnectionAttempted || _closing) return;
        _startupConnectionAttempted = true;
        if (string.IsNullOrWhiteSpace(value: ConnectionStringInput.Text))
        {
            ConnectionStatus.Text = "Enter a connection string on the Database tab, then click Test Connection.";
            return;
        }
        await TestConnectionAsync();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _closing = true;
        _calendarTimer.Stop();
        _calendarTimer.Tick -= CalendarTimer_Tick;
        _lifetime.Cancel();
        if (!_testing)
        {
            _lifetime.Dispose();
        }
    }
    #endregion

    #region Database Connection and Project Loading
    private string BuildConnectionString(string input)
    {
        if (string.IsNullOrWhiteSpace(value: input))
        {
            throw new ArgumentException(message: "Enter a SQL Server connection string.", paramName: nameof(input));
        }
        SqlConnectionStringBuilder builder = new(connectionString: input.Trim())
        {
            InitialCatalog = _configuration.DatabaseName,
            ConnectTimeout = _configuration.ConnectionTimeoutSeconds,
            PersistSecurityInfo = false
        };
        if (string.IsNullOrWhiteSpace(value: builder.DataSource))
        {
            throw new ArgumentException(message: "The connection string must specify a server.", paramName: nameof(input));
        }
        return builder.ConnectionString;
    }

    private void ConnectionStringInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_initializing) return;
        InvalidateConnection();
        ConnectionStatus.Text = "Connection changed. Test it to load projects.";
    }

    private void InvalidateConnection()
    {
        _validatedConnectionString = null;
        ActiveProject = null;
        SetHistoricDatesUnavailable();
        _loadingProjects = true;
        ProjectSelector.ItemsSource = null;
        _loadingProjects = false;
        ProjectSelector.IsEnabled = false;
        SelectProjectButton.IsEnabled = false;
        ProjectStatus.Text = "Test the database connection on the Database tab to load projects.";
    }

    private async void TestConnectionButton_Click(object sender, RoutedEventArgs e)
    {
        await TestConnectionAsync();
    }

    private async Task TestConnectionAsync()
    {
        if (_testing || _closing) return;
        _testing = true;
        InvalidateConnection();
        ConnectionStringInput.IsEnabled = false;
        TestConnectionButton.IsEnabled = false;
        ConnectionStatus.Text = "Testing connection...";
        bool opened = false;
        try
        {
            string connectionString = BuildConnectionString(input: ConnectionStringInput.Text);
            using SqlConnection connection = new(connectionString: connectionString);
            await connection.OpenAsync(cancellationToken: _lifetime.Token);
            opened = true;
            ConnectionStatus.Text = "Connection succeeded. Reading projects...";
            using SqlCommand command = new(cmdText: ProjectQuery, connection: connection)
            {
                CommandTimeout = _configuration.CommandTimeoutSeconds
            };
            using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken: _lifetime.Token);
            List<ProjectItem> projects = new();
            while (await reader.ReadAsync(cancellationToken: _lifetime.Token))
            {
                projects.Add(item: new ProjectItem(
                    ProjectId: reader.GetInt32(i: 0),
                    ProjectName: reader.GetString(i: 1),
                    TimeZoneId: reader.IsDBNull(i: 2) ? null : reader.GetString(i: 2)));
            }
            if (_closing) return;
            ApplyProjectList(connectionString: connectionString, projects: projects);
            ConnectionStatus.Text = $"Connected to {_configuration.DatabaseName}. Active projects available: {projects.Count}.";
        }
        catch (OperationCanceledException)
        {
            if (!_closing) ConnectionStatus.Text = "Connection test cancelled.";
        }
        catch (SqlException exception)
        {
            // SQL messages can contain connection details. Display actionable codes without credentials.
            ConnectionStatus.Text = opened
                ? $"Connection succeeded, but projects could not be read (SQL error {exception.Number}). Check dbo.Project and SELECT permission."
                : $"Connection failed (SQL error {exception.Number}). Check server, authentication, network and certificate settings.";
        }
        catch (ArgumentException)
        {
            ConnectionStatus.Text = "Invalid connection string. Specify a SQL Server and valid connection options.";
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidCastException or IndexOutOfRangeException)
        {
            ConnectionStatus.Text = opened
                ? "Connection succeeded, but the Project table does not match the required schema."
                : "Connection could not be opened. Check the connection options and authentication provider.";
        }
        catch (Exception)
        {
            // Keep provider-specific failures inside the asynchronous UI boundary.
            ConnectionStatus.Text = "The connection test could not complete. Check the SQL client and authentication configuration.";
        }
        finally
        {
            _testing = false;
            if (_closing)
            {
                _lifetime.Dispose();
            }
            else
            {
                ConnectionStringInput.IsEnabled = true;
                TestConnectionButton.IsEnabled = true;
            }
        }
    }

    private void ApplyProjectList(string connectionString, List<ProjectItem> projects)
    {
        int? restoreId = string.Equals(a: connectionString, b: _savedConnectionString, comparisonType: StringComparison.Ordinal)
            ? _settings.ActiveProjectId : null;
        _validatedConnectionString = connectionString;
        ActiveProject = null;
        SetHistoricDatesUnavailable();
        _loadingProjects = true;
        ProjectSelector.ItemsSource = projects;
        ProjectSelector.SelectedIndex = -1;
        _loadingProjects = false;
        ProjectSelector.IsEnabled = projects.Count > 0;
        SelectProjectButton.IsEnabled = projects.Count > 0;
        ProjectStatus.Text = projects.Count > 0
            ? "Select the project that applies to this software."
            : "No active projects were found in the connected database.";
        foreach (ProjectItem project in projects)
        {
            if (project.ProjectId == restoreId)
            {
                ProjectSelector.SelectedItem = project;
                break;
            }
        }
        SavePreferences();
    }

    private void SelectProjectButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectSelector.IsEnabled)
        {
            ProjectSelector.Focus();
            ProjectSelector.IsDropDownOpen = true;
        }
    }

    private void ProjectSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || _loadingProjects || _validatedConnectionString is null) return;
        ActiveProject = ProjectSelector.SelectedItem as ProjectItem;
        if (ActiveProject is null) return;
        if (TryGetProjectTimeZone(timeZoneId: ActiveProject.TimeZoneId, timeZone: out TimeZoneInfo? zone))
        {
            ProjectStatus.Text = $"Project-local time zone: {zone!.DisplayName}";
            _projectTimeZone = zone;
            RefreshHistoricDates(utcNow: DateTime.UtcNow, resetSelection: true);
        }
        else
        {
            ProjectStatus.Text = "Project selected. Its time zone is missing or unrecognised; configure it in DLR Report before scheduling.";
            SetHistoricDatesUnavailable();
        }
        SavePreferences();
    }

    private static bool TryGetProjectTimeZone(string? timeZoneId, out TimeZoneInfo? timeZone)
    {
        timeZone = null;
        if (string.IsNullOrWhiteSpace(value: timeZoneId)) return false;
        try
        {
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(id: timeZoneId);
            return true;
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }
    #endregion

    #region Time and Inclusive Date Selection
    private void ScheduledTime_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        UpdateScheduleDescription();
        SavePreferences();
    }

    private void UpdateScheduleDescription()
    {
        SchedulePreferenceStatus.Text = $"Selected time: {ScheduledLocalTime.ToString(format: "HH:mm", provider: CultureInfo.InvariantCulture)} (project-local, 24-hour clock). Scheduling and computation are not yet enabled.";
    }

    private void HousekeepingDate_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        if (ValidateDateRange()) SavePreferences();
    }

    private bool ValidateDateRange()
    {
        bool deleteValid = ValidateHistoricRange(startPicker: StartDate, endPicker: EndDate,
            status: DateRangeStatus, label: "Delete Data");
        bool manualValid = ValidateHistoricRange(startPicker: ManualStartDate, endPicker: ManualEndDate,
            status: ManualDateRangeStatus, label: "Manual Data Reductions");
        return deleteValid && manualValid;
    }

    private bool ValidateHistoricRange(DatePicker startPicker, DatePicker endPicker, TextBlock status, string label)
    {
        status.Foreground = Brushes.Firebrick;
        if (_latestCompleteDay is not DateTime latest)
        {
            status.Text = string.Empty;
            return false;
        }
        if (startPicker.SelectedDate is not DateTime start || endPicker.SelectedDate is not DateTime end)
        {
            status.Text = $"{label}: select both a start date and an end date.";
            return false;
        }
        if (start.Date > latest || end.Date > latest)
        {
            status.Text = $"{label}: only complete project-local days through {latest:yyyy-MM-dd} may be selected.";
            return false;
        }
        if (start.Date > end.Date)
        {
            status.Text = $"{label}: the start date must be on or before the end date.";
            return false;
        }
        status.Foreground = Brushes.Black;
        status.Text = $"{label}: {start:yyyy-MM-dd} to {end:yyyy-MM-dd} (inclusive).";
        return true;
    }

    private void HousekeepingDate_ValidationError(object? sender, DatePickerDateValidationErrorEventArgs e)
    {
        e.ThrowException = false;
        if (sender is DatePicker picker) picker.SelectedDate = null;
        TextBlock status = sender == ManualStartDate || sender == ManualEndDate ? ManualDateRangeStatus : DateRangeStatus;
        status.Foreground = Brushes.Firebrick;
        status.Text = "Invalid date. Choose a valid completed project-local day using the calendar.";
    }

    private DatePicker[] HistoricPickers() => [StartDate, EndDate, ManualStartDate, ManualEndDate];

    private void SetHistoricDatesUnavailable()
    {
        _projectTimeZone = null;
        _latestCompleteDay = null;
        bool previousInitializing = _initializing;
        _initializing = true;
        foreach (DatePicker picker in HistoricPickers())
        {
            picker.IsEnabled = false;
            picker.SelectedDate = null;
        }
        _initializing = previousInitializing;
        HistoricDateStatus.Text = "Select a connected project with a valid time zone to set completed-day dates.";
        DateRangeStatus.Text = string.Empty;
        ManualDateRangeStatus.Text = string.Empty;
    }

    private void RefreshHistoricDates(DateTime utcNow, bool resetSelection)
    {
        if (_projectTimeZone is null) return;
        DateTime latest = TimeZoneInfo.ConvertTimeFromUtc(dateTime: utcNow, destinationTimeZone: _projectTimeZone).Date.AddDays(value: -1);
        if (!resetSelection && _latestCompleteDay == latest) return;
        _latestCompleteDay = latest;
        bool previousInitializing = _initializing;
        _initializing = true;
        foreach (DatePicker picker in HistoricPickers())
        {
            DateTime? selected = resetSelection ? latest : picker.SelectedDate;
            if (selected > latest) selected = latest;
            picker.SelectedDate = null;
            picker.BlackoutDates.Clear();
            picker.DisplayDateEnd = latest;
            picker.BlackoutDates.Add(item: new CalendarDateRange(start: latest.AddDays(value: 1), end: DateTime.MaxValue));
            picker.SelectedDate = selected;
            picker.DisplayDate = selected ?? latest;
            picker.IsEnabled = true;
        }
        _initializing = previousInitializing;
        HistoricDateStatus.Text = $"Project-local time zone: {_projectTimeZone.DisplayName}. Latest complete day: {latest:yyyy-MM-dd}.";
        ValidateDateRange();
    }

    private void CalendarTimer_Tick(object? sender, EventArgs e)
    {
        RefreshHistoricDates(utcNow: DateTime.UtcNow, resetSelection: false);
    }
    #endregion



    #region Local Configuration and Preference Persistence
    private string? LoadConfigurationAndSettings()
    {
        try
        {
            string configurationPath = Path.Combine(path1: AppContext.BaseDirectory, path2: "appsettings.json");
            _configuration = JsonSerializer.Deserialize<ApplicationConfiguration>(json: File.ReadAllText(path: configurationPath))
                ?? throw new InvalidDataException(message: "Application configuration is empty.");
            if (string.IsNullOrWhiteSpace(value: _configuration.DatabaseName)
                || _configuration.ConnectionTimeoutSeconds is < 1 or > 120
                || _configuration.CommandTimeoutSeconds is < 1 or > 120
                || !TimeOnly.TryParseExact(s: _configuration.DefaultScheduledTime, format: "HH:mm",
                    provider: CultureInfo.InvariantCulture, style: DateTimeStyles.None, result: out _))
            {
                throw new InvalidDataException(message: "Application configuration is invalid.");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // The fallback is limited to safe interface defaults, never server or credential values.
            _configuration = new ApplicationConfiguration();
            return "Application configuration could not be read. Safe interface defaults are in use.";
        }
        try
        {
            if (!File.Exists(path: _settingsPath)) return null;
            _settings = JsonSerializer.Deserialize<UserSettings>(json: File.ReadAllText(path: _settingsPath))
                ?? throw new InvalidDataException(message: "User settings are empty.");
            if (!string.IsNullOrEmpty(value: _settings.ProtectedConnectionString))
            {
                byte[] encrypted = Convert.FromBase64String(s: _settings.ProtectedConnectionString);
                byte[] decrypted = ProtectedData.Unprotect(encryptedData: encrypted, optionalEntropy: null, scope: DataProtectionScope.CurrentUser);
                try
                {
                    _savedConnectionString = BuildConnectionString(input: Encoding.UTF8.GetString(bytes: decrypted));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(buffer: decrypted);
                }
            }
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
            or CryptographicException or ArgumentException or FormatException)
        {
            _settings = new UserSettings();
            _savedConnectionString = null;
            return "Saved preferences could not be read. Re-enter and test the database connection.";
        }
    }

    private void SavePreferences()
    {
        if (_initializing || _closing) return;
        try
        {
            _settings.ScheduledTime = ScheduledLocalTime.ToString(format: "HH:mm", provider: CultureInfo.InvariantCulture);
            _settings.StartDate = null;
            _settings.EndDate = null;
            // Untested connection edits never replace saved, validated database/project identity.
            if (_validatedConnectionString is not null)
            {
                byte[] cleartext = Encoding.UTF8.GetBytes(s: _validatedConnectionString);
                try
                {
                    byte[] encrypted = ProtectedData.Protect(userData: cleartext, optionalEntropy: null, scope: DataProtectionScope.CurrentUser);
                    _settings.ProtectedConnectionString = Convert.ToBase64String(inArray: encrypted);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(buffer: cleartext);
                }
                _settings.ActiveProjectId = ActiveProject?.ProjectId;
            }
            string directory = Path.GetDirectoryName(path: _settingsPath)
                ?? throw new InvalidOperationException(message: "The settings directory could not be determined.");
            Directory.CreateDirectory(path: directory);
            string temporaryPath = _settingsPath + ".tmp";
            File.WriteAllText(path: temporaryPath, contents: JsonSerializer.Serialize(value: _settings,
                options: new JsonSerializerOptions { WriteIndented = true }));
            File.Move(sourceFileName: temporaryPath, destFileName: _settingsPath, overwrite: true);
            if (_validatedConnectionString is not null) _savedConnectionString = _validatedConnectionString;
            SettingsStatus.Text = "Preferences saved for this Windows user.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException or InvalidOperationException)
        {
            SettingsStatus.Text = "Preferences could not be saved. Current selections remain available until this window closes.";
        }
    }
    #endregion

    #region Data Models
    public sealed record ProjectItem(int ProjectId, string ProjectName, string? TimeZoneId);

    public sealed class ApplicationConfiguration
    {
        public string DatabaseName { get; set; } = "DBTrackGeometry";
        public string DefaultScheduledTime { get; set; } = "00:10";
        public int ConnectionTimeoutSeconds { get; set; } = 15;
        public int CommandTimeoutSeconds { get; set; } = 30;
    }

    public sealed class UserSettings
    {
        public string? ProtectedConnectionString { get; set; }
        public int? ActiveProjectId { get; set; }
        public string? ScheduledTime { get; set; }
        public DateTime? StartDate { get; set; }
        public DateTime? EndDate { get; set; }
    }
    #endregion
}
#endregion
