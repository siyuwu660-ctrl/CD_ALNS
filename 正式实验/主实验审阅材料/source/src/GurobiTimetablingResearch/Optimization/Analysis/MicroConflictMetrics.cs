// 微观冲突指标计算器从报告中提取总冲突、结构冲突、热点站点和严重度等可比较标量。
// 审计结果解析器比较试探前后的指标向量，将候选判定为改善、持平或恶化，作为事务提交条件。
namespace GurobiTimetablingResearch.Optimization;

internal sealed record MicroConflictMetrics(
    int RawConflictCount,
    int CanonicalConflictCount,
    int RootConflictCount,
    int EpisodeConflictCount,
    int ConflictedStationCount,
    int AffectedTrainCount,
    int ConnectedComponentCount);

internal static class MicroConflictMetricCalculator
{
    private static readonly HashSet<string> RouteDomainKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "missing_incoming_route",
        "missing_outgoing_route",
        "empty_track_domain",
        "benchmark_track_domain_empty"
    };

    internal static MicroConflictMetrics Calculate(IReadOnlyList<MicroConflictExplanation> conflicts)
    {
        int canonicalCount = conflicts
            .Select(BuildCanonicalKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        int rootCount = conflicts
            .Select(BuildRootKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        int stationCount = conflicts
            .Select(conflict => conflict.StationName)
            .Where(station => !string.IsNullOrWhiteSpace(station))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        int headwayEpisodeCount = HeadwayConflictEpisodeAnalyzer.Analyze(
            conflicts,
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)).Count;
        int nonHeadwayEpisodeCount = conflicts
            .Where(conflict => !string.Equals(conflict.ConflictKind, "exact_headway_infeasible", StringComparison.OrdinalIgnoreCase))
            .Select(BuildRootKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        int affectedTrainCount = conflicts
            .SelectMany(conflict => new[] { conflict.FirstTrainIndex, conflict.SecondTrainIndex })
            .Where(train => train.HasValue)
            .Select(train => train!.Value)
            .Distinct()
            .Count();
        return new MicroConflictMetrics(
            conflicts.Count,
            canonicalCount,
            rootCount,
            headwayEpisodeCount + nonHeadwayEpisodeCount,
            stationCount,
            affectedTrainCount,
            CountConnectedComponents(conflicts));
    }

    internal static int CountConnectedComponents(IReadOnlyList<MicroConflictExplanation> conflicts)
    {
        if (conflicts.Count == 0)
        {
            return 0;
        }
        int[] parent = Enumerable.Range(0, conflicts.Count).ToArray();
        for (int first = 0; first < conflicts.Count; first++)
        {
            for (int second = first + 1; second < conflicts.Count; second++)
            {
                if (AreConnected(conflicts[first], conflicts[second]))
                {
                    Union(parent, first, second);
                }
            }
        }
        return Enumerable.Range(0, conflicts.Count).Select(index => Find(parent, index)).Distinct().Count();
    }

    private static bool AreConnected(MicroConflictExplanation first, MicroConflictExplanation second)
    {
        if (!string.IsNullOrWhiteSpace(first.StationName) &&
            string.Equals(first.StationName, second.StationName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        HashSet<int> firstTrains = new[] { first.FirstTrainIndex, first.SecondTrainIndex }
            .Where(item => item.HasValue).Select(item => item!.Value).ToHashSet();
        return new[] { second.FirstTrainIndex, second.SecondTrainIndex }
            .Where(item => item.HasValue).Select(item => item!.Value).Any(firstTrains.Contains);
    }

    private static int Find(int[] parent, int item)
    {
        while (parent[item] != item)
        {
            parent[item] = parent[parent[item]];
            item = parent[item];
        }
        return item;
    }

    private static void Union(int[] parent, int first, int second)
    {
        int firstRoot = Find(parent, first);
        int secondRoot = Find(parent, second);
        if (firstRoot != secondRoot)
        {
            parent[secondRoot] = firstRoot;
        }
    }

    internal static string BuildCanonicalKey(MicroConflictExplanation conflict)
    {
        return string.Join("::",
            conflict.ConflictKind,
            conflict.StationName,
            conflict.FirstTrainIndex?.ToString() ?? "-",
            conflict.SecondTrainIndex?.ToString() ?? "-",
            conflict.StationOrder?.ToString() ?? "-",
            conflict.SecondStationOrder?.ToString() ?? "-",
            conflict.FirstEventSide?.ToString() ?? "-",
            conflict.SecondEventSide?.ToString() ?? "-",
            conflict.FirstSectionName ?? "-",
            conflict.SecondSectionName ?? "-",
            conflict.ResourceKey);
    }

    internal static string BuildRootKey(MicroConflictExplanation conflict)
    {
        if (!string.IsNullOrWhiteSpace(conflict.RootConflictKey))
        {
            return conflict.RootConflictKey;
        }

        if (RouteDomainKinds.Contains(conflict.ConflictKind) && conflict.FirstTrainIndex.HasValue)
        {
            return string.Join("::",
                "route_domain",
                conflict.StationName,
                conflict.FirstTrainIndex.Value,
                conflict.StationOrder?.ToString() ?? "-");
        }

        return BuildCanonicalKey(conflict);
    }
}

internal enum CutAuditOutcome
{
    Accepted,
    Retained,
    Rejected,
    UnknownTimeLimit,
    UnknownNodeLimit,
    NoIncumbent
}

internal static class CutAuditOutcomeResolver
{
    internal static CutAuditOutcome Resolve(bool hasIncumbent, int solverStatus, bool accepted)
    {
        if (accepted)
        {
            return CutAuditOutcome.Accepted;
        }

        if (!hasIncumbent)
        {
            return solverStatus switch
            {
                9 => CutAuditOutcome.UnknownTimeLimit,
                8 => CutAuditOutcome.UnknownNodeLimit,
                _ => CutAuditOutcome.NoIncumbent
            };
        }

        return CutAuditOutcome.Rejected;
    }
}
