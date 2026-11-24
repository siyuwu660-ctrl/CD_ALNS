using System.Text.Json;
using GurobiTimetablingResearch.Domain;
using GurobiTimetablingResearch.IO;

namespace GurobiTimetablingResearch.Optimization;

internal sealed record ResourceRunOptions(string OutputDirectory,double TotalSeconds=14400,double IterationSeconds=150,
    double ReserveSeconds=600,int Seed=0,int MaxRounds=500,bool StopAtFirstJoint=false,double TargetRelativeGap=.01,
    double BoundPassSeconds=120,double BoundTotalSeconds=900,int BoundEveryQualityRounds=10,double? QualityIterationSeconds=null,bool UseAlns=false,bool CompactOutput=false,ResourceAlnsSettings? AlnsSettings=null,
    bool ResumeStrictJoint=false,double PriorElapsedSeconds=0,bool ImproveQuality=false,bool GuideFeasibilitySpan=false, bool QuotaControl = false);
internal sealed record ResourceRunResult(string Status,bool CompleteMacroFeasible,bool JointFeasible,int Rounds,double Seconds,
    ServiceObjectiveVector? BestObjective,string? BestFingerprint,ResourceSpanGapReport SpanGap,string StopReason,
    int? FirstJointRound,double? FirstJointSeconds,int QualityRounds,double BoundEffortSeconds,string BoundStatus,string FinalVerificationStatus)
{
    /// <summary>Business comparator key for the accepted timetable in the summary.</summary>
    public object? ObjectiveSelectionKey => ResourceSpanObjectivePolicy.SummaryKey(BestObjective);
}
internal static class ResourceLnsRunner
{
    internal static int FeasibilityNativeSeed(int seed,int round)
    {
        if(seed is <0 or >2_000_000_000 || round<1)throw new ArgumentOutOfRangeException();
        return round==1?seed:(int)((seed+104729L*round)%2_000_000_001L);
    }
    internal static double IterationSecondsForPhase(ResourceRunOptions options,bool qualityPhase)
        =>qualityPhase?options.QualityIterationSeconds??options.IterationSeconds:options.IterationSeconds;
    /// <summary>Repair station conflicts and improve the strictly verified timetable within the run budget.</summary>
    /// <param name="instance">Complete input and shared hard constraints.</param>
    /// <param name="snapshot">Initial stop pattern and event times in seconds.</param>
    /// <param name="options">Search mode, comparison arm, wall-clock seconds and output path.</param>
    /// <returns>Solver status and the best accepted timetable's evidence, if available.</returns>
    /// <remarks>The run writes attempt, quota comparison and acceptance evidence to a new output directory.</remarks>
    internal static ResourceRunResult Solve(TimetablingInstance instance,MasterSolutionSnapshot snapshot,ResourceRunOptions options)
    {
        if(!double.IsFinite(options.TotalSeconds) || !double.IsFinite(options.IterationSeconds) || !double.IsFinite(options.ReserveSeconds) ||
            options.TotalSeconds<=0 || options.IterationSeconds<=0 || options.ReserveSeconds<0 || options.ReserveSeconds>=options.TotalSeconds || options.Seed is <0 or >2_000_000_000 || options.MaxRounds<0)
            throw new ArgumentException("Invalid resource LNS options.");
        if((options.ImproveQuality || options.GuideFeasibilitySpan) && !options.UseAlns)
            throw new ArgumentException("Search improvements require ALNS.");
        _=ResourceSpanGapReport.Calculate(null,null,options.TargetRelativeGap);
        if(!double.IsFinite(options.PriorElapsedSeconds) || options.PriorElapsedSeconds<0)
            throw new ArgumentException("Invalid prior elapsed seconds.");
        if(options.ResumeStrictJoint && snapshot.VerifiedMicroAssignments?.Count!=instance.TrainStops.Count)
            throw new ArgumentException("Strict continuation requires a complete saved resource witness.");
        if(options.QualityIterationSeconds is double qualitySeconds && (!double.IsFinite(qualitySeconds) || qualitySeconds<=0))
            throw new ArgumentException("Invalid quality iteration budget.");
        var boundBudget=new ResourceSpanBoundBudget(options.BoundPassSeconds,options.BoundTotalSeconds,options.BoundEveryQualityRounds);
        string output=Path.GetFullPath(options.OutputDirectory);
        if(Directory.Exists(output)) throw new IOException("Refusing to overwrite resource LNS evidence.");
        Directory.CreateDirectory(output);var clock=System.Diagnostics.Stopwatch.StartNew();
        var phaseBudget=new ResourcePhaseBudget(options.TotalSeconds,options.ReserveSeconds,()=>clock.Elapsed.TotalSeconds);
        double Remaining()=>phaseBudget.Remaining();
        double SearchRemaining()=>phaseBudget.SearchRemaining();
        void Write(string name,object? value)
        {
            if(options.CompactOutput && name.StartsWith("round_",StringComparison.Ordinal)) return;
            Stages45Runner.Write(output,name,value!);
        }
        var adaptive=options.UseAlns?new ResourceAlnsPolicy(options.Seed,options.AlnsSettings,options.ImproveQuality):null;
        var neighborhoods=options.UseAlns?new ResourceAlnsNeighborhood(instance, options.QuotaControl):null;
        var feasibilityMemory=new ResourceFeasibilitySearchMemory();
        var feasibilityLinks=new Dictionary<string,IReadOnlyList<ResourceTightLink>>(StringComparer.Ordinal);
        var qualityBudgets=options.ImproveQuality?new ResourceQualityBudgetPolicy(options.Seed):null;
        string localOutput=options.CompactOutput?Path.Combine(output,".scratch"):output;
        Directory.CreateDirectory(localOutput);
        var policy=ServiceWindowPolicy.Create(instance,new(true));
        snapshot=snapshot with { VerifiedMicroAssignments=options.ResumeStrictJoint?snapshot.VerifiedMicroAssignments:null,BoundReport=null,SolutionRole=TimetableSolutionRole.WorkingSolution };
        MasterSolutionSnapshotStore.Save(Path.Combine(output,"input_macro.snapshot.json"),instance.Meta.InstanceName,snapshot);
        Write("configuration.json",new { options,instance.Meta,policy,inputFingerprint=SnapshotFingerprint.Calculate(snapshot),
            freshMicroState=!options.ResumeStrictJoint,importedFeedbackCount=0,importedMicroAssignments=snapshot.VerifiedMicroAssignments?.Count??0,
            algorithm=options.UseAlns?(options.ImproveQuality?"ConflictDrivenAlns_QuotaBlocking_AdaptiveEffort_20261024":options.GuideFeasibilitySpan?"ConflictDrivenAlns_SpanGuidance_20261023":"ConflictDrivenAlns_DirectionScale_BoundedFallback_20261023"):"ConflictDrivenLns",adaptiveSettings=adaptive?.Settings,
            binarySha256=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(typeof(ResourceLnsRunner).Assembly.Location))),
            scope="FullMacroRestrictedLocalSearch_OnlyStrictReadbackAcceptsJoint",globalLowerBound=(double?)null,
            objectiveSchedule=options.GuideFeasibilitySpan?"ConflictFirst_BoundedSpanGuidance_ThenSpan_WindowPenalty_Supplement":"UnchangedUntilFirstStrictJoint_ThenSpan_WindowPenalty_Supplement",
            boundScope=ResourceGlobalSpanBoundSolver.BoundScope,boundStartsAfterFirstStrictJoint=true });
        MasterSolutionSnapshot? joint=null,currentQuality=null;ServiceObjectiveVector? objective=null;
        HashSet<string> qualityStructures=new(StringComparer.Ordinal),qualityTimetables=new(StringComparer.Ordinal);IReadOnlyList<ResourceTightLink> microLinks=[];
        var retimingAttempts=new RetimingAttemptPolicy();
        HashSet<string> closedQuotaRecoverySignatures=new(StringComparer.Ordinal);
        string? lastRetimingBest=null;int lastRetimingRound=-10;double retimingEffortSeconds=0;
        double? qualityStarted=null;int round=0,stagnation=0;bool completeMacro=false;
        string status="UnknownBudget";var pool=new ResourceCandidatePool(6);ResourceMacroTemplate? template=null;
        bool finalVerificationAttempted=false,finalVerificationFailed=false;
        string finalVerificationStatus="NotAttempted";
        int qualityRound=0;int? firstJointRound=null;double? firstJointSeconds=null,globalLowerBound=null;
        string boundStatus="NotStartedBeforeJoint";bool boundDisabled=false;
        ResourceGlobalSpanBoundSolver? boundSolver=null;
        ResourceSpanGapReport Gap()=>ResourceSpanGapReport.Calculate(objective?.GlobalSpanSeconds,globalLowerBound,options.TargetRelativeGap);
        try
        {
            if(options.ResumeStrictJoint && !ValidImportedWitness(instance,snapshot))
            { status="ResumeStrictVerificationFailed";finalVerificationStatus=status;return Finish(); }
            var initial=Verify(snapshot,0,SearchRemaining);completeMacro=initial.CompleteMacro;
            if(options.ResumeStrictJoint && (!initial.JointFeasible || initial.Readback?.Passed!=true))
            { status="ResumeStrictVerificationFailed";finalVerificationStatus=status;return Finish(); }
            if(!initial.CompleteMacro || initial.Canonical is null) { status=initial.Status;FinalVerification();return Finish(); }
            snapshot=initial.Canonical with { VerifiedMicroAssignments=null,BoundReport=null,SolutionRole=TimetableSolutionRole.WorkingSolution };
            AcceptJoint(initial,0);
            if(options.StopAtFirstJoint && joint is not null)
            {
                status="JointFeasible";finalVerificationStatus="AlreadyStrictlyVerifiedJoint";
                return Finish();
            }
            var profile=ResourceConflictProfiler.Evaluate(instance,snapshot,SearchRemaining,preferred:Preferred(joint));SaveProfile("initial_profile.json",profile);
            if(!pool.Add(snapshot,profile)) { status="UnknownInitialResourceProfile";FinalVerification();return Finish(); }
            SaveBest();double lastBuild=0;
            if(options.MaxRounds>0 && !(options.StopAtFirstJoint && joint is not null) && SearchRemaining()>0)
            {
                Write("progress.json",new { phase="BuildImmutableMacroTemplate",seconds=clock.Elapsed.TotalSeconds,jointFeasible=joint is not null });
                var build=System.Diagnostics.Stopwatch.StartNew();template=new(instance,policy,snapshot,SearchRemaining);build.Stop();
                Write("template.json",new { template.SourceRows,template.SourceVariables,seconds=build.Elapsed.TotalSeconds,
                    immutableOriginalRows=true,repeatedFullMacroBuild=false });
            }
            while(template is not null && SearchRemaining()>Math.Max(2,lastBuild+2) && round<options.MaxRounds && !(options.StopAtFirstJoint && joint is not null))
            {
                bool qualityPhase=joint is not null;
                if(qualityPhase)
                {
                    if(Gap().TargetMet) break;
                    qualityRound++;
                    TryGlobalRetiming();
                    UpdateGlobalBound();
                    if(Gap().TargetMet || SearchRemaining()<=Math.Max(2,lastBuild+2)) break;
                }
                round++;
                var iterationClock=System.Diagnostics.Stopwatch.StartNew();
                bool hadJoint=joint is not null;
                var qualityAnchor=options.ImproveQuality?currentQuality??joint:joint;
                var anchor=joint is null?pool.Select(round,stagnation):new ResourcePoolEntry(qualityAnchor!,ResourceConflictProfiler.Evaluate(instance,qualityAnchor!,SearchRemaining,pool.Best.Profile,Preferred(qualityAnchor)));
                string anchorSearchKey=ResourceFeasibilitySearchMemory.AnchorKey(anchor.Snapshot);
                neighborhoods?.PreparePhase(qualityPhase,options.ImproveQuality);
                if(qualityPhase && options.ImproveQuality)
                    neighborhoods!.BlockingLinks=template.TightLinks(anchor.Snapshot,SearchRemaining).Concat(microLinks).Distinct().ToArray();
                var neighborhoodClock=System.Diagnostics.Stopwatch.StartNew();
                if(!qualityPhase && neighborhoods is not null)
                {
                    neighborhoods.FeasibilityRadiusCap=ResourceFeasibilitySearchMemory.RadiusCap(stagnation);
                    if(stagnation>=3)
                    {
                        if(!feasibilityLinks.TryGetValue(anchorSearchKey,out var links))
                        {
                            links=template.TightLinks(anchor.Snapshot,SearchRemaining);
                            if(feasibilityLinks.Count>=6)feasibilityLinks.Remove(feasibilityLinks.Keys.First());
                            feasibilityLinks.Add(anchorSearchKey,links);
                        }
                        neighborhoods.BlockingLinks=links;
                    }
                    else neighborhoods.BlockingLinks=[];
                }
                ResourceAlnsAction? action=null;
                string[] skippedActions=[];
                string? recoverySelection=null;
                if(adaptive is not null)
                {
                    var available=neighborhoods!.Available(anchor.Snapshot,anchor.Profile,qualityPhase,
                        qualityPhase?qualityRound:round,Math.Min(SearchRemaining(),IterationSecondsForPhase(options,qualityPhase)),SearchRemaining);
                    action=adaptive.SelectForRound(available,qualityPhase,qualityPhase?qualityRound:round,stagnation);
                    if(!qualityPhase)
                    {
                        var scopes=available.ToDictionary(a=>a,a=>ResourceFeasibilitySearchMemory.ScopeKey(
                            neighborhoods.Build(anchor.Snapshot,anchor.Profile,a,round,SearchRemaining,stagnation)));
                        string preferredScope=ResourceFeasibilitySearchMemory.ScopeKey(
                            neighborhoods.Build(anchor.Snapshot,anchor.Profile,action,round,SearchRemaining,stagnation));
                        (action,recoverySelection,skippedActions)=feasibilityMemory.Select(anchorSearchKey,scopes,action,
                            preferredScope,round,stagnation,eligible=>adaptive.Select(eligible,false));
                    }
                }
                string selectionReason=action is null?"LegacySchedule":action.Operator==ResourceAlnsOperator.Legacy?
                    ((qualityPhase?qualityRound:round)==1?"InitialLegacy":"StagnationLegacy"):"Adaptive";
                selectionReason=recoverySelection??selectionReason;
                var neighborhood=action is not null?neighborhoods!.Build(anchor.Snapshot,anchor.Profile,action,qualityPhase?qualityRound:round,SearchRemaining,stagnation,qualityPhase):
                    qualityPhase?ResourceSpanNeighborhood.Select(instance,anchor.Snapshot,qualityRound,stagnation,SearchRemaining):
                    ResourceNeighborhood.Select(instance,anchor.Snapshot,anchor.Profile,round,stagnation,SearchRemaining);
                string neighborhoodSignature=ResourceFeasibilitySearchMemory.ScopeKey(neighborhood);
                int nativeSeed=!qualityPhase && options.UseAlns?FeasibilityNativeSeed(options.Seed,round):options.Seed;
                neighborhoodClock.Stop();double neighborhoodSeconds=neighborhoodClock.Elapsed.TotalSeconds;
                var anchorValue=Vector(anchor.Snapshot,anchor.Profile,qualityPhase);
                var bestValue=qualityPhase?Vector(joint!,anchor.Profile,true):Vector(pool.Best.Snapshot,pool.Best.Profile,false);
                bool candidateValid=false,candidateStrict=false,diverse=false,currentAccepted=false;
                double guidanceSeconds=0;string guidanceStatus="NotRequested";
                double before=pool.Best.Profile.ViolationSeconds;
                double? spanBefore=objective?.GlobalSpanSeconds;
                double budget=Math.Min(SearchRemaining(),Math.Max(IterationSecondsForPhase(options,qualityPhase),lastBuild+30));
                ResourceQualityBudgetChoice? effortChoice=null;
                if(qualityPhase && qualityBudgets is not null && action is not null)
                {
                    double maximum=Math.Min(budget,IterationSecondsForPhase(options,true));
                    if(maximum<=0)throw new TimeoutException("Quality effort selection deadline expired.");
                    effortChoice=qualityBudgets.Select(action.Id,maximum);budget=effortChoice.Seconds;
                }
                var roundPlan=new { round,neighborhood,stagnation,budget,action=action?.Id,selectionReason,anchorFingerprint=SnapshotFingerprint.Calculate(anchor.Snapshot),
                    anchorSearchKey,neighborhoodSignature,nativeSeed,skippedActions,blockingLinks=neighborhoods?.BlockingLinks.Count??0,
                    anchorViolationSeconds=anchor.Profile.ViolationSeconds,globalBestViolationSeconds=before,
                    phase=qualityPhase?"StrictJointSpanQualityLns":"ResourceFeasibilityLns",qualityRound,
                    spanBefore,spanGap=Gap(),freeTrains=neighborhood.FreeTimeStopIds.Select(id=>instance.StopsById[id].TrainIndex).Distinct().Count(),
                    mandatorySeedTrains=neighborhood.SeedStopIds.Select(id=>instance.StopsById[id].TrainIndex).Distinct().Count(),searchOnly=true };
                Write($"round_{round:D3}_plan.json",roundPlan);
                if(options.CompactOutput)File.AppendAllText(Path.Combine(output,"neighborhood_history.jsonl"),
                    System.Text.Json.JsonSerializer.Serialize(roundPlan)+Environment.NewLine);
                if(options.CompactOutput)
                    foreach(string path in Directory.EnumerateFiles(localOutput,"local.log*")) File.Delete(path);
                var quotaExpansions = neighborhood.QuotaExpansions ?? [];
                bool quotaTriggered = !qualityPhase && neighborhood.MaxStopChanges > 0 && anchor.Profile.ViolationSeconds > 0 &&
                    quotaExpansions.Any(expansion => expansion.NeedsReplacement);
                if (quotaTriggered)
                    File.AppendAllText(Path.Combine(output, "quota_comparison.jsonl"), JsonSerializer.Serialize(new
                    {
                        round, stage = "trigger", control = options.QuotaControl, anchorFingerprint = SnapshotFingerprint.Calculate(anchor.Snapshot),
                        action = action?.Id, nativeSeed, neighborhood.MaxStopChanges, freeTimes = neighborhood.FreeTimeStopIds.Count,
                        freeStops = neighborhood.FreeStopIds.Count,
                        freeTrains = neighborhood.FreeTimeStopIds.Select(id => instance.StopsById[id].TrainIndex).Distinct().Count(),
                        expansions = quotaExpansions.Select(expansion => new { expansion.NeedsReplacement, expansion.GuidedAdditions, expansion.SelectedAdditions, expansion.Bounded })
                    }) + Environment.NewLine);
                var repair=ResourceLocalSolver.Solve(template,anchor.Snapshot,neighborhood,anchor.Profile,
                    new(budget,Path.Combine(localOutput,options.CompactOutput?"local.log":$"round_{round:D3}.log"),
                        nativeSeed,RequireZeroMicro:joint is not null,BoundedAlnsConstruction:options.UseAlns,
                        ExploreStructure:qualityPhase && options.ImproveQuality && stagnation>=2 && qualityRound%3==0,
                        AdaptiveQualityBudget:qualityPhase && options.ImproveQuality,QualityPatienceSeconds:effortChoice?.Seconds),SearchRemaining);
                var comparedCandidate = repair.Candidate;
                MasterSolutionSnapshot? comparedCanonical = null;
                int comparedNativeStatus = repair.NativeStatus;
                if (quotaTriggered)
                    File.AppendAllText(Path.Combine(output, "quota_comparison.jsonl"), JsonSerializer.Serialize(new
                    {
                        round, stage = "attempt", enteredNativeRepair = comparedNativeStatus != 0, nativeStatus = comparedNativeStatus
                    }) + Environment.NewLine);
                double localActionSeconds=iterationClock.Elapsed.TotalSeconds;
                repair=RecoverClosedQuota(repair,SearchRemaining,budget,action?.Id,out var recoveryAttempt);
                bool candidateFromRecovery=recoveryAttempt.Applied;
                double externalRecoverySeconds=recoveryAttempt.Seconds;
                var recoveryAttempts=new List<ResourceBoundaryRecoveryAttempt>();
                if(recoveryAttempt.Attempted)recoveryAttempts.Add(recoveryAttempt);
                lastBuild=repair.BuildSeconds;Write($"round_{round:D3}_repair.json",repair with { Candidate=null });
                bool improved=false;string roundStatus=repair.Status;ResourceConflictProfile? evaluated=null;
                if(repair.Candidate is not null && SearchRemaining()>0)
                {
                    var candidate=repair.Candidate;
                    if(!options.CompactOutput) MasterSolutionSnapshotStore.Save(Path.Combine(output,$"candidate_{round:D3}.snapshot.json"),instance.Meta.InstanceName,candidate);
                    var diagnosis=DirectedCandidateDiagnostics.Analyze(instance,candidate);
                    Write($"round_{round:D3}_macro_diagnostic.json",new { diagnosis.ReadyForStrictCheck,conflicts=diagnosis.Conflicts.Count,
                        diagnosis.HeadwayDeficitSeconds,unresolvedLongDwells=diagnosis.UnresolvedLongDwellStopIds.Count });
                    if(diagnosis.ReadyForStrictCheck)
                    {
                        evaluated=ResourceConflictProfiler.Evaluate(instance,candidate,SearchRemaining,anchor.Profile,options.UseAlns?repair.SelectedResources:null);
                        SaveProfile($"round_{round:D3}_profile.json",evaluated);
                        candidateValid=evaluated.Complete && evaluated.UnknownStations==0;
                        if(!qualityPhase && options.GuideFeasibilitySpan && candidateValid && repair.MicroSlack is double microSlack &&
                            feasibilityMemory.ShouldGuide(stagnation,anchor.Profile.ViolationSeconds,evaluated.ViolationSeconds))
                        {
                            double guideBudget=ResourceSearchPolicy.GuidanceBudget(budget,
                                Math.Min(budget-iterationClock.Elapsed.TotalSeconds,SearchRemaining()),ShouldStrictlyVerify(evaluated));
                            if(guideBudget>=2)
                            {
                                var guideClock=System.Diagnostics.Stopwatch.StartNew();
                                double GuideRemaining()=>Math.Min(guideBudget-guideClock.Elapsed.TotalSeconds,SearchRemaining());
                                try
                                {
                                    var baseValue=ServiceBusinessObjectiveEvaluator.Calculate(instance,candidate,policy);
                                    var guided=ResourceLocalSolver.Solve(template,anchor.Snapshot,neighborhood,anchor.Profile,
                                        new(guideBudget,Path.Combine(localOutput,"guide.log"),nativeSeed,BoundedAlnsConstruction:true,
                                            Guidance:new(microSlack,baseValue.ServicePenalty,baseValue.GlobalSpanSeconds),StopAtZeroMicro:true),GuideRemaining);
                                    double secondsBeforeGuideRecovery=iterationClock.Elapsed.TotalSeconds-externalRecoverySeconds;
                                    guided=RecoverClosedQuota(guided,GuideRemaining,guideBudget,action?.Id,out var guidedRecovery);
                                    externalRecoverySeconds+=guidedRecovery.Seconds;
                                    if(guidedRecovery.Attempted)recoveryAttempts.Add(guidedRecovery);
                                    double afterGuideRecovery=guideClock.Elapsed.TotalSeconds;
                                    guidanceStatus=guided.Status;
                                    if(guided.Candidate is not null && GuideRemaining()>0 && DirectedCandidateDiagnostics.Analyze(instance,guided.Candidate).ReadyForStrictCheck)
                                    {
                                        var guidedProfile=ResourceConflictProfiler.Evaluate(instance,guided.Candidate,GuideRemaining,anchor.Profile,guided.SelectedResources);
                                        var guidedValue=ServiceBusinessObjectiveEvaluator.Calculate(instance,guided.Candidate,policy);
                                        if(ResourceSearchPolicy.GuidanceAccepts(evaluated.ViolationSeconds,baseValue,guidedProfile.ViolationSeconds,guidedValue,
                                            guidedProfile.Complete && guidedProfile.UnknownStations==0,couldBeJoint:ShouldStrictlyVerify(guidedProfile)))
                                        {
                                            if(pool.Add(candidate,evaluated))SaveBest();candidate=guided.Candidate;evaluated=guidedProfile;repair=guided;
                                            candidateFromRecovery=guidedRecovery.Applied;
                                            if(candidateFromRecovery)localActionSeconds=secondsBeforeGuideRecovery;
                                            guidanceStatus=candidateFromRecovery?"AcceptedSeparateClosedQuotaRecovery":"AcceptedSpanGuidance";
                                        }
                                        else guidanceStatus="OriginalRepairRetained";
                                    }
                                    if(guidedRecovery.Applied)externalRecoverySeconds+=guideClock.Elapsed.TotalSeconds-afterGuideRecovery;
                                }
                                catch(TimeoutException) { guidanceStatus="GuidanceDeadline_OriginalRepairRetained"; }
                                catch(Gurobi.GRBException ex) { guidanceStatus="GuidanceSolverError_OriginalRepairRetained_"+ex.ErrorCode; }
                                guidanceSeconds=guideClock.Elapsed.TotalSeconds;
                                feasibilityMemory.RecordGuidance(guidanceStatus.StartsWith("Accepted",StringComparison.Ordinal));
                            }
                        }
                        bool newPattern=!pool.ContainsPattern(candidate);
                        if(pool.Add(candidate,evaluated)) { SaveBest();diverse=!qualityPhase && newPattern; }
                        improved=pool.Best.Profile.ViolationSeconds<before-1e-5;
                        bool possiblyJoint=ShouldStrictlyVerify(evaluated);
                        if(possiblyJoint && SearchRemaining()>0)
                        {
                            var proof=Verify(candidate,round,SearchRemaining,evaluated);candidateStrict=proof.JointFeasible && proof.Canonical is not null && proof.Readback?.Passed==true;
                            if (candidateStrict && ReferenceEquals(comparedCandidate, candidate))
                                comparedCanonical = proof.Canonical;
                            AcceptJoint(proof,round);roundStatus=proof.Status;
                            if(qualityPhase && options.ImproveQuality && candidateStrict)
                            {
                                var canonical=proof.Canonical!;string structure=ResourceSearchPolicy.StructureKey(canonical);
                                var currentValue=ServiceBusinessObjectiveEvaluator.Calculate(instance,currentQuality!,policy);
                                var candidateValue=ServiceBusinessObjectiveEvaluator.Calculate(instance,canonical,policy);
                                double remainingFraction=SearchRemaining()/Math.Max(1,options.TotalSeconds-options.ReserveSeconds-(qualityStarted??0));
                                currentAccepted=ResourceSearchPolicy.AcceptCurrent(objective!,currentValue,candidateValue,true,
                                    !qualityStructures.Contains(structure),stagnation,remainingFraction);
                                if(currentAccepted)
                                {
                                    currentQuality=canonical;qualityStructures.Add(structure);
                                    diverse=qualityTimetables.Add(ResourceSearchPolicy.TimetableKey(canonical));microLinks=repair.TightLinks??[];
                                }
                            }
                        }
                    }
                    else roundStatus="UnknownMacroDiagnosticRejected";
                }
                // Auxiliary objective changes or conservative conflict-score changes cannot
                // masquerade as span improvement in phase B. Start its stagnation clock fresh.
                if(qualityPhase) improved=objective?.GlobalSpanSeconds<spanBefore-1e-6;
                stagnation=(!qualityPhase && joint is not null) || improved?0:stagnation+1;
                if(!qualityPhase && options.UseAlns)
                    feasibilityMemory.Record(anchorSearchKey,neighborhoodSignature,round,
                        !candidateFromRecovery && (improved || candidateValid && evaluated!.ViolationSeconds<anchor.Profile.ViolationSeconds-1e-5),roundStatus);
                double reward=0;
                if(adaptive is not null && action is not null)
                {
                    var after=repair.Candidate is not null && evaluated is not null?Vector(repair.Candidate,evaluated,qualityPhase):anchorValue;
                    var nowBest=qualityPhase && joint is not null?Vector(joint,anchor.Profile,true):Vector(pool.Best.Snapshot,pool.Best.Profile,false);
                    bool best=ResourceAlnsPolicy.Gain(bestValue,nowBest)>0;
                    reward=qualityPhase && options.ImproveQuality
                        ?ResourceSearchPolicy.QualityReward(anchorValue,after,candidateStrict,objective?.GlobalSpanSeconds<spanBefore-1e-6,diverse)
                        :adaptive.Reward(anchorValue,after,qualityPhase?candidateStrict:candidateValid,!hadJoint && joint is not null,best,diverse);
                    if(!qualityPhase && options.GuideFeasibilitySpan && candidateValid && after[0]<=anchorValue[0]+1e-6 &&
                        after[1]<=anchorValue[1]+1e-6 && after[2]<anchorValue[2]-1e-6)reward+=.1;
                    // Only the selected local attempt enters its arm/budget statistics. An expanded
                    // recovery and its subsequent checks are a separate candidate-producing action.
                    var localObservation=ResourceActionAccounting.LocalObservation(reward,iterationClock.Elapsed.TotalSeconds,
                        localActionSeconds,externalRecoverySeconds,candidateFromRecovery);
                    reward=localObservation.Reward;
                    adaptive.Record(action,qualityPhase,reward,localObservation.Seconds);
                    if(effortChoice is not null)
                        qualityBudgets!.Record(action.Id,effortChoice.TierSeconds,candidateStrict?reward:0,localObservation.Seconds);
                    if(recoveryAttempts.Count>0)
                        File.AppendAllText(Path.Combine(output,"local_action_history.jsonl"),System.Text.Json.JsonSerializer.Serialize(new {
                            round,phase=qualityPhase?"Quality":"Feasibility",action=action.Id,reward,seconds=localObservation.Seconds,
                            candidateFromRecovery,creditScope="LocalAttemptOnly_ExpandedRecoveryExcluded"})+Environment.NewLine);
                    Write("alns_state.json",new { round,settings=adaptive.Settings,weights=adaptive.Snapshot(),qualityBudgetWeights=qualityBudgets?.Snapshot() });
                }
                string? effectiveAction=candidateFromRecovery?"ClosedQuotaBoundaryRecovery":action?.Id;
                var iteration=new { round,phase=qualityPhase?"Quality":"Feasibility",qualityRound,action=effectiveAction,sourceAction=action?.Id,selectionReason,stagnation,
                    anchorSearchKey,neighborhoodSignature,nativeSeed,skippedActions,searchRadiusSeconds=neighborhood.RadiusSeconds,
                    nativeStatus=repair.NativeStatus,microSlack=repair.MicroSlack,macroFreeIntegers=repair.Audit?.CompactFreeIntegers,
                    candidateFromRecovery,recoveryAttempts,scope=candidateFromRecovery?"ExpandedFullMacroRecovery":"LocalNeighborhood",
                    localActionSeconds=candidateFromRecovery?localActionSeconds:iterationClock.Elapsed.TotalSeconds-externalRecoverySeconds,
                    externalRecoverySeconds=candidateFromRecovery?iterationClock.Elapsed.TotalSeconds-localActionSeconds:externalRecoverySeconds,
                    seconds=clock.Elapsed.TotalSeconds,iterationSeconds=iterationClock.Elapsed.TotalSeconds,neighborhoodSeconds,reward,
                    before,after=evaluated?.ViolationSeconds,best=pool.Best.Profile.ViolationSeconds,jointFeasible=joint is not null,
                    status=roundStatus,freeTimes=candidateFromRecovery?(int?)null:neighborhood.FreeTimeStopIds.Count,
                    freeStops=candidateFromRecovery?(int?)null:neighborhood.FreeStopIds.Count,
                    localFreeTimes=neighborhood.FreeTimeStopIds.Count,localFreeStops=neighborhood.FreeStopIds.Count,
                    repair.BuildSeconds,repair.SolveSeconds,repair.MicroRows,repair.MicroIntegers,repair.ConstructionLimitReason,candidateStrict,
                    guidanceSeconds,guidanceStatus,currentAccepted,distinctQualityStructures=qualityStructures.Count,distinctQualityTimetables=qualityTimetables.Count,repair.DiversityConstraint,
                    qualityBudgetTierSeconds=effortChoice?.TierSeconds,qualityRepairBudgetSeconds=effortChoice?.Seconds,
                    currentQualityObjective=currentQuality is null?null:ServiceBusinessObjectiveEvaluator.Calculate(instance,currentQuality,policy),
                    changedStops=repair.Candidate?.StopByStopId.Count(p=>anchor.Snapshot.StopByStopId[p.Key]!=p.Value),
                    spanBefore,bestJointObjective=objective,candidateObjective=repair.Candidate is null?null:ServiceBusinessObjectiveEvaluator.Calculate(instance,repair.Candidate,policy),
                    candidateConflictCount=evaluated?.Complete==true && evaluated.UnknownStations==0?evaluated.Conflicts.Count:(int?)null,
                    candidateEvaluationComplete=evaluated?.Complete,candidateUnknownStations=evaluated?.UnknownStations,
                    cumulativeSeconds=options.PriorElapsedSeconds+clock.Elapsed.TotalSeconds,spanGap=Gap(),weights=adaptive?.Snapshot() };
                if (quotaTriggered)
                {
                    bool accepted = comparedNativeStatus != 0 && comparedCanonical is not null;
                    File.AppendAllText(Path.Combine(output, "quota_comparison.jsonl"), JsonSerializer.Serialize(new
                    {
                        round, stage = "outcome", enteredNativeRepair = comparedNativeStatus != 0, accepted,
                        nativeStatus = comparedNativeStatus, candidateFromRecovery, candidateStrict
                    }) + Environment.NewLine);
                    if (comparedCanonical is not null && comparedCanonical.StopByStopId.Any(pair => anchor.Snapshot.StopByStopId[pair.Key] != pair.Value))
                    {
                        MasterSolutionSnapshotStore.Save(Path.Combine(output, $"quota_{round:D3}_before.snapshot.json"), instance.Meta.InstanceName, anchor.Snapshot);
                        SaveProfile($"quota_{round:D3}_before_profile.json", anchor.Profile);
                        Write($"quota_{round:D3}_counts.json", new { before = QuotaDependencySupport.Analyze(instance, anchor.Snapshot),
                            after = QuotaDependencySupport.Analyze(instance, comparedCanonical),
                            afterSnapshot = $"joint_candidate_{round:D3}.snapshot.json", strictRound = round });
                    }
                }
                if(options.UseAlns || options.CompactOutput)
                    File.AppendAllText(Path.Combine(output,"iterations.jsonl"),System.Text.Json.JsonSerializer.Serialize(iteration)+Environment.NewLine);
                if(joint is not null) WriteGap("QualityRoundCompleted");
                Write("progress.json",new { round,status=roundStatus,action=effectiveAction,sourceAction=action?.Id,candidateFromRecovery,selectionReason,neighborhoodSeconds,seconds=clock.Elapsed.TotalSeconds,remainingSeconds=Remaining(),
                    bestViolationSeconds=pool.Best.Profile.ViolationSeconds,currentViolationSeconds=evaluated?.ViolationSeconds,
                    failedStations=evaluated?.Stations.Values.Count(s=>s.Status==MicroSolveStatus.ProvenInfeasible),unknownStations=evaluated?.UnknownStations,
                    stagnation,poolSize=pool.Count,jointFeasible=joint is not null,bestJointObjective=objective,
                    firstJointRound,firstJointSeconds,qualityRound,spanGap=Gap(),boundStatus,
                    repair.BuildSeconds,repair.SolveSeconds,repair.Audit,repair.ConstructionLimitReason });
                Console.WriteLine(FormattableString.Invariant($"resource round={round} status={roundStatus} Vbest={pool.Best.Profile.ViolationSeconds:F3} V={evaluated?.ViolationSeconds:F3} build={repair.BuildSeconds:F2} solve={repair.SolveSeconds:F2} joint={joint is not null} wall={clock.Elapsed.TotalSeconds:F1}"));
            }
            // Reserved time is a real execution phase, even if the search found no joint incumbent.
            FinalVerification();
            status=joint is not null?"JointFeasible":round>=options.MaxRounds?"UnknownRoundLimit":"UnknownBudget";
            return Finish();
        }
        catch(TimeoutException)
        {
            status="UnknownSearchBudget";FinalVerification();return Finish();
        }
        catch(Exception ex)
        {
            Write("error.json",new { error=ex.ToString(),round,seconds=clock.Elapsed.TotalSeconds });status="Error";
            FinalVerification();return Finish();
        }
        finally { boundSolver?.Dispose();template?.Dispose(); }

        ResourceLocalResult RecoverClosedQuota(ResourceLocalResult result,Func<double> remaining,double allowance,
            string? sourceAction,out ResourceBoundaryRecoveryAttempt attempt)
        {
            attempt=new(false,false,0,"NotRequired");
            if(result.Candidate is null || remaining()<=0 || !ClosedQuotaCandidateRecovery.HasViolation(instance,result.Candidate))return result;
            var recoveryClock=System.Diagnostics.Stopwatch.StartNew();
            string sourceFingerprint=SnapshotFingerprint.Calculate(result.Candidate);
            MasterSolutionSnapshot? recovered=null;string recoveryStatus="NoAcceptedRecovery";
            try
            {
                recovered=ClosedQuotaCandidateRecovery.TryRecover(instance,result.Candidate,policy,remaining,
                    Math.Min(30,allowance*.2),closedQuotaRecoverySignatures,Path.Combine(localOutput,"closed_quota_recovery.log"));
                if(recovered is null)return result;
                recoveryStatus="ExpandedMacroCandidate";
                // Recovery can change every physical/resource decision. Do not reuse slack, assignments,
                // tight links or a profile belonging to the pre-recovery candidate for reward/acceptance.
                return result with { Candidate=recovered,SelectedResources=null,MicroSlack=null,TightLinks=null,
                    Status=result.Status+"_ClosedQuotaRecovered" };
            }
            catch(TimeoutException){recoveryStatus="UnknownBudget";return result;}
            catch(Gurobi.GRBException){recoveryStatus="SolverError";return result;}
            catch(InvalidDataException){recoveryStatus="InvalidRecovery";return result;}
            finally
            {
                attempt=new(true,recovered is not null,recoveryClock.Elapsed.TotalSeconds,recoveryStatus,
                    sourceFingerprint,recovered is null?null:SnapshotFingerprint.Calculate(recovered));
                File.AppendAllText(Path.Combine(output,"closed_quota_recovery_outcomes.jsonl"),System.Text.Json.JsonSerializer.Serialize(new {
                    round,action="ClosedQuotaBoundaryRecovery",sourceAction,attempt,
                    scope="ExpandedFullMacroRecovery",originalNeighborhoodPreserved=false,
                    changedStops=recovered?.StopByStopId.Count(p=>p.Value!=result.Candidate.StopByStopId[p.Key]),
                    changedArrivalTimes=recovered?.ArrivalSecondsByStopId.Count(p=>p.Value!=result.Candidate.ArrivalSecondsByStopId[p.Key]),
                    changedDepartureTimes=recovered?.DepartureSecondsByStopId.Count(p=>p.Value!=result.Candidate.DepartureSecondsByStopId[p.Key]),
                    requiresIndependentJointVerification=true,globalBound=false})+Environment.NewLine);
            }
        }

        void TryGlobalRetiming()
        {
            if(joint is null || options.StopAtFirstJoint || !options.ImproveQuality || SearchRemaining()<15)return;
            string fingerprint=SnapshotFingerprint.Calculate(joint);
            if(fingerprint==lastRetimingBest && stagnation<3 || qualityRound-lastRetimingRound<3)return;
            double allowance=RetimingAttemptPolicy.AvailableAllowance(options.TotalSeconds,SearchRemaining(),retimingEffortSeconds);
            if(retimingEffortSeconds>=options.TotalSeconds*.05 || allowance<10 || !retimingAttempts.CanFitAttempt(allowance))return;
            if(!retimingAttempts.CanAttemptSource(fingerprint,qualityRound,allowance,out string sourceRetryReason))return;
            var effort=System.Diagnostics.Stopwatch.StartNew();
            double RetimingRemaining()=>Math.Min(allowance-effort.Elapsed.TotalSeconds,SearchRemaining());
            double verificationReserve=retimingAttempts.VerificationReserve(allowance);
            double OptimizationRemaining()=>Math.Max(0,RetimingRemaining()-verificationReserve);
            var source=joint;var before=objective;lastRetimingBest=fingerprint;lastRetimingRound=qualityRound;
            string retimingStatus="NoCandidate";string? structure=null;int scalarStatus=0;bool accepted=false,scalarAttempted=false,buildExpired=false;
            bool allObjectivesOptimal=false,strictRetimingVerified=false;
            RetimingSourceFeasibilityCheck? sourceFeasibility=null;
            IReadOnlyList<RetimingObjectiveStageResult>? objectiveStages=null;
            RetimingAttemptOutcome attemptOutcome=RetimingAttemptOutcome.Unfinished;
            double buildSeconds=0,scalarSeconds=0,verificationSeconds=0;
            bool constructionCompleted=false;
            void Progress(string phase)=>Write("progress.json",new {phase,round,qualityRound,seconds=clock.Elapsed.TotalSeconds,
                jointFeasible=true,allowanceSeconds=allowance,verificationReserveSeconds=verificationReserve,
                remainingSeconds=RetimingRemaining(),buildSeconds,scalarSeconds,verificationSeconds});
            try
            {
                Progress("GlobalRetimingBuild");
                List<MicroWitnessCertificate> patterns=[];
                foreach(var station in (source.VerifiedMicroAssignments??[]).GroupBy(w=>w.StationName,StringComparer.OrdinalIgnoreCase))
                {
                    if(OptimizationRemaining()<=0)throw new TimeoutException("Retiming certificate budget expired.");
                    var built=MicroWitnessCertificateBuilder.Build(new(instance,source,station.ToArray(),
                        instance.Meta.InstanceName,"strict-global-retiming",0));
                    if(!built.IsSupported || built.Certificate is null)
                        throw new InvalidDataException("Retiming certificate unsupported: "+built.Reason);
                    patterns.Add(built.Certificate);
                }
                if(patterns.Sum(p=>p.Witnesses.Count)!=instance.TrainStops.Count)
                    throw new InvalidDataException("Retiming requires complete saved micro assignments.");
                // Conditions encode the selected resource order; omit time-bearing certificate IDs.
                string payload=ResourceSearchPolicy.StructureKey(source)+System.Text.Json.JsonSerializer.Serialize(
                    patterns.OrderBy(p=>p.StationName,StringComparer.Ordinal).Select(p=>new {p.StationName,p.Conditions}));
                if(OptimizationRemaining()<=0)throw new TimeoutException("Retiming build budget expired.");
                using var coordinator=FixedDiscreteContinuousCoordinator.Build(instance,source,patterns,
                    stationEventEdges:SavedJointSnapshotVerifier.ResolveSavedStationEdges(instance,source),
                    serviceWindows:policy,spanFirstRetiming:true,remaining:OptimizationRemaining);
                constructionCompleted=true;buildSeconds=effort.Elapsed.TotalSeconds;
                retimingAttempts.ObserveBuild(buildSeconds);
                structure=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(payload+"|"+coordinator.DiscreteBranchKey)));
                if(!retimingAttempts.CanAttemptStructure(structure,qualityRound,allowance,out string structureRetryReason))
                {retimingStatus="DeferredStructure_"+structureRetryReason;return;}
                if(OptimizationRemaining()<=0){buildExpired=true;throw new TimeoutException("Retiming construction exhausted optimization allowance.");}
                Progress("GlobalRetimingOptimize");
                ContinuousCoordinationResult? result;
                var scalarClock=System.Diagnostics.Stopwatch.StartNew();
                try { result=coordinator.SolveSpanFirst(OptimizationRemaining(),OptimizationRemaining,
                    options.CompactOutput?null:Path.Combine(output,"retiming.log")); }
                finally { scalarSeconds=scalarClock.Elapsed.TotalSeconds; }
                retimingStatus=result?.StopReason??"NoScalarIncumbent";scalarStatus=result?.StatusCode??0;
                sourceFeasibility=result?.SourceFeasibility;objectiveStages=result?.RetimingStages;
                scalarAttempted=objectiveStages?.Any(s=>s.StatusCode.HasValue)==true;
                allObjectivesOptimal=result?.AllRetimingObjectivesOptimal==true;
                attemptOutcome=sourceFeasibility?.Inconsistent==true?RetimingAttemptOutcome.SourceInconsistent:RetimingAttemptOutcome.Unfinished;
                if(result?.Snapshot is not null && result.MacroValidated && RetimingRemaining()>0)
                {
                    // Retain fixed tracks/routes as preferred witnesses with refreshed times. All acceptance
                    // still passes independent macro/micro checks and saved-snapshot fixed-resource readback.
                    string candidateFingerprint=SnapshotFingerprint.Calculate(result.Snapshot);
                    var candidate=result.Snapshot with { VerifiedMicroAssignments=source.VerifiedMicroAssignments?.Select(w=>w with {
                        CandidateSnapshotFingerprint=candidateFingerprint,
                        ArrivalSeconds=result.Snapshot.ArrivalSecondsByStopId[w.RequirementId],
                        DepartureSeconds=result.Snapshot.DepartureSecondsByStopId[w.RequirementId] }).ToArray() };
                    Progress("GlobalRetimingStrictVerification");
                    var verificationClock=System.Diagnostics.Stopwatch.StartNew();
                    Stages45JointProof proof;
                    try { proof=Verify(candidate,100000+qualityRound,RetimingRemaining); }
                    finally { verificationSeconds=verificationClock.Elapsed.TotalSeconds; }
                    strictRetimingVerified=proof.JointFeasible && proof.Readback?.Passed==true;
                    if(allObjectivesOptimal && strictRetimingVerified)attemptOutcome=RetimingAttemptOutcome.AllObjectivesOptimal;
                    AcceptJoint(proof,100000+qualityRound);
                    accepted=SnapshotFingerprint.Calculate(joint!)!=fingerprint;
                    if(accepted){currentQuality=joint;qualityStructures.Add(ResourceSearchPolicy.StructureKey(joint!));qualityTimetables.Add(ResourceSearchPolicy.TimetableKey(joint!));}
                }
            }
            catch(Exception ex){buildExpired|=!constructionCompleted && OptimizationRemaining()<=0;retimingStatus="SkippedOrFailed_ExistingStrictJointRetained: "+ex.Message;}
            finally
            {
                if(!constructionCompleted){buildSeconds=effort.Elapsed.TotalSeconds;retimingAttempts.ObserveBuild(buildSeconds);}
                retimingEffortSeconds+=effort.Elapsed.TotalSeconds;
                if(buildExpired)attemptOutcome=RetimingAttemptOutcome.BuildBudgetExpired;
                retimingAttempts.Record(fingerprint,structure,qualityRound,allowance,
                    attemptOutcome,scalarAttempted);
                File.AppendAllText(Path.Combine(output,"retiming_history.jsonl"),System.Text.Json.JsonSerializer.Serialize(new {qualityRound,sourceFingerprint=fingerprint,structure,retimingStatus,scalarStatus,scalarAttempted,buildExpired,accepted,
                    sourceRetryReason,sourceFeasibility,objectiveStages,allObjectivesOptimal,strictRetimingVerified,attemptOutcome,
                    allowanceSeconds=allowance,verificationReserveSeconds=verificationReserve,buildSeconds,scalarSeconds,verificationSeconds,
                    wallSeconds=effort.Elapsed.TotalSeconds,totalRetimingSeconds=retimingEffortSeconds,
                    before,after=objective,spanBenefitSeconds=(before?.GlobalSpanSeconds??0)-(objective?.GlobalSpanSeconds??0),
                    scope="FixedStructureFullTimeRecourse_NotGlobalLowerBound",existingStrictJointRetained=joint is not null})+Environment.NewLine);
            }
        }

        void UpdateGlobalBound()
        {
            if(joint is null || options.StopAtFirstJoint || boundDisabled) return;
            double allowed=boundBudget.Allowance(true,qualityRound,SearchRemaining());
            if(allowed<=.01) return;
            var effort=System.Diagnostics.Stopwatch.StartNew();
            try
            {
                Write("progress.json",new { phase="GlobalSpanLowerBound",round,qualityRound,seconds=clock.Elapsed.TotalSeconds,
                    jointFeasible=true,firstJointRound,firstJointSeconds,allowedSeconds=allowed,spanGap=Gap() });
                // No native model/environment is created until this strictly gated point.
                boundSolver??=new(instance,options.Seed);
                var result=boundSolver.Solve(allowed,SearchRemaining,Path.Combine(output,$"bound_quality_{qualityRound:D3}.log"));
                boundStatus=result.Status;
                Write($"bound_quality_{qualityRound:D3}.json",result);
                if(result.LowerBoundSeconds is double lower) globalLowerBound=Math.Max(globalLowerBound??0,lower);
                if(result.NativeStatus is Gurobi.GRB.Status.INFEASIBLE or Gurobi.GRB.Status.INF_OR_UNBD or Gurobi.GRB.Status.UNBOUNDED)
                {
                    globalLowerBound=null;boundStatus="BoundModelContradictsExistingJoint";boundDisabled=true;
                }
            }
            catch(Exception ex)
            {
                // Quality-bound failure must not erase the already checked joint solution.
                boundStatus="BoundError_ExistingJointRetained";boundDisabled=true;
                boundSolver?.Dispose();boundSolver=null;
                Write($"bound_quality_{qualityRound:D3}_error.json",new { error=ex.ToString(),existingJointRetained=true });
            }
            finally
            {
                effort.Stop();boundBudget.Record(qualityRound,effort.Elapsed.TotalSeconds);WriteGap("BoundPassCompleted");
            }
        }
        void WriteGap(string reason)
        {
            var record=new { reason,round,qualityRound,seconds=clock.Elapsed.TotalSeconds,
            firstJointRound,firstJointSeconds,spanGap=Gap(),boundStatus,boundEffortSeconds=boundBudget.UsedSeconds,
            boundScope=ResourceGlobalSpanBoundSolver.BoundScope,bestObjective=objective,finalVerificationStatus,globalOptimalityProven=false };
            Write("gap_progress.json",record);
            File.AppendAllText(Path.Combine(output,"gap_history.jsonl"),System.Text.Json.JsonSerializer.Serialize(record)+Environment.NewLine);
        }

        Stages45JointProof Verify(MasterSolutionSnapshot candidate,int id,Func<double> deadline,ResourceConflictProfile? resources=null)
        {
            var assignments=options.UseAlns && resources is not null?resources.Stations.Values.SelectMany(st=>st.Assignments.Select(a=>
                new VerifiedMicroAssignmentWitness(st.StationName,a.RequirementId,a.TrainIndex,a.IsStop,a.TrackName,
                    a.IncomingRoute?.RouteId,a.OutgoingRoute?.RouteId,ArrivalSeconds:a.ArrivalSeconds,DepartureSeconds:a.DepartureSeconds))).ToArray():null;
            var verificationClock=System.Diagnostics.Stopwatch.StartNew();
            var proof=Stages45JointVerifier.Verify(instance,candidate,policy,deadline,Math.Min(120,deadline()),output,id,
                promotionSlots:0,preferredAssignments:assignments??candidate.VerifiedMicroAssignments,allowClosedQuotaRecovery:false);
            if(proof.JointFeasible && proof.Readback?.Passed==true)retimingAttempts.ObserveVerification(verificationClock.Elapsed.TotalSeconds);
            return proof;
        }
        void FinalVerification()
        {
            if(finalVerificationAttempted) return;
            finalVerificationAttempted=true;
            try
            {
            if(options.StopAtFirstJoint && joint is not null)
            { finalVerificationStatus="AlreadyStrictlyVerifiedJoint";Write("final_verification.json",new { status=finalVerificationStatus,jointFeasible=true });return; }
            if(Remaining()<=0)
            { finalVerificationStatus="SkippedTotalBudgetExpired";Write("final_verification.json",new { status=finalVerificationStatus,jointFeasible=joint is not null,remainingSeconds=0 });return; }
            Write("progress.json",new { phase="FinalStrictVerification",round,seconds=clock.Elapsed.TotalSeconds,jointFeasible=joint is not null });
            var final=Verify(joint??(pool.Count>0?pool.Best.Snapshot:snapshot),round+1,Remaining,joint is null && pool.Count>0?pool.Best.Profile:null);
            finalVerificationStatus=final.JointFeasible && final.Canonical is not null && final.Readback?.Passed==true?"Passed":final.Status;
            AcceptJoint(final,round+1);
            Write("final_verification.json",new { final.Status,final.CompleteMacro,final.JointFeasible,final.Seconds,
                finalVerificationStatus,reserveSeconds=options.ReserveSeconds,remainingSeconds=Remaining() });
            }
            catch(Exception ex)
            {
                finalVerificationFailed=true;finalVerificationStatus="FinalVerificationError";
                Write("final_verification.json",new { status="FinalVerificationError",error=ex.ToString(),
                    jointFeasible=joint is not null,existingJointRetained=joint is not null,remainingSeconds=Remaining() });
            }
        }
        void AcceptJoint(Stages45JointProof proof,int id)
        {
            if(!proof.JointFeasible || proof.Canonical is null || proof.Readback?.Passed!=true) return;
            var value=ServiceBusinessObjectiveEvaluator.Calculate(instance,proof.Canonical,policy);
            if(objective is not null && !ResourceSpanObjectivePolicy.Better(value,objective)) return;
            joint=proof.Canonical;objective=value;
            if(firstJointRound is null)
            {
                firstJointRound=id;firstJointSeconds=clock.Elapsed.TotalSeconds;qualityStarted=clock.Elapsed.TotalSeconds;
                currentQuality=joint;qualityStructures.Add(ResourceSearchPolicy.StructureKey(joint));
                qualityTimetables.Add(ResourceSearchPolicy.TimetableKey(joint));
                MasterSolutionSnapshotStore.Save(Path.Combine(output,"first_joint_feasible.snapshot.json"),instance.Meta.InstanceName,joint);
                Write("first_joint_acceptance.json",new { round=id,seconds=firstJointSeconds,objective,readback=proof.Readback });
            }
            MasterSolutionSnapshotStore.Save(Path.Combine(output,"joint_feasible.snapshot.json"),instance.Meta.InstanceName,joint);
            Write("joint_acceptance.json",new { round=id,proof.Status,proof.JointFeasible,fingerprint=SnapshotFingerprint.Calculate(joint),
                objective,readback=proof.Readback,seconds=clock.Elapsed.TotalSeconds,firstJointRound,firstJointSeconds,
                spanGap=Gap(),globalOptimalityProven=false });
            WriteGap("StrictJointAccepted");
        }
        IReadOnlyList<LocalMicroRepairSelectedAlternative>? Preferred(MasterSolutionSnapshot? value)
            =>options.ResumeStrictJoint || options.ImproveQuality?value?.VerifiedMicroAssignments?.Select(w=>new LocalMicroRepairSelectedAlternative(
                w.StationName,w.RequirementId,w.TrainIndex,w.TrackName,w.IncomingRouteId,w.OutgoingRouteId,w.IsStop)).ToArray():null;
        double[] Vector(MasterSolutionSnapshot value,ResourceConflictProfile profile,bool quality)
        {
            var v=ServiceBusinessObjectiveEvaluator.Calculate(instance,value,policy);
            return quality?[v.GlobalSpanSeconds,v.ServicePenalty,v.RunningTimeSupplementSeconds]:
                [profile.ViolationSeconds,v.ServicePenalty,v.GlobalSpanSeconds,v.RunningTimeSupplementSeconds];
        }
        void SaveBest()
        {
            MasterSolutionSnapshotStore.Save(Path.Combine(output,"best_resource.snapshot.json"),instance.Meta.InstanceName,pool.Best.Snapshot);
            SaveProfile("best_resource_profile.json",pool.Best.Profile);
        }
        void SaveProfile(string name,ResourceConflictProfile profile)=>Write(name,new { profile.Complete,profile.UnknownStations,profile.ViolationSeconds,
            conflictCount=profile.Conflicts.Count,profile.Conflicts,stations=profile.Stations.Values,
            scope="SearchHeuristic_NotLowerBound_NotJointAcceptance" });
        ResourceRunResult Finish()
        {
            if(finalVerificationFailed) status="Error";
            string stopReason=DetermineStopReason(status,completeMacro,joint is not null,round,options,Gap(),finalVerificationStatus);
            if(status!="Error" && joint is not null) status="JointFeasible";
            WriteGap("Finished");
            var result=new ResourceRunResult(status,completeMacro,joint is not null,round,clock.Elapsed.TotalSeconds,objective,
                joint is null?null:SnapshotFingerprint.Calculate(joint),Gap(),stopReason,firstJointRound,firstJointSeconds,
                qualityRound,boundBudget.UsedSeconds,boundStatus,finalVerificationStatus);Write("summary.json",result);return result;
        }
    }

    internal static int Run(string[] args)
    {
        string Required(string prefix)=>args.FirstOrDefault(a=>a.StartsWith(prefix,StringComparison.Ordinal))?[prefix.Length..]
            ??throw new ArgumentException("Missing "+prefix);
        double Number(string prefix,double fallback)=>args.FirstOrDefault(a=>a.StartsWith(prefix,StringComparison.Ordinal)) is string text?
            double.Parse(text[prefix.Length..],System.Globalization.CultureInfo.InvariantCulture):fallback;
        var instance=new NormalizedDatasetLoader().Load(Required("--dataset="));
        var snapshot=MasterSolutionSnapshotStore.Load(Required("--repair-snapshot="),instance.Meta.InstanceName,instance.Trains.Select(t=>t.TrainIndex).ToHashSet());
        var result=Solve(instance,snapshot,new(Required("--repair-output="),Number("--repair-total=",14400),Number("--repair-iteration=",150),
            Number("--repair-reserve=",600),checked((int)Number("--repair-seed=",0)),checked((int)Number("--repair-rounds=",500)),args.Contains("--repair-first-joint"),
            Number("--repair-target-gap=",.01),Number("--repair-bound-pass=",120),Number("--repair-bound-total=",900),
            checked((int)Number("--repair-bound-interval=",10)),QualityIterationSeconds:
                args.Any(a=>a.StartsWith("--repair-quality-iteration=",StringComparison.Ordinal))?Number("--repair-quality-iteration=",600):null,
            UseAlns:args.Contains("--repair-alns"),CompactOutput:args.Contains("--repair-compact"),
            ResumeStrictJoint:args.Contains("--repair-resume-joint"),PriorElapsedSeconds:Number("--repair-prior-seconds=",0),
            ImproveQuality:args.Contains("--alns-quality-search") || args.Contains("--alns-span-improvements"),
            GuideFeasibilitySpan:args.Contains("--alns-feasibility-span") || args.Contains("--alns-span-improvements")));
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result));return result.Status is "Error" or "ResumeStrictVerificationFailed"?2:0;
    }

    internal static bool ValidImportedWitness(TimetablingInstance instance,MasterSolutionSnapshot snapshot)
    {
        var witnesses=snapshot.VerifiedMicroAssignments;
        if(witnesses is null || !MacroMicroJointAcceptancePolicy.CheckIdentity(SnapshotFingerprint.Calculate(snapshot),
            instance.TrainStops.Select(s=>s.StopId),witnesses).IsValid)return false;
        return witnesses.All(w=>w.VerificationScope=="station_micro_validated" &&
            instance.StopsById.TryGetValue(w.RequirementId,out var stop) && stop.TrainIndex==w.TrainIndex &&
            stop.StationName.Equals(w.StationName,StringComparison.OrdinalIgnoreCase) && snapshot.StopByStopId[w.RequirementId]==(w.IsStop?1:0) &&
            w.ArrivalSeconds is double a && double.IsFinite(a) && Math.Abs(a-snapshot.ArrivalSecondsByStopId[w.RequirementId])<=1e-6 &&
            w.DepartureSeconds is double d && double.IsFinite(d) && Math.Abs(d-snapshot.DepartureSecondsByStopId[w.RequirementId])<=1e-6);
    }

    internal static bool ShouldStrictlyVerify(ResourceConflictProfile profile)=>profile.Complete && profile.UnknownStations==0 &&
        profile.Stations.Count>0 && profile.Stations.Values.All(s=>s.Status==MicroSolveStatus.Feasible);

    internal static string DetermineStopReason(string status,bool completeMacro,bool joint,int round,ResourceRunOptions options,
        ResourceSpanGapReport gap,string finalVerificationStatus)=>status=="Error"?"Error":
        status=="ResumeStrictVerificationFailed"?status:
        joint && finalVerificationStatus is not ("Passed" or "AlreadyStrictlyVerifiedJoint")?"FinalVerificationNotConfirmed":
        options.StopAtFirstJoint && joint?"RequestedFirstJoint":gap.TargetMet?"TargetRelativeGapMet":
        round>=options.MaxRounds?"RoundLimit":!completeMacro?status:"TimeBudgetOrInsufficientNextRoundBudget";
}
