// 冲突 episode 分析器按列车对、位置和连续事件关系聚合微观冲突，形成可独立修复的事件簇。
// 聚合结果保存冲突规模、涉及车站区间和稳定标识，供候选排序、均衡抽样及邻域传播共同使用。
namespace GurobiTimetablingResearch.Optimization;

internal sealed record HeadwayConflictEpisode(
    string EpisodeId,
    int FirstTrainIndex,
    int SecondTrainIndex,
    int MinimumStationOrder,
    int MaximumStationOrder,
    int ConflictRowCount,
    int PersistenceCount,
    int DistinctResourceCount,
    double MaximumConflictWindowSeconds,
    IReadOnlyList<string> StationNames,
    IReadOnlyList<MicroConflictExplanation> Conflicts);

internal static class HeadwayConflictEpisodeAnalyzer
{
    internal static IReadOnlyList<HeadwayConflictEpisode> Analyze(
        IReadOnlyList<MicroConflictExplanation> conflicts,
        IReadOnlyDictionary<string, int> persistenceByEpisodeId)
    {
        return conflicts
            .Where(IsExactPairConflict)
            .GroupBy(BuildCanonicalPair)
            .Select(group => BuildEpisode(group.Key.FirstTrain, group.Key.SecondTrain, group.ToList(), persistenceByEpisodeId))
            .OrderByDescending(episode => episode.PersistenceCount)
            .ThenByDescending(episode => episode.ConflictRowCount)
            .ThenBy(episode => episode.EpisodeId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static string BuildEpisodeId(int firstTrainIndex, int secondTrainIndex)
    {
        (int first, int second) = firstTrainIndex <= secondTrainIndex
            ? (firstTrainIndex, secondTrainIndex)
            : (secondTrainIndex, firstTrainIndex);
        return $"headway-pair::{first}::{second}";
    }

    private static bool IsExactPairConflict(MicroConflictExplanation conflict) =>
        string.Equals(conflict.ConflictKind, "exact_headway_infeasible", StringComparison.OrdinalIgnoreCase) &&
        conflict.FirstTrainIndex.HasValue &&
        conflict.SecondTrainIndex.HasValue;

    private static (int FirstTrain, int SecondTrain) BuildCanonicalPair(MicroConflictExplanation conflict)
    {
        int first = conflict.FirstTrainIndex!.Value;
        int second = conflict.SecondTrainIndex!.Value;
        return first <= second ? (first, second) : (second, first);
    }

    private static HeadwayConflictEpisode BuildEpisode(
        int firstTrainIndex,
        int secondTrainIndex,
        IReadOnlyList<MicroConflictExplanation> conflicts,
        IReadOnlyDictionary<string, int> persistenceByEpisodeId)
    {
        string episodeId = BuildEpisodeId(firstTrainIndex, secondTrainIndex);
        List<int> stationOrders = conflicts
            .SelectMany(conflict => new[] { conflict.StationOrder, conflict.SecondStationOrder })
            .Where(order => order.HasValue)
            .Select(order => order!.Value)
            .ToList();
        HashSet<string> resources = conflicts
            .SelectMany(conflict => new[]
            {
                conflict.ResourceKey,
                conflict.FirstSectionName ?? string.Empty,
                conflict.SecondSectionName ?? string.Empty
            })
            .Where(resource => !string.IsNullOrWhiteSpace(resource))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new HeadwayConflictEpisode(
            episodeId,
            firstTrainIndex,
            secondTrainIndex,
            stationOrders.Count == 0 ? 0 : stationOrders.Min(),
            stationOrders.Count == 0 ? 0 : stationOrders.Max(),
            conflicts.Count,
            persistenceByEpisodeId.TryGetValue(episodeId, out int priorPersistence) ? priorPersistence + 1 : 1,
            resources.Count,
            conflicts.Max(conflict => Math.Max(0.0, conflict.EndSeconds - conflict.StartSeconds)),
            conflicts.Select(conflict => conflict.StationName)
                .Where(station => !string.IsNullOrWhiteSpace(station))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(station => station, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            conflicts);
    }
}
