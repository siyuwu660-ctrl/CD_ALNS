// 安全径路域解析器枚举两个停站的可选径路笛卡尔积，分析是否存在缺失追踪间隔或不可判定组合。
// 结果区分全域安全、部分安全和必须保守处理的情况，防止主问题因缺少径路规则而低估车站冲突。
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

internal enum RouteDomainHeadwayKind
{
    NoAdditionalConstraint,
    UnavoidablePositive,
    MixedRouteChoiceRisk
}

internal sealed record RouteDomainHeadwayAnalysis(
    RouteDomainHeadwayKind Kind,
    int SafeLowerBoundSeconds,
    int RecordedPairCount,
    int MissingPairCount);

internal static class SafeRouteDomainHeadwayResolver
{
    internal static int ResolveLowerBound(
        IReadOnlyList<RouteRecord> leadingRoutes,
        IReadOnlyList<RouteRecord> followingRoutes,
        Func<int, int, int?> findRecordedHeadway,
        IReadOnlySet<string>? leadingAllowedTracks = null,
        IReadOnlySet<string>? followingAllowedTracks = null)
        => Analyze(
            leadingRoutes,
            followingRoutes,
            findRecordedHeadway,
            leadingAllowedTracks,
            followingAllowedTracks).SafeLowerBoundSeconds;

    internal static RouteDomainHeadwayAnalysis Analyze(
        IReadOnlyList<RouteRecord> leadingRoutes,
        IReadOnlyList<RouteRecord> followingRoutes,
        Func<int, int, int?> findRecordedHeadway,
        IReadOnlySet<string>? leadingAllowedTracks = null,
        IReadOnlySet<string>? followingAllowedTracks = null)
    {
        List<RouteRecord> filteredLeading = FilterByTracks(leadingRoutes, leadingAllowedTracks);
        List<RouteRecord> filteredFollowing = FilterByTracks(followingRoutes, followingAllowedTracks);
        if (filteredLeading.Count == 0 || filteredFollowing.Count == 0)
        {
            return new RouteDomainHeadwayAnalysis(RouteDomainHeadwayKind.NoAdditionalConstraint, 0, 0, 0);
        }

        int minimum = int.MaxValue;
        int recordedPairCount = 0;
        int missingPairCount = 0;
        bool hasPositive = false;
        foreach (RouteRecord leading in filteredLeading)
        {
            foreach (RouteRecord following in filteredFollowing)
            {
                int? recorded = findRecordedHeadway(leading.RouteId, following.RouteId);
                if (!recorded.HasValue)
                {
                    missingPairCount++;
                    continue;
                }

                recordedPairCount++;
                hasPositive |= recorded.Value > 0;
                minimum = Math.Min(minimum, recorded.Value);
            }
        }

        if (missingPairCount > 0)
        {
            return new RouteDomainHeadwayAnalysis(
                hasPositive ? RouteDomainHeadwayKind.MixedRouteChoiceRisk : RouteDomainHeadwayKind.NoAdditionalConstraint,
                0,
                recordedPairCount,
                missingPairCount);
        }

        int safeLowerBound = minimum == int.MaxValue ? 0 : minimum;
        return new RouteDomainHeadwayAnalysis(
            safeLowerBound > 0 ? RouteDomainHeadwayKind.UnavoidablePositive : RouteDomainHeadwayKind.NoAdditionalConstraint,
            safeLowerBound,
            recordedPairCount,
            0);
    }

    private static List<RouteRecord> FilterByTracks(
        IReadOnlyList<RouteRecord> routes,
        IReadOnlySet<string>? allowedTracks)
    {
        return routes
            .Where(route => allowedTracks is null || allowedTracks.Contains(route.TrackName))
            .ToList();
    }
}
