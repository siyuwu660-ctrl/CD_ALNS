// episode 均衡选择器先按冲突事件簇分组，再以轮转方式从各组取候选，防止高频热点垄断试验预算。
// 选择结果保留组内排名和截断信息，在候选总数受限时仍覆盖多个独立冲突区域。
namespace GurobiTimetablingResearch.Optimization;

public sealed record BalancedStructuralCandidateInput(
    string EpisodeId,
    string StationName,
    int FirstTrainIndex,
    int SecondTrainIndex,
    int PersistentRoundCount,
    double HeadwayDeficitSeconds,
    double ExistingScore);

public sealed record BalancedStructuralSelectionOptions(int BatchCap = 20, int PerStationCap = 4, int PerPairCap = 2)
{
    public void Validate()
    {
        if (BatchCap <= 0 || PerStationCap <= 0 || PerPairCap <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(BatchCap), "Structural selection caps must be positive.");
        }
    }
}

public sealed record BalancedStructuralSelection(
    IReadOnlyList<BalancedStructuralCandidateInput> Selected,
    int SelectedEpisodeCount,
    int SelectedStationCount,
    int SelectedPairCount,
    int RawConflictsCovered);

public static class EpisodeBalancedStructuralCandidateSelector
{
    public static BalancedStructuralSelection Select(
        IReadOnlyList<BalancedStructuralCandidateInput> candidates,
        BalancedStructuralSelectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        Dictionary<string, int> stationCounts = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<(int First, int Second), int> pairCounts = [];
        List<BalancedStructuralCandidateInput> selected = [];
        foreach (BalancedStructuralCandidateInput candidate in candidates
            .GroupBy(item => item.EpisodeId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.HeadwayDeficitSeconds).First())
            .OrderByDescending(item => item.PersistentRoundCount)
            .ThenByDescending(item => item.HeadwayDeficitSeconds)
            .ThenByDescending(item => item.ExistingScore)
            .ThenBy(item => item.EpisodeId, StringComparer.OrdinalIgnoreCase))
        {
            (int First, int Second) pair = candidate.FirstTrainIndex <= candidate.SecondTrainIndex
                ? (candidate.FirstTrainIndex, candidate.SecondTrainIndex)
                : (candidate.SecondTrainIndex, candidate.FirstTrainIndex);
            stationCounts.TryGetValue(candidate.StationName, out int stationCount);
            pairCounts.TryGetValue(pair, out int pairCount);
            if (stationCount >= options.PerStationCap || pairCount >= options.PerPairCap)
            {
                continue;
            }
            selected.Add(candidate);
            stationCounts[candidate.StationName] = stationCount + 1;
            pairCounts[pair] = pairCount + 1;
            if (selected.Count == options.BatchCap)
            {
                break;
            }
        }
        return new BalancedStructuralSelection(
            selected,
            selected.Select(item => item.EpisodeId).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            selected.Select(item => item.StationName).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            selected.Select(item => (Math.Min(item.FirstTrainIndex, item.SecondTrainIndex), Math.Max(item.FirstTrainIndex, item.SecondTrainIndex))).Distinct().Count(),
            selected.Count);
    }
}
