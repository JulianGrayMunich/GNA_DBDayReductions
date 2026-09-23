#region Imports
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;
#endregion
namespace GNA_DBDayReductions;
#region Application Launch Modes
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e: e);
        if (e.Args.Length == 0) { new MainWindow().Show(); return; }
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        int result = 1;
        string? profilePath = e.Args.Length == 2 ? e.Args[1] : null;
        try
        {
            switch (e.Args[0])
            {
                case "--run-daily" when profilePath is not null:
                    result = await Task.Run(function: () => ScheduledReductionRunner.RunAsync(path: profilePath)); break;
                case "--schedule-check" when profilePath is not null:
                    using (WindowsScheduler scheduler = new()) scheduler.Dispatch(profilePath: profilePath);
                    result = 0; break;
                case "--install-scheduler" when profilePath is not null:
                    ScheduledReductionProfile profile = ScheduledReductionProfile.Read(path: profilePath);
                    RequireAdministrator();
                    string executable = Path.ChangeExtension(path: typeof(App).Assembly.Location, extension: ".exe");
                    if (!string.Equals(a: Path.GetFullPath(path: profile.ExecutablePath), b: executable, comparisonType: StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(message: "The schedule must launch this application executable.");
                    SystemActivityLog.EnsureFolder(folder: profile.LogFolder);
                    SchedulerPasswordWindow prompt = new(account: profile.OwnerName);
                    if (prompt.ShowDialog() != true) { result = 2; break; }
                    using (System.Security.SecureString password = prompt.Password)
                    using (WindowsScheduler scheduler = new()) scheduler.Register(profile: profile, profilePath: profilePath, password: password);
                    result = 0; break;
                case "--stop-scheduler" when profilePath is not null:
                    if (profilePath != ScheduledReductionProfile.CurrentSid) throw new InvalidOperationException(message: "Use the same Windows account to stop the scheduler.");
                    RequireAdministrator();
                    using (WindowsScheduler scheduler = new()) scheduler.Disable(sid: profilePath);
                    result = 0; break;
                default: throw new ArgumentException(message: "Unrecognised command-line arguments.");
            }
        }
        catch (Exception exception)
        {
            if (e.Args[0] is "--install-scheduler" or "--stop-scheduler")
                MessageBox.Show(messageBoxText: exception.Message, caption: "Daily reduction scheduler", button: MessageBoxButton.OK, icon: MessageBoxImage.Error);
            else
            {
                try
                {
                    ScheduledReductionProfile profile = ScheduledReductionProfile.Read(path: profilePath ?? string.Empty);
                    SystemActivityLog.Append(folder: profile.LogFolder, zone: TimeZoneInfo.FindSystemTimeZoneById(id: profile.TimeZoneId), message: "Failed completion — scheduler: " + exception.Message);
                }
                catch { /* Task Scheduler receives the nonzero exit code. */ }
            }
        }
        Shutdown(exitCode: result);
    }
    private static void RequireAdministrator()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(ntIdentity: identity).IsInRole(role: WindowsBuiltInRole.Administrator))
            throw new InvalidOperationException(message: "Administrator elevation is required to configure the highest-privilege task.");
    }
}
#endregion


