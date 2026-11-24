using System.Diagnostics;
using Gurobi;

namespace GurobiTimetablingResearch.Optimization;

internal sealed record IntegratedProofExtractionResult(
    IReadOnlyList<ContinuousProofRow> Rows,
    string Strategy,
    double RuntimeSeconds);

internal static class IntegratedInfeasibilityEvidenceExtractor
{
    internal static IntegratedProofExtractionResult Extract(GRBModel model, double timeLimitSeconds)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (timeLimitSeconds <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(timeLimitSeconds));

        Stopwatch clock = Stopwatch.StartNew();
        try
        {
            using GRBModel relaxation = model.Relax();
            relaxation.Set(GRB.IntParam.OutputFlag, 0);
            relaxation.Set(GRB.IntParam.DualReductions, 0);
            relaxation.Set(GRB.IntParam.InfUnbdInfo, 1);
            relaxation.Set(GRB.DoubleParam.TimeLimit, timeLimitSeconds);
            relaxation.Optimize();
            if (relaxation.Get(GRB.IntAttr.Status) == GRB.Status.INFEASIBLE)
            {
                IReadOnlyList<ContinuousProofRow> rows =
                    ContinuousInfeasibilityEvidenceExtractor.Extract(relaxation);
                if (rows.Count > 0 && rows.Any(row => row.EvidenceKind == "farkas_dual"))
                {
                    clock.Stop();
                    return new(rows, "lp_relaxation_farkas", clock.Elapsed.TotalSeconds);
                }
            }
        }
        catch (GRBException)
        {
            // A relaxation proof is an optimization, not a correctness prerequisite.
            // Fall through to the bounded MIP IIS path when Gurobi cannot expose it.
        }

        double remaining = Math.Max(1.0, timeLimitSeconds - clock.Elapsed.TotalSeconds);
        model.Set(GRB.DoubleParam.TimeLimit, remaining);
        IReadOnlyList<ContinuousProofRow> fallback = ContinuousInfeasibilityEvidenceExtractor.Extract(model);
        clock.Stop();
        return new(
            fallback,
            fallback.Count > 0 ? "bounded_mip_iis" : "proof_unavailable",
            clock.Elapsed.TotalSeconds);
    }
}
