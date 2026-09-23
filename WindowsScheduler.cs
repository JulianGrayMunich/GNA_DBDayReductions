#region Imports
using System.Runtime.InteropServices;
using System.Security;
using System.Xml.Linq;
#endregion
namespace GNA_DBDayReductions;
#region Windows Task Scheduler Adapter
public sealed record SchedulerSnapshot(bool Enabled, bool Running, string? ProfilePath, int LastResult);
public sealed class WindowsScheduler : IDisposable
{
    private const int TaskCreateOrUpdate = 6;
    private const int PasswordLogon = 1;
    private readonly List<object> _objects = new();
    private readonly dynamic _folder;
    public WindowsScheduler()
    {
        Type type = Type.GetTypeFromProgID(progID: "Schedule.Service") ?? throw new InvalidOperationException(message: "Windows Task Scheduler is unavailable.");
        dynamic service = Keep(instance: Activator.CreateInstance(type: type) ?? throw new InvalidOperationException(message: "Cannot connect to Windows Task Scheduler."));
        try { service.Connect(); _folder = Keep(instance: service.GetFolder(path: "\\")); }
        catch { Dispose(); throw; }
    }
    private dynamic Keep(object instance)
    {
        foreach (object existing in _objects) if (ReferenceEquals(objA: existing, objB: instance)) return instance;
        _objects.Add(item: instance); return instance;
    }
    private dynamic? Find(string name)
    {
        try { return Keep(instance: _folder.GetTask(path: name)); }
        catch (Exception exception) when (exception.HResult == unchecked((int)0x80070002) || exception.HResult == unchecked((int)0x80070003)) { return null; }
    }
    private static string OwnedProfile(dynamic task, string sid)
    {
        XDocument xml = XDocument.Parse(text: (string)task.Xml);
        XElement registration = xml.Root?.Element(name: SchedulerTaskDefinition.Ns + "RegistrationInfo") ?? throw new InvalidOperationException(message: "Invalid task registration.");
        if ((string?)registration.Element(name: SchedulerTaskDefinition.Ns + "Source") != "GNA_DBDayReductions:" + sid)
            throw new InvalidOperationException(message: "A task with this name belongs to another application. No changes were made.");
        return (string?)registration.Element(name: SchedulerTaskDefinition.Ns + "Description") ?? throw new InvalidOperationException(message: "Scheduler profile not found.");
    }
    public SchedulerSnapshot Read(string sid)
    {
        dynamic? dispatcher = Find(name: SchedulerTaskDefinition.DispatcherName(sid: sid));
        dynamic? worker = Find(name: SchedulerTaskDefinition.WorkerName(sid: sid));
        string? path = dispatcher is null ? null : OwnedProfile(task: dispatcher, sid: sid);
        if (worker is not null)
        {
            string workerPath = OwnedProfile(task: worker, sid: sid);
            if (path is not null && path != workerPath) throw new InvalidOperationException(message: "Scheduler task profiles do not match. Stop and restart the schedule.");
            path ??= workerPath;
        }
        bool enabled = dispatcher is not null && (bool)dispatcher.Enabled;
        if (!enabled && worker is not null && (bool)worker.Enabled)
            throw new InvalidOperationException(message: "An incomplete schedule was found. Click Stop, then restart the schedule.");
        if (enabled && (worker is null || !(bool)worker.Enabled)) throw new InvalidOperationException(message: "Daily reduction task is missing or disabled. Stop and restart the schedule.");
        return new(Enabled: enabled, Running: worker is not null && ((int)worker.State is 2 or 4), ProfilePath: path, LastResult: worker is null ? 0 : (int)worker.LastTaskResult);
    }
    public void Register(ScheduledReductionProfile profile, string profilePath, SecureString password)
    {
        string[] names = [SchedulerTaskDefinition.WorkerName(sid: profile.OwnerSid), SchedulerTaskDefinition.DispatcherName(sid: profile.OwnerSid)];
        foreach (string name in names)
        {
            dynamic? existing = Find(name: name);
            if (existing is null) continue;
            OwnedProfile(task: existing, sid: profile.OwnerSid);
            if ((bool)existing.Enabled || ((int)existing.State is 2 or 4)) throw new InvalidOperationException(message: "Stop the existing schedule and allow its running reduction to finish before starting a new schedule.");
        }
        IntPtr buffer = Marshal.SecureStringToBSTR(s: password);
        try
        {
            string clear = Marshal.PtrToStringBSTR(ptr: buffer);
            try
            {
                for (int index = 0; index < names.Length; index++)
                {
                    string xml = SchedulerTaskDefinition.Build(profile: profile, profilePath: profilePath, dispatcher: index == 1, utcNow: DateTime.UtcNow);
                    Keep(instance: _folder.RegisterTask(path: names[index], xmlText: xml, flags: TaskCreateOrUpdate,
                        userId: profile.OwnerName, password: clear, logonType: PasswordLogon, sddl: null));
                }
            }
            catch
            {
                foreach (string name in names) { dynamic? created = Find(name: name); if (created is not null) created.Enabled = false; }
                throw;
            }
            finally { clear = string.Empty; }
        }
        finally { Marshal.ZeroFreeBSTR(s: buffer); }
    }
    public void Disable(string sid)
    {
        foreach (string name in new[] { SchedulerTaskDefinition.DispatcherName(sid: sid), SchedulerTaskDefinition.WorkerName(sid: sid) })
        {
            dynamic? task = Find(name: name);
            if (task is null) continue;
            OwnedProfile(task: task, sid: sid); task.Enabled = false;
        }
    }
    public void Dispatch(string profilePath)
    {
        ScheduledReductionProfile profile = ScheduledReductionProfile.Read(path: profilePath);
        if (!ProjectSchedule.IsDue(profile: profile, utcNow: DateTime.UtcNow)) return;
        SchedulerSnapshot snapshot = Read(sid: profile.OwnerSid);
        if (!snapshot.Enabled || snapshot.ProfilePath != profilePath) return;
        dynamic task = Find(name: SchedulerTaskDefinition.WorkerName(sid: profile.OwnerSid)) ?? throw new InvalidOperationException(message: "Daily reduction task not found.");
        if ((int)task.State is not (2 or 4)) Keep(instance: task.Run(parameters: null));
    }
    public void Dispose()
    {
        for (int index = _objects.Count - 1; index >= 0; index--)
            if (Marshal.IsComObject(o: _objects[index])) Marshal.FinalReleaseComObject(o: _objects[index]);
        _objects.Clear();
    }
}
#endregion



