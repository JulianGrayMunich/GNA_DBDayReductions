#region Imports
using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
#endregion
namespace GNA_DBDayReductions;
#region Performance Models
public sealed record PerformanceSensor(string SourceTable, string SensorType, string EntityKey, string SensorName,
    IReadOnlyDictionary<DateTime, bool> Days)
{
    public int PassDays => Days.Count(predicate: day => day.Value);
    public int FailDays => Days.Count(predicate: day => !day.Value);
}
public sealed record PerformanceSummaryRow(string SensorType, string SensorName, int? PassDays, int? FailDays, string Status);
public sealed record PerformanceReport(ReductionRequest Request, IReadOnlyList<PerformanceSensor> FailingSensors,
    IReadOnlyList<PerformanceSummaryRow> Summary)
{
    public string Heading => $"Pass/Fail : {Request.StartDate:yyyy-MM-dd} to {Request.EndDate:yyyy-MM-dd}";
    public string SummaryMessage => FailingSensors.Count == 0 ? "No fails recorded for this period" : string.Empty;
}
public static class PerformanceReports
{
    public static PerformanceReport Build(ReductionRequest request, IEnumerable<string> types, IEnumerable<PerformanceSensor> sensors)
    {
        List<PerformanceSensor> registered = new(collection: sensors);
        List<PerformanceSensor> failed = new();
        foreach (PerformanceSensor sensor in registered) if (sensor.FailDays > 0) failed.Add(item: sensor);
        failed.Sort(comparison: (left, right) =>
        {
            int value = StringComparer.OrdinalIgnoreCase.Compare(x: left.SensorType, y: right.SensorType);
            if (value == 0) value = StringComparer.OrdinalIgnoreCase.Compare(x: left.SensorName, y: right.SensorName);
            if (value == 0) value = left.PassDays.CompareTo(value: right.PassDays);
            return value != 0 ? value : StringComparer.Ordinal.Compare(x: left.EntityKey, y: right.EntityKey);
        });
        List<PerformanceSummaryRow> summary = new();
        foreach (PerformanceSensor sensor in failed)
            summary.Add(item: new(SensorType: sensor.SensorType, SensorName: sensor.SensorName, PassDays: sensor.PassDays, FailDays: sensor.FailDays, Status: string.Empty));
        foreach (string type in types.Distinct(comparer: StringComparer.OrdinalIgnoreCase))
            if (!registered.Exists(match: sensor => sensor.SensorType == type))
                summary.Add(item: new(SensorType: type, SensorName: string.Empty, PassDays: null, FailDays: null, Status: "No sensors"));
        summary = summary.OrderBy(keySelector: row => row.SensorType, comparer: StringComparer.OrdinalIgnoreCase)
            .ThenBy(keySelector: row => row.SensorName, comparer: StringComparer.OrdinalIgnoreCase).ThenBy(keySelector: row => row.PassDays).ToList();
        return new(Request: request, FailingSensors: failed.AsReadOnly(), Summary: summary.AsReadOnly());
    }
}
#endregion
#region Committed Performance Query
public sealed partial class DailyReductionService
{
    public async Task<PerformanceReport> ReadPerformanceAsync(string connectionString, ReductionRequest request, CancellationToken cancellationToken)
    {
        if (request.StartDate.Date > request.EndDate.Date) throw new InvalidOperationException(message: "Select a valid performance date range.");
        using SqlConnection connection = new(connectionString: connectionString);
        await connection.OpenAsync(cancellationToken: cancellationToken);
        List<ReductionPlan> plans = await DiscoverAsync(connection: connection, cancellationToken: cancellationToken);
        // Read only committed records without holding reporting locks while the reducer advances.
        using SqlTransaction transaction = connection.BeginTransaction(iso: IsolationLevel.ReadCommitted);
        await ValidateProjectAsync(connection: connection, transaction: transaction, request: request, cancellationToken: cancellationToken);
        Dictionary<(string Source, string Key), Dictionary<DateTime, bool>> statuses = new();
        using (SqlCommand exists = Command(connection: connection, transaction: transaction, sql: "SELECT OBJECT_ID(N'dbo.DailyReductionStatistics',N'U');"))
        {
            object? id = await exists.ExecuteScalarAsync(cancellationToken: cancellationToken);
            if (id is not null && id != DBNull.Value)
            {
                using SqlCommand command = Command(connection: connection, transaction: transaction, sql: """
                    SELECT SourceTable,EntityKey,LocalDate,Performance FROM dbo.DailyReductionStatistics
                    WHERE Project_ID=@project AND LocalDate>=@start AND LocalDate<=@end;
                    """);
                Add(command: command, name: "@project", type: SqlDbType.Int, value: request.ProjectId);
                Add(command: command, name: "@start", type: SqlDbType.Date, value: request.StartDate.Date);
                Add(command: command, name: "@end", type: SqlDbType.Date, value: request.EndDate.Date);
                using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken: cancellationToken);
                while (await reader.ReadAsync(cancellationToken: cancellationToken))
                {
                    var key = (reader.GetString(i: 0), reader.GetString(i: 1));
                    if (!statuses.TryGetValue(key: key, value: out Dictionary<DateTime, bool>? days)) { days = new(); statuses.Add(key: key, value: days); }
                    string performance = reader.GetString(i: 3);
                    if (performance is not ("Pass" or "Fail")) throw new InvalidOperationException(message: "Unexpected performance value in statistics.");
                    if (!days.TryAdd(key: reader.GetDateTime(i: 2).Date, value: performance == "Pass"))
                        throw new InvalidOperationException(message: "Duplicate daily performance records require correction.");
                }
            }
        }
        List<PerformanceSensor> sensors = new();
        List<string> types = new();
        foreach (ReductionPlan plan in plans)
        {
            string type = plan.Source[..^6]; types.Add(item: type);
            string select = plan.Rule.Identity switch
            {
                "Point" => "SELECT r.PointName_ID,p.PointName FROM registrations r JOIN dbo.PointName p ON p.PointName_ID=r.PointName_ID",
                "Pair" => "SELECT r.PrismPair_ID,CONCAT(t.TrackName,N' / ',l.PointName,N' - ',q.PointName) FROM registrations r JOIN dbo.PrismPairs p ON p.PrismPair_ID=r.PrismPair_ID JOIN dbo.Track t ON t.Track_ID=p.Track_ID JOIN dbo.PointName l ON l.PointName_ID=p.Left_ID JOIN dbo.PointName q ON q.PointName_ID=p.Right_ID",
                "Array" => "SELECT r.Array_ID,a.ArrayName FROM registrations r JOIN dbo.PrismArray a ON a.Array_ID=r.Array_ID",
                "ArrayPoint" => "SELECT r.Array_ID,r.PointName_ID,CONCAT(a.ArrayName,N' / ',p.PointName) FROM registrations r JOIN dbo.PrismArray a ON a.Array_ID=r.Array_ID JOIN dbo.PointName p ON p.PointName_ID=r.PointName_ID",
                "Sensor" => "SELECT r.SensorID,s.SensorName FROM registrations r JOIN dbo.GeotecSensors s ON s.SensorID=r.SensorID",
                _ => throw new InvalidOperationException(message: "Unknown performance identity.")
            };
            using SqlCommand command = Command(connection: connection, transaction: transaction, sql: "WITH registrations AS (" + RegistrySql(plan: plan) + ") " + select + ";");
            AddRegistryParameters(command: command, plan: plan, projectId: request.ProjectId);
            using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken: cancellationToken);
            while (await reader.ReadAsync(cancellationToken: cancellationToken))
            {
                string[] ids = new string[plan.Keys.Length];
                for (int index = 0; index < ids.Length; index++) ids[index] = reader.GetInt32(i: index).ToString(provider: CultureInfo.InvariantCulture);
                string key = string.Join(separator: "/", value: ids);
                statuses.TryGetValue(key: (plan.Source, key), value: out Dictionary<DateTime, bool>? days);
                sensors.Add(item: new(SourceTable: plan.Source, SensorType: type, EntityKey: key,
                    SensorName: reader.GetString(i: plan.Keys.Length), Days: days ?? new Dictionary<DateTime, bool>()));
            }
        }
        cancellationToken.ThrowIfCancellationRequested(); transaction.Commit();
        return PerformanceReports.Build(request: request, types: types, sensors: sensors);
    }
}
#endregion

