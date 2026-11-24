namespace GurobiTimetablingResearch.Optimization;

/// <summary>
/// 表示反馈批次与热点精确化之间的有界调度结果。
/// </summary>
public sealed record ValidityFirstHotspotEscalationDecision(
    bool MustEvaluateHotspot,
    string Reason);

/// <summary>
/// 使用连续反馈-only轮数限制热点等待时间；该策略只决定是否必须评估热点，
/// 不改变反馈有效性，也不决定热点闭包能否安全进入主问题。
/// </summary>
public static class ValidityFirstHotspotEscalationPolicy
{
    public static ValidityFirstHotspotEscalationDecision Decide(
        bool microFeasible,
        int newPermanentFeedbackCount,
        int consecutiveFeedbackOnlyRounds,
        int maximumFeedbackOnlyRounds,
        bool requireJointRepairScope = false)
    {
        if (newPermanentFeedbackCount < 0 || consecutiveFeedbackOnlyRounds < 0 ||
            maximumFeedbackOnlyRounds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(consecutiveFeedbackOnlyRounds));
        }

        if (microFeasible)
        {
            return new ValidityFirstHotspotEscalationDecision(
                false,
                "joint_feasible_no_hotspot");
        }

        if (requireJointRepairScope)
        {
            return new ValidityFirstHotspotEscalationDecision(
                true,
                "phased_recovery_requires_core_and_protection_scope");
        }

        if (consecutiveFeedbackOnlyRounds >= maximumFeedbackOnlyRounds)
        {
            return new ValidityFirstHotspotEscalationDecision(
                true,
                newPermanentFeedbackCount > 0
                    ? "maximum_feedback_only_rounds_reached_despite_new_feedback"
                    : "maximum_feedback_only_rounds_reached");
        }

        return new ValidityFirstHotspotEscalationDecision(
            false,
            newPermanentFeedbackCount > 0
                ? "feedback_batch_within_hotspot_wait_bound"
                : "no_feedback_hotspot_path");
    }
}
