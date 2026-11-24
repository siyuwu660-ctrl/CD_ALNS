using Gurobi;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

internal sealed record WindowNeighborhood(IReadOnlySet<int> ActiveTrains,double Start,double Width,
    IReadOnlyList<int> QuotaExchangeTrains,string Scope="RestrictedSearch");

internal static class WindowNeighborhoodPlanner
{
    internal static int WidthAfterFailures(int failures)=>failures<2?3600:failures<4?5400:7200;
    internal static WindowNeighborhood Select(TimetablingInstance instance,MasterSolutionSnapshot? candidate,
        IReadOnlyDictionary<string,int> stops,double start,double width,IReadOnlySet<int>? conflictTrains=null)
    {
        HashSet<int> active=instance.Trains.Where(t=>Origin(t)>=start && Origin(t)<start+width).Select(t=>t.TrainIndex).ToHashSet();
        if(conflictTrains is not null) active.UnionWith(conflictTrains);
        if(active.Count==0) active.Add(instance.Trains.OrderBy(t=>Math.Abs(Origin(t)-start)).First().TrainIndex);
        HashSet<int> exchange=[];
        // All stop decisions of an admitted train are released together, preserving its corridor quota flexibility.
        // Add opposite-state station partners, not every train in a daily quota (which would collapse all windows).
        foreach(var rule in instance.StationStopQuotaRules)
        {
            var matches=instance.TrainStops.Where(s=>s.StationName.Equals(rule.StationName,StringComparison.OrdinalIgnoreCase) && s.TrainType.Equals(rule.TrainType,StringComparison.OrdinalIgnoreCase)).ToArray();
            var states=matches.Where(s=>active.Contains(s.TrainIndex)).Select(s=>stops.GetValueOrDefault(s.StopId)).Distinct().ToArray();
            foreach(int value in states)
            {
                var partner=matches.Where(s=>!active.Contains(s.TrainIndex) && stops.GetValueOrDefault(s.StopId)!=value && s.StopRequirement==StopRequirement.OptionalStop)
                    .OrderBy(s=>Math.Abs(Origin(instance.Trains.Single(t=>t.TrainIndex==s.TrainIndex))-(start+width/2))).FirstOrDefault();
                if(partner is not null) exchange.Add(partner.TrainIndex);
            }
        }
        active.UnionWith(exchange);return new(active,start,width,exchange.Order().ToArray());
        double Origin(TrainRecord t)=>candidate is not null && candidate.DepartureSecondsByStopId.TryGetValue(t.OrderedStops[0].StopId,out double v)
            ?v:Math.Max(0,t.OrderedStops[0].RawDepTimeSeconds??t.OrderedStops[0].EarliestDepSeconds);
    }
}

internal static class Stages45LocalModelBuilder
{
    internal static BasicMilpModel Build(TimetablingInstance instance,ServiceWindowPolicy policy,
        IReadOnlyDictionary<string,int> stopPattern,MasterSolutionSnapshot? candidate,IReadOnlySet<int> active,
        IReadOnlyList<SectionConflictEdgeRequest> edges,int radius,bool restricted,
        IReadOnlyList<StopLinearCut>? strengthening=null,IReadOnlyList<PromotedMicroComponent>? microRepairs=null,
        bool allowCandidateStart=true)
    {
        BasicMilpModel model=new BasicMilpModelBuilder(instance,neighborRadius:radius,additionalSectionEdges:edges,
            serviceWindows:policy,screenTemporalPrecedence:true,promotedMicroComponents:microRepairs).Build();
        try
        {
            model.Model.Set(GRB.IntParam.OutputFlag,0);
            if(candidate is not null && allowCandidateStart) MasterSolutionSnapshotApplier.ApplyExpandableMacroMipStart(model,candidate);
            foreach(var p in stopPattern) if(model.StopVars.TryGetValue(p.Key,out var variable)) variable.Start=p.Value;
            var origin=instance.Trains.ToDictionary(t=>t.TrainIndex,t=>(double)(t.OrderedStops[0].RawDepTimeSeconds??t.OrderedStops[0].EarliestDepSeconds));
            if(restricted)
            {
                foreach(var stop in instance.TrainStops.Where(s=>!active.Contains(s.TrainIndex)))
                    if(stopPattern.TryGetValue(stop.StopId,out int value)) Fix(model.StopVars[stop.StopId],value);
                foreach(var relation in model.SectionConflictCandidates)
                {
                    if(active.Contains(relation.FirstTrainIndex) || active.Contains(relation.SecondTrainIndex)) continue;
                    string key=BasicMilpModelBuilder.BuildSectionPrecedenceKey(relation);
                    if(candidate is not null && candidate.SectionPrecedenceByKey.TryGetValue(key,out int value)) Fix(model.SectionPrecedenceVars[key],value);
                    else if(candidate is not null)
                    {
                        var first=instance.TrainStops.Single(s=>s.TrainIndex==relation.FirstTrainIndex && s.StationOrder==relation.FirstStopOrder);
                        var second=instance.TrainStops.Single(s=>s.TrainIndex==relation.SecondTrainIndex && s.StationOrder==relation.SecondStopOrder);
                        double a=candidate.DepartureSecondsByStopId[first.StopId],b=candidate.DepartureSecondsByStopId[second.StopId];
                        if(Math.Abs(a-b)>1e-6) Fix(model.SectionPrecedenceVars[key],a<b?0:1);
                    }
                    else if(origin[relation.FirstTrainIndex]>=0 && origin[relation.SecondTrainIndex]>=0 && origin[relation.FirstTrainIndex]!=origin[relation.SecondTrainIndex])
                        Fix(model.SectionPrecedenceVars[key],origin[relation.FirstTrainIndex]<origin[relation.SecondTrainIndex]?0:1);
                }
                foreach(var relation in model.StationEventConflictCandidates)
                {
                    if(active.Contains(relation.FirstTrainIndex) || active.Contains(relation.SecondTrainIndex)) continue;
                    string key=BasicMilpModelBuilder.BuildStationEventPrecedenceKey(relation);
                    if(candidate is not null && candidate.StationEventPrecedenceByKey.TryGetValue(key,out int value)) Fix(model.StationEventPrecedenceVars[key],value);
                    // No reference-derived station-event ordering: incoming/outgoing relations are not origin order.
                }
            }
            foreach(var cut in strengthening??[]) cut.Apply(model.Model,model.StopVars);
            model.Model.Update();return model;
            static void Fix(GRBVar variable,int value)
            {
                if(value<variable.LB || value>variable.UB) throw new FixedDiscreteDomainContradictionException($"RestrictedSearch fixation conflicts with original bounds: {variable.VarName}.");
                variable.LB=variable.UB=value;
            }
        }
        catch { model.Dispose();throw; }
    }
}

internal static class Stages45FamilyDiagnostics
{
    internal static object Capture(BasicMilpModel model)
    {
        model.Model.Update();var variables=model.Model.GetVars();
        return new { trains=model.Instance.Trains.Count,stops=model.Instance.TrainStops.Count,variables=variables.Length,rows=model.Model.NumConstrs,
            families=variables.GroupBy(v=>v.VarName.Split('_')[0]).Select(g=>new { family=g.Key,count=g.Count(),freeIntegers=g.Count(v=>v.VType!=GRB.CONTINUOUS && v.LB<v.UB),fixedCount=g.Count(v=>v.LB==v.UB) }).OrderByDescending(g=>g.count).ToArray(),
            rowFamilies=model.Model.GetConstrs().GroupBy(r=>r.ConstrName.Split('_')[0]).Select(g=>new { family=g.Key,count=g.Count() }).OrderByDescending(g=>g.count).ToArray(),
            policyIdentity=model.ServiceWindows.Identity,objectives=model.Model.NumObj,scope="RestrictedSearch",globalLowerBound=(double?)null };
    }
}

internal static class RestrictedInfeasibilityAnalyzer
{
    // An IIS of a restricted/sparse model is used ONLY to release a neighborhood, never to cut the original problem.
    internal static HashSet<int> FindTrainDependencies(GRBModel model,double seconds)
    {
        HashSet<int> trains=[];if(model.Status!=GRB.Status.INFEASIBLE || seconds<=0) return trains;
        model.Set(GRB.DoubleParam.TimeLimit,seconds);model.Set(GRB.IntParam.OutputFlag,0);
        try
        {
            model.ComputeIIS();
            foreach(var row in model.GetConstrs().Where(r=>r.IISConstr!=0)) Add(row.ConstrName);
            foreach(var variable in model.GetVars().Where(v=>v.IISLB!=0 || v.IISUB!=0)) Add(variable.VarName);
        }
        catch(GRBException) { /* Budget-interrupted IIS is not a proof and may have no readable core. */ }
        return trains;
        void Add(string value)
        {
            foreach(System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(value,@"(?:^|[_:])t(\d+)(?:[_:]|$)"))
                trains.Add(int.Parse(match.Groups[1].Value,System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}

internal static class Stages45SolvePolicy
{
    internal static FirstIncumbentTrackingCallback ConfigureCallback(GRBModel model,bool stopAtFirstJoint,bool hasJoint)
    {
        var callback=new FirstIncumbentTrackingCallback(stopAfterFirstIncumbent:stopAtFirstJoint || !hasJoint);
        model.SetCallback(callback);return callback;
    }
}
