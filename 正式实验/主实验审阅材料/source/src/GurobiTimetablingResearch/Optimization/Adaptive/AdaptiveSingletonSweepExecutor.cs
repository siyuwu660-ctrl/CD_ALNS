// 分层扫描执行器实现直接候选、IIS 引导候选及扩展候选的具体试验流程，并把每次求解归一为统一证据对象。
// 各层使用候选描述、执行预算和接受态副本控制搜索范围，选择策略仅返回当前层的最佳可提交胜者。
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

internal sealed record AcceptedStateDescriptor(
    string SnapshotFingerprint,
    string ConflictFingerprint,
    MasterSolutionSnapshot Snapshot,
    int RootConflictCount);

internal sealed record AdaptiveCandidateLayerDescriptor(
    string Layer,
    IReadOnlyList<AdaptiveCandidateDescriptor> Candidates);

internal sealed record AdaptiveExecutionBudget(
    double CandidateTimeLimitSeconds,
    double? RemainingInvocationRuntimeSeconds);

internal sealed record AdaptiveWinnerDescriptor(
    string CandidateId,
    string Layer,
    StructuralSingletonGlobalTrialScore Score,
    AdaptiveSingletonQualityMetrics Quality,
    string ExecutionToken);

internal sealed record AdaptiveLayerExecutionResult(
    string Layer,
    IReadOnlyList<AdaptiveLayerCandidateResult> CandidateResults,
    AdaptiveWinnerDescriptor? Winner,
    IReadOnlyList<AdaptiveUnknownEvidence> UnknownEvidence,
    bool Completed);

internal sealed record InitialReferenceDescriptor(
    MasterSolutionSnapshot Snapshot,
    string Fingerprint);

internal sealed record PreparedAcceptedTransition(
    AdaptiveWinnerDescriptor Winner,
    string InputAcceptedFingerprint,
    string OutputAcceptedFingerprint,
    string OutputConflictFingerprint,
    MasterSolutionSnapshot OutputSnapshot,
    int OutputRootConflictCount,
    string ExecutionToken);

internal sealed record AdaptiveCheckpointWriteRequest(
    string Path,
    string TerminationReason,
    int AcceptedTransitionIndex);

internal sealed record AcceptedTransitionCommitResult(
    AcceptedStateDescriptor AcceptedState,
    string CandidateId,
    bool CheckpointSaved);

internal interface IAdaptiveSingletonSweepExecutor
{
    AdaptiveCandidatePoolDescriptor BuildCandidatePools(
        AcceptedStateDescriptor acceptedState,
        AdaptiveCandidateSelectionConfiguration configuration);

    AdaptiveLayerExecutionResult EvaluateDirectLayer(
        AcceptedStateDescriptor acceptedState,
        AdaptiveCandidateLayerDescriptor layer,
        AdaptiveExecutionBudget budget);

    AdaptiveLayerExecutionResult EvaluateIisLayer(
        AcceptedStateDescriptor acceptedState,
        AdaptiveCandidateLayerDescriptor layer,
        AdaptiveExecutionBudget budget);

    PreparedAcceptedTransition PrepareTransition(
        AcceptedStateDescriptor acceptedState,
        AdaptiveWinnerDescriptor winner,
        InitialReferenceDescriptor initialReference);

    AcceptedTransitionCommitResult CommitPreparedTransition(
        PreparedAcceptedTransition transition,
        AdaptiveCheckpointWriteRequest checkpoint);
}

internal sealed record AdaptiveLayerCandidateResult(
    string CandidateId,
    StructuralCandidateProbeOutcome ProbeOutcome,
    bool Completed,
    bool HasValidatedIncumbent,
    bool StrictImprovement,
    bool QualityBudgetEligible,
    StructuralSingletonGlobalTrialScore? Score);

internal static class AdaptiveDirectLayerSelectionPolicy
{
    internal static AdaptiveLayerCandidateResult? SelectBest(
        IReadOnlyList<AdaptiveLayerCandidateResult> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Any(item => !item.Completed))
        {
            throw new InvalidOperationException(
                "A direct singleton layer cannot be classified before every candidate completes.");
        }
        return candidates
            .Where(item => item.ProbeOutcome == StructuralCandidateProbeOutcome.Feasible &&
                item.HasValidatedIncumbent && item.StrictImprovement &&
                item.QualityBudgetEligible && item.Score is not null)
            .OrderBy(item => item.Score!, Comparer<StructuralSingletonGlobalTrialScore>.Create(
                StructuralSingletonGlobalTrialSelectionPolicy.Compare))
            .FirstOrDefault();
    }
}

internal sealed record AdaptiveInitialDirectAcceptedState(
    string SnapshotFingerprint,
    MasterSolutionSnapshot Snapshot,
    IReadOnlyList<SectionConflictEdgeRequest> SectionEdges,
    IReadOnlyList<StationEventConflictCandidate> StationEdges,
    IReadOnlyList<string> AcceptedCandidateIds,
    IReadOnlyList<PromotedMicroComponent> PromotedComponents);

internal sealed record AdaptiveInitialDirectExecutionRequest(
    AdaptiveInitialDirectAcceptedState AcceptedState,
    InitialReferenceDescriptor InitialReference,
    AdaptiveCandidatePoolDescriptor CandidatePools,
    IReadOnlyList<StructuralCandidateProbeDiagnostic> ProbeDiagnostics,
    AdaptiveQualityBudgetOptions QualityBudgets);

internal sealed record AdaptiveInitialDirectTrialRequest(
    int CandidateIndex,
    string ProbeId,
    HeadwayConflictCandidate Candidate,
    AdaptiveInitialDirectAcceptedState AcceptedState);

internal sealed record AdaptiveInitialDirectCandidateEvidence(
    string CandidateId,
    int CandidateIndex,
    string ProbeId,
    StructuralCandidateProbeOutcome ProbeOutcome,
    string InputAcceptedStateFingerprint,
    string MasterStatus,
    int SolutionCount,
    bool MasterValidatorFeasible,
    int MasterValidatorViolationCount,
    bool GlobalMicroExecuted,
    bool StrictImprovement,
    bool QualityBudgetEligible,
    AdaptiveSingletonQualityMetrics? Quality,
    StructuralSingletonGlobalTrialScore? Score,
    IReadOnlyList<string> QualityRejectionReasons,
    int? SelectionRank,
    bool Selected,
    string Outcome);

internal sealed class AdaptiveInitialDirectTrial<TLease> : IDisposable
    where TLease : class, IDisposable
{
    private bool _disposed;

    internal AdaptiveInitialDirectTrial(
        StructuralSingletonGlobalTrialDiagnostic diagnostic,
        MasterSolutionSnapshot? snapshot,
        TLease lease)
    {
        Diagnostic = diagnostic ?? throw new ArgumentNullException(nameof(diagnostic));
        Snapshot = snapshot;
        Lease = lease ?? throw new ArgumentNullException(nameof(lease));
    }

    internal StructuralSingletonGlobalTrialDiagnostic Diagnostic { get; }
    internal MasterSolutionSnapshot? Snapshot { get; }
    internal TLease Lease { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        Lease.Dispose();
    }
}

internal sealed class AdaptiveInitialDirectExecutionResult<TLease> : IDisposable
    where TLease : class, IDisposable
{
    private readonly TransferableDisposableLease<AdaptiveInitialDirectTrial<TLease>>? _winnerLease;

    internal AdaptiveInitialDirectExecutionResult(
        IReadOnlyList<AdaptiveInitialDirectCandidateEvidence> evidence,
        AdaptiveInitialDirectTrial<TLease>? winner,
        string terminationReason)
    {
        Evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
        _winnerLease = winner is null
            ? null
            : new TransferableDisposableLease<AdaptiveInitialDirectTrial<TLease>>(winner);
        TerminationReason = terminationReason;
    }

    internal IReadOnlyList<AdaptiveInitialDirectCandidateEvidence> Evidence { get; }
    internal AdaptiveInitialDirectCandidateEvidence? WinnerEvidence =>
        Evidence.SingleOrDefault(item => item.Selected);
    internal bool HasWinner => _winnerLease?.HasValue == true;
    internal string TerminationReason { get; }

    internal AdaptiveInitialDirectLayerCompletion CreateInitialIisHandoff(
        AdaptiveInitialDirectExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new AdaptiveInitialDirectLayerCompletion(
            Completed: true,
            HasWinner: WinnerEvidence is not null,
            AcceptedStateFingerprint: request.AcceptedState.SnapshotFingerprint,
            InitialPoolCanonicalCandidateIds: request.CandidatePools.Initial
                .Select(item => item.CanonicalCandidateId)
                .ToList(),
            DirectEvaluatedCandidateIds: Evidence.Select(item => item.CandidateId).ToList(),
            UnknownEvidence: Evidence
                .Where(item => string.Equals(
                    item.Outcome,
                    "no_master_incumbent",
                    StringComparison.OrdinalIgnoreCase))
                .Select(item => new AdaptiveUnknownEvidence(
                    item.CandidateId,
                    "initial_direct",
                    "direct_global_rebuild",
                    item.MasterStatus,
                    null,
                    true))
                .ToList(),
            CandidateUniverseFingerprint: request.CandidatePools.CandidateUniverseFingerprint,
            SelectionConfigurationFingerprint: request.CandidatePools.SelectionConfigurationFingerprint,
            TerminationReason: TerminationReason);
    }

    internal AdaptiveInitialDirectTrial<TLease> TransferWinner() =>
        _winnerLease?.Transfer() ?? throw new InvalidOperationException(
            "The Initial Direct result has no transferable winner or it was already transferred.");

    public void Dispose() => _winnerLease?.Dispose();
}

internal static class AdaptiveInitialDirectExecutor
{
    internal static AdaptiveInitialDirectExecutionResult<TLease> Run<TLease>(
        AdaptiveInitialDirectExecutionRequest request,
        Func<AdaptiveInitialDirectTrialRequest, AdaptiveInitialDirectTrial<TLease>> execute)
        where TLease : class, IDisposable
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(execute);
        ValidateRequest(request);

        IReadOnlyList<AdaptiveInitialDirectTrialRequest> eligible = SelectEligible(request);
        List<AdaptiveInitialDirectCandidateEvidence> evidence = [];
        AdaptiveInitialDirectTrial<TLease>? bestTrial = null;
        StructuralSingletonGlobalTrialScore? bestScore = null;
        string? bestCandidateId = null;
        try
        {
            foreach (AdaptiveInitialDirectTrialRequest candidateRequest in eligible)
            {
                AdaptiveInitialDirectTrial<TLease>? trial = null;
                try
                {
                    trial = execute(candidateRequest) ?? throw new InvalidOperationException(
                        "The Initial Direct trial runner returned no execution lease.");
                    ValidateTrial(candidateRequest, trial);
                    AdaptiveInitialDirectCandidateEvidence candidateEvidence =
                        BuildEvidence(request, candidateRequest, trial);
                    evidence.Add(candidateEvidence);

                    if (candidateEvidence.StrictImprovement &&
                        candidateEvidence.QualityBudgetEligible &&
                        candidateEvidence.Score is not null)
                    {
                        if (bestScore is null || StructuralSingletonGlobalTrialSelectionPolicy.Compare(
                                candidateEvidence.Score, bestScore) < 0)
                        {
                            bestTrial?.Dispose();
                            bestTrial = trial;
                            bestScore = candidateEvidence.Score;
                            bestCandidateId = candidateEvidence.CandidateId;
                            trial = null;
                        }
                    }
                }
                finally
                {
                    trial?.Dispose();
                }
            }

            IReadOnlyList<AdaptiveInitialDirectCandidateEvidence> ranked =
                ApplyFinalRanking(evidence, bestCandidateId);
            string terminationReason = ResolveTerminationReason(ranked, request.QualityBudgets);
            AdaptiveInitialDirectExecutionResult<TLease> result = new(
                ranked,
                bestTrial,
                terminationReason);
            bestTrial = null;
            return result;
        }
        catch
        {
            bestTrial?.Dispose();
            throw;
        }
    }

    private static void ValidateRequest(AdaptiveInitialDirectExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.AcceptedState);
        ArgumentNullException.ThrowIfNull(request.AcceptedState.Snapshot);
        ArgumentNullException.ThrowIfNull(request.AcceptedState.SectionEdges);
        ArgumentNullException.ThrowIfNull(request.AcceptedState.StationEdges);
        ArgumentNullException.ThrowIfNull(request.AcceptedState.AcceptedCandidateIds);
        ArgumentNullException.ThrowIfNull(request.AcceptedState.PromotedComponents);
        ArgumentNullException.ThrowIfNull(request.InitialReference);
        ArgumentNullException.ThrowIfNull(request.CandidatePools);
        ArgumentNullException.ThrowIfNull(request.ProbeDiagnostics);
        ArgumentNullException.ThrowIfNull(request.QualityBudgets);
        if (!string.Equals(
                request.AcceptedState.SnapshotFingerprint,
                SnapshotFingerprint.Calculate(request.AcceptedState.Snapshot),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Initial Direct accepted-state fingerprint does not match its snapshot.");
        }
        if (!string.Equals(
                request.InitialReference.Fingerprint,
                SnapshotFingerprint.Calculate(request.InitialReference.Snapshot),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Initial Direct immutable-reference fingerprint does not match its snapshot.");
        }
    }

    private static IReadOnlyList<AdaptiveInitialDirectTrialRequest> SelectEligible(
        AdaptiveInitialDirectExecutionRequest request)
    {
        HashSet<string> acceptedIds = request.AcceptedState.AcceptedCandidateIds
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> seenCanonical = new(StringComparer.OrdinalIgnoreCase);
        List<AdaptiveInitialDirectTrialRequest> selected = [];
        for (int index = 0; index < request.CandidatePools.Initial.Count; index++)
        {
            AdaptiveCandidateDescriptor descriptor = request.CandidatePools.Initial[index];
            if (!seenCanonical.Add(descriptor.CanonicalCandidateId) ||
                acceptedIds.Contains(descriptor.CandidateId) ||
                acceptedIds.Contains(descriptor.CanonicalCandidateId))
            {
                continue;
            }

            List<StructuralCandidateProbeDiagnostic> matches = request.ProbeDiagnostics
                .Where(item => item.Request.Mode == StructuralCandidateProbeMode.Individual &&
                    item.Execution.Outcome == StructuralCandidateProbeOutcome.Feasible &&
                    item.Request.Candidates.Count == 1 &&
                    string.Equals(
                        item.Request.Candidates[0].CandidateKey,
                        descriptor.CandidateId,
                        StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count > 1)
            {
                throw new InvalidOperationException(
                    $"Initial Direct candidate '{descriptor.CandidateId}' has multiple Individual + Feasible probes.");
            }
            if (matches.Count == 0)
            {
                continue;
            }
            StructuralCandidateProbeDiagnostic probe = matches[0];
            selected.Add(new AdaptiveInitialDirectTrialRequest(
                index,
                probe.Request.ProbeId,
                probe.Request.Candidates[0],
                request.AcceptedState));
        }
        return selected;
    }

    private static void ValidateTrial<TLease>(
        AdaptiveInitialDirectTrialRequest request,
        AdaptiveInitialDirectTrial<TLease> trial)
        where TLease : class, IDisposable
    {
        StructuralSingletonGlobalTrialDiagnostic diagnostic = trial.Diagnostic;
        if (!string.Equals(
                diagnostic.CandidateId,
                request.Candidate.CandidateKey,
                StringComparison.OrdinalIgnoreCase) ||
            diagnostic.CandidateIndex != request.CandidateIndex ||
            diagnostic.ProbeOutcome != StructuralCandidateProbeOutcome.Feasible)
        {
            throw new InvalidDataException(
                "Initial Direct trial diagnostics do not match the requested feasible singleton.");
        }
        if (!string.Equals(
                diagnostic.InputSnapshotFingerprint,
                request.AcceptedState.SnapshotFingerprint,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Initial Direct trial did not rebuild from the unchanged accepted-state fingerprint.");
        }
    }

    private static AdaptiveInitialDirectCandidateEvidence BuildEvidence<TLease>(
        AdaptiveInitialDirectExecutionRequest request,
        AdaptiveInitialDirectTrialRequest candidateRequest,
        AdaptiveInitialDirectTrial<TLease> trial)
        where TLease : class, IDisposable
    {
        StructuralSingletonGlobalTrialDiagnostic diagnostic = trial.Diagnostic;
        bool validatorClean = diagnostic.SolutionCount > 0 &&
            diagnostic.MasterValidatorFeasible &&
            diagnostic.MasterValidatorViolationCount == 0;
        bool globalMicroExecuted = diagnostic.Score is not null;
        if (globalMicroExecuted && (!validatorClean || trial.Snapshot is null))
        {
            throw new InvalidDataException(
                "Initial Direct Global Micro evidence requires a Validator-clean incumbent snapshot.");
        }

        AdaptiveSingletonQualityMetrics? quality = globalMicroExecuted
            ? AdaptiveSingletonQualityCalculator.Calculate(
                request.AcceptedState.Snapshot,
                trial.Snapshot!,
                request.InitialReference.Snapshot)
            : null;
        AdaptiveQualityBudgetEvaluation? budgetEvaluation = quality is null
            ? null
            : AdaptiveQualityBudgetPolicy.Evaluate(quality, request.QualityBudgets);
        bool strictImprovement = globalMicroExecuted && diagnostic.StrictImprovement;
        bool budgetEligible = budgetEvaluation?.Eligible == true;
        string outcome = diagnostic.SolutionCount == 0
            ? "no_master_incumbent"
            : !validatorClean
                ? "master_validator_rejected"
                : !globalMicroExecuted
                    ? "global_micro_not_executed"
                    : !strictImprovement
                        ? "no_strict_micro_improvement"
                        : !budgetEligible
                            ? "quality_budget_rejected"
                            : "eligible_strict_improvement";
        return new AdaptiveInitialDirectCandidateEvidence(
            diagnostic.CandidateId,
            candidateRequest.CandidateIndex,
            candidateRequest.ProbeId,
            StructuralCandidateProbeOutcome.Feasible,
            diagnostic.InputSnapshotFingerprint,
            diagnostic.MasterStatus,
            diagnostic.SolutionCount,
            diagnostic.MasterValidatorFeasible,
            diagnostic.MasterValidatorViolationCount,
            globalMicroExecuted,
            strictImprovement,
            budgetEligible,
            quality,
            diagnostic.Score,
            budgetEvaluation?.RejectionReasons ?? Array.Empty<string>(),
            null,
            false,
            outcome);
    }

    private static IReadOnlyList<AdaptiveInitialDirectCandidateEvidence> ApplyFinalRanking(
        IReadOnlyList<AdaptiveInitialDirectCandidateEvidence> evidence,
        string? winnerCandidateId)
    {
        Dictionary<string, int> ranks = evidence
            .Where(item => item.StrictImprovement && item.QualityBudgetEligible && item.Score is not null)
            .OrderBy(item => item.Score!, Comparer<StructuralSingletonGlobalTrialScore>.Create(
                StructuralSingletonGlobalTrialSelectionPolicy.Compare))
            .Select((item, index) => (item.CandidateId, Rank: index + 1))
            .ToDictionary(item => item.CandidateId, item => item.Rank, StringComparer.OrdinalIgnoreCase);
        return evidence.Select(item =>
        {
            bool selected = winnerCandidateId is not null && string.Equals(
                item.CandidateId,
                winnerCandidateId,
                StringComparison.OrdinalIgnoreCase);
            int? rank = ranks.TryGetValue(item.CandidateId, out int value) ? value : null;
            string outcome = item.Outcome == "eligible_strict_improvement"
                ? selected
                    ? "selected_strict_improvement"
                    : "dominated_by_better_feasible_singleton"
                : item.Outcome;
            return item with { SelectionRank = rank, Selected = selected, Outcome = outcome };
        }).ToList();
    }

    private static string ResolveTerminationReason(
        IReadOnlyList<AdaptiveInitialDirectCandidateEvidence> evidence,
        AdaptiveQualityBudgetOptions budgets)
    {
        if (evidence.Any(item => item.Selected))
        {
            return "initial_direct_winner_ready";
        }
        List<AdaptiveQualityCandidate> qualityCandidates = evidence
            .Where(item => item.Score is not null && item.Quality is not null)
            .Select(item => new AdaptiveQualityCandidate(
                item.CandidateId,
                item.Score!,
                item.Quality!,
                item.StrictImprovement))
            .ToList();
        AdaptiveNoWinnerQualityClassification classification =
            AdaptiveQualityEligibleSelector.ClassifyNoWinner(qualityCandidates, budgets);
        return classification.QualityBudgetBlocksAllImprovingCandidates
            ? AdaptiveSingletonTerminationReasons.QualityBudget
            : "initial_direct_no_strict_improvement";
    }
}

internal sealed record AdaptiveInitialDirectLayerCompletion(
    bool Completed,
    bool HasWinner,
    string AcceptedStateFingerprint,
    IReadOnlyList<string> InitialPoolCanonicalCandidateIds,
    IReadOnlyList<string> DirectEvaluatedCandidateIds,
    IReadOnlyList<AdaptiveUnknownEvidence> UnknownEvidence,
    string CandidateUniverseFingerprint = "",
    string SelectionConfigurationFingerprint = "",
    string TerminationReason = "");

internal sealed record AdaptiveInitialIisExecutionRequest(
    AdaptiveInitialDirectAcceptedState AcceptedState,
    InitialReferenceDescriptor InitialReference,
    AdaptiveCandidatePoolDescriptor CandidatePools,
    IReadOnlyList<StructuralCandidateProbeDiagnostic> ProbeDiagnostics,
    AdaptiveQualityBudgetOptions QualityBudgets,
    AdaptiveInitialDirectLayerCompletion DirectCompletion,
    IisGuidedRepairOptions IisOptions,
    AdaptiveIisLayerSemantics? LayerSemantics = null);

internal sealed record AdaptiveIisLayerSemantics(
    string LayerName,
    string ClosureUnknownSource,
    string SelectedWinnerOutcome,
    string WinnerTerminationReason,
    string UnknownTerminationReason,
    string ProvenExhaustedTerminationReason,
    string NoEligibleWinnerTerminationReason)
{
    internal static AdaptiveIisLayerSemantics Initial { get; } = new(
        "initial_iis",
        "iis_dependency_closure",
        "selected_first_eligible_strict_iis_winner",
        "initial_iis_winner_ready",
        "initial_iis_unresolved_unknown",
        "initial_iis_proven_infeasible_exhausted",
        "initial_iis_no_eligible_winner");

    internal static AdaptiveIisLayerSemantics Expanded { get; } = new(
        "expanded_iis",
        "iis_dependency_closure",
        "selected_first_eligible_strict_expanded_iis_winner",
        "expanded_iis_winner_ready",
        "expanded_iis_unresolved_unknown",
        "expanded_iis_proven_infeasible_exhausted",
        "expanded_iis_no_eligible_winner");
}

internal sealed record AdaptiveInitialIisTrialRequest(
    int CandidateIndex,
    string ProbeId,
    HeadwayConflictCandidate Candidate,
    StructuralCandidateProbeExecution ProbeExecution,
    AdaptiveInitialDirectAcceptedState AcceptedState,
    IisDependencyOverlay InitialOverlay);

internal sealed record AdaptiveInitialIisCandidateEvidence(
    string CandidateId,
    int CandidateIndex,
    string ProbeId,
    string InputAcceptedStateFingerprint,
    StructuralCandidateProbeOutcome InitialProbeOutcome,
    StructuralCandidateProbeOutcome FinalOutcome,
    string SolverStatus,
    int? ExpansionRound,
    string ClosureTerminationReason,
    int SolutionCount,
    bool MasterValidatorFeasible,
    int MasterValidatorViolationCount,
    bool GlobalMicroExecuted,
    bool StrictImprovement,
    bool QualityBudgetEligible,
    AdaptiveSingletonQualityMetrics? Quality,
    StructuralSingletonGlobalTrialScore? Score,
    IReadOnlyList<string> QualityRejectionReasons,
    bool FirstEligibleStrictWinner,
    bool GloballyBestWinnerClaimed,
    string Outcome,
    IReadOnlyList<IisGuidedRepairTraceDiagnostic> ExpansionTrace,
    IReadOnlyList<IisDependencyPlanDiagnostic> ExpansionPlanDiagnostics,
    IReadOnlyList<StructuralTrialIisDiagnostic> IisDiagnostics,
    IReadOnlyList<string> AttemptLocalOverlayDependencyIdentities);

internal sealed class AdaptiveInitialIisCandidateExecution<TLease> : IDisposable
    where TLease : class, IDisposable
{
    private bool _disposed;

    internal AdaptiveInitialIisCandidateExecution(
        StructuralCandidateProbeOutcome outcome,
        string solverStatus,
        int? expansionRound,
        string terminationReason,
        StructuralSingletonGlobalTrialDiagnostic? diagnostic,
        MasterSolutionSnapshot? snapshot,
        IReadOnlyList<IisGuidedRepairTraceDiagnostic> trace,
        IReadOnlyList<IisDependencyPlanDiagnostic> planDiagnostics,
        IReadOnlyList<StructuralTrialIisDiagnostic> iisDiagnostics,
        IReadOnlyList<string> overlayDependencyIdentities,
        TLease? lease)
    {
        Outcome = outcome;
        SolverStatus = solverStatus ?? throw new ArgumentNullException(nameof(solverStatus));
        ExpansionRound = expansionRound;
        TerminationReason = terminationReason ?? throw new ArgumentNullException(nameof(terminationReason));
        Diagnostic = diagnostic;
        Snapshot = snapshot;
        Trace = trace ?? throw new ArgumentNullException(nameof(trace));
        PlanDiagnostics = planDiagnostics ?? throw new ArgumentNullException(nameof(planDiagnostics));
        IisDiagnostics = iisDiagnostics ?? throw new ArgumentNullException(nameof(iisDiagnostics));
        OverlayDependencyIdentities = overlayDependencyIdentities
            ?? throw new ArgumentNullException(nameof(overlayDependencyIdentities));
        Lease = lease;
    }

    internal StructuralCandidateProbeOutcome Outcome { get; }
    internal string SolverStatus { get; }
    internal int? ExpansionRound { get; }
    internal string TerminationReason { get; }
    internal StructuralSingletonGlobalTrialDiagnostic? Diagnostic { get; }
    internal MasterSolutionSnapshot? Snapshot { get; }
    internal IReadOnlyList<IisGuidedRepairTraceDiagnostic> Trace { get; }
    internal IReadOnlyList<IisDependencyPlanDiagnostic> PlanDiagnostics { get; }
    internal IReadOnlyList<StructuralTrialIisDiagnostic> IisDiagnostics { get; }
    internal IReadOnlyList<string> OverlayDependencyIdentities { get; }
    internal TLease? Lease { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        Lease?.Dispose();
    }
}

internal sealed class AdaptiveInitialIisExecutionResult<TLease> : IDisposable
    where TLease : class, IDisposable
{
    private readonly TransferableDisposableLease<AdaptiveInitialIisCandidateExecution<TLease>>? _winnerLease;

    internal AdaptiveInitialIisExecutionResult(
        IReadOnlyList<AdaptiveInitialIisCandidateEvidence> evidence,
        IReadOnlyList<AdaptiveUnknownEvidence> unknownEvidence,
        AdaptiveInitialIisCandidateExecution<TLease>? winner,
        string terminationReason,
        bool layerFullyEvaluated)
    {
        Evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
        UnknownEvidence = unknownEvidence ?? throw new ArgumentNullException(nameof(unknownEvidence));
        _winnerLease = winner is null
            ? null
            : new TransferableDisposableLease<AdaptiveInitialIisCandidateExecution<TLease>>(winner);
        TerminationReason = terminationReason;
        LayerFullyEvaluated = layerFullyEvaluated;
    }

    internal IReadOnlyList<AdaptiveInitialIisCandidateEvidence> Evidence { get; }
    internal IReadOnlyList<AdaptiveUnknownEvidence> UnknownEvidence { get; }
    internal AdaptiveInitialIisCandidateEvidence? WinnerEvidence =>
        Evidence.SingleOrDefault(item => item.FirstEligibleStrictWinner);
    internal bool HasWinner => _winnerLease?.HasValue == true;
    internal string TerminationReason { get; }
    internal bool LayerFullyEvaluated { get; }
    internal bool GloballyBestWinnerClaimed => false;

    internal AdaptiveInitialIisLayerCompletion CreateExpandedDirectHandoff(
        AdaptiveInitialIisExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new AdaptiveInitialIisLayerCompletion(
            Completed: true,
            HasWinner: WinnerEvidence is not null,
            LayerFullyEvaluated: LayerFullyEvaluated,
            AcceptedStateFingerprint: request.AcceptedState.SnapshotFingerprint,
            CandidateUniverseFingerprint: request.CandidatePools.CandidateUniverseFingerprint,
            SelectionConfigurationFingerprint: request.CandidatePools.SelectionConfigurationFingerprint,
            InitialPoolCanonicalCandidateIds: request.CandidatePools.Initial
                .Select(item => item.CanonicalCandidateId)
                .ToList(),
            EvaluatedCandidateIds: Evidence.Select(item => item.CandidateId).ToList(),
            UnknownEvidence: UnknownEvidence,
            TerminationReason: TerminationReason);
    }

    internal AdaptiveInitialIisCandidateExecution<TLease> TransferWinner() =>
        _winnerLease?.Transfer() ?? throw new InvalidOperationException(
            "The Initial IIS result has no transferable winner or it was already transferred.");

    public void Dispose() => _winnerLease?.Dispose();
}

internal static class AdaptiveInitialIisExecutor
{
    internal static AdaptiveInitialIisExecutionResult<TLease> Run<TLease>(
        AdaptiveInitialIisExecutionRequest request,
        Func<AdaptiveInitialIisTrialRequest, AdaptiveInitialIisCandidateExecution<TLease>> execute)
        where TLease : class, IDisposable
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(execute);
        ValidateRequest(request);
        AdaptiveIisLayerSemantics semantics =
            request.LayerSemantics ?? AdaptiveIisLayerSemantics.Initial;

        IReadOnlyList<AdaptiveInitialIisTrialRequest> candidates = SelectEligible(request);
        List<AdaptiveInitialIisCandidateEvidence> evidence = [];
        List<AdaptiveUnknownEvidence> unknownEvidence = request.DirectCompletion.UnknownEvidence.ToList();
        for (int candidateOffset = 0; candidateOffset < candidates.Count; candidateOffset++)
        {
            AdaptiveInitialIisTrialRequest candidate = candidates[candidateOffset];
            AdaptiveInitialIisCandidateExecution<TLease>? execution = null;
            try
            {
                execution = execute(candidate) ?? throw new InvalidOperationException(
                    "The Initial IIS trial runner returned no execution result.");
                ValidateExecution(candidate, execution);
                AdaptiveInitialIisCandidateEvidence candidateEvidence =
                    BuildEvidence(request, candidate, execution);
                if (execution.Outcome == StructuralCandidateProbeOutcome.Unknown)
                {
                    unknownEvidence.Add(new AdaptiveUnknownEvidence(
                        candidate.Candidate.CandidateKey,
                        semantics.LayerName,
                        semantics.ClosureUnknownSource,
                        execution.SolverStatus,
                        execution.ExpansionRound,
                        true));
                }

                bool winner = candidateEvidence.StrictImprovement &&
                    candidateEvidence.QualityBudgetEligible &&
                    candidateEvidence.Score is not null;
                if (!winner)
                {
                    evidence.Add(candidateEvidence);
                    continue;
                }

                AdaptiveInitialIisCandidateEvidence selectedEvidence = candidateEvidence with
                {
                    FirstEligibleStrictWinner = true,
                    Outcome = semantics.SelectedWinnerOutcome
                };
                evidence.Add(selectedEvidence);
                AdaptiveInitialIisExecutionResult<TLease> result = new(
                    evidence,
                    unknownEvidence,
                    execution,
                    semantics.WinnerTerminationReason,
                    candidateOffset == candidates.Count - 1);
                execution = null;
                return result;
            }
            finally
            {
                execution?.Dispose();
            }
        }

        return new AdaptiveInitialIisExecutionResult<TLease>(
            evidence,
            unknownEvidence,
            null,
            ResolveNoWinnerTermination(request, evidence, unknownEvidence, semantics),
            true);
    }

    private static void ValidateRequest(AdaptiveInitialIisExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.AcceptedState);
        ArgumentNullException.ThrowIfNull(request.AcceptedState.Snapshot);
        ArgumentNullException.ThrowIfNull(request.AcceptedState.SectionEdges);
        ArgumentNullException.ThrowIfNull(request.AcceptedState.StationEdges);
        ArgumentNullException.ThrowIfNull(request.AcceptedState.AcceptedCandidateIds);
        ArgumentNullException.ThrowIfNull(request.AcceptedState.PromotedComponents);
        ArgumentNullException.ThrowIfNull(request.InitialReference);
        ArgumentNullException.ThrowIfNull(request.InitialReference.Snapshot);
        ArgumentNullException.ThrowIfNull(request.CandidatePools);
        ArgumentNullException.ThrowIfNull(request.CandidatePools.Initial);
        ArgumentNullException.ThrowIfNull(request.ProbeDiagnostics);
        ArgumentNullException.ThrowIfNull(request.QualityBudgets);
        ArgumentNullException.ThrowIfNull(request.DirectCompletion);
        ArgumentNullException.ThrowIfNull(request.DirectCompletion.InitialPoolCanonicalCandidateIds);
        ArgumentNullException.ThrowIfNull(request.DirectCompletion.DirectEvaluatedCandidateIds);
        ArgumentNullException.ThrowIfNull(request.DirectCompletion.UnknownEvidence);
        ArgumentNullException.ThrowIfNull(request.IisOptions);
        if (!request.DirectCompletion.Completed)
        {
            throw new InvalidOperationException(
                "Initial IIS cannot start before Initial Direct completes.");
        }
        if (request.DirectCompletion.HasWinner)
        {
            throw new InvalidOperationException(
                "Initial IIS cannot start after Initial Direct returns a winner.");
        }
        if (!request.IisOptions.Enabled)
        {
            throw new InvalidOperationException(
                "Initial IIS requires IIS-guided dependency repair to be enabled.");
        }
        string acceptedFingerprint = SnapshotFingerprint.Calculate(request.AcceptedState.Snapshot);
        if (!string.Equals(
                request.AcceptedState.SnapshotFingerprint,
                acceptedFingerprint,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Initial IIS accepted-state fingerprint does not match its snapshot.");
        }
        if (!string.Equals(
                request.DirectCompletion.AcceptedStateFingerprint,
                request.AcceptedState.SnapshotFingerprint,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Initial IIS accepted state changed after Initial Direct completed.");
        }
        if (!string.Equals(
                request.InitialReference.Fingerprint,
                SnapshotFingerprint.Calculate(request.InitialReference.Snapshot),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Initial IIS immutable-reference fingerprint does not match its snapshot.");
        }
        IReadOnlyList<string> currentInitialIds = request.CandidatePools.Initial
            .Select(item => item.CanonicalCandidateId)
            .ToList();
        if (!currentInitialIds.SequenceEqual(
                request.DirectCompletion.InitialPoolCanonicalCandidateIds,
                StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Initial IIS candidate pool differs from the pool evaluated by Initial Direct.");
        }
    }

    private static IReadOnlyList<AdaptiveInitialIisTrialRequest> SelectEligible(
        AdaptiveInitialIisExecutionRequest request)
    {
        HashSet<string> acceptedIds = request.AcceptedState.AcceptedCandidateIds
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> seenCanonical = new(StringComparer.OrdinalIgnoreCase);
        List<(AdaptiveInitialIisTrialRequest Request, double Score, string CanonicalId)> selected = [];
        for (int index = 0; index < request.CandidatePools.Initial.Count; index++)
        {
            AdaptiveCandidateDescriptor descriptor = request.CandidatePools.Initial[index];
            if (!seenCanonical.Add(descriptor.CanonicalCandidateId) ||
                acceptedIds.Contains(descriptor.CandidateId) ||
                acceptedIds.Contains(descriptor.CanonicalCandidateId))
            {
                continue;
            }
            List<StructuralCandidateProbeDiagnostic> matches = request.ProbeDiagnostics
                .Where(item => item.Request.Mode == StructuralCandidateProbeMode.Individual &&
                    item.Execution.Outcome == StructuralCandidateProbeOutcome.ProvenInfeasible &&
                    item.Request.Candidates.Count == 1 &&
                    string.Equals(
                        item.Request.Candidates[0].CandidateKey,
                        descriptor.CandidateId,
                        StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count > 1)
            {
                throw new InvalidOperationException(
                    $"Initial IIS candidate '{descriptor.CandidateId}' has multiple Individual + ProvenInfeasible probes.");
            }
            if (matches.Count == 0)
            {
                continue;
            }
            StructuralCandidateProbeDiagnostic probe = matches[0];
            selected.Add((
                new AdaptiveInitialIisTrialRequest(
                    index,
                    probe.Request.ProbeId,
                    probe.Request.Candidates[0],
                    probe.Execution,
                    request.AcceptedState,
                    IisDependencyOverlay.Empty),
                descriptor.Score,
                descriptor.CanonicalCandidateId));
        }
        return selected
            .OrderBy(item => item.Request.CandidateIndex)
            .ThenByDescending(item => item.Score)
            .ThenBy(item => item.CanonicalId, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Request)
            .ToList();
    }

    private static void ValidateExecution<TLease>(
        AdaptiveInitialIisTrialRequest request,
        AdaptiveInitialIisCandidateExecution<TLease> execution)
        where TLease : class, IDisposable
    {
        if (request.ProbeExecution.Outcome != StructuralCandidateProbeOutcome.ProvenInfeasible)
        {
            throw new InvalidOperationException(
                "Only an Individual + ProvenInfeasible singleton may enter Initial IIS.");
        }
        if (execution.Outcome != StructuralCandidateProbeOutcome.Feasible)
        {
            return;
        }
        StructuralSingletonGlobalTrialDiagnostic diagnostic = execution.Diagnostic
            ?? throw new InvalidDataException(
                "A feasible Initial IIS closure must provide Global-trial diagnostics.");
        if (execution.Lease is null || execution.Snapshot is null || diagnostic.SolutionCount <= 0)
        {
            throw new InvalidDataException(
                "A feasible Initial IIS closure must retain its incumbent snapshot and lease.");
        }
        if (!string.Equals(
                diagnostic.CandidateId,
                request.Candidate.CandidateKey,
                StringComparison.OrdinalIgnoreCase) ||
            diagnostic.CandidateIndex != request.CandidateIndex ||
            !string.Equals(
                diagnostic.InputSnapshotFingerprint,
                request.AcceptedState.SnapshotFingerprint,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Initial IIS feasible diagnostics do not match the requested singleton or accepted state.");
        }
    }

    private static AdaptiveInitialIisCandidateEvidence BuildEvidence<TLease>(
        AdaptiveInitialIisExecutionRequest request,
        AdaptiveInitialIisTrialRequest candidate,
        AdaptiveInitialIisCandidateExecution<TLease> execution)
        where TLease : class, IDisposable
    {
        StructuralSingletonGlobalTrialDiagnostic? diagnostic = execution.Diagnostic;
        bool validatorClean = execution.Outcome == StructuralCandidateProbeOutcome.Feasible &&
            diagnostic is not null && diagnostic.SolutionCount > 0 &&
            diagnostic.MasterValidatorFeasible && diagnostic.MasterValidatorViolationCount == 0;
        bool globalMicroExecuted = validatorClean && diagnostic!.Score is not null;
        AdaptiveSingletonQualityMetrics? quality = globalMicroExecuted
            ? AdaptiveSingletonQualityCalculator.Calculate(
                request.AcceptedState.Snapshot,
                execution.Snapshot!,
                request.InitialReference.Snapshot)
            : null;
        AdaptiveQualityBudgetEvaluation? budget = quality is null
            ? null
            : AdaptiveQualityBudgetPolicy.Evaluate(quality, request.QualityBudgets);
        bool strictImprovement = globalMicroExecuted && diagnostic!.StrictImprovement;
        bool qualityEligible = strictImprovement && budget?.Eligible == true;
        string outcome = execution.Outcome switch
        {
            StructuralCandidateProbeOutcome.Unknown => "unresolved_unknown",
            StructuralCandidateProbeOutcome.ProvenInfeasible => "proven_infeasible_exhausted",
            _ when !validatorClean => "master_validator_rejected",
            _ when !globalMicroExecuted => "global_micro_not_executed",
            _ when !strictImprovement => "no_strict_micro_improvement",
            _ when !qualityEligible => "quality_budget_rejected",
            _ => "eligible_strict_improvement"
        };
        return new AdaptiveInitialIisCandidateEvidence(
            candidate.Candidate.CandidateKey,
            candidate.CandidateIndex,
            candidate.ProbeId,
            candidate.AcceptedState.SnapshotFingerprint,
            candidate.ProbeExecution.Outcome,
            execution.Outcome,
            execution.SolverStatus,
            execution.ExpansionRound,
            execution.TerminationReason,
            diagnostic?.SolutionCount ?? 0,
            diagnostic?.MasterValidatorFeasible == true,
            diagnostic?.MasterValidatorViolationCount ?? 0,
            globalMicroExecuted,
            strictImprovement,
            qualityEligible,
            quality,
            diagnostic?.Score,
            budget?.RejectionReasons ?? Array.Empty<string>(),
            false,
            false,
            outcome,
            execution.Trace,
            execution.PlanDiagnostics,
            execution.IisDiagnostics,
            execution.OverlayDependencyIdentities);
    }

    private static string ResolveNoWinnerTermination(
        AdaptiveInitialIisExecutionRequest request,
        IReadOnlyList<AdaptiveInitialIisCandidateEvidence> evidence,
        IReadOnlyList<AdaptiveUnknownEvidence> unknownEvidence,
        AdaptiveIisLayerSemantics semantics)
    {
        if (unknownEvidence.Count > 0)
        {
            return semantics.UnknownTerminationReason;
        }
        List<AdaptiveQualityCandidate> qualityCandidates = evidence
            .Where(item => item.Score is not null && item.Quality is not null)
            .Select(item => new AdaptiveQualityCandidate(
                item.CandidateId,
                item.Score!,
                item.Quality!,
                item.StrictImprovement))
            .ToList();
        AdaptiveNoWinnerQualityClassification qualityClassification =
            AdaptiveQualityEligibleSelector.ClassifyNoWinner(
                qualityCandidates,
                request.QualityBudgets);
        if (qualityClassification.QualityBudgetBlocksAllImprovingCandidates)
        {
            return AdaptiveSingletonTerminationReasons.QualityBudget;
        }
        return evidence.Count > 0 && evidence.All(item =>
                item.FinalOutcome == StructuralCandidateProbeOutcome.ProvenInfeasible)
            ? semantics.ProvenExhaustedTerminationReason
            : semantics.NoEligibleWinnerTerminationReason;
    }
}

internal sealed record AdaptiveInitialIisLayerCompletion(
    bool Completed,
    bool HasWinner,
    bool LayerFullyEvaluated,
    string AcceptedStateFingerprint,
    string CandidateUniverseFingerprint,
    string SelectionConfigurationFingerprint,
    IReadOnlyList<string> InitialPoolCanonicalCandidateIds,
    IReadOnlyList<string> EvaluatedCandidateIds,
    IReadOnlyList<AdaptiveUnknownEvidence> UnknownEvidence,
    string TerminationReason);

internal sealed record AdaptiveExpandedDirectLayerCompletion(
    bool Completed,
    bool HasWinner,
    bool LayerFullyEvaluated,
    string AcceptedStateFingerprint,
    string CandidateUniverseFingerprint,
    string SelectionConfigurationFingerprint,
    IReadOnlyList<string> InitialPoolCanonicalCandidateIds,
    IReadOnlyList<string> ExpandedPoolCanonicalCandidateIds,
    IReadOnlyList<string> ExpandedDeltaCanonicalCandidateIds,
    IReadOnlyList<StructuralCandidateProbeDiagnostic> ProbeDiagnostics,
    IReadOnlyList<string> DirectEvaluatedCandidateIds,
    IReadOnlyList<AdaptiveUnknownEvidence> UnknownEvidence,
    IReadOnlyList<string> IncompleteCandidateIds,
    IReadOnlyList<string> DuplicateSuppressedCandidateIds,
    string TerminationReason);

internal sealed record AdaptiveExpandedDirectExecutionRequest(
    AdaptiveInitialDirectAcceptedState AcceptedState,
    InitialReferenceDescriptor InitialReference,
    AdaptiveCandidatePoolDescriptor CandidatePools,
    IReadOnlyList<HeadwayConflictCandidate> CandidateUniverse,
    AdaptiveCandidateSelectionConfiguration SelectionConfiguration,
    AdaptiveQualityBudgetOptions QualityBudgets,
    AdaptiveInitialDirectLayerCompletion DirectCompletion,
    AdaptiveInitialIisLayerCompletion IisCompletion,
    int AcceptedRootConflictCount);

internal sealed record AdaptiveExpandedCandidatePoolEvidence(
    string CandidateUniverseFingerprint,
    string InitialPoolFingerprint,
    string ExpandedPoolFingerprint,
    string ExpandedDeltaFingerprint,
    string SelectionConfigurationFingerprint,
    IReadOnlyList<string> InitialCandidateIds,
    IReadOnlyList<string> ExpandedCandidateIds,
    IReadOnlyList<string> ExpandedDeltaCandidateIds);

internal static class AdaptiveExpandedDirectProbeSelectionPolicy
{
    internal static IReadOnlyList<StructuralCandidateProbeDiagnostic> SelectFeasibleIndividuals(
        IReadOnlyList<AdaptiveCandidateDescriptor> expandedDelta,
        IReadOnlyList<StructuralCandidateProbeDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(expandedDelta);
        ArgumentNullException.ThrowIfNull(diagnostics);
        Dictionary<string, string> canonicalByCandidateId = expandedDelta
            .GroupBy(item => item.CandidateId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First().CanonicalCandidateId,
                StringComparer.OrdinalIgnoreCase);
        HashSet<string> selectedCanonicalIds = new(StringComparer.OrdinalIgnoreCase);
        List<StructuralCandidateProbeDiagnostic> selected = [];
        foreach (StructuralCandidateProbeDiagnostic diagnostic in diagnostics)
        {
            if (diagnostic.Request.Mode != StructuralCandidateProbeMode.Individual ||
                diagnostic.Execution.Outcome != StructuralCandidateProbeOutcome.Feasible ||
                diagnostic.Request.Candidates.Count != 1)
            {
                continue;
            }
            string candidateId = diagnostic.Request.Candidates[0].CandidateKey;
            if (canonicalByCandidateId.TryGetValue(candidateId, out string? canonicalId) &&
                selectedCanonicalIds.Add(canonicalId))
            {
                selected.Add(diagnostic);
            }
        }
        return selected;
    }
}

internal static class AdaptiveExpandedDirectExecutionPolicy
{
    internal static bool MayComputeIis(StructuralCandidateProbeOutcome outcome) => false;
}

internal sealed class AdaptiveExpandedDirectExecutionResult<TLease> : IDisposable
    where TLease : class, IDisposable
{
    private readonly AdaptiveInitialDirectExecutionResult<TLease> _directResult;

    internal AdaptiveExpandedDirectExecutionResult(
        AdaptiveExpandedCandidatePoolEvidence poolEvidence,
        IReadOnlyList<StructuralCandidateProbeDiagnostic> probeDiagnostics,
        IReadOnlyList<string> duplicateSuppressedCandidateIds,
        IReadOnlyList<AdaptiveUnknownEvidence> unknownEvidence,
        IReadOnlyList<string> incompleteCandidateIds,
        AdaptiveInitialDirectExecutionResult<TLease> directResult,
        string terminationReason)
    {
        PoolEvidence = poolEvidence ?? throw new ArgumentNullException(nameof(poolEvidence));
        ProbeDiagnostics = probeDiagnostics ?? throw new ArgumentNullException(nameof(probeDiagnostics));
        DuplicateSuppressedCandidateIds = duplicateSuppressedCandidateIds
            ?? throw new ArgumentNullException(nameof(duplicateSuppressedCandidateIds));
        UnknownEvidence = unknownEvidence ?? throw new ArgumentNullException(nameof(unknownEvidence));
        IncompleteCandidateIds = incompleteCandidateIds
            ?? throw new ArgumentNullException(nameof(incompleteCandidateIds));
        _directResult = directResult ?? throw new ArgumentNullException(nameof(directResult));
        TerminationReason = terminationReason;
    }

    internal AdaptiveExpandedCandidatePoolEvidence PoolEvidence { get; }
    internal IReadOnlyList<StructuralCandidateProbeDiagnostic> ProbeDiagnostics { get; }
    internal IReadOnlyList<string> DuplicateSuppressedCandidateIds { get; }
    internal IReadOnlyList<AdaptiveUnknownEvidence> UnknownEvidence { get; }
    internal IReadOnlyList<string> IncompleteCandidateIds { get; }
    internal IReadOnlyList<AdaptiveInitialDirectCandidateEvidence> TrialEvidence =>
        _directResult.Evidence;
    internal IReadOnlyList<string> ProbedCandidateIds => ProbeDiagnostics
        .Select(item => item.Request.Candidates.Single().CandidateKey)
        .ToList();
    internal IReadOnlyList<string> EvaluatedCandidateIds => TrialEvidence
        .Select(item => item.CandidateId)
        .ToList();
    internal AdaptiveInitialDirectCandidateEvidence? WinnerEvidence =>
        _directResult.WinnerEvidence;
    internal bool HasWinner => _directResult.HasWinner;
    internal bool Completed => true;
    internal bool HasIncompleteEvidence => IncompleteCandidateIds.Count > 0;
    internal int FeasibleProbeCount => ProbeDiagnostics.Count(item =>
        item.Execution.Outcome == StructuralCandidateProbeOutcome.Feasible);
    internal int ProvenInfeasibleProbeCount => ProbeDiagnostics.Count(item =>
        item.Execution.Outcome == StructuralCandidateProbeOutcome.ProvenInfeasible);
    internal int UnknownProbeCount => ProbeDiagnostics.Count(item =>
        item.Execution.Outcome == StructuralCandidateProbeOutcome.Unknown);
    internal int QualityBlockedCandidateCount => TrialEvidence.Count(item =>
        item.StrictImprovement && !item.QualityBudgetEligible);
    internal string TerminationReason { get; }

    internal AdaptiveExpandedDirectLayerCompletion CreateExpandedIisHandoff(
        AdaptiveExpandedDirectExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new AdaptiveExpandedDirectLayerCompletion(
            Completed: true,
            HasWinner: WinnerEvidence is not null,
            LayerFullyEvaluated: true,
            AcceptedStateFingerprint: request.AcceptedState.SnapshotFingerprint,
            CandidateUniverseFingerprint: PoolEvidence.CandidateUniverseFingerprint,
            SelectionConfigurationFingerprint: PoolEvidence.SelectionConfigurationFingerprint,
            InitialPoolCanonicalCandidateIds: PoolEvidence.InitialCandidateIds,
            ExpandedPoolCanonicalCandidateIds: PoolEvidence.ExpandedCandidateIds,
            ExpandedDeltaCanonicalCandidateIds: PoolEvidence.ExpandedDeltaCandidateIds,
            ProbeDiagnostics: ProbeDiagnostics,
            DirectEvaluatedCandidateIds: EvaluatedCandidateIds,
            UnknownEvidence: UnknownEvidence,
            IncompleteCandidateIds: IncompleteCandidateIds,
            DuplicateSuppressedCandidateIds: DuplicateSuppressedCandidateIds,
            TerminationReason: TerminationReason);
    }

    internal AdaptiveInitialDirectTrial<TLease> TransferWinner() =>
        _directResult.TransferWinner();

    public void Dispose() => _directResult.Dispose();
}

internal static class AdaptiveExpandedDirectExecutor
{
    internal static AdaptiveExpandedDirectExecutionResult<TLease> Run<TLease>(
        AdaptiveExpandedDirectExecutionRequest request,
        Func<StructuralCandidateProbeRequest, StructuralCandidateProbeExecution> probe,
        Func<AdaptiveInitialDirectTrialRequest, AdaptiveInitialDirectTrial<TLease>> execute)
        where TLease : class, IDisposable
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(execute);
        ValidateRequest(request);

        AdaptiveExpandedCandidatePoolEvidence poolEvidence = CreatePoolEvidence(request.CandidatePools);
        HashSet<string> previouslyEvaluated = request.DirectCompletion.DirectEvaluatedCandidateIds
            .Concat(request.IisCompletion.EvaluatedCandidateIds)
            .Select(id => ResolveCanonicalId(id, request.CandidatePools))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> selectedCanonical = new(StringComparer.OrdinalIgnoreCase);
        List<string> duplicateSuppressed = [];
        List<HeadwayConflictCandidate> candidatesToProbe = [];
        foreach (AdaptiveCandidateDescriptor descriptor in request.CandidatePools.ExpandedDelta)
        {
            string canonicalId = descriptor.CanonicalCandidateId;
            if (!selectedCanonical.Add(canonicalId) || previouslyEvaluated.Contains(canonicalId))
            {
                duplicateSuppressed.Add(descriptor.CandidateId);
                continue;
            }
            List<HeadwayConflictCandidate> matches = request.CandidateUniverse
                .Where(item => string.Equals(
                    item.CandidateKey,
                    descriptor.CandidateId,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count != 1)
            {
                throw new InvalidDataException(
                    $"Expanded Direct candidate '{descriptor.CandidateId}' must map to exactly one model candidate.");
            }
            candidatesToProbe.Add(matches[0]);
        }

        IReadOnlyList<StructuralCandidateProbeDiagnostic> probeDiagnostics =
            StructuralCandidateFeasibilityDiagnosticRunner.RunIndividuals(
                candidatesToProbe,
                probe,
                "expanded-individual");
        if (probeDiagnostics.Any(item =>
                item.Execution.Outcome == StructuralCandidateProbeOutcome.ProvenInfeasible &&
                (item.Execution.IisDiagnostics.Count > 0 ||
                 item.Execution.DependencyCatalog is not null)))
        {
            throw new InvalidOperationException(
                "Task 7C Expanded Direct probes must not ComputeIIS or retain an IIS dependency catalog.");
        }
        IReadOnlyList<StructuralCandidateProbeDiagnostic> feasibleDiagnostics =
            AdaptiveExpandedDirectProbeSelectionPolicy.SelectFeasibleIndividuals(
                request.CandidatePools.ExpandedDelta,
                probeDiagnostics);
        AdaptiveCandidatePoolDescriptor directPool = request.CandidatePools with
        {
            Initial = request.CandidatePools.Expanded
        };
        AdaptiveInitialDirectExecutionRequest directRequest = new(
            request.AcceptedState,
            request.InitialReference,
            directPool,
            feasibleDiagnostics,
            request.QualityBudgets);
        AdaptiveInitialDirectExecutionResult<TLease>? directResult = null;
        try
        {
            directResult = AdaptiveInitialDirectExecutor.Run(
                directRequest,
                trialRequest =>
                {
                    AdaptiveInitialDirectTrial<TLease> trial = execute(trialRequest)
                        ?? throw new InvalidOperationException(
                            "The Expanded Direct trial runner returned no execution lease.");
                    if (trial.Diagnostic.RootConflictsBefore != request.AcceptedRootConflictCount)
                    {
                        trial.Dispose();
                        throw new InvalidDataException(
                            "Expanded Direct trial Micro baseline differs from the accepted state.");
                    }
                    return trial;
                });
            List<AdaptiveUnknownEvidence> unknownEvidence = probeDiagnostics
                .Where(item => item.Execution.Outcome == StructuralCandidateProbeOutcome.Unknown)
                .Select(item => new AdaptiveUnknownEvidence(
                    item.Request.Candidates.Single().CandidateKey,
                    "expanded_direct",
                    "individual_probe",
                    item.Execution.SolverStatusName,
                    null,
                    true))
                .Concat(directResult.Evidence
                    .Where(item => string.Equals(
                        item.Outcome,
                        "no_master_incumbent",
                        StringComparison.OrdinalIgnoreCase))
                    .Select(item => new AdaptiveUnknownEvidence(
                        item.CandidateId,
                        "expanded_direct",
                        "direct_global_rebuild",
                        item.MasterStatus,
                        null,
                        true)))
                .ToList();
            IReadOnlyList<string> incompleteIds = unknownEvidence
                .Select(item => item.CandidateId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            string termination = directResult.WinnerEvidence is not null
                ? "expanded_direct_winner_ready"
                : incompleteIds.Count > 0
                    ? "expanded_direct_unresolved_unknown"
                    : directResult.TerminationReason == AdaptiveSingletonTerminationReasons.QualityBudget
                        ? AdaptiveSingletonTerminationReasons.QualityBudget
                        : "expanded_direct_no_strict_improvement";
            AdaptiveExpandedDirectExecutionResult<TLease> result = new(
                poolEvidence,
                probeDiagnostics,
                duplicateSuppressed,
                unknownEvidence,
                incompleteIds,
                directResult,
                termination);
            directResult = null;
            return result;
        }
        finally
        {
            directResult?.Dispose();
        }
    }

    private static void ValidateRequest(AdaptiveExpandedDirectExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.AcceptedState);
        ArgumentNullException.ThrowIfNull(request.AcceptedState.Snapshot);
        ArgumentNullException.ThrowIfNull(request.AcceptedState.SectionEdges);
        ArgumentNullException.ThrowIfNull(request.AcceptedState.StationEdges);
        ArgumentNullException.ThrowIfNull(request.AcceptedState.AcceptedCandidateIds);
        ArgumentNullException.ThrowIfNull(request.AcceptedState.PromotedComponents);
        ArgumentNullException.ThrowIfNull(request.InitialReference);
        ArgumentNullException.ThrowIfNull(request.InitialReference.Snapshot);
        ArgumentNullException.ThrowIfNull(request.CandidatePools);
        ArgumentNullException.ThrowIfNull(request.CandidatePools.Universe);
        ArgumentNullException.ThrowIfNull(request.CandidatePools.Initial);
        ArgumentNullException.ThrowIfNull(request.CandidatePools.Expanded);
        ArgumentNullException.ThrowIfNull(request.CandidatePools.ExpandedDelta);
        ArgumentNullException.ThrowIfNull(request.CandidateUniverse);
        ArgumentNullException.ThrowIfNull(request.SelectionConfiguration);
        ArgumentNullException.ThrowIfNull(request.QualityBudgets);
        ArgumentNullException.ThrowIfNull(request.DirectCompletion);
        ArgumentNullException.ThrowIfNull(request.IisCompletion);
        if (!request.DirectCompletion.Completed || request.DirectCompletion.HasWinner)
        {
            throw new InvalidOperationException(
                "Expanded Direct requires a completed no-winner Initial Direct layer.");
        }
        if (!request.IisCompletion.Completed || !request.IisCompletion.LayerFullyEvaluated ||
            request.IisCompletion.HasWinner)
        {
            throw new InvalidOperationException(
                "Expanded Direct requires a fully evaluated no-winner Initial IIS layer.");
        }
        if (request.DirectCompletion.UnknownEvidence.Count > 0 ||
            request.IisCompletion.UnknownEvidence.Count > 0)
        {
            throw new InvalidOperationException(
                "Expanded Direct cannot start after unresolved earlier-layer evidence.");
        }
        request.SelectionConfiguration.Validate();
        if (request.SelectionConfiguration.ExpandedCap <= request.SelectionConfiguration.InitialCap)
        {
            throw new InvalidOperationException(
                "Expanded Direct requires expanded cap to be greater than initial cap.");
        }
        if (request.AcceptedRootConflictCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request.AcceptedRootConflictCount));
        }
        string snapshotFingerprint = SnapshotFingerprint.Calculate(request.AcceptedState.Snapshot);
        if (!string.Equals(
                snapshotFingerprint,
                request.AcceptedState.SnapshotFingerprint,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                request.DirectCompletion.AcceptedStateFingerprint,
                request.AcceptedState.SnapshotFingerprint,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                request.IisCompletion.AcceptedStateFingerprint,
                request.AcceptedState.SnapshotFingerprint,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Expanded Direct accepted-state fingerprint changed across earlier layers.");
        }
        if (!string.Equals(
                request.InitialReference.Fingerprint,
                SnapshotFingerprint.Calculate(request.InitialReference.Snapshot),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Expanded Direct immutable-reference fingerprint does not match its snapshot.");
        }
        string universeFingerprint = AdaptiveCandidatePoolBuilder.FingerprintUniverse(
            request.CandidatePools.Universe);
        string configurationFingerprint = AdaptiveCandidatePoolBuilder.FingerprintConfiguration(
            request.SelectionConfiguration);
        if (!string.Equals(
                universeFingerprint,
                request.CandidatePools.CandidateUniverseFingerprint,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                configurationFingerprint,
                request.CandidatePools.SelectionConfigurationFingerprint,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                request.DirectCompletion.CandidateUniverseFingerprint,
                universeFingerprint,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                request.IisCompletion.CandidateUniverseFingerprint,
                universeFingerprint,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                request.DirectCompletion.SelectionConfigurationFingerprint,
                configurationFingerprint,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                request.IisCompletion.SelectionConfigurationFingerprint,
                configurationFingerprint,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Expanded Direct candidate universe or search configuration evidence is stale.");
        }

        IReadOnlyList<string> initialIds = CanonicalIds(request.CandidatePools.Initial);
        IReadOnlyList<string> expandedIds = CanonicalIds(request.CandidatePools.Expanded);
        IReadOnlyList<string> deltaIds = CanonicalIds(request.CandidatePools.ExpandedDelta);
        HashSet<string> expandedSet = expandedIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> expectedDelta = expandedSet
            .Where(id => !initialIds.Contains(id, StringComparer.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!initialIds.All(expandedSet.Contains) ||
            !expectedDelta.SetEquals(deltaIds))
        {
            throw new InvalidDataException(
                "Expanded Direct requires initial subset expanded and delta equal to expanded minus initial.");
        }
        if (!initialIds.SequenceEqual(
                request.DirectCompletion.InitialPoolCanonicalCandidateIds,
                StringComparer.OrdinalIgnoreCase) ||
            !initialIds.SequenceEqual(
                request.IisCompletion.InitialPoolCanonicalCandidateIds,
                StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Expanded Direct initial-pool evidence differs from earlier layers.");
        }
    }

    private static AdaptiveExpandedCandidatePoolEvidence CreatePoolEvidence(
        AdaptiveCandidatePoolDescriptor pools) => new(
        pools.CandidateUniverseFingerprint,
        AdaptiveCandidatePoolBuilder.FingerprintCandidateIds(pools.Initial),
        AdaptiveCandidatePoolBuilder.FingerprintCandidateIds(pools.Expanded),
        AdaptiveCandidatePoolBuilder.FingerprintCandidateIds(pools.ExpandedDelta),
        pools.SelectionConfigurationFingerprint,
        CanonicalIds(pools.Initial),
        CanonicalIds(pools.Expanded),
        CanonicalIds(pools.ExpandedDelta));

    private static IReadOnlyList<string> CanonicalIds(
        IReadOnlyList<AdaptiveCandidateDescriptor> candidates)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        return candidates
            .Where(item => seen.Add(item.CanonicalCandidateId))
            .Select(item => item.CanonicalCandidateId)
            .ToList();
    }

    private static string ResolveCanonicalId(
        string candidateId,
        AdaptiveCandidatePoolDescriptor pools)
    {
        AdaptiveCandidateDescriptor? descriptor = pools.Universe
            .Concat(pools.Initial)
            .Concat(pools.Expanded)
            .Concat(pools.ExpandedDelta)
            .FirstOrDefault(item => string.Equals(
                item.CandidateId,
                candidateId,
                StringComparison.OrdinalIgnoreCase));
        return descriptor?.CanonicalCandidateId ?? candidateId;
    }
}

internal sealed record AdaptiveExpandedIisExecutionRequest(
    AdaptiveInitialDirectAcceptedState AcceptedState,
    InitialReferenceDescriptor InitialReference,
    AdaptiveCandidatePoolDescriptor CandidatePools,
    AdaptiveCandidateSelectionConfiguration SelectionConfiguration,
    AdaptiveQualityBudgetOptions QualityBudgets,
    IisGuidedRepairOptions IisOptions,
    AdaptiveInitialDirectLayerCompletion DirectCompletion,
    AdaptiveInitialIisLayerCompletion InitialIisCompletion,
    AdaptiveExpandedDirectLayerCompletion ExpandedDirectCompletion,
    int AcceptedRootConflictCount);

internal static class AdaptiveExpandedIisExecutionPolicy
{
    internal static bool MayComputeIis(StructuralCandidateProbeOutcome outcome) =>
        AdaptiveIisLayerSelectionPolicy.MayComputeIis(outcome);
}

internal sealed class AdaptiveExpandedIisExecutionResult<TLease> : IDisposable
    where TLease : class, IDisposable
{
    private readonly AdaptiveInitialIisExecutionResult<TLease> _iisResult;

    internal AdaptiveExpandedIisExecutionResult(
        string acceptedStateFingerprint,
        AdaptiveExpandedCandidatePoolEvidence poolEvidence,
        IReadOnlyList<string> eligibleCandidateIds,
        IReadOnlyList<string> skippedCandidateIds,
        AdaptiveInitialIisExecutionResult<TLease> iisResult)
    {
        AcceptedStateFingerprint = acceptedStateFingerprint
            ?? throw new ArgumentNullException(nameof(acceptedStateFingerprint));
        PoolEvidence = poolEvidence ?? throw new ArgumentNullException(nameof(poolEvidence));
        EligibleCandidateIds = eligibleCandidateIds
            ?? throw new ArgumentNullException(nameof(eligibleCandidateIds));
        SkippedCandidateIds = skippedCandidateIds
            ?? throw new ArgumentNullException(nameof(skippedCandidateIds));
        _iisResult = iisResult ?? throw new ArgumentNullException(nameof(iisResult));
    }

    internal string AcceptedStateFingerprint { get; }
    internal AdaptiveExpandedCandidatePoolEvidence PoolEvidence { get; }
    internal IReadOnlyList<string> EligibleCandidateIds { get; }
    internal IReadOnlyList<string> SkippedCandidateIds { get; }
    internal IReadOnlyList<AdaptiveInitialIisCandidateEvidence> Evidence => _iisResult.Evidence;
    internal IReadOnlyList<AdaptiveUnknownEvidence> UnknownEvidence => _iisResult.UnknownEvidence;
    internal IReadOnlyList<string> EvaluatedCandidateIds => Evidence
        .Select(item => item.CandidateId)
        .ToList();
    internal IReadOnlyList<string> UnevaluatedEligibleCandidateIds => EligibleCandidateIds
        .Where(id => !EvaluatedCandidateIds.Contains(id, StringComparer.OrdinalIgnoreCase))
        .ToList();
    internal IReadOnlyList<string> IncompleteCandidateIds => UnknownEvidence
        .Where(item => item.Incomplete)
        .Select(item => item.CandidateId)
        .Concat(UnevaluatedEligibleCandidateIds)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
    internal IReadOnlyList<string> QualityBlockedCandidateIds => Evidence
        .Where(item => item.StrictImprovement && !item.QualityBudgetEligible)
        .Select(item => item.CandidateId)
        .ToList();
    internal AdaptiveInitialIisCandidateEvidence? WinnerEvidence => _iisResult.WinnerEvidence;
    internal bool HasWinner => _iisResult.HasWinner;
    internal bool HasIncompleteEvidence => IncompleteCandidateIds.Count > 0;
    internal bool LayerFullyEvaluated => _iisResult.LayerFullyEvaluated;
    internal bool GloballyBestWinnerClaimed => false;
    internal string WinnerSelectionSemantics =>
        "first_eligible_strict_expanded_iis_winner";
    internal string LayerOutcome => _iisResult.TerminationReason;

    internal AdaptiveInitialIisCandidateExecution<TLease> TransferWinner() =>
        _iisResult.TransferWinner();

    public void Dispose() => _iisResult.Dispose();
}

internal static class AdaptiveExpandedIisExecutor
{
    internal static AdaptiveExpandedIisExecutionResult<TLease> Run<TLease>(
        AdaptiveExpandedIisExecutionRequest request,
        Func<AdaptiveInitialIisTrialRequest, AdaptiveInitialIisCandidateExecution<TLease>> execute)
        where TLease : class, IDisposable
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(execute);
        ValidateRequest(request);

        HashSet<string> acceptedCanonicalIds = request.AcceptedState.AcceptedCandidateIds
            .Select(id => ResolveCanonicalId(id, request.CandidatePools))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> priorIisCanonicalIds = request.InitialIisCompletion.EvaluatedCandidateIds
            .Select(id => ResolveCanonicalId(id, request.CandidatePools))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> seenCanonicalIds = new(StringComparer.OrdinalIgnoreCase);
        List<AdaptiveCandidateDescriptor> eligibleDescriptors = [];
        List<string> skippedCandidateIds = [];
        foreach (AdaptiveCandidateDescriptor descriptor in request.CandidatePools.ExpandedDelta)
        {
            string canonicalId = descriptor.CanonicalCandidateId;
            if (!seenCanonicalIds.Add(canonicalId) ||
                acceptedCanonicalIds.Contains(canonicalId) ||
                priorIisCanonicalIds.Contains(canonicalId))
            {
                skippedCandidateIds.Add(descriptor.CandidateId);
                continue;
            }
            List<StructuralCandidateProbeDiagnostic> matches =
                request.ExpandedDirectCompletion.ProbeDiagnostics
                    .Where(item =>
                        item.Request.Mode == StructuralCandidateProbeMode.Individual &&
                        item.Execution.Outcome == StructuralCandidateProbeOutcome.ProvenInfeasible &&
                        item.Request.Candidates.Count == 1 &&
                        string.Equals(
                            item.Request.Candidates[0].CandidateKey,
                            descriptor.CandidateId,
                            StringComparison.OrdinalIgnoreCase))
                    .ToList();
            if (matches.Count > 1)
            {
                throw new InvalidOperationException(
                    $"Expanded IIS candidate '{descriptor.CandidateId}' has multiple Individual + ProvenInfeasible probes.");
            }
            if (matches.Count == 0)
            {
                skippedCandidateIds.Add(descriptor.CandidateId);
                continue;
            }
            eligibleDescriptors.Add(descriptor);
        }

        IReadOnlyList<AdaptiveUnknownEvidence> inheritedUnknownEvidence =
            request.ExpandedDirectCompletion.UnknownEvidence
                .Select(item => new AdaptiveUnknownEvidence(
                    item.CandidateId,
                    "expanded_iis",
                    string.Equals(
                        item.Source,
                        "individual_probe",
                        StringComparison.OrdinalIgnoreCase)
                        ? "expanded_probe"
                        : $"expanded_direct_{item.Source}",
                    item.SolverStatus,
                    item.ExpansionRound,
                    true))
                .ToList();
        AdaptiveCandidatePoolDescriptor eligiblePools = request.CandidatePools with
        {
            Initial = eligibleDescriptors,
            Expanded = eligibleDescriptors,
            ExpandedDelta = eligibleDescriptors
        };
        AdaptiveInitialDirectLayerCompletion eligibleCompletion = new(
            Completed: true,
            HasWinner: false,
            AcceptedStateFingerprint: request.AcceptedState.SnapshotFingerprint,
            InitialPoolCanonicalCandidateIds: eligibleDescriptors
                .Select(item => item.CanonicalCandidateId)
                .ToList(),
            DirectEvaluatedCandidateIds: Array.Empty<string>(),
            UnknownEvidence: inheritedUnknownEvidence,
            CandidateUniverseFingerprint: request.CandidatePools.CandidateUniverseFingerprint,
            SelectionConfigurationFingerprint: request.CandidatePools.SelectionConfigurationFingerprint,
            TerminationReason: request.ExpandedDirectCompletion.TerminationReason);
        AdaptiveInitialIisExecutionRequest commonRequest = new(
            request.AcceptedState,
            request.InitialReference,
            eligiblePools,
            request.ExpandedDirectCompletion.ProbeDiagnostics,
            request.QualityBudgets,
            eligibleCompletion,
            request.IisOptions,
            AdaptiveIisLayerSemantics.Expanded);
        AdaptiveInitialIisExecutionResult<TLease>? innerResult = null;
        try
        {
            innerResult = AdaptiveInitialIisExecutor.Run(
                commonRequest,
                candidateRequest =>
                {
                    AdaptiveInitialIisCandidateExecution<TLease> execution =
                        execute(candidateRequest) ?? throw new InvalidOperationException(
                            "The Expanded IIS trial runner returned no execution result.");
                    if (execution.Diagnostic is not null &&
                        execution.Diagnostic.RootConflictsBefore != request.AcceptedRootConflictCount)
                    {
                        execution.Dispose();
                        throw new InvalidDataException(
                            "Expanded IIS trial Micro baseline differs from the accepted state.");
                    }
                    return execution;
                });
            AdaptiveExpandedIisExecutionResult<TLease> result = new(
                request.AcceptedState.SnapshotFingerprint,
                CreatePoolEvidence(request.CandidatePools),
                eligibleDescriptors.Select(item => item.CandidateId).ToList(),
                skippedCandidateIds,
                innerResult);
            innerResult = null;
            return result;
        }
        finally
        {
            innerResult?.Dispose();
        }
    }

    private static void ValidateRequest(AdaptiveExpandedIisExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.AcceptedState);
        ArgumentNullException.ThrowIfNull(request.AcceptedState.Snapshot);
        ArgumentNullException.ThrowIfNull(request.AcceptedState.SectionEdges);
        ArgumentNullException.ThrowIfNull(request.AcceptedState.StationEdges);
        ArgumentNullException.ThrowIfNull(request.AcceptedState.AcceptedCandidateIds);
        ArgumentNullException.ThrowIfNull(request.AcceptedState.PromotedComponents);
        ArgumentNullException.ThrowIfNull(request.InitialReference);
        ArgumentNullException.ThrowIfNull(request.InitialReference.Snapshot);
        ArgumentNullException.ThrowIfNull(request.CandidatePools);
        ArgumentNullException.ThrowIfNull(request.CandidatePools.Universe);
        ArgumentNullException.ThrowIfNull(request.CandidatePools.Initial);
        ArgumentNullException.ThrowIfNull(request.CandidatePools.Expanded);
        ArgumentNullException.ThrowIfNull(request.CandidatePools.ExpandedDelta);
        ArgumentNullException.ThrowIfNull(request.SelectionConfiguration);
        ArgumentNullException.ThrowIfNull(request.QualityBudgets);
        ArgumentNullException.ThrowIfNull(request.IisOptions);
        ArgumentNullException.ThrowIfNull(request.DirectCompletion);
        ArgumentNullException.ThrowIfNull(request.InitialIisCompletion);
        ArgumentNullException.ThrowIfNull(request.ExpandedDirectCompletion);
        ArgumentNullException.ThrowIfNull(request.ExpandedDirectCompletion.ProbeDiagnostics);
        ArgumentNullException.ThrowIfNull(request.ExpandedDirectCompletion.UnknownEvidence);
        if (!request.DirectCompletion.Completed || request.DirectCompletion.HasWinner)
        {
            throw new InvalidOperationException(
                "Expanded IIS requires a completed no-winner Initial Direct layer.");
        }
        if (!request.InitialIisCompletion.Completed ||
            !request.InitialIisCompletion.LayerFullyEvaluated ||
            request.InitialIisCompletion.HasWinner)
        {
            throw new InvalidOperationException(
                "Expanded IIS requires a fully evaluated no-winner Initial IIS layer.");
        }
        if (!request.ExpandedDirectCompletion.Completed ||
            !request.ExpandedDirectCompletion.LayerFullyEvaluated ||
            request.ExpandedDirectCompletion.HasWinner)
        {
            throw new InvalidOperationException(
                "Expanded IIS requires a fully evaluated no-winner Expanded Direct layer.");
        }
        if (request.DirectCompletion.UnknownEvidence.Count > 0 ||
            request.InitialIisCompletion.UnknownEvidence.Count > 0)
        {
            throw new InvalidOperationException(
                "Expanded IIS cannot start after unresolved Initial-layer evidence.");
        }
        if (!request.IisOptions.Enabled)
        {
            throw new InvalidOperationException(
                "Expanded IIS requires IIS-guided dependency repair to be enabled.");
        }
        if (request.AcceptedRootConflictCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request.AcceptedRootConflictCount));
        }
        if (IsFormalTermination(request.DirectCompletion.TerminationReason) ||
            IsFormalTermination(request.InitialIisCompletion.TerminationReason) ||
            IsFormalTermination(request.ExpandedDirectCompletion.TerminationReason))
        {
            throw new InvalidOperationException(
                "Expanded IIS cannot start after the sweep has formally terminated.");
        }

        string acceptedFingerprint = SnapshotFingerprint.Calculate(request.AcceptedState.Snapshot);
        if (!string.Equals(
                acceptedFingerprint,
                request.AcceptedState.SnapshotFingerprint,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                request.DirectCompletion.AcceptedStateFingerprint,
                acceptedFingerprint,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                request.InitialIisCompletion.AcceptedStateFingerprint,
                acceptedFingerprint,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                request.ExpandedDirectCompletion.AcceptedStateFingerprint,
                acceptedFingerprint,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Expanded IIS accepted-state fingerprint changed across the sweep.");
        }
        if (!string.Equals(
                request.InitialReference.Fingerprint,
                SnapshotFingerprint.Calculate(request.InitialReference.Snapshot),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Expanded IIS immutable-reference fingerprint does not match its snapshot.");
        }

        request.SelectionConfiguration.Validate();
        string universeFingerprint = AdaptiveCandidatePoolBuilder.FingerprintUniverse(
            request.CandidatePools.Universe);
        string configurationFingerprint = AdaptiveCandidatePoolBuilder.FingerprintConfiguration(
            request.SelectionConfiguration);
        if (!string.Equals(
                request.CandidatePools.CandidateUniverseFingerprint,
                universeFingerprint,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                request.CandidatePools.SelectionConfigurationFingerprint,
                configurationFingerprint,
                StringComparison.OrdinalIgnoreCase) ||
            !SameFingerprint(request.DirectCompletion.CandidateUniverseFingerprint, universeFingerprint) ||
            !SameFingerprint(request.InitialIisCompletion.CandidateUniverseFingerprint, universeFingerprint) ||
            !SameFingerprint(request.ExpandedDirectCompletion.CandidateUniverseFingerprint, universeFingerprint) ||
            !SameFingerprint(request.DirectCompletion.SelectionConfigurationFingerprint, configurationFingerprint) ||
            !SameFingerprint(request.InitialIisCompletion.SelectionConfigurationFingerprint, configurationFingerprint) ||
            !SameFingerprint(request.ExpandedDirectCompletion.SelectionConfigurationFingerprint, configurationFingerprint))
        {
            throw new InvalidDataException(
                "Expanded IIS candidate universe or search configuration evidence is stale.");
        }

        IReadOnlyList<string> initialIds = CanonicalIds(request.CandidatePools.Initial);
        IReadOnlyList<string> expandedIds = CanonicalIds(request.CandidatePools.Expanded);
        IReadOnlyList<string> deltaIds = CanonicalIds(request.CandidatePools.ExpandedDelta);
        HashSet<string> expandedSet = expandedIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> expectedDelta = expandedSet
            .Where(id => !initialIds.Contains(id, StringComparer.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!initialIds.All(expandedSet.Contains) || !expectedDelta.SetEquals(deltaIds))
        {
            throw new InvalidDataException(
                "Expanded IIS requires Initial subset Expanded and Delta equal to Expanded minus Initial.");
        }
        if (!SequenceEqual(initialIds, request.DirectCompletion.InitialPoolCanonicalCandidateIds) ||
            !SequenceEqual(initialIds, request.InitialIisCompletion.InitialPoolCanonicalCandidateIds) ||
            !SequenceEqual(initialIds, request.ExpandedDirectCompletion.InitialPoolCanonicalCandidateIds) ||
            !SequenceEqual(expandedIds, request.ExpandedDirectCompletion.ExpandedPoolCanonicalCandidateIds) ||
            !SequenceEqual(deltaIds, request.ExpandedDirectCompletion.ExpandedDeltaCanonicalCandidateIds))
        {
            throw new InvalidDataException(
                "Expanded IIS pool evidence differs from the completed earlier layers.");
        }
    }

    private static bool IsFormalTermination(string reason) =>
        string.Equals(reason, AdaptiveSingletonTerminationReasons.RuntimeBudget,
            StringComparison.OrdinalIgnoreCase) ||
        string.Equals(reason, AdaptiveSingletonTerminationReasons.SolverFailure,
            StringComparison.OrdinalIgnoreCase) ||
        string.Equals(reason, AdaptiveSingletonTerminationReasons.UserCancelled,
            StringComparison.OrdinalIgnoreCase);

    private static bool SameFingerprint(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool SequenceEqual(
        IReadOnlyList<string> left,
        IReadOnlyList<string> right) =>
        left.SequenceEqual(right, StringComparer.OrdinalIgnoreCase);

    private static AdaptiveExpandedCandidatePoolEvidence CreatePoolEvidence(
        AdaptiveCandidatePoolDescriptor pools) => new(
        pools.CandidateUniverseFingerprint,
        AdaptiveCandidatePoolBuilder.FingerprintCandidateIds(pools.Initial),
        AdaptiveCandidatePoolBuilder.FingerprintCandidateIds(pools.Expanded),
        AdaptiveCandidatePoolBuilder.FingerprintCandidateIds(pools.ExpandedDelta),
        pools.SelectionConfigurationFingerprint,
        CanonicalIds(pools.Initial),
        CanonicalIds(pools.Expanded),
        CanonicalIds(pools.ExpandedDelta));

    private static IReadOnlyList<string> CanonicalIds(
        IReadOnlyList<AdaptiveCandidateDescriptor> candidates)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        return candidates
            .Where(item => seen.Add(item.CanonicalCandidateId))
            .Select(item => item.CanonicalCandidateId)
            .ToList();
    }

    private static string ResolveCanonicalId(
        string candidateId,
        AdaptiveCandidatePoolDescriptor pools)
    {
        AdaptiveCandidateDescriptor? descriptor = pools.Universe
            .Concat(pools.Initial)
            .Concat(pools.Expanded)
            .Concat(pools.ExpandedDelta)
            .FirstOrDefault(item => string.Equals(
                item.CandidateId,
                candidateId,
                StringComparison.OrdinalIgnoreCase));
        return descriptor?.CanonicalCandidateId ?? candidateId;
    }
}

internal sealed record AdaptiveIisLayerSelectionResult(
    AdaptiveLayerCandidateResult? Winner,
    int ConsumedCandidateCount,
    bool LayerFullyEvaluated,
    bool GloballyBestWinnerClaimed,
    int UnknownCandidateCount);

internal static class AdaptiveIisLayerSelectionPolicy
{
    internal static bool MayComputeIis(StructuralCandidateProbeOutcome outcome) =>
        outcome == StructuralCandidateProbeOutcome.ProvenInfeasible;

    internal static AdaptiveIisLayerSelectionResult SelectFirstStrictUnderPriority(
        IReadOnlyList<AdaptiveLayerCandidateResult> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        int consumed = 0;
        int unknown = 0;
        foreach (AdaptiveLayerCandidateResult candidate in candidates)
        {
            if (!candidate.Completed)
            {
                return new AdaptiveIisLayerSelectionResult(
                    null, consumed, false, false, unknown + 1);
            }
            consumed++;
            if (candidate.ProbeOutcome == StructuralCandidateProbeOutcome.Unknown)
            {
                unknown++;
                continue;
            }
            if (!MayComputeIis(candidate.ProbeOutcome))
            {
                continue;
            }
            if (candidate.HasValidatedIncumbent && candidate.StrictImprovement &&
                candidate.QualityBudgetEligible)
            {
                return new AdaptiveIisLayerSelectionResult(
                    candidate, consumed, false, false, unknown);
            }
        }
        return new AdaptiveIisLayerSelectionResult(
            null, consumed, true, false, unknown);
    }
}
