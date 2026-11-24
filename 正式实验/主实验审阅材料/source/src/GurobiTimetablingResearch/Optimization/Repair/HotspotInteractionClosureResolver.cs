namespace GurobiTimetablingResearch.Optimization;

/// <summary>
/// 汇总一个车站作业在当前硬时间域内可能使用的股道和进路，用于计算热点资源闭包。
/// </summary>
public sealed record HotspotRequirementDomain(
    string RequirementId,
    string StationName,
    int TrainIndex,
    double EarliestOccupationStartSeconds,
    double LatestOccupationEndSeconds,
    IReadOnlyList<string> TrackNames,
    IReadOnlyList<int> RouteIds);

/// <summary>
/// 返回完整热点闭包；若规模上限不足，则不返回局部子集，避免遗漏同站资源占用者。
/// </summary>
public sealed record HotspotInteractionClosureResult(
    bool IsComplete,
    IReadOnlyList<HotspotRequirementDomain> Requirements,
    string Status);

/// <summary>
/// 通过股道交集和已记录的进路间隔关系递归扩展种子 core，得到同站联合建模范围。
/// </summary>
public static class HotspotInteractionClosureResolver
{
    public static HotspotInteractionClosureResult Resolve(
        IReadOnlyList<HotspotRequirementDomain> domains,
        IReadOnlyList<string> seedRequirementIds,
        int maximumRequirementCount,
        Func<int, int, int?> findRecordedHeadway)
    {
        ArgumentNullException.ThrowIfNull(domains);
        ArgumentNullException.ThrowIfNull(seedRequirementIds);
        ArgumentNullException.ThrowIfNull(findRecordedHeadway);
        if (maximumRequirementCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumRequirementCount));
        }
        if (seedRequirementIds.Count == 0)
        {
            throw new ArgumentException("A hotspot closure requires at least one seed requirement.", nameof(seedRequirementIds));
        }

        Dictionary<string, HotspotRequirementDomain> byId = domains.ToDictionary(
            domain => domain.RequirementId,
            StringComparer.OrdinalIgnoreCase);
        List<HotspotRequirementDomain> seeds = seedRequirementIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(id => byId.TryGetValue(id, out HotspotRequirementDomain? domain)
                ? domain
                : throw new InvalidOperationException($"Hotspot seed requirement '{id}' does not exist."))
            .ToList();
        string stationName = seeds[0].StationName;
        if (seeds.Any(seed => !string.Equals(seed.StationName, stationName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("A hotspot interaction closure cannot span multiple stations.");
        }

        List<HotspotRequirementDomain> stationDomains = domains
            .Where(domain => string.Equals(domain.StationName, stationName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        HashSet<string> selected = new(seedRequirementIds, StringComparer.OrdinalIgnoreCase);
        bool changed;
        do
        {
            changed = false;
            foreach (HotspotRequirementDomain candidate in stationDomains.Where(item => !selected.Contains(item.RequirementId)))
            {
                if (!stationDomains.Where(item => selected.Contains(item.RequirementId))
                    .Any(selectedDomain => CanInteract(selectedDomain, candidate, findRecordedHeadway)))
                {
                    continue;
                }

                selected.Add(candidate.RequirementId);
                changed = true;
                if (selected.Count > maximumRequirementCount)
                {
                    return new HotspotInteractionClosureResult(
                        false,
                        [],
                        "hotspot_scope_budget_exhausted");
                }
            }
        }
        while (changed);

        IReadOnlyList<HotspotRequirementDomain> closure = stationDomains
            .Where(domain => selected.Contains(domain.RequirementId))
            .OrderBy(domain => domain.RequirementId, StringComparer.Ordinal)
            .ToList();
        return new HotspotInteractionClosureResult(true, closure, "complete");
    }

    private static bool CanInteract(
        HotspotRequirementDomain first,
        HotspotRequirementDomain second,
        Func<int, int, int?> findRecordedHeadway)
    {
        bool occupationDomainsOverlap =
            first.EarliestOccupationStartSeconds <= second.LatestOccupationEndSeconds &&
            second.EarliestOccupationStartSeconds <= first.LatestOccupationEndSeconds;
        bool sharesPotentialTrack = first.TrackNames.Intersect(
            second.TrackNames,
            StringComparer.OrdinalIgnoreCase).Any();
        bool hasPotentialRouteHeadway = first.RouteIds.Any(firstRoute => second.RouteIds.Any(secondRoute =>
        {
            int? forward = findRecordedHeadway(firstRoute, secondRoute);
            int? reverse = findRecordedHeadway(secondRoute, firstRoute);
            bool forwardCanBind = forward.HasValue &&
                                  second.EarliestOccupationStartSeconds - first.LatestOccupationEndSeconds < forward.Value;
            bool reverseCanBind = reverse.HasValue &&
                                  first.EarliestOccupationStartSeconds - second.LatestOccupationEndSeconds < reverse.Value;
            return forwardCanBind || reverseCanBind;
        }));
        return sharesPotentialTrack && occupationDomainsOverlap || hasPotentialRouteHeadway;
    }
}
