// 追踪冲突候选选择器把 episode 中尚未激活的区间边和车站事件边转换为统一候选，并按压力及覆盖范围排序。
// 传播解析器沿相关 episode 收集受影响列车集合，为后续邻域扩张提供图上的初始顶点。
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

internal sealed record HeadwayConflictCandidate(
    string CandidateKey,
    string EpisodeId,
    string ResourceKey,
    int FirstTrainIndex,
    int SecondTrainIndex,
    double Score,
    double PersistenceScore,
    double CoverageScore,
    double PropagationScore,
    double ConflictGraphScore,
    double ScarcityScore,
    SectionConflictEdgeRequest? SectionEdge,
    StationEventConflictCandidate? StationEdge,
    StructuralCandidateEvidence? Evidence = null);

internal sealed record HeadwayConflictCandidateBatch(
    HeadwayConflictEdgeExpansion Expansion,
    IReadOnlyList<HeadwayConflictCandidate> Candidates);

internal static class HeadwayConflictCandidateSelector
{
    internal static IReadOnlyList<HeadwayConflictCandidate> ResolveAvailableCandidates(
        IReadOnlyList<HeadwayConflictEpisode> episodes,
        IReadOnlySet<string> activeSectionEdgeKeys,
        IReadOnlySet<string> activeStationEdgeKeys) =>
        BuildCandidatePool(episodes, activeSectionEdgeKeys, activeStationEdgeKeys);

    internal static HeadwayConflictCandidateBatch Select(
        IReadOnlyList<HeadwayConflictEpisode> episodes,
        IReadOnlySet<string> activeSectionEdgeKeys,
        IReadOnlySet<string> activeStationEdgeKeys,
        int maxEdges)
    {
        if (maxEdges <= 0)
        {
            return EmptyBatch();
        }

        List<HeadwayConflictCandidate> pool = BuildCandidatePool(
            episodes,
            activeSectionEdgeKeys,
            activeStationEdgeKeys);

        List<HeadwayConflictCandidate> selected = new();
        HashSet<string> selectedEpisodes = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> selectedResources = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> selectedEdgeClasses = new(StringComparer.OrdinalIgnoreCase);
        while (selected.Count < maxEdges && pool.Count > 0)
        {
            HeadwayConflictCandidate? next = pool
                .Where(candidate =>
                    !selectedEpisodes.Contains(candidate.EpisodeId) &&
                    !selectedResources.Contains(candidate.ResourceKey) &&
                    !selectedEdgeClasses.Contains(ResolveEdgeClass(candidate)))
                .OrderByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.CandidateKey, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            next ??= pool
                .Where(candidate =>
                    !selectedEpisodes.Contains(candidate.EpisodeId) &&
                    !selectedEdgeClasses.Contains(ResolveEdgeClass(candidate)))
                .OrderByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.CandidateKey, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            next ??= pool
                .Where(candidate =>
                    !selectedEpisodes.Contains(candidate.EpisodeId) &&
                    !selectedResources.Contains(candidate.ResourceKey))
                .OrderByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.CandidateKey, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            next ??= pool
                .Where(candidate => !selectedEpisodes.Contains(candidate.EpisodeId))
                .OrderByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.CandidateKey, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            next ??= pool[0];

            selected.Add(next);
            selectedEpisodes.Add(next.EpisodeId);
            selectedResources.Add(next.ResourceKey);
            selectedEdgeClasses.Add(ResolveEdgeClass(next));
            pool.Remove(next);
        }

        return new HeadwayConflictCandidateBatch(
            new HeadwayConflictEdgeExpansion(
                selected.Where(candidate => candidate.SectionEdge is not null)
                    .Select(candidate => candidate.SectionEdge!)
                    .ToList(),
                selected.Where(candidate => candidate.StationEdge is not null)
                    .Select(candidate => candidate.StationEdge!)
                    .ToList()),
            selected);
    }

    internal static HeadwayConflictCandidateBatch SelectExact(
        IReadOnlyList<HeadwayConflictEpisode> episodes,
        IReadOnlySet<string> activeSectionEdgeKeys,
        IReadOnlySet<string> activeStationEdgeKeys,
        int cap,
        int perStationCap = 4,
        int perPairCap = 2)
    {
        if (cap <= 0 || perStationCap <= 0 || perPairCap <= 0)
        {
            return EmptyBatch();
        }

        Dictionary<string, HeadwayConflictEpisode> episodeById = episodes.ToDictionary(
            episode => episode.EpisodeId,
            StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> stationCounts = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<(int First, int Second), int> pairCounts = [];
        List<HeadwayConflictCandidate> selected = [];
        foreach (HeadwayConflictCandidate candidate in BuildCandidatePool(
                episodes,
                activeSectionEdgeKeys,
                activeStationEdgeKeys)
            .OrderByDescending(candidate => episodeById[candidate.EpisodeId].PersistenceCount)
            .ThenByDescending(candidate => episodeById[candidate.EpisodeId].MaximumConflictWindowSeconds)
            .ThenByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.CandidateKey, StringComparer.OrdinalIgnoreCase))
        {
            HeadwayConflictEpisode episode = episodeById[candidate.EpisodeId];
            string stationName = candidate.StationEdge?.StationName ??
                episode.StationNames.FirstOrDefault() ?? candidate.ResourceKey;
            (int First, int Second) pair = candidate.FirstTrainIndex <= candidate.SecondTrainIndex
                ? (candidate.FirstTrainIndex, candidate.SecondTrainIndex)
                : (candidate.SecondTrainIndex, candidate.FirstTrainIndex);
            stationCounts.TryGetValue(stationName, out int stationCount);
            pairCounts.TryGetValue(pair, out int pairCount);
            if (stationCount >= perStationCap || pairCount >= perPairCap)
            {
                continue;
            }
            selected.Add(candidate);
            stationCounts[stationName] = stationCount + 1;
            pairCounts[pair] = pairCount + 1;
            if (selected.Count == cap)
            {
                break;
            }
        }

        return new HeadwayConflictCandidateBatch(
            new HeadwayConflictEdgeExpansion(
                selected.Where(candidate => candidate.SectionEdge is not null)
                    .Select(candidate => candidate.SectionEdge!)
                    .ToList(),
                selected.Where(candidate => candidate.StationEdge is not null)
                    .Select(candidate => candidate.StationEdge!)
                    .ToList()),
            selected);
    }

    internal static HeadwayConflictCandidateBatch SelectAllExact(
        IReadOnlyList<HeadwayConflictEpisode> episodes,
        IReadOnlySet<string> activeSectionEdgeKeys,
        IReadOnlySet<string> activeStationEdgeKeys) =>
        SelectExact(
            episodes,
            activeSectionEdgeKeys,
            activeStationEdgeKeys,
            int.MaxValue,
            int.MaxValue,
            int.MaxValue);

    private static List<HeadwayConflictCandidate> BuildCandidatePool(
        IReadOnlyList<HeadwayConflictEpisode> episodes,
        IReadOnlySet<string> activeSectionEdgeKeys,
        IReadOnlySet<string> activeStationEdgeKeys)
    {
        IReadOnlyDictionary<int, int> trainDegrees = episodes
            .SelectMany(episode => new[] { episode.FirstTrainIndex, episode.SecondTrainIndex })
            .GroupBy(trainIndex => trainIndex)
            .ToDictionary(group => group.Key, group => group.Count());

        return episodes
            .SelectMany(episode => BuildCandidates(episode, trainDegrees, activeSectionEdgeKeys, activeStationEdgeKeys))
            .GroupBy(candidate => candidate.CandidateKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.CandidateKey, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.CandidateKey, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<HeadwayConflictCandidate> BuildCandidates(
        HeadwayConflictEpisode episode,
        IReadOnlyDictionary<int, int> trainDegrees,
        IReadOnlySet<string> activeSectionEdgeKeys,
        IReadOnlySet<string> activeStationEdgeKeys)
    {
        double persistenceScore = episode.PersistenceCount * 1000.0;
        double coverageScore = episode.ConflictRowCount * 100.0;
        double propagationScore = Math.Max(0, episode.MaximumStationOrder - episode.MinimumStationOrder) * 20.0;
        double conflictGraphScore = (
            trainDegrees.GetValueOrDefault(episode.FirstTrainIndex) +
            trainDegrees.GetValueOrDefault(episode.SecondTrainIndex)) * 10.0;
        double scarcityScore = 100.0 / Math.Max(1, episode.DistinctResourceCount);
        double score = persistenceScore + coverageScore + propagationScore + conflictGraphScore + scarcityScore;

        foreach (MicroConflictExplanation conflict in episode.Conflicts)
        {
            HeadwayConflictEdgeExpansion expansion = HeadwayConflictEdgeExpander.Expand([conflict]);
            foreach (SectionConflictEdgeRequest edge in expansion.SectionEdges)
            {
                string modelKey = InitialConflictGraphBuilder.BuildCanonicalPairKey(
                    edge.SectionName, edge.FirstTrainIndex, edge.SecondTrainIndex);
                if (activeSectionEdgeKeys.Contains(modelKey))
                {
                    continue;
                }

                yield return new HeadwayConflictCandidate(
                    $"section::{modelKey}", episode.EpisodeId, edge.SectionName,
                    episode.FirstTrainIndex, episode.SecondTrainIndex, score,
                    persistenceScore, coverageScore, propagationScore, conflictGraphScore, scarcityScore,
                    edge, null,
                    StructuralCandidateEvidenceFactory.Create(conflict, "section", edge.SectionName));
            }

            foreach (StationEventConflictCandidate edge in expansion.StationEdges)
            {
                string modelKey = BasicMilpModelBuilder.BuildStationEventPrecedenceKey(edge);
                if (activeStationEdgeKeys.Contains(modelKey))
                {
                    continue;
                }

                yield return new HeadwayConflictCandidate(
                    $"station::{modelKey}", episode.EpisodeId, edge.StationName,
                    episode.FirstTrainIndex, episode.SecondTrainIndex, score,
                    persistenceScore, coverageScore, propagationScore, conflictGraphScore, scarcityScore,
                    null, edge,
                    StructuralCandidateEvidenceFactory.Create(conflict, "station_event", edge.StationName));
            }
        }
    }

    private static HeadwayConflictCandidateBatch EmptyBatch() => new(
        new HeadwayConflictEdgeExpansion(
            Array.Empty<SectionConflictEdgeRequest>(),
            Array.Empty<StationEventConflictCandidate>()),
        Array.Empty<HeadwayConflictCandidate>());

    private static string ResolveEdgeClass(HeadwayConflictCandidate candidate) =>
        candidate.SectionEdge is null ? "station_event" : "section";
}

internal static class HeadwayConflictPropagationResolver
{
    internal static IReadOnlySet<int> ResolveTrainIndices(
        IReadOnlyList<HeadwayConflictCandidate> selectedCandidates,
        IReadOnlyList<HeadwayConflictEpisode> episodes)
    {
        HashSet<int> released = selectedCandidates
            .SelectMany(candidate => new[] { candidate.FirstTrainIndex, candidate.SecondTrainIndex })
            .ToHashSet();
        HashSet<int> seedTrains = new(released);
        HashSet<string> selectedEpisodeIds = selectedCandidates
            .Select(candidate => candidate.EpisodeId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<HeadwayConflictEpisode> selectedEpisodes = episodes
            .Where(episode => selectedEpisodeIds.Contains(episode.EpisodeId))
            .ToList();

        foreach (HeadwayConflictEpisode episode in episodes)
        {
            bool sharesIncidentTrain = seedTrains.Contains(episode.FirstTrainIndex) ||
                seedTrains.Contains(episode.SecondTrainIndex);
            bool isAdjacentToSelectedSpan = selectedEpisodes.Any(selected =>
                episode.MinimumStationOrder <= selected.MaximumStationOrder + 1 &&
                episode.MaximumStationOrder >= selected.MinimumStationOrder - 1);
            if (sharesIncidentTrain && isAdjacentToSelectedSpan)
            {
                released.Add(episode.FirstTrainIndex);
                released.Add(episode.SecondTrainIndex);
            }
        }

        return released;
    }
}
