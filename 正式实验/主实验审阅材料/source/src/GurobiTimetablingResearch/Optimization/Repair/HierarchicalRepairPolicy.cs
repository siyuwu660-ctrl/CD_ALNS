// 分层修复策略将可调整决策划分为先后、时间、停站等等级，并用 RepairDecisionNeighborhood 表示列车与事件范围。
// 工厂依据冲突 episode 构建每层释放集合，计数器则量化新增自由度，供试探求解选择最小可行层级。
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

public enum RepairDecisionLevel
{
    TimeOnly,
    TimeAndRunningSupplement,
    LocalPrecedence,
    FlexibleStop,
    LocalMicroExact
}

public static class HierarchicalRepairPolicy
{
    public static IReadOnlyList<RepairDecisionLevel> OrderedLevels { get; } = Array.AsReadOnly(
    [
        RepairDecisionLevel.TimeOnly,
        RepairDecisionLevel.TimeAndRunningSupplement,
        RepairDecisionLevel.LocalPrecedence,
        RepairDecisionLevel.FlexibleStop,
        RepairDecisionLevel.LocalMicroExact
    ]);
}

public static class RepairDecisionReleasePolicy
{
    public static bool ReleasesRunningSupplement(RepairDecisionLevel level, bool insideSpan) =>
        insideSpan && level >= RepairDecisionLevel.TimeAndRunningSupplement;

    public static bool ReleasesPrecedence(RepairDecisionLevel level, bool insideSpan) =>
        insideSpan && level >= RepairDecisionLevel.LocalPrecedence;

    public static bool ReleasesStop(
        RepairDecisionLevel level,
        StopRequirement requirement,
        TrainStopRole role,
        bool insideSpan) =>
        insideSpan &&
        level >= RepairDecisionLevel.FlexibleStop &&
        role == TrainStopRole.Intermediate &&
        requirement == StopRequirement.OptionalStop;
}

public sealed record RepairGeographicSpan(int MinimumStationOrder, int MaximumStationOrder)
{
    public bool Contains(int stationOrder) =>
        stationOrder >= MinimumStationOrder && stationOrder <= MaximumStationOrder;
}

public sealed record RepairDecisionNeighborhood(
    RepairDecisionLevel DecisionLevel,
    IReadOnlySet<int> ReleasedTrainIndices,
    IReadOnlyDictionary<int, RepairGeographicSpan> SpansByTrainIndex,
    IisDependencyOverlay? DependencyOverlay = null);

public sealed record RepairDecisionReleaseCounts(
    int ReleasedTimeVariableCount,
    int ReleasedRunningSupplementVariableCount,
    int ReleasedSectionPrecedenceVariableCount,
    int ReleasedStationEventPrecedenceVariableCount,
    int ReleasedStopVariableCount,
    int? AffectedMinimumStationOrder,
    int? AffectedMaximumStationOrder,
    string StationSpanDescription)
{
    public int ReleasedPrecedenceVariableCount =>
        ReleasedSectionPrecedenceVariableCount + ReleasedStationEventPrecedenceVariableCount;
}

internal static class RepairDecisionReleaseCounter
{
    internal static RepairDecisionReleaseCounts Count(
        BasicMilpModel model,
        RepairDecisionNeighborhood neighborhood)
    {
        IisDependencyOverlay overlay = RepairDecisionDependencyOverlayResolver.Resolve(neighborhood);
        int releasedTimes = model.Instance.TrainStops.Sum(stop =>
            (StructuralRepairNeighborhoodApplier.IsTimeEventReleased(
                neighborhood, $"arrival::{stop.StopId}", stop.TrainIndex, stop.StationOrder) ? 1 : 0) +
            (StructuralRepairNeighborhoodApplier.IsTimeEventReleased(
                neighborhood, $"departure::{stop.StopId}", stop.TrainIndex, stop.StationOrder) ? 1 : 0));
        int releasedStops = model.Instance.TrainStops.Count(stop =>
            overlay.FlexibleStopKeys.Contains(stop.StopId) || RepairDecisionReleasePolicy.ReleasesStop(
                neighborhood.DecisionLevel,
                stop.StopRequirement,
                stop.StopRole,
                StructuralRepairNeighborhoodApplier.IsInsideSpan(
                    neighborhood, stop.TrainIndex, stop.StationOrder)));
        int releasedSupplement = model.RunningTimeSupplementVars.Keys.Count(key =>
        {
            TrainSectionRepairContext section = TrainSectionRepairContextResolver.Resolve(model.Instance, key);
            return overlay.SupplementSectionKeys.Contains(key) || RepairDecisionReleasePolicy.ReleasesRunningSupplement(
                neighborhood.DecisionLevel,
                StructuralRepairNeighborhoodApplier.IsInsideSpan(
                    neighborhood, section.TrainIndex, section.FromStationOrder) &&
                StructuralRepairNeighborhoodApplier.IsInsideSpan(
                    neighborhood, section.TrainIndex, section.ToStationOrder));
        });
        int releasedSection = model.SectionConflictCandidates.Count(candidate =>
            overlay.SectionPrecedenceKeys.Contains(BasicMilpModelBuilder.BuildSectionPrecedenceKey(candidate)) ||
            RepairDecisionReleasePolicy.ReleasesPrecedence(
                neighborhood.DecisionLevel,
                StructuralRepairNeighborhoodApplier.IsInsideSpan(neighborhood, candidate.FirstTrainIndex, candidate.FirstStopOrder) &&
                StructuralRepairNeighborhoodApplier.IsInsideSpan(neighborhood, candidate.FirstTrainIndex, candidate.FirstEndStopOrder) &&
                StructuralRepairNeighborhoodApplier.IsInsideSpan(neighborhood, candidate.SecondTrainIndex, candidate.SecondStopOrder) &&
                StructuralRepairNeighborhoodApplier.IsInsideSpan(neighborhood, candidate.SecondTrainIndex, candidate.SecondEndStopOrder)));
        int releasedStation = model.StationEventConflictCandidates.Count(candidate =>
            overlay.StationEventPrecedenceKeys.Contains(BasicMilpModelBuilder.BuildStationEventPrecedenceKey(candidate)) ||
            RepairDecisionReleasePolicy.ReleasesPrecedence(
                neighborhood.DecisionLevel,
                StructuralRepairNeighborhoodApplier.IsInsideSpan(neighborhood, candidate.FirstTrainIndex, candidate.FirstStationOrder) &&
                StructuralRepairNeighborhoodApplier.IsInsideSpan(neighborhood, candidate.SecondTrainIndex, candidate.SecondStationOrder)));
        string span = string.Join('|', neighborhood.SpansByTrainIndex
            .OrderBy(item => item.Key)
            .Select(item => $"t{item.Key}:{item.Value.MinimumStationOrder}-{item.Value.MaximumStationOrder}"));
        int? affectedMinimum = neighborhood.SpansByTrainIndex.Count == 0
            ? null
            : neighborhood.SpansByTrainIndex.Values.Min(item => item.MinimumStationOrder);
        int? affectedMaximum = neighborhood.SpansByTrainIndex.Count == 0
            ? null
            : neighborhood.SpansByTrainIndex.Values.Max(item => item.MaximumStationOrder);
        return new RepairDecisionReleaseCounts(
            releasedTimes,
            releasedSupplement,
            releasedSection,
            releasedStation,
            releasedStops,
            affectedMinimum,
            affectedMaximum,
            span);
    }
}

internal static class RepairDecisionNeighborhoodFactory
{
    internal static RepairDecisionNeighborhood Create(
        TimetablingInstance instance,
        RepairDecisionLevel level,
        IReadOnlySet<int> releasedTrainIndices,
        IReadOnlyList<HeadwayConflictEpisode> selectedEpisodes,
        int geographicPadding = 1)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(releasedTrainIndices);
        ArgumentNullException.ThrowIfNull(selectedEpisodes);
        if (geographicPadding < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(geographicPadding));
        }
        Dictionary<int, RepairGeographicSpan> spans = [];
        foreach (int trainIndex in releasedTrainIndices)
        {
            List<HeadwayConflictEpisode> incident = selectedEpisodes.Where(item =>
                item.FirstTrainIndex == trainIndex || item.SecondTrainIndex == trainIndex).ToList();
            if (incident.Count == 0)
            {
                continue;
            }

            TrainRecord? train = instance.Trains.FirstOrDefault(item => item.TrainIndex == trainIndex);
            if (train is null || train.OrderedStops.Count == 0)
            {
                continue;
            }

            int routeMinimum = train.OrderedStops.Min(stop => stop.StationOrder);
            int routeMaximum = train.OrderedStops.Max(stop => stop.StationOrder);
            int minimum = Math.Max(routeMinimum, incident.Min(item => item.MinimumStationOrder) - geographicPadding);
            int maximum = Math.Min(routeMaximum, incident.Max(item => item.MaximumStationOrder) + geographicPadding);
            if (minimum > maximum)
            {
                continue;
            }
            spans[trainIndex] = new RepairGeographicSpan(minimum, maximum);
        }
        return new RepairDecisionNeighborhood(level, releasedTrainIndices, spans);
    }
}
