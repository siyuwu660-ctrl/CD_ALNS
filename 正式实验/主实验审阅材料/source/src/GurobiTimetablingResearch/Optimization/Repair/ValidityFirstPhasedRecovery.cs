using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Gurobi;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

/// <summary>
/// 区分初始 Macro/可行后业务求优与 Micro 可行性恢复使用的目标类型。
/// </summary>
public enum ValidityFirstSolveObjectiveKind
{
    BusinessMultiObjective,
    FeasibilityRecovery
}

/// <summary>
/// 描述完整热点闭包进入 Master 后的预计规模，用于在构造前执行显式资源预算检查。
/// </summary>
public sealed record ValidityFirstRepairScopeEstimate(
    int RequirementCount,
    int AlternativeCount,
    int EstimatedVariableCount,
    int EstimatedConstraintCount,
    int ResourceConflictRelationCount,
    int OrderVariableCount = 0,
    int LegacyEstimatedVariableCount = 0,
    int LegacyOrderVariableCount = 0);

public sealed record ValidityFirstRepairScopeBudgetDecision(
    bool Allowed,
    string Status);

/// <summary>
/// 使用需求、备选方案、变量、约束和资源冲突关系五个上限控制热点规模；这些上限只属于算法预算，不是业务约束。
/// </summary>
public sealed record ValidityFirstRepairScopeBudget(
    int MaximumRequirementCount,
    int MaximumAlternativeCount,
    int MaximumEstimatedVariableCount,
    int MaximumEstimatedConstraintCount,
    int MaximumResourceConflictRelationCount)
{
    public static ValidityFirstRepairScopeBudget FromOptions(CheckerLoopOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new ValidityFirstRepairScopeBudget(
            options.ValidityFirstRecoveryHotspotMaximumRequirementCount,
            options.ValidityFirstRecoveryHotspotMaximumAlternativeCount,
            options.ValidityFirstRecoveryHotspotMaximumEstimatedVariableCount,
            options.ValidityFirstRecoveryHotspotMaximumEstimatedConstraintCount,
            options.ValidityFirstRecoveryHotspotMaximumConflictRelationCount);
    }

    public ValidityFirstRepairScopeBudgetDecision Evaluate(ValidityFirstRepairScopeEstimate estimate)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        bool allowed = estimate.RequirementCount <= MaximumRequirementCount &&
                       estimate.AlternativeCount <= MaximumAlternativeCount &&
                       estimate.EstimatedVariableCount <= MaximumEstimatedVariableCount &&
                       estimate.EstimatedConstraintCount <= MaximumEstimatedConstraintCount &&
                       estimate.ResourceConflictRelationCount <= MaximumResourceConflictRelationCount;
        string scale = string.Join(';',
            $"requirements={estimate.RequirementCount}/{MaximumRequirementCount}",
            $"alternatives={estimate.AlternativeCount}/{MaximumAlternativeCount}",
            $"estimated_variables={estimate.EstimatedVariableCount}/{MaximumEstimatedVariableCount}",
            $"estimated_constraints={estimate.EstimatedConstraintCount}/{MaximumEstimatedConstraintCount}",
            $"resource_relations={estimate.ResourceConflictRelationCount}/{MaximumResourceConflictRelationCount}");
        return new ValidityFirstRepairScopeBudgetDecision(
            allowed,
            allowed ? $"complete::{scale}" : $"repair_scope_budget_exhausted::{scale}");
    }
}

/// <summary>
/// 把修复参考、有效反馈版本、完整闭包和求解参数合成为稳定键，避免无信息增益地重复失败试验。
/// </summary>
public static class ValidityFirstRepairAttemptKeyBuilder
{
    public static string Build(
        string referenceFingerprint,
        IReadOnlyList<string> validFeedbackIds,
        string scopeFingerprint,
        ValidityFirstSolveParameterPlan solvePlan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(referenceFingerprint);
        ArgumentNullException.ThrowIfNull(validFeedbackIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeFingerprint);
        ArgumentNullException.ThrowIfNull(solvePlan);
        string canonical = string.Join('|',
            referenceFingerprint,
            string.Join(';', validFeedbackIds.Order(StringComparer.OrdinalIgnoreCase)),
            scopeFingerprint,
            solvePlan.ObjectiveKind,
            solvePlan.MipFocus.ToString(CultureInfo.InvariantCulture),
            solvePlan.MipGap.ToString("R", CultureInfo.InvariantCulture),
            solvePlan.SolutionLimit?.ToString(CultureInfo.InvariantCulture) ?? "default");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}

public sealed class ValidityFirstRepairAttemptRegistry
{
    private readonly HashSet<string> _failedAttemptKeys = new(StringComparer.Ordinal);

    public bool ShouldAttempt(string attemptKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attemptKey);
        return !_failedAttemptKeys.Contains(attemptKey);
    }

    public void RecordFailure(string attemptKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attemptKey);
        _failedAttemptKeys.Add(attemptKey);
    }
}

/// <summary>
/// 只依据是否存在可用解决定是否进入外部验证，使 SolutionLimit 和 TimeLimit 的 incumbent 都不会被丢弃。
/// </summary>
public static class ValidityFirstIncumbentValidationPolicy
{
    public static bool ShouldValidate(int statusCode, int solutionCount)
    {
        _ = statusCode;
        return solutionCount > 0;
    }
}

/// <summary>
/// 阶段2只在前一个范围已被证明不可行时扩大首末端换序预算；Unknown 不提供扩大范围的证明。
/// </summary>
public static class ValidityFirstEndpointOrderReleasePolicy
{
    public static IReadOnlyList<int> Budgets { get; } = [0, 1, 2, 4];

    public static int? ResolveNextBudget(int currentBudget, int statusCode, int solutionCount)
    {
        if (solutionCount > 0 || statusCode != GRB.Status.INFEASIBLE)
        {
            return null;
        }
        int currentIndex = Enumerable.Range(0, Budgets.Count)
            .FirstOrDefault(index => Budgets[index] == currentBudget, -1);
        return currentIndex >= 0 && currentIndex + 1 < Budgets.Count
            ? Budgets[currentIndex + 1]
            : null;
    }
}

public enum ValidityFirstNeighborhoodTransitionKind
{
    InitialRestricted,
    ProvenInfeasibilityRelease,
    HeuristicConflictLinkedSwitch,
    FullReleaseFallback
}

public sealed record ValidityFirstRepairNeighborhoodPlan(
    string NeighborhoodId,
    int EndpointOrderChangeBudget,
    IReadOnlySet<string> ReleasedRequirementIds,
    double RequestedSolveSeconds,
    ValidityFirstNeighborhoodTransitionKind TransitionKind,
    string Reason,
    bool ReleasesAllVerifiedAssignments,
    RepairActionType ActionType = RepairActionType.LegacyFallback,
    string ActionPlanId = "",
    string ActionApplicability = "legacy");

/// <summary>
/// 保存一次已构造局部邻域的稳定业务标识，使后续恢复可以重建同一组临时释放，
/// 而不是根据数量重新挑选对象。
/// </summary>
public sealed record ValidityFirstSavedRepairNeighborhood(
    string NeighborhoodId,
    int EndpointOrderChangeBudget,
    IReadOnlySet<string> ReleasedRequirementIds,
    double SourceRequestedSolveSeconds = 60.0);

public static class ValidityFirstSavedRepairNeighborhoodLoader
{
    public static ValidityFirstSavedRepairNeighborhood Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement fixation = document.RootElement.GetProperty("temporaryMicroFixations");
        string neighborhoodId = fixation.GetProperty("neighborhoodId").GetString()
            ?? throw new InvalidDataException("Saved repair neighborhood has no neighborhoodId.");
        int endpointOrderChangeBudget = fixation.GetProperty("endpointOrderChangeBudget").GetInt32();
        double sourceRequestedSolveSeconds = fixation.TryGetProperty(
            "requestedSolveSeconds",
            out JsonElement requestedSolveSecondsElement)
            ? requestedSolveSecondsElement.GetDouble()
            : 60.0;
        HashSet<string> releasedRequirementIds = fixation
            .GetProperty("releasedRequirementIds")
            .EnumerateArray()
            .Select(item => item.GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (endpointOrderChangeBudget < 0 || releasedRequirementIds.Count == 0 ||
            !double.IsFinite(sourceRequestedSolveSeconds) || sourceRequestedSolveSeconds <= 0.0)
        {
            throw new InvalidDataException(
                "Saved repair neighborhood must contain a non-negative K and at least one released business ID.");
        }
        return new ValidityFirstSavedRepairNeighborhood(
            neighborhoodId,
            endpointOrderChangeBudget,
            releasedRequirementIds,
            sourceRequestedSolveSeconds);
    }
}

/// <summary>
/// 按“核心列车、与核心直接冲突的列车、全部见证”逐级释放临时微观固定，
/// 同时按 K=0、1、2、4 放开首末端顺序。每一级都是独立的临时搜索邻域，
/// 不生成永久割，也不把超时解释成原问题不可行。
/// </summary>
public static class ValidityFirstRepairNeighborhoodPlanner
{
    /// <summary>
    /// 为已经进入 Master 的热点组件生成三层有界恢复邻域：先固定可靠离散分配只调整时刻，
    /// 再释放当前冲突列车的跨站分配，最后释放热点站分配并扩大首末端顺序邻域。
    /// 每层只改变临时上下界和顺序信赖域，不生成永久反馈，也不改变业务安全约束。
    /// </summary>
    public static IReadOnlyList<ValidityFirstRepairNeighborhoodPlan> BuildHotspotExactificationRecovery(
        IReadOnlyList<VerifiedMicroAssignmentWitness>? witnesses,
        string hotspotStationName,
        IReadOnlySet<int> currentConflictTrainIndices,
        IReadOnlySet<int> fallbackRelatedTrainIndices,
        bool hasCompleteHotspotCandidate,
        double fixedAssignmentRetimingSeconds,
        double conflictLinkedRetimingSeconds,
        double fallbackRetimingSeconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hotspotStationName);
        ArgumentNullException.ThrowIfNull(currentConflictTrainIndices);
        ArgumentNullException.ThrowIfNull(fallbackRelatedTrainIndices);
        if (!double.IsFinite(fixedAssignmentRetimingSeconds) || fixedAssignmentRetimingSeconds <= 0.0 ||
            !double.IsFinite(conflictLinkedRetimingSeconds) || conflictLinkedRetimingSeconds <= 0.0 ||
            !double.IsFinite(fallbackRetimingSeconds) || fallbackRetimingSeconds <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(fixedAssignmentRetimingSeconds));
        }

        IReadOnlyList<VerifiedMicroAssignmentWitness> available = witnesses ?? [];
        HashSet<string> conflictTrainRequirements = available
            .Where(witness => currentConflictTrainIndices.Contains(witness.TrainIndex))
            .Select(witness => witness.RequirementId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> hotspotAndConflictRequirements = available
            .Where(witness =>
                fallbackRelatedTrainIndices.Contains(witness.TrainIndex) ||
                string.Equals(
                    witness.StationName,
                    hotspotStationName,
                    StringComparison.OrdinalIgnoreCase))
            .Select(witness => witness.RequirementId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        List<ValidityFirstRepairNeighborhoodPlan> plans = [];
        if (hasCompleteHotspotCandidate)
        {
            plans.Add(new ValidityFirstRepairNeighborhoodPlan(
                "hotspot_fixed_assignments_retiming_k0",
                0,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                fixedAssignmentRetimingSeconds,
                ValidityFirstNeighborhoodTransitionKind.InitialRestricted,
                "固定当前时刻下重新验证或求得的离散分配，保留原业务时间范围，只联合调整时刻。",
                false));
        }

        plans.Add(new ValidityFirstRepairNeighborhoodPlan(
            "hotspot_release_conflict_trains_k2",
            2,
            conflictTrainRequirements,
            conflictLinkedRetimingSeconds,
            ValidityFirstNeighborhoodTransitionKind.HeuristicConflictLinkedSwitch,
            "释放当前冲突列车关联的进路、股道和模式选择，并相对当前修复参考允许至多两条首末端顺序变化。",
            false));
        plans.Add(new ValidityFirstRepairNeighborhoodPlan(
            "hotspot_release_station_and_conflict_trains_k4",
            4,
            hotspotAndConflictRequirements,
            fallbackRetimingSeconds,
            ValidityFirstNeighborhoodTransitionKind.HeuristicConflictLinkedSwitch,
            "后备邻域释放热点站的候选分配和冲突列车的跨站分配，同时保留其他站的可靠离散选择。",
            false));

        return DistinctByMathematicalNeighborhood(plans);
    }

    /// <summary>
    /// 恢复运行先重放已保存的局部释放集合，再尝试当前全部冲突列车形成的不同局部集合；
    /// 全释放只保留为最后的短预算后备分支。
    /// </summary>
    public static IReadOnlyList<ValidityFirstRepairNeighborhoodPlan> BuildPrioritizedContinuation(
        IReadOnlyList<VerifiedMicroAssignmentWitness>? witnesses,
        ValidityFirstSavedRepairNeighborhood savedNeighborhood,
        IReadOnlySet<int> currentConflictTrainIndices,
        double longLocalTimeLimitSeconds,
        double fullReleaseFallbackTimeLimitSeconds)
    {
        ArgumentNullException.ThrowIfNull(savedNeighborhood);
        ArgumentNullException.ThrowIfNull(currentConflictTrainIndices);
        if (!double.IsFinite(longLocalTimeLimitSeconds) || longLocalTimeLimitSeconds <= 0.0 ||
            !double.IsFinite(fullReleaseFallbackTimeLimitSeconds) || fullReleaseFallbackTimeLimitSeconds <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(longLocalTimeLimitSeconds));
        }

        IReadOnlyList<VerifiedMicroAssignmentWitness> reliable = witnesses ?? [];
        HashSet<string> availableRequirementIds = reliable
            .Select(witness => witness.RequirementId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<string> missingSavedIds = savedNeighborhood.ReleasedRequirementIds
            .Where(id => !availableRequirementIds.Contains(id))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (missingSavedIds.Count > 0)
        {
            throw new InvalidDataException(
                $"Saved repair neighborhood references missing business IDs: {string.Join('|', missingSavedIds)}");
        }

        HashSet<string> currentConflictRequirements = reliable
            .Where(witness => currentConflictTrainIndices.Contains(witness.TrainIndex))
            .Select(witness => witness.RequirementId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> allRequirements = reliable
            .Select(witness => witness.RequirementId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<ValidityFirstRepairNeighborhoodPlan> candidates =
        [
            new(
                "replay_saved_release_correlated_k4",
                savedNeighborhood.EndpointOrderChangeBudget,
                savedNeighborhood.ReleasedRequirementIds,
                longLocalTimeLimitSeconds,
                ValidityFirstNeighborhoodTransitionKind.InitialRestricted,
                "按保存的稳定业务ID重放上一轮释放集合，并相对当前修复参考保留原K邻域。",
                false),
            new(
                "release_current_conflict_trains_k4",
                savedNeighborhood.EndpointOrderChangeBudget,
                currentConflictRequirements,
                longLocalTimeLimitSeconds,
                ValidityFirstNeighborhoodTransitionKind.HeuristicConflictLinkedSwitch,
                "释放当前全部冲突列车在已验证站的离散选择，以改变第二分支的冲突相关性。",
                false),
            new(
                "release_all_verified_k4",
                savedNeighborhood.EndpointOrderChangeBudget,
                allRequirements,
                fullReleaseFallbackTimeLimitSeconds,
                ValidityFirstNeighborhoodTransitionKind.FullReleaseFallback,
                "剩余预算内的短后备分支：释放全部临时微观固定，但保留业务和安全约束。",
                true)
        ];
        return DistinctByMathematicalNeighborhood(candidates);
    }

    public static IReadOnlyList<ValidityFirstRepairNeighborhoodPlan> Build(
        IReadOnlyList<VerifiedMicroAssignmentWitness>? witnesses,
        IReadOnlySet<int> coreTrainIndices,
        IReadOnlySet<int> directlyRelatedTrainIndices,
        double localTimeLimitSeconds,
        double fullTimeLimitSeconds)
    {
        ArgumentNullException.ThrowIfNull(coreTrainIndices);
        ArgumentNullException.ThrowIfNull(directlyRelatedTrainIndices);
        if (!double.IsFinite(localTimeLimitSeconds) || localTimeLimitSeconds <= 0.0 ||
            !double.IsFinite(fullTimeLimitSeconds) || fullTimeLimitSeconds <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(localTimeLimitSeconds));
        }

        IReadOnlyList<VerifiedMicroAssignmentWitness> reliable = witnesses ?? [];
        HashSet<string> coreRequirements = reliable
            .Where(witness => coreTrainIndices.Contains(witness.TrainIndex))
            .Select(witness => witness.RequirementId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<int> correlatedTrains = coreTrainIndices
            .Concat(directlyRelatedTrainIndices)
            .ToHashSet();
        HashSet<string> correlatedRequirements = reliable
            .Where(witness => correlatedTrains.Contains(witness.TrainIndex))
            .Select(witness => witness.RequirementId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> allRequirements = reliable
            .Select(witness => witness.RequirementId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        List<ValidityFirstRepairNeighborhoodPlan> plans =
        [
            new(
                "fixed_verified_k0", 0,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                localTimeLimitSeconds,
                ValidityFirstNeighborhoodTransitionKind.InitialRestricted,
                "固定已验证的非热点离散选择，先检查最小 K=0 联合邻域。",
                false),
            new(
                "release_core_trains_k0", 0, coreRequirements,
                localTimeLimitSeconds,
                ValidityFirstNeighborhoodTransitionKind.HeuristicConflictLinkedSwitch,
                "释放热点核心列车在其他已验证站的进路、股道和模式选择。",
                false),
            new(
                "release_core_trains_k1", 1, coreRequirements,
                localTimeLimitSeconds,
                ValidityFirstNeighborhoodTransitionKind.HeuristicConflictLinkedSwitch,
                "保留不相关微观见证，并允许相关首末端顺序最多变化 1 条。",
                false),
            new(
                "release_correlated_trains_k2", 2, correlatedRequirements,
                localTimeLimitSeconds,
                ValidityFirstNeighborhoodTransitionKind.HeuristicConflictLinkedSwitch,
                "释放核心及直接冲突列车的跨站离散选择，并允许 K=2。",
                false),
            new(
                "release_correlated_trains_k4", 4, correlatedRequirements,
                localTimeLimitSeconds,
                ValidityFirstNeighborhoodTransitionKind.HeuristicConflictLinkedSwitch,
                "保持冲突关联释放组不变，将相关首末端顺序邻域扩大到 K=4。",
                false),
            new(
                "release_all_verified_k4", 4, allRequirements,
                fullTimeLimitSeconds,
                ValidityFirstNeighborhoodTransitionKind.FullReleaseFallback,
                "预算内的后备分支：释放全部临时微观固定，但继续保留真实业务和安全约束。",
                true)
        ];

        return DistinctByMathematicalNeighborhood(plans);
    }

    private static IReadOnlyList<ValidityFirstRepairNeighborhoodPlan> DistinctByMathematicalNeighborhood(
        IEnumerable<ValidityFirstRepairNeighborhoodPlan> plans)
    {
        List<ValidityFirstRepairNeighborhoodPlan> distinct = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (ValidityFirstRepairNeighborhoodPlan plan in plans)
        {
            string key = $"{plan.EndpointOrderChangeBudget}|" +
                         string.Join(';', plan.ReleasedRequirementIds.Order(StringComparer.OrdinalIgnoreCase));
            if (seen.Add(key))
            {
                distinct.Add(plan);
            }
        }
        return distinct;
    }
}

public static class ValidityFirstCumulativeRuntimePolicy
{
    public static double ResolveElapsedSeconds(
        double previouslyConsumedSeconds,
        double currentProcessElapsedSeconds)
    {
        if (!double.IsFinite(previouslyConsumedSeconds) || previouslyConsumedSeconds < 0.0 ||
            !double.IsFinite(currentProcessElapsedSeconds) || currentProcessElapsedSeconds < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(previouslyConsumedSeconds));
        }
        return previouslyConsumedSeconds + currentProcessElapsedSeconds;
    }
}

public static class ValidityFirstSavedNeighborhoodReplayReporter
{
    public static void Write(
        string outputDirectory,
        string sourcePath,
        string repairReferenceFingerprint,
        string hotspotStationName,
        ValidityFirstSavedRepairNeighborhood savedNeighborhood,
        int availableWitnessCount,
        double newRequestedSeconds)
    {
        Directory.CreateDirectory(outputDirectory);
        string path = Path.Combine(outputDirectory, "validity_first_saved_neighborhood_replay.csv");
        using StreamWriter writer = new(path, false, new UTF8Encoding(false));
        writer.WriteLine(
            "source_path,repair_reference_fingerprint,hotspot_station,source_neighborhood," +
            "released_business_id_count,available_witness_count,k,source_requested_seconds,new_requested_seconds," +
            "local_neighborhood_difference");
        writer.WriteLine(string.Join(',',
            Csv(sourcePath),
            repairReferenceFingerprint,
            Csv(hotspotStationName),
            Csv(savedNeighborhood.NeighborhoodId),
            savedNeighborhood.ReleasedRequirementIds.Count.ToString(CultureInfo.InvariantCulture),
            availableWitnessCount.ToString(CultureInfo.InvariantCulture),
            savedNeighborhood.EndpointOrderChangeBudget.ToString(CultureInfo.InvariantCulture),
            savedNeighborhood.SourceRequestedSolveSeconds.ToString("R", CultureInfo.InvariantCulture),
            newRequestedSeconds.ToString("R", CultureInfo.InvariantCulture),
            "solve_budget_only"));
    }

    private static string Csv(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) < 0
            ? value
            : $"\"{value.Replace("\"", "\"\"")}\"";
}

public static class ValidityFirstNeighborhoodTransitionPolicy
{
    public static ValidityFirstNeighborhoodTransitionKind Resolve(
        int previousStatusCode,
        int previousSolutionCount,
        bool reliableIisMembers,
        bool fullReleaseFallback)
    {
        if (fullReleaseFallback)
        {
            return ValidityFirstNeighborhoodTransitionKind.FullReleaseFallback;
        }
        if (previousStatusCode == GRB.Status.INFEASIBLE &&
            previousSolutionCount == 0 && reliableIisMembers)
        {
            return ValidityFirstNeighborhoodTransitionKind.ProvenInfeasibilityRelease;
        }
        return ValidityFirstNeighborhoodTransitionKind.HeuristicConflictLinkedSwitch;
    }

    public static bool CanTryNext(int previousStatusCode, int previousSolutionCount) =>
        previousSolutionCount == 0 &&
        (previousStatusCode == GRB.Status.INFEASIBLE ||
         previousStatusCode == GRB.Status.TIME_LIMIT);
}

public static class ValidityFirstRepairNeighborhoodReporter
{
    public static void Append(
        string outputDirectory,
        int iterationIndex,
        int attemptIndex,
        ValidityFirstRepairNeighborhoodPlan plan,
        ValidityFirstNeighborhoodTransitionKind actualTransition,
        VerifiedMicroAssignmentTemporaryFixResult fixation,
        bool solveStarted,
        string status,
        int solutionCount,
        double? solveRuntimeSeconds,
        ValidityFirstIisDiagnostic? iis)
    {
        Directory.CreateDirectory(outputDirectory);
        string path = Path.Combine(outputDirectory, "validity_first_repair_neighborhood_attempts.csv");
        bool header = !File.Exists(path);
        using StreamWriter writer = new(path, true, new UTF8Encoding(false));
        if (header)
        {
            writer.WriteLine(
                "iteration,attempt,neighborhood,action_type,action_plan_id,action_applicability," +
                "transition,reason,k,requested_seconds," +
                "fixed_requirements,released_requirements,releases_all,solve_started,status,solution_count," +
                "solve_seconds,iis_seconds,iis_status");
        }
        writer.WriteLine(string.Join(',',
            iterationIndex.ToString(CultureInfo.InvariantCulture),
            attemptIndex.ToString(CultureInfo.InvariantCulture),
            Csv(plan.NeighborhoodId),
            plan.ActionType,
            Csv(plan.ActionPlanId),
            Csv(plan.ActionApplicability),
            actualTransition,
            Csv(plan.Reason),
            plan.EndpointOrderChangeBudget.ToString(CultureInfo.InvariantCulture),
            plan.RequestedSolveSeconds.ToString("R", CultureInfo.InvariantCulture),
            fixation.FixedRequirementCount.ToString(CultureInfo.InvariantCulture),
            (fixation.ReleasedRequirementIds?.Count ?? 0).ToString(CultureInfo.InvariantCulture),
            plan.ReleasesAllVerifiedAssignments,
            solveStarted,
            status,
            solutionCount.ToString(CultureInfo.InvariantCulture),
            solveRuntimeSeconds?.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty,
            iis?.RuntimeSeconds.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty,
            Csv(iis?.Status ?? string.Empty)));
    }

    private static string Csv(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) < 0
            ? value
            : $"\"{value.Replace("\"", "\"\"")}\"";
}

/// <summary>
/// 从修复参考中提取首区间和末区间的先后关系，并只允许冲突列车及其相邻列车在给定 K 内换序。
/// </summary>
public static class ValidityFirstEndpointOrderTrustRegionBuilder
{
    public static PrecedenceTrustRegion Build(
        TimetablingInstance instance,
        MasterSolutionSnapshot repairReference,
        IReadOnlySet<int> evidenceTrainIndices,
        int maximumEndpointOrderChanges,
        string regionId)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(repairReference);
        ArgumentNullException.ThrowIfNull(evidenceTrainIndices);
        ArgumentException.ThrowIfNullOrWhiteSpace(regionId);
        if (maximumEndpointOrderChanges < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEndpointOrderChanges));
        }

        HashSet<string> endpointSections = instance.Trains
            .SelectMany(train => EndpointSectionNames(train))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> endpointCenter = repairReference.SectionPrecedenceByKey
            .Where(item => TryParseSectionKey(item.Key, out string sectionName, out _, out _) &&
                           endpointSections.Contains(sectionName))
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);

        HashSet<int> neighborhoodTrains = evidenceTrainIndices.ToHashSet();
        AddReferenceNeighbors(
            instance.Trains
                .Where(train => repairReference.DepartureSecondsByStopId.ContainsKey(train.OrderedStops[0].StopId))
                .OrderBy(train => repairReference.DepartureSecondsByStopId[train.OrderedStops[0].StopId])
                .ThenBy(train => train.TrainIndex)
                .Select(train => train.TrainIndex)
                .ToList(),
            evidenceTrainIndices,
            neighborhoodTrains);
        AddReferenceNeighbors(
            instance.Trains
                .Where(train => repairReference.ArrivalSecondsByStopId.ContainsKey(train.OrderedStops[^1].StopId))
                .OrderBy(train => repairReference.ArrivalSecondsByStopId[train.OrderedStops[^1].StopId])
                .ThenBy(train => train.TrainIndex)
                .Select(train => train.TrainIndex)
                .ToList(),
            evidenceTrainIndices,
            neighborhoodTrains);

        HashSet<string> releasableKeys = endpointCenter.Keys
            .Where(key => TryParseSectionKey(key, out _, out int firstTrain, out int secondTrain) &&
                          (neighborhoodTrains.Contains(firstTrain) || neighborhoodTrains.Contains(secondTrain)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new PrecedenceTrustRegion(
            regionId,
            endpointCenter,
            releasableKeys,
            maximumEndpointOrderChanges,
            0);
    }

    private static IEnumerable<string> EndpointSectionNames(TrainRecord train)
    {
        if (train.OrderedStops.Count > 1 &&
            !string.IsNullOrWhiteSpace(train.OrderedStops[1].IncomingSectionName))
        {
            yield return train.OrderedStops[1].IncomingSectionName!;
        }
        if (train.OrderedStops.Count > 1 &&
            !string.IsNullOrWhiteSpace(train.OrderedStops[^1].IncomingSectionName))
        {
            yield return train.OrderedStops[^1].IncomingSectionName!;
        }
    }

    private static void AddReferenceNeighbors(
        IReadOnlyList<int> orderedTrainIndices,
        IReadOnlySet<int> evidenceTrainIndices,
        ISet<int> result)
    {
        for (int index = 0; index < orderedTrainIndices.Count; index++)
        {
            if (!evidenceTrainIndices.Contains(orderedTrainIndices[index]))
            {
                continue;
            }
            if (index > 0)
            {
                result.Add(orderedTrainIndices[index - 1]);
            }
            if (index + 1 < orderedTrainIndices.Count)
            {
                result.Add(orderedTrainIndices[index + 1]);
            }
        }
    }

    private static bool TryParseSectionKey(
        string key,
        out string sectionName,
        out int firstTrainIndex,
        out int secondTrainIndex)
    {
        sectionName = string.Empty;
        firstTrainIndex = 0;
        secondTrainIndex = 0;
        string[] parts = key.Split("::", StringSplitOptions.None);
        if (parts.Length < 3 ||
            !int.TryParse(parts[^2], NumberStyles.Integer, CultureInfo.InvariantCulture, out firstTrainIndex) ||
            !int.TryParse(parts[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out secondTrainIndex))
        {
            return false;
        }
        sectionName = string.Join("::", parts.Take(parts.Length - 2));
        return !string.IsNullOrWhiteSpace(sectionName);
    }
}

/// <summary>
/// 记录本轮允许的首末端换序数量，以及求解结果相对修复参考和初始参考实际发生的变化。
/// </summary>
public static class ValidityFirstEndpointOrderAuditReporter
{
    public static void Append(
        int iteration,
        string phase,
        int allowedChanges,
        PrecedenceTrustRegion trustRegion,
        MasterSolutionSnapshot repairReference,
        MasterSolutionSnapshot initialReference,
        MasterSolutionSnapshot result,
        string outputDirectory)
    {
        ArgumentNullException.ThrowIfNull(trustRegion);
        Directory.CreateDirectory(outputDirectory);
        string path = Path.Combine(outputDirectory, "validity_first_endpoint_order_audit.csv");
        bool writeHeader = !File.Exists(path);
        using StreamWriter writer = new(path, true, new UTF8Encoding(false));
        if (writeHeader)
        {
            writer.WriteLine(
                "iteration,phase,allowed_changes,center_relation_count,releasable_relation_count," +
                "changes_from_repair_reference,changes_from_initial_reference");
        }
        writer.WriteLine(string.Join(',',
            iteration.ToString(CultureInfo.InvariantCulture),
            phase,
            allowedChanges.ToString(CultureInfo.InvariantCulture),
            trustRegion.CenterValuesByKey.Count.ToString(CultureInfo.InvariantCulture),
            trustRegion.HotspotKeys.Count.ToString(CultureInfo.InvariantCulture),
            CountChanges(trustRegion.CenterValuesByKey, result.SectionPrecedenceByKey)
                .ToString(CultureInfo.InvariantCulture),
            CountChanges(
                    initialReference.SectionPrecedenceByKey
                        .Where(item => trustRegion.CenterValuesByKey.ContainsKey(item.Key))
                        .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase),
                    result.SectionPrecedenceByKey)
                .ToString(CultureInfo.InvariantCulture)));
    }

    private static int CountChanges(
        IReadOnlyDictionary<string, int> reference,
        IReadOnlyDictionary<string, int> result) => reference.Count(item =>
        result.TryGetValue(item.Key, out int value) && value != item.Value);
}
