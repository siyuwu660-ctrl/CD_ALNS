using Gurobi;
namespace GurobiTimetablingResearch.Optimization;

/// <summary>Defines local solve priorities and the business ordering of verified incumbents.</summary>
internal static class ResourceSpanObjectivePolicy
{
    internal static void ApplyLocalPriorities(GRBModel model,bool qualityPhase)
    {
        for(int i=0;i<model.NumObj;i++)
        {
            model.Set(GRB.IntParam.ObjNumber,i);
            int shifted=model.ObjNPriority+2;
            model.ObjNPriority=Priority(model.ObjNName,shifted,qualityPhase);
            model.ObjNAbsTol=0;model.ObjNRelTol=0;
        }
    }
    /// <summary>Compare accepted witnesses by rounded span, raw service penalty and rounded supplement.</summary>
    /// <param name="candidate">Independently accepted candidate with nonnegative finite objective values.</param>
    /// <param name="incumbent">Accepted historical best under the same service policy.</param>
    /// <returns>True only when the candidate improves the complete lexicographic key.</returns>
    /// <exception cref="ArgumentException">Policies differ or objective values are invalid.</exception>
    internal static bool Better(ServiceObjectiveVector candidate,ServiceObjectiveVector incumbent)
    {
        if(candidate.PolicyIdentity!=incumbent.PolicyIdentity) throw new ArgumentException("Cannot compare different service policies.");
        foreach(double value in new[]{candidate.GlobalSpanSeconds,candidate.ServicePenalty,candidate.RunningTimeSupplementSeconds,
            incumbent.GlobalSpanSeconds,incumbent.ServicePenalty,incumbent.RunningTimeSupplementSeconds})
            if(!double.IsFinite(value) || value<0) throw new ArgumentException("Objective values must be finite and nonnegative.");
        // Order the stored finite values, without fuzzy pairwise ties: those can
        // form cycles and accumulate deterioration in higher-priority objectives.
        // Feasibility and search-reward tolerances are separate from this ordering.
        return Key(candidate).CompareTo(Key(incumbent)) < 0;
    }

    /// <summary>Produce the business ranking key for a finite objective vector.</summary>
    /// <param name="objective">Raw objective with span and supplement in seconds.</param>
    /// <returns>Whole-second span, raw service penalty and whole-second supplement in that order.</returns>
    internal static (double Span, double ServicePenalty, double Supplement) Key(ServiceObjectiveVector objective) =>
        (Math.Round(objective.GlobalSpanSeconds, MidpointRounding.AwayFromZero), objective.ServicePenalty,
            Math.Round(objective.RunningTimeSupplementSeconds, MidpointRounding.AwayFromZero));
    /// <summary>Expose the selection key in summaries alongside full-precision objectives.</summary>
    /// <param name="objective">Accepted objective, or null when no solution was accepted.</param>
    /// <returns>A named summary key, or null when the run has no accepted objective.</returns>
    internal static object? SummaryKey(ServiceObjectiveVector? objective)
    {
        if (objective is null) return null;
        var (span, penalty, supplement) = Key(objective);
        return new { spanSeconds = span, servicePenalty = penalty, supplementSeconds = supplement };
    }

    internal static int Priority(string name,int original,bool qualityPhase)=>!qualityPhase?original:name switch
    {
        "global_span"=>5,
        "service_window_penalty"=>4,
        _=>original
    };
}
