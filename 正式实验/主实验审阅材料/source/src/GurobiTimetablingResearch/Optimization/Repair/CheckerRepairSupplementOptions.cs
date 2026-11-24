// 修复附加时分选项封装运行时分松弛比例及启用条件，为结构修复模型提供受控的连续补救范围。
// 独立选项对象使正常主模型保持原始时分语义，只有指定修复阶段才扩展相应变量边界。
namespace GurobiTimetablingResearch.Optimization;

public sealed record CheckerRepairSupplementOptions
{
    private CheckerRepairSupplementOptions(double ratio)
    {
        Ratio = ratio;
    }

    public double Ratio { get; }

    public static CheckerRepairSupplementOptions Create(double ratio)
    {
        if (!double.IsFinite(ratio) || ratio < 0.0 || ratio > 0.20)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ratio), ratio,
                "Checker repair running-time supplement ratio must be finite and within [0, 0.20].");
        }

        return new CheckerRepairSupplementOptions(ratio);
    }
}
