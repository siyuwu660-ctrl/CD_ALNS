namespace GurobiTimetablingResearch.Optimization;

/// <summary>
/// 指定同一套 Macro、Global Micro 和修复组件采用哪一种主—子问题调度策略。
/// </summary>
public enum CheckerCoordinationStrategy
{
    Legacy,
    AdaptiveSingleton,
    ValidityFirstBenders
}
