// 本文件用枚举封闭停站要求、运行方向、停站角色及车站端点事件等有限状态域。
// 枚举值代替自由文本参与分支判断和索引构造，保证数据装载、建模与微观校验采用一致的状态编码。
namespace GurobiTimetablingResearch.Domain;

/// <summary>
/// 统一后的停站要求枚举。
/// 数据层把原始中文字符串转换为稳定枚举，模型层不再直接依赖 CSV 文本。
/// </summary>
public enum StopRequirement
{
    MustStop,
    OptionalStop,
    MustPass
}

/// <summary>
/// 统一后的运行方向枚举。
/// 当前研究主问题按单向走廊组织，但数据接口保留上下行信息，便于后续筛选和扩展。
/// </summary>
public enum TravelDirection
{
    Unknown,
    Up,
    Down
}

/// <summary>
/// 列车在停站序列中的位置角色。
/// 该角色用于固定始发站和终到站必须停站，并避免模型构建时重复判断首末站。
/// </summary>
public enum TrainStopRole
{
    Origin,
    Intermediate,
    Destination
}

/// <summary>
/// 宏观代理间隔使用的站内事件类型。
/// 原始进路数据会把通过作业拆成“通过接车”和“通过发车”，但运输优化文献中常见的 ordered headway
/// 通常按 Arrival / Departure / Passing 三类事件定义。因此 master 只在三类事件上聚合代理间隔，
/// 微观 slave/checker 后续仍可通过 RouteRecord.OperationType 读取更细的原始作业类型。
/// </summary>
public enum MovementEvent
{
    Unknown,
    Arrival,
    Departure,
    Passing
}

public enum StationEventSide
{
    Incoming,
    Outgoing
}
