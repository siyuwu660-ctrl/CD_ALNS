// 已选进路事件间隔策略按照事件的真实先后顺序查询“前车进路→后车进路”。
// 缺少该有序记录表示没有额外专用间隔；若反向存在规则，则阶段 3 只固定当前合法顺序，避免重定时后无约束反转。
namespace GurobiTimetablingResearch.Optimization;

internal enum SelectedRouteEventHeadwayDecisionKind
{
    NoConstraint,
    Satisfied,
    Violated
}

internal enum SelectedRouteEventOrder
{
    None,
    FirstBeforeSecond,
    SecondBeforeFirst
}

internal sealed record SelectedRouteEventHeadwayDecision(
    SelectedRouteEventHeadwayDecisionKind Kind,
    SelectedRouteEventOrder FixedOrder,
    int RequiredHeadwaySeconds,
    string RuleBasis,
    int? ForwardHeadwaySeconds,
    int? ReverseHeadwaySeconds,
    double RealizedGapSeconds);

internal static class SelectedRouteEventHeadwayPolicy
{
    private const double ToleranceSeconds = GurobiTimetablingResearch.Domain.TimetableTimePolicy.NumericToleranceSeconds;

    internal static SelectedRouteEventHeadwayDecision Evaluate(
        int firstRouteId,
        double firstTimeSeconds,
        int secondRouteId,
        double secondTimeSeconds,
        Func<int, int, int?> findRecordedHeadway)
    {
        ArgumentNullException.ThrowIfNull(findRecordedHeadway);
        int? forward = findRecordedHeadway(firstRouteId, secondRouteId);
        int? reverse = findRecordedHeadway(secondRouteId, firstRouteId);
        if (!forward.HasValue && !reverse.HasValue)
        {
            return new SelectedRouteEventHeadwayDecision(
                SelectedRouteEventHeadwayDecisionKind.NoConstraint,
                SelectedRouteEventOrder.None,
                0,
                "both_ordered_pairs_missing_no_additional_headway",
                null,
                null,
                Math.Abs(secondTimeSeconds - firstTimeSeconds));
        }

        double delta = secondTimeSeconds - firstTimeSeconds;
        if (delta > ToleranceSeconds)
        {
            return EvaluateKnownOrder(
                SelectedRouteEventOrder.FirstBeforeSecond,
                delta,
                forward,
                forward.HasValue
                    ? "recorded_ordered_pair_headway"
                    : "missing_ordered_pair_no_additional_headway",
                forward,
                reverse);
        }
        if (delta < -ToleranceSeconds)
        {
            return EvaluateKnownOrder(
                SelectedRouteEventOrder.SecondBeforeFirst,
                -delta,
                reverse,
                reverse.HasValue
                    ? "recorded_ordered_pair_headway"
                    : "missing_ordered_pair_no_additional_headway",
                forward,
                reverse);
        }

        // 同时事件没有天然前后关系；选择一个缺项或显式零间隔的合法方向，并把该方向固定为零边界。
        if (!forward.HasValue || forward.Value <= ToleranceSeconds)
        {
            return new SelectedRouteEventHeadwayDecision(
                SelectedRouteEventHeadwayDecisionKind.Satisfied,
                SelectedRouteEventOrder.FirstBeforeSecond,
                forward ?? 0,
                forward.HasValue
                    ? "recorded_ordered_pair_headway"
                    : "missing_ordered_pair_no_additional_headway",
                forward,
                reverse,
                0.0);
        }
        if (!reverse.HasValue || reverse.Value <= ToleranceSeconds)
        {
            return new SelectedRouteEventHeadwayDecision(
                SelectedRouteEventHeadwayDecisionKind.Satisfied,
                SelectedRouteEventOrder.SecondBeforeFirst,
                reverse ?? 0,
                reverse.HasValue
                    ? "recorded_ordered_pair_headway"
                    : "missing_ordered_pair_no_additional_headway",
                forward,
                reverse,
                0.0);
        }

        return new SelectedRouteEventHeadwayDecision(
            SelectedRouteEventHeadwayDecisionKind.Violated,
            SelectedRouteEventOrder.None,
            Math.Min(forward.Value, reverse.Value),
            "both_ordered_pairs_require_positive_headway",
            forward,
            reverse,
            0.0);
    }

    private static SelectedRouteEventHeadwayDecision EvaluateKnownOrder(
        SelectedRouteEventOrder order,
        double realizedGapSeconds,
        int? orderedHeadwaySeconds,
        string ruleBasis,
        int? forwardHeadwaySeconds,
        int? reverseHeadwaySeconds)
    {
        int required = orderedHeadwaySeconds ?? 0;
        return new SelectedRouteEventHeadwayDecision(
            realizedGapSeconds + ToleranceSeconds >= required
                ? SelectedRouteEventHeadwayDecisionKind.Satisfied
                : SelectedRouteEventHeadwayDecisionKind.Violated,
            order,
            required,
            ruleBasis,
            forwardHeadwaySeconds,
            reverseHeadwaySeconds,
            realizedGapSeconds);
    }
}
