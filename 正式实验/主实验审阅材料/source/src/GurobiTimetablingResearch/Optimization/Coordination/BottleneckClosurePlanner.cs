using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

internal static class BottleneckClosurePlanner
{
    internal static int Expand(TimetablingInstance instance,IReadOnlyList<SectionConflictCandidate> modeled,
        IReadOnlySet<string> waitingStops,Dictionary<string,SectionConflictEdgeRequest> edges,int maxPairs)
    {
        if(maxPairs<1) throw new ArgumentOutOfRangeException(nameof(maxPairs));
        string Key(SectionConflictCandidate c)=>InitialConflictGraphBuilder.BuildCanonicalPairKey(c.SectionName,c.FirstTrainIndex,c.SecondTrainIndex);
        var existing=modeled.Select(Key).Concat(edges.Keys).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var full=new InitialConflictGraphBuilder(instance,int.MaxValue).Build();
        var supported=OvertakingStopProjectionPlanner.Plan(instance,modeled).Select(p=>p.ProjectionId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var priority=OvertakingStopProjectionPlanner.Plan(instance,full).Where(p=>!supported.Contains(p.ProjectionId) &&
            (waitingStops.Contains(p.FirstStopId) || waitingStops.Contains(p.SecondStopId)))
            .Select(p=>(Math.Min(p.FirstTrainIndex,p.SecondTrainIndex),Math.Max(p.FirstTrainIndex,p.SecondTrainIndex))).ToHashSet();
        var groups=full.Where(c=>!existing.Contains(Key(c))).GroupBy(c=>(Math.Min(c.FirstTrainIndex,c.SecondTrainIndex),Math.Max(c.FirstTrainIndex,c.SecondTrainIndex)))
            .OrderByDescending(g=>priority.Contains(g.Key)).ThenBy(g=>g.Key.Item1).ThenBy(g=>g.Key.Item2).Take(maxPairs);
        int added=0;
        foreach(var group in groups) foreach(var c in group)
            if(edges.TryAdd(Key(c),new(c.SectionName,c.FirstTrainIndex,c.SecondTrainIndex))) added++;
        return added;
    }
}
