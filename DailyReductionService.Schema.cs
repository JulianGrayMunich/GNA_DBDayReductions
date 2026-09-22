#region Imports
using System.Data;
using Microsoft.Data.SqlClient;
#endregion
namespace GNA_DBDayReductions;
#region Schema Discovery and Preparation
public sealed partial class DailyReductionService
{
    private async Task<List<ReductionColumn>> ColumnsAsync(SqlConnection connection, string schema, string table, CancellationToken cancellationToken)
    {
        using SqlCommand command = Command(connection: connection, transaction: null, sql: """
            SELECT c.name,TYPE_NAME(c.system_type_id),c.precision,c.scale,c.is_nullable,c.is_identity,c.is_computed
            FROM sys.columns c JOIN sys.tables t ON t.object_id=c.object_id JOIN sys.schemas s ON s.schema_id=t.schema_id
            WHERE s.name=@schema AND t.name=@table ORDER BY c.column_id;
            """);
        Add(command: command, name: "@schema", type: SqlDbType.NVarChar, value: schema);
        Add(command: command, name: "@table", type: SqlDbType.NVarChar, value: table);
        List<ReductionColumn> columns = new();
        using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken: cancellationToken);
        while (await reader.ReadAsync(cancellationToken: cancellationToken))
            columns.Add(item: new(Name: reader.GetString(i: 0), Type: reader.GetString(i: 1), Precision: reader.GetByte(i: 2),
                Scale: reader.GetByte(i: 3), Nullable: reader.GetBoolean(i: 4), Identity: reader.GetBoolean(i: 5), Computed: reader.GetBoolean(i: 6)));
        return columns;
    }
    private async Task<List<ReductionPlan>> DiscoverAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        List<(string Schema, string Name)> names = new();
        using (SqlCommand command = Command(connection: connection, transaction: null,
            sql: "SELECT s.name,t.name FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id WHERE RIGHT(t.name,6)=N'Epochs' AND t.is_ms_shipped=0 ORDER BY s.name,t.name;"))
        using (SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken: cancellationToken))
            while (await reader.ReadAsync(cancellationToken: cancellationToken)) names.Add(item: (reader.GetString(i: 0), reader.GetString(i: 1)));
        if (names.Count == 0) throw new InvalidOperationException(message: "No Epochs tables were found.");
        List<ReductionPlan> plans = new();
        foreach ((string schema, string source) in names)
        {
            if (!_options.Tables.TryGetValue(key: schema + "." + source, value: out ReductionTableRule? rule))
                throw new InvalidOperationException(message: $"No project-ownership rule is configured for {schema}.{source}. No reductions have been written.");
            string[] keys = rule.Identity switch
            {
                "Point" => ["PointName_ID"], "Pair" => ["PrismPair_ID"], "Array" => ["Array_ID"],
                "ArrayPoint" => ["Array_ID", "PointName_ID"], "Sensor" => ["SensorID"],
                _ => throw new InvalidOperationException(message: $"Invalid identity rule for {source}.")
            };
            if ((rule.Identity is "Array" or "ArrayPoint") && !rule.ArrayType.HasValue
                || rule.Identity == "Sensor" && string.IsNullOrWhiteSpace(value: rule.SensorType))
                throw new InvalidOperationException(message: $"Missing registration type for {source}.");
            string target = source[..^6] + "Daily";
            List<ReductionColumn> sourceColumns = await ColumnsAsync(connection: connection, schema: schema, table: source, cancellationToken: cancellationToken);
            List<ReductionColumn> targetColumns = await ColumnsAsync(connection: connection, schema: schema, table: target, cancellationToken: cancellationToken);
            foreach (string required in keys.Concat(second: ["UTCtime", "IsDeleted"]))
                if (!sourceColumns.Exists(match: c => c.Name == required)) throw new InvalidOperationException(message: $"{source} lacks {required}.");
            List<ReductionColumn> measurements = new();
            foreach (ReductionColumn column in sourceColumns)
            {
                if (column.Identity || keys.Contains(value: column.Name) || column.Name is "UTCtime" or "IsDeleted" or "LatestReading" or "ReadingCount" or "ReplacementName" or "NoOfObservations") continue;
                if (column.Computed || column.Type is not ("decimal" or "numeric" or "int" or "smallint" or "tinyint" or "bigint") || column.Precision > 18)
                    throw new InvalidOperationException(message: $"Unsupported measurement {source}.{column.Name} ({column.Type}); review the mapping before reducing.");
                measurements.Add(item: column);
            }
            if (measurements.Count == 0) throw new InvalidOperationException(message: $"No measurement fields found in {source}.");
            if (targetColumns.Count > 0)
            {
                foreach (string required in keys.Concat(second: ["UTCtime", "IsDeleted"]))
                    if (!targetColumns.Exists(match: c => c.Name == required)) throw new InvalidOperationException(message: $"{target} lacks {required}.");
                foreach (ReductionColumn column in targetColumns)
                {
                    bool measurement = measurements.Exists(match: m => m.Name == column.Name);
                    if (measurement && (column.Computed || column.Identity || column.Type is not ("decimal" or "numeric") || column.Precision > 18))
                        throw new InvalidOperationException(message: $"Unsupported Daily measurement definition: {target}.{column.Name}.");
                    bool known = column.Identity || column.Computed || measurement || keys.Contains(value: column.Name)
                        || column.Name is "UTCtime" or "IsDeleted" or "NoOfObservations" || rule.Identity == "Sensor" && column.Name == "ReplacementName";
                    if (!known) throw new InvalidOperationException(message: $"Unmapped Daily column {target}.{column.Name}. Review the schema before reducing.");
                }
            }
            plans.Add(item: new(Schema: schema, Source: source, Target: target, Rule: rule, Keys: keys, Measurements: measurements, TargetColumns: targetColumns));
        }
        return plans;
    }
    private static string MeasurementType(ReductionColumn source)
        => source.Type is "decimal" or "numeric" ? $"decimal({source.Precision},{source.Scale})" : "decimal(18,6)";
    private async Task PrepareTargetAsync(SqlConnection connection, SqlTransaction transaction, ReductionPlan plan, CancellationToken cancellationToken)
    {
        string target = Table(schema: plan.Schema, name: plan.Target);
        if (plan.TargetColumns.Count == 0)
        {
            List<string> definitions = new();
            foreach (string key in plan.Keys) definitions.Add(item: Q(name: key) + " int NOT NULL");
            definitions.Add(item: "[UTCtime] datetime2(0) NOT NULL");
            foreach (ReductionColumn column in plan.Measurements) definitions.Add(item: Q(name: column.Name) + " " + MeasurementType(source: column) + " NULL");
            definitions.Add(item: "[IsDeleted] bit NOT NULL DEFAULT(0)");
            definitions.Add(item: "[NoOfObservations] int NULL");
            if (plan.Rule.Identity == "Sensor") definitions.Add(item: "[ReplacementName] nvarchar(50) NOT NULL");
            string keyList = string.Join(separator: ",", values: plan.Keys.Select(selector: Q));
            definitions.Add(item: "PRIMARY KEY (" + keyList + ",[UTCtime])");
            string owner = plan.Rule.Identity switch { "Point" => "PointName", "Pair" => "PrismPairs", "Array" => "PrismArray", "ArrayPoint" => "PrismArrayPoint", _ => "GeotecSensors" };
            definitions.Add(item: $"FOREIGN KEY ({keyList}) REFERENCES [dbo].{Q(name: owner)} ({keyList})");
            await ExecuteAsync(connection: connection, transaction: transaction, sql: $"CREATE TABLE {target} ({string.Join(separator: ",", values: definitions)});", cancellationToken: cancellationToken);
        }
        else
        {
            foreach (ReductionColumn source in plan.Measurements)
            {
                ReductionColumn? existing = plan.TargetColumns.Find(match: c => c.Name == source.Name);
                if (existing is null)
                    await ExecuteAsync(connection: connection, transaction: transaction, sql: $"ALTER TABLE {target} ADD {Q(name: source.Name)} {MeasurementType(source: source)} NULL;", cancellationToken: cancellationToken);
                else if (!existing.Nullable)
                    await ExecuteAsync(connection: connection, transaction: transaction, sql: $"ALTER TABLE {target} ALTER COLUMN {Q(name: existing.Name)} decimal({existing.Precision},{existing.Scale}) NULL;", cancellationToken: cancellationToken);
            }
            if (!plan.TargetColumns.Exists(match: c => c.Name == "NoOfObservations"))
                await ExecuteAsync(connection: connection, transaction: transaction, sql: $"ALTER TABLE {target} ADD [NoOfObservations] int NULL;", cancellationToken: cancellationToken);
        }
    }
    private const string StatisticsDdl = """
        IF OBJECT_ID(N'dbo.DailyReductionStatistics',N'U') IS NULL
        CREATE TABLE dbo.DailyReductionStatistics (
          Project_ID int NOT NULL, SourceSchema nvarchar(128) NOT NULL, SourceTable nvarchar(128) NOT NULL,
          LocalDate date NOT NULL, EntityKey nvarchar(80) NOT NULL,
          PointName_ID int NULL, PrismPair_ID int NULL, Array_ID int NULL, SensorID int NULL,
          UTCtime datetime2(0) NOT NULL, TimeZoneId nvarchar(200) NOT NULL, MeasurementField nvarchar(128) NOT NULL,
          OriginalObservationCount int NOT NULL, RetainedObservationCount int NOT NULL, RejectedObservationCount int NOT NULL,
          OriginalMean decimal(28,10) NULL, DailyMean decimal(28,10) NULL, AcceptedTrimmingPasses int NOT NULL,
          Performance nvarchar(4) NOT NULL CHECK (Performance IN (N'Pass',N'Fail')),
          AlgorithmVersion nvarchar(40) NOT NULL, ComparisonTolerance decimal(28,16) NOT NULL, ComputedAtUTC datetime2(7) NOT NULL,
          PRIMARY KEY (Project_ID,SourceSchema,SourceTable,LocalDate,EntityKey),
          FOREIGN KEY (Project_ID) REFERENCES dbo.Project(Project_ID),
          CHECK (OriginalObservationCount=RetainedObservationCount+RejectedObservationCount),
          CHECK ((OriginalObservationCount=0 AND Performance=N'Fail') OR (OriginalObservationCount>0 AND Performance=N'Pass'))
        );
        """;
}
#endregion
