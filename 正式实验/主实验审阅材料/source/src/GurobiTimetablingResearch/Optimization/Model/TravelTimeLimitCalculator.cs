// 旅行时间上限计算器根据基准旅行时分和允许放大比例得到可进入线性约束的整数秒上界。
// 集中处理舍入与边界条件，保证数据预处理、模型构造和结果验证采用相同计算口径。
namespace GurobiTimetablingResearch.Optimization;

internal static class TravelTimeLimitCalculator
{
    internal static double ResolveElapsedSeconds(double departureAtStartSeconds, double arrivalAtEndSeconds)
    {
        return arrivalAtEndSeconds - departureAtStartSeconds;
    }

    internal static bool IsSatisfied(
        double departureAtStartSeconds,
        double arrivalAtEndSeconds,
        int maxTravelTimeSeconds,
        double toleranceSeconds)
    {
        return ResolveElapsedSeconds(departureAtStartSeconds, arrivalAtEndSeconds)
            <= maxTravelTimeSeconds + toleranceSeconds;
    }
}
