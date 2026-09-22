#region Imports
using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
#endregion
namespace GNA_DBDayReductions;
#region Project Registration and Epoch Reads
public sealed partial class DailyReductionService
{
    private static string RegistrySql(ReductionPlan plan) => plan.Rule.Identity switch
    {
        "Point" => "SELECT p.PointName_ID FROM dbo.PointName p WHERE p.Project_ID=@project AND p.IsDeleted=0",
        "Pair" => "SELECT p.PrismPair_ID FROM dbo.PrismPairs p JOIN dbo.Track t ON t.Track_ID=p.Track_ID JOIN dbo.PointName l ON l.PointName_ID=p.Left_ID JOIN dbo.PointName r ON r.PointName_ID=p.Right_ID WHERE t.Project_ID=@project AND t.IsDeleted=0 AND p.IsDeleted=0 AND l.IsDeleted=0 AND r.IsDeleted=0",
        "Array" => "SELECT a.Array_ID FROM dbo.PrismArray a WHERE a.Project_ID=@project AND a.ArrayType=@kind AND a.IsDeleted=0",
        "ArrayPoint" => "SELECT a.Array_ID,p.PointName_ID FROM dbo.PrismArray a JOIN dbo.PrismArrayPoint ap ON ap.Array_ID=a.Array_ID JOIN dbo.PointName p ON p.PointName_ID=ap.PointName_ID WHERE a.Project_ID=@project AND a.ArrayType=@kind AND a.IsDeleted=0 AND ap.IsDeleted=0 AND p.IsDeleted=0",
        "Sensor" => "SELECT s.SensorID,s.ReplacementName FROM dbo.GeotecSensors s WHERE s.Project_ID=@project AND s.SensorType=@sensorType AND s.IsDeleted=0",
        _ => throw new InvalidOperationException(message: "Unknown registration rule.")
    };
    private static void AddRegistryParameters(SqlCommand command, ReductionPlan plan, int projectId)
    {
        Add(command: command, name: "@project", type: SqlDbType.Int, value: projectId);
        Add(command: command, name: "@kind", type: SqlDbType.Int, value: plan.Rule.ArrayType);
        Add(command: command, name: "@sensorType", type: SqlDbType.NVarChar, value: plan.Rule.SensorType);
    }
    private async Task ValidateOwnershipAsync(SqlConnection connection, SqlTransaction transaction, ReductionPlan plan, int projectId, CancellationToken cancellationToken)
    {
        string? sql = plan.Rule.Identity switch
        {
            "Pair" => "SELECT COUNT_BIG(*) FROM dbo.PrismPairs p JOIN dbo.Track t ON t.Track_ID=p.Track_ID JOIN dbo.PointName l ON l.PointName_ID=p.Left_ID JOIN dbo.PointName r ON r.PointName_ID=p.Right_ID WHERE t.Project_ID=@project AND t.IsDeleted=0 AND p.IsDeleted=0 AND (l.Project_ID<>t.Project_ID OR r.Project_ID<>t.Project_ID)",
            "Array" or "ArrayPoint" => "SELECT COUNT_BIG(*) FROM dbo.PrismArray a JOIN dbo.PrismArrayPoint ap ON ap.Array_ID=a.Array_ID JOIN dbo.PointName p ON p.PointName_ID=ap.PointName_ID WHERE a.Project_ID=@project AND a.ArrayType=@kind AND a.IsDeleted=0 AND ap.IsDeleted=0 AND p.Project_ID<>a.Project_ID",
            _ => null
        };
        if (plan.Rule.Identity is "Sensor" or "Array" or "ArrayPoint")
        {
            string registry = plan.Rule.Identity == "Sensor" ? "GeotecSensors" : "PrismArray";
            string key = plan.Rule.Identity == "Sensor" ? "SensorID" : "Array_ID";
            string type = plan.Rule.Identity == "Sensor" ? "SensorType<>@sensorType" : "ArrayType<>@kind";
            using SqlCommand coverage = Command(connection: connection, transaction: transaction,
                sql: $"SELECT COUNT_BIG(*) FROM {Table(schema: plan.Schema, name: plan.Source)} e JOIN dbo.{Q(name: registry)} r ON e.{Q(name: key)}=r.{Q(name: key)} WHERE r.Project_ID=@project AND r.IsDeleted=0 AND e.IsDeleted=0 AND r.{type};");
            AddRegistryParameters(command: coverage, plan: plan, projectId: projectId);
            if (Convert.ToInt64(value: await coverage.ExecuteScalarAsync(cancellationToken: cancellationToken), provider: CultureInfo.InvariantCulture) != 0)
                throw new InvalidOperationException(message: $"{plan.Source}: configured registration type does not match existing project epochs. Review appsettings.json.");
        }
        if (sql is null) return;
        using SqlCommand command = Command(connection: connection, transaction: transaction, sql: sql);
        AddRegistryParameters(command: command, plan: plan, projectId: projectId);
        if (Convert.ToInt64(value: await command.ExecuteScalarAsync(cancellationToken: cancellationToken), provider: CultureInfo.InvariantCulture) != 0)
            throw new InvalidOperationException(message: $"{plan.Source}: registration contains points belonging to another project.");
    }
    private async Task<List<ReductionEntity>> ReadEntitiesAsync(SqlConnection connection, SqlTransaction transaction, ReductionPlan plan, int projectId, CancellationToken cancellationToken)
    {
        using SqlCommand command = Command(connection: connection, transaction: transaction,
            sql: "SELECT * FROM (" + RegistrySql(plan: plan) + ") registration ORDER BY " + string.Join(separator: ",", values: plan.Keys.Select(selector: Q)));
        AddRegistryParameters(command: command, plan: plan, projectId: projectId);
        List<ReductionEntity> entities = new();
        using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken: cancellationToken);
        while (await reader.ReadAsync(cancellationToken: cancellationToken))
        {
            int[] ids = new int[plan.Keys.Length];
            for (int index = 0; index < ids.Length; index++) ids[index] = reader.GetInt32(i: index);
            string replacement = plan.Rule.Identity == "Sensor" ? reader.GetString(i: ids.Length) : string.Empty;
            entities.Add(item: new(Ids: ids, ReplacementName: replacement));
        }
        return entities;
    }
    private async Task<Dictionary<string, List<decimal?>[]>> ReadDayAsync(SqlConnection connection, SqlTransaction transaction,
        ReductionPlan plan, int projectId, List<ReductionEntity> entities, ReductionDay day, CancellationToken cancellationToken, DataTable? extracted = null)
    {
        Dictionary<string, List<decimal?>[]> result = new();
        foreach (ReductionEntity entity in entities)
        {
            List<decimal?>[] fields = new List<decimal?>[plan.Measurements.Count];
            for (int index = 0; index < fields.Length; index++) fields[index] = new();
            result.Add(key: entity.Key, value: fields);
        }
        string keyFields = string.Join(separator: ",", values: plan.Keys.Select(selector: k => "e." + Q(name: k)));
        string measurementFields = string.Join(separator: ",", values: plan.Measurements.Select(selector: c => "e." + Q(name: c.Name)));
        string join = string.Join(separator: " AND ", values: plan.Keys.Select(selector: k => "e." + Q(name: k) + "=r." + Q(name: k)));
        using SqlCommand command = Command(connection: connection, transaction: transaction,
            sql: $"SELECT e.* FROM {Table(schema: plan.Schema, name: plan.Source)} e JOIN ({RegistrySql(plan: plan)}) r ON {join} WHERE e.IsDeleted=0 AND e.UTCtime>=@start AND e.UTCtime<@end ORDER BY {keyFields},e.UTCtime;");
        AddRegistryParameters(command: command, plan: plan, projectId: projectId);
        Add(command: command, name: "@start", type: SqlDbType.DateTime2, value: day.StartUtc);
        Add(command: command, name: "@end", type: SqlDbType.DateTime2, value: day.EndUtc);
        using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken: cancellationToken);
        if (extracted is not null && extracted.Columns.Count == 0)
            for (int ordinal = 0; ordinal < reader.FieldCount; ordinal++)
                extracted.Columns.Add(columnName: reader.GetName(i: ordinal), type: reader.GetFieldType(i: ordinal));
        while (await reader.ReadAsync(cancellationToken: cancellationToken))
        {
            if (extracted is not null)
            {
                object[] row = new object[reader.FieldCount];
                reader.GetValues(values: row); extracted.Rows.Add(values: row);
            }
            int[] ids = new int[plan.Keys.Length];
            for (int index = 0; index < ids.Length; index++) ids[index] = reader.GetInt32(i: reader.GetOrdinal(name: plan.Keys[index]));
            List<decimal?>[] fields = result[string.Join(separator: "/", values: ids)];
            for (int index = 0; index < fields.Length; index++)
            {
                int ordinal = reader.GetOrdinal(name: plan.Measurements[index].Name);
                fields[index].Add(item: reader.IsDBNull(i: ordinal) ? null : Convert.ToDecimal(value: reader.GetValue(i: ordinal), provider: CultureInfo.InvariantCulture));
            }
        }
        return result;
    }
}
#endregion


