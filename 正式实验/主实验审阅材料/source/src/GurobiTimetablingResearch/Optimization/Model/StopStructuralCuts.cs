using Gurobi;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

internal sealed record StopLinearCut(string Id,IReadOnlyDictionary<string,double> Coefficients,double RightHandSide,
    string Proof,string Scope="OriginalHard")
{
    internal bool Satisfied(IReadOnlyDictionary<string,int> stops,double tolerance=1e-6)
        =>Coefficients.Sum(p=>p.Value*stops[p.Key])<=RightHandSide+tolerance;
    internal void Apply(GRBModel model,IReadOnlyDictionary<string,GRBVar> stops)
    {
        if(Scope!="OriginalHard") throw new InvalidOperationException("Local cuts cannot be installed as global strengthening.");
        GRBLinExpr lhs=new();foreach(var p in Coefficients) lhs.AddTerm(p.Value,stops[p.Key]);
        model.AddConstr(lhs,GRB.LESS_EQUAL,RightHandSide,Id);
    }
}

internal sealed record StopTravelBudgetPlan(IReadOnlyList<StopLinearCut> Budgets,IReadOnlyList<StopLinearCut> Covers,
    IReadOnlyList<object> Audit)
{
    internal IReadOnlyList<StopLinearCut> AllCuts=>Budgets.Concat(Covers).ToArray();
}

/// <summary>Telescoping run equalities + nonnegative supplement + conditional minimum dwell.</summary>
internal static class StopTravelBudgetCutBuilder
{
    internal static StopTravelBudgetPlan Build(TimetablingInstance instance)
    {
        List<StopLinearCut> budgets=[],covers=[];List<object> audit=[];int index=0;
        foreach(var limit in instance.TrainSectionTravelTimeLimitRules)
        {
            var train=instance.Trains.Single(t=>t.TrainIndex==limit.TrainIndex);
            var (start,end)=TrainSectionTravelTimeResolver.Resolve(train,limit);
            var stops=train.OrderedStops.Where(s=>s.StationOrder>=start.StationOrder && s.StationOrder<=end.StationOrder).ToArray();
            Dictionary<string,double> costs=[];double baseline=0;
            for(int k=1;k<stops.Length;k++)
            {
                RunTimeRule rule=ResolveRuntime(instance,stops[k-1],stops[k]);baseline+=rule.RunTimeSeconds;
                Add(stops[k-1].StopId,rule.StartAddSeconds);Add(stops[k].StopId,rule.StopAddSeconds);
                if(k<stops.Length-1) Add(stops[k].StopId,NormalDwellTimeResolver.Resolve(instance.NormalDwellTimeRules,stops[k]).NormalMinDwellSeconds);
            }
            var budget=new StopLinearCut($"stop_travel_budget_{++index}",costs,limit.MaxTravelTimeSeconds-baseline,
                $"travel-rule-{index}: arr({end.StopId})-dep({start.StopId}); runtime telescoping, start/stop adds, minimum dwell, supplements>=0");
            budgets.Add(budget);
            // A box upper bound is conservative; redundant status is not asserted from equal stop counts alone.
            bool redundant=costs.Values.Where(c=>c>0).Sum()<=budget.RightHandSide;
            if(costs.Values.All(c=>c>=0) && budget.RightHandSide>=0 && !redundant)
            {
                var cover=costs.Where(p=>p.Value>0).OrderByDescending(p=>p.Value).ThenBy(p=>p.Key,StringComparer.Ordinal).ToList();
                double sum=cover.Sum(p=>p.Value);
                foreach(var p in cover.ToArray().Reverse()) if(sum-p.Value>budget.RightHandSide+1e-6) { cover.Remove(p);sum-=p.Value; }
                if(sum>budget.RightHandSide+1e-6)
                    covers.Add(new($"stop_travel_cover_{index}",cover.ToDictionary(p=>p.Key,_=>1.0),cover.Count-1,
                        $"{budget.Id}: nonnegative cost cover sum={sum} > budget={budget.RightHandSide}"));
            }
            audit.Add(new { rule=index,train.TrainIndex,baseline,budget=budget.RightHandSide,coefficients=costs,
                redundantByBox=redundant,negativeCoefficient=costs.Values.Any(c=>c<0),coverCount=covers.Count(c=>c.Id==$"stop_travel_cover_{index}") });
            void Add(string key,double value) { costs[key]=costs.GetValueOrDefault(key)+value; }
        }
        return new(budgets,covers,audit);
    }

    internal static RunTimeRule ResolveRuntime(TimetablingInstance instance,TrainStopRecord first,TrainStopRecord next)
    {
        if(next.IncomingSectionName is null || !instance.SectionByName.TryGetValue(next.IncomingSectionName,out var section))
            throw new InvalidDataException($"Missing section for {next.StopId}.");
        bool forward=section.FromStation.Equals(first.StationName,StringComparison.OrdinalIgnoreCase) && section.ToStation.Equals(next.StationName,StringComparison.OrdinalIgnoreCase);
        bool reverse=section.ToStation.Equals(first.StationName,StringComparison.OrdinalIgnoreCase) && section.FromStation.Equals(next.StationName,StringComparison.OrdinalIgnoreCase);
        if(!forward && !reverse) throw new InvalidDataException("Runtime section does not match train traversal.");
        TravelDirection direction=forward?section.DirectionTag:section.DirectionTag==TravelDirection.Up?TravelDirection.Down:section.DirectionTag==TravelDirection.Down?TravelDirection.Up:TravelDirection.Unknown;
        return instance.RunTimeRules.FirstOrDefault(r=>r.ScaleName.Equals(next.RunScaleName,StringComparison.OrdinalIgnoreCase) &&
            r.SectionName.Equals(next.IncomingSectionName,StringComparison.OrdinalIgnoreCase) && r.Direction==direction)
            ??throw new InvalidDataException($"Missing runtime for {next.StopId}.");
    }
}

internal sealed record LegalStopDomain(int Lower,int Upper,IReadOnlyList<string> Tracks,bool Contradiction);
internal static class StructuralStopDomains
{
    internal static Dictionary<string,LegalStopDomain> Build(TimetablingInstance instance)
    {
        Dictionary<string,LegalStopDomain> result=[];
        foreach(var train in instance.Trains) foreach(var stop in train.OrderedStops)
        {
            var stopping=StationLevelMicroFeasibilitySlave.BuildRequirementForMode(instance,train,stop,true,0,0);
            var passing=StationLevelMicroFeasibilitySlave.BuildRequirementForMode(instance,train,stop,false,0,0);
            bool stopOk=Legal(stopping),passOk=Legal(passing);
            int lower=stop.StopRole!=TrainStopRole.Intermediate || stop.StopRequirement==StopRequirement.MustStop || !passOk?1:0;
            int upper=stop.StopRequirement==StopRequirement.MustPass || !stopOk?0:1;
            result[stop.StopId]=new(lower,upper,stopping.FeasibleTracks,lower>upper);
        }
        return result;
        static bool Legal(StationMicroRequirement r)=>r.FeasibleTracks.Count>0 && (!r.RequiresIncomingRoute || r.IncomingRoutes.Count>0) &&
            (!r.RequiresOutgoingRoute || r.OutgoingRoutes.Count>0);
    }
}

internal sealed record StationCapacityPlan(IReadOnlyList<StopLinearCut> Cuts,int CandidateIntervals,int ZeroEnergyTerms,
    IReadOnlyList<object> Evidence,string Scope="OriginalHard");

internal static class StationResourceCapacityCutBuilder
{
    // Same-track occupations have no business-time overlap allowance.
    // Use the full minimum dwell duration in the compulsory-energy inequality.
    internal static StationCapacityPlan Build(TimetablingInstance instance,TemporalBoundScope scope,int maxIntervalsPerStation=24)
    {
        if(scope!=TemporalBoundScope.OriginalHard) throw new ArgumentException("Global capacity cuts require original hard domains.",nameof(scope));
        if(maxIntervalsPerStation<1) throw new ArgumentOutOfRangeException(nameof(maxIntervalsPerStation));
        var domains=StructuralStopDomains.Build(instance);var bounds=HardEventBoundPropagator.Build(instance);
        List<StopLinearCut> cuts=[];List<object> evidence=[];int intervals=0,zero=0;
        foreach(var group in instance.TrainStops.Where(s=>s.StopRole==TrainStopRole.Intermediate && domains[s.StopId].Upper==1).GroupBy(s=>s.StationName))
        {
            var jobs=group.Select(s=>new { Stop=s,Tracks=domains[s.StopId].Tracks,
                Earliest=bounds.Arrival(s.StopId).Lower,
                Latest=Math.Min(bounds.Arrival(s.StopId).Upper,bounds.Departure(s.StopId).Upper-NormalDwellTimeResolver.Resolve(instance.NormalDwellTimeRules,s).NormalMinDwellSeconds),
                Duration=Math.Max(0,NormalDwellTimeResolver.Resolve(instance.NormalDwellTimeRules,s).NormalMinDwellSeconds) }).Where(j=>j.Latest>=j.Earliest).ToArray();
            var subsets=jobs.Select(j=>j.Tracks.OrderBy(x=>x,StringComparer.Ordinal).ToArray())
                .Append(jobs.SelectMany(j=>j.Tracks).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x=>x,StringComparer.Ordinal).ToArray())
                .Where(x=>x.Length>0).GroupBy(x=>string.Join("|",x),StringComparer.OrdinalIgnoreCase).Select(g=>g.First()).Take(16).ToArray();
            // Linear-size candidate selection, not all O(n^2) endpoint combinations.
            var points=jobs.SelectMany(j=>new[]{j.Earliest,j.Earliest+j.Duration,j.Latest,j.Latest+j.Duration}).Distinct().Order().ToArray();
            var candidates=points.Zip(points.Skip(1),(a,b)=>(a,b)).Where(i=>i.b>i.a).Take(maxIntervalsPerStation).ToArray();
            foreach(var interval in candidates) foreach(var tracks in subsets)
            {
                intervals++;Dictionary<string,double> energy=[];
                foreach(var j in jobs.Where(j=>j.Tracks.All(t=>tracks.Contains(t,StringComparer.OrdinalIgnoreCase))))
                {
                    double e=Math.Min(Overlap(j.Earliest,j.Earliest+j.Duration,interval.a,interval.b),Overlap(j.Latest,j.Latest+j.Duration,interval.a,interval.b));
                    if(e>1e-6) energy.Add(j.Stop.StopId,e);else zero++;
                }
                double rhs=tracks.Length*(interval.b-interval.a);
                bool nonredundant=energy.Values.Sum()>rhs+1e-6;
                if(nonredundant) cuts.Add(new($"track_capacity_{cuts.Count+1}",energy,rhs,
                    $"{group.Key}; tracks={string.Join('|',tracks)}; interval=[{interval.a},{interval.b}]; legal-domain subset; min overlap; full minimum dwell"));
                evidence.Add(new { station=group.Key,tracks,start=interval.a,end=interval.b,energy,rhs,nonredundant });
            }
        }
        return new(cuts,intervals,zero,evidence);
    }
    private static double Overlap(double a,double b,double x,double y)=>Math.Max(0,Math.Min(b,y)-Math.Max(a,x));
}
