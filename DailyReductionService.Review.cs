#region Imports
using System.Data;
using Microsoft.Data.SqlClient;
#endregion
namespace GNA_DBDayReductions;
#region Table Review Models
public sealed record ReductionReview(string SourceTable, string TargetTable, DataTable EpochReadings, DataTable Passes, DataTable DailyResults, IReadOnlyList<ReductionDay> Days);
internal sealed record ReviewSnapshot(ReductionReview Review, List<ReductionEntity> Entities);
#endregion
#region Table Review Preparation
public sealed partial class DailyReductionService
{
    private async Task<ReviewSnapshot> PrepareReviewAsync(SqlConnection connection, ReductionPlan plan, ReductionRequest request,
        List<ReductionDay> days, CancellationToken cancellationToken)
    {
        // Finish the read transaction before waiting for a person. Revalidate the same inputs before committing.
        using SqlTransaction transaction = connection.BeginTransaction(iso: IsolationLevel.Serializable);
        await ValidateProjectAsync(connection: connection, transaction: transaction, request: request, cancellationToken: cancellationToken);
        await ValidateOwnershipAsync(connection: connection, transaction: transaction, plan: plan, projectId: request.ProjectId, cancellationToken: cancellationToken);
        List<ReductionEntity> entities = await ReadEntitiesAsync(connection: connection, transaction: transaction, plan: plan, projectId: request.ProjectId, cancellationToken: cancellationToken);
        DataTable raw = new(tableName: "Epoch readings");
        DataTable passes = new(tableName: "Reduction passes");
        DataTable final = new(tableName: "Daily results");
        foreach (DataTable table in new[] { passes, final })
        {
            table.Columns.Add(columnName: "LocalDate", type: typeof(DateTime));
            foreach (string key in plan.Keys) table.Columns.Add(columnName: key, type: typeof(int));
        }
        passes.Columns.Add(columnName: "Measurement", type: typeof(string));
        passes.Columns.Add(columnName: "DrivesStatistics", type: typeof(bool));
        System.Reflection.PropertyInfo[] properties = typeof(ReductionPass).GetProperties();
        foreach (System.Reflection.PropertyInfo property in properties)
            passes.Columns.Add(columnName: property.Name, type: Nullable.GetUnderlyingType(nullableType: property.PropertyType) ?? property.PropertyType);
        final.Columns.Add(columnName: "UTCtime", type: typeof(DateTime));
        foreach (ReductionColumn column in plan.Measurements) final.Columns.Add(columnName: column.Name, type: typeof(decimal));
        final.Columns.Add(columnName: "NoOfObservations", type: typeof(int));
        final.Columns.Add(columnName: "StatisticsField", type: typeof(string));
        final.Columns.Add(columnName: "OriginalMean", type: typeof(decimal));
        final.Columns.Add(columnName: "MeanDifference", type: typeof(decimal));
        final.Columns.Add(columnName: "RetainedCount", type: typeof(int));
        final.Columns.Add(columnName: "RejectedCount", type: typeof(int));
        final.Columns.Add(columnName: "AcceptedPasses", type: typeof(int));
        final.Columns.Add(columnName: "Performance", type: typeof(string));
        foreach (ReductionDay day in days)
        {
            Dictionary<string, List<decimal?>[]> readings = await ReadDayAsync(connection: connection, transaction: transaction, plan: plan,
                projectId: request.ProjectId, entities: entities, day: day, cancellationToken: cancellationToken, extracted: raw);
            foreach (ReductionEntity entity in entities)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DataRow resultRow = final.NewRow(); resultRow["LocalDate"] = day.LocalDate; resultRow["UTCtime"] = day.NoonUtc;
                for (int key = 0; key < plan.Keys.Length; key++) resultRow[plan.Keys[key]] = entity.Ids[key];
                for (int field = 0; field < plan.Measurements.Count; field++)
                {
                    ReductionColumn column = plan.Measurements[field];
                    List<ReductionPass> trace = new();
                    ReductionResult result = ReductionMath.Reduce(observations: readings[entity.Key][field], comparisonTolerance: _options.ComparisonTolerance, trace: trace);
                    foreach (ReductionPass pass in trace)
                    {
                        DataRow row = passes.NewRow(); row["LocalDate"] = day.LocalDate;
                        for (int key = 0; key < plan.Keys.Length; key++) row[plan.Keys[key]] = entity.Ids[key];
                        row["Measurement"] = column.Name; row["DrivesStatistics"] = field == 0;
                        foreach (System.Reflection.PropertyInfo property in properties) row[property.Name] = property.GetValue(obj: pass) ?? DBNull.Value;
                        passes.Rows.Add(row: row);
                    }
                    ReductionColumn? target = plan.TargetColumns.Find(match: c => c.Name == column.Name);
                    int scale = target?.Scale ?? (column.Type is "decimal" or "numeric" ? column.Scale : 6);
                    resultRow[column.Name] = result.DailyMean.HasValue ? Math.Round(d: result.DailyMean.Value, decimals: scale, mode: MidpointRounding.AwayFromZero) : DBNull.Value;
                    if (field == 0)
                    {
                        resultRow["NoOfObservations"] = result.OriginalCount == 0 ? DBNull.Value : result.OriginalCount;
                        resultRow["StatisticsField"] = column.Name;
                        resultRow["OriginalMean"] = (object?)result.OriginalMean ?? DBNull.Value;
                        resultRow["MeanDifference"] = (object?)(result.OriginalMean - result.DailyMean) ?? DBNull.Value;
                        resultRow["RetainedCount"] = result.RetainedCount; resultRow["RejectedCount"] = result.RejectedCount;
                        resultRow["AcceptedPasses"] = result.AcceptedPasses; resultRow["Performance"] = result.Performance;
                    }
                }
                final.Rows.Add(row: resultRow);
            }
        }
        cancellationToken.ThrowIfCancellationRequested(); transaction.Commit();
        return new(Review: new(SourceTable: plan.Schema + "." + plan.Source, TargetTable: plan.Schema + "." + plan.Target,
            EpochReadings: raw, Passes: passes, DailyResults: final, Days: days.AsReadOnly()), Entities: entities);
    }
    private static void VerifyReviewedEntities(List<ReductionEntity> expected, List<ReductionEntity> actual)
    {
        if (expected.Count != actual.Count) throw ChangedReview();
        for (int index = 0; index < expected.Count; index++)
            if (expected[index].Key != actual[index].Key || expected[index].ReplacementName != actual[index].ReplacementName) throw ChangedReview();
    }
    private static void VerifyReviewedReadings(DataTable expected, DataTable actual)
    {
        if (expected.Rows.Count != actual.Rows.Count || expected.Columns.Count != actual.Columns.Count) throw ChangedReview();
        for (int column = 0; column < expected.Columns.Count; column++)
            if (expected.Columns[column].ColumnName != actual.Columns[column].ColumnName) throw ChangedReview();
        for (int row = 0; row < expected.Rows.Count; row++)
            for (int column = 0; column < expected.Columns.Count; column++)
                if (!Equals(objA: expected.Rows[row][column], objB: actual.Rows[row][column])) throw ChangedReview();
    }
    private static InvalidOperationException ChangedReview() => new(message: "The registrations or epoch readings changed during review. This table was not committed. Run Test again to review the updated data.");
}
#endregion


