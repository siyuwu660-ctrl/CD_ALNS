// 运行时分补偿范围按修复邻域中的列车和区间计算允许松弛的变量键及上下界。
// 作用域对象把补偿严格限制在当前候选关联区域，防止局部修复借助无关区间的大范围延时获得虚假可行性。
namespace GurobiTimetablingResearch.Optimization;

public sealed record RepairRunningTimeSupplementContext(
    RepairDecisionLevel DecisionLevel,
    int TrainIndex,
    string SectionName,
    int FromStationOrder,
    int ToStationOrder,
    int BaseRuntimeSeconds,
    double SnapshotValue,
    double RepairRatio,
    bool IsSelectedTrain,
    RepairGeographicSpan? GeographicSpan);

public sealed record RepairRunningTimeSupplementBounds(
    bool IsReleased,
    double LowerBound,
    double UpperBound);

public static class RepairRunningTimeSupplementScope
{
    public static RepairRunningTimeSupplementBounds Resolve(
        RepairRunningTimeSupplementContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(context.SectionName))
        {
            throw new ArgumentException("Repair supplement section name is required.", nameof(context));
        }
        if (context.BaseRuntimeSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(context), context.BaseRuntimeSeconds,
                "Repair supplement base runtime cannot be negative.");
        }
        if (!double.IsFinite(context.SnapshotValue) || context.SnapshotValue < 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(context), context.SnapshotValue,
                "Repair supplement snapshot value must be finite and non-negative.");
        }

        double ratio = CheckerRepairSupplementOptions.Create(context.RepairRatio).Ratio;
        if (context.GeographicSpan is not null &&
            context.GeographicSpan.MinimumStationOrder > context.GeographicSpan.MaximumStationOrder)
        {
            throw new ArgumentException("Repair geographic span minimum cannot exceed its maximum.", nameof(context));
        }

        bool isInsideScope =
            context.DecisionLevel >= RepairDecisionLevel.TimeAndRunningSupplement &&
            context.IsSelectedTrain &&
            context.GeographicSpan is not null &&
            context.GeographicSpan.Contains(context.FromStationOrder) &&
            context.GeographicSpan.Contains(context.ToStationOrder);
        if (!isInsideScope)
        {
            return new RepairRunningTimeSupplementBounds(
                false,
                context.SnapshotValue,
                context.SnapshotValue);
        }

        double upperBound = Math.Ceiling(context.BaseRuntimeSeconds * ratio);
        return new RepairRunningTimeSupplementBounds(true, 0.0, upperBound);
    }
}
