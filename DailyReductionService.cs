#region Imports
using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
#endregion
namespace GNA_DBDayReductions;
#region Configuration and Schema Models
public sealed class ReductionOptions
{
    public decimal ComparisonTolerance { get; set; } = 0.000000000001m;
    public int CommandTimeoutSeconds { get; set; } = 120;
    public Dictionary<string, ReductionTableRule> Tables { get; set; } = new();
}
public sealed class ReductionTableRule
{
    public string Identity { get; set; } = string.Empty;
    public int? ArrayType { get; set; }
    public string? SensorType { get; set; }
}
public sealed record ReductionRequest(int ProjectId, string ProjectName, string TimeZoneId, DateTime StartDate, DateTime EndDate);
public sealed record ReductionSummary(int Tables, int Rows, int Pass, int Fail);
internal sealed record ReductionColumn(string Name, string Type, byte Precision, byte Scale, bool Nullable, bool Identity, bool Computed);
internal sealed record ReductionPlan(string Schema, string Source, string Target, ReductionTableRule Rule,
    string[] Keys, List<ReductionColumn> Measurements, List<ReductionColumn> TargetColumns);
internal sealed record ReductionEntity(int[] Ids, string ReplacementName)
{
    public string Key => string.Join(separator: "/", values: Ids);
}
#endregion
#region SQL Reduction Service
public sealed partial class DailyReductionService
{
    #region SQL Helpers
    private const string LockResource = "GNA_DBDayReductions:DailyReduction";
    private const string StatisticsTable = "[dbo].[DailyReductionStatistics]";
    private const string AlgorithmVersion = "TrimExtremes2SE-v1";
    private readonly ReductionOptions _options;
    public DailyReductionService(ReductionOptions options)
    {
        _options = options ?? throw new ArgumentNullException(paramName: nameof(options));
        if (_options.ComparisonTolerance < 0 || _options.CommandTimeoutSeconds is < 1 or > 3600)
            throw new InvalidOperationException(message: "Invalid reduction options in appsettings.json.");
    }
    private SqlCommand Command(SqlConnection connection, SqlTransaction? transaction, string sql)
        => new(cmdText: sql, connection: connection, transaction: transaction) { CommandTimeout = _options.CommandTimeoutSeconds };
    private static string Q(string name) => "[" + name.Replace(oldValue: "]", newValue: "]]") + "]";
    private static string Table(string schema, string name) => Q(name: schema) + "." + Q(name: name);
    private static void Add(SqlCommand command, string name, SqlDbType type, object? value)
        => command.Parameters.Add(parameterName: name, sqlDbType: type).Value = value ?? DBNull.Value;
    private async Task ExecuteAsync(SqlConnection connection, SqlTransaction? transaction, string sql, CancellationToken cancellationToken)
    {
        using SqlCommand command = Command(connection: connection, transaction: transaction, sql: sql);
        await command.ExecuteNonQueryAsync(cancellationToken: cancellationToken);
    }
    #endregion
    #region Run and Commit Boundaries
    public async Task<ReductionSummary> RunAsync(string connectionString, ReductionRequest request,
        IProgress<string>? progress, CancellationToken cancellationToken, Func<ReductionReview, CancellationToken, Task>? review = null)
    {
        TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById(id: request.TimeZoneId);
        DateTime today = TimeZoneInfo.ConvertTimeFromUtc(dateTime: DateTime.UtcNow, destinationTimeZone: zone).Date;
        if (request.StartDate.Date > request.EndDate.Date || request.EndDate.Date >= today)
            throw new InvalidOperationException(message: "Select an inclusive range of completed project-local days.");
        List<ReductionDay> days = new();
        for (DateTime date = request.StartDate.Date; date <= request.EndDate.Date; date = date.AddDays(value: 1))
            days.Add(item: ReductionDay.Create(localDate: date, zone: zone));
        SqlConnectionStringBuilder builder = new(connectionString: connectionString) { Pooling = false };
        using SqlConnection connection = new(connectionString: builder.ConnectionString);
        await connection.OpenAsync(cancellationToken: cancellationToken);
        using (SqlCommand acquire = Command(connection: connection, transaction: null, sql: "DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource=@resource,@LockMode='Exclusive',@LockOwner='Session',@LockTimeout=0; SELECT @r;"))
        {
            Add(command: acquire, name: "@resource", type: SqlDbType.NVarChar, value: LockResource);
            int result = Convert.ToInt32(value: await acquire.ExecuteScalarAsync(cancellationToken: cancellationToken), provider: CultureInfo.InvariantCulture);
            if (result < 0) throw new InvalidOperationException(message: "Another daily reduction is running in this database.");
        }
        // Non-pooled connection disposal releases the session lock on every exit path.
        List<ReductionPlan> plans = await DiscoverAsync(connection: connection, cancellationToken: cancellationToken);
        using (SqlTransaction preparation = connection.BeginTransaction(iso: IsolationLevel.Serializable))
        {
            await ValidateProjectAsync(connection: connection, transaction: preparation, request: request, cancellationToken: cancellationToken);
            foreach (ReductionPlan plan in plans)
                await ValidateOwnershipAsync(connection: connection, transaction: preparation, plan: plan, projectId: request.ProjectId, cancellationToken: cancellationToken);
            await ExecuteAsync(connection: connection, transaction: preparation, sql: StatisticsDdl, cancellationToken: cancellationToken);
            foreach (ReductionPlan plan in plans)
                await PrepareTargetAsync(connection: connection, transaction: preparation, plan: plan, cancellationToken: cancellationToken);
            preparation.Commit();
        }
        int completed = 0, rowCount = 0, passes = 0, failures = 0;
        foreach (ReductionPlan plan in plans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(value: $"Reducing {plan.Schema}.{plan.Source}...");
            ReviewSnapshot? snapshot = null;
            if (review is not null)
            {
                snapshot = await PrepareReviewAsync(connection: connection, plan: plan, request: request, days: days, cancellationToken: cancellationToken);
                progress?.Report(value: $"Review {plan.Source}: {snapshot.Review.EpochReadings.Rows.Count} extracted readings. Awaiting Commit and Next Table.");
                await review(arg1: snapshot.Review, arg2: cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
            }
            DataTable? checkedReadings = snapshot is null ? null : new DataTable();
            int tableRows = 0, tablePasses = 0, tableFailures = 0;
            using SqlTransaction transaction = connection.BeginTransaction(iso: IsolationLevel.Serializable);
            try
            {
                await ValidateProjectAsync(connection: connection, transaction: transaction, request: request, cancellationToken: cancellationToken);
                await ValidateOwnershipAsync(connection: connection, transaction: transaction, plan: plan, projectId: request.ProjectId, cancellationToken: cancellationToken);
                List<ReductionEntity> entities = await ReadEntitiesAsync(connection: connection, transaction: transaction,
                    plan: plan, projectId: request.ProjectId, cancellationToken: cancellationToken);
                if (snapshot is not null) VerifyReviewedEntities(expected: snapshot.Entities, actual: entities);
                using SqlCommand daily = CreateDailyCommand(connection: connection, transaction: transaction, plan: plan);
                using SqlCommand statistics = CreateStatisticsCommand(connection: connection, transaction: transaction);
                foreach (ReductionDay day in days)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Dictionary<string, List<decimal?>[]> readings = await ReadDayAsync(connection: connection, transaction: transaction,
                        plan: plan, projectId: request.ProjectId, entities: entities, day: day, cancellationToken: cancellationToken, extracted: checkedReadings);
                    foreach (ReductionEntity entity in entities)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        List<decimal?>[] fields = readings[entity.Key];
                        ReductionResult[] results = new ReductionResult[fields.Length];
                        for (int index = 0; index < fields.Length; index++)
                            results[index] = ReductionMath.Reduce(observations: fields[index], comparisonTolerance: _options.ComparisonTolerance);
                        await WriteDailyAsync(command: daily, plan: plan, entity: entity, day: day, results: results, cancellationToken: cancellationToken);
                        await WriteStatisticsAsync(command: statistics, plan: plan, entity: entity, request: request, day: day,
                            result: results[0], cancellationToken: cancellationToken);
                        tableRows++;
                        if (results[0].OriginalCount > 0) tablePasses++; else tableFailures++;
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (snapshot is not null) VerifyReviewedReadings(expected: snapshot.Review.EpochReadings, actual: checkedReadings!);
                transaction.Commit();
                completed++; rowCount += tableRows; passes += tablePasses; failures += tableFailures;
                progress?.Report(value: $"Committed {plan.Source}: {tableRows} daily/statistics rows; Pass {tablePasses}, Fail {tableFailures}.");
            }
            catch
            {
                progress?.Report(value: $"{plan.Source} did not complete and is rolled back. Previously committed tables: {completed}. Rerunning is safe.");
                throw;
            }
        }
        return new(Tables: completed, Rows: rowCount, Pass: passes, Fail: failures);
    }
    private async Task ValidateProjectAsync(SqlConnection connection, SqlTransaction transaction, ReductionRequest request, CancellationToken cancellationToken)
    {
        using SqlCommand command = Command(connection: connection, transaction: transaction,
            sql: "SELECT [ProjectName],[TimeZoneId],[ProjectStartDate] FROM [dbo].[Project] WHERE [Project_ID]=@project AND [IsDeleted]=0;");
        Add(command: command, name: "@project", type: SqlDbType.Int, value: request.ProjectId);
        using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken: cancellationToken);
        if (!await reader.ReadAsync(cancellationToken: cancellationToken)
            || reader.GetString(i: 0) != request.ProjectName || reader.IsDBNull(i: 1) || reader.GetString(i: 1) != request.TimeZoneId)
            throw new InvalidOperationException(message: "The active project or time zone has changed. Retest the database connection.");
        if (request.StartDate.Date < reader.GetDateTime(i: 2).Date)
            throw new InvalidOperationException(message: "The selected range begins before ProjectStartDate.");
    }
    #endregion
}
#endregion

