// 反馈仲裁策略根据证据来源与可靠性，把微观反馈分类为永久加割、临时试探或忽略。
// 策略 record 将冲突事实和模型动作分开，只有满足稳定性条件的反馈才进入长期主问题约束集合。
namespace GurobiTimetablingResearch.Optimization;

public enum FeedbackType { ExactHeadway, StationAssignmentCore }
public enum EvidenceType { UnavoidableConflict, ProvenInfeasibleCore, SearchLimitUnknown }
public enum FeedbackAction { StructuralSeparation, StationCoreOrPromotion, NoPermanentCut }

public sealed record FeedbackEvidence(
    FeedbackType FeedbackType,
    EvidenceType EvidenceType,
    bool ProvenInfeasible,
    int PersistentRoundCount,
    bool HasUnseenExactCandidates);

public static class FeedbackArbitrationPolicy
{
    public static FeedbackAction Resolve(FeedbackEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.EvidenceType == EvidenceType.SearchLimitUnknown || !evidence.ProvenInfeasible)
        {
            return FeedbackAction.NoPermanentCut;
        }
        return evidence.FeedbackType == FeedbackType.StationAssignmentCore &&
               evidence.EvidenceType == EvidenceType.ProvenInfeasibleCore
            ? FeedbackAction.StationCoreOrPromotion
            : FeedbackAction.StructuralSeparation;
    }
}

public static class PermanentMicroFeedbackEligibilityPolicy
{
    public static bool Allows(MicroSolveStatus status, bool hasProvenCore) =>
        status == MicroSolveStatus.ProvenInfeasible && hasProvenCore;
}
