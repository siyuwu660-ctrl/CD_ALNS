using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Gurobi;
using GurobiTimetablingResearch.Domain;
using GurobiTimetablingResearch.IO;

namespace GurobiTimetablingResearch.Optimization;

public sealed record Stages03Options(string OutputDirectory,double TotalSeconds=180,double IterationSeconds=30,
    int Radius=4,int BatchSize=50,bool BuildOnly=false,bool CompleteRelations=false,bool ServiceWindows=true,
    IReadOnlyList<ServiceWindow>? ExplicitWindows=null);
public sealed record Stages03Round(int Round,int Relations,int AddedEdges,double BuildSeconds,double ReduceSeconds,
    double SolveSeconds,double FixedCarrierSeconds,double CheckSeconds,double? FirstIncumbentSeconds,
    int? Violations,string Status,object Size,PartialDiscreteCompileAudit? Compact,double CompleteProofSeconds=0);
public sealed record Stages03RunResult(bool CompleteMacroFeasible,bool JointFeasible,string Status,string? Fingerprint,
    double TotalSeconds,ServiceObjectiveVector? BusinessObjective,IReadOnlyList<Stages03Round> Rounds);

/// <summary>Bounded opt-in experiment. Does not invoke old six-hour profiles or claim Macro-Micro joint feasibility.</summary>
public static class Stages03Runner
{
    private static readonly JsonSerializerOptions Json=new() { WriteIndented=true,PropertyNamingPolicy=JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive=true,Converters={new JsonStringEnumConverter()} };

    public static int Run(string[] args)
    {
        string? Value(string prefix)=>args.FirstOrDefault(a=>a.StartsWith(prefix,StringComparison.Ordinal))?[prefix.Length..];
        bool Flag(string value)=>args.Contains(value,StringComparer.Ordinal);
        double Number(string prefix,double fallback)=>Value(prefix) is string text?double.Parse(text,CultureInfo.InvariantCulture):fallback;
        string dataset=Value("--dataset=") ?? throw new ArgumentException("Stages 0-3 requires explicit --dataset=path.");
        string output=Value("--stages-output=") ?? throw new ArgumentException("Stages 0-3 requires a new --stages-output=path.");
        TimetablingInstance instance=new NormalizedDatasetLoader().Load(dataset);
        if(Value("--stages-verify-saved-joint=") is string saved)
        {
            if(Directory.Exists(output)) throw new IOException("Verification output must be a new directory.");
            var verification=SavedJointSnapshotVerifier.Run(instance,saved,output,0.05,StationEndpointOccupationPolicy.DefaultSeconds);
            Console.WriteLine(JsonSerializer.Serialize(verification,Json));
            return verification.Passed?0:2;
        }
        IReadOnlyList<ServiceWindow>? windows=Value("--stages-service-policy=") is string policyPath
            ? JsonSerializer.Deserialize<List<ServiceWindow>>(File.ReadAllText(policyPath),Json) : null;
        Stages03Options options=new(output,Number("--stages-total-seconds=",180),Number("--stages-iteration-seconds=",30),
            checked((int)Number("--stages-radius=",4)),checked((int)Number("--stages-batch-size=",50)),
            Flag("--stages-build-only"),Flag("--stages-complete-relations"),!Flag("--stages-disable-service-windows"),windows);
        var result=Solve(instance,options);
        Console.WriteLine($"Stages 0-3: status={result.Status}, full_macro={result.CompleteMacroFeasible}, joint_UB=False, wall_seconds={result.TotalSeconds:F3}");
        return result.Status=="Error"?2:0;
    }

    public static Stages03RunResult Solve(TimetablingInstance instance,Stages03Options options)
    {
        if(!double.IsFinite(options.TotalSeconds) || options.TotalSeconds<=0 || !double.IsFinite(options.IterationSeconds) ||
            options.IterationSeconds<=0 || options.Radius<0 || options.BatchSize<=0) throw new ArgumentOutOfRangeException(nameof(options));
        string output=Path.GetFullPath(options.OutputDirectory);
        if(Directory.Exists(output)) throw new IOException($"Refusing to overwrite experiment directory: {output}");
        Directory.CreateDirectory(output);
        Stopwatch wall=Stopwatch.StartNew();
        ServiceWindowPolicy policy=ServiceWindowPolicy.Create(instance,new ServiceWindowOptions(options.ServiceWindows),options.ExplicitWindows);
        Write(output,"configuration.json",new { options,policy,policy.Identity,instance.Meta,
            scope="OriginalHard",softPreferencesNeverPrune=true,globalUpperBound=(double?)null });
        Write(output,"static_audit.json",StaticAudit(instance));
        List<Stages03Round> rounds=[];
        Dictionary<string,SectionConflictEdgeRequest> added=new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string,int> recurrence=new(StringComparer.OrdinalIgnoreCase);
        MasterSolutionSnapshot? seed=null;
        ServiceObjectiveVector? objective=null;
        string? fingerprint=null;
        string status="UnknownBudget";
        bool accepted=false;
        try
        {
            for(int round=1;wall.Elapsed.TotalSeconds<options.TotalSeconds;round++)
            {
                Stopwatch build=Stopwatch.StartNew();
                using BasicMilpModel model=new BasicMilpModelBuilder(instance,
                    neighborRadius:options.CompleteRelations?int.MaxValue:options.Radius,
                    additionalSectionEdges:added.Values.ToArray(),serviceWindows:policy,screenTemporalPrecedence:true).Build();
                model.Model.Set(GRB.IntParam.OutputFlag,0);
                model.Model.Update();
                build.Stop();
                object size=Size(model);
                Console.WriteLine($"round={round} build_seconds={build.Elapsed.TotalSeconds:F3} relations={model.SectionConflictCandidates.Count} vars={model.Model.NumVars} rows={model.Model.NumConstrs}");
                Write(output,$"round_{round:D3}_size.json",size);
                Write(output,$"round_{round:D3}_temporal.json",new {
                    scope="OriginalHard",count=model.TemporalPrecedenceAudit.Count,
                    classes=model.TemporalPrecedenceAudit.GroupBy(a=>a.Decision.Classification).ToDictionary(g=>g.Key,g=>g.Count()),
                    fixedRelations=model.TemporalPrecedenceAudit.Where(a=>a.Decision.FixedValue.HasValue) });
                if(options.BuildOnly)
                {
                    rounds.Add(new(round,model.SectionConflictCandidates.Count,0,build.Elapsed.TotalSeconds,0,0,0,0,null,null,"BuildOnly",size,null));
                    status="BuildOnly";break;
                }
                if(wall.Elapsed.TotalSeconds>=options.TotalSeconds) { status="UnknownBudgetDuringBuild";break; }
                double remaining=options.TotalSeconds-wall.Elapsed.TotalSeconds;
                FixedDiscreteReductionAudit reduction;
                Stopwatch reductionClock=Stopwatch.StartNew();
                try { reduction=FixedDiscreteModelReducer.Apply(model.Model,budgetSeconds:Math.Min(5,remaining)); }
                catch(FixedDiscreteDomainContradictionException ex)
                {
                    status=options.CompleteRelations?"ProvenMacroInfeasible":"UnknownSparseInfeasible";
                    Write(output,$"round_{round:D3}_domain_contradiction.json",new { status,scope=options.CompleteRelations?"CompleteMacro":"SparseOnly",ex.Message });
                    rounds.Add(new(round,model.SectionConflictCandidates.Count,0,build.Elapsed.TotalSeconds,reductionClock.Elapsed.TotalSeconds,
                        0,0,0,null,null,status,size,null));
                    break;
                }
                Write(output,$"round_{round:D3}_reduction.json",reduction);
                if(seed is not null) MasterSolutionSnapshotApplier.ApplyExpandableMacroMipStart(model,seed);
                using PartialDiscreteModelCompiler compact=new(model.Model);
                Write(output,$"round_{round:D3}_compact.json",compact.Audit);
                remaining=options.TotalSeconds-wall.Elapsed.TotalSeconds;
                if(remaining<=1) { status="UnknownBudgetBeforeSolve";break; }
                compact.Model.Set(GRB.DoubleParam.TimeLimit,Math.Min(options.IterationSeconds,Math.Max(0.01,remaining-1)));
                compact.Model.Set(GRB.IntParam.MIPFocus,1);
                compact.Model.Set(GRB.IntParam.Seed,0);
                compact.Model.Set(GRB.IntParam.Threads,4);
                compact.Model.Set(GRB.IntParam.SolutionLimit,1);
                compact.Model.Set(GRB.IntParam.OutputFlag,1);
                compact.Model.Set(GRB.IntParam.LogToConsole,0);
                compact.Model.Set(GRB.StringParam.LogFile,Path.Combine(output,$"round_{round:D3}.log"));
                FirstIncumbentTrackingCallback tracker=new(stopAfterFirstIncumbent:true);
                compact.Model.SetCallback(tracker);
                Stopwatch solve=Stopwatch.StartNew();compact.Model.Optimize();solve.Stop();
                double carrierSeconds=0,checkSeconds=0,completeProofSeconds=0;
                MasterValidationReport? validation=null;
                int addedCount=0;
                if(compact.Model.SolCount>0)
                {
                    if(wall.Elapsed.TotalSeconds>=options.TotalSeconds) { status="UnknownBudgetBeforeCarrier"; }
                    else
                    {
                        compact.FixSourceToIncumbent(model.Model);
                        model.Model.Set(GRB.DoubleParam.TimeLimit,Math.Min(5,Math.Max(0.01,options.TotalSeconds-wall.Elapsed.TotalSeconds)));
                        Stopwatch carrier=Stopwatch.StartNew();model.Optimize();carrier.Stop();carrierSeconds=carrier.Elapsed.TotalSeconds;
                        if(model.HasIncumbentSolution)
                        {
                            Stopwatch check=Stopwatch.StartNew();validation=new MasterSolutionValidator().Validate(model);check.Stop();checkSeconds=check.Elapsed.TotalSeconds;
                            Write(output,$"round_{round:D3}_validation.json",validation);
                            seed=MasterSolutionSnapshot.Capture(model);
                            objective=ServiceBusinessObjectiveEvaluator.Calculate(instance,seed,policy);
                            if(validation.IsFeasible && validation.Violations.Count==0)
                            {
                                FullMacroCandidateVerification proof=FullMacroCandidateVerifier.Verify(instance,seed,policy,
                                    ()=>options.TotalSeconds-wall.Elapsed.TotalSeconds,options.IterationSeconds,
                                    Path.Combine(output,$"round_{round:D3}_complete_carrier.log"));
                                completeProofSeconds=proof.Seconds;
                                Write(output,$"round_{round:D3}_complete_carrier.json",proof with { Snapshot=null });
                                if(proof.Snapshot is null) status=proof.Status;
                                else
                                {
                                seed=proof.Snapshot;
                                objective=ServiceBusinessObjectiveEvaluator.Calculate(instance,seed,policy);
                                string path=Path.Combine(output,"complete_macro.snapshot.json");
                                MasterSolutionSnapshotStore.Save(path,instance.Meta.InstanceName,seed);
                                MasterSolutionSnapshot loaded=MasterSolutionSnapshotStore.Load(path,instance.Meta.InstanceName,instance.Trains.Select(t=>t.TrainIndex).ToHashSet());
                                fingerprint=SnapshotFingerprint.Calculate(seed);
                                if(fingerprint!=SnapshotFingerprint.Calculate(loaded)) throw new InvalidDataException("Serialized candidate changed.");
                                ServiceObjectiveVector readback=ServiceBusinessObjectiveEvaluator.Calculate(instance,loaded,policy);
                                if(readback!=objective) throw new InvalidDataException("Serialized business objective changed.");
                                Write(output,"business_objective.json",new { vector=readback,policy,repairSurrogate=(double?)null,fullMacro=true,
                                    jointFeasible=false,globalUpperBound=(double?)null,fingerprint,snapshotSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) });
                                accepted=true;status="CompleteMacroFeasible";
                                }
                            }
                            else
                            {
                                var batch=MacroClosureBatchSelector.Select(instance,validation,recurrence,options.BatchSize);
                                foreach(var edge in batch)
                                    if(added.TryAdd(InitialConflictGraphBuilder.BuildCanonicalPairKey(edge.SectionName,edge.FirstTrainIndex,edge.SecondTrainIndex),edge)) addedCount++;
                                status=addedCount>0?"ContinueClosure":"UnknownUnresolvedBusinessViolation";
                            }
                        }
                        else status="UnknownFixedCarrier";
                    }
                }
                else status=compact.Model.Status==GRB.Status.INFEASIBLE
                    ? options.CompleteRelations?"ProvenMacroInfeasible":"UnknownSparseInfeasible"
                    : "UnknownNoIncumbent";
                rounds.Add(new(round,model.SectionConflictCandidates.Count,addedCount,build.Elapsed.TotalSeconds,reduction.Seconds,
                    solve.Elapsed.TotalSeconds,carrierSeconds,checkSeconds,tracker.FirstIncumbentTimeSeconds,
                    validation?.Violations.Count,status,size,compact.Audit,completeProofSeconds));
                Write(output,"progress.json",rounds);
                if(accepted || status!="ContinueClosure") break;
            }
        }
        catch(Exception ex) { status="Error";Write(output,"error.json",new { exception=ex.ToString() }); }
        if(status=="ContinueClosure") status="UnknownBudget";
        Stages03RunResult result=new(accepted,false,status,fingerprint,wall.Elapsed.TotalSeconds,objective,rounds);
        Write(output,"summary.json",result);
        return result;
    }

    private static object Size(BasicMilpModel model)
    {
        var primary=model.StopVars.Values.Concat(model.SectionPrecedenceVars.Values).Concat(model.StationEventPrecedenceVars.Values).Select(v=>v.Index).ToHashSet();
        var all=model.Model.GetVars();
        return new { variables=all.Length,rows=model.Model.NumConstrs,binary=all.Count(v=>v.VType==GRB.BINARY),
            primaryFreeBinary=all.Count(v=>primary.Contains(v.Index) && v.VType==GRB.BINARY && v.LB<v.UB),
            auxiliaryFreeBinary=all.Count(v=>!primary.Contains(v.Index) && v.VType==GRB.BINARY && v.LB<v.UB),
            fixedVariables=all.Count(v=>v.LB==v.UB),trainCount=model.Instance.Trains.Count,stopCount=model.Instance.TrainStops.Count,
            sectionRelations=model.SectionConflictCandidates.Count,bigM=new { count=model.ConditionalBigMAuditRows.Count,
                maximum=model.ConditionalBigMAuditRows.Select(r=>r.Value).DefaultIfEmpty(0).Max() } };
    }
    private static object StaticAudit(TimetablingInstance instance)
    {
        var bounds=HardEventBoundPropagator.Build(instance);
        var preprocessing=StopModePreprocessor.Analyze(instance);
        var quotas=TrainSectionStopQuotaConstraintBuilder.Plan(instance).Select(q=>new { q.RuleId,q.MinStops,q.MaxStops,
            possible=q.CountedStops.Count(s=>s.StopRequirement!=StopRequirement.MustPass),
            mandatory=q.CountedStops.Count(s=>s.StopRequirement==StopRequirement.MustStop || s.StopRole!=TrainStopRole.Intermediate) }).ToArray();
        int[] references=instance.Trains.Select(t=>t.OrderedStops[0].RawDepTimeSeconds).Where(t=>t is >=0).Select(t=>t!.Value).Order().ToArray();
        return new { scope="OriginalHard",bounds.TightenedLowerCount,bounds.TightenedUpperCount,bounds.Contradictions,
            routeTrackContradictions=preprocessing.Contradictions,quotaNecessaryContradictions=quotas.Where(q=>q.possible<q.MinStops || q.mandatory>q.MaxStops),
            rawOriginDepartureFirst=references.Length>0?(int?)references[0]:null,rawOriginDepartureLast=references.Length>0?(int?)references[^1]:null,
            referenceHorizonSeconds=references.Length>0?(int?)(references[^1]-references[0]):null,warning="Raw service references are not proven feasible hard windows." };
    }
    private static void Write<T>(string directory,string name,T value)=>File.WriteAllText(Path.Combine(directory,name),JsonSerializer.Serialize(value,Json));
}
