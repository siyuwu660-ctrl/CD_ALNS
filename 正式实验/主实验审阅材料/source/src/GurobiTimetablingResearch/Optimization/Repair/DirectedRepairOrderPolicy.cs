using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

// Shared by native fixings and the identity used to skip exhausted restricted models.
internal static class DirectedRepairOrderPolicy
{
    /// <summary>Resolve pair-wide requests against the requested model edges before adding anchor guards.</summary>
    /// <param name="requestedEdges">Section relations selected by the caller before witness expansion.</param>
    /// <param name="releasedPairs">Pairs deliberately released on all their requested sections.</param>
    /// <param name="localEdges">Additional section-specific release requests.</param>
    /// <returns>Canonical section keys eligible for explicit release in this attempt.</returns>
    internal static IReadOnlySet<string> ResolveSectionKeys(IReadOnlyList<SectionConflictEdgeRequest> requestedEdges,
        IReadOnlySet<string> releasedPairs, IReadOnlyCollection<SectionConflictEdgeRequest>? localEdges = null)
    {
        var selectedEdges = requestedEdges.Where(edge =>
            releasedPairs.Contains(DirectedFeasibilityRepair.Pair(edge.FirstTrainIndex, edge.SecondTrainIndex)));
        return selectedEdges.Concat(localEdges ?? [])
            .Select(edge => BasicMilpModelBuilder.BuildSectionPrecedenceKey(edge.SectionName, edge.FirstTrainIndex, edge.SecondTrainIndex))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Identify the section orders that preserve physical reversals already in the anchor.</summary>
    /// <param name="witnesses">Headway-consistent reversal witnesses from actual anchor events.</param>
    /// <returns>Incoming and outgoing canonical section keys of all witnessed reversals.</returns>
    internal static IReadOnlySet<string> AnchorGuardKeys(IReadOnlyList<OvertakingStopProjection> witnesses)
        => witnesses.SelectMany(witness => new[] { witness.IncomingPrecedenceKey, witness.OutgoingPrecedenceKey })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Decide whether a sparse repair can change a section order.</summary>
    /// <param name="key">Section and canonical pair key.</param>
    /// <param name="first">First train index in the canonical pair.</param>
    /// <param name="second">Second train index in the canonical pair.</param>
    /// <param name="anchor">Anchor containing previously stored order decisions.</param>
    /// <param name="releasedSectionKeys">Canonical section keys explicitly released in this repair.</param>
    /// <param name="changedTrains">Trains participating in a stop change.</param>
    /// <param name="fixUnreleased">Whether only explicitly released section orders may change.</param>
    /// <param name="fixAll">Whether all section orders are fixed.</param>
    /// <param name="isAnchorGuard">Whether the section participates in a witnessed anchor reversal.</param>
    /// <returns>True when the section relation is free in the local repair.</returns>
    internal static bool ReleaseSection(string key, int first, int second, MasterSolutionSnapshot anchor,
        IReadOnlySet<string> releasedSectionKeys, IReadOnlySet<int> changedTrains,
        bool fixUnreleased, bool fixAll, bool isAnchorGuard = false)
        => !fixAll && (releasedSectionKeys.Contains(key) || !fixUnreleased && !isAnchorGuard &&
            (!anchor.SectionPrecedenceByKey.ContainsKey(key) || changedTrains.Contains(first) || changedTrains.Contains(second)));
    internal static bool FixStation(int first,int second,IReadOnlySet<string> released,bool fixUnreleased,bool fixAll,bool fixFeedback)
        =>fixFeedback || fixAll || fixUnreleased && !released.Contains(DirectedFeasibilityRepair.Pair(first,second));
}
