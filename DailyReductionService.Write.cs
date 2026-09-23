#region Imports
using System.Data;
using Microsoft.Data.SqlClient;
#endregion
namespace GNA_DBDayReductions;
#region Daily Values and Audit Writes
public sealed partial class DailyReductionService
{
    private SqlCommand CreateDailyCommand(SqlConnection connection, SqlTransaction transaction, ReductionPlan plan)
    {
        string target = Table(schema: plan.Schema, name: plan.Target);
        List<string> predicates = new() { "[UTCtime]=@time" };
        List<string> columns = new() { "[UTCtime]", "[IsDeleted]", "[NoOfObservations]" };
        List<string> values = new() { "@time", "0", "@count" };
        List<string> assignments = new() { "[IsDeleted]=0", "[NoOfObservations]=@count" };
        for (int index = 0; index < plan.Keys.Length; index++)
        {
            string column = Q(name: plan.Keys[index]);
            predicates.Add(item: column + "=@k" + index);
            columns.Add(item: column); values.Add(item: "@k" + index);
        }
        for (int index = 0; index < plan.Measurements.Count; index++)
        {
            string column = Q(name: plan.Measurements[index].Name);
            columns.Add(item: column); values.Add(item: "@v" + index);
            assignments.Add(item: column + "=@v" + index);
        }
        if (plan.Rule.Identity == "Sensor")
        {
            columns.Add(item: "[ReplacementName]"); values.Add(item: "@replacement"); assignments.Add(item: "[ReplacementName]=@replacement");
        }
        string predicate = string.Join(separator: " AND ", values: predicates);
        SqlCommand command = Command(connection: connection, transaction: transaction, sql: $"""
            IF (SELECT COUNT_BIG(*) FROM {target} WITH (UPDLOCK,HOLDLOCK) WHERE {predicate})>1
                THROW 51001,'Duplicate Daily identity/timestamp. Resolve the duplicate rows before reducing.',1;
            UPDATE {target} WITH (UPDLOCK,HOLDLOCK) SET {string.Join(separator: ",", values: assignments)} WHERE {predicate};
            IF @@ROWCOUNT=0 INSERT INTO {target} ({string.Join(separator: ",", values: columns)}) VALUES ({string.Join(separator: ",", values: values)});
            """);
        Add(command: command, name: "@time", type: SqlDbType.DateTime2, value: null);
        Add(command: command, name: "@count", type: SqlDbType.Int, value: null);
        for (int index = 0; index < plan.Keys.Length; index++) Add(command: command, name: "@k" + index, type: SqlDbType.Int, value: null);
        for (int index = 0; index < plan.Measurements.Count; index++)
        {
            ReductionColumn source = plan.Measurements[index];
            ReductionColumn? targetColumn = plan.TargetColumns.Find(match: c => c.Name == source.Name);
            SqlParameter parameter = command.Parameters.Add(parameterName: "@v" + index, sqlDbType: SqlDbType.Decimal);
            parameter.Precision = targetColumn?.Precision ?? (source.Type is "decimal" or "numeric" ? source.Precision : (byte)18);
            parameter.Scale = targetColumn?.Scale ?? (source.Type is "decimal" or "numeric" ? source.Scale : (byte)6);
        }
        if (plan.Rule.Identity == "Sensor") command.Parameters.Add(parameterName: "@replacement", sqlDbType: SqlDbType.NVarChar, size: 50);
        return command;
    }
    private static async Task WriteDailyAsync(SqlCommand command, ReductionPlan plan, ReductionEntity entity, ReductionDay day,
        ReductionResult[] results, CancellationToken cancellationToken)
    {
        command.Parameters["@time"].Value = day.NoonUtc;
        command.Parameters["@count"].Value = results[0].OriginalCount == 0 ? DBNull.Value : results[0].OriginalCount;
        for (int index = 0; index < entity.Ids.Length; index++) command.Parameters["@k" + index].Value = entity.Ids[index];
        for (int index = 0; index < results.Length; index++)
        {
            SqlParameter parameter = command.Parameters["@v" + index];
            parameter.Value = results[index].DailyMean.HasValue
                ? Math.Round(d: results[index].DailyMean!.Value, decimals: parameter.Scale, mode: MidpointRounding.AwayFromZero) : DBNull.Value;
        }
        if (plan.Rule.Identity == "Sensor") command.Parameters["@replacement"].Value = entity.ReplacementName;
        await command.ExecuteNonQueryAsync(cancellationToken: cancellationToken);
    }
    private SqlCommand CreateStatisticsCommand(SqlConnection connection, SqlTransaction transaction)
    {
        string[] keys = ["Project_ID", "SourceTable", "LocalDate", "EntityKey"];
        string[] fields = ["PointName_ID", "PrismPair_ID", "Array_ID", "SensorID", "UTCtime", "MeasurementField",
            "OriginalObservationCount", "RetainedObservationCount", "RejectedObservationCount", "OriginalMean", "DailyMean",
            "AcceptedTrimmingPasses", "Performance", "ComparisonTolerance"];
        string predicate = string.Join(separator: " AND ", values: keys.Select(selector: k => Q(name: k) + "=@" + k));
        string assignments = string.Join(separator: ",", values: fields.Select(selector: k => Q(name: k) + "=@" + k));
        string[] all = [.. keys, .. fields];
        SqlCommand command = Command(connection: connection, transaction: transaction, sql: $"""
            UPDATE {StatisticsTable} WITH (UPDLOCK,HOLDLOCK) SET {assignments} WHERE {predicate};
            IF @@ROWCOUNT=0 INSERT INTO {StatisticsTable} ({string.Join(separator: ",", values: all.Select(selector: Q))})
            VALUES ({string.Join(separator: ",", values: all.Select(selector: k => "@" + k))});
            """);
        foreach (string name in all)
        {
            SqlDbType type = name switch
            {
                "Project_ID" or "PointName_ID" or "PrismPair_ID" or "Array_ID" or "SensorID" or "OriginalObservationCount" or "RetainedObservationCount" or "RejectedObservationCount" or "AcceptedTrimmingPasses" => SqlDbType.Int,
                "LocalDate" => SqlDbType.Date,
                "UTCtime" => SqlDbType.DateTime2,
                "OriginalMean" or "DailyMean" or "ComparisonTolerance" => SqlDbType.Decimal,
                _ => SqlDbType.NVarChar
            };
            SqlParameter parameter = command.Parameters.Add(parameterName: "@" + name, sqlDbType: type);
            if (type == SqlDbType.Decimal) { parameter.Precision = 28; parameter.Scale = name == "ComparisonTolerance" ? (byte)16 : (byte)10; }
            if (type == SqlDbType.NVarChar) parameter.Size = name switch { "EntityKey" => 80, "Performance" => 4, _ => 128 };
        }
        return command;
    }
    private async Task WriteStatisticsAsync(SqlCommand command, ReductionPlan plan, ReductionEntity entity, ReductionRequest request,
        ReductionDay day, ReductionResult result, CancellationToken cancellationToken)
    {
        command.Parameters["@Project_ID"].Value = request.ProjectId;
        command.Parameters["@SourceTable"].Value = plan.Source;
        command.Parameters["@LocalDate"].Value = day.LocalDate;
        command.Parameters["@EntityKey"].Value = entity.Key;
        foreach (string name in new[] { "PointName_ID", "PrismPair_ID", "Array_ID", "SensorID" }) command.Parameters["@" + name].Value = DBNull.Value;
        for (int index = 0; index < plan.Keys.Length; index++) command.Parameters["@" + plan.Keys[index]].Value = entity.Ids[index];
        command.Parameters["@UTCtime"].Value = day.NoonUtc;
        command.Parameters["@MeasurementField"].Value = plan.Measurements[0].Name;
        command.Parameters["@OriginalObservationCount"].Value = result.OriginalCount;
        command.Parameters["@RetainedObservationCount"].Value = result.RetainedCount;
        command.Parameters["@RejectedObservationCount"].Value = result.RejectedCount;
        command.Parameters["@OriginalMean"].Value = (object?)result.OriginalMean ?? DBNull.Value;
        command.Parameters["@DailyMean"].Value = (object?)result.DailyMean ?? DBNull.Value;
        command.Parameters["@AcceptedTrimmingPasses"].Value = result.AcceptedPasses;
        command.Parameters["@Performance"].Value = result.Performance;
        command.Parameters["@ComparisonTolerance"].Value = _options.ComparisonTolerance;
        await command.ExecuteNonQueryAsync(cancellationToken: cancellationToken);
    }
}
#endregion



