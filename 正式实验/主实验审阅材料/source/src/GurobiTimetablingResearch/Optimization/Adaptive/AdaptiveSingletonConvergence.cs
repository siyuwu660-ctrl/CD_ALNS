// 收敛协调器用显式状态机描述各候选层的扫描、接受、跳过和终止转换，并将每次转换记录为事件序列。
// 状态中保存未决证据、分层摘要和停止原因，用于在预算受限时判断继续扩展、重扫还是稳定终止。
namespace GurobiTimetablingResearch.Optimization;

public sealed record AdaptiveSingletonOptions(
    bool Enabled,
    int InitialCandidateCap,
    int ExpandedCandidateCap,
    double? TotalRuntimeBudgetSeconds,
    double? MaxInitialTotalRetimingSeconds,
    double? MaxInitialPointRetimingSeconds,
    string? InitialReferenceSnapshotInput)
{
    internal static AdaptiveSingletonOptions Disabled { get; } =
        new(false, 20, 40, null, null, null, null);

    internal AdaptiveSingletonOptions Validate()
    {
        if (InitialCandidateCap <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(InitialCandidateCap));
        }
        if (ExpandedCandidateCap < InitialCandidateCap)
        {
            throw new ArgumentException(
                "Expanded singleton candidate cap must be greater than or equal to the initial cap.",
                nameof(ExpandedCandidateCap));
        }
        ValidateOptionalPositive(TotalRuntimeBudgetSeconds, nameof(TotalRuntimeBudgetSeconds));
        ValidateOptionalNonNegative(
            MaxInitialTotalRetimingSeconds,
            nameof(MaxInitialTotalRetimingSeconds));
        ValidateOptionalNonNegative(
            MaxInitialPointRetimingSeconds,
            nameof(MaxInitialPointRetimingSeconds));
        return this;
    }

    private static void ValidateOptionalPositive(double? value, string parameterName)
    {
        if (value.HasValue && (!double.IsFinite(value.Value) || value.Value <= 0.0))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static void ValidateOptionalNonNegative(double? value, string parameterName)
    {
        if (value.HasValue && (!double.IsFinite(value.Value) || value.Value < 0.0))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}

internal enum AdaptiveSingletonStateKind
{
    InitialDirectEvaluating,
    InitialIisEvaluating,
    ExpandedDirectEvaluating,
    ExpandedIisEvaluating,
    WinnerReadyForQualityCheck,
    WinnerAccepted,
    MicroZero,
    PolicyStagnationConfirmed,
    UnresolvedUnknown,
    SafetyCapReached,
    RuntimeBudgetReached,
    QualityBudgetExhausted,
    SolverFailure,
    UserCancelled
}

internal enum AdaptiveSingletonCommandKind
{
    None,
    EvaluateInitialDirect,
    EvaluateInitialIis,
    BuildExpandedPool,
    EvaluateExpandedDirect,
    EvaluateExpandedIis,
    PrepareWinner,
    CommitWinner,
    SaveTerminalCheckpoint
}

internal enum AdaptiveSingletonEventKind
{
    LayerCompletedWithoutWinner,
    StrictWinnerFound,
    WinnerCommitted,
    AcceptedStateReady,
    RuntimeBudgetReached,
    QualityBudgetBlocksAllImprovingCandidates,
    SolverFailed,
    UserCancelled
}

public sealed record AdaptiveUnknownEvidence(
    string CandidateId,
    string Layer,
    string Source,
    string SolverStatus,
    int? ExpansionRound,
    bool Incomplete);

internal sealed record AdaptiveLayerSummary(
    string Layer,
    int EvaluatedCandidateCount,
    int StrictImprovingCandidateCount,
    int BudgetRejectedCandidateCount,
    int UnknownCandidateCount,
    int FeasibleProbeCount = 0,
    int ProvenInfeasibleProbeCount = 0,
    IReadOnlyList<string>? EvaluatedCandidateIds = null,
    IReadOnlyList<string>? SkippedCandidateIds = null,
    IReadOnlyList<string>? QualityBlockedCandidateIds = null);

internal sealed record AdaptiveSingletonEvent(
    AdaptiveSingletonEventKind Kind,
    string CandidateId = "",
    string AcceptedStateFingerprint = "",
    int? RootConflictCountAfter = null,
    string SweepEvidenceKey = "",
    AdaptiveLayerSummary? LayerSummary = null,
    IReadOnlyList<AdaptiveUnknownEvidence>? UnknownEvidence = null);

internal sealed record AdaptiveSingletonCoordinatorState(
    AdaptiveSingletonStateKind Kind,
    string AcceptedStateFingerprint,
    int AcceptedTransitionCount,
    int SafetyAcceptedTransitionCap,
    string SweepEvidenceKey,
    IReadOnlyList<AdaptiveUnknownEvidence> UnknownEvidence,
    IReadOnlyList<AdaptiveLayerSummary> LayerSummaries,
    string PendingWinnerCandidateId = "",
    AdaptiveSingletonStateKind? WinnerSourceState = null)
{
    internal static AdaptiveSingletonCoordinatorState Start(
        string acceptedStateFingerprint,
        int safetyAcceptedTransitionCap,
        string sweepEvidenceKey,
        int acceptedTransitionCount = 0)
    {
        if (string.IsNullOrWhiteSpace(acceptedStateFingerprint))
        {
            throw new ArgumentException("Accepted-state fingerprint is required.", nameof(acceptedStateFingerprint));
        }
        if (safetyAcceptedTransitionCap <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(safetyAcceptedTransitionCap));
        }
        if (acceptedTransitionCount < 0 || acceptedTransitionCount > safetyAcceptedTransitionCap)
        {
            throw new ArgumentOutOfRangeException(nameof(acceptedTransitionCount));
        }
        return new AdaptiveSingletonCoordinatorState(
            AdaptiveSingletonStateKind.InitialDirectEvaluating,
            acceptedStateFingerprint,
            acceptedTransitionCount,
            safetyAcceptedTransitionCap,
            sweepEvidenceKey,
            Array.Empty<AdaptiveUnknownEvidence>(),
            Array.Empty<AdaptiveLayerSummary>());
    }
}

internal sealed record AdaptiveSingletonTransition(
    AdaptiveSingletonCoordinatorState State,
    AdaptiveSingletonCommandKind Command,
    string TerminationReason = "");

internal static class AdaptiveSingletonTerminationReasons
{
    internal const string MicroZero = "micro_zero_conflicts_reached";
    internal const string PolicyStagnation = "singleton_policy_stagnation_confirmed";
    internal const string SafetyCap = "safety_iteration_cap_reached";
    internal const string RuntimeBudget = "total_runtime_budget_reached";
    internal const string QualityBudget = "quality_budget_blocks_all_improving_singletons";
    internal const string UnresolvedUnknown = "unresolved_unknown_evidence";
    internal const string SolverFailure = "solver_failure";
    internal const string UserCancelled = "user_cancelled";
    internal const string CheckpointWriteFailure = "checkpoint_write_failure";
    internal const string BaselineMismatch = "baseline_mismatch";
}

internal static class AdaptiveSingletonConvergenceCoordinator
{
    internal static AdaptiveSingletonTransition Begin(
        AdaptiveSingletonCoordinatorState state,
        int currentRootConflictCount)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (currentRootConflictCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(currentRootConflictCount));
        }
        if (currentRootConflictCount == 0)
        {
            return Terminate(state, AdaptiveSingletonStateKind.MicroZero,
                AdaptiveSingletonTerminationReasons.MicroZero);
        }
        if (state.AcceptedTransitionCount >= state.SafetyAcceptedTransitionCap)
        {
            return Terminate(state, AdaptiveSingletonStateKind.SafetyCapReached,
                AdaptiveSingletonTerminationReasons.SafetyCap);
        }
        return new AdaptiveSingletonTransition(
            state,
            AdaptiveSingletonCommandKind.EvaluateInitialDirect);
    }

    internal static AdaptiveSingletonTransition Advance(
        AdaptiveSingletonCoordinatorState state,
        AdaptiveSingletonEvent evidence)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(evidence);

        if (IsTerminal(state.Kind))
        {
            return new AdaptiveSingletonTransition(state, AdaptiveSingletonCommandKind.None,
                ResolveTerminationReason(state.Kind));
        }

        AdaptiveSingletonTransition? interrupted = ResolveInterruption(state, evidence.Kind);
        if (interrupted is not null)
        {
            return interrupted;
        }

        if (evidence.Kind == AdaptiveSingletonEventKind.LayerCompletedWithoutWinner)
        {
            state = AccumulateLayerEvidence(state, evidence);
        }

        return (state.Kind, evidence.Kind) switch
        {
            (AdaptiveSingletonStateKind.InitialDirectEvaluating,
                AdaptiveSingletonEventKind.LayerCompletedWithoutWinner) =>
                Move(state, AdaptiveSingletonStateKind.InitialIisEvaluating,
                    AdaptiveSingletonCommandKind.EvaluateInitialIis),
            (AdaptiveSingletonStateKind.InitialIisEvaluating,
                AdaptiveSingletonEventKind.LayerCompletedWithoutWinner) =>
                Move(state, AdaptiveSingletonStateKind.ExpandedDirectEvaluating,
                    AdaptiveSingletonCommandKind.BuildExpandedPool),
            (AdaptiveSingletonStateKind.ExpandedDirectEvaluating,
                AdaptiveSingletonEventKind.LayerCompletedWithoutWinner) =>
                Move(state, AdaptiveSingletonStateKind.ExpandedIisEvaluating,
                    AdaptiveSingletonCommandKind.EvaluateExpandedIis),
            (AdaptiveSingletonStateKind.ExpandedIisEvaluating,
                AdaptiveSingletonEventKind.LayerCompletedWithoutWinner) =>
                FinishNoWinnerSweep(state),
            (AdaptiveSingletonStateKind.InitialDirectEvaluating or
             AdaptiveSingletonStateKind.InitialIisEvaluating or
             AdaptiveSingletonStateKind.ExpandedDirectEvaluating or
             AdaptiveSingletonStateKind.ExpandedIisEvaluating,
                AdaptiveSingletonEventKind.StrictWinnerFound) =>
                PrepareWinner(state, evidence.CandidateId),
            (AdaptiveSingletonStateKind.WinnerReadyForQualityCheck,
                AdaptiveSingletonEventKind.WinnerCommitted) =>
                AcceptWinner(state, evidence),
            (AdaptiveSingletonStateKind.WinnerAccepted,
                AdaptiveSingletonEventKind.AcceptedStateReady) =>
                ResolveAcceptedState(state, evidence),
            (_, AdaptiveSingletonEventKind.QualityBudgetBlocksAllImprovingCandidates) =>
                Terminate(state, AdaptiveSingletonStateKind.QualityBudgetExhausted,
                    AdaptiveSingletonTerminationReasons.QualityBudget),
            _ => throw new InvalidOperationException(
                $"Adaptive singleton transition '{state.Kind}' + '{evidence.Kind}' is invalid.")
        };
    }

    private static AdaptiveSingletonTransition? ResolveInterruption(
        AdaptiveSingletonCoordinatorState state,
        AdaptiveSingletonEventKind eventKind) => eventKind switch
        {
            AdaptiveSingletonEventKind.RuntimeBudgetReached =>
                Terminate(state, AdaptiveSingletonStateKind.RuntimeBudgetReached,
                    AdaptiveSingletonTerminationReasons.RuntimeBudget),
            AdaptiveSingletonEventKind.SolverFailed =>
                Terminate(state, AdaptiveSingletonStateKind.SolverFailure,
                    AdaptiveSingletonTerminationReasons.SolverFailure),
            AdaptiveSingletonEventKind.UserCancelled =>
                Terminate(state, AdaptiveSingletonStateKind.UserCancelled,
                    AdaptiveSingletonTerminationReasons.UserCancelled),
            _ => null
        };

    private static AdaptiveSingletonTransition Move(
        AdaptiveSingletonCoordinatorState state,
        AdaptiveSingletonStateKind next,
        AdaptiveSingletonCommandKind command) =>
        new(state with { Kind = next }, command);

    private static AdaptiveSingletonTransition PrepareWinner(
        AdaptiveSingletonCoordinatorState state,
        string candidateId)
    {
        if (string.IsNullOrWhiteSpace(candidateId))
        {
            throw new ArgumentException("A strict winner must have a candidate ID.", nameof(candidateId));
        }
        return new AdaptiveSingletonTransition(
            state with
            {
                Kind = AdaptiveSingletonStateKind.WinnerReadyForQualityCheck,
                PendingWinnerCandidateId = candidateId,
                WinnerSourceState = state.Kind
            },
            AdaptiveSingletonCommandKind.PrepareWinner);
    }

    private static AdaptiveSingletonTransition AcceptWinner(
        AdaptiveSingletonCoordinatorState state,
        AdaptiveSingletonEvent evidence)
    {
        if (string.IsNullOrWhiteSpace(evidence.AcceptedStateFingerprint))
        {
            throw new ArgumentException("Committed winner fingerprint is required.");
        }
        return new AdaptiveSingletonTransition(
            state with
            {
                Kind = AdaptiveSingletonStateKind.WinnerAccepted,
                AcceptedStateFingerprint = evidence.AcceptedStateFingerprint,
                AcceptedTransitionCount = state.AcceptedTransitionCount + 1,
                PendingWinnerCandidateId = string.Empty,
                WinnerSourceState = null,
                UnknownEvidence = Array.Empty<AdaptiveUnknownEvidence>(),
                LayerSummaries = Array.Empty<AdaptiveLayerSummary>(),
                SweepEvidenceKey = evidence.SweepEvidenceKey
            },
            AdaptiveSingletonCommandKind.CommitWinner);
    }

    private static AdaptiveSingletonTransition ResolveAcceptedState(
        AdaptiveSingletonCoordinatorState state,
        AdaptiveSingletonEvent evidence)
    {
        if (evidence.RootConflictCountAfter == 0)
        {
            return Terminate(state, AdaptiveSingletonStateKind.MicroZero,
                AdaptiveSingletonTerminationReasons.MicroZero);
        }
        if (state.AcceptedTransitionCount >= state.SafetyAcceptedTransitionCap)
        {
            return Terminate(state, AdaptiveSingletonStateKind.SafetyCapReached,
                AdaptiveSingletonTerminationReasons.SafetyCap);
        }
        return new AdaptiveSingletonTransition(
            state with
            {
                Kind = AdaptiveSingletonStateKind.InitialDirectEvaluating,
                SweepEvidenceKey = evidence.SweepEvidenceKey
            },
            AdaptiveSingletonCommandKind.EvaluateInitialDirect);
    }

    private static AdaptiveSingletonTransition FinishNoWinnerSweep(
        AdaptiveSingletonCoordinatorState state)
    {
        if (state.UnknownEvidence.Count > 0)
        {
            return Terminate(state, AdaptiveSingletonStateKind.UnresolvedUnknown,
                AdaptiveSingletonTerminationReasons.UnresolvedUnknown);
        }
        int improving = state.LayerSummaries.Sum(item => item.StrictImprovingCandidateCount);
        int rejected = state.LayerSummaries.Sum(item => item.BudgetRejectedCandidateCount);
        if (improving > 0 && rejected >= improving)
        {
            return Terminate(state, AdaptiveSingletonStateKind.QualityBudgetExhausted,
                AdaptiveSingletonTerminationReasons.QualityBudget);
        }
        return Terminate(state, AdaptiveSingletonStateKind.PolicyStagnationConfirmed,
            AdaptiveSingletonTerminationReasons.PolicyStagnation);
    }

    private static AdaptiveSingletonCoordinatorState AccumulateLayerEvidence(
        AdaptiveSingletonCoordinatorState state,
        AdaptiveSingletonEvent evidence)
    {
        if (!string.IsNullOrWhiteSpace(evidence.SweepEvidenceKey) &&
            !string.Equals(
                state.SweepEvidenceKey,
                evidence.SweepEvidenceKey,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Adaptive layer evidence belongs to a different accepted-state sweep.");
        }
        IReadOnlyList<AdaptiveUnknownEvidence> unknown = evidence.UnknownEvidence
            ?? Array.Empty<AdaptiveUnknownEvidence>();
        List<AdaptiveUnknownEvidence> accumulatedUnknown = state.UnknownEvidence
            .Concat(unknown)
            .ToList();
        List<AdaptiveLayerSummary> summaries = state.LayerSummaries.ToList();
        if (evidence.LayerSummary is not null)
        {
            summaries.Add(evidence.LayerSummary);
        }
        return state with
        {
            UnknownEvidence = accumulatedUnknown,
            LayerSummaries = summaries
        };
    }

    private static AdaptiveSingletonTransition Terminate(
        AdaptiveSingletonCoordinatorState state,
        AdaptiveSingletonStateKind terminalState,
        string reason) => new(
            state with { Kind = terminalState },
            AdaptiveSingletonCommandKind.SaveTerminalCheckpoint,
            reason);

    private static bool IsTerminal(AdaptiveSingletonStateKind kind) => kind is
        AdaptiveSingletonStateKind.MicroZero or
        AdaptiveSingletonStateKind.PolicyStagnationConfirmed or
        AdaptiveSingletonStateKind.UnresolvedUnknown or
        AdaptiveSingletonStateKind.SafetyCapReached or
        AdaptiveSingletonStateKind.RuntimeBudgetReached or
        AdaptiveSingletonStateKind.QualityBudgetExhausted or
        AdaptiveSingletonStateKind.SolverFailure or
        AdaptiveSingletonStateKind.UserCancelled;

    private static string ResolveTerminationReason(AdaptiveSingletonStateKind kind) => kind switch
    {
        AdaptiveSingletonStateKind.MicroZero => AdaptiveSingletonTerminationReasons.MicroZero,
        AdaptiveSingletonStateKind.PolicyStagnationConfirmed =>
            AdaptiveSingletonTerminationReasons.PolicyStagnation,
        AdaptiveSingletonStateKind.UnresolvedUnknown =>
            AdaptiveSingletonTerminationReasons.UnresolvedUnknown,
        AdaptiveSingletonStateKind.SafetyCapReached => AdaptiveSingletonTerminationReasons.SafetyCap,
        AdaptiveSingletonStateKind.RuntimeBudgetReached =>
            AdaptiveSingletonTerminationReasons.RuntimeBudget,
        AdaptiveSingletonStateKind.QualityBudgetExhausted =>
            AdaptiveSingletonTerminationReasons.QualityBudget,
        AdaptiveSingletonStateKind.SolverFailure => AdaptiveSingletonTerminationReasons.SolverFailure,
        AdaptiveSingletonStateKind.UserCancelled => AdaptiveSingletonTerminationReasons.UserCancelled,
        _ => string.Empty
    };
}
