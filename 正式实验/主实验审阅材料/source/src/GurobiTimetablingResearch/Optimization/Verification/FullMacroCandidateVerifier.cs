using System.Diagnostics;
using Gurobi;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

internal sealed record FullMacroCandidateVerification(string Status,double Seconds,int CompleteRelations,int Variables,int Rows,
    string SourceFingerprint,string? CompleteFingerprint,MasterValidationReport? Validation,MasterSolutionSnapshot? Snapshot);

/// <summary>Candidate-only complete carrier, not a global feasibility/optimality proof when it fails.</summary>
internal static class FullMacroCandidateVerifier
{
    internal static FullMacroCandidateVerification Verify(TimetablingInstance instance,MasterSolutionSnapshot candidate,
        ServiceWindowPolicy policy,Func<double> remainingSeconds,double solveSeconds,string logPath,
        Action<BasicMilpModel,MasterSolutionSnapshot>? onVerified=null)
    {
        Stopwatch clock=Stopwatch.StartNew();
        string sourceFingerprint=SnapshotFingerprint.Calculate(candidate);
        if(remainingSeconds()<=0) return new("UnknownBudgetBeforeCompleteCarrier",clock.Elapsed.TotalSeconds,0,0,0,sourceFingerprint,null,null,null);
        using BasicMilpModel complete=new BasicMilpModelBuilder(instance,neighborRadius:int.MaxValue,
            serviceWindows:policy,screenTemporalPrecedence:true,
            stationEventEdges:SavedJointSnapshotVerifier.ResolveSavedStationEdges(instance,candidate)).Build();
        complete.Model.Set(GRB.IntParam.OutputFlag,0);
        // Preserve every original bound. Only this candidate's physical decisions and existing order witnesses are fixed.
        FixAll(complete.ArrivalVars,candidate.ArrivalSecondsByStopId,"arrival");
        FixAll(complete.DepartureVars,candidate.DepartureSecondsByStopId,"departure");
        FixAll(complete.StopVars,candidate.StopByStopId.ToDictionary(p=>p.Key,p=>(double)p.Value),"stop");
        FixAll(complete.RunningTimeSupplementVars,candidate.RunningTimeSupplementSecondsByKey ??
            throw new InvalidDataException("Complete carrier requires all running-time supplements."),"supplement");
        FixExisting(complete.SectionPrecedenceVars,candidate.SectionPrecedenceByKey,"section_order");
        FixExisting(complete.StationEventPrecedenceVars,candidate.StationEventPrecedenceByKey,"station_order");
        // Missing/tied orders get starts, never hard orders derived from reference order.
        MasterSolutionSnapshotApplier.ApplyExpandableMacroMipStart(complete,candidate);
        complete.Model.Update();
        int variables=complete.Model.NumVars,rows=complete.Model.NumConstrs,relations=complete.SectionConflictCandidates.Count;
        if(remainingSeconds()<=0) return Result("UnknownBudgetDuringCompleteCarrierBuild");
        complete.Model.Set(GRB.DoubleParam.TimeLimit,Math.Min(solveSeconds,Math.Max(0.001,remainingSeconds())));
        complete.Model.Set(GRB.IntParam.Threads,4);
        complete.Model.Set(GRB.IntParam.Seed,0);
        complete.Model.Set(GRB.IntParam.MIPFocus,1);
        complete.Model.Set(GRB.IntParam.SolutionLimit,1);
        complete.Model.Set(GRB.IntParam.OutputFlag,1);
        complete.Model.Set(GRB.IntParam.LogToConsole,0);
        complete.Model.Set(GRB.StringParam.LogFile,logPath);
        complete.Optimize();
        if(!complete.HasIncumbentSolution)
            return Result(complete.Model.Status==GRB.Status.INFEASIBLE?"UnknownCompleteCandidateRejected":"UnknownCompleteCarrierNoIncumbent");
        MasterValidationReport validation=new MasterSolutionValidator().Validate(complete);
        if(!validation.IsFeasible || validation.Violations.Count!=0) return Result("UnknownCompleteCarrierViolation",validation);
        MasterSolutionSnapshot captured=MasterSolutionSnapshot.Capture(complete);
        Same(candidate.ArrivalSecondsByStopId,captured.ArrivalSecondsByStopId);
        Same(candidate.DepartureSecondsByStopId,captured.DepartureSecondsByStopId);
        Same(candidate.RunningTimeSupplementSecondsByKey!,captured.RunningTimeSupplementSecondsByKey!);
        if(candidate.StopByStopId.Any(p=>captured.StopByStopId[p.Key]!=p.Value)) throw new InvalidDataException("Complete carrier changed stops.");
        // Canonicalize metadata, not timetable: preserve exact input times/supplements after the equality carrier verifies them.
        MasterSolutionSnapshot canonical=candidate with {
            SectionPrecedenceByKey=captured.SectionPrecedenceByKey,
            StationEventPrecedenceByKey=captured.StationEventPrecedenceByKey,
            ServiceObjective=ServiceBusinessObjectiveEvaluator.Calculate(instance,candidate,policy)
        };
        // The complete carrier has already proved this exact physical candidate valid.
        // An exhausted micro/readback budget cannot revoke that completed macro proof.
        if(remainingSeconds()<=0) return Result("UnknownBudgetDuringCompleteCarrierCheck",validation,canonical);
        onVerified?.Invoke(complete,canonical);
        if(remainingSeconds()<=0) return Result("UnknownBudgetDuringJointCheck",validation,canonical);
        return Result("CompleteMacroFeasible",validation,canonical);

        FullMacroCandidateVerification Result(string status,MasterValidationReport? report=null,MasterSolutionSnapshot? snapshot=null)
            =>new(status,clock.Elapsed.TotalSeconds,relations,variables,rows,sourceFingerprint,
                snapshot is null?null:SnapshotFingerprint.Calculate(snapshot),report,snapshot);

        void FixAll(IReadOnlyDictionary<string,GRBVar> variables,IReadOnlyDictionary<string,double> values,string family)
        {
            if(variables.Count!=values.Count || variables.Keys.Any(k=>!values.ContainsKey(k)))
                throw new InvalidDataException($"Incomplete candidate {family} keys.");
            foreach(var pair in values)
            {
                if(!double.IsFinite(pair.Value)) throw new InvalidDataException($"Invalid candidate {family} value.");
                complete.Model.AddConstr(variables[pair.Key],GRB.EQUAL,pair.Value,$"complete_candidate::{family}::{pair.Key}");
            }
        }
        void FixExisting(IReadOnlyDictionary<string,GRBVar> variables,IReadOnlyDictionary<string,int> values,string family)
        {
            foreach(var pair in values)
            {
                if(pair.Value is not (0 or 1) || !variables.TryGetValue(pair.Key,out GRBVar? variable))
                    throw new InvalidDataException($"Invalid candidate {family} witness: {pair.Key}.");
                complete.Model.AddConstr(variable,GRB.EQUAL,pair.Value,$"complete_candidate::{family}::{pair.Key}");
            }
        }
        static void Same(IReadOnlyDictionary<string,double> input,IReadOnlyDictionary<string,double> actual)
        {
            if(input.Count!=actual.Count || input.Any(p=>!actual.TryGetValue(p.Key,out double value) || Math.Abs(value-p.Value)>1e-6))
                throw new InvalidDataException("Complete carrier changed candidate physical decisions.");
        }
    }
}
