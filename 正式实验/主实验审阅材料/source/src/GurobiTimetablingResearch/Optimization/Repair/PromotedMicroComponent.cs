// 已提升微观组件把经车站子问题确认的重要径路股道选择和站内要求固化为可持久化结构记录。
// 组件存储以规范键去重，模型增强器把这些局部精确约束加入后续主问题，使已发现的微观结构无需重复搜索。
using System.Security.Cryptography;
using System.Text;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

public sealed record PromotedRouteTrackAlternative(
    string TrackName,
    int? IncomingRouteId,
    int? OutgoingRouteId,
    bool? IsStop = null);

public sealed record PromotedStationRequirement(
    string RequirementId,
    int TrainIndex,
    int StationOrder,
    bool IsStop,
    IReadOnlyList<PromotedRouteTrackAlternative> AllowedAlternatives,
    bool PreserveAllLegalStopModes = false);

/// <summary>
/// 描述 promotion 中停站变量与进路—股道选择变量之间的联动结构。
/// </summary>
public sealed record PromotedStopModeLinkPlan(
    bool IsFlexible,
    int StoppingAlternativeCount,
    int PassingAlternativeCount);

/// <summary>
/// 检查热点组件是否完整保留了声明的停站自由度，防止把缺失的模式域误当成精确模型。
/// </summary>
public static class PromotedStopModeLinkPlanBuilder
{
    public static PromotedStopModeLinkPlan Build(PromotedStationRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        int stoppingCount = requirement.AllowedAlternatives.Count(alternative =>
            (alternative.IsStop ?? requirement.IsStop));
        int passingCount = requirement.AllowedAlternatives.Count(alternative =>
            !(alternative.IsStop ?? requirement.IsStop));

        if (requirement.PreserveAllLegalStopModes && (stoppingCount == 0 || passingCount == 0))
        {
            throw new InvalidOperationException(
                $"Promoted requirement '{requirement.RequirementId}' declares complete stop-mode freedom " +
                "but does not contain both stopping and passing alternatives.");
        }

        return new PromotedStopModeLinkPlan(
            requirement.PreserveAllLegalStopModes && stoppingCount > 0 && passingCount > 0,
            stoppingCount,
            passingCount);
    }
}

public sealed record PromotedMicroComponent(
    string ComponentId,
    string StationName,
    IReadOnlyList<int> TrainIndices,
    IReadOnlyList<PromotedStationRequirement> Requirements,
    string PromotionReason,
    string SourceCore,
    int PromotionIteration,
    int PersistentRounds,
    bool Active);

public sealed record PromotedMicroBuildDiagnostic(
    string StationName,
    string ComponentId,
    int TrainCount,
    int AlternativeVariableCount,
    int ConstraintCount,
    string SourceCore,
    int PromotionIteration,
    int PersistentRounds,
    int OrderVariableCount = 0,
    int LegacyOrderVariableCount = 0);

/// <summary>
/// 记录热点关系在紧凑表达下需要的顺序变量和条件约束数量，并保留旧逐候选建模的变量数量作为对照。
/// </summary>
public sealed record PromotedMicroCompactRelationEstimate(
    int OrderVariableCount,
    int ConditionalConstraintCount,
    int LegacyOrderVariableCount);

/// <summary>
/// 按“真实时间事件”和“共享资源”归并顺序变量，但仍逐个进路组合保留各自的精确安全间隔约束。
/// </summary>
public static class PromotedMicroCompactRelationEstimator
{
    public static PromotedMicroCompactRelationEstimate Estimate(
        IReadOnlyList<PromotedStationRequirement> requirements,
        Func<int, int, bool> hasRecordedHeadway,Func<double>? remaining=null)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        ArgumentNullException.ThrowIfNull(hasRecordedHeadway);
        long compactOrderVariables = 0;
        long conditionalConstraints = 0;
        long legacyOrderVariables = 0;

        for (int firstIndex = 0; firstIndex < requirements.Count; firstIndex++)
        {
            PromotedStationRequirement first = requirements[firstIndex];
            for (int secondIndex = firstIndex + 1; secondIndex < requirements.Count; secondIndex++)
            {
                if(remaining?.Invoke()<=0) throw new TimeoutException("Micro model estimation deadline expired.");
                PromotedStationRequirement second = requirements[secondIndex];
                IReadOnlyList<string> commonStoppingTracks = first.AllowedAlternatives
                    .Where(alternative => alternative.IsStop ?? first.IsStop)
                    .Select(alternative => alternative.TrackName)
                    .Intersect(
                        second.AllowedAlternatives
                            .Where(alternative => alternative.IsStop ?? second.IsStop)
                            .Select(alternative => alternative.TrackName),
                        StringComparer.OrdinalIgnoreCase)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (commonStoppingTracks.Count > 0)
                {
                    compactOrderVariables++;
                    conditionalConstraints += 2L * commonStoppingTracks.Count;
                }

                legacyOrderVariables +=
                    (from firstAlternative in first.AllowedAlternatives
                     from secondAlternative in second.AllowedAlternatives
                     where (firstAlternative.IsStop ?? first.IsStop) &&
                           (secondAlternative.IsStop ?? second.IsStop) &&
                           string.Equals(
                               firstAlternative.TrackName,
                               secondAlternative.TrackName,
                               StringComparison.OrdinalIgnoreCase)
                     select 1).LongCount();

                foreach (bool firstIncoming in new[] { true, false })
                {
                    foreach (bool secondIncoming in new[] { true, false })
                    {
                        IReadOnlyList<int> firstRouteIds = RouteIds(first, firstIncoming);
                        IReadOnlyList<int> secondRouteIds = RouteIds(second, secondIncoming);
                        long activeRoutePairs =
                            (from firstRouteId in firstRouteIds
                             from secondRouteId in secondRouteIds
                             where HasEitherDirection(
                                 firstRouteId,
                                 secondRouteId,
                                 hasRecordedHeadway)
                             select 1).LongCount();
                        if (activeRoutePairs > 0)
                        {
                            compactOrderVariables++;
                            conditionalConstraints += 2L * activeRoutePairs;
                        }

                        legacyOrderVariables +=
                            (from firstAlternative in first.AllowedAlternatives
                             let firstRouteId = RouteId(firstAlternative, firstIncoming)
                             where firstRouteId.HasValue
                             from secondAlternative in second.AllowedAlternatives
                             let secondRouteId = RouteId(secondAlternative, secondIncoming)
                             where secondRouteId.HasValue && HasEitherDirection(
                                 firstRouteId.Value,
                                 secondRouteId.Value,
                                 hasRecordedHeadway)
                             select 1).LongCount();
                    }
                }
            }
        }

        return new PromotedMicroCompactRelationEstimate(
            checked((int)compactOrderVariables),
            checked((int)conditionalConstraints),
            checked((int)legacyOrderVariables));
    }

    private static IReadOnlyList<int> RouteIds(
        PromotedStationRequirement requirement,
        bool incoming) => requirement.AllowedAlternatives
        .Select(alternative => RouteId(alternative, incoming))
        .Where(routeId => routeId.HasValue)
        .Select(routeId => routeId!.Value)
        .Distinct()
        .ToList();

    private static int? RouteId(PromotedRouteTrackAlternative alternative, bool incoming) =>
        incoming ? alternative.IncomingRouteId : alternative.OutgoingRouteId;

    private static bool HasEitherDirection(
        int firstRouteId,
        int secondRouteId,
        Func<int, int, bool> hasRecordedHeadway) =>
        hasRecordedHeadway(firstRouteId, secondRouteId) ||
        hasRecordedHeadway(secondRouteId, firstRouteId);
}

public sealed class PromotedMicroComponentStore
{
    private readonly List<PromotedMicroComponent> _components;

    public PromotedMicroComponentStore(IEnumerable<PromotedMicroComponent>? components = null)
    {
        _components = components?.GroupBy(item => item.ComponentId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last()).ToList() ?? [];
    }

    public IReadOnlyList<PromotedMicroComponent> Components => _components;
    public IReadOnlyList<PromotedMicroComponent> ActiveComponents => _components.Where(item => item.Active).ToList();

    public void Upsert(PromotedMicroComponent component)
    {
        int index = _components.FindIndex(item => string.Equals(
            item.ComponentId, component.ComponentId, StringComparison.OrdinalIgnoreCase));
        if (index < 0) _components.Add(component); else _components[index] = component;
    }

    public void SetCoordinationReopenedStations(IReadOnlySet<string> stationNames)
    {
        for (int index = 0; index < _components.Count; index++)
        {
            PromotedMicroComponent component = _components[index];
            if (!component.ComponentId.StartsWith("micro_reopen::", StringComparison.OrdinalIgnoreCase)) continue;
            _components[index] = component with { Active = stationNames.Contains(component.StationName) };
        }
    }
}

/// <summary>
/// 将完整热点审计标识压缩为有界且稳定的 Gurobi 符号前缀；完整组件标识仍保留在
/// promotion 和审计文件中，模型名称仅使用可读短段与哈希避免超过255字符限制。
/// </summary>
public static class PromotedComponentNamePolicy
{
    public static string BuildVariablePrefix(string componentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(componentId);
        string readable = new string(componentId
            .Select(character => char.IsLetterOrDigit(character) ? character : '_')
            .Take(32)
            .ToArray());
        string hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(componentId)))
            .ToLowerInvariant()[..16];
        return $"promoted_{readable}_{hash}";
    }
}

public static class MicroPromotionEligibilityPolicy
{
    public static bool ShouldPromote(
        bool provenStationCore,
        bool persistentHotspot,
        int persistentRounds,
        int threshold,
        bool enabled = true)
    {
        if (threshold <= 0) throw new ArgumentOutOfRangeException(nameof(threshold));
        return enabled && (provenStationCore || (persistentHotspot && persistentRounds >= threshold));
    }
}

internal static class PromotedMicroComponentModelAugmenter
{
    internal static StationMicroRepairAugmentation Apply(
        BasicMilpModel model,
        PromotedMicroComponent component,
        int endpointOccupationSeconds)
    {
        Dictionary<int, RouteRecord> routeById = model.Instance.Routes.ToDictionary(route => route.RouteId);
        string prefix = PromotedComponentNamePolicy.BuildVariablePrefix(component.ComponentId);
        Dictionary<string, IReadOnlyList<PromotedRouteTrackAlternative>> allowed =
            new(StringComparer.OrdinalIgnoreCase);
        List<StationMicroRequirement> requirements = [];
        foreach (PromotedStationRequirement promoted in component.Requirements)
        {
            TrainStopRecord stop = model.Instance.TrainStops.Single(item =>
                item.TrainIndex == promoted.TrainIndex && item.StationOrder == promoted.StationOrder);
            TrainRecord train = model.Instance.Trains.Single(item => item.TrainIndex == promoted.TrainIndex);
            PromotedStopModeLinkPlan linkPlan = PromotedStopModeLinkPlanBuilder.Build(promoted);
            if (linkPlan.IsFlexible)
            {
                if (stop.StopRole != TrainStopRole.Intermediate || stop.StopRequirement != StopRequirement.OptionalStop)
                {
                    throw new InvalidOperationException(
                        $"Promoted requirement '{promoted.RequirementId}' may preserve both stop modes only for an optional intermediate stop.");
                }

                requirements.Add(BuildRequirement(model, component, promoted, stop, train, routeById, true));
                requirements.Add(BuildRequirement(model, component, promoted, stop, train, routeById, false));
            }
            else
            {
                // 旧 promotion 继续把局部修复选中的停站状态作为固定条件，保持原有实验行为。
                model.Model.AddConstr(
                    model.StopVars[stop.StopId],
                    Gurobi.GRB.EQUAL,
                    promoted.IsStop ? 1.0 : 0.0,
                    $"{prefix}_stop_mode_t{stop.TrainIndex}_s{stop.StationOrder}");
                requirements.Add(BuildRequirement(
                    model,
                    component,
                    promoted,
                    stop,
                    train,
                    routeById,
                    promoted.IsStop));
            }

            allowed[stop.StopId] = promoted.AllowedAlternatives;
        }

        return StationMicroRepairModelAugmenter.Apply(
            model,
            requirements,
            endpointOccupationSeconds,
            allowed,
            prefix);
    }

    private static StationMicroRequirement BuildRequirement(
        BasicMilpModel model,
        PromotedMicroComponent component,
        PromotedStationRequirement promoted,
        TrainStopRecord stop,
        TrainRecord train,
        IReadOnlyDictionary<int, RouteRecord> routeById,
        bool isStop)
    {
        IReadOnlyList<PromotedRouteTrackAlternative> modeAlternatives = promoted.AllowedAlternatives
            .Where(alternative => (alternative.IsStop ?? promoted.IsStop) == isStop)
            .ToList();
        if (modeAlternatives.Count == 0)
        {
            throw new InvalidOperationException(
                $"Promoted requirement '{promoted.RequirementId}' has no alternatives for stop mode '{isStop}'.");
        }

        IReadOnlyList<RouteRecord> incomingRoutes = modeAlternatives
            .Where(item => item.IncomingRouteId.HasValue)
            .Select(item => ResolveRoute(routeById, item.IncomingRouteId!.Value, component.ComponentId))
            .DistinctBy(item => item.RouteId)
            .ToList();
        IReadOnlyList<RouteRecord> outgoingRoutes = modeAlternatives
            .Where(item => item.OutgoingRouteId.HasValue)
            .Select(item => ResolveRoute(routeById, item.OutgoingRouteId!.Value, component.ComponentId))
            .DistinctBy(item => item.RouteId)
            .ToList();
        IReadOnlyList<string> tracks = modeAlternatives
            .Select(item => item.TrackName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        string? outgoingSection = train.OrderedStops
            .SkipWhile(item => item.StationOrder != stop.StationOrder)
            .Skip(1)
            .FirstOrDefault()?.IncomingSectionName;
        return new StationMicroRequirement(
            stop.StopId,
            promoted.TrainIndex,
            train.TrainNo,
            promoted.StationOrder,
            component.StationName,
            isStop,
            stop.StopRole,
            0.0,
            0.0,
            stop.IncomingSectionName,
            outgoingSection,
            stop.StopRole == TrainStopRole.Origin ? MovementEvent.Unknown : isStop ? MovementEvent.Arrival : MovementEvent.Passing,
            stop.StopRole == TrainStopRole.Destination ? MovementEvent.Unknown : isStop ? MovementEvent.Departure : MovementEvent.Passing,
            stop.StopRole != TrainStopRole.Origin,
            stop.StopRole != TrainStopRole.Destination,
            incomingRoutes,
            outgoingRoutes,
            tracks,
            tracks,
            StopRequirement: stop.StopRequirement);
    }

    private static RouteRecord ResolveRoute(
        IReadOnlyDictionary<int, RouteRecord> routeById,
        int routeId,
        string componentId) =>
        routeById.TryGetValue(routeId, out RouteRecord? route)
            ? route
            : throw new InvalidDataException(
                $"Promoted Micro component '{componentId}' references missing route {routeId}.");
}
