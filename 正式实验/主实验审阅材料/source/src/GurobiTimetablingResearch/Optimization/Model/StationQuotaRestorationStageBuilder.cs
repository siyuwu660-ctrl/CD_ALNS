// 车站配额恢复阶段在滚动或局部模型因固定决策导致配额不可行时，按缺口选择有限列车并恢复相关停站变量。
// 缺额计算器、邻域解析器和预算策略共同控制解冻规模；终止分类及日志解析用于判断恢复阶段是否产生有效 incumbent。
using System.Globalization;
using System.Text.RegularExpressions;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

public static class NonNegativeDoubleArgumentParser
{
    public static double? Parse(IReadOnlyList<string> args, string prefix)
    {
        string? raw = args.FirstOrDefault(arg => arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (raw is null)
        {
            return null;
        }

        string value = raw[prefix.Length..];
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) &&
               double.IsFinite(parsed) &&
               parsed >= 0.0
            ? parsed
            : null;
    }
}

public sealed record StationQuotaRestorationStage(
    int StageIndex,
    IReadOnlyList<StationStopQuotaRule> AddedRules,
    IReadOnlyList<StationStopQuotaRule> CumulativeRules);

public static class StationQuotaRestorationStageBuilder
{
    public static IReadOnlyList<StationQuotaRestorationStage> Build(
        IReadOnlyList<StationStopQuotaRule> rules,
        int maxStationsPerStage)
    {
        if (maxStationsPerStage <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxStationsPerStage));
        }

        List<IGrouping<string, StationStopQuotaRule>> stationGroups = rules
            .GroupBy(rule => rule.StationName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        List<StationQuotaRestorationStage> stages = [];
        List<StationStopQuotaRule> cumulative = [];

        for (int first = 0; first < stationGroups.Count; first += maxStationsPerStage)
        {
            List<StationStopQuotaRule> added = stationGroups
                .Skip(first)
                .Take(maxStationsPerStage)
                .SelectMany(group => group)
                .ToList();
            cumulative.AddRange(added);
            stages.Add(new StationQuotaRestorationStage(
                stages.Count + 1,
                added,
                cumulative.ToList()));
        }

        return stages;
    }
}

public static class StationQuotaRestorationNeighborhoodResolver
{
    public static IReadOnlySet<int> ResolveTrainIndices(
        TimetablingInstance instance,
        IReadOnlyList<StationStopQuotaRule> addedRules)
    {
        HashSet<string> stationTypes = addedRules
            .Select(rule => BuildKey(rule.StationName, rule.TrainType))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return instance.TrainStops
            .Where(stop => stationTypes.Contains(BuildKey(stop.StationName, stop.TrainType)))
            .Select(stop => stop.TrainIndex)
            .ToHashSet();
    }

    private static string BuildKey(string stationName, string trainType) => $"{stationName}\u001f{trainType}";
}

public sealed record StationQuotaRestorationBudget(
    double TotalTimeLimitSeconds,
    bool IsOverride);

public static class StationQuotaRestorationBudgetPolicy
{
    public static StationQuotaRestorationBudget Resolve(
        int stageIndex,
        double defaultTimeLimitSeconds,
        int? overrideStageIndex,
        double? overrideTotalTimeLimitSeconds)
    {
        if (stageIndex <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stageIndex));
        }
        if (!double.IsFinite(defaultTimeLimitSeconds) || defaultTimeLimitSeconds <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(defaultTimeLimitSeconds));
        }
        if (overrideStageIndex.HasValue != overrideTotalTimeLimitSeconds.HasValue)
        {
            throw new ArgumentException("A restoration stage override requires both a stage index and a total time limit.");
        }
        if (!overrideStageIndex.HasValue)
        {
            return new StationQuotaRestorationBudget(defaultTimeLimitSeconds, false);
        }
        if (overrideStageIndex.Value <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(overrideStageIndex));
        }
        if (!double.IsFinite(overrideTotalTimeLimitSeconds!.Value) || overrideTotalTimeLimitSeconds.Value <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(overrideTotalTimeLimitSeconds));
        }

        return stageIndex == overrideStageIndex.Value
            ? new StationQuotaRestorationBudget(overrideTotalTimeLimitSeconds.Value, true)
            : new StationQuotaRestorationBudget(defaultTimeLimitSeconds, false);
    }
}

public sealed record StationQuotaRuleDeficit(
    string TrainType,
    int StartTimeSeconds,
    int EndTimeSeconds,
    int MinimumStops,
    int MaximumStops,
    int ActualStops,
    int Deficit);

public sealed record StationQuotaDeficitReport(
    string StationName,
    IReadOnlyList<StationQuotaRuleDeficit> Rules,
    int TotalDeficit);

public static class StationQuotaDeficitCalculator
{
    public static StationQuotaDeficitReport Calculate(
        TimetablingInstance instance,
        MasterSolutionSnapshot snapshot,
        IReadOnlyList<StationStopQuotaRule> rules)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(rules);
        if (rules.Count == 0)
        {
            throw new ArgumentException("At least one station quota rule is required.", nameof(rules));
        }

        string[] stationNames = rules
            .Select(rule => rule.StationName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (stationNames.Length != 1)
        {
            throw new ArgumentException("Quota deficit diagnostics require rules from exactly one station.", nameof(rules));
        }

        List<StationQuotaRuleDeficit> deficits = [];
        foreach (StationStopQuotaRule rule in rules
                     .OrderBy(rule => rule.StartTimeSeconds)
                     .ThenBy(rule => rule.TrainType, StringComparer.OrdinalIgnoreCase))
        {
            int actualStops = instance.TrainStops.Count(stop =>
                string.Equals(stop.StationName, rule.StationName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(stop.TrainType, rule.TrainType, StringComparison.OrdinalIgnoreCase) &&
                snapshot.StopByStopId.TryGetValue(stop.StopId, out int isStop) &&
                isStop == 1 &&
                TryResolveEventTime(snapshot, stop, out double eventTimeSeconds) &&
                eventTimeSeconds >= rule.StartTimeSeconds - 1e-6 &&
                eventTimeSeconds <= rule.EndTimeSeconds + 1e-6);
            int deficit = Math.Max(0, rule.MinStops - actualStops);
            deficits.Add(new StationQuotaRuleDeficit(
                rule.TrainType,
                rule.StartTimeSeconds,
                rule.EndTimeSeconds,
                rule.MinStops,
                rule.MaxStops,
                actualStops,
                deficit));
        }

        return new StationQuotaDeficitReport(
            stationNames[0],
            deficits,
            deficits.Sum(item => item.Deficit));
    }

    private static bool TryResolveEventTime(
        MasterSolutionSnapshot snapshot,
        TrainStopRecord stop,
        out double eventTimeSeconds)
    {
        IReadOnlyDictionary<string, double> eventTimes = stop.StopRole == TrainStopRole.Origin
            ? snapshot.DepartureSecondsByStopId
            : snapshot.ArrivalSecondsByStopId;
        return eventTimes.TryGetValue(stop.StopId, out eventTimeSeconds);
    }
}

public enum MacroSolveTermination
{
    Optimal,
    FeasibleIncumbentFound,
    TimeLimitNoIncumbent,
    TimeLimitIncumbent,
    Infeasible,
    InterruptedOther
}

public static class MacroSolveTerminationClassifier
{
    public static MacroSolveTermination Resolve(int statusCode, bool hasIncumbent, bool feasibilityFirst)
    {
        if (feasibilityFirst && hasIncumbent)
        {
            return MacroSolveTermination.FeasibleIncumbentFound;
        }

        return statusCode switch
        {
            Gurobi.GRB.Status.OPTIMAL => MacroSolveTermination.Optimal,
            Gurobi.GRB.Status.TIME_LIMIT when hasIncumbent => MacroSolveTermination.TimeLimitIncumbent,
            Gurobi.GRB.Status.TIME_LIMIT => MacroSolveTermination.TimeLimitNoIncumbent,
            Gurobi.GRB.Status.INFEASIBLE => MacroSolveTermination.Infeasible,
            _ => MacroSolveTermination.InterruptedOther
        };
    }
}

public enum MipStartFeedback
{
    NotReported,
    Loaded,
    PartialCompletion,
    DidNotProduceIncumbent,
    PartialDidNotProduceIncumbent,
    ProducedIncumbent,
    Rejected
}

public static class MipStartFeedbackClassifier
{
    public static MipStartFeedback Resolve(string? solverLog)
    {
        if (string.IsNullOrWhiteSpace(solverLog))
        {
            return MipStartFeedback.NotReported;
        }

        bool partial = solverLog.Contains("Completing partial solution", StringComparison.OrdinalIgnoreCase);
        bool didNotProduce = solverLog.Contains(
            "MIP start did not produce a new incumbent solution",
            StringComparison.OrdinalIgnoreCase);
        if (partial && didNotProduce)
        {
            return MipStartFeedback.PartialDidNotProduceIncumbent;
        }
        if (solverLog.Contains("MIP start produced solution", StringComparison.OrdinalIgnoreCase))
        {
            return MipStartFeedback.ProducedIncumbent;
        }
        if (solverLog.Contains("violates constraint", StringComparison.OrdinalIgnoreCase) ||
            solverLog.Contains("MIP start rejected", StringComparison.OrdinalIgnoreCase))
        {
            return MipStartFeedback.Rejected;
        }
        if (didNotProduce)
        {
            return MipStartFeedback.DidNotProduceIncumbent;
        }
        if (partial)
        {
            return MipStartFeedback.PartialCompletion;
        }
        if (solverLog.Contains("Loaded user MIP start", StringComparison.OrdinalIgnoreCase))
        {
            return MipStartFeedback.Loaded;
        }

        return MipStartFeedback.NotReported;
    }
}

public sealed record GurobiLogSolveSummary(
    double? BestObjective,
    double? BestBound,
    double? MipGap);

public static partial class GurobiLogSolveSummaryParser
{
    public static GurobiLogSolveSummary Parse(string? solverLog)
    {
        if (string.IsNullOrWhiteSpace(solverLog))
        {
            return new GurobiLogSolveSummary(null, null, null);
        }

        MatchCollection matches = BestSummaryRegex().Matches(solverLog);
        if (matches.Count == 0)
        {
            return new GurobiLogSolveSummary(null, null, null);
        }

        // 主问题采用字典序多目标；首个原生日志摘要对应全局跨度主目标，后续摘要对应补充目标。
        Match match = matches[0];
        return new GurobiLogSolveSummary(
            ParseOptionalDouble(match.Groups[1].Value),
            ParseOptionalDouble(match.Groups[2].Value),
            ParseOptionalPercent(match.Groups[3].Value));
    }

    private static double? ParseOptionalDouble(string raw) =>
        double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : null;

    private static double? ParseOptionalPercent(string raw)
    {
        string normalized = raw.Trim().TrimEnd('%');
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value / 100.0
            : null;
    }

    [GeneratedRegex(
        @"Best objective\s+([^,]+),\s+best bound\s+([^,]+),\s+gap\s+([^\r\n]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BestSummaryRegex();
}

public static class MacroBaselineAcceptancePolicy
{
    public static bool IsAccepted(
        int admittedTrainCount,
        int totalTrainCount,
        int completedQuotaStageCount,
        int totalQuotaStageCount,
        bool hasIncumbent,
        int validatorViolationCount) =>
        admittedTrainCount == totalTrainCount &&
        completedQuotaStageCount == totalQuotaStageCount &&
        hasIncumbent &&
        validatorViolationCount == 0;
}
