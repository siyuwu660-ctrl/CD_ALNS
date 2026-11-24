// 结构修复邻域升级器从候选涉及列车出发，沿冲突分量和走廊位置逐级增加列车、区间及时间窗口范围。
// 事务策略保存各层尝试结果，只在获得可接受 incumbent 时提交；无解或停滞则按生命周期规则升级或终止。
using Gurobi;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

internal sealed class StructuralRepairNeighborhoodEscalationOptions
{
    private StructuralRepairNeighborhoodEscalationOptions(
        bool enabled,
        int basePadding,
        IReadOnlyList<int> escalationPaddings,
        IReadOnlyList<int> escalationWindowSeconds,
        int initialLevel)
    {
        Enabled = enabled;
        BasePadding = basePadding;
        EscalationPaddings = escalationPaddings;
        EscalationWindowSeconds = escalationWindowSeconds;
        InitialLevel = initialLevel;
    }

    internal bool Enabled { get; }
    internal int BasePadding { get; }
    internal IReadOnlyList<int> EscalationPaddings { get; }
    internal IReadOnlyList<int> EscalationWindowSeconds { get; }
    internal int InitialLevel { get; }

    internal static StructuralRepairNeighborhoodEscalationOptions Create(
        bool enabled,
        int basePadding,
        IEnumerable<int> escalationPaddings,
        IEnumerable<int> escalationWindows,
        int initialLevel)
    {
        if (basePadding <= 0)
        {
            throw new ArgumentException("Base structural repair padding must be positive.", nameof(basePadding));
        }

        int[] paddings = escalationPaddings?.ToArray()
            ?? throw new ArgumentNullException(nameof(escalationPaddings));
        if (paddings.Length != 2 || paddings.Any(value => value <= basePadding) ||
            !IsStrictlyIncreasing(paddings))
        {
            throw new ArgumentException(
                "Escalation paddings must contain two positive, strictly increasing values greater than base padding.",
                nameof(escalationPaddings));
        }

        int[] windows = escalationWindows?.ToArray()
            ?? throw new ArgumentNullException(nameof(escalationWindows));
        if (windows.Length == 0 || windows.Any(value => value <= 0 || value > 86400) ||
            !IsStrictlyIncreasing(windows))
        {
            throw new ArgumentException(
                "Escalation windows must be positive, strictly increasing, and no greater than 86400 seconds.",
                nameof(escalationWindows));
        }
        if (initialLevel is < 0 or > 1)
        {
            throw new ArgumentException("Initial neighborhood level must be 0 or 1.", nameof(initialLevel));
        }
        if (!enabled && initialLevel != 0)
        {
            throw new ArgumentException("Initial neighborhood level requires escalation to be enabled.", nameof(initialLevel));
        }

        return new StructuralRepairNeighborhoodEscalationOptions(
            enabled,
            basePadding,
            Array.AsReadOnly(paddings),
            Array.AsReadOnly(windows),
            initialLevel);
    }

    internal int ResolveInitialLevel(int iterationOffset, int localIterationIndex) =>
        Enabled && iterationOffset > 0 && localIterationIndex == 1
            ? InitialLevel
            : 0;

    private static bool IsStrictlyIncreasing(IReadOnlyList<int> values)
    {
        for (int index = 1; index < values.Count; index++)
        {
            if (values[index] <= values[index - 1])
            {
                return false;
            }
        }
        return true;
    }
}

internal sealed record StructuralRepairNeighborhoodLevelPlan(
    int Level,
    string Mode,
    int? ReferenceOrderPadding,
    IReadOnlyList<int> WindowSeconds);

internal sealed record StructuralRepairConflictComponentResolution(
    IReadOnlySet<int> ReleasedTrainIndices,
    IReadOnlySet<int> ComponentTrainIndices,
    int ComponentCount);

internal sealed record StructuralRepairNeighborhoodResolution(
    int Level,
    string Mode,
    int? ReferenceOrderPadding,
    IReadOnlySet<int> ReleasedTrainIndices,
    int ComponentCount,
    int ComponentTrainCount);

internal static class StructuralRepairNeighborhoodEscalationResolver
{
    internal static IReadOnlyList<StructuralRepairNeighborhoodResolution> ResolveLevels(
        TimetablingInstance instance,
        HeadwayConflictEdgeExpansion expansion,
        IReadOnlyList<HeadwayConflictCandidate> selectedCandidates,
        IReadOnlyList<HeadwayConflictEpisode> episodes,
        IReadOnlySet<int> propagationTrainIndices,
        int basePadding,
        StructuralRepairNeighborhoodEscalationOptions options)
    {
        List<StructuralRepairNeighborhoodResolution> levels = [];
        IReadOnlySet<int> level0 = StructuralRepairNeighborhoodResolver.ResolveTrainIndices(
            instance, expansion, basePadding, propagationTrainIndices);
        levels.Add(new(0, "reference_order", basePadding, level0, 0, 0));
        if (!options.Enabled)
        {
            return levels;
        }

        IReadOnlySet<int> level1 = StructuralRepairNeighborhoodResolver.ResolveTrainIndices(
            instance, expansion, options.EscalationPaddings[0], propagationTrainIndices);
        IReadOnlySet<int> level2 = StructuralRepairNeighborhoodResolver.ResolveTrainIndices(
            instance, expansion, options.EscalationPaddings[1], propagationTrainIndices);
        levels.Add(new(1, "reference_order", options.EscalationPaddings[0], level1, 0, 0));
        levels.Add(new(2, "reference_order", options.EscalationPaddings[1], level2, 0, 0));

        HashSet<string> selectedEpisodeIds = selectedCandidates
            .Select(candidate => candidate.EpisodeId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<int> candidateTrains = selectedCandidates
            .SelectMany(candidate => new[] { candidate.FirstTrainIndex, candidate.SecondTrainIndex })
            .ToHashSet();
        StructuralRepairConflictComponentResolution component = ResolveSelectedComponents(
            episodes, selectedEpisodeIds, level2, candidateTrains, propagationTrainIndices);
        levels.Add(new(
            3,
            "conflict_component",
            null,
            component.ReleasedTrainIndices,
            component.ComponentCount,
            component.ComponentTrainIndices.Count));
        return levels;
    }

    internal static bool AreEpisodesAdjacent(
        HeadwayConflictEpisode first,
        HeadwayConflictEpisode second)
    {
        bool sharesTrain = first.FirstTrainIndex == second.FirstTrainIndex ||
            first.FirstTrainIndex == second.SecondTrainIndex ||
            first.SecondTrainIndex == second.FirstTrainIndex ||
            first.SecondTrainIndex == second.SecondTrainIndex;
        bool spansTouch = first.MinimumStationOrder <= second.MaximumStationOrder + 1 &&
            first.MaximumStationOrder >= second.MinimumStationOrder - 1;
        return sharesTrain && spansTouch;
    }

    internal static StructuralRepairConflictComponentResolution ResolveSelectedComponents(
        IReadOnlyList<HeadwayConflictEpisode> episodes,
        IReadOnlySet<string> selectedEpisodeIds,
        IReadOnlySet<int> level2ReleasedTrainIndices,
        IReadOnlySet<int> selectedCandidateTrainIndices,
        IReadOnlySet<int> propagationTrainIndices)
    {
        IReadOnlyList<IReadOnlyList<HeadwayConflictEpisode>> components =
            ConflictConnectedComponentBuilder.Build(episodes, AreEpisodesAdjacent);
        List<IReadOnlyList<HeadwayConflictEpisode>> selectedComponents = components
            .Where(component => component.Any(episode => selectedEpisodeIds.Contains(episode.EpisodeId)))
            .ToList();
        HashSet<int> componentTrains = selectedComponents
            .SelectMany(component => component)
            .SelectMany(episode => new[] { episode.FirstTrainIndex, episode.SecondTrainIndex })
            .ToHashSet();
        HashSet<int> released = new(level2ReleasedTrainIndices);
        released.UnionWith(selectedCandidateTrainIndices);
        released.UnionWith(propagationTrainIndices);
        released.UnionWith(componentTrains);
        return new StructuralRepairConflictComponentResolution(
            released,
            componentTrains,
            selectedComponents.Count);
    }
}

internal enum NeighborhoodEscalationAction
{
    ContinueNextAttempt,
    ContinueNextLevel,
    AcceptAndStop,
    ExhaustAndRollback
}

internal static class NeighborhoodEscalationLifecyclePolicy
{
    internal static NeighborhoodEscalationAction ResolveAfterAttempt(bool accepted) =>
        accepted
            ? NeighborhoodEscalationAction.AcceptAndStop
            : NeighborhoodEscalationAction.ContinueNextAttempt;

    internal static NeighborhoodEscalationAction ResolveAfterLevel(
        bool escalationEnabled,
        int currentLevel,
        int finalLevel,
        bool accepted)
    {
        if (accepted)
        {
            return NeighborhoodEscalationAction.AcceptAndStop;
        }
        return escalationEnabled && currentLevel < finalLevel
            ? NeighborhoodEscalationAction.ContinueNextLevel
            : NeighborhoodEscalationAction.ExhaustAndRollback;
    }
}

internal sealed record NeighborhoodEscalationAttemptOutcome(
    int Level,
    int WindowSeconds,
    int SolverStatusCode,
    bool HasIncumbent,
    bool Accepted,
    string Outcome);

internal static class NeighborhoodEscalationTerminationClassifier
{
    internal static string Classify(IReadOnlyList<NeighborhoodEscalationAttemptOutcome> attempts)
    {
        if (attempts.Any(attempt => attempt.HasIncumbent) &&
            attempts.All(attempt => !attempt.Accepted))
        {
            return "neighborhood_escalation_no_acceptable_incumbent";
        }
        if (attempts.Count > 0 && attempts.All(attempt => attempt.SolverStatusCode == GRB.Status.INFEASIBLE))
        {
            return "neighborhood_escalation_bounded_batch_infeasible";
        }
        return "neighborhood_escalation_unknown_budget_exhausted";
    }
}

internal sealed record NeighborhoodEscalationTransactionalState(
    int Iteration,
    int SectionEdgeCount,
    int StationEdgeCount,
    string SnapshotIdentity);

internal static class NeighborhoodEscalationTransactionalPolicy
{
    internal static NeighborhoodEscalationTransactionalState ResolveCommittedState(
        NeighborhoodEscalationTransactionalState acceptedState,
        NeighborhoodEscalationTransactionalState trialState,
        bool accepted) =>
        accepted ? trialState : acceptedState;
}

internal static class CheckerNeighborhoodEscalationCliResolver
{
    internal static StructuralRepairNeighborhoodEscalationOptions Resolve(
        bool enabled,
        string? paddingsRaw,
        string? windowsRaw,
        int initialLevel,
        int basePadding,
        bool hasCheckpointResume)
    {
        if (initialLevel > 0 && !hasCheckpointResume)
        {
            throw new ArgumentException(
                "An initial escalated neighborhood level requires a resumed Checker checkpoint.",
                nameof(initialLevel));
        }
        IReadOnlyList<int> paddings = paddingsRaw is null
            ? Array.AsReadOnly([4, 6])
            : CheckerZeroConflictArgumentParser.ParseWindowSeconds(
                paddingsRaw,
                "--checker-loop-neighborhood-escalation-paddings");
        IReadOnlyList<int> windows = windowsRaw is null
            ? Array.AsReadOnly([600, 1200, 1800])
            : CheckerZeroConflictArgumentParser.ParseWindowSeconds(
                windowsRaw,
                "--checker-loop-neighborhood-escalation-windows-seconds");
        return StructuralRepairNeighborhoodEscalationOptions.Create(
            enabled, basePadding, paddings, windows, initialLevel);
    }
}
