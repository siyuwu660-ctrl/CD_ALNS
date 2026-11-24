// 微观搜索选项定义子问题的时间限制、搜索上限和统一状态枚举，用于约束车站级组合搜索的计算预算。
// 强类型状态区分可行、不可行、超时和未判定，防止预算耗尽被误认为严格不可行。
namespace GurobiTimetablingResearch.Optimization;

public enum MicroSolveStatus
{
    Feasible,
    ProvenInfeasible,
    Unknown
}

internal sealed record MicroSearchOptions(long MaxNodes, TimeSpan TimeLimit)
{
    internal static MicroSearchOptions Default { get; } = new(250_000, TimeSpan.FromSeconds(5));
}
