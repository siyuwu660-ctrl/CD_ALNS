// 运行时分附加策略计算修复阶段允许增加的区间运行秒数，并验证实际补偿量没有越过比例界限。
// 策略把正常时分与临时补救界分开表达，使 Checker 可以有限放松时间而不永久改变基础运行标尺。
namespace GurobiTimetablingResearch.Optimization;

public enum RunningTimeValidationOutcome
{
    Valid,
    BelowMinimum,
    SupplementExceeded
}

public sealed class RunningTimeSupplementPolicy
{
    private RunningTimeSupplementPolicy(double ratio)
    {
        Ratio = ratio;
    }

    public double Ratio { get; }

    public static RunningTimeSupplementPolicy Create(double ratio)
    {
        if (double.IsNaN(ratio) || double.IsInfinity(ratio) || ratio < 0.0 || ratio > 0.20)
        {
            throw new ArgumentOutOfRangeException(nameof(ratio), "Running-time supplement ratio must be in [0, 0.20].");
        }
        return new RunningTimeSupplementPolicy(ratio);
    }

    public int ResolveMaximumSeconds(int baseRuntimeSeconds)
    {
        if (baseRuntimeSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(baseRuntimeSeconds));
        }
        return (int)Math.Ceiling(baseRuntimeSeconds * Ratio);
    }

    public static string BuildKey(int trainIndex, string sectionName) => $"{trainIndex}::{sectionName}";
}

public static class RunningTimeSupplementValidator
{
    public static RunningTimeValidationOutcome Classify(
        double realizedSeconds,
        double requiredBaseSeconds,
        double maximumSupplementSeconds,
        double toleranceSeconds)
    {
        if (realizedSeconds < requiredBaseSeconds - toleranceSeconds)
        {
            return RunningTimeValidationOutcome.BelowMinimum;
        }
        return realizedSeconds > requiredBaseSeconds + maximumSupplementSeconds + toleranceSeconds
            ? RunningTimeValidationOutcome.SupplementExceeded
            : RunningTimeValidationOutcome.Valid;
    }
}
