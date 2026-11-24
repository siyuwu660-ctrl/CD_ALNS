// 自适应命令行执行层负责参数解释、初始快照与检查点校验、运行选项构造以及最终控制台摘要。
// 路由器将 CLI 字符串转换为强类型配置，并通过预检对象阻止不一致的续跑输入进入求解引擎。
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

internal static class ProgramHelpText
{
    internal static string Build() => """
        GurobiTimetablingResearch

        Joint-feasible checkpoint readback verification (does not optimize timetable):
          <dataset-directory>
          --verify-joint-feasible-checkpoint=<clean_checkpoint.json>
          --joint-feasible-baseline-output=<new_empty_directory>

        Resumable complete Macro -> Macro-Micro feedback execution:
          Fresh (builds a complete rolling Macro first):
            <dataset-directory>
            --macro-micro-fresh
            --macro-micro-output=<run_directory>
            --macro-micro-total-budget-seconds=7200
            --macro-micro-iteration-seconds=300
            --macro-micro-save-reserve-seconds=120

          Resume (never rebuilds the initial Macro):
            <dataset-directory>
            --macro-micro-resume
            --macro-micro-checkpoint=<run_directory\state\macro_micro_feedback.checkpoint.json>
            --macro-micro-total-budget-seconds=7200
            --macro-micro-iteration-seconds=300
            --macro-micro-save-reserve-seconds=120

          The total budget is cumulative from the first Micro check. Ctrl+C requests a
          bounded safe stop; the current solve returns before the checkpoint is replaced.
          Reusing the same fresh output directory automatically resumes its checkpoint.

        Adaptive Singleton fixed-snapshot Checker:
          <dataset-directory>                         for example .\Data\JZ
          --solve
          --checker-loop-multi-iteration
          --checker-loop-zero-conflict
          --checker-global-evaluate-feasible-singletons
          --checker-iis-guided-repair
          --checker-loop-adaptive-singleton-convergence
          --master-snapshot-input=<master_snapshot.json>       fresh run only
          --checker-loop-checkpoint-input=<path>               resume only
          --checker-loop-checkpoint-output=<path>              required
          --artifacts-output=<new_run_directory>               optional isolated output root
          --checker-adaptive-artifacts-output=<path>            optional unique run directory
          --checker-adaptive-expected-initial-root-conflicts=835
          --checker-loop-max-iterations=<N>       accepted-transition safety cap; N > 0
          --checker-loop-initial-candidate-cap=20
          --checker-loop-expanded-candidate-cap=40
          --time-limit-seconds=40
          --checker-loop-secondary-time-limit-seconds=40
          --checker-repair-running-time-supplement-ratio=0.05
          --checker-loop-recourse-windows-seconds=300,600,1200,1800
          --checker-iis-max-expansion-rounds=5
          --checker-iis-temporal-expansion-step-seconds=300
          --checker-iis-max-extra-temporal-seconds=1800
          --checker-loop-endpoint-occupation-seconds=0
          --disable-micro-promotion
          --checker-loop-total-runtime-budget-seconds=<seconds>
          --checker-validity-total-runtime-budget-seconds=3600
          --checker-validity-final-validation-reserve-seconds=120
          --checker-validity-hotspot-max-requirements=40
          --checker-validity-max-feedback-only-rounds=2
          --checker-validity-recovery-mip-focus=1
          --checker-validity-recovery-mip-gap=0.05
          --checker-validity-iis-time-limit-seconds=10
          --checker-validity-iis-cumulative-budget-seconds=60
          --checker-validity-local-neighborhood-time-limit-seconds=60
          --checker-validity-fixed-assignment-retiming-time-limit-seconds=120
          --checker-validity-conflict-linked-retiming-time-limit-seconds=300
          --repair-neighborhood-selector=legacy|propagation
          --repair-action-selector=legacy|rule_based
          --checker-stop-after-first-joint-feasible
          --checker-macro-candidate-strategy=complete-objectives|early-check
          --macro-full-relations-build-only                    build full Macro relation model without Optimize
          --checker-validity-previously-consumed-runtime-seconds=<seconds>
          --checker-validity-replay-neighborhood-input=<temporary_restrictions.json>
          --checker-validity-replay-hotspot-station=<station>
          --checker-validity-phased-recovery
          --checker-validity-recovery-hotspot-max-requirements=80
          --checker-validity-recovery-hotspot-max-alternatives=2000
          --checker-validity-recovery-hotspot-max-estimated-variables=200000
          --checker-validity-recovery-hotspot-max-estimated-constraints=500000
          --checker-validity-recovery-hotspot-max-conflict-relations=150000
          --checker-loop-max-initial-total-retiming-seconds=<seconds>
          --checker-loop-max-initial-point-retiming-seconds=<seconds>

        Fresh input and checkpoint resume input are mutually exclusive. Adaptive checkpoint v3
        preserves the immutable initial reference and accepted-transition continuity.
        LocalMicroExact and component-wise repair are not available in Adaptive mode.
        """;
}

internal static class AdaptiveCheckerExecutionRouter
{
    internal static T Execute<T>(
        bool adaptiveEnabled,
        Func<T> adaptive,
        Func<T> legacy)
    {
        ArgumentNullException.ThrowIfNull(adaptive);
        ArgumentNullException.ThrowIfNull(legacy);
        return adaptiveEnabled ? adaptive() : legacy();
    }
}

internal sealed class AdaptiveSingletonInitialState<TModel> : IDisposable
    where TModel : class, IDisposable
{
    private AdaptiveAcceptedState<TModel>? _acceptedState;

    internal AdaptiveSingletonInitialState(
        AdaptiveAcceptedState<TModel> acceptedState,
        InitialReferenceDescriptor initialReference)
    {
        _acceptedState = acceptedState ?? throw new ArgumentNullException(nameof(acceptedState));
        InitialReference = initialReference ?? throw new ArgumentNullException(nameof(initialReference));
    }

    internal AdaptiveAcceptedState<TModel> AcceptedState => _acceptedState
        ?? throw new ObjectDisposedException(nameof(AdaptiveSingletonInitialState<TModel>));
    internal InitialReferenceDescriptor InitialReference { get; }

    internal AdaptiveAcceptedState<TModel> TransferAcceptedState()
    {
        AdaptiveAcceptedState<TModel> acceptedState = _acceptedState
            ?? throw new InvalidOperationException(
                "The Adaptive initial accepted state was already transferred or disposed.");
        _acceptedState = null;
        return acceptedState;
    }

    public void Dispose()
    {
        _acceptedState?.Dispose();
        _acceptedState = null;
    }
}

internal static class AdaptiveSingletonInitialStateFactory
{
    internal static AdaptiveSingletonInitialState<TModel> Create<TModel>(
        TModel acceptedModel,
        MasterSolutionSnapshot snapshot,
        MicroFeasibilityReport microReport,
        IReadOnlyList<SectionConflictEdgeRequest> sectionEdges,
        IReadOnlyList<StationEventConflictCandidate> stationEdges,
        IReadOnlyList<string> acceptedCandidateIds,
        IReadOnlyList<PromotedMicroComponent> promotedComponents)
        where TModel : class, IDisposable
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        AdaptiveAcceptedState<TModel>? acceptedState = null;
        TModel? unownedModel = acceptedModel;
        try
        {
            acceptedState = new AdaptiveAcceptedState<TModel>(
                unownedModel,
                snapshot,
                microReport,
                sectionEdges,
                stationEdges,
                acceptedCandidateIds,
                promotedComponents,
                acceptedTransitionIndex: 0);
            unownedModel = null;
            MasterSolutionSnapshot initialReferenceSnapshot = AdaptiveAcceptedStateCopies.Copy(snapshot);
            InitialReferenceDescriptor initialReference = new(
                initialReferenceSnapshot,
                SnapshotFingerprint.Calculate(initialReferenceSnapshot));
            AdaptiveSingletonInitialState<TModel> result = new(acceptedState, initialReference);
            acceptedState = null;
            return result;
        }
        catch
        {
            acceptedState?.Dispose();
            unownedModel?.Dispose();
            throw;
        }
    }

    internal static AdaptiveSingletonInitialState<TModel> Resume<TModel>(
        TModel acceptedModel,
        CheckerCheckpoint checkpoint,
        MicroFeasibilityReport recomputedMicroReport,
        AdaptiveCheckpointResumeConfiguration configuration)
        where TModel : class, IDisposable
    {
        TModel? unownedModel = acceptedModel;
        AdaptiveAcceptedState<TModel>? acceptedState = null;
        try
        {
            AdaptiveCheckpointResumeValidator.ValidateConfiguration(checkpoint, configuration);
            MasterSolutionSnapshot initialReferenceSnapshot = checkpoint.InitialReferenceSnapshot
                ?? throw new InvalidDataException(
                    "Adaptive resume checkpoint has no immutable initial reference snapshot.");
            string actualInitialReferenceFingerprint = SnapshotFingerprint.Calculate(
                initialReferenceSnapshot);
            if (!string.Equals(
                    actualInitialReferenceFingerprint,
                    checkpoint.InitialReferenceSnapshotFingerprint,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Adaptive resume initial-reference fingerprint does not match its snapshot.");
            }
            string acceptedFingerprint = SnapshotFingerprint.Calculate(checkpoint.Snapshot);
            if (!string.Equals(
                    acceptedFingerprint,
                    checkpoint.AcceptedStateFingerprint,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Adaptive resume accepted-state fingerprint does not match its snapshot.");
            }
            string recomputedConflictFingerprint = MicroConflictFingerprint.Calculate(
                recomputedMicroReport.Conflicts);
            if (!string.Equals(
                    recomputedConflictFingerprint,
                    checkpoint.MicroConflictFingerprint,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Adaptive resume Global Micro conflict fingerprint does not match checkpoint evidence.");
            }
            MicroConflictMetrics recomputedMetrics = MicroConflictMetricCalculator.Calculate(
                recomputedMicroReport.Conflicts);
            MicroConflictCategoryCounts recomputedCategories = MicroConflictCategoryCounter.Calculate(
                recomputedMicroReport.Conflicts);
            AdaptiveCheckpointMicroMetrics storedMetrics = checkpoint.MicroMetrics
                ?? throw new InvalidDataException("Adaptive resume checkpoint has no Micro metrics.");
            if (storedMetrics.RootConflictCount != recomputedMetrics.RootConflictCount ||
                storedMetrics.EpisodeConflictCount != recomputedMetrics.EpisodeConflictCount ||
                storedMetrics.ExactConflictCount != recomputedCategories.ExactHeadwayConflictCount ||
                storedMetrics.AssignmentConflictCount !=
                recomputedCategories.StationAssignmentConflictCount)
            {
                throw new InvalidDataException(
                    "Adaptive resume Global Micro metrics do not match checkpoint evidence.");
            }
            AdaptiveSingletonQualityMetrics? lastQuality = checkpoint.LastWinnerQuality is null
                ? null
                : new AdaptiveSingletonQualityMetrics(
                    checkpoint.LastWinnerQuality.StepTotalRetimingSeconds,
                    checkpoint.LastWinnerQuality.InitialTotalRetimingSeconds,
                    checkpoint.LastWinnerQuality.StepMaximumRetimingSeconds,
                    checkpoint.LastWinnerQuality.InitialMaximumRetimingSeconds,
                    checkpoint.LastWinnerQuality.IncrementalRunningSupplementSeconds,
                    checkpoint.LastWinnerQuality.CurrentTotalRunningSupplementSeconds,
                    checkpoint.LastWinnerQuality.CurrentSupplementSectionCount);
            acceptedState = new AdaptiveAcceptedState<TModel>(
                unownedModel,
                checkpoint.Snapshot,
                recomputedMicroReport,
                checkpoint.ActiveSectionEdges,
                checkpoint.ActiveStationEventEdges,
                checkpoint.AcceptedStructuralCandidateIds.ToList(),
                checkpoint.PromotedMicroComponents ?? [],
                checkpoint.LastAcceptedIteration,
                checkpoint.LastWinnerLayer,
                checkpoint.LastWinnerCandidateId,
                lastQuality);
            unownedModel = null;
            AdaptiveSingletonInitialState<TModel> result = new(
                acceptedState,
                new InitialReferenceDescriptor(
                    AdaptiveAcceptedStateCopies.Copy(initialReferenceSnapshot),
                    actualInitialReferenceFingerprint));
            acceptedState = null;
            return result;
        }
        catch
        {
            acceptedState?.Dispose();
            unownedModel?.Dispose();
            throw;
        }
    }
}

internal sealed record AdaptiveCheckpointResumeConfiguration(
    string SearchConfigurationFingerprint,
    int InitialCandidateCap,
    int ExpandedCandidateCap,
    int SafetyAcceptedTransitionCap)
{
    internal static AdaptiveCheckpointResumeConfiguration From(CheckerLoopOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        AdaptiveSingletonOptions adaptive = options.AdaptiveSingletonOptions.Validate();
        string canonical = string.Join('|',
            "adaptive-search-v1",
            options.MaxIterations,
            adaptive.InitialCandidateCap,
            adaptive.ExpandedCandidateCap,
            options.TimeLimitSecondsPerSolve.ToString("R", CultureInfo.InvariantCulture),
            options.SecondaryTimeLimitSecondsPerSolve.ToString("R", CultureInfo.InvariantCulture),
            string.Join(',', options.RecourseWindowSeconds),
            options.ExactPerStationCap,
            options.ExactPerPairCap,
            options.EndpointOccupationSeconds,
            options.RepairRunningTimeSupplementRatio.ToString("R", CultureInfo.InvariantCulture),
            options.IisGuidedOptions.MaxExpansionRounds,
            options.IisGuidedOptions.TemporalExpansionStepSeconds,
            options.IisGuidedOptions.MaxExtraTemporalSeconds,
            options.ZeroConflictMode,
            options.EnableFeasibleSingletonGlobalEvaluation,
            options.EnableLocalMicroRepair,
            options.EnableMicroPromotion);
        return new AdaptiveCheckpointResumeConfiguration(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant(),
            adaptive.InitialCandidateCap,
            adaptive.ExpandedCandidateCap,
            options.MaxIterations);
    }
}

internal static class AdaptiveCheckpointResumeValidator
{
    internal static void ValidateConfiguration(
        CheckerCheckpoint checkpoint,
        AdaptiveCheckpointResumeConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(configuration);
        if (!checkpoint.AdaptiveSingletonMode || checkpoint.InitialReferenceSnapshot is null)
        {
            throw new InvalidDataException(
                "Adaptive resume requires a v3 Adaptive checkpoint with an initial reference.");
        }
        if (!string.Equals(
                checkpoint.SearchConfigurationFingerprint,
                configuration.SearchConfigurationFingerprint,
                StringComparison.Ordinal) ||
            checkpoint.InitialCandidateCap != configuration.InitialCandidateCap ||
            checkpoint.ExpandedCandidateCap != configuration.ExpandedCandidateCap ||
            checkpoint.SafetyAcceptedTransitionCap != configuration.SafetyAcceptedTransitionCap)
        {
            throw new InvalidDataException(
                "Adaptive resume search configuration does not match the checkpoint.");
        }
    }
}

internal static class AdaptiveSingletonRunOptionsFactory
{
    internal static AdaptiveSingletonRunOptions Create(CheckerLoopOptions checkerOptions)
    {
        ArgumentNullException.ThrowIfNull(checkerOptions);
        AdaptiveSingletonOptions adaptive = checkerOptions.AdaptiveSingletonOptions.Validate();
        if (!adaptive.Enabled)
        {
            throw new InvalidOperationException(
                "Adaptive RunEngine options require Adaptive singleton convergence to be enabled.");
        }
        return new AdaptiveSingletonRunOptions(
            checkerOptions.MaxIterations,
            adaptive.TotalRuntimeBudgetSeconds,
            new AdaptiveQualityBudgetOptions(
                adaptive.MaxInitialTotalRetimingSeconds,
                adaptive.MaxInitialPointRetimingSeconds)).Validate();
    }
}

internal sealed record AdaptiveBaselinePreflightResult(bool Passed, string Evidence);

internal static class AdaptiveBaselinePreflight
{
    internal static AdaptiveBaselinePreflightResult Evaluate(
        int expectedRootConflicts,
        int actualRootConflicts,
        int masterValidatorViolationCount,
        int acceptedTransitionIndex,
        MicroSolveStatus microStatus)
    {
        if (expectedRootConflicts < 0 || actualRootConflicts < 0 ||
            masterValidatorViolationCount < 0 || acceptedTransitionIndex < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedRootConflicts),
                "Adaptive baseline evidence counts must be non-negative.");
        }
        bool passed = expectedRootConflicts == actualRootConflicts &&
                      masterValidatorViolationCount == 0 &&
                      acceptedTransitionIndex == 0 &&
                      microStatus != MicroSolveStatus.Unknown;
        return new AdaptiveBaselinePreflightResult(
            passed,
            $"expected_root={expectedRootConflicts};actual_root={actualRootConflicts};" +
            $"validator_violations={masterValidatorViolationCount};" +
            $"transition_index={acceptedTransitionIndex};micro_status={microStatus}");
    }
}

internal sealed record AdaptiveSingletonInvocationResult(
    int InitialRootConflictCount,
    int FinalRootConflictCount,
    int AcceptedTransitionCount,
    string TerminationReason,
    string FinalAcceptedStateFingerprint,
    int UnresolvedUnknownCount,
    int QualityBlockedCount,
    MasterSolutionSnapshot FinalAcceptedSnapshot,
    MicroFeasibilityReport FinalMicroReport,
    MicroFeasibilityReport? InitialMicroReport = null,
    string InitialReferenceFingerprint = "",
    string InitialAcceptedStateFingerprint = "",
    MasterValidationReport? FinalValidation = null,
    IReadOnlyList<SectionConflictEdgeRequest>? FinalSectionEdges = null,
    IReadOnlyList<StationEventConflictCandidate>? FinalStationEdges = null,
    IReadOnlyList<string>? AcceptedCandidateIds = null,
    IReadOnlyList<AdaptiveSingletonSweepDiagnostic>? SweepDiagnostics = null,
    IReadOnlyList<AdaptiveSingletonCompletedSweep>? SweepHistory = null,
    IReadOnlyList<StructuralSingletonGlobalTrialDiagnostic>? CandidateTrials = null,
    IReadOnlyList<AdaptiveUnknownEvidence>? UnknownEvidence = null,
    double TotalRuntimeSeconds = 0.0);

internal static class AdaptiveSingletonConsoleSummaryFormatter
{
    internal static IReadOnlyList<string> Format(AdaptiveSingletonInvocationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return
        [
            $"Adaptive initial root conflicts: {result.InitialRootConflictCount}",
            $"Adaptive final root conflicts: {result.FinalRootConflictCount}",
            $"Adaptive accepted transition count: {result.AcceptedTransitionCount}",
            $"Adaptive termination reason: {result.TerminationReason}",
            $"Adaptive final accepted-state fingerprint: {result.FinalAcceptedStateFingerprint}",
            $"Adaptive unresolved Unknown count: {result.UnresolvedUnknownCount}",
            $"Adaptive quality-blocked count: {result.QualityBlockedCount}"
        ];
    }
}
