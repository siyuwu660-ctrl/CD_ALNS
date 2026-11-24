// 正常停时解析器把按列车、车站序号和站名定义的规则建立多级索引，为每个停站选择最具体的上下界。
// 解析结果标记匹配来源，既供 MILP 设置停时约束，也供数据诊断识别缺省规则和覆盖关系。
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

public sealed record NormalDwellResolution(
    bool Applies,
    int NormalMinDwellSeconds,
    int NormalMaxDwellSeconds,
    int WaitingMaxDwellSeconds,
    bool IsFallback);

public static class NormalDwellRuleIndex
{
    public static IReadOnlyDictionary<(int TrainIndex, int StationOrder), NormalDwellTimeRule> Create(
        IReadOnlyList<NormalDwellTimeRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        Dictionary<(int TrainIndex, int StationOrder), NormalDwellTimeRule> result = [];
        foreach (NormalDwellTimeRule rule in rules)
        {
            if (rule.MinDwellSeconds < 0 || rule.MaxDwellSeconds < rule.MinDwellSeconds)
            {
                throw new InvalidDataException(
                    $"Invalid normal dwell range for train {rule.TrainIndex}, station order {rule.StationOrder}.");
            }
            if (!result.TryAdd((rule.TrainIndex, rule.StationOrder), rule))
            {
                throw new InvalidDataException(
                    $"Duplicate normal dwell key: train {rule.TrainIndex}, station order {rule.StationOrder}.");
            }
        }
        return result;
    }
}

public static class NormalDwellTimeResolver
{
    public static NormalDwellResolution Resolve(
        IReadOnlyList<NormalDwellTimeRule> rules,
        TrainStopRecord stop)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(stop);
        if (stop.StopRole != TrainStopRole.Intermediate)
        {
            return new NormalDwellResolution(false, 0, 0, stop.MaxDwellSeconds, false);
        }

        NormalDwellTimeRule? rule = rules.FirstOrDefault(item =>
            item.TrainIndex == stop.TrainIndex && item.StationOrder == stop.StationOrder);
        if (rule is null)
        {
            return new NormalDwellResolution(
                true,
                stop.MinDwellSeconds,
                stop.MaxDwellSeconds,
                stop.MaxDwellSeconds,
                true);
        }
        if (!string.Equals(rule.StationName, stop.StationName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Normal dwell station mismatch for train {stop.TrainIndex}, order {stop.StationOrder}: " +
                $"'{rule.StationName}' versus '{stop.StationName}'.");
        }
        if (stop.MaxDwellSeconds < rule.MaxDwellSeconds)
        {
            throw new InvalidDataException(
                $"Waiting dwell maximum is below normal dwell maximum for train {stop.TrainIndex}, " +
                $"station {stop.StationName}.");
        }

        return new NormalDwellResolution(
            true,
            rule.MinDwellSeconds,
            rule.MaxDwellSeconds,
            stop.MaxDwellSeconds,
            false);
    }
}
