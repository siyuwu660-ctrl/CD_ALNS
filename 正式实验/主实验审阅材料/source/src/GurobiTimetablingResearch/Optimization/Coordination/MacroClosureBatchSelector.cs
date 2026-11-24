using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

public static class MacroClosureBatchSelector
{
    public static IReadOnlyList<SectionConflictEdgeRequest> Select(TimetablingInstance instance,
        MasterValidationReport report,IDictionary<string,int> recurrence,int batchSize)
    {
        if(batchSize<=0) throw new ArgumentOutOfRangeException(nameof(batchSize));
        var ranked=(report.SectionRelationFindings ?? []).Where(f=>!f.WasModeled)
            .GroupBy(f=>InitialConflictGraphBuilder.BuildCanonicalPairKey(f.SectionName,f.FirstTrainIndex,f.SecondTrainIndex))
            .Select(g=>
            {
                var f=g.First(); int times=recurrence.TryGetValue(g.Key,out int old)?old+1:1; recurrence[g.Key]=times;
                return (Finding:f,Score:g.Sum(x=>x.Violations.Count),Recurrence:times);
            }).OrderByDescending(x=>x.Score).ThenByDescending(x=>x.Recurrence)
            .ThenBy(x=>x.Finding.SectionName,StringComparer.Ordinal).ThenBy(x=>x.Finding.FirstTrainIndex).ThenBy(x=>x.Finding.SecondTrainIndex).ToList();
        // Round-robin sections gives deterministic resource diversity without random cut selection.
        var groups=ranked.GroupBy(x=>x.Finding.SectionName).Select(g=>new Queue<SectionRelationValidationFinding>(g.Select(x=>x.Finding))).ToList();
        List<SectionRelationValidationFinding> selected=[];
        while(selected.Count<batchSize && groups.Any(q=>q.Count>0))
            foreach(var queue in groups) { if(selected.Count>=batchSize) break; if(queue.Count>0) selected.Add(queue.Dequeue()); }
        HashSet<(int,int)> pairs=selected.Select(f=>(Math.Min(f.FirstTrainIndex,f.SecondTrainIndex),Math.Max(f.FirstTrainIndex,f.SecondTrainIndex))).ToHashSet();
        // Every shared section of a selected train pair is included. Adjacent weak-station order links cannot disappear.
        return new InitialConflictGraphBuilder(instance,int.MaxValue).Build()
            .Where(c=>pairs.Contains((Math.Min(c.FirstTrainIndex,c.SecondTrainIndex),Math.Max(c.FirstTrainIndex,c.SecondTrainIndex))))
            .Select(c=>new SectionConflictEdgeRequest(c.SectionName,c.FirstTrainIndex,c.SecondTrainIndex)).ToArray();
    }
}
