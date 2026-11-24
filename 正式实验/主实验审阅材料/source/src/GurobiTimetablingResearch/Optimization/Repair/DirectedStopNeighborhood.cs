using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

internal static class DirectedStopNeighborhood
{
    /// <summary>Release only section orders adjacent to the active stop neighborhood, with a bounded number of pairs.</summary>
    /// <param name="instance">Original visits and their route positions.</param>
    /// <param name="anchor">Anchor arrival and departure times in seconds.</param>
    /// <param name="relations">Available section relations in canonical train order.</param>
    /// <param name="activeStopIds">Visits whose adjacent sections are eligible.</param>
    /// <param name="alreadyReleasedPairs">Pairs already released on their full requested routes.</param>
    /// <param name="maxNewPairs">Maximum number of additional pairs to select.</param>
    /// <returns>Section-specific requests for the selected adjacent relations.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The pair budget is negative.</exception>
    internal static IReadOnlyList<SectionConflictEdgeRequest> SelectAdjacentOrderRelations(
        TimetablingInstance instance, MasterSolutionSnapshot anchor, IReadOnlyList<SectionConflictCandidate> relations,
        IReadOnlySet<string> activeStopIds, IReadOnlySet<string> alreadyReleasedPairs, int maxNewPairs = 24)
    {
        if(maxNewPairs<0) throw new ArgumentOutOfRangeException(nameof(maxNewPairs));
        if (activeStopIds.Count == 0 || maxNewPairs == 0) return [];
        var positions=activeStopIds.Select(id=>instance.StopsById[id]).Select(s=>(s.TrainIndex,s.StationOrder)).ToHashSet();
        var stops=instance.TrainStops.ToDictionary(s=>(s.TrainIndex,s.StationOrder));
        // Rank pairs by the nearest eligible events and retain only the sections touching active visits.
        return relations.Where(c=>positions.Contains((c.FirstTrainIndex,c.FirstStopOrder)) || positions.Contains((c.FirstTrainIndex,c.FirstEndStopOrder)) ||
                positions.Contains((c.SecondTrainIndex,c.SecondStopOrder)) || positions.Contains((c.SecondTrainIndex,c.SecondEndStopOrder)))
            .GroupBy(c=>DirectedFeasibilityRepair.Pair(c.FirstTrainIndex,c.SecondTrainIndex))
            .Where(group => !alreadyReleasedPairs.Contains(group.Key)).OrderBy(group => group.Min(Distance))
            .ThenBy(group => group.Key, StringComparer.Ordinal).Take(maxNewPairs)
            .SelectMany(group => group.Select(relation =>
                new SectionConflictEdgeRequest(relation.SectionName, relation.FirstTrainIndex, relation.SecondTrainIndex)))
            .ToArray();
        double Distance(SectionConflictCandidate c)=>Math.Min(
            Math.Abs(anchor.DepartureSecondsByStopId[stops[(c.FirstTrainIndex,c.FirstStopOrder)].StopId]-anchor.DepartureSecondsByStopId[stops[(c.SecondTrainIndex,c.SecondStopOrder)].StopId]),
            Math.Abs(anchor.ArrivalSecondsByStopId[stops[(c.FirstTrainIndex,c.FirstEndStopOrder)].StopId]-anchor.ArrivalSecondsByStopId[stops[(c.SecondTrainIndex,c.SecondEndStopOrder)].StopId]));
    }

    internal static IReadOnlySet<string> Build(TimetablingInstance instance,IReadOnlyDictionary<string,int> pattern,
        IReadOnlySet<string> targetStops,IReadOnlyList<StopLinearCut> cuts,IReadOnlyCollection<string> excludedPatterns,int maxCells=64)
    {
        if(maxCells<4) throw new ArgumentOutOfRangeException(nameof(maxCells));
        var domains=StructuralStopDomains.Build(instance);
        var targets=targetStops.Where(id=>domains.TryGetValue(id,out var d) && d.Lower==0 && d.Upper==1)
            .Order(StringComparer.Ordinal).ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> active=new(StringComparer.OrdinalIgnoreCase);
        var proposals=ConflictGuidedQuotaExchange.Propose(instance,pattern,new Dictionary<string,double>(),cuts,
            excludedPatterns.ToHashSet(),maxLength:8,maxProposals:12,maxNodes:100000,allowZeroGain:true,targetStops:targets);
        foreach(var proposal in proposals)
            if(active.Union(proposal.ChangedStopIds,StringComparer.OrdinalIgnoreCase).Count()<=maxCells)
                active.UnionWith(proposal.ChangedStopIds); // Never truncate a balancing cycle.
        foreach(string id in targets) if(active.Count<maxCells) active.Add(id);
        return active;
    }
}
