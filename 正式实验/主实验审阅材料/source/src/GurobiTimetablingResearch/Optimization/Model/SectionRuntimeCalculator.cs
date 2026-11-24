// 区间运行时分计算器根据运行标尺、方向和起停附加时分组合出列车通过相邻停站所需的最小时间。
// 该纯计算组件集中保持主模型、校验器和修复阶段对区间耗时的解释一致。
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

internal static class SectionRuntimeCalculator
{
    internal static int ResolveRequiredSeconds(
        RunTimeRule rule,
        bool stopsAtSectionOrigin,
        bool stopsAtSectionDestination)
    {
        return rule.RunTimeSeconds
            + (stopsAtSectionOrigin ? rule.StartAddSeconds : 0)
            + (stopsAtSectionDestination ? rule.StopAddSeconds : 0);
    }

    internal static bool IsExactWithinTolerance(
        double realizedSeconds,
        double requiredSeconds,
        double toleranceSeconds)
    {
        return Math.Abs(realizedSeconds - requiredSeconds) <= toleranceSeconds;
    }
}
