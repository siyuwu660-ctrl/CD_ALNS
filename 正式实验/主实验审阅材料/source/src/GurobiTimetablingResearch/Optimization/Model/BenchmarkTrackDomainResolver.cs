// 基准股道域解析器把站点股道限制与可选股道集合求交，得到每个停站实际允许进入模型的离散选择域。
// 返回对象同时保留解析结果和限制来源，供预处理诊断及径路变量构造判断域是否为空。
namespace GurobiTimetablingResearch.Optimization;

internal sealed record BenchmarkTrackDomainResult(
    IReadOnlyList<string> OrdinaryTracks,
    IReadOnlyList<string> BenchmarkTracks,
    IReadOnlyList<string> AllowedTracks,
    bool HasBenchmarkRestriction);

internal static class BenchmarkTrackDomainResolver
{
    internal static BenchmarkTrackDomainResult Resolve(
        IReadOnlyList<string> ordinaryTracks,
        IReadOnlyList<string>? benchmarkTracks)
    {
        List<string> normalizedOrdinary = ordinaryTracks
            .Where(track => !string.IsNullOrWhiteSpace(track))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(track => track, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (benchmarkTracks is null)
        {
            return new BenchmarkTrackDomainResult(
                normalizedOrdinary,
                Array.Empty<string>(),
                normalizedOrdinary,
                false);
        }

        List<string> normalizedBenchmark = benchmarkTracks
            .Where(track => !string.IsNullOrWhiteSpace(track))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(track => track, StringComparer.OrdinalIgnoreCase)
            .ToList();
        List<string> allowed = normalizedOrdinary
            .Intersect(normalizedBenchmark, StringComparer.OrdinalIgnoreCase)
            .OrderBy(track => track, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new BenchmarkTrackDomainResult(
            normalizedOrdinary,
            normalizedBenchmark,
            allowed,
            true);
    }
}
