// 受控候选解析器用规范候选键从可用集合中精确定位唯一对象，并显式拒绝缺失或重复匹配。
// 该组件服务于命令行定点试验，避免排序变化导致用户指定的候选被替换成其他结构边。
namespace GurobiTimetablingResearch.Optimization;

internal static class ControlledStructuralCandidateResolver
{
    internal static T ResolveExact<T>(
        IReadOnlyList<T> candidates,
        string candidateId,
        Func<T, string> identitySelector)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);
        ArgumentNullException.ThrowIfNull(identitySelector);

        List<T> matches = candidates
            .Where(candidate => string.Equals(
                identitySelector(candidate),
                candidateId,
                StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count != 1)
        {
            throw new InvalidOperationException(
                $"Controlled structural candidate '{candidateId}' must match exactly one candidate; " +
                $"exact_match_count={matches.Count}.");
        }
        return matches[0];
    }
}
