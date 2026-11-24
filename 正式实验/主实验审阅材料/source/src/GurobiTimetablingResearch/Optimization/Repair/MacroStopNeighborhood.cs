using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

internal sealed record MacroStopNeighborhood(IReadOnlySet<string> FreeStopIds,IReadOnlySet<string> RelatedStopIds,
    IReadOnlySet<string> RequiredOptionalStopIds,IReadOnlySet<string> HardFixedRootStopIds,bool CoreBounded)
{
    internal static MacroStopNeighborhood Select(TimetablingInstance instance,MasterSolutionSnapshot candidate,
        DirectedDiagnosis diagnosis,MacroRepairContext context,IReadOnlySet<string> released,IEnumerable<string> exploratorySeeds,
        int scale,Func<double>? remaining=null)
    {
        var domains=StructuralStopDomains.Build(instance);
        bool Optional(string id)=>domains[id].Lower==0 && domains[id].Upper==1;
        var roots=context.FeedbackStops.Concat(diagnosis.Conflicts.SelectMany(c=>new[]{c.FirstStopId,c.SecondStopId}))
            .Concat(diagnosis.UnresolvedLongDwellStopIds)
            .Concat((diagnosis.QuotaRows??[]).Where(r=>r.Violation>0).SelectMany(r=>r.StopIds))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var rootStops=roots.Select(id=>instance.StopsById[id]).ToArray();
        var feedbackTrains=context.FeedbackPairs.SelectMany(p=>p.Split('/').Select(int.Parse)).ToHashSet();
        // Along-train propagation and released physical neighbors precede unrelated span extremes.
        var related=instance.TrainStops.Where(s=>Optional(s.StopId)).Select(s=>new {Stop=s,
            Distance=rootStops.Where(r=>r.TrainIndex==s.TrainIndex).Select(r=>(double)Math.Abs(r.StationOrder-s.StationOrder))
                .DefaultIfEmpty(feedbackTrains.Contains(s.TrainIndex)?0:double.PositiveInfinity).Min()})
            .Where(p=>p.Distance<=2*(scale+1) || scale==2 && double.IsFinite(p.Distance))
            .OrderBy(p=>p.Distance).ThenBy(p=>p.Stop.StopId,StringComparer.Ordinal).Select(p=>p.Stop.StopId);
        var neighbors=instance.TrainStops.Where(s=>Optional(s.StopId)).Select(s=>new {Stop=s,
            Distance=rootStops.Where(r=>r.TrainIndex!=s.TrainIndex && r.StationName.Equals(s.StationName,StringComparison.OrdinalIgnoreCase) &&
                released.Contains(DirectedFeasibilityRepair.Pair(r.TrainIndex,s.TrainIndex)))
                .Select(r=>Math.Abs(Time(r)-Time(s))).DefaultIfEmpty(double.PositiveInfinity).Min()})
            .Where(p=>p.Distance<=600*(1<<scale)).OrderBy(p=>p.Distance).ThenBy(p=>p.Stop.StopId,StringComparer.Ordinal).Select(p=>p.Stop.StopId);
        int limit=32*(scale+1),trains=16*(scale+1);
        // Diagnostic roots are mandatory before an IIS exists too. A nearby optional
        // stop cannot stand in for a root that propagation subsequently fixes.
        var core=MacroRepairDependencies.ExpandStops(instance,candidate,roots,related.Concat(neighbors),limit,trains,remaining);
        var all=MacroRepairDependencies.ExpandStops(instance,candidate,roots,core.FreeTimeStopIds.Concat(exploratorySeeds),limit,trains,remaining);
        return new(all.FreeStopIds,core.FreeStopIds,roots.Where(Optional).ToHashSet(StringComparer.OrdinalIgnoreCase),
            roots.Where(id=>!Optional(id)).ToHashSet(StringComparer.OrdinalIgnoreCase),core.Bounded);
        double Time(TrainStopRecord s)=>s.StopRole==TrainStopRole.Origin?candidate.DepartureSecondsByStopId[s.StopId]:candidate.ArrivalSecondsByStopId[s.StopId];
    }
}
