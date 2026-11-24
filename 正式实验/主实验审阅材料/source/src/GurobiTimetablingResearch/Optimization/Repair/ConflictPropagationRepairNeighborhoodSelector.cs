// 冲突传播邻域选择器只决定本次修复模型临时释放哪些已验证微观选择。
// 它把时刻事件、时间边界和资源域关系组成有向图，用可解释的传播量排序候选需求；结果不会生成永久约束或不可行性证明。
using System.Diagnostics;
using System.Globalization;
using System.Text;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

public enum RepairNeighborhoodSelectorKind
{
    Legacy,
    Propagation
}

public static class RepairNeighborhoodSelectorArgumentParser
{
    public static RepairNeighborhoodSelectorKind Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            string.Equals(value, "legacy", StringComparison.OrdinalIgnoreCase))
        {
            return RepairNeighborhoodSelectorKind.Legacy;
        }
        if (string.Equals(value, "propagation", StringComparison.OrdinalIgnoreCase))
        {
            return RepairNeighborhoodSelectorKind.Propagation;
        }
        throw new ArgumentException(
            $"Unknown repair-neighborhood selector '{value}'. Expected legacy or propagation.");
    }
}

public enum ConflictPropagationRelationType
{
    TrainDwellLower,
    TrainDwellUpper,
    TrainRuntimeLower,
    TrainRuntimeUpper,
    ExactHeadwayCurrentChoice,
    PotentialResourceCompetition,
    StopRouteOvertakingDependency
}

public enum ConflictPropagationBoundDirection
{
    Lower,
    Upper,
    Association
}

public enum ConflictPropagationRelationReliability
{
    ProvenCurrentBoundary,
    ConditionalCurrentChoice,
    PotentialDomainOverlap
}

public sealed record ConflictPropagationEvent(
    string EventId,
    string DemandId,
    int TrainIndex,
    string StationName,
    int StationOrder,
    StationEventSide EventSide,
    double CurrentTimeSeconds,
    double EarliestTimeSeconds,
    double LatestTimeSeconds,
    IReadOnlyList<string> ResourceDomainKeys,
    bool HasReliableWitness);

public sealed record ConflictPropagationRelation(
    string RelationId,
    string FromEventId,
    string ToEventId,
    ConflictPropagationRelationType RelationType,
    ConflictPropagationBoundDirection BoundDirection,
    double RequiredDifferenceSeconds,
    ConflictPropagationRelationReliability Reliability,
    string Description,
    string Source,
    string Condition);

public sealed record ConflictPropagationSeed(
    string ConflictId,
    string ConflictKind,
    IReadOnlyDictionary<string, double> InitialEventShifts,
    IReadOnlySet<string> DirectDemandIds,
    bool PreserveDirectDemandGroup,
    string Action,
    string ProofContext);

public sealed record ConflictPropagationSelectionRequest(
    RepairNeighborhoodSelectorKind Selector,
    string CandidateFingerprint,
    string BranchId,
    int EndpointOrderChangeBudget,
    int ReleaseBudget,
    IReadOnlySet<string> LegacyReleasedRequirementIds,
    IReadOnlySet<string> TemporarilyFixedRequirementIds,
    IReadOnlyList<ConflictPropagationEvent> Events,
    IReadOnlyList<ConflictPropagationRelation> Relations,
    IReadOnlyList<ConflictPropagationSeed> Seeds);

public sealed record ConflictPropagationSelectionAuditRow(
    string CandidateFingerprint,
    string Branch,
    string Selector,
    string SeedConflictId,
    string EventId,
    string DemandId,
    string RelationType,
    double? SlackSeconds,
    double EstimatedShiftSeconds,
    string SelectionReason,
    bool Selected,
    string BoundaryReason,
    string FallbackReason,
    string PropagationPath,
    string RelationSource,
    string RelationCondition,
    string RelationReliability);

public sealed record ConflictPropagationSelectionResult(
    IReadOnlySet<string> ReleasedRequirementIds,
    IReadOnlyList<ConflictPropagationSelectionAuditRow> AuditRows,
    string FallbackReason,
    double AnalysisSeconds,
    int EventCount,
    int RelationCount,
    int SeedCount);

/// <summary>
/// 沿时间差上下界传播候选时移，并用资源域关联补入可能阻碍该时移的离散选择。
/// 传播值只用于选择临时邻域；条件关系和 Unknown 核心不会被解释为原问题的硬结论。
/// </summary>
public static class ConflictPropagationRepairNeighborhoodSelector
{
    private const double ToleranceSeconds = 1e-6;

    public static ConflictPropagationSelectionResult Select(
        ConflictPropagationSelectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Stopwatch stopwatch = Stopwatch.StartNew();
        Validate(request);
        if (request.Selector == RepairNeighborhoodSelectorKind.Legacy)
        {
            return BuildLegacyResult(request, stopwatch);
        }

        Dictionary<string, ConflictPropagationEvent> eventById = request.Events.ToDictionary(
            item => item.EventId,
            StringComparer.OrdinalIgnoreCase);
        Dictionary<string, CandidateEvidence> evidenceByDemand = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> groupedDirectDemands = new(StringComparer.OrdinalIgnoreCase);

        foreach (ConflictPropagationSeed seed in request.Seeds
                     .OrderBy(item => item.ConflictId, StringComparer.Ordinal)
                     .ThenBy(item => item.Action, StringComparer.Ordinal))
        {
            foreach (string demandId in seed.DirectDemandIds.Order(StringComparer.OrdinalIgnoreCase))
            {
                CandidateEvidence evidence = GetEvidence(evidenceByDemand, demandId);
                evidence.AddDirect(seed, demandId);
                if (seed.PreserveDirectDemandGroup)
                {
                    groupedDirectDemands.Add(demandId);
                }
            }
            PropagateSeed(seed, request, eventById, evidenceByDemand);
        }

        int releaseBudget = request.ReleaseBudget > 0
            ? request.ReleaseBudget
            : request.LegacyReleasedRequirementIds.Count;
        string fallbackReason = string.Empty;
        HashSet<string> selectableGrouped = groupedDirectDemands
            .Where(request.TemporarilyFixedRequirementIds.Contains)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selectableGrouped.Count > releaseBudget)
        {
            fallbackReason = "完整 assignment 需求组超过本分支释放预算，保持 Legacy 集合，避免截断必须联合处理的组。";
            return BuildFallbackResult(request, evidenceByDemand, fallbackReason, stopwatch);
        }

        List<CandidateEvidence> ranked = evidenceByDemand.Values
            .Where(item => request.TemporarilyFixedRequirementIds.Contains(item.DemandId))
            .OrderBy(item => item.IsDirect ? 0 : item.IsNumericallyPropagated ? 1 : 2)
            .ThenByDescending(item => item.ConflictIds.Count)
            .ThenByDescending(item => item.MaximumAbsoluteShiftSeconds)
            .ThenBy(item => item.IncrementalFreedomScore)
            .ThenBy(item => item.DemandId, StringComparer.Ordinal)
            .ToList();
        HashSet<string> selected = new(selectableGrouped, StringComparer.OrdinalIgnoreCase);
        foreach (CandidateEvidence candidate in ranked)
        {
            if (selected.Count >= releaseBudget)
            {
                break;
            }
            selected.Add(candidate.DemandId);
        }

        if (selected.Count == 0 && request.LegacyReleasedRequirementIds.Count > 0)
        {
            fallbackReason = request.Seeds.Count == 0
                ? "当前冲突无法映射到事件级种子，保持 Legacy 释放集合。"
                : "传播候选均缺少当前可靠临时固定，保持 Legacy 释放集合。";
            return BuildFallbackResult(request, evidenceByDemand, fallbackReason, stopwatch);
        }

        List<ConflictPropagationSelectionAuditRow> rows = BuildAuditRows(
            request, evidenceByDemand, selected, fallbackReason);
        stopwatch.Stop();
        return new ConflictPropagationSelectionResult(
            selected,
            rows,
            fallbackReason,
            stopwatch.Elapsed.TotalSeconds,
            request.Events.Count,
            request.Relations.Count,
            request.Seeds.Count);
    }

    private static void PropagateSeed(
        ConflictPropagationSeed seed,
        ConflictPropagationSelectionRequest request,
        IReadOnlyDictionary<string, ConflictPropagationEvent> eventById,
        IDictionary<string, CandidateEvidence> evidenceByDemand)
    {
        Queue<PropagationState> queue = new();
        Dictionary<string, double> strongestPositive = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, double> strongestNegative = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string eventId, double shift) in seed.InitialEventShifts
                     .OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            if (!eventById.ContainsKey(eventId) || Math.Abs(shift) <= ToleranceSeconds)
            {
                continue;
            }
            queue.Enqueue(new PropagationState(
                eventId,
                shift,
                seed.ConflictId,
                $"{seed.ConflictId}:{seed.Action}->{eventId}",
                null,
                null));
        }

        while (queue.Count > 0)
        {
            PropagationState state = queue.Dequeue();
            Dictionary<string, double> strongest = state.ShiftSeconds >= 0.0
                ? strongestPositive
                : strongestNegative;
            double magnitude = Math.Abs(state.ShiftSeconds);
            if (strongest.TryGetValue(state.EventId, out double priorMagnitude) &&
                priorMagnitude >= magnitude - ToleranceSeconds)
            {
                continue;
            }
            strongest[state.EventId] = magnitude;
            ConflictPropagationEvent current = eventById[state.EventId];
            string boundaryReason = ResolveTimeWindowBoundary(current, state.ShiftSeconds);
            CandidateEvidence currentEvidence = GetEvidence(evidenceByDemand, current.DemandId);
            currentEvidence.AddPropagation(seed, current, state, boundaryReason);
            if (!string.IsNullOrWhiteSpace(boundaryReason))
            {
                continue;
            }

            foreach (ConflictPropagationRelation relation in request.Relations
                         .Where(item => string.Equals(item.FromEventId, state.EventId, StringComparison.OrdinalIgnoreCase) ||
                                        string.Equals(item.ToEventId, state.EventId, StringComparison.OrdinalIgnoreCase))
                         .OrderBy(item => item.RelationId, StringComparer.Ordinal))
            {
                if (!TryResolveNextShift(relation, state, eventById, out string nextEventId,
                        out double nextShift, out double? slack))
                {
                    continue;
                }
                ConflictPropagationEvent next = eventById[nextEventId];
                if (relation.BoundDirection == ConflictPropagationBoundDirection.Association)
                {
                    CandidateEvidence association = GetEvidence(evidenceByDemand, next.DemandId);
                    association.AddAssociation(seed, next, relation, state.Path, slack);
                    continue;
                }
                if (Math.Abs(nextShift) <= ToleranceSeconds)
                {
                    continue;
                }
                queue.Enqueue(new PropagationState(
                    nextEventId,
                    nextShift,
                    seed.ConflictId,
                    $"{state.Path}->{relation.RelationId}->{nextEventId}",
                    relation,
                    slack));
            }
        }
    }

    private static bool TryResolveNextShift(
        ConflictPropagationRelation relation,
        PropagationState state,
        IReadOnlyDictionary<string, ConflictPropagationEvent> eventById,
        out string nextEventId,
        out double nextShift,
        out double? slackSeconds)
    {
        nextEventId = string.Empty;
        nextShift = 0.0;
        slackSeconds = null;
        if (!eventById.TryGetValue(relation.FromEventId, out ConflictPropagationEvent? from) ||
            !eventById.TryGetValue(relation.ToEventId, out ConflictPropagationEvent? to))
        {
            return false;
        }
        bool atFrom = string.Equals(state.EventId, relation.FromEventId, StringComparison.OrdinalIgnoreCase);
        if (relation.BoundDirection == ConflictPropagationBoundDirection.Association)
        {
            nextEventId = atFrom ? relation.ToEventId : relation.FromEventId;
            slackSeconds = null;
            return !string.Equals(nextEventId, state.EventId, StringComparison.OrdinalIgnoreCase);
        }

        double realizedDifference = to.CurrentTimeSeconds - from.CurrentTimeSeconds;
        double slack = relation.BoundDirection == ConflictPropagationBoundDirection.Lower
            ? realizedDifference - relation.RequiredDifferenceSeconds
            : relation.RequiredDifferenceSeconds - realizedDifference;
        slack = Math.Max(0.0, slack);
        slackSeconds = slack;

        if (relation.BoundDirection == ConflictPropagationBoundDirection.Lower)
        {
            if (atFrom && state.ShiftSeconds > ToleranceSeconds)
            {
                nextEventId = relation.ToEventId;
                nextShift = Math.Max(0.0, state.ShiftSeconds - slack);
                return true;
            }
            if (!atFrom && state.ShiftSeconds < -ToleranceSeconds)
            {
                nextEventId = relation.FromEventId;
                nextShift = -Math.Max(0.0, -state.ShiftSeconds - slack);
                return true;
            }
        }
        else
        {
            if (atFrom && state.ShiftSeconds < -ToleranceSeconds)
            {
                nextEventId = relation.ToEventId;
                nextShift = -Math.Max(0.0, -state.ShiftSeconds - slack);
                return true;
            }
            if (!atFrom && state.ShiftSeconds > ToleranceSeconds)
            {
                nextEventId = relation.FromEventId;
                nextShift = Math.Max(0.0, state.ShiftSeconds - slack);
                return true;
            }
        }
        return false;
    }

    private static string ResolveTimeWindowBoundary(
        ConflictPropagationEvent item,
        double shiftSeconds)
    {
        double target = item.CurrentTimeSeconds + shiftSeconds;
        if (target < item.EarliestTimeSeconds - ToleranceSeconds ||
            target > item.LatestTimeSeconds + ToleranceSeconds)
        {
            return $"传播时移越过事件时间窗 [{item.EarliestTimeSeconds:R},{item.LatestTimeSeconds:R}]。";
        }
        return string.Empty;
    }

    private static List<ConflictPropagationSelectionAuditRow> BuildAuditRows(
        ConflictPropagationSelectionRequest request,
        IReadOnlyDictionary<string, CandidateEvidence> evidenceByDemand,
        IReadOnlySet<string> selected,
        string fallbackReason)
    {
        List<ConflictPropagationSelectionAuditRow> rows = [];
        foreach (CandidateEvidence evidence in evidenceByDemand.Values
                     .OrderBy(item => item.DemandId, StringComparer.Ordinal))
        {
            bool hasFix = request.TemporarilyFixedRequirementIds.Contains(evidence.DemandId);
            bool isSelected = hasFix && selected.Contains(evidence.DemandId);
            string boundary = string.Join('|', evidence.BoundaryReasons
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal));
            if (!hasFix)
            {
                boundary = string.IsNullOrWhiteSpace(boundary)
                    ? "该需求未固定或没有当前可靠的临时固定，不能虚构释放。"
                    : $"{boundary}|该需求未固定或没有当前可靠的临时固定，不能虚构释放。";
            }
            rows.Add(new ConflictPropagationSelectionAuditRow(
                request.CandidateFingerprint,
                request.BranchId,
                request.Selector.ToString().ToLowerInvariant(),
                string.Join('|', evidence.ConflictIds.Order(StringComparer.Ordinal)),
                evidence.EventId,
                evidence.DemandId,
                evidence.RelationType,
                evidence.MinimumSlackSeconds,
                evidence.MaximumAbsoluteShiftSeconds,
                evidence.SelectionReason,
                isSelected,
                boundary,
                fallbackReason,
                evidence.PropagationPath,
                evidence.RelationSource,
                evidence.RelationCondition,
                evidence.RelationReliability));
        }
        foreach (string legacyId in request.LegacyReleasedRequirementIds
                     .Where(id => !evidenceByDemand.ContainsKey(id))
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            rows.Add(new ConflictPropagationSelectionAuditRow(
                request.CandidateFingerprint, request.BranchId, "propagation", string.Empty,
                string.Empty, legacyId, string.Empty, null, 0.0,
                "Legacy 集合中的需求未被本轮传播图选中。", selected.Contains(legacyId),
                string.Empty, fallbackReason, string.Empty, string.Empty, string.Empty, string.Empty));
        }
        return rows;
    }

    private static ConflictPropagationSelectionResult BuildLegacyResult(
        ConflictPropagationSelectionRequest request,
        Stopwatch stopwatch)
    {
        List<ConflictPropagationSelectionAuditRow> rows = request.LegacyReleasedRequirementIds
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(id => new ConflictPropagationSelectionAuditRow(
                request.CandidateFingerprint, request.BranchId, "legacy", string.Empty,
                string.Empty, id, "legacy_release_set", null, 0.0,
                "沿用现有协调器生成的临时释放集合。", true,
                string.Empty, string.Empty, string.Empty, "legacy-planner", string.Empty, "legacy"))
            .ToList();
        stopwatch.Stop();
        return new ConflictPropagationSelectionResult(
            new HashSet<string>(request.LegacyReleasedRequirementIds, StringComparer.OrdinalIgnoreCase),
            rows, string.Empty, stopwatch.Elapsed.TotalSeconds,
            request.Events.Count, request.Relations.Count, request.Seeds.Count);
    }

    private static ConflictPropagationSelectionResult BuildFallbackResult(
        ConflictPropagationSelectionRequest request,
        IReadOnlyDictionary<string, CandidateEvidence> evidenceByDemand,
        string fallbackReason,
        Stopwatch stopwatch)
    {
        HashSet<string> released = new(
            request.LegacyReleasedRequirementIds.Where(request.TemporarilyFixedRequirementIds.Contains),
            StringComparer.OrdinalIgnoreCase);
        List<ConflictPropagationSelectionAuditRow> rows = BuildAuditRows(
            request, evidenceByDemand, released, fallbackReason);
        stopwatch.Stop();
        return new ConflictPropagationSelectionResult(
            released, rows, fallbackReason, stopwatch.Elapsed.TotalSeconds,
            request.Events.Count, request.Relations.Count, request.Seeds.Count);
    }

    private static CandidateEvidence GetEvidence(
        IDictionary<string, CandidateEvidence> values,
        string demandId)
    {
        if (!values.TryGetValue(demandId, out CandidateEvidence? evidence))
        {
            evidence = new CandidateEvidence(demandId);
            values.Add(demandId, evidence);
        }
        return evidence;
    }

    private static void Validate(ConflictPropagationSelectionRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CandidateFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.BranchId);
        if (request.EndpointOrderChangeBudget < 0 || request.ReleaseBudget < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request.ReleaseBudget));
        }
        List<string> duplicateEvents = request.Events
            .GroupBy(item => item.EventId, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();
        if (duplicateEvents.Count > 0)
        {
            throw new InvalidDataException(
                $"Duplicate propagation event IDs: {string.Join('|', duplicateEvents)}");
        }
        foreach (ConflictPropagationEvent item in request.Events)
        {
            if (!double.IsFinite(item.CurrentTimeSeconds) ||
                !double.IsFinite(item.EarliestTimeSeconds) ||
                !double.IsFinite(item.LatestTimeSeconds) ||
                item.LatestTimeSeconds < item.EarliestTimeSeconds)
            {
                throw new InvalidDataException($"Invalid time domain for event '{item.EventId}'.");
            }
        }
    }

    private sealed class CandidateEvidence
    {
        internal CandidateEvidence(string demandId)
        {
            DemandId = demandId;
        }

        internal string DemandId { get; }
        internal HashSet<string> ConflictIds { get; } = new(StringComparer.Ordinal);
        internal List<string> BoundaryReasons { get; } = [];
        internal bool IsDirect { get; private set; }
        internal bool IsNumericallyPropagated { get; private set; }
        internal double MaximumAbsoluteShiftSeconds { get; private set; }
        internal double? MinimumSlackSeconds { get; private set; }
        internal int IncrementalFreedomScore { get; private set; } = int.MaxValue;
        internal string EventId { get; private set; } = string.Empty;
        internal string RelationType { get; private set; } = string.Empty;
        internal string SelectionReason { get; private set; } = string.Empty;
        internal string PropagationPath { get; private set; } = string.Empty;
        internal string RelationSource { get; private set; } = string.Empty;
        internal string RelationCondition { get; private set; } = string.Empty;
        internal string RelationReliability { get; private set; } = string.Empty;

        internal void AddDirect(ConflictPropagationSeed seed, string demandId)
        {
            ConflictIds.Add(seed.ConflictId);
            IsDirect = true;
            SelectionReason = seed.PreserveDirectDemandGroup
                ? "直接冲突需求组必须整体参与本次临时邻域；该分组是诊断范围，不声明为最小核心。"
                : "需求直接参与当前冲突。";
            RelationType = seed.ConflictKind;
            RelationSource = seed.ProofContext;
            IncrementalFreedomScore = 0;
        }

        internal void AddPropagation(
            ConflictPropagationSeed seed,
            ConflictPropagationEvent item,
            PropagationState state,
            string boundaryReason)
        {
            ConflictIds.Add(seed.ConflictId);
            EventId = item.EventId;
            MaximumAbsoluteShiftSeconds = Math.Max(
                MaximumAbsoluteShiftSeconds,
                Math.Abs(state.ShiftSeconds));
            IsNumericallyPropagated |= state.Relation is not null;
            if (state.SlackSeconds.HasValue)
            {
                MinimumSlackSeconds = !MinimumSlackSeconds.HasValue
                    ? state.SlackSeconds
                    : Math.Min(MinimumSlackSeconds.Value, state.SlackSeconds.Value);
            }
            IncrementalFreedomScore = Math.Min(
                IncrementalFreedomScore,
                item.ResourceDomainKeys.Count);
            PropagationPath = state.Path;
            if (string.IsNullOrWhiteSpace(SelectionReason))
            {
                SelectionReason = IsNumericallyPropagated
                    ? "沿可靠或带条件标记的时间边界传播后，需要释放该需求的离散选择。"
                    : "该事件是冲突修复的直接时移起点。";
            }
            if (state.Relation is not null)
            {
                RelationType = state.Relation.RelationType.ToString();
                RelationSource = state.Relation.Source;
                RelationCondition = state.Relation.Condition;
                RelationReliability = state.Relation.Reliability.ToString();
            }
            if (!string.IsNullOrWhiteSpace(boundaryReason))
            {
                BoundaryReasons.Add(boundaryReason);
            }
        }

        internal void AddAssociation(
            ConflictPropagationSeed seed,
            ConflictPropagationEvent item,
            ConflictPropagationRelation relation,
            string path,
            double? slack)
        {
            ConflictIds.Add(seed.ConflictId);
            EventId = item.EventId;
            RelationType = relation.RelationType.ToString();
            MinimumSlackSeconds = slack;
            IncrementalFreedomScore = Math.Min(
                IncrementalFreedomScore,
                item.ResourceDomainKeys.Count);
            SelectionReason = relation.Reliability == ConflictPropagationRelationReliability.ProvenCurrentBoundary
                ? "资源关系直接连接到冲突传播路径。"
                : "条件或潜在资源关系用于邻域排序，不作为无条件硬剪枝。";
            PropagationPath = $"{path}->{relation.RelationId}->{item.EventId}";
            RelationSource = relation.Source;
            RelationCondition = relation.Condition;
            RelationReliability = relation.Reliability.ToString();
        }
    }

    private sealed record PropagationState(
        string EventId,
        double ShiftSeconds,
        string ConflictId,
        string Path,
        ConflictPropagationRelation? Relation,
        double? SlackSeconds);
}

public sealed record ConflictPropagationGraph(
    IReadOnlyList<ConflictPropagationEvent> Events,
    IReadOnlyList<ConflictPropagationRelation> Relations,
    IReadOnlyList<ConflictPropagationSeed> Seeds,
    IReadOnlySet<string> TemporarilyFixedRequirementIds);

/// <summary>
/// 从当前候选快照、Micro 冲突和已验证分配构建事件级传播图。
/// 资源关系通过“车站—进路/股道”索引连接相邻使用者，避免枚举全体列车笛卡尔积。
/// </summary>
public static class ConflictPropagationGraphBuilder
{
    private const double TimeToleranceSeconds = GurobiTimetablingResearch.Domain.TimetableTimePolicy.NumericToleranceSeconds;

    public static ConflictPropagationGraph Build(
        TimetablingInstance instance,
        MasterSolutionSnapshot snapshot,
        MicroFeasibilityReport report,
        IReadOnlyList<VerifiedMicroAssignmentWitness>? witnesses,
        IReadOnlySet<string>? additionalCoreRequirementIds = null)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(report);
        IReadOnlyList<VerifiedMicroAssignmentWitness> availableWitnesses = witnesses ?? [];
        Dictionary<string, VerifiedMicroAssignmentWitness> witnessByRequirement = availableWitnesses
            .GroupBy(item => item.RequirementId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
        Dictionary<(int TrainIndex, int StationOrder), TrainStopRecord> stopByTrainOrder =
            instance.TrainStops.ToDictionary(item => (item.TrainIndex, item.StationOrder));
        List<ConflictPropagationEvent> events = [];
        foreach (TrainStopRecord stop in instance.TrainStops
                     .OrderBy(item => item.TrainIndex)
                     .ThenBy(item => item.StationOrder))
        {
            bool isStop = snapshot.StopByStopId.TryGetValue(stop.StopId, out int stopValue)
                ? stopValue == 1
                : stop.StopRequirement != StopRequirement.MustPass;
            witnessByRequirement.TryGetValue(stop.StopId, out VerifiedMicroAssignmentWitness? witness);
            if (stop.StopRole != TrainStopRole.Origin &&
                snapshot.ArrivalSecondsByStopId.TryGetValue(stop.StopId, out double arrival))
            {
                events.Add(BuildEvent(
                    instance, snapshot, stop, isStop, witness,
                    StationEventSide.Incoming, arrival,
                    stop.EarliestArrSeconds, stop.LatestArrSeconds));
            }
            if (stop.StopRole != TrainStopRole.Destination &&
                snapshot.DepartureSecondsByStopId.TryGetValue(stop.StopId, out double departure))
            {
                events.Add(BuildEvent(
                    instance, snapshot, stop, isStop, witness,
                    StationEventSide.Outgoing, departure,
                    stop.EarliestDepSeconds, stop.LatestDepSeconds));
            }
        }

        Dictionary<string, ConflictPropagationEvent> eventById = events.ToDictionary(
            item => item.EventId,
            StringComparer.OrdinalIgnoreCase);
        List<ConflictPropagationRelation> relations = [];
        AddTrainTemporalRelations(instance, snapshot, eventById, relations);
        AddCurrentRouteHeadways(instance, availableWitnesses, eventById, relations);
        AddIndexedResourceAssociations(events, relations);
        List<ConflictPropagationSeed> seeds = BuildSeeds(
            report, stopByTrainOrder, eventById, additionalCoreRequirementIds);
        HashSet<string> fixedRequirementIds = availableWitnesses
            .Select(item => item.RequirementId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new ConflictPropagationGraph(
            events,
            relations
                .GroupBy(item => item.RelationId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(item => item.RelationId, StringComparer.Ordinal)
                .ToList(),
            seeds,
            fixedRequirementIds);
    }

    private static ConflictPropagationEvent BuildEvent(
        TimetablingInstance instance,
        MasterSolutionSnapshot snapshot,
        TrainStopRecord stop,
        bool isStop,
        VerifiedMicroAssignmentWitness? witness,
        StationEventSide side,
        double currentTime,
        double earliest,
        double latest)
    {
        HashSet<string> resources = ResolveLegalResourceKeys(instance, stop, isStop, side);
        if (witness is not null)
        {
            resources.Add($"track::{stop.StationName}::{witness.TrackName}");
            int? routeId = side == StationEventSide.Incoming
                ? witness.IncomingRouteId
                : witness.OutgoingRouteId;
            if (routeId.HasValue)
            {
                resources.Add($"route::{routeId.Value}");
            }
        }
        bool witnessMatchesTime = witness is not null &&
            (side == StationEventSide.Incoming
                ? !witness.ArrivalSeconds.HasValue ||
                  Math.Abs(witness.ArrivalSeconds.Value - currentTime) <= TimeToleranceSeconds
                : !witness.DepartureSeconds.HasValue ||
                  Math.Abs(witness.DepartureSeconds.Value - currentTime) <= TimeToleranceSeconds);
        return new ConflictPropagationEvent(
            BuildEventId(stop.StopId, side),
            stop.StopId,
            stop.TrainIndex,
            stop.StationName,
            stop.StationOrder,
            side,
            currentTime,
            earliest,
            latest,
            resources.Order(StringComparer.Ordinal).ToList(),
            witnessMatchesTime);
    }

    private static HashSet<string> ResolveLegalResourceKeys(
        TimetablingInstance instance,
        TrainStopRecord stop,
        bool isStop,
        StationEventSide side)
    {
        HashSet<string> result = new(StringComparer.OrdinalIgnoreCase);
        if (instance.TryGetTrackChoiceRecord(
                stop.TrainIndex,
                stop.StationOrder,
                isStop,
                out TrainTrackChoiceRecord? trackChoice) && trackChoice is not null)
        {
            foreach (string track in trackChoice.AllowedTrackNames)
            {
                result.Add($"track::{stop.StationName}::{track}");
            }
        }

        TrainRecord train = instance.Trains.First(item => item.TrainIndex == stop.TrainIndex);
        TrainStopRecord? nextStop = train.OrderedStops.FirstOrDefault(item =>
            item.StationOrder == stop.StationOrder + 1);
        string? sectionName = side == StationEventSide.Incoming
            ? stop.IncomingSectionName
            : nextStop?.IncomingSectionName;
        MovementEvent movement = isStop
            ? side == StationEventSide.Incoming ? MovementEvent.Arrival : MovementEvent.Departure
            : MovementEvent.Passing;
        if (!string.IsNullOrWhiteSpace(sectionName))
        {
            foreach (RouteRecord route in instance.Routes.Where(route =>
                         string.Equals(route.StationName, stop.StationName, StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(route.SectionName, sectionName, StringComparison.OrdinalIgnoreCase) &&
                         route.MovementEvent == movement && RouteEventDirectionPolicy.Matches(route, side == StationEventSide.Incoming)))
            {
                result.Add($"route::{route.RouteId}");
                result.Add($"track::{stop.StationName}::{route.TrackName}");
            }
        }
        return result;
    }

    private static void AddTrainTemporalRelations(
        TimetablingInstance instance,
        MasterSolutionSnapshot snapshot,
        IReadOnlyDictionary<string, ConflictPropagationEvent> eventById,
        ICollection<ConflictPropagationRelation> relations)
    {
        RunningTimeSupplementPolicy supplementPolicy = RunningTimeSupplementPolicy.Create(0.05);
        foreach (TrainRecord train in instance.Trains)
        {
            for (int index = 0; index < train.OrderedStops.Count; index++)
            {
                TrainStopRecord stop = train.OrderedStops[index];
                string arrivalId = BuildEventId(stop.StopId, StationEventSide.Incoming);
                string departureId = BuildEventId(stop.StopId, StationEventSide.Outgoing);
                if (eventById.ContainsKey(arrivalId) && eventById.ContainsKey(departureId))
                {
                    bool currentStop = snapshot.StopByStopId.TryGetValue(stop.StopId, out int stopValue) &&
                                       stopValue == 1;
                    NormalDwellResolution dwell = NormalDwellTimeResolver.Resolve(
                        instance.NormalDwellTimeRules,
                        stop);
                    int minimum = currentStop ? dwell.NormalMinDwellSeconds : 0;
                    int dwellMaximum = currentStop ? dwell.WaitingMaxDwellSeconds : 0;
                    ConflictPropagationRelationReliability reliability = stop.StopRequirement == StopRequirement.OptionalStop
                        ? ConflictPropagationRelationReliability.ConditionalCurrentChoice
                        : ConflictPropagationRelationReliability.ProvenCurrentBoundary;
                    string condition = stop.StopRequirement == StopRequirement.OptionalStop
                        ? $"current_is_stop={currentStop}"
                        : stop.StopRequirement.ToString();
                    relations.Add(new ConflictPropagationRelation(
                        $"dwell-lower::{stop.StopId}", arrivalId, departureId,
                        ConflictPropagationRelationType.TrainDwellLower,
                        ConflictPropagationBoundDirection.Lower,
                        minimum, reliability,
                        "同一列车到发事件的最小停时边界。",
                        "NormalDwellTimeResolver", condition));
                    relations.Add(new ConflictPropagationRelation(
                        $"dwell-upper::{stop.StopId}", arrivalId, departureId,
                        ConflictPropagationRelationType.TrainDwellUpper,
                        ConflictPropagationBoundDirection.Upper,
                        dwellMaximum, reliability,
                        "同一列车到发事件的最大停时边界。",
                        "OvertakingDwellConstraintBuilder", condition));
                }

                if (index + 1 >= train.OrderedStops.Count)
                {
                    continue;
                }
                TrainStopRecord next = train.OrderedStops[index + 1];
                string fromId = BuildEventId(stop.StopId, StationEventSide.Outgoing);
                string toId = BuildEventId(next.StopId, StationEventSide.Incoming);
                if (!eventById.ContainsKey(fromId) || !eventById.ContainsKey(toId))
                {
                    continue;
                }
                RunTimeRule rule = ResolveRunTimeRule(instance, stop, next);
                relations.Add(new ConflictPropagationRelation(
                    $"runtime-lower::{stop.StopId}->{next.StopId}", fromId, toId,
                    ConflictPropagationRelationType.TrainRuntimeLower,
                    ConflictPropagationBoundDirection.Lower,
                    rule.RunTimeSeconds,
                    ConflictPropagationRelationReliability.ProvenCurrentBoundary,
                    "相邻车站间无条件成立的基础运行时下界。",
                    "BasicMilpModelBuilder.run", "base-runtime"));
                int runtimeMaximum = rule.RunTimeSeconds + rule.StartAddSeconds + rule.StopAddSeconds +
                                     supplementPolicy.ResolveMaximumSeconds(rule.RunTimeSeconds);
                relations.Add(new ConflictPropagationRelation(
                    $"runtime-upper::{stop.StopId}->{next.StopId}", fromId, toId,
                    ConflictPropagationRelationType.TrainRuntimeUpper,
                    ConflictPropagationBoundDirection.Upper,
                    runtimeMaximum,
                    ConflictPropagationRelationReliability.ProvenCurrentBoundary,
                    "相邻车站间按最大起停附加和运行补时得到的安全上界。",
                    "BasicMilpModelBuilder.run", "maximum-legal-runtime"));
            }
        }
    }

    private static void AddCurrentRouteHeadways(
        TimetablingInstance instance,
        IReadOnlyList<VerifiedMicroAssignmentWitness> witnesses,
        IReadOnlyDictionary<string, ConflictPropagationEvent> eventById,
        ICollection<ConflictPropagationRelation> relations)
    {
        List<(ConflictPropagationEvent Event, int RouteId)> selectedEvents = [];
        foreach (VerifiedMicroAssignmentWitness witness in witnesses)
        {
            Add(witness, StationEventSide.Incoming, witness.IncomingRouteId);
            Add(witness, StationEventSide.Outgoing, witness.OutgoingRouteId);
        }
        foreach (IGrouping<string, (ConflictPropagationEvent Event, int RouteId)> stationGroup in
                 selectedEvents.GroupBy(item => item.Event.StationName, StringComparer.OrdinalIgnoreCase))
        {
            List<(ConflictPropagationEvent Event, int RouteId)> ordered = stationGroup
                .OrderBy(item => item.Event.CurrentTimeSeconds)
                .ThenBy(item => item.Event.EventId, StringComparer.Ordinal)
                .ToList();
            for (int index = 0; index + 1 < ordered.Count; index++)
            {
                (ConflictPropagationEvent leading, int leadingRoute) = ordered[index];
                (ConflictPropagationEvent following, int followingRoute) = ordered[index + 1];
                if (leading.DemandId == following.DemandId ||
                    !instance.TryGetRouteHeadwayRule(leadingRoute, followingRoute, out RouteHeadwayRule? rule) ||
                    rule is null)
                {
                    continue;
                }
                relations.Add(new ConflictPropagationRelation(
                    $"route-headway::{leading.EventId}->{following.EventId}::{leadingRoute}->{followingRoute}",
                    leading.EventId,
                    following.EventId,
                    ConflictPropagationRelationType.ExactHeadwayCurrentChoice,
                    ConflictPropagationBoundDirection.Lower,
                    rule.MinHeadwaySeconds,
                    ConflictPropagationRelationReliability.ConditionalCurrentChoice,
                    "当前已验证进路选择下适用的精确间隔。",
                    "RouteHeadwayRule", $"routes={leadingRoute}->{followingRoute}"));
            }
        }

        void Add(
            VerifiedMicroAssignmentWitness witness,
            StationEventSide side,
            int? routeId)
        {
            string eventId = BuildEventId(witness.RequirementId, side);
            if (routeId.HasValue && eventById.TryGetValue(eventId, out ConflictPropagationEvent? item))
            {
                selectedEvents.Add((item, routeId.Value));
            }
        }
    }

    private static void AddIndexedResourceAssociations(
        IReadOnlyList<ConflictPropagationEvent> events,
        ICollection<ConflictPropagationRelation> relations)
    {
        IEnumerable<(string Resource, ConflictPropagationEvent Event)> indexed =
            from item in events
            from resource in item.ResourceDomainKeys
            select (resource, item);
        foreach (IGrouping<string, (string Resource, ConflictPropagationEvent Event)> group in
                 indexed.GroupBy(item => item.Resource, StringComparer.OrdinalIgnoreCase))
        {
            List<ConflictPropagationEvent> ordered = group
                .Select(item => item.Event)
                .GroupBy(item => item.EventId, StringComparer.OrdinalIgnoreCase)
                .Select(items => items.First())
                .OrderBy(item => item.CurrentTimeSeconds)
                .ThenBy(item => item.EventId, StringComparer.Ordinal)
                .ToList();
            for (int index = 0; index + 1 < ordered.Count; index++)
            {
                ConflictPropagationEvent first = ordered[index];
                ConflictPropagationEvent second = ordered[index + 1];
                if (string.Equals(first.DemandId, second.DemandId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                relations.Add(new ConflictPropagationRelation(
                    $"resource::{group.Key}::{first.EventId}->{second.EventId}",
                    first.EventId,
                    second.EventId,
                    ConflictPropagationRelationType.PotentialResourceCompetition,
                    ConflictPropagationBoundDirection.Association,
                    0.0,
                    ConflictPropagationRelationReliability.PotentialDomainOverlap,
                    "两个事件的合法进路或股道域在同一资源索引中相交。",
                    "legal-domain-resource-index", group.Key));
            }
        }
    }

    private static List<ConflictPropagationSeed> BuildSeeds(
        MicroFeasibilityReport report,
        IReadOnlyDictionary<(int TrainIndex, int StationOrder), TrainStopRecord> stopByTrainOrder,
        IReadOnlyDictionary<string, ConflictPropagationEvent> eventById,
        IReadOnlySet<string>? additionalCoreRequirementIds)
    {
        List<ConflictPropagationSeed> seeds = [];
        foreach (MicroConflictExplanation conflict in report.Conflicts
                     .OrderBy(BuildConflictId, StringComparer.Ordinal))
        {
            string conflictId = BuildConflictId(conflict);
            if (string.Equals(conflict.ConflictKind, "exact_headway_infeasible", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryResolveConflictEvent(conflict.FirstTrainIndex, conflict.StationOrder,
                        conflict.FirstEventSide, stopByTrainOrder, eventById, out ConflictPropagationEvent? first) ||
                    !TryResolveConflictEvent(conflict.SecondTrainIndex,
                        conflict.SecondStationOrder ?? conflict.StationOrder,
                        conflict.SecondEventSide, stopByTrainOrder, eventById,
                        out ConflictPropagationEvent? second))
                {
                    continue;
                }
                double required = TryReadEvidenceNumber(conflict.Evidence, "required_gap") ??
                                  Math.Abs(conflict.EndSeconds - conflict.StartSeconds);
                ConflictPropagationEvent resolvedFirst = first!;
                ConflictPropagationEvent resolvedSecond = second!;
                double realized = TryReadEvidenceNumber(conflict.Evidence, "realized_gap") ??
                                  Math.Abs(resolvedSecond.CurrentTimeSeconds - resolvedFirst.CurrentTimeSeconds);
                double deficit = Math.Max(0.0, required - realized);
                HashSet<string> direct = new(
                    [resolvedFirst.DemandId, resolvedSecond.DemandId], StringComparer.OrdinalIgnoreCase);
                seeds.Add(new ConflictPropagationSeed(
                    conflictId, conflict.ConflictKind,
                    new Dictionary<string, double> { [resolvedSecond.EventId] = deficit },
                    direct, false, "delay-following", "exact-current-resource-choice"));
                seeds.Add(new ConflictPropagationSeed(
                    conflictId, conflict.ConflictKind,
                    new Dictionary<string, double> { [resolvedFirst.EventId] = -deficit },
                    direct, false, "advance-leading", "exact-current-resource-choice"));
                seeds.Add(new ConflictPropagationSeed(
                    conflictId, conflict.ConflictKind,
                    new Dictionary<string, double>
                    {
                        [resolvedFirst.EventId] = -deficit / 2.0,
                        [resolvedSecond.EventId] = deficit / 2.0
                    },
                    direct, false, "split-shift", "exact-current-resource-choice"));
                continue;
            }

            HashSet<string> assignmentGroup = new(StringComparer.OrdinalIgnoreCase);
            AddConflictDemand(conflict.FirstTrainIndex, conflict.StationOrder, assignmentGroup);
            AddConflictDemand(
                conflict.SecondTrainIndex,
                conflict.SecondStationOrder ?? conflict.StationOrder,
                assignmentGroup);
            foreach (string id in additionalCoreRequirementIds ?? new HashSet<string>())
            {
                assignmentGroup.Add(id);
            }
            if (assignmentGroup.Count > 0)
            {
                seeds.Add(new ConflictPropagationSeed(
                    conflictId,
                    conflict.ConflictKind,
                    new Dictionary<string, double>(),
                    assignmentGroup,
                    true,
                    "joint-assignment-group",
                    report.Status == MicroSolveStatus.Unknown
                        ? "unknown-diagnostic-group-not-minimal"
                        : "proven-station-subproblem-conflict-group-not-minimum-cardinality"));
            }

            void AddConflictDemand(int? trainIndex, int? order, ISet<string> output)
            {
                if (trainIndex.HasValue && order.HasValue &&
                    stopByTrainOrder.TryGetValue((trainIndex.Value, order.Value), out TrainStopRecord? stop))
                {
                    output.Add(stop.StopId);
                }
            }
        }
        return seeds;
    }

    private static bool TryResolveConflictEvent(
        int? trainIndex,
        int? stationOrder,
        StationEventSide? side,
        IReadOnlyDictionary<(int TrainIndex, int StationOrder), TrainStopRecord> stopByTrainOrder,
        IReadOnlyDictionary<string, ConflictPropagationEvent> eventById,
        out ConflictPropagationEvent? item)
    {
        item = null;
        if (!trainIndex.HasValue || !stationOrder.HasValue || !side.HasValue ||
            !stopByTrainOrder.TryGetValue((trainIndex.Value, stationOrder.Value), out TrainStopRecord? stop))
        {
            return false;
        }
        return eventById.TryGetValue(BuildEventId(stop.StopId, side.Value), out item);
    }

    private static RunTimeRule ResolveRunTimeRule(
        TimetablingInstance instance,
        TrainStopRecord current,
        TrainStopRecord next)
    {
        string sectionName = next.IncomingSectionName
            ?? throw new InvalidOperationException($"Stop '{next.StopId}' has no incoming section.");
        SectionRecord section = instance.SectionByName[sectionName];
        bool same = string.Equals(section.FromStation, current.StationName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(section.ToStation, next.StationName, StringComparison.OrdinalIgnoreCase);
        TravelDirection direction = same
            ? section.DirectionTag
            : section.DirectionTag switch
            {
                TravelDirection.Up => TravelDirection.Down,
                TravelDirection.Down => TravelDirection.Up,
                _ => TravelDirection.Unknown
            };
        return instance.RunTimeRules.First(rule =>
            string.Equals(rule.ScaleName, next.RunScaleName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(rule.SectionName, sectionName, StringComparison.OrdinalIgnoreCase) &&
            rule.Direction == direction);
    }

    private static double? TryReadEvidenceNumber(string evidence, string key)
    {
        string prefix = key + "=";
        string? token = evidence.Split([',', ';'], StringSplitOptions.TrimEntries)
            .FirstOrDefault(item => item.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return token is not null && double.TryParse(
            token[prefix.Length..], NumberStyles.Float,
            CultureInfo.InvariantCulture, out double value)
            ? value
            : null;
    }

    private static string BuildConflictId(MicroConflictExplanation conflict) =>
        !string.IsNullOrWhiteSpace(conflict.RootConflictKey)
            ? conflict.RootConflictKey
            : string.Join("::",
                conflict.ConflictKind,
                conflict.StationName,
                conflict.FirstTrainIndex?.ToString(CultureInfo.InvariantCulture) ?? "none",
                conflict.SecondTrainIndex?.ToString(CultureInfo.InvariantCulture) ?? "none",
                conflict.StationOrder?.ToString(CultureInfo.InvariantCulture) ?? "none",
                conflict.SecondStationOrder?.ToString(CultureInfo.InvariantCulture) ?? "none",
                conflict.FirstEventSide?.ToString() ?? "none",
                conflict.SecondEventSide?.ToString() ?? "none",
                conflict.ResourceKey);

    private static string BuildEventId(string demandId, StationEventSide side) =>
        $"{demandId}::{(side == StationEventSide.Incoming ? "arrival" : "departure")}";
}

/// <summary>
/// 将每次选择的传播证据逐行写出，便于把释放决定与实际模型分支和候选指纹对应起来。
/// </summary>
public static class ConflictPropagationSelectionReporter
{
    public static void Append(
        string outputDirectory,
        int iterationIndex,
        ConflictPropagationSelectionResult result)
    {
        Directory.CreateDirectory(outputDirectory);
        string path = Path.Combine(outputDirectory, "repair_neighborhood_selector_audit.csv");
        bool writeHeader = !File.Exists(path);
        using StreamWriter writer = new(path, append: true, new UTF8Encoding(false));
        if (writeHeader)
        {
            writer.WriteLine(
                "iteration,branch,selector,seed_conflict_id,event_id,demand_id,relation_type,slack,estimated_shift,selection_reason,selected,boundary_reason,fallback_reason,candidate_fingerprint,propagation_path,relation_source,relation_condition,relation_reliability");
        }
        foreach (ConflictPropagationSelectionAuditRow row in result.AuditRows)
        {
            writer.WriteLine(string.Join(',',
                iterationIndex.ToString(CultureInfo.InvariantCulture),
                Csv(row.Branch), Csv(row.Selector), Csv(row.SeedConflictId), Csv(row.EventId),
                Csv(row.DemandId), Csv(row.RelationType),
                row.SlackSeconds?.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty,
                row.EstimatedShiftSeconds.ToString("R", CultureInfo.InvariantCulture),
                Csv(row.SelectionReason), row.Selected ? "true" : "false",
                Csv(row.BoundaryReason), Csv(row.FallbackReason), Csv(row.CandidateFingerprint),
                Csv(row.PropagationPath), Csv(row.RelationSource), Csv(row.RelationCondition),
                Csv(row.RelationReliability)));
        }

        string summaryPath = Path.Combine(outputDirectory, "repair_neighborhood_selector_summary.csv");
        bool writeSummaryHeader = !File.Exists(summaryPath);
        using StreamWriter summary = new(summaryPath, append: true, new UTF8Encoding(false));
        if (writeSummaryHeader)
        {
            summary.WriteLine(
                "iteration,branch,selector,selected_count,event_count,relation_count,seed_count,analysis_seconds,fallback_reason,candidate_fingerprint");
        }
        ConflictPropagationSelectionAuditRow? first = result.AuditRows.FirstOrDefault();
        summary.WriteLine(string.Join(',',
            iterationIndex.ToString(CultureInfo.InvariantCulture),
            Csv(first?.Branch ?? string.Empty),
            Csv(first?.Selector ?? string.Empty),
            result.ReleasedRequirementIds.Count.ToString(CultureInfo.InvariantCulture),
            result.EventCount.ToString(CultureInfo.InvariantCulture),
            result.RelationCount.ToString(CultureInfo.InvariantCulture),
            result.SeedCount.ToString(CultureInfo.InvariantCulture),
            result.AnalysisSeconds.ToString("R", CultureInfo.InvariantCulture),
            Csv(result.FallbackReason),
            Csv(first?.CandidateFingerprint ?? string.Empty)));
    }

    private static string Csv(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) < 0
            ? value
            : $"\"{value.Replace("\"", "\"\"")}\"";
}
