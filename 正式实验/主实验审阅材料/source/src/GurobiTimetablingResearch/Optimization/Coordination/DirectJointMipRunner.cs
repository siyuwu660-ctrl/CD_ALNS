using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Gurobi;
using GurobiTimetablingResearch.Domain;
using GurobiTimetablingResearch.IO;

namespace GurobiTimetablingResearch.Optimization;

internal sealed record DirectJointMipOptions(string OutputDirectory,double TotalSeconds=43200,double ReserveSeconds=600,int Seed=0,int Threads=4);
internal sealed record DirectJointMipResult(string Status,bool JointFeasible,double Seconds,ServiceObjectiveVector? Objective,
    double? LowerBoundSeconds,ResourceSpanGapReport SpanGap,int NativeStatus,bool PrimaryOptimal,string BoundScope,double? ExactModelLowerBoundSeconds)
{
    /// <summary>Business comparator key for the accepted timetable in the summary.</summary>
    public object? ObjectiveSelectionKey => ResourceSpanObjectivePolicy.SummaryKey(Objective);
}

/// <summary>Unrestricted original macro plus all legal station resource choices. No anchor, neighborhood, soft micro rows or repair cuts.</summary>
internal static class DirectJointMipRunner
{
    internal const string BoundScope="SameInstance_CompleteMacro_StrictResourceTime_ClosedQuotaBoundaryRelaxation_ScalarGlobalSpan_NoLocalFixings";
    internal static int Run(string[] args)
    {
        string Need(string key)=>args.SingleOrDefault(a=>a.StartsWith(key,StringComparison.Ordinal))?[key.Length..]??throw new ArgumentException("Missing "+key);
        double Number(string key,double fallback)=>args.SingleOrDefault(a=>a.StartsWith(key,StringComparison.Ordinal)) is string a?double.Parse(a[key.Length..],CultureInfo.InvariantCulture):fallback;
        var instance=new NormalizedDatasetLoader().Load(Path.GetFullPath(Need("--dataset=")));
        var result=Solve(instance,new(Need("--direct-output="),Number("--direct-total=",43200),Number("--direct-reserve=",600),
            checked((int)Number("--direct-seed=",0)),checked((int)Number("--direct-threads=",4))));
        Console.WriteLine(JsonSerializer.Serialize(result));return result.JointFeasible?0:2;
    }

    /// <summary>Solve the full joint model and retain the best strictly verified phase candidate.</summary>
    /// <param name="instance">Complete original timetable and station resources.</param>
    /// <param name="options">Output location and wall-clock solve and verification budgets in seconds.</param>
    /// <param name="phaseStarting">Optional notification before a native objective phase begins.</param>
    /// <returns>Native status, raw span bound and accepted objective when verification succeeds.</returns>
    /// <exception cref="ArgumentException">The budget, seed or thread count is invalid.</exception>
    /// <exception cref="IOException">The requested output location already exists.</exception>
    internal static DirectJointMipResult Solve(TimetablingInstance instance,DirectJointMipOptions options,Action<string>? phaseStarting=null)
    {
        if(!double.IsFinite(options.TotalSeconds) || !double.IsFinite(options.ReserveSeconds) || options.TotalSeconds<=0 ||
            options.ReserveSeconds<0 || options.ReserveSeconds>=options.TotalSeconds || options.Threads<1 || options.Seed<0)
            throw new ArgumentException("Invalid direct model budget/options");
        string output=Path.GetFullPath(options.OutputDirectory);
        if(Directory.Exists(output) || File.Exists(output))throw new IOException("Refusing to overwrite a direct MIP experiment");
        Directory.CreateDirectory(output);var clock=Stopwatch.StartNew();
        double Remaining()=>Math.Max(0,options.TotalSeconds-clock.Elapsed.TotalSeconds);
        double SearchRemaining()=>Math.Max(0,Remaining()-options.ReserveSeconds);
        void Write(string name,object value)=>Stages45Runner.Write(output,name,value);
        void Progress(string phase,object? detail=null)=>Write("progress.json",new {phase,seconds=clock.Elapsed.TotalSeconds,remainingSeconds=Remaining(),detail});
        var policy=ServiceWindowPolicy.Create(instance,new(true));
        double? lower=null,exactLower=null;bool primaryOptimal=false;int native=0;ServiceObjectiveVector? objective=null;
        List<(MasterSolutionSnapshot Snapshot, ServiceObjectiveVector Objective)> phaseCandidatesList = [];
        bool verificationAttempted = false;
        Write("configuration.json",new {options,instance.Meta,policy,fromOriginalData=true,importedSnapshot=false,
            objective="GlobalSpan_ServicePenalty_RunningSupplement_Lexicographic",boundScope=BoundScope,
            solverParameters=new {method=LowMemoryMipPolicy.DualSimplexMethod,threads=options.Threads},
            completeMacro=true,completeStationDomains=true,endpointOccupationSeconds=StationEndpointOccupationPolicy.DefaultSeconds,softMicroConstraints=false,
            budgetScope="Total wall clock including construction and strict verification; reserve deducted before search"});
        DirectJointMipResult Finish(string status,bool joint=false)
        {
            var gap=ResourceSpanGapReport.Calculate(joint?objective?.GlobalSpanSeconds:null,lower,0);
            var result=new DirectJointMipResult(status,joint,clock.Elapsed.TotalSeconds,objective,lower,gap,native,primaryOptimal,BoundScope,exactLower);
            Write("summary.json",result);Progress("Finished",result);return result;
        }
        DirectJointMipResult VerifyRetainedCandidate()
        {
            if (phaseCandidatesList.Count == 0) return Finish(native==GRB.Status.INFEASIBLE?"ProvenInfeasible":"UnknownNoIncumbent");
            if(Remaining()<=0)return Finish("UnknownVerificationBudget");
            verificationAttempted=true;Progress("StrictJointVerification",new {lowerBoundSeconds=lower});
            string verificationStatus = "Budget";
            int verificationRound = 0;
            // Verify in business order so the preferred candidate receives the reserve first.
            foreach (var phaseCandidate in phaseCandidatesList.OrderBy(entry => ResourceSpanObjectivePolicy.Key(entry.Objective)))
            {
                if (Remaining() <= 0) break;
                MasterSolutionSnapshot candidate = phaseCandidate.Snapshot;
                var proof = Stages45JointVerifier.Verify(instance, candidate, policy, Remaining, Math.Min(120, Remaining()),
                    output, verificationRound++, promotionSlots: 0, preferredAssignments: candidate.VerifiedMicroAssignments);
                verificationStatus = proof.Status;
                if (!proof.JointFeasible || proof.Canonical is null || proof.Readback?.Passed != true) continue;

                var acceptedObjective = ServiceBusinessObjectiveEvaluator.Calculate(instance, proof.Canonical, policy);
                if (objective is not null && !ResourceSpanObjectivePolicy.Better(acceptedObjective, objective)) continue;
                if (SnapshotFingerprint.Calculate(proof.Canonical) != SnapshotFingerprint.Calculate(candidate)) primaryOptimal = false;
                if (objective is null)
                    Write("first_joint_acceptance.json", new { seconds = clock.Elapsed.TotalSeconds, objective = acceptedObjective, proof.Readback });
                File.Copy(proof.Readback.SnapshotPath, Path.Combine(output, "best_joint.snapshot.json"), overwrite: true);
                objective = acceptedObjective;
                Write("joint_acceptance.json", new { seconds = clock.Elapsed.TotalSeconds, objective, proof.Readback, scope = BoundScope });

            }
            return objective is not null ? Finish("JointFeasible", true) : Finish("UnknownJointVerification_" + verificationStatus);
        }
        try
        {
            Progress("BuildingCompleteMacro");
            using var master=new BasicMilpModelBuilder(instance,neighborRadius:int.MaxValue,serviceWindows:policy,screenTemporalPrecedence:true).Build();
            var model=master.Model;model.Update();
            var expressions=new Dictionary<string,GRBLinExpr>(StringComparer.Ordinal);
            for(int i=0;i<model.NumObj;i++){model.Set(GRB.IntParam.ObjNumber,i);expressions.Add(model.ObjNName,model.GetObjective(i));}
            var augmentations=AddCompleteMicro(master,SearchRemaining,(station,count)=>Progress("BuildingCompleteStationMicro",new {station,completedStations=count}));
            model.Update();
            Write("model.json",new {variables=model.NumVars,rows=model.NumConstrs,integers=model.NumIntVars,
                stations=augmentations.Count,alternatives=augmentations.Sum(a=>a.AlternativeCount),buildSeconds=clock.Elapsed.TotalSeconds,
                allLegalStopModes=true,localFixings=false,allowedAlternativeRestrictions=false});
            Write("solver_parameters.json",LowMemoryMipPolicy.Apply(model,options.Threads));
            model.Set(GRB.IntParam.Seed,options.Seed);
            model.Set(GRB.IntParam.OutputFlag,1);model.Set(GRB.IntParam.LogToConsole,0);
            model.Set(GRB.StringParam.LogFile,Path.Combine(output,"gurobi.log"));
            ResourceLocalSolver.ConfigureNumerics(model);
            model.Set(GRB.DoubleParam.MIPGap,0);model.Set(GRB.DoubleParam.MIPGapAbs,0);
            // This unconditioned copy exists BEFORE any lexicographic optimum lock.
            // Strict primal rows stay unchanged. The copy only supplies a common LB
            // using the same strict time rows as the current resource checker.
            if(SearchRemaining()>.01)
            {
                using var bound=new GRBModel(model);
                Write("comparison_bound_solver_parameters.json",LowMemoryMipPolicy.Apply(bound,options.Threads));
                ResourceGlobalSpanBoundSolver.ConfigureSingleSpanObjective(bound);
                foreach(var row in bound.GetConstrs())
                {
                    string name=row.ConstrName;
                    if(!(name.EndsWith("_fwd",StringComparison.Ordinal) || name.EndsWith("_rev",StringComparison.Ordinal)))continue;
                    double tolerance=TimetableTimePolicy.BusinessRelaxationSeconds;
                    if(tolerance>0)
                    { if(row.Sense!=GRB.GREATER_EQUAL)throw new InvalidOperationException("Unexpected micro separation sense");row.RHS-=tolerance; }
                }
                bound.Update();bound.Set(GRB.StringParam.LogFile,Path.Combine(output,"comparison_bound.log"));
                bound.Set(GRB.DoubleParam.TimeLimit,Math.Max(.001,Math.Min(600,SearchRemaining()*.05)));
                var liveBound=new DirectJointMipProgress(output,"comparison_span_bound",clock,null);bound.SetCallback(liveBound);
                Progress("SolvingComparableSpanBound");bound.Optimize();
                if(liveBound.Error is not null)throw new IOException(liveBound.Error);
                if(ResourceGlobalSpanBoundSolver.CanUseBound(bound.Status))lower=ValidLowerBound(bound.ObjBoundC);
                Write("span_bound.json",new {lowerBoundSeconds=lower,nativeStatus=bound.Status,scope=BoundScope,
                    relaxedIncumbentIsNotJointUpperBound=true,seconds=clock.Elapsed.TotalSeconds});
            }
            string[] objectives=["global_span","service_window_penalty","running_time_supplement"];
            // Scalar phases avoid interpreting secondary-objective native bounds as span bounds.
            foreach(string phase in objectives.Where(expressions.ContainsKey))
            {
                if(SearchRemaining()<=.01)break;
                phaseStarting?.Invoke(phase);
                model.NumObj=0;model.Update();model.SetObjective(expressions[phase],GRB.MINIMIZE);model.Update();
                var live=new DirectJointMipProgress(output,phase,clock,lower);
                model.SetCallback(live);model.Set(GRB.DoubleParam.TimeLimit,SearchRemaining());
                Progress("Solving_"+phase,new {lowerBoundSeconds=lower});
                master.Optimize();native=model.Status;
                if(live.Error is not null)throw new IOException(live.Error);
                if(phase=="global_span")
                {
                    primaryOptimal=native==GRB.Status.OPTIMAL;
                    if(ResourceGlobalSpanBoundSolver.CanUseBound(native))exactLower=ValidLowerBound(model.ObjBoundC);
                    Write("exact_span_bound.json",new {exactModelLowerBoundSeconds=exactLower,rawNativeBound=exactLower is null?(double?)null:model.ObjBoundC,
                        nativeStatus=native,nativePrimaryOptimal=primaryOptimal,scope=BoundScope,closedQuotaRecountRequired=true,seconds=clock.Elapsed.TotalSeconds});
                }
                if(!master.HasIncumbentSolution)break;
                MasterSolutionSnapshot candidate = MasterSolutionSnapshot.Capture(master);
                string fingerprint=SnapshotFingerprint.Calculate(candidate);
                var assignments=augmentations.SelectMany(a=>a.CaptureSelectedAlternatives()).Select(a=>new VerifiedMicroAssignmentWitness(
                    a.StationName,a.RequirementId,a.TrainIndex,a.IsStop!.Value,a.TrackName,a.IncomingRouteId,a.OutgoingRouteId,
                    fingerprint,candidate.ArrivalSecondsByStopId[a.RequirementId],candidate.DepartureSecondsByStopId[a.RequirementId])).ToArray();
                candidate = candidate with { VerifiedMicroAssignments = assignments };
                var candidateObjective = ServiceBusinessObjectiveEvaluator.Calculate(instance, candidate, policy);
                if (!phaseCandidatesList.Any(entry => SnapshotFingerprint.Calculate(entry.Snapshot) == fingerprint &&
                    entry.Snapshot.VerifiedMicroAssignments?.SequenceEqual(assignments) == true))
                    phaseCandidatesList.Add((candidate, candidateObjective));
                if (phaseCandidatesList.All(entry => !ResourceSpanObjectivePolicy.Better(entry.Objective, candidateObjective)))
                {
                    MasterSolutionSnapshotStore.Save(Path.Combine(output, "native_candidate.snapshot.json"), instance.Meta.InstanceName, candidate);
                    Write("native_candidate.json", new { phase, seconds = clock.Elapsed.TotalSeconds, objective = candidateObjective,
                        nativeStatus = native, strictJointVerified = false });
                }
                if(native!=GRB.Status.OPTIMAL)break;
                // No relaxation/tolerance is introduced in the lexicographic lock.
                double optimum=model.ObjVal;
                model.AddConstr(expressions[phase],GRB.LESS_EQUAL,optimum,"lex_optimum_"+phase);model.Update();
            }
            return VerifyRetainedCandidate();
        }
        catch(Exception ex)
        {
            Write("error.json",new {error=ex.ToString(),retainedCandidate=phaseCandidatesList.Count > 0});
            if(phaseCandidatesList.Count > 0 && !verificationAttempted && Remaining()>0)
            {
                try{return VerifyRetainedCandidate();}
                catch(Exception verificationError){Write("verification_error.json",new {error=verificationError.ToString()});}
            }
            if (objective is not null) return Finish("JointFeasible", true);
            return Finish(ex is TimeoutException?"UnknownBuildBudget":"Error");
        }
    }

    internal static IReadOnlyList<StationMicroRepairAugmentation> AddCompleteMicro(BasicMilpModel model,Func<double> remaining,Action<string,int>? progress=null)
    {
        var instance=model.Instance;List<StationMicroRepairAugmentation> result=[];
        model.Model.Update();
        foreach(var station in instance.TrainStops.GroupBy(s=>s.StationName,StringComparer.OrdinalIgnoreCase).OrderBy(g=>g.Key,StringComparer.Ordinal))
        {
            if(remaining()<=0)throw new TimeoutException("Complete micro build budget");
            progress?.Invoke(station.Key,result.Count);List<StationMicroRequirement> requirements=[];
            foreach(var stop in station)
            {
                var variable=model.StopVars[stop.StopId];var train=instance.Trains.Single(t=>t.TrainIndex==stop.TrainIndex);
                foreach(bool mode in new[]{false,true}.Where(m=>(m?1:0)>=variable.LB && (m?1:0)<=variable.UB))
                    requirements.Add(StationLevelMicroFeasibilitySlave.BuildRequirementForMode(instance,train,stop,mode,0,0));
            }
            result.Add(StationMicroRepairModelAugmenter.Apply(model,requirements,StationEndpointOccupationPolicy.DefaultSeconds,variablePrefix:"direct_station_"+result.Count,
                remaining:remaining,occupationOnlyForStops:false));
        }
        return result;
    }

    internal static double? ValidLowerBound(double raw)=>double.IsFinite(raw) && Math.Abs(raw)<GRB.INFINITY?Math.Max(0,raw-1e-6):null;
}

internal sealed class DirectJointMipProgress(string output,string phase,Stopwatch clock,double? priorLower):GRBCallback
{
    private double last=-30;
    internal string? Error {get;private set;}
    protected override void Callback()
    {
        try
        {
            if(where!=GRB.Callback.MIP)return;
            double seconds=clock.Elapsed.TotalSeconds;if(seconds-last<30)return;last=seconds;
            var row=new {phase,seconds,nodes=GetDoubleInfo(GRB.Callback.MIP_NODCNT),solutions=GetIntInfo(GRB.Callback.MIP_SOLCNT),
                nativeIncumbentObjective=Finite(GetDoubleInfo(GRB.Callback.MIP_OBJBST)),
                lowerBoundSeconds=phase=="comparison_span_bound"?DirectJointMipRunner.ValidLowerBound(GetDoubleInfo(GRB.Callback.MIP_OBJBND)):priorLower,
                exactModelSpanBound=phase=="global_span"?DirectJointMipRunner.ValidLowerBound(GetDoubleInfo(GRB.Callback.MIP_OBJBND)):null,
                nativeIncumbentStrictlyVerified=false,boundScope=DirectJointMipRunner.BoundScope};
            Stages45Runner.Write(output,"progress.json",row);
            File.AppendAllText(Path.Combine(output,"iterations.jsonl"),JsonSerializer.Serialize(row)+Environment.NewLine);
        }
        catch(Exception ex){Error=ex.ToString();Abort();}
    }
    private static double? Finite(double v)=>double.IsFinite(v) && Math.Abs(v)<GRB.INFINITY?v:null;
}
