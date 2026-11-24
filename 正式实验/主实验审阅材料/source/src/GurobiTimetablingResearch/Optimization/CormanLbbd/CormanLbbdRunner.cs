using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Gurobi;
using GurobiTimetablingResearch.Domain;
using GurobiTimetablingResearch.IO;

namespace GurobiTimetablingResearch.Optimization;

internal sealed record CormanLbbdOptions(string OutputDirectory,double TotalSeconds=43200,double MasterSeconds=7200,
    double SubproblemSeconds=7200,int AggregationCuts=0,int Seed=0,int Threads=4,int MaximumRounds=100000,int MaximumStationArcs=2_000_000,bool SpanFirst=false)
{
    internal void Validate()
    {
        if(string.IsNullOrWhiteSpace(OutputDirectory) || new[]{TotalSeconds,MasterSeconds,SubproblemSeconds}.Any(x=>!double.IsFinite(x)||x<=0)
            || AggregationCuts<0 || Seed<0 || Seed>2_000_000_000 || Threads<1 || MaximumRounds<1 || MaximumStationArcs<1)
            throw new ArgumentException("Invalid Corman LBBD budget/options");
    }
}
internal sealed record CormanLbbdResult(string Status,bool JointFeasible,int Rounds,int Cuts,double Seconds,
    bool MasterOptimalAtAcceptance,ServiceObjectiveVector? Objective,string StopReason,int? NativeStatus)
{
    /// <summary>Business comparator key for the accepted timetable in the summary.</summary>
    public object? ObjectiveSelectionKey => ResourceSpanObjectivePolicy.SummaryKey(Objective);
}

internal static class CormanLbbdRunner
{
    internal static int Run(string[] args)
    {
        var values=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach(string arg in args)
        {
            if(arg=="--corman-lbbd")continue;
            int equal=arg.IndexOf('=');if(equal<0 || !values.TryAdd(arg[..equal],arg[(equal+1)..]))throw new ArgumentException("Malformed/duplicate Corman argument: "+arg);
        }
        string[] allowed=["--dataset","--corman-output","--corman-total","--corman-master","--corman-subproblem","--corman-aggregation-cuts","--corman-seed","--corman-threads","--corman-max-rounds","--corman-max-station-arcs","--corman-objective"];
        if(values.Keys.Except(allowed).Any())throw new ArgumentException("Unsupported Corman argument; ALNS and saved-candidate inputs are not accepted");
        string Need(string key)=>values.TryGetValue(key,out var value)?value:throw new ArgumentException("Missing "+key);
        double D(string key,double fallback)=>values.TryGetValue(key,out var v)?double.Parse(v,CultureInfo.InvariantCulture):fallback;
        int I(string key,int fallback)=>values.TryGetValue(key,out var v)?int.Parse(v,CultureInfo.InvariantCulture):fallback;
        string objectiveMode=values.GetValueOrDefault("--corman-objective","service-first");
        if(objectiveMode is not ("service-first" or "span-first"))throw new ArgumentException("Unknown Corman objective order");
        var options=new CormanLbbdOptions(Need("--corman-output"),D("--corman-total",43200),D("--corman-master",7200),
            D("--corman-subproblem",7200),I("--corman-aggregation-cuts",0),I("--corman-seed",0),I("--corman-threads",4),
            I("--corman-max-rounds",100000),I("--corman-max-station-arcs",2_000_000),objectiveMode=="span-first");
        options.Validate();var instance=new NormalizedDatasetLoader().Load(Path.GetFullPath(Need("--dataset")));
        var result=Solve(instance,options);Console.WriteLine(JsonSerializer.Serialize(result));return result.JointFeasible?0:2;
    }

    internal static CormanLbbdResult Solve(TimetablingInstance instance,CormanLbbdOptions options)
    {
        options.Validate();CheckDirectionSupport(instance);
        string output=Path.GetFullPath(options.OutputDirectory);
        if(Directory.Exists(output) || File.Exists(output))throw new IOException("Refusing to overwrite an experiment");
        Directory.CreateDirectory(output);var clock=Stopwatch.StartNew();int round=0,cuts=0;int? nativeStatus=null;
        double Remaining()=>Math.Max(0,options.TotalSeconds-clock.Elapsed.TotalSeconds);
        void Write(string name,object data)=>Stages45Runner.Write(output,name,data);
        void Progress(string phase,object? details=null,bool announce=true)
        {
            Write("progress.json",new {phase,round,cuts,seconds=clock.Elapsed.TotalSeconds,remainingSeconds=Remaining(),time=DateTimeOffset.Now,details});
            if(announce)Console.WriteLine($"corman phase={phase} round={round} cuts={cuts} wall={clock.Elapsed.TotalSeconds:F1}");
        }
        CormanLbbdResult Finish(string status,string reason,bool joint=false,bool masterOptimal=false,ServiceObjectiveVector? objective=null)
        {
            var result=new CormanLbbdResult(status,joint,round,cuts,clock.Elapsed.TotalSeconds,masterOptimal,objective,reason,nativeStatus);
            Write("run_summary.json",result);Progress("Finished",result);return result;
        }
        var policy=ServiceWindowPolicy.Create(instance,new(true));
        Write("configuration.json",new {algorithm="LeutwilerCorman2022_SmtPositiveCycleLbbd_ProjectAdapter",options,instance.Meta,
            solverParameters=new {method=LowMemoryMipPolicy.DualSimplexMethod,threads=options.Threads},
            paper="10.1016/j.ejor.2022.02.043",firstJointStop=true,fromOriginalData=true,importedSnapshot=false,
            stopModeGuardedCuts=true,objective=options.SpanFirst?"GlobalSpan_ServicePenalty_RunningSupplement_Lexicographic":"ServicePenalty_GlobalSpan_RunningSupplement_Lexicographic",
            paperObjectiveIsDifferent=true,macroRelations="Complete",satBacktracking="FirstUIP_CDCL_Nonchronological",
            proofExtraction="ResolutionParentsFromEmptyClause_Asin2008_Section4_1",
            variant=options.AggregationCuts==1?"Simple":options.AggregationCuts==0?"Aggregated":"CappedAggregation_ProjectSetting",
            numericTolerance=CormanSmtSolver.Epsilon,endpointOccupationSeconds=StationEndpointOccupationPolicy.DefaultSeconds,
            budgetSemantics="SubproblemSeconds per SMT certificate; construction separately capped; total wall clock includes build and verification",
            started=DateTimeOffset.Now});
        try
        {
            Progress("BuildingCompleteMaster");
            using var master=new BasicMilpModelBuilder(instance,neighborRadius:int.MaxValue,serviceWindows:policy,screenTemporalPrecedence:true).Build();
            ConfigureObjective(master.Model,options.SpanFirst);
            master.Model.Set(GRB.IntParam.OutputFlag,1);master.Model.Set(GRB.IntParam.LogToConsole,0);
            master.Model.Set(GRB.StringParam.LogFile,Path.Combine(output,"master.log"));
            Write("solver_parameters.json",LowMemoryMipPolicy.Apply(master.Model,options.Threads));
            master.Model.Set(GRB.IntParam.Seed,options.Seed);
            master.Model.Set(GRB.DoubleParam.MIPGap,0);master.Model.Set(GRB.DoubleParam.MIPGapAbs,0);
            var knownCuts=new HashSet<string>(StringComparer.Ordinal);
            var feasibleCache=new Dictionary<string,IReadOnlyList<VerifiedMicroAssignmentWitness>>(StringComparer.Ordinal);
            string[] stations=instance.TrainStops.Select(s=>s.StationName).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
            for(round=1;round<=options.MaximumRounds;round++)
            {
                if(Remaining()<=0)return Finish("UnknownBudget","TotalBudgetBeforeMaster");
                Progress("SolvingMaster",new {variables=master.Model.NumVars,rows=master.Model.NumConstrs});
                master.Model.Set(GRB.DoubleParam.TimeLimit,Math.Min(options.MasterSeconds,Remaining()));master.Optimize();
                int masterStatus=master.Model.Status;nativeStatus=masterStatus;bool masterOptimal=masterStatus==GRB.Status.OPTIMAL;
                // Persist before any incumbent-dependent branch or subproblem.
                // The multi-objective master does not expose a comparable scalar
                // global-span bound; never relabel another objective's native bound.
                Write("master_status.json",new {round,nativeStatus,solCount=master.Model.SolCount,seconds=clock.Elapsed.TotalSeconds,
                    lowerBoundSeconds=(double?)null,boundScope="NoComparableGlobalSpanBound_MultiObjectiveMaster",
                    masterScope="SameInstance_CompleteMacro_ValidCormanCuts"});
                if(!master.HasIncumbentSolution)return Finish(masterStatus==GRB.Status.INFEASIBLE?"ProvenInfeasible":"UnknownMaster",
                    masterStatus==GRB.Status.INFEASIBLE?"CompleteMasterAndValidCutsInfeasible":"MasterEndedWithoutIncumbent");
                if(Remaining()<=0)return Finish("UnknownBudget","TotalBudgetAfterMaster");
                var snapshot=MasterSolutionSnapshot.Capture(master);
                var macroCheck=new MasterSolutionValidator().Validate(master);
                if(!macroCheck.IsFeasible || macroCheck.Violations.Count!=0)
                {
                    // Candidate-only recovery and independent acceptance; never learn master cuts
                    // or inherit a global bound from the restricted recovery problem.
                    if(ClosedQuotaCandidateRecovery.HasViolation(instance,snapshot))
                    {
                        var recoveredProof=Stages45JointVerifier.Verify(instance,snapshot,policy,Remaining,
                            Math.Min(options.MasterSeconds,Remaining()),output,round,promotionSlots:0);
                        if(recoveredProof.JointFeasible && recoveredProof.Canonical is not null && recoveredProof.Readback?.Passed==true)
                        {
                            File.Copy(recoveredProof.Readback.SnapshotPath,Path.Combine(output,"best_joint.snapshot.json"));
                            Write("first_joint_acceptance.json",new {round,seconds=clock.Elapsed.TotalSeconds,
                                restrictedRecovery=true,globalOptimality=false,recoveredProof.Readback,
                                objective=recoveredProof.Canonical.ServiceObjective});
                            return Finish("JointFeasible","ClosedQuotaCandidateRecovery",true,false,recoveredProof.Canonical.ServiceObjective);
                        }
                    }
                    return Finish("UnknownMacroValidation","MasterCandidateRejected");
                }
                string fingerprint=SnapshotFingerprint.Calculate(snapshot);
                int before=cuts;bool allFeasible=true;List<VerifiedMicroAssignmentWitness> witnesses=[];List<object> stationResults=[];
                void RecordIteration(bool complete)=>File.AppendAllText(Path.Combine(output,"iterations.jsonl"),JsonSerializer.Serialize(new {
                    round,seconds=clock.Elapsed.TotalSeconds,masterStatus,masterOptimal,fingerprint,cutsAdded=cuts-before,cuts,
                    stationScanCompleted=complete,allStationsFeasible=complete && allFeasible,stationResults})+Environment.NewLine);
                foreach(string station in stations)
                {
                    if(Remaining()<=0)return Finish("UnknownBudget","TotalBudgetBeforeSubproblem");
                    var stopIds=instance.TrainStops.Where(s=>s.StationName.Equals(station,StringComparison.OrdinalIgnoreCase)).Select(s=>s.StopId).Order(StringComparer.Ordinal).ToArray();
                    string cacheKey=Hash(new {station,events=stopIds.Select(id=>new {id,mode=snapshot.StopByStopId[id],a=snapshot.ArrivalSecondsByStopId[id],d=snapshot.DepartureSecondsByStopId[id]})});
                    if(feasibleCache.TryGetValue(cacheKey,out var cached))
                    {
                        witnesses.AddRange(cached.Select(w=>w with {CandidateSnapshotFingerprint=fingerprint}));
                        stationResults.Add(new {station,status="FeasibleCached",cuts=0});continue;
                    }
                    var subClock=Stopwatch.StartNew();bool Budget()=>Remaining()>0 && subClock.Elapsed.TotalSeconds<options.SubproblemSeconds;
                    Progress("BuildingStationSubproblem",new {station});
                    var sub=CormanStationAdapter.Build(instance,snapshot,station,Budget,options.MaximumStationArcs);
                    Progress("SolvingStationSubproblem",new {station,variables=sub.Problem.VariableCount,arcs=sub.Problem.Arcs.Length});
                    var aggregation=CormanCutGenerator.Aggregate(sub.Problem,sub.Boundaries,()=>Remaining()>0,options.AggregationCuts,
                        certificateSeconds:options.SubproblemSeconds,progress:(call,completedCuts,statistics)=>Progress("SolvingStationSubproblem",
                            new {station,variables=sub.Problem.VariableCount,arcs=sub.Problem.Arcs.Length,call,completedCuts,
                                certificateCompleted=statistics is not null,statistics},announce:false));
                    if(aggregation.Original.Status==CormanStatus.Feasible)
                    {
                        var selected=sub.Witnesses(aggregation.Original,fingerprint);feasibleCache[cacheKey]=selected;witnesses.AddRange(selected);
                    }
                    else
                    {
                        allFeasible=false;var events=sub.Events.ToDictionary(p=>p.Key,p=>p.Value.Side==TimetableEventSide.Arrival?master.ArrivalVars[p.Value.StopId]:master.DepartureVars[p.Value.StopId]);
                        for(int i=0;i<aggregation.Cuts.Count;i++)
                        {
                            var terms=aggregation.Cuts[i];var times=sub.Boundaries.ToDictionary(p=>p.Key,p=>p.Value.Lower);
                            if(CormanCutGenerator.Satisfied(terms,times))throw new InvalidOperationException("Certificate cut does not exclude the source candidate");
                            var namedTerms=terms.Select(t=>new {from=sub.Problem.Nodes[t.From],to=sub.Problem.Nodes[t.To],length=t.Length}).ToArray();
                            string cutId=Hash(new {station,modes=sub.Modes.OrderBy(p=>p.Key,StringComparer.Ordinal),terms=namedTerms});
                            if(!knownCuts.Add(cutId))continue;
                            CormanMilpCuts.Add(master.Model,events,terms,"corman_"+(++cuts),sub.Modes,master.StopVars);
                            File.AppendAllText(Path.Combine(output,"cuts.jsonl"),JsonSerializer.Serialize(new {
                                round,station,cutId,sourceFingerprint=fingerprint,modeGuards=sub.Modes,terms=namedTerms,
                                proofKind=terms.Length==0?"CompleteUnsatWithoutTimeEscape":"Equation11PositiveCycleCover",
                                graphHash=Hash(sub.Problem),cycles=aggregation.Proofs[i],aggregationIndex=i})+Environment.NewLine);
                        }
                    }
                    stationResults.Add(new {station,status=aggregation.Original.Status.ToString(),cuts=aggregation.Cuts.Count,
                        seconds=subClock.Elapsed.TotalSeconds,aggregation.Calls,aggregation.Finished,aggregation.Reason,
                        lastCertificateStatus=aggregation.LastStatus.ToString(),aggregation.Statistics});
                    if(aggregation.LastStatus==CormanStatus.Unknown)
                    {
                        // Keep previously certified cuts as evidence, but a
                        // later aggregation call timing out aborts this run too.
                        RecordIteration(false);
                        return Finish(Remaining()<=0?"UnknownBudget":"UnknownSubproblem","IncompleteCertificateDuringAggregation");
                    }
                }
                RecordIteration(true);
                if(allFeasible)
                {
                    Progress("StrictJointVerification",new {assignments=witnesses.Count});
                    var proof=Stages45JointVerifier.Verify(instance,snapshot,policy,Remaining,Math.Min(300,Remaining()),output,round,
                        promotionSlots:0,preferredAssignments:witnesses);
                    if(!proof.JointFeasible || proof.Canonical is null || proof.Readback?.Passed!=true)
                        return Finish("UnknownJointVerification",proof.Status);
                    File.Copy(proof.Readback.SnapshotPath,Path.Combine(output,"best_joint.snapshot.json"));
                    Write("first_joint_acceptance.json",new {round,seconds=clock.Elapsed.TotalSeconds,masterOptimal,proof.Readback,
                        objective=proof.Canonical.ServiceObjective});
                    return Finish("JointFeasible","RequestedFirstJoint",true,masterOptimal &&
                        SnapshotFingerprint.Calculate(proof.Canonical)==fingerprint,proof.Canonical.ServiceObjective);
                }
                if(cuts==before)return Finish("UnknownNoNewCut","SourceCandidateWasNotSeparated");
                master.Model.Update();
            }
            round=options.MaximumRounds;return Finish("UnknownRoundLimit","MaximumRounds");
        }
        catch(TimeoutException ex){return Finish("UnknownBudget",ex.Message);}
        catch(CormanConstructionLimitException ex){return Finish("UnknownConstructionLimit",ex.Message);}
        catch(Exception ex){Write("error.json",new {type=ex.GetType().FullName,ex.Message});return Finish("UnknownError",ex.Message);}
    }

    internal static void ConfigureObjective(GRBModel model,bool spanFirst)
    { if(spanFirst)ResourceSpanObjectivePolicy.ApplyLocalPriorities(model,true);model.Update(); }
    internal static string Hash(object data)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data))));
    private static void CheckDirectionSupport(TimetablingInstance instance)
    {
        // The shared macro builder currently assumes a single service direction
        // on each section. The generic paper kernel has no such restriction.
        var traversals=instance.Trains.SelectMany(t=>t.OrderedStops.Zip(t.OrderedStops.Skip(1),(a,b)=>(b.IncomingSectionName,From:a.StationName,To:b.StationName)))
            .Where(x=>!string.IsNullOrWhiteSpace(x.IncomingSectionName)).GroupBy(x=>x.IncomingSectionName,StringComparer.OrdinalIgnoreCase);
        if(traversals.Any(g=>g.Select(x=>(x.From,x.To)).Distinct().Count()>1))throw new NotSupportedException("Project macro adapter supports one service direction per section; generic Corman graph kernel is unrestricted");
    }
}
