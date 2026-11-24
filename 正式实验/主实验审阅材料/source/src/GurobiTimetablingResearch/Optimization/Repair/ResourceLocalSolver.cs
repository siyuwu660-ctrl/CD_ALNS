using Gurobi;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

internal sealed record ResourceLocalOptions(double Seconds,string LogPath,int Seed=0,bool RequireZeroMicro=false,double DiagnosticSeconds=5,bool BoundedAlnsConstruction=false,ResourceGuidanceCaps? Guidance=null,bool ExploreStructure=false,bool AdaptiveQualityBudget=false,bool StopAtZeroMicro=false,double? QualityPatienceSeconds=null);
internal sealed record ResourceLocalResult(string Status,MasterSolutionSnapshot? Candidate,double? MicroSlack,
    double BuildSeconds,double SolveSeconds,int NativeStatus,PartialDiscreteCompileAudit? Audit,int MicroRows=0,int MicroIntegers=0,
    double PolishSeconds=0,double NumericalRecoverySeconds=0,bool NumericalRecoveryAttempted=false,ResourceNumericalAudit? NumericalAudit=null,IReadOnlyList<LocalMicroRepairSelectedAlternative>? SelectedResources=null,string? ConstructionLimitReason=null,string DiversityConstraint="None",IReadOnlyList<ResourceTightLink>? TightLinks=null);
internal static class ResourceLocalSolver
{
    internal static ResourceLocalResult Solve(ResourceMacroTemplate template,MasterSolutionSnapshot snapshot,
        ResourceNeighborhood neighborhood,ResourceConflictProfile profile,ResourceLocalOptions options,Func<double>? remaining=null)
    {
        if(!double.IsFinite(options.Seconds) || options.Seconds<=0 || options.Seed<0) throw new ArgumentOutOfRangeException(nameof(options));
        if(options.QualityPatienceSeconds is double patience && (!options.AdaptiveQualityBudget || !double.IsFinite(patience) || patience<=0))
            throw new ArgumentException("Quality patience requires a positive adaptive quality budget.");
        var clock=System.Diagnostics.Stopwatch.StartNew();
        double Left()=>Math.Min(options.Seconds-clock.Elapsed.TotalSeconds,remaining?.Invoke()??double.PositiveInfinity);
        void CheckBudget() { if(Left()<=0) throw new TimeoutException("Local resource construction deadline expired."); }
        try
        {
        using var local=template.Build(snapshot,neighborhood,Left);var model=local.Model;var view=template.View(local);
        Stages45Runner.Write(Path.GetDirectoryName(options.LogPath)!,Path.GetFileName(options.LogPath)+".projection.json",
            new { local.Audit,local.ConstantViolations });
        var instance=template.Instance;
        var stations=neighborhood.FreeTimeStopIds.Concat(neighborhood.FreeStopIds).Concat(neighborhood.SeedStopIds)
            .Select(id=>instance.StopsById[id].StationName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<StationMicroRepairAugmentation> augmentations=[];
        List<GRBVar> slacks=[];int microRows=0,microIntegers=0;GRBLinExpr violation=new();
        int index=0;
        foreach(string station in stations.Order(StringComparer.Ordinal))
        {
            CheckBudget();
            string prefix="resource_lns_"+index++;List<StationMicroRequirement> requirements=[];
            foreach(var train in instance.Trains) foreach(var stop in train.OrderedStops.Where(s=>s.StationName.Equals(station,StringComparison.OrdinalIgnoreCase)))
            {
                var variable=view.StopVars[stop.StopId];
                foreach(bool mode in new[]{false,true}.Where(mode=>(mode?1:0)>=variable.LB && (mode?1:0)<=variable.UB))
                    requirements.Add(StationLevelMicroFeasibilitySlave.BuildRequirementForMode(instance,train,stop,mode,
                        snapshot.ArrivalSecondsByStopId[stop.StopId],snapshot.DepartureSecondsByStopId[stop.StopId]));
            }
            var augmentation=StationMicroRepairModelAugmenter.Apply(view,requirements,StationEndpointOccupationPolicy.DefaultSeconds,variablePrefix:prefix,remaining:Left,
                maximumRows:options.BoundedAlnsConstruction?2_000_000-microRows:null,
                maximumAlternatives:options.BoundedAlnsConstruction?200_000-microIntegers:null);
            augmentations.Add(augmentation);
            CheckBudget();
            microRows+=augmentation.ConditionalConstraintCount;microIntegers+=augmentation.OrderVariableCount+augmentation.AlternativeCount;
            var saved=profile.Stations.GetValueOrDefault(station)?.Assignments.ToDictionary(a=>a.RequirementId,StringComparer.OrdinalIgnoreCase);
            foreach(var group in augmentation.ChoiceBindings.GroupBy(c=>c.RequirementId,StringComparer.OrdinalIgnoreCase))
            {
                var assignment=saved?.GetValueOrDefault(group.Key);
                var chosen=group.FirstOrDefault(c=>assignment is not null && c.IsStop==assignment.IsStop &&
                    c.TrackName==assignment.TrackName && c.IncomingRouteId==assignment.IncomingRoute?.RouteId && c.OutgoingRouteId==assignment.OutgoingRoute?.RouteId)
                    ??group.FirstOrDefault(c=>c.IsStop==(snapshot.StopByStopId[group.Key]==1))??group.First();
                foreach(var choice in group) choice.Variable.Start=choice==chosen?1:0;
            }
            foreach(var order in augmentation.OrderBindings)
            {
                double first=(order.FirstEventSide==StationEventSide.Incoming?snapshot.ArrivalSecondsByStopId:snapshot.DepartureSecondsByStopId)[order.FirstRequirementId]+order.FirstTimeOffsetSeconds;
                double second=(order.SecondEventSide==StationEventSide.Incoming?snapshot.ArrivalSecondsByStopId:snapshot.DepartureSecondsByStopId)[order.SecondRequirementId]+order.SecondTimeOffsetSeconds;
                order.Variable.Start=first<=second?1:0;
            }
            model.Update();
            foreach(var row in model.GetConstrs().Where(r=>r.ConstrName.StartsWith(prefix+"_",StringComparison.Ordinal) &&
                (r.ConstrName.EndsWith("_fwd",StringComparison.Ordinal) || r.ConstrName.EndsWith("_rev",StringComparison.Ordinal))))
            {
                CheckBudget();
                if(row.Sense!=GRB.GREATER_EQUAL) throw new InvalidOperationException("Resource separation row must be >=.");
                var expression=model.GetRow(row);double atAnchor=expression.Constant;
                for(int j=0;j<expression.Size;j++) atAnchor+=expression.GetCoeff(j)*expression.GetVar(j).Start;
                var slack=model.AddVar(0,options.RequireZeroMicro?0:GRB.INFINITY,0,GRB.CONTINUOUS,"resource_slack_"+slacks.Count);
                slack.Start=Math.Max(0,row.RHS-atAnchor);model.ChgCoeff(row,slack,1);violation.AddTerm(1,slack);slacks.Add(slack);
            }
        }
        model.Update();
        ResourceSpanObjectivePolicy.ApplyLocalPriorities(model,options.RequireZeroMicro);
        GRBLinExpr changes=new(),movement=new();
        foreach(string id in neighborhood.FreeStopIds)
        {
            if(snapshot.StopByStopId[id]==0) changes.AddTerm(1,view.StopVars[id]);
            else { changes.AddConstant(1);changes.AddTerm(-1,view.StopVars[id]); }
        }
        model.AddConstr(changes,GRB.LESS_EQUAL,neighborhood.MaxStopChanges,"resource_stop_change_limit");
        foreach(string id in neighborhood.FreeTimeStopIds)
        {
            var stop=instance.StopsById[id];
            if(stop.StopRole!=TrainStopRole.Origin) Move(view.ArrivalVars[id],snapshot.ArrivalSecondsByStopId[id]);
            if(stop.StopRole!=TrainStopRole.Destination) Move(view.DepartureVars[id],snapshot.DepartureSecondsByStopId[id]);
        }
        model.SetObjectiveN(violation,model.NumObj,6,1,0,0,"resource_micro_violation");model.Update();
        model.SetObjectiveN(changes,model.NumObj,2,1,0,0,"resource_stop_changes");model.Update();
        model.SetObjectiveN(movement,model.NumObj,1,1,0,0,"resource_time_movement");model.Update();
        string diversity=options.ExploreStructure?ResourceLocalSearchControls.RequireDiversity(model,changes,augmentations):"None";
        if(options.Guidance is not null)ResourceLocalSearchControls.ApplyGuidance(model,violation,options.Guidance);
        double built=clock.Elapsed.TotalSeconds;
        if(Left()<=0) return new("UnknownLocalBuildBudget",null,null,built,0,0,local.Audit,microRows,microIntegers);
        model.Set(GRB.IntParam.Threads,4);model.Set(GRB.IntParam.Seed,options.Seed);model.Set(GRB.IntParam.MIPFocus,1);
        // Match the unchanged full-candidate carrier's numerical contract. Fixed outside
        // events can carry sub-microsecond residuals accepted by that original verifier.
        ConfigureNumerics(model);
        model.Set(GRB.DoubleParam.MIPGap,0);model.Set(GRB.DoubleParam.MIPGapAbs,0);
        model.Set(GRB.DoubleParam.TimeLimit,Math.Max(.01,Left()*.85));
        model.Set(GRB.IntParam.OutputFlag,1);model.Set(GRB.IntParam.LogToConsole,0);model.Set(GRB.StringParam.LogFile,options.LogPath);
        var live=new DirectedRepairProgress(slacks.ToArray(),options.StopAtZeroMicro,options.LogPath+".live.json");
        ResourceQualityProgress? qualityLive=null;
        if(options.AdaptiveQualityBudget)
        {
            GRBLinExpr? span=null;
            for(int i=0;i<model.NumObj;i++) { model.Set(GRB.IntParam.ObjNumber,i);if(model.ObjNName=="global_span")span=model.GetObjective(i); }
            if(span is null)throw new InvalidOperationException("Missing span objective.");
            double anchorSpan=ServiceBusinessObjectiveEvaluator.Calculate(instance,snapshot,template.Policy).GlobalSpanSeconds;
            qualityLive=new(span,new(anchorSpan,Left()*.85,options.QualityPatienceSeconds),options.LogPath+".live.json");model.SetCallback(qualityLive);
        }
        else model.SetCallback(live);
        var solve=System.Diagnostics.Stopwatch.StartNew();model.Optimize();solve.Stop();int native=model.Status;
        if((qualityLive?.Error??live.Error) is string callbackError) throw new InvalidOperationException(callbackError);
        if(model.SolCount==0)
        {
            if(native==GRB.Status.INFEASIBLE && Left()>1)
            {
                model.Set(GRB.DoubleParam.TimeLimit,Math.Min(options.DiagnosticSeconds,Left()));model.ComputeIIS();
                model.Write(options.LogPath+".restricted.ilp");
                var rows=model.GetConstrs().Where(r=>r.IISConstr!=0).Take(24).Select(r=>new { name=r.ConstrName,rhs=r.RHS,sense=r.Sense,
                    terms=model.GetRow(r).Size }).ToArray();
                Stages45Runner.Write(Path.GetDirectoryName(options.LogPath)!,Path.GetFileName(options.LogPath)+".restricted_diagnostic.json",
                    new { minimal=model.IISMinimal==1,rows,local.ConstantViolations,searchGuidance=false,globalInfeasibilityProof=false });
            }
            return new(native==GRB.Status.INFEASIBLE?"UnknownRestrictedResourceInfeasible":"UnknownNoResourceIncumbent",
                null,null,built,solve.Elapsed.TotalSeconds,native,local.Audit,microRows,microIntegers);
        }
        // The search callback must not terminate numerical polishing.
        model.SetCallback(null);
        // Recover an endpoint misclassification before polish freezes the discrete
        // category. This is one attempt in the existing local problem/budget.
        if(Left()>0)
        {
            MasterSolutionSnapshot? boundaryCandidate=null;
            try { boundaryCandidate=template.Capture(local); }
            catch(ArgumentOutOfRangeException ex) when(ex.ParamName=="value")
            {
                // Let the existing numerical audit/retry handle this raw incumbent.
            }
            int boundaryRows=boundaryCandidate is null?0:
                ClosedQuotaCandidateRecovery.RestrictLocalBoundaryCategories(model,instance,boundaryCandidate);
            if(boundaryRows>0)
            {
                model.Update();model.Set(GRB.DoubleParam.TimeLimit,Left()*.5);
                model.Set(GRB.StringParam.LogFile,options.LogPath+".closed_quota.log");
                model.Optimize();
                if(model.SolCount==0)
                    return new("UnknownRestrictedClosedQuotaRecovery",null,null,built,solve.Elapsed.TotalSeconds,
                        model.Status,local.Audit,microRows,microIntegers);
            }
        }
        // Preserve the best found assignment while polishing continuous decisions at strict numerical tolerances.
        double polishSeconds=0;
        if(Left()>0.1)
        {
            var values=model.GetVars().Select(v=>(Variable:v,Value:v.X)).ToArray();
            foreach(var p in values)
            {
                p.Variable.Start=p.Value;
                if(p.Variable.VType!=GRB.CONTINUOUS) p.Variable.LB=p.Variable.UB=Math.Round(p.Value);
            }
            DirectedFeasibilityRepair.ConfigurePolish(model,Math.Max(.01,Left()));
            model.Set(GRB.DoubleParam.FeasibilityTol,1e-6);
            model.Set(GRB.StringParam.LogFile,options.LogPath+".polish.log");var polish=System.Diagnostics.Stopwatch.StartNew();model.Optimize();polish.Stop();polishSeconds=polish.Elapsed.TotalSeconds;
        }
        var beforeNumerics=ResourceNumericalAudit.Read(model,template,local);var numerical=beforeNumerics;
        bool retried=false;double recoverySeconds=0;
        if(!numerical.Passed && Left()>.1)
        {
            // Integer decisions are already fixed by the polish above. Re-solve only the
            // same continuous model/objectives; never clip values or relax original bounds.
            retried=true;model.Reset();
            model.Set(GRB.IntParam.Presolve,0);model.Set(GRB.IntParam.Aggregate,0);model.Set(GRB.IntParam.Method,1);
            model.Set(GRB.IntParam.NumericFocus,3);model.Set(GRB.DoubleParam.OptimalityTol,1e-9);
            model.Set(GRB.DoubleParam.FeasibilityTol,SolverNumericalValueNormalizer.BoundTolerance);
            model.Set(GRB.DoubleParam.TimeLimit,Math.Max(.01,Math.Min(30,Left())));
            model.Set(GRB.StringParam.LogFile,options.LogPath+".numeric_retry.log");
            var retryClock=System.Diagnostics.Stopwatch.StartNew();model.Optimize();retryClock.Stop();recoverySeconds=retryClock.Elapsed.TotalSeconds;
            numerical=ResourceNumericalAudit.Read(model,template,local);
        }
        Stages45Runner.Write(Path.GetDirectoryName(options.LogPath)!,Path.GetFileName(options.LogPath)+".numeric.json",
            new { before=beforeNumerics,after=numerical,retried,recoverySeconds,nativeStatus=model.Status });
        if(!numerical.Passed)
            return new("UnknownResourceNumerics",null,null,built,solve.Elapsed.TotalSeconds,native,local.Audit,microRows,microIntegers,
                polishSeconds,recoverySeconds,retried,numerical);
        MasterSolutionSnapshot candidate;
        try { candidate=template.Capture(local); }
        catch(ArgumentOutOfRangeException ex) when(ex.ParamName=="value")
        {
            // Defensive boundary for the shared nonnegative-value normalizer. Unexpected
            // programming/input exceptions still escape to the runner's visible error path.
            Stages45Runner.Write(Path.GetDirectoryName(options.LogPath)!,Path.GetFileName(options.LogPath)+".capture_rejected.json",new { error=ex.ToString() });
            return new("UnknownResourceNumerics",null,null,built,solve.Elapsed.TotalSeconds,native,local.Audit,microRows,microIntegers,
                polishSeconds,recoverySeconds,retried,numerical);
        }
        return new("UnverifiedResourceCandidate",candidate,slacks.Sum(s=>s.X),built,solve.Elapsed.TotalSeconds,native,
            local.Audit,microRows,microIntegers,polishSeconds,recoverySeconds,retried,numerical,
            augmentations.SelectMany(a=>a.CaptureSelectedAlternatives()).ToArray(),DiversityConstraint:diversity,
            TightLinks:options.AdaptiveQualityBudget?ResourceTightSupport.FromSolvedModel(model,view,Left):null);

        void Move(GRBVar variable,double anchor)
        {
            var delta=model.AddVar(0,GRB.INFINITY,0,GRB.CONTINUOUS,"resource_move_"+variable.VarName);delta.Start=0;
            model.AddConstr(delta-variable,GRB.GREATER_EQUAL,-anchor,"resource_move_pos_"+variable.VarName);
            model.AddConstr(delta+variable,GRB.GREATER_EQUAL,anchor,"resource_move_neg_"+variable.VarName);movement.AddTerm(1,delta);
        }
        }
        catch(ResourceNeighborhoodSizeException ex) { return new("UnknownLocalSizeLimit",null,null,clock.Elapsed.TotalSeconds,0,0,null,ConstructionLimitReason:ex.Message); }
        catch(TimeoutException) { return new("UnknownLocalBuildBudget",null,null,clock.Elapsed.TotalSeconds,0,0,null); }
    }

    internal static void ConfigureNumerics(GRBModel model)
    {
        model.Set(GRB.DoubleParam.FeasibilityTol,1e-6);model.Set(GRB.DoubleParam.IntFeasTol,1e-8);model.Set(GRB.IntParam.NumericFocus,2);
        // The fixed floating-point macro boundary is already accepted by the full carrier.
        // Initial multiobjective presolve can aggregate its tiny residuals inconsistently;
        // disable that layer only, retaining ordinary per-objective MIP presolve.
        model.Set(GRB.IntParam.MultiObjPre,0);
    }
}

internal sealed class ResourceNeighborhoodSizeException(string message):Exception(message);
