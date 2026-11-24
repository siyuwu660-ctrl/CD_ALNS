// 本文件集中定义不可变 record 与只读集合，作为 CSV 数据层、主问题、微观检查器和结果导出器之间的标准数据契约。
// TimetablingInstance 聚合列车、区间、径路、追踪间隔和配额规则，其余记录按业务实体拆分，便于建立字典索引并安全传递快照。
using System.Collections.ObjectModel;

namespace GurobiTimetablingResearch.Domain;

/// <summary>
/// 已加载统一数据集的基础元信息。
/// </summary>
public sealed record InstanceMeta(string InstanceName, string SourceDirectory, string TimeUnit);

/// <summary>
/// 从 !计算目标区段.csv 推导出的目标走廊定义。
/// 这个对象既可用于数据校验，也可用于后续 rolling horizon 或 corridor-aware 逻辑。
/// </summary>
public sealed record CorridorDefinition(
    IReadOnlyList<string> OrderedSectionNames,
    IReadOnlyDictionary<string, int> SectionOrderMap);

/// <summary>
/// 供模型层直接使用的标准化区间记录。
/// </summary>
public sealed record SectionRecord(
    string SectionName,
    string FromStation,
    string ToStation,
    TravelDirection DirectionTag,
    string SectionType,
    double? Length);

/// <summary>
/// 列车层对象，内部引用该列车按顺序排列的停站记录。
/// 虽然优化代码大多直接遍历 OrderedStops，但保留独立的列车对象会让
/// 统计、日志和后续分解逻辑清晰很多。
/// </summary>
public sealed record TrainRecord(
    int TrainIndex,
    string TrainNo,
    string TrainType,
    IReadOnlyList<TrainStopRecord> OrderedStops);

/// <summary>
/// 两套案例数据统一后得到的标准停站记录，完整保存 master、slave 和 Checker 共同使用的时刻、停站及标尺字段。
/// </summary>
public sealed record TrainStopRecord(
    string StopId,
    int TrainIndex,
    string TrainNo,
    string TrainType,
    int StationOrder,
    string StationName,
    string? IncomingSectionName,
    int? RawArrTimeSeconds,
    int? RawDepTimeSeconds,
    int EarliestArrSeconds,
    int LatestArrSeconds,
    int EarliestDepSeconds,
    int LatestDepSeconds,
    bool IdealStop,
    int StopDeviationWeight,
    StopRequirement StopRequirement,
    int MinDwellSeconds,
    int MaxDwellSeconds,
    string? RunScaleName,
    string? SlowScaleName,
    int MinSlowAdjustmentSeconds,
    int MaxSlowAdjustmentSeconds,
    int LeftDepartureWeight,
    int RightDepartureWeight,
    TrainStopRole StopRole);

public sealed record NormalDwellTimeRule(
    int TrainIndex,
    int StationOrder,
    string StationName,
    int MinDwellSeconds,
    int MaxDwellSeconds);

public sealed record DatasetDiagnostic(
    string DiagnosticType,
    string Message,
    int? TrainIndex = null,
    int? StationOrder = null,
    string? StationName = null);

/// <summary>
/// 从 运行标尺.csv 按方向展开后的运行时间规则。
/// </summary>
public sealed record RunTimeRule(
    string ScaleName,
    string SectionName,
    TravelDirection Direction,
    int RunTimeSeconds,
    int StartAddSeconds,
    int StopAddSeconds);

/// <summary>
/// 从 天窗.csv 标准化得到的天窗规则。
/// </summary>
public sealed record MaintenanceWindowRule(
    string SectionName,
    string FromStation,
    bool IncludeFromStation,
    string ToStation,
    bool IncludeToStation,
    TravelDirection Direction,
    int StartTimeSeconds,
    int EndTimeSeconds);

/// <summary>
/// 车站-时段层面的停站次数约束。
/// 虽然第一版基础 MILP 还没有真正用到它，但数据层已经先加载进来，
/// 方便后面逐步增强模型。
/// </summary>
public sealed record StationStopQuotaRule(
    string StationName,
    string TrainType,
    int StartTimeSeconds,
    int EndTimeSeconds,
    int MinStops,
    int MaxStops);

/// <summary>
/// 列车在指定区段上的停站次数约束。
/// 这类规则常用于限制某一列车在某段走廊内最多/最少停多少站，
/// 是当前 master 中非常自然的一类宏观约束。
/// </summary>
public sealed record TrainSectionStopQuotaRule(
    int TrainIndex,
    string SectionStartStation,
    string SectionEndStation,
    int MinStops,
    int MaxStops,
    bool IncludeStartStation,
    bool IncludeEndStation);

public sealed record TrainSectionTravelTimeLimitRule(
    int TrainIndex,
    string SectionStartStation,
    string SectionEndStation,
    int MaxTravelTimeSeconds);

public sealed record BenchmarkTrackRestriction(
    int TrainIndex,
    int StationOrder,
    string StationName,
    IReadOnlyList<string> AllowedTrackNames);

/// <summary>
/// 统一后的精确进路记录。
/// 当前阶段不在 master 中直接枚举它，但需要依赖它把微观进路压缩成 route family。
/// </summary>
public sealed record RouteRecord(
    int RouteId,
    string StationName,
    string SectionName,
    string TrackName,
    string OperationType,
    MovementEvent MovementEvent);

/// <summary>
/// 由精确进路对间隔聚合得到的 route family 级代理间隔。
/// 当前先采用“常见模式 + 安全裕量”的方式构造代理值：
/// 1. 先在同一站、同一前后 family 组合内统计最常见的精确间隔；
/// 2. 再附加一个统一安全裕量，得到供 master 使用的中观代理间隔。
/// </summary>
public sealed record MovementHeadwayRule(
    string StationName,
    MovementEvent LeadingEvent,
    MovementEvent FollowingEvent,
    int ProxyHeadwaySeconds,
    int ModeHeadwaySeconds,
    int SafetyMarginSeconds,
    int SampleCount);

/// <summary>
/// 精确进路对的最小间隔规则。
/// 微观 Checker 使用该原始规则评估车站可实施性，它独立于宏观主问题使用的代理间隔。
/// </summary>
public sealed record RouteHeadwayRule(
    int LeadingRouteId,
    int FollowingRouteId,
    int MinHeadwaySeconds);

/// <summary>
/// 列车在指定车站的候选股道请求。
/// 微观 Checker 按停站或通过模式读取候选集合，判断是否存在无冲突的股道分配。
/// </summary>
public sealed record TrainTrackChoiceRecord(
    int TrainIndex,
    int StationOrder,
    string StationName,
    bool IsStoppingChoice,
    IReadOnlyList<string> AllowedTrackNames);

/// <summary>
/// 初始稀疏冲突图中的候选列车对。
/// 它不是完整 pairwise precedence 集，而是顺序驱动的初始冲突边，
/// 后续若 checker 发现遗漏冲突，再动态补边。
/// </summary>
public sealed record SectionConflictCandidate(
    string SectionName,
    int FirstTrainIndex,
    int SecondTrainIndex,
    int FirstStopOrder,
    int SecondStopOrder,
    int FirstEndStopOrder,
    int SecondEndStopOrder,
    int RankGap,
    string StartStationName,
    string EndStationName);

public sealed record SectionConflictEdgeRequest(
    string SectionName,
    int FirstTrainIndex,
    int SecondTrainIndex);

public sealed record StationEventConflictCandidate(
    string StationName,
    int FirstTrainIndex,
    int SecondTrainIndex,
    int FirstStationOrder,
    int SecondStationOrder,
    StationEventSide FirstSide,
    StationEventSide SecondSide);

/// <summary>
/// 站点越行能力画像。
/// 它不是微观股道/进路优化结果，而是 master 用来判断“相邻区间顺序是否允许变化”的中观证据。
/// </summary>
public sealed record StationOvertakingProfile(
    string StationName,
    bool IsBoundaryStation,
    int CapabilityScore,
    bool IsOvertakingCapable,
    int TrackCount,
    int RouteTrackCount,
    bool HasDepartureEvent,
    bool HasPassingEvent,
    bool HasStopTrackEvidence,
    bool HasPassTrackEvidence,
    int MustStopCount,
    int OptionalStopCount,
    int MustPassCount);

/// <summary>
/// 完整的内存实例对象，供模型构建器直接消费。
/// </summary>
public sealed class TimetablingInstance
{
    public TimetablingInstance(
        InstanceMeta meta,
        CorridorDefinition corridor,
        IReadOnlyList<string> stations,
        IReadOnlyList<SectionRecord> sections,
        IReadOnlyList<TrainRecord> trains,
        IReadOnlyList<TrainStopRecord> trainStops,
        IReadOnlyList<RunTimeRule> runTimeRules,
        IReadOnlyList<MaintenanceWindowRule> maintenanceWindows,
        IReadOnlyList<StationStopQuotaRule> stationStopQuotaRules,
        IReadOnlyList<TrainSectionStopQuotaRule> trainSectionStopQuotaRules,
        IReadOnlyList<TrainSectionTravelTimeLimitRule> trainSectionTravelTimeLimitRules,
        IReadOnlyList<RouteRecord> routes,
        IReadOnlyList<MovementHeadwayRule> movementHeadwayRules,
        IReadOnlyList<RouteHeadwayRule> routeHeadwayRules,
        IReadOnlyList<TrainTrackChoiceRecord> trainTrackChoiceRecords,
        IReadOnlyList<StationOvertakingProfile> stationOvertakingProfiles,
        IReadOnlyList<BenchmarkTrackRestriction> benchmarkTrackRestrictions,
        IReadOnlyList<NormalDwellTimeRule>? normalDwellTimeRules = null,
        IReadOnlyList<DatasetDiagnostic>? datasetDiagnostics = null)
    {
        Meta = meta;
        Corridor = corridor;
        Stations = stations;
        Sections = sections;
        Trains = trains;
        TrainStops = trainStops;
        RunTimeRules = runTimeRules;
        MaintenanceWindows = maintenanceWindows;
        StationStopQuotaRules = stationStopQuotaRules;
        TrainSectionStopQuotaRules = trainSectionStopQuotaRules;
        TrainSectionTravelTimeLimitRules = trainSectionTravelTimeLimitRules;
        Routes = routes;
        MovementHeadwayRules = movementHeadwayRules;
        RouteHeadwayRules = routeHeadwayRules;
        TrainTrackChoiceRecords = trainTrackChoiceRecords;
        StationOvertakingProfiles = stationOvertakingProfiles;
        BenchmarkTrackRestrictions = benchmarkTrackRestrictions;
        NormalDwellTimeRules = normalDwellTimeRules ?? Array.Empty<NormalDwellTimeRule>();
        DatasetDiagnostics = datasetDiagnostics ?? Array.Empty<DatasetDiagnostic>();

        SectionByName = new ReadOnlyDictionary<string, SectionRecord>(sections.ToDictionary(section => section.SectionName, StringComparer.OrdinalIgnoreCase));
        StopsById = new ReadOnlyDictionary<string, TrainStopRecord>(trainStops.ToDictionary(stop => stop.StopId, StringComparer.OrdinalIgnoreCase));
        MovementHeadwayRuleMap = new ReadOnlyDictionary<string, MovementHeadwayRule>(
            movementHeadwayRules.ToDictionary(
                rule => BuildMovementHeadwayKey(rule.StationName, rule.LeadingEvent, rule.FollowingEvent),
                StringComparer.OrdinalIgnoreCase));
        RouteHeadwayRuleMap = new ReadOnlyDictionary<string, RouteHeadwayRule>(
            routeHeadwayRules.ToDictionary(
                rule => BuildRouteHeadwayKey(rule.LeadingRouteId, rule.FollowingRouteId),
                StringComparer.OrdinalIgnoreCase));
        TrainTrackChoiceRecordMap = new ReadOnlyDictionary<string, TrainTrackChoiceRecord>(
            trainTrackChoiceRecords.ToDictionary(
                record => BuildTrackChoiceKey(record.TrainIndex, record.StationOrder, record.IsStoppingChoice),
                StringComparer.OrdinalIgnoreCase));
        StationOvertakingProfileMap = new ReadOnlyDictionary<string, StationOvertakingProfile>(
            stationOvertakingProfiles.ToDictionary(profile => profile.StationName, StringComparer.OrdinalIgnoreCase));
        BenchmarkTrackRestrictionMap = new ReadOnlyDictionary<string, BenchmarkTrackRestriction>(
            benchmarkTrackRestrictions.ToDictionary(
                restriction => BuildBenchmarkTrackRestrictionKey(restriction.TrainIndex, restriction.StationOrder),
                StringComparer.OrdinalIgnoreCase));
        NormalDwellTimeRuleMap = new ReadOnlyDictionary<string, NormalDwellTimeRule>(
            NormalDwellTimeRules.ToDictionary(
                rule => BuildNormalDwellTimeKey(rule.TrainIndex, rule.StationOrder),
                StringComparer.OrdinalIgnoreCase));
    }

    public InstanceMeta Meta { get; }
    public CorridorDefinition Corridor { get; }
    public IReadOnlyList<string> Stations { get; }
    public IReadOnlyList<SectionRecord> Sections { get; }
    public IReadOnlyList<TrainRecord> Trains { get; }
    public IReadOnlyList<TrainStopRecord> TrainStops { get; }
    public IReadOnlyList<RunTimeRule> RunTimeRules { get; }
    public IReadOnlyList<MaintenanceWindowRule> MaintenanceWindows { get; }
    public IReadOnlyList<StationStopQuotaRule> StationStopQuotaRules { get; }
    public IReadOnlyList<TrainSectionStopQuotaRule> TrainSectionStopQuotaRules { get; }
    public IReadOnlyList<TrainSectionTravelTimeLimitRule> TrainSectionTravelTimeLimitRules { get; }
    public IReadOnlyList<RouteRecord> Routes { get; }
    public IReadOnlyList<MovementHeadwayRule> MovementHeadwayRules { get; }
    public IReadOnlyList<RouteHeadwayRule> RouteHeadwayRules { get; }
    public IReadOnlyList<TrainTrackChoiceRecord> TrainTrackChoiceRecords { get; }
    public IReadOnlyList<StationOvertakingProfile> StationOvertakingProfiles { get; }
    public IReadOnlyList<BenchmarkTrackRestriction> BenchmarkTrackRestrictions { get; }
    public IReadOnlyList<NormalDwellTimeRule> NormalDwellTimeRules { get; }
    public IReadOnlyList<DatasetDiagnostic> DatasetDiagnostics { get; }

    public IReadOnlyDictionary<string, SectionRecord> SectionByName { get; }
    public IReadOnlyDictionary<string, TrainStopRecord> StopsById { get; }
    public IReadOnlyDictionary<string, MovementHeadwayRule> MovementHeadwayRuleMap { get; }
    public IReadOnlyDictionary<string, RouteHeadwayRule> RouteHeadwayRuleMap { get; }
    public IReadOnlyDictionary<string, TrainTrackChoiceRecord> TrainTrackChoiceRecordMap { get; }
    public IReadOnlyDictionary<string, StationOvertakingProfile> StationOvertakingProfileMap { get; }
    public IReadOnlyDictionary<string, BenchmarkTrackRestriction> BenchmarkTrackRestrictionMap { get; }
    public IReadOnlyDictionary<string, NormalDwellTimeRule> NormalDwellTimeRuleMap { get; }

    public bool TryGetMovementHeadwayRule(
        string stationName,
        MovementEvent leadingEvent,
        MovementEvent followingEvent,
        out MovementHeadwayRule? rule)
    {
        return MovementHeadwayRuleMap.TryGetValue(
            BuildMovementHeadwayKey(stationName, leadingEvent, followingEvent),
            out rule);
    }

    public bool TryGetRouteHeadwayRule(int leadingRouteId, int followingRouteId, out RouteHeadwayRule? rule)
    {
        return RouteHeadwayRuleMap.TryGetValue(
            BuildRouteHeadwayKey(leadingRouteId, followingRouteId),
            out rule);
    }

    public bool TryGetTrackChoiceRecord(int trainIndex, int stationOrder, bool isStoppingChoice, out TrainTrackChoiceRecord? record)
    {
        return TrainTrackChoiceRecordMap.TryGetValue(
            BuildTrackChoiceKey(trainIndex, stationOrder, isStoppingChoice),
            out record);
    }

    public bool TryGetBenchmarkTrackRestriction(int trainIndex, int stationOrder, out BenchmarkTrackRestriction? restriction)
    {
        return BenchmarkTrackRestrictionMap.TryGetValue(
            BuildBenchmarkTrackRestrictionKey(trainIndex, stationOrder),
            out restriction);
    }

    public bool TryGetNormalDwellTimeRule(int trainIndex, int stationOrder, out NormalDwellTimeRule? rule)
    {
        return NormalDwellTimeRuleMap.TryGetValue(
            BuildNormalDwellTimeKey(trainIndex, stationOrder),
            out rule);
    }

    private static string BuildMovementHeadwayKey(
        string stationName,
        MovementEvent leadingEvent,
        MovementEvent followingEvent)
    {
        return $"{stationName}::{leadingEvent}::{followingEvent}";
    }

    private static string BuildRouteHeadwayKey(int leadingRouteId, int followingRouteId)
    {
        return $"{leadingRouteId}::{followingRouteId}";
    }

    private static string BuildTrackChoiceKey(int trainIndex, int stationOrder, bool isStoppingChoice)
    {
        return $"{trainIndex}::{stationOrder}::{(isStoppingChoice ? "stop" : "pass")}";
    }

    private static string BuildBenchmarkTrackRestrictionKey(int trainIndex, int stationOrder)
    {
        return $"{trainIndex}::{stationOrder}";
    }

    private static string BuildNormalDwellTimeKey(int trainIndex, int stationOrder)
    {
        return $"{trainIndex}::{stationOrder}";
    }
}
