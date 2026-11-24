namespace GurobiTimetablingResearch.Optimization;

public enum TemporalBoundScope { OriginalHard, ObjectiveCutoff, RestrictedSearch, SoftPreference }
public sealed record TemporalRelationRow(HardEventBound First, HardEventBound Second,
    double ForwardMinimum, double ForwardMaximum, double ReverseMinimum, double ReverseMaximum);
public sealed record TemporalRelationDecision(string Classification, int? FixedValue, bool IsContradiction,
    TemporalBoundScope Scope, IReadOnlyList<string> Premises);

/// <summary>Only universal hard-domain implications can fix global order. 0=first before second.</summary>
public static class TemporalConflictGraphBuilder
{
    public static TemporalRelationDecision Classify(IReadOnlyList<TemporalRelationRow> rows, TemporalBoundScope scope)
    {
        if (scope != TemporalBoundScope.OriginalHard)
            throw new ArgumentException("Global precedence screening accepts original hard bounds only; soft/local/cutoff envelopes require a separate scoped rebuild.",nameof(scope));
        if (rows.Count == 0) throw new ArgumentException("At least one resource row is required.",nameof(rows));
        foreach (TemporalRelationRow r in rows)
        {
            if (!double.IsFinite(r.First.Lower) || !double.IsFinite(r.First.Upper) || r.First.Lower>r.First.Upper ||
                !double.IsFinite(r.Second.Lower) || !double.IsFinite(r.Second.Upper) || r.Second.Lower>r.Second.Upper ||
                !double.IsFinite(r.ForwardMinimum) || !double.IsFinite(r.ForwardMaximum) ||
                !double.IsFinite(r.ReverseMinimum) || !double.IsFinite(r.ReverseMaximum) ||
                r.ForwardMinimum<0 || r.ForwardMaximum<r.ForwardMinimum || r.ReverseMinimum<0 || r.ReverseMaximum<r.ReverseMinimum)
                throw new ArgumentException("Malformed hard envelope/headway range.",nameof(rows));
        }
        bool forwardImpossible=rows.Any(r=>r.Second.Upper-r.First.Lower < r.ForwardMinimum-1e-6);
        bool reverseImpossible=rows.Any(r=>r.First.Upper-r.Second.Lower < r.ReverseMinimum-1e-6);
        string[] premises=rows.SelectMany(r=>r.First.LowerSources.Concat(r.First.UpperSources)
            .Concat(r.Second.LowerSources).Concat(r.Second.UpperSources)).Distinct(StringComparer.Ordinal).ToArray();
        if (forwardImpossible && reverseImpossible) return new("Contradiction",null,true,scope,premises);
        if (reverseImpossible) return new(rows.All(r=>r.Second.Lower-r.First.Upper>=r.ForwardMaximum) ? "GuaranteedFirst" : "ForcedFirst",0,false,scope,premises);
        if (forwardImpossible) return new(rows.All(r=>r.First.Lower-r.Second.Upper>=r.ReverseMaximum) ? "GuaranteedSecond" : "ForcedSecond",1,false,scope,premises);
        return new("Free",null,false,scope,premises);
    }
}

public sealed record TemporalPrecedenceAudit(string Key, TemporalRelationDecision Decision);
