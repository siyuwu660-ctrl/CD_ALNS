// 自适应运行引擎以“已接受状态”为事务边界，依次驱动候选层扫描、胜者租约、质量判定和状态提交。
// 引擎通过执行器接口、观察器和时钟隔离实际求解、诊断与预算计时；只有提交守卫通过的快照才替换当前接受态。
using System.Diagnostics;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

internal enum AdaptiveSingletonLayerKind
{
    InitialDirect,
    InitialIis,
    ExpandedDirect,
    ExpandedIis
}

internal static class AdaptiveSingletonLayerNames
{
    internal const string InitialDirect = "initial_direct";
    internal const string InitialIis = "initial_iis";
    internal const string ExpandedDirect = "expanded_direct";
    internal const string ExpandedIis = "expanded_iis";

    internal static string Resolve(AdaptiveSingletonLayerKind layer) => layer switch
    {
        AdaptiveSingletonLayerKind.InitialDirect => InitialDirect,
        AdaptiveSingletonLayerKind.InitialIis => InitialIis,
        AdaptiveSingletonLayerKind.ExpandedDirect => ExpandedDirect,
        AdaptiveSingletonLayerKind.ExpandedIis => ExpandedIis,
        _ => throw new ArgumentOutOfRangeException(nameof(layer))
    };
}

internal sealed record AdaptiveSingletonRunOptions(
    int SafetyAcceptedTransitionCap,
    double? TotalRuntimeBudgetSeconds,
    AdaptiveQualityBudgetOptions QualityBudgets)
{
    internal AdaptiveSingletonRunOptions Validate()
    {
        if (SafetyAcceptedTransitionCap <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(SafetyAcceptedTransitionCap));
        }
        if (TotalRuntimeBudgetSeconds.HasValue &&
            (!double.IsFinite(TotalRuntimeBudgetSeconds.Value) ||
             TotalRuntimeBudgetSeconds.Value <= 0.0))
        {
            throw new ArgumentOutOfRangeException(nameof(TotalRuntimeBudgetSeconds));
        }
        ArgumentNullException.ThrowIfNull(QualityBudgets);
        return this;
    }
}

internal sealed record AdaptiveSingletonSweepContext(
    string SweepEvidenceKey,
    string CandidateUniverseFingerprint,
    string InputAcceptedStateFingerprint,
    string SearchConfigurationFingerprint = "",
    int CandidateUniverseSize = 0,
    int InitialCandidateCount = 0,
    int ExpandedCandidateCount = 0,
    string InitialCandidatePoolFingerprint = "",
    string ExpandedCandidatePoolFingerprint = "");

internal sealed record AdaptiveAcceptedStateSnapshot(
    string AcceptedStateFingerprint,
    string ConflictFingerprint,
    MasterSolutionSnapshot Snapshot,
    MicroFeasibilityReport MicroReport,
    MicroConflictMetrics ConflictMetrics,
    IReadOnlyList<SectionConflictEdgeRequest> SectionEdges,
    IReadOnlyList<StationEventConflictCandidate> StationEdges,
    IReadOnlyList<string> AcceptedCandidateIds,
    IReadOnlyList<PromotedMicroComponent> PromotedComponents,
    IReadOnlyDictionary<string, double> RunningSupplementSecondsByKey,
    int AcceptedTransitionIndex,
    string LastWinnerLayer,
    string LastWinnerCandidateId,
    AdaptiveSingletonQualityMetrics? LastWinnerQuality);

internal sealed class AdaptiveAcceptedState<TModel> : IDisposable
    where TModel : class, IDisposable
{
    private TModel? _model;

    internal AdaptiveAcceptedState(
        TModel model,
        MasterSolutionSnapshot snapshot,
        MicroFeasibilityReport microReport,
        IReadOnlyList<SectionConflictEdgeRequest> sectionEdges,
        IReadOnlyList<StationEventConflictCandidate> stationEdges,
        IReadOnlyList<string> acceptedCandidateIds,
        IReadOnlyList<PromotedMicroComponent> promotedComponents,
        int acceptedTransitionIndex,
        string lastWinnerLayer = "",
        string lastWinnerCandidateId = "",
        AdaptiveSingletonQualityMetrics? lastWinnerQuality = null)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        if (acceptedTransitionIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(acceptedTransitionIndex));
        }
        Snapshot = AdaptiveAcceptedStateCopies.Copy(snapshot);
        MicroReport = AdaptiveAcceptedStateCopies.Copy(microReport);
        SectionEdges = sectionEdges?.ToList() ?? throw new ArgumentNullException(nameof(sectionEdges));
        StationEdges = stationEdges?.ToList() ?? throw new ArgumentNullException(nameof(stationEdges));
        AcceptedCandidateIds = acceptedCandidateIds?.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            ?? throw new ArgumentNullException(nameof(acceptedCandidateIds));
        PromotedComponents = promotedComponents?.ToList()
            ?? throw new ArgumentNullException(nameof(promotedComponents));
        AcceptedTransitionIndex = acceptedTransitionIndex;
        LastWinnerLayer = lastWinnerLayer ?? string.Empty;
        LastWinnerCandidateId = lastWinnerCandidateId ?? string.Empty;
        LastWinnerQuality = lastWinnerQuality;
        AcceptedStateFingerprint = SnapshotFingerprint.Calculate(Snapshot);
        ConflictMetrics = MicroConflictMetricCalculator.Calculate(MicroReport.Conflicts);
        ConflictFingerprint = MicroConflictFingerprint.Calculate(MicroReport.Conflicts);
        RunningSupplementSecondsByKey = new Dictionary<string, double>(
            Snapshot.RunningTimeSupplementSecondsByKey ?? new Dictionary<string, double>(),
            StringComparer.OrdinalIgnoreCase);
        PersistedIisOverlayDependencyIdentities = Array.Empty<string>();
    }

    internal bool HasModel => _model is not null;
    internal TModel Model => _model ?? throw new ObjectDisposedException(nameof(AdaptiveAcceptedState<TModel>));
    internal MasterSolutionSnapshot Snapshot { get; }
    internal MicroFeasibilityReport MicroReport { get; }
    internal MicroConflictMetrics ConflictMetrics { get; }
    internal string AcceptedStateFingerprint { get; }
    internal string ConflictFingerprint { get; }
    internal IReadOnlyList<SectionConflictEdgeRequest> SectionEdges { get; }
    internal IReadOnlyList<StationEventConflictCandidate> StationEdges { get; }
    internal IReadOnlyList<string> AcceptedCandidateIds { get; }
    internal IReadOnlyList<PromotedMicroComponent> PromotedComponents { get; }
    internal IReadOnlyDictionary<string, double> RunningSupplementSecondsByKey { get; }
    internal IReadOnlyList<string> PersistedIisOverlayDependencyIdentities { get; }
    internal int AcceptedTransitionIndex { get; }
    internal string LastWinnerLayer { get; }
    internal string LastWinnerCandidateId { get; }
    internal AdaptiveSingletonQualityMetrics? LastWinnerQuality { get; }

    internal AdaptiveAcceptedStateSnapshot CreateSnapshot() => new(
        AcceptedStateFingerprint,
        ConflictFingerprint,
        AdaptiveAcceptedStateCopies.Copy(Snapshot),
        AdaptiveAcceptedStateCopies.Copy(MicroReport),
        ConflictMetrics,
        SectionEdges.ToList(),
        StationEdges.ToList(),
        AcceptedCandidateIds.ToList(),
        PromotedComponents.ToList(),
        new Dictionary<string, double>(RunningSupplementSecondsByKey, StringComparer.OrdinalIgnoreCase),
        AcceptedTransitionIndex,
        LastWinnerLayer,
        LastWinnerCandidateId,
        LastWinnerQuality);

    public void Dispose()
    {
        _model?.Dispose();
        _model = null;
    }
}

internal sealed class AdaptiveSingletonWinnerLease<TModel> : IDisposable
    where TModel : class, IDisposable
{
    private TModel? _model;

    internal AdaptiveSingletonWinnerLease(TModel model)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
    }

    internal bool IsLive => _model is not null;

    internal TModel Transfer()
    {
        TModel model = _model ?? throw new InvalidOperationException(
            "The Adaptive winner model lease has already been transferred or disposed.");
        _model = null;
        return model;
    }

    public void Dispose()
    {
        _model?.Dispose();
        _model = null;
    }
}

internal sealed class AdaptiveSingletonWinner<TModel> : IDisposable
    where TModel : class, IDisposable
{
    internal AdaptiveSingletonWinner(
        string candidateId,
        string snapshotCandidateId,
        AdaptiveSingletonLayerKind layer,
        string inputAcceptedStateFingerprint,
        MasterSolutionSnapshot snapshot,
        MicroFeasibilityReport microReport,
        bool hasIncumbent,
        int masterValidatorViolationCount,
        SectionConflictEdgeRequest? sectionEdge,
        StationEventConflictCandidate? stationEdge,
        string initialReferenceFingerprint,
        string executionToken,
        string modelExecutionToken,
        string snapshotExecutionToken,
        string microExecutionToken,
        IReadOnlyList<string> persistedIisOverlayDependencyIdentities,
        AdaptiveSingletonQualityMetrics claimedQuality,
        AdaptiveSingletonWinnerLease<TModel> modelLease)
    {
        CandidateId = candidateId ?? throw new ArgumentNullException(nameof(candidateId));
        SnapshotCandidateId = snapshotCandidateId ?? throw new ArgumentNullException(nameof(snapshotCandidateId));
        Layer = layer;
        InputAcceptedStateFingerprint = inputAcceptedStateFingerprint
            ?? throw new ArgumentNullException(nameof(inputAcceptedStateFingerprint));
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        MicroReport = microReport ?? throw new ArgumentNullException(nameof(microReport));
        HasIncumbent = hasIncumbent;
        MasterValidatorViolationCount = masterValidatorViolationCount;
        SectionEdge = sectionEdge;
        StationEdge = stationEdge;
        InitialReferenceFingerprint = initialReferenceFingerprint
            ?? throw new ArgumentNullException(nameof(initialReferenceFingerprint));
        ExecutionToken = executionToken ?? throw new ArgumentNullException(nameof(executionToken));
        ModelExecutionToken = modelExecutionToken ?? throw new ArgumentNullException(nameof(modelExecutionToken));
        SnapshotExecutionToken = snapshotExecutionToken ?? throw new ArgumentNullException(nameof(snapshotExecutionToken));
        MicroExecutionToken = microExecutionToken ?? throw new ArgumentNullException(nameof(microExecutionToken));
        PersistedIisOverlayDependencyIdentities = persistedIisOverlayDependencyIdentities
            ?? throw new ArgumentNullException(nameof(persistedIisOverlayDependencyIdentities));
        ClaimedQuality = claimedQuality ?? throw new ArgumentNullException(nameof(claimedQuality));
        ModelLease = modelLease ?? throw new ArgumentNullException(nameof(modelLease));
    }

    internal string CandidateId { get; }
    internal string SnapshotCandidateId { get; }
    internal AdaptiveSingletonLayerKind Layer { get; }
    internal string InputAcceptedStateFingerprint { get; }
    internal MasterSolutionSnapshot Snapshot { get; }
    internal MicroFeasibilityReport MicroReport { get; }
    internal bool HasIncumbent { get; }
    internal int MasterValidatorViolationCount { get; }
    internal SectionConflictEdgeRequest? SectionEdge { get; }
    internal StationEventConflictCandidate? StationEdge { get; }
    internal string InitialReferenceFingerprint { get; }
    internal string ExecutionToken { get; }
    internal string ModelExecutionToken { get; }
    internal string SnapshotExecutionToken { get; }
    internal string MicroExecutionToken { get; }
    internal IReadOnlyList<string> PersistedIisOverlayDependencyIdentities { get; }
    internal AdaptiveSingletonQualityMetrics ClaimedQuality { get; }
    internal AdaptiveSingletonWinnerLease<TModel> ModelLease { get; }
    internal bool IsLive => ModelLease.IsLive;

    public void Dispose() => ModelLease.Dispose();
}

internal sealed class AdaptiveSingletonLayerExecutionResult<TModel> : IDisposable
    where TModel : class, IDisposable
{
    private readonly TransferableDisposableLease<AdaptiveSingletonWinner<TModel>>? _winnerLease;
    private readonly IReadOnlyList<IDisposable> _losingLeases;
    private bool _disposed;

    internal AdaptiveSingletonLayerExecutionResult(
        AdaptiveSingletonLayerKind layer,
        string inputAcceptedStateFingerprint,
        string sweepEvidenceKey,
        AdaptiveLayerSummary summary,
        IReadOnlyList<AdaptiveUnknownEvidence> unknownEvidence,
        bool completed,
        AdaptiveSingletonWinner<TModel>? winner,
        IReadOnlyList<IDisposable> losingLeases)
    {
        Layer = layer;
        InputAcceptedStateFingerprint = inputAcceptedStateFingerprint
            ?? throw new ArgumentNullException(nameof(inputAcceptedStateFingerprint));
        SweepEvidenceKey = sweepEvidenceKey ?? throw new ArgumentNullException(nameof(sweepEvidenceKey));
        Summary = summary ?? throw new ArgumentNullException(nameof(summary));
        UnknownEvidence = unknownEvidence ?? throw new ArgumentNullException(nameof(unknownEvidence));
        Completed = completed;
        _winnerLease = winner is null
            ? null
            : new TransferableDisposableLease<AdaptiveSingletonWinner<TModel>>(winner);
        _losingLeases = losingLeases ?? throw new ArgumentNullException(nameof(losingLeases));
    }

    internal AdaptiveSingletonLayerKind Layer { get; }
    internal string InputAcceptedStateFingerprint { get; }
    internal string SweepEvidenceKey { get; }
    internal AdaptiveLayerSummary Summary { get; }
    internal IReadOnlyList<AdaptiveUnknownEvidence> UnknownEvidence { get; }
    internal bool Completed { get; }
    internal bool HasWinner => _winnerLease?.HasValue == true;

    internal AdaptiveSingletonWinner<TModel> TransferWinner() =>
        _winnerLease?.Transfer() ?? throw new InvalidOperationException(
            "The Adaptive layer has no live winner to transfer.");

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _winnerLease?.Dispose();
        foreach (IDisposable lease in _losingLeases)
        {
            lease.Dispose();
        }
    }
}

internal interface IAdaptiveSingletonRunExecutor<TModel>
    where TModel : class, IDisposable
{
    AdaptiveSingletonSweepContext PrepareSweep(
        AdaptiveAcceptedStateSnapshot acceptedState,
        InitialReferenceDescriptor initialReference);

    AdaptiveSingletonLayerExecutionResult<TModel> EvaluateInitialDirect(
        AdaptiveAcceptedStateSnapshot acceptedState,
        InitialReferenceDescriptor initialReference,
        AdaptiveSingletonSweepContext sweep);

    AdaptiveSingletonLayerExecutionResult<TModel> EvaluateInitialIis(
        AdaptiveAcceptedStateSnapshot acceptedState,
        InitialReferenceDescriptor initialReference,
        AdaptiveSingletonSweepContext sweep);

    AdaptiveSingletonLayerExecutionResult<TModel> EvaluateExpandedDirect(
        AdaptiveAcceptedStateSnapshot acceptedState,
        InitialReferenceDescriptor initialReference,
        AdaptiveSingletonSweepContext sweep);

    AdaptiveSingletonLayerExecutionResult<TModel> EvaluateExpandedIis(
        AdaptiveAcceptedStateSnapshot acceptedState,
        InitialReferenceDescriptor initialReference,
        AdaptiveSingletonSweepContext sweep);
}

/// <summary>
/// 将四类生产扫描结果适配到统一运行引擎接口，负责转换胜者租约、证据列表和分层摘要。
/// 该适配器由自适应执行流程组装，不作为 Program/Solve 的直接入口。
/// </summary>
internal sealed class AdaptiveSingletonProductionSliceAdapter :
    IAdaptiveSingletonRunExecutor<BasicMilpModel>
{
    private readonly Func<AdaptiveAcceptedStateSnapshot, InitialReferenceDescriptor,
        AdaptiveSingletonSweepContext> _prepareSweep;
    private readonly Func<AdaptiveAcceptedStateSnapshot, InitialReferenceDescriptor,
        AdaptiveSingletonSweepContext,
        AdaptiveInitialDirectExecutionResult<StructuralSingletonGlobalTrialExecution>> _initialDirect;
    private readonly Func<AdaptiveAcceptedStateSnapshot, InitialReferenceDescriptor,
        AdaptiveSingletonSweepContext,
        AdaptiveInitialIisExecutionResult<StructuralSingletonGlobalTrialExecution>> _initialIis;
    private readonly Func<AdaptiveAcceptedStateSnapshot, InitialReferenceDescriptor,
        AdaptiveSingletonSweepContext,
        AdaptiveExpandedDirectExecutionResult<StructuralSingletonGlobalTrialExecution>> _expandedDirect;
    private readonly Func<AdaptiveAcceptedStateSnapshot, InitialReferenceDescriptor,
        AdaptiveSingletonSweepContext,
        AdaptiveExpandedIisExecutionResult<StructuralSingletonGlobalTrialExecution>> _expandedIis;
    private readonly Func<string, HeadwayConflictCandidate> _resolveCandidate;

    internal AdaptiveSingletonProductionSliceAdapter(
        Func<AdaptiveAcceptedStateSnapshot, InitialReferenceDescriptor,
            AdaptiveSingletonSweepContext> prepareSweep,
        Func<AdaptiveAcceptedStateSnapshot, InitialReferenceDescriptor,
            AdaptiveSingletonSweepContext,
            AdaptiveInitialDirectExecutionResult<StructuralSingletonGlobalTrialExecution>> initialDirect,
        Func<AdaptiveAcceptedStateSnapshot, InitialReferenceDescriptor,
            AdaptiveSingletonSweepContext,
            AdaptiveInitialIisExecutionResult<StructuralSingletonGlobalTrialExecution>> initialIis,
        Func<AdaptiveAcceptedStateSnapshot, InitialReferenceDescriptor,
            AdaptiveSingletonSweepContext,
            AdaptiveExpandedDirectExecutionResult<StructuralSingletonGlobalTrialExecution>> expandedDirect,
        Func<AdaptiveAcceptedStateSnapshot, InitialReferenceDescriptor,
            AdaptiveSingletonSweepContext,
            AdaptiveExpandedIisExecutionResult<StructuralSingletonGlobalTrialExecution>> expandedIis,
        Func<string, HeadwayConflictCandidate> resolveCandidate)
    {
        _prepareSweep = prepareSweep ?? throw new ArgumentNullException(nameof(prepareSweep));
        _initialDirect = initialDirect ?? throw new ArgumentNullException(nameof(initialDirect));
        _initialIis = initialIis ?? throw new ArgumentNullException(nameof(initialIis));
        _expandedDirect = expandedDirect ?? throw new ArgumentNullException(nameof(expandedDirect));
        _expandedIis = expandedIis ?? throw new ArgumentNullException(nameof(expandedIis));
        _resolveCandidate = resolveCandidate ?? throw new ArgumentNullException(nameof(resolveCandidate));
    }

    public AdaptiveSingletonSweepContext PrepareSweep(
        AdaptiveAcceptedStateSnapshot acceptedState,
        InitialReferenceDescriptor initialReference) => _prepareSweep(acceptedState, initialReference);

    public AdaptiveSingletonLayerExecutionResult<BasicMilpModel> EvaluateInitialDirect(
        AdaptiveAcceptedStateSnapshot acceptedState,
        InitialReferenceDescriptor initialReference,
        AdaptiveSingletonSweepContext sweep)
    {
        using AdaptiveInitialDirectExecutionResult<StructuralSingletonGlobalTrialExecution> result =
            _initialDirect(acceptedState, initialReference, sweep);
        AdaptiveSingletonWinner<BasicMilpModel>? winner = result.HasWinner
            ? AdaptDirectWinner(result.TransferWinner(), AdaptiveSingletonLayerKind.InitialDirect,
                acceptedState, initialReference)
            : null;
        IReadOnlyList<AdaptiveUnknownEvidence> unknown = result.Evidence
            .Where(item => string.Equals(
                item.Outcome,
                "no_master_incumbent",
                StringComparison.OrdinalIgnoreCase))
            .Select(item => new AdaptiveUnknownEvidence(
                item.CandidateId,
                AdaptiveSingletonLayerNames.InitialDirect,
                "direct_global_rebuild",
                item.MasterStatus,
                null,
                true))
            .ToList();
        return CreateLayerResult(
            AdaptiveSingletonLayerKind.InitialDirect,
            acceptedState,
            sweep,
            result.Evidence.Count,
            result.Evidence.Count(item => item.StrictImprovement),
            result.Evidence.Count(item => item.StrictImprovement && !item.QualityBudgetEligible),
            unknown,
            completed: true,
            winner,
            result.Evidence.Count(item => item.ProbeOutcome == StructuralCandidateProbeOutcome.Feasible),
            result.Evidence.Count(item => item.ProbeOutcome == StructuralCandidateProbeOutcome.ProvenInfeasible),
            result.Evidence.Select(item => item.CandidateId).ToList(),
            [],
            result.Evidence.Where(item => item.StrictImprovement && !item.QualityBudgetEligible)
                .Select(item => item.CandidateId).ToList());
    }

    public AdaptiveSingletonLayerExecutionResult<BasicMilpModel> EvaluateInitialIis(
        AdaptiveAcceptedStateSnapshot acceptedState,
        InitialReferenceDescriptor initialReference,
        AdaptiveSingletonSweepContext sweep)
    {
        using AdaptiveInitialIisExecutionResult<StructuralSingletonGlobalTrialExecution> result =
            _initialIis(acceptedState, initialReference, sweep);
        AdaptiveSingletonWinner<BasicMilpModel>? winner = result.HasWinner
            ? AdaptIisWinner(result.TransferWinner(), AdaptiveSingletonLayerKind.InitialIis,
                acceptedState, initialReference)
            : null;
        return CreateLayerResult(
            AdaptiveSingletonLayerKind.InitialIis,
            acceptedState,
            sweep,
            result.Evidence.Count,
            result.Evidence.Count(item => item.StrictImprovement),
            result.Evidence.Count(item => item.StrictImprovement && !item.QualityBudgetEligible),
            result.UnknownEvidence,
            result.LayerFullyEvaluated,
            winner,
            evaluatedCandidateIds: result.Evidence.Select(item => item.CandidateId).ToList(),
            qualityBlockedCandidateIds: result.Evidence
                .Where(item => item.StrictImprovement && !item.QualityBudgetEligible)
                .Select(item => item.CandidateId).ToList());
    }

    public AdaptiveSingletonLayerExecutionResult<BasicMilpModel> EvaluateExpandedDirect(
        AdaptiveAcceptedStateSnapshot acceptedState,
        InitialReferenceDescriptor initialReference,
        AdaptiveSingletonSweepContext sweep)
    {
        using AdaptiveExpandedDirectExecutionResult<StructuralSingletonGlobalTrialExecution> result =
            _expandedDirect(acceptedState, initialReference, sweep);
        AdaptiveSingletonWinner<BasicMilpModel>? winner = result.HasWinner
            ? AdaptDirectWinner(result.TransferWinner(), AdaptiveSingletonLayerKind.ExpandedDirect,
                acceptedState, initialReference)
            : null;
        return CreateLayerResult(
            AdaptiveSingletonLayerKind.ExpandedDirect,
            acceptedState,
            sweep,
            result.TrialEvidence.Count,
            result.TrialEvidence.Count(item => item.StrictImprovement),
            result.QualityBlockedCandidateCount,
            result.UnknownEvidence,
            result.Completed && !result.HasIncompleteEvidence,
            winner,
            result.FeasibleProbeCount,
            result.ProvenInfeasibleProbeCount,
            result.EvaluatedCandidateIds,
            result.DuplicateSuppressedCandidateIds,
            result.TrialEvidence.Where(item => item.StrictImprovement && !item.QualityBudgetEligible)
                .Select(item => item.CandidateId).ToList());
    }

    public AdaptiveSingletonLayerExecutionResult<BasicMilpModel> EvaluateExpandedIis(
        AdaptiveAcceptedStateSnapshot acceptedState,
        InitialReferenceDescriptor initialReference,
        AdaptiveSingletonSweepContext sweep)
    {
        using AdaptiveExpandedIisExecutionResult<StructuralSingletonGlobalTrialExecution> result =
            _expandedIis(acceptedState, initialReference, sweep);
        AdaptiveSingletonWinner<BasicMilpModel>? winner = result.HasWinner
            ? AdaptIisWinner(result.TransferWinner(), AdaptiveSingletonLayerKind.ExpandedIis,
                acceptedState, initialReference)
            : null;
        return CreateLayerResult(
            AdaptiveSingletonLayerKind.ExpandedIis,
            acceptedState,
            sweep,
            result.Evidence.Count,
            result.Evidence.Count(item => item.StrictImprovement),
            result.QualityBlockedCandidateIds.Count,
            result.UnknownEvidence,
            result.LayerFullyEvaluated && !result.HasIncompleteEvidence,
            winner,
            evaluatedCandidateIds: result.EvaluatedCandidateIds,
            skippedCandidateIds: result.SkippedCandidateIds,
            qualityBlockedCandidateIds: result.QualityBlockedCandidateIds);
    }

    private AdaptiveSingletonWinner<BasicMilpModel> AdaptDirectWinner(
        AdaptiveInitialDirectTrial<StructuralSingletonGlobalTrialExecution> trial,
        AdaptiveSingletonLayerKind layer,
        AdaptiveAcceptedStateSnapshot acceptedState,
        InitialReferenceDescriptor initialReference)
    {
        using (trial)
        {
            return AdaptWinner(
                trial.Lease,
                trial.Diagnostic,
                trial.Snapshot,
                layer,
                acceptedState,
                initialReference);
        }
    }

    private AdaptiveSingletonWinner<BasicMilpModel> AdaptIisWinner(
        AdaptiveInitialIisCandidateExecution<StructuralSingletonGlobalTrialExecution> trial,
        AdaptiveSingletonLayerKind layer,
        AdaptiveAcceptedStateSnapshot acceptedState,
        InitialReferenceDescriptor initialReference)
    {
        using (trial)
        {
            if (trial.Lease is null || trial.Diagnostic is null)
            {
                throw new InvalidDataException(
                    "An Adaptive IIS winner must own its validated global trial lease and diagnostic.");
            }
            return AdaptWinner(
                trial.Lease,
                trial.Diagnostic,
                trial.Snapshot,
                layer,
                acceptedState,
                initialReference);
        }
    }

    private AdaptiveSingletonWinner<BasicMilpModel> AdaptWinner(
        StructuralSingletonGlobalTrialExecution execution,
        StructuralSingletonGlobalTrialDiagnostic diagnostic,
        MasterSolutionSnapshot? snapshot,
        AdaptiveSingletonLayerKind layer,
        AdaptiveAcceptedStateSnapshot acceptedState,
        InitialReferenceDescriptor initialReference)
    {
        if (!execution.HasTransferableModel || snapshot is null || execution.MicroReport is null)
        {
            throw new InvalidDataException(
                "An Adaptive production winner must contain one model, snapshot, and Global Micro report.");
        }
        HeadwayConflictCandidate candidate = _resolveCandidate(diagnostic.CandidateId)
            ?? throw new InvalidDataException(
                $"Adaptive winner candidate '{diagnostic.CandidateId}' is not in the current universe.");
        BasicMilpModel model = execution.TransferModel();
        try
        {
            AdaptiveSingletonWinnerLease<BasicMilpModel> lease = new(model);
            AdaptiveSingletonQualityMetrics quality = AdaptiveSingletonQualityCalculator.Calculate(
                acceptedState.Snapshot,
                snapshot,
                initialReference.Snapshot);
            string token = diagnostic.TrialSnapshotFingerprint;
            AdaptiveSingletonWinner<BasicMilpModel> winner = new(
                diagnostic.CandidateId,
                candidate.CandidateKey,
                layer,
                diagnostic.InputSnapshotFingerprint,
                snapshot,
                execution.MicroReport,
                diagnostic.SolutionCount > 0,
                diagnostic.MasterValidatorViolationCount,
                candidate.SectionEdge,
                candidate.StationEdge,
                initialReference.Fingerprint,
                token,
                token,
                token,
                token,
                Array.Empty<string>(),
                quality,
                lease);
            model = null!;
            return winner;
        }
        catch
        {
            model?.Dispose();
            throw;
        }
    }

    private static AdaptiveSingletonLayerExecutionResult<BasicMilpModel> CreateLayerResult(
        AdaptiveSingletonLayerKind layer,
        AdaptiveAcceptedStateSnapshot acceptedState,
        AdaptiveSingletonSweepContext sweep,
        int evaluatedCount,
        int improvingCount,
        int budgetRejectedCount,
        IReadOnlyList<AdaptiveUnknownEvidence> unknown,
        bool completed,
        AdaptiveSingletonWinner<BasicMilpModel>? winner,
        int feasibleProbeCount = 0,
        int provenInfeasibleProbeCount = 0,
        IReadOnlyList<string>? evaluatedCandidateIds = null,
        IReadOnlyList<string>? skippedCandidateIds = null,
        IReadOnlyList<string>? qualityBlockedCandidateIds = null) => new(
            layer,
            acceptedState.AcceptedStateFingerprint,
            sweep.SweepEvidenceKey,
            new AdaptiveLayerSummary(
                AdaptiveSingletonLayerNames.Resolve(layer),
                evaluatedCount,
                improvingCount,
                budgetRejectedCount,
                unknown.Count,
                feasibleProbeCount,
                provenInfeasibleProbeCount,
                evaluatedCandidateIds ?? [],
                skippedCandidateIds ?? [],
                qualityBlockedCandidateIds ?? []),
            unknown,
            completed,
            winner,
            Array.Empty<IDisposable>());
}

internal interface IAdaptiveSingletonRunClock
{
    double ElapsedSeconds { get; }
}

internal sealed class AdaptiveSingletonStopwatchClock : IAdaptiveSingletonRunClock
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    public double ElapsedSeconds => _stopwatch.Elapsed.TotalSeconds;
}

internal sealed record AdaptiveAcceptedStateCommitDraft(
    string CandidateId,
    string WinnerLayer,
    string InputAcceptedStateFingerprint,
    string OutputAcceptedStateFingerprint,
    string OutputConflictFingerprint,
    int RootConflictCountBefore,
    int RootConflictCountAfter,
    int OutputSectionEdgeCount,
    int OutputStationEdgeCount,
    int OutputAcceptedCandidateCount,
    int OutputAcceptedTransitionIndex,
    AdaptiveSingletonQualityMetrics Quality);

internal interface IAdaptiveAcceptedStateCommitGuard
{
    void ValidateDraft(AdaptiveAcceptedStateCommitDraft draft);
}

internal sealed class NoOpAdaptiveAcceptedStateCommitGuard : IAdaptiveAcceptedStateCommitGuard
{
    internal static NoOpAdaptiveAcceptedStateCommitGuard Instance { get; } = new();
    public void ValidateDraft(AdaptiveAcceptedStateCommitDraft draft) { }
}

internal sealed record AdaptiveAcceptedStateCommitResult<TModel>(
    AdaptiveAcceptedState<TModel> AcceptedState,
    AdaptiveAcceptedStateCommitDraft Draft)
    where TModel : class, IDisposable;

internal sealed class AdaptiveAcceptedStateTransaction<TModel>
    where TModel : class, IDisposable
{
    private readonly IAdaptiveAcceptedStateCommitGuard _guard;

    internal AdaptiveAcceptedStateTransaction(IAdaptiveAcceptedStateCommitGuard? guard = null)
    {
        _guard = guard ?? NoOpAdaptiveAcceptedStateCommitGuard.Instance;
    }

    internal AdaptiveAcceptedStateCommitResult<TModel> Commit(
        AdaptiveAcceptedState<TModel> current,
        AdaptiveSingletonWinner<TModel> winner,
        InitialReferenceDescriptor initialReference,
        AdaptiveQualityBudgetOptions qualityBudgets)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(winner);
        ArgumentNullException.ThrowIfNull(initialReference);
        ArgumentNullException.ThrowIfNull(qualityBudgets);

        ValidateWinnerIdentity(current, winner, initialReference);
        MasterSolutionSnapshot outputSnapshot = AdaptiveAcceptedStateCopies.Copy(winner.Snapshot);
        MicroFeasibilityReport outputReport = AdaptiveAcceptedStateCopies.Copy(winner.MicroReport);
        string outputFingerprint = SnapshotFingerprint.Calculate(outputSnapshot);
        MicroConflictMetrics outputMetrics = MicroConflictMetricCalculator.Calculate(outputReport.Conflicts);
        string outputConflictFingerprint = MicroConflictFingerprint.Calculate(outputReport.Conflicts);
        if (outputMetrics.RootConflictCount >= current.ConflictMetrics.RootConflictCount)
        {
            throw new InvalidDataException(
                "Adaptive winner does not strictly reduce accepted Micro root conflicts.");
        }

        AdaptiveSingletonQualityMetrics actualQuality = AdaptiveSingletonQualityCalculator.Calculate(
            current.Snapshot,
            outputSnapshot,
            initialReference.Snapshot);
        if (actualQuality != winner.ClaimedQuality)
        {
            throw new InvalidDataException(
                "Adaptive winner quality does not match the immutable initial-reference calculation.");
        }
        AdaptiveQualityBudgetEvaluation budget = AdaptiveQualityBudgetPolicy.Evaluate(
            actualQuality,
            qualityBudgets);
        if (!budget.Eligible)
        {
            throw new InvalidDataException(
                $"Adaptive winner exceeds quality budgets: {string.Join(',', budget.RejectionReasons)}.");
        }

        List<SectionConflictEdgeRequest> sectionEdges = current.SectionEdges.ToList();
        List<StationEventConflictCandidate> stationEdges = current.StationEdges.ToList();
        if (winner.SectionEdge is not null)
        {
            sectionEdges.Add(winner.SectionEdge);
        }
        else
        {
            stationEdges.Add(winner.StationEdge!);
        }
        List<string> candidateIds = current.AcceptedCandidateIds.ToList();
        candidateIds.Add(winner.CandidateId);
        int nextTransitionIndex = current.AcceptedTransitionIndex + 1;
        AdaptiveAcceptedStateCommitDraft draft = new(
            winner.CandidateId,
            AdaptiveSingletonLayerNames.Resolve(winner.Layer),
            current.AcceptedStateFingerprint,
            outputFingerprint,
            outputConflictFingerprint,
            current.ConflictMetrics.RootConflictCount,
            outputMetrics.RootConflictCount,
            sectionEdges.Count,
            stationEdges.Count,
            candidateIds.Count,
            nextTransitionIndex,
            actualQuality);

        // 在模型所有权转移前最后执行提交守卫；守卫失败时，当前接受态和胜者租约仍保持原有归属。
        _guard.ValidateDraft(draft);
        TModel? transferredModel = null;
        try
        {
            transferredModel = winner.ModelLease.Transfer();
            AdaptiveAcceptedState<TModel> next = new(
                transferredModel,
                outputSnapshot,
                outputReport,
                sectionEdges,
                stationEdges,
                candidateIds,
                current.PromotedComponents,
                nextTransitionIndex,
                AdaptiveSingletonLayerNames.Resolve(winner.Layer),
                winner.CandidateId,
                actualQuality);
            transferredModel = null;
            return new AdaptiveAcceptedStateCommitResult<TModel>(next, draft);
        }
        catch
        {
            transferredModel?.Dispose();
            throw;
        }
    }

    private static void ValidateWinnerIdentity(
        AdaptiveAcceptedState<TModel> current,
        AdaptiveSingletonWinner<TModel> winner,
        InitialReferenceDescriptor initialReference)
    {
        if (!winner.IsLive)
        {
            throw new InvalidOperationException("Adaptive winner model lease is no longer live.");
        }
        if (string.IsNullOrWhiteSpace(winner.CandidateId) ||
            !string.Equals(winner.CandidateId, winner.SnapshotCandidateId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Adaptive winner candidate identity does not match its snapshot trial identity.");
        }
        if (!string.Equals(
                winner.InputAcceptedStateFingerprint,
                current.AcceptedStateFingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Adaptive winner was not built from the current accepted-state fingerprint.");
        }
        if (!winner.HasIncumbent)
        {
            throw new InvalidDataException("Adaptive winner has no legal Master incumbent.");
        }
        if (winner.MasterValidatorViolationCount != 0)
        {
            throw new InvalidDataException("Adaptive winner failed Master validation.");
        }
        if (current.AcceptedCandidateIds.Contains(winner.CandidateId, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Adaptive winner candidate is already accepted.");
        }
        if ((winner.SectionEdge is null) == (winner.StationEdge is null))
        {
            throw new InvalidDataException(
                "Adaptive winner must own exactly one section or station-event structural edge.");
        }
        if (winner.SectionEdge is not null &&
            !winner.CandidateId.StartsWith("section::", StringComparison.OrdinalIgnoreCase) ||
            winner.StationEdge is not null &&
            !winner.CandidateId.StartsWith("station::", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Adaptive winner candidate type does not match its structural edge.");
        }
        if (winner.PersistedIisOverlayDependencyIdentities.Count != 0)
        {
            throw new InvalidDataException(
                "Attempt-local IIS dependency overlay cannot be persisted in accepted state.");
        }
        if (string.IsNullOrWhiteSpace(winner.ExecutionToken) ||
            !string.Equals(winner.ExecutionToken, winner.ModelExecutionToken, StringComparison.Ordinal) ||
            !string.Equals(winner.ExecutionToken, winner.SnapshotExecutionToken, StringComparison.Ordinal) ||
            !string.Equals(winner.ExecutionToken, winner.MicroExecutionToken, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Adaptive winner model, snapshot, and Micro report do not belong to one trial.");
        }
        string actualInitialFingerprint = SnapshotFingerprint.Calculate(initialReference.Snapshot);
        if (!string.Equals(initialReference.Fingerprint, actualInitialFingerprint, StringComparison.Ordinal) ||
            !string.Equals(
                winner.InitialReferenceFingerprint,
                initialReference.Fingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Adaptive winner did not use the invocation's immutable initial reference.");
        }
    }
}

internal sealed record AdaptiveSingletonCompletedSweep(
    string AcceptedStateFingerprintBefore,
    string AcceptedStateFingerprintAfter,
    string ConflictFingerprintBefore,
    string ConflictFingerprintAfter,
    int AcceptedTransitionIndexBefore,
    int AcceptedTransitionIndexAfter,
    MicroConflictMetrics MetricsBefore,
    MicroConflictMetrics MetricsAfter,
    MicroConflictCategoryCounts CategoriesBefore,
    MicroConflictCategoryCounts CategoriesAfter,
    int ResolvedConflictCount,
    int IntroducedConflictCount,
    int PersistentConflictCount,
    string CandidateUniverseFingerprint,
    string SearchConfigurationFingerprint,
    int CandidateUniverseSize,
    int InitialCandidateCount,
    int ExpandedCandidateCount,
    string InitialCandidatePoolFingerprint,
    string ExpandedCandidatePoolFingerprint,
    IReadOnlyList<AdaptiveLayerSummary> LayerSummaries,
    IReadOnlyList<AdaptiveUnknownEvidence> UnknownEvidence,
    string WinnerLayer,
    string WinnerCandidateId,
    bool Accepted,
    AdaptiveSingletonQualityMetrics? AcceptedQuality,
    double SweepRuntimeSeconds);

internal sealed record AdaptiveSingletonRunTermination(
    string TerminationReason,
    int SweepCount,
    IReadOnlyList<AdaptiveUnknownEvidence> UnknownEvidence,
    IReadOnlyList<AdaptiveLayerSummary> LayerSummaries,
    string FailureEvidence,
    string StagnationScope);

internal interface IAdaptiveSingletonRunObserver
{
    void OnAcceptedTransition(
        AdaptiveAcceptedStateSnapshot acceptedState,
        InitialReferenceDescriptor initialReference,
        AdaptiveSingletonCompletedSweep completedSweep);

    void OnTerminated(
        AdaptiveAcceptedStateSnapshot acceptedState,
        InitialReferenceDescriptor initialReference,
        AdaptiveSingletonRunTermination termination);
}

internal sealed class NullAdaptiveSingletonRunObserver : IAdaptiveSingletonRunObserver
{
    internal static NullAdaptiveSingletonRunObserver Instance { get; } = new();

    private NullAdaptiveSingletonRunObserver()
    {
    }

    public void OnAcceptedTransition(
        AdaptiveAcceptedStateSnapshot acceptedState,
        InitialReferenceDescriptor initialReference,
        AdaptiveSingletonCompletedSweep completedSweep)
    {
    }

    public void OnTerminated(
        AdaptiveAcceptedStateSnapshot acceptedState,
        InitialReferenceDescriptor initialReference,
        AdaptiveSingletonRunTermination termination)
    {
    }
}

internal sealed class AdaptiveSingletonRunResult<TModel> : IDisposable
    where TModel : class, IDisposable
{
    internal AdaptiveSingletonRunResult(
        string terminationReason,
        AdaptiveAcceptedState<TModel> finalAcceptedState,
        int sweepCount,
        IReadOnlyList<AdaptiveSingletonCompletedSweep> sweepHistory,
        IReadOnlyList<AdaptiveUnknownEvidence> terminalUnknownEvidence,
        IReadOnlyList<AdaptiveLayerSummary> terminalLayerSummaries,
        string failureEvidence = "")
    {
        TerminationReason = terminationReason;
        FinalAcceptedState = finalAcceptedState;
        SweepCount = sweepCount;
        SweepHistory = sweepHistory;
        TerminalUnknownEvidence = terminalUnknownEvidence;
        TerminalLayerSummaries = terminalLayerSummaries;
        FailureEvidence = failureEvidence;
    }

    internal string TerminationReason { get; }
    internal AdaptiveAcceptedState<TModel> FinalAcceptedState { get; }
    internal int SweepCount { get; }
    internal int AcceptedTransitionCount => FinalAcceptedState.AcceptedTransitionIndex;
    internal IReadOnlyList<AdaptiveSingletonCompletedSweep> SweepHistory { get; }
    internal IReadOnlyList<AdaptiveUnknownEvidence> TerminalUnknownEvidence { get; }
    internal IReadOnlyList<AdaptiveLayerSummary> TerminalLayerSummaries { get; }
    internal string FailureEvidence { get; }
    internal string StagnationScope => string.Equals(
            TerminationReason,
            AdaptiveSingletonTerminationReasons.PolicyStagnation,
            StringComparison.Ordinal)
        ? "configured_candidate_policy"
        : string.Empty;

    public void Dispose() => FinalAcceptedState.Dispose();
}

internal sealed class AdaptiveSingletonRunEngine<TModel>
    where TModel : class, IDisposable
{
    private readonly IAdaptiveSingletonRunExecutor<TModel> _executor;
    private readonly IAdaptiveSingletonRunClock _clock;
    private readonly AdaptiveAcceptedStateTransaction<TModel> _transaction;
    private readonly Func<bool> _cancelled;
    private readonly IAdaptiveSingletonRunObserver _observer;

    internal AdaptiveSingletonRunEngine(
        IAdaptiveSingletonRunExecutor<TModel> executor,
        IAdaptiveSingletonRunClock clock,
        AdaptiveAcceptedStateTransaction<TModel> transaction,
        Func<bool>? cancelled = null,
        IAdaptiveSingletonRunObserver? observer = null)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _transaction = transaction ?? throw new ArgumentNullException(nameof(transaction));
        _cancelled = cancelled ?? (() => false);
        _observer = observer ?? NullAdaptiveSingletonRunObserver.Instance;
    }

    internal AdaptiveSingletonRunResult<TModel> Run(
        AdaptiveAcceptedState<TModel> initialAcceptedState,
        InitialReferenceDescriptor initialReference,
        AdaptiveSingletonRunOptions options)
    {
        ArgumentNullException.ThrowIfNull(initialAcceptedState);
        ArgumentNullException.ThrowIfNull(initialReference);
        options.Validate();
        AdaptiveAcceptedState<TModel> current = initialAcceptedState;
        List<AdaptiveSingletonCompletedSweep> history = [];
        int sweepCount = 0;
        AdaptiveSingletonCoordinatorState? coordinatorState = null;
        try
        {
            while (true)
            {
                AdaptiveSingletonTransition? interruption = ResolveExternalInterruption(
                    current,
                    options,
                    coordinatorState);
                if (interruption is not null)
                {
                    return Finish(current, initialReference, interruption, sweepCount, history);
                }

                AdaptiveSingletonCoordinatorState initialCoordinator =
                    AdaptiveSingletonCoordinatorState.Start(
                        current.AcceptedStateFingerprint,
                        options.SafetyAcceptedTransitionCap,
                        string.Empty,
                        current.AcceptedTransitionIndex);
                AdaptiveSingletonTransition begin = AdaptiveSingletonConvergenceCoordinator.Begin(
                    initialCoordinator,
                    current.ConflictMetrics.RootConflictCount);
                if (!string.IsNullOrWhiteSpace(begin.TerminationReason))
                {
                    return Finish(current, initialReference, begin, sweepCount, history);
                }

                double sweepStartedSeconds = _clock.ElapsedSeconds;
                AdaptiveAcceptedStateSnapshot immutableSweepState = current.CreateSnapshot();
                AdaptiveSingletonSweepContext sweep = _executor.PrepareSweep(
                    immutableSweepState,
                    initialReference);
                ValidateSweep(sweep, immutableSweepState);
                sweepCount++;
                coordinatorState = initialCoordinator with { SweepEvidenceKey = sweep.SweepEvidenceKey };
                List<AdaptiveLayerSummary> sweepSummaries = [];
                List<AdaptiveUnknownEvidence> sweepUnknown = [];

                foreach (AdaptiveSingletonLayerKind layer in Enum.GetValues<AdaptiveSingletonLayerKind>())
                {
                    interruption = ResolveExternalInterruption(current, options, coordinatorState);
                    if (interruption is not null)
                    {
                        history.Add(CreateSweepHistory(
                            immutableSweepState,
                            sweep,
                            sweepSummaries,
                            sweepUnknown,
                            string.Empty,
                            string.Empty,
                            false,
                            immutableSweepState,
                            Math.Max(0.0, _clock.ElapsedSeconds - sweepStartedSeconds)));
                        return Finish(current, initialReference, interruption, sweepCount, history);
                    }

                    using AdaptiveSingletonLayerExecutionResult<TModel> layerResult = ExecuteLayer(
                        layer,
                        immutableSweepState,
                        initialReference,
                        sweep);
                    ValidateLayerResult(layerResult, layer, immutableSweepState, sweep);
                    sweepSummaries.Add(layerResult.Summary);
                    sweepUnknown.AddRange(layerResult.UnknownEvidence);

                    interruption = ResolveExternalInterruption(current, options, coordinatorState);
                    if (interruption is not null)
                    {
                        history.Add(CreateSweepHistory(
                            immutableSweepState,
                            sweep,
                            sweepSummaries,
                            sweepUnknown,
                            string.Empty,
                            string.Empty,
                            false,
                            immutableSweepState,
                            Math.Max(0.0, _clock.ElapsedSeconds - sweepStartedSeconds)));
                        return Finish(current, initialReference, interruption, sweepCount, history);
                    }

                    if (!layerResult.HasWinner)
                    {
                        coordinatorState = AdaptiveSingletonConvergenceCoordinator.Advance(
                            coordinatorState,
                            new AdaptiveSingletonEvent(
                                AdaptiveSingletonEventKind.LayerCompletedWithoutWinner,
                                SweepEvidenceKey: sweep.SweepEvidenceKey,
                                LayerSummary: layerResult.Summary,
                                UnknownEvidence: layerResult.UnknownEvidence)).State;
                        if (IsTerminal(coordinatorState.Kind))
                        {
                            AdaptiveSingletonTransition terminal =
                                AdaptiveSingletonConvergenceCoordinator.Advance(
                                    coordinatorState,
                                    new AdaptiveSingletonEvent(
                                        AdaptiveSingletonEventKind.LayerCompletedWithoutWinner));
                            history.Add(CreateSweepHistory(
                                immutableSweepState,
                                sweep,
                                sweepSummaries,
                                sweepUnknown,
                                string.Empty,
                                string.Empty,
                                false,
                                immutableSweepState,
                                Math.Max(0.0, _clock.ElapsedSeconds - sweepStartedSeconds)));
                            return Finish(current, initialReference, terminal, sweepCount, history);
                        }
                        continue;
                    }

                    AdaptiveSingletonWinner<TModel> winner = layerResult.TransferWinner();
                    using (winner)
                    {
                        AdaptiveSingletonTransition prepare =
                            AdaptiveSingletonConvergenceCoordinator.Advance(
                                coordinatorState,
                                new AdaptiveSingletonEvent(
                                    AdaptiveSingletonEventKind.StrictWinnerFound,
                                    winner.CandidateId,
                                    SweepEvidenceKey: sweep.SweepEvidenceKey));
                        AdaptiveAcceptedStateCommitResult<TModel> commit = _transaction.Commit(
                            current,
                            winner,
                            initialReference,
                            options.QualityBudgets);
                        AdaptiveAcceptedState<TModel> previous = current;
                        current = commit.AcceptedState;
                        previous.Dispose();
                        AdaptiveSingletonTransition accepted =
                            AdaptiveSingletonConvergenceCoordinator.Advance(
                                prepare.State,
                                new AdaptiveSingletonEvent(
                                    AdaptiveSingletonEventKind.WinnerCommitted,
                                    winner.CandidateId,
                                    current.AcceptedStateFingerprint,
                                    current.ConflictMetrics.RootConflictCount,
                                    sweep.SweepEvidenceKey));
                        AdaptiveSingletonTransition next =
                            AdaptiveSingletonConvergenceCoordinator.Advance(
                                accepted.State,
                                new AdaptiveSingletonEvent(
                                    AdaptiveSingletonEventKind.AcceptedStateReady,
                                    AcceptedStateFingerprint: current.AcceptedStateFingerprint,
                                    RootConflictCountAfter: current.ConflictMetrics.RootConflictCount,
                                    SweepEvidenceKey: sweep.SweepEvidenceKey));
                        history.Add(CreateSweepHistory(
                            immutableSweepState,
                            sweep,
                            sweepSummaries,
                            sweepUnknown,
                            AdaptiveSingletonLayerNames.Resolve(layer),
                            winner.CandidateId,
                            true,
                            current.CreateSnapshot(),
                            Math.Max(0.0, _clock.ElapsedSeconds - sweepStartedSeconds)));
                        _observer.OnAcceptedTransition(
                            current.CreateSnapshot(),
                            initialReference,
                            history[^1]);
                        coordinatorState = next.State;
                        if (!string.IsNullOrWhiteSpace(next.TerminationReason))
                        {
                            return Finish(current, initialReference, next, sweepCount, history);
                        }
                    }
                    break;
                }
            }
        }
        catch (AdaptiveCheckpointWriteException exception)
        {
            return new AdaptiveSingletonRunResult<TModel>(
                AdaptiveSingletonTerminationReasons.CheckpointWriteFailure,
                current,
                sweepCount,
                history.ToList(),
                coordinatorState?.UnknownEvidence.ToList() ?? [],
                coordinatorState?.LayerSummaries.ToList() ?? [],
                exception.Message);
        }
        catch (Exception exception)
        {
            AdaptiveSingletonCoordinatorState failureState = coordinatorState ??
                AdaptiveSingletonCoordinatorState.Start(
                    current.AcceptedStateFingerprint,
                    options.SafetyAcceptedTransitionCap,
                    string.Empty,
                    current.AcceptedTransitionIndex);
            AdaptiveSingletonTransition failure = AdaptiveSingletonConvergenceCoordinator.Advance(
                failureState,
                new AdaptiveSingletonEvent(AdaptiveSingletonEventKind.SolverFailed));
            return Finish(
                current,
                initialReference,
                failure,
                sweepCount,
                history,
                exception.Message);
        }
    }

    private AdaptiveSingletonLayerExecutionResult<TModel> ExecuteLayer(
        AdaptiveSingletonLayerKind layer,
        AdaptiveAcceptedStateSnapshot state,
        InitialReferenceDescriptor initialReference,
        AdaptiveSingletonSweepContext sweep) => layer switch
        {
            AdaptiveSingletonLayerKind.InitialDirect =>
                _executor.EvaluateInitialDirect(state, initialReference, sweep),
            AdaptiveSingletonLayerKind.InitialIis =>
                _executor.EvaluateInitialIis(state, initialReference, sweep),
            AdaptiveSingletonLayerKind.ExpandedDirect =>
                _executor.EvaluateExpandedDirect(state, initialReference, sweep),
            AdaptiveSingletonLayerKind.ExpandedIis =>
                _executor.EvaluateExpandedIis(state, initialReference, sweep),
            _ => throw new ArgumentOutOfRangeException(nameof(layer))
        };

    private AdaptiveSingletonTransition? ResolveExternalInterruption(
        AdaptiveAcceptedState<TModel> current,
        AdaptiveSingletonRunOptions options,
        AdaptiveSingletonCoordinatorState? currentCoordinator)
    {
        AdaptiveSingletonEventKind? eventKind = _cancelled()
            ? AdaptiveSingletonEventKind.UserCancelled
            : options.TotalRuntimeBudgetSeconds.HasValue &&
              _clock.ElapsedSeconds >= options.TotalRuntimeBudgetSeconds.Value
                ? AdaptiveSingletonEventKind.RuntimeBudgetReached
                : null;
        if (!eventKind.HasValue)
        {
            return null;
        }
        AdaptiveSingletonCoordinatorState state = currentCoordinator ??
            AdaptiveSingletonCoordinatorState.Start(
                current.AcceptedStateFingerprint,
                options.SafetyAcceptedTransitionCap,
                string.Empty,
                current.AcceptedTransitionIndex);
        return AdaptiveSingletonConvergenceCoordinator.Advance(
            state,
            new AdaptiveSingletonEvent(eventKind.Value));
    }

    private static void ValidateSweep(
        AdaptiveSingletonSweepContext sweep,
        AdaptiveAcceptedStateSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(sweep);
        if (string.IsNullOrWhiteSpace(sweep.SweepEvidenceKey) ||
            string.IsNullOrWhiteSpace(sweep.CandidateUniverseFingerprint) ||
            !string.Equals(
                sweep.InputAcceptedStateFingerprint,
                state.AcceptedStateFingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Adaptive sweep preparation does not belong to the current accepted state.");
        }
    }

    private static void ValidateLayerResult(
        AdaptiveSingletonLayerExecutionResult<TModel> result,
        AdaptiveSingletonLayerKind expectedLayer,
        AdaptiveAcceptedStateSnapshot state,
        AdaptiveSingletonSweepContext sweep)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Layer != expectedLayer ||
            !result.Completed && result.UnknownEvidence.Count == 0 ||
            !string.Equals(
                result.InputAcceptedStateFingerprint,
                state.AcceptedStateFingerprint,
                StringComparison.Ordinal) ||
            !string.Equals(result.SweepEvidenceKey, sweep.SweepEvidenceKey, StringComparison.Ordinal) ||
            !string.Equals(
                result.Summary.Layer,
                AdaptiveSingletonLayerNames.Resolve(expectedLayer),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Adaptive layer result is incomplete or belongs to a different accepted-state sweep.");
        }
    }

    private static AdaptiveSingletonCompletedSweep CreateSweepHistory(
        AdaptiveAcceptedStateSnapshot state,
        AdaptiveSingletonSweepContext sweep,
        IReadOnlyList<AdaptiveLayerSummary> summaries,
        IReadOnlyList<AdaptiveUnknownEvidence> unknown,
        string winnerLayer,
        string winnerCandidateId,
        bool accepted,
        AdaptiveAcceptedStateSnapshot outputState,
        double sweepRuntimeSeconds)
    {
        HashSet<string> beforeIdentities = state.MicroReport.Conflicts
            .Select(MicroConflictMetricCalculator.BuildCanonicalKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> afterIdentities = outputState.MicroReport.Conflicts
            .Select(MicroConflictMetricCalculator.BuildCanonicalKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new AdaptiveSingletonCompletedSweep(
            state.AcceptedStateFingerprint,
            outputState.AcceptedStateFingerprint,
            state.ConflictFingerprint,
            outputState.ConflictFingerprint,
            state.AcceptedTransitionIndex,
            outputState.AcceptedTransitionIndex,
            state.ConflictMetrics,
            outputState.ConflictMetrics,
            MicroConflictCategoryCounter.Calculate(state.MicroReport.Conflicts),
            MicroConflictCategoryCounter.Calculate(outputState.MicroReport.Conflicts),
            beforeIdentities.Except(afterIdentities, StringComparer.OrdinalIgnoreCase).Count(),
            afterIdentities.Except(beforeIdentities, StringComparer.OrdinalIgnoreCase).Count(),
            beforeIdentities.Intersect(afterIdentities, StringComparer.OrdinalIgnoreCase).Count(),
            sweep.CandidateUniverseFingerprint,
            sweep.SearchConfigurationFingerprint,
            sweep.CandidateUniverseSize,
            sweep.InitialCandidateCount,
            sweep.ExpandedCandidateCount,
            sweep.InitialCandidatePoolFingerprint,
            sweep.ExpandedCandidatePoolFingerprint,
            summaries.ToList(),
            unknown.ToList(),
            winnerLayer,
            winnerCandidateId,
            accepted,
            accepted ? outputState.LastWinnerQuality : null,
            sweepRuntimeSeconds);
    }

    private AdaptiveSingletonRunResult<TModel> Finish(
        AdaptiveAcceptedState<TModel> current,
        InitialReferenceDescriptor initialReference,
        AdaptiveSingletonTransition terminal,
        int sweepCount,
        IReadOnlyList<AdaptiveSingletonCompletedSweep> history,
        string failureEvidence = "")
    {
        AdaptiveSingletonRunResult<TModel> result = new(
            terminal.TerminationReason,
            current,
            sweepCount,
            history.ToList(),
            terminal.State.UnknownEvidence.ToList(),
            terminal.State.LayerSummaries.ToList(),
            failureEvidence);
        _observer.OnTerminated(
            current.CreateSnapshot(),
            initialReference,
            new AdaptiveSingletonRunTermination(
                result.TerminationReason,
                result.SweepCount,
                result.TerminalUnknownEvidence.ToList(),
                result.TerminalLayerSummaries.ToList(),
                result.FailureEvidence,
                result.StagnationScope));
        return result;
    }

    private static bool IsTerminal(AdaptiveSingletonStateKind state) => state is
        AdaptiveSingletonStateKind.MicroZero or
        AdaptiveSingletonStateKind.PolicyStagnationConfirmed or
        AdaptiveSingletonStateKind.UnresolvedUnknown or
        AdaptiveSingletonStateKind.SafetyCapReached or
        AdaptiveSingletonStateKind.RuntimeBudgetReached or
        AdaptiveSingletonStateKind.QualityBudgetExhausted or
        AdaptiveSingletonStateKind.SolverFailure or
        AdaptiveSingletonStateKind.UserCancelled;
}

internal static class AdaptiveAcceptedStateCopies
{
    internal static MasterSolutionSnapshot Copy(MasterSolutionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new MasterSolutionSnapshot(
            snapshot.ObjectiveValue,
            new Dictionary<string, double>(snapshot.ArrivalSecondsByStopId, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, double>(snapshot.DepartureSecondsByStopId, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, int>(snapshot.StopByStopId, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, int>(snapshot.SectionPrecedenceByKey, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, int>(snapshot.StationEventPrecedenceByKey, StringComparer.OrdinalIgnoreCase),
            snapshot.AdmittedTrainIndices.ToHashSet(),
            new Dictionary<string, double>(
                snapshot.RunningTimeSupplementSecondsByKey ?? new Dictionary<string, double>(),
                StringComparer.OrdinalIgnoreCase));
    }

    internal static MicroFeasibilityReport Copy(MicroFeasibilityReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return report with
        {
            Conflicts = report.Conflicts.ToList(),
            StationSummaries = report.StationSummaries.ToList(),
            RouteChoiceDiagnostics = report.RouteChoiceDiagnostics.ToList()
        };
    }
}
