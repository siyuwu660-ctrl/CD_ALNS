// 程序流程权限组件根据命令行模式集中判断哪些求解、校验、修复和导出阶段可以执行。
// 同文件中的参数解析器与快照加载器校验修复比例及输入来源，防止互斥流程或不完整状态被组合运行。
using System.Globalization;

namespace GurobiTimetablingResearch.Optimization;

/// <summary>
/// 将独立协调实验的终止状态映射为进程退出码，避免把中断或无完整可行解的运行包装成成功结果。
/// </summary>
public static class CheckerLoopProcessExitCodePolicy
{
    public static int Resolve(
        CheckerCoordinationStrategy coordinationStrategy,
        string terminationReason,
        bool hasBestJointFeasibleSolution)
    {
        if (coordinationStrategy != CheckerCoordinationStrategy.ValidityFirstBenders)
        {
            return 0;
        }

        return hasBestJointFeasibleSolution && (
            string.Equals(
                terminationReason,
                "MICRO_FEASIBLE_SUCCESS",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                terminationReason,
                "joint_feasible_first_hit",
                StringComparison.OrdinalIgnoreCase))
            ? 0
            : 1;
    }
}

public sealed record ProgramFlowPermissions(
    bool AllowsRolling,
    bool AllowsMasterValidation,
    int MaximumMicroCheckCount,
    bool AllowsRepair,
    bool AllowsStructuralFeedback)
{
    public static ProgramFlowPermissions For(
        ExperimentMode mode,
        int? checkerMaxIterations = null) => mode switch
    {
        ExperimentMode.MacroBootstrap => new(true, true, 0, false, false),
        ExperimentMode.MicroCheckOnly => new(false, true, 1, false, false),
        ExperimentMode.OneCheckerIteration => new(false, true, 2, true, true),
        ExperimentMode.FixedSnapshotMultiIterationChecker => new(
            false,
            true,
            ResolveMultiIterationMicroCheckCount(checkerMaxIterations),
            true,
            true),
        ExperimentMode.ValidityFirstBendersChecker => new(
            false,
            true,
            ResolveValidityFirstMicroCheckCount(checkerMaxIterations),
            true,
            true),
        ExperimentMode.ControlledCandidateRepair => new(false, true, 2, true, true),
        ExperimentMode.StandardMaster => new(false, true, 1, false, false),
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
    };

    private static int ResolveMultiIterationMicroCheckCount(int? checkerMaxIterations)
    {
        if (!checkerMaxIterations.HasValue || checkerMaxIterations.Value < 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(checkerMaxIterations),
                "FixedSnapshotMultiIterationChecker requires at least two Checker iterations.");
        }
        return checked(checkerMaxIterations.Value + 1);
    }

    private static int ResolveValidityFirstMicroCheckCount(int? checkerMaxIterations)
    {
        if (!checkerMaxIterations.HasValue || checkerMaxIterations.Value < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(checkerMaxIterations),
                "ValidityFirstBendersChecker requires at least one coordination iteration.");
        }

        return checked(checkerMaxIterations.Value + 1);
    }

    public void EnsureStructuralRepairAllowed()
    {
        if (!AllowsRepair || !AllowsStructuralFeedback)
        {
            throw new InvalidOperationException("The selected experiment mode forbids structural repair.");
        }
    }
}

public static class CheckerRepairSupplementArgumentParser
{
    public static double Parse(IReadOnlyList<string> args, string prefix)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (string.IsNullOrWhiteSpace(prefix))
        {
            throw new ArgumentException("A repair supplement argument prefix is required.", nameof(prefix));
        }
        List<string> matches = args
            .Where(arg => arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count == 0)
        {
            return CheckerRepairSupplementOptions.Create(0.05).Ratio;
        }
        if (matches.Count > 1)
        {
            throw new ArgumentException($"Argument '{prefix}' may be supplied only once.");
        }

        string raw = matches[0][prefix.Length..];
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double ratio))
        {
            throw new ArgumentException($"Argument '{prefix}' requires a valid number.");
        }
        return CheckerRepairSupplementOptions.Create(ratio).Ratio;
    }
}

public static class MasterSnapshotInputLoader
{
    public static MasterSolutionSnapshot Load(
        string path,
        string expectedInstanceName,
        IReadOnlySet<int> expectedTrainIndices)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A Master snapshot input path is required.", nameof(path));
        }
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Required Master snapshot input was not found.", fullPath);
        }
        return MasterSolutionSnapshotStore.Load(fullPath, expectedInstanceName, expectedTrainIndices);
    }
}
