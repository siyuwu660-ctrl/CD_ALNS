using GurobiTimetablingResearch.Domain;
using GurobiTimetablingResearch.IO;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
namespace GurobiTimetablingResearch.Optimization;

internal sealed class JhFromZeroBudget
{
    private readonly double total,macro,reserve;
    internal JhFromZeroBudget(double total,double macro,double reserve)
    {
        if(!double.IsFinite(total) || !double.IsFinite(macro) || !double.IsFinite(reserve) ||
            total<=0 || macro<=0 || reserve<0 || macro+reserve>=total) throw new ArgumentException("Invalid from-zero phase budget.");
        this.total=total;this.macro=macro;this.reserve=reserve;
    }
    internal double MacroRemaining(double elapsed)=>Math.Max(0,Math.Min(macro-elapsed,total-elapsed));
    internal double JointRemaining(double elapsed)=>Math.Max(0,total-elapsed);
    internal double RecoveryRemaining(double elapsed)=>Math.Max(0,total-reserve-elapsed);
}
internal sealed record JhFromZeroOptions(string OutputDirectory,double TotalSeconds=172800,double MacroSeconds=28800,
    double ReserveSeconds=1800,int Seed=0,double ColdSolveSeconds=1800,double MacroRepairSeconds=1800,
    double MacroIterationSeconds=180,double FeasibilityIterationSeconds=150,double QualityIterationSeconds=600,
    double TargetRelativeGap=.01,double BoundPassSeconds=1800,double BoundTotalSeconds=21600,int BoundEveryQualityRounds=10,int JointMaxRounds=100000,bool UseAlns=false,bool StopAtFirstJoint=false,bool CompactOutput=false,
    bool RobustMacroInitialization=true,JhMacroRecoverySettings? MacroRecovery=null,bool ImproveQuality=false,bool GuideFeasibilitySpan=false,bool DisableMacroAlns=false,MacroAlnsSettings? MacroAlns=null, bool QuotaControl = false);
internal sealed record JhFromZeroResult(string Status,bool CompleteMacroFeasible,double Seconds,ResourceRunResult? Joint);
internal static class JhFromZeroRunner
{
    /// <summary>Build a macro timetable from original input, then run the configured joint repair and quality search.</summary>
    /// <param name="instance">Original timetable input.</param>
    /// <param name="options">Phase budgets in seconds, seed, comparison arm and output path.</param>
    /// <returns>Completed phases, final status and the joint result when that phase runs.</returns>
    /// <remarks>The run creates a new evidence directory.</remarks>
    internal static JhFromZeroResult Solve(TimetablingInstance instance,JhFromZeroOptions options)
    {
        if((options.ImproveQuality || options.GuideFeasibilitySpan) && !options.UseAlns)
            throw new ArgumentException("Search improvements require ALNS.");
        var budget=new JhFromZeroBudget(options.TotalSeconds,options.MacroSeconds,options.ReserveSeconds);
        if(new[]{options.ColdSolveSeconds,options.MacroRepairSeconds,options.MacroIterationSeconds,
            options.FeasibilityIterationSeconds,options.QualityIterationSeconds,options.BoundPassSeconds}.Any(v=>!double.IsFinite(v) || v<=0) ||
            options.Seed<0 || options.JointMaxRounds<1 || options.BoundEveryQualityRounds<1 ||
            !double.IsFinite(options.BoundTotalSeconds) || options.BoundTotalSeconds<0)
            throw new ArgumentException("Invalid from-zero solver options.");
        var recoverySettings=options.MacroRecovery??new JhMacroRecoverySettings();
        recoverySettings.Validate();
        if(options.RobustMacroInitialization && options.Seed>2_000_000_000) throw new ArgumentException("Gurobi seed out of range.");
        var recovery=options.RobustMacroInitialization?new JhMacroRecovery(recoverySettings):null;
        _=ResourceSpanGapReport.Calculate(null,null,options.TargetRelativeGap);
        string output=Path.GetFullPath(options.OutputDirectory);
        if(Directory.Exists(output)) throw new IOException("Refusing to overwrite a from-zero experiment.");
        Directory.CreateDirectory(output);
        var clock=Stopwatch.StartNew();DateTimeOffset started=DateTimeOffset.Now;
        using var macroTemplate=options.UseAlns && !options.DisableMacroAlns?new DirectedRepairModelCache():null;
        MasterSolutionSnapshot? macro=null;ResourceRunResult? joint=null;int attempt=0;string status="UnknownMacroBudget";
        MacroAlnsState? macroLearning=null;
        HashSet<string> closedQuotaRecoveryAttempts=new(StringComparer.Ordinal);
        JhMacroCandidate? bestCandidate=null;DirectedMacroSearchState? macroState=null;int sweepOffset=0;bool checkpointPassed=false;
        var trainIds=instance.Trains.Select(t=>t.TrainIndex).ToHashSet();
        void Write(string name,object data)=>Stages45Runner.Write(output,name,data);
        void Event(string phase,object? details=null)
        {
            var record=new { phase,seconds=clock.Elapsed.TotalSeconds,time=DateTimeOffset.Now,attempt,details };
            Write("progress.json",record);
            File.AppendAllText(Path.Combine(output,"stage_history.jsonl"),JsonSerializer.Serialize(record)+Environment.NewLine);
            Console.WriteLine($"from-zero phase={phase} attempt={attempt} wall={clock.Elapsed.TotalSeconds:F1}");
        }
        void Checkpoint()
        {
            if(checkpointPassed || clock.Elapsed.TotalSeconds<options.MacroSeconds) return;
            checkpointPassed=true;Event("MacroCheckpointPassed",new { action="ContinueThisRunBestWithinGlobalBudget",remaining=budget.RecoveryRemaining(clock.Elapsed.TotalSeconds) });
        }
        List<JhMacroCandidate> RetainCandidates(IEnumerable<string> paths)
        {
            var loaded=new List<JhMacroCandidate>();
            var choices=new List<JhMacroCandidate>();if(bestCandidate is not null) choices.Add(bestCandidate);
            foreach(string path in paths)
            {
                string relative=Path.GetRelativePath(output,Path.GetFullPath(path));
                if(Path.IsPathRooted(relative) || relative==".." || relative.StartsWith(".."+Path.DirectorySeparatorChar,StringComparison.Ordinal))
                    throw new InvalidDataException("Continuation input must originate in this run.");
                var snapshot=MasterSolutionSnapshotStore.Load(path,instance.Meta.InstanceName,trainIds);
                var diagnosis=DirectedCandidateDiagnostics.Analyze(instance,snapshot);
                loaded.Add(new(snapshot,path,DirectedRepairRunner.Score(diagnosis),diagnosis.Conflicts.Count,diagnosis.UnresolvedLongDwellStopIds.Count));
            }
            choices.AddRange(loaded);bestCandidate=JhMacroCandidate.SelectBest(choices);
            if(bestCandidate is null) return loaded;
            MasterSolutionSnapshotStore.Save(Path.Combine(output,"best_macro_candidate.snapshot.json"),instance.Meta.InstanceName,bestCandidate.Snapshot);
            var progress=new { attempt,bestCandidate.SourcePath,bestCandidate.Score,bestCandidate.Conflicts,bestCandidate.LongDwells,
                fingerprint=SnapshotFingerprint.Calculate(bestCandidate.Snapshot),seconds=clock.Elapsed.TotalSeconds,
                scope="BestDiagnosticCandidate_NotMacroOrJointCertificate",fromThisRunOnly=true };
            Write("macro_candidate_progress.json",progress);
            File.AppendAllText(Path.Combine(output,"macro_candidate_history.jsonl"),JsonSerializer.Serialize(progress)+Environment.NewLine);
            return loaded;
        }
        DirectedMacroSearchState LoadState(string directory)
            =>JsonSerializer.Deserialize<DirectedMacroSearchState>(File.ReadAllText(Path.Combine(directory,"macro_search_state.json")),
                new JsonSerializerOptions { PropertyNameCaseInsensitive=true })??throw new InvalidDataException("Missing macro continuation state.");
        void RecoveryProgress()
        {
            if(recovery is null) return;
            Write("macro_recovery.json",new { recovery.GenerationCount,
                bestScore=bestCandidate?.Score,seconds=clock.Elapsed.TotalSeconds,remaining=budget.RecoveryRemaining(clock.Elapsed.TotalSeconds),
                candidates=recovery.Branches.Select(b=>new { b.Id,b.Candidate.SourcePath,b.Candidate.Score,b.Candidate.Conflicts,b.Candidate.LongDwells,
                    b.Attempts,b.FailedBatches,b.SweepOffset,b.Continuation.LastBuildSeconds,b.Continuation.ConsecutiveBuildBudgetFailures,
                    buildBlocked=DirectedRepairBudget.Blocked(b.Continuation),fingerprint=SnapshotFingerprint.Calculate(b.Candidate.Snapshot) }),
                scope="DiagnosticCandidates_NotFeasibilityCertificates" });
        }
        Write("configuration.json",new { options,instance.Meta,started,nominalDeadline=started.AddSeconds(options.TotalSeconds),
            fromOriginalData=true,importedSnapshot=false,importedFeedbackCount=0,
            inputSemantics=$"ExistingProjectNormalized{instance.Meta.InstanceName}_NoHistoricalSolutionState",
            macroAlnsEnabled=options.UseAlns && !options.DisableMacroAlns,macroAlnsSettings=options.MacroAlns??new MacroAlnsSettings(),
            algorithm=options.UseAlns?(options.DisableMacroAlns?"FreshQuotaSeed_LegacyBoundedMacro_ResourceAlns":"FreshQuotaSeed_AdaptiveMacroDirectionScale_ResourceAlns"):"FreshQuotaSeed_SparseImprovement_BoundedMacroContinuation_ResourceJointLns_SpanGap",
            macroRecoveryEnabled=recovery is not null,macroRecoverySettings=recoverySettings,
            emptyCandidateGenerationPolicy="GlobalBudget_InitialSolveTimes1_2_4_4_RepairAndVerificationReserved",
            macroCheckpointIsHardLimit=false,initialCandidateRounds=3,macroPairBaseLimit=160,macroPairMaximum=640,catastrophicTrialScoreMultiplier=4,
            macroTemplateReuseEnabled=macroTemplate is not null,buildFailureLimit=3,repairBudgetPolicy="MeasuredBuildPlusSolve_WithinGlobalWallBudget",
            budgetSemantics="Wall clock incl build/check, synchronous native overhead may overshoot" });
        try
        {
            Directory.CreateDirectory(Path.Combine(output,"01_macro"));
            while(budget.RecoveryRemaining(clock.Elapsed.TotalSeconds)>5 && macro is null)
            {
                attempt++;Checkpoint();
                double left=budget.RecoveryRemaining(clock.Elapsed.TotalSeconds);
                JhMacroBranch? branch=recovery?.Select();
                double repairCap=recovery is null?options.MacroRepairSeconds:Math.Min(options.MacroRepairSeconds,recoverySettings.RepairBatchSeconds);
                JhMacroGeneration? generation=null;
                if(recovery is not null && branch is null)
                {
                    // A short run must still try its first seed; only later restarts require a full repair slice.
                    double generationReserve=recovery.GenerationCount==0?Math.Min(repairCap,left/3):repairCap;
                    generation=recovery.BeginGeneration(left,options.ColdSolveSeconds,generationReserve,options.Seed);
                    if(generation is null)
                    {
                        branch=recovery.Select(allowStalled:true);
                        if(branch is null) { status=recovery.Branches.Any(b=>DirectedRepairBudget.Blocked(b.Continuation))?"UnknownMacroBuildBudget":"UnknownMacroBudget";break; }
                    }
                }
                if(generation is not null || recovery is null && bestCandidate is null)
                {
                    string coldDir=Path.Combine(output,"01_macro",$"attempt_{attempt:D3}_cold");
                    double solve=generation?.SolveSeconds??Math.Min(options.ColdSolveSeconds*Math.Pow(2,Math.Min(attempt-1,2)),Math.Max(.1,left-2));
                    double total=generation?.TotalSeconds??Math.Min(left,solve+300),reserve=Math.Min(2,total/10);
                    int seed=generation?.Seed??options.Seed+attempt-1;
                    Event("ColdMacroCandidate",new { coldDir,total,solve,seed,generation=generation?.Index,candidateImprovementRounds=3 });
                    var cold=BottleneckExperimentRunner.Solve(instance,new(coldDir,total,solve,reserve,
                        Seed:seed,SafeSparse:true,SoftWindows:true,MacroCandidateRounds:3));
                    Event("ColdCandidateFinished",cold);Checkpoint();
                    if(cold.Status=="Error") { status="ErrorColdGeneration";break; }
                    var generated=RetainCandidates(Directory.GetFiles(coldDir,"candidate_*.snapshot.json").Order(StringComparer.Ordinal));
                    string coldStatePath=Path.Combine(coldDir,"macro_search_state.json");
                    var coldState=File.Exists(coldStatePath)?LoadState(coldDir):null;
                    if(recovery is not null)
                    {
                        if(coldState is not null) recovery.MergeGuards(coldState);
                        foreach(var item in generated) recovery.Add(item);
                        branch=recovery.Select();
                        RecoveryProgress();
                        // A failed or duplicate generation leaves every previous candidate intact.
                        if(branch is null) continue;
                    }
                    else
                    {
                        if(bestCandidate is null) continue;
                        macroState=coldState;
                    }
                }
                var repairCandidate=branch?.Candidate??bestCandidate!;
                var repairState=branch is null?macroState:recovery!.StateFor(branch);
                if(options.UseAlns && !options.DisableMacroAlns) repairState=(repairState??new DirectedMacroSearchState([],[])) with {Alns=macroLearning};
                int repairSweep=branch?.SweepOffset??sweepOffset;
                left=budget.RecoveryRemaining(clock.Elapsed.TotalSeconds);
                if(left<=5) break;
                string repairDir=Path.Combine(output,"01_macro",$"attempt_{attempt:D3}_repair");
                double repairTotal=DirectedRepairBudget.Batch(repairCap,options.MacroRepairSeconds,left,options.MacroIterationSeconds,
                    repairState?.LastBuildSeconds??0,repairState?.ConsecutiveBuildBudgetFailures??0);
                Event("CompleteMacroRepair",new { candidatePath=repairCandidate.SourcePath,repairDir,seconds=repairTotal,branch=branch?.Id,
                    inputFingerprint=SnapshotFingerprint.Calculate(repairCandidate.Snapshot),repairCandidate.Score,sweepOffset=repairSweep,retainedEdges=repairState?.Edges.Length??0 });
                var repair=DirectedRepairRunner.Solve(instance,repairCandidate.Snapshot,new(repairDir,repairTotal,options.MacroIterationSeconds,
                    ReserveSeconds:Math.Min(120,repairTotal/10),Seed:recovery is null?options.Seed+attempt-1:JhMacroRecovery.Seed(options.Seed,attempt-1),MaxRounds:100000,
                    ComponentClosure:true,StopAtFirstCompleteMacro:true,BoundedMacroSearch:true,MacroSweepOffset:repairSweep,MacroState:repairState,
                    MacroStagnationRounds:recovery is null?0:recoverySettings.StagnationRounds,
                    UseMacroAlns:options.UseAlns && !options.DisableMacroAlns,MacroAlns:options.MacroAlns,
                    ClosedQuotaRecoveryAttempts:closedQuotaRecoveryAttempts),macroTemplate);
                Event("MacroRepairFinished",repair);Checkpoint();
                if(repair.Status=="Error") { status="ErrorMacroRepair";break; }
                var repaired=RetainCandidates(Directory.GetFiles(repairDir,"best_working.snapshot.json"));
                var nextState=LoadState(repairDir);
                if(options.UseAlns && !options.DisableMacroAlns) macroLearning=nextState.Alns;
                if(branch is not null)
                {
                    recovery!.Record(branch,JhMacroCandidate.SelectBest(repaired)??repairCandidate,nextState,repair.Rounds);
                    RecoveryProgress();
                }
                else { macroState=nextState;sweepOffset=checked(sweepOffset+Math.Max(1,repair.Rounds)); }
                if(repair.Rounds==0) break; // Insufficient time even to build: do not spin through empty batches.
                string completePath=Path.Combine(repairDir,"complete_macro.snapshot.json");
                if(!repair.CompleteMacroFeasible || !File.Exists(completePath)) continue;
                macro=MasterSolutionSnapshotStore.Load(completePath,instance.Meta.InstanceName,instance.Trains.Select(t=>t.TrainIndex).ToHashSet())
                    with { VerifiedMicroAssignments=null,BoundReport=null,SolutionRole=TimetableSolutionRole.WorkingSolution };
                MasterSolutionSnapshotStore.Save(Path.Combine(output,"complete_macro.snapshot.json"),instance.Meta.InstanceName,macro);
                Write("macro_handoff.json",new { source=completePath,fingerprint=SnapshotFingerprint.Calculate(macro),
                    seconds=clock.Elapsed.TotalSeconds,fromThisRunOnly=true,importedMicroAssignments=0 });
            }
            macroTemplate?.Dispose();
            if(macro is not null)
            {
                double left=budget.JointRemaining(clock.Elapsed.TotalSeconds);
                if(left<=options.ReserveSeconds) status="UnknownBudgetBeforeJoint";
                else
                {
                    Event("JointAndSpan",new { seconds=left,reserveSeconds=options.ReserveSeconds });
                    joint=ResourceLnsRunner.Solve(instance,macro,new(Path.Combine(output,"02_joint_span"),left,
                        options.FeasibilityIterationSeconds,options.ReserveSeconds,options.Seed,options.JointMaxRounds,StopAtFirstJoint:options.StopAtFirstJoint,
                        TargetRelativeGap:options.TargetRelativeGap,BoundPassSeconds:options.BoundPassSeconds,
                        BoundTotalSeconds:options.BoundTotalSeconds,BoundEveryQualityRounds:options.BoundEveryQualityRounds,
                        QualityIterationSeconds:options.QualityIterationSeconds,UseAlns:options.UseAlns,CompactOutput:options.CompactOutput,
                        ImproveQuality:options.ImproveQuality,GuideFeasibilitySpan:options.GuideFeasibilitySpan,
                        PriorElapsedSeconds: clock.Elapsed.TotalSeconds, QuotaControl: options.QuotaControl));
                    status=joint.Status;Event("JointAndSpanFinished",joint);
                }
            }
        }
        catch(Exception ex) { status="Error";Write("error.json",new { exception=ex.ToString(),attempt,seconds=clock.Elapsed.TotalSeconds }); }
        var result=new JhFromZeroResult(status,macro is not null,clock.Elapsed.TotalSeconds,joint);
        Write("run_summary.json",result);Event("Finished",result);return result;
    }
    internal static int Run(string[] args)
    {
        string? Value(string key)=>args.FirstOrDefault(a=>a.StartsWith(key,StringComparison.Ordinal))?[key.Length..];
        double Number(string key,double fallback)=>Value(key) is string s?double.Parse(s,CultureInfo.InvariantCulture):fallback;
        var instance=new NormalizedDatasetLoader().Load(Value("--dataset=")??throw new ArgumentException("Dataset required."));
        var result=Solve(instance,new(Value("--from-zero-output=")??throw new ArgumentException("New output required."),
            TotalSeconds:Number("--from-zero-total=",172800),MacroSeconds:Number("--from-zero-macro=",28800),
            ReserveSeconds:Number("--from-zero-reserve=",1800),Seed:checked((int)Number("--from-zero-seed=",0)),
            ColdSolveSeconds:Number("--from-zero-cold-solve=",1800),
            MacroRepairSeconds:Number("--from-zero-macro-repair=",1800),
            MacroIterationSeconds:Number("--from-zero-macro-iteration=",180),
            BoundTotalSeconds:Number("--from-zero-bound-total=",21600),
            UseAlns:args.Contains("--from-zero-alns"),DisableMacroAlns:args.Contains("--from-zero-no-macro-alns"),
            MacroAlns:new(Exploration:Number("--macro-alns-exploration=",.15),Smoothing:Number("--macro-alns-smoothing=",.25),
                UpdatePeriod:checked((int)Number("--macro-alns-period=",10)),WeightFloor:Number("--macro-alns-floor=",.05),
                FirstFeasibleReward:Number("--macro-alns-first-reward=",8),BestReward:Number("--macro-alns-best-reward=",4),
                ImprovementReward:Number("--macro-alns-improvement-reward=",1)),StopAtFirstJoint:args.Contains("--from-zero-first-joint"),
            CompactOutput:args.Contains("--from-zero-compact"),
            RobustMacroInitialization:!args.Contains("--from-zero-legacy-macro"),
            ImproveQuality:args.Contains("--alns-quality-search") || args.Contains("--alns-span-improvements"),
            GuideFeasibilitySpan:args.Contains("--alns-feasibility-span") || args.Contains("--alns-span-improvements")) );
        Console.WriteLine(JsonSerializer.Serialize(result));return result.Status.StartsWith("Error",StringComparison.Ordinal)?2:0;
    }
}
