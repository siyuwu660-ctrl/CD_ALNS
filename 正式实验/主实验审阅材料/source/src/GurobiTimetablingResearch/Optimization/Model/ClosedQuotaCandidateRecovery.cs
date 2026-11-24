using Gurobi;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

/// <summary>Candidate-only recovery. Bounds from this restricted problem are never global bounds.</summary>
internal static class ClosedQuotaCandidateRecovery
{
    internal static bool HasViolation(TimetablingInstance instance, MasterSolutionSnapshot candidate) =>
        instance.StationStopQuotaRules.Any(rule =>
        {
            int count = Matching(instance, rule.StationName, rule.TrainType).Count(stop =>
                candidate.StopByStopId[stop.StopId] == 1 && StationQuotaWindowPolicy.Contains(
                    EventTime(stop, candidate), rule.StartTimeSeconds, rule.EndTimeSeconds));
            return count < rule.MinStops || count > rule.MaxStops;
        });

    internal static MasterSolutionSnapshot? TryRecover(TimetablingInstance instance, MasterSolutionSnapshot candidate,
        ServiceWindowPolicy policy, Func<double> remaining, double seconds, ISet<string> attemptedSignatures, string logPath)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        double budget = Math.Min(seconds, Math.Min(30, Math.Max(0, remaining()) * .5));
        double Left() => Math.Min(budget - clock.Elapsed.TotalSeconds, remaining());
        if (Left() <= 0 || !HasViolation(instance, candidate)) return null;
        // Closed endpoints share the verifier's numeric tolerance; no business offset.
        var boundaries = (from entry in instance.StationStopQuotaRules.Select((rule,index)=>(Rule:rule,Id:$"station-stop-{index+1:D3}"))
                          let rule = entry.Rule
                          from stop in Matching(instance, rule.StationName, rule.TrainType)
                          let time = EventTime(stop, candidate)
                          where candidate.StopByStopId[stop.StopId] == 1 &&
                              (Math.Abs(time-rule.StartTimeSeconds) <= TimetableTimePolicy.NumericToleranceSeconds ||
                               Math.Abs(time-rule.EndTimeSeconds) <= TimetableTimePolicy.NumericToleranceSeconds)
                          select (RuleId:entry.Id, stop.StopId)).ToHashSet();
        if (boundaries.Count == 0) return null;
        string signature = SnapshotFingerprint.Calculate(candidate) + "|" + string.Join("|",
            boundaries.OrderBy(x=>x.RuleId,StringComparer.Ordinal).ThenBy(x=>x.StopId,StringComparer.Ordinal)
                .Select(x=>x.RuleId+"::"+x.StopId+"=inside_if_stop"));
        if (!attemptedSignatures.Add(signature)) return null;
        // One append-only journal deduplicates across verification rounds without
        // generating a separate marker/log file for every rejected candidate.
        string key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(signature)));
        string directory = Path.GetDirectoryName(logPath)!;
        string journal = Path.Combine(directory,"closed_quota_recovery.jsonl");
        if (File.Exists(journal))
            foreach (string line in File.ReadLines(journal))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var entry = System.Text.Json.JsonDocument.Parse(line);
                if (entry.RootElement.TryGetProperty("key",out var saved) && saved.GetString()==key) return null;
            }
        File.AppendAllText(journal,System.Text.Json.JsonSerializer.Serialize(new {key,budget,
            sourceFingerprint=SnapshotFingerprint.Calculate(candidate),boundaryCount=boundaries.Count,
            scope="RestrictedCandidateRecovery",globalBound=false})+Environment.NewLine);
        if (Left() <= 0) return null;
        using var recovery = new BasicMilpModelBuilder(instance, neighborRadius:int.MaxValue,
            serviceWindows:policy, screenTemporalPrecedence:true).Build();
        foreach (var binding in recovery.StationQuotaCategoryBindings)
        {
            if (!boundaries.Contains((binding.RuleId,binding.StopId))) continue;
            // Fix only the boundary category conditional on stopping. Other events may
            // move across windows and stop choices may compensate the actual quota.
            recovery.Model.AddConstr(binding.InWindowVariable-recovery.StopVars[binding.StopId],
                GRB.EQUAL,0,"closed_recovery_"+binding.BindingId);
        }
        MasterSolutionSnapshotApplier.ApplyExpandableMacroMipStart(recovery,candidate);
        recovery.Model.Update();
        if (Left() <= 0) return null;
        ResourceLocalSolver.ConfigureNumerics(recovery.Model);
        recovery.Model.Set(GRB.IntParam.OutputFlag,1);
        recovery.Model.Set(GRB.IntParam.LogToConsole,0);
        recovery.Model.Set(GRB.StringParam.LogFile,Path.Combine(directory,"closed_quota_recovery.log"));
        recovery.Model.Set(GRB.IntParam.SolutionLimit,1);
        recovery.Model.Set(GRB.DoubleParam.TimeLimit,Left());
        recovery.Optimize();
        if (!recovery.HasIncumbentSolution || Left() <= 0) return null;
        var validation = new MasterSolutionValidator().Validate(recovery);
        if (!validation.IsFeasible || validation.Violations.Count != 0) return null;
        var recovered = MasterSolutionSnapshot.Capture(recovery);
        return Left() <= 0 || HasViolation(instance,recovered) ? null : recovered;
    }

    // Compiled local models retain named quota variables but not binding metadata.
    // Add original category equations locally, before fixing integer decisions for polish.
    internal static int RestrictLocalBoundaryCategories(GRBModel model,TimetablingInstance instance,
        MasterSolutionSnapshot candidate)
    {
        if(!HasViolation(instance,candidate))return 0;
        int added=0;
        foreach(var rule in instance.StationStopQuotaRules)
        foreach(var stop in Matching(instance,rule.StationName,rule.TrainType))
        {
            if(candidate.StopByStopId[stop.StopId]!=1)continue;
            double time=EventTime(stop,candidate);
            if(Math.Abs(time-rule.StartTimeSeconds)>TimetableTimePolicy.NumericToleranceSeconds &&
                Math.Abs(time-rule.EndTimeSeconds)>TimetableTimePolicy.NumericToleranceSeconds)continue;
            string suffix=$"t{stop.TrainIndex}_s{stop.StationOrder}_{rule.StartTimeSeconds}_{rule.EndTimeSeconds}";
            var inside=model.GetVarByName("quota_inwin_"+suffix);
            // A projected-out constant requires the separate complete recovery at verification.
            var stopped=model.GetVarByName("stop_t"+stop.TrainIndex+"_s"+stop.StationOrder);
            if(inside is null || stopped is null)continue;
            model.AddConstr(inside-stopped,GRB.EQUAL,0,"local_closed_recovery_"+added++);
        }
        return added;
    }

    private static IEnumerable<TrainStopRecord> Matching(TimetablingInstance instance,string station,string trainType) =>
        instance.TrainStops.Where(s=>s.StationName.Equals(station,StringComparison.OrdinalIgnoreCase) &&
            s.TrainType.Equals(trainType,StringComparison.OrdinalIgnoreCase));
    private static double EventTime(TrainStopRecord stop,MasterSolutionSnapshot candidate) =>
        (stop.StopRole==TrainStopRole.Origin?candidate.DepartureSecondsByStopId:candidate.ArrivalSecondsByStopId)[stop.StopId];
}
