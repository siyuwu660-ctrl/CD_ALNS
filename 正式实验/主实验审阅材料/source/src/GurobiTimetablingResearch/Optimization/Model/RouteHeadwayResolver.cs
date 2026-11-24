// 径路追踪间隔解析器按车站、方向和径路对查找最具体规则，为车站事件冲突约束提供安全间隔秒数。
// 解析逻辑统一处理对称组合与缺失项，避免不同建模阶段对同一径路对采用不同默认值。
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

internal static class RouteHeadwayResolver
{
    internal static int? ResolveMinimumRecordedHeadway(
        IReadOnlyList<RouteRecord> leadingRoutes,
        IReadOnlyList<RouteRecord> followingRoutes,
        Func<int, int, int?> findRecordedHeadway)
    {
        int? minimum = null;

        foreach (RouteRecord leadingRoute in leadingRoutes)
        {
            foreach (RouteRecord followingRoute in followingRoutes)
            {
                int? recordedHeadway = findRecordedHeadway(leadingRoute.RouteId, followingRoute.RouteId);
                if (recordedHeadway.HasValue)
                {
                    minimum = minimum.HasValue
                        ? Math.Min(minimum.Value, recordedHeadway.Value)
                        : recordedHeadway.Value;
                }
            }
        }

        return minimum;
    }
}
