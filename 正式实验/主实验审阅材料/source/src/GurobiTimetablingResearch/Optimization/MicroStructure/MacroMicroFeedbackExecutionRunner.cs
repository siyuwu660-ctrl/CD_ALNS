using System.Text;
using System.Text.Json;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

/// <summary>
/// Connects a fresh, independently validated complete Macro candidate to the
/// same resumable Macro-Micro feedback loop used by resumed runs.  The first
/// checkpoint is written before any Micro work, so a completed Macro is never
/// lost merely because the process stops during Micro initialization.
/// </summary>
internal static class MacroMicroFeedbackExecutionRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    internal static int StartFromFreshMacro(
        TimetablingInstance instance,
        string datasetDirectory,
        string outputDirectory,
        string macroSnapshotPath,
        double initialMacroSeconds,
        double runningTimeSupplementRatio,
        int endpointOccupationSeconds,
        MacroMicroExecutionOptions options,
        Func<bool>? stopRequested = null)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (options.Mode != MacroMicroExecutionMode.Fresh)
            throw new ArgumentException("Fresh Macro execution requires fresh entry options.", nameof(options));
        string output = Path.GetFullPath(outputDirectory);
        string checkpointPath = MacroMicroFeedbackCheckpointStore.DefaultPath(output);
        MasterSolutionSnapshot macro = MasterSolutionSnapshotStore.Load(
            macroSnapshotPath,
            instance.Meta.InstanceName,
            instance.Trains.Select(train => train.TrainIndex).ToHashSet());
        MacroMicroFeedbackCheckpoint checkpoint = MacroMicroFeedbackCheckpoint.CreateMacroReady(
            instance.Meta.InstanceName,
            datasetDirectory,
            output,
            macroSnapshotPath,
            SnapshotFingerprint.Calculate(macro),
            runningTimeSupplementRatio,
            endpointOccupationSeconds,
            initialMacroSeconds);
        MacroMicroFeedbackCheckpointStore.SaveAtomic(checkpointPath, checkpoint);
        WriteResumeScript(checkpoint, checkpointPath, options);
        return ContinueFromMacroReady(instance, datasetDirectory, checkpointPath, options, stopRequested);
    }

    internal static int Resume(
        TimetablingInstance instance,
        string datasetDirectory,
        MacroMicroExecutionOptions options,
        Func<bool>? stopRequested = null)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (options.Mode != MacroMicroExecutionMode.Resume || string.IsNullOrWhiteSpace(options.CheckpointPath))
            throw new ArgumentException("Resume execution requires --macro-micro-resume and a checkpoint.");
        MacroMicroFeedbackCheckpoint checkpoint = MacroMicroFeedbackCheckpointStore.Load(
            options.CheckpointPath,
            instance.Meta.InstanceName,
            datasetDirectory);
        return checkpoint.Stage switch
        {
            MacroMicroFeedbackCheckpointStage.MacroReady =>
                ContinueFromMacroReady(instance, datasetDirectory, options.CheckpointPath, options, stopRequested),
            MacroMicroFeedbackCheckpointStage.FeedbackLoop =>
                MacroMicroFeedbackLoopRunner.Run(
                    instance,
                    new DynamicHotspotRunRequest(
                        checkpoint.BaselineDirectory,
                        checkpoint.OutputDirectory,
                        options.TotalBudgetSeconds),
                    options.IterationTimeLimitSeconds,
                    options.SaveReserveSeconds,
                    options.CheckpointPath,
                    stopRequested,
                    checkpoint.InitialMacroSeconds),
            MacroMicroFeedbackCheckpointStage.JointFeasible =>
                VerifySavedSuccess(instance, checkpoint),
            _ => throw new InvalidDataException(
                $"Checkpoint stage '{checkpoint.Stage}' is not resumable.")
        };
    }

    private static int ContinueFromMacroReady(
        TimetablingInstance instance,
        string datasetDirectory,
        string checkpointPath,
        MacroMicroExecutionOptions options,
        Func<bool>? stopRequested)
    {
        MacroMicroFeedbackCheckpoint checkpoint = MacroMicroFeedbackCheckpointStore.Load(
            checkpointPath,
            instance.Meta.InstanceName,
            datasetDirectory);
        if (checkpoint.Stage != MacroMicroFeedbackCheckpointStage.MacroReady)
            throw new InvalidDataException($"Expected MacroReady checkpoint, actual={checkpoint.Stage}.");
        if (stopRequested?.Invoke() == true) return 130;
        double previouslyConsumedSeconds = checkpoint.ConsumedFeedbackSeconds;
        System.Diagnostics.Stopwatch feedbackClock = System.Diagnostics.Stopwatch.StartNew();

        string output = checkpoint.OutputDirectory;
        string state = Path.Combine(output, "state");
        string baseline = Path.Combine(state, "fresh_baseline");
        string baselineState = Path.Combine(baseline, "state");
        string patternsDirectory = Path.Combine(baseline, "patterns");
        Directory.CreateDirectory(state);
        Directory.CreateDirectory(baselineState);
        Directory.CreateDirectory(patternsDirectory);

        MasterSolutionSnapshot macro = MasterSolutionSnapshotStore.Load(
            checkpoint.CurrentCandidatePath,
            instance.Meta.InstanceName,
            instance.Trains.Select(train => train.TrainIndex).ToHashSet());
        string fingerprint = SnapshotFingerprint.Calculate(macro);
        if (!string.Equals(fingerprint, checkpoint.CurrentCandidateFingerprint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Macro-ready checkpoint fingerprint does not match its snapshot.");

        using ValidatedSnapshotCarrier carrier = new ValidatedSnapshotCarrierBuilder().Build(
            instance,
            neighborRadius: int.MaxValue,
            macro,
            checkpoint.RunningTimeSupplementRatio,
            endpointOccupationSeconds: checkpoint.EndpointOccupationSeconds);
        WriteJson(Path.Combine(state, "fresh_macro_independent_validation.json"), new
        {
            candidateFingerprint = fingerprint,
            carrier.Validation.IsFeasible,
            violationCount = carrier.Validation.Violations.Count,
            violations = carrier.Validation.Violations,
            validationKind = "fixed_serialized_candidate_complete_macro_independent_check"
        });
        if (!carrier.Validation.IsFeasible || carrier.Validation.Violations.Count != 0)
        {
            SaveTerminal(checkpointPath, checkpoint, "fresh_macro_independent_validation_failed");
            WriteResult(output,
                "fresh_macro_independent_validation_failed",
                "The rolling Macro snapshot did not pass the complete independent Macro check.");
            return 2;
        }

        StationLevelMicroFeasibilitySlaveResult analysis =
            CheckerMicroValidationFactory.CreateSlave(checkpoint.EndpointOccupationSeconds).Solve(carrier.Model);
        int unknownCount = analysis.StationResults.Count(result =>
            result.AssignmentResult.Status == MicroSolveStatus.Unknown);
        IReadOnlyList<VerifiedMicroAssignmentWitness> witnesses =
            VerifiedMicroAssignmentWitnessBuilder.Capture(analysis, fingerprint);
        WriteJson(Path.Combine(state, "fresh_macro_initial_global_micro.json"), new
        {
            candidateFingerprint = fingerprint,
            status = analysis.Status.ToString(),
            analysis.IsFeasible,
            conflictCount = analysis.Conflicts.Count,
            unknownCount,
            witnessCount = witnesses.Count,
            conflicts = analysis.Conflicts,
            routeChoiceDiagnostics = analysis.RouteChoiceDiagnostics,
            stationResults = analysis.StationResults
        });
        string initialBestPath = Path.Combine(state, "best_macro_candidate.snapshot.json");
        MasterSolutionSnapshotStore.Save(initialBestPath, instance.Meta.InstanceName, macro);
        WriteJson(Path.Combine(state, "best_macro_candidate.json"),
            new MacroMicroBestCandidateState(
                fingerprint,
                DirectResumeCandidateScore.FromAnalysis(
                    analysis,
                    "full_line",
                    carrier.Validation.IsFeasible && carrier.Validation.Violations.Count == 0,
                    decisionChangeCount: 0),
                Iteration: 0,
                initialBestPath,
                "initial_complete_macro_full_line_micro_baseline"));

        if (analysis.IsFeasible && analysis.Status == MicroSolveStatus.Feasible &&
            analysis.Conflicts.Count == 0 && unknownCount == 0)
            return SaveAndVerifyInitialJointSolution(
                instance,
                macro,
                analysis,
                witnesses,
                checkpointPath,
                checkpoint with
                {
                    ConsumedFeedbackSeconds = previouslyConsumedSeconds + feedbackClock.Elapsed.TotalSeconds
                });

        IReadOnlyList<MicroWitnessCertificate> patterns = MicroStructureExperimentRunner.BuildCertificates(
            instance,
            macro,
            witnesses,
            checkpoint.EndpointOccupationSeconds);
        string bundlePath = Path.Combine(patternsDirectory, "initial_partial_pattern_bundle.json");
        MicroPatternReconstructionBundle bundle = MicroPatternReconstructionBundle.Create(
            "fresh_macro_initial_verified_station_patterns",
            instance,
            macro,
            checkpoint.CurrentCandidatePath,
            $"{instance.Meta.InstanceName}:{instance.Trains.Count}:{instance.TrainStops.Count}",
            $"routes={instance.Routes.Count};headways={instance.RouteHeadwayRules.Count};endpoint={checkpoint.EndpointOccupationSeconds}",
            typeof(MacroMicroFeedbackExecutionRunner).Assembly.ManifestModule.ModuleVersionId.ToString("D"),
            checkpoint.RunningTimeSupplementRatio,
            checkpoint.EndpointOccupationSeconds,
            patterns.Select(pattern => pattern.StationName).ToArray(),
            patterns);
        MicroPatternReconstructionBundleStore.Save(bundlePath, bundle);

        string[] corridor = ResolveCorridorStationOrder(instance);
        DynamicHotspotEvidence evidence = MacroMicroFeedbackLoopRunner.CreateEvidenceFromStationAnalysisForExecution(
            analysis,
            fingerprint,
            corridor,
            "fresh_macro_initial_global_micro");
        DirectResumeFeedbackSelection? initialFeedbackSelection =
            analysis.Status == MicroSolveStatus.ProvenInfeasible
                ? MacroMicroFeedbackLoopRunner.CreateDirectResumeInitialFeedbackSelection(
                    instance,
                    macro,
                    analysis,
                    checkpoint.EndpointOccupationSeconds)
                : null;
        string proofPath = Path.Combine(baselineState, "last_master_infeasibility_proof.json");
        WriteJson(proofPath, new { scheduledEvidence = evidence });
        WriteJson(Path.Combine(state, "fresh_macro_initial_feedback_selection.json"), new
        {
            candidateFingerprint = fingerprint,
            selection = initialFeedbackSelection,
            scheduling = initialFeedbackSelection?.Pending is null
                ? "no_safe_direct_feedback_use_proof_linked_hotspot"
                : "direct_complete_macro_feedback_before_integrated_hotspot"
        });
        WriteJson(Path.Combine(baseline, "run_summary.json"), new
        {
            version = "macro_micro_fresh_bootstrap_v1",
            input = new
            {
                dataset = checkpoint.DatasetDirectory,
                sourcePath = checkpoint.CurrentCandidatePath,
                bundlePath,
                macroFingerprint = fingerprint
            },
            initialGlobalMicro = new
            {
                status = analysis.Status.ToString(),
                conflictCount = analysis.Conflicts.Count,
                unknownCount,
                witnessCount = witnesses.Count,
                patternCount = patterns.Count
            }
        });

        MacroMicroFeedbackCheckpoint nextCheckpoint = checkpoint with
        {
            BaselineDirectory = baseline,
            ConsumedFeedbackSeconds = previouslyConsumedSeconds + feedbackClock.Elapsed.TotalSeconds,
            Status = evidence.Status == DynamicHotspotProofStatus.HardProof
                ? "fresh_micro_proof_ready"
                : "fresh_micro_unknown_or_unmapped",
            CurrentEvidence = evidence
        };
        if (initialFeedbackSelection is not null)
            nextCheckpoint = AttachInitialConflictCompression(nextCheckpoint, initialFeedbackSelection);
        if (evidence.Status != DynamicHotspotProofStatus.HardProof &&
            nextCheckpoint.PendingMacroFeedback is null)
        {
            MacroMicroFeedbackCheckpointStore.SaveAtomic(checkpointPath, nextCheckpoint);
            WriteResult(output,
                "fresh_micro_unknown_or_unmapped",
                "Initial Global Micro did not provide a proven, mapped repair direction; no permanent cut was created.");
            return 1;
        }
        nextCheckpoint = nextCheckpoint with
        {
            Stage = MacroMicroFeedbackCheckpointStage.FeedbackLoop,
            Status = nextCheckpoint.PendingMacroFeedback is null
                ? "feedback_loop_ready"
                : "initial_conflict_compression_ready"
        };
        MacroMicroFeedbackCheckpointStore.SaveAtomic(checkpointPath, nextCheckpoint);
        if (stopRequested?.Invoke() == true) return 130;

        return MacroMicroFeedbackLoopRunner.Run(
            instance,
            new DynamicHotspotRunRequest(
                baseline,
                output,
                options.TotalBudgetSeconds,
                ProofPath: proofPath),
            options.IterationTimeLimitSeconds,
            options.SaveReserveSeconds,
            resumeCheckpointPath: checkpointPath,
            stopRequested,
            checkpoint.InitialMacroSeconds);

    }

    internal static MacroMicroFeedbackCheckpoint AttachInitialConflictCompression(
        MacroMicroFeedbackCheckpoint checkpoint,
        DirectResumeFeedbackSelection selection)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(selection);

        MacroRepairFeedback[] permanent = checkpoint.PermanentFeedback
            .Concat(selection.RetainImmediately.Where(item => item.IsPermanent))
            .GroupBy(item => item.FeedbackId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        MacroRepairFeedback[] temporary = checkpoint.TemporarySearchFeedback
            .Concat(selection.RetainImmediately.Where(item => !item.IsPermanent))
            .GroupBy(item => item.FeedbackId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        return checkpoint with
        {
            PermanentFeedback = permanent,
            TemporarySearchFeedback = temporary,
            PendingMacroFeedback = selection.Pending,
            Status = selection.Pending is null
                ? checkpoint.Status
                : "initial_global_micro_feedback_ready"
        };
    }

    private static int SaveAndVerifyInitialJointSolution(
        TimetablingInstance instance,
        MasterSolutionSnapshot macro,
        StationLevelMicroFeasibilitySlaveResult analysis,
        IReadOnlyList<VerifiedMicroAssignmentWitness> witnesses,
        string checkpointPath,
        MacroMicroFeedbackCheckpoint checkpoint)
    {
        string fingerprint = SnapshotFingerprint.Calculate(macro);
        MacroMicroJointIdentityCheck identity = MacroMicroJointAcceptancePolicy.CheckIdentity(
            fingerprint,
            instance.TrainStops.Select(stop => stop.StopId),
            witnesses);
        VerifiedMicroAssignmentValidationReport fixedValidation = VerifiedMicroAssignmentValidator.Validate(
            instance,
            macro,
            witnesses,
            analysis,
            checkpoint.EndpointOccupationSeconds);
        MasterSolutionSnapshot candidate = macro with { VerifiedMicroAssignments = witnesses };
        string successPath = Path.Combine(checkpoint.OutputDirectory, "state", "joint_feasible.snapshot.json");
        bool complete = identity.IsValid && fixedValidation.IsValid &&
                        witnesses.Count == instance.TrainStops.Count;
        if (complete)
        {
            MasterSolutionSnapshotStore.Save(successPath, instance.Meta.InstanceName, candidate);
            MasterSolutionSnapshot readback = MasterSolutionSnapshotStore.Load(
                successPath,
                instance.Meta.InstanceName,
                instance.Trains.Select(train => train.TrainIndex).ToHashSet());
            complete = string.Equals(SnapshotFingerprint.Calculate(readback), fingerprint,
                           StringComparison.OrdinalIgnoreCase) &&
                       (readback.VerifiedMicroAssignments?.Count ?? 0) == instance.TrainStops.Count;
        }
        if (!complete)
        {
            SaveTerminal(checkpointPath, checkpoint, "initial_joint_validation_failed");
            WriteResult(checkpoint.OutputDirectory,
                "initial_joint_validation_failed",
                "Initial Macro had a station assignment, but identity/fixed-resource readback validation failed.");
            return 2;
        }

        MacroMicroFeedbackCheckpointStore.SaveAtomic(checkpointPath, checkpoint with
        {
            Stage = MacroMicroFeedbackCheckpointStage.JointFeasible,
            CurrentCandidatePath = successPath,
            CurrentCandidateFingerprint = fingerprint,
            Status = "strict_joint_feasible"
        });
        WriteJson(Path.Combine(checkpoint.OutputDirectory, "run_summary.json"), new
        {
            version = "macro_micro_fresh_bootstrap_v1",
            finalStatus = "strict_joint_feasible",
            strictJointFeasible = true,
            candidateFingerprint = fingerprint,
            assignmentCount = witnesses.Count,
            checkpointPath,
            checkpoint.InitialMacroSeconds
        });
        WriteResult(checkpoint.OutputDirectory,
            "strict_joint_feasible",
            "The first complete Macro candidate and its same-candidate resource assignment passed readback.");
        return 0;
    }

    private static int VerifySavedSuccess(
        TimetablingInstance instance,
        MacroMicroFeedbackCheckpoint checkpoint)
    {
        MasterSolutionSnapshot candidate = MasterSolutionSnapshotStore.Load(
            checkpoint.CurrentCandidatePath,
            instance.Meta.InstanceName,
            instance.Trains.Select(train => train.TrainIndex).ToHashSet());
        bool valid = string.Equals(
                         SnapshotFingerprint.Calculate(candidate),
                         checkpoint.CurrentCandidateFingerprint,
                         StringComparison.OrdinalIgnoreCase) &&
                     (candidate.VerifiedMicroAssignments?.Count ?? 0) == instance.TrainStops.Count;
        if (!valid)
            throw new InvalidDataException("Saved joint-feasible checkpoint failed identity/completeness readback.");
        Console.WriteLine($"Joint-feasible result already exists: {checkpoint.CurrentCandidatePath}");
        return 0;
    }

    private static void SaveTerminal(
        string checkpointPath,
        MacroMicroFeedbackCheckpoint checkpoint,
        string status) => MacroMicroFeedbackCheckpointStore.SaveAtomic(
        checkpointPath,
        checkpoint with { Stage = MacroMicroFeedbackCheckpointStage.Terminal, Status = status });

    private static string[] ResolveCorridorStationOrder(TimetablingInstance instance)
    {
        TrainRecord? longest = instance.Trains
            .OrderByDescending(train => train.OrderedStops.Count)
            .ThenBy(train => train.TrainIndex)
            .FirstOrDefault();
        return longest?.OrderedStops.Select(stop => stop.StationName)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            ?? instance.Stations.ToArray();
    }

    private static void WriteResult(string output, string status, string reason)
    {
        Directory.CreateDirectory(output);
        File.WriteAllText(
            Path.Combine(output, "result.md"),
            $"# Macro-Micro run\n\n- status: `{status}`\n- reason: {reason}\n",
            new UTF8Encoding(false));
    }

    private static void WriteResumeScript(
        MacroMicroFeedbackCheckpoint checkpoint,
        string checkpointPath,
        MacroMicroExecutionOptions options)
    {
        string command =
            "dotnet run --no-build -c Release --project .\\src\\GurobiTimetablingResearch\\GurobiTimetablingResearch.csproj -- " +
            $"\"{checkpoint.DatasetDirectory}\" --macro-micro-resume " +
            $"--macro-micro-checkpoint=\"{Path.GetFullPath(checkpointPath)}\" " +
            $"--macro-micro-total-budget-seconds={options.TotalBudgetSeconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} " +
            $"--macro-micro-iteration-seconds={options.IterationTimeLimitSeconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} " +
            $"--macro-micro-save-reserve-seconds={options.SaveReserveSeconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}";
        File.WriteAllText(
            Path.Combine(checkpoint.OutputDirectory, "reproduce.ps1"),
            command + Environment.NewLine,
            new UTF8Encoding(false));
    }

    private static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(false));
    }
}
