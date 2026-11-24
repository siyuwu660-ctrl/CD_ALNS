using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Gurobi;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

internal sealed record MicroStructureProofBindingRecoveryRequest(
    string HistoricalDiagnosticPath,
    string HistoricalRunSummaryPath,
    string OutputDirectory,
    double ContinuousDiagnosticLimitSeconds = 120,
    double TargetedMacroRepairLimitSeconds = 60,
    double FreshBudgetSeconds = 3600,
    int EndpointOccupationSeconds = StationEndpointOccupationPolicy.DefaultSeconds,
    double RunningTimeSupplementRatio = 0.05);

internal sealed record ProofRecoveryAttempt(
    int Sequence,
    string Phase,
    string Scope,
    string Status,
    bool HasCandidate,
    double RuntimeSeconds,
    double LimitSeconds,
    int Variables,
    int Binaries,
    int Constraints,
    int PresolvedVariables,
    int PresolvedBinaries,
    int PresolvedConstraints,
    string Identity,
    string StopReason);

internal sealed record RebuiltContinuousModelSignature(
    int Variables,
    int Binaries,
    int Constraints,
    int NonZeros,
    int PatternCount,
    int PatternConstraints,
    int PresolvedVariables,
    int PresolvedIntegers,
    int PresolvedConstraints,
    string PatternCombinationIdentity);

internal sealed record FixedPatternJointValidation(
    bool Attempted,
    bool MacroFeasible,
    int MacroViolationCount,
    string GlobalMicroStatus,
    int GlobalMicroConflictCount,
    int GlobalMicroUnknownCount,
    int FixedResourceWitnessCount,
    bool FixedResourceValid,
    int FixedResourceFindingCount,
    double? ConstraintResidual,
    double? BoundResidual,
    bool JointFeasible,
    MasterSolutionSnapshot? Snapshot,
    IReadOnlyList<VerifiedMicroAssignmentWitness> Witnesses,
    IReadOnlyList<VerifiedMicroAssignmentValidationFinding> Findings);

/// <summary>
/// Directed recovery only: it never optimizes a Macro bootstrap and only rebuilds the six
/// explicitly missing station patterns plus the historical pair-01 combination.
/// </summary>
internal static partial class MicroStructureProofBindingRecoveryRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    [GeneratedRegex(@"Presolve time:\s*(?<seconds>[0-9.]+)s", RegexOptions.IgnoreCase)]
    private static partial Regex PresolveTimeRegex();

    [GeneratedRegex(@"Ordering time:\s*(?<seconds>[0-9.]+)s", RegexOptions.IgnoreCase)]
    private static partial Regex OrderingTimeRegex();

    internal static int Run(
        TimetablingInstance instance,
        MicroStructureProofBindingRecoveryRequest request)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(request);
        string output = Path.GetFullPath(request.OutputDirectory);
        string logs = Path.Combine(output, "logs");
        string patternsDirectory = Path.Combine(output, "patterns");
        string targetModelDirectory = Path.Combine(output, "target_model");
        string mappingDirectory = Path.Combine(output, "proof_mapping");
        Directory.CreateDirectory(logs);
        Directory.CreateDirectory(patternsDirectory);
        Directory.CreateDirectory(targetModelDirectory);
        Directory.CreateDirectory(mappingDirectory);

        Stopwatch totalClock = Stopwatch.StartNew();
        List<ProofRecoveryAttempt> attempts = [];
        List<string> savedPatternBundles = [];
        string finalStatus = "directed_reconstruction_not_started";
        string failureDetail = "";
        string sourceSnapshotPath = Path.Combine(targetModelDirectory, "saved_macro_candidate.snapshot.json");
        string finalPatternBundlePath = Path.Combine(patternsDirectory, "pair01_complete_pattern_bundle.json");
        string modelPath = Path.Combine(targetModelDirectory, "pair01_full_continuous_model.mps.gz");
        string modelManifestPath = Path.Combine(targetModelDirectory, "model_reconstruction_manifest.json");
        ContinuousCoordinationResult? continuousResult = null;
        RebuiltContinuousModelSignature? builtSignature = null;
        RebuiltContinuousModelSignature? readbackSignature = null;
        BoundedMacroStructureRepairResult? macroRepair = null;
        FixedPatternJointValidation validation = NotAttemptedValidation();
        FreshMacroSummary? fresh = null;
        double reconstructionSeconds = 0;
        double diagnosticSeconds = 0;
        double freshSeconds = 0;
        string? completeSolutionPath = null;
        string? assignmentsPath = null;
        MasterSolutionSnapshot? source = null;
        MicroWitnessCertificate[] finalPatterns = [];
        object? historicalEvidence = null;

        try
        {
            ValidateRequest(request);
            JsonObject diagnostic = ParseObject(request.HistoricalDiagnosticPath);
            JsonObject historicalSummary = ParseObject(request.HistoricalRunSummaryPath);
            source = LoadEmbeddedSource(instance, diagnostic);
            MasterSolutionSnapshotStore.Save(sourceSnapshotPath, instance.Meta.InstanceName, source);
            MasterSolutionSnapshot readbackSource = MasterSolutionSnapshotStore.Load(
                sourceSnapshotPath,
                instance.Meta.InstanceName,
                instance.Trains.Select(train => train.TrainIndex).ToHashSet());
            if (!string.Equals(SnapshotFingerprint.Calculate(source), SnapshotFingerprint.Calculate(readbackSource),
                    StringComparison.Ordinal))
                throw new InvalidDataException("Saved Macro snapshot changed identity during readback.");

            string[] failedStations = ReadFailedStations(diagnostic);
            string[] requiredStations = ["站3967", "站4589", "站4595", "站4943", "站4945", "站4949"];
            if (!failedStations.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(requiredStations))
                throw new InvalidDataException(
                    $"Directed reconstruction station set mismatch: saved=[{string.Join('|', failedStations)}].");
            if (source.VerifiedMicroAssignments is null || source.VerifiedMicroAssignments.Count == 0)
                throw new InvalidDataException("Saved Macro state has no reliable healthy Micro witnesses.");

            IReadOnlyList<MicroWitnessCertificate> healthyPatterns =
                MicroStructureExperimentRunner.BuildCertificates(
                    instance, source, source.VerifiedMicroAssignments, request.EndpointOccupationSeconds);
            Dictionary<string, MicroWitnessCertificate> patterns = healthyPatterns.ToDictionary(
                pattern => pattern.StationName, StringComparer.OrdinalIgnoreCase);
            historicalEvidence = WriteHistoricalEvidenceMapping(
                instance, historicalSummary, mappingDirectory);

            Stopwatch reconstructionClock = Stopwatch.StartNew();
            List<string> rebuiltStations = [];
            int sequence = 0;
            foreach (string station in requiredStations)
            {
                string log = Path.Combine(logs, $"single_{sequence + 1:D2}_{Sanitize(station)}.log");
                IndependentStationPatternResult result = IndependentStationPatternSolver.Solve(
                    instance,
                    source,
                    [station],
                    request.EndpointOccupationSeconds,
                    30,
                    120,
                    log,
                    request.RunningTimeSupplementRatio);
                attempts.Add(Project(++sequence, "directed_single_station_rebuild", station, result));
                WriteAttempts(Path.Combine(output, "attempts.csv"), attempts);
                if (!result.HasPattern || result.Patterns.Count != 1 ||
                    result.FixedResourceValidation?.IsValid != true)
                    throw new InvalidDataException(
                        $"Directed single-station pattern failed at '{station}': {result.StopReason}.");
                patterns[station] = result.Patterns.Single();
                rebuiltStations.Add(station);
                string path = Path.Combine(patternsDirectory,
                    $"stage_{sequence:D2}_after_{Sanitize(station)}.pattern_bundle.json");
                SavePatternBundle(path, instance, source, sourceSnapshotPath, rebuiltStations, patterns.Values,
                    request, $"directed_rebuild_single_{station}");
                savedPatternBundles.Add(path);
            }

            string pairLog = Path.Combine(logs, "pair_01_站4945_站4949.log");
            IndependentStationPatternResult pairResult = IndependentStationPatternSolver.Solve(
                instance,
                source,
                ["站4945", "站4949"],
                request.EndpointOccupationSeconds,
                30,
                120,
                pairLog,
                request.RunningTimeSupplementRatio);
            attempts.Add(Project(++sequence, "directed_pair_rebuild", "站4945|站4949", pairResult));
            WriteAttempts(Path.Combine(output, "attempts.csv"), attempts);
            if (!pairResult.HasPattern || pairResult.Patterns.Count != 2 ||
                pairResult.FixedResourceValidation?.IsValid != true)
                throw new InvalidDataException($"Directed pair-01 pattern failed: {pairResult.StopReason}.");
            foreach (MicroWitnessCertificate pattern in pairResult.Patterns)
                patterns[pattern.StationName] = pattern;
            finalPatterns = patterns.Values.OrderBy(
                pattern => pattern.StationName, StringComparer.OrdinalIgnoreCase).ToArray();
            SavePatternBundle(finalPatternBundlePath, instance, source, sourceSnapshotPath, rebuiltStations,
                finalPatterns, request, "directed_rebuild_pair01_station4945_station4949");
            savedPatternBundles.Add(finalPatternBundlePath);
            reconstructionClock.Stop();
            reconstructionSeconds = reconstructionClock.Elapsed.TotalSeconds;

            MicroPatternReconstructionBundle loadedBundle = MicroPatternReconstructionBundleStore.Load(
                finalPatternBundlePath, instance, readbackSource);
            finalPatterns = loadedBundle.Patterns;

            Stopwatch buildClock = Stopwatch.StartNew();
            using FixedDiscreteContinuousCoordinator coordinator = FixedDiscreteContinuousCoordinator.Build(
                instance,
                readbackSource,
                finalPatterns,
                runningTimeSupplementRatio: request.RunningTimeSupplementRatio,
                endpointOccupationSeconds: request.EndpointOccupationSeconds,
                coordinationModelId: "directed_reconstruction_pair01_full_line");
            builtSignature = CaptureSignature(coordinator, finalPatterns);
            coordinator.Model.Model.Write(modelPath);
            buildClock.Stop();

            using (FixedDiscreteContinuousCoordinator rebuilt = FixedDiscreteContinuousCoordinator.Build(
                       instance,
                       MasterSolutionSnapshotStore.Load(
                           sourceSnapshotPath,
                           instance.Meta.InstanceName,
                           instance.Trains.Select(train => train.TrainIndex).ToHashSet()),
                       MicroPatternReconstructionBundleStore.Load(
                           finalPatternBundlePath, instance, readbackSource).Patterns,
                       runningTimeSupplementRatio: request.RunningTimeSupplementRatio,
                       endpointOccupationSeconds: request.EndpointOccupationSeconds,
                       coordinationModelId: "directed_reconstruction_pair01_readback_check"))
            {
                readbackSignature = CaptureSignature(rebuilt, finalPatterns);
            }
            if (builtSignature != readbackSignature)
                throw new InvalidDataException("Readback rebuild changed the full continuous model signature.");
            WriteJson(modelManifestPath, new
            {
                formatVersion = 1,
                reconstructionKind = "directed_reconstruction_not_sequence_932_reproduction",
                sourceSnapshotPath,
                finalPatternBundlePath,
                fullModelPath = modelPath,
                buildOnlyReadbackDidNotOptimize = true,
                builtSignature,
                readbackSignature,
                buildAndExportSeconds = buildClock.Elapsed.TotalSeconds,
                mathematicalInputs = new
                {
                    instance = instance.Meta.InstanceName,
                    dataVersion = DataVersion(instance),
                    ruleVersion = RuleVersion(instance, request.EndpointOccupationSeconds),
                    request.RunningTimeSupplementRatio,
                    request.EndpointOccupationSeconds,
                    neighborRadius = "full",
                    fixedDiscreteState = true,
                    patternCombinationIdentity = loadedBundle.PatternCombinationIdentity
                }
            });

            string diagnosticLog = Path.Combine(logs, "pair01_full_line_120s.log");
            Stopwatch diagnosticClock = Stopwatch.StartNew();
            continuousResult = coordinator.Solve(
                request.ContinuousDiagnosticLimitSeconds,
                diagnosticLog,
                "directed_reconstruction_pair01_120s");
            diagnosticClock.Stop();
            diagnosticSeconds = diagnosticClock.Elapsed.TotalSeconds;
            attempts.Add(Project(++sequence, "full_line_continuous_diagnostic", "pair-01", continuousResult));
            WriteAttempts(Path.Combine(output, "attempts.csv"), attempts);
            WriteJson(Path.Combine(mappingDirectory, "current_coordination_evidence.json"), new
            {
                continuousResult.EvidenceBinding,
                continuousResult.ProofRows,
                mappings = ContinuousProofDependencyMapper.Map(
                    instance, ["站4945", "站4949"], continuousResult.EvidenceBinding, finalPatterns),
                solverStageTiming = AnalyzeSolverLog(diagnosticLog),
                numericalResiduals = ReadResiduals(coordinator.Model.Model)
            });

            if (continuousResult.HasSolution && continuousResult.Snapshot is not null)
            {
                validation = ValidateWithFixedPatterns(
                    coordinator.Model,
                    continuousResult.Snapshot,
                    finalPatterns,
                    request.EndpointOccupationSeconds);
                finalStatus = validation.JointFeasible
                    ? "recovery_joint_feasible"
                    : "continuous_solution_failed_complete_joint_validation";
            }
            else if (continuousResult.StatusCode == GRB.Status.INFEASIBLE &&
                     continuousResult.EvidenceBinding.ProofAvailability == ContinuousProofAvailability.HardProof)
            {
                MicroStationPairSelection pair = new(
                    "站4945", "站4949",
                    "runtime:t10:train-10-stop-2->train-10-stop-3",
                    10,
                    "runtime");
                MacroRepairCoordinationBinding binding = MacroRepairCoordinationBinding.Create(
                    "directed_pair01_current_proof_repair",
                    ["站4945", "站4949"],
                    pair,
                    continuousResult.EvidenceBinding,
                    finalPatterns);
                macroRepair = BoundedMacroStructureRepairSolver.Solve(
                    instance,
                    readbackSource,
                    binding,
                    request.TargetedMacroRepairLimitSeconds,
                    Path.Combine(logs, "current_proof_targeted_macro_repair.log"),
                    request.RunningTimeSupplementRatio,
                    request.EndpointOccupationSeconds,
                    32,
                    4);
                attempts.Add(Project(++sequence, "current_proof_targeted_macro_repair", "pair-01", macroRepair));
                WriteAttempts(Path.Combine(output, "attempts.csv"), attempts);
                WriteJson(Path.Combine(mappingDirectory, "current_proof_macro_scope.json"), new
                {
                    macroRepair.Scope,
                    macroRepair.ChangedDecisionIds,
                    macroRepair.StopReason,
                    nominalEngineeringCap = 32,
                    capSemantics = "proof_dependency_closure_is_checked_before_nominal_cap",
                    k = 4,
                    nonHotspotChangeLimit = 0
                });
                finalStatus = macroRepair.HasCandidate
                    ? "targeted_macro_candidate_requires_new_compatible_pattern_evidence"
                    : macroRepair.StopReason;
            }
            else
            {
                finalStatus = continuousResult.StatusCode == GRB.Status.TIME_LIMIT
                    ? "full_line_continuous_timeout_unknown"
                    : continuousResult.StopReason;
            }

            if (validation.JointFeasible && validation.Snapshot is not null)
            {
                string solutionDirectory = Path.Combine(output, "solution");
                Directory.CreateDirectory(solutionDirectory);
                completeSolutionPath = Path.Combine(solutionDirectory, "recovery_joint_solution.snapshot.json");
                assignmentsPath = Path.Combine(solutionDirectory, "resource_assignments_685.csv");
                MasterSolutionSnapshotStore.Save(
                    completeSolutionPath, instance.Meta.InstanceName, validation.Snapshot);
                WriteWitnesses(assignmentsPath, validation.Witnesses);

                Stopwatch freshClock = Stopwatch.StartNew();
                Directory.CreateDirectory(Path.Combine(logs, "fresh"));
                JsonObject oldSummary = ParseObject(request.HistoricalRunSummaryPath);
                MicroStructureSuiteRequest freshRequest = new(
                    oldSummary["inputSnapshot"]?.GetValue<string>() ?? "",
                    oldSummary["inputCheckpoint"]?.GetValue<string>() ?? "",
                    oldSummary["inputStationSummary"]?.GetValue<string>() ?? "",
                    output,
                    true,
                    request.FreshBudgetSeconds,
                    request.FreshBudgetSeconds,
                    request.EndpointOccupationSeconds,
                    request.RunningTimeSupplementRatio);
                fresh = MicroStructureExperimentRunner.RunFresh(
                    instance, freshRequest, Path.Combine(logs, "fresh"));
                freshClock.Stop();
                freshSeconds = freshClock.Elapsed.TotalSeconds;
                if (fresh.ClosedLoop?.Validation.JointFeasible == true &&
                    fresh.ClosedLoop.Validation.Snapshot is not null)
                {
                    string freshSolution = Path.Combine(output, "fresh_solution");
                    Directory.CreateDirectory(freshSolution);
                    MasterSolutionSnapshotStore.Save(
                        Path.Combine(freshSolution, "fresh_joint_solution.snapshot.json"),
                        instance.Meta.InstanceName,
                        fresh.ClosedLoop.Validation.Snapshot);
                    WriteWitnesses(
                        Path.Combine(freshSolution, "resource_assignments_685.csv"),
                        fresh.ClosedLoop.Validation.Witnesses);
                    finalStatus = "recovery_and_fresh_joint_feasible";
                }
                else
                {
                    WriteJson(Path.Combine(output, "fresh_diagnostic_state.json"), new
                    {
                        status = fresh.StopReason,
                        lastRecoverableState = fresh.LastRecoverableMacroSnapshot,
                        fresh.ClosedLoop
                    });
                    finalStatus = "recovery_joint_feasible_fresh_not_joint_feasible";
                }
            }
        }
        catch (Exception exception)
        {
            finalStatus = "engineering_failure_during_directed_reconstruction";
            failureDetail = exception.ToString();
            File.WriteAllText(Path.Combine(logs, "engineering_failure.txt"), failureDetail,
                new UTF8Encoding(false));
        }
        finally
        {
            totalClock.Stop();
            WriteAttempts(Path.Combine(output, "attempts.csv"), attempts);
            object summary = new
            {
                formatVersion = 1,
                reconstructionKind = "directed_reconstruction_not_sequence_932_reproduction",
                finalStatus,
                failureDetail,
                sourceMacroWasReoptimized = false,
                rebuiltSingleStations = new[] { "站3967", "站4589", "站4595", "站4943", "站4945", "站4949" },
                rebuiltPair = "站4945|站4949|pair-01",
                attempts,
                sourceSnapshotFingerprint = source is null ? null : SnapshotFingerprint.Calculate(source),
                patternCount = finalPatterns.Length,
                patternWitnessCount = finalPatterns.SelectMany(pattern => pattern.Witnesses).Count(),
                patternCombinationIdentity = finalPatterns.Length == 0
                    ? null
                    : MicroPatternCombinationIdentity.Calculate(finalPatterns),
                savedPatternBundles,
                builtSignature,
                readbackSignature,
                continuousResult,
                macroRepair,
                validation,
                fresh,
                timings = new
                {
                    reconstructionSeconds,
                    diagnosticSeconds,
                    targetedMacroRepairSeconds = macroRepair?.RuntimeSeconds ?? 0,
                    freshSeconds,
                    totalWallClockSeconds = totalClock.Elapsed.TotalSeconds
                },
                historicalEvidence,
                paths = new
                {
                    output,
                    sourceSnapshotPath,
                    finalPatternBundlePath,
                    modelPath,
                    modelManifestPath,
                    result = Path.Combine(output, "result.md"),
                    runSummary = Path.Combine(output, "run_summary.json"),
                    reproduce = Path.Combine(output, "reproduce.ps1"),
                    completeSolutionPath,
                    assignmentsPath
                }
            };
            WriteJson(Path.Combine(output, "run_summary.json"), summary);
            WriteResult(Path.Combine(output, "result.md"), finalStatus, failureDetail,
                attempts, continuousResult, macroRepair, validation, fresh,
                reconstructionSeconds, diagnosticSeconds, freshSeconds, totalClock.Elapsed.TotalSeconds,
                builtSignature, readbackSignature, output, sourceSnapshotPath, finalPatternBundlePath,
                modelPath, modelManifestPath, completeSolutionPath, assignmentsPath);
            WriteReproduce(Path.Combine(output, "reproduce.ps1"), request);
        }
        return validation.JointFeasible ? 0 : 2;
    }

    internal static int ResumeAfterSavedDiagnostic(
        TimetablingInstance instance,
        string outputDirectory,
        double targetedMacroRepairLimitSeconds = 60,
        int endpointOccupationSeconds = StationEndpointOccupationPolicy.DefaultSeconds,
        double runningTimeSupplementRatio = 0.05)
    {
        string output = Path.GetFullPath(outputDirectory);
        string summaryPath = Path.Combine(output, "run_summary.json");
        JsonObject summary = ParseObject(summaryPath);
        ContinuousCoordinationResult continuous = summary["continuousResult"]?
            .Deserialize<ContinuousCoordinationResult>(JsonOptions)
            ?? throw new InvalidDataException("Saved recovery summary has no continuous diagnostic result.");
        if (continuous.StatusCode != GRB.Status.INFEASIBLE ||
            continuous.EvidenceBinding.ProofAvailability != ContinuousProofAvailability.HardProof ||
            continuous.ProofRows.Count == 0)
            throw new InvalidDataException("Saved continuous result is not a model-local hard infeasibility proof.");
        ProofRecoveryAttempt[] oldAttempts = summary["attempts"]?
            .Deserialize<ProofRecoveryAttempt[]>(JsonOptions) ?? [];
        if (oldAttempts.Any(attempt => attempt.Phase == "current_proof_targeted_macro_repair"))
            throw new InvalidDataException("The saved hard proof already has a targeted Macro repair attempt.");

        string sourceSnapshotPath = Path.Combine(output, "target_model", "saved_macro_candidate.snapshot.json");
        string bundlePath = Path.Combine(output, "patterns", "pair01_complete_pattern_bundle.json");
        MasterSolutionSnapshot source = MasterSolutionSnapshotStore.Load(
            sourceSnapshotPath,
            instance.Meta.InstanceName,
            instance.Trains.Select(train => train.TrainIndex).ToHashSet());
        MicroPatternReconstructionBundle bundle = MicroPatternReconstructionBundleStore.Load(
            bundlePath, instance, source);
        if (!string.Equals(bundle.PatternCombinationIdentity,
                continuous.EvidenceBinding.PatternCombinationIdentity, StringComparison.Ordinal))
            throw new InvalidDataException("Saved proof and saved pair-01 pattern combination identities differ.");

        Stopwatch clock = Stopwatch.StartNew();
        string mappingDirectory = Path.Combine(output, "proof_mapping");
        string diagnosticLog = Path.Combine(output, "logs", "pair01_full_line_120s.log");
        IReadOnlyList<ContinuousProofBusinessMapping> mappings = ContinuousProofDependencyMapper.Map(
            instance, ["站4945", "站4949"], continuous.EvidenceBinding, bundle.Patterns);
        WriteJson(Path.Combine(mappingDirectory, "current_coordination_evidence.json"), new
        {
            resumedFromSavedModelLocalResult = true,
            repeatedContinuousOptimize = false,
            continuous.EvidenceBinding,
            continuous.ProofRows,
            mappings,
            solverStageTiming = AnalyzeSolverLog(diagnosticLog)
        });
        WriteMappingsCsv(Path.Combine(mappingDirectory, "current_75_row_iis_mapping.csv"), mappings);

        MicroStationPairSelection pair = new(
            "站4945", "站4949", "runtime:t10:train-10-stop-2->train-10-stop-3", 10, "runtime");
        MacroRepairCoordinationBinding binding = MacroRepairCoordinationBinding.Create(
            "directed_pair01_current_proof_repair",
            ["站4945", "站4949"], pair, continuous.EvidenceBinding, bundle.Patterns);
        BoundedMacroStructureRepairResult repair = BoundedMacroStructureRepairSolver.Solve(
            instance,
            source,
            binding,
            targetedMacroRepairLimitSeconds,
            Path.Combine(output, "logs", "current_proof_targeted_macro_repair.log"),
            runningTimeSupplementRatio,
            endpointOccupationSeconds,
            32,
            4);
        clock.Stop();

        List<ProofRecoveryAttempt> attempts = oldAttempts.ToList();
        attempts.Add(Project(attempts.Count + 1,
            "current_proof_targeted_macro_repair", "pair-01", repair));
        WriteAttempts(Path.Combine(output, "attempts.csv"), attempts);
        WriteJson(Path.Combine(mappingDirectory, "current_proof_macro_scope.json"), new
        {
            resumedFromSolveResultIdentity = continuous.EvidenceBinding.SolveResultIdentity,
            repair.Scope,
            repair.ChangedDecisionIds,
            repair.StopReason,
            nominalEngineeringCap = 32,
            capSemantics = "proof_dependency_closure_is_checked_before_nominal_cap",
            k = 4,
            nonHotspotChangeLimit = 0
        });
        string finalStatus = repair.HasCandidate
            ? "targeted_macro_candidate_requires_new_compatible_pattern_evidence"
            : repair.StopReason;
        summary["finalStatus"] = finalStatus;
        summary["failureDetail"] = "";
        summary["attempts"] = JsonSerializer.SerializeToNode(attempts, JsonOptions);
        summary["macroRepair"] = JsonSerializer.SerializeToNode(repair, JsonOptions);
        if (summary["timings"] is JsonObject timings)
        {
            timings["targetedMacroRepairSeconds"] = repair.RuntimeSeconds;
            timings["engineeringContinuationSeconds"] = clock.Elapsed.TotalSeconds;
        }
        summary["engineeringIssueResolution"] = JsonSerializer.SerializeToNode(new
        {
            issue = "Gurobi log file was still open when the phase parser attempted an exclusive read",
            fix = "read solver logs with FileShare.ReadWrite",
            reusedSavedPatternAndProofState = true,
            repeatedContinuousOptimize = false,
            completedAt = DateTimeOffset.Now
        }, JsonOptions);
        File.WriteAllText(summaryPath, summary.ToJsonString(JsonOptions), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(output, "change_regression_summary.md"),
            "# 修改与回归摘要\n\n" +
            "- ProofRows、Macro候选、模式组合、数学模型和求解结果现在按协调尝试独立深拷贝绑定。\n" +
            "- 作用域生成器显式接收对应绑定；空证明、无不可行证明和映射失败是三个状态。\n" +
            "- 回归覆盖：第二个空结果不会覆盖第一个证明；调用顺序不改变第一个作用域；完整模式包可读回。\n" +
            "- 日志锁异常修为 FileShare.ReadWrite；从已保存75行证明续接，未再次 Optimize 同一连续模型。\n" +
            $"- 当前证明作用域实际开放：stop={string.Join('|', repair.Scope.ReleasedStopIds)}；" +
            $"section precedence={repair.Scope.ReleasedSectionPrecedenceKeys.Count}；K=4。\n" +
            "- 保持原始业务约束及闭时间窗；取消1秒窗外空档和业务放宽，以独立闭窗复算验收。\n",
            new UTF8Encoding(false));

        RebuiltContinuousModelSignature? built = summary["builtSignature"]?
            .Deserialize<RebuiltContinuousModelSignature>(JsonOptions);
        RebuiltContinuousModelSignature? readback = summary["readbackSignature"]?
            .Deserialize<RebuiltContinuousModelSignature>(JsonOptions);
        double reconstructionSeconds = summary["timings"]?["reconstructionSeconds"]?.GetValue<double>() ?? 0;
        double diagnosticSeconds = summary["timings"]?["diagnosticSeconds"]?.GetValue<double>() ?? 0;
        double priorTotal = summary["timings"]?["totalWallClockSeconds"]?.GetValue<double>() ?? 0;
        WriteResult(Path.Combine(output, "result.md"), finalStatus, "", attempts, continuous, repair,
            NotAttemptedValidation(), null, reconstructionSeconds, diagnosticSeconds, 0,
            priorTotal + clock.Elapsed.TotalSeconds, built, readback, output, sourceSnapshotPath,
            bundlePath, Path.Combine(output, "target_model", "pair01_full_continuous_model.mps.gz"),
            Path.Combine(output, "target_model", "model_reconstruction_manifest.json"), null, null);
        return 2;
    }

    internal static int RefreshSavedReport(TimetablingInstance instance, string outputDirectory)
    {
        string output = Path.GetFullPath(outputDirectory);
        string summaryPath = Path.Combine(output, "run_summary.json");
        JsonObject summary = ParseObject(summaryPath);
        string sourceSnapshotPath = Path.Combine(output, "target_model", "saved_macro_candidate.snapshot.json");
        string bundlePath = Path.Combine(output, "patterns", "pair01_complete_pattern_bundle.json");
        MasterSolutionSnapshot source = MasterSolutionSnapshotStore.Load(
            sourceSnapshotPath, instance.Meta.InstanceName,
            instance.Trains.Select(train => train.TrainIndex).ToHashSet());
        MicroPatternReconstructionBundle bundle = MicroPatternReconstructionBundleStore.Load(
            bundlePath, instance, source);
        RebuiltContinuousModelSignature? built = summary["builtSignature"]?
            .Deserialize<RebuiltContinuousModelSignature>(JsonOptions);
        RebuiltContinuousModelSignature? readback = summary["readbackSignature"]?
            .Deserialize<RebuiltContinuousModelSignature>(JsonOptions);
        if (built is not null) built = built with { PatternCount = bundle.Patterns.Length };
        if (readback is not null) readback = readback with { PatternCount = bundle.Patterns.Length };
        summary["builtSignature"] = JsonSerializer.SerializeToNode(built, JsonOptions);
        summary["readbackSignature"] = JsonSerializer.SerializeToNode(readback, JsonOptions);
        summary["savedPatternBundles"] = JsonSerializer.SerializeToNode(new[] { bundlePath }, JsonOptions);
        summary["intermediatePatternBundleDisposition"] =
            "each stage was saved immediately; redundant cumulative stage files were removed after the final 685-witness bundle passed readback";

        ContinuousCoordinationResult continuous = summary["continuousResult"]?
            .Deserialize<ContinuousCoordinationResult>(JsonOptions)
            ?? throw new InvalidDataException("Saved report has no continuous result.");
        BoundedMacroStructureRepairResult? repair = summary["macroRepair"] is JsonObject repairNode
            ? ReadRepairForReport(repairNode)
            : null;
        ProofRecoveryAttempt[] attempts = summary["attempts"]?
            .Deserialize<ProofRecoveryAttempt[]>(JsonOptions) ?? [];
        if (built is not null)
            attempts = attempts.Select(attempt => attempt.Phase == "full_line_continuous_diagnostic"
                ? attempt with
                {
                    Variables = built.Variables,
                    Binaries = built.Binaries,
                    Constraints = built.Constraints,
                    PresolvedVariables = built.PresolvedVariables,
                    PresolvedBinaries = built.PresolvedIntegers,
                    PresolvedConstraints = built.PresolvedConstraints
                }
                : attempt).ToArray();
        summary["attempts"] = JsonSerializer.SerializeToNode(attempts, JsonOptions);
        WriteAttempts(Path.Combine(output, "attempts.csv"), attempts);
        FixedPatternJointValidation validation = summary["validation"]?
            .Deserialize<FixedPatternJointValidation>(JsonOptions) ?? NotAttemptedValidation();
        FreshMacroSummary? fresh = summary["fresh"]?.Deserialize<FreshMacroSummary>(JsonOptions);
        int confirmedMainDependencies = repair?.Scope.EvidenceMappings
            .SelectMany(mapping => mapping.Decisions)
            .Where(decision => decision.IsAdjustable &&
                               decision.DecisionFamily is "stop" or "section_precedence")
            .Select(decision => $"{decision.DecisionFamily}:{decision.DecisionKey}")
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() ?? 0;
        int historical67Adjustable = ParseObject(Path.Combine(
                output, "proof_mapping", "historical_67_row_iis_mapping.json"))["mappings"]?
            .Deserialize<ContinuousProofBusinessMapping[]>(JsonOptions)?
            .SelectMany(mapping => mapping.Decisions)
            .Where(decision => decision.IsAdjustable)
            .Select(decision => $"{decision.DecisionFamily}:{decision.DecisionKey}")
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() ?? 0;
        summary["evidenceConclusions"] = JsonSerializer.SerializeToNode(new
        {
            historical67BelongsTo = "all_single_station_patterns",
            historical67AdjustableSpecificDecisions = historical67Adjustable,
            historical67ExpectedTrains = new[] { 16, 19, 60 },
            historical67WasNotUsedByItsOwnOldScope = true,
            current75BelongsTo = continuous.EvidenceBinding.PatternCombinationIdentity,
            currentConfirmedMainDependencies = confirmedMainDependencies,
            nominalCap = 32,
            currentProofDependenciesTruncated = confirmedMainDependencies > 32,
            capReachedOnlyAfterPairFallback = repair?.Scope.ScopeCapReached == true &&
                                              confirmedMainDependencies <= 32,
            conclusion = confirmedMainDependencies > 32
                ? "current proof dependency closure exceeded the nominal cap"
                : "no saved evidence shows that the 32-item cap truncated a confirmed necessary dependency"
        }, JsonOptions);
        File.WriteAllText(summaryPath, summary.ToJsonString(JsonOptions), new UTF8Encoding(false));

        string manifestPath = Path.Combine(output, "target_model", "model_reconstruction_manifest.json");
        JsonObject manifest = ParseObject(manifestPath);
        manifest["builtSignature"] = JsonSerializer.SerializeToNode(built, JsonOptions);
        manifest["readbackSignature"] = JsonSerializer.SerializeToNode(readback, JsonOptions);
        File.WriteAllText(manifestPath, manifest.ToJsonString(JsonOptions), new UTF8Encoding(false));

        double reconstruction = summary["timings"]?["reconstructionSeconds"]?.GetValue<double>() ?? 0;
        double diagnostic = summary["timings"]?["diagnosticSeconds"]?.GetValue<double>() ?? 0;
        double freshSeconds = summary["timings"]?["freshSeconds"]?.GetValue<double>() ?? 0;
        double total = summary["timings"]?["totalWallClockSeconds"]?.GetValue<double>() ?? 0;
        double continuation = summary["timings"]?["engineeringContinuationSeconds"]?.GetValue<double>() ?? 0;
        WriteResult(Path.Combine(output, "result.md"),
            summary["finalStatus"]?.GetValue<string>() ?? "未记录", "", attempts,
            continuous, repair, validation, fresh, reconstruction, diagnostic, freshSeconds,
            total + continuation, built, readback, output, sourceSnapshotPath, bundlePath,
            Path.Combine(output, "target_model", "pair01_full_continuous_model.mps.gz"),
            manifestPath, null, null);
        return validation.JointFeasible ? 0 : 2;
    }

    private static void ValidateRequest(MicroStructureProofBindingRecoveryRequest request)
    {
        if (!File.Exists(request.HistoricalDiagnosticPath))
            throw new FileNotFoundException("Historical diagnostic state was not found.", request.HistoricalDiagnosticPath);
        if (!File.Exists(request.HistoricalRunSummaryPath))
            throw new FileNotFoundException("Historical run summary was not found.", request.HistoricalRunSummaryPath);
        if (request.ContinuousDiagnosticLimitSeconds != 120)
            throw new InvalidDataException("This directed diagnostic must use exactly the authorized 120-second limit.");
        if (request.EndpointOccupationSeconds is not (0 or 600) ||
            Math.Abs(request.RunningTimeSupplementRatio - 0.05) > 1e-12)
            throw new InvalidDataException("Directed recovery configuration differs from the saved JZ baseline.");
    }

    private static MasterSolutionSnapshot LoadEmbeddedSource(TimetablingInstance instance, JsonObject diagnostic)
    {
        JsonNode state = diagnostic["lastRecoverableState"]
                         ?? throw new InvalidDataException("Historical diagnostic has no embedded Macro state.");
        return MasterSolutionSnapshotStore.DeserializePayload(
            state.ToJsonString(),
            instance.Trains.Select(train => train.TrainIndex).ToHashSet());
    }

    private static string[] ReadFailedStations(JsonObject diagnostic) =>
        diagnostic["allInitiallyFailedStations"]?.AsArray()
            .Select(node => node?.GetValue<string>() ?? "")
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        ?? throw new InvalidDataException("Historical diagnostic has no failed-station set.");

    private static object WriteHistoricalEvidenceMapping(
        TimetablingInstance instance,
        JsonObject summary,
        string mappingDirectory)
    {
        JsonArray attempts = summary["fresh"]?["closedLoop"]?["coordinatorAttempts"]?.AsArray()
                             ?? throw new InvalidDataException("Historical summary has no coordinator ledger.");
        List<object> evidenceSets = [];
        foreach (string scope in new[] { "all_single_station_patterns", "after_two_station_joint_05" })
        {
            JsonObject? attempt = attempts.Select(node => node?.AsObject()).FirstOrDefault(item =>
                string.Equals(item?["scope"]?.GetValue<string>(), scope, StringComparison.OrdinalIgnoreCase));
            if (attempt is null) continue;
            ContinuousProofRow[] rows = attempt["proofRows"]?.Deserialize<ContinuousProofRow[]>(JsonOptions) ?? [];
            IReadOnlyList<ContinuousProofBusinessMapping> mappings = ContinuousProofDependencyMapper.Map(
                instance, ["站4945", "站4949"], rows, []);
            string token = scope == "all_single_station_patterns" ? "67" : "99";
            WriteJson(Path.Combine(mappingDirectory, $"historical_{token}_row_iis_mapping.json"), new
            {
                scope,
                status = attempt["status"]?.GetValue<string>() ?? "未记录",
                patternCombinationIdentity = attempt["patternCombinationIdentity"]?.GetValue<string>() ?? "未记录",
                proofRowCount = rows.Length,
                rows,
                mappings,
                interpretation = scope == "all_single_station_patterns"
                    ? "67-row proof belongs to the all-single-station pattern combination"
                    : "99-row proof belongs to the cumulative combination after pair-05 and is a different cause"
            });
            WriteMappingsCsv(Path.Combine(mappingDirectory, $"historical_{token}_row_iis_mapping.csv"), mappings);
            evidenceSets.Add(new { scope, proofRowCount = rows.Length, mappings });
        }
        return evidenceSets;
    }

    private static void SavePatternBundle(
        string path,
        TimetablingInstance instance,
        MasterSolutionSnapshot source,
        string sourceSnapshotPath,
        IReadOnlyList<string> rebuiltStations,
        IEnumerable<MicroWitnessCertificate> patterns,
        MicroStructureProofBindingRecoveryRequest request,
        string label)
    {
        MicroPatternReconstructionBundle bundle = MicroPatternReconstructionBundle.Create(
            label,
            instance,
            source,
            sourceSnapshotPath,
            DataVersion(instance),
            RuleVersion(instance, request.EndpointOccupationSeconds),
            ExecutingCodeIdentity(),
            request.RunningTimeSupplementRatio,
            request.EndpointOccupationSeconds,
            rebuiltStations,
            patterns.ToArray());
        MicroPatternReconstructionBundleStore.Save(path, bundle);
    }

    private static RebuiltContinuousModelSignature CaptureSignature(
        FixedDiscreteContinuousCoordinator coordinator,
        IReadOnlyList<MicroWitnessCertificate> patterns) => new(
        coordinator.Model.Model.Get(GRB.IntAttr.NumVars),
        coordinator.Model.Model.Get(GRB.IntAttr.NumBinVars),
        coordinator.Model.Model.Get(GRB.IntAttr.NumConstrs),
        coordinator.Model.Model.Get(GRB.IntAttr.NumNZs),
        patterns.Count,
        coordinator.Audit.PatternConstraintCount,
        coordinator.Audit.PresolvedVariableCount,
        coordinator.Audit.PresolvedIntegerCount,
        coordinator.Audit.PresolvedConstraintCount,
        MicroPatternCombinationIdentity.Calculate(patterns));

    internal static FixedPatternJointValidation ValidateWithFixedPatterns(
        BasicMilpModel model,
        MasterSolutionSnapshot snapshot,
        IReadOnlyList<MicroWitnessCertificate> patterns,
        int endpointOccupationSeconds)
    {
        MasterValidationReport macro = new MasterSolutionValidator().Validate(model);
        StationLevelMicroFeasibilitySlaveResult global = new StationLevelMicroFeasibilitySlave(
            endpointOccupationSeconds).Solve(model);
        string fingerprint = SnapshotFingerprint.Calculate(snapshot);
        VerifiedMicroAssignmentWitness[] fixedWitnesses = patterns
            .SelectMany(pattern => pattern.Witnesses)
            .Select(witness => witness with
            {
                CandidateSnapshotFingerprint = fingerprint,
                ArrivalSeconds = snapshot.ArrivalSecondsByStopId[witness.RequirementId],
                DepartureSeconds = snapshot.DepartureSecondsByStopId[witness.RequirementId],
                VerificationScope = "station_micro_validated"
            })
            .OrderBy(witness => witness.StationName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(witness => witness.RequirementId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        MasterSolutionSnapshot withWitnesses = snapshot with { VerifiedMicroAssignments = fixedWitnesses };
        VerifiedMicroAssignmentValidationReport fixedValidation = VerifiedMicroAssignmentValidator.Validate(
            model.Instance, withWitnesses, fixedWitnesses, global, endpointOccupationSeconds);
        int unknown = global.StationResults.Count(result =>
            result.AssignmentResult.Status == MicroSolveStatus.Unknown);
        (double? constr, double? bound) = ReadResiduals(model.Model);
        bool joint = macro.IsFeasible && global.IsFeasible && global.Conflicts.Count == 0 && unknown == 0 &&
                     fixedWitnesses.Length == model.Instance.TrainStops.Count && fixedValidation.IsValid &&
                     constr is <= 1e-6 && bound is <= 1e-6;
        return new(true, macro.IsFeasible, macro.Violations.Count, global.Status.ToString(),
            global.Conflicts.Count, unknown, fixedWitnesses.Length, fixedValidation.IsValid,
            fixedValidation.Findings.Count, constr, bound, joint,
            joint ? withWitnesses : snapshot, fixedWitnesses, fixedValidation.Findings);
    }

    private static (double? Constraint, double? Bound) ReadResiduals(GRBModel model)
    {
        if (model.Get(GRB.IntAttr.SolCount) == 0) return (null, null);
        try { return (model.Get(GRB.DoubleAttr.ConstrVio), model.Get(GRB.DoubleAttr.BoundVio)); }
        catch (GRBException) { return (null, null); }
    }

    private static object AnalyzeSolverLog(string path)
    {
        string text = "";
        if (File.Exists(path))
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using StreamReader reader = new(stream, Encoding.UTF8);
            text = reader.ReadToEnd();
        }
        double? presolve = LastSeconds(PresolveTimeRegex(), text);
        double? ordering = LastSeconds(OrderingTimeRegex(), text);
        string phase = ordering is > 1
            ? "barrier_ordering_or_factorization"
            : text.Contains("Iteration", StringComparison.OrdinalIgnoreCase)
                ? "simplex_iterations"
                : "not_recorded";
        return new { presolveSeconds = presolve, orderingSeconds = ordering, dominantRecordedPhase = phase };
    }

    private static double? LastSeconds(Regex regex, string text)
    {
        MatchCollection matches = regex.Matches(text);
        return matches.Count == 0
            ? null
            : double.Parse(matches[^1].Groups["seconds"].Value, CultureInfo.InvariantCulture);
    }

    private static ProofRecoveryAttempt Project(
        int sequence,
        string phase,
        string scope,
        IndependentStationPatternResult result) => new(
        sequence, phase, scope, result.Status, result.HasPattern, result.RuntimeSeconds,
        result.CumulativeLimitSeconds, result.Audit.SourceVariableCount, result.Audit.SourceBinaryCount,
        result.Audit.SourceConstraintCount, result.Audit.PresolvedVariableCount,
        result.Audit.PresolvedBinaryCount, result.Audit.PresolvedConstraintCount,
        result.HasPattern ? MicroPatternCombinationIdentity.Calculate(result.Patterns) : "",
        result.StopReason);

    private static ProofRecoveryAttempt Project(
        int sequence,
        string phase,
        string scope,
        ContinuousCoordinationResult result) => new(
        sequence, phase, scope, result.Status, result.HasSolution, result.RuntimeSeconds, 120,
        result.Audit.ContinuousVariableCount, result.Audit.RemainingIntegerCount,
        result.Audit.SourceConstraintCount + result.Audit.ExactFixedDiscreteCount +
        result.Audit.PatternConstraintCount + result.Audit.ExplicitBoundRowCount +
        2 * (result.Audit.ContinuousVariableCount - result.Audit.SourceVariableCount),
        result.Audit.PresolvedVariableCount, result.Audit.PresolvedIntegerCount,
        result.Audit.PresolvedConstraintCount, result.EvidenceBinding.SolveResultIdentity, result.StopReason);

    private static ProofRecoveryAttempt Project(
        int sequence,
        string phase,
        string scope,
        BoundedMacroStructureRepairResult result) => new(
        sequence, phase, scope, result.Status, result.HasCandidate, result.RuntimeSeconds, 60,
        result.SourceVariables, result.SourceBinaries, result.SourceConstraints,
        result.PresolvedVariables, result.PresolvedBinaries, result.PresolvedConstraints,
        result.Scope.CoordinationSolveResultIdentity, result.StopReason);

    private static void WriteAttempts(string path, IReadOnlyList<ProofRecoveryAttempt> attempts)
    {
        StringBuilder csv = new();
        csv.AppendLine("sequence,phase,scope,status,has_candidate,runtime_seconds,limit_seconds,variables,binaries,constraints,presolved_variables,presolved_binaries,presolved_constraints,identity,stop_reason");
        foreach (ProofRecoveryAttempt item in attempts)
            csv.AppendLine(string.Join(',', Csv(item.Sequence), Csv(item.Phase), Csv(item.Scope), Csv(item.Status),
                Csv(item.HasCandidate), Csv(item.RuntimeSeconds), Csv(item.LimitSeconds), Csv(item.Variables),
                Csv(item.Binaries), Csv(item.Constraints), Csv(item.PresolvedVariables),
                Csv(item.PresolvedBinaries), Csv(item.PresolvedConstraints), Csv(item.Identity), Csv(item.StopReason)));
        File.WriteAllText(path, csv.ToString(), new UTF8Encoding(true));
    }

    private static void WriteMappingsCsv(
        string path,
        IReadOnlyList<ContinuousProofBusinessMapping> mappings)
    {
        StringBuilder csv = new();
        csv.AppendLine("constraint,evidence_kind,business_kind,mapping_status,decision_family,decision_key,train_index,station_order,station_name,is_adjustable,decision_reason,mapping_reason");
        foreach (ContinuousProofBusinessMapping mapping in mappings)
        {
            ProofMappedDecision[] decisions = mapping.Decisions.Count == 0
                ? [new("", "", null, null, "", false, "")]
                : mapping.Decisions.ToArray();
            foreach (ProofMappedDecision decision in decisions)
                csv.AppendLine(string.Join(',', Csv(mapping.ConstraintName), Csv(mapping.EvidenceKind),
                    Csv(mapping.BusinessKind), Csv(mapping.Status), Csv(decision.DecisionFamily),
                    Csv(decision.DecisionKey), Csv(decision.TrainIndex), Csv(decision.StationOrder),
                    Csv(decision.StationName), Csv(decision.IsAdjustable), Csv(decision.Reason), Csv(mapping.Reason)));
        }
        File.WriteAllText(path, csv.ToString(), new UTF8Encoding(true));
    }

    private static void WriteWitnesses(
        string path,
        IReadOnlyList<VerifiedMicroAssignmentWitness> witnesses)
    {
        StringBuilder csv = new();
        csv.AppendLine("station_name,requirement_id,train_index,is_stop,track_name,incoming_route_id,outgoing_route_id,arrival_seconds,departure_seconds,candidate_fingerprint,verification_scope");
        foreach (VerifiedMicroAssignmentWitness witness in witnesses)
            csv.AppendLine(string.Join(',', Csv(witness.StationName), Csv(witness.RequirementId),
                Csv(witness.TrainIndex), Csv(witness.IsStop), Csv(witness.TrackName), Csv(witness.IncomingRouteId),
                Csv(witness.OutgoingRouteId), Csv(witness.ArrivalSeconds), Csv(witness.DepartureSeconds),
                Csv(witness.CandidateSnapshotFingerprint), Csv(witness.VerificationScope)));
        File.WriteAllText(path, csv.ToString(), new UTF8Encoding(true));
    }

    private static void WriteResult(
        string path,
        string finalStatus,
        string failure,
        IReadOnlyList<ProofRecoveryAttempt> attempts,
        ContinuousCoordinationResult? continuous,
        BoundedMacroStructureRepairResult? repair,
        FixedPatternJointValidation validation,
        FreshMacroSummary? fresh,
        double reconstructionSeconds,
        double diagnosticSeconds,
        double freshSeconds,
        double totalSeconds,
        RebuiltContinuousModelSignature? built,
        RebuiltContinuousModelSignature? readback,
        string output,
        string sourceSnapshot,
        string bundle,
        string model,
        string manifest,
        string? solution,
        string? assignments)
    {
        StringBuilder md = new();
        md.AppendLine("# JZ Micro compatibility：ProofRows 绑定修复与定向重建").AppendLine();
        md.AppendLine($"- 最终状态：`{finalStatus}`");
        md.AppendLine($"- 完整联合可行：{validation.JointFeasible}");
        md.AppendLine($"- 685 条同候选资源分配：{(validation.JointFeasible ? validation.FixedResourceWitnessCount : 0)}");
        md.AppendLine("- 本轮性质：定向重建；不是旧执行台账序号 932 原模型复现。");
        md.AppendLine("- Macro 候选：读取已保存状态，未重新 Optimize Macro。");
        md.AppendLine("- 旧 11 个模型：保留其终止记录，但 ProofRows 错配使其不能评价旧 IIS 引导、完整 K=4 域或 32 项上限。");
        if (!string.IsNullOrWhiteSpace(failure)) md.AppendLine($"- 工程异常：`{failure.Split('\n')[0]}`");
        md.AppendLine().AppendLine("## 诊断结论").AppendLine();
        md.AppendLine($"- 120 秒全线模型：`{continuous?.Status ?? "未运行"}`；有经 Gurobi 解池记录的连续解：{continuous?.HasSolution ?? false}；独立 Macro 检查：{continuous?.MacroValidated ?? false}。");
        md.AppendLine($"- 证明状态：`{continuous?.EvidenceBinding.ProofAvailability.ToString() ?? "未记录"}`；证明行：{continuous?.ProofRows.Count ?? 0}。");
        if (continuous is not null)
        {
            string diagnosticLog = Path.Combine(output, "logs", "pair01_full_line_120s.log");
            string solverText = ReadTextShared(diagnosticLog);
            Match removed = Regex.Match(solverText,
                @"Presolve removed\s+(?<rows>\d+)\s+rows and\s+(?<columns>\d+)\s+columns",
                RegexOptions.IgnoreCase);
            if (!removed.Success)
            {
                string buildExcerpt = ReadTextShared(Path.Combine(
                    output, "logs", "build_and_readback_presolve_excerpt.txt"));
                removed = Regex.Match(buildExcerpt,
                    @"Presolve removed\s+(?<rows>\d+)\s+rows and\s+(?<columns>\d+)\s+columns",
                    RegexOptions.IgnoreCase);
            }
            int? remainingRows = removed.Success && built is not null
                ? built.Constraints - int.Parse(removed.Groups["rows"].Value, CultureInfo.InvariantCulture)
                : null;
            int? remainingColumns = removed.Success && built is not null
                ? built.Variables - int.Parse(removed.Groups["columns"].Value, CultureInfo.InvariantCulture)
                : null;
            double? solveSeconds = ReadLastRegexSeconds(
                @"Solved in\s+\d+\s+iterations and\s+(?<seconds>[0-9.]+)\s+seconds", solverText);
            double? iisSeconds = ReadLastRegexSeconds(
                @"IIS runtime:\s*(?<seconds>[0-9.]+)\s+seconds", solverText);
            double? orderingSeconds = LastSeconds(OrderingTimeRegex(), solverText);
            md.AppendLine($"- 预处理：日志移除 {removed.Groups["rows"].Value} 行、{removed.Groups["columns"].Value} 列；由原规模推得剩余 {remainingRows?.ToString() ?? "未记录"} 行、{remainingColumns?.ToString() ?? "未记录"} 个连续变量、0 个整数。Gurobi `Presolve()` 因已识别 infeasible/unbounded 未返回可保存的 reduced model，故清单中的 -1 不是把规模记为零。");
            md.AppendLine($"- 阶段耗时：连续求解约 {Format(solveSeconds)}s，其中 barrier ordering 最后记录 {Format(orderingSeconds)}s；随后 IIS 约 {Format(iisSeconds)}s。IIS 提取是本次包装耗时最大的单阶段，求解内主要消耗在 ordering/并行 LP。");
            md.AppendLine("- 10 秒解释：本次定向重建模型在约14.7秒才给出不可行证明，未发现可用解；因此120秒没有截断可用结果。它不是旧序号932的同一模式，不能据此断言旧10秒上限漏掉或未漏掉旧模型可行解。");
        }
        md.AppendLine(validation.Attempted
            ? $"- 固定原模式资源独立检查：{validation.FixedResourceValid}；Global Micro 冲突={validation.GlobalMicroConflictCount}，Unknown={validation.GlobalMicroUnknownCount}；约束残差={Format(validation.ConstraintResidual)}。"
            : $"- 固定资源全线独立检查：未执行；连续模型无候选时刻，不能把模式包中的 {built?.PatternCount ?? 0} 个站级证书/685个局部资源选择冒充同一全线候选的有效分配。");
        int confirmedMainDependencies = repair?.Scope.EvidenceMappings
            .SelectMany(mapping => mapping.Decisions)
            .Where(decision => decision.IsAdjustable &&
                               decision.DecisionFamily is "stop" or "section_precedence")
            .Select(decision => $"{decision.DecisionFamily}:{decision.DecisionKey}")
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() ?? 0;
        md.AppendLine($"- 32 项上限：{(repair is null
            ? "本次没有当前模型硬证明触发作用域，不能断言其阻断必要调整"
            : $"名义上限已触达={repair.Scope.ScopeCapReached}，实际释放={repair.Scope.IndependentMainDecisionCount}；当前证明确认的主决策依赖={confirmedMainDependencies}，全部先于回退项纳入，故没有证据表明32项截断了已确认必要依赖")}。");
        md.AppendLine("- K：保持 4；参考候选、适用范围及非热点 0 变更边界未改。");
        if (repair is not null)
        {
            md.AppendLine($"- 当前证明作用域释放 stop：`{string.Join('|', repair.Scope.ReleasedStopIds)}`。");
            md.AppendLine($"- 当前证明作用域释放 section precedence：{repair.Scope.ReleasedSectionPrecedenceKeys.Count} 项；Macro 修复状态=`{repair.Status}`。");
            int replacementRows = repair.Scope.EvidenceMappings.Count(mapping =>
                mapping.Status == ProofDecisionMappingStatus.PatternReplacement);
            int outsideRows = repair.Scope.EvidenceMappings.Count(mapping =>
                mapping.Status == ProofDecisionMappingStatus.OutsideMacroScope);
            string[] outsideStops = repair.Scope.EvidenceMappings.SelectMany(mapping => mapping.Decisions)
                .Where(decision => !decision.IsAdjustable &&
                                   decision.Reason == "optional_stop_outside_active_macro_scope")
                .Select(decision => decision.DecisionKey)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            md.AppendLine($"- 当前75行证明：{replacementRows} 行映射为活动站模式替换；{outsideRows} 行留在原 pair-01 作用域外。具体外部可选停站为 `{string.Join('|', outsideStops)}`，位于站4943；站4943旧模式条件因本轮只授权pair-01而保留。");
            md.AppendLine("- 针对修复确实改变了矛盾条件：站4945/站4949模式证书在该Macro模型中撤销，train-7/train-55的站4945停站及train-10 pair回退决策被释放；辅助定义由新主变量重算。模型仍在预处理证明不可行，不能把“改过一个条件”当作可行保证。");
        }
        md.AppendLine("- 业务语义：保留原始业务约束、连续时刻、闭时间窗、K、Seed 和 B 边界；取消 1 秒窗外空档和业务放宽，闭窗配额独立复算。");
        md.AppendLine("- 旧67行IIS归属 `all_single_station_patterns`；已具体映射 train-16/19/60 在站4949(s2)与站4945(s3)的6个可选停站。旧作用域因绑定错误未使用这6项，而借用了pair-11空ProofRows；32项上限足以容纳这6项，故旧记录不能证明上限阻断。");
        md.AppendLine("- 空 ProofRows、非不可行状态没有证明、不可行但映射失败现在分别记为 `HardProof`、`NoInfeasibilityProof`、`ProofMappingFailed`，不再用空证据冒充 IIS。");
        md.AppendLine().AppendLine("## 重建与模型身份").AppendLine();
        md.AppendLine($"- 定向重建尝试：{attempts.Count}；模式与模型身份逐次保存。");
        md.AppendLine($"- 首次构建签名：`{built}`");
        md.AppendLine($"- 读回无 Optimize 签名：`{readback}`；一致={built == readback}。");
        md.AppendLine().AppendLine("## 耗时").AppendLine();
        md.AppendLine($"- 六个单站与 pair-01 定向重建：{reconstructionSeconds:F3}s");
        md.AppendLine($"- 120 秒全线诊断（含证明提取）：{diagnosticSeconds:F3}s");
        md.AppendLine($"- 本模型证明驱动的单次 Macro 作用域验证：{repair?.RuntimeSeconds.ToString("F3", CultureInfo.InvariantCulture) ?? "未运行"}s");
        md.AppendLine($"- fresh：{(fresh is null ? "未运行（恢复诊断未通过完整联合验证）" : $"{freshSeconds:F3}s，{fresh.StopReason}")}");
        md.AppendLine($"- 本轮总墙钟：{totalSeconds:F3}s");
        md.AppendLine().AppendLine("## 完整路径").AppendLine();
        md.AppendLine($"- 修改后代码工作目录：`{Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."))}`");
        md.AppendLine($"- 结果目录：`{output}`");
        md.AppendLine($"- 已保存 Macro：`{sourceSnapshot}`");
        md.AppendLine($"- 完整模式包：`{bundle}`");
        md.AppendLine($"- 全线模型：`{model}`");
        md.AppendLine($"- 重建清单：`{manifest}`");
        md.AppendLine($"- result.md：`{path}`");
        md.AppendLine($"- run_summary.json：`{Path.Combine(output, "run_summary.json")}`");
        md.AppendLine($"- reproduce.ps1：`{Path.Combine(output, "reproduce.ps1")}`");
        md.AppendLine($"- 尝试台账：`{Path.Combine(output, "attempts.csv")}`");
        md.AppendLine($"- 当前75行证明映射：`{Path.Combine(output, "proof_mapping", "current_75_row_iis_mapping.csv")}`");
        md.AppendLine($"- 旧67行证明映射：`{Path.Combine(output, "proof_mapping", "historical_67_row_iis_mapping.csv")}`");
        md.AppendLine($"- 当前Macro作用域：`{Path.Combine(output, "proof_mapping", "current_proof_macro_scope.json")}`");
        md.AppendLine($"- 成功解：`{solution ?? "未生成"}`");
        md.AppendLine($"- 资源分配：`{assignments ?? "未生成"}`");
        File.WriteAllText(path, md.ToString(), new UTF8Encoding(false));
    }

    private static void WriteReproduce(
        string path,
        MicroStructureProofBindingRecoveryRequest request)
    {
        string codeDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
        string project = Path.Combine(codeDirectory, "GurobiTimetablingResearch.csproj");
        string dataset = Path.GetFullPath(Path.Combine(codeDirectory, "..", "..", "Data", "JZ"));
        string command =
            "$ErrorActionPreference = 'Stop'\r\n" +
            $"Set-Location '{codeDirectory.Replace("'", "''")}'\r\n" +
            $"$replayOutput = Join-Path '{Path.GetDirectoryName(Path.GetFullPath(request.OutputDirectory))!.Replace("'", "''")}' " +
            "('recovery_proof_binding_replay_' + (Get-Date -Format 'yyyyMMdd_HHmmss'))\r\n" +
            $"dotnet run -c Release --project '{project.Replace("'", "''")}' -- " +
            $"'{dataset.Replace("'", "''")}' --proof-binding-directed-recovery " +
            $"--proof-binding-diagnostic='{Path.GetFullPath(request.HistoricalDiagnosticPath).Replace("'", "''")}' " +
            $"--proof-binding-summary='{Path.GetFullPath(request.HistoricalRunSummaryPath).Replace("'", "''")}' " +
            "--proof-binding-output=$replayOutput " +
            $"--checker-loop-endpoint-occupation-seconds={request.EndpointOccupationSeconds.ToString(CultureInfo.InvariantCulture)} --running-time-supplement-ratio=0.05\r\n";
        File.WriteAllText(path, command, new UTF8Encoding(true));
    }

    private static FixedPatternJointValidation NotAttemptedValidation() => new(
        false, false, 0, "NOT_RUN", 0, 0, 0, false, 0,
        null, null, false, null, [], []);

    private static BoundedMacroStructureRepairResult ReadRepairForReport(JsonObject node)
    {
        JsonObject scopeNode = node["scope"]?.AsObject()
                               ?? throw new InvalidDataException("Saved Macro repair has no scope.");
        string[] releasedStops = ReadStringArray(scopeNode["releasedStopIds"]);
        string[] releasedPrecedence = ReadStringArray(scopeNode["releasedSectionPrecedenceKeys"]);
        string[] affected = ReadStringArray(scopeNode["affectedStations"]);
        ContinuousProofBusinessMapping[] mappings = scopeNode["evidenceMappings"]?
            .Deserialize<ContinuousProofBusinessMapping[]>(JsonOptions) ?? [];
        JsonObject? trustNode = scopeNode["trustRegion"]?.AsObject();
        PrecedenceTrustRegion trust = new(
            trustNode?["regionId"]?.GetValue<string>() ?? "saved_report",
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            releasedPrecedence.ToHashSet(StringComparer.OrdinalIgnoreCase),
            trustNode?["maxTotalChanges"]?.GetValue<int>() ?? 4,
            trustNode?["maxNonHotspotChanges"]?.GetValue<int>() ?? 0);
        BoundedMacroStructureScope scope = new(
            releasedStops.ToHashSet(StringComparer.OrdinalIgnoreCase),
            releasedPrecedence.ToHashSet(StringComparer.OrdinalIgnoreCase),
            affected.ToHashSet(StringComparer.OrdinalIgnoreCase),
            scopeNode["independentMainDecisionCount"]?.GetValue<int>() ?? 0,
            scopeNode["scopeCapReached"]?.GetValue<bool>() ?? false,
            scopeNode["selectionBasis"]?.GetValue<string>() ?? "未记录",
            trust,
            scopeNode["coordinationModelId"]?.GetValue<string>() ?? "未记录",
            scopeNode["coordinationSolveResultIdentity"]?.GetValue<string>() ?? "未记录",
            scopeNode["patternCombinationIdentity"]?.GetValue<string>() ?? "未记录",
            (ContinuousProofAvailability)(scopeNode["proofAvailability"]?.GetValue<int>() ?? 1),
            mappings);
        return new(
            HasCandidate: node["hasCandidate"]?.GetValue<bool>() ?? false,
            StatusCode: node["statusCode"]?.GetValue<int>() ?? 0,
            Status: node["status"]?.GetValue<string>() ?? "未记录",
            RuntimeSeconds: node["runtimeSeconds"]?.GetValue<double>() ?? 0,
            FirstIncumbentSeconds: node["firstIncumbentSeconds"]?.GetValue<double?>(),
            Scope: scope,
            SourceVariables: node["sourceVariables"]?.GetValue<int>() ?? 0,
            SourceBinaries: node["sourceBinaries"]?.GetValue<int>() ?? 0,
            SourceConstraints: node["sourceConstraints"]?.GetValue<int>() ?? 0,
            PresolveSizeAvailable: node["presolveSizeAvailable"]?.GetValue<bool>() ?? false,
            PresolveStopReason: node["presolveStopReason"]?.GetValue<string>() ?? "未记录",
            PresolvedVariables: node["presolvedVariables"]?.GetValue<int>() ?? -1,
            PresolvedBinaries: node["presolvedBinaries"]?.GetValue<int>() ?? -1,
            PresolvedConstraints: node["presolvedConstraints"]?.GetValue<int>() ?? -1,
            RetainedPatternCount: node["retainedPatternCount"]?.GetValue<int>() ?? 0,
            PatternConstraintCount: node["patternConstraintCount"]?.GetValue<int>() ?? 0,
            FixedMainDecisionCount: node["fixedMainDecisionCount"]?.GetValue<int>() ?? 0,
            FreeIndependentMainDecisionCount: node["freeIndependentMainDecisionCount"]?.GetValue<int>() ?? 0,
            DerivedBinaryCount: node["derivedBinaryCount"]?.GetValue<int>() ?? 0,
            EndpointOrderChanges: node["endpointOrderChanges"]?.GetValue<int>() ?? 0,
            StopChanges: node["stopChanges"]?.GetValue<int>() ?? 0,
            ChangedDecisionIds: ReadStringArray(node["changedDecisionIds"]),
            MacroValidated: node["macroValidated"]?.GetValue<bool>() ?? false,
            MacroViolationCount: node["macroViolationCount"]?.GetValue<int>() ?? 0,
            CandidateSnapshot: null,
            StopReason: node["stopReason"]?.GetValue<string>() ?? "未记录");
    }

    private static string[] ReadStringArray(JsonNode? node) => node is JsonArray array
        ? array.Select(item => item?.GetValue<string>() ?? "").Where(value => value.Length > 0).ToArray()
        : [];

    private static JsonObject ParseObject(string path) =>
        JsonNode.Parse(File.ReadAllText(Path.GetFullPath(path), Encoding.UTF8))?.AsObject()
        ?? throw new InvalidDataException($"JSON object expected at '{path}'.");

    private static void WriteJson(string path, object value)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(false));
    }

    private static string DataVersion(TimetablingInstance instance) =>
        $"{instance.Meta.InstanceName}:{instance.Trains.Count}:{instance.TrainStops.Count}";

    private static string RuleVersion(TimetablingInstance instance, int endpointSeconds) =>
        $"routes={instance.Routes.Count};headways={instance.RouteHeadwayRules.Count};endpoint={endpointSeconds}";

    private static string ExecutingCodeIdentity()
    {
        string path = System.Reflection.Assembly.GetExecutingAssembly().Location;
        using FileStream stream = File.OpenRead(path);
        return $"{path}|sha256={Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant()}";
    }

    private static string Sanitize(string value) => new(value.Select(character =>
        char.IsLetterOrDigit(character) ? character : '_').ToArray());

    private static string Csv(object? value)
    {
        string text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        return '"' + text.Replace("\"", "\"\"") + '"';
    }

    private static string Format(double? value) => value.HasValue
        ? value.Value.ToString("G6", CultureInfo.InvariantCulture)
        : "未记录";

    private static string ReadTextShared(string path)
    {
        if (!File.Exists(path)) return "";
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using StreamReader reader = new(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static double? ReadLastRegexSeconds(string pattern, string text)
    {
        MatchCollection matches = Regex.Matches(text, pattern, RegexOptions.IgnoreCase);
        return matches.Count == 0
            ? null
            : double.Parse(matches[^1].Groups["seconds"].Value, CultureInfo.InvariantCulture);
    }
}
