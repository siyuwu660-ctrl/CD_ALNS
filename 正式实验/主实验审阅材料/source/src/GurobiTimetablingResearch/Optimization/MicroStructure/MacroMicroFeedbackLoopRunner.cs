using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

internal sealed record MacroMicroFeedbackAttempt(
    int Iteration,
    string ReferenceFingerprint,
    string EvidenceIdentity,
    IReadOnlyList<string> Stations,
    IReadOnlyList<string> StopIds,
    string ScopeStatus,
    int Variables,
    int Binaries,
    int Constraints,
    int PresolvedVariables,
    int PresolvedBinaries,
    int PresolvedConstraints,
    int RetainedHardPatternCount,
    string SolveStatus,
    double BuildSeconds,
    double PresolveSeconds,
    double SolveSeconds,
    string ProofExtractionStrategy,
    double ProofExtractionSeconds,
    double GlobalMicroSeconds,
    int GlobalMicroConflictCount,
    int GlobalMicroUnknownCount,
    string CandidateFingerprint,
    string NextAction,
    double ElapsedSeconds,
    string FeedbackClass = "",
    string FeedbackId = "",
    bool CompleteMacroReplotCalled = false,
    string CompleteMacroReplotStatus = "",
    double CompleteMacroReplotSeconds = 0.0,
    int CompleteMacroDecisionChangeCount = 0,
    string ParentCandidateFingerprint = "",
    string ReplottedCandidateFingerprint = "");

internal sealed record DirectMacroFeedbackRecheck(
    CompleteMacroFeedbackRepairResult Macro,
    CompleteMacroFeedbackRepairResult? TimeFirstMacro,
    MacroRepairFeedback? TimeFirstProposal,
    string MacroRepairStage,
    MasterSolutionSnapshot? Candidate,
    StationLevelMicroFeasibilitySlaveResult? StationAnalysis,
    string RecheckStage,
    string HotspotStatus,
    double MicroSeconds,
    bool MacroValidated,
    int MacroViolationCount,
    IReadOnlyList<VerifiedMicroAssignmentWitness> Witnesses,
    MacroMicroJointIdentityCheck? IdentityCheck,
    MicroFeasibilityReport? IndependentMicro,
    VerifiedMicroAssignmentValidationReport? FixedValidation,
    bool StrictJointFeasible);

internal sealed record StagedMacroFeedbackRepairResult(
    CompleteMacroFeedbackRepairResult Selected,
    CompleteMacroFeedbackRepairResult? TimeFirst,
    MacroRepairFeedback? TimeFirstProposal,
    CompleteMacroFeedbackRepairResult? FullMacro,
    string Stage,
    double RuntimeSeconds);

internal sealed record DirectResumeFeedbackSelection(
    MacroRepairFeedback? Pending,
    IReadOnlyList<MacroRepairFeedback> RetainImmediately,
    string Reason);

internal sealed record DirectResumeBaselineCheck(
    StationLevelMicroFeasibilitySlaveResult Analysis,
    bool MacroValidated,
    int MacroViolationCount,
    double MicroSeconds);

internal sealed record DirectResumeFreshBootstrap(
    IReadOnlyList<MacroRepairFeedback> LoadedRetained,
    DirectResumeBaselineCheck Baseline,
    DirectResumeFeedbackSelection Selection);

internal sealed record DirectFeedbackNoCandidateDecision(
    bool ShouldStop,
    string Status,
    string NextAction);

/// <summary>
/// Starts from a validated saved Macro, jointly exposes exact Micro decisions only
/// on a proof-selected contiguous hotspot, and lets the complete Macro timetable
/// retime around that structure.  A complete Global Micro check selects resources
/// at every station for each candidate and drives the next hotspot.  Independently
/// saved station patterns are starts only; they are never stitched in as hard rows.
/// </summary>
internal static class MacroMicroFeedbackLoopRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    internal static int Run(
        TimetablingInstance instance,
        DynamicHotspotRunRequest request,
        double iterationTimeLimitSeconds = 90.0,
        double? saveReserveSecondsOverride = null,
        string? resumeCheckpointPath = null,
        Func<bool>? stopRequested = null,
        double initialMacroSeconds = 0.0)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (iterationTimeLimitSeconds <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(iterationTimeLimitSeconds));

        Stopwatch totalClock = Stopwatch.StartNew();
        DynamicHotspotResolvedRun resolved = request.Resolve();
        DynamicHotspotModelGate executionGate = CreateExecutionGate();
        double totalBudgetSeconds = resolved.ProbeSeconds;
        double saveReserveSeconds = saveReserveSecondsOverride ??
            Math.Min(30.0, Math.Max(5.0, totalBudgetSeconds * 0.10));
        if (saveReserveSeconds <= 0.0 || saveReserveSeconds >= totalBudgetSeconds)
            throw new ArgumentOutOfRangeException(nameof(saveReserveSecondsOverride));
        Directory.CreateDirectory(resolved.OutputDirectory);
        string logsDirectory = Path.Combine(resolved.OutputDirectory, "logs");
        string stateDirectory = Path.Combine(resolved.OutputDirectory, "state");
        Directory.CreateDirectory(logsDirectory);
        Directory.CreateDirectory(stateDirectory);
        string resultPath = Path.Combine(resolved.OutputDirectory, "result.md");
        string summaryPath = Path.Combine(resolved.OutputDirectory, "run_summary.json");
        string tracePath = Path.Combine(resolved.OutputDirectory, "repair_trace.csv");
        string diagnosticPath = Path.Combine(stateDirectory, "diagnostic_state.json");
        string successPath = Path.Combine(stateDirectory, "joint_feasible.snapshot.json");
        string lastCandidatePath = Path.Combine(stateDirectory, "last_macro_candidate.snapshot.json");
        string bestCandidatePath = Path.Combine(stateDirectory, "best_macro_candidate.snapshot.json");
        string bestCandidateStatePath = Path.Combine(stateDirectory, "best_macro_candidate.json");
        string checkpointPath = string.IsNullOrWhiteSpace(resumeCheckpointPath)
            ? MacroMicroFeedbackCheckpointStore.DefaultPath(resolved.OutputDirectory)
            : Path.GetFullPath(resumeCheckpointPath);
        MacroMicroFeedbackCheckpoint? resumed = string.IsNullOrWhiteSpace(resumeCheckpointPath)
            ? null
            : MacroMicroFeedbackCheckpointStore.Load(
                resumeCheckpointPath,
                instance.Meta.InstanceName,
                resolved.DatasetDirectory);
        if (resumed is not null && resumed.Stage != MacroMicroFeedbackCheckpointStage.FeedbackLoop)
            throw new InvalidDataException(
                $"Feedback-loop resume requires stage FeedbackLoop, actual={resumed.Stage}.");
        if (resumed is not null &&
            (!string.Equals(Path.GetFullPath(resumed.BaselineDirectory), resolved.BaselineDirectory,
                 StringComparison.OrdinalIgnoreCase) ||
             !string.Equals(Path.GetFullPath(resumed.OutputDirectory), resolved.OutputDirectory,
                 StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Checkpoint baseline/output identity does not match the resume request.");
        List<MacroMicroFeedbackAttempt> attempts = [];
        List<MacroRepairFeedback> permanentFeedback = [];
        List<MacroRepairFeedback> temporarySearchFeedback = [];
        MacroRepairFeedback? pendingMacroFeedback = null;
        HashSet<string> attemptedMathematicalStates = new(StringComparer.Ordinal);
        double carriedFeedbackSeconds = resumed?.ConsumedFeedbackSeconds ?? 0.0;
        if (resumed is not null) initialMacroSeconds = resumed.InitialMacroSeconds;
        int nextIteration = resumed?.NextIteration ?? 1;
        double checkpointRunningTimeSupplementRatio = resumed?.RunningTimeSupplementRatio ?? 0.0;
        int checkpointEndpointOccupationSeconds = resumed?.EndpointOccupationSeconds ?? 0;

        double CurrentFeedbackSeconds() => carriedFeedbackSeconds + totalClock.Elapsed.TotalSeconds;

        string finalStatus = "not_started";
        string finalReason = "not_started";
        string finalStatePath = diagnosticPath;
        bool strictJointFeasible = false;
        bool readbackPassed = false;
        int initialPatternCount = 0;
        int initialWitnessCount = 0;
        string[] corridorStationOrder = [];
        DynamicHotspotEvidence? currentEvidence = null;
        DynamicHotspotScopeSelection? lastScope = null;
        MasterSolutionSnapshot? currentReference = null;
        object? finalValidation = null;
        DirectResumeCandidateArchive candidateArchive = new();

        DirectResumeCandidateArchiveDecision ObserveCompleteCandidate(
            MasterSolutionSnapshot candidate,
            StationLevelMicroFeasibilitySlaveResult analysis,
            string recheckStage,
            bool macroValidated,
            int decisionChangeCount,
            int iteration,
            string candidatePath)
        {
            string fingerprint = SnapshotFingerprint.Calculate(candidate);
            DirectResumeCandidateArchiveDecision decision = candidateArchive.Observe(
                new DirectResumeCandidateObservation(
                    fingerprint,
                    DirectResumeCandidateScore.FromAnalysis(
                        analysis, recheckStage, macroValidated, decisionChangeCount),
                    iteration,
                    candidatePath,
                    candidate));
            if (decision.BecameBest)
            {
                MasterSolutionSnapshotStore.Save(
                    bestCandidatePath, instance.Meta.InstanceName, candidate);
                WriteJson(bestCandidateStatePath, new MacroMicroBestCandidateState(
                    fingerprint,
                    decision.Current.Score,
                    iteration,
                    bestCandidatePath,
                    decision.Reason));
            }
            return decision;
        }

        void SaveSafePoint(MacroMicroFeedbackCheckpointStage stage, string status)
        {
            if (currentReference is null) return;
            string candidatePath = stage == MacroMicroFeedbackCheckpointStage.JointFeasible && File.Exists(successPath)
                ? successPath
                : lastCandidatePath;
            if (!string.Equals(candidatePath, successPath, StringComparison.OrdinalIgnoreCase))
                MasterSolutionSnapshotStore.Save(
                    candidatePath, instance.Meta.InstanceName, currentReference);
            MacroMicroFeedbackCheckpointStore.SaveAtomic(
                checkpointPath,
                new MacroMicroFeedbackCheckpoint(
                    MacroMicroFeedbackCheckpoint.CurrentFormatVersion,
                    DateTimeOffset.Now,
                    stage,
                    instance.Meta.InstanceName,
                    resolved.DatasetDirectory,
                    resolved.BaselineDirectory,
                    resolved.OutputDirectory,
                    candidatePath,
                    SnapshotFingerprint.Calculate(currentReference),
                    checkpointRunningTimeSupplementRatio,
                    checkpointEndpointOccupationSeconds,
                    initialMacroSeconds,
                    CurrentFeedbackSeconds(),
                    nextIteration,
                    currentEvidence,
                    permanentFeedback.ToArray(),
                    temporarySearchFeedback.ToArray(),
                    pendingMacroFeedback,
                    attempts.ToArray(),
                    attemptedMathematicalStates.ToArray(),
                    status,
                    typeof(MacroMicroFeedbackLoopRunner).Assembly.ManifestModule.ModuleVersionId.ToString("D")));
        }

        try
        {
            MasterSolutionSnapshot baselineReference = MasterSolutionSnapshotStore.Load(
                resolved.MacroSnapshotPath,
                instance.Meta.InstanceName,
                instance.Trains.Select(train => train.TrainIndex).ToHashSet());
            string sourceFingerprint = SnapshotFingerprint.Calculate(baselineReference);
            if (!string.Equals(sourceFingerprint, resolved.MacroFingerprint, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"Saved Macro identity mismatch: recorded={resolved.MacroFingerprint}, actual={sourceFingerprint}.");

            MicroPatternReconstructionBundle bundle = MicroPatternReconstructionBundleStore.Load(
                resolved.PatternBundlePath, instance, baselineReference);
            checkpointRunningTimeSupplementRatio = bundle.RunningTimeSupplementRatio;
            checkpointEndpointOccupationSeconds = bundle.EndpointOccupationSeconds;
            if (resumed is not null &&
                (Math.Abs(resumed.RunningTimeSupplementRatio - bundle.RunningTimeSupplementRatio) > 1e-12 ||
                 resumed.EndpointOccupationSeconds != bundle.EndpointOccupationSeconds))
                throw new InvalidDataException("Resume checkpoint model semantics do not match its baseline bundle.");
            initialPatternCount = bundle.Patterns.Length;
            initialWitnessCount = bundle.Patterns.Sum(pattern => pattern.Witnesses.Count);
            currentReference = resumed is null
                ? baselineReference
                : MasterSolutionSnapshotStore.Load(
                    resumed.CurrentCandidatePath,
                    instance.Meta.InstanceName,
                    instance.Trains.Select(train => train.TrainIndex).ToHashSet());
            if (resumed is not null && !string.Equals(
                    SnapshotFingerprint.Calculate(currentReference),
                    resumed.CurrentCandidateFingerprint,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Resume checkpoint candidate fingerprint does not match its snapshot.");
            if (resumed is not null)
            {
                attempts.AddRange(resumed.Attempts ?? []);
                permanentFeedback.AddRange(resumed.PermanentFeedback ?? []);
                temporarySearchFeedback.AddRange(resumed.TemporarySearchFeedback ?? []);
                pendingMacroFeedback = resumed.PendingMacroFeedback;
                foreach (string identity in resumed.AttemptedMathematicalStates ?? [])
                    attemptedMathematicalStates.Add(identity);
            }
            if (File.Exists(bestCandidateStatePath) && File.Exists(bestCandidatePath))
            {
                MacroMicroBestCandidateState bestState =
                    JsonSerializer.Deserialize<MacroMicroBestCandidateState>(
                        File.ReadAllText(bestCandidateStatePath), JsonOptions)
                    ?? throw new InvalidDataException("Saved best Macro candidate metadata is invalid.");
                MasterSolutionSnapshot bestSnapshot = MasterSolutionSnapshotStore.Load(
                    bestCandidatePath,
                    instance.Meta.InstanceName,
                    instance.Trains.Select(train => train.TrainIndex).ToHashSet());
                string bestFingerprint = SnapshotFingerprint.Calculate(bestSnapshot);
                if (!string.Equals(bestFingerprint, bestState.CandidateFingerprint,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Saved best Macro candidate identity is inconsistent.");
                candidateArchive.Observe(new DirectResumeCandidateObservation(
                    bestFingerprint,
                    bestState.Score,
                    bestState.Iteration,
                    bestCandidatePath,
                    bestSnapshot));
            }
            MacroMicroFeedbackPatternUse patternUse = MacroMicroFeedbackPolicy.SelectPatternUse(
                bundle.Patterns,
                currentReference,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase));

            // The saved rolling Macro is only accepted as the loop root after a fresh fixed-state
            // solve and the independent Macro validator both accept the exact serialized state.
            using (ValidatedSnapshotCarrier initialCarrier = new ValidatedSnapshotCarrierBuilder().Build(
                       instance,
                       neighborRadius: int.MaxValue,
                       currentReference,
                       bundle.RunningTimeSupplementRatio,
                       endpointOccupationSeconds: bundle.EndpointOccupationSeconds))
            {
                WriteJson(Path.Combine(stateDirectory, "initial_macro_validation.json"), new
                {
                    candidateFingerprint = SnapshotFingerprint.Calculate(currentReference),
                    initialCarrier.Validation.IsFeasible,
                    violationCount = initialCarrier.Validation.Violations.Count,
                    violations = initialCarrier.Validation.Violations,
                    validationKind = "fixed_serialized_candidate_complete_macro_independent_check"
                });
            }

            currentEvidence = resumed?.CurrentEvidence ?? DynamicHotspotEvidenceLoader.Load(
                resolved.ProofPath, instance, bundle.Patterns);
            if (string.IsNullOrWhiteSpace(currentEvidence.SourceCandidateFingerprint))
            {
                currentEvidence = currentEvidence with
                {
                    SourceCandidateFingerprint = SnapshotFingerprint.Calculate(currentReference)
                };
            }
            corridorStationOrder = ResolveCorridorStationOrder(instance);
            DynamicHotspotComponentBuildResult initialComponents = DynamicHotspotComponentFactory.Build(
                instance,
                currentReference,
                corridorStationOrder,
                bundle.EndpointOccupationSeconds,
                currentEvidence.ProofIdentity);
            if (!initialComponents.IsComplete)
                throw new InvalidDataException($"Full hotspot component domain is incomplete: {initialComponents.Status}");
            Dictionary<string, DynamicHotspotStationCost> costs = initialComponents.StationAudits.ToDictionary(
                audit => audit.StationName,
                audit => new DynamicHotspotStationCost(
                    audit.StationName,
                    checked(audit.AlternativeCount + audit.EstimatedOrderVariables),
                    checked(audit.AlternativeCount + audit.EstimatedOrderVariables),
                    audit.EstimatedConstraints),
                StringComparer.OrdinalIgnoreCase);
            IReadOnlyList<VerifiedMicroAssignmentWitness> mipStart = patternUse.StartPatterns
                .SelectMany(pattern => pattern.Witnesses)
                .ToArray();
            SaveSafePoint(MacroMicroFeedbackCheckpointStage.FeedbackLoop,
                resumed is null ? "feedback_loop_initialized" : "feedback_loop_resumed");

            for (int iteration = nextIteration; ; iteration++)
            {
                nextIteration = iteration;
                if (stopRequested?.Invoke() == true)
                {
                    finalStatus = "interrupted_at_safe_point";
                    finalReason = "user_requested_stop; current safe state was persisted";
                    break;
                }
                double remaining = totalBudgetSeconds - CurrentFeedbackSeconds() - saveReserveSeconds;
                if (remaining <= 1.0)
                {
                    finalStatus = "budget_exhausted";
                    finalReason = "shared_budget_reserved_for_state_and_validation";
                    break;
                }
                if (pendingMacroFeedback is not null)
                {
                    string directReferenceFingerprint = SnapshotFingerprint.Calculate(currentReference);
                    MacroRepairFeedback feedback = pendingMacroFeedback;
                    pendingMacroFeedback = null;
                    if (!string.Equals(feedback.SourceCandidateFingerprint, directReferenceFingerprint,
                            StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Pending Macro feedback belongs to another candidate.");
                    double macroLimit = CalculateAutomaticCompleteMacroRepairTimeLimit(
                        remaining,
                        useFullRemainingBudget: false);
                    string feedbackPath = Path.Combine(stateDirectory,
                        $"iteration_{iteration:D3}_direct_macro_feedback.json");
                    WriteJson(feedbackPath, feedback);
                    DirectMacroFeedbackRecheck direct = ExecuteDirectMacroFeedbackRecheck(
                        instance,
                        currentReference,
                        feedback,
                        permanentFeedback.Concat(temporarySearchFeedback).ToArray(),
                        macroLimit,
                        Path.Combine(logsDirectory, $"iteration_{iteration:D3}_direct_complete_macro_replot.log"),
                        bundle.RunningTimeSupplementRatio,
                        bundle.EndpointOccupationSeconds);
                    WriteJson(Path.Combine(stateDirectory,
                        $"iteration_{iteration:D3}_direct_macro_replot_result.json"), direct.Macro);
                    if (direct.TimeFirstMacro is not null)
                        WriteJson(Path.Combine(stateDirectory,
                            $"iteration_{iteration:D3}_direct_time_first_result.json"),
                            direct.TimeFirstMacro);
                    if (direct.TimeFirstProposal is not null)
                        WriteJson(Path.Combine(stateDirectory,
                            $"iteration_{iteration:D3}_direct_time_first_proposal.json"),
                            direct.TimeFirstProposal);
                    string directNextAction;
                    bool directNoCandidateShouldStop = false;
                    string directCandidateFingerprint = direct.Macro.CandidateFingerprint;
                    int conflictCount = direct.StationAnalysis?.Conflicts.Count ?? 0;
                    int unknownCount = direct.StationAnalysis?.StationResults.Count(result =>
                        result.AssignmentResult.Status == MicroSolveStatus.Unknown) ?? 0;
                    if (!direct.Macro.HasCandidate || direct.Candidate is null)
                    {
                        DirectFeedbackNoCandidateDecision failure =
                            ClassifyDirectFeedbackNoCandidate(
                                feedback.IsPermanent,
                                direct.Macro.Status);
                        finalStatus = failure.Status;
                        finalReason = direct.Macro.StopReason;
                        directNextAction = failure.NextAction;
                        directNoCandidateShouldStop = failure.ShouldStop;
                    }
                    else
                    {
                        List<MacroRepairFeedback> acceptedFeedbackLedger = feedback.IsPermanent
                            ? permanentFeedback
                            : temporarySearchFeedback;
                        if (acceptedFeedbackLedger.All(item => !string.Equals(
                                item.FeedbackId, feedback.FeedbackId,
                                StringComparison.OrdinalIgnoreCase)))
                            acceptedFeedbackLedger.Add(feedback);
                        MasterSolutionSnapshot replotted = direct.Candidate;
                        currentReference = replotted;
                        string candidatePath = Path.Combine(stateDirectory,
                            $"macro_candidate_{iteration:D3}_{directCandidateFingerprint[..12]}.snapshot.json");
                        MasterSolutionSnapshotStore.Save(candidatePath, instance.Meta.InstanceName, replotted);
                        MasterSolutionSnapshotStore.Save(lastCandidatePath, instance.Meta.InstanceName, replotted);
                        WriteJson(Path.Combine(stateDirectory,
                            $"macro_candidate_{iteration:D3}_lineage.json"), new
                        {
                            candidateId = directCandidateFingerprint,
                            parentCandidateId = directReferenceFingerprint,
                            sourceFeedback = feedback,
                            direct.Macro.Status,
                            direct.Macro.MacroValidated,
                            direct.Macro.MacroViolationCount,
                            direct.Macro.ChangedDecisionIds,
                            snapshotPath = candidatePath,
                            feedbackMode = "batched_proven_conditional_time_relations",
                            endpointOrderChangeBudget = 4,
                            nonHotspotEndpointChangeBudget = 0
                        });
                        if (direct.StationAnalysis is not null)
                        {
                            WriteMicroCheck(Path.Combine(stateDirectory,
                                    $"iteration_{iteration:D3}_direct_replotted_micro_check.json"),
                                directCandidateFingerprint,
                                direct.RecheckStage,
                                direct.HotspotStatus,
                                direct.StationAnalysis);
                            DirectResumeCandidateArchiveDecision archiveDecision = ObserveCompleteCandidate(
                                replotted,
                                direct.StationAnalysis,
                                direct.RecheckStage,
                                direct.MacroValidated,
                                direct.Macro.ChangedDecisionIds.Count,
                                iteration,
                                candidatePath);
                            WriteJson(Path.Combine(stateDirectory,
                                $"iteration_{iteration:D3}_candidate_archive_decision.json"), new
                            {
                                archiveDecision.BecameBest,
                                archiveDecision.Reason,
                                currentCandidate = archiveDecision.Current.CandidateFingerprint,
                                bestCandidate = archiveDecision.Best?.CandidateFingerprint ?? "",
                                currentScore = archiveDecision.Current.Score,
                                bestScore = archiveDecision.Best?.Score
                            });
                        }
                        if (direct.StrictJointFeasible && direct.StationAnalysis is not null)
                        {
                            replotted = replotted with { VerifiedMicroAssignments = direct.Witnesses };
                            MasterSolutionSnapshotStore.Save(successPath, instance.Meta.InstanceName, replotted);
                            MasterSolutionSnapshot readback = MasterSolutionSnapshotStore.Load(
                                successPath,
                                instance.Meta.InstanceName,
                                instance.Trains.Select(train => train.TrainIndex).ToHashSet());
                            MacroMicroJointIdentityCheck readbackIdentity =
                                MacroMicroJointAcceptancePolicy.CheckIdentity(
                                    directCandidateFingerprint,
                                    instance.TrainStops.Select(stop => stop.StopId),
                                    readback.VerifiedMicroAssignments ?? []);
                            VerifiedMicroAssignmentValidationReport readbackValidation =
                                VerifiedMicroAssignmentValidator.Validate(
                                    instance,
                                    readback,
                                    readback.VerifiedMicroAssignments ?? [],
                                    direct.StationAnalysis,
                                    bundle.EndpointOccupationSeconds);
                            readbackPassed = readbackIdentity.IsValid && readbackValidation.IsValid &&
                                string.Equals(SnapshotFingerprint.Calculate(readback), directCandidateFingerprint,
                                    StringComparison.OrdinalIgnoreCase);
                            strictJointFeasible = readbackPassed;
                            finalStatus = strictJointFeasible ? "strict_joint_feasible" : "joint_validation_failed";
                            finalReason = strictJointFeasible
                                ? "conditional_feedback_replotted_macro_and_same_candidate_resources_passed_readback"
                                : "conditional_feedback_candidate_failed_serialized_fixed_resource_readback";
                            finalStatePath = strictJointFeasible ? successPath : diagnosticPath;
                            finalValidation = new
                            {
                                source = "direct_conditional_feedback_recheck",
                                direct.IdentityCheck,
                                direct.FixedValidation,
                                independentMicroStatus = direct.IndependentMicro?.Status.ToString(),
                                independentMicroConflictCount = direct.IndependentMicro?.Conflicts.Count,
                                readbackIdentity,
                                readbackValidation,
                                readbackPassed
                            };
                            directNextAction = strictJointFeasible ? "accept_and_stop" : "stop_validation_failure";
                        }
                        else if (direct.StationAnalysis?.Status == MicroSolveStatus.ProvenInfeasible)
                        {
                            DirectResumeFeedbackSelection selection = SelectNextFeedbackFromFullLineCheck(
                                instance,
                                replotted,
                                direct.StationAnalysis,
                                bundle.EndpointOccupationSeconds,
                                permanentFeedback,
                                temporarySearchFeedback);
                            pendingMacroFeedback = selection.Pending;
                            if (pendingMacroFeedback is not null)
                            {
                                directNextAction =
                                    $"compress_full_line_micro_before_hotspot::{selection.Reason}";
                            }
                            else
                            {
                                currentEvidence = CreateEvidenceFromStationAnalysis(
                                    direct.StationAnalysis, directCandidateFingerprint, corridorStationOrder,
                                    "direct_replotted_global_micro") with
                                {
                                    SourceCandidateFingerprint = directCandidateFingerprint
                                };
                                directNextAction = currentEvidence.Status == DynamicHotspotProofStatus.HardProof
                                    ? "no_new_type_a_relation_schedule_proof_linked_hotspot"
                                    : "stop_replotted_micro_conflict_unmapped";
                                if (currentEvidence.Status != DynamicHotspotProofStatus.HardProof)
                                {
                                    finalStatus = "global_micro_conflict_unmapped";
                                    finalReason = "conditional_feedback_candidate_has_no_adjustable_conflict_mapping";
                                }
                            }
                        }
                        else
                        {
                            finalStatus = "global_micro_unknown";
                            finalReason = "conditional_feedback_recheck_unknown_creates_no_cut";
                            directNextAction = "stop_unknown_without_cut";
                        }
                    }
                    attempts.Add(new MacroMicroFeedbackAttempt(
                        iteration,
                        directReferenceFingerprint,
                        feedback.FeedbackId,
                        feedback.AffectedStations,
                        feedback.StopValuesByStopId.Keys.ToArray(),
                        "direct_batched_conditional_time_feedback",
                        direct.Macro.Variables,
                        direct.Macro.Binaries,
                        direct.Macro.Constraints,
                        direct.Macro.PresolvedVariables,
                        direct.Macro.PresolvedBinaries,
                        direct.Macro.PresolvedConstraints,
                        0,
                        direct.Macro.Status,
                        0,
                        0,
                        direct.Macro.RuntimeSeconds,
                        "all_legal_route_pairs_exact_projection",
                        0,
                        direct.MicroSeconds,
                        conflictCount,
                        unknownCount,
                        directCandidateFingerprint,
                        directNextAction,
                        CurrentFeedbackSeconds(),
                        feedback.Classification.ToString(),
                        feedback.FeedbackId,
                        true,
                        direct.Macro.Status,
                        direct.Macro.RuntimeSeconds,
                        direct.Macro.ChangedDecisionIds.Count,
                        directReferenceFingerprint,
                        directCandidateFingerprint));
                    WriteTrace(tracePath, attempts);
                    nextIteration = iteration + 1;
                    SaveSafePoint(
                        strictJointFeasible
                            ? MacroMicroFeedbackCheckpointStage.JointFeasible
                            : MacroMicroFeedbackCheckpointStage.FeedbackLoop,
                        strictJointFeasible ? "strict_joint_feasible" : "iteration_complete");
                    if (strictJointFeasible || directNoCandidateShouldStop ||
                        finalStatus is "joint_validation_failed" or
                        "global_micro_unknown" or "global_micro_conflict_unmapped" or
                        "complete_macro_replot_unknown")
                        break;
                    continue;
                }
                if (currentEvidence.Status != DynamicHotspotProofStatus.HardProof)
                {
                    finalStatus = "no_valid_repair_evidence";
                    finalReason = $"evidence_status={currentEvidence.Status}";
                    break;
                }

                string referenceFingerprint = SnapshotFingerprint.Calculate(currentReference);
                DynamicHotspotScopeBudget scopeBudget = new(
                    executionGate.MaximumVariables,
                    executionGate.MaximumActiveBinaries,
                    executionGate.MaximumConstraints);
                ProofLinkedConflictCore proofCore = ProofLinkedConflictCoreBuilder.Build(
                    instance, currentReference, currentEvidence, referenceFingerprint);
                ConflictCoreRepairDecision repairDecision = ConflictCoreRepairEscalationPolicy.Select(
                    referenceFingerprint,
                    currentEvidence.ProofIdentity,
                    proofCore.IsComplete,
                    attemptedMathematicalStates);
                DynamicHotspotScopeSelection scope;
                if (repairDecision.Stage == ConflictCoreRepairStage.ProofLinkedCore)
                {
                    scope = DynamicHotspotScopeSelector.SelectProofLinkedForExecution(
                        proofCore, costs, scopeBudget);
                }
                else if (repairDecision.Stage == ConflictCoreRepairStage.DependencyExpandedCore)
                {
                    ProofLinkedConflictCore expanded = ProofLinkedConflictCoreBuilder.ExpandOrderParticipants(
                        instance, currentReference, proofCore);
                    scope = DynamicHotspotScopeSelector.SelectProofLinkedForExecution(
                        expanded, costs, scopeBudget) with { Status = "dependency_expanded_core" };
                }
                else
                {
                    scope = DynamicHotspotScopeSelector.SelectContiguousForExecution(
                        currentEvidence,
                        corridorStationOrder,
                        costs,
                        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
                        scopeBudget) with
                    {
                        Status = $"contiguous_corridor_fallback::{proofCore.Status}"
                    };
                }
                lastScope = scope;
                if (!scope.IsAccepted)
                {
                    finalStatus = "scope_rejected";
                    finalReason = scope.Status;
                    attempts.Add(new MacroMicroFeedbackAttempt(
                        iteration,
                        SnapshotFingerprint.Calculate(currentReference),
                        currentEvidence.ProofIdentity,
                        scope.RequiredStationNames,
                        scope.StopIds,
                        scope.Status,
                        0, 0, 0, 0, 0, 0, 0,
                        "NOT_RUN", 0, 0, 0, "not_requested", 0, 0, 0, 0, "", "stop_scope_gate",
                            CurrentFeedbackSeconds()));
                    WriteTrace(tracePath, attempts);
                    break;
                }

                string stateIdentity = ConflictCoreRepairEscalationPolicy.BuildStateIdentity(
                    referenceFingerprint, currentEvidence.ProofIdentity, repairDecision, scope);
                if (!attemptedMathematicalStates.Add(stateIdentity))
                {
                    finalStatus = "no_new_mathematical_state";
                    finalReason = "same_reference_and_same_joint_scope_would_repeat_unchanged_model";
                    break;
                }

                DynamicHotspotComponentBuildResult currentComponents = DynamicHotspotComponentFactory.Build(
                    instance,
                    currentReference,
                    scope.StationNames,
                    bundle.EndpointOccupationSeconds,
                    currentEvidence.ProofIdentity);
                if (!currentComponents.IsComplete)
                    throw new InvalidDataException(
                        $"Candidate-specific hotspot component domain is incomplete: {currentComponents.Status}");
                HashSet<string> activeStations = scope.StationNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
                PromotedMicroComponent[] components = currentComponents.Components.ToArray();
                IReadOnlyList<MicroWitnessCertificate> activeProtections =
                    MacroMicroFeedbackPolicy.SelectActiveProtections(
                        patternUse.HardConstraintPatterns,
                        currentReference,
                        activeStations);
                double solveLimit = CalculateIntegratedHotspotTimeLimit(
                    remaining,
                    iterationTimeLimitSeconds);
                string logPath = Path.Combine(logsDirectory, $"iteration_{iteration:D3}.log");
                using DynamicHotspotIntegratedCoordinator coordinator = DynamicHotspotIntegratedCoordinator.Build(
                    new DynamicHotspotIntegratedBuildRequest(
                        instance,
                        currentReference,
                        scope,
                        components,
                        activeProtections,
                        executionGate,
                        mipStart,
                        bundle.EndpointOccupationSeconds,
                        bundle.RunningTimeSupplementRatio,
                        EndpointOrderChangeBudget: 4));
                double candidateBatchMicroSeconds = 0.0;
                DynamicHotspotIntegratedBatchSolveResult candidateBatch = coordinator.SolveBatch(
                    solveLimit,
                    logPath,
                    maximumCandidates: 3,
                    _ =>
                    {
                        Stopwatch candidateMicroClock = Stopwatch.StartNew();
                        StationLevelMicroFeasibilitySlaveResult analysis =
                            CheckerMicroValidationFactory.CreateSlave(bundle.EndpointOccupationSeconds)
                                .Solve(coordinator.Model);
                        candidateMicroClock.Stop();
                        candidateBatchMicroSeconds += candidateMicroClock.Elapsed.TotalSeconds;
                        return analysis;
                    });
                DynamicHotspotIntegratedEvaluatedCandidate? selectedBatchCandidate = candidateBatch.Selected;
                DynamicHotspotIntegratedSolveResult solve = selectedBatchCandidate?.Solve ??
                    candidateBatch.TerminalResult ?? throw new InvalidOperationException(
                        "Integrated hotspot candidate batch returned no solve result.");
                WriteJson(Path.Combine(stateDirectory, $"iteration_{iteration:D3}_candidate_batch.json"), new
                {
                    maximumCandidates = 3,
                    candidateBatch.StructuralDiversityConstraintCount,
                    selectedCandidateId = selectedBatchCandidate?.CandidateId ?? "",
                    candidates = candidateBatch.Candidates.Select(item => new
                    {
                        item.CandidateId,
                        item.TimetableSpanSeconds,
                        item.Score.IsCompleteFullLine,
                        item.Score.IsMacroValidated,
                        item.Score.UnknownCount,
                        item.Score.StationAssignmentConflictCount,
                        item.Score.ExactHeadwayConflictCount,
                        item.Score.TotalHeadwayDeficitSeconds,
                        item.Score.TotalConflictCount,
                        item.Score.DecisionChangeCount
                    })
                });
                double globalMicroSeconds = candidateBatchMicroSeconds;
                int globalConflictCount = 0;
                int globalUnknownCount = 0;
                string candidateFingerprint = string.Empty;
                string nextAction;
                string feedbackClass = string.Empty;
                string feedbackId = string.Empty;
                bool completeMacroReplotCalled = false;
                string completeMacroReplotStatus = string.Empty;
                double completeMacroReplotSeconds = 0.0;
                int completeMacroDecisionChangeCount = 0;
                string parentCandidateFingerprint = string.Empty;
                string replottedCandidateFingerprint = string.Empty;

                if (solve.Candidate is not null && solve.MacroValidated)
                {
                    MasterSolutionSnapshot candidate = solve.Candidate;
                    candidateFingerprint = SnapshotFingerprint.Calculate(candidate);
                    string integratedCandidatePath = Path.Combine(stateDirectory,
                        $"integrated_candidate_{iteration:D3}_{candidateFingerprint[..12]}.snapshot.json");
                    MasterSolutionSnapshotStore.Save(
                        integratedCandidatePath, instance.Meta.InstanceName, candidate);
                    MasterSolutionSnapshotStore.Save(lastCandidatePath, instance.Meta.InstanceName, candidate);
                    currentReference = candidate;

                    StationLevelMicroFeasibilitySlaveResult stationAnalysis =
                        selectedBatchCandidate?.MicroAnalysis ?? throw new InvalidOperationException(
                            "A selected integrated candidate has no full-line Micro analysis.");
                    globalConflictCount = stationAnalysis.Conflicts.Count;
                    globalUnknownCount = stationAnalysis.StationResults.Count(result =>
                        result.AssignmentResult.Status == MicroSolveStatus.Unknown);
                    WriteMicroCheck(Path.Combine(stateDirectory,
                            $"iteration_{iteration:D3}_integrated_candidate_micro_check.json"),
                        candidateFingerprint,
                        "full_line",
                        stationAnalysis.Status.ToString(),
                        stationAnalysis);
                    DirectResumeCandidateArchiveDecision integratedArchiveDecision = ObserveCompleteCandidate(
                        candidate,
                        stationAnalysis,
                        "full_line",
                        solve.MacroValidated,
                        selectedBatchCandidate?.Score.DecisionChangeCount ?? 0,
                        iteration,
                        integratedCandidatePath);
                    WriteJson(Path.Combine(stateDirectory,
                        $"iteration_{iteration:D3}_integrated_candidate_archive_decision.json"), new
                    {
                        integratedArchiveDecision.BecameBest,
                        integratedArchiveDecision.Reason,
                        currentCandidate = integratedArchiveDecision.Current.CandidateFingerprint,
                        bestCandidate = integratedArchiveDecision.Best?.CandidateFingerprint ?? "",
                        currentScore = integratedArchiveDecision.Current.Score,
                        bestScore = integratedArchiveDecision.Best?.Score
                    });

                    if (stationAnalysis.IsFeasible &&
                        stationAnalysis.Status == MicroSolveStatus.Feasible &&
                        globalConflictCount == 0 &&
                        globalUnknownCount == 0)
                    {
                        Stopwatch validationClock = Stopwatch.StartNew();
                        IReadOnlyList<VerifiedMicroAssignmentWitness> witnesses =
                            VerifiedMicroAssignmentWitnessBuilder.Capture(stationAnalysis, candidateFingerprint);
                        candidate = candidate with { VerifiedMicroAssignments = witnesses };
                        using ValidatedSnapshotCarrier validationCarrier =
                            new ValidatedSnapshotCarrierBuilder().Build(
                                instance,
                                neighborRadius: int.MaxValue,
                                candidate,
                                bundle.RunningTimeSupplementRatio,
                                endpointOccupationSeconds: bundle.EndpointOccupationSeconds);
                        MicroFeasibilityReport independentMicro =
                            CheckerMicroValidationFactory.CreateChecker(bundle.EndpointOccupationSeconds)
                                .Validate(validationCarrier.Model);
                        VerifiedMicroAssignmentValidationReport fixedValidation =
                            VerifiedMicroAssignmentValidator.Validate(
                                instance, candidate, witnesses, stationAnalysis, bundle.EndpointOccupationSeconds);
                        int distinctRequirements = witnesses.Select(witness => witness.RequirementId)
                            .Distinct(StringComparer.OrdinalIgnoreCase).Count();
                        strictJointFeasible =
                            solve.MacroViolationCount == 0 &&
                            independentMicro.IsFeasible &&
                            independentMicro.Status == MicroSolveStatus.Feasible &&
                            independentMicro.Conflicts.Count == 0 &&
                            fixedValidation.IsValid &&
                            witnesses.Count == instance.TrainStops.Count &&
                            distinctRequirements == instance.TrainStops.Count;
                        if (strictJointFeasible)
                        {
                            MasterSolutionSnapshotStore.Save(successPath, instance.Meta.InstanceName, candidate);
                            MasterSolutionSnapshot readback = MasterSolutionSnapshotStore.Load(
                                successPath,
                                instance.Meta.InstanceName,
                                instance.Trains.Select(train => train.TrainIndex).ToHashSet());
                            VerifiedMicroAssignmentValidationReport readbackValidation =
                                VerifiedMicroAssignmentValidator.Validate(
                                    instance,
                                    readback,
                                    readback.VerifiedMicroAssignments ?? [],
                                    stationAnalysis,
                                    bundle.EndpointOccupationSeconds);
                            readbackPassed =
                                string.Equals(SnapshotFingerprint.Calculate(readback), candidateFingerprint,
                                    StringComparison.OrdinalIgnoreCase) &&
                                readbackValidation.IsValid &&
                                (readback.VerifiedMicroAssignments?.Count ?? 0) == instance.TrainStops.Count;
                            strictJointFeasible &= readbackPassed;
                            finalValidation = new
                            {
                                validationSeconds = validationClock.Elapsed.TotalSeconds,
                                macroViolationCount = solve.MacroViolationCount,
                                independentMicroStatus = independentMicro.Status.ToString(),
                                independentMicroConflictCount = independentMicro.Conflicts.Count,
                                assignmentCount = witnesses.Count,
                                distinctRequirementCount = distinctRequirements,
                                fixedValidation,
                                readbackPassed
                            };
                        }
                        validationClock.Stop();
                        if (strictJointFeasible)
                        {
                            finalStatus = "strict_joint_feasible";
                            finalReason =
                                $"same_full_line_times_and_{instance.TrainStops.Count}_fixed_resource_assignments_passed_independent_validation";
                            finalStatePath = successPath;
                            nextAction = "accept_and_stop";
                        }
                        else
                        {
                            finalStatus = "joint_validation_failed";
                            finalReason = "global_assignment_found_but_fixed_assignment_or_readback_validation_failed";
                            nextAction = "stop_validation_failure";
                        }
                    }
                    else if (stationAnalysis.Status == MicroSolveStatus.ProvenInfeasible)
                    {
                        IReadOnlyList<VerifiedMicroAssignmentWitness> healthyStarts =
                            VerifiedMicroAssignmentWitnessBuilder.Capture(stationAnalysis, candidateFingerprint);
                        if (healthyStarts.Count > 0) mipStart = healthyStarts;
                        currentReference = candidate;
                        DirectResumeFeedbackSelection selection = SelectNextFeedbackFromFullLineCheck(
                            instance,
                            candidate,
                            stationAnalysis,
                            bundle.EndpointOccupationSeconds,
                            permanentFeedback,
                            temporarySearchFeedback);
                        pendingMacroFeedback = selection.Pending;
                        if (pendingMacroFeedback is not null)
                        {
                            nextAction = $"compress_full_line_micro_before_next_hotspot::{selection.Reason}";
                        }
                        else
                        {
                            currentEvidence = CreateEvidenceFromStationAnalysis(
                                stationAnalysis, candidateFingerprint, corridorStationOrder, "global_micro") with
                            {
                                SourceCandidateFingerprint = candidateFingerprint
                            };
                            nextAction = currentEvidence.Status == DynamicHotspotProofStatus.HardProof
                                ? "replot_macro_with_next_global_micro_hotspot"
                                : "stop_unmapped_global_micro_conflict";
                            if (currentEvidence.Status != DynamicHotspotProofStatus.HardProof)
                            {
                                finalStatus = "global_micro_conflict_unmapped";
                                finalReason = "proven_global_micro_failure_has_no_adjustable_station_mapping";
                            }
                        }
                    }
                    else
                    {
                        finalStatus = "global_micro_unknown";
                        finalReason = "global_micro_timeout_or_unknown_does_not_create_repair_evidence";
                        nextAction = "stop_unknown_without_cut";
                    }
                }
                else if (solve.Status == "INFEASIBLE" && solve.ProofRows.Count > 0)
                {
                    string proofIdentity = CalculateIdentity(
                        "integrated_iis",
                        referenceFingerprint,
                        solve.ProofRows.Select(row => $"{row.ConstraintName}:{row.BusinessKind}:{row.BusinessId}"));
                    currentEvidence = MacroMicroFeedbackPolicy.CreateIntegratedInfeasibilityEvidence(
                        instance,
                        proofIdentity,
                        scope.StationNames,
                        solve.ProofRows,
                        scope.StopIds,
                        corridorStationOrder) with
                    {
                        SourceCandidateFingerprint = referenceFingerprint
                    };
                    WriteJson(Path.Combine(stateDirectory, $"iteration_{iteration:D3}_proof.json"), new
                    {
                        proofIdentity,
                        solve.ProofExtractionStrategy,
                        solve.ProofExtractionSeconds,
                        sourceRows = solve.ProofRows,
                        scheduledEvidence = currentEvidence,
                        note = "The complete bounded proof is retained. It is not promoted to a route-independent permanent cut."
                    });

                    MacroRepairFeedback? feedback = MacroRepairFeedbackFactory.FromFixedPatternProof(
                        instance,
                        currentReference,
                        referenceFingerprint,
                        $"integrated_hotspot::{scope.ProofIdentity}::{string.Join('|', scope.StationNames)}",
                        proofIdentity,
                        MicroSolveStatus.ProvenInfeasible,
                        scope.StationNames,
                        solve.ProofRows,
                        []);
                    if (feedback is null)
                    {
                        nextAction = currentEvidence.Status == DynamicHotspotProofStatus.HardProof
                            ? "no_macro_condition_mapped_expand_joint_scope"
                            : "stop_integrated_iis_mapping_failed";
                        if (currentEvidence.Status != DynamicHotspotProofStatus.HardProof)
                        {
                            finalStatus = "integrated_iis_not_actionable";
                            finalReason = $"mapped_status={currentEvidence.Status}";
                        }
                    }
                    else
                    {
                        feedbackClass = feedback.Classification.ToString();
                        feedbackId = feedback.FeedbackId;
                        completeMacroReplotCalled = true;
                        parentCandidateFingerprint = referenceFingerprint;
                        WriteJson(Path.Combine(stateDirectory, $"iteration_{iteration:D3}_macro_feedback.json"),
                            feedback);
                        double macroRemaining = totalBudgetSeconds - CurrentFeedbackSeconds() - saveReserveSeconds;
                        double macroLimit = CalculateAutomaticCompleteMacroRepairTimeLimit(
                            macroRemaining,
                            useFullRemainingBudget: false);
                        MacroRepairFeedback[] retainedSearchFeedback = permanentFeedback
                            .Concat(temporarySearchFeedback)
                            .ToArray();
                        CompleteMacroFeedbackRepairResult macro = CompleteMacroFeedbackRepairSolver.Solve(
                            instance,
                            currentReference,
                            feedback,
                            retainedSearchFeedback,
                            macroLimit,
                            Path.Combine(logsDirectory, $"iteration_{iteration:D3}_complete_macro_replot.log"),
                            bundle.RunningTimeSupplementRatio,
                            bundle.EndpointOccupationSeconds,
                            endpointOrderChangeBudget: 4);
                        if (!macro.HasCandidate && macro.Status == "INFEASIBLE" &&
                            temporarySearchFeedback.Count > 0)
                        {
                            // Type-B history is a run-local anti-cycle device, not a proof.  If its
                            // conjunction blocks the complete Macro, retract it and keep only proven
                            // feedback plus the current candidate-bound proposal.
                            WriteJson(Path.Combine(stateDirectory,
                                $"iteration_{iteration:D3}_temporary_feedback_retraction.json"), new
                            {
                                reason = "retained_type_b_conjunction_made_complete_macro_infeasible",
                                retractedFeedbackIds = temporarySearchFeedback.Select(item => item.FeedbackId),
                                retainedPermanentFeedbackIds = permanentFeedback.Select(item => item.FeedbackId)
                            });
                            temporarySearchFeedback.Clear();
                            macro = CompleteMacroFeedbackRepairSolver.Solve(
                                instance,
                                currentReference,
                                feedback,
                                permanentFeedback,
                            CalculateAutomaticCompleteMacroRepairTimeLimit(
                                totalBudgetSeconds - CurrentFeedbackSeconds() - saveReserveSeconds,
                                useFullRemainingBudget: true),
                                Path.Combine(logsDirectory,
                                    $"iteration_{iteration:D3}_complete_macro_replot_after_tabu_retraction.log"),
                                bundle.RunningTimeSupplementRatio,
                                bundle.EndpointOccupationSeconds,
                                endpointOrderChangeBudget: 4);
                        }
                        completeMacroReplotStatus = macro.Status;
                        completeMacroReplotSeconds = macro.RuntimeSeconds;
                        completeMacroDecisionChangeCount = macro.ChangedDecisionIds.Count;
                        replottedCandidateFingerprint = macro.CandidateFingerprint;
                        WriteJson(Path.Combine(stateDirectory, $"iteration_{iteration:D3}_macro_replot_result.json"), macro);

                        if (!macro.HasCandidate || macro.CandidateSnapshot is null)
                        {
                            nextAction = macro.Status == "INFEASIBLE"
                                ? "withdraw_temporary_proposal_and_use_proof_linked_scope_expansion"
                                : "stop_complete_macro_replot_unknown_without_permanent_cut";
                            if (macro.Status != "INFEASIBLE")
                            {
                                finalStatus = "complete_macro_replot_unknown";
                                finalReason = macro.StopReason;
                            }
                        }
                        else
                        {
                            if (feedback.IsPermanent)
                            {
                                if (permanentFeedback.All(item => item.FeedbackId != feedback.FeedbackId))
                                    permanentFeedback.Add(feedback);
                            }
                            else if (temporarySearchFeedback.All(item => item.FeedbackId != feedback.FeedbackId))
                            {
                                temporarySearchFeedback.Add(feedback);
                            }
                            MasterSolutionSnapshot replotted = macro.CandidateSnapshot;
                            candidateFingerprint = macro.CandidateFingerprint;
                            string candidatePath = Path.Combine(
                                stateDirectory,
                                $"macro_candidate_{iteration:D3}_{candidateFingerprint[..12]}.snapshot.json");
                            MasterSolutionSnapshotStore.Save(candidatePath, instance.Meta.InstanceName, replotted);
                            MasterSolutionSnapshotStore.Save(lastCandidatePath, instance.Meta.InstanceName, replotted);
                            WriteJson(Path.Combine(stateDirectory, $"macro_candidate_{iteration:D3}_lineage.json"), new
                            {
                                candidateId = candidateFingerprint,
                                parentCandidateId = referenceFingerprint,
                                instance = instance.Meta,
                                sourceFeedback = feedback,
                                macro.Status,
                                macro.MacroValidated,
                                macro.MacroViolationCount,
                                macro.ChangedDecisionIds,
                                snapshotPath = candidatePath,
                                historicalMicroPatternsHardBound = false,
                                endpointOrderChangeBudget = 4,
                                nonHotspotEndpointChangeBudget = 0
                            });

                            Stopwatch recheckClock = Stopwatch.StartNew();
                            using ValidatedSnapshotCarrier carrier = new ValidatedSnapshotCarrierBuilder().Build(
                                instance,
                                neighborRadius: int.MaxValue,
                                replotted,
                                bundle.RunningTimeSupplementRatio,
                                endpointOccupationSeconds: bundle.EndpointOccupationSeconds);
                            StationLevelMicroFeasibilitySlave slave =
                                CheckerMicroValidationFactory.CreateSlave(bundle.EndpointOccupationSeconds);
                            StationLevelMicroFeasibilitySlaveResult hotspotCheck = slave.Solve(
                                carrier.Model,
                                new MicroFeasibilitySlaveRequest(activeStations));
                            StationLevelMicroFeasibilitySlaveResult stationAnalysis = hotspotCheck;
                            string recheckStage = "hotspot";
                            if (hotspotCheck.IsFeasible && hotspotCheck.Status == MicroSolveStatus.Feasible)
                            {
                                stationAnalysis = slave.Solve(carrier.Model);
                                recheckStage = "full_line";
                            }
                            recheckClock.Stop();
                            globalMicroSeconds = recheckClock.Elapsed.TotalSeconds;
                            globalConflictCount = stationAnalysis.Conflicts.Count;
                            globalUnknownCount = stationAnalysis.StationResults.Count(result =>
                                result.AssignmentResult.Status == MicroSolveStatus.Unknown);
                            WriteMicroCheck(Path.Combine(stateDirectory,
                                    $"iteration_{iteration:D3}_replotted_micro_check.json"),
                                candidateFingerprint,
                                recheckStage,
                                hotspotCheck.Status.ToString(),
                                stationAnalysis);
                            DirectResumeCandidateArchiveDecision replotArchiveDecision = ObserveCompleteCandidate(
                                replotted,
                                stationAnalysis,
                                recheckStage,
                                macro.MacroValidated,
                                macro.ChangedDecisionIds.Count,
                                iteration,
                                candidatePath);
                            WriteJson(Path.Combine(stateDirectory,
                                $"iteration_{iteration:D3}_replotted_candidate_archive_decision.json"), new
                            {
                                replotArchiveDecision.BecameBest,
                                replotArchiveDecision.Reason,
                                currentCandidate = replotArchiveDecision.Current.CandidateFingerprint,
                                bestCandidate = replotArchiveDecision.Best?.CandidateFingerprint ?? "",
                                currentScore = replotArchiveDecision.Current.Score,
                                bestScore = replotArchiveDecision.Best?.Score
                            });

                            if (stationAnalysis.IsFeasible &&
                                stationAnalysis.Status == MicroSolveStatus.Feasible &&
                                globalConflictCount == 0 && globalUnknownCount == 0)
                            {
                                IReadOnlyList<VerifiedMicroAssignmentWitness> witnesses =
                                    VerifiedMicroAssignmentWitnessBuilder.Capture(
                                        stationAnalysis, candidateFingerprint);
                                MacroMicroJointIdentityCheck identity =
                                    MacroMicroJointAcceptancePolicy.CheckIdentity(
                                        candidateFingerprint,
                                        instance.TrainStops.Select(stop => stop.StopId),
                                        witnesses);
                                MicroFeasibilityReport independentMicro =
                                    CheckerMicroValidationFactory.CreateChecker(bundle.EndpointOccupationSeconds)
                                        .Validate(carrier.Model);
                                VerifiedMicroAssignmentValidationReport fixedValidation =
                                    VerifiedMicroAssignmentValidator.Validate(
                                        instance,
                                        replotted,
                                        witnesses,
                                        stationAnalysis,
                                        bundle.EndpointOccupationSeconds);
                                strictJointFeasible = carrier.Validation.IsFeasible &&
                                    carrier.Validation.Violations.Count == 0 &&
                                    independentMicro.IsFeasible &&
                                    independentMicro.Status == MicroSolveStatus.Feasible &&
                                    independentMicro.Conflicts.Count == 0 &&
                                    fixedValidation.IsValid && identity.IsValid;
                                finalValidation = new
                                {
                                    source = "complete_macro_replot_fixed_candidate_recheck",
                                    macroViolationCount = carrier.Validation.Violations.Count,
                                    independentMicroStatus = independentMicro.Status.ToString(),
                                    independentMicroConflictCount = independentMicro.Conflicts.Count,
                                    identity,
                                    fixedValidation
                                };
                                if (strictJointFeasible)
                                {
                                    replotted = replotted with { VerifiedMicroAssignments = witnesses };
                                    MasterSolutionSnapshotStore.Save(
                                        successPath, instance.Meta.InstanceName, replotted);
                                    MasterSolutionSnapshot readback = MasterSolutionSnapshotStore.Load(
                                        successPath,
                                        instance.Meta.InstanceName,
                                        instance.Trains.Select(train => train.TrainIndex).ToHashSet());
                                    MacroMicroJointIdentityCheck readbackIdentity =
                                        MacroMicroJointAcceptancePolicy.CheckIdentity(
                                            candidateFingerprint,
                                            instance.TrainStops.Select(stop => stop.StopId),
                                            readback.VerifiedMicroAssignments ?? []);
                                    VerifiedMicroAssignmentValidationReport readbackValidation =
                                        VerifiedMicroAssignmentValidator.Validate(
                                            instance,
                                            readback,
                                            readback.VerifiedMicroAssignments ?? [],
                                            stationAnalysis,
                                            bundle.EndpointOccupationSeconds);
                                    readbackPassed = readbackIdentity.IsValid && readbackValidation.IsValid &&
                                        string.Equals(SnapshotFingerprint.Calculate(readback), candidateFingerprint,
                                            StringComparison.OrdinalIgnoreCase);
                                    strictJointFeasible &= readbackPassed;
                                    finalStatus = strictJointFeasible
                                        ? "strict_joint_feasible"
                                        : "joint_validation_failed";
                                    finalReason = strictJointFeasible
                                        ? $"independently_replotted_macro_and_same_candidate_{instance.TrainStops.Count}_resources_passed_readback"
                                        : "replotted_candidate_failed_serialized_fixed_resource_readback";
                                    finalStatePath = strictJointFeasible ? successPath : diagnosticPath;
                                    nextAction = strictJointFeasible ? "accept_and_stop" : "stop_validation_failure";
                                }
                                else
                                {
                                    finalStatus = "joint_validation_failed";
                                    finalReason = "replotted_candidate_failed_independent_joint_validation";
                                    nextAction = "stop_validation_failure";
                                }
                            }
                            else if (stationAnalysis.Status == MicroSolveStatus.ProvenInfeasible)
                            {
                                currentReference = replotted;
                                DirectResumeFeedbackSelection selection = SelectNextFeedbackFromFullLineCheck(
                                    instance,
                                    replotted,
                                    stationAnalysis,
                                    bundle.EndpointOccupationSeconds,
                                    permanentFeedback,
                                    temporarySearchFeedback);
                                pendingMacroFeedback = selection.Pending;
                                if (pendingMacroFeedback is not null)
                                {
                                    nextAction =
                                        $"compress_full_line_micro_before_next_hotspot::{selection.Reason}";
                                }
                                else
                                {
                                    currentEvidence = CreateEvidenceFromStationAnalysis(
                                        stationAnalysis, candidateFingerprint, corridorStationOrder,
                                        "replotted_global_micro") with
                                    {
                                        SourceCandidateFingerprint = candidateFingerprint
                                    };
                                    nextAction = currentEvidence.Status == DynamicHotspotProofStatus.HardProof
                                        ? "replotted_macro_failed_micro_schedule_new_candidate_bound_hotspot"
                                        : "stop_replotted_micro_conflict_unmapped";
                                    if (currentEvidence.Status != DynamicHotspotProofStatus.HardProof)
                                    {
                                        finalStatus = "global_micro_conflict_unmapped";
                                        finalReason = "replotted_candidate_micro_failure_has_no_adjustable_mapping";
                                    }
                                }
                            }
                            else
                            {
                                finalStatus = "global_micro_unknown";
                                finalReason = "replotted_candidate_micro_check_unknown_creates_no_feedback";
                                nextAction = "stop_unknown_without_cut";
                            }
                        }
                    }
                }
                else
                {
                    (finalStatus, finalReason, nextAction) = ClassifyNoCandidate(
                        coordinator.Audit.GatePassed,
                        coordinator.Audit.GateStatus,
                        solve.Status);
                }

                attempts.Add(new MacroMicroFeedbackAttempt(
                    iteration,
                    referenceFingerprint,
                    scope.ProofIdentity,
                    scope.StationNames,
                    scope.StopIds,
                    scope.Status,
                    coordinator.Audit.ActiveVariableCount,
                    coordinator.Audit.ActiveBinaryCount,
                    coordinator.Audit.ActiveConstraintCount,
                    coordinator.Audit.PresolvedVariableCount,
                    coordinator.Audit.PresolvedActiveBinaryCount,
                    coordinator.Audit.PresolvedConstraintCount,
                    coordinator.Audit.RetainedPatternCount,
                    solve.Status,
                    coordinator.Audit.BuildSeconds,
                    coordinator.Audit.PresolveSeconds,
                    solve.RuntimeSeconds,
                    solve.ProofExtractionStrategy,
                    solve.ProofExtractionSeconds,
                    globalMicroSeconds,
                    globalConflictCount,
                    globalUnknownCount,
                    candidateFingerprint,
                    nextAction,
                    CurrentFeedbackSeconds(),
                    feedbackClass,
                    feedbackId,
                    completeMacroReplotCalled,
                    completeMacroReplotStatus,
                    completeMacroReplotSeconds,
                    completeMacroDecisionChangeCount,
                    parentCandidateFingerprint,
                    replottedCandidateFingerprint));
                WriteTrace(tracePath, attempts);
                if (CurrentFeedbackSeconds() >= 3600.0 &&
                    !File.Exists(Path.Combine(stateDirectory, "milestone_3600.json")))
                {
                    WriteJson(Path.Combine(stateDirectory, "milestone_3600.json"), new
                    {
                        elapsedSeconds = CurrentFeedbackSeconds(),
                        currentReferenceFingerprint = SnapshotFingerprint.Calculate(currentReference),
                        currentEvidence,
                        permanentFeedback,
                        temporarySearchFeedback,
                        attempts,
                        note = "Non-overwriting milestone from the same cumulative experiment; the clock was not reset."
                    });
                }
                nextIteration = iteration + 1;
                SaveSafePoint(
                    strictJointFeasible
                        ? MacroMicroFeedbackCheckpointStage.JointFeasible
                        : MacroMicroFeedbackCheckpointStage.FeedbackLoop,
                    strictJointFeasible ? "strict_joint_feasible" : "iteration_complete");

                if (strictJointFeasible ||
                    finalStatus is "joint_validation_failed" or "global_micro_unknown" or
                        "global_micro_conflict_unmapped" or "integrated_iis_not_actionable" or
                        "infeasible_without_mapped_proof" or "integrated_solve_unknown" or
                        "model_size_gate_rejected")
                    break;
            }
        }
        catch (Exception exception)
        {
            finalStatus = "engineering_failure";
            finalReason = exception.Message;
            WriteJson(diagnosticPath, new
            {
                finalStatus,
                finalReason,
                exception = exception.ToString(),
                attempts,
                elapsedSeconds = CurrentFeedbackSeconds()
            });
        }

        totalClock.Stop();
        SaveSafePoint(
            strictJointFeasible
                ? MacroMicroFeedbackCheckpointStage.JointFeasible
                : MacroMicroFeedbackCheckpointStage.FeedbackLoop,
            finalStatus);
        if (!strictJointFeasible)
        {
            WriteJson(diagnosticPath, new
            {
                finalStatus,
                finalReason,
                currentReferenceFingerprint = currentReference is null
                    ? null
                    : SnapshotFingerprint.Calculate(currentReference),
                currentEvidence,
                lastScope,
                bestCandidate = candidateArchive.Best is null
                    ? null
                    : new
                    {
                        candidateArchive.Best.CandidateFingerprint,
                        candidateArchive.Best.Score,
                        candidateArchive.Best.CandidatePath
                    },
                attempts,
                mathematicalMeaning =
                    "Failure or timeout concerns only the executed K=4 feedback-loop scopes; it is not a proof that the original JZ instance is infeasible.",
                elapsedSeconds = CurrentFeedbackSeconds()
            });
        }
        WriteTrace(tracePath, attempts);
        WriteJson(summaryPath, new
        {
            version = "macro_micro_feedback_loop_v4_conflict_compression_before_hotspot",
            method = "saved_validated_macro__full_line_micro_feedback_compression__staged_complete_macro_replot__proof_linked_hotspot_fallback__fixed_candidate_global_micro_recheck",
            input = new
            {
                resolved.DatasetDirectory,
                resolved.MacroSnapshotPath,
                resolved.PatternBundlePath,
                resolved.ProofPath,
                resolved.MacroFingerprint
            },
            boundaries = new
            {
                initialMacroOptimizeCalled = initialMacroSeconds > 0.0,
                resumedFromCheckpoint = resumed is not null,
                savedIndependentStationPatternsHardBound = false,
                savedPatternsUsedAsMipStarts = true,
                initialPatternCount,
                initialWitnessCount,
                expectedWitnessCount = instance.TrainStops.Count,
                partialInitialPatternBundleAllowed = true,
                fullLineContinuousTimesFree = true,
                completeMacroBusinessRules = true,
                modelSizeGateEnabled = false,
                k = 4,
                nonHotspotEndpointChanges = 0,
                b = "off",
                timeoutCreatesPermanentExclusion = false,
                conditionalIisConvertedToUnconditionalTimeCut = false,
                allRouteExactHeadwayProjectionEnabled = true,
                initialFullLineFeedbackCompressionEnabled = true,
                uniqueTrackResourceCoreProjectionEnabled = true,
                bestCandidateSeparatedFromCurrentSearch = true,
                integratedHotspotUsesRemainingSharedBudget = true,
                legacyIterationSecondsIsHardCap = false,
                timeCutPremisesIncludeStopAndStationEventOrder = true,
                completeConflictRowsPersisted = true
            },
            budget = new
            {
                totalBudgetSeconds,
                iterationTimeLimitSeconds,
                saveReserveSeconds,
                initialMacroSeconds,
                consumedFeedbackSeconds = CurrentFeedbackSeconds()
            },
            attempts,
            permanentFeedback,
            temporarySearchFeedback,
            bestCandidate = candidateArchive.Best is null
                ? null
                : new
                {
                    candidateArchive.Best.CandidateFingerprint,
                    candidateArchive.Best.Score,
                    candidateArchive.Best.CandidatePath
                },
            finalStatus,
            finalReason,
            strictJointFeasible,
            readbackPassed,
            finalValidation,
            finalStatePath,
            checkpointPath,
            totalSeconds = CurrentFeedbackSeconds()
        });
        WriteResult(
            resultPath,
            resolved,
            attempts,
            finalStatus,
            finalReason,
            strictJointFeasible,
            readbackPassed,
            finalStatePath,
            CurrentFeedbackSeconds());
        WriteReproduce(
            resolved,
            checkpointPath,
            totalBudgetSeconds,
            iterationTimeLimitSeconds,
            saveReserveSeconds);
        return strictJointFeasible ? 0 : finalStatus == "engineering_failure" ? 3 : 1;
    }

    internal static int ResumeDirectFeedback(
        TimetablingInstance instance,
        string snapshotPath,
        string? feedbackPath,
        IReadOnlyList<string> retainedFeedbackPaths,
        IReadOnlyList<string> runTabuPaths,
        string? conflictReportPath,
        string outputDirectory,
        double totalBudgetSeconds,
        double attemptTimeLimitSeconds,
        int maximumAttempts,
        double runningTimeSupplementRatio,
        int endpointOccupationSeconds)
    {
        if (totalBudgetSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(totalBudgetSeconds));
        if (attemptTimeLimitSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(attemptTimeLimitSeconds));
        if (maximumAttempts <= 0) throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        Directory.CreateDirectory(outputDirectory);
        string stateDirectory = Path.Combine(outputDirectory, "state");
        string logsDirectory = Path.Combine(outputDirectory, "logs");
        Directory.CreateDirectory(stateDirectory);
        Directory.CreateDirectory(logsDirectory);
        MasterSolutionSnapshot current = MasterSolutionSnapshotStore.Load(
            snapshotPath,
            instance.Meta.InstanceName,
            instance.Trains.Select(train => train.TrainIndex).ToHashSet());
        MacroRepairFeedback[] loadedRetained;
        DirectResumeBaselineCheck? freshBootstrapBaseline = null;
        DirectResumeFeedbackSelection initialSelection;
        if (string.IsNullOrWhiteSpace(feedbackPath) && string.IsNullOrWhiteSpace(conflictReportPath))
        {
            DirectResumeFreshBootstrap bootstrap = CreateDirectResumeFreshBootstrap(
                instance,
                current,
                retainedFeedbackPaths,
                runningTimeSupplementRatio,
                endpointOccupationSeconds);
            loadedRetained = bootstrap.LoadedRetained.ToArray();
            freshBootstrapBaseline = bootstrap.Baseline;
            initialSelection = bootstrap.Selection;
        }
        else
        {
            loadedRetained = LoadDirectResumeFeedback(retainedFeedbackPaths);
            initialSelection = CreateDirectResumeInitialFeedbackSelection(
                instance, current, feedbackPath, conflictReportPath, endpointOccupationSeconds);
        }
        MacroRepairFeedback initialPending = initialSelection.Pending ?? throw new InvalidDataException(
            "Saved direct-resume evidence contains no new Macro repair feedback.");
        Queue<MacroRepairFeedback> stagedTrackOccupationFeedback = new();
        MacroRepairFeedback StageTrackOccupationFeedback(MacroRepairFeedback feedback)
        {
            if (!feedback.IsPermanent || feedback.ConditionalTimeCuts.Count > 0 ||
                feedback.ConditionalTrackOccupationCuts.Count <= 1)
                return feedback;
            IReadOnlyList<MacroRepairFeedback> stages =
                SplitProvenTrackOccupationFeedbackByStation(feedback);
            foreach (MacroRepairFeedback stage in stages.Skip(1))
                stagedTrackOccupationFeedback.Enqueue(stage);
            return stages[0];
        }
        MacroRepairFeedback pending = StageTrackOccupationFeedback(initialPending);

        IReadOnlyList<MacroRepairFeedback> preparedRetained =
            PrepareDirectResumeRetainedFeedback(
                loadedRetained.Concat(initialSelection.RetainImmediately).ToArray(), pending);
        MacroRepairFeedback[] loadedRunTabu = runTabuPaths
            .Select(path => JsonSerializer.Deserialize<MacroRepairFeedback>(
                File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidDataException($"Saved run-local tabu is empty or invalid: {path}"))
            .ToArray();
        IReadOnlyList<MacroRepairFeedback> preparedRunTabu =
            PrepareDirectResumeRunTabu(loadedRunTabu);
        if (preparedRunTabu.Any(item => string.Equals(item.FeedbackId, pending.FeedbackId,
                StringComparison.OrdinalIgnoreCase)) ||
            preparedRunTabu.Any(tabu => preparedRetained.Any(proven => string.Equals(
                tabu.FeedbackId, proven.FeedbackId, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException(
                "Direct resume pending, proven, and run-local feedback identities must be distinct.");
        int initialRetainedFeedbackCount = preparedRetained.Count;
        int initialRetainedTimeCutCount = preparedRetained.Sum(item => item.ConditionalTimeCuts.Count);
        int initialRetainedTrackOccupationCutCount = preparedRetained.Sum(
            item => item.ConditionalTrackOccupationCuts.Count);
        int initialRunTabuCount = preparedRunTabu.Count;
        double finalSaveReserveSeconds = Math.Min(
            120.0,
            Math.Max(1.0, totalBudgetSeconds * 0.10));

        Stopwatch clock = Stopwatch.StartNew();
        List<MacroRepairFeedback> retained = [.. preparedRetained, .. preparedRunTabu];
        List<object> attempts = [];
        List<object> branchEvents = [];
        DirectResumeCandidateArchive archive = new();
        DirectResumeExplorationPolicy explorationPolicy = new(maximumDepth: 2);
        DirectResumeCandidateObservation? activeExplorationReference = null;
        MacroRepairFeedback? activeExplorationPending = null;
        int explorationDepth = 0;
        DirectResumeBaselineCheck baseline = freshBootstrapBaseline ?? CheckSavedReferenceForArchive(
            instance, current, retained, runningTimeSupplementRatio, endpointOccupationSeconds);
        string baselineFingerprint = SnapshotFingerprint.Calculate(current);
        string baselineCheckPath = Path.Combine(stateDirectory, "baseline_full_line_micro_check.json");
        WriteMicroCheck(
            baselineCheckPath,
            baselineFingerprint,
            "full_line",
            "baseline_not_hotspot_filtered",
            baseline.Analysis);
        DirectResumeCandidateArchiveDecision baselineArchiveDecision = archive.Observe(
            new DirectResumeCandidateObservation(
                baselineFingerprint,
                DirectResumeCandidateScore.FromAnalysis(
                    baseline.Analysis,
                    "full_line",
                    baseline.MacroValidated,
                    decisionChangeCount: 0),
                0,
                snapshotPath,
                current));
        MacroRepairFeedback? bestPending = baselineArchiveDecision.BecameBest ? pending : null;
        DirectResumeReleaseLevel releaseLevel = pending.IsPermanent &&
            pending.ConditionalTimeCuts.Count + pending.ConditionalTrackOccupationCuts.Count > 0
                ? DirectResumeReleaseLevel.EvidencePremises
                : DirectResumeReleaseLevel.FullMacro;
        string finalStatus = "not_started";
        string finalReason = "not_started";
        string finalState = Path.Combine(stateDirectory, "diagnostic_state.json");
        bool strict = false;
        bool TryAdvanceStagedTrackOccupationFeedback(int iteration, string reason)
        {
            while (stagedTrackOccupationFeedback.Count > 0)
            {
                MacroRepairFeedback staged = stagedTrackOccupationFeedback.Dequeue();
                if (retained.Any(item => string.Equals(
                        item.FeedbackId, staged.FeedbackId, StringComparison.OrdinalIgnoreCase)))
                    continue;
                current = archive.RollbackToBest()?.Snapshot ?? current;
                string referenceFingerprint = SnapshotFingerprint.Calculate(current);
                if (!IsFeedbackBoundToReference(staged, referenceFingerprint))
                {
                    branchEvents.Add(new
                    {
                        iteration,
                        action = "discard_staged_core_after_reference_rollback",
                        bestCandidate = referenceFingerprint,
                        feedbackCandidate = staged.SourceCandidateFingerprint,
                        discardedFeedback = staged.FeedbackId,
                        station = staged.AffectedStations.SingleOrDefault() ?? "",
                        reason = "branch_local_feedback_does_not_belong_to_restored_reference"
                    });
                    continue;
                }
                pending = staged;
                bestPending = staged;
                releaseLevel = DirectResumeReleaseLevel.EvidencePremises;
                activeExplorationReference = null;
                activeExplorationPending = null;
                explorationDepth = 0;
                branchEvents.Add(new
                {
                    iteration,
                    action = "advance_to_next_proven_station_resource_core",
                    bestCandidate = archive.Best?.CandidateFingerprint ?? "",
                    nextFeedback = staged.FeedbackId,
                    station = staged.AffectedStations.SingleOrDefault() ?? "",
                    remainingStagedCoreCount = stagedTrackOccupationFeedback.Count,
                    reason
                });
                return true;
            }
            return false;
        }
        for (int iteration = 1; ; iteration++)
        {
            if (!CanStartDirectResumeAttempt(iteration, maximumAttempts))
            {
                finalStatus = "attempt_limit_reached";
                finalReason = "configured_direct_resume_attempt_limit_reached";
                break;
            }
            double remaining = totalBudgetSeconds - clock.Elapsed.TotalSeconds - finalSaveReserveSeconds;
            if (remaining <= 1.0)
            {
                finalStatus = "budget_exhausted";
                finalReason = "resume_budget_reserved_for_save";
                break;
            }
            string parent = SnapshotFingerprint.Calculate(current);
            IReadOnlyList<MacroRepairFeedback> attemptRetained = retained
                .Where(item => !string.Equals(
                    item.FeedbackId,
                    pending.FeedbackId,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            DirectMacroFeedbackRecheck execution = ExecuteDirectMacroFeedbackRecheck(
                instance,
                current,
                pending,
                attemptRetained,
                CalculateDirectResumeAttemptTimeLimit(remaining, attemptTimeLimitSeconds),
                Path.Combine(logsDirectory, $"direct_resume_{iteration:D3}.log"),
                runningTimeSupplementRatio,
                endpointOccupationSeconds,
                releaseLevel);
            WriteJson(Path.Combine(stateDirectory, $"attempt_{iteration:D3}_feedback.json"), pending);
            WriteJson(Path.Combine(stateDirectory, $"attempt_{iteration:D3}_macro_result.json"), execution.Macro);
            if (execution.TimeFirstMacro is not null)
                WriteJson(Path.Combine(stateDirectory, $"attempt_{iteration:D3}_time_first_result.json"),
                    execution.TimeFirstMacro);
            if (execution.TimeFirstProposal is not null)
                WriteJson(Path.Combine(stateDirectory, $"attempt_{iteration:D3}_time_first_proposal.json"),
                    execution.TimeFirstProposal);
            if (!execution.Macro.HasCandidate || execution.Candidate is null)
            {
                finalStatus = execution.Macro.Status == "INFEASIBLE"
                    ? "macro_with_proven_feedback_infeasible"
                    : "macro_with_proven_feedback_unknown";
                finalReason = execution.Macro.StopReason;
                attempts.Add(new
                {
                    iteration, parent, feedbackId = pending.FeedbackId,
                    releaseLevel = releaseLevel.ToString(),
                    timeCutCount = pending.ConditionalTimeCuts.Count,
                    trackOccupationCutCount = pending.ConditionalTrackOccupationCuts.Count,
                    execution.MacroRepairStage,
                    timeFirstStatus = execution.TimeFirstMacro?.Status ?? "not_run",
                    timeFirstSeconds = execution.TimeFirstMacro?.RuntimeSeconds ?? 0.0,
                    timeFirstProposalId = execution.TimeFirstProposal?.FeedbackId ?? "",
                    execution.Macro.Status, execution.Macro.RuntimeSeconds,
                    candidate = "", microStatus = "not_run", conflictCount = 0
                });
                if (activeExplorationReference is not null &&
                    activeExplorationPending is not null &&
                    releaseLevel != DirectResumeReleaseLevel.FullMacro)
                {
                    DirectResumeBranchRecovery recovery =
                        DirectResumeBranchPolicy.ExpandFromReference(
                            activeExplorationReference,
                            releaseLevel);
                    current = activeExplorationReference.Snapshot ?? throw new InvalidDataException(
                        "Exploratory reference has no recoverable snapshot.");
                    archive.Observe(activeExplorationReference);
                    pending = activeExplorationPending;
                    releaseLevel = recovery.NextReleaseLevel;
                    branchEvents.Add(new
                    {
                        iteration,
                        action = "expand_non_best_branch_after_failed_attempt",
                        recovery.ReferenceFingerprint,
                        explorationDepth,
                        nextReleaseLevel = releaseLevel.ToString(),
                        recovery.Reason,
                        failedStatus = execution.Macro.Status,
                        failedReason = execution.Macro.StopReason
                    });
                    continue;
                }
                if (archive.Best is not null && bestPending is not null &&
                    releaseLevel != DirectResumeReleaseLevel.FullMacro)
                {
                    DirectResumeBranchRecovery recovery =
                        DirectResumeBranchPolicy.RecoverAfterFailure(archive, releaseLevel);
                    current = archive.Current!.Snapshot ?? throw new InvalidDataException(
                        "Archived best candidate has no recoverable snapshot.");
                    pending = bestPending;
                    releaseLevel = recovery.NextReleaseLevel;
                    branchEvents.Add(new
                    {
                        iteration,
                        action = "rollback_to_best_and_expand_scope",
                        recovery.ReferenceFingerprint,
                        nextReleaseLevel = releaseLevel.ToString(),
                        recovery.Reason,
                        failedStatus = execution.Macro.Status,
                        failedReason = execution.Macro.StopReason
                    });
                    continue;
                }
                if (execution.Macro.Status != "INFEASIBLE")
                {
                    if (pending.IsPermanent)
                        AddDirectResumeRunHistory(retained, pending);
                    if (TryAdvanceStagedTrackOccupationFeedback(
                            iteration, "previous_station_core_returned_no_macro_candidate"))
                        continue;
                }
                break;
            }

            current = execution.Candidate;
            string candidate = execution.Macro.CandidateFingerprint;
            string candidatePath = Path.Combine(stateDirectory,
                $"candidate_{iteration:D3}_{candidate[..12]}.snapshot.json");
            MasterSolutionSnapshotStore.Save(candidatePath, instance.Meta.InstanceName, current);
            if (execution.StationAnalysis is not null)
                WriteMicroCheck(Path.Combine(stateDirectory, $"attempt_{iteration:D3}_micro_check.json"),
                    candidate, execution.RecheckStage, execution.HotspotStatus, execution.StationAnalysis);
            DirectResumeCandidateArchiveDecision archiveDecision = execution.StationAnalysis is null
                ? archive.Observe(new DirectResumeCandidateObservation(
                    candidate,
                    new DirectResumeCandidateScore(
                        false, execution.MacroValidated, 1, 0, 0, 0, 0, 0,
                        execution.Macro.ChangedDecisionIds.Count),
                    iteration,
                    candidatePath,
                    current))
                : archive.Observe(new DirectResumeCandidateObservation(
                    candidate,
                    DirectResumeCandidateScore.FromAnalysis(
                        execution.StationAnalysis,
                        execution.RecheckStage,
                        execution.MacroValidated,
                        execution.Macro.ChangedDecisionIds.Count),
                    iteration,
                    candidatePath,
                    current));
            attempts.Add(new
            {
                iteration, parent, feedbackId = pending.FeedbackId,
                releaseLevel = releaseLevel.ToString(),
                timeCutCount = pending.ConditionalTimeCuts.Count,
                trackOccupationCutCount = pending.ConditionalTrackOccupationCuts.Count,
                execution.MacroRepairStage,
                timeFirstStatus = execution.TimeFirstMacro?.Status ?? "not_run",
                timeFirstSeconds = execution.TimeFirstMacro?.RuntimeSeconds ?? 0.0,
                timeFirstProposalId = execution.TimeFirstProposal?.FeedbackId ?? "",
                execution.Macro.Status, execution.Macro.RuntimeSeconds,
                candidate,
                decisionChanges = execution.Macro.ChangedDecisionIds.Count,
                microStatus = execution.StationAnalysis?.Status.ToString() ?? "not_run",
                conflictCount = execution.StationAnalysis?.Conflicts.Count ?? 0,
                unknownCount = execution.StationAnalysis?.StationResults.Count(item =>
                    item.AssignmentResult.Status == MicroSolveStatus.Unknown) ?? 0,
                becameBest = archiveDecision.BecameBest,
                archiveReason = archiveDecision.Reason,
                bestCandidate = archive.Best?.CandidateFingerprint ?? "",
                candidatePath
            });
            if (execution.StrictJointFeasible && execution.StationAnalysis is not null)
            {
                current = current with { VerifiedMicroAssignments = execution.Witnesses };
                finalState = Path.Combine(stateDirectory, "joint_feasible.snapshot.json");
                MasterSolutionSnapshotStore.Save(finalState, instance.Meta.InstanceName, current);
                MasterSolutionSnapshot readback = MasterSolutionSnapshotStore.Load(
                    finalState,
                    instance.Meta.InstanceName,
                    instance.Trains.Select(train => train.TrainIndex).ToHashSet());
                MacroMicroJointIdentityCheck identity = MacroMicroJointAcceptancePolicy.CheckIdentity(
                    candidate,
                    instance.TrainStops.Select(stop => stop.StopId),
                    readback.VerifiedMicroAssignments ?? []);
                VerifiedMicroAssignmentValidationReport fixedValidation =
                    VerifiedMicroAssignmentValidator.Validate(
                        instance,
                        readback,
                        readback.VerifiedMicroAssignments ?? [],
                        execution.StationAnalysis,
                        endpointOccupationSeconds);
                strict = identity.IsValid && fixedValidation.IsValid &&
                    string.Equals(SnapshotFingerprint.Calculate(readback), candidate,
                        StringComparison.OrdinalIgnoreCase);
                finalStatus = strict ? "strict_joint_feasible" : "readback_validation_failed";
                finalReason = strict
                    ? $"same_candidate_macro_micro_{instance.TrainStops.Count}_assignments_passed_serialized_readback"
                    : "fixed_resource_or_identity_readback_failed";
                break;
            }
            if (execution.StationAnalysis?.Status != MicroSolveStatus.ProvenInfeasible)
            {
                finalStatus = "micro_recheck_unknown";
                finalReason = "unknown_does_not_create_feedback";
                if (!archiveDecision.BecameBest &&
                    activeExplorationReference is not null &&
                    activeExplorationPending is not null &&
                    releaseLevel != DirectResumeReleaseLevel.FullMacro)
                {
                    DirectResumeBranchRecovery recovery =
                        DirectResumeBranchPolicy.ExpandFromReference(
                            activeExplorationReference,
                            releaseLevel);
                    current = activeExplorationReference.Snapshot ?? throw new InvalidDataException(
                        "Exploratory reference has no recoverable snapshot.");
                    archive.Observe(activeExplorationReference);
                    pending = activeExplorationPending;
                    releaseLevel = recovery.NextReleaseLevel;
                    branchEvents.Add(new
                    {
                        iteration,
                        action = "unknown_non_best_branch_expanded_without_cut",
                        recovery.ReferenceFingerprint,
                        explorationDepth,
                        nextReleaseLevel = releaseLevel.ToString(),
                        recovery.Reason
                    });
                    continue;
                }
                if (!archiveDecision.BecameBest && archive.Best is not null &&
                    bestPending is not null && releaseLevel != DirectResumeReleaseLevel.FullMacro)
                {
                    DirectResumeBranchRecovery recovery =
                        DirectResumeBranchPolicy.RecoverAfterFailure(archive, releaseLevel);
                    current = archive.Current!.Snapshot ?? throw new InvalidDataException(
                        "Archived best candidate has no recoverable snapshot.");
                    pending = bestPending;
                    releaseLevel = recovery.NextReleaseLevel;
                    branchEvents.Add(new
                    {
                        iteration,
                        action = "unknown_branch_rolled_back_without_cut",
                        recovery.ReferenceFingerprint,
                        nextReleaseLevel = releaseLevel.ToString(),
                        recovery.Reason
                    });
                    continue;
                }
                if (TryAdvanceStagedTrackOccupationFeedback(
                        iteration, "previous_station_core_micro_recheck_was_unknown"))
                    continue;
                break;
            }
            MacroRepairFeedback? provenTimeFeedback = ExactHeadwayMacroFeedbackProjector.TryCreate(
                instance,
                current,
                candidate,
                $"direct_resume::{iteration:D3}",
                CalculateIdentity("direct_resume", candidate,
                    execution.StationAnalysis.Conflicts.Select(conflict =>
                        $"{conflict.StationName}:{conflict.FirstTrainIndex}:{conflict.SecondTrainIndex}:" +
                        $"{conflict.FirstEventSide}:{conflict.SecondEventSide}:{conflict.RequiredGapSeconds}")),
                execution.StationAnalysis.Conflicts);
            List<MacroRepairFeedback> provenResourceFeedback = [];
            MacroRepairFeedback? temporaryResourceProposal = null;
            List<string> resourceMappingReasons = [];
            foreach (StationLevelMicroSubproblemResult stationResult in
                     execution.StationAnalysis.StationResults.Where(item => !item.IsFeasible))
            {
                MacroRepairFeedback? mapped = UniqueTrackOccupationMacroFeedbackProjector.TryCreate(
                    instance,
                    current,
                    candidate,
                    $"direct_resume_station_resource::{stationResult.StationName}",
                    CalculateIdentity(
                        "direct_resume_station_resource",
                        candidate,
                        stationResult.ConflictCore.SupportStopIds.Concat(
                            stationResult.ConflictCore.SupportPrecedenceKeys)),
                    stationResult.AssignmentResult.Status,
                    stationResult.ConflictCore,
                    endpointOccupationSeconds,
                    out string mappingReason);
                if (mapped is not null)
                {
                    provenResourceFeedback.Add(mapped);
                }
                else if (temporaryResourceProposal is null)
                {
                    temporaryResourceProposal = TryCreateTemporaryResourceProposal(
                        current,
                        candidate,
                        $"direct_resume_station_resource::{stationResult.StationName}",
                        CalculateIdentity(
                            "direct_resume_station_resource",
                            candidate,
                            stationResult.ConflictCore.SupportStopIds.Concat(
                                stationResult.ConflictCore.SupportPrecedenceKeys)),
                        stationResult.AssignmentResult.Status,
                        stationResult.ConflictCore,
                        out string fallbackReason);
                    mappingReason += $";fallback={fallbackReason}";
                }
                resourceMappingReasons.Add($"{stationResult.StationName}:{mappingReason}");
            }
            MacroRepairFeedback? resourceProposal = SelectUntriedResourceProposal(
                provenResourceFeedback,
                temporaryResourceProposal,
                retained.Append(pending).ToArray());
            if (resourceProposal is { IsPermanent: true } &&
                resourceProposal.ConditionalTrackOccupationCuts.Count > 0)
                resourceProposal = StageTrackOccupationFeedback(resourceProposal);

            // Type-A rows remain proven constraints. Type-B rows remain only as run-local tabu:
            // they prevent this search from alternating between already rejected discrete
            // signatures, but are never promoted to business constraints or exported as proven
            // feedback for another experiment.
            AddDirectResumeRunHistory(retained, pending);
            string resourceMappingSummary = string.Join(';', resourceMappingReasons);
            bool resourceCoreCoveredByTimeFeedback =
                IsResourceProposalFullyCoveredByTimeFeedback(
                    provenTimeFeedback,
                    resourceProposal,
                    resourceMappingSummary);
            DirectResumeFeedbackSelection selection = SelectNextDirectFeedback(
                provenTimeFeedback,
                resourceProposal,
                retained,
                resourceCoreCoveredByTimeFeedback);
            foreach (MacroRepairFeedback companion in selection.RetainImmediately)
            {
                AddDirectResumeRunHistory(retained, companion);
                WriteJson(Path.Combine(stateDirectory,
                    $"attempt_{iteration:D3}_retained_companion_feedback.json"), companion);
            }
            MacroRepairFeedback? next = selection.Pending;
            string nextFeedbackReason = $"{selection.Reason};" +
                $"time={(provenTimeFeedback is null ? "none" : provenTimeFeedback.FeedbackId)};" +
                $"resource={(resourceProposal is null ? "none" : resourceProposal.FeedbackId)};" +
                $"resource_core_covered_by_time={resourceCoreCoveredByTimeFeedback};" +
                $"resource_mapping={resourceMappingSummary}";
            if (next is not null)
                WriteJson(Path.Combine(stateDirectory, $"attempt_{iteration:D3}_next_feedback.json"), next);
            if (!archiveDecision.BecameBest)
            {
                if (activeExplorationReference is not null &&
                    activeExplorationPending is not null &&
                    releaseLevel != DirectResumeReleaseLevel.FullMacro)
                {
                    if (next?.IsPermanent == true)
                        AddDirectResumeRunHistory(retained, next);
                    DirectResumeBranchRecovery recovery =
                        DirectResumeBranchPolicy.ExpandFromReference(
                            activeExplorationReference,
                            releaseLevel);
                    current = activeExplorationReference.Snapshot ?? throw new InvalidDataException(
                        "Exploratory reference has no recoverable snapshot.");
                    archive.Observe(activeExplorationReference);
                    pending = activeExplorationPending;
                    releaseLevel = recovery.NextReleaseLevel;
                    branchEvents.Add(new
                    {
                        iteration,
                        action = "non_improving_non_best_branch_candidate_archived_then_expand",
                        exploredCandidate = candidate,
                        branchReference = activeExplorationReference.CandidateFingerprint,
                        bestCandidate = archive.Best?.CandidateFingerprint ?? "",
                        explorationDepth,
                        nextReleaseLevel = releaseLevel.ToString(),
                        recovery.Reason,
                        derivedFeedback = next?.FeedbackId ?? "",
                        derivedFeedbackClass = next?.Classification.ToString() ?? "none"
                    });
                    continue;
                }
                if (archive.Best is not null && bestPending is not null &&
                    releaseLevel != DirectResumeReleaseLevel.FullMacro)
                {
                    if (next?.IsPermanent == true)
                        AddDirectResumeRunHistory(retained, next);
                    DirectResumeBranchRecovery recovery =
                        DirectResumeBranchPolicy.RecoverAfterFailure(archive, releaseLevel);
                    current = archive.Current!.Snapshot ?? throw new InvalidDataException(
                        "Archived best candidate has no recoverable snapshot.");
                    pending = bestPending;
                    releaseLevel = recovery.NextReleaseLevel;
                    branchEvents.Add(new
                    {
                        iteration,
                        action = "non_improving_candidate_archived_then_rollback",
                        exploredCandidate = candidate,
                        bestCandidate = archive.Best.CandidateFingerprint,
                        recovery.ReferenceFingerprint,
                        nextReleaseLevel = releaseLevel.ToString(),
                        recovery.Reason,
                        derivedFeedback = next?.FeedbackId ?? "",
                        derivedFeedbackClass = next?.Classification.ToString() ?? "none"
                    });
                    continue;
                }
                if (archive.Best is not null && releaseLevel == DirectResumeReleaseLevel.FullMacro)
                {
                    DirectResumeExplorationDecision exploration = explorationPolicy.Consider(
                        archive.Best,
                        archiveDecision.Current,
                        next?.FeedbackId ?? "",
                        explorationDepth);
                    if (exploration.ShouldExplore && next is not null)
                    {
                        activeExplorationReference = archiveDecision.Current;
                        activeExplorationPending = next;
                        explorationDepth = exploration.NextDepth;
                        current = archiveDecision.Current.Snapshot ?? throw new InvalidDataException(
                            "Exploratory candidate has no recoverable snapshot.");
                        pending = next;
                        releaseLevel = next.IsPermanent &&
                            next.ConditionalTimeCuts.Count +
                            next.ConditionalTrackOccupationCuts.Count > 0
                                ? DirectResumeReleaseLevel.EvidencePremises
                                : DirectResumeReleaseLevel.FullMacro;
                        branchEvents.Add(new
                        {
                            iteration,
                            action = "continue_from_bounded_non_best_candidate",
                            exploredCandidate = candidate,
                            bestCandidate = archive.Best.CandidateFingerprint,
                            explorationDepth,
                            nextFeedback = next.FeedbackId,
                            nextReleaseLevel = releaseLevel.ToString(),
                            exploration.Reason
                        });
                        continue;
                    }
                    if (next?.IsPermanent == true)
                        AddDirectResumeRunHistory(retained, next);
                    current = archive.RollbackToBest()?.Snapshot ?? current;
                    activeExplorationReference = null;
                    activeExplorationPending = null;
                    if (TryAdvanceStagedTrackOccupationFeedback(
                            iteration, "bounded_non_best_branch_finished_without_improvement"))
                        continue;
                    finalStatus = "full_macro_exploration_did_not_improve_best";
                    finalReason = $"full_macro_candidate_was_checked_and_archived_but_bounded_exploration_stopped:{exploration.Reason}";
                    branchEvents.Add(new
                    {
                        iteration,
                        action = "full_macro_non_improvement_stop",
                        exploredCandidate = candidate,
                        bestCandidate = archive.Best.CandidateFingerprint,
                        explorationDepth,
                        exploration.Reason
                    });
                    break;
                }
            }
            if (next is null)
            {
                if (TryAdvanceStagedTrackOccupationFeedback(
                        iteration, "current_result_has_no_new_feedback_but_staged_core_remains"))
                    continue;
                finalStatus = "remaining_micro_conflicts_have_no_mapped_macro_repair_premises";
                finalReason = string.IsNullOrWhiteSpace(nextFeedbackReason)
                    ? "remaining conflicts have neither a safe type-A cut nor a completely mapped type-B proposal"
                    : nextFeedbackReason;
                break;
            }
            pending = next;
            if (archiveDecision.BecameBest)
            {
                activeExplorationReference = null;
                activeExplorationPending = null;
                explorationDepth = 0;
                bestPending = next;
                releaseLevel = next.IsPermanent &&
                    next.ConditionalTimeCuts.Count + next.ConditionalTrackOccupationCuts.Count > 0
                        ? DirectResumeReleaseLevel.EvidencePremises
                        : DirectResumeReleaseLevel.FullMacro;
                branchEvents.Add(new
                {
                    iteration,
                    action = "promote_best_and_reset_release_scope",
                    bestCandidate = archive.Best?.CandidateFingerprint ?? candidate,
                    nextFeedback = next.FeedbackId,
                    nextReleaseLevel = releaseLevel.ToString()
                });
            }
        }
        clock.Stop();
        if (!strict)
        {
            string currentState = Path.Combine(stateDirectory, "current_search_state.snapshot.json");
            MasterSolutionSnapshotStore.Save(currentState, instance.Meta.InstanceName, current);
            DirectResumeCandidateObservation? best = archive.Best;
            if (best?.Snapshot is not null)
            {
                finalState = Path.Combine(stateDirectory, "best_verified_candidate.snapshot.json");
                MasterSolutionSnapshotStore.Save(finalState, instance.Meta.InstanceName, best.Snapshot);
            }
            else
            {
                finalState = Path.Combine(stateDirectory, "diagnostic_state.snapshot.json");
                MasterSolutionSnapshotStore.Save(finalState, instance.Meta.InstanceName, current);
            }
        }
        WriteJson(Path.Combine(stateDirectory, "candidate_archive.json"), new
        {
            bestCandidate = archive.Best?.CandidateFingerprint ?? "",
            currentCandidate = archive.Current?.CandidateFingerprint ?? "",
            entries = archive.Entries
        });
        WriteJson(Path.Combine(stateDirectory, "branch_events.json"), branchEvents);
        WriteJson(Path.Combine(outputDirectory, "run_summary.json"), new
        {
            version = "conditional_feedback_direct_resume_v14_candidate_bound_staged_resource_cores",
            source = new { snapshotPath, feedbackPath, retainedFeedbackPaths, runTabuPaths, conflictReportPath },
            totalBudgetSeconds,
            attemptTimeLimitSeconds,
            maximumAttempts,
            finalSaveReserveSeconds,
            initialRetainedFeedbackCount,
            initialRetainedTimeCutCount,
            initialRetainedTrackOccupationCutCount,
            initialRunTabuCount,
            initialFeedbackSelectionReason = initialSelection.Reason,
            elapsedSeconds = clock.Elapsed.TotalSeconds,
            finalStatus,
            finalReason,
            strictJointFeasible = strict,
            baseline = new
            {
                candidate = baselineFingerprint,
                baseline.MacroValidated,
                baseline.MacroViolationCount,
                status = baseline.Analysis.Status.ToString(),
                conflictCount = baseline.Analysis.Conflicts.Count,
                unknownCount = baseline.Analysis.StationResults.Count(item =>
                    item.AssignmentResult.Status == MicroSolveStatus.Unknown),
                baseline.MicroSeconds,
                baselineCheckPath
            },
            bestCandidate = archive.Best?.CandidateFingerprint ?? "",
            bestScore = archive.Best?.Score,
            currentCandidate = archive.Current?.CandidateFingerprint ?? "",
            boundedExploration = new
            {
                maximumDepth = 2,
                resourceCoreReboundLimit = "none",
                reachedDepth = explorationDepth,
                activeReference = activeExplorationReference?.CandidateFingerprint ?? ""
            },
            attempts,
            branchEvents,
            finalState
        });
        File.WriteAllText(Path.Combine(outputDirectory, "result.md"),
            $"# Conditional feedback targeted resume\n\n" +
            $"- Strict joint feasible: **{strict}**\n" +
            $"- Final status: `{finalStatus}`\n" +
            $"- Reason: `{finalReason}`\n" +
            $"- Attempts: `{attempts.Count}`\n" +
            $"- Per-attempt limit seconds: `{attemptTimeLimitSeconds:R}`\n" +
            $"- Maximum attempts: `{maximumAttempts}`\n" +
            $"- Final validation/save reserve seconds: `{finalSaveReserveSeconds:R}`\n" +
            $"- Retained proven feedback items: `{initialRetainedFeedbackCount}`\n" +
            $"- Retained guarded time cuts: `{initialRetainedTimeCutCount}`\n" +
            $"- Retained unique-track occupation cuts: `{initialRetainedTrackOccupationCutCount}`\n" +
            $"- Restored run-local tabu items: `{initialRunTabuCount}`\n" +
            $"- Best complete candidate: `{archive.Best?.CandidateFingerprint ?? "none"}`\n" +
            $"- Current search candidate: `{archive.Current?.CandidateFingerprint ?? "none"}`\n" +
            $"- Baseline full-line conflicts: `{baseline.Analysis.Conflicts.Count}`\n" +
            $"- Baseline full-line Unknown: `{baseline.Analysis.StationResults.Count(item => item.AssignmentResult.Status == MicroSolveStatus.Unknown)}`\n" +
            $"- Elapsed seconds: `{clock.Elapsed.TotalSeconds:R}`\n" +
            $"- Final state: `{finalState}`\n",
            new UTF8Encoding(false));
        return strict ? 0 : 1;
    }

    internal static MacroRepairFeedback CreateDirectResumeInitialFeedback(
        TimetablingInstance instance,
        MasterSolutionSnapshot current,
        string? feedbackPath,
        string? conflictReportPath,
        int endpointOccupationSeconds = StationEndpointOccupationPolicy.DefaultSeconds)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(current);
        string currentFingerprint = SnapshotFingerprint.Calculate(current);
        MacroRepairFeedback? saved = null;
        if (!string.IsNullOrWhiteSpace(feedbackPath))
        {
            if (!File.Exists(feedbackPath))
                throw new FileNotFoundException("Saved direct Macro feedback was not found.", feedbackPath);
            saved = JsonSerializer.Deserialize<MacroRepairFeedback>(
                File.ReadAllText(feedbackPath), JsonOptions)
                ?? throw new InvalidDataException("Saved direct Macro feedback is empty or invalid.");
            if (!string.Equals(saved.SourceCandidateFingerprint, currentFingerprint,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Saved direct feedback does not belong to the resume snapshot.");
            if (saved.ConditionalTimeCuts.Count > 0 || saved.ConditionalTrackOccupationCuts.Count > 0)
                return saved;
        }

        if (string.IsNullOrWhiteSpace(conflictReportPath) || !File.Exists(conflictReportPath))
            throw new InvalidDataException(
                "A current Micro conflict report is required when no serialized direct feedback is supplied.");
        using JsonDocument conflictDocument = JsonDocument.Parse(File.ReadAllText(conflictReportPath));
        JsonElement root = conflictDocument.RootElement;
        if (root.TryGetProperty("candidateFingerprint", out JsonElement reportFingerprintElement))
        {
            string? reportFingerprint = reportFingerprintElement.GetString();
            if (!string.Equals(reportFingerprint, currentFingerprint, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "Saved Micro conflict report does not belong to the resume snapshot.");
        }
        MicroSolveStatus reportStatus = MicroSolveStatus.ProvenInfeasible;
        if (root.TryGetProperty("status", out JsonElement statusElement) &&
            !Enum.TryParse(statusElement.GetString(), ignoreCase: true, out reportStatus))
            throw new InvalidDataException("Saved Micro check has an invalid solve status.");
        if (reportStatus != MicroSolveStatus.ProvenInfeasible)
            throw new InvalidDataException(
                $"Saved Micro check status {reportStatus} cannot create Macro feedback.");
        if (!root.TryGetProperty("conflicts", out JsonElement conflictElement))
            throw new InvalidDataException("Saved Micro check has no complete conflicts collection.");
        MicroConflictExplanation[] conflicts = conflictElement.Deserialize<MicroConflictExplanation[]>(JsonOptions)
            ?? throw new InvalidDataException("Saved Micro conflicts cannot be deserialized.");
        MacroRepairFeedback? projected = ExactHeadwayMacroFeedbackProjector.TryCreate(
            instance,
            current,
            currentFingerprint,
            saved?.SourceModelIdentity ?? "recovered_current_global_micro_conflicts",
            CalculateIdentity("saved_global_micro_conflicts", currentFingerprint,
                conflicts.Select(conflict =>
                    $"{conflict.StationName}:{conflict.FirstTrainIndex}:{conflict.SecondTrainIndex}:" +
                    $"{conflict.FirstEventSide}:{conflict.SecondEventSide}:{conflict.RequiredGapSeconds}")),
            conflicts);
        if (projected is not null)
            return projected;

        List<string> mappingReasons = [];
        if (root.TryGetProperty("stationResults", out JsonElement stationResultsElement))
        {
            foreach (JsonElement stationResultElement in stationResultsElement.EnumerateArray())
            {
                if (!stationResultElement.TryGetProperty("conflictCore", out JsonElement coreElement))
                    continue;
                StationConflictCore? core = coreElement.Deserialize<StationConflictCore>(JsonOptions);
                if (core is null)
                {
                    mappingReasons.Add("saved_station_conflict_core_deserialization_failed");
                    continue;
                }
                MacroRepairFeedback? temporary = UniqueTrackOccupationMacroFeedbackProjector.TryCreate(
                    instance,
                    current,
                    currentFingerprint,
                    saved?.SourceModelIdentity ?? $"saved_micro_check::{Path.GetFileName(conflictReportPath)}",
                    CalculateIdentity(
                        "saved_station_resource_core",
                        currentFingerprint,
                        core.SupportStopIds.Concat(core.SupportPrecedenceKeys)),
                    reportStatus,
                    core,
                    endpointOccupationSeconds,
                    out string mappingReason);
                if (temporary is null)
                {
                    temporary = TryCreateTemporaryResourceProposal(
                        current,
                        currentFingerprint,
                        saved?.SourceModelIdentity ?? $"saved_micro_check::{Path.GetFileName(conflictReportPath)}",
                        CalculateIdentity(
                            "saved_station_resource_core",
                            currentFingerprint,
                            core.SupportStopIds.Concat(core.SupportPrecedenceKeys)),
                        reportStatus,
                        core,
                        out string fallbackReason);
                    mappingReason += $";fallback={fallbackReason}";
                }
                mappingReasons.Add($"{core.StationName}:{mappingReason}");
                if (temporary is not null)
                    return temporary;
            }
        }
        throw new InvalidDataException(
            "Saved conflicts contain no eligible type-A relation or completely mapped type-B resource core. " +
            string.Join(";", mappingReasons));
    }

    internal static DirectResumeFeedbackSelection CreateDirectResumeInitialFeedbackSelection(
        TimetablingInstance instance,
        MasterSolutionSnapshot current,
        string? feedbackPath,
        string? conflictReportPath,
        int endpointOccupationSeconds = StationEndpointOccupationPolicy.DefaultSeconds)
    {
        MacroRepairFeedback primary = CreateDirectResumeInitialFeedback(
            instance, current, feedbackPath, conflictReportPath, endpointOccupationSeconds);
        MacroRepairFeedback? provenTime = primary.IsPermanent && primary.ConditionalTimeCuts.Count > 0
            ? primary
            : null;
        string resourceMappingReason = string.Empty;
        bool shouldRebuildSavedResourceBatch = provenTime is not null ||
            primary.ConditionalTrackOccupationCuts.Count > 0;
        MacroRepairFeedback? rebuiltResource = shouldRebuildSavedResourceBatch
            ? TryCreateSavedResourceProposal(
                instance, current, conflictReportPath, endpointOccupationSeconds,
                out resourceMappingReason)
            : null;
        MacroRepairFeedback? resource = provenTime is not null
            ? rebuiltResource
            : rebuiltResource ?? primary;
        return SelectNextDirectFeedback(
            provenTime,
            resource,
            [],
            IsResourceProposalFullyCoveredByTimeFeedback(
                provenTime, resource, resourceMappingReason));
    }

    internal static DirectResumeFeedbackSelection CreateDirectResumeInitialFeedbackSelection(
        TimetablingInstance instance,
        MasterSolutionSnapshot current,
        StationLevelMicroFeasibilitySlaveResult currentAnalysis,
        int endpointOccupationSeconds = StationEndpointOccupationPolicy.DefaultSeconds,
        IReadOnlyList<MacroRepairFeedback>? retainedFeedback = null)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(currentAnalysis);
        if (currentAnalysis.Status != MicroSolveStatus.ProvenInfeasible)
            throw new InvalidDataException(
                $"Current Micro status {currentAnalysis.Status} cannot create fresh Macro feedback.");

        string fingerprint = SnapshotFingerprint.Calculate(current);
        string modelIdentity = "fresh_current_global_micro_check";
        MacroRepairFeedback? provenTime = ExactHeadwayMacroFeedbackProjector.TryCreate(
            instance,
            current,
            fingerprint,
            modelIdentity,
            CalculateIdentity(
                "fresh_current_global_micro_conflicts",
                fingerprint,
                currentAnalysis.Conflicts.Select(conflict =>
                    $"{conflict.StationName}:{conflict.FirstTrainIndex}:{conflict.SecondTrainIndex}:" +
                    $"{conflict.FirstEventSide}:{conflict.SecondEventSide}:{conflict.RequiredGapSeconds}")),
            currentAnalysis.Conflicts);
        MacroRepairFeedback? resource = TryCreateCurrentResourceProposal(
            instance,
            current,
            currentAnalysis,
            endpointOccupationSeconds,
            retainedFeedback ?? [],
            out string resourceMappingReason);
        DirectResumeFeedbackSelection selection = SelectNextDirectFeedback(
            provenTime,
            resource,
            [],
            IsResourceProposalFullyCoveredByTimeFeedback(
                provenTime, resource, resourceMappingReason));
        return selection with { Reason = $"fresh_current_candidate::{selection.Reason}" };
    }

    private static DirectResumeFeedbackSelection SelectNextFeedbackFromFullLineCheck(
        TimetablingInstance instance,
        MasterSolutionSnapshot current,
        StationLevelMicroFeasibilitySlaveResult analysis,
        int endpointOccupationSeconds,
        List<MacroRepairFeedback> permanentFeedback,
        List<MacroRepairFeedback> temporarySearchFeedback)
    {
        IReadOnlyList<MacroRepairFeedback> retained = permanentFeedback
            .Concat(temporarySearchFeedback)
            .ToArray();
        DirectResumeFeedbackSelection selection = CreateDirectResumeInitialFeedbackSelection(
            instance,
            current,
            analysis,
            endpointOccupationSeconds,
            retained);
        foreach (MacroRepairFeedback companion in selection.RetainImmediately)
        {
            List<MacroRepairFeedback> target = companion.IsPermanent
                ? permanentFeedback
                : temporarySearchFeedback;
            if (target.All(item => !string.Equals(
                    item.FeedbackId, companion.FeedbackId, StringComparison.OrdinalIgnoreCase)))
                target.Add(companion);
        }
        return selection;
    }

    private static MacroRepairFeedback? TryCreateCurrentResourceProposal(
        TimetablingInstance instance,
        MasterSolutionSnapshot current,
        StationLevelMicroFeasibilitySlaveResult currentAnalysis,
        int endpointOccupationSeconds,
        IReadOnlyList<MacroRepairFeedback> retainedFeedback,
        out string reason)
    {
        string fingerprint = SnapshotFingerprint.Calculate(current);
        List<string> mappingReasons = [];
        List<MacroRepairFeedback> provenResourceFeedback = [];
        MacroRepairFeedback? temporaryResourceProposal = null;
        foreach (StationLevelMicroSubproblemResult stationResult in currentAnalysis.StationResults)
        {
            StationConflictCore core = stationResult.ConflictCore;
            bool containsAssignmentConflict = stationResult.Conflicts.Any(conflict =>
                string.Equals(
                    conflict.ConflictKind,
                    "station_route_track_assignment_infeasible",
                    StringComparison.OrdinalIgnoreCase));
            if (containsAssignmentConflict && !string.Equals(
                    core.PrimaryConflictKind,
                    "station_route_track_assignment_infeasible",
                    StringComparison.OrdinalIgnoreCase))
                core = core with { PrimaryConflictKind = "station_route_track_assignment_infeasible" };

            string modelIdentity = $"fresh_current_micro_check::{core.StationName}";
            string solveIdentity = CalculateIdentity(
                "fresh_current_station_resource_core",
                fingerprint,
                core.SupportStopIds.Concat(core.SupportPrecedenceKeys));
            MacroRepairFeedback? proposal = UniqueTrackOccupationMacroFeedbackProjector.TryCreate(
                instance,
                current,
                fingerprint,
                modelIdentity,
                solveIdentity,
                currentAnalysis.Status,
                core,
                endpointOccupationSeconds,
                out string mappingReason);
            if (proposal is not null)
            {
                provenResourceFeedback.Add(proposal);
            }
            else if (temporaryResourceProposal is null)
            {
                temporaryResourceProposal = TryCreateTemporaryResourceProposal(
                    current,
                    fingerprint,
                    modelIdentity,
                    solveIdentity,
                    currentAnalysis.Status,
                    core,
                    out string fallbackReason);
                mappingReason += $";fallback={fallbackReason}";
            }
            mappingReasons.Add($"{core.StationName}:{mappingReason}");
        }

        reason = string.Join(';', mappingReasons);
        return SelectUntriedResourceProposal(
            provenResourceFeedback,
            temporaryResourceProposal,
            retainedFeedback);
    }

    private static MacroRepairFeedback? TryCreateSavedResourceProposal(
        TimetablingInstance instance,
        MasterSolutionSnapshot current,
        string? conflictReportPath,
        int endpointOccupationSeconds,
        out string reason)
    {
        if (string.IsNullOrWhiteSpace(conflictReportPath) || !File.Exists(conflictReportPath))
        {
            reason = "saved_micro_check_not_supplied_for_resource_companion";
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(conflictReportPath));
        JsonElement root = document.RootElement;
        string fingerprint = SnapshotFingerprint.Calculate(current);
        if (root.TryGetProperty("candidateFingerprint", out JsonElement fingerprintElement) &&
            !string.Equals(fingerprintElement.GetString(), fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            reason = "saved_resource_companion_candidate_fingerprint_mismatch";
            return null;
        }
        MicroSolveStatus status = MicroSolveStatus.ProvenInfeasible;
        if (root.TryGetProperty("status", out JsonElement statusElement) &&
            !Enum.TryParse(statusElement.GetString(), ignoreCase: true, out status))
        {
            reason = "saved_resource_companion_status_invalid";
            return null;
        }
        if (status != MicroSolveStatus.ProvenInfeasible)
        {
            reason = $"{status.ToString().ToLowerInvariant()}_does_not_create_resource_companion";
            return null;
        }
        if (!root.TryGetProperty("stationResults", out JsonElement stationResultsElement))
        {
            reason = "saved_micro_check_has_no_station_results";
            return null;
        }

        List<string> mappingReasons = [];
        List<MacroRepairFeedback> provenResourceFeedback = [];
        MacroRepairFeedback? temporaryResourceProposal = null;
        foreach (JsonElement stationResultElement in stationResultsElement.EnumerateArray())
        {
            if (!stationResultElement.TryGetProperty("conflictCore", out JsonElement coreElement))
                continue;
            StationConflictCore? core = coreElement.Deserialize<StationConflictCore>(JsonOptions);
            if (core is null)
            {
                mappingReasons.Add("saved_station_conflict_core_deserialization_failed");
                continue;
            }

            // Older snapshots could label a real assignment-derived core as exact_headway when
            // both conflict kinds occurred once.  Recover it only when the same station result
            // explicitly contains the assignment-infeasible row; never infer it from a filename.
            bool containsAssignmentConflict = stationResultElement.TryGetProperty(
                    "conflicts", out JsonElement conflictsElement) &&
                conflictsElement.EnumerateArray().Any(conflict =>
                    conflict.TryGetProperty("conflictKind", out JsonElement kindElement) &&
                    string.Equals(kindElement.GetString(),
                        "station_route_track_assignment_infeasible",
                        StringComparison.OrdinalIgnoreCase));
            if (containsAssignmentConflict && !string.Equals(
                    core.PrimaryConflictKind,
                    "station_route_track_assignment_infeasible",
                    StringComparison.OrdinalIgnoreCase))
                core = core with { PrimaryConflictKind = "station_route_track_assignment_infeasible" };

            MacroRepairFeedback? proposal = UniqueTrackOccupationMacroFeedbackProjector.TryCreate(
                instance,
                current,
                fingerprint,
                $"saved_micro_check::{Path.GetFileName(conflictReportPath)}::{core.StationName}",
                CalculateIdentity(
                    "saved_station_resource_core",
                    fingerprint,
                    core.SupportStopIds.Concat(core.SupportPrecedenceKeys)),
                status,
                core,
                endpointOccupationSeconds,
                out string mappingReason);
            if (proposal is not null)
            {
                provenResourceFeedback.Add(proposal);
            }
            else if (temporaryResourceProposal is null)
            {
                temporaryResourceProposal = TryCreateTemporaryResourceProposal(
                    current,
                    fingerprint,
                    $"saved_micro_check::{Path.GetFileName(conflictReportPath)}::{core.StationName}",
                    CalculateIdentity(
                        "saved_station_resource_core",
                        fingerprint,
                        core.SupportStopIds.Concat(core.SupportPrecedenceKeys)),
                    status,
                    core,
                    out string fallbackReason);
                mappingReason += $";fallback={fallbackReason}";
            }
            mappingReasons.Add($"{core.StationName}:{mappingReason}");
        }

        reason = string.Join(';', mappingReasons);
        return provenResourceFeedback.Count > 0
            ? CombineProvenTrackOccupationFeedback(provenResourceFeedback)
            : temporaryResourceProposal;
    }

    internal static MacroRepairFeedback? TryCreateTemporaryResourceProposal(
        MasterSolutionSnapshot current,
        string sourceCandidateFingerprint,
        string sourceModelIdentity,
        string sourceSolveResultIdentity,
        MicroSolveStatus status,
        StationConflictCore core,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(core);
        if (status != MicroSolveStatus.ProvenInfeasible)
        {
            reason = $"{status.ToString().ToLowerInvariant()}_does_not_create_temporary_feedback";
            return null;
        }
        if (!string.Equals(core.PrimaryConflictKind,
                "station_route_track_assignment_infeasible", StringComparison.OrdinalIgnoreCase))
        {
            reason = $"unsupported_conflict_core_kind::{core.PrimaryConflictKind}";
            return null;
        }
        string actualFingerprint = SnapshotFingerprint.Calculate(current);
        if (!string.Equals(actualFingerprint, sourceCandidateFingerprint,
                StringComparison.OrdinalIgnoreCase))
        {
            reason = "source_candidate_fingerprint_mismatch";
            return null;
        }

        Dictionary<string, int> stops = new(StringComparer.OrdinalIgnoreCase);
        List<string> missingStops = [];
        foreach (string stopId in core.SupportStopIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (current.StopByStopId.TryGetValue(stopId, out int value))
                stops[stopId] = value;
            else
                missingStops.Add(stopId);
        }

        Dictionary<string, int> sections = new(StringComparer.OrdinalIgnoreCase);
        List<string> missingSections = [];
        foreach (string evidenceKey in core.SupportPrecedenceKeys.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (TryResolveSectionPrecedenceKey(current, evidenceKey, out string modelKey, out int value))
                sections[modelKey] = value;
            else
                missingSections.Add(evidenceKey);
        }
        if (missingStops.Count > 0 || missingSections.Count > 0)
        {
            reason = $"resource_core_mapping_incomplete;missing_stops={string.Join('|', missingStops)};" +
                     $"missing_section_relations={string.Join('|', missingSections)}";
            return null;
        }
        if (stops.Count + sections.Count == 0)
        {
            reason = "resource_core_contains_no_macro_adjustable_premises";
            return null;
        }

        string feedbackId = CalculateIdentity(
            "station_resource_core_temporary_proposal",
            sourceCandidateFingerprint,
            stops.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                .Select(item => $"stop:{item.Key}={item.Value}")
                .Concat(sections.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(item => $"section:{item.Key}={item.Value}")));
        reason = $"mapped_resource_core_temporary_proposal;station={core.StationName};" +
                 $"trains={string.Join('|', core.TrainIndices)};stops={stops.Count};sections={sections.Count}";
        return MacroRepairFeedbackFactory.CreateTemporaryProposal(
            feedbackId,
            sourceCandidateFingerprint,
            sourceModelIdentity,
            sourceSolveResultIdentity,
            [core.StationName],
            stops,
            sections,
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            "Temporary, candidate-bound search proposal from an irreducible station route-track " +
            "assignment core. At least one exactly mapped stop or section-order premise changes in " +
            "the next complete Macro solve. It is not a route-independent infeasibility cut and is " +
            "withdrawn after that attempt.");
    }

    private static bool TryResolveSectionPrecedenceKey(
        MasterSolutionSnapshot current,
        string evidenceKey,
        out string modelKey,
        out int value)
    {
        if (current.SectionPrecedenceByKey.TryGetValue(evidenceKey, out value))
        {
            modelKey = evidenceKey;
            return true;
        }

        int secondSeparator = evidenceKey.LastIndexOf("::", StringComparison.Ordinal);
        int firstSeparator = secondSeparator > 0
            ? evidenceKey.LastIndexOf("::", secondSeparator - 1, StringComparison.Ordinal)
            : -1;
        if (firstSeparator <= 0 || secondSeparator <= firstSeparator + 2 ||
            !int.TryParse(evidenceKey[(firstSeparator + 2)..secondSeparator],
                NumberStyles.Integer, CultureInfo.InvariantCulture, out int firstTrain) ||
            !int.TryParse(evidenceKey[(secondSeparator + 2)..],
                NumberStyles.Integer, CultureInfo.InvariantCulture, out int secondTrain))
        {
            modelKey = string.Empty;
            value = 0;
            return false;
        }
        string sectionName = evidenceKey[..firstSeparator];
        string reverseKey = BasicMilpModelBuilder.BuildSectionPrecedenceKey(
            sectionName, secondTrain, firstTrain);
        if (current.SectionPrecedenceByKey.TryGetValue(reverseKey, out value))
        {
            modelKey = reverseKey;
            return true;
        }
        modelKey = string.Empty;
        value = 0;
        return false;
    }

    internal static IReadOnlyList<MacroRepairFeedback> PrepareDirectResumeRetainedFeedback(
        IReadOnlyList<MacroRepairFeedback> retained,
        MacroRepairFeedback pending)
    {
        ArgumentNullException.ThrowIfNull(retained);
        ArgumentNullException.ThrowIfNull(pending);
        if (retained.Any(item => !item.IsPermanent))
            throw new InvalidDataException(
                "Direct resume can retain only proven feedback; temporary repair proposals must be resubmitted explicitly.");
        if (retained.Any(item => item.ConditionalTimeCuts.Count == 0 &&
                item.ConditionalTrackOccupationCuts.Count == 0))
            throw new InvalidDataException(
                "Retained proven feedback must include its serialized guarded time or track-occupation cuts.");
        if (retained.Any(item => string.IsNullOrWhiteSpace(item.SourceCandidateFingerprint) ||
                string.IsNullOrWhiteSpace(item.SourceModelIdentity) ||
                string.IsNullOrWhiteSpace(item.SourceSolveResultIdentity)))
            throw new InvalidDataException(
                "Retained proven feedback is missing source candidate, model, or solve identity.");
        if (retained.Any(item => string.Equals(item.FeedbackId, pending.FeedbackId,
                StringComparison.OrdinalIgnoreCase)) ||
            retained.GroupBy(item => item.FeedbackId, StringComparer.OrdinalIgnoreCase)
                .Any(group => group.Count() > 1))
            throw new InvalidDataException("Direct resume feedback identities must be distinct.");
        return retained.ToArray();
    }

    internal static IReadOnlyList<MacroRepairFeedback> PrepareDirectResumeRunTabu(
        IReadOnlyList<MacroRepairFeedback> runTabu)
    {
        ArgumentNullException.ThrowIfNull(runTabu);
        if (runTabu.Any(item => item.IsPermanent))
            throw new InvalidDataException(
                "Run-local tabu restore accepts only temporary repair proposals.");
        if (runTabu.Any(item => item.ConditionalTimeCuts.Count != 0 ||
                item.ConditionalTrackOccupationCuts.Count != 0))
            throw new InvalidDataException(
                "Run-local tabu cannot carry guarded proven cuts.");
        if (runTabu.Any(item => string.IsNullOrWhiteSpace(item.SourceCandidateFingerprint) ||
                string.IsNullOrWhiteSpace(item.SourceModelIdentity) ||
                string.IsNullOrWhiteSpace(item.SourceSolveResultIdentity)))
            throw new InvalidDataException(
                "Restored run-local tabu is missing source candidate, model, or solve identity.");
        if (runTabu.Any(item => item.StopValuesByStopId.Count == 0 &&
                item.SectionPrecedenceValuesByKey.Count == 0 &&
                item.StationEventPrecedenceValuesByKey.Count == 0))
            throw new InvalidDataException(
                "Restored run-local tabu must contain at least one exact discrete premise.");
        if (runTabu.GroupBy(item => item.FeedbackId, StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Count() > 1))
            throw new InvalidDataException("Restored run-local tabu identities must be distinct.");
        return runTabu.ToArray();
    }

    internal static bool AddDirectResumeRunHistory(
        IList<MacroRepairFeedback> history,
        MacroRepairFeedback feedback)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(feedback);
        if (history.Any(item => string.Equals(item.FeedbackId, feedback.FeedbackId,
                StringComparison.OrdinalIgnoreCase)))
            return false;
        history.Add(feedback);
        MacroFeedbackLayers compacted = MacroFeedbackLayers.Create(history);
        history.Clear();
        foreach (MacroRepairFeedback retained in compacted.Active)
            history.Add(retained);
        return true;
    }

    internal static bool IsResourceProposalFullyCoveredByTimeFeedback(
        MacroRepairFeedback? provenTimeFeedback,
        MacroRepairFeedback? resourceProposal,
        string resourceMappingReason)
    {
        if (provenTimeFeedback is null || resourceProposal is null ||
            resourceProposal.IsPermanent ||
            !resourceMappingReason.Contains(
                "current_unique_track_occupations_do_not_overlap",
                StringComparison.OrdinalIgnoreCase))
            return false;

        string[] resourceStops = resourceProposal.StopValuesByStopId
            .Where(item => item.Value == 1)
            .Select(item => item.Key)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (resourceStops.Length != 2)
            return false;

        HashSet<string> resourceStopSet = new(resourceStops, StringComparer.OrdinalIgnoreCase);
        return provenTimeFeedback.ConditionalTimeCuts.Any(cut =>
        {
            HashSet<string> cutStops = new(
                [cut.EarlierEvent.StopId, cut.LaterEvent.StopId],
                StringComparer.OrdinalIgnoreCase);
            return cutStops.SetEquals(resourceStopSet) &&
                resourceStopSet.All(stopId =>
                    cut.RequiredStopValuesByStopId.TryGetValue(stopId, out int value) &&
                    value == 1);
        });
    }

    internal static DirectResumeFeedbackSelection SelectNextDirectFeedback(
        MacroRepairFeedback? provenTimeFeedback,
        MacroRepairFeedback? resourceProposal,
        IReadOnlyList<MacroRepairFeedback> retainedFeedback,
        bool resourceCoreCoveredByTimeFeedback = false)
    {
        ArgumentNullException.ThrowIfNull(retainedFeedback);
        bool IsNew(MacroRepairFeedback? feedback) => feedback is not null &&
            retainedFeedback.All(item => !string.Equals(
                item.FeedbackId, feedback.FeedbackId, StringComparison.OrdinalIgnoreCase));

        bool timeIsNew = IsNew(provenTimeFeedback);
        bool resourceIsNew = IsNew(resourceProposal);
        if (timeIsNew && resourceIsNew && resourceCoreCoveredByTimeFeedback)
        {
            return new DirectResumeFeedbackSelection(
                provenTimeFeedback,
                [],
                "proven_time_relation_fully_covers_nonoverlapping_resource_core");
        }
        if (timeIsNew && resourceIsNew)
        {
            // The proven time relation is safe to retain immediately.  Drive the next solve with
            // the resource proposal so the staged repair cannot accept a time-only candidate that
            // still carries the same irreducible route-track signature.
            return new DirectResumeFeedbackSelection(
                resourceProposal,
                [provenTimeFeedback!],
                "proven_time_relation_retained_and_resource_core_drives_full_macro");
        }
        if (resourceIsNew)
            return new DirectResumeFeedbackSelection(
                resourceProposal, [], "resource_core_drives_full_macro");
        if (timeIsNew)
            return new DirectResumeFeedbackSelection(
                provenTimeFeedback, [], "proven_time_relation_drives_repair");
        return new DirectResumeFeedbackSelection(
            null, [], "no_new_safe_time_relation_or_mapped_resource_proposal");
    }

    internal static MacroRepairFeedback? SelectUntriedResourceProposal(
        IReadOnlyList<MacroRepairFeedback> provenResourceFeedback,
        MacroRepairFeedback? temporaryResourceProposal,
        IReadOnlyList<MacroRepairFeedback> retainedFeedback)
    {
        ArgumentNullException.ThrowIfNull(provenResourceFeedback);
        ArgumentNullException.ThrowIfNull(retainedFeedback);

        HashSet<string> retainedCutIds = retainedFeedback
            .Where(item => item.IsPermanent)
            .SelectMany(item => item.ConditionalTrackOccupationCuts)
            .Select(cut => cut.CutId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        MacroRepairFeedback[] untriedProven = provenResourceFeedback
            .Where(item => item.ConditionalTrackOccupationCuts.Any(cut =>
                !retainedCutIds.Contains(cut.CutId)))
            .ToArray();
        if (untriedProven.Length > 0)
            return CombineProvenTrackOccupationFeedback(untriedProven);

        // A singleton-track proof remains permanent in the retained ledger.  Once it has
        // already been scheduled, it must not indefinitely hide a different multi-route
        // conflict from the same candidate.  The latter stays a reversible type-B proposal.
        return temporaryResourceProposal;
    }

    internal static MacroRepairFeedback CombineProvenTrackOccupationFeedback(
        IReadOnlyList<MacroRepairFeedback> feedbackItems)
    {
        ArgumentNullException.ThrowIfNull(feedbackItems);
        if (feedbackItems.Count == 0)
            throw new ArgumentException(
                "At least one proven track-occupation feedback item is required.",
                nameof(feedbackItems));
        if (feedbackItems.Any(item =>
                !item.IsPermanent || item.ConditionalTrackOccupationCuts.Count == 0))
            throw new InvalidDataException(
                "Only proven conditional track-occupation feedback may be batched.");
        string[] candidateFingerprints = feedbackItems
            .Select(item => item.SourceCandidateFingerprint)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (candidateFingerprints.Length != 1)
            throw new InvalidDataException(
                "Track-occupation feedback from different Macro candidates cannot be batched.");

        ConditionalMacroTrackOccupationCut[] cuts = feedbackItems
            .SelectMany(item => item.ConditionalTrackOccupationCuts)
            .GroupBy(cut => cut.CutId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(cut => cut.CutId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string batchIdentity = CalculateIdentity(
            "batched_unique_track_occupation_feedback",
            candidateFingerprints[0],
            cuts.Select(cut => cut.CutId));
        return MacroRepairFeedbackFactory.CreateProvenConditionalTrackOccupationConstraint(
            batchIdentity,
            candidateFingerprints[0],
            $"batched_unique_track_occupation::{feedbackItems.Count}",
            CalculateIdentity(
                "batched_unique_track_occupation_sources",
                candidateFingerprints[0],
                feedbackItems.Select(item => item.SourceSolveResultIdentity)),
            feedbackItems.SelectMany(item => item.AffectedStations)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(station => station, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            cuts,
            "Every member is an independently proven singleton-track occupation relation; " +
            "batching changes scheduling granularity, not validity.",
            feedbackItems.SelectMany(item => item.SourceProofRows).ToArray());
    }

    internal static IReadOnlyList<MacroRepairFeedback> SplitProvenTrackOccupationFeedbackByStation(
        MacroRepairFeedback feedback)
    {
        ArgumentNullException.ThrowIfNull(feedback);
        if (!feedback.IsPermanent || feedback.ConditionalTrackOccupationCuts.Count == 0)
            throw new InvalidDataException(
                "Only proven conditional track-occupation feedback may be staged by station.");
        if (feedback.ConditionalTimeCuts.Count > 0)
            throw new InvalidDataException(
                "Mixed time and track feedback must be scheduled by its original feedback policy.");

        return feedback.ConditionalTrackOccupationCuts
            .GroupBy(cut => cut.StationName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                ConditionalMacroTrackOccupationCut[] stationCuts = group
                    .OrderBy(cut => cut.CutId, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                string identity = CalculateIdentity(
                    "staged_unique_track_occupation_feedback",
                    feedback.SourceCandidateFingerprint,
                    stationCuts.Select(cut => cut.CutId));
                return MacroRepairFeedbackFactory.CreateProvenConditionalTrackOccupationConstraint(
                    identity,
                    feedback.SourceCandidateFingerprint,
                    $"{feedback.SourceModelIdentity}::staged::{group.Key}",
                    feedback.SourceSolveResultIdentity,
                    [group.Key],
                    stationCuts,
                    $"Staged from proven feedback {feedback.FeedbackId}; " +
                    "only scheduling granularity changes and every original guard remains attached.",
                    feedback.SourceProofRows);
            })
            .ToArray();
    }

    internal static bool IsFeedbackBoundToReference(
        MacroRepairFeedback feedback,
        string referenceFingerprint)
    {
        ArgumentNullException.ThrowIfNull(feedback);
        if (string.IsNullOrWhiteSpace(referenceFingerprint))
            throw new ArgumentException("A reference fingerprint is required.", nameof(referenceFingerprint));
        return string.Equals(
            feedback.SourceCandidateFingerprint,
            referenceFingerprint,
            StringComparison.OrdinalIgnoreCase);
    }

    internal static IReadOnlyList<string> ParseDirectResumeRetainedFeedbackPaths(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(path => path.Length > 0)
                .ToArray();

    internal static double CalculateDirectResumeAttemptTimeLimit(
        double remainingSeconds,
        double requestedAttemptSeconds)
    {
        if (remainingSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(remainingSeconds));
        if (requestedAttemptSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(requestedAttemptSeconds));
        return Math.Min(remainingSeconds, requestedAttemptSeconds);
    }

    internal static double CalculateIntegratedHotspotTimeLimit(
        double remainingSeconds,
        double legacyIterationSeconds)
    {
        if (!double.IsFinite(remainingSeconds) || remainingSeconds <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(remainingSeconds));
        if (!double.IsFinite(legacyIterationSeconds) || legacyIterationSeconds <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(legacyIterationSeconds));

        // The caller already subtracts the final save/validation reserve.  The former
        // --macro-micro-iteration-seconds value is retained for checkpoint/CLI compatibility,
        // but is no longer a hard 300-second stop for the only integrated hotspot attempt.
        return remainingSeconds;
    }

    internal static double CalculateAutomaticCompleteMacroRepairTimeLimit(
        double remainingSeconds,
        bool useFullRemainingBudget)
    {
        if (!double.IsFinite(remainingSeconds))
            throw new ArgumentOutOfRangeException(nameof(remainingSeconds));
        if (useFullRemainingBudget)
            return Math.Max(1.0, remainingSeconds);
        return Math.Max(
            1.0,
            Math.Min(remainingSeconds, Math.Max(120.0, remainingSeconds * 0.35)));
    }

    internal static bool CanStartDirectResumeAttempt(int iteration, int maximumAttempts)
    {
        if (iteration <= 0) throw new ArgumentOutOfRangeException(nameof(iteration));
        if (maximumAttempts <= 0) throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        return iteration <= maximumAttempts;
    }

    internal static bool CanTryFixedDiscreteTimeFirst(
        MasterSolutionSnapshot reference,
        MacroRepairFeedback feedback,
        IReadOnlyList<MacroRepairFeedback> retainedFeedback,
        double timeLimitSeconds)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(feedback);
        ArgumentNullException.ThrowIfNull(retainedFeedback);
        if (!feedback.IsPermanent ||
            feedback.ConditionalTimeCuts.Count + feedback.ConditionalTrackOccupationCuts.Count == 0 ||
            timeLimitSeconds < 2.0)
            return false;
        string referenceFingerprint = SnapshotFingerprint.Calculate(reference);
        return !retainedFeedback.Any(item =>
            !item.IsPermanent && string.Equals(
                item.SourceCandidateFingerprint,
                referenceFingerprint,
                StringComparison.OrdinalIgnoreCase));
    }

    internal static DynamicHotspotModelGate CreateExecutionGate() => new(
        int.MaxValue,
        int.MaxValue,
        int.MaxValue,
        int.MaxValue,
        double.MaxValue);

    internal static DirectResumeFreshBootstrap CreateDirectResumeFreshBootstrap(
        TimetablingInstance instance,
        MasterSolutionSnapshot reference,
        IReadOnlyList<string> retainedFeedbackPaths,
        double runningTimeSupplementRatio = 0.05,
        int endpointOccupationSeconds = StationEndpointOccupationPolicy.DefaultSeconds)
    {
        MacroRepairFeedback[] loadedRetained = LoadDirectResumeFeedback(retainedFeedbackPaths);
        DirectResumeBaselineCheck baseline = CheckSavedReferenceForArchive(
            instance,
            reference,
            loadedRetained,
            runningTimeSupplementRatio,
            endpointOccupationSeconds);
        if (!baseline.MacroValidated)
            throw new InvalidDataException(
                "Fresh feedback bootstrap rejected the serialized Macro candidate.");
        DirectResumeFeedbackSelection selection = CreateDirectResumeInitialFeedbackSelection(
            instance,
            reference,
            baseline.Analysis,
            endpointOccupationSeconds,
            loadedRetained);
        return new DirectResumeFreshBootstrap(loadedRetained, baseline, selection);
    }

    private static MacroRepairFeedback[] LoadDirectResumeFeedback(
        IReadOnlyList<string> feedbackPaths) => feedbackPaths
        .Select(path => JsonSerializer.Deserialize<MacroRepairFeedback>(
            File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException($"Saved retained feedback is empty or invalid: {path}"))
        .ToArray();

    internal static DirectResumeBaselineCheck CheckSavedReferenceForArchive(
        TimetablingInstance instance,
        MasterSolutionSnapshot reference,
        IReadOnlyList<MacroRepairFeedback> retainedFeedback,
        double runningTimeSupplementRatio,
        int endpointOccupationSeconds)
    {
        ArgumentNullException.ThrowIfNull(retainedFeedback);
        Stopwatch microClock = Stopwatch.StartNew();
        StationEventConflictCandidate[] stationEdges =
            CompleteMacroFeedbackRepairSolver.ResolveConditionalStationEdges(
                instance,
                retainedFeedback.Where(item => item.IsPermanent)
                    .SelectMany(item => item.ConditionalTimeCuts)
                    .GroupBy(cut => cut.CutId, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToArray())
                .Where(edge => reference.StationEventPrecedenceByKey.ContainsKey(
                    BasicMilpModelBuilder.BuildStationEventPrecedenceKey(edge)))
                .ToArray();
        using ValidatedSnapshotCarrier carrier = new ValidatedSnapshotCarrierBuilder().Build(
            instance,
            neighborRadius: int.MaxValue,
            reference,
            runningTimeSupplementRatio,
            stationEdges: stationEdges,
            endpointOccupationSeconds: endpointOccupationSeconds);
        StationLevelMicroFeasibilitySlaveResult analysis =
            CheckerMicroValidationFactory.CreateSlave(endpointOccupationSeconds).Solve(carrier.Model);
        microClock.Stop();
        return new DirectResumeBaselineCheck(
            analysis,
            carrier.Validation.IsFeasible,
            carrier.Validation.Violations.Count,
            microClock.Elapsed.TotalSeconds);
    }

    private static DirectMacroFeedbackRecheck ExecuteDirectMacroFeedbackRecheck(
        TimetablingInstance instance,
        MasterSolutionSnapshot reference,
        MacroRepairFeedback feedback,
        IReadOnlyList<MacroRepairFeedback> retainedFeedback,
        double timeLimitSeconds,
        string logFile,
        double runningTimeSupplementRatio,
        int endpointOccupationSeconds,
        DirectResumeReleaseLevel? controlledReleaseLevel = null)
    {
        Stopwatch microClock = new();
        StagedMacroFeedbackRepairResult staged = ExecuteStagedMacroFeedbackRepair(
            instance,
            reference,
            feedback,
            retainedFeedback,
            timeLimitSeconds,
            logFile,
            runningTimeSupplementRatio,
            endpointOccupationSeconds,
            controlledReleaseLevel);
        CompleteMacroFeedbackRepairResult macro = staged.Selected;
        if (!macro.HasCandidate || macro.CandidateSnapshot is null)
            return new DirectMacroFeedbackRecheck(
                macro, staged.TimeFirst, staged.TimeFirstProposal, staged.Stage,
                null, null, "not_run", "not_run", 0,
                false, macro.MacroViolationCount, [], null, null, null, false);

        MasterSolutionSnapshot candidate = macro.CandidateSnapshot;
        StationEventConflictCandidate[] feedbackStationEdges =
            CompleteMacroFeedbackRepairSolver.ResolveConditionalStationEdges(
                instance,
                retainedFeedback.Where(item => item.IsPermanent)
                    .SelectMany(item => item.ConditionalTimeCuts)
                    .Concat(feedback.ConditionalTimeCuts)
                    .GroupBy(cut => cut.CutId, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToArray());
        using ValidatedSnapshotCarrier carrier = new ValidatedSnapshotCarrierBuilder().Build(
            instance,
            neighborRadius: int.MaxValue,
            candidate,
            runningTimeSupplementRatio,
            stationEdges: feedbackStationEdges,
            endpointOccupationSeconds: endpointOccupationSeconds);
        StationLevelMicroFeasibilitySlave slave = CheckerMicroValidationFactory.CreateSlave(
            endpointOccupationSeconds);
        HashSet<string> activeStations = feedback.AffectedStations.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        microClock.Start();
        StationLevelMicroFeasibilitySlaveResult hotspot = slave.Solve(
            carrier.Model,
            new MicroFeasibilitySlaveRequest(activeStations));
        // Candidate quality and acceptance are comparable only after the same full-line check.
        // The hotspot pass remains an early diagnostic, but even a failed hotspot is followed by
        // a complete station check so that a one-conflict hotspot cannot hide failures elsewhere.
        StationLevelMicroFeasibilitySlaveResult analysis = slave.Solve(carrier.Model);
        string stage = "full_line";
        microClock.Stop();

        if (!analysis.IsFeasible || analysis.Status != MicroSolveStatus.Feasible ||
            analysis.Conflicts.Count != 0 || analysis.StationResults.Any(result =>
                result.AssignmentResult.Status == MicroSolveStatus.Unknown))
            return new DirectMacroFeedbackRecheck(
                macro,
                staged.TimeFirst,
                staged.TimeFirstProposal,
                staged.Stage,
                candidate,
                analysis,
                stage,
                hotspot.Status.ToString(),
                microClock.Elapsed.TotalSeconds,
                carrier.Validation.IsFeasible,
                carrier.Validation.Violations.Count,
                [],
                null,
                null,
                null,
                false);

        string candidateFingerprint = macro.CandidateFingerprint;
        IReadOnlyList<VerifiedMicroAssignmentWitness> witnesses =
            VerifiedMicroAssignmentWitnessBuilder.Capture(analysis, candidateFingerprint);
        MacroMicroJointIdentityCheck identity = MacroMicroJointAcceptancePolicy.CheckIdentity(
            candidateFingerprint,
            instance.TrainStops.Select(stop => stop.StopId),
            witnesses);
        MicroFeasibilityReport independent = CheckerMicroValidationFactory.CreateChecker(
            endpointOccupationSeconds).Validate(carrier.Model);
        VerifiedMicroAssignmentValidationReport fixedValidation =
            VerifiedMicroAssignmentValidator.Validate(
                instance, candidate, witnesses, analysis, endpointOccupationSeconds);
        bool strict = carrier.Validation.IsFeasible && carrier.Validation.Violations.Count == 0 &&
            independent.IsFeasible && independent.Status == MicroSolveStatus.Feasible &&
            independent.Conflicts.Count == 0 && fixedValidation.IsValid && identity.IsValid;
        return new DirectMacroFeedbackRecheck(
            macro,
            staged.TimeFirst,
            staged.TimeFirstProposal,
            staged.Stage,
            candidate,
            analysis,
            stage,
            hotspot.Status.ToString(),
            microClock.Elapsed.TotalSeconds,
            carrier.Validation.IsFeasible,
            carrier.Validation.Violations.Count,
            witnesses,
            identity,
            independent,
            fixedValidation,
            strict);
    }

    internal static StagedMacroFeedbackRepairResult ExecuteStagedMacroFeedbackRepair(
        TimetablingInstance instance,
        MasterSolutionSnapshot reference,
        MacroRepairFeedback feedback,
        IReadOnlyList<MacroRepairFeedback> retainedFeedback,
        double timeLimitSeconds,
        string? logFile,
        double runningTimeSupplementRatio,
        int endpointOccupationSeconds,
        DirectResumeReleaseLevel? controlledReleaseLevel = null)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(feedback);
        ArgumentNullException.ThrowIfNull(retainedFeedback);
        if (timeLimitSeconds <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(timeLimitSeconds));

        Stopwatch clock = Stopwatch.StartNew();
        if (controlledReleaseLevel is DirectResumeReleaseLevel.EvidenceCorridor or
            DirectResumeReleaseLevel.EvidenceCorridorExpanded)
        {
            int stationPadding = controlledReleaseLevel == DirectResumeReleaseLevel.EvidenceCorridor
                ? 0
                : 1;
            CompleteMacroFeedbackRepairResult corridor =
                CompleteMacroFeedbackRepairSolver.SolveEvidenceCorridorRepair(
                    instance,
                    reference,
                    feedback,
                    retainedFeedback,
                    stationPadding,
                    timeLimitSeconds,
                    PhaseLogPath(logFile, $"evidence_corridor_{stationPadding}"),
                    runningTimeSupplementRatio,
                    endpointOccupationSeconds,
                    endpointOrderChangeBudget: 4);
            clock.Stop();
            return new StagedMacroFeedbackRepairResult(
                corridor,
                null,
                null,
                null,
                stationPadding == 0
                    ? "evidence_corridor_macro_repair"
                    : "expanded_evidence_corridor_macro_repair",
                clock.Elapsed.TotalSeconds);
        }
        if (controlledReleaseLevel == DirectResumeReleaseLevel.EvidenceTrainDependencies)
        {
            CompleteMacroFeedbackRepairResult dependency =
                CompleteMacroFeedbackRepairSolver.SolveEvidenceDependencyRepair(
                    instance,
                    reference,
                    feedback,
                    retainedFeedback,
                    timeLimitSeconds,
                    PhaseLogPath(logFile, "evidence_dependencies"),
                    runningTimeSupplementRatio,
                    endpointOccupationSeconds,
                    endpointOrderChangeBudget: 4);
            clock.Stop();
            return new StagedMacroFeedbackRepairResult(
                dependency,
                null,
                null,
                null,
                "evidence_train_dependency_macro_repair",
                clock.Elapsed.TotalSeconds);
        }
        if (controlledReleaseLevel == DirectResumeReleaseLevel.FullMacro)
        {
            CompleteMacroFeedbackRepairResult fullOnly = CompleteMacroFeedbackRepairSolver.Solve(
                instance,
                reference,
                feedback,
                retainedFeedback,
                timeLimitSeconds,
                PhaseLogPath(logFile, "full_macro"),
                runningTimeSupplementRatio,
                endpointOccupationSeconds,
                endpointOrderChangeBudget: 4);
            clock.Stop();
            return new StagedMacroFeedbackRepairResult(
                fullOnly,
                null,
                null,
                fullOnly,
                "full_macro_controlled_release",
                clock.Elapsed.TotalSeconds);
        }
        CompleteMacroFeedbackRepairResult? timeFirst = null;
        bool canTryTimeFirst = CanTryFixedDiscreteTimeFirst(
            reference,
            feedback,
            retainedFeedback,
            timeLimitSeconds);
        if (canTryTimeFirst)
        {
            bool introducesTrackOrder = feedback.ConditionalTrackOccupationCuts.Count > 0;
            double timeFirstLimit = introducesTrackOrder
                ? Math.Max(2.0, timeLimitSeconds * 0.35)
                : Math.Max(1.0, timeLimitSeconds * 0.2);
            timeFirst = CompleteMacroFeedbackRepairSolver.SolveFixedDiscreteTime(
                instance,
                reference,
                feedback,
                retainedFeedback,
                timeFirstLimit,
                PhaseLogPath(logFile, "time_only"),
                runningTimeSupplementRatio,
                endpointOccupationSeconds);
            if (timeFirst.HasCandidate && timeFirst.CandidateSnapshot is not null)
            {
                clock.Stop();
                return new StagedMacroFeedbackRepairResult(
                    timeFirst,
                    timeFirst,
                    null,
                    null,
                    introducesTrackOrder
                        ? "fixed_macro_discrete_track_order"
                        : "fixed_discrete_full_line_time",
                    clock.Elapsed.TotalSeconds);
            }
        }

        CompleteMacroFeedbackRepairResult? evidenceScoped = null;
        MacroDiscreteReleaseScope? releaseScope = null;
        if (timeFirst?.Status == "INFEASIBLE")
        {
            releaseScope = CompleteMacroFeedbackRepairSolver.BuildEvidencePremiseReleaseScope(
                reference, feedback, retainedFeedback);
            double scopedRemaining = timeLimitSeconds - clock.Elapsed.TotalSeconds;
            if (releaseScope.HasReleasedMainDecision && scopedRemaining >= 2.0)
            {
                double scopedLimit = Math.Max(2.0, scopedRemaining * 0.35);
                evidenceScoped = CompleteMacroFeedbackRepairSolver.SolveEvidencePremiseRepair(
                    instance,
                    reference,
                    feedback,
                    retainedFeedback,
                    scopedLimit,
                    PhaseLogPath(logFile, "evidence_scope"),
                    runningTimeSupplementRatio,
                    endpointOccupationSeconds,
                    endpointOrderChangeBudget: 4);
                if (evidenceScoped.HasCandidate && evidenceScoped.CandidateSnapshot is not null)
                {
                    clock.Stop();
                    return new StagedMacroFeedbackRepairResult(
                        evidenceScoped,
                        timeFirst,
                        null,
                        null,
                        "evidence_premise_macro_repair",
                        clock.Elapsed.TotalSeconds);
                }
            }
        }

        if (controlledReleaseLevel == DirectResumeReleaseLevel.EvidencePremises &&
            timeFirst is not null)
        {
            CompleteMacroFeedbackRepairResult selected = evidenceScoped ?? timeFirst;
            clock.Stop();
            return new StagedMacroFeedbackRepairResult(
                selected,
                timeFirst,
                null,
                null,
                evidenceScoped is null
                    ? $"evidence_premise_unavailable_after_{timeFirst.Status.ToLowerInvariant()}"
                    : $"evidence_premise_macro_repair_{evidenceScoped.Status.ToLowerInvariant()}",
                clock.Elapsed.TotalSeconds);
        }

        CompleteMacroFeedbackRepairResult? corridorScoped = null;
        if (timeFirst?.Status == "INFEASIBLE" &&
            (evidenceScoped is null || evidenceScoped.Status == "INFEASIBLE"))
        {
            double corridorRemaining = timeLimitSeconds - clock.Elapsed.TotalSeconds;
            if (corridorRemaining >= 2.0)
            {
                double corridorLimit = Math.Max(2.0, corridorRemaining * 0.25);
                corridorScoped = CompleteMacroFeedbackRepairSolver.SolveEvidenceCorridorRepair(
                    instance,
                    reference,
                    feedback,
                    retainedFeedback,
                    stationPadding: 0,
                    corridorLimit,
                    PhaseLogPath(logFile, "evidence_corridor_0"),
                    runningTimeSupplementRatio,
                    endpointOccupationSeconds,
                    endpointOrderChangeBudget: 4);
                if (corridorScoped.HasCandidate && corridorScoped.CandidateSnapshot is not null)
                {
                    clock.Stop();
                    return new StagedMacroFeedbackRepairResult(
                        corridorScoped,
                        timeFirst,
                        null,
                        null,
                        "evidence_corridor_macro_repair",
                        clock.Elapsed.TotalSeconds);
                }
            }
        }

        CompleteMacroFeedbackRepairResult? expandedCorridorScoped = null;
        if (corridorScoped?.Status == "INFEASIBLE")
        {
            double corridorRemaining = timeLimitSeconds - clock.Elapsed.TotalSeconds;
            if (corridorRemaining >= 2.0)
            {
                double corridorLimit = Math.Max(2.0, corridorRemaining * 0.35);
                expandedCorridorScoped = CompleteMacroFeedbackRepairSolver.SolveEvidenceCorridorRepair(
                    instance,
                    reference,
                    feedback,
                    retainedFeedback,
                    stationPadding: 1,
                    corridorLimit,
                    PhaseLogPath(logFile, "evidence_corridor_1"),
                    runningTimeSupplementRatio,
                    endpointOccupationSeconds,
                    endpointOrderChangeBudget: 4);
                if (expandedCorridorScoped.HasCandidate &&
                    expandedCorridorScoped.CandidateSnapshot is not null)
                {
                    clock.Stop();
                    return new StagedMacroFeedbackRepairResult(
                        expandedCorridorScoped,
                        timeFirst,
                        null,
                        null,
                        "expanded_evidence_corridor_macro_repair",
                        clock.Elapsed.TotalSeconds);
                }
            }
        }

        CompleteMacroFeedbackRepairResult? dependencyScoped = null;
        if (timeFirst?.Status == "INFEASIBLE" &&
            expandedCorridorScoped?.Status == "INFEASIBLE")
        {
            double dependencyRemaining = timeLimitSeconds - clock.Elapsed.TotalSeconds;
            if (dependencyRemaining >= 2.0)
            {
                double dependencyLimit = Math.Min(
                    dependencyRemaining,
                    Math.Max(2.0, dependencyRemaining * 0.35));
                dependencyScoped = CompleteMacroFeedbackRepairSolver.SolveEvidenceDependencyRepair(
                    instance,
                    reference,
                    feedback,
                    retainedFeedback,
                    dependencyLimit,
                    PhaseLogPath(logFile, "evidence_dependencies"),
                    runningTimeSupplementRatio,
                    endpointOccupationSeconds,
                    endpointOrderChangeBudget: 4);
                if (dependencyScoped.HasCandidate && dependencyScoped.CandidateSnapshot is not null)
                {
                    clock.Stop();
                    return new StagedMacroFeedbackRepairResult(
                        dependencyScoped,
                        timeFirst,
                        null,
                        null,
                        "evidence_train_dependency_macro_repair",
                        clock.Elapsed.TotalSeconds);
                }
            }
        }

        MacroRepairFeedback? timeFirstProposal = null;
        IReadOnlyList<MacroRepairFeedback> fullRetainedFeedback = retainedFeedback;
        if (timeFirst?.Status == "INFEASIBLE" &&
            feedback.ConditionalTrackOccupationCuts.Count == 0 &&
            feedback.StopValuesByStopId.Count + feedback.SectionPrecedenceValuesByKey.Count +
            feedback.StationEventPrecedenceValuesByKey.Count > 0)
        {
            timeFirstProposal = MacroRepairFeedbackFactory.CreateTemporaryProposal(
                CalculateIdentity(
                    "fixed_discrete_time_infeasible_temporary_premise_change",
                    SnapshotFingerprint.Calculate(reference),
                    feedback.StopValuesByStopId.Select(item => $"stop:{item.Key}={item.Value}")
                        .Concat(feedback.SectionPrecedenceValuesByKey.Select(item =>
                            $"section:{item.Key}={item.Value}"))
                        .Concat(feedback.StationEventPrecedenceValuesByKey.Select(item =>
                            $"station_event:{item.Key}={item.Value}"))),
                SnapshotFingerprint.Calculate(reference),
                "fixed_discrete_full_line_time_repair",
                $"{feedback.FeedbackId}::{timeFirst.StatusCode}",
                feedback.AffectedStations,
                feedback.StopValuesByStopId,
                feedback.SectionPrecedenceValuesByKey,
                feedback.StationEventPrecedenceValuesByKey,
                "Temporary search proposal only: the current fixed Macro discrete structure could not " +
                "satisfy the proven conditional time relations. This attempt changes at least one " +
                "feedback premise; the proposal is retractable and is not a global infeasibility cut.");
            fullRetainedFeedback = retainedFeedback.Append(timeFirstProposal).ToArray();
        }

        double remaining = Math.Max(1.0, timeLimitSeconds - clock.Elapsed.TotalSeconds);
        CompleteMacroFeedbackRepairResult full = CompleteMacroFeedbackRepairSolver.Solve(
            instance,
            reference,
            feedback,
            fullRetainedFeedback,
            remaining,
            PhaseLogPath(logFile, "full_macro"),
            runningTimeSupplementRatio,
            endpointOccupationSeconds,
            endpointOrderChangeBudget: 4);
        clock.Stop();
        return new StagedMacroFeedbackRepairResult(
            full,
            timeFirst,
            timeFirstProposal,
            full,
            dependencyScoped is not null
                ? $"full_macro_after_evidence_dependencies_{dependencyScoped.Status.ToLowerInvariant()}"
                : expandedCorridorScoped is not null
                    ? $"full_macro_after_expanded_corridor_{expandedCorridorScoped.Status.ToLowerInvariant()}"
                : corridorScoped is not null
                    ? $"full_macro_after_corridor_{corridorScoped.Status.ToLowerInvariant()}"
                : evidenceScoped is not null
                    ? $"full_macro_after_evidence_scope_{evidenceScoped.Status.ToLowerInvariant()}"
                : timeFirst is null
                    ? "full_macro"
                    : $"full_macro_after_time_only_{timeFirst.Status.ToLowerInvariant()}",
            clock.Elapsed.TotalSeconds);
    }

    private static string? PhaseLogPath(string? logFile, string phase)
    {
        if (string.IsNullOrWhiteSpace(logFile)) return null;
        string extension = Path.GetExtension(logFile);
        string withoutExtension = extension.Length == 0
            ? logFile
            : logFile[..^extension.Length];
        return $"{withoutExtension}_{phase}{extension}";
    }

    internal static DynamicHotspotEvidence CreateEvidenceFromStationAnalysisForExecution(
        StationLevelMicroFeasibilitySlaveResult analysis,
        string candidateFingerprint,
        IReadOnlyList<string> corridorStationOrder,
        string sourceKind)
    {
        MacroMicroStationConflictSignal[] signals = analysis.StationResults
            .Where(result => !result.IsFeasible)
            .Select(result => new MacroMicroStationConflictSignal(
                result.StationName,
                Math.Max(1, Math.Max(result.ConflictCore.ConflictCount, result.Conflicts.Count)),
                result.ConflictCore.SupportStopIds,
                result.ConflictCore.TrainIndices,
                result.ConflictCore.SupportPrecedenceKeys))
            .ToArray();
        return MacroMicroFeedbackPolicy.CreateGlobalConflictEvidence(
            CalculateIdentity(sourceKind, candidateFingerprint, signals.Select(signal =>
                $"{signal.StationName}:{signal.ConflictCount}:{string.Join(',', signal.SupportStopIds)}:" +
                $"{string.Join(',', signal.SupportTrainIndices ?? [])}:" +
                $"{string.Join(',', signal.SupportPrecedenceKeys ?? [])}")),
            analysis.Status,
            signals,
            corridorStationOrder);
    }

    private static DynamicHotspotEvidence CreateEvidenceFromStationAnalysis(
        StationLevelMicroFeasibilitySlaveResult analysis,
        string candidateFingerprint,
        IReadOnlyList<string> corridorStationOrder,
        string sourceKind) => CreateEvidenceFromStationAnalysisForExecution(
        analysis,
        candidateFingerprint,
        corridorStationOrder,
        sourceKind);

    private static void WriteMicroCheck(
        string path,
        string candidateFingerprint,
        string recheckStage,
        string hotspotStatus,
        StationLevelMicroFeasibilitySlaveResult analysis)
    {
        int unknownCount = analysis.StationResults.Count(result =>
            result.AssignmentResult.Status == MicroSolveStatus.Unknown);
        WriteJson(path, new
        {
            candidateFingerprint,
            recheckStage,
            hotspotStatus,
            status = analysis.Status.ToString(),
            conflictCount = analysis.Conflicts.Count,
            unknownCount,
            conflicts = analysis.Conflicts,
            routeChoiceDiagnostics = analysis.RouteChoiceDiagnostics,
            stationResults = analysis.StationResults.Select(result => new
            {
                result.StationName,
                result.IsFeasible,
                status = result.AssignmentResult.Status.ToString(),
                conflictCount = result.Conflicts.Count,
                conflicts = result.Conflicts,
                routeChoiceDiagnostics = result.RouteChoiceDiagnostics,
                result.ConflictCore
            })
        });
    }

    internal static (string FinalStatus, string FinalReason, string NextAction) ClassifyNoCandidate(
        bool gatePassed,
        string gateStatus,
        string solveStatus)
    {
        if (!gatePassed)
            return ("model_size_gate_rejected", gateStatus, "stop_model_size_gate");
        if (string.Equals(solveStatus, "INFEASIBLE", StringComparison.Ordinal))
            return ("infeasible_without_mapped_proof", "infeasible_status_has_no_model_local_proof_rows", "stop_without_cut");
        return ("integrated_solve_unknown", "timeout_or_interruption_does_not_create_permanent_exclusion", "stop_without_cut");
    }

    internal static DirectFeedbackNoCandidateDecision ClassifyDirectFeedbackNoCandidate(
        bool isPermanent,
        string macroStatus)
    {
        if (!isPermanent)
        {
            return new DirectFeedbackNoCandidateDecision(
                false,
                string.Equals(macroStatus, "INFEASIBLE", StringComparison.OrdinalIgnoreCase)
                    ? "temporary_macro_repair_withdrawn"
                    : "temporary_macro_repair_unknown",
                "withdraw_candidate_bound_proposal_and_use_existing_proof_linked_hotspot");
        }

        return new DirectFeedbackNoCandidateDecision(
            true,
            string.Equals(macroStatus, "INFEASIBLE", StringComparison.OrdinalIgnoreCase)
                ? "complete_macro_with_proven_feedback_infeasible"
                : "complete_macro_replot_unknown",
            string.Equals(macroStatus, "INFEASIBLE", StringComparison.OrdinalIgnoreCase)
                ? "retain_proven_cut_and_stop_no_macro_candidate"
                : "stop_unknown_without_new_cut");
    }

    private static string[] ResolveCorridorStationOrder(TimetablingInstance instance)
    {
        return instance.TrainStops.GroupBy(stop => stop.StationOrder)
            .OrderBy(group => group.Key)
            .Select(group =>
            {
                string[] stations = group.Select(stop => stop.StationName)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (stations.Length != 1)
                    throw new InvalidDataException(
                        $"Station order {group.Key} is not one fixed corridor station: {string.Join('|', stations)}.");
                return stations[0];
            })
            .ToArray();
    }

    private static string CalculateIdentity(string kind, string candidateFingerprint, IEnumerable<string> details)
    {
        string payload = string.Join('\n', new[] { kind, candidateFingerprint }.Concat(details.Order(StringComparer.Ordinal)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    private static void WriteTrace(string path, IReadOnlyList<MacroMicroFeedbackAttempt> attempts)
    {
        StringBuilder text = new();
        text.AppendLine("iteration,reference_fingerprint,evidence_identity,stations,stop_ids,scope_status,variables,binaries,constraints,presolved_variables,presolved_binaries,presolved_constraints,retained_hard_patterns,solve_status,build_seconds,presolve_seconds,solve_seconds,proof_extraction_strategy,proof_extraction_seconds,global_micro_seconds,global_micro_conflicts,global_micro_unknown,candidate_fingerprint,next_action,elapsed_seconds,feedback_class,feedback_id,complete_macro_replot_called,complete_macro_replot_status,complete_macro_replot_seconds,complete_macro_decision_changes,parent_candidate_fingerprint,replotted_candidate_fingerprint");
        foreach (MacroMicroFeedbackAttempt attempt in attempts)
        {
            text.AppendLine(string.Join(',', new[]
            {
                attempt.Iteration.ToString(CultureInfo.InvariantCulture),
                Csv(attempt.ReferenceFingerprint), Csv(attempt.EvidenceIdentity),
                Csv(string.Join('|', attempt.Stations)), Csv(string.Join('|', attempt.StopIds)),
                Csv(attempt.ScopeStatus),
                attempt.Variables.ToString(CultureInfo.InvariantCulture),
                attempt.Binaries.ToString(CultureInfo.InvariantCulture),
                attempt.Constraints.ToString(CultureInfo.InvariantCulture),
                attempt.PresolvedVariables.ToString(CultureInfo.InvariantCulture),
                attempt.PresolvedBinaries.ToString(CultureInfo.InvariantCulture),
                attempt.PresolvedConstraints.ToString(CultureInfo.InvariantCulture),
                attempt.RetainedHardPatternCount.ToString(CultureInfo.InvariantCulture),
                Csv(attempt.SolveStatus),
                attempt.BuildSeconds.ToString("R", CultureInfo.InvariantCulture),
                attempt.PresolveSeconds.ToString("R", CultureInfo.InvariantCulture),
                attempt.SolveSeconds.ToString("R", CultureInfo.InvariantCulture),
                Csv(attempt.ProofExtractionStrategy),
                attempt.ProofExtractionSeconds.ToString("R", CultureInfo.InvariantCulture),
                attempt.GlobalMicroSeconds.ToString("R", CultureInfo.InvariantCulture),
                attempt.GlobalMicroConflictCount.ToString(CultureInfo.InvariantCulture),
                attempt.GlobalMicroUnknownCount.ToString(CultureInfo.InvariantCulture),
                Csv(attempt.CandidateFingerprint), Csv(attempt.NextAction),
                attempt.ElapsedSeconds.ToString("R", CultureInfo.InvariantCulture),
                Csv(attempt.FeedbackClass), Csv(attempt.FeedbackId),
                attempt.CompleteMacroReplotCalled ? "true" : "false",
                Csv(attempt.CompleteMacroReplotStatus),
                attempt.CompleteMacroReplotSeconds.ToString("R", CultureInfo.InvariantCulture),
                attempt.CompleteMacroDecisionChangeCount.ToString(CultureInfo.InvariantCulture),
                Csv(attempt.ParentCandidateFingerprint), Csv(attempt.ReplottedCandidateFingerprint)
            }));
        }
        File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
    }

    private static void WriteResult(
        string path,
        DynamicHotspotResolvedRun resolved,
        IReadOnlyList<MacroMicroFeedbackAttempt> attempts,
        string status,
        string reason,
        bool joint,
        bool readback,
        string finalStatePath,
        double seconds)
    {
        StringBuilder text = new();
        text.AppendLine("# Macro–Micro feedback-loop experiment");
        text.AppendLine();
        text.AppendLine($"- Strict joint feasible: **{joint}**");
        text.AppendLine($"- Fixed-resource readback passed: **{readback}**");
        text.AppendLine($"- Final status: `{status}`");
        text.AppendLine($"- Reason: `{reason}`");
        text.AppendLine($"- Attempts: {attempts.Count}");
        text.AppendLine($"- Total seconds: {seconds:F3}");
        text.AppendLine($"- Final state: `{finalStatePath}`");
        text.AppendLine();
        text.AppendLine("The saved rolling Macro is independently revalidated before use. A failed hotspot Micro solve now creates candidate-bound typed feedback and calls a separate complete Macro model. Every accepted replot is saved with its parent fingerprint and decision diff, then checked again with that exact fixed Macro candidate.");
        text.AppendLine();
        text.AppendLine("A fixed-pattern IIS is a retractable type-B proposal, never an unconditional permanent cut. Type-A persistence is reserved for proofs that eliminate the complete protected route and continuous-time domain. Unknown creates no exclusion. The final acceptance test requires one common full-line timetable, zero Macro violations, zero Global Micro conflicts/Unknown, the exact input-derived assignment identity set, fixed-resource validation, and serialization readback.");
        text.AppendLine();
        text.AppendLine($"- Baseline: `{resolved.BaselineDirectory}`");
        text.AppendLine($"- Initial proof: `{resolved.ProofPath}`");
        File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
    }

    private static void WriteReproduce(
        DynamicHotspotResolvedRun resolved,
        string checkpointPath,
        double totalBudgetSeconds,
        double iterationSeconds,
        double saveReserveSeconds)
    {
        string path = Path.Combine(resolved.OutputDirectory, "reproduce.ps1");
        string command =
            "dotnet run --no-build -c Release --project .\\src\\GurobiTimetablingResearch\\GurobiTimetablingResearch.csproj -- " +
            $"\"{resolved.DatasetDirectory}\" --macro-micro-resume " +
            $"--macro-micro-checkpoint=\"{checkpointPath}\" " +
            $"--macro-micro-total-budget-seconds={totalBudgetSeconds.ToString("R", CultureInfo.InvariantCulture)} " +
            $"--macro-micro-iteration-seconds={iterationSeconds.ToString("R", CultureInfo.InvariantCulture)} " +
            $"--macro-micro-save-reserve-seconds={saveReserveSeconds.ToString("R", CultureInfo.InvariantCulture)}";
        File.WriteAllText(path, command + Environment.NewLine, new UTF8Encoding(false));
    }

    private static void WriteJson<T>(string path, T value) =>
        File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(false));

    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
}
