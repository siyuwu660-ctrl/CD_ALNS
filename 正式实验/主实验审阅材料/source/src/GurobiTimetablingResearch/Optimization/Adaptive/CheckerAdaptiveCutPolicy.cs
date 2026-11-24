// 自适应加割策略读取连续轮次的冲突趋势，按改善幅度和停滞次数决定下一轮允许尝试的候选数量。
// 趋势点与决策 record 将观测数据和策略输出分离，便于 Checker 循环稳定控制加割强度。
namespace GurobiTimetablingResearch.Optimization;

internal sealed record CheckerConflictTrendPoint(int RootConflictCount, int EpisodeConflictCount);

internal sealed record CheckerAdaptiveCutDecision(
    int BatchSize,
    int RepairNeighborPadding,
    string Reason);

internal static class CheckerAdaptiveCutPolicy
{
    private const double MaterialImprovementRatio = 0.15;

    internal static CheckerAdaptiveCutDecision Resolve(
        IReadOnlyList<CheckerConflictTrendPoint> history,
        int configuredMaxBatch,
        int basePadding)
    {
        int maximumBatch = Math.Max(1, configuredMaxBatch);
        int minimumPadding = Math.Max(0, basePadding);
        if (history.Count < 2)
        {
            return new CheckerAdaptiveCutDecision(maximumBatch, minimumPadding, "initial_or_insufficient_history");
        }

        int current = ResolvePrimaryMetric(history[^1]);
        int previous = ResolvePrimaryMetric(history[^2]);
        if (current > previous)
        {
            bool consecutiveRegression = history.Count >= 3 &&
                previous > ResolvePrimaryMetric(history[^3]);
            return consecutiveRegression
                ? new CheckerAdaptiveCutDecision(1, minimumPadding + 2, "consecutive_conflict_regression")
                : new CheckerAdaptiveCutDecision(Math.Max(1, maximumBatch / 2), minimumPadding + 1, "conflict_regression");
        }

        bool recoveringFromRegression = history.Count >= 3 &&
            previous > ResolvePrimaryMetric(history[^3]) &&
            current < previous;
        if (recoveringFromRegression)
        {
            return new CheckerAdaptiveCutDecision(
                Math.Max(1, maximumBatch / 2),
                minimumPadding + 1,
                "recovery_after_regression");
        }

        double improvementRatio = previous <= 0
            ? 0.0
            : (double)(previous - current) / previous;
        if (improvementRatio >= MaterialImprovementRatio)
        {
            return new CheckerAdaptiveCutDecision(maximumBatch, minimumPadding, "material_conflict_improvement");
        }

        return new CheckerAdaptiveCutDecision(
            Math.Max(1, maximumBatch - 1),
            minimumPadding,
            "flat_conflict_response");
    }

    private static int ResolvePrimaryMetric(CheckerConflictTrendPoint point) =>
        point.EpisodeConflictCount >= 0 ? point.EpisodeConflictCount : point.RootConflictCount;
}
