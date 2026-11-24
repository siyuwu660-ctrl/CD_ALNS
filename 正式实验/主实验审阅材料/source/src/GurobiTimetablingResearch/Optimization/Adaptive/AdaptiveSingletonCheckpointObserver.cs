// 检查点观察器监听自适应运行事件，把当前接受态、候选上下文、微观指标和收敛状态组装为 CheckerCheckpoint。
// 写入采用明确的候选身份和轮次元数据，使中断后的运行可以从最后一次已提交状态继续，而不会重复接受未提交试验。
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

internal sealed record AdaptiveCheckpointCandidateContext(
    string CandidateUniverseFingerprint,
    string InitialCandidatePoolFingerprint,
    string ExpandedCandidatePoolFingerprint,
    IReadOnlyList<string> InitialCandidateIds,
    IReadOnlyList<string> ExpandedCandidateIds)
{
    internal static AdaptiveCheckpointCandidateContext Empty { get; } = new(
        string.Empty,
        string.Empty,
        string.Empty,
        Array.Empty<string>(),
        Array.Empty<string>());
}

internal static class AdaptiveSingletonCheckpointFactory
{
    internal static CheckerCheckpoint Create(
        AdaptiveAcceptedStateSnapshot acceptedState,
        InitialReferenceDescriptor initialReference,
        AdaptiveSingletonRunTermination termination,
        AdaptiveCheckpointCandidateContext candidateContext,
        CheckerLoopOptions options,
        string sourceIdentity)
    {
        ArgumentNullException.ThrowIfNull(acceptedState);
        ArgumentNullException.ThrowIfNull(initialReference);
        ArgumentNullException.ThrowIfNull(termination);
        ArgumentNullException.ThrowIfNull(candidateContext);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceIdentity);

        AdaptiveCheckpointResumeConfiguration configuration =
            AdaptiveCheckpointResumeConfiguration.From(options);
        MicroConflictMetrics metrics = acceptedState.ConflictMetrics;
        MicroConflictCategoryCounts categories = MicroConflictCategoryCounter.Calculate(
            acceptedState.MicroReport.Conflicts);
        AdaptiveSingletonQualityMetrics? quality = acceptedState.LastWinnerQuality;
        return new CheckerCheckpoint(
            acceptedState.AcceptedTransitionIndex,
            AdaptiveAcceptedStateCopies.Copy(acceptedState.Snapshot),
            acceptedState.SectionEdges.ToList(),
            acceptedState.StationEdges.ToList(),
            acceptedState.AcceptedCandidateIds.ToHashSet(StringComparer.OrdinalIgnoreCase),
            options.ZeroConflictMode,
            options.EndpointOccupationSeconds,
            sourceIdentity,
            DateTimeOffset.UtcNow,
            options.RepairRunningTimeSupplementRatio,
            acceptedState.PromotedComponents.ToList(),
            RepairDecisionLevel.FlexibleStop,
            "normal-dwell-v1",
            Array.Empty<string>(),
            AdaptiveSingletonMode: true,
            InitialReferenceSnapshot: AdaptiveAcceptedStateCopies.Copy(initialReference.Snapshot),
            InitialReferenceSnapshotFingerprint: initialReference.Fingerprint,
            AcceptedStateFingerprint: acceptedState.AcceptedStateFingerprint,
            MicroConflictFingerprint: acceptedState.ConflictFingerprint,
            SearchConfigurationFingerprint: configuration.SearchConfigurationFingerprint,
            CandidateUniverseFingerprint: candidateContext.CandidateUniverseFingerprint,
            InitialCandidateIds: candidateContext.InitialCandidateIds.ToList(),
            ExpandedCandidateIds: candidateContext.ExpandedCandidateIds.ToList(),
            UnknownEvidence: termination.UnknownEvidence.ToList(),
            TerminationReason: termination.TerminationReason,
            FormatVersion: 3,
            MicroMetrics: new AdaptiveCheckpointMicroMetrics(
                metrics.RawConflictCount,
                metrics.CanonicalConflictCount,
                metrics.RootConflictCount,
                metrics.EpisodeConflictCount,
                categories.ExactHeadwayConflictCount,
                categories.StationAssignmentConflictCount,
                metrics.ConflictedStationCount,
                metrics.AffectedTrainCount,
                metrics.ConnectedComponentCount),
            InitialCandidatePoolFingerprint: candidateContext.InitialCandidatePoolFingerprint,
            ExpandedCandidatePoolFingerprint: candidateContext.ExpandedCandidatePoolFingerprint,
            InitialCandidateCap: configuration.InitialCandidateCap,
            ExpandedCandidateCap: configuration.ExpandedCandidateCap,
            SafetyAcceptedTransitionCap: configuration.SafetyAcceptedTransitionCap,
            LastWinnerLayer: acceptedState.LastWinnerLayer,
            LastWinnerCandidateId: acceptedState.LastWinnerCandidateId,
            LastWinnerQuality: quality is null
                ? null
                : new AdaptiveCheckpointQualityMetrics(
                    quality.StepTotalRetimingSeconds,
                    quality.InitialTotalRetimingSeconds,
                    quality.StepMaximumRetimingSeconds,
                    quality.InitialMaximumRetimingSeconds,
                    quality.IncrementalRunningSupplementSeconds,
                    quality.CurrentTotalRunningSupplementSeconds,
                    quality.CurrentSupplementSectionCount),
            StagnationScope: termination.StagnationScope);
    }
}

internal sealed class AdaptiveSingletonCheckpointObserver : IAdaptiveSingletonRunObserver
{
    private readonly string _checkpointPath;
    private readonly string _instanceName;
    private readonly CheckerLoopOptions _options;
    private readonly Func<AdaptiveCheckpointCandidateContext> _candidateContext;
    private readonly string _sourceIdentity;

    internal AdaptiveSingletonCheckpointObserver(
        string checkpointPath,
        string instanceName,
        CheckerLoopOptions options,
        Func<AdaptiveCheckpointCandidateContext> candidateContext,
        string sourceIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpointPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceName);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(candidateContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceIdentity);
        _checkpointPath = Path.GetFullPath(checkpointPath);
        _instanceName = instanceName;
        _options = options;
        _candidateContext = candidateContext;
        _sourceIdentity = sourceIdentity;
    }

    public void OnAcceptedTransition(
        AdaptiveAcceptedStateSnapshot acceptedState,
        InitialReferenceDescriptor initialReference,
        AdaptiveSingletonCompletedSweep completedSweep)
    {
        AdaptiveSingletonRunTermination transition = new(
            string.Empty,
            acceptedState.AcceptedTransitionIndex,
            completedSweep.UnknownEvidence.ToList(),
            completedSweep.LayerSummaries.ToList(),
            string.Empty,
            string.Empty);
        Save(acceptedState, initialReference, transition);
    }

    public void OnTerminated(
        AdaptiveAcceptedStateSnapshot acceptedState,
        InitialReferenceDescriptor initialReference,
        AdaptiveSingletonRunTermination termination) =>
        Save(acceptedState, initialReference, termination);

    private void Save(
        AdaptiveAcceptedStateSnapshot acceptedState,
        InitialReferenceDescriptor initialReference,
        AdaptiveSingletonRunTermination termination)
    {
        try
        {
            CheckerCheckpoint checkpoint = AdaptiveSingletonCheckpointFactory.Create(
                acceptedState,
                initialReference,
                termination,
                _candidateContext(),
                _options,
                _sourceIdentity);
            CheckerCheckpointStore.SaveAtomic(_checkpointPath, _instanceName, checkpoint);
        }
        catch (Exception exception) when (exception is not AdaptiveCheckpointWriteException)
        {
            throw new AdaptiveCheckpointWriteException(
                $"Failed to persist Adaptive checkpoint '{_checkpointPath}'.",
                exception);
        }
    }
}

internal sealed class AdaptiveCheckpointWriteException : IOException
{
    internal AdaptiveCheckpointWriteException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
