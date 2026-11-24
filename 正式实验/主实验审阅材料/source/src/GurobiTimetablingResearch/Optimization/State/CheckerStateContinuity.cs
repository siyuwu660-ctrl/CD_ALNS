// 状态连续性组件为解快照和微观冲突集合生成稳定指纹，并校验新一轮是否确实基于上一轮接受态。
// 通过哈希后的规范序列比较状态，可阻止重复轮次、意外回退或未提交试验污染长期 Checker 状态。
using System.Security.Cryptography;
using System.Text;

namespace GurobiTimetablingResearch.Optimization;

internal static class CheckerAcceptedStateContinuityGuard
{
    internal static void EnsureNextInput(
        string acceptedSnapshotFingerprint,
        string nextInputSnapshotFingerprint,
        int roundIndex)
    {
        if (string.Equals(
                acceptedSnapshotFingerprint,
                nextInputSnapshotFingerprint,
                StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Checker accepted-state continuity failed before round {roundIndex}: " +
            $"expected snapshot '{acceptedSnapshotFingerprint}', actual '{nextInputSnapshotFingerprint}'.");
    }
}

internal static class MicroConflictFingerprint
{
    internal static string Calculate(IReadOnlyList<MicroConflictExplanation> conflicts)
    {
        ArgumentNullException.ThrowIfNull(conflicts);
        string canonicalIdentitySet = string.Join(
            '\n',
            conflicts
                .Select(MicroConflictMetricCalculator.BuildCanonicalKey)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase));
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonicalIdentitySet)))
            .ToLowerInvariant();
    }
}

internal static class CheckerAcceptedRoundTerminationResolver
{
    internal static string? Resolve(
        bool fixedSnapshotMultiIterationMode,
        bool microFeasible) =>
        !microFeasible
            ? null
            : fixedSnapshotMultiIterationMode
                ? "MICRO_FEASIBLE_SUCCESS"
                : "micro_feasible";
}
