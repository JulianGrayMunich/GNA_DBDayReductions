#region Imports
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
#endregion
namespace GNA_DBDayReductions;
#region Persisted Unattended Configuration and Activity Log
public sealed class ScheduledReductionProfile
{
    public string OwnerSid { get; set; } = string.Empty;
    public string OwnerName { get; set; } = string.Empty;
    public int ProjectId { get; set; }
    public string ProjectName { get; set; } = string.Empty;
    public string TimeZoneId { get; set; } = string.Empty;
    public string ScheduledTime { get; set; } = string.Empty;
    public string LogFolder { get; set; } = string.Empty;
    public string ProtectedConnectionString { get; set; } = string.Empty;
    public ReductionOptions Options { get; set; } = new();
    public string ExecutablePath { get; set; } = string.Empty;
    public static string CurrentSid => WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException(message: "Windows user identity is unavailable.");
    public static string ProfileDirectory => Path.Combine(path1: Environment.GetFolderPath(folder: Environment.SpecialFolder.LocalApplicationData), path2: "GNA", path3: "GNA_DBDayReductions", path4: "Scheduler");
    public static string Protect(string connectionString)
    {
        byte[] plain = Encoding.UTF8.GetBytes(s: connectionString);
        try { return Convert.ToBase64String(inArray: ProtectedData.Protect(userData: plain, optionalEntropy: null, scope: DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(buffer: plain); }
    }
    public string ConnectionString()
    {
        byte[] plain = ProtectedData.Unprotect(encryptedData: Convert.FromBase64String(s: ProtectedConnectionString), optionalEntropy: null, scope: DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(bytes: plain); }
        finally { CryptographicOperations.ZeroMemory(buffer: plain); }
    }
    public static ScheduledReductionProfile Read(string path)
    {
        ScheduledReductionProfile profile = JsonSerializer.Deserialize<ScheduledReductionProfile>(json: File.ReadAllText(path: path))
            ?? throw new InvalidDataException(message: "The scheduler profile is empty.");
        if (profile.OwnerSid != CurrentSid) throw new InvalidOperationException(message: "Use the same Windows account that configured this schedule.");
        if (profile.ProjectId <= 0 || string.IsNullOrWhiteSpace(value: profile.ProjectName) || !Path.IsPathFullyQualified(path: profile.LogFolder))
            throw new InvalidDataException(message: "The scheduled project or log folder is invalid.");
        TimeZoneInfo.FindSystemTimeZoneById(id: profile.TimeZoneId);
        return profile;
    }
    public string Save()
    {
        Directory.CreateDirectory(path: ProfileDirectory);
        string path = Path.Combine(path1: ProfileDirectory, path2: Guid.NewGuid().ToString(format: "N") + ".json");
        File.WriteAllText(path: path, contents: JsonSerializer.Serialize(value: this, options: new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }
}
public static class SystemActivityLog
{
    public const string FileName = "SystemActivitylog.txt";
    public static void EnsureFolder(string folder)
    {
        if (!Path.IsPathFullyQualified(path: folder)) throw new InvalidOperationException(message: "Select an absolute log folder path.");
        string root = Path.GetPathRoot(path: folder) ?? throw new InvalidOperationException(message: "Invalid log folder.");
        if (!folder.StartsWith(value: @"\\", comparisonType: StringComparison.Ordinal) && new DriveInfo(driveName: root).DriveType == DriveType.Network)
            throw new InvalidOperationException(message: "Use a UNC network folder path instead of a mapped drive for logged-off execution.");
        Directory.CreateDirectory(path: folder);
        using FileStream stream = new(path: Path.Combine(path1: folder, path2: FileName), mode: FileMode.OpenOrCreate, access: FileAccess.Write, share: FileShare.ReadWrite);
    }
    public static void Append(string folder, TimeZoneInfo zone, string message)
    {
        EnsureFolder(folder: folder);
        string path = Path.Combine(path1: folder, path2: FileName);
        string safe = message.Replace(oldValue: "\r", newValue: " ").Replace(oldValue: "\n", newValue: " ");
        string line = $"DBDayReductions: {TimeZoneInfo.ConvertTimeFromUtc(dateTime: DateTime.UtcNow, destinationTimeZone: zone):yyyy-MM-dd HH:mm:ss} — {safe}{Environment.NewLine}";
        // Append with a short retry so two application instances never truncate each other's entries.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using FileStream stream = new(path: path, mode: FileMode.Append, access: FileAccess.Write, share: FileShare.Read);
                byte[] bytes = Encoding.UTF8.GetBytes(s: line); stream.Write(buffer: bytes); stream.Flush(flushToDisk: true); return;
            }
            catch (IOException) when (attempt < 9) { Thread.Sleep(millisecondsTimeout: 100); }
        }
    }
}
#endregion

