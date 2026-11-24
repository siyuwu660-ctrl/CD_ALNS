using GurobiTimetablingResearch.Domain;
using GurobiTimetablingResearch.IO;

namespace GurobiTimetablingResearch.Optimization;

internal sealed record DirectedRunOptions(string OutputDirectory,double TotalSeconds=7200,double IterationSeconds=180,
    double ReserveSeconds=600,int Seed=0,int MaxRounds=500,bool StopAtFirstJoint=false,bool ComponentClosure=false,int TrialRepairRounds=3,bool CoupledMicroRepair=false,
    bool StopAtFirstCompleteMacro=false,bool BoundedMacroSearch=false,int MacroPairLimit=160,int MacroSweepOffset=0,
    DirectedMacroSearchState? MacroState=null,int MacroStagnationRounds=0,bool UseMacroAlns=false,MacroAlnsSettings? MacroAlns=null,ISet<string>? ClosedQuotaRecoveryAttempts=null);
internal sealed record DirectedMacroSearchState(SectionConflictEdgeRequest[] Edges,string[] TriedPatterns,
    int ExpansionLevel=0,int TimeoutStreak=0,int Stalls=0,MacroAlnsState? Alns=null,string[]? ClosedQuotaRecoverySignatures=null,
    double LastBuildSeconds=0,int ConsecutiveBuildBudgetFailures=0,int ActiveGuardVersion=0,MacroRepairContext? Context=null);
internal sealed record DirectedRunResult(string Status,bool CompleteMacroFeasible,bool JointFeasible,int Rounds,double Seconds,
    ServiceObjectiveVector? BestObjective,string? BestFingerprint);
internal sealed class DirectedStopTrialGate(double anchorScore,int followupRounds,double maxScoreMultiplier=double.PositiveInfinity)
{
    internal double AnchorScore { get; }=double.IsFinite(anchorScore) && anchorScore>=0?anchorScore:throw new ArgumentOutOfRangeException(nameof(anchorScore));
    internal int RemainingAttempts { get; private set; }=followupRounds is >=0 and <=100?followupRounds+1:throw new ArgumentOutOfRangeException(nameof(followupRounds));
    private readonly double trialMultiplier=!double.IsNaN(maxScoreMultiplier) && maxScoreMultiplier>=1?maxScoreMultiplier:throw new ArgumentOutOfRangeException(nameof(maxScoreMultiplier));
    internal string Observe(double? score)
    {
        if(RemainingAttempts<=0) throw new InvalidOperationException("Stop trial is already closed.");
        if(score.HasValue && (!double.IsFinite(score.Value) || score.Value<0)) throw new ArgumentOutOfRangeException(nameof(score));
        RemainingAttempts--;
        if(score<AnchorScore-TimetableTimePolicy.NumericToleranceSeconds) { RemainingAttempts=0;return "Commit"; }
        if(score>Math.Max(1,AnchorScore)*trialMultiplier) { RemainingAttempts=0;return "Rollback"; }
        return RemainingAttempts>0?"Continue":"Rollback";
    }
}
internal static class DirectedRepairRunner
{
    /// <summary>Run directed repair with whole-route pair moves and section-scoped local stop moves.</summary>
    /// <param name="instance">Original timetable instance and business constraints.</param>
    /// <param name="candidate">Initial timetable with event times in seconds.</param>
    /// <param name="options">Repair budgets in seconds, search controls and output path.</param>
    /// <param name="modelCache">Optional borrowed cache for immutable sparse model templates.</param>
    /// <returns>Repair status and the best strictly verified candidate, if any.</returns>
    /// <remarks>The run records its attempts in the configured output directory.</remarks>
    internal static DirectedRunResult Solve(TimetablingInstance instance,MasterSolutionSnapshot candidate,DirectedRunOptions options,DirectedRepairModelCache? modelCache=null)
    {
        if(!double.IsFinite(options.TotalSeconds) || !double.IsFinite(options.IterationSeconds) || !double.IsFinite(options.ReserveSeconds) ||
            options.TotalSeconds<=0 || options.IterationSeconds<=0 || options.ReserveSeconds<0 || options.ReserveSeconds>=options.TotalSeconds || options.MaxRounds<1 || options.Seed<0 || options.TrialRepairRounds is <0 or >100 ||
            options.MacroPairLimit is <1 or >10000 || options.MacroSweepOffset<0 || options.MacroStagnationRounds is <0 or >1000 ||
            options.MacroState is { ExpansionLevel:<0 or >4 } || options.MacroState is { TimeoutStreak:<0 } || options.MacroState is { Stalls:<0 } ||
            options.MacroState is {ConsecutiveBuildBudgetFailures:<0} || options.MacroState is { } invalid && (!double.IsFinite(invalid.LastBuildSeconds) || invalid.LastBuildSeconds<0))
            throw new ArgumentException("Invalid directed repair budget.");
        string output=Path.GetFullPath(options.OutputDirectory);
        if(Directory.Exists(output)) throw new IOException("Refusing to overwrite repair evidence.");
        Directory.CreateDirectory(output);var wall=System.Diagnostics.Stopwatch.StartNew();
        using var ownedCache=options.UseMacroAlns && modelCache is null?new DirectedRepairModelCache():null;
        modelCache??=ownedCache;
        double Remaining()=>options.TotalSeconds-wall.Elapsed.TotalSeconds;
        double SearchRemaining()=>Remaining()-options.ReserveSeconds;
        void Write(string name,object value)=>Stages45Runner.Write(output,name,value);
        var policy=ServiceWindowPolicy.Create(instance,new(true));
        Write("configuration.json",new { options,instance.Meta,policy,scope="RestrictedDirectedRepair",globalLowerBound=(double?)null,
            binarySha256=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(typeof(DirectedRepairRunner).Assembly.Location))),
            inputFingerprint=SnapshotFingerprint.Calculate(candidate),warning="Diagnosis and elastic incumbents are not full Macro or joint certificates." });
        var diagnosis=DirectedCandidateDiagnostics.Analyze(instance,candidate);Write("initial_diagnosis.json",diagnosis);
        var working=candidate;var bestWorking=candidate;var bestDiagnosis=diagnosis;
        bool sparseMacro=options.BoundedMacroSearch && options.UseMacroAlns;
        var contexts=new Dictionary<string,MacroRepairContext>(StringComparer.Ordinal);
        var initialContext=MacroRepairContext.For(candidate,options.MacroState?.Context) with {CoverageRequired=MacroRepairContext.NearFeasible(diagnosis)};
        contexts[initialContext.Fingerprint]=initialContext;
        var closedQuotaAttempts=options.ClosedQuotaRecoveryAttempts??new HashSet<string>(StringComparer.Ordinal);
        foreach(string signature in options.MacroState?.ClosedQuotaRecoverySignatures??[]) closedQuotaAttempts.Add(signature);
        var macroAlns=options.UseMacroAlns?new MacroAlnsPolicy(options.Seed,options.MacroState?.Alns,options.MacroAlns):null;
        if(options.BoundedMacroSearch)
            MasterSolutionSnapshotStore.Save(Path.Combine(output,"best_working.snapshot.json"),instance.Meta.InstanceName,bestWorking);
        DirectedJointProgress? bestRepairProgress=null,lastJointProgress=null;
        MasterSolutionSnapshot? best=null;ServiceObjectiveVector? bestObjective=null;
        var full=new InitialConflictGraphBuilder(instance,int.MaxValue).Build();
        var fullByKey=full.ToDictionary(BasicMilpModelBuilder.BuildSectionPrecedenceKey,StringComparer.OrdinalIgnoreCase);
        var fullByPair=full.GroupBy(c=>DirectedFeasibilityRepair.Pair(c.FirstTrainIndex,c.SecondTrainIndex)).ToDictionary(g=>g.Key,g=>g.ToArray());
        var projections=OvertakingStopProjectionPlanner.Plan(instance,full);
        Dictionary<string,SectionConflictEdgeRequest> edges=new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string,SectionConflictEdgeRequest> learnedGuards=new(StringComparer.OrdinalIgnoreCase);
        foreach(string key in candidate.SectionPrecedenceByKey.Keys)
        {
            if(!fullByKey.TryGetValue(key,out var relation)) throw new InvalidDataException("Unknown input section order key: "+key);
            if(!sparseMacro) Add(relation);
        }
        var cuts=StopTravelBudgetCutBuilder.Build(instance).AllCuts;
        Dictionary<string,PromotedMicroComponent> micro=[];HashSet<int> microTrains=[];HashSet<string> microStops=[];
        Dictionary<string,ConditionalMacroTimeCut> timeCuts=[];
        HashSet<string> triedPatterns=[ConflictGuidedQuotaExchange.Key(candidate.StopByStopId)];
        if(options.MacroState is { } state)
        {
            foreach(var edge in !sparseMacro || state.ActiveGuardVersion==1?state.Edges:[])
            {
                string key=BasicMilpModelBuilder.BuildSectionPrecedenceKey(edge.SectionName,edge.FirstTrainIndex,edge.SecondTrainIndex);
                if(!fullByKey.TryGetValue(key,out var relation)) throw new InvalidDataException("Invalid continuation edge: "+key);
                Add(relation);if(sparseMacro) learnedGuards[key]=edge;
            }
            triedPatterns.UnionWith(state.TriedPatterns);
        }
        HashSet<string> rejectedChecks=[];Dictionary<string,int> verificationAttempts=[];int stalls=0,round=0;bool complete=false;string status="UnknownBudget";
        DirectedStopTrialGate? trial=null;MasterSolutionSnapshot? trialAnchor=null;DirectedDiagnosis? trialAnchorDiagnosis=null;
        IReadOnlyDictionary<string,int>? trialPattern=null;
        // These sets survive conflict-closure rounds: a smaller residual must not
        // silently collapse a previously expanded neighborhood back to two pairs.
        HashSet<string> retainedReleased=new(StringComparer.Ordinal),solvedRestricted=new(StringComparer.Ordinal);
        var retainedLocalSectionEdges = new HashSet<SectionConflictEdgeRequest>();
        HashSet<int> focusTrains=[];int expansionLevel=0,timeoutStreak=0;
        double lastBuildSeconds=options.MacroState?.LastBuildSeconds??0;
        int buildBudgetFailures=options.MacroState?.ConsecutiveBuildBudgetFailures??0;
        int lastBestImprovementRound=0;
        if(options.MacroStagnationRounds>0 && options.MacroState is { } continuation)
        {
            expansionLevel=continuation.ExpansionLevel;timeoutStreak=continuation.TimeoutStreak;stalls=continuation.Stalls;
        }
        bool useOriginalTimeDomain=false;HashSet<string> dependencyStops=new(StringComparer.OrdinalIgnoreCase);
        var stopsByPosition=instance.TrainStops.ToDictionary(s=>(s.TrainIndex,s.StationOrder));
        try
        {
            while(SearchRemaining()>0 && round<options.MaxRounds)
            {
                if(options.BoundedMacroSearch && buildBudgetFailures>=3) {status="UnknownMacroBuildBudget";break;}
                if(options.BoundedMacroSearch && options.StopAtFirstCompleteMacro && options.MacroStagnationRounds>0 &&
                    round-lastBestImprovementRound>=options.MacroStagnationRounds &&
                    !(sparseMacro && contexts.TryGetValue(MacroRepairContext.Identity(bestWorking),out var protectedContext) && protectedContext.NeedsProtectedAttempt))
                { status="UnknownMacroStagnation";break; }
                if(SearchRemaining()<lastBuildSeconds+5) { status="UnknownBudget";break; }
                round++;double before=Score(diagnosis);int previousEdges=edges.Count,attemptedExpansionLevel=expansionLevel;
                bool microMode=best is null && (timeCuts.Count>0 || micro.Count>0);
                bool boundedMacro=options.BoundedMacroSearch && best is null && !microMode;
                var context=MacroRepairContext.For(working,contexts.GetValueOrDefault(MacroRepairContext.Identity(working))) with {CoverageRequired=MacroRepairContext.NearFeasible(diagnosis)};
                string[] relevantWaiting=MacroRepairDependencies.RelevantWaiting(diagnosis);
                var requiredAction=sparseMacro?context.RequiredAction(context.CoverageRequired):null;
                MacroAlnsAction? macroAction=boundedMacro && macroAlns is not null?trial is not null?new(MacroAlnsDirection.LegacyFallback,Math.Min(2,expansionLevel),true):
                    macroAlns.Select(stalls,diagnosis.UnresolvedLongDwellStopIds.Count>0 || relevantWaiting.Length>0,requiredAction):null;
                HashSet<string> requiredPairs=sparseMacro?MacroRepairDependencies.Required(diagnosis,context.FeedbackPairs):new(StringComparer.Ordinal);
                if(sparseMacro)
                {
                    edges.Clear();foreach(var guard in learnedGuards) edges.Add(guard.Key,guard.Value);
                    foreach(string pair in requiredPairs) LearnPair(pair);
                }
                int pairLimit=Math.Min(80,12+stalls*8);
                var released=diagnosis.Conflicts.GroupBy(c=>DirectedFeasibilityRepair.Pair(c.FirstTrainIndex,c.SecondTrainIndex))
                    .OrderByDescending(g=>g.Sum(c=>c.Kind=="headway"?c.Magnitude:180)).ThenBy(g=>g.Key,StringComparer.Ordinal)
                    .Take(pairLimit).Select(g=>g.Key).ToHashSet(StringComparer.Ordinal);
                var componentPairs=options.ComponentClosure?ConflictComponentPairs(diagnosis.Conflicts):[];
                foreach(string pair in componentPairs) released.Add(pair);
                focusTrains.UnionWith(diagnosis.Conflicts.SelectMany(c=>new[]{c.FirstTrainIndex,c.SecondTrainIndex}));
                focusTrains.UnionWith(microTrains);
                if(best is null && expansionLevel>0 && !microMode && !boundedMacro)
                {
                    if(expansionLevel>=2)
                    {
                        // Expand through temporal neighbors, not just the connected
                        // components of the remaining violations.
                        var frontier=full.Where(c=>focusTrains.Contains(c.FirstTrainIndex)!=focusTrains.Contains(c.SecondTrainIndex))
                            .Select(c=>new { Train=focusTrains.Contains(c.FirstTrainIndex)?c.SecondTrainIndex:c.FirstTrainIndex,
                                Distance=PairDistance(c) })
                            .GroupBy(x=>x.Train).OrderBy(g=>g.Min(x=>x.Distance)).ThenBy(g=>g.Key)
                            .Take(4).Select(g=>g.Key).ToArray();
                        focusTrains.UnionWith(frontier);
                    }
                    // Full incident guard edges cover the core/background boundary.
                    // Background/background omissions remain subject to full checking.
                    foreach(var group in fullByPair.Where(g=>g.Value.Any(c=>focusTrains.Contains(c.FirstTrainIndex) || focusTrains.Contains(c.SecondTrainIndex))))
                        released.Add(group.Key);
                }
                if(best is null && !microMode && !boundedMacro) released.UnionWith(retainedReleased);
                if(microMode)
                {
                    released.Clear();
                    if(expansionLevel>=1)
                    {
                        foreach(var cut in timeCuts.Values)
                            released.Add(DirectedFeasibilityRepair.Pair(instance.StopsById[cut.EarlierEvent.StopId].TrainIndex,instance.StopsById[cut.LaterEvent.StopId].TrainIndex));
                        foreach(var component in micro.Values)
                            foreach(int a in component.TrainIndices) foreach(int b in component.TrainIndices.Where(b=>b>a))
                                released.Add(DirectedFeasibilityRepair.Pair(a,b));
                    }
                    if(expansionLevel>=2)
                    {
                        // Open a bounded boundary first; a Micro core is not permission
                        // to release every pair incident on every conflicted train.
                        foreach(var pair in fullByPair.Where(g=>g.Value.Any(c=>microTrains.Contains(c.FirstTrainIndex) || microTrains.Contains(c.SecondTrainIndex)))
                            .Where(g=>!released.Contains(g.Key)).OrderBy(g=>g.Value.Min(PairDistance)).ThenBy(g=>g.Key,StringComparer.Ordinal)
                            .Take(12*(expansionLevel-1))) released.Add(pair.Key);
                    }
                    if(options.CoupledMicroRepair) AccumulateMicroReleases(released,retainedReleased);
                }
                foreach(string pair in released) AddPair(pair);
                // Add actual waiting witnesses before search, instead of waiting for exclusion closure to stall.
                foreach(var stop in diagnosis.SupportedLongDwellStopIds)
                {
                    var possible=WaitingWitnesses(diagnosis,stop);
                    if(possible.Any(p=>edges.ContainsKey(p.IncomingPrecedenceKey) && edges.ContainsKey(p.OutgoingPrecedenceKey))) continue;
                    var witness=possible.FirstOrDefault();if(witness is not null) LearnPair(DirectedFeasibilityRepair.Pair(witness.FirstTrainIndex,witness.SecondTrainIndex));
                }
                foreach(var stop in diagnosis.UnresolvedLongDwellStopIds.OrderByDescending(id=>diagnosis.StopPressure.GetValueOrDefault(id)).Take(sparseMacro?int.MaxValue:8))
                {
                    var potential=projections.Where(p=>p.FirstStopId==stop || p.SecondStopId==stop)
                        .OrderBy(p=>Math.Abs(working.ArrivalSecondsByStopId[p.FirstStopId==stop?p.SecondStopId:p.FirstStopId]-working.DepartureSecondsByStopId[stop]))
                        .FirstOrDefault(p=>boundedMacro || !edges.ContainsKey(p.IncomingPrecedenceKey) || !edges.ContainsKey(p.OutgoingPrecedenceKey));
                    if(potential is not null)
                    {
                        string pair=DirectedFeasibilityRepair.Pair(potential.FirstTrainIndex,potential.SecondTrainIndex);released.Add(pair);AddPair(pair);
                        if(sparseMacro || requiredPairs.Count<options.MacroPairLimit) requiredPairs.Add(pair);
                    }
                }
                if(!microMode && (microTrains.Count>0 || best is not null || released.Count==0))
                    foreach(string pair in RotatePairs(fullByPair.Where(g=>microTrains.Count==0 || g.Value.Any(c=>microTrains.Contains(c.FirstTrainIndex) || microTrains.Contains(c.SecondTrainIndex)))
                        .Select(g=>g.Key).ToArray(),round,12)) { released.Add(pair);AddPair(pair); }
                if(best is null && (!microMode || options.CoupledMicroRepair)) retainedReleased.UnionWith(released);
                ConflictQuotaProposal? exchange=null;
                var pattern=macroAction is {LegacyFallback:false}?working.StopByStopId:trialPattern??working.StopByStopId;
                IReadOnlySet<string>? freeStops=null;int stopChangeLimit=stalls>=5?8:6;
                MacroStopNeighborhood? macroStops=null;
                IReadOnlyList<SectionConflictEdgeRequest> localStopOrderEdges = [];
                if(options.CoupledMicroRepair && microMode && expansionLevel>=4)
                {
                    freeStops=DirectedStopNeighborhood.Build(instance,pattern,microStops.Concat(dependencyStops).ToHashSet(StringComparer.OrdinalIgnoreCase),
                        cuts,triedPatterns);
                    localStopOrderEdges = DirectedStopNeighborhood.SelectAdjacentOrderRelations(instance, working, full, freeStops, released);
                    retainedLocalSectionEdges.UnionWith(localStopOrderEdges);
                }
                if((macroAction is null || macroAction.LegacyFallback) && !(options.CoupledMicroRepair && microMode) && trial is null && (!microMode || expansionLevel>=4) && (stalls>=3 || round%4==0))
                {
                    var targetCore=micro.Values.OrderByDescending(c=>c.PromotionIteration)
                        .Select(c=>c.Requirements.Select(r=>r.RequirementId).Where(microStops.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase))
                        .FirstOrDefault(ids=>ids.Count>0);
                    var proposals=ExchangeProposals(instance,pattern,diagnosis.StopPressure,microStops,cuts,triedPatterns,best is not null,allowBridge:stalls>=5,focusStops:targetCore);
                    Write($"round_{round:D3}_exchange_proposals.json",proposals.Select(p=>new { p.ChangedStopIds,p.EstimatedGain,p.PatternKey }));
                    exchange=proposals.FirstOrDefault();
                    if(exchange is not null)
                    {
                        pattern=exchange.Stops;triedPatterns.Add(exchange.PatternKey);
                        if(options.TrialRepairRounds>0 && best is null && before>TimetableTimePolicy.NumericToleranceSeconds)
                        {
                            trial=new(before,options.TrialRepairRounds,boundedMacro?4:double.PositiveInfinity);trialAnchor=working;trialAnchorDiagnosis=diagnosis;trialPattern=pattern;
                        }
                    }
                }
                if(boundedMacro)
                {
                    var spanExtremes=instance.TrainStops.Where(s=>s.StopRole==TrainStopRole.Origin)
                        .OrderBy(s=>working.DepartureSecondsByStopId[s.StopId]).Take(2)
                        .Concat(instance.TrainStops.Where(s=>s.StopRole==TrainStopRole.Destination)
                            .OrderByDescending(s=>working.ArrivalSecondsByStopId[s.StopId]).Take(2))
                        .Select(s=>s.TrainIndex).ToHashSet();
                    var severity=diagnosis.Conflicts.GroupBy(c=>DirectedFeasibilityRepair.Pair(c.FirstTrainIndex,c.SecondTrainIndex))
                        .ToDictionary(g=>g.Key,g=>g.Sum(c=>c.Kind=="headway"?c.Magnitude:180));
                    var priorities=fullByPair.Select(p=>new MacroPairPriority(p.Key,p.Value[0].FirstTrainIndex,p.Value[0].SecondTrainIndex,
                        severity.GetValueOrDefault(p.Key)+(spanExtremes.Contains(p.Value[0].FirstTrainIndex) || spanExtremes.Contains(p.Value[0].SecondTrainIndex)?1:0),p.Value.Min(PairDistance))).ToArray();
                    released=DirectedMacroNeighborhood.Select(priorities,macroAction is {LegacyFallback:false}?macroAction.PairLimit:options.MacroPairLimit,macroAction is {LegacyFallback:false}?0:expansionLevel,
                        checked(round+options.MacroSweepOffset),requiredPairs);
                    if(macroAction is {LegacyFallback:false})
                    {
                        // Active relations are selected independently of full snapshot order metadata.
                        // Full physical diagnosis separates omitted conflicts after every candidate.
                        if(macroAction.Direction==MacroAlnsDirection.TimeOnly) released.Clear();
                        if(macroAction.Direction==MacroAlnsDirection.StopQuotaTimeOrder)
                        {
                            macroStops=MacroStopNeighborhood.Select(instance,working,diagnosis,context,released,
                                instance.TrainStops.Where(s=>spanExtremes.Contains(s.TrainIndex)).Select(s=>s.StopId),macroAction.Scale,SearchRemaining);
                            freeStops=macroStops.FreeStopIds;stopChangeLimit=6*(macroAction.Scale+1);
                        }
                        if(macroAction.Direction==MacroAlnsDirection.WaitingSupportTimeOrder)
                        {
                            released=MacroRepairDependencies.FillWaiting(requiredPairs,relevantWaiting,released,macroAction.PairLimit);
                        }
                    }
                    foreach(string pair in released) AddPair(pair);
                    retainedReleased.Clear();retainedReleased.UnionWith(released);
                }
                // Micro stage 3 is an explicit unchanged-stop control, with all
                // original hard windows retained but no temporary movement radius.
                double? movementRadius=expansionLevel switch { 0 or 1=>600,2=>1200,3 when !microMode=>2400,_=>null };
                if(macroAction is {LegacyFallback:false}) movementRadius=macroAction.RadiusSeconds;
                if(sparseMacro && context.OriginalTimeDomain) movementRadius=null;
                if(options.CoupledMicroRepair && microMode)
                {
                    useOriginalTimeDomain|=movementRadius is null;
                    if(useOriginalTimeDomain) movementRadius=null;
                }
                bool verifyInputBeforeRepair=round==1 && diagnosis.ReadyForStrictCheck;
                IReadOnlyCollection<SectionConflictEdgeRequest>? localReleasedEdges =
                    options.CoupledMicroRepair && microMode && (expansionLevel >= 4 || retainedLocalSectionEdges.Count > 0)
                        ? retainedLocalSectionEdges : null;
                var requestedEdges = edges.Values.ToArray();
                var releasedSectionKeys = DirectedRepairOrderPolicy.ResolveSectionKeys(requestedEdges, released, localReleasedEdges);
                string searchKey=SearchKey(instance,working,requestedEdges,released,pattern,movementRadius,
                    micro.Keys.Concat(timeCuts.Keys).Append("microStage="+(microMode?expansionLevel:-1))
                        .Append("freeStops="+string.Join(";",(freeStops ?? new HashSet<string>()).Order(StringComparer.Ordinal))+"/"+stopChangeLimit),
                        fixUnreleasedOrders:boundedMacro,fixAllOrders:macroAction?.Direction==MacroAlnsDirection.TimeOnly,
                        fixFeedbackOrders:microMode && expansionLevel==0,
                        localReleasedEdges: localReleasedEdges, anchorWitnesses: diagnosis.Witnesses);
                bool repeated=best is null && (sparseMacro?(context.ExhaustedSearches??[]).Contains(searchKey):!boundedMacro && solvedRestricted.Contains(searchKey));
                double iterationBudget=boundedMacro?DirectedRepairBudget.Iteration(SearchRemaining(),options.IterationSeconds,timeoutStreak,lastBuildSeconds,trial is not null):
                    Math.Min(SearchRemaining(),Math.Min(Math.Max(600,options.IterationSeconds),options.IterationSeconds*Math.Pow(2,Math.Min(timeoutStreak,2))));
                Write($"round_{round:D3}_plan.json",new { beforeScore=before,releasedPairs=released,releasedSectionKeys,addedRelations=edges.Count-previousEdges,
                    relations=edges.Count,fullDiagnosticRelations=full.Count,learnedGuardRelations=learnedGuards.Count,activeGuardVersion=sparseMacro?1:0,
                    requiredActionReason=requiredAction is null?null:context.PendingFeedback?context.FeedbackReason:"NearFeasibleCoverage",coverage=context,
                    nominalPairLimit=macroAction?.PairLimit??options.MacroPairLimit,mandatoryScaleUpgrade=requiredPairs.Count>(macroAction?.PairLimit??options.MacroPairLimit),componentClosurePairs=componentPairs,exchange=exchange?.ChangedStopIds,estimatedExchangeGain=exchange?.EstimatedGain,microComponents=micro.Count,microConflictStops=microStops,
                    expansionLevel,focusTrains,retainedReleaseCount=retainedReleased.Count,movementRadius,searchKey,skippedRepeatedOptimal=repeated,iterationBudget,verifyInputBeforeRepair,
                    freeStopIds=freeStops,macroStopScope=macroStops,stopChangeLimit,localStopOrderEdges,dependencyStops,useOriginalTimeDomain,
                    conditionalTimeCuts=timeCuts.Count,microMode,boundedMacro,macroAction,macroWeights=macroAlns?.Snapshot(),quotaViolations=diagnosis.QuotaViolationCount,quotaMagnitude=diagnosis.QuotaViolationMagnitude,overtakerCapViolations=diagnosis.OvertakerCapViolationCount,requiredPairs,unboundedOriginalStopControl=microMode && expansionLevel==3,
                    exchangeScoreScope="CoreHitMinusChangeCost_SearchProxy_NotMeasuredConflictReduction",
                    phase=verifyInputBeforeRepair?"VerifyUnchangedInput":microMode?(micro.Count==0?"ConditionalTimeFeedback":"CoreResourceRepair"):best is null?"FeasibilityRepair":"JointIncumbentImprovement",trialAnchorScore=trial?.AnchorScore,trialAttemptsRemaining=trial?.RemainingAttempts,remainingSeconds=Remaining() });
                if(SearchRemaining()<=0) break;
                if(repeated && expansionLevel>=4 && exchange is null && trial is null &&
                    !full.Any(c=>focusTrains.Contains(c.FirstTrainIndex)!=focusTrains.Contains(c.SecondTrainIndex)))
                { status="UnknownNeighborhoodExhausted";break; }
                // No invented solver status or zero slack certificate: an unchanged
                // diagnostic-clear input still has to pass the complete verifier.
                var repairClock=System.Diagnostics.Stopwatch.StartNew();
                var repair=verifyInputBeforeRepair?new DirectedRepairResult("UnknownInputAwaitingStrictVerification",working,null,null,0,0,0,null,new Dictionary<string,double>()):
                    repeated?new DirectedRepairResult("UnknownRepeatedOptimalNeighborhood",null,null,null,0,0,0,null,new Dictionary<string,double>()):
                    DirectedFeasibilityRepair.Solve(instance,working,diagnosis,requestedEdges,released,pattern,policy,
                    new(Math.Min(iterationBudget,SearchRemaining()),Path.Combine(output,$"round_{round:D3}.log"),Elastic:best is null && !microMode,
                        Seed:options.Seed,MovementRadiusSeconds:movementRadius,FreeStopIds:freeStops,MaxStopChanges:stopChangeLimit,
                        FeasibilityFirst:options.CoupledMicroRepair && microMode,FixUnreleasedOrders:boundedMacro,FixAllOrders:macroAction?.Direction==MacroAlnsDirection.TimeOnly,
                        StopAtZeroElastic:!sparseMacro,CapturePropagationIis:sparseMacro),micro.Values.ToArray(),timeCuts.Values.ToArray(),microMode,microMode && expansionLevel==0,boundedMacro?modelCache:null,localReleasedEdges);
                double localRepairSeconds=verifyInputBeforeRepair || repeated?0:repairClock.Elapsed.TotalSeconds;
                lastBuildSeconds=Math.Max(lastBuildSeconds,repair.BuildSeconds);
                if(options.CoupledMicroRepair && repair.IisGuidance is { } guidance)
                {
                    retainedReleased.UnionWith(guidance.ReleasedPairs);dependencyStops.UnionWith(guidance.StopIds);
                    useOriginalTimeDomain|=guidance.RemoveTemporaryRadius;
                    if(guidance.ReleasedPairs.Count>0) expansionLevel=Math.Max(1,expansionLevel);
                }
                bool exhausted=repair.NativeStatus==Gurobi.GRB.Status.OPTIMAL && repair.PolishNativeStatus==Gurobi.GRB.Status.OPTIMAL &&
                    (repair.ElasticDeficitSeconds>TimetableTimePolicy.NumericToleranceSeconds || repair.UnknownSupportAmount>TimetableTimePolicy.NumericToleranceSeconds);
                if(exhausted) solvedRestricted.Add(searchKey);
                if(sparseMacro && macroAction is not null)
                {
                    bool effectiveOrders=macroAction.Direction!=MacroAlnsDirection.TimeOnly && requiredPairs.IsSubsetOf(released) &&
                        MacroRepairDependencies.CoreOrdersAdjustable(requiredPairs,repair.FreeSectionOrdersByPair??new Dictionary<string,int>());
                    var actualStops=repair.EffectiveFreeStopIds??new HashSet<string>();
                    bool effectiveStops=macroStops is not null && MacroRepairDependencies.CoreStopsAdjustable(
                        macroStops.RelatedStopIds,macroStops.RequiredOptionalStopIds,actualStops,macroStops.CoreBounded);
                    context=context.Observe(macroAction,repair.NativeStatus,repair.PolishNativeStatus,effectiveOrders,effectiveStops)
                        .Feedback(repair.IisGuidance).Exhaust(searchKey,repair.NativeStatus,repair.PolishNativeStatus,repair.ElasticDeficitSeconds,repair.UnknownSupportAmount);
                    contexts[context.Fingerprint]=context;
                    Write($"round_{round:D3}_coverage.json",new {effectiveOrders,effectiveStops,scope=macroStops,actualFreeStopIds=actualStops,
                        requiredStopsFixed=macroStops?.RequiredOptionalStopIds.Where(id=>!actualStops.Contains(id)).ToArray(),
                        effectiveRelatedStopIds=macroStops?.RelatedStopIds.Where(actualStops.Contains).ToArray(),context});
                    foreach(string pair in context.FeedbackPairs) LearnPair(pair);
                }
                bool buildExhausted=DirectedRepairBudget.BuildExhausted(repair.Status);
                buildBudgetFailures=buildExhausted?buildBudgetFailures+1:0;
                timeoutStreak=buildExhausted || IterationTimedOut(repair.NativeStatus,repair.PolishNativeStatus)?timeoutStreak+1:0;
                if(best is null && (exhausted || repeated || RestrictedDomainFailure(repair.Status) || timeoutStreak>=2))
                    expansionLevel=AdvanceExpansionLevel(expansionLevel,attemptedExpansionLevel);
                Write($"round_{round:D3}_solve.json",repair with { Candidate=null });status=repair.Status;
                double? trialScore=null;bool macroRecorded=false,macroVerified=false,recoveryApplied=false,recoveryRequested=false;
                double? localRepairScore=null;double recoverySeconds=0,verificationSeconds=0;
                void RecordMacro()
                {
                    if(macroRecorded || macroAction is null || macroAlns is null) return;
                    macroRecorded=true;
                    bool externalInput=verifyInputBeforeRepair;
                    double? actionScore=recoveryRequested?localRepairScore:trialScore;
                    bool actionVerified=!recoveryRequested && macroVerified;
                    double reward=0;
                    if(externalInput) macroAlns.ObserveExternal(actionScore,actionVerified);
                    else reward=macroAlns.Record(macroAction,repair.Candidate is not null,before,actionScore,actionVerified);
                    File.AppendAllText(Path.Combine(output,"macro_alns_history.jsonl"),System.Text.Json.JsonSerializer.Serialize(new {
                        round,@event=externalInput?"ExternalObservation":"SelectedMacroAction",action=externalInput?"VerifyUnchangedInput":macroAction.Id,
                        sourceAction=macroAction.Id,Direction=externalInput?(MacroAlnsDirection?)null:macroAction.Direction,Scale=externalInput?(int?)null:macroAction.Scale,
                        LegacyFallback=!externalInput && macroAction.LegacyFallback,
                        pairLimit=macroAction.LegacyFallback?Math.Min(fullByPair.Count,options.MacroPairLimit*(1<<Math.Min(2,attemptedExpansionLevel))):macroAction.PairLimit,releasedPairs=released.Count,movementRadius,freeStopIds=freeStops,
                        beforeScore=before,afterScore=actionScore,hasIncumbent=repair.Candidate is not null,verifiedMacro=actionVerified,recoveryRequested,recoveryApplied=false,
                        creditScope=externalInput?"ExternalObservation_NoMacroArmCredit":"SelectedMacroAction",reward,
                        armSeconds=externalInput?0:localRepairSeconds+(recoveryRequested?0:verificationSeconds),
                        seconds=externalInput?verificationSeconds:localRepairSeconds+(recoveryRequested?0:verificationSeconds),
                        settings=macroAlns.Settings,state=macroAlns.Snapshot(),scope="MacroAdaptiveSearch_NotFeasibilityCertificate" })+Environment.NewLine);
                    if(!recoveryRequested) return;
                    // Expanded recovery can change any discrete decision. Its result and
                    // verification belong to this separate action, never to the local arm.
                    if(recoveryApplied) macroAlns.ObserveExternal(trialScore,macroVerified);
                    File.AppendAllText(Path.Combine(output,"macro_alns_history.jsonl"),System.Text.Json.JsonSerializer.Serialize(new {
                        round,@event="ExpandedRecovery",action="ClosedQuotaBoundaryRecovery",sourceAction=macroAction.Id,
                        beforeScore=localRepairScore,afterScore=recoveryApplied?trialScore:null,hasIncumbent=recoveryApplied,
                        verifiedMacro=recoveryApplied && macroVerified,recoveryRequested,recoveryApplied,
                        creditScope="SeparateClosedQuotaRecovery_NoMacroArmCredit",reward=0,armSeconds=0,
                        seconds=recoverySeconds+verificationSeconds,
                        decisionScope="ExpandedCompleteMacroStructure_AllDecisionsMayChange",
                        settings=macroAlns.Settings,state=macroAlns.Snapshot(),scope="MacroAdaptiveSearch_NotFeasibilityCertificate" })+Environment.NewLine);
                }
                if(repair.Candidate is not null && Remaining()>0)
                {
                    var next=repair.Candidate;
                    var localDiagnosis=DirectedCandidateDiagnostics.Analyze(instance,next);
                    if(sparseMacro)
                    {
                        foreach(string pair in MacroRepairDependencies.Required(localDiagnosis)) LearnPair(pair);
                        var separation=MacroRepairDependencies.SeparateWaiting(localDiagnosis,projections,p=>
                            Math.Abs(next.ArrivalSecondsByStopId[p.FirstStopId]-next.ArrivalSecondsByStopId[p.SecondStopId]));
                        foreach(string pair in separation.ReleasedPairs) LearnPair(pair);
                        context=context.Feedback(separation,"SeparatedWaitingDependency");contexts[context.Fingerprint]=context;
                    }
                    localRepairScore=Score(localDiagnosis);
                    // Recovery can alter the complete discrete structure: only use it before resource feedback exists.
                    if(boundedMacro && micro.Count==0 && timeCuts.Count==0 && ClosedQuotaCandidateRecovery.HasViolation(instance,next))
                    {
                        recoveryRequested=true;
                        var recoveryClock=System.Diagnostics.Stopwatch.StartNew();
                        var recovered=ClosedQuotaCandidateRecovery.TryRecover(instance,next,policy,SearchRemaining,
                            Math.Min(30,SearchRemaining()),closedQuotaAttempts,Path.Combine(output,"closed_quota_recovery.log"));
                        recoverySeconds=recoveryClock.Elapsed.TotalSeconds;
                        if(recovered is not null)
                        {
                            next=recovered;recoveryApplied=true;
                            // A global recovery is a new candidate, not a continuation of a provisional stop exchange.
                            trial=null;trialAnchor=null;trialAnchorDiagnosis=null;trialPattern=null;
                        }
                        else status="UnknownClosedQuotaRecovery";
                    }
                    var nextDiagnosis=recoveryApplied?DirectedCandidateDiagnostics.Analyze(instance,next):localDiagnosis;
                    triedPatterns.Add(ConflictGuidedQuotaExchange.Key(next.StopByStopId));
                    MasterSolutionSnapshotStore.Save(Path.Combine(output,$"candidate_{round:D3}.snapshot.json"),instance.Meta.InstanceName,next);
                    Write($"round_{round:D3}_diagnosis.json",nextDiagnosis);
                    double after=Score(nextDiagnosis);trialScore=after;bool acceptedWorking=trial is not null || exchange is null || after<before-TimetableTimePolicy.NumericToleranceSeconds ||
                        before<=TimetableTimePolicy.NumericToleranceSeconds && after<=TimetableTimePolicy.NumericToleranceSeconds && microStops.Count>0;
                    // Separate omitted violations without losing the near-feasible physical anchor.
                    // Explicit bounded stop trials retain their existing commit/rollback gate.
                    if(sparseMacro && context.CoverageRequired && trial is null && after>before+TimetableTimePolicy.NumericToleranceSeconds)
                        acceptedWorking=false;
                    if(acceptedWorking) { working=next;diagnosis=nextDiagnosis; }
                    stalls=after<before-TimetableTimePolicy.NumericToleranceSeconds?0:stalls+1;
                    if(after<Score(bestDiagnosis)-TimetableTimePolicy.NumericToleranceSeconds && (!options.CoupledMicroRepair || bestRepairProgress is null))
                    {
                        bestWorking=next;bestDiagnosis=nextDiagnosis;lastBestImprovementRound=round;
                        if(boundedMacro) { expansionLevel=0;timeoutStreak=0; }
                        MasterSolutionSnapshotStore.Save(Path.Combine(output,"best_working.snapshot.json"),instance.Meta.InstanceName,bestWorking);
                    }
                    Write($"round_{round:D3}_transition.json",new { beforeScore=before,afterScore=after,acceptedWorking,provisionalStopTrial=trial is not null,exchangeApplied=exchange is not null,
                        recoveryRequested,recoveryApplied,exchangeGain=exchange?.EstimatedGain,physicalConflicts=nextDiagnosis.Conflicts.Count,unresolvedLongDwells=nextDiagnosis.UnresolvedLongDwellStopIds.Count,
                        retiming=Retiming(instance,next,candidate) });
                    if(nextDiagnosis.ReadyForStrictCheck && (recoveryApplied || verifyInputBeforeRepair || repair.ElasticDeficitSeconds<=TimetableTimePolicy.NumericToleranceSeconds) && !rejectedChecks.Contains(nextDiagnosis.SourceFingerprint) && Remaining()>0)
                    {
                        int attempt=verificationAttempts.GetValueOrDefault(nextDiagnosis.SourceFingerprint);
                        verificationAttempts[nextDiagnosis.SourceFingerprint]=attempt+1;
                        double verificationBudget=Math.Min(Remaining(),300*Math.Pow(2,Math.Min(attempt,2)));
                        var verificationClock=System.Diagnostics.Stopwatch.StartNew();
                        double VerificationRemaining()=>Math.Min(Remaining(),verificationBudget-verificationClock.Elapsed.TotalSeconds);
                        Write($"round_{round:D3}_verification_plan.json",new { attempt=attempt+1,verificationBudget });
                        if(options.StopAtFirstCompleteMacro)
                        {
                            var macroProof=FullMacroCandidateVerifier.Verify(instance,next,policy,VerificationRemaining,
                                verificationBudget,Path.Combine(output,$"round_{round:D3}_complete.log"));
                            verificationSeconds=verificationClock.Elapsed.TotalSeconds;
                            Write($"round_{round:D3}_complete.json",macroProof with { Snapshot=null });
                            status=macroProof.Status;
                            if(macroProof.Snapshot is not null)
                            {
                                complete=true;macroVerified=true;status="CompleteMacroFeasible";RecordMacro();
                                MasterSolutionSnapshotStore.Save(Path.Combine(output,"complete_macro.snapshot.json"),instance.Meta.InstanceName,macroProof.Snapshot);
                                Write("complete_macro_acceptance.json",new { round,status,fingerprint=SnapshotFingerprint.Calculate(macroProof.Snapshot),
                                    jointVerified=false,scope="CompleteMacroOnly_NoMicroSearch" });
                                break;
                            }
                            Write("progress.json",new { round,status,complete,joint=false,seconds=wall.Elapsed.TotalSeconds });
                            RecordMacro();continue;
                        }
                        var proof=Stages45JointVerifier.Verify(instance,next,policy,VerificationRemaining,verificationBudget,output,round,options.CoupledMicroRepair?5:1,
                            promoteWitnessFailures:true,knownTimeCutIds:timeCuts.Keys.ToHashSet(),retainedCoreComponents:micro.Values.ToArray(),batchResourceFeedback:options.CoupledMicroRepair,
                            allowClosedQuotaRecovery:false);
                        verificationSeconds=verificationClock.Elapsed.TotalSeconds;
                        macroVerified=proof.CompleteMacro;complete|=proof.CompleteMacro;status=proof.Status;microTrains=proof.ConflictTrains.ToHashSet();
                        lastJointProgress=proof.JointProgress;
                        if(options.CoupledMicroRepair && proof.CompleteMacro && proof.Canonical is not null && proof.JointProgress?.IsBetterThan(bestRepairProgress)==true)
                        {
                            bestRepairProgress=proof.JointProgress;bestWorking=proof.Canonical;bestDiagnosis=DirectedCandidateDiagnostics.Analyze(instance,bestWorking);
                            MasterSolutionSnapshotStore.Save(Path.Combine(output,"best_repair.snapshot.json"),instance.Meta.InstanceName,bestWorking);
                            MasterSolutionSnapshotStore.Save(Path.Combine(output,"best_working.snapshot.json"),instance.Meta.InstanceName,bestWorking);
                            Write("best_repair_acceptance.json",new { round,progress=bestRepairProgress,fingerprint=SnapshotFingerprint.Calculate(bestWorking),
                                scope="CompleteGlobalEvaluation_SearchRank_NotJointCertificate",jointVerified=proof.JointFeasible });
                        }
                        if(proof.CompleteMacro && proof.Canonical is not null)
                        {
                            MasterSolutionSnapshotStore.Save(Path.Combine(output,"complete_macro.snapshot.json"),instance.Meta.InstanceName,proof.Canonical);
                            Write("complete_macro_acceptance.json",new { round,status="CompleteMacroFeasible",
                                fingerprint=SnapshotFingerprint.Calculate(proof.Canonical),sourceFingerprint=nextDiagnosis.SourceFingerprint,
                                jointVerified=proof.JointFeasible,optimalityProven=false,scope="CompleteMacro_NotAutomaticallyJointFeasible" });
                        }
                        microStops=(proof.ConflictStopIds ?? new HashSet<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
                        bool newFeedback=false;
                        foreach(var cut in proof.TimeCuts ?? []) newFeedback|=timeCuts.TryAdd(cut.CutId,cut);
                        foreach(var component in proof.MicroRepairs)
                            newFeedback|=RetainMicroComponent(micro,component);
                        if(proof.CompleteMacro && proof.Canonical is not null && !proof.JointFeasible && (timeCuts.Count>0 || micro.Count>0))
                        {
                            working=proof.Canonical;diagnosis=DirectedCandidateDiagnostics.Analyze(instance,working);
                            if(!options.CoupledMicroRepair)
                            {
                                bestWorking=working;bestDiagnosis=diagnosis;
                                MasterSolutionSnapshotStore.Save(Path.Combine(output,"best_working.snapshot.json"),instance.Meta.InstanceName,working);
                            }
                            expansionLevel=NextMicroExpansion(expansionLevel,attemptedExpansionLevel,newFeedback,options.CoupledMicroRepair);stalls=newFeedback?0:stalls;
                            if(!options.CoupledMicroRepair) { retainedReleased.Clear();focusTrains.Clear(); }
                            var retainedTrains=timeCuts.Values.SelectMany(c=>new[]{instance.StopsById[c.EarlierEvent.StopId].TrainIndex,instance.StopsById[c.LaterEvent.StopId].TrainIndex})
                                .Concat(micro.Values.SelectMany(c=>c.TrainIndices)).ToHashSet();
                            if(options.CoupledMicroRepair) microTrains.UnionWith(retainedTrains);else microTrains=retainedTrains;
                            Write("retained_micro_feedback.json",new { timeCuts=timeCuts.Values,components=micro.Values,scope="NecessaryConditionalRowsAndRestrictedResourceSubsets" });
                        }
                        if(proof.JointFeasible && proof.Canonical is not null)
                        {
                            var objective=ServiceBusinessObjectiveEvaluator.Calculate(instance,proof.Canonical,policy);
                            if(bestObjective is null || Better(objective,bestObjective))
                            {
                                best=proof.Canonical;bestObjective=objective;working=best;diagnosis=DirectedCandidateDiagnostics.Analyze(instance,best);
                                MasterSolutionSnapshotStore.Save(Path.Combine(output,"joint_feasible.snapshot.json"),instance.Meta.InstanceName,best);
                                Write("joint_upper_bound.json",new { objective,objectiveSelectionKey=ResourceSpanObjectivePolicy.SummaryKey(objective),fingerprint=SnapshotFingerprint.Calculate(best),globalLowerBound=(double?)null,optimalityProven=false });
                            }
                            if(options.StopAtFirstJoint) { RecordMacro();break; }
                        }
                        else if(proof.MicroRepairs.Count==0 && RememberRejection(proof.Status)) rejectedChecks.Add(nextDiagnosis.SourceFingerprint);
                    }
                }
                else stalls++;
                RecordMacro();
                if(trial is not null)
                {
                    string decision=trial.Observe(trialScore);
                    Write($"round_{round:D3}_stop_trial.json",new { decision,trial.AnchorScore,trial.RemainingAttempts,candidateScore=trialScore,
                        anchorFingerprint=SnapshotFingerprint.Calculate(trialAnchor!) });
                    if(decision=="Rollback") { working=trialAnchor!;diagnosis=trialAnchorDiagnosis!; }
                    if(decision!="Continue") { trial=null;trialAnchor=null;trialAnchorDiagnosis=null;trialPattern=null; }
                }
                if(stalls>=5 && best is null && trial is null) { working=bestWorking;diagnosis=bestDiagnosis; }
                if(best is not null) { working=best;diagnosis=DirectedCandidateDiagnostics.Analyze(instance,best); }
                Write("progress.json",new { round,status,complete,joint=best is not null,bestObjective,workingConflicts=diagnosis.Conflicts.Count,
                    unresolvedLongDwells=diagnosis.UnresolvedLongDwellStopIds.Count,bestDiagnosticScore=Score(bestDiagnosis),provisionalStopTrial=trial is not null,
                    expansionLevel,retainedReleaseCount=retainedReleased.Count,solvedRestrictedCount=solvedRestricted.Count,lastJointProgress,bestRepairProgress,
                    lastBuildSeconds,buildBudgetFailures,templateBuilds=modelCache?.BuildCount,templateCopies=modelCache?.CopyCount,wallSeconds=wall.Elapsed.TotalSeconds });
                Console.WriteLine($"directed round={round} status={status} conflicts={diagnosis.Conflicts.Count} long_dwell={diagnosis.UnresolvedLongDwellStopIds.Count} slack={repair.ElasticDeficitSeconds:F2} free_int={repair.Audit?.CompactFreeIntegers} joint={best is not null} wall={wall.Elapsed.TotalSeconds:F1}");
            }
        }
        catch(Exception ex) { status="Error";Write("error.json",new { exception=ex.ToString() }); }
        if(best is not null && status!="Error") status="JointFeasible";
        else if(status is "UnverifiedRepairCandidate" || status.StartsWith("Continue",StringComparison.Ordinal)) status="UnknownBudget";
        var result=new DirectedRunResult(status,complete,best is not null,round,wall.Elapsed.TotalSeconds,bestObjective,best is null?null:SnapshotFingerprint.Calculate(best));
        if(options.BoundedMacroSearch)
            Write("macro_search_state.json",new DirectedMacroSearchState((sparseMacro?learnedGuards:edges).Values.ToArray(),triedPatterns.Order(StringComparer.Ordinal).ToArray(),
                options.MacroStagnationRounds>0?expansionLevel:0,options.MacroStagnationRounds>0?timeoutStreak:0,options.MacroStagnationRounds>0?stalls:0,macroAlns?.Snapshot(),closedQuotaAttempts.Order(StringComparer.Ordinal).ToArray(),lastBuildSeconds,buildBudgetFailures,sparseMacro?1:0,
                sparseMacro?MacroRepairContext.For(bestWorking,contexts.GetValueOrDefault(MacroRepairContext.Identity(bestWorking))) with {CoverageRequired=MacroRepairContext.NearFeasible(bestDiagnosis)}:null));
        Write("summary.json",result);return result;
        void Add(SectionConflictCandidate c)=>edges.TryAdd(BasicMilpModelBuilder.BuildSectionPrecedenceKey(c),new(c.SectionName,c.FirstTrainIndex,c.SecondTrainIndex));
        void AddPair(string pair) { if(fullByPair.TryGetValue(pair,out var relations)) foreach(var c in relations) Add(c); }
        void LearnPair(string pair)
        {
            AddPair(pair);
            if(sparseMacro && fullByPair.TryGetValue(pair,out var relations))
                foreach(var c in relations) learnedGuards.TryAdd(BasicMilpModelBuilder.BuildSectionPrecedenceKey(c),new(c.SectionName,c.FirstTrainIndex,c.SecondTrainIndex));
        }
        double PairDistance(SectionConflictCandidate c)
        {
            string a=stopsByPosition[(c.FirstTrainIndex,c.FirstStopOrder)].StopId,b=stopsByPosition[(c.SecondTrainIndex,c.SecondStopOrder)].StopId;
            string ae=stopsByPosition[(c.FirstTrainIndex,c.FirstEndStopOrder)].StopId,be=stopsByPosition[(c.SecondTrainIndex,c.SecondEndStopOrder)].StopId;
            return Math.Min(Math.Abs(working.DepartureSecondsByStopId[a]-working.DepartureSecondsByStopId[b]),
                Math.Abs(working.ArrivalSecondsByStopId[ae]-working.ArrivalSecondsByStopId[be]));
        }
    }

    internal static void AccumulateMicroReleases(HashSet<string> current,HashSet<string> retained,IEnumerable<string>? additional=null)
    {
        current.UnionWith(retained);
        if(additional is not null) current.UnionWith(additional);
        retained.UnionWith(current);
    }

    /// <summary>Identify the restricted model by its expanded graph, section release scope and anchor fixings.</summary>
    /// <param name="instance">Original visits and routes.</param>
    /// <param name="anchor">Anchor pattern, event times in seconds and stored orders.</param>
    /// <param name="edges">Requested section edges before adding witness guards.</param>
    /// <param name="released">Whole-pair requests on the original edge scope and station-event decisions.</param>
    /// <param name="pattern">Target stop decisions by visit.</param>
    /// <param name="radius">Temporary movement radius in seconds, or null for original bounds.</param>
    /// <param name="microIds">Promoted components, time cuts and repair controls included in the identity.</param>
    /// <param name="fixUnreleasedOrders">Whether implicit order releases are disabled.</param>
    /// <param name="fixAllOrders">Whether every order is fixed.</param>
    /// <param name="fixFeedbackOrders">Whether saved feedback station orders are fixed.</param>
    /// <param name="localReleasedEdges">Optional section-specific neighborhood in addition to whole-pair requests.</param>
    /// <param name="anchorWitnesses">Physical anchor reversals that must be represented in the graph.</param>
    /// <returns>Stable SHA-256 identity for equivalent restricted attempts.</returns>
    internal static string SearchKey(TimetablingInstance instance,MasterSolutionSnapshot anchor,IReadOnlyList<SectionConflictEdgeRequest> edges,
        IReadOnlySet<string> released,IReadOnlyDictionary<string,int> pattern,double? radius,IEnumerable<string> microIds,
        bool fixUnreleasedOrders=true,bool fixAllOrders=false,bool fixFeedbackOrders=false,
        IReadOnlyCollection<SectionConflictEdgeRequest>? localReleasedEdges = null,
        IReadOnlyList<OvertakingStopProjection>? anchorWitnesses = null)
    {
        anchorWitnesses ??= [];
        var releasedSectionKeys = DirectedRepairOrderPolicy.ResolveSectionKeys(edges, released, localReleasedEdges);
        var modelEdges = DirectedFeasibilityRepair.IncludeAnchorReversals(instance, edges.Concat(localReleasedEdges ?? []).Distinct().ToArray(), anchorWitnesses);
        var anchorGuardKeys = DirectedRepairOrderPolicy.AnchorGuardKeys(anchorWitnesses);
        var changed=instance.TrainStops.Where(s=>pattern[s.StopId]!=anchor.StopByStopId[s.StopId]).Select(s=>s.TrainIndex).ToHashSet();
        var fixedOrders = modelEdges.Select(edge => (Edge: edge,
                Key: BasicMilpModelBuilder.BuildSectionPrecedenceKey(edge.SectionName, edge.FirstTrainIndex, edge.SecondTrainIndex)))
            .Where(item => !DirectedRepairOrderPolicy.ReleaseSection(item.Key, item.Edge.FirstTrainIndex, item.Edge.SecondTrainIndex,
                anchor, releasedSectionKeys, changed, fixUnreleasedOrders || localReleasedEdges is not null,
                fixAllOrders, anchorGuardKeys.Contains(item.Key)))
            // Anchor event times below identify orders inferred from physical events.
            .Select(item => item.Key + "=" + (!anchorGuardKeys.Contains(item.Key) &&
                anchor.SectionPrecedenceByKey.TryGetValue(item.Key, out int order) ? order.ToString() : "physical"))
            .Order(StringComparer.Ordinal);
        var stationEdges=SavedJointSnapshotVerifier.ResolveSavedStationEdges(instance,anchor);
        var stationOrders=stationEdges.OrderBy(BasicMilpModelBuilder.BuildStationEventPrecedenceKey,StringComparer.Ordinal).Select(e=>
        {
            string key=BasicMilpModelBuilder.BuildStationEventPrecedenceKey(e);
            bool fixedOrder=DirectedRepairOrderPolicy.FixStation(e.FirstTrainIndex,e.SecondTrainIndex,released,fixUnreleasedOrders,fixAllOrders,fixFeedbackOrders);
            return key+"="+(fixedOrder?anchor.StationEventPrecedenceByKey[key].ToString():"free");
        });
        string N(double value)=>value.ToString("R",System.Globalization.CultureInfo.InvariantCulture);
        var events=instance.TrainStops.OrderBy(s=>s.StopId,StringComparer.Ordinal).Select(s=>s.StopId+":"+
            (s.StopRole==TrainStopRole.Origin?"-":N(anchor.ArrivalSecondsByStopId[s.StopId]))+":"+
            (s.StopRole==TrainStopRole.Destination?"-":N(anchor.DepartureSecondsByStopId[s.StopId])));
        string text=string.Join("|","RestrictedSearchV3",fixUnreleasedOrders,fixAllOrders,fixFeedbackOrders,localReleasedEdges is not null,
            ConflictGuidedQuotaExchange.Key(pattern),radius.HasValue?N(radius.Value):"unbounded",
            string.Join(";",events),string.Join(";",fixedOrders),string.Join(";",stationOrders),
            string.Join(";",modelEdges.Select(e=>BasicMilpModelBuilder.BuildSectionPrecedenceKey(e.SectionName,e.FirstTrainIndex,e.SecondTrainIndex)).Order(StringComparer.Ordinal)),
            string.Join(";",releasedSectionKeys.Order(StringComparer.Ordinal)),string.Join(";",microIds.Order(StringComparer.Ordinal)));
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
    }

    private static object Retiming(TimetablingInstance instance,MasterSolutionSnapshot working,MasterSolutionSnapshot reference)
    {
        // Always report displacement from the run's immutable input, not from an
        // already-replaced working variable.
        var changes=instance.TrainStops.SelectMany(s=>new[]{
            (s.TrainIndex,Delta:s.StopRole==TrainStopRole.Origin?0:Math.Abs(working.ArrivalSecondsByStopId[s.StopId]-reference.ArrivalSecondsByStopId[s.StopId])),
            (s.TrainIndex,Delta:s.StopRole==TrainStopRole.Destination?0:Math.Abs(working.DepartureSecondsByStopId[s.StopId]-reference.DepartureSecondsByStopId[s.StopId]))}).ToArray();
        return new { changedTrains=changes.Where(x=>x.Delta>TimetableTimePolicy.NumericToleranceSeconds).Select(x=>x.TrainIndex).Distinct().Count(),
            maxMovementFromInput=changes.Max(x=>x.Delta),totalMovementFromInput=changes.Sum(x=>x.Delta) };
    }

    internal static double Score(DirectedDiagnosis d)=>d.HeadwayDeficitSeconds+180*d.Conflicts.Count(c=>c.Kind!="headway")+600*d.UnresolvedLongDwellStopIds.Count+600*d.QuotaViolationMagnitude;
    // Multiple outcomes from one attempted neighborhood advance at most once.
    internal static int AdvanceExpansionLevel(int current,int attemptedLevel)=>Math.Min(4,Math.Max(current,attemptedLevel+1));
    internal static int NextMicroExpansion(int current,int attempted,bool newFeedback,bool coupled)=>
        newFeedback?(coupled?Math.Max(1,Math.Min(3,current)):0):AdvanceExpansionLevel(current,attempted);
    internal static bool IterationTimedOut(int native,int? polishNative)=>
        native==Gurobi.GRB.Status.TIME_LIMIT || polishNative==Gurobi.GRB.Status.TIME_LIMIT;
    internal static bool RestrictedDomainFailure(string status)=>
        status is "UnknownRestrictedInfeasible" or "UnknownRestrictedTimeDomain" or "UnknownRestrictedDomainContradiction" or "UnknownRestrictedStationOrder" or "UnknownRestrictedFixedOrderDomain";
    internal static bool RetainMicroComponent(Dictionary<string,PromotedMicroComponent> micro,PromotedMicroComponent component)
    {
        // Complete-station IDs and core-domain hashes are immutable within this
        // instance. A later evidence timestamp alone is not a new search domain.
        if(micro.ContainsKey(component.ComponentId)) return false;
        foreach(string key in micro.Where(p=>p.Value.StationName.Equals(component.StationName,StringComparison.OrdinalIgnoreCase)).Select(p=>p.Key).ToArray())
            micro.Remove(key);
        return micro.TryAdd(component.ComponentId,component);
    }
    internal static string[] ConflictComponentPairs(IReadOnlyList<DirectedConflict> conflicts,int maxPairs=160)
    {
        if(maxPairs<0) throw new ArgumentOutOfRangeException(nameof(maxPairs));
        if(maxPairs==0 || conflicts.Count==0) return [];
        Dictionary<int,Dictionary<int,double>> adjacency=[];
        foreach(var c in conflicts)
        {
            if(c.FirstTrainIndex==c.SecondTrainIndex) continue;
            double weight=c.Kind=="headway"?Math.Max(0,c.Magnitude):180;
            Edge(c.FirstTrainIndex,c.SecondTrainIndex,weight);Edge(c.SecondTrainIndex,c.FirstTrainIndex,weight);
        }
        List<int[]> components=[];HashSet<int> unseen=adjacency.Keys.ToHashSet();
        while(unseen.Count>0)
        {
            int seed=unseen.Min();List<int> component=[];Queue<int> queue=new();queue.Enqueue(seed);unseen.Remove(seed);
            while(queue.TryDequeue(out int train))
            {
                component.Add(train);
                foreach(int next in adjacency[train].Keys.Order()) if(unseen.Remove(next)) queue.Enqueue(next);
            }
            components.Add(component.Order().ToArray());
        }
        List<string> selected=[];
        foreach(var component in components.OrderByDescending(g=>g.Sum(t=>adjacency[t].Values.Sum())).ThenBy(g=>g[0]))
        {
            int remaining=maxPairs-selected.Count;if(remaining==0) break;
            int maxTrains=(int)Math.Floor((1+Math.Sqrt(1+8.0*remaining))/2);
            int[] trains=component;
            if(component.Length>maxTrains)
            {
                // A bounded connected subset, then its entire pair clique; never claim
                // the complete oversized component has been unlocked.
                HashSet<int> subset=[component.OrderByDescending(t=>adjacency[t].Values.Sum()).ThenBy(t=>t).First()];
                while(subset.Count<maxTrains)
                {
                    int next=subset.SelectMany(t=>adjacency[t].Keys).Where(t=>!subset.Contains(t)).Distinct()
                        .OrderByDescending(t=>adjacency[t].Where(p=>subset.Contains(p.Key)).Sum(p=>p.Value))
                        .ThenByDescending(t=>adjacency[t].Values.Sum()).ThenBy(t=>t).First();
                    subset.Add(next);
                }
                trains=subset.Order().ToArray();
            }
            for(int i=0;i<trains.Length;i++) for(int j=i+1;j<trains.Length;j++) selected.Add(DirectedFeasibilityRepair.Pair(trains[i],trains[j]));
        }
        return selected.ToArray();
        void Edge(int a,int b,double weight)
        {
            if(!adjacency.TryGetValue(a,out var neighbors)) adjacency.Add(a,neighbors=[]);
            neighbors[b]=neighbors.GetValueOrDefault(b)+weight;
        }
    }
    internal static bool RememberRejection(string status)=>status is "UnknownCompleteCandidateRejected" or "UnknownCompleteCarrierViolation";
    internal static string[] RotatePairs(IReadOnlyList<string> eligible,int round,int count)
        =>eligible.Count==0?[]:Enumerable.Range(0,Math.Min(count,eligible.Count))
            .Select(i=>eligible[(int)(((long)(round-1)*count+i)%eligible.Count)]).ToArray();
    internal static OvertakingStopProjection[] WaitingWitnesses(DirectedDiagnosis diagnosis,string stop)
        =>diagnosis.Witnesses.Where(p=>diagnosis.SuggestedOrders.TryGetValue(p.OutgoingPrecedenceKey,out int outgoing) &&
            (outgoing==1?p.FirstStopId:p.SecondStopId).Equals(stop,StringComparison.OrdinalIgnoreCase)).ToArray();
    internal static IReadOnlyList<ConflictQuotaProposal> ExchangeProposals(TimetablingInstance instance,IReadOnlyDictionary<string,int> pattern,
        IReadOnlyDictionary<string,double> macroPressure,IReadOnlySet<string> microStops,IReadOnlyList<StopLinearCut> cuts,
        IReadOnlySet<string> tried,bool jointIncumbent,bool allowBridge=false,IReadOnlySet<string>? focusStops=null)
    {
        // Diagnostic pressure says WHERE to explore, not that adding a stop is
        // beneficial. Both removal of a selected stop and addition of an absent
        // stop must reach the quota/cut audit and the actual repair trial.
        var pressure=macroPressure.ToDictionary(p=>p.Key,
            p=>p.Value*(pattern.GetValueOrDefault(p.Key)==1?-1:1),StringComparer.OrdinalIgnoreCase);
        var target=focusStops is { Count:>0 }?focusStops:microStops;
        foreach(string stop in target)
            if(pattern.TryGetValue(stop,out int current)) pressure[stop]=pressure.GetValueOrDefault(stop)+(current==1?-600:600);
        return ConflictGuidedQuotaExchange.Propose(instance,pattern,pressure,cuts,tried,allowZeroGain:jointIncumbent || microStops.Count>0 || allowBridge,
            targetStops:target);
    }
    /// <summary>Compare accepted joint solutions by the common business objective order.</summary>
    /// <param name="candidate">Verified candidate's raw objective values.</param>
    /// <param name="incumbent">Previously retained verified objective under the same policy.</param>
    /// <returns>Whether the candidate ranks strictly ahead of the incumbent.</returns>
    /// <exception cref="ArgumentException">Policies differ or an objective value is invalid.</exception>
    internal static bool Better(ServiceObjectiveVector candidate, ServiceObjectiveVector incumbent)
        => ResourceSpanObjectivePolicy.Better(candidate, incumbent);
    internal static int Run(string[] args)
    {
        string Required(string prefix)=>args.FirstOrDefault(a=>a.StartsWith(prefix,StringComparison.Ordinal))?[prefix.Length..]
            ??throw new ArgumentException("Missing "+prefix);
        var instance=new NormalizedDatasetLoader().Load(Required("--dataset="));
        string input=Path.GetFullPath(Required("--repair-snapshot=")),output=Path.GetFullPath(Required("--repair-output="));
        var snapshot=MasterSolutionSnapshotStore.Load(input,instance.Meta.InstanceName,instance.Trains.Select(t=>t.TrainIndex).ToHashSet());
        if(!args.Contains("--repair-diagnose-only"))
        {
            double Number(string prefix,double fallback)=>args.FirstOrDefault(a=>a.StartsWith(prefix,StringComparison.Ordinal)) is string text?
                double.Parse(text[prefix.Length..],System.Globalization.CultureInfo.InvariantCulture):fallback;
            var result=Solve(instance,snapshot,new(output,Number("--repair-total=",7200),Number("--repair-iteration=",180),Number("--repair-reserve=",600),
                checked((int)Number("--repair-seed=",0)),checked((int)Number("--repair-rounds=",500)),args.Contains("--repair-first-joint"),args.Contains("--repair-component-closure"),checked((int)Number("--repair-trial-rounds=",3)),args.Contains("--repair-coupled-micro")));
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result));return result.Status=="Error"?2:0;
        }
        if(Directory.Exists(output)) throw new IOException("Refusing to overwrite repair evidence.");
        Directory.CreateDirectory(output);
        var diagnosis=DirectedCandidateDiagnostics.Analyze(instance,snapshot);
        Stages45Runner.Write(output,"diagnosis.json",diagnosis);
        var summary=new { conflicts=diagnosis.Conflicts.Count,diagnosis.HeadwayDeficitSeconds,
            supportedLongDwells=diagnosis.SupportedLongDwellStopIds.Count,unresolvedLongDwells=diagnosis.UnresolvedLongDwellStopIds.Count,
            diagnosis.SourceFingerprint,scope="DiagnosticOnly_NotCompleteMacroOrJointProof" };
        Stages45Runner.Write(output,"diagnosis_summary.json",summary);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(summary));
        return 0;
    }
}
