#region Imports
using System.Globalization;
#endregion
namespace GNA_DBDayReductions;
#region Reduction Pass Diagnostics
public sealed record ReductionPass(int Pass, string Stage, int Count, decimal? Mean, double? StandardDeviation,
    double? StandardError, int? CandidateCount, decimal? CandidateMean, double? CandidateStandardDeviation,
    double? CandidateStandardError, double? TwiceCandidateSE, decimal? MeanDifference, decimal ComparisonTolerance,
    decimal? Minimum, decimal? Maximum, string Decision, string RetainedValues);

public static class ReductionDiagnostics
{
    internal static void Record(ICollection<ReductionPass>? trace, List<decimal> values, int first, int count,
        decimal? mean, int pass, string stage, string decision, decimal tolerance,
        int? candidateCount = null, decimal? candidateMean = null, double? candidateSE = null,
        decimal? difference = null, decimal? minimum = null, decimal? maximum = null)
    {
        if (trace is null) return;
        double? sd = null, se = null;
        if (count > 1 && mean.HasValue)
        {
            double total = 0;
            for (int index = first; index < first + count; index++)
            {
                double deviation = (double)(values[index] - mean.Value);
                total += deviation * deviation;
            }
            sd = Math.Sqrt(d: total / (count - 1)); se = sd / Math.Sqrt(d: count);
        }
        string[] retained = new string[count];
        for (int index = 0; index < count; index++) retained[index] = values[first + index].ToString(format: "G29", provider: CultureInfo.InvariantCulture);
        trace.Add(item: new(Pass: pass, Stage: stage, Count: count, Mean: mean, StandardDeviation: sd, StandardError: se,
            CandidateCount: candidateCount, CandidateMean: candidateMean,
            CandidateStandardDeviation: candidateSE * (candidateCount.HasValue ? Math.Sqrt(d: candidateCount.Value) : 0),
            CandidateStandardError: candidateSE, TwiceCandidateSE: candidateSE * 2, MeanDifference: difference,
            ComparisonTolerance: tolerance, Minimum: minimum, Maximum: maximum, Decision: decision,
            RetainedValues: string.Join(separator: "; ", value: retained)));
    }
}
#endregion
