namespace GNA_DBDayReductions;
#region Statistics Schema Upgrade
public sealed partial class DailyReductionService
{
    // Runs inside the existing schema-preparation transaction, under the reducer application lock.
    // Dynamic SQL permits the same batch to run before and after the legacy column is removed.
    private const string StatisticsUpgradeDdl = """
        IF COL_LENGTH(N'dbo.DailyReductionStatistics',N'SourceSchema') IS NOT NULL
        BEGIN
            EXEC sys.sp_executesql N'
                IF EXISTS (SELECT 1 FROM dbo.DailyReductionStatistics WHERE SourceSchema IS NULL OR SourceSchema<>N''dbo'')
                    THROW 51003,''Cannot remove SourceSchema: statistics contain a source schema other than dbo.'',1;
                IF EXISTS (SELECT 1 FROM dbo.DailyReductionStatistics
                           GROUP BY Project_ID,SourceTable,LocalDate,EntityKey HAVING COUNT_BIG(*)>1)
                    THROW 51004,''Cannot remove SourceSchema: duplicate statistics keys require review.'',1;';
            DECLARE @constraint sysname;
            SELECT @constraint=name FROM sys.key_constraints
            WHERE parent_object_id=OBJECT_ID(N'dbo.DailyReductionStatistics') AND type=N'PK';
            IF @constraint IS NULL
                THROW 51005,'Cannot upgrade statistics: the expected primary key is missing.',1;
            DECLARE @alter nvarchar(max)=N'ALTER TABLE dbo.DailyReductionStatistics DROP CONSTRAINT '+QUOTENAME(@constraint)+N';
                ALTER TABLE dbo.DailyReductionStatistics DROP COLUMN SourceSchema;
                ALTER TABLE dbo.DailyReductionStatistics ADD CONSTRAINT '+QUOTENAME(@constraint)+N'
                PRIMARY KEY (Project_ID,SourceTable,LocalDate,EntityKey);';
            EXEC sys.sp_executesql @alter;
        END;
        IF COL_LENGTH(N'dbo.DailyReductionStatistics',N'TimeZoneId') IS NOT NULL
            EXEC sys.sp_executesql N'ALTER TABLE dbo.DailyReductionStatistics DROP COLUMN TimeZoneId;';
        IF COL_LENGTH(N'dbo.DailyReductionStatistics',N'AlgorithmVersion') IS NOT NULL
            EXEC sys.sp_executesql N'ALTER TABLE dbo.DailyReductionStatistics DROP COLUMN AlgorithmVersion;';
        IF COL_LENGTH(N'dbo.DailyReductionStatistics',N'ComputedAtUTC') IS NOT NULL
            EXEC sys.sp_executesql N'ALTER TABLE dbo.DailyReductionStatistics DROP COLUMN ComputedAtUTC;';
        """;
}
#endregion


