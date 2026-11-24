// 列车区段旅行时间解析器按列车和走廊端点筛选停站序列，定位规则覆盖的首末事件及中间区间。
// 解析结果供总旅行时间约束构造使用，避免在建模阶段重复处理端点包含关系与方向顺序。
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

internal static class TrainSectionTravelTimeResolver
{
    internal static (TrainStopRecord StartStop, TrainStopRecord EndStop) Resolve(
        TrainRecord train,
        TrainSectionTravelTimeLimitRule rule)
    {
        if (train.TrainIndex != rule.TrainIndex)
        {
            throw new InvalidDataException(
                $"Travel-time rule train {rule.TrainIndex} does not match train {train.TrainIndex}.");
        }

        List<TrainStopRecord> startMatches = train.OrderedStops
            .Where(stop => string.Equals(stop.StationName, rule.SectionStartStation, StringComparison.OrdinalIgnoreCase))
            .ToList();
        List<TrainStopRecord> endMatches = train.OrderedStops
            .Where(stop => string.Equals(stop.StationName, rule.SectionEndStation, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (startMatches.Count != 1 || endMatches.Count != 1)
        {
            throw new InvalidDataException(
                $"Travel-time endpoints must occur exactly once for train {train.TrainIndex}: " +
                $"start='{rule.SectionStartStation}' matches={startMatches.Count}, " +
                $"end='{rule.SectionEndStation}' matches={endMatches.Count}.");
        }

        TrainStopRecord startStop = startMatches[0];
        TrainStopRecord endStop = endMatches[0];
        if (startStop.StationOrder >= endStop.StationOrder)
        {
            throw new InvalidDataException(
                $"Travel-time endpoints are not ordered for train {train.TrainIndex}: " +
                $"start_order={startStop.StationOrder}, end_order={endStop.StationOrder}.");
        }

        return (startStop, endStop);
    }
}
