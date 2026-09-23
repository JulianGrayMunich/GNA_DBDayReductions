#region Imports
using System.IO;
using Microsoft.Data.SqlClient;
#endregion
namespace GNA_DBDayReductions;
#region Unattended Reduction Runner
public static class ScheduledReductionRunner
{
    public static ReductionRequest PreviousDay(ScheduledReductionProfile profile, DateTime utcNow)
    {
        TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById(id: profile.TimeZoneId);
        DateTime day = TimeZoneInfo.ConvertTimeFromUtc(dateTime: utcNow, destinationTimeZone: zone).Date.AddDays(value: -1);
        return new(ProjectId: profile.ProjectId, ProjectName: profile.ProjectName, TimeZoneId: profile.TimeZoneId, StartDate: day, EndDate: day);
    }
    public static async Task<int> RunAsync(string path, DateTime? utcNow = null)
    {
        ScheduledReductionProfile? profile = null;
        TimeZoneInfo zone = TimeZoneInfo.Utc;
        int finished = 0;
        string phase = "Starting";
        using CancellationTokenSource cancellation = new(delay: TimeSpan.FromMinutes(value: SchedulerTaskDefinition.MaximumRunMinutes) - TimeSpan.FromSeconds(value: SchedulerTaskDefinition.CancellationGraceSeconds));
        using Timer watchdog = new(callback: _ =>
        {
            if (Interlocked.CompareExchange(location1: ref finished, value: 1, comparand: 0) != 0) return;
            WriteOutcome(profile: profile, zone: zone, message: "Failed completion — execution terminated before the 15-minute limit. Last operation: " + Volatile.Read(location: ref phase));
            Environment.Exit(exitCode: 2);
        }, state: null, dueTime: TimeSpan.FromMinutes(value: SchedulerTaskDefinition.MaximumRunMinutes) - TimeSpan.FromSeconds(value: SchedulerTaskDefinition.TerminationGraceSeconds), period: Timeout.InfiniteTimeSpan);
        try
        {
            profile = ScheduledReductionProfile.Read(path: path);
            zone = TimeZoneInfo.FindSystemTimeZoneById(id: profile.TimeZoneId);
            ReductionRequest request = PreviousDay(profile: profile, utcNow: utcNow ?? DateTime.UtcNow);
            SystemActivityLog.Append(folder: profile.LogFolder, zone: zone, message: $"Started — {profile.ProjectName} — reduction date {request.StartDate:yyyy-MM-dd}.");
            DailyReductionService service = new(options: profile.Options);
            Progress<string> progress = new(handler: message => Volatile.Write(location: ref phase, value: message));
            ReductionSummary result = await service.RunAsync(connectionString: profile.ConnectionString(), request: request,
                progress: progress, cancellationToken: cancellation.Token, review: null).ConfigureAwait(continueOnCapturedContext: false);
            if (Interlocked.CompareExchange(location1: ref finished, value: 1, comparand: 0) == 0)
            {
                SystemActivityLog.Append(folder: profile.LogFolder, zone: zone,
                    message: $"Successful completion — {profile.ProjectName} — reduction date {request.StartDate:yyyy-MM-dd}; {result.Tables} tables, {result.Rows} results; Pass {result.Pass}, Fail {result.Fail}.");
            }
            return 0;
        }
        catch (Exception exception)
        {
            // A failed success-log append is also a failed execution, with a fallback diagnostic.
            Interlocked.Exchange(location1: ref finished, value: 1);
            string reason = exception is OperationCanceledException ? "Execution time limit reached; the current table was rolled back."
                : exception is SqlException sql ? $"SQL error {sql.Number}: {sql.Message}" : exception.Message;
            WriteOutcome(profile: profile, zone: zone, message: $"Failed completion — {profile?.ProjectName ?? "unknown project"} — {reason} Last operation: {Volatile.Read(location: ref phase)}. Previously committed tables remain.");
            return 1;
        }
    }
    private static void WriteOutcome(ScheduledReductionProfile? profile, TimeZoneInfo zone, string message)
    {
        try
        {
            if (profile is not null) { SystemActivityLog.Append(folder: profile.LogFolder, zone: zone, message: message); return; }
        }
        catch (Exception exception) { message += " Log folder unavailable: " + exception.GetType().Name; }
        try { SystemActivityLog.Append(folder: ScheduledReductionProfile.ProfileDirectory, zone: zone, message: message); }
        catch { /* Nonzero exit code remains available in Task Scheduler if both log locations fail. */ }
    }
}
#endregion


