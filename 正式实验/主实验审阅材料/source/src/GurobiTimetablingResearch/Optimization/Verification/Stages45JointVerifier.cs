using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

internal sealed record Stages45JointProof(bool CompleteMacro,bool JointFeasible,string Status,
    MasterSolutionSnapshot? Canonical,IReadOnlySet<int> ConflictTrains,object? MicroEvidence,
    SavedJointSnapshotVerificationResult? Readback,IReadOnlyList<PromotedMicroComponent> MicroRepairs,double Seconds,
    IReadOnlySet<string>? ConflictStopIds=null,IReadOnlyList<ConditionalMacroTimeCut>? TimeCuts=null,DirectedJointProgress? JointProgress=null);

internal static class Stages45JointVerifier
{
    internal static Stages45JointProof Verify(TimetablingInstance instance,MasterSolutionSnapshot candidate,
        ServiceWindowPolicy policy,Func<double> remaining,double solveSeconds,string output,int round,
        int promotionSlots=3,int maximumAlternatives=800,int maximumConditionalRows=200000,bool promoteWitnessFailures=false,
        IReadOnlySet<string>? knownTimeCutIds=null,IReadOnlyList<PromotedMicroComponent>? retainedCoreComponents=null,bool batchResourceFeedback=false,IReadOnlyList<VerifiedMicroAssignmentWitness>? preferredAssignments=null,
        bool allowClosedQuotaRecovery=true)
    {
        var clock=System.Diagnostics.Stopwatch.StartNew();
        if(allowClosedQuotaRecovery && ClosedQuotaCandidateRecovery.HasViolation(instance,candidate) && remaining()>0)
        {
            var recovered=ClosedQuotaCandidateRecovery.TryRecover(instance,candidate,policy,remaining,solveSeconds,
                new HashSet<string>(StringComparer.Ordinal),Path.Combine(output,$"round_{round:D3}_closed_quota_recovery.log"));
            if(recovered is not null)
            {
                candidate=recovered;
                // A changed timetable or stop pattern invalidates the original resource certificate.
                preferredAssignments=null;
            }
        }
        HashSet<int> conflicts=[];HashSet<string> conflictStops=new(StringComparer.OrdinalIgnoreCase);
        MasterSolutionSnapshot? joint=null;object? evidence=null;List<PromotedMicroComponent> repairs=[];List<object> repairAudit=[];
        IReadOnlyList<ConditionalMacroTimeCut> timeCuts=[];
        DirectedJointProgress? jointProgress=null;
        FullMacroCandidateVerification macro=FullMacroCandidateVerifier.Verify(instance,candidate,policy,remaining,solveSeconds,
            Path.Combine(output,$"round_{round:D3}_complete.log"),(model,canonical)=>
            {
                var request=new MicroFeasibilitySlaveRequest(RemainingSeconds:remaining,FixedAssignments:preferredAssignments);
                var slave=CheckerMicroValidationFactory.CreateSlave(StationEndpointOccupationPolicy.DefaultSeconds).Solve(model,request);
                var independent=CheckerMicroValidationFactory.CreateChecker(StationEndpointOccupationPolicy.DefaultSeconds).Validate(model,request);
                if(knownTimeCutIds is not null)
                {
                    string source=SnapshotFingerprint.Calculate(canonical);
                    timeCuts=ExactHeadwayMacroFeedbackProjector.TryCreate(instance,canonical,source,
                        "complete_macro_fixed_candidate",$"round_{round}_global_micro",slave.Conflicts)?.ConditionalTimeCuts ?? [];
                }
                bool deferPromotion=!batchResourceFeedback && knownTimeCutIds is not null && timeCuts.Any(c=>!knownTimeCutIds.Contains(c.CutId));
                var preciseCoreStations=slave.StationResults.Where(s=>s.AssignmentResult.Status==MicroSolveStatus.ProvenInfeasible &&
                    s.ConflictCore.SupportStopIds.Count>0).Select(s=>s.StationName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach(var station in slave.StationResults.Where(s=>!s.IsFeasible || s.AssignmentResult.Status!=MicroSolveStatus.Feasible))
                {
                    conflicts.UnionWith(station.ConflictCore.TrainIndices);
                    if(preciseCoreStations.Contains(station.StationName)) conflictStops.UnionWith(station.ConflictCore.SupportStopIds);
                    else conflictStops.UnionWith(instance.TrainStops.Where(s=>s.StationName.Equals(station.StationName,StringComparison.OrdinalIgnoreCase) &&
                        (station.ConflictCore.TrainIndices.Count==0 || station.ConflictCore.TrainIndices.Contains(s.TrainIndex))).Select(s=>s.StopId));
                }
                foreach(var conflict in independent.Conflicts)
                {
                    if(conflict.FirstTrainIndex is null && conflict.SecondTrainIndex is null && preciseCoreStations.Contains(conflict.StationName)) continue;
                    conflictStops.UnionWith(instance.TrainStops.Where(s=>s.StationName.Equals(conflict.StationName,StringComparison.OrdinalIgnoreCase) &&
                        (conflict.FirstTrainIndex is null && conflict.SecondTrainIndex is null || s.TrainIndex==conflict.FirstTrainIndex || s.TrainIndex==conflict.SecondTrainIndex)).Select(s=>s.StopId));
                    if(conflict.FirstTrainIndex is int first) conflicts.Add(first);
                    if(conflict.SecondTrainIndex is int second) conflicts.Add(second);
                }
                foreach(var station in slave.StationResults.Where(s=>s.AssignmentResult.Status==MicroSolveStatus.ProvenInfeasible)
                    .OrderBy(s=>batchResourceFeedback?DirectedFeedbackPriority.LastPromotion(s.StationName,retainedCoreComponents):0)
                    .ThenByDescending(s=>knownTimeCutIds is null?0:s.Conflicts.Count))
                {
                    if(remaining()<=0 || deferPromotion || repairs.Count>=promotionSlots) break;
                    var buildClock=System.Diagnostics.Stopwatch.StartNew();
                    var build=knownTimeCutIds is not null?DirectedMicroCoreBuilder.Build(model,station,round,retainedCoreComponents):
                        ValidityFirstHotspotComponentBuilder.BuildCompleteStationComponent(model,station.StationName,StationEndpointOccupationPolicy.DefaultSeconds,round,
                            "stages45_original_resource_repair_restricted_search");
                    var estimate=build.Component is null?null:PromotedMicroCompactRelationEstimator.Estimate(build.Component.Requirements,
                        (a,b)=>instance.TryGetRouteHeadwayRule(a,b,out _));
                    bool supported=build.IsComplete && build.Component is not null && build.AlternativeCount<=maximumAlternatives &&
                        estimate is not null && estimate.ConditionalConstraintCount<=maximumConditionalRows;
                    repairAudit.Add(new { station.StationName,build.IsComplete,build.Reason,build.RequirementCount,build.AlternativeCount,
                        estimate,supported,seconds=buildClock.Elapsed.TotalSeconds,scope="RestrictedSearch",globalInfeasibilityCut=false });
                    if(supported) repairs.Add(build.Component!);
                }
                string fingerprint=SnapshotFingerprint.Calculate(canonical);
                var assignments=VerifiedMicroAssignmentWitnessBuilder.Capture(slave,fingerprint);
                var fixedCheck=VerifiedMicroAssignmentValidator.Validate(instance,canonical,assignments,slave,StationEndpointOccupationPolicy.DefaultSeconds);
                var identity=MacroMicroJointAcceptancePolicy.CheckIdentity(fingerprint,instance.TrainStops.Select(s=>s.StopId),assignments);
                var witnessErrors=fixedCheck.Findings.Where(f=>f.Code!="missing_saved_witness").ToArray();
                var failedStations=slave.StationResults.Where(s=>s.AssignmentResult.Status!=MicroSolveStatus.Feasible || !s.IsFeasible)
                    .Select(s=>s.StationName).Concat(independent.Conflicts.Select(c=>c.StationName))
                    .Concat(instance.Stations.Where(s=>witnessErrors.Any(f=>f.Detail.Contains($"station='{s}'",StringComparison.OrdinalIgnoreCase))))
                    .Where(s=>!string.IsNullOrEmpty(s)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                jointProgress=new(failedStations,slave.StationResults.Count(s=>s.AssignmentResult.Status==MicroSolveStatus.Unknown),
                    timeCuts.Count,witnessErrors.Length+(identity.IsValid?0:1),
                    slave.StationResults.Count==instance.TrainStops.Select(s=>s.StationName).Distinct(StringComparer.OrdinalIgnoreCase).Count() &&
                    independent.Status!=MicroSolveStatus.Unknown && remaining()>0);
                if(promoteWitnessFailures && !fixedCheck.IsValid && !deferPromotion)
                {
                    // A rejected saved assignment is not a proof that every assignment is infeasible.
                    // Retain the complete original station domain as restricted-search guidance only.
                    var witnessStations=instance.Stations.Where(station=>fixedCheck.Findings.Any(f=>
                        f.Detail.Contains($"station='{station}'",StringComparison.OrdinalIgnoreCase)) && !preciseCoreStations.Contains(station)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    conflictStops.UnionWith(instance.TrainStops.Where(s=>witnessStations.Contains(s.StationName)).Select(s=>s.StopId));
                    foreach(string station in witnessStations)
                    {
                        if(remaining()<=0 || repairs.Count>=promotionSlots) break;
                        if(repairs.Any(c=>c.StationName.Equals(station,StringComparison.OrdinalIgnoreCase))) continue;
                        var build=ValidityFirstHotspotComponentBuilder.BuildCompleteStationComponent(model,station,StationEndpointOccupationPolicy.DefaultSeconds,round,
                            "directed_saved_witness_repair_restricted_search");
                        var estimate=build.Component is null?null:PromotedMicroCompactRelationEstimator.Estimate(build.Component.Requirements,
                            (a,b)=>instance.TryGetRouteHeadwayRule(a,b,out _));
                        bool supported=build.IsComplete && build.Component is not null && build.AlternativeCount<=maximumAlternatives &&
                            estimate is not null && estimate.ConditionalConstraintCount<=maximumConditionalRows;
                        repairAudit.Add(new { station,reason="saved_witness_rejected",build.IsComplete,build.AlternativeCount,estimate,supported,
                            scope="RestrictedSearch",globalInfeasibilityCut=false });
                        if(supported) { repairs.Add(build.Component!);conflicts.UnionWith(build.Component!.TrainIndices); }
                    }
                }
                evidence=new { status=slave.Status,conflicts=slave.Conflicts,unknownStations=slave.StationResults.Count(s=>s.AssignmentResult.Status==MicroSolveStatus.Unknown),
                    independentStatus=independent.Status,independentConflicts=independent.Conflicts,assignments=assignments.Count,identity,fixedCheck,repairAudit,
                    timeCuts,deferPromotion,jointProgress,feedbackSourceFingerprint=fingerprint };
                if(slave.Status==MicroSolveStatus.Feasible && slave.Conflicts.Count==0 && independent.Status==MicroSolveStatus.Feasible &&
                    independent.IsFeasible && independent.Conflicts.Count==0 && fixedCheck.IsValid && identity.IsValid &&
                    assignments.Count==instance.TrainStops.Count && remaining()>0)
                    joint=canonical with { VerifiedMicroAssignments=assignments,SolutionRole=TimetableSolutionRole.BestJointFeasibleSolution,
                        ServiceObjective=ServiceBusinessObjectiveEvaluator.Calculate(instance,canonical,policy),BoundReport=null };
            });
        Stages45Runner.Write(output,$"round_{round:D3}_complete.json",macro with { Snapshot=null });
        Stages45Runner.Write(output,$"round_{round:D3}_micro.json",evidence);
        if(macro.Snapshot is null) return Result(false,false,macro.Status,null,null);
        if(joint is null) return Result(true,false,remaining()>0?"UnknownOrConflictingMicro":"UnknownJointBudget",macro.Snapshot,null);
        string path=Path.Combine(output,$"joint_candidate_{round:D3}.snapshot.json");
        MasterSolutionSnapshotStore.Save(path,instance.Meta.InstanceName,joint);
        if(remaining()<=0) return Result(true,false,"UnknownBeforeReadback",macro.Snapshot,null);
        try
        {
            var readback=SavedJointSnapshotVerifier.Run(instance,path,Path.Combine(output,$"readback_{round:D3}"),.05,StationEndpointOccupationPolicy.DefaultSeconds,remaining,policy);
            var loaded=MasterSolutionSnapshotStore.Load(path,instance.Meta.InstanceName,instance.Trains.Select(t=>t.TrainIndex).ToHashSet());
            var expected=ServiceBusinessObjectiveEvaluator.Calculate(instance,joint,policy);
            bool sameObjective=loaded.ServiceObjective==expected && ServiceBusinessObjectiveEvaluator.Calculate(instance,loaded,policy)==expected;
            bool accepted=readback.Passed && sameObjective && remaining()>0;
            return Result(true,accepted,accepted?"JointFeasible":"UnknownReadbackRejected",accepted?loaded:macro.Snapshot,readback);
        }
        catch(TimeoutException) { return Result(true,false,"UnknownReadbackBudget",macro.Snapshot,null); }
        catch(BoundedSnapshotCandidateRejectedException) { return Result(true,false,"UnknownFixedSavedCandidateRejected",macro.Snapshot,null); }
        Stages45JointProof Result(bool complete,bool accepted,string status,MasterSolutionSnapshot? snapshot,SavedJointSnapshotVerificationResult? readback)
            =>new(complete,accepted,status,snapshot,conflicts,evidence,readback,repairs,clock.Elapsed.TotalSeconds,conflictStops,timeCuts,jointProgress);
    }
}
