#region Imports
using System.Globalization;
using System.IO;
using System.Xml.Linq;
#endregion
namespace GNA_DBDayReductions;
#region Project Local Schedule and Windows Task Definitions
public static class ProjectSchedule
{
    public const int DispatchToleranceSeconds = 120;
    public static TimeOnly Time(ScheduledReductionProfile profile)
    {
        if (!TimeOnly.TryParseExact(s: profile.ScheduledTime, format: "HH:mm", provider: CultureInfo.InvariantCulture, style: DateTimeStyles.None, result: out TimeOnly time) || time.Minute == 0)
            throw new InvalidDataException(message: "The scheduled time must use HH:mm and minutes 01–59.");
        return time;
    }
    public static DateTime? DueUtc(DateTime date, TimeOnly time, TimeZoneInfo zone)
    {
        DateTime local = DateTime.SpecifyKind(value: date.Date.Add(time.ToTimeSpan()), kind: DateTimeKind.Unspecified);
        // A nonexistent DST time is a missed run; an ambiguous time runs at its first occurrence only.
        if (zone.IsInvalidTime(dateTime: local)) return null;
        if (zone.IsAmbiguousTime(dateTime: local))
        {
            TimeSpan[] offsets = zone.GetAmbiguousTimeOffsets(dateTime: local);
            TimeSpan offset = offsets.Max();
            return new DateTimeOffset(dateTime: local, offset: offset).UtcDateTime;
        }
        return TimeZoneInfo.ConvertTimeToUtc(dateTime: local, sourceTimeZone: zone);
    }
    public static DateTime NextUtc(ScheduledReductionProfile profile, DateTime utcNow)
    {
        TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById(id: profile.TimeZoneId);
        DateTime date = TimeZoneInfo.ConvertTimeFromUtc(dateTime: utcNow, destinationTimeZone: zone).Date;
        TimeOnly time = Time(profile: profile);
        for (int day = 0; day < 370; day++)
        {
            DateTime? due = DueUtc(date: date.AddDays(value: day), time: time, zone: zone);
            if (due > utcNow) return due.Value;
        }
        throw new InvalidOperationException(message: "Cannot resolve the next project-local schedule.");
    }
    public static bool IsDue(ScheduledReductionProfile profile, DateTime utcNow)
    {
        TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById(id: profile.TimeZoneId);
        DateTime local = TimeZoneInfo.ConvertTimeFromUtc(dateTime: utcNow, destinationTimeZone: zone);
        DateTime? due = DueUtc(date: local.Date, time: Time(profile: profile), zone: zone);
        return due.HasValue && utcNow >= due.Value && utcNow < due.Value.AddSeconds(value: DispatchToleranceSeconds);
    }
    public static IReadOnlyList<TimeSpan> UtcSlots(ScheduledReductionProfile profile, DateTime utcNow)
    {
        TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById(id: profile.TimeZoneId);
        HashSet<TimeSpan> offsets = new() { zone.BaseUtcOffset };
        foreach (TimeZoneInfo.AdjustmentRule rule in zone.GetAdjustmentRules())
        {
            if (rule.DateEnd.Year < utcNow.Year) continue;
            offsets.Add(item: zone.BaseUtcOffset + rule.BaseUtcOffsetDelta);
            offsets.Add(item: zone.BaseUtcOffset + rule.BaseUtcOffsetDelta + rule.DaylightDelta);
        }
        SortedSet<TimeSpan> slots = new();
        foreach (TimeSpan offset in offsets)
        {
            double minutes = (Time(profile: profile).ToTimeSpan() - offset).TotalMinutes;
            slots.Add(item: TimeSpan.FromMinutes(value: (minutes % 1440 + 1440) % 1440));
        }
        if (slots.Count > 48) throw new InvalidOperationException(message: "Too many time-zone offsets for Windows Task Scheduler.");
        return slots.ToList();
    }
}
public static class SchedulerTaskDefinition
{
    public const int MaximumRunMinutes = 15;
    public const int CancellationGraceSeconds = 30;
    public const int TerminationGraceSeconds = 10;
    public static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    public static string WorkerName(string sid) => "GNA_DBDayReductions_" + sid;
    public static string DispatcherName(string sid) => WorkerName(sid: sid) + "_Schedule";
    public static string Build(ScheduledReductionProfile profile, string profilePath, bool dispatcher, DateTime utcNow)
    {
        XElement E(string name, object? value) => new(name: Ns + name, content: value);
        XElement triggers = E(name: "Triggers", value: null);
        if (dispatcher)
        {
            foreach (TimeSpan slot in ProjectSchedule.UtcSlots(profile: profile, utcNow: utcNow))
            {
                DateTime start = utcNow.Date.Add(value: slot);
                if (start <= utcNow) start = start.AddDays(value: 1);
                triggers.Add(content: E(name: "CalendarTrigger", value: new object[] {
                    E(name:"StartBoundary",value:start.ToString(format:"yyyy-MM-ddTHH:mm:ss'Z'",provider:CultureInfo.InvariantCulture)),
                    E(name:"Enabled",value:true),E(name:"ScheduleByDay",value:E(name:"DaysInterval",value:1)) }));
            }
        }
        XElement root = new(name: Ns + "Task", content: new object[] { new XAttribute(name: "version", value: "1.2"),
            E(name:"RegistrationInfo",value:new object[] { E(name:"Description",value:profilePath), E(name:"Source",value:"GNA_DBDayReductions:"+profile.OwnerSid) }),
            triggers,
            E(name:"Principals",value:new XElement(name: Ns+"Principal", content: new object[] { new XAttribute(name: "id", value: "Operator"),E(name: "UserId", value: profile.OwnerSid),E(name: "LogonType", value: "Password"),E(name: "RunLevel", value: "HighestAvailable") })),
            E(name:"Settings",value:new object[] {
                E(name: "MultipleInstancesPolicy", value: "IgnoreNew"),E(name: "DisallowStartIfOnBatteries", value: false),E(name: "StopIfGoingOnBatteries", value: false),
                E(name: "AllowHardTerminate", value: true),E(name: "StartWhenAvailable", value: false),E(name: "RunOnlyIfNetworkAvailable", value: false),
                E(name: "IdleSettings", value: new object[]{E(name: "StopOnIdleEnd", value: false),E(name: "RestartOnIdle", value: false)}),
                E(name: "AllowStartOnDemand", value: true),E(name: "Enabled", value: true),E(name: "Hidden", value: false),E(name: "RunOnlyIfIdle", value: false),E(name: "WakeToRun", value: false),E(name: "ExecutionTimeLimit", value: System.Xml.XmlConvert.ToString(value: TimeSpan.FromMinutes(value: MaximumRunMinutes))),E(name: "Priority", value: 7) }),
            E(name:"Actions",value:new XElement(name: Ns+"Exec", content: new object[] { E(name: "Command", value: profile.ExecutablePath),
                E(name: "Arguments", value: (dispatcher?"--schedule-check ":"--run-daily ")+"\""+profilePath+"\""),E(name: "WorkingDirectory", value: Path.GetDirectoryName(path:profile.ExecutablePath)) })) });
        root.Element(name: Ns + "Actions")!.SetAttributeValue(name: "Context", value: "Operator");
        return root.ToString();
    }
}
#endregion





