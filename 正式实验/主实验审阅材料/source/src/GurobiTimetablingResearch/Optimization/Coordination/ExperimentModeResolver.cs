// 实验模式解析器把互斥的命令行开关归一为单一 ExperimentMode，并校验每种模式所需或禁止的参数组合。
// 强类型请求对象让入口程序按模式分派，避免求解流程依赖散落的布尔开关组合。
namespace GurobiTimetablingResearch.Optimization;

public enum ExperimentMode
{
    MacroBootstrap,
    MicroCheckOnly,
    OneCheckerIteration,
    FixedSnapshotMultiIterationChecker,
    ValidityFirstBendersChecker,
    ControlledCandidateRepair,
    StandardMaster
}

public sealed record ExperimentModeRequest(
    ExperimentMode Mode,
    string? MasterSnapshotPath,
    int? CheckerMaxIterations,
    string? StructuralCandidateId = null,
    bool AdaptiveSingletonConvergence = false,
    CheckerCoordinationStrategy CoordinationStrategy = CheckerCoordinationStrategy.Legacy);

public static class ExperimentModeResolver
{
    private const string SnapshotPrefix = "--master-snapshot-input=";
    private const string CheckerIterationsPrefix = "--checker-loop-max-iterations=";
    private const string RepairSupplementPrefix = "--checker-repair-running-time-supplement-ratio=";
    private const string MacroSupplementPrefix = "--running-time-supplement-ratio=";
    private const string StructuralCandidatePrefix = "--structural-candidate-id=";
    private const string InitialCandidateCapPrefix = "--checker-loop-initial-candidate-cap=";
    private const string ExpandedCandidateCapPrefix = "--checker-loop-expanded-candidate-cap=";
    private const string InitialReferenceSnapshotPrefix =
        "--checker-loop-initial-reference-snapshot-input=";
    private const string AdaptiveArtifactsOutputPrefix =
        "--checker-adaptive-artifacts-output=";
    private const string AdaptiveExpectedRootPrefix =
        "--checker-adaptive-expected-initial-root-conflicts=";

    private static readonly string[] ResumeInputPrefixes =
    [
        "--checker-loop-import-artifacts=",
        "--checker-loop-checkpoint-input="
    ];

    public static ExperimentModeRequest Resolve(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        bool microCheckOnly = HasFlag(args, "--micro-check-only");
        bool checkerLoop = HasFlag(args, "--checker-loop");
        bool multiIterationChecker = HasFlag(args, "--checker-loop-multi-iteration");
        bool validityFirstBenders = HasFlag(args, "--checker-loop-validity-first-benders");
        bool phasedFeasibilityRecovery = HasFlag(args, "--checker-validity-phased-recovery");
        bool controlledCandidateRepair = HasFlag(args, "--controlled-candidate-repair");
        bool rolling = HasFlag(args, "--rolling") || HasFlag(args, "--rolling-adaptive");
        string? snapshotPath = ReadOptionalValue(args, SnapshotPrefix);
        string? structuralCandidateId = ReadOptionalValue(args, StructuralCandidatePrefix);
        int? checkerIterations = ReadOptionalInt(args, CheckerIterationsPrefix);
        bool hasRepairSupplement = HasValueArgument(args, RepairSupplementPrefix);
        bool hasMacroSupplement = HasValueArgument(args, MacroSupplementPrefix);
        bool hasResumeInput = ResumeInputPrefixes.Any(prefix => HasValueArgument(args, prefix));
        bool hasCheckpointInput = HasValueArgument(args, "--checker-loop-checkpoint-input=");
        bool hasArtifactImport = HasValueArgument(args, "--checker-loop-import-artifacts=");
        bool hasCheckpointOutput = HasValueArgument(args, "--checker-loop-checkpoint-output=");
        bool localMicroRepair = HasFlag(args, "--checker-loop-local-micro-repair");
        bool iisGuidedRepair = HasFlag(args, "--checker-iis-guided-repair");
        bool zeroConflictChecker = HasFlag(args, "--checker-loop-zero-conflict");
        bool evaluateFeasibleSingletons = HasFlag(
            args, "--checker-global-evaluate-feasible-singletons");
        bool adaptiveSingletonConvergence = HasFlag(
            args, "--checker-loop-adaptive-singleton-convergence");
        bool componentWiseRepair = HasFlag(args, "--checker-loop-component-wise-repair");
        int? initialCandidateCap = ReadOptionalInt(args, InitialCandidateCapPrefix);
        int? expandedCandidateCap = ReadOptionalInt(args, ExpandedCandidateCapPrefix);
        bool hasAlternateInitialReference =
            HasValueArgument(args, InitialReferenceSnapshotPrefix);
        string? adaptiveArtifactsOutput = ReadOptionalValue(args, AdaptiveArtifactsOutputPrefix);
        bool hasAdaptiveArtifactsOutput = adaptiveArtifactsOutput is not null;
        int? adaptiveExpectedRootConflicts = ReadOptionalInt(args, AdaptiveExpectedRootPrefix);

        int explicitModeCount = (microCheckOnly ? 1 : 0) +
                                (checkerLoop ? 1 : 0) +
                                (multiIterationChecker ? 1 : 0) +
                                (validityFirstBenders ? 1 : 0) +
                                (controlledCandidateRepair ? 1 : 0);
        if (explicitModeCount > 1)
        {
            throw new ArgumentException(
                "--micro-check-only, --checker-loop, --checker-loop-multi-iteration, --checker-loop-validity-first-benders, and --controlled-candidate-repair are mutually exclusive.");
        }

        Reject(
            evaluateFeasibleSingletons && !checkerLoop && !multiIterationChecker,
            "--checker-global-evaluate-feasible-singletons requires --checker-loop or --checker-loop-multi-iteration.");
        Reject(
            evaluateFeasibleSingletons && !zeroConflictChecker,
            "--checker-global-evaluate-feasible-singletons requires --checker-loop-zero-conflict.");
        Reject(
            adaptiveSingletonConvergence && !multiIterationChecker,
            "--checker-loop-adaptive-singleton-convergence requires --checker-loop-multi-iteration.");
        Reject(
            hasAdaptiveArtifactsOutput && !adaptiveSingletonConvergence,
            "--checker-adaptive-artifacts-output requires Adaptive singleton convergence.");
        Reject(
            adaptiveExpectedRootConflicts.HasValue && !adaptiveSingletonConvergence,
            "--checker-adaptive-expected-initial-root-conflicts requires Adaptive singleton convergence.");
        if (adaptiveSingletonConvergence)
        {
            int resolvedInitialCap = initialCandidateCap ?? 20;
            int resolvedExpandedCap = expandedCandidateCap ?? 40;
            Reject(resolvedInitialCap <= 0,
                "Adaptive singleton initial candidate cap must be greater than zero.");
            Reject(resolvedExpandedCap < resolvedInitialCap,
                "Adaptive singleton expanded candidate cap must be greater than or equal to the initial cap.");
            Reject(componentWiseRepair,
                "Adaptive singleton convergence cannot enable component-wise repair.");
            Reject(hasAlternateInitialReference,
                "Task 7D-2B1 Adaptive mode uses --master-snapshot-input as its immutable initial reference.");
            Reject(adaptiveExpectedRootConflicts < 0,
                "Adaptive expected initial root conflicts must be non-negative.");
            Reject(hasAdaptiveArtifactsOutput && string.IsNullOrWhiteSpace(adaptiveArtifactsOutput),
                "Adaptive artifact output path must be non-empty.");
            Reject(adaptiveExpectedRootConflicts.HasValue && hasCheckpointInput,
                "Adaptive initial-root baseline validation is available only for a fresh Master snapshot.");
        }

        if (validityFirstBenders)
        {
            Reject(rolling, "ValidityFirstBendersChecker cannot be combined with Rolling.");
            Reject(!zeroConflictChecker,
                "ValidityFirstBendersChecker requires --checker-loop-zero-conflict.");
            Reject(localMicroRepair,
                "ValidityFirstBendersChecker cannot execute the Legacy LocalMicroExact scheduling path.");
            Reject(adaptiveSingletonConvergence,
                "ValidityFirstBendersChecker cannot execute Adaptive Singleton convergence.");
            Reject(evaluateFeasibleSingletons,
                "ValidityFirstBendersChecker does not use the Legacy feasible-singleton sweep.");
            Reject(componentWiseRepair,
                "ValidityFirstBendersChecker does not use the Legacy component-wise repair scheduler.");
            Reject(hasArtifactImport,
                "ValidityFirstBendersChecker cannot import legacy Checker artifacts.");
            Reject(hasMacroSupplement,
                "ValidityFirstBendersChecker cannot change the frozen Macro running-time supplement policy.");
            Reject(hasRepairSupplement,
                "ValidityFirstBendersChecker does not use the Legacy local-repair supplement option.");
            Reject(!string.IsNullOrWhiteSpace(structuralCandidateId),
                "--structural-candidate-id requires --controlled-candidate-repair.");
            Reject(!string.IsNullOrWhiteSpace(snapshotPath) && hasCheckpointInput,
                "ValidityFirstBendersChecker cannot combine a Master snapshot with a native checkpoint input.");
            Reject(!hasCheckpointOutput,
                "ValidityFirstBendersChecker requires --checker-loop-checkpoint-output.");
            if (!checkerIterations.HasValue || checkerIterations.Value < 1)
            {
                throw new ArgumentException(
                    "ValidityFirstBendersChecker requires --checker-loop-max-iterations=N with N >= 1.");
            }

            return new ExperimentModeRequest(
                ExperimentMode.ValidityFirstBendersChecker,
                snapshotPath,
                checkerIterations,
                CoordinationStrategy: CheckerCoordinationStrategy.ValidityFirstBenders);
        }

        Reject(phasedFeasibilityRecovery,
            "--checker-validity-phased-recovery requires --checker-loop-validity-first-benders.");

        if (microCheckOnly)
        {
            RequireSnapshot(snapshotPath, ExperimentMode.MicroCheckOnly);
            Reject(rolling, "Mode B cannot be combined with Rolling.");
            Reject(hasRepairSupplement, "Mode B cannot configure repair running-time supplement.");
            Reject(hasMacroSupplement, "Mode B cannot configure the Macro running-time supplement.");
            Reject(hasResumeInput || hasCheckpointOutput, "Mode B cannot import or resume Checker state.");
            Reject(checkerIterations.HasValue, "Mode B cannot configure Checker iterations.");
            Reject(!string.IsNullOrWhiteSpace(structuralCandidateId),
                "--structural-candidate-id requires --controlled-candidate-repair.");
            return new ExperimentModeRequest(ExperimentMode.MicroCheckOnly, snapshotPath, null);
        }

        if (checkerLoop)
        {
            RequireSnapshot(snapshotPath, ExperimentMode.OneCheckerIteration);
            Reject(rolling, "Mode C cannot be combined with Rolling.");
            Reject(hasResumeInput || hasCheckpointOutput, "Mode C cannot import or resume Checker state.");
            Reject(hasMacroSupplement, "Mode C cannot configure the Macro running-time supplement.");
            Reject(!string.IsNullOrWhiteSpace(structuralCandidateId),
                "--structural-candidate-id requires --controlled-candidate-repair.");
            if (checkerIterations != 1)
            {
                throw new ArgumentException(
                    "Mode C requires an explicit --checker-loop-max-iterations=1.");
            }
            return new ExperimentModeRequest(
                ExperimentMode.OneCheckerIteration,
                snapshotPath,
                checkerIterations);
        }

        if (multiIterationChecker)
        {
            if (adaptiveSingletonConvergence)
            {
                Reject(hasArtifactImport,
                    "Adaptive singleton convergence cannot resume from legacy Checker artifacts.");
                Reject(string.IsNullOrWhiteSpace(snapshotPath) == !hasCheckpointInput,
                    "Adaptive singleton convergence requires exactly one fresh Master snapshot or checkpoint input.");
                Reject(!hasCheckpointOutput,
                    "Adaptive singleton convergence requires --checker-loop-checkpoint-output.");
            }
            else
            {
                RequireSnapshot(snapshotPath, ExperimentMode.FixedSnapshotMultiIterationChecker);
            }
            Reject(rolling, "FixedSnapshotMultiIterationChecker cannot be combined with Rolling.");
            Reject(!adaptiveSingletonConvergence && hasResumeInput,
                "FixedSnapshotMultiIterationChecker cannot import or resume a different Checker state.");
            Reject(hasMacroSupplement,
                "FixedSnapshotMultiIterationChecker cannot configure the Macro running-time supplement.");
            Reject(!string.IsNullOrWhiteSpace(structuralCandidateId),
                "--structural-candidate-id requires --controlled-candidate-repair.");
            Reject(adaptiveSingletonConvergence && !zeroConflictChecker,
                "Adaptive singleton convergence requires --checker-loop-zero-conflict.");
            Reject(adaptiveSingletonConvergence && !evaluateFeasibleSingletons,
                "Adaptive singleton convergence requires --checker-global-evaluate-feasible-singletons.");
            Reject(adaptiveSingletonConvergence && !iisGuidedRepair,
                "Adaptive singleton convergence requires --checker-iis-guided-repair.");
            Reject(adaptiveSingletonConvergence && localMicroRepair,
                "Adaptive singleton convergence cannot enable LocalMicroExact repair.");
            Reject(!adaptiveSingletonConvergence && hasCheckpointOutput,
                "FixedSnapshotMultiIterationChecker checkpoint output requires adaptive singleton convergence.");
            int minimumIterationCount = adaptiveSingletonConvergence ? 1 : 2;
            if (!checkerIterations.HasValue || checkerIterations.Value < minimumIterationCount)
            {
                throw new ArgumentException(
                    $"FixedSnapshotMultiIterationChecker requires an explicit --checker-loop-max-iterations=N with N >= {minimumIterationCount}.");
            }
            return new ExperimentModeRequest(
                ExperimentMode.FixedSnapshotMultiIterationChecker,
                snapshotPath,
                checkerIterations,
                AdaptiveSingletonConvergence: adaptiveSingletonConvergence,
                CoordinationStrategy: adaptiveSingletonConvergence
                    ? CheckerCoordinationStrategy.AdaptiveSingleton
                    : CheckerCoordinationStrategy.Legacy);
        }

        if (controlledCandidateRepair)
        {
            RequireSnapshot(snapshotPath, ExperimentMode.ControlledCandidateRepair);
            if (string.IsNullOrWhiteSpace(structuralCandidateId))
            {
                throw new ArgumentException(
                    "ControlledCandidateRepair requires a non-empty --structural-candidate-id.");
            }
            Reject(rolling, "ControlledCandidateRepair cannot be combined with Rolling.");
            Reject(checkerIterations.HasValue,
                "ControlledCandidateRepair does not execute a Checker iteration.");
            Reject(hasResumeInput || hasCheckpointOutput,
                "ControlledCandidateRepair cannot import, resume, or update Checker state.");
            Reject(hasMacroSupplement,
                "ControlledCandidateRepair cannot configure the Macro running-time supplement.");
            Reject(localMicroRepair,
                "ControlledCandidateRepair cannot enable LocalMicroExact repair.");
            Reject(!iisGuidedRepair,
                "ControlledCandidateRepair requires --checker-iis-guided-repair.");
            return new ExperimentModeRequest(
                ExperimentMode.ControlledCandidateRepair,
                snapshotPath,
                null,
                structuralCandidateId);
        }

        Reject(checkerIterations.HasValue,
            "Checker iterations require --checker-loop or --checker-loop-multi-iteration.");
        Reject(hasRepairSupplement, "Repair running-time supplement requires Mode C.");
        Reject(hasResumeInput || hasCheckpointOutput, "Checker resume inputs require a Checker mode.");
        Reject(!string.IsNullOrWhiteSpace(snapshotPath),
            "--master-snapshot-input requires --micro-check-only, --checker-loop, or --checker-loop-multi-iteration.");
        Reject(!string.IsNullOrWhiteSpace(structuralCandidateId),
            "--structural-candidate-id requires --controlled-candidate-repair.");

        return new ExperimentModeRequest(
            rolling ? ExperimentMode.MacroBootstrap : ExperimentMode.StandardMaster,
            null,
            null);
    }

    private static void RequireSnapshot(string? snapshotPath, ExperimentMode mode)
    {
        if (string.IsNullOrWhiteSpace(snapshotPath))
        {
            throw new ArgumentException($"{mode} requires a non-empty --master-snapshot-input.");
        }
    }

    private static bool HasFlag(IReadOnlyList<string> args, string flag) =>
        args.Any(arg => string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase));

    private static bool HasValueArgument(IReadOnlyList<string> args, string prefix) =>
        args.Any(arg => arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static string? ReadOptionalValue(IReadOnlyList<string> args, string prefix)
    {
        List<string> matches = args
            .Where(arg => arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count > 1)
        {
            throw new ArgumentException($"Argument '{prefix}' may be supplied only once.");
        }
        return matches.Count == 0 ? null : matches[0][prefix.Length..];
    }

    private static int? ReadOptionalInt(IReadOnlyList<string> args, string prefix)
    {
        string? raw = ReadOptionalValue(args, prefix);
        if (raw is null)
        {
            return null;
        }
        if (!int.TryParse(raw, out int value))
        {
            throw new ArgumentException($"Argument '{prefix}' requires a valid integer value.");
        }
        return value;
    }

    private static void Reject(bool condition, string message)
    {
        if (condition)
        {
            throw new ArgumentException(message);
        }
    }
}
