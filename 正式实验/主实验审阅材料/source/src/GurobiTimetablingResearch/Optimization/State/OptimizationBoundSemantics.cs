namespace GurobiTimetablingResearch.Optimization;

/// <summary>
/// 标识快照在主—子问题协调过程中的用途，防止把仍有微观冲突的候选解当作完整问题可行解。
/// </summary>
public enum TimetableSolutionRole
{
    WorkingSolution,
    BestDiagnosticSolution,
    BestJointFeasibleSolution
}

/// <summary>
/// 标识求解器下界来自哪一类模型，用于判断该下界能否覆盖完整问题的决策空间。
/// </summary>
public enum SolverBoundScope
{
    None,
    GlobalStrengthenedMaster,
    LocalRepair,
    HeuristicTrial
}

/// <summary>
/// 分别保存原始业务一级目标和二级目标，避免把多目标模型的单个 ObjVal 当作含义明确的业务指标。
/// </summary>
public sealed record BusinessObjectiveVector
{
    public BusinessObjectiveVector(
        double PrimaryGlobalSpanSeconds,
        double SecondaryRunningTimeSupplementSeconds)
    {
        if (!double.IsFinite(PrimaryGlobalSpanSeconds) || PrimaryGlobalSpanSeconds < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(PrimaryGlobalSpanSeconds));
        }

        if (!double.IsFinite(SecondaryRunningTimeSupplementSeconds) ||
            SecondaryRunningTimeSupplementSeconds < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(SecondaryRunningTimeSupplementSeconds));
        }

        this.PrimaryGlobalSpanSeconds = PrimaryGlobalSpanSeconds;
        this.SecondaryRunningTimeSupplementSeconds = SecondaryRunningTimeSupplementSeconds;
    }

    public double PrimaryGlobalSpanSeconds { get; }
    public double SecondaryRunningTimeSupplementSeconds { get; }
}

/// <summary>
/// 将求解器可行性容差内的轻微负数归一为零，同时拒绝真正违反非负下界的数值。
/// </summary>
public static class SolverNumericalValueNormalizer
{
    public const double BoundTolerance = 1e-6;

    public static double NormalizeNonNegative(double value)
    {
        if (!double.IsFinite(value) || value < -BoundTolerance)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                "The solver value must be finite and cannot violate its non-negative bound beyond tolerance.");
        }

        return value < 0.0 ? 0.0 : value;
    }
}

/// <summary>
/// 使用所有列车的首发、终到和运行时分附加量计算当前模型定义的两级业务目标。
/// </summary>
public static class BusinessObjectiveCalculator
{
    public static BusinessObjectiveVector Calculate(
        IEnumerable<double> originDepartureSeconds,
        IEnumerable<double> destinationArrivalSeconds,
        IEnumerable<double> runningTimeSupplementSeconds)
    {
        double[] origins = ValidateTimes(originDepartureSeconds, nameof(originDepartureSeconds));
        double[] destinations = ValidateTimes(destinationArrivalSeconds, nameof(destinationArrivalSeconds));
        double[] supplements = ValidateSupplements(runningTimeSupplementSeconds);

        return new BusinessObjectiveVector(
            destinations.Max() - origins.Min(),
            supplements.Sum());
    }

    private static double[] ValidateTimes(IEnumerable<double> values, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values);
        double[] materialized = values.ToArray();
        if (materialized.Length == 0 || materialized.Any(value => !double.IsFinite(value)))
        {
            throw new ArgumentException("Time values must be non-empty and finite.", parameterName);
        }

        return materialized;
    }

    private static double[] ValidateSupplements(IEnumerable<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        double[] materialized;
        try
        {
            materialized = values
                .Select(SolverNumericalValueNormalizer.NormalizeNonNegative)
                .ToArray();
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new ArgumentException(
                "Running-time supplements must be finite and non-negative within solver tolerance.",
                nameof(values),
                exception);
        }

        return materialized;
    }
}

/// <summary>
/// 保存经过作用域校验后的完整问题上下界；空值表示当前证据不足以形成对应的全局界。
/// </summary>
public sealed record OptimizationBoundReport(
    double? GlobalLowerBound,
    double? GlobalUpperBound,
    double? GlobalRelativeGap,
    SolverBoundScope SolverBoundScope);

/// <summary>
/// 根据解角色、Macro/Micro 验证结果和模型作用域筛选可用于完整问题的 UB、LB 与 gap。
/// </summary>
public static class OptimizationBoundPolicy
{
    public static OptimizationBoundReport Evaluate(
        TimetableSolutionRole solutionRole,
        SolverBoundScope solverBoundScope,
        bool macroValidated,
        bool microFeasible,
        BusinessObjectiveVector objective,
        double? solverLowerBound)
    {
        ArgumentNullException.ThrowIfNull(objective);

        if (solutionRole == TimetableSolutionRole.BestJointFeasibleSolution &&
            (!macroValidated || !microFeasible))
        {
            throw new ArgumentException(
                "BestJointFeasibleSolution requires both Macro validation and a feasible Global Micro result.",
                nameof(solutionRole));
        }

        if (solverLowerBound.HasValue && !double.IsFinite(solverLowerBound.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(solverLowerBound));
        }

        double? globalUpperBound = solutionRole == TimetableSolutionRole.BestJointFeasibleSolution &&
                                   macroValidated &&
                                   microFeasible
            ? objective.PrimaryGlobalSpanSeconds
            : null;

        double? globalLowerBound = solverBoundScope == SolverBoundScope.GlobalStrengthenedMaster
            ? solverLowerBound
            : null;

        double? globalRelativeGap = globalUpperBound.HasValue && globalLowerBound.HasValue
            ? CalculateRelativeGap(globalUpperBound.Value, globalLowerBound.Value)
            : null;

        return new OptimizationBoundReport(
            globalLowerBound,
            globalUpperBound,
            globalRelativeGap,
            solverBoundScope);
    }

    private static double CalculateRelativeGap(double upperBound, double lowerBound)
    {
        double denominator = Math.Max(Math.Abs(upperBound), 1e-9);
        return Math.Max(0.0, upperBound - lowerBound) / denominator;
    }
}
