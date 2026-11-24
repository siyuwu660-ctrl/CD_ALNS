// 时间补救策略计算事件可释放时窗、分阶段求解计划和结构冲突作用域，并根据每次结果决定提交、扩大窗口或终止。
// 多个纯策略对象把参数解析、边界计算、动作选择和停止原因分开，使 Checker 的时间重排过程可测试且可解释。
using System.Globalization;

namespace GurobiTimetablingResearch.Optimization;

public sealed class TemporalRecourseWindowPolicy
{
    private TemporalRecourseWindowPolicy(IReadOnlyList<int> windowSeconds)
    {
        WindowSeconds = windowSeconds;
    }

    public IReadOnlyList<int> WindowSeconds { get; }

    public static TemporalRecourseWindowPolicy Create(IEnumerable<int> windowSeconds)
    {
        ArgumentNullException.ThrowIfNull(windowSeconds);
        int[] stages = windowSeconds.ToArray();
        if (stages.Length == 0)
        {
            throw new ArgumentException("Temporal recourse window stages cannot be empty.", nameof(windowSeconds));
        }

        for (int index = 0; index < stages.Length; index++)
        {
            if (stages[index] <= 0 || stages[index] > 86400)
            {
                throw new ArgumentException(
                    "Temporal recourse window stages must be positive and no greater than 86400 seconds.",
                    nameof(windowSeconds));
            }

            if (index > 0 && stages[index] <= stages[index - 1])
            {
                throw new ArgumentException(
                    "Temporal recourse window stages must be strictly increasing.",
                    nameof(windowSeconds));
            }
        }

        return new TemporalRecourseWindowPolicy(Array.AsReadOnly(stages));
    }
}

public sealed record TemporalEventBounds(
    double LowerBound,
    double UpperBound,
    bool IsFixed,
    bool HasEmptyIntersection);

public static class TemporalEventBoundsCalculator
{
    private const double Tolerance = 1e-9;

    public static TemporalEventBounds Resolve(
        double inputLower,
        double inputUpper,
        double snapshotValue,
        int windowSeconds,
        bool released)
    {
        if (inputLower > inputUpper + Tolerance)
        {
            return new TemporalEventBounds(inputLower, inputUpper, false, true);
        }

        double lower = released
            ? Math.Max(inputLower, snapshotValue - windowSeconds)
            : snapshotValue;
        double upper = released
            ? Math.Min(inputUpper, snapshotValue + windowSeconds)
            : snapshotValue;
        bool outsideInputDomain = snapshotValue < inputLower - Tolerance || snapshotValue > inputUpper + Tolerance;
        bool empty = lower > upper + Tolerance || outsideInputDomain;

        return new TemporalEventBounds(
            lower,
            upper,
            !empty && Math.Abs(upper - lower) <= Tolerance,
            empty);
    }
}

public static class CheckerZeroConflictArgumentParser
{
    public static IReadOnlyList<int> ParseWindowSeconds(string rawValue, string argumentName)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            throw new ArgumentException($"{argumentName} requires a comma-separated recourse window ladder.", argumentName);
        }

        string[] tokens = rawValue.Split(',', StringSplitOptions.TrimEntries);
        List<int> stages = new(tokens.Length);
        foreach (string token in tokens)
        {
            if (token.Length == 0 || !int.TryParse(token, out int seconds))
            {
                throw new ArgumentException(
                    $"{argumentName} contains an invalid integer stage: '{token}'.",
                    argumentName);
            }

            stages.Add(seconds);
        }

        try
        {
            return TemporalRecourseWindowPolicy.Create(stages).WindowSeconds;
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException($"{argumentName}: {exception.Message}", argumentName, exception);
        }
    }
}

public sealed record TemporalRecourseAttemptOutcome(
    int WindowSeconds,
    bool HasIncumbent,
    string SolverStatus);

public sealed record TemporalRecourseAttemptResolution(
    int AcceptedWindowSeconds,
    int AttemptCount,
    bool Exhausted,
    string SolverStatus);

public static class TemporalRecourseAttemptResolver
{
    public static TemporalRecourseAttemptResolution Resolve(
        IReadOnlyList<TemporalRecourseAttemptOutcome> attempts)
    {
        for (int index = 0; index < attempts.Count; index++)
        {
            TemporalRecourseAttemptOutcome attempt = attempts[index];
            if (attempt.HasIncumbent)
            {
                return new TemporalRecourseAttemptResolution(
                    attempt.WindowSeconds,
                    index + 1,
                    false,
                    attempt.SolverStatus);
            }
        }

        return new TemporalRecourseAttemptResolution(
            0,
            attempts.Count,
            true,
            attempts.LastOrDefault()?.SolverStatus ?? string.Empty);
    }
}

public static class CheckerZeroConflictTerminationResolver
{
    public static string Resolve(
        bool microFeasible,
        int unseenCandidateCount,
        bool recourseExhausted)
    {
        if (microFeasible)
        {
            return "micro_feasible";
        }

        if (recourseExhausted)
        {
            return "temporal_recourse_exhausted";
        }

        return unseenCandidateCount == 0
            ? "no_unseen_structural_candidates"
            : string.Empty;
    }
}

public enum CheckerStationCoreAction
{
    MicroFeasible,
    ContinueExactSeparation,
    TrialProjectedCore,
    MasterDecisionSpaceInsufficient,
    StationCoreTrialsExhausted
}

public static class CheckerStationCoreActionResolver
{
    public static CheckerStationCoreAction Resolve(
        bool microFeasible,
        int unseenExactCandidateCount,
        int projectedCoreCandidateCount,
        int unsupportedCoreCount,
        bool allTrialsExhausted)
    {
        if (unseenExactCandidateCount < 0 || projectedCoreCandidateCount < 0 || unsupportedCoreCount < 0)
        {
            throw new ArgumentException("Checker station-core candidate counts cannot be negative.");
        }

        if (projectedCoreCandidateCount > 0 && allTrialsExhausted)
        {
            throw new ArgumentException("Unseen projected station cores cannot coexist with an exhausted trial state.");
        }

        if (microFeasible)
        {
            if (unseenExactCandidateCount != 0 || projectedCoreCandidateCount != 0 ||
                unsupportedCoreCount != 0 || allTrialsExhausted)
            {
                throw new ArgumentException("A micro-feasible state cannot contain unresolved station-core feedback.");
            }

            return CheckerStationCoreAction.MicroFeasible;
        }

        if (unseenExactCandidateCount > 0)
        {
            return CheckerStationCoreAction.ContinueExactSeparation;
        }

        if (projectedCoreCandidateCount > 0)
        {
            return CheckerStationCoreAction.TrialProjectedCore;
        }

        if (unsupportedCoreCount > 0)
        {
            return CheckerStationCoreAction.MasterDecisionSpaceInsufficient;
        }

        if (allTrialsExhausted)
        {
            return CheckerStationCoreAction.StationCoreTrialsExhausted;
        }

        throw new ArgumentException("The unresolved Checker state contains no exact or station-core disposition.");
    }
}

public enum CheckerStationCoreCommitAction
{
    PreservePriorState,
    CommitAcceptedCarrier
}

public static class CheckerStationCoreCommitResolver
{
    public static CheckerStationCoreCommitAction Resolve(
        bool hasIncumbent,
        bool passesAcceptanceRule)
    {
        if (passesAcceptanceRule && !hasIncumbent)
        {
            throw new ArgumentException("A station-core trial without an incumbent cannot pass acceptance.");
        }

        return passesAcceptanceRule
            ? CheckerStationCoreCommitAction.CommitAcceptedCarrier
            : CheckerStationCoreCommitAction.PreservePriorState;
    }
}

public sealed record StationCoreTrialExecutionPlan(
    IReadOnlyList<int> RecourseWindowSeconds,
    bool BuildFreshProjectionCarrier,
    bool ProjectionCarrierAppliesTemporalBounds);

public static class StationCoreTrialExecutionPlanBuilder
{
    public static StationCoreTrialExecutionPlan Build(IReadOnlyList<int> recourseWindowSeconds)
    {
        ArgumentNullException.ThrowIfNull(recourseWindowSeconds);
        IReadOnlyList<int> validated = TemporalRecourseWindowPolicy.Create(recourseWindowSeconds).WindowSeconds;
        return new StationCoreTrialExecutionPlan(
            validated,
            BuildFreshProjectionCarrier: true,
            ProjectionCarrierAppliesTemporalBounds: false);
    }
}

public static class CheckerStructuralConflictScopeResolver
{
    public static IReadOnlyList<MicroConflictExplanation> Resolve(
        IReadOnlyList<MicroConflictExplanation> conflicts,
        bool zeroConflictMode,
        string? focusStationName)
    {
        ArgumentNullException.ThrowIfNull(conflicts);
        if (zeroConflictMode || string.IsNullOrWhiteSpace(focusStationName))
        {
            return conflicts.ToList();
        }

        return conflicts
            .Where(conflict => string.Equals(
                conflict.StationName,
                focusStationName,
                StringComparison.OrdinalIgnoreCase))
            .ToList();
    }
}

public static class CheckerStationCoreTerminalReasonResolver
{
    public static string Resolve(
        int supportedTrialCount,
        int acceptedTrialCount,
        int noIncumbentTrialCount,
        int unsupportedCoreCount)
    {
        if (supportedTrialCount < 0 || acceptedTrialCount < 0 || noIncumbentTrialCount < 0 || unsupportedCoreCount < 0 ||
            acceptedTrialCount > supportedTrialCount || noIncumbentTrialCount > supportedTrialCount ||
            acceptedTrialCount + noIncumbentTrialCount > supportedTrialCount)
        {
            throw new ArgumentException("Station-core trial outcome counts are contradictory.");
        }

        if (acceptedTrialCount > 0)
        {
            return string.Empty;
        }

        if (unsupportedCoreCount > 0)
        {
            return "master_decision_space_insufficient";
        }

        if (supportedTrialCount > 0)
        {
            return noIncumbentTrialCount == supportedTrialCount
                ? "station_core_recourse_exhausted"
                : "station_core_trials_exhausted";
        }

        throw new ArgumentException("A station-core terminal reason requires supported trials or unsupported cores.");
    }
}

public static class CheckerStructuralSolveTimeLimitResolver
{
    public static double Resolve(
        bool zeroConflictMode,
        double primaryTimeLimitSeconds,
        double secondaryTimeLimitSeconds)
    {
        if (primaryTimeLimitSeconds <= 0 || secondaryTimeLimitSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(primaryTimeLimitSeconds),
                "Checker solve time limits must be positive.");
        }

        return zeroConflictMode
            ? Math.Min(primaryTimeLimitSeconds, secondaryTimeLimitSeconds)
            : primaryTimeLimitSeconds;
    }
}

public static class CheckerPositiveSecondsArgumentParser
{
    public static double Parse(string rawValue, string argumentName)
    {
        if (!double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ||
            !double.IsFinite(value) ||
            value <= 0)
        {
            throw new ArgumentException($"{argumentName} must be a finite positive number of seconds.");
        }

        return value;
    }
}
