using Gurobi;
using GurobiTimetablingResearch.Domain;
using GurobiTimetablingResearch.IO;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace GurobiTimetablingResearch.Optimization;

internal sealed record BottleneckExperimentOptions(string OutputDirectory,double TotalSeconds=2100,double SolveSeconds=1800,
    double ReserveSeconds=120,int Radius=4,int Seed=0,bool SafeSparse=true,bool SoftWindows=true,bool FixedOriginOrder=false,
    bool FixStops=false,bool Cycle=false,string? SnapshotPath=null,string? ConflictReportPath=null,bool StopAtFirstCandidate=false,int? MacroCandidateRounds=null);
internal sealed record BottleneckExperimentResult(string Status,bool JointFeasible,bool CompleteMacroFeasible,
    double Seconds,int Rounds,double? FirstCandidateWallSeconds,double? FirstJointWallSeconds);

internal static class BottleneckExperimentRunner
{
    internal static int Run(string[] args)
    {
        string? Value(string p)=>args.FirstOrDefault(a=>a.StartsWith(p,StringComparison.Ordinal))?[p.Length..];
        double Number(string p,double d)=>Value(p) is string v?double.Parse(v,CultureInfo.InvariantCulture):d;
        var options=new BottleneckExperimentOptions(Value("--experiment-output=")??throw new ArgumentException("New output required."),
            Number("--experiment-total=",2100),Number("--experiment-solve=",1800),Number("--experiment-reserve=",120),
            checked((int)Number("--experiment-radius=",4)),checked((int)Number("--experiment-seed=",0)),
            !args.Contains("--experiment-legacy-sparse"),!args.Contains("--experiment-no-soft"),args.Contains("--experiment-fixed-origin"),
            args.Contains("--experiment-fix-stops"),args.Contains("--experiment-cycle"),Value("--experiment-snapshot="),Value("--experiment-conflicts="));
        var result=Solve(new NormalizedDatasetLoader().Load(Value("--dataset=")??throw new ArgumentException("Dataset required.")),options);
        Console.WriteLine(JsonSerializer.Serialize(result)); return result.Status=="Error"?2:0;
    }

    internal static string NoIncumbentStatus(int nativeStatus)=>nativeStatus==GRB.Status.INFEASIBLE?"UnknownRestrictedOrSparseInfeasible":"UnknownNoIncumbent";
    internal static BottleneckExperimentResult Solve(TimetablingInstance instance,BottleneckExperimentOptions options)
    {
        if(!double.IsFinite(options.TotalSeconds) || options.TotalSeconds<=0 || !double.IsFinite(options.SolveSeconds) || options.SolveSeconds<=0 ||
            !double.IsFinite(options.ReserveSeconds) || options.ReserveSeconds<0 || options.ReserveSeconds>=options.TotalSeconds || options.Radius<0 || options.Seed<0 ||
            options.MacroCandidateRounds is <1 || options.Cycle && (!options.FixStops || options.SnapshotPath is null || options.ConflictReportPath is null))
            throw new ArgumentException("Invalid experiment budget or cycle controls.");
        string output=Path.GetFullPath(options.OutputDirectory);
        if(Directory.Exists(output)) throw new IOException("Refusing to overwrite experiment evidence.");
        Directory.CreateDirectory(output);
        var wall=Stopwatch.StartNew(); double Remaining()=>options.TotalSeconds-wall.Elapsed.TotalSeconds;
        double SearchRemaining()=>Remaining()-options.ReserveSeconds;
        void Write(string name,object value)=>Stages45Runner.Write(output,name,value);
        string status="UnknownBudget"; bool joint=false,complete=false; int round=0;
        Dictionary<string,SectionConflictEdgeRequest> edges=[];
        double? firstCandidate=null,firstJoint=null;
        var policy=ServiceWindowPolicy.Create(instance,new(options.SoftWindows));
        Write("configuration.json",new { options,instance.Meta,policy,scope=options.FixStops || options.FixedOriginOrder?"RestrictedSearch":"SparseMacroExperiment",
            binarySha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(BottleneckExperimentRunner).Assembly.Location))),
            globalLowerBound=(double?)null,warning="No timeout, local bound, or sparse-model infeasibility certifies original JH infeasibility. Soft windows are preferences. One seed is a pilot." });
        try
        {
            var cuts=StopTravelBudgetCutBuilder.Build(instance).AllCuts;
            var seed=QuotaStopSkeletonSolver.Solve(instance,options.Seed,Math.Min(20,Math.Max(.01,SearchRemaining())),cuts);
            Write("seed.json",seed);
            if(!seed.IsQuotaFeasible) { status="UnknownQuotaSkeleton";return Finish(); }
            IReadOnlyDictionary<string,int> pattern=seed.Stops;
            if(options.SnapshotPath is not null)
                pattern=MasterSolutionSnapshotStore.Load(options.SnapshotPath,instance.Meta.InstanceName,instance.Trains.Select(t=>t.TrainIndex).ToHashSet()).StopByStopId;
            BottleneckExperimentControls.ValidatePattern(instance,pattern);
            Dictionary<string,int> recurrence=[];
            Dictionary<string,PromotedMicroComponent> repairs=[];
            HashSet<int> conflicts=[];
            if(options.ConflictReportPath is not null)
            {
                var report=JsonSerializer.Deserialize<MasterValidationReport>(File.ReadAllText(options.ConflictReportPath),new JsonSerializerOptions { PropertyNameCaseInsensitive=true })
                    ??throw new InvalidDataException("Missing conflict report.");
                foreach(var e in MacroClosureBatchSelector.Select(instance,report,recurrence,50)) Add(e);
                conflicts.UnionWith(edges.Values.SelectMany(e=>new[]{e.FirstTrainIndex,e.SecondTrainIndex}));
            }
            if(options.Cycle)
            {
                var proposal=QuotaCycleExchange.Propose(instance,pattern,conflicts,cuts); Write("cycle.json",proposal);
                if(!proposal.Applied) { status="UnknownNoAdmissibleCycle"; return Finish(); }
                pattern=proposal.Stops;
            }
            Write("initial_pattern.json",pattern); Write("initial_edges.json",edges.Values.ToArray());
            while(SearchRemaining()>0)
            {
                round++; var build=Stopwatch.StartNew();
                using var model=new BasicMilpModelBuilder(instance,neighborRadius:options.Radius,additionalSectionEdges:edges.Values.ToArray(),
                    serviceWindows:policy,screenTemporalPrecedence:true,preserveOmittedOvertakingSupport:options.SafeSparse,
                    promotedMicroComponents:repairs.Values.ToArray()).Build();
                model.Model.Set(GRB.IntParam.OutputFlag,0);
                foreach(var p in pattern)
                {
                    var v=model.StopVars[p.Key]; v.Start=p.Value;
                    if(options.FixStops)
                    {
                        if(p.Value<v.LB || p.Value>v.UB) throw new InvalidDataException("Stop pattern violates original domain: "+p.Key);
                        v.LB=v.UB=p.Value;
                    }
                }
                int originRows=options.FixedOriginOrder?BottleneckExperimentControls.ApplyOriginOrder(model):0;
                foreach(var cut in cuts) cut.Apply(model.Model,model.StopVars);
                model.Model.Update(); Write($"round_{round:D3}_families.json",Stages45FamilyDiagnostics.Capture(model));
                FixedDiscreteModelReducer.Apply(model.Model,budgetSeconds:Math.Min(3,Math.Max(.001,SearchRemaining())));
                using var compact=new PartialDiscreteModelCompiler(model.Model); build.Stop();
                if(SearchRemaining()<=0) { status="UnknownBudgetDuringBuild"; break; }
                compact.Model.Set(GRB.IntParam.Threads,4); compact.Model.Set(GRB.IntParam.Seed,options.Seed);
                compact.Model.Set(GRB.IntParam.MIPFocus,1); compact.Model.Set(GRB.IntParam.LogToConsole,0);
                compact.Model.Set(GRB.IntParam.OutputFlag,1); compact.Model.Set(GRB.StringParam.LogFile,Path.Combine(output,$"round_{round:D3}.log"));
                double limit=Math.Min(options.SolveSeconds,SearchRemaining()); compact.Model.Set(GRB.DoubleParam.TimeLimit,limit);
                var callback=new BottleneckProgressCallback(Path.Combine(output,$"round_{round:D3}_live.json")); compact.Model.SetCallback(callback);
                var solve=Stopwatch.StartNew(); compact.Model.Optimize(); solve.Stop();
                Write($"round_{round:D3}_solve.json",new { round,originRows,buildSeconds=build.Elapsed.TotalSeconds,solveSeconds=solve.Elapsed.TotalSeconds,
                    continuousSolveLimit=limit,nativeStatus=compact.Model.Status,solutions=compact.Model.SolCount,nodes=compact.Model.NodeCount,
                    work=compact.Model.Get(GRB.DoubleAttr.Work),callback.FirstIncumbentSeconds,callback.Error,compact.Audit,
                    relations=model.SectionConflictCandidates.Count,wallSeconds=wall.Elapsed.TotalSeconds,scope="ExperimentalModelOnly" });
                if(callback.Error is not null) throw new InvalidOperationException(callback.Error);
                if(compact.Model.SolCount==0) { status=NoIncumbentStatus(compact.Model.Status); break; }
                firstCandidate??=wall.Elapsed.TotalSeconds;
                if(Remaining()<=0) { status="UnknownBeforeCarrier"; break; }
                compact.FixSourceToIncumbent(model.Model); model.Model.Set(GRB.DoubleParam.TimeLimit,Math.Min(10,Remaining()));
                model.Model.Set(GRB.IntParam.SolutionLimit,1); model.Optimize();
                if(!model.HasIncumbentSolution) { status="UnknownCandidateCarrier"; break; }
                var candidate=MasterSolutionSnapshot.Capture(model);
                MasterSolutionSnapshotStore.Save(Path.Combine(output,$"candidate_{round:D3}.snapshot.json"),instance.Meta.InstanceName,candidate);
                var validation=new MasterSolutionValidator().Validate(model); Write($"round_{round:D3}_validation.json",validation);
                if(options.StopAtFirstCandidate) { status="FreshMacroCandidate";break; }
                if(options.MacroCandidateRounds is int candidateRounds && (validation.IsFeasible || round>=candidateRounds))
                { status="ImprovedMacroCandidate";break; }
                int added=0;
                if(!validation.IsFeasible)
                {
                    foreach(var edge in MacroClosureBatchSelector.Select(instance,validation,recurrence,50)) if(Add(edge)) added++;
                    // A long dwell enabled by an unknown witness can be the only violated rule.
                    // Expand actual pair support rather than globally forbidding the candidate pattern.
                    if(added==0) added=BottleneckClosurePlanner.Expand(instance,model.SectionConflictCandidates,
                        model.BeingOvertakenVars.Where(p=>p.Value.X>.5).Select(p=>p.Key).ToHashSet(),edges,50);
                    status=added>0?"ContinueClosure":"UnknownUnresolvedMacroViolation";
                }
                else
                {
                    var proof=Stages45JointVerifier.Verify(instance,candidate,policy,Remaining,options.SolveSeconds,output,round,
                        Math.Max(0,3-repairs.Count));
                    complete|=proof.CompleteMacro; joint=proof.JointFeasible; status=proof.Status;
                    foreach(var repair in proof.MicroRepairs) repairs.TryAdd(repair.ComponentId,repair);
                    if(joint && proof.Canonical is not null)
                    {
                        firstJoint=wall.Elapsed.TotalSeconds;
                        MasterSolutionSnapshotStore.Save(Path.Combine(output,"joint_feasible.snapshot.json"),instance.Meta.InstanceName,proof.Canonical);
                        Write("joint_upper_bound.json",new { objective=ServiceBusinessObjectiveEvaluator.Calculate(instance,proof.Canonical,policy),
                            fingerprint=SnapshotFingerprint.Calculate(proof.Canonical),globalLowerBound=(double?)null,optimalityProven=false });
                    }
                    if(!joint && proof.MicroRepairs.Count==0) break;
                }
                Write("progress.json",new { round,status,added,complete,joint,wallSeconds=wall.Elapsed.TotalSeconds });
                Console.WriteLine($"experiment round={round} status={status} violations={validation.Violations.Count} added={added} wall={wall.Elapsed.TotalSeconds:F1}");
                if(joint || added==0 && !validation.IsFeasible) break;
                if(!options.FixStops) pattern=candidate.StopByStopId; // Partial stop start only; never reload a rejected full timing/order start.
            }
            bool Add(SectionConflictEdgeRequest edge)=>edges.TryAdd(InitialConflictGraphBuilder.BuildCanonicalPairKey(edge.SectionName,edge.FirstTrainIndex,edge.SecondTrainIndex),edge);
        }
        catch(FixedDiscreteDomainContradictionException ex) { status="UnknownExperimentalDomainContradiction"; Write("domain_contradiction.json",new { ex.Message }); }
        catch(Exception ex) { status="Error"; Write("error.json",new { exception=ex.ToString() }); }
        return Finish();
        BottleneckExperimentResult Finish()
        {
            // Preserve discoveries even if the last expanded model is contradictory
            // or has no incumbent; they are independent of best-candidate selection.
            if(options.MacroCandidateRounds.HasValue)
                Write("macro_search_state.json",new DirectedMacroSearchState(edges.Values.ToArray(),[]));
            if(status.StartsWith("Continue",StringComparison.Ordinal)) status="UnknownBudget";
            var result=new BottleneckExperimentResult(status,joint,complete,wall.Elapsed.TotalSeconds,round,firstCandidate,firstJoint);
            Write("summary.json",result); return result;
        }
    }
}

internal sealed class BottleneckProgressCallback(string path):GRBCallback
{
    private double last=-30;
    public double? FirstIncumbentSeconds { get; private set; }
    public string? Error { get; private set; }
    protected override void Callback()
    {
        try
        {
            if(where==GRB.Callback.MIPSOL)
            {
                FirstIncumbentSeconds??=GetDoubleInfo(GRB.Callback.RUNTIME); Abort();
            }
            if(where!=GRB.Callback.MIP) return;
            double seconds=GetDoubleInfo(GRB.Callback.RUNTIME);
            if(seconds-last<30) return; last=seconds;
            double? Finite(double x)=>double.IsFinite(x) && Math.Abs(x)<GRB.INFINITY/2?x:null;
            File.WriteAllText(path,JsonSerializer.Serialize(new { seconds,nodes=GetDoubleInfo(GRB.Callback.MIP_NODCNT),
                solutionCount=GetIntInfo(GRB.Callback.MIP_SOLCNT),experimentalObjective=Finite(GetDoubleInfo(GRB.Callback.MIP_OBJBST)),
                experimentalBound=Finite(GetDoubleInfo(GRB.Callback.MIP_OBJBND)),scope="CurrentExperimentalObjectiveOnly_NotGlobalJointBound" }));
        }
        catch(Exception ex) { Error=ex.ToString(); Abort(); }
    }
}
