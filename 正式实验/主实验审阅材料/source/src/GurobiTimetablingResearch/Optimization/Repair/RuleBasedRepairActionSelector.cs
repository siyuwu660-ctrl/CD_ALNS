// 规则式修复动作选择器把当前冲突、已建模的合法资源域和可靠微观见证整理为 T/R/O/回退动作。
// 它只改变一次修复中临时开放的决策与尝试顺序，不生成永久割，也不调用冲突传播范围选择器。
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

public enum RepairActionSelectorKind
{
    Legacy,
    RuleBased
}

public static class RepairActionSelectorArgumentParser
{
    public static RepairActionSelectorKind Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            string.Equals(value, "legacy", StringComparison.OrdinalIgnoreCase))
        {
            return RepairActionSelectorKind.Legacy;
        }
        if (string.Equals(value, "rule_based", StringComparison.OrdinalIgnoreCase))
        {
            return RepairActionSelectorKind.RuleBased;
        }
        throw new ArgumentException(
            $"Unknown repair-action selector '{value}'. Expected legacy or rule_based.");
    }
}

public enum RepairActionType
{
    TimeRetiming,
    ResourceTimeCoordination,
    EndpointOrderCoordination,
    StationOvertakingCoordination,
    LegacyFallback
}

public enum RepairActionApplicability
{
    Applicable,
    NotApplicable,
    Undecided
}

public enum RepairActionBudgetTier
{
    Short,
    FixedAssignmentRetiming,
    Long,
    Legacy
}

public sealed record RepairActionRequirementFact(
    string RequirementId,
    string StationName,
    int TrainIndex,
    bool HasReliableCurrentWitness,
    int LegalAlternativeCount,
    bool HasCompleteJointModel,
    bool MustStop,
    bool ForcePass,
    bool IsEndpointRequirement = false);

public sealed record RepairActionConflictSignal(
    string ConflictId,
    string ConflictKind,
    string StationName,
    IReadOnlySet<int> TrainIndices,
    IReadOnlySet<string> RequirementIds,
    double RequiredTimeShiftSeconds,
    double ReliableTimeFlexibilitySeconds,
    bool DirectResourceCompetition,
    bool FixedTimeResourceSwapProvenIneffective,
    bool CanAdjustEndpointOrder,
    bool CanAdjustStationOvertaking);

public sealed record RepairActionScaleEstimate(
    int InvolvedTrainCount,
    int ReleasedRequirementCount,
    int FixedRequirementCount,
    int EstimatedDiscreteChoiceCount,
    int EndpointOrderChangeBudget);

public sealed record RepairActionPlan(
    string PlanId,
    string CandidateFingerprint,
    RepairActionType ActionType,
    RepairActionApplicability Applicability,
    IReadOnlySet<string> TriggerConflictIds,
    IReadOnlySet<string> TriggerBusinessIds,
    IReadOnlySet<string> LegacyReleasedRequirementIds,
    IReadOnlySet<string> AddedRequirementIds,
    IReadOnlyDictionary<string, string> AddedRequirementReasons,
    IReadOnlyList<string> OpenedDecisions,
    IReadOnlyList<string> FixedDecisions,
    IReadOnlyList<string> ApplicabilityConditions,
    string DecisionReason,
    int EndpointOrderChangeBudget,
    string RepairReferenceFingerprint,
    RepairActionBudgetTier BudgetTier,
    double RequestedSolveSeconds,
    RepairActionScaleEstimate EstimatedScale,
    bool Selected,
    int? SelectedRank,
    bool CreatesPermanentFeedback,
    ValidityFirstRepairNeighborhoodPlan? ExecutablePlan,
    IReadOnlySet<string>? PlannedFixedRequirementIds = null,
    string MappedToPlanId = "");

public sealed record RepairActionSelectionRequest(
    RepairActionSelectorKind Selector,
    string CandidateFingerprint,
    string RepairReferenceFingerprint,
    IReadOnlyList<ValidityFirstRepairNeighborhoodPlan> LegacyPlans,
    IReadOnlyList<RepairActionRequirementFact> RequirementFacts,
    IReadOnlyList<RepairActionConflictSignal> ConflictSignals,
    double ShortSolveSeconds,
    double FixedAssignmentRetimingSeconds,
    double LongSolveSeconds);

public sealed record RepairActionSelectionResult(
    IReadOnlyList<RepairActionPlan> AllActionPlans,
    IReadOnlyList<ValidityFirstRepairNeighborhoodPlan> ExecutablePlans,
    double AnalysisSeconds,
    bool UsesPropagationAnalysis,
    string FallbackReason);

/// <summary>
/// 使用固定规则构造并排序时刻、资源和顺序动作。排序只依赖当前实例事实，平局按动作类型和稳定业务标识处理。
/// </summary>
public static class RuleBasedRepairActionSelector
{
    public static RepairActionSelectionResult Select(RepairActionSelectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);
        Stopwatch stopwatch = Stopwatch.StartNew();
        if (request.Selector == RepairActionSelectorKind.Legacy)
        {
            IReadOnlyList<RepairActionPlan> legacyAudits = request.LegacyPlans
                .Select((plan, index) => BuildLegacyAudit(request, plan, index + 1, true))
                .ToList();
            stopwatch.Stop();
            return new RepairActionSelectionResult(
                legacyAudits,
                request.LegacyPlans,
                stopwatch.Elapsed.TotalSeconds,
                false,
                string.Empty);
        }

        if (request.LegacyPlans.Count == 0)
        {
            stopwatch.Stop();
            return new RepairActionSelectionResult([], [], stopwatch.Elapsed.TotalSeconds, false,
                "legacy_plan_set_empty");
        }

        Dictionary<string, RepairActionRequirementFact> factById = request.RequirementFacts
            .GroupBy(item => item.RequirementId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Count() == 1
                    ? group.Single()
                    : throw new InvalidDataException(
                        $"Repair action requirement '{group.Key}' has multiple business owners."),
                StringComparer.OrdinalIgnoreCase);
        HashSet<string> triggerBusinessIds = request.ConflictSignals
            .SelectMany(item => item.RequirementIds)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> triggerConflictIds = request.ConflictSignals
            .Select(item => item.ConflictId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<int> triggerTrains = request.ConflictSignals
            .SelectMany(item => item.TrainIndices)
            .ToHashSet();
        int maximumLegacyReleaseCount = request.LegacyPlans
            .Max(plan => plan.ReleasedRequirementIds.Count);
        int maximumK = request.LegacyPlans.Max(plan => plan.EndpointOrderChangeBudget);
        double maximumRequestedSeconds = request.LegacyPlans.Max(plan => plan.RequestedSolveSeconds);
        ValidityFirstRepairNeighborhoodPlan firstSource = request.LegacyPlans[0];
        ValidityFirstRepairNeighborhoodPlan resourceSource = request.LegacyPlans.FirstOrDefault(plan =>
                plan.ReleasedRequirementIds.Count > 0 && !plan.ReleasesAllVerifiedAssignments)
            ?? request.LegacyPlans[^1];
        ValidityFirstRepairNeighborhoodPlan orderSource = request.LegacyPlans
                .Where(plan => plan.EndpointOrderChangeBudget > 0 && !plan.ReleasesAllVerifiedAssignments)
                .OrderByDescending(plan => plan.EndpointOrderChangeBudget)
                .ThenBy(plan => plan.NeighborhoodId, StringComparer.Ordinal)
                .FirstOrDefault()
            ?? request.LegacyPlans[^1];
        ValidityFirstRepairNeighborhoodPlan fallbackSource = request.LegacyPlans
                .LastOrDefault(plan => plan.ReleasesAllVerifiedAssignments)
            ?? request.LegacyPlans[^1];

        List<RepairActionPlan> candidates =
        [
            BuildTimeAction(request, firstSource, factById, triggerConflictIds, triggerBusinessIds,
                triggerTrains, maximumRequestedSeconds),
            BuildResourceAction(request, resourceSource, factById, triggerConflictIds,
                triggerBusinessIds, triggerTrains, maximumLegacyReleaseCount, maximumRequestedSeconds),
            BuildEndpointOrderAction(request, orderSource, factById, triggerConflictIds,
                triggerBusinessIds, triggerTrains, maximumLegacyReleaseCount, maximumK,
                maximumRequestedSeconds),
            BuildStationOvertakingAction(request, orderSource, factById, triggerConflictIds,
                triggerBusinessIds, triggerTrains, maximumLegacyReleaseCount, maximumRequestedSeconds),
            BuildLegacyAudit(request, fallbackSource, null, false)
        ];

        List<(RepairActionPlan Plan, double Score)> ranked = candidates
            .Where(plan => plan.ActionType != RepairActionType.LegacyFallback &&
                           plan.Applicability == RepairActionApplicability.Applicable &&
                           plan.ExecutablePlan is not null)
            .Select(plan => (Plan: plan, Score: Score(plan, request)))
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Plan.ActionType)
            .ThenBy(item => item.Plan.PlanId, StringComparer.Ordinal)
            .ToList();
        if (ranked.Count == 0)
        {
            IReadOnlyList<RepairActionPlan> fallbackAudits = request.LegacyPlans
                .Select((plan, index) => BuildLegacyAudit(request, plan, index + 1, false))
                .ToList();
            stopwatch.Stop();
            return new RepairActionSelectionResult(
                [.. candidates.Where(plan => plan.ActionType != RepairActionType.LegacyFallback),
                 .. fallbackAudits],
                fallbackAudits.Select(plan => plan.ExecutablePlan!).ToList(),
                stopwatch.Elapsed.TotalSeconds,
                false,
                "no_rule_based_action_proven_applicable");
        }
        // 规则动作只调整原分支之前的尝试顺序；完整 Legacy 分支表始终作为非递归后备追加。
        // 先用当前可靠见证、释放业务ID和K识别无新增变化的计划；正式尝试仍会记录实际固定、释放和模型指纹。
        IReadOnlyList<RepairActionPlan> completeLegacyFallback = request.LegacyPlans
            .Select((plan, index) => BuildLegacyAudit(request, plan, index + 1, false))
            .ToList();
        Dictionary<string, RepairActionPlan> legacyByMathematicalNeighborhood =
            completeLegacyFallback
                .GroupBy(PlannedMathematicalSignature, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.First(),
                    StringComparer.OrdinalIgnoreCase);
        List<RepairActionPlan> selected = [];
        Dictionary<string, RepairActionPlan> selectedByMathematicalNeighborhood =
            new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> mappedToPlanId = new(StringComparer.OrdinalIgnoreCase);
        foreach ((RepairActionPlan plan, _) in ranked)
        {
            string signature = PlannedMathematicalSignature(plan);
            if (legacyByMathematicalNeighborhood.TryGetValue(
                    signature,
                    out RepairActionPlan? legacyEquivalent) &&
                legacyEquivalent.RequestedSolveSeconds + 1e-9 >= plan.RequestedSolveSeconds)
            {
                // 规则动作没有增加模型自由度时，交回原 Legacy 分支，避免仅改动作标签就提前消耗预算。
                mappedToPlanId[plan.PlanId] = legacyEquivalent.PlanId;
                continue;
            }
            if (!selectedByMathematicalNeighborhood.TryGetValue(signature, out RepairActionPlan? equivalent))
            {
                selected.Add(plan);
                selectedByMathematicalNeighborhood.Add(signature, plan);
            }
            else if (equivalent.RequestedSolveSeconds + 1e-9 >= plan.RequestedSolveSeconds)
            {
                mappedToPlanId[plan.PlanId] = equivalent.PlanId;
            }
            else
            {
                selected.Add(plan);
                selectedByMathematicalNeighborhood[signature] = plan;
            }
        }

        foreach (RepairActionPlan fallback in completeLegacyFallback)
        {
            // LegacyFallback 是完整的原调度入口，始终按原顺序保留，不能被单个规则动作吞掉。
            selected.Add(fallback);
        }

        Dictionary<string, int> rankByPlanId = selected
            .Select((plan, index) => (plan.PlanId, Rank: index + 1))
            .ToDictionary(item => item.PlanId, item => item.Rank, StringComparer.OrdinalIgnoreCase);
        List<RepairActionPlan> audited = candidates
            .Where(plan => plan.ActionType != RepairActionType.LegacyFallback)
            .Concat(completeLegacyFallback)
            .Select(plan => rankByPlanId.TryGetValue(plan.PlanId, out int rank)
                ? plan with { Selected = true, SelectedRank = rank }
                : mappedToPlanId.TryGetValue(plan.PlanId, out string? mapped)
                    ? plan with { MappedToPlanId = mapped }
                    : plan)
            .ToList();
        IReadOnlyList<ValidityFirstRepairNeighborhoodPlan> executable = selected
            .Select(plan => plan.ExecutablePlan!)
            .ToList();
        stopwatch.Stop();
        return new RepairActionSelectionResult(
            audited,
            executable,
            stopwatch.Elapsed.TotalSeconds,
            false,
            "complete_legacy_fallback_appended");
    }

    private static RepairActionPlan BuildTimeAction(
        RepairActionSelectionRequest request,
        ValidityFirstRepairNeighborhoodPlan source,
        IReadOnlyDictionary<string, RepairActionRequirementFact> facts,
        IReadOnlySet<string> conflictIds,
        IReadOnlySet<string> businessIds,
        IReadOnlySet<int> trains,
        double maximumRequestedSeconds)
    {
        List<string> missing = businessIds
            .Where(id => !facts.TryGetValue(id, out RepairActionRequirementFact? fact) ||
                         !fact.HasCompleteJointModel || !fact.HasReliableCurrentWitness)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        RepairActionApplicability applicability = businessIds.Count == 0
            ? RepairActionApplicability.Undecided
            : missing.Count == 0
                ? RepairActionApplicability.Applicable
                : RepairActionApplicability.NotApplicable;
        string reason = businessIds.Count == 0
            ? "冲突尚未映射到当前精确模型中的业务需求，不能确认固定离散结构。"
            : missing.Count > 0
                ? $"当前候选缺少可靠且完整的离散见证：{string.Join('|', missing)}。"
                : "当前冲突涉及的离散选择具有当前候选见证，先固定选择并在原时间域内联合重定时。";
        double budget = Math.Min(maximumRequestedSeconds, request.FixedAssignmentRetimingSeconds);
        ValidityFirstRepairNeighborhoodPlan? executable = applicability == RepairActionApplicability.Applicable
            ? source with
            {
                NeighborhoodId = $"{source.NeighborhoodId}_action_t",
                EndpointOrderChangeBudget = 0,
                ReleasedRequirementIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                RequestedSolveSeconds = budget,
                ReleasesAllVerifiedAssignments = false,
                ActionType = RepairActionType.TimeRetiming,
                ActionPlanId = "T::fixed_discrete_retiming",
                ActionApplicability = applicability.ToString(),
                Reason = reason
            }
            : null;
        return new RepairActionPlan(
            "T::fixed_discrete_retiming", request.CandidateFingerprint,
            RepairActionType.TimeRetiming, applicability, conflictIds, businessIds,
            source.ReleasedRequirementIds, new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            ["arrival_departure_time"],
            ["stop_mode", "route", "track", "endpoint_order"],
            ["当前候选见证完整", "离散选择属于当前候选", "时刻保持原业务上下界"],
            reason, 0, request.RepairReferenceFingerprint,
            RepairActionBudgetTier.FixedAssignmentRetiming, budget,
            Estimate(trains, facts, new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0),
            false, null, false, executable,
            PlannedFixedRequirements(facts, new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
    }

    private static RepairActionPlan BuildResourceAction(
        RepairActionSelectionRequest request,
        ValidityFirstRepairNeighborhoodPlan source,
        IReadOnlyDictionary<string, RepairActionRequirementFact> facts,
        IReadOnlySet<string> conflictIds,
        IReadOnlySet<string> businessIds,
        IReadOnlySet<int> trains,
        int maximumLegacyReleaseCount,
        double maximumRequestedSeconds)
    {
        bool resourceEvidence = request.ConflictSignals.Any(signal => signal.DirectResourceCompetition);
        bool hasReliableTemporaryFixation = businessIds.Any(id =>
            facts.TryGetValue(id, out RepairActionRequirementFact? fact) &&
            fact.HasReliableCurrentWitness);
        List<string> missing = businessIds
            .Where(id => !facts.TryGetValue(id, out RepairActionRequirementFact? fact) ||
                         !fact.HasCompleteJointModel || fact.LegalAlternativeCount <= 0)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        HashSet<string> released = source.ReleasedRequirementIds
            .Concat(businessIds.Where(id =>
                facts.TryGetValue(id, out RepairActionRequirementFact? fact) &&
                fact.HasReliableCurrentWitness))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool exceedsExistingTier = maximumLegacyReleaseCount > 0 &&
                                   released.Count > maximumLegacyReleaseCount;
        RepairActionApplicability applicability = businessIds.Count == 0
            ? RepairActionApplicability.Undecided
            : !hasReliableTemporaryFixation || missing.Count > 0 || exceedsExistingTier
                ? RepairActionApplicability.NotApplicable
                : resourceEvidence
                    ? RepairActionApplicability.Applicable
                    : RepairActionApplicability.Undecided;
        string reason = !hasReliableTemporaryFixation
            ? "当前候选没有与冲突需求对应的可靠临时微观固定，R 不会产生实际释放，交回 Legacy 后备流程。"
            : missing.Count > 0
            ? $"相关需求缺少完整合法资源域：{string.Join('|', missing)}。"
            : exceedsExistingTier
                ? "动作依赖超过现有最大释放档位，未截断必要业务依赖。"
                : resourceEvidence
                    ? "存在直接资源竞争，联合开放合法进路、股道及必要时刻；固定时刻换路无效不会排除本动作。"
                    : "当前证据不足以确认资源竞争是主要阻塞。";
        HashSet<string> added = released
            .Except(source.ReleasedRequirementIds, StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> addedReasons = added.ToDictionary(
            id => id,
            _ => "该需求直接参与当前资源竞争，必须与原释放集合联合求解。",
            StringComparer.OrdinalIgnoreCase);
        double budget = Math.Min(maximumRequestedSeconds, source.RequestedSolveSeconds);
        ValidityFirstRepairNeighborhoodPlan? executable = applicability == RepairActionApplicability.Applicable
            ? source with
            {
                NeighborhoodId = $"{source.NeighborhoodId}_action_r",
                EndpointOrderChangeBudget = 0,
                ReleasedRequirementIds = released,
                RequestedSolveSeconds = budget,
                ReleasesAllVerifiedAssignments = false,
                ActionType = RepairActionType.ResourceTimeCoordination,
                ActionPlanId = "R::resource_time_coordination",
                ActionApplicability = applicability.ToString(),
                Reason = reason
            }
            : null;
        return new RepairActionPlan(
            "R::resource_time_coordination", request.CandidateFingerprint,
            RepairActionType.ResourceTimeCoordination, applicability, conflictIds, businessIds,
            source.ReleasedRequirementIds, added, addedReasons,
            ["route", "track", "arrival_departure_time"],
            ["business_stop_mode", "endpoint_order"],
            ["完整合法资源域", "多列车资源关系在同一模型中", "时刻允许在原业务范围内联动"],
            reason, 0, request.RepairReferenceFingerprint,
            ResolveBudgetTier(budget, request), budget, Estimate(trains, facts, released, 0),
            false, null, false, executable,
            PlannedFixedRequirements(facts, released));
    }

    private static RepairActionPlan BuildEndpointOrderAction(
        RepairActionSelectionRequest request,
        ValidityFirstRepairNeighborhoodPlan source,
        IReadOnlyDictionary<string, RepairActionRequirementFact> facts,
        IReadOnlySet<string> conflictIds,
        IReadOnlySet<string> businessIds,
        IReadOnlySet<int> trains,
        int maximumLegacyReleaseCount,
        int maximumK,
        double maximumRequestedSeconds)
    {
        bool legal = maximumK > 0 && request.ConflictSignals.Any(signal => signal.CanAdjustEndpointOrder);
        HashSet<string> endpointDependencies = facts.Values
            .Where(fact => fact.IsEndpointRequirement && trains.Contains(fact.TrainIndex))
            .Select(fact => fact.RequirementId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> released = source.ReleasedRequirementIds
            .Concat(endpointDependencies)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool exceeds = maximumLegacyReleaseCount > 0 && released.Count > maximumLegacyReleaseCount;
        RepairActionApplicability applicability = legal && !exceeds
            ? RepairActionApplicability.Applicable
            : maximumK == 0
                ? RepairActionApplicability.NotApplicable
                : RepairActionApplicability.Undecided;
        string reason = maximumK == 0
            ? "当前配置没有可用的首末端 K 档位。"
            : exceeds
                ? "首末端换序所需依赖超过现有释放档位，未静默截断。"
                : legal
                    ? "当前冲突存在合法首末端顺序调整条件，K 相对当前 repair reference 计算。"
                    : "当前冲突尚不能可靠映射到可调整的首末端顺序。";
        HashSet<string> added = released
            .Except(source.ReleasedRequirementIds, StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> addedReasons = added.ToDictionary(
            id => id,
            _ => "该端点业务决策是实现当前顺序变化所需的直接依赖。",
            StringComparer.OrdinalIgnoreCase);
        int k = Math.Min(maximumK, Math.Max(1, source.EndpointOrderChangeBudget));
        double budget = Math.Min(maximumRequestedSeconds, source.RequestedSolveSeconds);
        ValidityFirstRepairNeighborhoodPlan? executable = applicability == RepairActionApplicability.Applicable
            ? source with
            {
                NeighborhoodId = $"{source.NeighborhoodId}_action_o_endpoint",
                EndpointOrderChangeBudget = k,
                ReleasedRequirementIds = released,
                RequestedSolveSeconds = budget,
                ReleasesAllVerifiedAssignments = false,
                ActionType = RepairActionType.EndpointOrderCoordination,
                ActionPlanId = "O::endpoint_order_coordination",
                ActionApplicability = applicability.ToString(),
                Reason = reason
            }
            : null;
        return new RepairActionPlan(
            "O::endpoint_order_coordination", request.CandidateFingerprint,
            RepairActionType.EndpointOrderCoordination, applicability, conflictIds, businessIds,
            source.ReleasedRequirementIds, added, addedReasons,
            ["endpoint_order", "arrival_departure_time", "dependent_route_track"],
            ["MustStop", "ForcePass", "business_time_windows", "section_safety"],
            ["使用现有 K 档位", "K 相对当前 repair reference", "实际换序另行由候选审计"],
            reason, k, request.RepairReferenceFingerprint,
            ResolveBudgetTier(budget, request), budget, Estimate(trains, facts, released, k),
            false, null, false, executable,
            PlannedFixedRequirements(facts, released));
    }

    private static RepairActionPlan BuildStationOvertakingAction(
        RepairActionSelectionRequest request,
        ValidityFirstRepairNeighborhoodPlan source,
        IReadOnlyDictionary<string, RepairActionRequirementFact> facts,
        IReadOnlySet<string> conflictIds,
        IReadOnlySet<string> businessIds,
        IReadOnlySet<int> trains,
        int maximumLegacyReleaseCount,
        double maximumRequestedSeconds)
    {
        bool legal = request.ConflictSignals.Any(signal => signal.CanAdjustStationOvertaking);
        HashSet<string> stationDependencies = request.ConflictSignals
            .Where(signal => signal.CanAdjustStationOvertaking)
            .SelectMany(signal => facts.Values.Where(fact =>
                string.Equals(fact.StationName, signal.StationName, StringComparison.OrdinalIgnoreCase) &&
                (signal.TrainIndices.Contains(fact.TrainIndex) ||
                 signal.RequirementIds.Contains(fact.RequirementId))))
            .Select(fact => fact.RequirementId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> released = source.ReleasedRequirementIds
            .Concat(stationDependencies)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool complete = stationDependencies.Count > 0 && stationDependencies.All(id =>
            facts.TryGetValue(id, out RepairActionRequirementFact? fact) && fact.HasCompleteJointModel);
        bool exceeds = maximumLegacyReleaseCount > 0 && released.Count > maximumLegacyReleaseCount;
        RepairActionApplicability applicability = legal && complete && !exceeds
            ? RepairActionApplicability.Applicable
            : legal && (!complete || exceeds)
                ? RepairActionApplicability.NotApplicable
                : RepairActionApplicability.Undecided;
        string reason = !legal
            ? "当前冲突站没有已建模的合法站内越行条件。"
            : !complete
                ? "站内越行动作缺少必要的进路、股道或相关列车联合模型。"
                : exceeds
                    ? "站内越行依赖超过现有释放档位，未静默裁剪。"
                    : "站内越行与首末端换序分开处理，并联合开放实现该越行所需的站内选择和时刻。";
        HashSet<string> added = released
            .Except(source.ReleasedRequirementIds, StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> addedReasons = added.ToDictionary(
            id => id,
            _ => "该站内需求是实现合法越行所需的停站、进路或股道依赖。",
            StringComparer.OrdinalIgnoreCase);
        double budget = Math.Min(maximumRequestedSeconds, source.RequestedSolveSeconds);
        ValidityFirstRepairNeighborhoodPlan? executable = applicability == RepairActionApplicability.Applicable
            ? source with
            {
                NeighborhoodId = $"{source.NeighborhoodId}_action_o_station",
                EndpointOrderChangeBudget = 0,
                ReleasedRequirementIds = released,
                RequestedSolveSeconds = budget,
                ReleasesAllVerifiedAssignments = false,
                ActionType = RepairActionType.StationOvertakingCoordination,
                ActionPlanId = "O::station_overtaking_coordination",
                ActionApplicability = applicability.ToString(),
                Reason = reason
            }
            : null;
        return new RepairActionPlan(
            "O::station_overtaking_coordination", request.CandidateFingerprint,
            RepairActionType.StationOvertakingCoordination, applicability, conflictIds, businessIds,
            source.ReleasedRequirementIds, added, addedReasons,
            ["station_overtaking_order", "dependent_stop_route_track", "arrival_departure_time"],
            ["MustStop", "ForcePass", "special_track_rules", "section_safety"],
            ["车站允许越行", "必要业务依赖完整", "其他资源使用者仍由联合安全约束保护"],
            reason, 0, request.RepairReferenceFingerprint,
            ResolveBudgetTier(budget, request), budget, Estimate(trains, facts, released, 0),
            false, null, false, executable,
            PlannedFixedRequirements(facts, released));
    }

    private static RepairActionPlan BuildLegacyAudit(
        RepairActionSelectionRequest request,
        ValidityFirstRepairNeighborhoodPlan source,
        int? rank,
        bool preserveOriginalPlan)
    {
        IReadOnlySet<int> trains = request.ConflictSignals.SelectMany(item => item.TrainIndices).ToHashSet();
        ValidityFirstRepairNeighborhoodPlan executable = preserveOriginalPlan
            ? source
            : source with
            {
                ActionType = RepairActionType.LegacyFallback,
                ActionPlanId = $"LegacyFallback::{source.NeighborhoodId}",
                ActionApplicability = RepairActionApplicability.Applicable.ToString()
            };
        return new RepairActionPlan(
            $"LegacyFallback::{source.NeighborhoodId}", request.CandidateFingerprint,
            RepairActionType.LegacyFallback, RepairActionApplicability.Applicable,
            request.ConflictSignals.Select(item => item.ConflictId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            request.ConflictSignals.SelectMany(item => item.RequirementIds)
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            source.ReleasedRequirementIds, new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            ["legacy_defined_decisions"], ["unchanged_business_constraints"],
            ["原组合分支保持可用"], source.Reason,
            source.EndpointOrderChangeBudget, request.RepairReferenceFingerprint,
            RepairActionBudgetTier.Legacy, source.RequestedSolveSeconds,
            Estimate(trains, request.RequirementFacts.ToDictionary(
                    item => item.RequirementId, StringComparer.OrdinalIgnoreCase),
                source.ReleasedRequirementIds, source.EndpointOrderChangeBudget),
            rank.HasValue, rank, false, executable,
            PlannedFixedRequirements(
                request.RequirementFacts.ToDictionary(
                    item => item.RequirementId,
                    StringComparer.OrdinalIgnoreCase),
                source.ReleasedRequirementIds));
    }

    private static RepairActionScaleEstimate Estimate(
        IReadOnlySet<int> trains,
        IReadOnlyDictionary<string, RepairActionRequirementFact> facts,
        IReadOnlySet<string> released,
        int k)
    {
        int choices = released.Sum(id => facts.TryGetValue(id, out RepairActionRequirementFact? fact)
            ? fact.LegalAlternativeCount
            : 0);
        return new RepairActionScaleEstimate(
            trains.Count,
            released.Count,
            Math.Max(0, facts.Count - released.Count),
            choices,
            k);
    }

    private static RepairActionBudgetTier ResolveBudgetTier(
        double seconds,
        RepairActionSelectionRequest request)
    {
        if (seconds <= request.ShortSolveSeconds + 1e-9)
        {
            return RepairActionBudgetTier.Short;
        }
        if (seconds <= request.FixedAssignmentRetimingSeconds + 1e-9)
        {
            return RepairActionBudgetTier.FixedAssignmentRetiming;
        }
        return RepairActionBudgetTier.Long;
    }

    private static double Score(RepairActionPlan plan, RepairActionSelectionRequest request)
    {
        double largestShift = request.ConflictSignals.Count == 0
            ? 0.0
            : request.ConflictSignals.Max(item => item.RequiredTimeShiftSeconds);
        double largestFlexibility = request.ConflictSignals.Count == 0
            ? 0.0
            : request.ConflictSignals.Max(item => item.ReliableTimeFlexibilitySeconds);
        bool strongTimingRestriction = largestShift > largestFlexibility + 1e-6;
        int alternativeCount = request.RequirementFacts
            .Where(item => plan.TriggerBusinessIds.Contains(item.RequirementId))
            .Sum(item => Math.Max(0, item.LegalAlternativeCount - 1));
        return plan.ActionType switch
        {
            RepairActionType.TimeRetiming => strongTimingRestriction ? 220.0 : 500.0,
            RepairActionType.ResourceTimeCoordination =>
                300.0 +
                (request.ConflictSignals.Any(item => item.DirectResourceCompetition) ? 220.0 : 0.0) +
                Math.Min(50.0, alternativeCount),
            RepairActionType.EndpointOrderCoordination => 250.0 + (strongTimingRestriction ? 180.0 : 0.0),
            RepairActionType.StationOvertakingCoordination => 260.0 + (strongTimingRestriction ? 190.0 : 0.0),
            _ => 0.0
        };
    }

    private static IReadOnlySet<string> PlannedFixedRequirements(
        IReadOnlyDictionary<string, RepairActionRequirementFact> facts,
        IReadOnlySet<string> released) =>
        facts.Values
            .Where(fact => fact.HasReliableCurrentWitness && !released.Contains(fact.RequirementId))
            .Select(fact => fact.RequirementId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string PlannedMathematicalSignature(RepairActionPlan plan) =>
        string.Join(';',
            $"k={plan.EndpointOrderChangeBudget}",
            $"fixed={string.Join('|', (plan.PlannedFixedRequirementIds ?? new HashSet<string>())
                .Order(StringComparer.OrdinalIgnoreCase))}",
            $"fixed_verified_structure={plan.ActionType == RepairActionType.TimeRetiming}");

    private static void Validate(RepairActionSelectionRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CandidateFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RepairReferenceFingerprint);
        ArgumentNullException.ThrowIfNull(request.LegacyPlans);
        ArgumentNullException.ThrowIfNull(request.RequirementFacts);
        ArgumentNullException.ThrowIfNull(request.ConflictSignals);
        if (!double.IsFinite(request.ShortSolveSeconds) || request.ShortSolveSeconds <= 0.0 ||
            !double.IsFinite(request.FixedAssignmentRetimingSeconds) ||
            request.FixedAssignmentRetimingSeconds <= 0.0 ||
            !double.IsFinite(request.LongSolveSeconds) || request.LongSolveSeconds <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }
    }
}

/// <summary>
/// 从当前候选、冲突与已进入 Master 的热点组件提取动作选择所需事实；这里只做当前状态审计，不做传播扩展。
/// </summary>
public static class RepairActionSelectionContextBuilder
{
    private static readonly Regex RealizedGapPattern = new(
        @"realized_gap\s*=\s*(?<value>-?\d+(?:\.\d+)?)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex RequiredGapPattern = new(
        @"required_gap\s*=\s*(?<value>-?\d+(?:\.\d+)?)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static RepairActionSelectionRequest Build(
        RepairActionSelectorKind selector,
        TimetablingInstance instance,
        MasterSolutionSnapshot repairReference,
        MicroFeasibilityReport conflictReport,
        IReadOnlyList<VerifiedMicroAssignmentWitness> witnesses,
        IReadOnlyList<PromotedMicroComponent> activeComponents,
        IReadOnlyList<ValidityFirstRepairNeighborhoodPlan> legacyPlans,
        IReadOnlySet<string> temporarilyReleasedStationNames,
        bool allowCurrentAssignmentCandidate,
        double shortSolveSeconds,
        double fixedAssignmentRetimingSeconds,
        double longSolveSeconds)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(repairReference);
        ArgumentNullException.ThrowIfNull(conflictReport);
        ArgumentNullException.ThrowIfNull(witnesses);
        ArgumentNullException.ThrowIfNull(activeComponents);
        ArgumentNullException.ThrowIfNull(legacyPlans);
        ArgumentNullException.ThrowIfNull(temporarilyReleasedStationNames);
        string candidateFingerprint = SnapshotFingerprint.Calculate(repairReference);
        Dictionary<string, VerifiedMicroAssignmentWitness> currentWitnesses =
            RepairActionCurrentWitnessPolicy.SelectCurrent(
                    witnesses,
                    repairReference,
                    candidateFingerprint,
                    allowCurrentAssignmentCandidate)
                .Where(witness => !temporarilyReleasedStationNames.Contains(witness.StationName))
            .GroupBy(item => item.RequirementId, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.OrdinalIgnoreCase);
        Dictionary<string, PromotedStationRequirement> requirementById = activeComponents
            .Where(component => component.Active)
            .SelectMany(component => component.Requirements.Select(requirement =>
                (component.StationName, Requirement: requirement)))
            .GroupBy(item => item.Requirement.RequirementId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    List<(string StationName, PromotedStationRequirement Requirement)> owners = group.ToList();
                    PromotedStationRequirement first = owners[0].Requirement;
                    if (owners.Any(owner => owner.Requirement.TrainIndex != first.TrainIndex ||
                                            !string.Equals(owner.StationName, owners[0].StationName,
                                                StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new InvalidDataException(
                            $"Promoted requirement '{group.Key}' has inconsistent business owners.");
                    }
                    return new PromotedStationRequirement(
                        first.RequirementId,
                        first.TrainIndex,
                        first.StationOrder,
                        first.IsStop,
                        owners.SelectMany(owner => owner.Requirement.AllowedAlternatives)
                            .Distinct()
                            .ToList(),
                        owners.Any(owner => owner.Requirement.PreserveAllLegalStopModes));
                },
                StringComparer.OrdinalIgnoreCase);
        List<RepairActionRequirementFact> facts = requirementById.Values
            .Select(requirement =>
            {
                TrainStopRecord stop = instance.StopsById.TryGetValue(
                    requirement.RequirementId,
                    out TrainStopRecord? resolved)
                    ? resolved
                    : throw new InvalidDataException(
                        $"Promoted requirement '{requirement.RequirementId}' has no timetable stop owner.");
                return new RepairActionRequirementFact(
                    requirement.RequirementId,
                    stop.StationName,
                    requirement.TrainIndex,
                    currentWitnesses.ContainsKey(requirement.RequirementId),
                    requirement.AllowedAlternatives.Distinct().Count(),
                    requirement.AllowedAlternatives.Count > 0,
                    stop.StopRequirement == StopRequirement.MustStop,
                    stop.StopRequirement == StopRequirement.MustPass,
                    stop.StopRole is TrainStopRole.Origin or TrainStopRole.Destination);
            })
            .OrderBy(item => item.RequirementId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        List<RepairActionConflictSignal> signals = conflictReport.Conflicts
            .Select((conflict, index) => BuildSignal(
                conflict,
                index,
                instance,
                repairReference,
                facts,
                legacyPlans))
            .ToList();
        return new RepairActionSelectionRequest(
            selector,
            candidateFingerprint,
            candidateFingerprint,
            legacyPlans,
            facts,
            signals,
            shortSolveSeconds,
            fixedAssignmentRetimingSeconds,
            longSolveSeconds);
    }

    /// <summary>Build repair guidance from a station conflict and the affected trains' legal tracks.</summary>
    /// <param name="conflict">Micro conflict at a particular station.</param>
    /// <param name="index">Ordinal used to identify the conflict.</param>
    /// <param name="instance">Original stop and route domains.</param>
    /// <param name="snapshot">Current timetable with physical event times in seconds.</param>
    /// <param name="facts">Station resource facts for the current candidate.</param>
    /// <param name="legacyPlans">Existing neighborhood plans supplying release budgets.</param>
    /// <returns>The conflict signal used to select repair actions.</returns>
    private static RepairActionConflictSignal BuildSignal(
        MicroConflictExplanation conflict,
        int index,
        TimetablingInstance instance,
        MasterSolutionSnapshot snapshot,
        IReadOnlyList<RepairActionRequirementFact> facts,
        IReadOnlyList<ValidityFirstRepairNeighborhoodPlan> legacyPlans)
    {
        HashSet<int> trains = new();
        if (conflict.FirstTrainIndex.HasValue)
        {
            trains.Add(conflict.FirstTrainIndex.Value);
        }
        if (conflict.SecondTrainIndex.HasValue)
        {
            trains.Add(conflict.SecondTrainIndex.Value);
        }
        HashSet<string> requirements = facts
            .Where(fact => trains.Contains(fact.TrainIndex) &&
                           string.Equals(fact.StationName, conflict.StationName,
                               StringComparison.OrdinalIgnoreCase))
            .Select(fact => fact.RequirementId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        double requiredShift = ResolveRequiredShift(conflict);
        double reliableFlexibility = requirements
            .Select(id => ResolveTimeFlexibility(instance.StopsById[id], snapshot))
            .DefaultIfEmpty(0.0)
            .Max();
        bool assignmentConflict = string.Equals(
            conflict.ConflictKind,
            "station_route_track_assignment_infeasible",
            StringComparison.OrdinalIgnoreCase);
        bool directResourceCompetition = assignmentConflict ||
                                         (!string.IsNullOrWhiteSpace(conflict.ResourceKey) && trains.Count >= 2);
        bool fixedTimeSwapIneffective = conflict.Evidence.Contains(
            "fixed_time_resource_swap_ineffective",
            StringComparison.OrdinalIgnoreCase);
        bool endpointAllowed = trains.Count >= 2 &&
                               legacyPlans.Any(plan => plan.EndpointOrderChangeBudget > 0);
        TrainStopRecord[] affectedVisits = instance.TrainStops.Where(stop => trains.Contains(stop.TrainIndex) &&
            stop.StationName.Equals(conflict.StationName, StringComparison.OrdinalIgnoreCase)).ToArray();
        bool stationOvertakingAllowed = affectedVisits.Any(waiting => affectedVisits.Any(passing =>
            StationOvertakingResourcePolicy.CanOvertake(instance, waiting, passing)));
        string conflictId = string.Join("::",
            "conflict",
            conflict.ConflictKind,
            conflict.StationName,
            conflict.FirstTrainIndex?.ToString(CultureInfo.InvariantCulture) ?? "none",
            conflict.SecondTrainIndex?.ToString(CultureInfo.InvariantCulture) ?? "none",
            conflict.FirstEventSide?.ToString() ?? "none",
            conflict.SecondEventSide?.ToString() ?? "none",
            index.ToString(CultureInfo.InvariantCulture));
        return new RepairActionConflictSignal(
            conflictId,
            conflict.ConflictKind,
            conflict.StationName,
            trains,
            requirements,
            requiredShift,
            reliableFlexibility,
            directResourceCompetition,
            fixedTimeSwapIneffective,
            endpointAllowed,
            stationOvertakingAllowed);
    }

    private static double ResolveRequiredShift(MicroConflictExplanation conflict)
    {
        Match realized = RealizedGapPattern.Match(conflict.Evidence ?? string.Empty);
        Match required = RequiredGapPattern.Match(conflict.Evidence ?? string.Empty);
        if (realized.Success && required.Success &&
            double.TryParse(realized.Groups["value"].Value, NumberStyles.Float,
                CultureInfo.InvariantCulture, out double realizedGap) &&
            double.TryParse(required.Groups["value"].Value, NumberStyles.Float,
                CultureInfo.InvariantCulture, out double requiredGap))
        {
            return Math.Max(0.0, requiredGap - realizedGap);
        }
        return Math.Max(0.0, conflict.EndSeconds - conflict.StartSeconds);
    }

    private static double ResolveTimeFlexibility(
        TrainStopRecord stop,
        MasterSolutionSnapshot snapshot)
    {
        if (!snapshot.ArrivalSecondsByStopId.TryGetValue(stop.StopId, out double arrival) ||
            !snapshot.DepartureSecondsByStopId.TryGetValue(stop.StopId, out double departure))
        {
            return 0.0;
        }
        double arrivalFlexibility = Math.Max(
            arrival - stop.EarliestArrSeconds,
            stop.LatestArrSeconds - arrival);
        double departureFlexibility = Math.Max(
            departure - stop.EarliestDepSeconds,
            stop.LatestDepSeconds - departure);
        return Math.Max(0.0, Math.Max(arrivalFlexibility, departureFlexibility));
    }
}

/// <summary>
/// 只保留指纹匹配或到发时刻逐项匹配的当前见证，防止旧候选分配被当作本轮固定值。
/// </summary>
public static class RepairActionCurrentWitnessPolicy
{
    public static IReadOnlyList<VerifiedMicroAssignmentWitness> SelectCurrent(
        IReadOnlyList<VerifiedMicroAssignmentWitness> witnesses,
        MasterSolutionSnapshot snapshot,
        string? candidateFingerprint = null,
        bool allowCurrentAssignmentCandidate = false)
    {
        ArgumentNullException.ThrowIfNull(witnesses);
        ArgumentNullException.ThrowIfNull(snapshot);
        string fingerprint = string.IsNullOrWhiteSpace(candidateFingerprint)
            ? SnapshotFingerprint.Calculate(snapshot)
            : candidateFingerprint;
        return witnesses.Where(witness =>
        {
            bool reliableScope = string.Equals(
                                     witness.VerificationScope,
                                     "station_micro_validated",
                                     StringComparison.Ordinal) ||
                                 (allowCurrentAssignmentCandidate && string.Equals(
                                     witness.VerificationScope,
                                     "candidate_assignment_not_exact_headway_verified",
                                     StringComparison.Ordinal));
            if (!reliableScope)
            {
                return false;
            }
            if (!string.IsNullOrWhiteSpace(witness.CandidateSnapshotFingerprint) &&
                string.Equals(
                    witness.CandidateSnapshotFingerprint,
                    fingerprint,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return witness.ArrivalSeconds.HasValue && witness.DepartureSeconds.HasValue &&
                   snapshot.ArrivalSecondsByStopId.TryGetValue(
                       witness.RequirementId,
                       out double arrival) &&
                   snapshot.DepartureSecondsByStopId.TryGetValue(
                       witness.RequirementId,
                       out double departure) &&
                   Math.Abs(arrival - witness.ArrivalSeconds.Value) <= 1e-6 &&
                   Math.Abs(departure - witness.DepartureSeconds.Value) <= 1e-6;
        }).ToList();
    }
}

/// <summary>
/// 把当前微观见证转换为单选组件，供 T 动作固定股道、进路及由当前时刻确定的资源顺序。
/// </summary>
public static class RepairActionVerifiedStructureBuilder
{
    public static IReadOnlyList<PromotedMicroComponent> Build(
        IReadOnlyList<VerifiedMicroAssignmentWitness> currentWitnesses)
    {
        ArgumentNullException.ThrowIfNull(currentWitnesses);
        IReadOnlyList<string> duplicates = currentWitnesses
            .GroupBy(item => item.RequirementId, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (duplicates.Count > 0)
        {
            throw new InvalidDataException(
                $"Current repair witnesses are duplicated: {string.Join('|', duplicates)}");
        }
        return currentWitnesses
            .GroupBy(item => item.StationName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new PromotedMicroComponent(
                $"repair_action_t::{group.Key}",
                group.Key,
                group.Select(item => item.TrainIndex).Distinct().Order().ToList(),
                group.OrderBy(item => item.RequirementId, StringComparer.OrdinalIgnoreCase)
                    .Select(item => new PromotedStationRequirement(
                        item.RequirementId,
                        item.TrainIndex,
                        0,
                        item.IsStop,
                        [new PromotedRouteTrackAlternative(
                            item.TrackName,
                            item.IncomingRouteId,
                            item.OutgoingRouteId,
                            item.IsStop)],
                        false))
                    .ToList(),
                "temporary_rule_based_time_retiming",
                "current_verified_micro_witnesses",
                0,
                1,
                true))
            .ToList();
    }
}

/// <summary>
/// 把每个候选动作的适用性、依赖补齐、实际排序与规模估计写入独立审计表。
/// </summary>
public static class RepairActionSelectionReporter
{
    public static void Append(
        string outputDirectory,
        int iterationIndex,
        string phase,
        RepairActionSelectionResult selection)
    {
        Directory.CreateDirectory(outputDirectory);
        string path = Path.Combine(outputDirectory, "repair_action_selection.csv");
        bool writeHeader = !File.Exists(path);
        using StreamWriter writer = new(path, true, new UTF8Encoding(false));
        if (writeHeader)
        {
            writer.WriteLine(
                "iteration,phase,candidate_fingerprint,action_plan,action_type,applicability,selected," +
                "selected_rank,trigger_conflicts,trigger_business_ids,legacy_release_ids,added_requirement_ids," +
                "added_requirement_reasons,opened_decisions,fixed_decisions,conditions,decision_reason,k," +
                "repair_reference,budget_tier,requested_seconds,involved_trains,released_requirements," +
                "fixed_requirements,planned_fixed_requirement_ids,mapped_to_plan_id," +
                "estimated_discrete_choices,creates_permanent_feedback,analysis_seconds");
        }
        foreach (RepairActionPlan plan in selection.AllActionPlans)
        {
            writer.WriteLine(string.Join(',',
                iterationIndex.ToString(CultureInfo.InvariantCulture),
                Csv(phase),
                Csv(plan.CandidateFingerprint),
                Csv(plan.PlanId),
                plan.ActionType,
                plan.Applicability,
                plan.Selected,
                plan.SelectedRank?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                Csv(string.Join('|', plan.TriggerConflictIds.Order(StringComparer.OrdinalIgnoreCase))),
                Csv(string.Join('|', plan.TriggerBusinessIds.Order(StringComparer.OrdinalIgnoreCase))),
                Csv(string.Join('|', plan.LegacyReleasedRequirementIds.Order(StringComparer.OrdinalIgnoreCase))),
                Csv(string.Join('|', plan.AddedRequirementIds.Order(StringComparer.OrdinalIgnoreCase))),
                Csv(string.Join('|', plan.AddedRequirementReasons
                    .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(item => $"{item.Key}:{item.Value}"))),
                Csv(string.Join('|', plan.OpenedDecisions)),
                Csv(string.Join('|', plan.FixedDecisions)),
                Csv(string.Join('|', plan.ApplicabilityConditions)),
                Csv(plan.DecisionReason),
                plan.EndpointOrderChangeBudget.ToString(CultureInfo.InvariantCulture),
                Csv(plan.RepairReferenceFingerprint),
                plan.BudgetTier,
                plan.RequestedSolveSeconds.ToString("R", CultureInfo.InvariantCulture),
                plan.EstimatedScale.InvolvedTrainCount.ToString(CultureInfo.InvariantCulture),
                plan.EstimatedScale.ReleasedRequirementCount.ToString(CultureInfo.InvariantCulture),
                plan.EstimatedScale.FixedRequirementCount.ToString(CultureInfo.InvariantCulture),
                Csv(string.Join('|', (plan.PlannedFixedRequirementIds ?? new HashSet<string>())
                    .Order(StringComparer.OrdinalIgnoreCase))),
                Csv(plan.MappedToPlanId),
                plan.EstimatedScale.EstimatedDiscreteChoiceCount.ToString(CultureInfo.InvariantCulture),
                plan.CreatesPermanentFeedback,
                selection.AnalysisSeconds.ToString("R", CultureInfo.InvariantCulture)));
        }
    }

    private static string Csv(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) < 0
            ? value
            : $"\"{value.Replace("\"", "\"\"")}\"";
}
