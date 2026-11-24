// 质量模块从冲突下降、目标劣化和求解成本计算候选指标，并用预算策略过滤不可接受的改进。
// 效率窗口保存近期接受点，通过单位成本收益和无胜者分类支持自适应流程的停机及层级切换决策。
namespace GurobiTimetablingResearch.Optimization;

internal sealed record AdaptiveSingletonQualityMetrics(
    double StepTotalRetimingSeconds,
    double InitialTotalRetimingSeconds,
    double StepMaximumRetimingSeconds,
    double InitialMaximumRetimingSeconds,
    double IncrementalRunningSupplementSeconds,
    double CurrentTotalRunningSupplementSeconds,
    int CurrentSupplementSectionCount);

internal static class AdaptiveSingletonQualityCalculator
{
    internal static AdaptiveSingletonQualityMetrics Calculate(
        MasterSolutionSnapshot previousAccepted,
        MasterSolutionSnapshot candidate,
        MasterSolutionSnapshot initialReference)
    {
        ArgumentNullException.ThrowIfNull(previousAccepted);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(initialReference);

        (double stepTotal, double stepMaximum) = CalculateRetiming(previousAccepted, candidate);
        (double initialTotal, double initialMaximum) = CalculateRetiming(initialReference, candidate);
        double previousSupplement = TotalSupplement(previousAccepted);
        double currentSupplement = TotalSupplement(candidate);
        int currentSectionCount = (candidate.RunningTimeSupplementSecondsByKey ??
                new Dictionary<string, double>())
            .Count(item => item.Value > 1e-9);
        return new AdaptiveSingletonQualityMetrics(
            stepTotal,
            initialTotal,
            stepMaximum,
            initialMaximum,
            currentSupplement - previousSupplement,
            currentSupplement,
            currentSectionCount);
    }

    private static (double Total, double Maximum) CalculateRetiming(
        MasterSolutionSnapshot reference,
        MasterSolutionSnapshot candidate)
    {
        EnsureSameKeys(reference.ArrivalSecondsByStopId, candidate.ArrivalSecondsByStopId, "arrival");
        EnsureSameKeys(reference.DepartureSecondsByStopId, candidate.DepartureSecondsByStopId, "departure");
        double total = 0.0;
        double maximum = 0.0;
        Add(reference.ArrivalSecondsByStopId, candidate.ArrivalSecondsByStopId);
        Add(reference.DepartureSecondsByStopId, candidate.DepartureSecondsByStopId);
        return (total, maximum);

        void Add(
            IReadOnlyDictionary<string, double> referenceValues,
            IReadOnlyDictionary<string, double> candidateValues)
        {
            foreach ((string key, double referenceValue) in referenceValues)
            {
                double deviation = Math.Abs(candidateValues[key] - referenceValue);
                total += deviation;
                maximum = Math.Max(maximum, deviation);
            }
        }
    }

    private static void EnsureSameKeys(
        IReadOnlyDictionary<string, double> reference,
        IReadOnlyDictionary<string, double> candidate,
        string family)
    {
        if (!reference.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase)
            .SetEquals(candidate.Keys))
        {
            throw new InvalidDataException(
                $"Adaptive quality {family} key set differs from the initial reference.");
        }
    }

    private static double TotalSupplement(MasterSolutionSnapshot snapshot) =>
        (snapshot.RunningTimeSupplementSecondsByKey ?? new Dictionary<string, double>())
        .Values.Sum();
}

internal sealed record AdaptiveQualityBudgetOptions(
    double? MaxInitialTotalRetimingSeconds,
    double? MaxInitialPointRetimingSeconds);

internal sealed record AdaptiveQualityBudgetEvaluation(
    bool Eligible,
    IReadOnlyList<string> RejectionReasons);

internal static class AdaptiveQualityBudgetPolicy
{
    private const double Tolerance = 1e-9;

    internal static AdaptiveQualityBudgetEvaluation Evaluate(
        AdaptiveSingletonQualityMetrics metrics,
        AdaptiveQualityBudgetOptions options)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(options);
        List<string> reasons = [];
        if (options.MaxInitialTotalRetimingSeconds.HasValue &&
            metrics.InitialTotalRetimingSeconds >
            options.MaxInitialTotalRetimingSeconds.Value + Tolerance)
        {
            reasons.Add("initial_total_retiming_budget_exceeded");
        }
        if (options.MaxInitialPointRetimingSeconds.HasValue &&
            metrics.InitialMaximumRetimingSeconds >
            options.MaxInitialPointRetimingSeconds.Value + Tolerance)
        {
            reasons.Add("initial_point_retiming_budget_exceeded");
        }
        return new AdaptiveQualityBudgetEvaluation(reasons.Count == 0, reasons);
    }
}

internal sealed record AdaptiveQualityCandidate(
    string CandidateId,
    StructuralSingletonGlobalTrialScore Score,
    AdaptiveSingletonQualityMetrics Quality,
    bool StrictImprovement);

internal sealed record AdaptiveNoWinnerQualityClassification(
    bool PolicyStagnation,
    bool QualityBudgetBlocksAllImprovingCandidates,
    int ImprovingCandidateCount,
    int BudgetRejectedCandidateCount);

internal static class AdaptiveQualityEligibleSelector
{
    internal static AdaptiveQualityCandidate? SelectBest(
        IReadOnlyList<AdaptiveQualityCandidate> candidates,
        AdaptiveQualityBudgetOptions budgets) => candidates
        .Where(item => item.StrictImprovement &&
            AdaptiveQualityBudgetPolicy.Evaluate(item.Quality, budgets).Eligible)
        .OrderBy(item => item.Score, Comparer<StructuralSingletonGlobalTrialScore>.Create(
            StructuralSingletonGlobalTrialSelectionPolicy.Compare))
        .FirstOrDefault();

    internal static AdaptiveNoWinnerQualityClassification ClassifyNoWinner(
        IReadOnlyList<AdaptiveQualityCandidate> candidates,
        AdaptiveQualityBudgetOptions budgets)
    {
        List<AdaptiveQualityCandidate> improving = candidates
            .Where(item => item.StrictImprovement)
            .ToList();
        int rejected = improving.Count(item =>
            !AdaptiveQualityBudgetPolicy.Evaluate(item.Quality, budgets).Eligible);
        return new AdaptiveNoWinnerQualityClassification(
            PolicyStagnation: improving.Count == 0,
            QualityBudgetBlocksAllImprovingCandidates:
                improving.Count > 0 && rejected == improving.Count,
            improving.Count,
            rejected);
    }
}

internal sealed record AdaptiveAcceptedEfficiencyPoint(
    int Gain,
    double RoundRuntimeSeconds)
{
    internal double? RuntimePerRemovedConflict => Gain > 0
        ? RoundRuntimeSeconds / Gain
        : null;
}

internal sealed record AdaptiveEfficiencyWindowMetrics(
    double RollingMeanGain,
    double RollingMedianRuntimePerRemovedConflict,
    int RollingTotalGain,
    int AcceptedRoundCount);

internal static class AdaptiveEfficiencyWindow
{
    internal static AdaptiveEfficiencyWindowMetrics Calculate(
        IReadOnlyList<AdaptiveAcceptedEfficiencyPoint> acceptedRounds,
        int windowSize)
    {
        ArgumentNullException.ThrowIfNull(acceptedRounds);
        if (windowSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(windowSize));
        }
        List<AdaptiveAcceptedEfficiencyPoint> window = acceptedRounds
            .Skip(Math.Max(0, acceptedRounds.Count - windowSize))
            .ToList();
        if (window.Count == 0)
        {
            return new AdaptiveEfficiencyWindowMetrics(0.0, 0.0, 0, 0);
        }
        int totalGain = window.Sum(item => item.Gain);
        List<double> perConflict = window
            .Select(item => item.RuntimePerRemovedConflict)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .Order()
            .ToList();
        double median = perConflict.Count == 0
            ? 0.0
            : perConflict.Count % 2 == 1
                ? perConflict[perConflict.Count / 2]
                : (perConflict[(perConflict.Count / 2) - 1] +
                   perConflict[perConflict.Count / 2]) / 2.0;
        return new AdaptiveEfficiencyWindowMetrics(
            (double)totalGain / window.Count,
            median,
            totalGain,
            window.Count);
    }
}
