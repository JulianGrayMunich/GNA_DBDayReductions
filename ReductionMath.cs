#region Imports
using System.Globalization;
#endregion

namespace GNA_DBDayReductions;

#region Reduction Arithmetic and Local Calendar
public sealed record ReductionResult(decimal? OriginalMean, decimal? DailyMean, int OriginalCount,
    int RetainedCount, int AcceptedPasses)
{
    public int RejectedCount => OriginalCount - RetainedCount;
    public string Performance => OriginalCount > 0 ? "Pass" : "Fail";
}

public static class ReductionMath
{
    public static ReductionResult Reduce(IEnumerable<decimal?> observations, decimal comparisonTolerance = 0.000000000001m, ICollection<ReductionPass>? trace = null)
    {
        ArgumentNullException.ThrowIfNull(argument: observations);
        if (comparisonTolerance < 0) throw new ArgumentOutOfRangeException(paramName: nameof(comparisonTolerance));
        List<decimal> values = new();
        foreach (decimal? value in observations) if (value.HasValue) values.Add(item: value.Value);
        if (values.Count == 0)
        {
            ReductionDiagnostics.Record(trace: trace, values: values, first: 0, count: 0, mean: null, pass: 0,
                stage: "Final", decision: "Fail", tolerance: comparisonTolerance);
            return new(OriginalMean: null, DailyMean: null, OriginalCount: 0, RetainedCount: 0, AcceptedPasses: 0);
        }
        values.Sort();
        int first = 0, count = values.Count, passes = 0;
        decimal originalMean = Mean(values: values, first: first, count: count);
        decimal adoptedMean = originalMean;
        ReductionDiagnostics.Record(trace: trace, values: values, first: first, count: count, mean: adoptedMean,
            pass: 0, stage: "Initial", decision: "Original sorted observations", tolerance: comparisonTolerance);
        while (count >= 5)
        {
            int candidateCount = count - 2;
            decimal candidateMean = Mean(values: values, first: first + 1, count: candidateCount);
            double squaredDeviations = 0;
            for (int index = first + 1; index < first + count - 1; index++)
            {
                // Subtract as decimal before conversion so coordinate offsets do not destroy precision.
                double deviation = (double)(values[index] - candidateMean);
                squaredDeviations += deviation * deviation;
            }
            double standardError = Math.Sqrt(d: squaredDeviations / (candidateCount - 1) / candidateCount);
            double difference = (double)Math.Abs(value: adoptedMean - candidateMean);
            double threshold = 2 * standardError;
            // Strict comparison; equality retains the larger set. This is a numerical floor, not a sensor tolerance.
            bool accept = difference > threshold + (double)comparisonTolerance;
            ReductionDiagnostics.Record(trace: trace, values: values, first: first, count: count, mean: adoptedMean,
                pass: passes + 1, stage: "Comparison", decision: accept ? "Accept trim: remove minimum and maximum" : "Retain larger set; stop",
                tolerance: comparisonTolerance, candidateCount: candidateCount, candidateMean: candidateMean,
                candidateSE: standardError, difference: Math.Abs(value: adoptedMean - candidateMean),
                minimum: values[first], maximum: values[first + count - 1]);
            if (!accept) break;
            first++;
            count = candidateCount;
            adoptedMean = candidateMean;
            passes++;
            ReductionDiagnostics.Record(trace: trace, values: values, first: first, count: count, mean: adoptedMean,
                pass: passes, stage: "After trim", decision: "Candidate adopted", tolerance: comparisonTolerance);
        }
        ReductionDiagnostics.Record(trace: trace, values: values, first: first, count: count, mean: adoptedMean,
            pass: passes, stage: "Final", decision: count < 5 ? "Fewer than five retained; use mean" : "Use retained larger set", tolerance: comparisonTolerance);
        return new(OriginalMean: originalMean, DailyMean: adoptedMean, OriginalCount: values.Count,
            RetainedCount: count, AcceptedPasses: passes);
    }

    private static decimal Mean(List<decimal> values, int first, int count)
    {
        decimal total = 0;
        for (int index = first; index < first + count; index++) total += values[index];
        return total / count;
    }
}

public sealed record ReductionDay(DateTime LocalDate, DateTime StartUtc, DateTime EndUtc, DateTime NoonUtc)
{
    public static ReductionDay Create(DateTime localDate, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(argument: zone);
        DateTime date = DateTime.SpecifyKind(value: localDate.Date, kind: DateTimeKind.Unspecified);
        DateTime start = ResolveLocal(local: date, zone: zone);
        DateTime end = ResolveLocal(local: date.AddDays(value: 1), zone: zone);
        DateTime noon = ResolveLocal(local: date.AddHours(value: 12), zone: zone);
        if (end <= start) throw new InvalidOperationException(message: $"No calendar day exists at {date:yyyy-MM-dd} in {zone.Id}.");
        return new(LocalDate: date, StartUtc: start, EndUtc: end, NoonUtc: noon);
    }

    private static DateTime ResolveLocal(DateTime local, TimeZoneInfo zone)
    {
        // Midnight DST gaps start at the first valid minute; folds use the earlier UTC occurrence.
        int minutes = 0;
        while (zone.IsInvalidTime(dateTime: local) && minutes < 1440) { local = local.AddMinutes(value: 1); minutes++; }
        if (zone.IsInvalidTime(dateTime: local)) throw new InvalidOperationException(message: "Cannot resolve the project-local calendar boundary.");
        if (zone.IsAmbiguousTime(dateTime: local))
        {
            TimeSpan[] offsets = zone.GetAmbiguousTimeOffsets(dateTime: local);
            TimeSpan earliestOffset = offsets[0] > offsets[1] ? offsets[0] : offsets[1];
            return new DateTimeOffset(dateTime: local, offset: earliestOffset).UtcDateTime;
        }
        return TimeZoneInfo.ConvertTimeToUtc(dateTime: local, sourceTimeZone: zone);
    }
}
#endregion


