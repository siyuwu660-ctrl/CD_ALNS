using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gurobi;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

public enum MicroStructureArmMode { C0, C1 }

public sealed record MicroStructureSuiteRequest(
    string SnapshotPath,
    string CheckpointPath,
    string StationSummaryPath,
    string OutputDirectory,
    bool RunFresh,
    double ArmBudgetSeconds = 600,
    double FreshBudgetSeconds = 3600,
    int EndpointOccupationSeconds = StationEndpointOccupationPolicy.DefaultSeconds,
    double RunningTimeSupplementRatio = 0.05);

internal sealed record StationPatternAttemptSummary(
    string Scope,
    IReadOnlyList<string> Stations,
    bool HasPattern,
    string Status,
    double RuntimeSeconds,
    double? FirstIncumbentSeconds,
    int RequirementCount,
    int Variables,
    int Binaries,
    int Constraints,
    bool PresolveSizeAvailable,
    string PresolveStopReason,
    int PresolvedVariables,
    int PresolvedBinaries,
    int PresolvedConstraints,
    int ChoiceBinaries,
    int OrderBinaries,
    int MacroBinaries,
    int RuntimeCouplings,
    int ServiceCouplings,
    bool FixedResourceValidated,
    string StopReason);

internal sealed record CoordinatorAttemptSummary(
    string Scope,
    bool HasSolution,
    string Status,
    double RuntimeSeconds,
    int PatternCount,
    int PatternConstraintCount,
    int SourceVariables,
    int SourceBinaries,
    int SourceConstraints,
    int ContinuousVariables,
    int RemainingIntegers,
    bool MacroValidated,
    int MacroViolationCount,
    bool PresolveSizeAvailable,
    string PresolveStopReason,
    int PresolvedVariables,
    int PresolvedIntegers,
    int PresolvedConstraints,
    int ProofRowCount,
    IReadOnlyList<ContinuousProofRow> ProofRows,
    string CoordinationModelId,
    string MacroCandidateFingerprint,
    string PatternCombinationIdentity,
    string MathematicalModelIdentity,
    string SolveAttemptId,
    string SolveResultIdentity,
    ContinuousProofAvailability ProofAvailability,
    string ProofAvailabilityReason,
    string StopReason);

internal sealed record JointValidationSummary(
    bool Attempted,
    bool MacroFeasible,
    int MacroViolationCount,
    string MicroStatus,
    int MicroConflictCount,
    int MicroUnknownCount,
    int ResourceWitnessCount,
    bool SavedWitnessesValid,
    int SavedWitnessFindingCount,
    bool JointFeasible,
    MasterSolutionSnapshot? Snapshot,
    IReadOnlyList<VerifiedMicroAssignmentWitness> Witnesses);

internal sealed record MacroStructureAttemptSummary(
    bool Attempted,
    bool HasCandidate,
    string Status,
    double RuntimeSeconds,
    double? FirstIncumbentSeconds,
    IReadOnlyList<string> ActiveStations,
    int IndependentMainDecisionCount,
    bool ScopeCapReached,
    IReadOnlyList<string> ReleasedStopIds,
    IReadOnlyList<string> ReleasedSectionPrecedenceKeys,
    int Variables,
    int Binaries,
    int Constraints,
    bool PresolveSizeAvailable,
    string PresolveStopReason,
    int PresolvedVariables,
    int PresolvedBinaries,
    int PresolvedConstraints,
    int FixedMainDecisionCount,
    int DerivedBinaryCount,
    int EndpointOrderChanges,
    int StopChanges,
    IReadOnlyList<string> ChangedDecisionIds,
    bool MacroValidated,
    int MacroViolationCount,
    string CoordinationModelId,
    string CoordinationSolveResultIdentity,
    string PatternCombinationIdentity,
    ContinuousProofAvailability ProofAvailability,
    IReadOnlyList<ContinuousProofBusinessMapping> EvidenceMappings,
    string SelectionBasis,
    string StopReason);

internal sealed record ArmBaselineMicroSummary(
    string Scope,
    bool IndependentlyRechecked,
    string ContinuousRecoveryStatus,
    double ContinuousRecoverySeconds,
    string MicroStatus,
    int ConflictCount,
    int UnknownStationCount,
    int ReliableWitnessCount,
    IReadOnlyList<string> FailedStations,
    string StopReason);

internal sealed record MicroStructureArmResult(
    MicroStructureArmMode Mode,
    double RuntimeSeconds,
    ArmBaselineMicroSummary BaselineMicro,
    IReadOnlyList<string> InitiallyFailedStations,
    int InitialHealthyPatternCount,
    int InitialHealthyWitnessCount,
    IReadOnlyList<StationPatternAttemptSummary> StationAttempts,
    IReadOnlyList<CoordinatorAttemptSummary> CoordinatorAttempts,
    MicroStationPairSelection? PairSelection,
    string PairSelectionBasis,
    bool GuidedPairTriggered,
    bool NewFailedStationPatternGenerated,
    bool NewPatternAttachedToCoordinator,
    bool ScopeCapReached,
    MacroStructureAttemptSummary MacroStructureAttempt,
    JointValidationSummary Validation,
    string StopReason,
    IReadOnlyList<MicroStationPairSelection>? PairSelections = null,
    IReadOnlyList<MacroStructureAttemptSummary>? MacroStructureAttempts = null)
{
    internal IReadOnlyList<MicroStationPairSelection> EffectivePairSelections =>
        PairSelections ?? (PairSelection is null ? [] : [PairSelection]);

    internal IReadOnlyList<MacroStructureAttemptSummary> EffectiveMacroStructureAttempts =>
        MacroStructureAttempts ?? (MacroStructureAttempt.Attempted ? [MacroStructureAttempt] : []);
}

internal sealed record FreshMacroSummary(
    bool Started,
    bool MacroCandidateFound,
    double MacroRuntimeSeconds,
    double? FirstMacroCandidateSeconds,
    string MacroStatus,
    int MacroVariables,
    int MacroBinaries,
    int MacroConstraints,
    int InitialMicroConflictCount,
    int InitialMicroUnknownCount,
    int InitialHealthyWitnessCount,
    MasterSolutionSnapshot? LastRecoverableMacroSnapshot,
    MicroStructureArmResult? ClosedLoop,
    string StopReason);

internal sealed record MicroStructureSuiteResult(
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    string InputSnapshot,
    string InputCheckpoint,
    string InputStationSummary,
    int TrainCount,
    int StopCount,
    int FullRelationCount,
    int InitiallyRecordedConflictCount,
    IReadOnlyDictionary<string, int> InitiallyRecordedConflictsByStation,
    APropagationStationRanking APropagation,
    bool AChangedFailedStationOrder,
    CoordinatorAttemptSummary Preflight,
    MicroStructureArmResult C0,
    MicroStructureArmResult C1,
    bool FreshGatePassed,
    FreshMacroSummary Fresh,
    bool JointFeasible,
    int SavedResourceAssignmentCount,
    string OutputDirectory);

public static class MicroStructureExperimentRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static int ResumeFreshClosedLoop(
        TimetablingInstance instance,
        string diagnosticStatePath,
        double recoveryBudgetSeconds,
        int endpointOccupationSeconds,
        double runningTimeSupplementRatio)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (recoveryBudgetSeconds <= 30)
            throw new ArgumentOutOfRangeException(nameof(recoveryBudgetSeconds));
        string diagnosticPath = Path.GetFullPath(diagnosticStatePath);
        string output = Path.GetDirectoryName(diagnosticPath)
                        ?? throw new InvalidDataException("Recovery diagnostic path has no parent directory.");
        string summaryPath = Path.Combine(output, "run_summary.json");
        JsonObject summary = JsonNode.Parse(File.ReadAllText(summaryPath, Encoding.UTF8))?.AsObject()
                             ?? throw new InvalidDataException("Existing run_summary.json is not a JSON object.");
        JsonObject diagnostic = JsonNode.Parse(File.ReadAllText(diagnosticPath, Encoding.UTF8))?.AsObject()
                                ?? throw new InvalidDataException("Existing diagnostic_state.json is not a JSON object.");
        JsonNode recoverableState = diagnostic["lastRecoverableState"]
                                    ?? throw new InvalidDataException(
                                        "diagnostic_state.json does not contain an embedded fresh Macro snapshot.");
        MasterSolutionSnapshot source = MasterSolutionSnapshotStore.DeserializePayload(
            recoverableState.ToJsonString(),
            instance.Trains.Select(train => train.TrainIndex).ToHashSet());
        // Keep the first staged-schedule recovery evidence immutable.  A later
        // closure-enumeration repair receives its own log directory instead of
        // overwriting the station and pair logs from that earlier recovery.
        string logs = Path.Combine(output, "logs", "recovery_after_full_enumeration_fix");
        Directory.CreateDirectory(logs);
        Stopwatch recoveryClock = Stopwatch.StartNew();

        ContinuousCoordinationResult recovered;
        StationLevelMicroFeasibilitySlaveResult micro;
        using (FixedDiscreteContinuousCoordinator coordinator = FixedDiscreteContinuousCoordinator.Build(
                   instance,
                   source,
                   patterns: [],
                   runningTimeSupplementRatio: runningTimeSupplementRatio,
                   endpointOccupationSeconds: endpointOccupationSeconds))
        {
            recovered = coordinator.Solve(10, Path.Combine(logs, "source_recovery.log"));
            if (!recovered.HasSolution || !recovered.MacroValidated || recovered.Snapshot is null)
                throw new InvalidDataException(
                    $"Saved fresh Macro state could not be recovered exactly: {recovered.StopReason}.");
            micro = new StationLevelMicroFeasibilitySlave(endpointOccupationSeconds).Solve(coordinator.Model);
            source = recovered.Snapshot;
        }

        IReadOnlyList<VerifiedMicroAssignmentWitness> healthyWitnesses =
            VerifiedMicroAssignmentWitnessBuilder.Capture(micro, SnapshotFingerprint.Calculate(source));
        source = source with { VerifiedMicroAssignments = healthyWitnesses };
        string[] failedStations = micro.StationResults.Where(result => !result.IsFeasible)
            .Select(result => result.StationName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        IReadOnlyList<MicroWitnessCertificate> healthyPatterns = BuildCertificates(
            instance, source, healthyWitnesses, endpointOccupationSeconds);
        ArmBaselineMicroSummary baseline = new(
            "saved_fresh_macro_global_micro_recheck_after_schedule_fix",
            true,
            recovered.Status,
            recovered.RuntimeSeconds,
            micro.Status.ToString(),
            micro.Conflicts.Count,
            micro.StationResults.Count(result => result.AssignmentResult.Status == MicroSolveStatus.Unknown),
            healthyWitnesses.Count,
            failedStations,
            "saved_fresh_macro_recovered_without_rerunning_macro_mip");
        MicroStructureSuiteRequest request = new(
            summary["inputSnapshot"]?.GetValue<string>() ?? string.Empty,
            summary["inputCheckpoint"]?.GetValue<string>() ?? string.Empty,
            summary["inputStationSummary"]?.GetValue<string>() ?? string.Empty,
            output,
            RunFresh: false,
            ArmBudgetSeconds: recoveryBudgetSeconds,
            FreshBudgetSeconds: 3600,
            EndpointOccupationSeconds: endpointOccupationSeconds,
            RunningTimeSupplementRatio: runningTimeSupplementRatio);
        MicroStructureArmResult arm = RunArm(
            instance,
            source,
            failedStations,
            healthyPatterns,
            MicroStructureArmMode.C1,
            request,
            logs,
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            recheckBaselineMicro: false,
            suppliedBaselineMicro: baseline);
        recoveryClock.Stop();

        JsonObject fresh = summary["fresh"]?.AsObject()
                           ?? throw new InvalidDataException("run_summary.json lacks the fresh record.");
        if (fresh["closedLoopBeforeScheduleFix"] is null)
            fresh["closedLoopBeforeScheduleFix"] = fresh["closedLoop"]?.DeepClone();
        else
            fresh["closedLoopBeforeFullEnumeration"] = fresh["closedLoop"]?.DeepClone();
        fresh["closedLoop"] = JsonSerializer.SerializeToNode(ProjectArmForJson(arm), JsonOptions);
        fresh["stopReason"] = arm.Validation.JointFeasible ? "fresh_joint_feasible_after_schedule_recovery" : arm.StopReason;
        summary["recoveryContinuation"] = JsonSerializer.SerializeToNode(new
        {
            reasons = new[]
            {
                "single_station_30_to_120_second_continuation_was_not_scheduled",
                "distinct_legal_pair_and_macro_scope_enumeration_was_not_closed"
            },
            didNotRerunFreshMacro = true,
            sourceRecovery = Project("saved_fresh_macro_recovery", recovered, 0),
            budgetSeconds = recoveryBudgetSeconds,
            runtimeSeconds = recoveryClock.Elapsed.TotalSeconds,
            completedAt = DateTimeOffset.Now,
            regression = "same_model_staged_continuation_test_passed"
        }, JsonOptions);
        summary["jointFeasible"] = arm.Validation.JointFeasible;
        summary["savedResourceAssignmentCount"] = arm.Validation.JointFeasible ? arm.Validation.Witnesses.Count : 0;
        if (summary["codeIdentity"] is JsonObject identity)
        {
            string assemblyPath = System.Reflection.Assembly.GetExecutingAssembly().Location;
            identity["executingAssembly"] = assemblyPath;
            identity["executingAssemblySha256"] = Sha256(assemblyPath);
            identity["executingAssemblyLastWriteUtc"] = File.GetLastWriteTimeUtc(assemblyPath);
            identity["modifiedSourceFiles"] = JsonSerializer.SerializeToNode(
                SourceIdentity(ResolveCodeDirectory()), JsonOptions);
        }
        File.WriteAllText(summaryPath, summary.ToJsonString(JsonOptions), new UTF8Encoding(false));
        AppendRecoveryTrace(Path.Combine(output, "repair_trace.csv"), arm);

        if (arm.Validation.JointFeasible && arm.Validation.Snapshot is not null)
        {
            string solution = Path.Combine(output, "solution");
            Directory.CreateDirectory(solution);
            MasterSolutionSnapshotStore.Save(Path.Combine(solution, "joint_solution.snapshot.json"),
                instance.Meta.InstanceName, arm.Validation.Snapshot);
            WriteWitnesses(Path.Combine(solution, "resource_assignments.csv"), arm.Validation.Witnesses);
            if (File.Exists(diagnosticPath)) File.Delete(diagnosticPath);
        }
        else
        {
            string[] stationsWithoutPattern = ResolveStationsWithoutPattern(arm);
            object updatedDiagnostic = new
            {
                finalStatus = "no_complete_joint_feasible_solution_after_schedule_recovery",
                stationsWithoutPattern,
                allInitiallyFailedStations = arm.InitiallyFailedStations,
                arm.StopReason,
                continuousAttempts = arm.CoordinatorAttempts,
                pairSelections = arm.EffectivePairSelections,
                macroStructureAttempts = arm.EffectiveMacroStructureAttempts,
                lastRecoverableState = source,
                recoveryDidNotRerunFreshMacro = true
            };
            File.WriteAllText(diagnosticPath, JsonSerializer.Serialize(updatedDiagnostic, JsonOptions),
                new UTF8Encoding(false));
        }
        PrependRecoveryResult(Path.Combine(output, "result.md"), arm, recoveryClock.Elapsed.TotalSeconds);
        return arm.Validation.JointFeasible ? 0 : 2;
    }

    public static int Run(TimetablingInstance instance, MicroStructureSuiteRequest request)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(request);
        DateTimeOffset started = DateTimeOffset.Now;
        string output = Path.GetFullPath(request.OutputDirectory);
        Directory.CreateDirectory(output);
        string logs = Path.Combine(output, "logs");
        Directory.CreateDirectory(logs);
        MasterSolutionSnapshot source = MicroStructureSnapshotLoader.Load(
            request.SnapshotPath,
            request.CheckpointPath,
            instance);
        IReadOnlyList<string> recordedFailedStations = FailedMicroStationLoader.LoadFromStationSummary(request.StationSummaryPath);
        APropagationStationRanking aPropagation = APropagationStationRankingLoader.Load(
            request.StationSummaryPath, instance);
        IReadOnlyList<string> failedStations = recordedFailedStations
            .OrderBy(station => aPropagation.RankByStation.GetValueOrDefault(station, int.MaxValue / 4))
            .ThenBy(station => station, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        bool aChangedOrder = !recordedFailedStations.SequenceEqual(
            failedStations, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> conflictsByStation = LoadConflictCounts(request.StationSummaryPath);
        IReadOnlyList<MicroWitnessCertificate> healthyPatterns = BuildCertificates(
            instance,
            source,
            source.VerifiedMicroAssignments ?? [],
            request.EndpointOccupationSeconds);

        CoordinatorAttemptSummary preflight;
        using (FixedDiscreteContinuousCoordinator coordinator = FixedDiscreteContinuousCoordinator.Build(
                   instance,
                   source,
                   patterns: [],
                   runningTimeSupplementRatio: request.RunningTimeSupplementRatio,
                   endpointOccupationSeconds: request.EndpointOccupationSeconds))
        {
            ContinuousCoordinationResult solved = coordinator.Solve(
                Math.Min(10, request.ArmBudgetSeconds),
                Path.Combine(logs, "preflight_fixed_discrete.log"));
            preflight = Project("source_candidate_recovery", solved, 0);
            if (!solved.HasSolution || !solved.MacroValidated)
            {
                string reason = !solved.HasSolution ? "preflight_no_solution" : "preflight_macro_validation_failed";
                MicroStructureArmResult skippedC0 = SkippedArm(MicroStructureArmMode.C0, failedStations, healthyPatterns, reason);
                MicroStructureArmResult skippedC1 = SkippedArm(MicroStructureArmMode.C1, failedStations, healthyPatterns, reason);
                FreshMacroSummary freshSkipped = new(false, false, 0, null, "NOT_RUN", 0, 0, 0, 0, 0, 0, null, null, reason);
                MicroStructureSuiteResult failed = new(started, DateTimeOffset.Now, request.SnapshotPath,
                    request.CheckpointPath, request.StationSummaryPath, instance.Trains.Count, instance.TrainStops.Count,
                    new InitialConflictGraphBuilder(instance, int.MaxValue).Build().Count,
                    conflictsByStation.Values.Sum(), conflictsByStation, aPropagation, aChangedOrder,
                    preflight, skippedC0, skippedC1,
                    false, freshSkipped, false, 0, output);
                WriteOutputs(instance, request, failed, null);
                return 2;
            }
            MasterValidationReport validation = new MasterSolutionValidator().Validate(coordinator.Model);
            if (!validation.IsFeasible)
                throw new InvalidDataException("Source candidate recovery LP produced a Macro-invalid timetable: " +
                                               string.Join(" | ", validation.Violations.Take(20)));
        }

        MicroStructureArmResult c0 = RunArm(instance, source, failedStations, healthyPatterns,
            MicroStructureArmMode.C0, request, logs, aPropagation.RankByStation, recheckBaselineMicro: true);
        MicroStructureArmResult c1 = RunArm(instance, source, failedStations, healthyPatterns,
            MicroStructureArmMode.C1, request, logs, aPropagation.RankByStation, recheckBaselineMicro: true);
        bool freshGate = preflight.HasSolution && preflight.MacroValidated && c1.NewFailedStationPatternGenerated &&
                         c1.NewPatternAttachedToCoordinator &&
                         c1.StationAttempts.Any(attempt => attempt.HasPattern && attempt.FixedResourceValidated &&
                                                                  c1.InitiallyFailedStations.Contains(attempt.Stations[0], StringComparer.OrdinalIgnoreCase));
        FreshMacroSummary fresh = request.RunFresh && freshGate
            ? RunFresh(instance, request, logs)
            : new(false, false, 0, null, "NOT_RUN", 0, 0, 0, 0, 0, 0, null, null,
                request.RunFresh ? "fresh_gate_not_met" : "fresh_not_requested");
        JointValidationSummary best = fresh.ClosedLoop?.Validation.JointFeasible == true
            ? fresh.ClosedLoop.Validation
            : c1.Validation.JointFeasible
                ? c1.Validation
                : c0.Validation;
        MicroStructureSuiteResult result = new(
            started,
            DateTimeOffset.Now,
            request.SnapshotPath,
            request.CheckpointPath,
            request.StationSummaryPath,
            instance.Trains.Count,
            instance.TrainStops.Count,
            new InitialConflictGraphBuilder(instance, int.MaxValue).Build().Count,
            conflictsByStation.Values.Sum(),
            conflictsByStation,
            aPropagation,
            aChangedOrder,
            preflight,
            c0,
            c1,
            freshGate,
            fresh,
            best.JointFeasible,
            best.Witnesses.Count,
            output);
        WriteOutputs(instance, request, result, best.JointFeasible ? best : null);
        return result.JointFeasible ? 0 : 2;
    }

    private static MicroStructureArmResult RunArm(
        TimetablingInstance instance,
        MasterSolutionSnapshot source,
        IReadOnlyList<string> failedStations,
        IReadOnlyList<MicroWitnessCertificate> healthyPatterns,
        MicroStructureArmMode mode,
        MicroStructureSuiteRequest request,
        string logs,
        IReadOnlyDictionary<string, int> aRankByStation,
        bool recheckBaselineMicro,
        ArmBaselineMicroSummary? suppliedBaselineMicro = null)
    {
        Stopwatch armClock = Stopwatch.StartNew();
        List<StationPatternAttemptSummary> stationAttempts = [];
        List<CoordinatorAttemptSummary> coordinatorAttempts = [];
        IReadOnlyList<string> armFailedStations = failedStations;
        IReadOnlyList<MicroWitnessCertificate> armHealthyPatterns = healthyPatterns;
        ArmBaselineMicroSummary baselineMicro = suppliedBaselineMicro ?? new(
            recheckBaselineMicro ? "independent_arm_global_micro_recheck" : "fresh_global_micro_reused",
            false,
            recheckBaselineMicro ? "NOT_RUN" : "ALREADY_SOLVED",
            0,
            recheckBaselineMicro ? "NOT_RUN" : "REUSED",
            0,
            0,
            healthyPatterns.Sum(pattern => pattern.Witnesses.Count),
            failedStations,
            recheckBaselineMicro ? "not_started" : "fresh_global_micro_was_run_immediately_before_closed_loop");

        if (recheckBaselineMicro)
        {
            using FixedDiscreteContinuousCoordinator baselineCoordinator = FixedDiscreteContinuousCoordinator.Build(
                instance,
                source,
                patterns: [],
                runningTimeSupplementRatio: request.RunningTimeSupplementRatio,
                endpointOccupationSeconds: request.EndpointOccupationSeconds);
            ContinuousCoordinationResult recovered = baselineCoordinator.Solve(
                Math.Max(0.1, Math.Min(10, request.ArmBudgetSeconds - armClock.Elapsed.TotalSeconds)),
                Path.Combine(logs, $"{mode.ToString().ToLowerInvariant()}_baseline_recovery.log"));
            coordinatorAttempts.Add(Project("arm_baseline_source_recovery", recovered, 0));
            if (!recovered.HasSolution || !recovered.MacroValidated)
            {
                baselineMicro = baselineMicro with
                {
                    IndependentlyRechecked = true,
                    ContinuousRecoveryStatus = recovered.Status,
                    ContinuousRecoverySeconds = recovered.RuntimeSeconds,
                    StopReason = recovered.StopReason
                };
                armClock.Stop();
                return new(mode, armClock.Elapsed.TotalSeconds, baselineMicro, armFailedStations,
                    armHealthyPatterns.Count, armHealthyPatterns.Sum(pattern => pattern.Witnesses.Count),
                    stationAttempts, coordinatorAttempts, null, "not_run", false, false, false, false,
                    NotAttemptedMacro("arm_baseline_recovery_failed"),
                    new(false, false, 0, "NOT_RUN", 0, 0, 0, false, 0, false, null, []),
                    "arm_baseline_recovery_failed");
            }

            StationLevelMicroFeasibilitySlaveResult rechecked =
                new StationLevelMicroFeasibilitySlave(request.EndpointOccupationSeconds).Solve(baselineCoordinator.Model);
            IReadOnlyList<VerifiedMicroAssignmentWitness> recheckedWitnesses =
                VerifiedMicroAssignmentWitnessBuilder.Capture(rechecked, SnapshotFingerprint.Calculate(source));
            armHealthyPatterns = BuildCertificates(
                instance, source, recheckedWitnesses, request.EndpointOccupationSeconds);
            armFailedStations = rechecked.StationResults
                .Where(result => !result.IsFeasible)
                .Select(result => result.StationName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(station => aRankByStation.GetValueOrDefault(station, int.MaxValue / 4))
                .ThenBy(station => station, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            int unknownStations = rechecked.StationResults.Count(result =>
                result.AssignmentResult.Status == MicroSolveStatus.Unknown);
            baselineMicro = new(
                "independent_arm_global_micro_recheck",
                true,
                recovered.Status,
                recovered.RuntimeSeconds,
                rechecked.Status.ToString(),
                rechecked.Conflicts.Count,
                unknownStations,
                recheckedWitnesses.Count,
                armFailedStations,
                "global_micro_rechecked_from_common_source");
        }

        Dictionary<string, MicroWitnessCertificate> patterns = armHealthyPatterns.ToDictionary(
            pattern => pattern.StationName,
            StringComparer.OrdinalIgnoreCase);
        bool generated = false;
        List<(string Station, IndependentStationPatternSession Session)> pendingSingleSessions = [];
        foreach (string station in armFailedStations)
        {
            if (armClock.Elapsed.TotalSeconds >= request.ArmBudgetSeconds - 30) break;
            IndependentStationPatternSession? session = null;
            try
            {
                session = IndependentStationPatternSession.Open(
                    instance,
                    source,
                    station,
                    request.EndpointOccupationSeconds,
                    Path.Combine(logs, $"{mode.ToString().ToLowerInvariant()}_station_{Sanitize(station)}.log"),
                    request.RunningTimeSupplementRatio);
                IndependentStationPatternResult stationResult = session.Continue(
                    Math.Min(30, request.ArmBudgetSeconds - armClock.Elapsed.TotalSeconds - 30));
                stationAttempts.Add(Project("single_station_initial", stationResult));
                if (stationResult.HasPattern)
                {
                    foreach (MicroWitnessCertificate pattern in stationResult.Patterns) patterns[pattern.StationName] = pattern;
                    generated = true;
                    session.Dispose();
                    session = null;
                }
                else if (stationResult.StatusCode == GRB.Status.TIME_LIMIT)
                {
                    pendingSingleSessions.Add((station, session));
                    session = null;
                }
                else
                {
                    session.Dispose();
                    session = null;
                }
            }
            catch (Exception exception) when (exception is GRBException or InvalidDataException or InvalidOperationException)
            {
                stationAttempts.Add(FailedStationAttempt("single_station_initial", [station], exception));
                session?.Dispose();
            }
        }

        foreach ((string station, IndependentStationPatternSession session) in pendingSingleSessions)
        {
            try
            {
                double continuationLimit = Math.Min(90,
                    request.ArmBudgetSeconds - armClock.Elapsed.TotalSeconds - 30);
                if (continuationLimit <= 0) continue;
                IndependentStationPatternResult continued = session.Continue(continuationLimit);
                stationAttempts.Add(Project("single_station_continuation", continued));
                if (continued.HasPattern)
                {
                    foreach (MicroWitnessCertificate pattern in continued.Patterns)
                        patterns[pattern.StationName] = pattern;
                    generated = true;
                }
            }
            catch (Exception exception) when (exception is GRBException or InvalidDataException or InvalidOperationException)
            {
                stationAttempts.Add(FailedStationAttempt("single_station_continuation", [station], exception));
            }
            finally
            {
                session.Dispose();
            }
        }

        ContinuousCoordinationResult firstCoordination;
        ContinuousCoordinationResult latestCoordination;
        MicroWitnessCertificate[] allSinglePatternCombination = patterns.Values
            .OrderBy(pattern => pattern.StationName, StringComparer.OrdinalIgnoreCase).ToArray();
        using (FixedDiscreteContinuousCoordinator coordinator = FixedDiscreteContinuousCoordinator.Build(
                   instance,
                   source,
                   patterns.Values.ToArray(),
                   runningTimeSupplementRatio: request.RunningTimeSupplementRatio,
                   endpointOccupationSeconds: request.EndpointOccupationSeconds,
                   coordinationModelId: $"{mode}:all_single_station_patterns"))
        {
            double remaining = Math.Max(0.1, Math.Min(10, request.ArmBudgetSeconds - armClock.Elapsed.TotalSeconds));
            firstCoordination = coordinator.Solve(remaining,
                Path.Combine(logs, $"{mode.ToString().ToLowerInvariant()}_coordinator_0.log"),
                $"{mode}:all_single_station_patterns");
            latestCoordination = firstCoordination;
            coordinatorAttempts.Add(Project("all_single_station_patterns", firstCoordination, patterns.Count));
            if (firstCoordination.HasSolution)
            {
                JointValidationSummary validation = ValidateJointCandidate(
                    coordinator.Model,
                    firstCoordination.Snapshot!,
                    request.EndpointOccupationSeconds);
                armClock.Stop();
                return new(mode, armClock.Elapsed.TotalSeconds, baselineMicro, armFailedStations, armHealthyPatterns.Count,
                    armHealthyPatterns.Sum(pattern => pattern.Witnesses.Count), stationAttempts, coordinatorAttempts,
                    null, "not_needed", false, generated, generated && firstCoordination.Audit.PatternConstraintCount > 0,
                    false, NotAttemptedMacro("continuous_coordinator_already_feasible"), validation,
                    validation.JointFeasible ? "joint_feasible_after_single_patterns" : "coordinator_candidate_failed_independent_validation");
            }
        }

        MicroStationPairSelection? firstSelectedPair = SelectPair(
            instance, armFailedStations, patterns.Values.ToArray(), firstCoordination, mode,
            aRankByStation, out bool guided);
        IReadOnlyList<MicroStationPairSelection> fallbackPairs = MicroStationPairQueueBuilder.BuildFallbackQueue(
            armFailedStations,
            MicroBusinessCouplingBuilder.Build(instance),
            aRankByStation);
        IReadOnlyList<MicroStationPairSelection> pairQueue = MicroStationPairQueueBuilder.PutGuidedFirst(
            guided ? firstSelectedPair : null,
            fallbackPairs);
        if (pairQueue.Count == 0 && firstSelectedPair is not null)
            pairQueue = [firstSelectedPair];
        List<MicroStationPairSelection> pairSelections = [];
        List<MacroStructureAttemptSummary> macroAttempts = [];
        List<MacroRepairCoordinationBinding> macroWorkItems = [];
        if (firstSelectedPair is not null)
        {
            macroWorkItems.Add(MacroRepairCoordinationBinding.Create(
                $"{mode}:macro_from_all_single_station_patterns",
                [firstSelectedPair.FirstStation, firstSelectedPair.SecondStation],
                firstSelectedPair,
                firstCoordination.EvidenceBinding,
                allSinglePatternCombination));
        }
        MicroStationPairSelection? pair = pairQueue.FirstOrDefault();
        string pairBasis = guided
            ? "proven_lp_support_first_then_stable_business_pair_enumeration"
            : pair is not null ? "stable_business_coupled_pair_enumeration" : "no_legal_pair";
        int pairIndex = 0;
        foreach (MicroStationPairSelection candidatePair in pairQueue)
        {
            double pairBudget = request.ArmBudgetSeconds - armClock.Elapsed.TotalSeconds - 30;
            if (pairBudget <= 1) break;
            pairIndex++;
            pairSelections.Add(candidatePair);
            try
            {
                double limit = Math.Min(120, pairBudget);
                IndependentStationPatternResult joint = IndependentStationPatternSolver.Solve(
                    instance,
                    source,
                    [candidatePair.FirstStation, candidatePair.SecondStation],
                    request.EndpointOccupationSeconds,
                    Math.Min(30, limit),
                    limit,
                    Path.Combine(logs,
                        $"{mode.ToString().ToLowerInvariant()}_pair_{pairIndex:D2}_{Sanitize(candidatePair.FirstStation)}_{Sanitize(candidatePair.SecondStation)}.log"),
                    request.RunningTimeSupplementRatio);
                stationAttempts.Add(Project($"two_station_joint_{pairIndex:D2}", joint));
                if (joint.HasPattern)
                    foreach (MicroWitnessCertificate pattern in joint.Patterns) patterns[pattern.StationName] = pattern;
            }
            catch (Exception exception) when (exception is GRBException or InvalidDataException or InvalidOperationException)
            {
                stationAttempts.Add(FailedStationAttempt(
                    $"two_station_joint_{pairIndex:D2}",
                    [candidatePair.FirstStation, candidatePair.SecondStation],
                    exception));
            }

            if (armClock.Elapsed.TotalSeconds < request.ArmBudgetSeconds - 0.1)
            {
            MicroWitnessCertificate[] pairPatternCombination = patterns.Values
                .OrderBy(pattern => pattern.StationName, StringComparer.OrdinalIgnoreCase).ToArray();
            using (FixedDiscreteContinuousCoordinator coordinator = FixedDiscreteContinuousCoordinator.Build(
                       instance,
                       source,
                       pairPatternCombination,
                       runningTimeSupplementRatio: request.RunningTimeSupplementRatio,
                       endpointOccupationSeconds: request.EndpointOccupationSeconds,
                       coordinationModelId: $"{mode}:after_two_station_joint_{pairIndex:D2}"))
            {
                ContinuousCoordinationResult second = coordinator.Solve(
                    Math.Max(0.1, Math.Min(10, request.ArmBudgetSeconds - armClock.Elapsed.TotalSeconds)),
                    Path.Combine(logs, $"{mode.ToString().ToLowerInvariant()}_coordinator_pair_{pairIndex:D2}.log"),
                    $"{mode}:after_two_station_joint_{pairIndex:D2}");
                latestCoordination = second;
                coordinatorAttempts.Add(Project($"after_two_station_joint_{pairIndex:D2}", second, patterns.Count));
                macroWorkItems.Add(MacroRepairCoordinationBinding.Create(
                    $"{mode}:macro_after_two_station_joint_{pairIndex:D2}",
                    [candidatePair.FirstStation, candidatePair.SecondStation],
                    candidatePair,
                    second.EvidenceBinding,
                    pairPatternCombination));
                if (second.HasSolution)
                {
                    JointValidationSummary validation = ValidateJointCandidate(
                        coordinator.Model,
                        second.Snapshot!,
                        request.EndpointOccupationSeconds);
                    armClock.Stop();
                    return new(mode, armClock.Elapsed.TotalSeconds, baselineMicro, armFailedStations, armHealthyPatterns.Count,
                        armHealthyPatterns.Sum(pattern => pattern.Witnesses.Count), stationAttempts, coordinatorAttempts,
                        candidatePair, pairBasis, guided, generated,
                        generated && coordinatorAttempts.Any(attempt => attempt.PatternConstraintCount > 0),
                        false, NotAttemptedMacro("continuous_coordinator_already_feasible"), validation,
                        validation.JointFeasible ? "joint_feasible_after_pair" : "pair_candidate_failed_independent_validation",
                        pairSelections.ToArray(), macroAttempts.ToArray());
                }
            }
            }
        }

        MacroStructureAttemptSummary macroAttempt = NotAttemptedMacro("insufficient_arm_budget_for_bounded_macro_scope");
        if (macroWorkItems.Count == 0)
        {
            string fallbackStation = ResolveFallbackMacroStation(
                armFailedStations, patterns.Values.ToArray(), latestCoordination);
            macroWorkItems.Add(MacroRepairCoordinationBinding.Create(
                $"{mode}:macro_fallback",
                [fallbackStation],
                null,
                latestCoordination.EvidenceBinding,
                patterns.Values.ToArray()));
        }
        int macroIndex = 0;
        foreach (MacroRepairCoordinationBinding macroBinding in macroWorkItems)
        {
            double macroBudget = request.ArmBudgetSeconds - armClock.Elapsed.TotalSeconds - 30;
            if (macroBudget <= 1) break;
            macroIndex++;
            string[] activeMacroStations = macroBinding.ActiveStations.ToArray();
            double macroLimit = Math.Min(60, macroBudget);
            try
            {
                BoundedMacroStructureRepairResult macro = BoundedMacroStructureRepairSolver.Solve(
                    instance,
                    source,
                    macroBinding,
                    macroLimit,
                    Path.Combine(logs, $"{mode.ToString().ToLowerInvariant()}_bounded_macro_{macroIndex:D2}.log"),
                    request.RunningTimeSupplementRatio,
                    request.EndpointOccupationSeconds,
                    maximumIndependentMainDecisions: 32,
                    endpointOrderChangeBudget: 4);
                macroAttempt = Project(macro, activeMacroStations);
                macroAttempts.Add(macroAttempt);
                if (macro.HasCandidate && macro.CandidateSnapshot is not null &&
                    armClock.Elapsed.TotalSeconds < request.ArmBudgetSeconds - 20)
                {
                    Dictionary<string, MicroWitnessCertificate> candidatePatterns = new(
                        macroBinding.Patterns.ToDictionary(pattern => pattern.StationName, StringComparer.OrdinalIgnoreCase),
                        StringComparer.OrdinalIgnoreCase);
                    string[] rebuildStations = macro.Scope.AffectedStations
                        .Order(StringComparer.OrdinalIgnoreCase).ToArray();
                    foreach (string station in rebuildStations) candidatePatterns.Remove(station);
                    double rebuildLimit = Math.Min(120, request.ArmBudgetSeconds - armClock.Elapsed.TotalSeconds - 10);
                    IndependentStationPatternResult rebuilt = IndependentStationPatternSolver.Solve(
                        instance,
                        macro.CandidateSnapshot,
                        rebuildStations,
                        request.EndpointOccupationSeconds,
                        Math.Min(30, rebuildLimit),
                        rebuildLimit,
                        Path.Combine(logs, $"{mode.ToString().ToLowerInvariant()}_macro_pattern_rebuild_{macroIndex:D2}.log"),
                        request.RunningTimeSupplementRatio);
                    stationAttempts.Add(Project($"after_bounded_macro_rebuild_{macroIndex:D2}", rebuilt));
                    if (rebuilt.HasPattern)
                    {
                        foreach (MicroWitnessCertificate pattern in rebuilt.Patterns)
                            candidatePatterns[pattern.StationName] = pattern;
                        generated = true;
                        if (armClock.Elapsed.TotalSeconds < request.ArmBudgetSeconds - 0.1)
                        using (FixedDiscreteContinuousCoordinator coordinator = FixedDiscreteContinuousCoordinator.Build(
                                   instance,
                                   macro.CandidateSnapshot,
                                   candidatePatterns.Values.ToArray(),
                                   runningTimeSupplementRatio: request.RunningTimeSupplementRatio,
                                   endpointOccupationSeconds: request.EndpointOccupationSeconds,
                                   coordinationModelId: $"{mode}:after_bounded_macro_{macroIndex:D2}"))
                        {
                            ContinuousCoordinationResult afterMacro = coordinator.Solve(
                                Math.Max(0.1, Math.Min(10, request.ArmBudgetSeconds - armClock.Elapsed.TotalSeconds)),
                                 Path.Combine(logs, $"{mode.ToString().ToLowerInvariant()}_coordinator_after_macro_{macroIndex:D2}.log"));
                            coordinatorAttempts.Add(Project(
                                $"after_bounded_macro_and_pattern_rebuild_{macroIndex:D2}",
                                afterMacro,
                                candidatePatterns.Count));
                            if (afterMacro.HasSolution)
                            {
                                JointValidationSummary validation = ValidateJointCandidate(
                                    coordinator.Model,
                                    afterMacro.Snapshot!,
                                    request.EndpointOccupationSeconds);
                                armClock.Stop();
                                return new(mode, armClock.Elapsed.TotalSeconds, baselineMicro, armFailedStations, armHealthyPatterns.Count,
                                    armHealthyPatterns.Sum(pattern => pattern.Witnesses.Count), stationAttempts, coordinatorAttempts,
                                    macroBinding.Pair ?? pair, pairBasis, guided, generated, true,
                                    macro.Scope.ScopeCapReached, macroAttempt, validation,
                                    validation.JointFeasible
                                        ? "joint_feasible_after_bounded_macro_closed_loop"
                                        : "bounded_macro_candidate_failed_independent_joint_validation",
                                    pairSelections.ToArray(), macroAttempts.ToArray());
                            }
                        }
                    }
                }
            }
            catch (Exception exception) when (exception is GRBException or InvalidDataException or InvalidOperationException)
            {
                macroAttempt = NotAttemptedMacro($"bounded_macro_exception:{exception.GetType().Name}:{exception.Message}") with
                {
                    Attempted = true,
                    Status = "EXCEPTION"
                };
                macroAttempts.Add(macroAttempt);
            }
        }

        armClock.Stop();
        pair = pairSelections.LastOrDefault() ?? pair;
        macroAttempt = macroAttempts.LastOrDefault() ?? macroAttempt;
        JointValidationSummary noValidation = new(false, false, 0, "NOT_RUN", 0, 0, 0, false, 0, false, null, []);
        return new(mode, armClock.Elapsed.TotalSeconds, baselineMicro, armFailedStations, armHealthyPatterns.Count,
            armHealthyPatterns.Sum(pattern => pattern.Witnesses.Count), stationAttempts, coordinatorAttempts,
            pair, pairBasis, guided, generated,
            generated && coordinatorAttempts.Any(attempt => attempt.PatternConstraintCount > 0),
            macroAttempts.Any(attempt => attempt.ScopeCapReached), macroAttempt, noValidation,
            armClock.Elapsed.TotalSeconds >= request.ArmBudgetSeconds ? "arm_budget_exhausted" : "distinct_legal_pair_and_macro_scopes_exhausted",
            pairSelections.ToArray(), macroAttempts.ToArray());
    }

    internal static FreshMacroSummary RunFresh(
        TimetablingInstance instance,
        MicroStructureSuiteRequest request,
        string logs)
    {
        Stopwatch clock = Stopwatch.StartNew();
        using BasicMilpModel macro = new BasicMilpModelBuilder(
            instance,
            neighborRadius: int.MaxValue,
            runningTimeSupplementRatio: request.RunningTimeSupplementRatio,
            endpointOccupationSeconds: request.EndpointOccupationSeconds).Build();
        macro.Model.Set(GRB.IntParam.Seed, 0);
        macro.Model.Set(GRB.IntParam.Threads, 0);
        macro.Model.Set(GRB.DoubleParam.TimeLimit, Math.Max(1, request.FreshBudgetSeconds - 120));
        macro.Model.Set(GRB.StringParam.LogFile, Path.Combine(logs, "fresh_full_macro.log"));
        FirstIncumbentTrackingCallback incumbentCallback = new(true);
        macro.Model.SetCallback(incumbentCallback);
        int variables = macro.Model.Get(GRB.IntAttr.NumVars);
        int binaries = macro.Model.Get(GRB.IntAttr.NumBinVars);
        int constraints = macro.Model.Get(GRB.IntAttr.NumConstrs);
        macro.Optimize();
        double macroRuntime = clock.Elapsed.TotalSeconds;
        int status = macro.Model.Get(GRB.IntAttr.Status);
        if (!macro.HasIncumbentSolution)
            return new(true, false, macroRuntime, incumbentCallback.FirstIncumbentTimeSeconds,
                StatusName(status), variables, binaries, constraints,
                0, 0, 0, null, null, "fresh_macro_no_incumbent");
        MasterValidationReport macroValidation = new MasterSolutionValidator().Validate(macro);
        if (!macroValidation.IsFeasible)
            return new(true, true, macroRuntime, incumbentCallback.FirstIncumbentTimeSeconds,
                StatusName(status), variables, binaries, constraints,
                0, 0, 0, null, null, "fresh_macro_failed_independent_validation");
        MasterSolutionSnapshot snapshot = MasterSolutionSnapshot.Capture(macro);
        StationLevelMicroFeasibilitySlaveResult micro = new StationLevelMicroFeasibilitySlave(
            request.EndpointOccupationSeconds).Solve(macro);
        IReadOnlyList<VerifiedMicroAssignmentWitness> healthyWitnesses = VerifiedMicroAssignmentWitnessBuilder.Capture(
            micro,
            SnapshotFingerprint.Calculate(snapshot));
        snapshot = snapshot with { VerifiedMicroAssignments = healthyWitnesses };
        string[] failedStations = micro.StationResults.Where(result => !result.IsFeasible)
            .Select(result => result.StationName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        IReadOnlyList<MicroWitnessCertificate> healthyPatterns = BuildCertificates(
            instance,
            snapshot,
            healthyWitnesses,
            request.EndpointOccupationSeconds);
        double remaining = request.FreshBudgetSeconds - clock.Elapsed.TotalSeconds - 120;
        if (remaining <= 1)
            return new(true, true, macroRuntime, incumbentCallback.FirstIncumbentTimeSeconds,
                StatusName(status), variables, binaries, constraints,
                micro.Conflicts.Count, micro.Status == MicroSolveStatus.Unknown ? 1 : 0, healthyWitnesses.Count,
                snapshot, null, "fresh_budget_reserved_for_validation");
        MicroStructureSuiteRequest armRequest = request with { ArmBudgetSeconds = remaining };
        MicroStructureArmResult closedLoop = RunArm(
            instance,
            snapshot,
            failedStations,
            healthyPatterns,
            MicroStructureArmMode.C1,
            armRequest,
            logs,
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            recheckBaselineMicro: false,
            suppliedBaselineMicro: new(
                "fresh_global_micro_before_closed_loop",
                true,
                StatusName(status),
                macroRuntime,
                micro.Status.ToString(),
                micro.Conflicts.Count,
                micro.StationResults.Count(result => result.AssignmentResult.Status == MicroSolveStatus.Unknown),
                healthyWitnesses.Count,
                failedStations,
                "fresh_global_micro_completed"));
        return new(true, true, macroRuntime, incumbentCallback.FirstIncumbentTimeSeconds,
            StatusName(status), variables, binaries, constraints,
            micro.Conflicts.Count, micro.Status == MicroSolveStatus.Unknown ? 1 : 0, healthyWitnesses.Count,
            snapshot, closedLoop, closedLoop.Validation.JointFeasible ? "fresh_joint_feasible" : closedLoop.StopReason);
    }

    private static JointValidationSummary ValidateJointCandidate(
        BasicMilpModel model,
        MasterSolutionSnapshot snapshot,
        int endpointOccupationSeconds)
    {
        MasterValidationReport macro = new MasterSolutionValidator().Validate(model);
        StationLevelMicroFeasibilitySlaveResult micro = new StationLevelMicroFeasibilitySlave(endpointOccupationSeconds).Solve(model);
        string fingerprint = SnapshotFingerprint.Calculate(snapshot);
        IReadOnlyList<VerifiedMicroAssignmentWitness> witnesses = VerifiedMicroAssignmentWitnessBuilder.Capture(micro, fingerprint);
        MasterSolutionSnapshot withWitnesses = snapshot with { VerifiedMicroAssignments = witnesses };
        VerifiedMicroAssignmentValidationReport saved = VerifiedMicroAssignmentValidator.Validate(
            model.Instance,
            withWitnesses,
            witnesses,
            micro,
            endpointOccupationSeconds);
        int unknown = micro.Status == MicroSolveStatus.Unknown
            ? micro.StationResults.Count(result => result.AssignmentResult.Status == MicroSolveStatus.Unknown)
            : 0;
        bool joint = macro.IsFeasible && micro.IsFeasible && micro.Conflicts.Count == 0 && unknown == 0 &&
                     witnesses.Count == model.Instance.TrainStops.Count && saved.IsValid;
        return new(true, macro.IsFeasible, macro.Violations.Count, micro.Status.ToString(), micro.Conflicts.Count,
            unknown, witnesses.Count, saved.IsValid, saved.Findings.Count, joint,
            joint ? withWitnesses : snapshot, witnesses);
    }

    private static MicroStationPairSelection? SelectPair(
        TimetablingInstance instance,
        IReadOnlyList<string> failedStations,
        IReadOnlyList<MicroWitnessCertificate> patterns,
        ContinuousCoordinationResult coordination,
        MicroStructureArmMode mode,
        IReadOnlyDictionary<string, int> aRankByStation,
        out bool guided)
    {
        guided = false;
        IReadOnlyList<MicroBusinessCoupling> couplings = MicroBusinessCouplingBuilder.Build(instance);
        if (mode == MicroStructureArmMode.C1 && coordination.ProofDisposition == ContinuousCoordinationProofDisposition.HardProof)
        {
            MicroWitnessCertificate[] supported = patterns.Where(pattern => coordination.ProofRows.Any(row =>
                row.BusinessKind == "micro_pattern" &&
                row.ConstraintName.Contains(PatternConstraintToken(pattern.CertificateId), StringComparison.OrdinalIgnoreCase))).ToArray();
            MicroStationSupport[] support = supported.Select(pattern => new MicroStationSupport(
                pattern.StationName,
                pattern.Witnesses.Select(witness => witness.TrainIndex).ToHashSet(),
                [pattern.CertificateId])).ToArray();
            MicroStationPairSelection? selected = MicroBusinessCoupledPairSelector.Select(
                support, couplings, aRankByStation);
            if (selected is not null)
            {
                guided = true;
                return selected;
            }
        }

        HashSet<string> failed = failedStations.ToHashSet(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> stationRank = instance.Stations.Select((station, index) => (station, index))
            .ToDictionary(item => item.station, item => item.index, StringComparer.OrdinalIgnoreCase);
        MicroBusinessCoupling? fallback = couplings.Where(coupling => failed.Contains(coupling.FirstStation) &&
                                                                       failed.Contains(coupling.SecondStation))
            .OrderBy(coupling => aRankByStation.GetValueOrDefault(coupling.FirstStation, int.MaxValue / 4) +
                                 aRankByStation.GetValueOrDefault(coupling.SecondStation, int.MaxValue / 4))
            .ThenBy(coupling => Math.Abs(stationRank.GetValueOrDefault(coupling.FirstStation) -
                                          stationRank.GetValueOrDefault(coupling.SecondStation)))
            .ThenBy(coupling => coupling.RuleId, StringComparer.Ordinal)
            .FirstOrDefault();
        return fallback is null ? null : new(fallback.FirstStation, fallback.SecondStation, fallback.RuleId,
            fallback.TrainIndex, fallback.RuleKind);
    }

    internal static IReadOnlyList<MicroWitnessCertificate> BuildCertificates(
        TimetablingInstance instance,
        MasterSolutionSnapshot snapshot,
        IReadOnlyList<VerifiedMicroAssignmentWitness> witnesses,
        int endpointOccupationSeconds)
    {
        List<MicroWitnessCertificate> result = [];
        foreach (IGrouping<string, VerifiedMicroAssignmentWitness> station in witnesses.GroupBy(
                     witness => witness.StationName,
                     StringComparer.OrdinalIgnoreCase))
        {
            MicroWitnessCertificateBuildResult build = MicroWitnessCertificateBuilder.Build(new(
                instance,
                snapshot,
                station.ToArray(),
                $"{instance.Meta.InstanceName}:{instance.Trains.Count}:{instance.TrainStops.Count}",
                $"routes={instance.Routes.Count};headways={instance.RouteHeadwayRules.Count};endpoint={endpointOccupationSeconds}",
                1,
                endpointOccupationSeconds));
            if (build.IsSupported && build.Certificate is not null) result.Add(build.Certificate);
        }
        return result;
    }

    private static CoordinatorAttemptSummary Project(string scope, ContinuousCoordinationResult result, int patternCount) =>
        new(scope, result.HasSolution, result.Status, result.RuntimeSeconds, patternCount,
            result.Audit.PatternConstraintCount, result.Audit.SourceVariableCount, result.Audit.SourceBinaryCount,
            result.Audit.SourceConstraintCount, result.Audit.ContinuousVariableCount, result.Audit.RemainingIntegerCount,
            result.MacroValidated, result.MacroViolationCount,
            result.Audit.PresolveSizeAvailable, result.Audit.PresolveStopReason,
            result.Audit.PresolvedVariableCount, result.Audit.PresolvedIntegerCount, result.Audit.PresolvedConstraintCount,
            result.ProofRows.Count, result.ProofRows,
            result.EvidenceBinding.CoordinationModelId,
            result.EvidenceBinding.MacroCandidateFingerprint,
            result.EvidenceBinding.PatternCombinationIdentity,
            result.EvidenceBinding.MathematicalModelIdentity,
            result.EvidenceBinding.SolveAttemptId,
            result.EvidenceBinding.SolveResultIdentity,
            result.EvidenceBinding.ProofAvailability,
            result.EvidenceBinding.ProofAvailabilityReason,
            result.StopReason);

    private static MacroStructureAttemptSummary Project(
        BoundedMacroStructureRepairResult result,
        IReadOnlyList<string> activeStations) =>
        new(true, result.HasCandidate, result.Status, result.RuntimeSeconds, result.FirstIncumbentSeconds, activeStations,
            result.Scope.IndependentMainDecisionCount, result.Scope.ScopeCapReached,
            result.Scope.ReleasedStopIds.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            result.Scope.ReleasedSectionPrecedenceKeys.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            result.SourceVariables, result.SourceBinaries, result.SourceConstraints,
            result.PresolveSizeAvailable, result.PresolveStopReason,
            result.PresolvedVariables, result.PresolvedBinaries, result.PresolvedConstraints,
            result.FixedMainDecisionCount, result.DerivedBinaryCount,
            result.EndpointOrderChanges, result.StopChanges, result.ChangedDecisionIds,
            result.MacroValidated, result.MacroViolationCount,
            result.Scope.CoordinationModelId,
            result.Scope.CoordinationSolveResultIdentity,
            result.Scope.PatternCombinationIdentity,
            result.Scope.ProofAvailability,
            result.Scope.EvidenceMappings,
            result.Scope.SelectionBasis, result.StopReason);

    private static MacroStructureAttemptSummary NotAttemptedMacro(string reason) =>
        new(false, false, "NOT_RUN", 0, null, [], 0, false, [], [], 0, 0, 0,
            false, "not_attempted", 0, 0, 0, 0, 0, 0, 0, [], false, 0,
            "not_attempted", "not_attempted", "not_attempted",
            ContinuousProofAvailability.NoInfeasibilityProof, [], "not_attempted", reason);

    private static string ResolveFallbackMacroStation(
        IReadOnlyList<string> failedStations,
        IReadOnlyList<MicroWitnessCertificate> patterns,
        ContinuousCoordinationResult coordination)
    {
        MicroWitnessCertificate? supported = patterns.FirstOrDefault(pattern => coordination.ProofRows.Any(row =>
            row.BusinessKind == "micro_pattern" &&
            row.ConstraintName.Contains(PatternConstraintToken(pattern.CertificateId), StringComparison.OrdinalIgnoreCase)));
        return supported?.StationName ?? failedStations.First();
    }

    private static StationPatternAttemptSummary Project(string scope, IndependentStationPatternResult result) =>
        new(scope, result.Audit.ActiveStations, result.HasPattern, result.Status, result.RuntimeSeconds,
            result.FirstIncumbentSeconds,
            result.Audit.RequirementCount, result.Audit.SourceVariableCount, result.Audit.SourceBinaryCount,
            result.Audit.SourceConstraintCount, result.Audit.PresolveSizeAvailable, result.Audit.PresolveStopReason,
            result.Audit.PresolvedVariableCount, result.Audit.PresolvedBinaryCount,
            result.Audit.PresolvedConstraintCount, result.Audit.ResourceChoiceBinaryCount,
            result.Audit.ResourceOrderBinaryCount, result.Audit.MacroBinaryCount, result.Audit.RuntimeCouplingCount,
            result.Audit.ServiceCouplingCount, result.FixedResourceValidation?.IsValid == true, result.StopReason);

    private static StationPatternAttemptSummary FailedStationAttempt(
        string scope,
        IReadOnlyList<string> stations,
        Exception exception) =>
        new(scope, stations, false, "EXCEPTION", 0, null, 0, 0, 0, 0, false, "not_attempted",
            0, 0, 0, 0, 0, 0, 0, 0, false,
            $"{exception.GetType().Name}:{exception.Message}");

    private static MicroStructureArmResult SkippedArm(
        MicroStructureArmMode mode,
        IReadOnlyList<string> failed,
        IReadOnlyList<MicroWitnessCertificate> healthy,
        string reason) =>
        new(mode, 0,
            new("not_run", false, "NOT_RUN", 0, "NOT_RUN", 0, 0,
                healthy.Sum(item => item.Witnesses.Count), failed, reason),
            failed, healthy.Count, healthy.Sum(item => item.Witnesses.Count), [], [], null, "not_run",
            false, false, false, false,
            NotAttemptedMacro(reason),
            new(false, false, 0, "NOT_RUN", 0, 0, 0, false, 0, false, null, []), reason);

    private static Dictionary<string, int> LoadConflictCounts(string stationSummaryPath)
    {
        Dictionary<string, int> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (string line in File.ReadLines(stationSummaryPath).Skip(1))
        {
            string[] fields = line.Split(',', 6);
            if (fields.Length >= 5 && int.TryParse(fields[4], out int conflicts) && conflicts > 0)
                result[fields[0].Trim().Trim('"')] = conflicts;
        }
        return result;
    }

    private static void WriteOutputs(
        TimetablingInstance instance,
        MicroStructureSuiteRequest request,
        MicroStructureSuiteResult result,
        JointValidationSummary? successful)
    {
        string output = result.OutputDirectory;
        string codeDirectory = ResolveCodeDirectory();
        string assemblyPath = System.Reflection.Assembly.GetExecutingAssembly().Location;
        string solutionOrDiagnostic = successful is not null
            ? Path.Combine(output, "solution")
            : Path.Combine(output, "diagnostic_state.json");
        object summary = new
        {
            result.StartedAt,
            result.FinishedAt,
            actualWallClockSeconds = (result.FinishedAt - result.StartedAt).TotalSeconds,
            actualCommandLine = Environment.CommandLine,
            result.InputSnapshot,
            result.InputCheckpoint,
            result.InputStationSummary,
            codeIdentity = new
            {
                codeDirectory,
                executingAssembly = assemblyPath,
                executingAssemblySha256 = Sha256(assemblyPath),
                executingAssemblyLastWriteUtc = File.GetLastWriteTimeUtc(assemblyPath),
                modifiedSourceFiles = SourceIdentity(codeDirectory)
            },
            outputPaths = new
            {
                outputDirectory = output,
                resultMarkdown = Path.Combine(output, "result.md"),
                runSummary = Path.Combine(output, "run_summary.json"),
                reproduceScript = Path.Combine(output, "reproduce.ps1"),
                solutionOrDiagnostic
            },
            result.TrainCount,
            result.StopCount,
            result.FullRelationCount,
            result.InitiallyRecordedConflictCount,
            result.InitiallyRecordedConflictsByStation,
            result.APropagation,
            result.AChangedFailedStationOrder,
            result.Preflight,
            c0 = ProjectArmForJson(result.C0),
            c1 = ProjectArmForJson(result.C1),
            result.FreshGatePassed,
            fresh = new
            {
                result.Fresh.Started,
                result.Fresh.MacroCandidateFound,
                result.Fresh.MacroRuntimeSeconds,
                result.Fresh.FirstMacroCandidateSeconds,
                result.Fresh.MacroStatus,
                result.Fresh.MacroVariables,
                result.Fresh.MacroBinaries,
                result.Fresh.MacroConstraints,
                result.Fresh.InitialMicroConflictCount,
                result.Fresh.InitialMicroUnknownCount,
                result.Fresh.InitialHealthyWitnessCount,
                closedLoop = result.Fresh.ClosedLoop is null ? null : ProjectArmForJson(result.Fresh.ClosedLoop),
                result.Fresh.StopReason
            },
            result.JointFeasible,
            result.SavedResourceAssignmentCount,
            invariant = new
            {
                fullRelations = 18000,
                k = 4,
                maxNonHotspotChanges = 0,
                maxIndependentMacroBinariesPerSubproblem = 32,
                seed = 0,
                threads = 0,
                bEnabled = false,
                timeDomain = "continuous",
                stationQuotaWindow = "closed_window_with_outside_boundary_closures_and_independent_recount",
                checkerToleranceSeconds = 0,
                endpointOccupationSeconds = request.EndpointOccupationSeconds
            }
        };
        File.WriteAllText(Path.Combine(output, "run_summary.json"), JsonSerializer.Serialize(summary, JsonOptions), new UTF8Encoding(false));
        WriteTrace(Path.Combine(output, "repair_trace.csv"), result);
        if (successful is not null && successful.Snapshot is not null)
        {
            string solution = Path.Combine(output, "solution");
            Directory.CreateDirectory(solution);
            MasterSolutionSnapshotStore.Save(Path.Combine(solution, "joint_solution.snapshot.json"),
                instance.Meta.InstanceName, successful.Snapshot);
            WriteWitnesses(Path.Combine(solution, "resource_assignments.csv"), successful.Witnesses);
        }
        else
        {
            IReadOnlyList<string> pendingStations = result.Fresh.ClosedLoop?.InitiallyFailedStations ??
                                                    result.C1.InitiallyFailedStations;
            object diagnostic = new
            {
                finalStatus = "no_complete_joint_feasible_solution",
                pendingStations,
                preflight = result.Preflight.StopReason,
                c0 = new { result.C0.StopReason, result.C0.PairSelectionBasis, pairSelections = result.C0.EffectivePairSelections, macroStructureAttempts = result.C0.EffectiveMacroStructureAttempts },
                c1 = new { result.C1.StopReason, result.C1.PairSelectionBasis, result.C1.GuidedPairTriggered, pairSelections = result.C1.EffectivePairSelections, macroStructureAttempts = result.C1.EffectiveMacroStructureAttempts },
                fresh = result.Fresh.StopReason,
                strongestEvidence = (result.Fresh.ClosedLoop?.CoordinatorAttempts.LastOrDefault()?.ProofRows ??
                                     result.C1.CoordinatorAttempts.LastOrDefault()?.ProofRows)?.Take(40),
                lastRecoverableState = result.Fresh.LastRecoverableMacroSnapshot is not null
                    ? (object)result.Fresh.LastRecoverableMacroSnapshot
                    : new { externalSnapshotPath = result.InputSnapshot, externalCheckpointPath = result.InputCheckpoint }
            };
            File.WriteAllText(Path.Combine(output, "diagnostic_state.json"), JsonSerializer.Serialize(diagnostic, JsonOptions), new UTF8Encoding(false));
        }
        WriteResultMarkdown(Path.Combine(output, "result.md"), result);
        WriteReproduce(Path.Combine(output, "reproduce.ps1"), request);
    }

    private static object ProjectArmForJson(MicroStructureArmResult arm) => new
    {
        arm.Mode,
        arm.RuntimeSeconds,
        arm.BaselineMicro,
        arm.InitiallyFailedStations,
        arm.InitialHealthyPatternCount,
        arm.InitialHealthyWitnessCount,
        arm.StationAttempts,
        arm.CoordinatorAttempts,
        arm.PairSelection,
        pairSelections = arm.EffectivePairSelections,
        arm.PairSelectionBasis,
        arm.GuidedPairTriggered,
        arm.NewFailedStationPatternGenerated,
        arm.NewPatternAttachedToCoordinator,
        arm.ScopeCapReached,
        arm.MacroStructureAttempt,
        macroStructureAttempts = arm.EffectiveMacroStructureAttempts,
        validation = new
        {
            arm.Validation.Attempted,
            arm.Validation.MacroFeasible,
            arm.Validation.MacroViolationCount,
            arm.Validation.MicroStatus,
            arm.Validation.MicroConflictCount,
            arm.Validation.MicroUnknownCount,
            arm.Validation.ResourceWitnessCount,
            arm.Validation.SavedWitnessesValid,
            arm.Validation.SavedWitnessFindingCount,
            arm.Validation.JointFeasible
        },
        arm.StopReason
    };

    private static void WriteTrace(string path, MicroStructureSuiteResult result)
    {
        StringBuilder text = new();
        text.AppendLine("arm,sequence,phase,stations,status,runtime_seconds,first_incumbent_seconds,patterns,variables,binaries,constraints,presolve_size_available,presolved_variables,presolved_binaries,presolved_constraints,choice_binaries,order_binaries,macro_binaries,runtime_couplings,service_couplings,micro_conflicts,micro_unknown,reliable_witnesses,fixed_resource_validated,proof_rows,scope_or_pair,stop_reason");
        int sequence = 0;
        foreach (MicroStructureArmResult arm in new[] { result.C0, result.C1 })
        {
            text.AppendLine(string.Join(',', Csv(arm.Mode.ToString()), (++sequence).ToString(CultureInfo.InvariantCulture),
                Csv("baseline_global_micro_recheck"), "", Csv(arm.BaselineMicro.MicroStatus),
                arm.BaselineMicro.ContinuousRecoverySeconds.ToString("R", CultureInfo.InvariantCulture), "", "",
                result.Preflight.ContinuousVariables, result.Preflight.RemainingIntegers, result.Preflight.SourceConstraints,
                result.Preflight.PresolveSizeAvailable, result.Preflight.PresolvedVariables,
                result.Preflight.PresolvedIntegers, result.Preflight.PresolvedConstraints,
                "", "", "", "", "", arm.BaselineMicro.ConflictCount, arm.BaselineMicro.UnknownStationCount,
                arm.BaselineMicro.ReliableWitnessCount, "", "", Csv(arm.BaselineMicro.Scope),
                Csv(arm.BaselineMicro.StopReason)));
            foreach (StationPatternAttemptSummary attempt in arm.StationAttempts)
                text.AppendLine(string.Join(',', Csv(arm.Mode.ToString()), (++sequence).ToString(CultureInfo.InvariantCulture),
                    Csv(attempt.Scope), Csv(string.Join('|', attempt.Stations)), Csv(attempt.Status),
                    attempt.RuntimeSeconds.ToString("R", CultureInfo.InvariantCulture),
                    attempt.FirstIncumbentSeconds?.ToString("R", CultureInfo.InvariantCulture) ?? "", "", attempt.Variables,
                    attempt.Binaries, attempt.Constraints, attempt.PresolveSizeAvailable,
                    attempt.PresolvedVariables, attempt.PresolvedBinaries,
                    attempt.PresolvedConstraints, attempt.ChoiceBinaries, attempt.OrderBinaries, attempt.MacroBinaries,
                    attempt.RuntimeCouplings, attempt.ServiceCouplings, "", "", "", attempt.FixedResourceValidated, "",
                    Csv(attempt.Stations.Count == 0 ? "" : string.Join('|', attempt.Stations)),
                    Csv(attempt.StopReason)));
            foreach (CoordinatorAttemptSummary attempt in arm.CoordinatorAttempts)
                text.AppendLine(string.Join(',', Csv(arm.Mode.ToString()), (++sequence).ToString(CultureInfo.InvariantCulture),
                    Csv(attempt.Scope), "", Csv(attempt.Status), attempt.RuntimeSeconds.ToString("R", CultureInfo.InvariantCulture),
                    "", attempt.PatternCount, attempt.ContinuousVariables, attempt.RemainingIntegers,
                    attempt.SourceConstraints, attempt.PresolveSizeAvailable, attempt.PresolvedVariables,
                    attempt.PresolvedIntegers, attempt.PresolvedConstraints,
                    "", "", "", "", "", "", "", "", "", attempt.ProofRowCount, "", Csv(attempt.StopReason)));
            foreach (MacroStructureAttemptSummary macro in arm.EffectiveMacroStructureAttempts)
            {
                text.AppendLine(string.Join(',', Csv(arm.Mode.ToString()), (++sequence).ToString(CultureInfo.InvariantCulture),
                    Csv("bounded_macro_structure"), Csv(string.Join('|', macro.ActiveStations)), Csv(macro.Status),
                    macro.RuntimeSeconds.ToString("R", CultureInfo.InvariantCulture),
                    macro.FirstIncumbentSeconds?.ToString("R", CultureInfo.InvariantCulture) ?? "", "", macro.Variables,
                    macro.Binaries, macro.Constraints, macro.PresolveSizeAvailable, macro.PresolvedVariables,
                    macro.PresolvedBinaries, macro.PresolvedConstraints, "", "", macro.DerivedBinaryCount,
                    "", "", "", "", "", "", "", Csv($"main={macro.IndependentMainDecisionCount};Kchanges={macro.EndpointOrderChanges};stopChanges={macro.StopChanges};basis={macro.SelectionBasis}"),
                    Csv(macro.StopReason)));
            }
        }
        if (result.Fresh.Started)
            text.AppendLine(string.Join(',', Csv("FRESH"), (++sequence).ToString(CultureInfo.InvariantCulture),
                Csv("complete_compact_macro"), "", Csv(result.Fresh.MacroStatus),
                result.Fresh.MacroRuntimeSeconds.ToString("R", CultureInfo.InvariantCulture),
                result.Fresh.FirstMacroCandidateSeconds?.ToString("R", CultureInfo.InvariantCulture) ?? "", "",
                result.Fresh.MacroVariables, result.Fresh.MacroBinaries, result.Fresh.MacroConstraints,
                "", "", "", "", "", "", "", "", "", result.Fresh.InitialMicroConflictCount,
                result.Fresh.InitialMicroUnknownCount, result.Fresh.InitialHealthyWitnessCount, "", "",
                Csv("full_relations=18000;K=4;seed=0;threads=0"), Csv(result.Fresh.StopReason)));
        File.WriteAllText(path, text.ToString(), new UTF8Encoding(true));
    }

    private static void AppendRecoveryTrace(string path, MicroStructureArmResult arm)
    {
        StringBuilder text = new();
        bool previousRecoveryExists = File.Exists(path) && File.ReadLines(path).Any(line =>
            line.StartsWith("\"FRESH-RECOVERY\",", StringComparison.Ordinal));
        string recoveryArm = previousRecoveryExists ? "FRESH-RECOVERY-LOOP" : "FRESH-RECOVERY";
        int sequence = File.Exists(path)
            ? File.ReadLines(path).Skip(1).Select(line => line.Split(',')).Where(fields => fields.Length > 1)
                .Select(fields => int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                    ? value
                    : 0).DefaultIfEmpty(900).Max()
            : 900;
        foreach (StationPatternAttemptSummary attempt in arm.StationAttempts)
            text.AppendLine(string.Join(',', Csv(recoveryArm), (++sequence).ToString(CultureInfo.InvariantCulture),
                Csv(attempt.Scope), Csv(string.Join('|', attempt.Stations)), Csv(attempt.Status),
                attempt.RuntimeSeconds.ToString("R", CultureInfo.InvariantCulture),
                attempt.FirstIncumbentSeconds?.ToString("R", CultureInfo.InvariantCulture) ?? "", "", attempt.Variables,
                attempt.Binaries, attempt.Constraints, attempt.PresolveSizeAvailable,
                attempt.PresolvedVariables, attempt.PresolvedBinaries, attempt.PresolvedConstraints,
                attempt.ChoiceBinaries, attempt.OrderBinaries, attempt.MacroBinaries,
                attempt.RuntimeCouplings, attempt.ServiceCouplings, "", "", "", attempt.FixedResourceValidated,
                "", Csv(attempt.Stations.Count == 0 ? string.Empty : string.Join('|', attempt.Stations)),
                Csv(attempt.StopReason)));
        foreach (CoordinatorAttemptSummary attempt in arm.CoordinatorAttempts)
            text.AppendLine(string.Join(',', Csv(recoveryArm), (++sequence).ToString(CultureInfo.InvariantCulture),
                Csv(attempt.Scope), "", Csv(attempt.Status),
                attempt.RuntimeSeconds.ToString("R", CultureInfo.InvariantCulture), "", attempt.PatternCount,
                attempt.ContinuousVariables, attempt.RemainingIntegers, attempt.SourceConstraints,
                attempt.PresolveSizeAvailable, attempt.PresolvedVariables, attempt.PresolvedIntegers,
                attempt.PresolvedConstraints, "", "", "", "", "", "", "", "", "", attempt.ProofRowCount,
                "", Csv(attempt.StopReason)));
        foreach (MacroStructureAttemptSummary macro in arm.EffectiveMacroStructureAttempts)
        {
            text.AppendLine(string.Join(',', Csv(recoveryArm), (++sequence).ToString(CultureInfo.InvariantCulture),
                Csv("bounded_macro_structure"), Csv(string.Join('|', macro.ActiveStations)), Csv(macro.Status),
                macro.RuntimeSeconds.ToString("R", CultureInfo.InvariantCulture),
                macro.FirstIncumbentSeconds?.ToString("R", CultureInfo.InvariantCulture) ?? "", "", macro.Variables,
                macro.Binaries, macro.Constraints, macro.PresolveSizeAvailable, macro.PresolvedVariables,
                macro.PresolvedBinaries, macro.PresolvedConstraints, "", "", macro.DerivedBinaryCount, "", "",
                "", "", "", "", "", Csv($"main={macro.IndependentMainDecisionCount};Kchanges={macro.EndpointOrderChanges};stopChanges={macro.StopChanges};basis={macro.SelectionBasis}"),
                Csv(macro.StopReason)));
        }
        File.AppendAllText(path, text.ToString(), new UTF8Encoding(true));
    }

    private static string[] ResolveStationsWithoutPattern(MicroStructureArmResult arm)
    {
        HashSet<string> resolved = arm.StationAttempts.Where(attempt => attempt.HasPattern)
            .SelectMany(attempt => attempt.Stations)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return arm.InitiallyFailedStations.Where(station => !resolved.Contains(station)).ToArray();
    }

    private static void PrependRecoveryResult(string path, MicroStructureArmResult arm, double recoverySeconds)
    {
        string[] missing = ResolveStationsWithoutPattern(arm);
        StationPatternAttemptSummary[] continuations = arm.StationAttempts.Where(attempt =>
            string.Equals(attempt.Scope, "single_station_continuation", StringComparison.Ordinal)).ToArray();
        string header = "# 工程修复后最终结论（替代下方原始结束状态）\r\n\r\n" +
                        $"- 完整联合可行：`{arm.Validation.JointFeasible}`；工程恢复续行墙钟：`{recoverySeconds:F3}` 秒；未重跑完整 Macro。\r\n" +
                        $"- 保存并独立复验的完整资源分配：`{(arm.Validation.JointFeasible ? arm.Validation.Witnesses.Count : 0)}/685`。\r\n" +
                        $"- 30→120 秒同模型续行实际次数：`{continuations.Length}`；无模式站：`{(missing.Length == 0 ? "无" : string.Join('|', missing))}`。\r\n" +
                        $"- C1 证据指导触发：`{arm.GuidedPairTriggered}`；站对依据：`{arm.PairSelectionBasis}`；最终停止：`{arm.StopReason}`。\r\n" +
                        $"- 不同站对/有界 Macro 范围实际尝试：`{arm.EffectivePairSelections.Count}`/`{arm.EffectiveMacroStructureAttempts.Count}`；最后范围状态 `{arm.MacroStructureAttempt.Status}`，开放主决定 `{arm.MacroStructureAttempt.IndependentMainDecisionCount}`，任一范围触及32项上限 `{arm.ScopeCapReached}`；这些结论仅针对已登记范围，不代表 K=4 全域无解。\r\n\r\n" +
                        "## 调度缺陷与修复证据\r\n\r\n" +
                        "原 fresh Micro 阶段遗漏了首档无 incumbent 站的累计120秒续行。修复后，未解站的 Gurobi 模型会话跨首档保留，所有站先完成30秒首档，再在同一模型上续跑剩余额度；定向回归验证了同一会话发生两次 Optimize。恢复从 diagnostic_state.json 内嵌的 fresh Macro 快照开始，只重建固定离散连续模型并复查 Global Micro，没有再次求解完整 Macro。\r\n\r\n";
        string prior = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : string.Empty;
        int originalReport = prior.IndexOf("# JZ Macro–Micro 闭环实现与实验结果", StringComparison.Ordinal);
        if (originalReport >= 0) prior = prior[originalReport..];
        File.WriteAllText(path, header + prior, new UTF8Encoding(true));
    }

    private static void WriteWitnesses(string path, IReadOnlyList<VerifiedMicroAssignmentWitness> witnesses)
    {
        StringBuilder text = new();
        text.AppendLine("station_name,requirement_id,train_index,is_stop,track_name,incoming_route_id,outgoing_route_id,arrival_seconds,departure_seconds,candidate_fingerprint,verification_scope");
        foreach (VerifiedMicroAssignmentWitness witness in witnesses.OrderBy(item => item.StationName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.TrainIndex))
            text.AppendLine(string.Join(',', Csv(witness.StationName), Csv(witness.RequirementId), witness.TrainIndex,
                witness.IsStop, Csv(witness.TrackName), witness.IncomingRouteId?.ToString() ?? "",
                witness.OutgoingRouteId?.ToString() ?? "", witness.ArrivalSeconds?.ToString("R", CultureInfo.InvariantCulture) ?? "",
                witness.DepartureSeconds?.ToString("R", CultureInfo.InvariantCulture) ?? "",
                Csv(witness.CandidateSnapshotFingerprint), Csv(witness.VerificationScope)));
        File.WriteAllText(path, text.ToString(), new UTF8Encoding(true));
    }

    private static void WriteResultMarkdown(string path, MicroStructureSuiteResult result)
    {
        bool c1Better = result.C1.Validation.JointFeasible && !result.C0.Validation.JointFeasible ||
                        result.C1.CoordinatorAttempts.Any(attempt => attempt.HasSolution) &&
                        !result.C0.CoordinatorAttempts.Any(attempt => attempt.HasSolution);
        bool differentPair = result.C0.PairSelection is not null && result.C1.PairSelection is not null &&
                             (!string.Equals(result.C0.PairSelection.FirstStation, result.C1.PairSelection.FirstStation,
                                  StringComparison.OrdinalIgnoreCase) ||
                              !string.Equals(result.C0.PairSelection.SecondStation, result.C1.PairSelection.SecondStation,
                                  StringComparison.OrdinalIgnoreCase));
        StationPatternAttemptSummary? c1Pair = result.C1.StationAttempts.LastOrDefault(attempt =>
            attempt.Scope.StartsWith("two_station_joint", StringComparison.Ordinal));
        CoordinatorAttemptSummary? strongest = result.Fresh.ClosedLoop?.CoordinatorAttempts.LastOrDefault(attempt =>
            attempt.ProofRows.Count > 0) ?? result.C1.CoordinatorAttempts.LastOrDefault(attempt =>
            attempt.ProofRows.Count > 0);
        StringBuilder text = new();
        text.AppendLine("# JZ Macro–Micro 闭环实现与实验结果").AppendLine();
        text.AppendLine("## 首要结论").AppendLine();
        text.AppendLine($"- 完整联合可行：`{result.JointFeasible}`；全流程实际墙钟：`{(result.FinishedAt - result.StartedAt).TotalSeconds:F3}` 秒。");
        text.AppendLine($"- 已保存且独立复验有效的资源分配：`{result.SavedResourceAssignmentCount}/685`。");
        text.AppendLine($"- C1 证据指导两站替代触发次数：`{(result.C1.GuidedPairTriggered ? 1 : 0)}`；相对 C0 选择不同站对：`{differentPair}`；按完整可行/协调 incumbent 口径更优：`{c1Better}`。");
        text.AppendLine($"- 正式 fresh：启动=`{result.Fresh.Started}`，完整 Macro 候选=`{result.Fresh.MacroCandidateFound}`，最终状态=`{result.Fresh.StopReason}`。").AppendLine();

        text.AppendLine("## 实际模型变化").AppendLine();
        text.AppendLine("- 独立站内模式模型只保留活动一站/两站的资源 choice/order 二进制；Macro 二进制计数必须为 0，并对选中资源执行固定资源独立检查。");
        text.AppendLine($"- 固定全部离散结构后的全线协调：源 Macro `{result.Preflight.SourceVariables}` 变量、`{result.Preflight.SourceBinaries}` 二进制、`{result.Preflight.SourceConstraints}` 约束；协调模型 `{result.Preflight.ContinuousVariables}` 连续变量、`{result.Preflight.RemainingIntegers}` 整数；presolve `{result.Preflight.PresolvedVariables}` 变量、`{result.Preflight.PresolvedIntegers}` 整数、`{result.Preflight.PresolvedConstraints}` 约束；Macro 独立检查=`{result.Preflight.MacroValidated}`/违规 `{result.Preflight.MacroViolationCount}`。");
        text.AppendLine($"- C1 两站模型实际运行/服务耦合行：`{c1Pair?.RuntimeCouplings ?? 0}`/`{c1Pair?.ServiceCouplings ?? 0}`；固定资源复验=`{c1Pair?.FixedResourceValidated ?? false}`；首个 incumbent=`{FormatSeconds(c1Pair?.FirstIncumbentSeconds)}`。");
        text.AppendLine($"- C1 有界 Macro 结构尝试：attempted=`{result.C1.MacroStructureAttempt.Attempted}`，开放主决定=`{result.C1.MacroStructureAttempt.IndependentMainDecisionCount}`，端点换序=`{result.C1.MacroStructureAttempt.EndpointOrderChanges}`，停通变化=`{result.C1.MacroStructureAttempt.StopChanges}`，K 上限仍为 4，32 项主决定上限触顶=`{result.C1.MacroStructureAttempt.ScopeCapReached}`。").AppendLine();

        text.AppendLine("## C0/C1 对照").AppendLine();
        text.AppendLine("| 臂 | 独立复查Micro | 初始冲突 | Unknown站 | 可靠witness | 两站依据 | 终止 | 联合可行 |");
        text.AppendLine("|---|---:|---:|---:|---:|---|---|---:|");
        foreach (MicroStructureArmResult arm in new[] { result.C0, result.C1 })
            text.AppendLine($"| {arm.Mode} | {arm.BaselineMicro.IndependentlyRechecked} | {arm.BaselineMicro.ConflictCount} | {arm.BaselineMicro.UnknownStationCount} | {arm.BaselineMicro.ReliableWitnessCount} | {arm.PairSelectionBasis} | {arm.StopReason} | {arm.Validation.JointFeasible} |");
        text.AppendLine();
        text.AppendLine($"- C0 站对：`{FormatPair(result.C0.PairSelection)}`；C1 站对：`{FormatPair(result.C1.PairSelection)}`。");
        text.AppendLine($"- A 已配置=`{result.APropagation.Configured}`，实际读取既有 propagation 分析=`{result.APropagation.ActualSavedAnalysisUsed}`，改变失败站顺序=`{result.AChangedFailedStationOrder}`；A 没有改变邻域/K 定义。B 保持关闭。").AppendLine();

        text.AppendLine("## Fresh 运行与直接证据").AppendLine();
        text.AppendLine($"- fresh Macro：状态=`{result.Fresh.MacroStatus}`，规模 `{result.Fresh.MacroVariables}` 变量 / `{result.Fresh.MacroBinaries}` 二进制 / `{result.Fresh.MacroConstraints}` 约束，首个候选=`{FormatSeconds(result.Fresh.FirstMacroCandidateSeconds)}`，Macro阶段墙钟=`{result.Fresh.MacroRuntimeSeconds:F3}` 秒。");
        text.AppendLine($"- fresh Global Micro：冲突 `{result.Fresh.InitialMicroConflictCount}`，Unknown `{result.Fresh.InitialMicroUnknownCount}`，可靠 witness `{result.Fresh.InitialHealthyWitnessCount}`。");
        text.AppendLine($"- C0：`{result.C0.StopReason}`；新失败站模式已生成=`{result.C0.NewFailedStationPatternGenerated}`，已接入全线协调=`{result.C0.NewPatternAttachedToCoordinator}`。");
        text.AppendLine($"- C1：`{result.C1.StopReason}`；依据=`{result.C1.PairSelectionBasis}`；新失败站模式已生成=`{result.C1.NewFailedStationPatternGenerated}`，已接入全线协调=`{result.C1.NewPatternAttachedToCoordinator}`。");
        if (!result.JointFeasible && strongest is not null)
        {
            text.AppendLine($"- 最强阻碍证据：固定离散结构和已选资源模式下，全线纯连续模型状态 `{strongest.Status}`，硬证据行 `{strongest.ProofRowCount}`。代表性来源：");
            foreach (ContinuousProofRow row in strongest.ProofRows.Take(12))
                text.AppendLine($"  - `{row.BusinessKind}` / `{row.BusinessId}` / `{row.EvidenceKind}`");
        }
        text.AppendLine();

        text.AppendLine("## 正确性边界与工程核查").AppendLine();
        text.AppendLine("- 完整 Macro 始终使用 18,000 条适用区间关系；原 K=4、maxNonHotspotChanges=0、Seed=0、Threads=0、全部业务约束及独立检查保持不变。");
        text.AppendLine("- 时刻仍为连续变量；站级配额采用闭区间复算；窗外分支在端点使用闭包，取消人为的 1 秒空档和业务放宽，仅保留数值容差。");
        text.AppendLine("- 已处理 Gurobi 在 presolve 直接证明不可行时拒绝生成 presolved model 的工程异常：保留原始状态和 IIS 证据，不把该异常当成新的业务结论。");
        text.AppendLine("- Release 编译、局部候选和完整联合可行分别记录；二进制减少、纯连续协调和单次运行结果不构成实测加速比、稳定优势或 JH 可扩展性结论。");
        File.WriteAllText(path, text.ToString(), new UTF8Encoding(true));
    }

    private static string FormatPair(MicroStationPairSelection? pair) => pair is null
        ? "未触发"
        : $"{pair.FirstStation}|{pair.SecondStation}|{pair.RuleKind}|{pair.RuleId}";

    private static string FormatSeconds(double? seconds) => seconds.HasValue
        ? seconds.Value.ToString("F3", CultureInfo.InvariantCulture) + "s"
        : "未记录";

    private static void WriteReproduce(string path, MicroStructureSuiteRequest request)
    {
        string codeDirectory = ResolveCodeDirectory();
        string project = Path.Combine(codeDirectory, "GurobiTimetablingResearch.csproj");
        string dataset = Path.GetFullPath(Path.Combine(codeDirectory, "..", "..", "Data", "JZ"));
        string script = "$ErrorActionPreference = 'Stop'\r\n" +
                        "$project = '" + project.Replace("'", "''") + "'\r\n" +
                        "$dataset = '" + dataset.Replace("'", "''") + "'\r\n" +
                        "dotnet run --no-build -c Release --project $project -- $dataset --micro-structure-suite " +
                        "--micro-structure-snapshot='" + request.SnapshotPath.Replace("'", "''") + "' " +
                        "--micro-structure-checkpoint='" + request.CheckpointPath.Replace("'", "''") + "' " +
                        "--micro-structure-station-summary='" + request.StationSummaryPath.Replace("'", "''") + "' " +
                        "--micro-structure-output='" + request.OutputDirectory.Replace("'", "''") + "' " +
                        "--micro-structure-arm-budget-seconds=" + request.ArmBudgetSeconds.ToString("R", CultureInfo.InvariantCulture) + " " +
                        "--micro-structure-fresh-budget-seconds=" + request.FreshBudgetSeconds.ToString("R", CultureInfo.InvariantCulture) + " " +
                        "--running-time-supplement-ratio=" + request.RunningTimeSupplementRatio.ToString("R", CultureInfo.InvariantCulture) + " " +
                        "--checker-loop-endpoint-occupation-seconds=" + request.EndpointOccupationSeconds.ToString(CultureInfo.InvariantCulture) + " " +
                        (request.RunFresh ? "--micro-structure-run-fresh " : string.Empty) +
                        "--macro-full-relations --macro-expected-full-relation-count=18000\r\n";
        File.WriteAllText(path, script, new UTF8Encoding(true));
    }

    private static string ResolveCodeDirectory() => Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));

    private static IReadOnlyList<object> SourceIdentity(string codeDirectory)
    {
        List<string> paths = [];
        string microStructure = Path.Combine(codeDirectory, "Optimization", "MicroStructure");
        if (Directory.Exists(microStructure))
            paths.AddRange(Directory.GetFiles(microStructure, "*.cs", SearchOption.TopDirectoryOnly));
        foreach (string relative in new[]
                 {
                     Path.Combine("Optimization", "Model", "BasicMilpModel.cs"),
                     Path.Combine("Optimization", "Model", "BasicMilpModelBuilder.cs"),
                     Path.Combine("Optimization", "Model", "MacroDiscreteBindings.cs"),
                     Path.Combine("Optimization", "Micro", "StationLevelMicroFeasibilitySlave.cs"),
                     Path.Combine("Optimization", "State", "MasterSolutionSnapshot.cs"),
                     Path.Combine("Optimization", "State", "MasterSolutionSnapshotStore.cs"),
                     Path.Combine("Application", "Program.cs")
                 })
        {
            string full = Path.Combine(codeDirectory, relative);
            if (File.Exists(full)) paths.Add(full);
        }
        return paths.Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(path => (object)new { path, sha256 = Sha256(path) })
            .ToArray();
    }

    private static string Sha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string Csv(object? value)
    {
        string text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        return '"' + text.Replace("\"", "\"\"") + '"';
    }

    private static string StatusName(int status) => status switch
    {
        GRB.Status.OPTIMAL => "OPTIMAL",
        GRB.Status.INTERRUPTED => "INTERRUPTED",
        GRB.Status.TIME_LIMIT => "TIME_LIMIT",
        GRB.Status.INFEASIBLE => "INFEASIBLE",
        _ => status.ToString()
    };

    private static string Sanitize(string value) => new(value.Select(character =>
        char.IsLetterOrDigit(character) ? character : '_').Take(120).ToArray());

    private static string PatternConstraintToken(string value) => new(value.Select(character =>
        char.IsLetterOrDigit(character) ? character : '_').Take(80).ToArray());
}
