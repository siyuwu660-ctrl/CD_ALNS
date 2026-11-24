// IIS 引导修复策略把一次不可行试验的阻塞类别映射为扩张依赖、升级邻域、重试或终止等动作。
// 转移规则结合尝试次数和配置上限，控制依赖闭包逐步扩大并防止无界重复求解。
namespace GurobiTimetablingResearch.Optimization;

internal sealed record IisGuidedRepairOptions(
    bool Enabled,
    int MaxExpansionRounds,
    int TemporalExpansionStepSeconds,
    int MaxExtraTemporalSeconds)
{
    internal static IisGuidedRepairOptions Create(
        bool enabled,
        int maxExpansionRounds,
        int temporalExpansionStepSeconds,
        int maxExtraTemporalSeconds)
    {
        if (maxExpansionRounds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxExpansionRounds));
        }
        if (temporalExpansionStepSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(temporalExpansionStepSeconds));
        }
        if (maxExtraTemporalSeconds < 0 || maxExtraTemporalSeconds % temporalExpansionStepSeconds != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxExtraTemporalSeconds));
        }
        return new IisGuidedRepairOptions(
            enabled,
            maxExpansionRounds,
            temporalExpansionStepSeconds,
            maxExtraTemporalSeconds);
    }
}

internal enum IisGuidedRepairAction
{
    ContinueExpansion,
    DependencyClosureFeasible,
    StopUnknown,
    StopExhausted
}

internal static class IisGuidedRepairTransitionPolicy
{
    internal static IisGuidedRepairAction ResolveAfterMaster(
        StructuralCandidateProbeOutcome outcome,
        int expansionRound,
        int maximumExpansionRounds)
    {
        if (expansionRound <= 0 || maximumExpansionRounds <= 0 || expansionRound > maximumExpansionRounds)
        {
            throw new ArgumentOutOfRangeException(nameof(expansionRound));
        }
        return outcome switch
        {
            StructuralCandidateProbeOutcome.Feasible => IisGuidedRepairAction.DependencyClosureFeasible,
            StructuralCandidateProbeOutcome.Unknown => IisGuidedRepairAction.StopUnknown,
            StructuralCandidateProbeOutcome.ProvenInfeasible when expansionRound == maximumExpansionRounds =>
                IisGuidedRepairAction.StopExhausted,
            StructuralCandidateProbeOutcome.ProvenInfeasible => IisGuidedRepairAction.ContinueExpansion,
            _ => throw new ArgumentOutOfRangeException(nameof(outcome))
        };
    }

    internal static string ResolveAfterMicro(bool accepted) => accepted
        ? "dependency_closure_accepted"
        : "dependency_closure_feasible_but_micro_rejected";
}
