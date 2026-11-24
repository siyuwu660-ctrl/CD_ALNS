// 实例投影器按列车索引集合裁剪 TimetablingInstance，同时保留这些列车引用到的区间、径路和规则。
// 投影后的自洽小实例用于滚动窗口与局部修复，减少 Gurobi 模型规模而不改变被保留列车的数据语义。
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

public static class TimetablingInstanceProjector
{
    public static TimetablingInstance Project(
        TimetablingInstance source,
        IReadOnlySet<int> admittedTrainIndices,
        bool includeGlobalStationQuotas = false)
        => Project(
            source,
            admittedTrainIndices,
            includeGlobalStationQuotas ? source.StationStopQuotaRules : Array.Empty<StationStopQuotaRule>());

    public static TimetablingInstance Project(
        TimetablingInstance source,
        IReadOnlySet<int> admittedTrainIndices,
        IReadOnlyList<StationStopQuotaRule> stationStopQuotaRules)
    {
        if (admittedTrainIndices.Count == 0)
        {
            throw new ArgumentException("At least one train must be admitted.", nameof(admittedTrainIndices));
        }

        List<TrainRecord> trains = source.Trains
            .Where(train => admittedTrainIndices.Contains(train.TrainIndex))
            .ToList();
        if (trains.Count != admittedTrainIndices.Count)
        {
            throw new ArgumentException("The admitted train set contains an unknown train index.", nameof(admittedTrainIndices));
        }

        if (trains.Count == source.Trains.Count && ReferenceEquals(stationStopQuotaRules, source.StationStopQuotaRules))
        {
            return source;
        }

        return new TimetablingInstance(
            source.Meta,
            source.Corridor,
            source.Stations,
            source.Sections,
            trains,
            source.TrainStops.Where(stop => admittedTrainIndices.Contains(stop.TrainIndex)).ToList(),
            source.RunTimeRules,
            source.MaintenanceWindows,
            stationStopQuotaRules,
            source.TrainSectionStopQuotaRules.Where(rule => admittedTrainIndices.Contains(rule.TrainIndex)).ToList(),
            source.TrainSectionTravelTimeLimitRules.Where(rule => admittedTrainIndices.Contains(rule.TrainIndex)).ToList(),
            source.Routes,
            source.MovementHeadwayRules,
            source.RouteHeadwayRules,
            source.TrainTrackChoiceRecords.Where(rule => admittedTrainIndices.Contains(rule.TrainIndex)).ToList(),
            source.StationOvertakingProfiles,
            source.BenchmarkTrackRestrictions.Where(rule => admittedTrainIndices.Contains(rule.TrainIndex)).ToList(),
            source.NormalDwellTimeRules.Where(rule => admittedTrainIndices.Contains(rule.TrainIndex)).ToList(),
            source.DatasetDiagnostics);
    }
}
