using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

internal sealed record ResourcePairConflict(string StationName,string FirstStopId,string SecondStopId,double Seconds);
internal sealed record ResourceStationProfile(string StationName,string Identity,MicroSolveStatus Status,
    IReadOnlyList<StationRouteTrackAlternative> Assignments,IReadOnlyList<ResourcePairConflict> Conflicts,
    IReadOnlyList<string> CoreStopIds);
internal sealed record ResourceConflictProfile(IReadOnlyDictionary<string,ResourceStationProfile> Stations,
    IReadOnlyList<ResourcePairConflict> Conflicts,double ViolationSeconds,int UnknownStations,bool Complete);

internal static class ResourceConflictProfiler
{
    internal static ResourceConflictProfile Evaluate(TimetablingInstance instance,MasterSolutionSnapshot snapshot,
        Func<double> remaining,ResourceConflictProfile? previous=null,IReadOnlyList<LocalMicroRepairSelectedAlternative>? preferred=null)
    {
        Dictionary<string,ResourceStationProfile> stations=new(StringComparer.OrdinalIgnoreCase);
        var trainById=instance.Trains.ToDictionary(t=>t.TrainIndex);
        var groups=instance.TrainStops.GroupBy(s=>s.StationName,StringComparer.OrdinalIgnoreCase).ToArray();
        foreach(var group in groups)
        {
            if(remaining()<=0) break;
            string identity=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
                string.Join(";",group.OrderBy(s=>s.StopId,StringComparer.Ordinal).Select(s=>
                    FormattableString.Invariant($"{s.StopId}:{snapshot.ArrivalSecondsByStopId[s.StopId]:R}:{snapshot.DepartureSecondsByStopId[s.StopId]:R}:{snapshot.StopByStopId[s.StopId]}"))))));
            var resourceChoices=preferred?.Where(p=>p.StationName.Equals(group.Key,StringComparison.OrdinalIgnoreCase)).ToArray()??[];
            if(resourceChoices.Length==0 && previous?.Stations.TryGetValue(group.Key,out var cached)==true && cached.Identity==identity && cached.Status!=MicroSolveStatus.Unknown)
            { stations.Add(group.Key,cached);continue; }
            var requirements=group.Select(s=>StationLevelMicroFeasibilitySlave.BuildRequirementForMode(instance,trainById[s.TrainIndex],s,
                snapshot.StopByStopId[s.StopId]==1,snapshot.ArrivalSecondsByStopId[s.StopId],snapshot.DepartureSecondsByStopId[s.StopId])).ToArray();
            var assignmentRequirements=requirements.Select(AssignmentRequirement).ToArray();
            var domains=assignmentRequirements.Select(StationRouteTrackAssignmentSolver.BuildAlternatives).ToArray();
            if(domains.Any(d=>d.Count==0))
            {
                stations.Add(group.Key,new(group.Key,identity,MicroSolveStatus.ProvenInfeasible,[],[],
                    assignmentRequirements.Where((_,i)=>domains[i].Count==0).Select(r=>r.RequirementId).ToArray()));
                continue;
            }
            int? Headway(int a,int b)=>instance.TryGetRouteHeadwayRule(a,b,out var rule)?rule!.MinHeadwaySeconds:null;
            StationRouteTrackAlternative[]? proposed=null;
            if(resourceChoices.Length==requirements.Length && resourceChoices.Select(c=>c.RequirementId).Distinct(StringComparer.OrdinalIgnoreCase).Count()==requirements.Length)
            {
                var choices=resourceChoices.ToDictionary(c=>c.RequirementId,StringComparer.OrdinalIgnoreCase);
                var matched=domains.Select(d=>d.FirstOrDefault(a=>choices.TryGetValue(a.RequirementId,out var c) &&
                    c.TrainIndex==a.TrainIndex && c.IsStop==a.IsStop && c.TrackName.Equals(a.TrackName,StringComparison.OrdinalIgnoreCase) &&
                    c.IncomingRouteId==a.IncomingRoute?.RouteId && c.OutgoingRouteId==a.OutgoingRoute?.RouteId)).ToArray();
                if(matched.All(a=>a is not null)) proposed=matched.Select(a=>a!).ToArray();
            }
            StationRouteTrackAssignmentResult? fixedResult=null;
            if(proposed is not null)
            {
                var fixedRequirements=assignmentRequirements.Select(r=>{
                    var a=proposed.First(a=>a.RequirementId==r.RequirementId);
                    return r with { FeasibleTracks=[a.TrackName],IncomingRoutes=a.IncomingRoute is null?[]:[a.IncomingRoute],
                        OutgoingRoutes=a.OutgoingRoute is null?[]:[a.OutgoingRoute] };
                }).ToArray();
                fixedResult=StationRouteTrackAssignmentSolver.Solve(fixedRequirements,Headway,
                    new(250_000,TimeSpan.FromSeconds(Math.Max(.001,Math.Min(2,remaining())))));
            }
            var exact=fixedResult?.IsFeasible==true?fixedResult:StationRouteTrackAssignmentSolver.Solve(assignmentRequirements,Headway,
                new(250_000,TimeSpan.FromSeconds(Math.Max(.001,Math.Min(2,remaining())))));
            StationRouteTrackAlternative[] selected;
            if(exact.IsFeasible) selected=exact.SelectedAlternatives.OrderBy(a=>a.TrainIndex).ToArray();
            else
            {
                // Deterministic coordinate descent gives a comparable search score, not a bound.
                selected=domains.Select(d=>d[0]).ToArray();
                for(int pass=0;pass<12 && remaining()>0;pass++)
                {
                    bool changed=false;
                    for(int i=0;i<domains.Length;i++)
                    {
                        var best=selected[i];double bestCost=Cost(best,i);
                        foreach(var alternative in domains[i])
                        {
                            double cost=Cost(alternative,i);
                            if(cost<bestCost-1e-6) { best=alternative;bestCost=cost; }
                        }
                        if(!Equals(best,selected[i])) { selected[i]=best;changed=true; }
                    }
                    if(!changed) break;
                }
                double Cost(StationRouteTrackAlternative value,int index)=>selected.Where((_,j)=>j!=index).Sum(other=>PairCost(value,other,Headway));
            }
            // A partial-slack MIP result is search guidance only. Validate its domain and
            // evaluate every pair; never promote it to feasible just because its score is small.
            double Total(StationRouteTrackAlternative[] a)
            {
                double value=0;for(int i=0;i<a.Length;i++) for(int j=i+1;j<a.Length;j++) value+=PairCost(a[i],a[j],Headway);
                return value;
            }
            if(!exact.IsFeasible && proposed is not null && Total(proposed)<Total(selected)-1e-6) selected=proposed;
            List<ResourcePairConflict> conflicts=[];
            for(int a=0;a<selected.Length;a++) for(int b=a+1;b<selected.Length;b++)
            {
                double cost=PairCost(selected[a],selected[b],Headway);
                if(cost>1e-6) conflicts.Add(new(group.Key,selected[a].RequirementId,selected[b].RequirementId,cost));
            }
            stations.Add(group.Key,new(group.Key,identity,exact.Status,selected,conflicts,
                exact.InfeasibleCore?.RequirementIds??[]));
        }
        var all=stations.Values.SelectMany(s=>s.Conflicts).ToArray();
        int unknown=groups.Length-stations.Count+stations.Values.Count(s=>s.Status==MicroSolveStatus.Unknown);
        // Empty route domains have a finite conspicuous search penalty, never a false zero.
        double missing=stations.Values.Where(s=>s.Assignments.Count==0).Sum(s=>s.CoreStopIds.Count*86400.0);
        return new(stations,all,all.Sum(c=>c.Seconds)+missing,unknown,stations.Count==groups.Length && remaining()>0);
    }

    internal static StationAssignmentRequirement AssignmentRequirement(StationMicroRequirement requirement)
    {
        var occupation=StationEndpointOccupationPolicy.Resolve(requirement.StopRole,requirement.IsStop,
            requirement.ArrivalSeconds,requirement.DepartureSeconds,StationEndpointOccupationPolicy.DefaultSeconds,0,86400);
        return new(requirement.StopId,requirement.TrainIndex,requirement.IsStop,requirement.ArrivalSeconds,
            requirement.DepartureSeconds,occupation.StartSeconds,occupation.EndSeconds,requirement.IncomingRoutes,
            requirement.OutgoingRoutes,requirement.FeasibleTracks);
    }

    internal static double PairCost(StationRouteTrackAlternative a,StationRouteTrackAlternative b,Func<int,int,int?> headway)
    {
        double cost=0;
        if(a.TrackName.Equals(b.TrackName,StringComparison.OrdinalIgnoreCase))
        {
            double deficit=Math.Min(a.OccupationEndSeconds-b.OccupationStartSeconds,b.OccupationEndSeconds-a.OccupationStartSeconds);
            if(deficit>TimetableTimePolicy.NumericToleranceSeconds) cost+=deficit;
        }
        foreach(var first in new[]{(a.IncomingRoute,a.ArrivalSeconds),(a.OutgoingRoute,a.DepartureSeconds)})
        foreach(var second in new[]{(b.IncomingRoute,b.ArrivalSeconds),(b.OutgoingRoute,b.DepartureSeconds)})
        {
            if(first.Item1 is null || second.Item1 is null) continue;
            int? forward=headway(first.Item1.RouteId,second.Item1.RouteId),reverse=headway(second.Item1.RouteId,first.Item1.RouteId);
            if(forward is null && reverse is null) continue;
            double deficit=Math.Min(Math.Max(0,(forward??0)-(second.Item2-first.Item2)),
                Math.Max(0,(reverse??0)-(first.Item2-second.Item2)));
            if(deficit>TimetableTimePolicy.NumericToleranceSeconds) cost+=deficit;
        }
        return cost;
    }
}
