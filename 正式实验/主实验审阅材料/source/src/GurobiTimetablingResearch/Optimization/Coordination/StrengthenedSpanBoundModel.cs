using Gurobi;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

/// <summary>Original-domain necessary conditions. Never a jointly feasible primal solver.</summary>
internal static class StrengthenedSpanBoundModel
{
    internal const string Scope="OriginalData_GlobalSpan_Supplement5Percent_StrictResourceTime_ClosedQuotaBoundaryRelaxation_NoLocalFixings_ProgressiveResources";
    internal static BasicMilpModel Build(TimetablingInstance instance)
    {
        var model=new BasicMilpModelBuilder(instance,neighborRadius:int.MaxValue,boundTimingOuterRelaxation:true).Build();
        ResourceGlobalSpanBoundSolver.ConfigureSingleSpanObjective(model.Model);
        return model;
    }
    internal static double AddStructuralCuts(BasicMilpModel m,StructuralSpanBoundReport p,StructuralCapacityBoundReport c)
    {
        var native=m.Model;native.Update();
        var f=native.GetVarByName("global_first_departure");var l=native.GetVarByName("global_last_arrival");
        double floor=Math.Max(p.LowerBoundSeconds,c.LowerBoundSeconds??double.NegativeInfinity);
        native.AddConstr(l-f,GRB.GREATER_EQUAL,floor,"bound_structural_floor");
        native.AddConstr(f,GRB.LESS_EQUAL,p.FirstDepartureUpperSeconds,"bound_quota_first");
        native.AddConstr(l,GRB.GREATER_EQUAL,p.LastArrivalLowerSeconds,"bound_quota_last");
        int index=0;
        foreach(var w in c.Sections)
        {
            // Sort endpoint times: sum(e_i) >= n(F+p_min)+h*n(n-1)/2,
            // and <= n(L-q_min)-h*n(n-1)/2, independent of train order.
            int n=w.EventCount;double packing=w.EffectiveHeadwaySeconds*n*(n-1)/2;
            GRBLinExpr sum=new();
            foreach(var e in w.Events)sum.AddTerm(1,e.IsOriginSide?m.DepartureVars[e.StopId]:m.ArrivalVars[e.StopId]);
            native.AddConstr(sum-n*f,GRB.GREATER_EQUAL,n*w.MinimumPrefixSeconds+packing-1e-6,"bound_pack_first_"+index);
            native.AddConstr(n*l-sum,GRB.GREATER_EQUAL,n*w.MinimumSuffixSeconds+packing-1e-6,"bound_pack_last_"+index++);
        }
        native.Update();return floor;
    }
    internal static StationMicroRepairAugmentation AddStation(BasicMilpModel m,string station,int index,Func<double> remaining)
    {
        m.Model.Update();var instance=m.Instance;List<StationMicroRequirement> req=[];
        foreach(var stop in instance.TrainStops.Where(s=>s.StationName.Equals(station,StringComparison.OrdinalIgnoreCase)))
        {
            var variable=m.StopVars[stop.StopId];var train=instance.Trains.Single(t=>t.TrainIndex==stop.TrainIndex);
            foreach(bool mode in new[]{false,true}.Where(b=>(b?1:0)>=variable.LB && (b?1:0)<=variable.UB))
                req.Add(StationLevelMicroFeasibilitySlave.BuildRequirementForMode(instance,train,stop,mode,0,0));
        }
        int oldRows=m.Model.NumConstrs;
        string prefix="bound_station_"+index;
        var augmentation=StationMicroRepairModelAugmenter.Apply(m,req,StationEndpointOccupationPolicy.DefaultSeconds,variablePrefix:prefix,remaining:remaining,occupationOnlyForStops:false);
        m.Model.Update();
        foreach(var row in m.Model.GetConstrs().Skip(oldRows))
        {
            string name=row.ConstrName;
            if(!name.EndsWith("_fwd",StringComparison.Ordinal) && !name.EndsWith("_rev",StringComparison.Ordinal))continue;
            double tolerance=TimetableTimePolicy.BusinessRelaxationSeconds;
            if(name.Contains("_track_",StringComparison.Ordinal))
            {
                // Endpoints are point events; intermediate occupations remain actual dwell intervals.
                tolerance=TimetableTimePolicy.BusinessRelaxationSeconds;
            }
            if(row.Sense!=GRB.GREATER_EQUAL)throw new InvalidDataException("Unrecognized separation sense");
            row.RHS-=tolerance;
        }
        m.Model.Update();return augmentation;
    }

    /// <summary>Add necessary section order and waiting dwell relations with continuous event times.</summary>
    /// <param name="m">Borrowed bound model to augment.</param>
    /// <param name="remaining">Remaining construction budget in seconds.</param>
    /// <exception cref="TimeoutException">The construction budget expires.</exception>
    internal static void AddTolerantOrderCoupling(BasicMilpModel m,Func<double> remaining)
    {
        var native=m.Model;native.Update();int index=0;
        void Either(GRBVar a,GRBVar b,GRBVar c,GRBVar d,string name)
        {
            var z=native.AddVar(0,1,0,GRB.BINARY,name);
            native.AddConstr(b-a+Math.Max(0,a.UB-b.LB)*z,GRB.GREATER_EQUAL,0,name+"_a");
            native.AddConstr(d-c+Math.Max(0,c.UB-d.LB)*z,GRB.GREATER_EQUAL,0,name+"_b");
            native.AddConstr(a-b+Math.Max(0,b.UB-a.LB)*(1-z),GRB.GREATER_EQUAL,0,name+"_c");
            native.AddConstr(c-d+Math.Max(0,d.UB-c.LB)*(1-z),GRB.GREATER_EQUAL,0,name+"_d");
        }
        foreach(var edge in m.SectionConflictCandidates)
        {
            if(remaining()<=0)throw new TimeoutException("Order coupling build budget");
            var a=m.Instance.Trains.Single(t=>t.TrainIndex==edge.FirstTrainIndex);
            var b=m.Instance.Trains.Single(t=>t.TrainIndex==edge.SecondTrainIndex);
            var a0=a.OrderedStops.Single(s=>s.StationOrder==edge.FirstStopOrder);
            var a1=a.OrderedStops.Single(s=>s.StationOrder==edge.FirstEndStopOrder);
            var b0=b.OrderedStops.Single(s=>s.StationOrder==edge.SecondStopOrder);
            var b1=b.OrderedStops.Single(s=>s.StationOrder==edge.SecondEndStopOrder);
            if(!a0.StationName.Equals(b0.StationName,StringComparison.OrdinalIgnoreCase))continue;
            Either(m.DepartureVars[a0.StopId],m.DepartureVars[b0.StopId],m.ArrivalVars[a1.StopId],m.ArrivalVars[b1.StopId],"bound_section_cont_"+index++);
        }
        // Only a pair sharing both adjacent sections has a required order-continuity row.
        var sharedVisits = OvertakingStopProjectionPlanner.Plan(m.Instance, m.SectionConflictCandidates)
            .SelectMany(pair => new[] { (pair.FirstStopId, pair.SecondStopId), (pair.SecondStopId, pair.FirstStopId) }).ToHashSet();
        foreach(var group in m.Instance.TrainStops.Where(s=>s.StopRole==TrainStopRole.Intermediate).GroupBy(s=>s.StationName))
        {
            var stops=group.ToArray();var support=stops.ToDictionary(s=>s.StopId,_=>new GRBLinExpr());

            for(int i=0;i<stops.Length;i++)for(int j=i+1;j<stops.Length;j++)
            {
                if(remaining()<=0)throw new TimeoutException("Station coupling build budget");
                var a=stops[i];var b=stops[j];
                if (!sharedVisits.Contains((a.StopId, b.StopId))) continue;
                var aa=m.ArrivalVars[a.StopId];var ad=m.DepartureVars[a.StopId];var ba=m.ArrivalVars[b.StopId];var bd=m.DepartureVars[b.StopId];
                bool firstCanWait = StationOvertakingResourcePolicy.CanOvertake(m.Instance, a, b);
                bool secondCanWait = StationOvertakingResourcePolicy.CanOvertake(m.Instance, b, a);
                if (!firstCanWait && !secondCanWait)
                    Either(aa,ba,ad,bd,"bound_no_overtake_"+index++);
                if (firstCanWait) Support(a,aa,ad,ba,bd);
                if (secondCanWait) Support(b,ba,bd,aa,ad);
                void Support(TrainStopRecord stop,GRBVar arr,GRBVar dep,GRBVar otherArr,GRBVar otherDep)
                {
                    string name="bound_overtake_"+index++;var z=native.AddVar(0,1,0,GRB.BINARY,name);
                    native.AddConstr(otherArr-arr+Math.Max(0,arr.UB-otherArr.LB)*(1-z),GRB.GREATER_EQUAL,0,name+"_arr");
                    native.AddConstr(dep-otherDep+Math.Max(0,otherDep.UB-dep.LB)*(1-z),GRB.GREATER_EQUAL,0,name+"_dep");
                    support[stop.StopId].AddTerm(1,z);
                }
            }
            foreach(var stop in stops)
            {
                var normal=NormalDwellTimeResolver.Resolve(m.Instance.NormalDwellTimeRules,stop);
                int waiting=Math.Max(stop.MaxDwellSeconds,normal.NormalMaxDwellSeconds);
                var w=native.AddVar(0,1,0,GRB.BINARY,"bound_wait_"+stop.StopId);
                native.AddConstr(w-m.StopVars[stop.StopId],GRB.LESS_EQUAL,0,"bound_wait_stop_"+stop.StopId);
                native.AddConstr(w-support[stop.StopId],GRB.LESS_EQUAL,0,"bound_wait_support_"+stop.StopId);
                native.AddConstr(m.DepartureVars[stop.StopId]-m.ArrivalVars[stop.StopId]-normal.NormalMaxDwellSeconds*m.StopVars[stop.StopId]-
                    (waiting-normal.NormalMaxDwellSeconds)*w,GRB.LESS_EQUAL,0,"bound_wait_max_"+stop.StopId);
            }
        }
        native.Update();
    }
}
