using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Gurobi;
using GurobiTimetablingResearch.Domain;
using GurobiTimetablingResearch.IO;

namespace GurobiTimetablingResearch.Optimization;

public sealed record Stages45Options(string OutputDirectory,double TotalSeconds=900,double IterationSeconds=45,
    double ValidationReserveSeconds=120,int Radius=4,int SeedCount=3,bool EnableStage4=true,bool EnableStage5=true,
    bool StopAtFirstJoint=true,int BatchSize=50,int MaxPromotedStations=3,int MaxPromotedAlternatives=800,int MaxPromotedConditionalRows=200000);
public sealed record Stages45Round(int Round,int SeedIndex,string Phase,int TrainCount,int ActiveTrains,int WidthSeconds,
    int Relations,int AddedEdges,double BuildSeconds,double SolveSeconds,double? FirstIncumbentSeconds,int? MacroViolations,
    string Status,PartialDiscreteCompileAudit? Compact,double JointCheckSeconds=0);
public sealed record Stages45RunResult(bool CompleteMacroFeasible,bool JointFeasible,string Status,string? Fingerprint,
    double TotalSeconds,ServiceObjectiveVector? JointUpperBound,IReadOnlyList<Stages45Round> Rounds);

/// <summary>All-train, opt-in, quota-aware RestrictedSearch. No local bound is a global bound.</summary>
public static class Stages45Runner
{
    private static readonly JsonSerializerOptions Json=new() { WriteIndented=true,PropertyNamingPolicy=JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive=true,Converters={new JsonStringEnumConverter()} };

    public static int Run(string[] args)
    {
        string? Value(string p)=>args.FirstOrDefault(a=>a.StartsWith(p,StringComparison.Ordinal))?[p.Length..];
        double Number(string p,double d)=>Value(p) is string v?double.Parse(v,CultureInfo.InvariantCulture):d;
        string data=Value("--dataset=")??throw new ArgumentException("Explicit --dataset= required.");
        string output=Value("--stages-output=")??throw new ArgumentException("New --stages-output= required.");
        var options=new Stages45Options(output,Number("--stages-total-seconds=",900),Number("--stages-iteration-seconds=",45),
            Number("--stages-validation-reserve=",120),checked((int)Number("--stages-radius=",4)),checked((int)Number("--stages-seeds=",3)),
            !args.Contains("--stages-disable-4"),!args.Contains("--stages-disable-5"),!args.Contains("--stages-improve"));
        var result=Solve(new NormalizedDatasetLoader().Load(data),options);
        Console.WriteLine($"Stages 4-5: {result.Status}; full_macro={result.CompleteMacroFeasible}; joint={result.JointFeasible}; seconds={result.TotalSeconds:F3}");
        return result.Status=="Error"?2:0;
    }

    /// <summary>Run joint repair and retain the best strictly verified timetable by business objective.</summary>
    /// <param name="instance">Original timetable data and resource rules.</param>
    /// <param name="options">Repair budget in seconds, search parameters and result directory.</param>
    /// <returns>Run status and the retained joint solution's objective, when available.</returns>
    public static Stages45RunResult Solve(TimetablingInstance instance,Stages45Options options)
    {
        if(!double.IsFinite(options.TotalSeconds) || options.TotalSeconds<=0 || !double.IsFinite(options.IterationSeconds) || options.IterationSeconds<=0 ||
            !double.IsFinite(options.ValidationReserveSeconds) || options.ValidationReserveSeconds<0 || options.ValidationReserveSeconds>=options.TotalSeconds ||
            options.Radius<0 || options.SeedCount<1 || options.BatchSize<1 || options.MaxPromotedStations<0 ||
            options.MaxPromotedAlternatives<1 || options.MaxPromotedConditionalRows<1) throw new ArgumentOutOfRangeException(nameof(options));
        string output=Path.GetFullPath(options.OutputDirectory);if(Directory.Exists(output)) throw new IOException("Refusing to overwrite experiment directory.");
        Directory.CreateDirectory(output);Stopwatch wall=Stopwatch.StartNew();double Remaining()=>options.TotalSeconds-wall.Elapsed.TotalSeconds;
        double SearchRemaining()=>Remaining()-options.ValidationReserveSeconds;
        var policy=ServiceWindowPolicy.Create(instance,new ServiceWindowOptions(true));
        Write(output,"configuration.json",new { options,instance.Meta,policy,policy.Identity,scope="RestrictedSearch",globalLowerBound=(double?)null,
            warning="Reference order is only a retractable search fixation. Skeletons and provisional candidates are not joint upper bounds." });
        List<Stages45Round> rounds=[];List<SeedSkeletonResult> seeds=[];
        Dictionary<string,SectionConflictEdgeRequest> edges=[];Dictionary<string,int> recurrence=[];
        MasterSolutionSnapshot? working=null,best=null;ServiceObjectiveVector? upper=null;bool completeMacro=false;string status="UnknownBudget";
        HashSet<int> conflictTrains=[];int failures=0,windowIndex=0;bool bootstrap=true;
        Dictionary<string,PromotedMicroComponent> microRepairs=[];bool allowCandidateStart=true;
        try
        {
            var travel=StopTravelBudgetCutBuilder.Build(instance);
            var capacity=StationResourceCapacityCutBuilder.Build(instance,TemporalBoundScope.OriginalHard);
            Write(output,"travel_budget_audit.json",travel);Write(output,"capacity_audit.json",capacity);
            var strengthening=options.EnableStage4?travel.AllCuts.Concat(capacity.Cuts).ToArray():Array.Empty<StopLinearCut>();
            for(int k=0;k<options.SeedCount && SearchRemaining()>0;k++)
            {
                SeedSkeletonResult seed;
                if(options.EnableStage4) seed=QuotaStopSkeletonSolver.Solve(instance,k,Math.Min(20,Math.Max(.01,SearchRemaining())),strengthening);
                else
                {
                    var domains=StructuralStopDomains.Build(instance);
                    var pattern=instance.TrainStops.ToDictionary(s=>s.StopId,s=>domains[s.StopId].Lower==domains[s.StopId].Upper?domains[s.StopId].Lower:s.IdealStop?1:0);
                    seed=new(false,"UncertifiedReferenceStopPattern","ReferenceOnly",pattern,"",0,["No quota feasibility claim; used only in restricted bootstrap / MIP starts."]);
                }
                Write(output,$"seed_{k:D2}.json",seed);
                if(seed.IsQuotaFeasible || !options.EnableStage4) seeds.Add(seed);
            }
            if(seeds.Count==0) status="UnknownNoStopSeed";
            int seedIndex=0;
            for(int round=1;seeds.Count>0 && SearchRemaining()>0;round++)
            {
                var seed=seeds[seedIndex%seeds.Count];var pattern=working?.StopByStopId??seed.Stops;
                int width=WindowNeighborhoodPlanner.WidthAfterFailures(failures);
                double originFirst=instance.Trains.Min(t=>(double)Math.Max(0,t.OrderedStops[0].RawDepTimeSeconds??t.OrderedStops[0].EarliestDepSeconds));
                double originLast=instance.Trains.Max(t=>(double)Math.Max(0,t.OrderedStops[0].RawDepTimeSeconds??t.OrderedStops[0].EarliestDepSeconds));
                int windowCount=Math.Max(1,(int)Math.Ceiling((originLast-originFirst)/1800)+1);
                var neighborhood=WindowNeighborhoodPlanner.Select(instance,working,pattern,originFirst+(windowIndex%windowCount)*1800,width,conflictTrains);
                HashSet<int> active=bootstrap?[]:neighborhood.ActiveTrains.ToHashSet();
                bool restricted=options.EnableStage5;
                string phase=!restricted?"UnrestrictedSeedStart":bootstrap?working is null?"BootstrapFixedSkeletonOrder":"ClosureFixedCandidate":"OverlappingWindowLNS";
                Stopwatch build=Stopwatch.StartNew();double solveSeconds=0,jointSeconds=0;int added=0;int? violations=null;double? firstIncumbent=null;
                PartialDiscreteCompileAudit? compileAudit=null;int relations=0;
                try
                {
                    using BasicMilpModel model=Stages45LocalModelBuilder.Build(instance,policy,pattern,working,active,edges.Values.ToArray(),options.Radius,restricted,
                        strengthening,microRepairs.Values.ToArray(),allowCandidateStart);
                    relations=model.SectionConflictCandidates.Count;
                    Write(output,$"round_{round:D3}_families.json",Stages45FamilyDiagnostics.Capture(model));
                    Write(output,$"round_{round:D3}_neighborhood.json",new { phase,active=active.Order(),neighborhood,globalQuotasRetained=true,temporaryReferenceOrder=bootstrap&&restricted });
                    FixedDiscreteModelReducer.Apply(model.Model,budgetSeconds:Math.Min(3,Math.Max(.001,SearchRemaining())));
                    using PartialDiscreteModelCompiler compact=new(model.Model);compileAudit=compact.Audit;build.Stop();
                    if(SearchRemaining()<=0) { status="UnknownBudgetDuringBuild";break; }
                    compact.Model.Set(GRB.DoubleParam.TimeLimit,Math.Min(options.IterationSeconds,Math.Max(.001,SearchRemaining())));
                    compact.Model.Set(GRB.IntParam.Threads,4);compact.Model.Set(GRB.IntParam.Seed,seedIndex);compact.Model.Set(GRB.IntParam.MIPFocus,1);
                    compact.Model.Set(GRB.IntParam.OutputFlag,1);compact.Model.Set(GRB.IntParam.LogToConsole,0);
                    compact.Model.Set(GRB.StringParam.LogFile,Path.Combine(output,$"round_{round:D3}.log"));
                    var callback=Stages45SolvePolicy.ConfigureCallback(compact.Model,options.StopAtFirstJoint,best is not null);
                    var solve=Stopwatch.StartNew();compact.Model.Optimize();solveSeconds=solve.Elapsed.TotalSeconds;firstIncumbent=callback.FirstIncumbentTimeSeconds;
                    if(compact.Model.SolCount==0)
                    {
                        status=compact.Model.Status==GRB.Status.INFEASIBLE?"UnknownRestrictedOrSparseInfeasible":"UnknownNoIncumbent";
                        if(compact.Model.Status==GRB.Status.INFEASIBLE)
                        {
                            var dependencies=RestrictedInfeasibilityAnalyzer.FindTrainDependencies(compact.Model,Math.Min(2,Math.Max(0,SearchRemaining())));
                            conflictTrains.UnionWith(dependencies.Where(t=>instance.Trains.Any(train=>train.TrainIndex==t)));
                            Write(output,$"round_{round:D3}_restricted_iis_release.json",new { dependencies,scope="RestrictedSearch",globalCut=false });
                        }
                        failures++;bootstrap=false;windowIndex++;if(failures%2==0 && working is null && conflictTrains.Count==0) { seedIndex++;bootstrap=true; }
                        // Sparse infeasibility may mean missing overtaking-dwell support. Add all shared sections of nearby train pairs.
                        if(compact.Model.Status==GRB.Status.INFEASIBLE) added=ExpandActivePairs(instance,active.Count==0?neighborhood.ActiveTrains:active,edges,options.BatchSize);
                    }
                    else if(Remaining()>0)
                    {
                        compact.FixSourceToIncumbent(model.Model);model.Model.Set(GRB.DoubleParam.TimeLimit,Math.Min(5,Math.Max(.001,Remaining())));
                        model.Model.Set(GRB.IntParam.SolutionLimit,1);model.Optimize();
                        if(!model.HasIncumbentSolution) { status="UnknownCandidateCarrier";failures++;bootstrap=false; }
                        else
                        {
                            working=MasterSolutionSnapshot.Capture(model);
                            MasterSolutionSnapshotStore.Save(Path.Combine(output,"working.snapshot.json"),instance.Meta.InstanceName,working);
                            var validation=new MasterSolutionValidator().Validate(model);violations=validation.Violations.Count;
                            Write(output,$"round_{round:D3}_validation.json",validation);
                            if(!validation.IsFeasible)
                            {
                                conflictTrains.Clear();
                                foreach(var edge in MacroClosureBatchSelector.Select(instance,validation,recurrence,options.BatchSize))
                                    if(edges.TryAdd(InitialConflictGraphBuilder.BuildCanonicalPairKey(edge.SectionName,edge.FirstTrainIndex,edge.SecondTrainIndex),edge))
                                    { added++;conflictTrains.Add(edge.FirstTrainIndex);conflictTrains.Add(edge.SecondTrainIndex); }
                                status=added>0?"ContinueMacroClosure":"UnknownUnresolvedMacroViolation";
                                // Try cheap continuous completion before releasing every conflicting train's discrete decisions.
                                // If it fails, IIS dependencies and window closure release the relevant fixations next round.
                                bootstrap=added>0;
                            }
                            else
                            {
                                var proof=Stages45JointVerifier.Verify(instance,working,policy,Remaining,options.IterationSeconds,output,round,
                                    Math.Max(0,options.MaxPromotedStations-microRepairs.Count),options.MaxPromotedAlternatives,options.MaxPromotedConditionalRows);jointSeconds=proof.Seconds;
                                completeMacro|=proof.CompleteMacro;status=proof.Status;conflictTrains=proof.ConflictTrains.ToHashSet();
                                foreach(var component in proof.MicroRepairs) if(microRepairs.Count<options.MaxPromotedStations)
                                    microRepairs.TryAdd(component.ComponentId,component);
                                Write(output,"restricted_micro_repairs.json",microRepairs.Values.ToArray());
                                // Unknown never yields a prohibition. Omit the rejected full start on retry to avoid immediate rediscovery.
                                allowCandidateStart=proof.JointFeasible;
                                if(proof.Canonical is not null) working=proof.Canonical;
                                if(proof.JointFeasible && proof.Canonical is not null)
                                {
                                    var vector=ServiceBusinessObjectiveEvaluator.Calculate(instance,proof.Canonical,policy);
                                    if(upper is null || ResourceSpanObjectivePolicy.Better(vector,upper))
                                    {
                                        best=proof.Canonical;upper=vector;
                                        MasterSolutionSnapshotStore.Save(Path.Combine(output,"joint_feasible.snapshot.json"),instance.Meta.InstanceName,best);
                                        Write(output,"business_upper_bound.json",new { vector,objectiveSelectionKey=ResourceSpanObjectivePolicy.SummaryKey(vector),policy.Identity,fingerprint=SnapshotFingerprint.Calculate(best),
                                            completeMacro=true,jointFeasible=true,readback=proof.Readback,globalLowerBound=(double?)null,optimalityProven=false });
                                    }
                                    failures=0;
                                }
                                else failures++;
                                bootstrap=false;windowIndex++;
                            }
                        }
                    }
                }
                catch(FixedDiscreteDomainContradictionException ex)
                {
                    build.Stop();status="UnknownRestrictedDomainContradiction";failures++;bootstrap=false;windowIndex++;
                    Write(output,$"round_{round:D3}_contradiction.json",new { status,scope="RestrictedSearch",ex.Message });
                    added=ExpandActivePairs(instance,neighborhood.ActiveTrains,edges,options.BatchSize);
                    if(failures%2==0 && working is null) { seedIndex++;bootstrap=true; }
                }
                rounds.Add(new(round,seedIndex%seeds.Count,phase,instance.Trains.Count,active.Count,width,relations,added,build.Elapsed.TotalSeconds,
                    solveSeconds,firstIncumbent,violations,status,compileAudit,jointSeconds));
                Write(output,"progress.json",rounds);
                Console.WriteLine($"round={round} phase={phase} active={active.Count}/{instance.Trains.Count} free_integer={compileAudit?.CompactFreeIntegers} status={status} wall={wall.Elapsed.TotalSeconds:F1}");
                if(best is not null && options.StopAtFirstJoint) break;
            }
        }
        catch(Exception ex) { status="Error";Write(output,"error.json",new { exception=ex.ToString() }); }
        if(best is not null && status!="Error") status="JointFeasible";
        else if(status.StartsWith("Continue",StringComparison.Ordinal)) status="UnknownBudget";
        var result=new Stages45RunResult(completeMacro,best is not null,status,best is null?null:SnapshotFingerprint.Calculate(best),wall.Elapsed.TotalSeconds,upper,rounds);
        Write(output,"summary.json",result);return result;
    }

    internal static int ExpandActivePairs(TimetablingInstance instance,IReadOnlySet<int> active,Dictionary<string,SectionConflictEdgeRequest> edges,int maxPairs)
    {
        int added=0;var candidates=new InitialConflictGraphBuilder(instance,int.MaxValue).Build();
        var pairs=candidates.Where(c=>active.Contains(c.FirstTrainIndex) && active.Contains(c.SecondTrainIndex))
            .GroupBy(c=>(c.FirstTrainIndex,c.SecondTrainIndex))
            .Where(g=>g.Any(c=>!edges.ContainsKey(InitialConflictGraphBuilder.BuildCanonicalPairKey(c.SectionName,c.FirstTrainIndex,c.SecondTrainIndex))))
            .Take(maxPairs);
        foreach(var pair in pairs) foreach(var c in pair)
            if(edges.TryAdd(InitialConflictGraphBuilder.BuildCanonicalPairKey(c.SectionName,c.FirstTrainIndex,c.SecondTrainIndex),
                new(c.SectionName,c.FirstTrainIndex,c.SecondTrainIndex))) added++;
        return added;
    }
    internal static void Write<T>(string directory,string name,T value)=>File.WriteAllText(Path.Combine(directory,name),JsonSerializer.Serialize(value,Json));
}
