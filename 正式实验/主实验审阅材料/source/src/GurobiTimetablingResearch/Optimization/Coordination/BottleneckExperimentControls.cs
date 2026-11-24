using Gurobi;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

internal sealed record QuotaCycleProposal(bool Applied,string Reason,IReadOnlyDictionary<string,int> Stops,
    IReadOnlyList<string> ChangedStopIds);

internal static class QuotaCycleExchange
{
    internal static QuotaCycleProposal Propose(TimetablingInstance instance,IReadOnlyDictionary<string,int> pattern,
        IReadOnlySet<int> conflictTrains,IReadOnlyList<StopLinearCut>? cuts=null)
    {
        QuotaCycleProposal No(string reason)=>new(false,reason,pattern,[]);
        var domains=StructuralStopDomains.Build(instance);
        if(instance.TrainStops.Any(s=>!pattern.TryGetValue(s.StopId,out int v) || v<domains[s.StopId].Lower || v>domains[s.StopId].Upper))
            return No("InvalidInputStopDomain");
        var trainRows=TrainSectionStopQuotaConstraintBuilder.Plan(instance);
        var stationRows=instance.StationStopQuotaRules.Select(r=>new { Rule=r,Stops=instance.TrainStops.Where(s=>
            s.StationName.Equals(r.StationName,StringComparison.OrdinalIgnoreCase) && s.TrainType.Equals(r.TrainType,StringComparison.OrdinalIgnoreCase)).ToArray() }).ToArray();
        if(stationRows.Any(r=>r.Stops.Any(s=>r.Rule.StartTimeSeconds>(s.StopRole==TrainStopRole.Origin?s.EarliestDepSeconds:s.EarliestArrSeconds) ||
            r.Rule.EndTimeSeconds<(s.StopRole==TrainStopRole.Origin?s.LatestDepSeconds:s.LatestArrSeconds)))) return No("UnsupportedTimeDependentStationQuota");
        var rows=trainRows.Select(r=>(Ids:r.CountedStops.Select(s=>s.StopId).ToArray(),Min:r.MinStops,Max:r.MaxStops))
            .Concat(stationRows.Select(r=>(Ids:r.Stops.Select(s=>s.StopId).ToArray(),Min:r.Rule.MinStops,Max:r.Rule.MaxStops))).ToArray();
        if(rows.Any(r=>r.Ids.Sum(id=>pattern[id])<r.Min || r.Ids.Sum(id=>pattern[id])>r.Max) || (cuts??[]).Any(c=>!c.Satisfied(pattern)))
            return No("InvalidInputQuotaOrTravelBudget");
        var trains=instance.Trains.OrderByDescending(t=>conflictTrains.Contains(t.TrainIndex)).ThenBy(t=>t.TrainIndex).ToArray();
        for(int i=0;i<trains.Length;i++) for(int j=i+1;j<trains.Length;j++)
        {
            var a=trains[i]; var b=trains[j];
            if(conflictTrains.Count>0 && !conflictTrains.Contains(a.TrainIndex) && !conflictTrains.Contains(b.TrainIndex)) continue;
            var common=(from x in a.OrderedStops
                        from y in b.OrderedStops
                        where x.StationName.Equals(y.StationName,StringComparison.OrdinalIgnoreCase) &&
                            x.TrainType.Equals(y.TrainType,StringComparison.OrdinalIgnoreCase) &&
                            domains[x.StopId].Lower==0 && domains[x.StopId].Upper==1 &&
                            domains[y.StopId].Lower==0 && domains[y.StopId].Upper==1 && pattern[x.StopId]!=pattern[y.StopId]
                        select (x,y)).ToArray();
            for(int u=0;u<common.Length;u++) for(int v=u+1;v<common.Length;v++)
            {
                if(pattern[common[u].x.StopId]==pattern[common[v].x.StopId]) continue;
                string[] changed=[common[u].x.StopId,common[u].y.StopId,common[v].x.StopId,common[v].y.StopId];
                if(changed.Distinct().Count()!=4) continue;
                var delta=changed.ToDictionary(id=>id,id=>1-2*pattern[id]);
                // Check EVERY original quota, including overlapping corridor rows, not just a nominal 2x2 table.
                if(rows.Any(r=>r.Ids.Sum(id=>delta.GetValueOrDefault(id))!=0)) continue;
                var result=pattern.ToDictionary(p=>p.Key,p=>p.Value);
                foreach(string id in changed) result[id]=1-result[id];
                if((cuts??[]).Any(c=>!c.Satisfied(result))) continue;
                return new(true,"AuditedFourCycle",result,changed);
            }
        }
        return No("NoAdmissibleFourCycle");
    }
}

internal static class BottleneckExperimentControls
{
    internal static void ValidatePattern(TimetablingInstance instance,IReadOnlyDictionary<string,int> pattern)
    {
        if(pattern.Count!=instance.TrainStops.Count || instance.TrainStops.Any(s=>!pattern.ContainsKey(s.StopId)))
            throw new InvalidDataException("Experimental stop pattern must have exactly all original stop keys.");
        var domains=StructuralStopDomains.Build(instance);
        foreach(var p in pattern)
            if(p.Value is not (0 or 1) || p.Value<domains[p.Key].Lower || p.Value>domains[p.Key].Upper)
                throw new InvalidDataException("Experimental stop pattern violates original binary/domain bounds: "+p.Key);
    }
    internal static int ApplyOriginOrder(BasicMilpModel model)
    {
        int count=0;
        foreach(var group in model.Instance.Trains.GroupBy(t=>t.OrderedStops[0].StationName,StringComparer.OrdinalIgnoreCase))
        {
            var trains=group.OrderBy(t=>t.OrderedStops[0].RawDepTimeSeconds ?? throw new InvalidDataException("Reference departure required for fixed-origin control."))
                .ThenBy(t=>t.TrainIndex).ToArray();
            for(int k=1;k<trains.Length;k++)
            {
                var a=trains[k-1].OrderedStops[0]; var b=trains[k].OrderedStops[0];
                model.Model.AddConstr(model.DepartureVars[a.StopId]-model.DepartureVars[b.StopId],GRB.LESS_EQUAL,0,
                    $"experiment_origin_order_t{a.TrainIndex}_t{b.TrainIndex}");
                count++;
            }
        }
        model.Model.Update(); return count;
    }
}
