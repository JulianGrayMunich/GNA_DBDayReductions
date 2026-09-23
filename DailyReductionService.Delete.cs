#region Imports
using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
#endregion
namespace GNA_DBDayReductions;
#region Transactional Historic Deletion
public sealed record DeletionSummary(int Tables, long DailyRows, long StatisticsRows);
public sealed partial class DailyReductionService
{
    public async Task<DeletionSummary> DeleteAsync(string connectionString, ReductionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(argument: request);
        TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById(id: request.TimeZoneId);
        DateTime today = TimeZoneInfo.ConvertTimeFromUtc(dateTime: DateTime.UtcNow, destinationTimeZone: zone).Date;
        if (request.StartDate.Date > request.EndDate.Date || request.EndDate.Date >= today)
            throw new InvalidOperationException(message: "Select an inclusive range of completed project-local days.");
        DateTime startUtc = ReductionDay.Create(localDate: request.StartDate, zone: zone).StartUtc;
        DateTime endUtc = ReductionDay.Create(localDate: request.EndDate, zone: zone).EndUtc;
        SqlConnectionStringBuilder builder = new(connectionString: connectionString) { Pooling = false };
        using SqlConnection connection = new(connectionString: builder.ConnectionString);
        await connection.OpenAsync(cancellationToken: cancellationToken);
        // The reducer holds the same lock, including while awaiting operator review.
        using (SqlCommand acquire = Command(connection: connection, transaction: null,
            sql: "DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource=@resource,@LockMode='Exclusive',@LockOwner='Session',@LockTimeout=0; SELECT @r;"))
        {
            Add(command: acquire, name: "@resource", type: SqlDbType.NVarChar, value: LockResource);
            if (Convert.ToInt32(value: await acquire.ExecuteScalarAsync(cancellationToken: cancellationToken), provider: CultureInfo.InvariantCulture) < 0)
                throw new InvalidOperationException(message: "Another reduction or deletion is running in this database.");
        }
        using SqlTransaction transaction = connection.BeginTransaction(iso: IsolationLevel.Serializable);
        using (SqlCommand project = Command(connection: connection, transaction: transaction,
            sql: "SELECT COUNT(*) FROM dbo.Project WHERE Project_ID=@project AND ProjectName=@name AND TimeZoneId=@zone AND IsDeleted=0;"))
        {
            Add(command: project, name: "@project", type: SqlDbType.Int, value: request.ProjectId);
            Add(command: project, name: "@name", type: SqlDbType.NVarChar, value: request.ProjectName);
            Add(command: project, name: "@zone", type: SqlDbType.NVarChar, value: request.TimeZoneId);
            if (Convert.ToInt32(value: await project.ExecuteScalarAsync(cancellationToken: cancellationToken), provider: CultureInfo.InvariantCulture) != 1)
                throw new InvalidOperationException(message: "The selected project or time zone has changed. Retest the connection.");
        }
        List<(string Schema, string Name)> tables = new();
        using (SqlCommand discover = Command(connection: connection, transaction: transaction,
            sql: "SELECT s.name,t.name FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id WHERE RIGHT(t.name,5)=N'Daily' AND t.is_ms_shipped=0 ORDER BY s.name,t.name;"))
        using (SqlDataReader reader = await discover.ExecuteReaderAsync(cancellationToken: cancellationToken))
            while (await reader.ReadAsync(cancellationToken: cancellationToken)) tables.Add(item: (reader.GetString(i: 0), reader.GetString(i: 1)));
        long dailyRows = 0;
        foreach ((string schema, string name) in tables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (schema != "dbo" || !_options.Tables.TryGetValue(key: schema + "." + name[..^5] + "Epochs", value: out ReductionTableRule? rule))
                throw new InvalidOperationException(message: $"No project-ownership rule for {schema}.{name}. All deletions are rolled back.");
            // Ownership deliberately includes retired registrations and soft-deleted rows.
            string ownership = rule.Identity switch
            {
                "Point" => "SELECT 1 FROM dbo.PointName p WHERE p.PointName_ID=d.PointName_ID AND p.Project_ID=@project",
                "Pair" => "SELECT 1 FROM dbo.PrismPairs p JOIN dbo.Track t ON t.Track_ID=p.Track_ID WHERE p.PrismPair_ID=d.PrismPair_ID AND t.Project_ID=@project",
                "Array" or "ArrayPoint" => "SELECT 1 FROM dbo.PrismArray a WHERE a.Array_ID=d.Array_ID AND a.Project_ID=@project",
                "Sensor" => "SELECT 1 FROM dbo.GeotecSensors s WHERE s.SensorID=d.SensorID AND s.Project_ID=@project",
                _ => throw new InvalidOperationException(message: $"Unknown project-ownership rule for {name}. All deletions are rolled back.")
            };
            using SqlCommand delete = Command(connection: connection, transaction: transaction,
                sql: $"DELETE d FROM {Table(schema: schema, name: name)} d WHERE d.UTCtime>=@start AND d.UTCtime<@end AND EXISTS ({ownership}); SELECT CONVERT(bigint,@@ROWCOUNT);");
            Add(command: delete, name: "@project", type: SqlDbType.Int, value: request.ProjectId);
            Add(command: delete, name: "@start", type: SqlDbType.DateTime2, value: startUtc);
            Add(command: delete, name: "@end", type: SqlDbType.DateTime2, value: endUtc);
            dailyRows += Convert.ToInt64(value: await delete.ExecuteScalarAsync(cancellationToken: cancellationToken), provider: CultureInfo.InvariantCulture);
        }
        using SqlCommand statistics = Command(connection: connection, transaction: transaction, sql: $"""
            IF OBJECT_ID(N'dbo.DailyReductionStatistics',N'U') IS NOT NULL
            BEGIN
              DELETE FROM {StatisticsTable} WHERE Project_ID=@project AND LocalDate>=@start AND LocalDate<=@end;
              SELECT CONVERT(bigint,@@ROWCOUNT);
            END
            ELSE SELECT CONVERT(bigint,0);
            """);
        Add(command: statistics, name: "@project", type: SqlDbType.Int, value: request.ProjectId);
        Add(command: statistics, name: "@start", type: SqlDbType.Date, value: request.StartDate.Date);
        Add(command: statistics, name: "@end", type: SqlDbType.Date, value: request.EndDate.Date);
        long statisticsRows = Convert.ToInt64(value: await statistics.ExecuteScalarAsync(cancellationToken: cancellationToken), provider: CultureInfo.InvariantCulture);
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        return new(Tables: tables.Count, DailyRows: dailyRows, StatisticsRows: statisticsRows);
    }
}
#endregion
