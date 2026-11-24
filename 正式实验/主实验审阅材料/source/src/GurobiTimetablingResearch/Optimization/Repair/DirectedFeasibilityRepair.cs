using Gurobi;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

internal sealed record DirectedRepairOptions(double Seconds,string LogPath,bool Elastic=true,int Seed=0,
    bool StabilizeTimes=true,double? MovementRadiusSeconds=600,IReadOnlySet<string>? FreeStopIds=null,int MaxStopChanges=6,bool FeasibilityFirst=false,
    bool FixUnreleasedOrders=false,bool FixAllOrders=false,bool StopAtZeroElastic=true,bool CapturePropagationIis=false);
internal sealed record DirectedRepairResult(string Status,MasterSolutionSnapshot? Candidate,double? ElasticDeficitSeconds,
    double? UnknownSupportAmount,double BuildSeconds,double SolveSeconds,int NativeStatus,PartialDiscreteCompileAudit? Audit,
    IReadOnlyDictionary<string,double> ElasticRows,double PolishSeconds=0,int? PolishNativeStatus=null,
    IReadOnlyDictionary<string,double>? CompactObjectives=null,IReadOnlyDictionary<string,double>? PolishedObjectives=null,double TotalSeconds=0,
    DirectedWarmStartAudit? WarmStart=null,DirectedIisGuidance? IisGuidance=null,int EffectiveFreeSectionOrders=0,bool MacroTemplateReused=false,
    IReadOnlyDictionary<string,int>? FreeSectionOrdersByPair=null,DirectedRepairStageTiming? StageTiming=null,int EffectiveFreeStops=0,
    IReadOnlySet<string>? EffectiveFreeStopIds=null);
internal sealed record DirectedRepairStageTiming(double TemplateBuildSeconds,double TemplateCopySeconds,double SourcePreparationSeconds,
    double DecisionFixingSeconds,double ElasticConstructionSeconds,double ReductionSeconds,double CompileSeconds,DirectedReductionSummary? Reduction);
internal sealed record DirectedReductionSummary(int SourceVariableCount,int InitiallyFixedVariableCount,int NewlyFixedVariableCount,
    int RemainingFreeDiscreteCount,int Passes,bool ReachedFixedPoint,double Seconds,int ImplicationRowCount);
internal static class DirectedFeasibilityRepair
{
    /// <summary>Include both adjacent section relations of each realized anchor reversal in sparse repair.</summary>
    /// <param name="instance">Original routes supplying each visit's adjacent sections.</param>
    /// <param name="edges">Already requested sparse relationships.</param>
    /// <param name="witnesses">Physical, headway-consistent anchor reversals.</param>
    /// <returns>The deduplicated section requests for the model template.</returns>
    internal static IReadOnlyList<SectionConflictEdgeRequest> IncludeAnchorReversals(TimetablingInstance instance,
        IReadOnlyList<SectionConflictEdgeRequest> edges, IReadOnlyList<OvertakingStopProjection> witnesses)
    {
        if (witnesses.Count == 0) return edges;

        // Materialized unreleased orders are fixed on the local copy and consume one flip.
        var outgoingSectionsMap = instance.Trains.SelectMany(train => train.OrderedStops.Zip(train.OrderedStops.Skip(1)))
            .ToDictionary(leg => leg.First.StopId, leg => leg.Second.IncomingSectionName, StringComparer.OrdinalIgnoreCase);
        return edges.Concat(witnesses.SelectMany(witness => new SectionConflictEdgeRequest[]
            {
                new(instance.StopsById[witness.FirstStopId].IncomingSectionName!, witness.FirstTrainIndex, witness.SecondTrainIndex),
                new(outgoingSectionsMap[witness.FirstStopId]!, witness.FirstTrainIndex, witness.SecondTrainIndex)
            }))
            .DistinctBy(edge => InitialConflictGraphBuilder.BuildCanonicalPairKey(edge.SectionName, edge.FirstTrainIndex, edge.SecondTrainIndex),
                StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>Build and solve a bounded repair with saved physical reversals included in the order graph.</summary>
    /// <param name="instance">Complete original instance.</param>
    /// <param name="candidate">Anchor timetable with actual event times in seconds.</param>
    /// <param name="diagnosis">Complete-graph diagnosis of the anchor.</param>
    /// <param name="edges">Requested sparse section relations.</param>
    /// <param name="releasedPairs">Pairs released on their originally requested sections and used for station-event decisions.</param>
    /// <param name="pattern">Target binary stopping pattern keyed by visit.</param>
    /// <param name="policy">Service preferences used for candidate evaluation.</param>
    /// <param name="options">Time budget in seconds, repair bounds and output options.</param>
    /// <param name="micro">Optional promoted station components.</param>
    /// <param name="timeCuts">Optional conditional macro time constraints.</param>
    /// <param name="microRepair">Whether this is a promoted resource repair.</param>
    /// <param name="fixFeedbackOrders">Whether to fix saved feedback event orders.</param>
    /// <param name="modelCache">Optional borrowed same-run template cache.</param>
    /// <param name="localReleasedEdges">Optional section-scoped neighborhood. Its edges are released in addition to explicit pair-wide requests.</param>
    /// <returns>Repair outcome and diagnostics, without changing the cached template.</returns>
    internal static DirectedRepairResult Solve(TimetablingInstance instance,MasterSolutionSnapshot candidate,DirectedDiagnosis diagnosis,
        IReadOnlyList<SectionConflictEdgeRequest> edges,IReadOnlySet<string> releasedPairs,IReadOnlyDictionary<string,int> pattern,
        ServiceWindowPolicy policy,DirectedRepairOptions options,IReadOnlyList<PromotedMicroComponent>? micro=null,
        IReadOnlyList<ConditionalMacroTimeCut>? timeCuts=null,bool microRepair=false,bool fixFeedbackOrders=false,DirectedRepairModelCache? modelCache=null,
        IReadOnlyCollection<SectionConflictEdgeRequest>? localReleasedEdges = null)
    {
        if(!double.IsFinite(options.Seconds) || options.Seconds<=0 || options.Seed<0) throw new ArgumentException("Invalid repair budget.");
        if(options.MovementRadiusSeconds is double radius && (!double.IsFinite(radius) || radius<0))
            throw new ArgumentException("Invalid temporary retiming radius.");
        BottleneckExperimentControls.ValidatePattern(instance,pattern);
        if(options.MaxStopChanges<0 || options.FreeStopIds?.Any(id=>!instance.StopsById.ContainsKey(id))==true)
            throw new ArgumentException("Invalid local stop neighborhood.");
        var clock=System.Diagnostics.Stopwatch.StartNew();double? builtSeconds=null;DirectedWarmStartAudit? warmStart=null;DirectedIisGuidance? iisGuidance=null;
        Dictionary<string,string> fixedPairByVariable=new(StringComparer.Ordinal);
        int effectiveFreeSectionOrders=0,effectiveFreeStops=0;
        Dictionary<string,int> freeByPair=new(StringComparer.Ordinal);
        HashSet<string> freeStopIds=new(StringComparer.OrdinalIgnoreCase);
        double sourceSeconds=0,fixSeconds=0,elasticSeconds=0,reductionSeconds=0,compileSeconds=0;
        FixedDiscreteReductionAudit? reduction=null;
        double Remaining()=>options.Seconds-clock.Elapsed.TotalSeconds;
        timeCuts ??= [];
        var stationEdges=SavedJointSnapshotVerifier.ResolveSavedStationEdges(instance,candidate)
            .Concat(CompleteMacroFeedbackRepairSolver.ResolveConditionalStationEdges(instance,timeCuts))
            .DistinctBy(BasicMilpModelBuilder.BuildStationEventPrecedenceKey).ToArray();
        var releasedSectionKeys = DirectedRepairOrderPolicy.ResolveSectionKeys(edges, releasedPairs, localReleasedEdges);
        var repairEdges = IncludeAnchorReversals(instance, edges.Concat(localReleasedEdges ?? []).Distinct().ToArray(), diagnosis.Witnesses);
        var witnessedOrderKeysSet = DirectedRepairOrderPolicy.AnchorGuardKeys(diagnosis.Witnesses);
        bool cacheEligible=modelCache is not null && !microRepair && (micro?.Count??0)==0 && timeCuts.Count==0;
        using var lease=cacheEligible?modelCache!.Acquire(instance,repairEdges,policy,stationEdges):null;
        using var owned=lease is null?new BasicMilpModelBuilder(instance,neighborRadius:0,additionalSectionEdges:repairEdges,
            serviceWindows:policy,screenTemporalPrecedence:true,preserveOmittedOvertakingSupport:true,promotedMicroComponents:micro,
            stationEventEdges:stationEdges).Build():null;
        var source=lease?.Source??owned!;sourceSeconds=clock.Elapsed.TotalSeconds;
        var positions=instance.TrainStops.ToDictionary(s=>(s.TrainIndex,s.StationOrder));
        source.Model.Set(GRB.IntParam.OutputFlag,0);
        CompleteMacroFeedbackRepairSolver.AddConditionalTimeCuts(source,timeCuts);
        foreach(var edge in stationEdges)
        {
            string key=BasicMilpModelBuilder.BuildStationEventPrecedenceKey(edge);
            var first=positions[(edge.FirstTrainIndex,edge.FirstStationOrder)];
            var second=positions[(edge.SecondTrainIndex,edge.SecondStationOrder)];
            double a=edge.FirstSide==StationEventSide.Incoming?candidate.ArrivalSecondsByStopId[first.StopId]:candidate.DepartureSecondsByStopId[first.StopId];
            double b=edge.SecondSide==StationEventSide.Incoming?candidate.ArrivalSecondsByStopId[second.StopId]:candidate.DepartureSecondsByStopId[second.StopId];
            int value=candidate.StationEventPrecedenceByKey.TryGetValue(key,out int saved)?saved:a<=b?0:1;
            var v=source.StationEventPrecedenceVars[key];v.Start=value;
            if(DirectedRepairOrderPolicy.FixStation(edge.FirstTrainIndex,edge.SecondTrainIndex,releasedPairs,options.FixUnreleasedOrders,options.FixAllOrders,fixFeedbackOrders))
            {
                if(value<v.LB || value>v.UB) return Empty("UnknownRestrictedStationOrder",0,null);
                if(v.LB!=v.UB) fixedPairByVariable[v.VarName]=Pair(edge.FirstTrainIndex,edge.SecondTrainIndex);
                v.LB=v.UB=value;
            }
        }
        var changedTrains=instance.TrainStops.Where(s=>pattern[s.StopId]!=candidate.StopByStopId[s.StopId]).Select(s=>s.TrainIndex).ToHashSet();
        GRBLinExpr stopChanges=new();
        foreach(var p in pattern)
        {
            var v=source.StopVars[p.Key];if(p.Value<v.LB || p.Value>v.UB) throw new InvalidDataException("Repair stop fixation conflicts with hard domain.");
            v.Start=p.Value;
            if(options.FreeStopIds?.Contains(p.Key)==true)
            {
                if(p.Value==0) stopChanges.AddTerm(1,v);else { stopChanges.AddConstant(1);stopChanges.AddTerm(-1,v); }
            }
            else v.LB=v.UB=p.Value;
        }
        if(options.FreeStopIds is { Count:>0 })
        {
            source.Model.AddConstr(stopChanges,GRB.LESS_EQUAL,options.MaxStopChanges,"repair_local_stop_changes");
            source.Model.SetObjectiveN(stopChanges,source.Model.NumObj,6,1,0,0,"repair_stop_changes");
        }
        foreach(var relation in source.SectionConflictCandidates)
        {
            string key=BasicMilpModelBuilder.BuildSectionPrecedenceKey(relation);
            var v=source.SectionPrecedenceVars[key];
            int value=diagnosis.SuggestedOrders.TryGetValue(key,out int suggested)?suggested:
                candidate.SectionPrecedenceByKey.GetValueOrDefault(key);
            v.Start=value;
            bool isAnchorGuard = witnessedOrderKeysSet.Contains(key);
            bool release = DirectedRepairOrderPolicy.ReleaseSection(key, relation.FirstTrainIndex, relation.SecondTrainIndex, candidate,
                releasedSectionKeys, changedTrains, options.FixUnreleasedOrders || localReleasedEdges is not null,
                options.FixAllOrders, isAnchorGuard);
            if(!release)
            {
                // Temporary search fixation only. Missing sparse metadata does not
                // authorize reopening every accumulated guard relation.
                int stable;
                if (isAnchorGuard) stable = diagnosis.SuggestedOrders[key];
                else if(candidate.SectionPrecedenceByKey.TryGetValue(key,out int saved)) stable=saved;
                else
                {
                    // Sparse input orders are completed from the anchor's physical departure order.
                    var a=positions[(relation.FirstTrainIndex,relation.FirstStopOrder)];
                    var b=positions[(relation.SecondTrainIndex,relation.SecondStopOrder)];
                    stable=candidate.DepartureSecondsByStopId[a.StopId]<=candidate.DepartureSecondsByStopId[b.StopId]?0:1;
                }
                if(v.LB<=stable && stable<=v.UB)
                {
                    if(v.LB!=v.UB) fixedPairByVariable[v.VarName]=Pair(relation.FirstTrainIndex,relation.SecondTrainIndex);
                    v.LB=v.UB=stable;v.Start=stable;
                }
                else
                {
                    iisGuidance=new(new HashSet<string>{Pair(relation.FirstTrainIndex,relation.SecondTrainIndex)},new HashSet<string>(),false);
                    return Empty("UnknownRestrictedFixedOrderDomain",0,null);
                }
            }
        }
        foreach(var p in source.ArrivalVars) p.Value.Start=candidate.ArrivalSecondsByStopId[p.Key];
        foreach(var p in source.DepartureVars) p.Value.Start=candidate.DepartureSecondsByStopId[p.Key];
        source.Model.Update();
        var originalTimeBounds=source.ArrivalVars.Concat(source.DepartureVars).Select(p=>new { Name=p.Value.VarName,StopId=p.Key,Lower=p.Value.LB,Upper=p.Value.UB })
            .ToDictionary(p=>p.Name,StringComparer.Ordinal);
        if((options.Elastic || microRepair) && options.StabilizeTimes && !ApplyTimeStability(source,candidate,options.MovementRadiusSeconds))
            return Empty("UnknownRestrictedTimeDomain",0,null);
        source.Model.Update();
        var temporaryTimeBounds=source.ArrivalVars.Concat(source.DepartureVars).ToDictionary(p=>p.Value.VarName,p=>
            new DirectedTemporaryTimeBound(p.Key,p.Value.LB>originalTimeBounds[p.Value.VarName].Lower+TimetableTimePolicy.NumericToleranceSeconds,
                p.Value.UB<originalTimeBounds[p.Value.VarName].Upper-TimetableTimePolicy.NumericToleranceSeconds),StringComparer.Ordinal);
        fixSeconds=clock.Elapsed.TotalSeconds-sourceSeconds;
        Dictionary<string,GRBVar> elastic=[];GRBLinExpr slackObjective=new(),supportObjective=new();
        source.Model.Update();
        if(options.Elastic)
        {
            foreach(var row in source.Model.GetConstrs().Where(r=>r.ConstrName.StartsWith("headway_",StringComparison.Ordinal) ||
                r.ConstrName.StartsWith("arrival_headway_",StringComparison.Ordinal) || r.ConstrName.StartsWith("station_headway_",StringComparison.Ordinal)))
            {
                if(row.Sense!=GRB.GREATER_EQUAL) throw new InvalidOperationException("Unexpected headway row orientation.");
                var slack=source.Model.AddVar(0,GRB.INFINITY,0,GRB.CONTINUOUS,"repair_slack_"+row.ConstrName);
                elastic.Add(row.ConstrName,slack);source.Model.ChgCoeff(row,slack,1);slackObjective.AddTerm(1,slack);
            }
            source.Model.Update();
            foreach(var v in source.Model.GetVars().Where(v=>v.VarName.StartsWith("unknown_overtaking_support_",StringComparison.Ordinal))) supportObjective.AddTerm(1,v);
            int count=source.Model.NumObj;
            source.Model.SetObjectiveN(slackObjective,count,7,1,0,0,"repair_headway_deficit");
            source.Model.SetObjectiveN(supportObjective,count+1,6,1,0,0,"repair_unknown_waiting_support");
        }
        source.Model.Update();
        if(Remaining()<=0) return Empty("UnknownBudgetDuringBuild",0,null);
        elasticSeconds=clock.Elapsed.TotalSeconds-sourceSeconds-fixSeconds;
        double reduceStart=clock.Elapsed.TotalSeconds;
        try { reduction=FixedDiscreteModelReducer.Apply(source.Model,budgetSeconds:Math.Min(2,Remaining())); }
        catch(FixedDiscreteDomainContradictionException ex)
        {
            Stages45Runner.Write(Path.GetDirectoryName(options.LogPath)!,Path.GetFileName(options.LogPath)+".domain.json",
                new { status="RestrictedPropagationContradiction",detail=ex.Message,globalInfeasibilityProof=false });
            // Reducer only applies propagated bounds after successful completion.
            // Keep the untouched temporary model and ask the native solver for causal IIS.
            if(!options.CapturePropagationIis) return Empty("UnknownRestrictedDomainContradiction",0,null);
        }
        reductionSeconds=clock.Elapsed.TotalSeconds-reduceStart;
        source.Model.Update();
        effectiveFreeSectionOrders=source.SectionPrecedenceVars.Values.Count(v=>v.LB!=v.UB);
        freeByPair=source.SectionConflictCandidates.GroupBy(c=>Pair(c.FirstTrainIndex,c.SecondTrainIndex))
            .ToDictionary(g=>g.Key,g=>g.Count(c=>{var v=source.SectionPrecedenceVars[BasicMilpModelBuilder.BuildSectionPrecedenceKey(c)];return v.LB!=v.UB;}),StringComparer.Ordinal);
        freeStopIds=source.StopVars.Where(p=>p.Value.LB!=p.Value.UB).Select(p=>p.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        effectiveFreeStops=freeStopIds.Count;
        double compileStart=clock.Elapsed.TotalSeconds;
        using var compact=new PartialDiscreteModelCompiler(source.Model);
        compileSeconds=clock.Elapsed.TotalSeconds-compileStart;
        double buildSeconds=clock.Elapsed.TotalSeconds;builtSeconds=buildSeconds;
        if(Remaining()<=0) return Empty("UnknownBudgetDuringCompile",0,compact.Audit);
        if(changedTrains.Count>0 && options.FreeStopIds is not { Count:>0 })
        {
            warmStart=DirectedStopWarmStart.Prepare(compact.Model,Math.Min(60,Remaining()*.15),options.LogPath+".warmstart.log");
            Stages45Runner.Write(Path.GetDirectoryName(options.LogPath)!,Path.GetFileName(options.LogPath)+".warmstart.json",warmStart);
            if(Remaining()<=0) return Empty("UnknownBudgetDuringWarmStart",0,compact.Audit);
        }
        compact.Model.Set(GRB.IntParam.Threads,4);compact.Model.Set(GRB.IntParam.Seed,options.Seed);
        compact.Model.Set(GRB.IntParam.MIPFocus,1);compact.Model.Set(GRB.DoubleParam.TimeLimit,Math.Max(.01,Remaining()-Math.Min(30,options.Seconds*.25)));
        if(options.FeasibilityFirst) compact.Model.Set(GRB.IntParam.SolutionLimit,1);
        compact.Model.Set(GRB.IntParam.OutputFlag,1);compact.Model.Set(GRB.IntParam.LogToConsole,0);compact.Model.Set(GRB.StringParam.LogFile,options.LogPath);
        var progress=new DirectedRepairProgress(compact.Model.GetVars().Where(v=>v.VarName.StartsWith("repair_slack_",StringComparison.Ordinal) ||
            v.VarName.StartsWith("unknown_overtaking_support_",StringComparison.Ordinal)).ToArray(),options.Elastic && options.StopAtZeroElastic,options.LogPath+".live.json");
        compact.Model.SetCallback(progress);
        var solve=System.Diagnostics.Stopwatch.StartNew();compact.Model.Optimize();solve.Stop();
        if(progress.Error is not null) throw new InvalidOperationException(progress.Error);
        if(compact.Model.SolCount==0)
        {
            int native=compact.Model.Status;
            if(native==GRB.Status.INFEASIBLE)
            {
                var iis=DirectedRestrictedIis.Capture(compact.Model,source.Model,Math.Max(0,Math.Min(30,Remaining())),
                    new { options.MovementRadiusSeconds,fixFeedbackOrders,patternKey=ConflictGuidedQuotaExchange.Key(pattern),releasedPairs,
                        microComponentIds=(micro ?? []).Select(c=>c.ComponentId),conditionalCutIds=timeCuts.Select(c=>c.CutId) });
                Stages45Runner.Write(Path.GetDirectoryName(options.LogPath)!,Path.GetFileName(options.LogPath)+".iis.json",iis);
                iisGuidance=DirectedIisGuidance.Analyze(iis,fixedPairByVariable,temporaryTimeBounds,
                    source.StopVars.ToDictionary(p=>p.Value.VarName,p=>p.Key,StringComparer.Ordinal));
            }
            return Empty(native==GRB.Status.INFEASIBLE?"UnknownRestrictedInfeasible":"UnknownNoRepairIncumbent",native,compact.Audit,solve.Elapsed.TotalSeconds);
        }
        if(Remaining()<=0) return Empty("UnknownBeforeRepairCarrier",compact.Model.Status,compact.Audit,solve.Elapsed.TotalSeconds);
        var compactObjectives=ObjectiveValues(compact.Model);
        var polish=System.Diagnostics.Stopwatch.StartNew();
        PrepareRepairCarrier(source.Model,compact);
        ConfigurePolish(source.Model,Math.Max(.01,Remaining()));
        source.Model.Set(GRB.IntParam.OutputFlag,1);source.Model.Set(GRB.IntParam.LogToConsole,0);
        source.Model.Set(GRB.StringParam.LogFile,options.LogPath+".polish.log");source.Optimize();polish.Stop();
        if(!source.HasIncumbentSolution) return Empty("UnknownRepairCarrier",compact.Model.Status,compact.Audit,solve.Elapsed.TotalSeconds)
            with { PolishSeconds=polish.Elapsed.TotalSeconds,PolishNativeStatus=source.Model.Status,CompactObjectives=compactObjectives };
        if(source.RunningTimeSupplementVars.Values.Any(v=>!double.IsFinite(v.X) || v.X < -SolverNumericalValueNormalizer.BoundTolerance))
            return Empty("UnknownRepairNumerics",compact.Model.Status,compact.Audit,solve.Elapsed.TotalSeconds)
                with { PolishSeconds=polish.Elapsed.TotalSeconds,PolishNativeStatus=source.Model.Status,CompactObjectives=compactObjectives };
        var violations=elastic.Where(p=>p.Value.X>TimetableTimePolicy.NumericToleranceSeconds).ToDictionary(p=>p.Key,p=>p.Value.X);
        double unknown=source.Model.GetVars().Where(v=>v.VarName.StartsWith("unknown_overtaking_support_",StringComparison.Ordinal)).Sum(v=>v.X);
        return new("UnverifiedRepairCandidate",MasterSolutionSnapshot.Capture(source),violations.Values.Sum(),unknown,buildSeconds,
            solve.Elapsed.TotalSeconds,compact.Model.Status,compact.Audit,violations,polish.Elapsed.TotalSeconds,source.Model.Status,
            compactObjectives,ObjectiveValues(source.Model),clock.Elapsed.TotalSeconds,warmStart,EffectiveFreeSectionOrders:effectiveFreeSectionOrders,MacroTemplateReused:lease?.TemplateReused??false,FreeSectionOrdersByPair:freeByPair,StageTiming:Timing(),EffectiveFreeStops:effectiveFreeStops,EffectiveFreeStopIds:freeStopIds);
        DirectedRepairStageTiming Timing()=>new(lease?.TemplateBuildSeconds??sourceSeconds,lease?.TemplateCopySeconds??0,sourceSeconds,
            fixSeconds,elasticSeconds,reductionSeconds,compileSeconds,reduction is null?null:new(reduction.SourceVariableCount,reduction.InitiallyFixedVariableCount,
                reduction.NewlyFixedVariableCount,reduction.RemainingFreeDiscreteCount,reduction.Passes,reduction.ReachedFixedPoint,reduction.Seconds,reduction.ImplicationRows.Count));
        DirectedRepairResult Empty(string status,int native,PartialDiscreteCompileAudit? audit,double seconds=0)=>
            new(status,null,null,null,builtSeconds??clock.Elapsed.TotalSeconds-seconds,seconds,native,audit,new Dictionary<string,double>(),TotalSeconds:clock.Elapsed.TotalSeconds,WarmStart:warmStart,IisGuidance:iisGuidance,EffectiveFreeSectionOrders:effectiveFreeSectionOrders,MacroTemplateReused:lease?.TemplateReused??false,FreeSectionOrdersByPair:freeByPair,StageTiming:Timing(),EffectiveFreeStops:effectiveFreeStops,EffectiveFreeStopIds:freeStopIds);
    }

    internal static string Pair(int a,int b)=>Math.Min(a,b)+"/"+Math.Max(a,b);
    internal static bool ApplyTimeStability(BasicMilpModel source,MasterSolutionSnapshot anchor,double? radius)
    {
        if(radius is double r && (!double.IsFinite(r) || r<0)) throw new ArgumentException("Invalid retiming radius.");
        source.Model.Update();GRBLinExpr movement=new();
        foreach(var stop in source.Instance.TrainStops)
        {
            if(stop.StopRole!=TrainStopRole.Origin && !Add(source.ArrivalVars[stop.StopId],anchor.ArrivalSecondsByStopId[stop.StopId])) return false;
            if(stop.StopRole!=TrainStopRole.Destination && !Add(source.DepartureVars[stop.StopId],anchor.DepartureSecondsByStopId[stop.StopId])) return false;
        }
        source.Model.Update();
        // Search-only stability: below safety/support but above business quality.
        // Joint-incumbent improvement and strict verification do not use this objective.
        source.Model.SetObjectiveN(movement,source.Model.NumObj,5,1,0,0,"repair_time_movement");
        return true;
        bool Add(GRBVar time,double value)
        {
            if(!double.IsFinite(value)) throw new InvalidDataException("Nonfinite retiming anchor.");
            if(radius is double window)
            {
                double lower=Math.Max(time.LB,value-window),upper=Math.Min(time.UB,value+window);
                if(lower>upper) return false;
                time.LB=lower;time.UB=upper;
            }
            var deviation=source.Model.AddVar(0,GRB.INFINITY,0,GRB.CONTINUOUS,"repair_move_"+time.VarName);
            source.Model.AddConstr(deviation-time,GRB.GREATER_EQUAL,-value,"repair_move_pos_"+time.VarName);
            source.Model.AddConstr(deviation+time,GRB.GREATER_EQUAL,value,"repair_move_neg_"+time.VarName);
            movement.AddTerm(1,deviation);return true;
        }
    }
    internal static void ConfigurePolish(GRBModel model,double seconds)
    {
        // Solve more accurately than the unchanged snapshot acceptance tolerance.
        // A numerically invalid incumbent is rejected, never clipped into feasibility.
        model.Set(GRB.DoubleParam.FeasibilityTol,1e-8);
        model.Set(GRB.DoubleParam.OptimalityTol,1e-8);
        model.Set(GRB.IntParam.NumericFocus,3);
        model.Set(GRB.IntParam.Threads,4);
        model.Set(GRB.DoubleParam.TimeLimit,seconds);
        model.Set(GRB.IntParam.SolutionLimit,int.MaxValue);
        model.Set(GRB.DoubleParam.MIPGap,0);
        model.Set(GRB.DoubleParam.MIPGapAbs,0);
    }
    internal static IReadOnlyDictionary<string,double> ObjectiveValues(GRBModel model)
    {
        int previous=model.Parameters.ObjNumber;Dictionary<string,double> values=[];
        for(int i=0;i<model.NumObj;i++) { model.Set(GRB.IntParam.ObjNumber,i);values[model.ObjNName]=model.ObjNVal; }
        model.Set(GRB.IntParam.ObjNumber,previous);return values;
    }
    internal static void PrepareRepairCarrier(GRBModel source,PartialDiscreteModelCompiler compact)
    {
        if(compact.Model.SolCount==0) throw new InvalidOperationException("No repair incumbent to polish.");
        var values=compact.Model.GetVars().ToDictionary(v=>v.VarName,v=>v.X,StringComparer.Ordinal);
        foreach(var variable in source.GetVars())
        {
            if(variable.LB==variable.UB) continue;
            if(!values.TryGetValue(variable.VarName,out double value))
                throw new InvalidOperationException("Missing nonfixed repair variable: "+variable.VarName);
            if(variable.VType!=GRB.CONTINUOUS)
            {
                value=Math.Round(value);
                source.AddConstr(variable,GRB.EQUAL,value,"repair_discrete::"+variable.VarName);
            }
            variable.Start=value;
        }
        // Search-only continuous polishing, never a fixed-candidate certificate.
        // The original source rows and domains remain; strict full verification is separate.
        source.Update();
    }
}

internal sealed class DirectedRepairProgress(GRBVar[] deficits,bool stopAtZero,string path):GRBCallback
{
    private double last=-30;
    internal string? Error { get; private set; }
    protected override void Callback()
    {
        try
        {
            if(where==GRB.Callback.MIPSOL && stopAtZero && deficits.Length>0 && GetSolution(deficits).Sum()<=TimetableTimePolicy.NumericToleranceSeconds) { Abort();return; }
            if(where!=GRB.Callback.MIP) return;
            double seconds=GetDoubleInfo(GRB.Callback.RUNTIME);if(seconds-last<30) return;last=seconds;
            File.WriteAllText(path,System.Text.Json.JsonSerializer.Serialize(new { seconds,nodes=GetDoubleInfo(GRB.Callback.MIP_NODCNT),
                solutions=GetIntInfo(GRB.Callback.MIP_SOLCNT),scope="RestrictedElasticRepair_NotGlobalBound" }));
        }
        catch(Exception ex) { Error=ex.ToString();Abort(); }
    }
}
