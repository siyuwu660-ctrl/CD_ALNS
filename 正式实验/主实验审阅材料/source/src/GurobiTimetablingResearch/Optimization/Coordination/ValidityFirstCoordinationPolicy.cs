namespace GurobiTimetablingResearch.Optimization;

public enum ValidityFirstCoordinationAction
{
    RebuildMaster,
    ImproveBusinessObjective,
    StopWithUnknown,
    StopTimeBudget
}

/// <summary>
/// 汇总一次 Global Micro 检查后的可执行信息，使协调顺序不依赖散落的布尔分支。
/// </summary>
public sealed record ValidityFirstCoordinationContext(
    bool MicroFeasible,
    int NewPermanentFeedbackCount,
    bool HasProvenCore,
    int CoreRepeatCount,
    bool AllKnownPermanentFeedbackActive,
    bool HasCompleteHotspotScope,
    bool HasRemainingTime);

public sealed record ValidityFirstCoordinationDecision(
    ValidityFirstCoordinationAction Action,
    bool IncludePermanentFeedbackBatch,
    bool IncludeHotspotExactification,
    string Reason);

/// <summary>
/// 优先提交有效反馈，并在重复 core、反馈表达不足或本轮无新反馈时升级为热点联合建模。
/// </summary>
public static class ValidityFirstCoordinationPolicy
{
    public static ValidityFirstCoordinationDecision Decide(ValidityFirstCoordinationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.NewPermanentFeedbackCount < 0 || context.CoreRepeatCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(context));
        }

        if (!context.HasRemainingTime)
        {
            return new ValidityFirstCoordinationDecision(
                ValidityFirstCoordinationAction.StopTimeBudget,
                false,
                false,
                "total_runtime_budget_exhausted");
        }

        if (context.MicroFeasible)
        {
            return new ValidityFirstCoordinationDecision(
                ValidityFirstCoordinationAction.ImproveBusinessObjective,
                false,
                false,
                "joint_feasible_objective_improvement");
        }

        bool includeFeedback = context.NewPermanentFeedbackCount > 0;
        bool includeHotspot = context.HasCompleteHotspotScope &&
                              (context.CoreRepeatCount >= 2 ||
                               (context.HasProvenCore &&
                                (!includeFeedback || context.AllKnownPermanentFeedbackActive)));

        if (includeFeedback || includeHotspot)
        {
            return new ValidityFirstCoordinationDecision(
                ValidityFirstCoordinationAction.RebuildMaster,
                includeFeedback,
                includeHotspot,
                includeHotspot
                    ? "valid_feedback_and_or_hotspot_exactification"
                    : "valid_feedback_batch");
        }

        return new ValidityFirstCoordinationDecision(
            ValidityFirstCoordinationAction.StopWithUnknown,
            false,
            false,
            "no_valid_feedback_or_complete_hotspot_scope");
    }
}
