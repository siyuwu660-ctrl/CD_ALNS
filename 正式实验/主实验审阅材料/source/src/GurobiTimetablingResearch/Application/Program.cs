// 程序入口负责解析命令行参数、装载标准化数据集，并把请求分派到完整 MILP、滚动时域、Checker 循环或自适应单候选流程。
// 各模式共享 TimetablingInstance、解快照和检查点对象，使求解、续跑、验证及产物导出使用同一套数据语义。
using Gurobi;
using System.Diagnostics;
using GurobiTimetablingResearch.Domain;
using GurobiTimetablingResearch.IO;
using GurobiTimetablingResearch.Optimization;

namespace GurobiTimetablingResearch;

/// <summary>
/// Gurobi 运行图研究工程的控制台入口。
/// 当前入口承担 master v1 的完整闭环：
/// 1. 读取统一格式后的 JZ/JH instance；
/// 2. 构建基础宏观 MILP；
/// 3. 导出 LP 和结构诊断报告；
/// 4. 在指定 --solve 时调用 Gurobi 求解；
/// 5. 对 incumbent solution 导出业务结果并执行宏观自检。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Contains("--server-package", StringComparer.Ordinal))
            return ServerExperimentRunner.Run(args);
        if (args.Contains("--strict-resource-repair", StringComparer.Ordinal))
            return StrictResourceRepairRunner.Run(args);
        if (args.Length==1 && args[0].StartsWith("--bound-build-provenance=",StringComparison.Ordinal))
            return StrengthenedSpanBoundRunner.WriteBuildEvidence(Path.GetFullPath(args[0]["--bound-build-provenance=".Length..]));
        if (args.Contains("--strengthened-span-bound", StringComparer.Ordinal))
            return StrengthenedSpanBoundRunner.Run(args);
        if (args.Contains("--structural-span-bound", StringComparer.Ordinal))
            return StructuralSpanBoundRunner.Run(args);
        if (args.Contains("--direct-joint-mip", StringComparer.Ordinal))
            return DirectJointMipRunner.Run(args);
        if (args.Contains("--corman-lbbd", StringComparer.Ordinal))
            return CormanLbbdRunner.Run(args);
        // --from-zero-alns enables independent adaptive macro and resource policies;
        // --from-zero-no-macro-alns retains the explicit legacy macro control.
        if (args.Contains("--jh-from-zero", StringComparer.Ordinal))
            return JhFromZeroRunner.Run(args);
        if (args.Contains("--resource-lns", StringComparer.Ordinal))
            return ResourceLnsRunner.Run(args);
        if (args.Contains("--directed-repair", StringComparer.Ordinal))
            return DirectedRepairRunner.Run(args);
        if (args.Contains("--bottleneck-experiment", StringComparer.Ordinal))
            return BottleneckExperimentRunner.Run(args);
        if (args.Contains("--stages-4-5", StringComparer.Ordinal))
            return Stages45Runner.Run(args);
        if (args.Contains("--stages-0-3", StringComparer.Ordinal))
            return Stages03Runner.Run(args);
        Stopwatch processRuntimeClock = Stopwatch.StartNew();
        if (args.Any(static arg => string.Equals(
                arg, "--help", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "-h", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine(ProgramHelpText.Build());
            return 0;
        }
        ExperimentModeRequest experimentModeRequest = ExperimentModeResolver.Resolve(args);
        MacroMicroExecutionOptions macroMicroExecution = MacroMicroExecutionOptions.Parse(args);
        ProgramFlowPermissions flowPermissions = ProgramFlowPermissions.For(
            experimentModeRequest.Mode,
            experimentModeRequest.CheckerMaxIterations);
        string repositoryRoot = ResolveRepositoryRoot();
        string datasetPath = ResolveDatasetPath(args, repositoryRoot);
        string? jointFeasibleBaselineCheckpoint = ResolveOptionalStringArgument(
            args, "--verify-joint-feasible-checkpoint=");
        string? jointFeasibleBaselineOutput = ResolveOptionalStringArgument(
            args, "--joint-feasible-baseline-output=");
        string? artifactsOutput = ResolveOptionalStringArgument(args, "--artifacts-output=");
        if (macroMicroExecution.Mode == MacroMicroExecutionMode.Fresh)
            artifactsOutput = macroMicroExecution.OutputDirectory;
        bool shouldRunMicroStructureSuite = args.Any(static arg => string.Equals(
            arg, "--micro-structure-suite", StringComparison.OrdinalIgnoreCase));
        string? microStructureSnapshot = ResolveOptionalStringArgument(args, "--micro-structure-snapshot=");
        string? microStructureCheckpoint = ResolveOptionalStringArgument(args, "--micro-structure-checkpoint=");
        string? microStructureStationSummary = ResolveOptionalStringArgument(args, "--micro-structure-station-summary=");
        string? microStructureOutput = ResolveOptionalStringArgument(args, "--micro-structure-output=");
        bool microStructureRunFresh = args.Any(static arg => string.Equals(
            arg, "--micro-structure-run-fresh", StringComparison.OrdinalIgnoreCase));
        double microStructureArmBudgetSeconds = ResolveOptionalDoubleArgument(
            args, "--micro-structure-arm-budget-seconds=") ?? 600.0;
        double microStructureFreshBudgetSeconds = ResolveOptionalDoubleArgument(
            args, "--micro-structure-fresh-budget-seconds=") ?? 3600.0;
        string? microStructureRecoveryDiagnostic = ResolveOptionalStringArgument(
            args, "--micro-structure-recover-fresh-diagnostic=");
        double microStructureRecoveryBudgetSeconds = ResolveOptionalDoubleArgument(
            args, "--micro-structure-recovery-budget-seconds=") ?? 2650.0;
        bool shouldRunProofBindingRecovery = args.Any(static arg => string.Equals(
            arg, "--proof-binding-directed-recovery", StringComparison.OrdinalIgnoreCase));
        string? proofBindingDiagnostic = ResolveOptionalStringArgument(args, "--proof-binding-diagnostic=");
        string? proofBindingSummary = ResolveOptionalStringArgument(args, "--proof-binding-summary=");
        string? proofBindingOutput = ResolveOptionalStringArgument(args, "--proof-binding-output=");
        string? proofBindingResumeOutput = ResolveOptionalStringArgument(
            args, "--proof-binding-resume-after-diagnostic=");
        string? proofBindingRefreshOutput = ResolveOptionalStringArgument(
            args, "--proof-binding-refresh-report=");
        bool shouldRunMicroExtendability = args.Any(static arg => string.Equals(
            arg, "--micro-extendability", StringComparison.OrdinalIgnoreCase));
        string? microExtendabilityBaseline = ResolveOptionalStringArgument(
            args, "--micro-extendability-baseline=");
        string? microExtendabilityOutput = ResolveOptionalStringArgument(
            args, "--micro-extendability-output=");
        string? microExtendabilityFeedback = ResolveOptionalStringArgument(
            args, "--micro-extendability-feedback=");
        double microExtendabilityPilotSeconds = ResolveOptionalDoubleArgument(
            args, "--micro-extendability-pilot-seconds=") ?? 600.0;
        double microExtendabilityFormalSeconds = ResolveOptionalDoubleArgument(
            args, "--micro-extendability-formal-seconds=") ?? 3600.0;
        bool shouldRunMicroPatternTimeDomain = args.Any(static arg => string.Equals(
            arg, "--micro-pattern-time-domain", StringComparison.OrdinalIgnoreCase));
        string? microPatternBaseline = ResolveOptionalStringArgument(args, "--micro-pattern-baseline=");
        string? microPatternOutput = ResolveOptionalStringArgument(args, "--micro-pattern-output=");
        double microPatternBudgetSeconds = ResolveOptionalDoubleArgument(
            args, "--micro-pattern-budget-seconds=") ?? 7200.0;
        double microPatternMilestoneSeconds = ResolveOptionalDoubleArgument(
            args, "--micro-pattern-milestone-seconds=") ?? 3600.0;
        double microPatternPreviouslyConsumedSeconds = ResolveOptionalDoubleArgument(
            args, "--micro-pattern-previously-consumed-seconds=") ?? 0.0;
        bool shouldRunDynamicHotspotIntegrated = args.Any(static arg => string.Equals(
            arg, "--micro-hotspot-integrated", StringComparison.OrdinalIgnoreCase));
        string? dynamicHotspotBaseline = ResolveOptionalStringArgument(
            args, "--micro-hotspot-baseline=");
        string? dynamicHotspotOutput = ResolveOptionalStringArgument(
            args, "--micro-hotspot-output=");
        string? dynamicHotspotProof = ResolveOptionalStringArgument(
            args, "--micro-hotspot-proof=");
        double dynamicHotspotProbeSeconds = ResolveOptionalDoubleArgument(
            args, "--micro-hotspot-probe-seconds=") ?? 300.0;
        bool shouldRunMacroMicroFeedbackLoop = args.Any(static arg => string.Equals(
            arg, "--micro-macro-feedback-loop", StringComparison.OrdinalIgnoreCase));
        if (macroMicroExecution.IsRequested && shouldRunMacroMicroFeedbackLoop)
            throw new ArgumentException(
                "The new --macro-micro-fresh/--macro-micro-resume entries cannot be combined with the legacy --micro-macro-feedback-loop entry.");
        string? macroMicroFeedbackBaseline = ResolveOptionalStringArgument(
            args, "--micro-feedback-baseline=");
        string? macroMicroFeedbackOutput = ResolveOptionalStringArgument(
            args, "--micro-feedback-output=");
        string? macroMicroFeedbackProof = ResolveOptionalStringArgument(
            args, "--micro-feedback-proof=");
        double macroMicroFeedbackBudgetSeconds = ResolveOptionalDoubleArgument(
            args, "--micro-feedback-budget-seconds=") ?? 600.0;
        double macroMicroFeedbackIterationSeconds = ResolveOptionalDoubleArgument(
            args, "--micro-feedback-iteration-seconds=") ?? 90.0;
        bool shouldResumeDirectMacroFeedback = args.Any(static arg => string.Equals(
            arg, "--micro-feedback-resume-direct", StringComparison.OrdinalIgnoreCase));
        string? directFeedbackSnapshot = ResolveOptionalStringArgument(
            args, "--micro-feedback-resume-snapshot=");
        string? directFeedbackEvidence = ResolveOptionalStringArgument(
            args, "--micro-feedback-resume-evidence=");
        string? directFeedbackRetainedEvidence = ResolveOptionalStringArgument(
            args, "--micro-feedback-resume-retained-evidence=");
        string? directFeedbackRunTabu = ResolveOptionalStringArgument(
            args, "--micro-feedback-resume-run-tabu=");
        string? directFeedbackConflicts = ResolveOptionalStringArgument(
            args, "--micro-feedback-resume-conflicts=");
        string? directFeedbackOutput = ResolveOptionalStringArgument(
            args, "--micro-feedback-resume-output=");
        double directFeedbackBudgetSeconds = ResolveOptionalDoubleArgument(
            args, "--micro-feedback-resume-budget-seconds=") ?? 1800.0;
        double directFeedbackAttemptSeconds = ResolveOptionalDoubleArgument(
            args, "--micro-feedback-resume-attempt-seconds=") ?? 600.0;
        int directFeedbackMaximumAttempts = ResolveOptionalIntArgument(
            args, "--micro-feedback-resume-max-attempts=") ?? int.MaxValue;
        if (shouldResumeDirectMacroFeedback &&
            (string.IsNullOrWhiteSpace(directFeedbackSnapshot) ||
             string.IsNullOrWhiteSpace(directFeedbackOutput)))
            throw new ArgumentException(
                "--micro-feedback-resume-direct requires snapshot and output paths.");
        if (shouldRunMacroMicroFeedbackLoop && new[]
            {
                macroMicroFeedbackBaseline,
                macroMicroFeedbackOutput
            }.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException(
                "--micro-macro-feedback-loop requires baseline and output paths.");
        if (shouldRunDynamicHotspotIntegrated && new[] { dynamicHotspotBaseline, dynamicHotspotOutput }
            .Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException(
                "--micro-hotspot-integrated requires baseline and output paths.");
        if (shouldRunMicroPatternTimeDomain && new[] { microPatternBaseline, microPatternOutput }
            .Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException(
                "--micro-pattern-time-domain requires baseline and output paths.");
        if (shouldRunMicroExtendability && new[]
            {
                microExtendabilityBaseline,
                microExtendabilityOutput
            }.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "--micro-extendability requires baseline and output paths.");
        }
        if (shouldRunProofBindingRecovery && new[]
            {
                proofBindingDiagnostic,
                proofBindingSummary,
                proofBindingOutput
            }.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "--proof-binding-directed-recovery requires diagnostic, summary, and output paths.");
        }
        if (shouldRunMicroStructureSuite && new[]
            {
                microStructureSnapshot,
                microStructureCheckpoint,
                microStructureStationSummary,
                microStructureOutput
            }.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "--micro-structure-suite requires snapshot, checkpoint, station-summary, and output paths.");
        }
        if (string.IsNullOrWhiteSpace(jointFeasibleBaselineCheckpoint) !=
            string.IsNullOrWhiteSpace(jointFeasibleBaselineOutput))
        {
            throw new ArgumentException(
                "Joint-feasible baseline verification requires both checkpoint input and output directory.");
        }
        bool shouldSolve = macroMicroExecution.Mode == MacroMicroExecutionMode.Fresh ||
            args.Any(static arg => string.Equals(arg, "--solve", StringComparison.OrdinalIgnoreCase));
        bool shouldBuildFullMacroRelationsOnly = args.Any(static arg => string.Equals(
            arg, "--macro-full-relations-build-only", StringComparison.OrdinalIgnoreCase));
        bool shouldUseFullMacroRelations = macroMicroExecution.Mode == MacroMicroExecutionMode.Fresh ||
            args.Any(static arg => string.Equals(
                arg, "--macro-full-relations", StringComparison.OrdinalIgnoreCase));
        if (shouldBuildFullMacroRelationsOnly && shouldSolve)
        {
            throw new ArgumentException(
                "--macro-full-relations-build-only cannot be combined with --solve.");
        }
        bool shouldUseRolling = macroMicroExecution.Mode == MacroMicroExecutionMode.Fresh ||
            args.Any(static arg => string.Equals(arg, "--rolling", StringComparison.OrdinalIgnoreCase));
        bool shouldUseAdaptiveRolling = args.Any(static arg => string.Equals(arg, "--rolling-adaptive", StringComparison.OrdinalIgnoreCase));
        bool shouldUseCheckerLoop = experimentModeRequest.Mode is
            ExperimentMode.OneCheckerIteration or
            ExperimentMode.FixedSnapshotMultiIterationChecker or
            ExperimentMode.ValidityFirstBendersChecker;
        bool shouldUseZeroConflictChecker = args.Any(static arg => string.Equals(arg, "--checker-loop-zero-conflict", StringComparison.OrdinalIgnoreCase));
        bool shouldUseLocalMicroRepair = args.Any(static arg => string.Equals(arg, "--checker-loop-local-micro-repair", StringComparison.OrdinalIgnoreCase));
        bool shouldUseNeighborhoodEscalation = args.Any(static arg => string.Equals(arg, "--checker-loop-neighborhood-escalation", StringComparison.OrdinalIgnoreCase));
        bool shouldUseIisGuidedRepair =
            experimentModeRequest.CoordinationStrategy == CheckerCoordinationStrategy.ValidityFirstBenders ||
            args.Any(static arg => string.Equals(arg, "--checker-iis-guided-repair", StringComparison.OrdinalIgnoreCase));
        bool shouldUseAdaptiveSingletonConvergence =
            experimentModeRequest.AdaptiveSingletonConvergence;
        bool shouldEvaluateFeasibleSingletons = args.Any(static arg => string.Equals(
            arg, "--checker-global-evaluate-feasible-singletons", StringComparison.OrdinalIgnoreCase));
        bool disableMicroPromotion = args.Any(static arg => string.Equals(arg, "--disable-micro-promotion", StringComparison.OrdinalIgnoreCase));
        bool enablePhasedFeasibilityRecovery = args.Any(static arg => string.Equals(
            arg, "--checker-validity-phased-recovery", StringComparison.OrdinalIgnoreCase));
        bool shouldWriteIis = args.Any(static arg => string.Equals(arg, "--iis", StringComparison.OrdinalIgnoreCase));
        double? timeLimitSeconds = ResolveOptionalDoubleArgument(args, "--time-limit-seconds=");
        double? seedTimeLimitSeconds = ResolveOptionalDoubleArgument(args, "--rolling-seed-time-limit-seconds=");
        double? windowTimeLimitSeconds = ResolveOptionalDoubleArgument(args, "--rolling-window-time-limit-seconds=");
        double? adaptiveScoreBudget = ResolveOptionalDoubleArgument(args, "--rolling-adaptive-score-budget=");
        double? rollingMinWindowTime = ResolveOptionalDoubleArgument(args, "--rolling-min-window-time-limit-seconds=");
        double? rollingMaxWindowTime = ResolveOptionalDoubleArgument(args, "--rolling-max-window-time-limit-seconds=");
        int? neighborRadius = ResolveOptionalIntArgument(args, "--neighbor-radius=");
        int? expectedFullMacroRelationCount = ResolveOptionalIntArgument(
            args, "--macro-expected-full-relation-count=");
        if (shouldUseFullMacroRelations)
        {
            if (macroMicroExecution.Mode != MacroMicroExecutionMode.Fresh &&
                neighborRadius.HasValue && neighborRadius.Value != int.MaxValue)
            {
                throw new ArgumentException(
                    "--macro-full-relations cannot be combined with a finite --neighbor-radius.");
            }
            neighborRadius = int.MaxValue;
        }
        MacroMicroFreshBootstrapPlan? freshBootstrapPlan =
            macroMicroExecution.Mode == MacroMicroExecutionMode.Fresh
                ? MacroMicroFreshBootstrapPlan.Create(macroMicroExecution)
                : null;
        int? rollingNeighborRadius = freshBootstrapPlan?.RollingNeighborRadius ?? neighborRadius;
        int? rollingWindowSize = ResolveOptionalIntArgument(args, "--rolling-window-size=");
        int? rollingWindowOverlap = ResolveOptionalIntArgument(args, "--rolling-window-overlap=");
        int? rollingMinWindowSize = ResolveOptionalIntArgument(args, "--rolling-min-window-size=");
        int? rollingMaxWindowSize = ResolveOptionalIntArgument(args, "--rolling-max-window-size=");
        int? rollingMinWindowOverlap = ResolveOptionalIntArgument(args, "--rolling-min-window-overlap=");
        int? rollingMaxWindowOverlap = ResolveOptionalIntArgument(args, "--rolling-max-window-overlap=");
        int? rollingHotspotWindowCount = ResolveOptionalIntArgument(args, "--rolling-hotspot-window-count=");
        int? rollingHotspotNeighborPadding = ResolveOptionalIntArgument(args, "--rolling-hotspot-neighbor-padding=");
        int? rollingInitialAdmissionBatchSize = ResolveOptionalIntArgument(args, "--rolling-initial-admission-batch-size=");
        int? rollingAdmissionBatchSize = ResolveOptionalIntArgument(args, "--rolling-admission-batch-size=");
        int? rollingMaxAdmissionRetries = ResolveOptionalIntArgument(args, "--rolling-max-admission-retries=");
        int? rollingStationQuotaRestorationOverrideStageIndex = ResolveOptionalIntArgument(
            args, "--rolling-station-quota-restoration-stage-index=");
        double? rollingStationQuotaRestorationOverrideTimeLimitSeconds = ResolveOptionalDoubleArgument(
            args, "--rolling-station-quota-restoration-time-limit-seconds=");
        double rollingStationQuotaRestorationMaxTimeLimitSeconds = ResolveOptionalDoubleArgument(
            args, "--rolling-station-quota-restoration-max-time-seconds=") ?? 600.0;
        double rollingFinalMacroTimeLimitSeconds = ResolveOptionalDoubleArgument(
            args, "--rolling-final-macro-time-limit-seconds=") ?? 600.0;
        int? checkerLoopMaxIterations = ResolveOptionalIntArgument(args, "--checker-loop-max-iterations=");
        int? checkerLoopMaxNewCuts = ResolveOptionalIntArgument(args, "--checker-loop-max-new-cuts=");
        int? checkerLoopMaxHeuristicTrials = ResolveOptionalIntArgument(args, "--checker-loop-max-heuristic-trials=");
        int? checkerLoopExactBatchCap = ResolveOptionalIntArgument(args, "--checker-exact-batch-cap=") ??
            ResolveOptionalIntArgument(args, "--checker-loop-exact-batch-cap=");
        int checkerExactPerStationCap = ResolveOptionalIntArgument(args, "--checker-exact-per-station-cap=") ?? 4;
        int checkerExactPerPairCap = ResolveOptionalIntArgument(args, "--checker-exact-per-pair-cap=") ?? 2;
        int validityFirstHotspotMaximumRequirementCount = ResolveOptionalIntArgument(
            args,
            "--checker-validity-hotspot-max-requirements=") ?? 40;
        double validityFirstTotalRuntimeBudgetSeconds = ResolveOptionalDoubleArgument(
            args,
            "--checker-validity-total-runtime-budget-seconds=") ?? 3600.0;
        double validityFirstFinalValidationReserveSeconds = ResolveOptionalDoubleArgument(
            args,
            "--checker-validity-final-validation-reserve-seconds=") ?? 120.0;
        int validityFirstMaximumFeedbackOnlyRounds = ResolveOptionalIntArgument(
            args,
            "--checker-validity-max-feedback-only-rounds=") ?? 2;
        int validityFirstRecoveryMipFocus = ResolveOptionalIntArgument(
            args,
            "--checker-validity-recovery-mip-focus=") ?? 1;
        double validityFirstRecoveryMipGap = ResolveOptionalDoubleArgument(
            args,
            "--checker-validity-recovery-mip-gap=") ?? 0.05;
        double validityFirstIisTimeLimitSeconds = ResolveOptionalDoubleArgument(
            args,
            "--checker-validity-iis-time-limit-seconds=") ?? 10.0;
        double validityFirstIisCumulativeBudgetSeconds = ResolveOptionalDoubleArgument(
            args,
            "--checker-validity-iis-cumulative-budget-seconds=") ?? 60.0;
        double validityFirstLocalNeighborhoodTimeLimitSeconds = ResolveOptionalDoubleArgument(
            args,
            "--checker-validity-local-neighborhood-time-limit-seconds=") ?? 60.0;
        double validityFirstFixedAssignmentRetimingTimeLimitSeconds = ResolveOptionalDoubleArgument(
            args,
            "--checker-validity-fixed-assignment-retiming-time-limit-seconds=") ?? 120.0;
        double validityFirstConflictLinkedRetimingTimeLimitSeconds = ResolveOptionalDoubleArgument(
            args,
            "--checker-validity-conflict-linked-retiming-time-limit-seconds=") ?? 300.0;
        double validityFirstPreviouslyConsumedRuntimeSeconds = ResolveOptionalDoubleArgument(
            args,
            "--checker-validity-previously-consumed-runtime-seconds=") ?? 0.0;
        string? validityFirstReplayNeighborhoodInputPath = ResolveOptionalStringArgument(
            args,
            "--checker-validity-replay-neighborhood-input=");
        string? validityFirstReplayHotspotStationName = ResolveOptionalStringArgument(
            args,
            "--checker-validity-replay-hotspot-station=");
        RepairNeighborhoodSelectorKind repairNeighborhoodSelector =
            RepairNeighborhoodSelectorArgumentParser.Parse(ResolveOptionalStringArgument(
                args,
                "--repair-neighborhood-selector="));
        RepairActionSelectorKind repairActionSelector =
            RepairActionSelectorArgumentParser.Parse(ResolveOptionalStringArgument(
                args,
                "--repair-action-selector="));
        MacroCandidateCheckStrategy macroCandidateCheckStrategy =
            MacroCandidateCheckStrategyParser.Parse(ResolveOptionalStringArgument(
                args,
                "--checker-macro-candidate-strategy="));
        MicroCoordinationMode microCoordinationMode = MicroCoordinationModeParser.Parse(
            ResolveOptionalStringArgument(args, "--micro-coordination-mode="));
        bool diagnosticCommonMacroCheckpoint = args.Any(arg => string.Equals(
            arg, "--diagnostic-common-macro-checkpoint", StringComparison.OrdinalIgnoreCase));
        bool stopAfterFirstJointFeasible = args.Any(arg => string.Equals(
            arg,
            "--checker-stop-after-first-joint-feasible",
            StringComparison.OrdinalIgnoreCase));
        int validityFirstRecoveryHotspotMaximumRequirementCount = ResolveOptionalIntArgument(
            args, "--checker-validity-recovery-hotspot-max-requirements=") ?? 80;
        int validityFirstRecoveryHotspotMaximumAlternativeCount = ResolveOptionalIntArgument(
            args, "--checker-validity-recovery-hotspot-max-alternatives=") ?? 2000;
        int validityFirstRecoveryHotspotMaximumEstimatedVariableCount = ResolveOptionalIntArgument(
            args, "--checker-validity-recovery-hotspot-max-estimated-variables=") ?? 200000;
        int validityFirstRecoveryHotspotMaximumEstimatedConstraintCount = ResolveOptionalIntArgument(
            args, "--checker-validity-recovery-hotspot-max-estimated-constraints=") ?? 500000;
        int validityFirstRecoveryHotspotMaximumConflictRelationCount = ResolveOptionalIntArgument(
            args, "--checker-validity-recovery-hotspot-max-conflict-relations=") ?? 150000;
        int checkerIisMaxExpansionRounds = ResolveOptionalIntArgument(args, "--checker-iis-max-expansion-rounds=") ?? 5;
        int checkerIisTemporalExpansionStepSeconds = ResolveOptionalIntArgument(
            args, "--checker-iis-temporal-expansion-step-seconds=") ?? 300;
        int checkerIisMaxExtraTemporalSeconds = ResolveOptionalNonNegativeIntArgument(
            args, "--checker-iis-max-extra-temporal-seconds=") ?? 1800;
        int checkerLoopInitialCandidateCap = ResolveOptionalIntArgument(
            args, "--checker-loop-initial-candidate-cap=") ?? 20;
        int checkerLoopExpandedCandidateCap = ResolveOptionalIntArgument(
            args, "--checker-loop-expanded-candidate-cap=") ?? 40;
        double? checkerLoopTotalRuntimeBudgetSeconds = ResolveOptionalDoubleArgument(
            args, "--checker-loop-total-runtime-budget-seconds=");
        double? checkerLoopMaxInitialTotalRetimingSeconds = ResolveOptionalDoubleArgument(
            args, "--checker-loop-max-initial-total-retiming-seconds=");
        double? checkerLoopMaxInitialPointRetimingSeconds = ResolveOptionalDoubleArgument(
            args, "--checker-loop-max-initial-point-retiming-seconds=");
        string? checkerLoopInitialReferenceSnapshotInput = ResolveOptionalStringArgument(
            args, "--checker-loop-initial-reference-snapshot-input=");
        AdaptiveSingletonOptions adaptiveSingletonOptions = new(
            shouldUseAdaptiveSingletonConvergence,
            checkerLoopInitialCandidateCap,
            checkerLoopExpandedCandidateCap,
            checkerLoopTotalRuntimeBudgetSeconds,
            checkerLoopMaxInitialTotalRetimingSeconds,
            checkerLoopMaxInitialPointRetimingSeconds,
            checkerLoopInitialReferenceSnapshotInput);
        adaptiveSingletonOptions.Validate();
        int microPromotionPersistenceRounds = ResolveOptionalIntArgument(args, "--micro-promotion-persistence-rounds=") ?? 2;
        double macroRunningTimeSupplementRatio = NonNegativeDoubleArgumentParser.Parse(
            args, "--running-time-supplement-ratio=") ?? 0.05;
        _ = RunningTimeSupplementPolicy.Create(macroRunningTimeSupplementRatio);
        double repairRunningTimeSupplementRatio = CheckerRepairSupplementArgumentParser.Parse(
            args, "--checker-repair-running-time-supplement-ratio=");
        _ = StationQuotaRestorationBudgetPolicy.Resolve(
            rollingStationQuotaRestorationOverrideStageIndex ?? 1,
            defaultTimeLimitSeconds: rollingStationQuotaRestorationMaxTimeLimitSeconds,
            rollingStationQuotaRestorationOverrideStageIndex,
            rollingStationQuotaRestorationOverrideTimeLimitSeconds);
        if (microPromotionPersistenceRounds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(microPromotionPersistenceRounds));
        }
        int checkerLoopInitialNeighborhoodLevel = ResolveOptionalIntArgument(
            args, "--checker-loop-initial-neighborhood-level=") ?? 0;
        int checkerLoopEndpointOccupationSeconds = CheckerEndpointOccupationArgumentParser.Parse(
            args,
            "--checker-loop-endpoint-occupation-seconds=");
        string? checkerLoopRecourseWindowsRaw = ResolveOptionalStringArgument(args, "--checker-loop-recourse-windows-seconds=");
        string? checkerSecondaryTimeLimitRaw = ResolveOptionalStringArgument(args, "--checker-loop-secondary-time-limit-seconds=");
        double checkerSecondaryTimeLimitSeconds = checkerSecondaryTimeLimitRaw is null
            ? 45.0
            : CheckerPositiveSecondsArgumentParser.Parse(
                checkerSecondaryTimeLimitRaw,
                "--checker-loop-secondary-time-limit-seconds");
        IReadOnlyList<int> checkerLoopRecourseWindows = checkerLoopRecourseWindowsRaw is null
            ? TemporalRecourseWindowPolicy.Create([300, 600, 1200, 1800]).WindowSeconds
            : CheckerZeroConflictArgumentParser.ParseWindowSeconds(
                checkerLoopRecourseWindowsRaw,
                "--checker-loop-recourse-windows-seconds");
        string? checkerFocusStation = ResolveOptionalStringArgument(args, "--checker-focus-station=");
        string? masterSnapshotInput = ResolveOptionalStringArgument(args, "--master-snapshot-input=");
        string? adaptiveArtifactsOutput = ResolveOptionalStringArgument(
            args, "--checker-adaptive-artifacts-output=");
        int? adaptiveExpectedInitialRootConflicts = ResolveOptionalNonNegativeIntArgument(
            args, "--checker-adaptive-expected-initial-root-conflicts=");
        CheckerResumeInput checkerResumeInput = CheckerResumeInputResolver.Resolve(
            ResolveOptionalStringArgument(args, "--checker-loop-import-artifacts="),
            ResolveOptionalStringArgument(args, "--checker-loop-checkpoint-input="),
            ResolveOptionalStringArgument(args, "--checker-loop-checkpoint-output="));
        StructuralRepairNeighborhoodEscalationOptions neighborhoodEscalationOptions =
            CheckerNeighborhoodEscalationCliResolver.Resolve(
                shouldUseNeighborhoodEscalation,
                ResolveOptionalStringArgument(args, "--checker-loop-neighborhood-escalation-paddings="),
                ResolveOptionalStringArgument(args, "--checker-loop-neighborhood-escalation-windows-seconds="),
                checkerLoopInitialNeighborhoodLevel,
                basePadding: 2,
                hasCheckpointResume: checkerResumeInput.Mode != CheckerResumeInputMode.None);
        _ = IisGuidedRepairOptions.Create(
            shouldUseIisGuidedRepair,
            checkerIisMaxExpansionRounds,
            checkerIisTemporalExpansionStepSeconds,
            checkerIisMaxExtraTemporalSeconds);
        if (experimentModeRequest.Mode == ExperimentMode.ControlledCandidateRepair &&
            Math.Abs(repairRunningTimeSupplementRatio - 0.05) > 1e-12)
        {
            throw new ArgumentException(
                "ControlledCandidateRepair requires --checker-repair-running-time-supplement-ratio=0.05.");
        }
        if (experimentModeRequest.Mode == ExperimentMode.ControlledCandidateRepair &&
            checkerIisMaxExpansionRounds > 5)
        {
            throw new ArgumentOutOfRangeException(
                nameof(checkerIisMaxExpansionRounds),
                "ControlledCandidateRepair allows at most five IIS dependency expansion rounds.");
        }

        Console.WriteLine($"Dataset path: {datasetPath}");
        Console.WriteLine($"Solve enabled: {shouldSolve}");
        Console.WriteLine($"Rolling enabled: {shouldUseRolling}");
        Console.WriteLine($"Adaptive rolling enabled: {shouldUseAdaptiveRolling}");
        Console.WriteLine($"Checker loop enabled: {shouldUseCheckerLoop}");
        if (shouldUseCheckerLoop)
        {
            Console.WriteLine($"Macro candidate strategy: {macroCandidateCheckStrategy}");
        }
        if (timeLimitSeconds.HasValue)
        {
            Console.WriteLine($"Time limit seconds: {timeLimitSeconds.Value}");
        }
        if (neighborRadius.HasValue)
        {
            Console.WriteLine(shouldUseFullMacroRelations
                ? "Macro relation mode: complete initial section relation set"
                : $"Neighbor radius: {neighborRadius.Value}");
        }
        if (freshBootstrapPlan is not null)
        {
            Console.WriteLine(
                $"Fresh Macro bootstrap: rolling_radius={freshBootstrapPlan.RollingNeighborRadius}, " +
                $"complete_relations=True, complete_time_limit_seconds={freshBootstrapPlan.FullMacroTimeLimitSeconds:F0}.");
        }

        try
        {
            NormalizedDatasetLoader loader = new();
            TimetablingInstance instance = loader.Load(datasetPath);

            Console.WriteLine($"Loaded instance '{instance.Meta.InstanceName}'.");
            Console.WriteLine($"Trains: {instance.Trains.Count}, Stops: {instance.TrainStops.Count}, Sections: {instance.Sections.Count}.");
            if (shouldUseFullMacroRelations)
            {
                int actualFullRelationCount = new InitialConflictGraphBuilder(instance, int.MaxValue).Build().Count;
                Console.WriteLine($"Initial complete Macro section relation count: {actualFullRelationCount}");
                if (expectedFullMacroRelationCount.HasValue &&
                    actualFullRelationCount != expectedFullMacroRelationCount.Value)
                {
                    throw new InvalidDataException(
                        $"Complete Macro relation count mismatch: expected={expectedFullMacroRelationCount.Value}, actual={actualFullRelationCount}.");
                }
            }

            bool macroMicroStopRequested = false;
            ConsoleCancelEventHandler? macroMicroCancelHandler = null;
            if (macroMicroExecution.IsRequested)
            {
                macroMicroCancelHandler = (_, eventArgs) =>
                {
                    eventArgs.Cancel = true;
                    macroMicroStopRequested = true;
                    Console.WriteLine(
                        "Macro-Micro stop requested. The current bounded solve will return, then the next safe checkpoint will be saved.");
                };
                Console.CancelKeyPress += macroMicroCancelHandler;
            }

            if (macroMicroExecution.Mode == MacroMicroExecutionMode.Resume)
            {
                try
                {
                    return MacroMicroFeedbackExecutionRunner.Resume(
                        instance,
                        datasetPath,
                        macroMicroExecution,
                        () => macroMicroStopRequested);
                }
                finally
                {
                    if (macroMicroCancelHandler is not null)
                        Console.CancelKeyPress -= macroMicroCancelHandler;
                }
            }

            if (macroMicroExecution.Mode == MacroMicroExecutionMode.Fresh)
            {
                string automaticCheckpoint = MacroMicroFeedbackCheckpointStore.DefaultPath(
                    macroMicroExecution.OutputDirectory!);
                if (File.Exists(automaticCheckpoint))
                {
                    Console.WriteLine($"Existing Macro-Micro checkpoint detected; resuming: {automaticCheckpoint}");
                    try
                    {
                        return MacroMicroFeedbackExecutionRunner.Resume(
                            instance,
                            datasetPath,
                            macroMicroExecution with
                            {
                                Mode = MacroMicroExecutionMode.Resume,
                                CheckpointPath = automaticCheckpoint
                            },
                            () => macroMicroStopRequested);
                    }
                    finally
                    {
                        if (macroMicroCancelHandler is not null)
                            Console.CancelKeyPress -= macroMicroCancelHandler;
                    }
                }
            }

            if (shouldRunMicroStructureSuite)
            {
                return MicroStructureExperimentRunner.Run(instance, new MicroStructureSuiteRequest(
                    Path.GetFullPath(microStructureSnapshot!),
                    Path.GetFullPath(microStructureCheckpoint!),
                    Path.GetFullPath(microStructureStationSummary!),
                    Path.GetFullPath(microStructureOutput!),
                    microStructureRunFresh,
                    microStructureArmBudgetSeconds,
                    microStructureFreshBudgetSeconds,
                    checkerLoopEndpointOccupationSeconds,
                    macroRunningTimeSupplementRatio));
            }

            if (shouldRunMicroPatternTimeDomain)
            {
                return MicroPatternTimeDomainRunner.Run(instance, new(
                    Path.GetFullPath(microPatternBaseline!),
                    Path.GetFullPath(microPatternOutput!),
                    Path.GetFullPath(datasetPath),
                    TotalBudgetSeconds: microPatternBudgetSeconds,
                    MilestoneSeconds: microPatternMilestoneSeconds,
                    SaveReserveSeconds: 120,
                    PreviouslyConsumedSeconds: microPatternPreviouslyConsumedSeconds,
                    EndpointOccupationSeconds: checkerLoopEndpointOccupationSeconds,
                    RunningTimeSupplementRatio: macroRunningTimeSupplementRatio));
            }

            if (shouldRunDynamicHotspotIntegrated)
            {
                return DynamicHotspotIntegratedRunner.Run(instance, new DynamicHotspotRunRequest(
                    Path.GetFullPath(dynamicHotspotBaseline!),
                    Path.GetFullPath(dynamicHotspotOutput!),
                    dynamicHotspotProbeSeconds,
                    ProofPath: string.IsNullOrWhiteSpace(dynamicHotspotProof)
                        ? null
                        : Path.GetFullPath(dynamicHotspotProof)));
            }

            if (shouldRunMacroMicroFeedbackLoop)
            {
                return MacroMicroFeedbackLoopRunner.Run(
                    instance,
                    new DynamicHotspotRunRequest(
                        Path.GetFullPath(macroMicroFeedbackBaseline!),
                        Path.GetFullPath(macroMicroFeedbackOutput!),
                        macroMicroFeedbackBudgetSeconds,
                        ProofPath: string.IsNullOrWhiteSpace(macroMicroFeedbackProof)
                            ? null
                            : Path.GetFullPath(macroMicroFeedbackProof)),
                    macroMicroFeedbackIterationSeconds);
            }

            if (shouldResumeDirectMacroFeedback)
            {
                return MacroMicroFeedbackLoopRunner.ResumeDirectFeedback(
                    instance,
                    Path.GetFullPath(directFeedbackSnapshot!),
                    string.IsNullOrWhiteSpace(directFeedbackEvidence)
                        ? null
                        : Path.GetFullPath(directFeedbackEvidence),
                    MacroMicroFeedbackLoopRunner.ParseDirectResumeRetainedFeedbackPaths(
                            directFeedbackRetainedEvidence)
                        .Select(Path.GetFullPath)
                        .ToArray(),
                    MacroMicroFeedbackLoopRunner.ParseDirectResumeRetainedFeedbackPaths(
                            directFeedbackRunTabu)
                        .Select(Path.GetFullPath)
                        .ToArray(),
                    string.IsNullOrWhiteSpace(directFeedbackConflicts)
                        ? null
                        : Path.GetFullPath(directFeedbackConflicts),
                    Path.GetFullPath(directFeedbackOutput!),
                    directFeedbackBudgetSeconds,
                    directFeedbackAttemptSeconds,
                    directFeedbackMaximumAttempts,
                    macroRunningTimeSupplementRatio,
                    checkerLoopEndpointOccupationSeconds);
            }

            if (shouldRunMicroExtendability)
            {
                return MicroExtendabilityRepairRunner.Run(instance, new(
                    Path.GetFullPath(microExtendabilityBaseline!),
                    Path.GetFullPath(microExtendabilityOutput!),
                    Path.GetFullPath(datasetPath),
                    HistoricalFeedbackPath: string.IsNullOrWhiteSpace(microExtendabilityFeedback)
                        ? null
                        : Path.GetFullPath(microExtendabilityFeedback),
                    PilotBudgetSeconds: microExtendabilityPilotSeconds,
                    FormalBudgetSeconds: microExtendabilityFormalSeconds,
                    EndpointOccupationSeconds: checkerLoopEndpointOccupationSeconds,
                    RunningTimeSupplementRatio: macroRunningTimeSupplementRatio));
            }

            if (shouldRunProofBindingRecovery)
            {
                return MicroStructureProofBindingRecoveryRunner.Run(instance, new(
                    Path.GetFullPath(proofBindingDiagnostic!),
                    Path.GetFullPath(proofBindingSummary!),
                    Path.GetFullPath(proofBindingOutput!),
                    ContinuousDiagnosticLimitSeconds: 120,
                    TargetedMacroRepairLimitSeconds: 60,
                    FreshBudgetSeconds: 3600,
                    EndpointOccupationSeconds: checkerLoopEndpointOccupationSeconds,
                    RunningTimeSupplementRatio: macroRunningTimeSupplementRatio));
            }

            if (!string.IsNullOrWhiteSpace(proofBindingResumeOutput))
            {
                return MicroStructureProofBindingRecoveryRunner.ResumeAfterSavedDiagnostic(
                    instance,
                    Path.GetFullPath(proofBindingResumeOutput),
                    targetedMacroRepairLimitSeconds: 60,
                    endpointOccupationSeconds: checkerLoopEndpointOccupationSeconds,
                    runningTimeSupplementRatio: macroRunningTimeSupplementRatio);
            }

            if (!string.IsNullOrWhiteSpace(proofBindingRefreshOutput))
            {
                return MicroStructureProofBindingRecoveryRunner.RefreshSavedReport(
                    instance, Path.GetFullPath(proofBindingRefreshOutput));
            }

            if (!string.IsNullOrWhiteSpace(microStructureRecoveryDiagnostic))
            {
                return MicroStructureExperimentRunner.ResumeFreshClosedLoop(
                    instance,
                    Path.GetFullPath(microStructureRecoveryDiagnostic),
                    microStructureRecoveryBudgetSeconds,
                    checkerLoopEndpointOccupationSeconds,
                    macroRunningTimeSupplementRatio);
            }

            if (!string.IsNullOrWhiteSpace(jointFeasibleBaselineCheckpoint))
            {
                JointFeasibleBaselineVerificationResult baseline = JointFeasibleBaselineVerifier.Run(
                    instance,
                    repositoryRoot,
                    jointFeasibleBaselineCheckpoint,
                    jointFeasibleBaselineOutput!);
                Console.WriteLine($"Round-12 baseline passed: {baseline.Passed}");
                Console.WriteLine($"Candidate fingerprint: {baseline.CandidateFingerprint}");
                Console.WriteLine($"Macro violations: {baseline.MacroViolationCount}");
                Console.WriteLine($"Global Micro conflicts/Unknown: {baseline.GlobalMicroConflictCount}/{baseline.GlobalMicroUnknownCount}");
                Console.WriteLine($"Saved assignments validated: {baseline.ValidatedSavedAssignmentCount}/{baseline.SavedAssignmentCount}");
                Console.WriteLine($"Baseline output: {baseline.OutputDirectory}");
                return baseline.Passed ? 0 : 2;
            }

            string artifactsDirectory = macroMicroExecution.Mode == MacroMicroExecutionMode.Fresh
                ? Path.GetFullPath(macroMicroExecution.OutputDirectory!)
                : string.IsNullOrWhiteSpace(artifactsOutput)
                    ? Path.Combine(repositoryRoot, "artifacts", instance.Meta.InstanceName)
                    : Path.GetFullPath(artifactsOutput);
            Directory.CreateDirectory(artifactsDirectory);
            IReadOnlyList<TrainSectionStopQuotaConstruction> sectionStopConstructions =
                TrainSectionStopQuotaConstructionReporter.Write(
                    instance,
                    Path.Combine(artifactsDirectory, "section_stop_rule_construction.csv"));
            Console.WriteLine(
                $"Section-stop rules mapped: {sectionStopConstructions.Count}; construction table written to: " +
                Path.Combine(artifactsDirectory, "section_stop_rule_construction.csv"));
            StopModePreprocessingResult stopModePreprocessing = StopModePreprocessor.Analyze(instance);
            StopModePreprocessingReporter.Write(stopModePreprocessing, artifactsDirectory);
            Console.WriteLine($"Stop-mode preprocessing: fixed={stopModePreprocessing.FixedStopValuesByStopId.Count}, contradictions={stopModePreprocessing.Contradictions.Count}.");

            if (shouldBuildFullMacroRelationsOnly)
            {
                FullMacroRelationBuildOnlyReport buildOnly = FullMacroRelationBuildOnlyRunner.Run(
                    instance,
                    artifactsDirectory,
                    macroRunningTimeSupplementRatio,
                    checkerLoopEndpointOccupationSeconds);
                Console.WriteLine($"Full Macro relation build-only count: {buildOnly.TotalRelationCount}");
                Console.WriteLine(
                    $"Build-only model: rows={buildOnly.ConstraintCount}, variables={buildOnly.VariableCount}, " +
                    $"binaries={buildOnly.BinaryVariableCount}, nonzeros={buildOnly.NonzeroCount}, " +
                    $"status={buildOnly.ModelStatus}, Optimize calls={buildOnly.OptimizeCallCount}.");
                return 0;
            }

            if (experimentModeRequest.Mode == ExperimentMode.ControlledCandidateRepair)
            {
                flowPermissions.EnsureStructuralRepairAllowed();
                string snapshotPath = Path.GetFullPath(experimentModeRequest.MasterSnapshotPath!);
                MasterSolutionSnapshot snapshot = MasterSnapshotInputLoader.Load(
                    snapshotPath,
                    instance.Meta.InstanceName,
                    instance.Trains.Select(train => train.TrainIndex).ToHashSet());
                CheckerLoopOptions controlledOptions = CheckerLoopOptionsComposer.Compose(
                    maxIterations: 1,
                    maxNewCutsPerIteration: 1,
                    timeLimitSecondsPerSolve: 40.0,
                    focusStationName: null,
                    maxHeuristicTrialsPerIteration: 1,
                    zeroConflictMode: true,
                    recourseWindowSeconds: checkerLoopRecourseWindows,
                    exactBatchCap: 1,
                    endpointOccupationSeconds: checkerLoopEndpointOccupationSeconds,
                    enableLocalMicroRepair: false,
                    secondaryTimeLimitSecondsPerSolve: 40.0,
                    neighborhoodEscalationOptions: neighborhoodEscalationOptions,
                    repairRunningTimeSupplementRatio: repairRunningTimeSupplementRatio,
                    exactPerStationCap: 1,
                    exactPerPairCap: 1,
                    enableMicroPromotion: false,
                    microPromotionPersistenceRounds: microPromotionPersistenceRounds,
                    enableIisGuidedRepair: shouldUseIisGuidedRepair,
                    iisMaxExpansionRounds: checkerIisMaxExpansionRounds,
                    iisTemporalExpansionStepSeconds: checkerIisTemporalExpansionStepSeconds,
                    iisMaxExtraTemporalSeconds: checkerIisMaxExtraTemporalSeconds);
                string controlledArtifactsDirectory = Path.Combine(
                    artifactsDirectory,
                    "controlled_candidate_repair");
                Directory.CreateDirectory(controlledArtifactsDirectory);
                MasterCheckerLoopSolver controlledSolver = new(
                    instance,
                    neighborRadius,
                    controlledOptions,
                    initialSeed: snapshot);
                using ControlledCandidateRepairResult controlledResult =
                    controlledSolver.RunControlledCandidateRepair(
                        experimentModeRequest.StructuralCandidateId!);
                ControlledCandidateRepairReporter.Write(
                    controlledResult,
                    controlledArtifactsDirectory);
                Console.WriteLine($"Controlled candidate: {controlledResult.CandidateId}");
                Console.WriteLine($"Controlled snapshot: {snapshotPath}");
                Console.WriteLine($"Controlled initial outcome: {controlledResult.InitialOutcome}");
                Console.WriteLine($"Controlled termination: {controlledResult.TerminationReason}");
                Console.WriteLine($"Controlled diagnostics written to: {controlledArtifactsDirectory}");
                return 0;
            }

            if (experimentModeRequest.Mode == ExperimentMode.MicroCheckOnly)
            {
                string snapshotPath = Path.GetFullPath(experimentModeRequest.MasterSnapshotPath!);
                MasterSolutionSnapshot snapshot = MasterSnapshotInputLoader.Load(
                    snapshotPath,
                    instance.Meta.InstanceName,
                    instance.Trains.Select(train => train.TrainIndex).ToHashSet());
                string microCheckOnlyArtifactsDirectory = Path.Combine(
                    artifactsDirectory,
                    "micro_check_only");
                MasterMicroFeasibilityChecker microChecker =
                    CheckerMicroValidationFactory.CreateChecker(checkerLoopEndpointOccupationSeconds);
                MicroCheckOnlyRunner runner = new(
                    carrierFactory: () => new ValidatedSnapshotCarrierBuilder().Build(
                        instance,
                        neighborRadius,
                        snapshot,
                        repairSupplementRatio: 0.05,
                        endpointOccupationSeconds: checkerLoopEndpointOccupationSeconds),
                    microCheck: microChecker.Validate,
                    detailedReportWriter: microChecker.WriteReports);
                MicroCheckOnlyResult result = runner.Run(microCheckOnlyArtifactsDirectory);
                Console.WriteLine($"Micro-check-only snapshot: {snapshotPath}");
                Console.WriteLine($"Micro-check-only fingerprint: {result.SnapshotFingerprint}");
                Console.WriteLine($"Micro C0 status: {result.Report.Status}");
                Console.WriteLine($"Micro C0 conflicts: {result.C0.RawConflictCount}");
                Console.WriteLine(
                    $"Mode B deltas: edges={result.StructuralEdgeDelta}, " +
                    $"cuts={result.CutDelta}, promotions={result.PromotionDelta}");
                return 0;
            }

            if (shouldUseRolling)
            {
                if (checkerResumeInput.Mode != CheckerResumeInputMode.None)
                {
                    throw new ArgumentException("Checker checkpoint resume cannot be combined with a new Rolling solve.");
                }
                RollingHorizonOptions options = new(
                    rollingWindowSize ?? Math.Min(25, Math.Max(8, instance.Trains.Count / 4)),
                    rollingWindowOverlap ?? Math.Min(5, Math.Max(1, instance.Trains.Count / 20)),
                    seedTimeLimitSeconds ?? Math.Min(timeLimitSeconds ?? 30.0, 30.0),
                    windowTimeLimitSeconds ?? Math.Min(timeLimitSeconds ?? 30.0, 30.0),
                    shouldUseAdaptiveRolling,
                    adaptiveScoreBudget ?? 0.0,
                    rollingMinWindowSize ?? 8,
                    rollingMaxWindowSize ?? 45,
                    rollingMinWindowOverlap ?? 2,
                    rollingMaxWindowOverlap ?? 12,
                    rollingMinWindowTime ?? 5.0,
                    rollingMaxWindowTime ?? Math.Min(timeLimitSeconds ?? 30.0, 30.0),
                    true,
                    rollingHotspotWindowCount ?? 3,
                    rollingHotspotNeighborPadding ?? 2,
                    rollingInitialAdmissionBatchSize ?? 8,
                    rollingAdmissionBatchSize ?? 8,
                    rollingMaxAdmissionRetries ?? 2,
                    RunningTimeSupplementRatio: macroRunningTimeSupplementRatio,
                    StationQuotaRestorationOverrideStageIndex: rollingStationQuotaRestorationOverrideStageIndex,
                    StationQuotaRestorationOverrideTimeLimitSeconds: rollingStationQuotaRestorationOverrideTimeLimitSeconds,
                    StationQuotaRestorationMaxTimeLimitSeconds: rollingStationQuotaRestorationMaxTimeLimitSeconds,
                    FinalMacroTimeLimitSeconds: rollingFinalMacroTimeLimitSeconds);

                Console.WriteLine($"Rolling window size: {options.WindowSize}");
                Console.WriteLine($"Rolling window overlap: {options.WindowOverlap}");
                Console.WriteLine($"Rolling seed time limit seconds: {options.SeedTimeLimitSeconds}");
                Console.WriteLine($"Rolling window time limit seconds: {options.WindowTimeLimitSeconds}");
                Console.WriteLine($"Rolling adaptive mode: {options.UseAdaptiveWindows}");
                Console.WriteLine($"Station-quota restoration maximum time limit seconds: {options.StationQuotaRestorationMaxTimeLimitSeconds}");
                Console.WriteLine(
                    macroMicroExecution.Mode == MacroMicroExecutionMode.Fresh
                        ? $"Final radius-{rollingNeighborRadius} rolling refinement time limit seconds: {options.FinalMacroTimeLimitSeconds}"
                        : $"Final full-Macro time limit seconds: {options.FinalMacroTimeLimitSeconds}");

                string rollingArtifactsDirectory = Path.Combine(artifactsDirectory, "rolling");
                Directory.CreateDirectory(rollingArtifactsDirectory);

                RollingHorizonMasterSolver rollingSolver = new(
                    instance,
                    rollingNeighborRadius,
                    options,
                    rollingArtifactsDirectory);
                using RollingHorizonResult rollingResult = rollingSolver.Solve();
                BasicMilpModel rollingModel = rollingResult.FinalModel;

                string lpPath = Path.Combine(rollingArtifactsDirectory, "rolling_final_model.lp");
                rollingModel.Write(lpPath);
                Console.WriteLine($"Rolling final LP model written to: {lpPath}");

                RollingHorizonMasterSolver.WriteProgressReport(rollingResult, rollingArtifactsDirectory);
                Console.WriteLine($"Rolling progress written to: {Path.Combine(rollingArtifactsDirectory, "rolling_progress.md")}");
                MasterSolutionSnapshot rollingSnapshot = MasterSolutionSnapshot.Capture(rollingModel);
                string rollingSnapshotPath = Path.Combine(rollingArtifactsDirectory, "master_snapshot.json");
                MasterSolutionSnapshotStore.Save(rollingSnapshotPath, instance.Meta.InstanceName, rollingSnapshot);
                Console.WriteLine($"Rolling Master snapshot written to: {rollingSnapshotPath}");

                if (macroMicroExecution.Mode == MacroMicroExecutionMode.Fresh)
                {
                    MacroBaselineSummary rollingSummary = rollingResult.BaselineSummary
                        ?? throw new InvalidDataException("Radius rolling solve did not produce its baseline summary.");
                    MasterValidationReport rollingValidation = rollingResult.FinalValidation
                        ?? throw new InvalidDataException("Radius rolling solve did not preserve its validation report.");
                    string rollingValidationPath = Path.Combine(
                        rollingArtifactsDirectory,
                        "rolling_seed_validation.md");
                    new MasterSolutionValidator().WriteReport(rollingValidation, rollingValidationPath);
                    MacroMicroFreshSeedAcceptance seedAcceptance =
                        MacroMicroFreshSeedAcceptancePolicy.Evaluate(rollingSummary, rollingValidation);
                    string seedAcceptancePath = Path.Combine(
                        rollingArtifactsDirectory,
                        "rolling_seed_acceptance.json");
                    File.WriteAllText(
                        seedAcceptancePath,
                        System.Text.Json.JsonSerializer.Serialize(
                            seedAcceptance,
                            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                    Console.WriteLine(
                        $"Radius rolling seed gate: accepted={seedAcceptance.IsAccepted}, " +
                        $"unmodeled_complete_relation_violations={seedAcceptance.UnmodeledRelationViolationCount}, " +
                        $"modeled_relation_violations={seedAcceptance.ModeledRelationViolationCount}, " +
                        $"other_business_violations={seedAcceptance.OtherBusinessViolationCount}, " +
                        $"reason={seedAcceptance.Reason}.");
                    if (!rollingModel.HasIncumbentSolution || !seedAcceptance.IsAccepted)
                    {
                        throw new InvalidDataException(
                            "Fresh Macro-Micro execution rejected the saved radius rolling seed because it " +
                            "was incomplete or violated a modeled/business constraint; the complete Macro solve was not started.");
                    }

                    MacroMicroFreshBootstrapPlan plan = freshBootstrapPlan
                        ?? throw new InvalidOperationException("Fresh Macro bootstrap plan is missing.");
                    FreshCompleteMacroSolveResult completeMacro = FreshCompleteMacroSolver.Solve(
                        instance,
                        rollingSnapshot,
                        Path.Combine(artifactsDirectory, "complete_macro"),
                        macroRunningTimeSupplementRatio,
                        checkerLoopEndpointOccupationSeconds,
                        plan.FullMacroTimeLimitSeconds,
                        expectedFullMacroRelationCount,
                        rollingValidation.Violations);
                    try
                    {
                        return MacroMicroFeedbackExecutionRunner.StartFromFreshMacro(
                            instance,
                            datasetPath,
                            macroMicroExecution.OutputDirectory!,
                            completeMacro.SnapshotPath,
                            processRuntimeClock.Elapsed.TotalSeconds,
                            macroRunningTimeSupplementRatio,
                            checkerLoopEndpointOccupationSeconds,
                            macroMicroExecution,
                            () => macroMicroStopRequested);
                    }
                    finally
                    {
                        if (macroMicroCancelHandler is not null)
                            Console.CancelKeyPress -= macroMicroCancelHandler;
                    }
                }

                if (shouldUseCheckerLoop)
                {
                    CheckerLoopOptions checkerLoopOptions = new(
                        checkerLoopMaxIterations ?? 3,
                        checkerLoopMaxNewCuts ?? 20,
                        timeLimitSeconds ?? 20.0,
                        FocusStationName: checkerFocusStation,
                        MaxHeuristicTrialsPerIteration: checkerLoopMaxHeuristicTrials ?? 3,
                        ZeroConflictMode: shouldUseZeroConflictChecker,
                        ConfiguredRecourseWindowSeconds: checkerLoopRecourseWindows,
                        ExactBatchCap: checkerLoopExactBatchCap ?? 20,
                        ExactPerStationCap: checkerExactPerStationCap,
                        ExactPerPairCap: checkerExactPerPairCap,
                        EndpointOccupationSeconds: checkerLoopEndpointOccupationSeconds,
                        RepairRunningTimeSupplementRatio: repairRunningTimeSupplementRatio,
                        EnableLocalMicroRepair: shouldUseLocalMicroRepair,
                        EnableMicroPromotion: !disableMicroPromotion,
                        MicroPromotionPersistenceRounds: microPromotionPersistenceRounds,
                        SecondaryTimeLimitSecondsPerSolve: checkerSecondaryTimeLimitSeconds,
                        EnableNeighborhoodEscalation: neighborhoodEscalationOptions.Enabled,
                        NeighborhoodEscalationPaddings: neighborhoodEscalationOptions.EscalationPaddings,
                        NeighborhoodEscalationWindowSeconds: neighborhoodEscalationOptions.EscalationWindowSeconds,
                        InitialNeighborhoodLevel: neighborhoodEscalationOptions.InitialLevel,
                        EnableIisGuidedRepair: shouldUseIisGuidedRepair,
                        IisMaxExpansionRounds: checkerIisMaxExpansionRounds,
                        IisTemporalExpansionStepSeconds: checkerIisTemporalExpansionStepSeconds,
                        IisMaxExtraTemporalSeconds: checkerIisMaxExtraTemporalSeconds);
                    string checkerLoopArtifactsDirectory = Path.Combine(artifactsDirectory, "checker_loop");
                    Directory.CreateDirectory(checkerLoopArtifactsDirectory);

                    MasterCheckerLoopSolver checkerLoopSolver = new(instance, neighborRadius, checkerLoopOptions, rollingSnapshot);
                    using CheckerLoopResult checkerLoopResult = checkerLoopSolver.Solve();
                    MasterCheckerLoopSolver.WriteProgressReport(checkerLoopResult, checkerLoopArtifactsDirectory);
                    Console.WriteLine($"Checker loop termination: {checkerLoopResult.TerminationReason}");
                    Console.WriteLine($"Checker loop injected cut count: {checkerLoopResult.ActiveCuts.Count}");
                    if (checkerLoopResult.FinalModel.HasIncumbentSolution)
                    {
                        ExportAndAnalyzeIncumbent(
                            checkerLoopResult.FinalModel,
                            checkerLoopArtifactsDirectory,
                            checkerLoopEndpointOccupationSeconds);
                    }

                    return CheckerLoopProcessExitCodePolicy.Resolve(
                        checkerLoopResult.CoordinationStrategy,
                        checkerLoopResult.TerminationReason,
                        checkerLoopResult.BestJointFeasibleSolution is not null);
                }

                ExportAndAnalyzeIncumbent(rollingModel, rollingArtifactsDirectory, includeMicroDiagnostics: false);
                return rollingResult.BaselineSummary?.IsAccepted == true ? 0 : 1;
            }

            try
            {
                if (shouldUseCheckerLoop)
                {
                    flowPermissions.EnsureStructuralRepairAllowed();
                    MasterSolutionSnapshot? checkerSeed = null;
                    CheckerCheckpoint? checkerCheckpoint = null;
                    CheckerLoopOptions checkerLoopOptions = CheckerLoopOptionsComposer.Compose(
                        experimentModeRequest.CheckerMaxIterations!.Value,
                        checkerLoopMaxNewCuts ?? 20,
                        timeLimitSeconds ?? 20.0,
                        checkerFocusStation,
                        checkerLoopMaxHeuristicTrials ?? 3,
                        shouldUseZeroConflictChecker,
                        checkerLoopRecourseWindows,
                        checkerLoopExactBatchCap ?? 20,
                        checkerLoopEndpointOccupationSeconds,
                        shouldUseLocalMicroRepair,
                        checkerSecondaryTimeLimitSeconds,
                        neighborhoodEscalationOptions,
                        repairRunningTimeSupplementRatio,
                        checkerExactPerStationCap,
                        checkerExactPerPairCap,
                        !disableMicroPromotion,
                        microPromotionPersistenceRounds,
                        shouldUseIisGuidedRepair,
                        checkerIisMaxExpansionRounds,
                        checkerIisTemporalExpansionStepSeconds,
                        checkerIisMaxExtraTemporalSeconds,
                        fixedSnapshotMultiIterationMode:
                            experimentModeRequest.Mode is
                                ExperimentMode.FixedSnapshotMultiIterationChecker or
                                ExperimentMode.ValidityFirstBendersChecker,
                        enableFeasibleSingletonGlobalEvaluation: shouldEvaluateFeasibleSingletons,
                        adaptiveSingletonOptions: adaptiveSingletonOptions,
                        coordinationStrategy: experimentModeRequest.CoordinationStrategy,
                        validityFirstHotspotMaximumRequirementCount:
                            validityFirstHotspotMaximumRequirementCount,
                        validityFirstTotalRuntimeBudgetSeconds:
                            validityFirstTotalRuntimeBudgetSeconds,
                        validityFirstFinalValidationReserveSeconds:
                            validityFirstFinalValidationReserveSeconds,
                        validityFirstMaximumFeedbackOnlyRounds:
                            validityFirstMaximumFeedbackOnlyRounds,
                        validityFirstRecoveryMipFocus:
                            validityFirstRecoveryMipFocus,
                        validityFirstRecoveryMipGap:
                            validityFirstRecoveryMipGap,
                        enablePhasedFeasibilityRecovery:
                            enablePhasedFeasibilityRecovery,
                        validityFirstRecoveryHotspotMaximumRequirementCount:
                            validityFirstRecoveryHotspotMaximumRequirementCount,
                        validityFirstRecoveryHotspotMaximumAlternativeCount:
                            validityFirstRecoveryHotspotMaximumAlternativeCount,
                        validityFirstRecoveryHotspotMaximumEstimatedVariableCount:
                            validityFirstRecoveryHotspotMaximumEstimatedVariableCount,
                        validityFirstRecoveryHotspotMaximumEstimatedConstraintCount:
                            validityFirstRecoveryHotspotMaximumEstimatedConstraintCount,
                        validityFirstRecoveryHotspotMaximumConflictRelationCount:
                            validityFirstRecoveryHotspotMaximumConflictRelationCount,
                        validityFirstIisTimeLimitSeconds:
                            validityFirstIisTimeLimitSeconds,
                        validityFirstIisCumulativeBudgetSeconds:
                            validityFirstIisCumulativeBudgetSeconds,
                        validityFirstLocalNeighborhoodTimeLimitSeconds:
                            validityFirstLocalNeighborhoodTimeLimitSeconds,
                        validityFirstFixedAssignmentRetimingTimeLimitSeconds:
                            validityFirstFixedAssignmentRetimingTimeLimitSeconds,
                        validityFirstConflictLinkedRetimingTimeLimitSeconds:
                            validityFirstConflictLinkedRetimingTimeLimitSeconds,
                        validityFirstPreviouslyConsumedRuntimeSeconds:
                            validityFirstPreviouslyConsumedRuntimeSeconds,
                        validityFirstReplayNeighborhoodInputPath:
                            validityFirstReplayNeighborhoodInputPath is null
                                ? null
                                : Path.GetFullPath(validityFirstReplayNeighborhoodInputPath),
                        validityFirstReplayHotspotStationName:
                            validityFirstReplayHotspotStationName,
                        repairNeighborhoodSelector:
                            repairNeighborhoodSelector,
                        repairActionSelector:
                            repairActionSelector,
                        stopAfterFirstJointFeasible:
                            stopAfterFirstJointFeasible,
                        macroCandidateCheckStrategy:
                            macroCandidateCheckStrategy,
                        microCoordinationMode:
                            microCoordinationMode);
                    if (checkerResumeInput.Mode != CheckerResumeInputMode.None &&
                        !string.IsNullOrWhiteSpace(masterSnapshotInput))
                    {
                        throw new ArgumentException(
                            "Checker checkpoint resume and --master-snapshot-input cannot be supplied together.");
                    }
                    if (checkerResumeInput.Mode == CheckerResumeInputMode.ImportArtifacts)
                    {
                        string importDirectory = Path.GetFullPath(checkerResumeInput.ImportArtifactsDirectory!);
                        checkerCheckpoint = CheckerArtifactCheckpointImporter.Import(
                            importDirectory,
                            instance,
                            new CheckerArtifactImportOptions(
                                ZeroConflictMode: shouldUseZeroConflictChecker,
                                EndpointOccupationSeconds: checkerLoopEndpointOccupationSeconds,
                                ExpectedSectionEdgeCount: 1154,
                                ExpectedStationEventEdgeCount: 46));
                        string checkpointOutputPath = Path.GetFullPath(checkerResumeInput.CheckpointOutputPath!);
                        CheckerCheckpointStore.SaveAtomic(
                            checkpointOutputPath,
                            instance.Meta.InstanceName,
                            checkerCheckpoint);
                        Console.WriteLine(
                            $"Checker artifacts imported: iteration={checkerCheckpoint.LastAcceptedIteration} " +
                            $"objective={checkerCheckpoint.Snapshot.ObjectiveValue:F0} " +
                            $"section_edges={checkerCheckpoint.ActiveSectionEdges.Count} " +
                            $"station_edges={checkerCheckpoint.ActiveStationEventEdges.Count}");
                        Console.WriteLine($"Checker checkpoint written to: {checkpointOutputPath}");
                    }
                    else if (checkerResumeInput.Mode == CheckerResumeInputMode.LoadCheckpoint)
                    {
                        string checkpointInputPath = Path.GetFullPath(checkerResumeInput.CheckpointInputPath!);
                        checkerCheckpoint = CheckerCheckpointStore.Load(
                            checkpointInputPath,
                            instance,
                            shouldUseZeroConflictChecker,
                            checkerLoopEndpointOccupationSeconds,
                            repairRunningTimeSupplementRatio,
                            adaptiveMode: shouldUseAdaptiveSingletonConvergence,
                            expectedCoordinationStrategy: experimentModeRequest.CoordinationStrategy,
                            expectedSearchConfigurationFingerprint:
                                experimentModeRequest.CoordinationStrategy == CheckerCoordinationStrategy.ValidityFirstBenders &&
                                !diagnosticCommonMacroCheckpoint
                                    ? ValidityFirstConfigurationFingerprint.Calculate(checkerLoopOptions)
                                    : null);
                        if (diagnosticCommonMacroCheckpoint)
                        {
                            Console.WriteLine(
                                "Diagnostic common-Macro checkpoint mode: search-budget fingerprint mismatch is intentionally ignored; " +
                                "instance, zero-conflict, endpoint, strategy, and running-time-supplement compatibility remain checked.");
                        }
                        Console.WriteLine($"Checker checkpoint loaded from: {checkpointInputPath}");
                    }
                    if (!string.IsNullOrWhiteSpace(masterSnapshotInput))
                    {
                        checkerSeed = MasterSnapshotInputLoader.Load(
                            Path.GetFullPath(masterSnapshotInput),
                            instance.Meta.InstanceName,
                            instance.Trains.Select(train => train.TrainIndex).ToHashSet());
                        Console.WriteLine($"Checker Master snapshot loaded from: {Path.GetFullPath(masterSnapshotInput)}");
                    }
                    MasterCheckerLoopSolver checkerLoopSolver = new(
                        instance,
                        neighborRadius,
                        checkerLoopOptions,
                        checkerSeed,
                        checkerCheckpoint,
                        checkerResumeInput.CheckpointOutputPath is null
                            ? null
                            : Path.GetFullPath(checkerResumeInput.CheckpointOutputPath),
                        validityFirstProcessElapsedBeforeSolveSeconds: 0.0,
                        validityFirstProcessElapsedSecondsProvider:
                            () => processRuntimeClock.Elapsed.TotalSeconds);
                    if (shouldUseAdaptiveSingletonConvergence)
                    {
                        DateTimeOffset adaptiveCreatedAtUtc = DateTimeOffset.UtcNow;
                        AdaptiveRunArtifactLayout adaptiveLayout = string.IsNullOrWhiteSpace(
                            adaptiveArtifactsOutput)
                            ? AdaptiveRunArtifactLayout.CreateUnique(
                                artifactsDirectory,
                                adaptiveCreatedAtUtc)
                            : AdaptiveRunArtifactLayout.CreateRequested(
                                Path.GetFullPath(adaptiveArtifactsOutput));
                        using CancellationTokenSource cancellation = new();
                        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
                        {
                            eventArgs.Cancel = true;
                            cancellation.Cancel();
                        };
                        Console.CancelKeyPress += cancelHandler;
                        try
                        {
                            AdaptiveSingletonInvocationResult adaptiveResult =
                                checkerLoopSolver.RunAdaptiveSingletonConvergence(
                                    () => cancellation.IsCancellationRequested,
                                    adaptiveExpectedInitialRootConflicts);
                            foreach (string summaryLine in
                                     AdaptiveSingletonConsoleSummaryFormatter.Format(adaptiveResult))
                            {
                                Console.WriteLine(summaryLine);
                            }
                            string checkpointPath = Path.GetFullPath(
                                checkerResumeInput.CheckpointOutputPath!);
                            string inputPath = Path.GetFullPath(
                                checkerResumeInput.Mode == CheckerResumeInputMode.LoadCheckpoint
                                    ? checkerResumeInput.CheckpointInputPath!
                                    : masterSnapshotInput!);
                            IReadOnlyList<AdaptiveSingletonSweepDiagnostic> diagnostics =
                                adaptiveResult.SweepDiagnostics ?? [];
                            IReadOnlyList<AdaptiveUnknownEvidence> unknownEvidence =
                                adaptiveResult.UnknownEvidence ?? [];
                            AdaptiveSingletonReporter.Write(
                                diagnostics,
                                unknownEvidence,
                                adaptiveLayout.SweepsDirectory);
                            AdaptiveRunManifest manifest = new(
                                Path.GetFileName(adaptiveLayout.RootDirectory),
                                adaptiveCreatedAtUtc,
                                Path.GetFullPath(datasetPath),
                                inputPath,
                                adaptiveResult.InitialAcceptedStateFingerprint,
                                MicroConflictFingerprint.Calculate(
                                    (adaptiveResult.InitialMicroReport ?? adaptiveResult.FinalMicroReport).Conflicts),
                                adaptiveResult.InitialRootConflictCount,
                                args.ToList(),
                                checkerLoopInitialCandidateCap,
                                checkerLoopExpandedCandidateCap,
                                experimentModeRequest.CheckerMaxIterations!.Value,
                                checkerLoopRecourseWindows.ToList(),
                                timeLimitSeconds ?? 20.0,
                                checkerSecondaryTimeLimitSeconds,
                                repairRunningTimeSupplementRatio,
                                checkpointPath,
                                Environment.Version.ToString(),
                                typeof(GRBModel).Assembly.GetName().Version?.ToString() ?? "unknown",
                                adaptiveLayout.RootDirectory,
                                AdaptiveCheckpointResumeConfiguration.From(checkerLoopOptions)
                                    .SearchConfigurationFingerprint,
                                instance.Meta.InstanceName);
                            AdaptiveRunArtifactWriter.Write(new AdaptiveRunArtifactRequest(
                                adaptiveLayout,
                                manifest,
                                diagnostics,
                                adaptiveResult.SweepHistory ?? [],
                                adaptiveResult.CandidateTrials ?? [],
                                unknownEvidence,
                                adaptiveResult.InitialMicroReport ?? adaptiveResult.FinalMicroReport,
                                adaptiveResult.FinalMicroReport,
                                adaptiveResult.FinalAcceptedSnapshot,
                                adaptiveResult.FinalValidation ?? throw new InvalidDataException(
                                    "Adaptive production result has no independent final validation."),
                                adaptiveResult.FinalSectionEdges ?? [],
                                adaptiveResult.FinalStationEdges ?? [],
                                adaptiveResult.AcceptedCandidateIds ?? [],
                                checkpointPath,
                                adaptiveResult.TerminationReason,
                                adaptiveResult.InitialReferenceFingerprint,
                                adaptiveResult.TotalRuntimeSeconds,
                                adaptiveResult.AcceptedTransitionCount));
                            Console.WriteLine(
                                $"Adaptive artifacts written to: {adaptiveLayout.RootDirectory}");
                        }
                        catch (Exception exception)
                        {
                            AdaptiveRunArtifactWriter.WriteFailure(
                                adaptiveLayout,
                                args,
                                Path.GetFullPath(checkerResumeInput.CheckpointOutputPath!),
                                exception);
                            throw;
                        }
                        finally
                        {
                            Console.CancelKeyPress -= cancelHandler;
                        }

                        return 0;
                    }

                    string defaultCheckerArtifactsName =
                        experimentModeRequest.Mode switch
                        {
                            ExperimentMode.ValidityFirstBendersChecker => "validity_first_benders",
                            ExperimentMode.FixedSnapshotMultiIterationChecker => "checker_loop_multi_iteration",
                            _ => "checker_loop"
                        };
                    string checkerLoopArtifactsDirectory = !string.IsNullOrWhiteSpace(checkerResumeInput.CheckpointOutputPath)
                        ? Path.GetDirectoryName(Path.GetFullPath(checkerResumeInput.CheckpointOutputPath))!
                        : Path.Combine(artifactsDirectory, defaultCheckerArtifactsName);
                    Directory.CreateDirectory(checkerLoopArtifactsDirectory);
                    if (experimentModeRequest.CoordinationStrategy ==
                        CheckerCoordinationStrategy.ValidityFirstBenders)
                    {
                        ValidityFirstRunIdentityReporter.Write(
                            checkerLoopArtifactsDirectory,
                            instance.Meta.InstanceName,
                            checkerLoopOptions,
                            DateTimeOffset.UtcNow,
                            !string.IsNullOrWhiteSpace(masterSnapshotInput)
                                ? "reused_snapshot"
                                : checkerResumeInput.Mode == CheckerResumeInputMode.LoadCheckpoint
                                    ? "checkpoint_resume"
                                    : "optimized_initial_master");
                    }

                    using CheckerLoopResult checkerLoopResult = checkerLoopSolver.Solve();
                    MasterCheckerLoopSolver.WriteProgressReport(checkerLoopResult, checkerLoopArtifactsDirectory);
                    Console.WriteLine($"Checker loop progress written to: {Path.Combine(checkerLoopArtifactsDirectory, "checker_loop_progress.md")}");
                    Console.WriteLine($"Checker loop termination: {checkerLoopResult.TerminationReason}");
                    Console.WriteLine($"Checker loop injected cut count: {checkerLoopResult.ActiveCuts.Count}");

                    if (checkerLoopResult.FinalModel.HasIncumbentSolution)
                    {
                        ExportAndAnalyzeIncumbent(
                            checkerLoopResult.FinalModel,
                            checkerLoopArtifactsDirectory,
                            checkerLoopEndpointOccupationSeconds);
                    }
                    else
                    {
                        Console.WriteLine("Checker loop finished without an incumbent solution for the final model.");
                    }

                    return CheckerLoopProcessExitCodePolicy.Resolve(
                        checkerLoopResult.CoordinationStrategy,
                        checkerLoopResult.TerminationReason,
                        checkerLoopResult.BestJointFeasibleSolution is not null);
                }

                BasicMilpModelBuilder builder = new(
                    instance,
                    neighborRadius,
                    runningTimeSupplementRatio: macroRunningTimeSupplementRatio);
                using BasicMilpModel model = builder.Build();

                string lpPath = Path.Combine(artifactsDirectory, "basic_model.lp");
                model.Write(lpPath);
                Console.WriteLine($"LP model written to: {lpPath}");

                MasterDiagnosticsReporter diagnosticsReporter = new();
                string diagnosticsPath = Path.Combine(artifactsDirectory, "master_diagnostics.md");
                diagnosticsReporter.WriteReport(model, diagnosticsPath);
                Console.WriteLine($"Master diagnostics written to: {diagnosticsPath}");

                if (shouldSolve)
                {
                    if (timeLimitSeconds.HasValue)
                    {
                        model.Model.Set(GRB.DoubleParam.TimeLimit, timeLimitSeconds.Value);
                    }

                    model.Optimize();
                    Console.WriteLine($"Solver status: {model.StatusName}");

                    if (model.HasIncumbentSolution)
                    {
                        ExportAndAnalyzeIncumbent(model, artifactsDirectory);
                    }
                    else
                    {
                        Console.WriteLine("No incumbent solution was found, so schedule export and validation were skipped.");
                        if (shouldWriteIis && model.Model.Status == GRB.Status.INFEASIBLE)
                        {
                            string iisPath = Path.Combine(artifactsDirectory, "master_iis.ilp");
                            model.IdentityAudit = GurobiModelIdentityAuditor.ValidateAndCapture(
                                model.Model,
                                "Program::master::before_iis",
                                Path.Combine(artifactsDirectory, "master_identity_failure.txt"));
                            model.Model.ComputeIIS();
                            model.Write(iisPath);
                            Console.WriteLine($"IIS written to: {iisPath}");
                        }
                    }
                }
            }
            catch (GRBException grbException) when (grbException.Message.Contains("license", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("The normalized data was loaded successfully, but Gurobi could not start because no valid license was found.");
                Console.WriteLine("Once a local Gurobi license is configured, the same entry point can build, solve, export, and validate the master model.");
                return 2;
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Unhandled error while running the master MILP workflow.");
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void ExportAndAnalyzeIncumbent(
        BasicMilpModel model,
        string artifactsDirectory,
        int endpointOccupationSeconds = StationEndpointOccupationPolicy.DefaultSeconds,
        bool includeMicroDiagnostics = true)
    {
        MasterSolutionSnapshot bootstrapSnapshot = MasterSolutionSnapshot.Capture(model);
        string bootstrapSnapshotPath = Path.Combine(artifactsDirectory, "master_snapshot.json");
        MasterSolutionSnapshotStore.Save(
            bootstrapSnapshotPath,
            model.Instance.Meta.InstanceName,
            bootstrapSnapshot);
        Console.WriteLine($"Master snapshot written to: {bootstrapSnapshotPath}");

        MasterSolutionExporter exporter = new();
        exporter.Export(model, artifactsDirectory);
        Console.WriteLine($"Master solution files written to: {artifactsDirectory}");

        MasterSolutionValidator validator = new();
        MasterValidationReport validationReport = validator.Validate(model);
        string validationPath = Path.Combine(artifactsDirectory, "master_validation.md");
        validator.WriteReport(validationReport, validationPath);
        Console.WriteLine($"Master validation written to: {validationPath}");
        Console.WriteLine($"Master validation feasible: {validationReport.IsFeasible}, violations: {validationReport.Violations.Count}");

        MasterPrecedenceAnalyzer precedenceAnalyzer = new();
        precedenceAnalyzer.WriteReports(model, artifactsDirectory);
        Console.WriteLine($"Precedence analysis written to: {Path.Combine(artifactsDirectory, "precedence_analysis.md")}");

        MasterOvertakingAnalyzer overtakingAnalyzer = new();
        overtakingAnalyzer.WriteReports(model, artifactsDirectory);
        Console.WriteLine($"Overtaking analysis written to: {Path.Combine(artifactsDirectory, "overtaking_summary.md")}");

        if (!includeMicroDiagnostics)
        {
            Console.WriteLine("Fresh Macro bootstrap complete; Micro Checker was not started.");
            return;
        }

        MasterMicroFeasibilityChecker microChecker = CheckerMicroValidationFactory.CreateChecker(endpointOccupationSeconds);
        MicroFeasibilityReport microReport = microChecker.Validate(model);
        microChecker.WriteReports(microReport, artifactsDirectory);
        Console.WriteLine($"Micro feasibility written to: {Path.Combine(artifactsDirectory, "micro_feasibility.md")}");
        Console.WriteLine($"Micro checker feasible: {microReport.IsFeasible}, conflicts: {microReport.Conflicts.Count}");

        MicroConflictCutCandidateGenerator cutCandidateGenerator = new();
        MicroCutCandidateReport cutCandidateReport = cutCandidateGenerator.Generate(microReport);
        cutCandidateGenerator.WriteReports(cutCandidateReport, artifactsDirectory);
        Console.WriteLine($"Micro cut candidates written to: {Path.Combine(artifactsDirectory, "micro_cut_candidates.md")}");
        Console.WriteLine($"Micro cut candidate count: {cutCandidateReport.Candidates.Count}");
    }

    private static string ResolveRepositoryRoot()
    {
        // AppContext.BaseDirectory 指向 bin/Debug/net8.0，因此需要回退到研究工程根目录。
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    }

    private static string ResolveDatasetPath(IReadOnlyList<string> args, string repositoryRoot)
    {
        string defaultDatasetPath = Path.Combine(repositoryRoot, "Data", "JH");
        string? explicitDatasetPath = args.FirstOrDefault(static arg => !arg.StartsWith("--", StringComparison.OrdinalIgnoreCase));
        return explicitDatasetPath is null ? defaultDatasetPath : Path.GetFullPath(explicitDatasetPath);
    }

    private static double? ResolveOptionalDoubleArgument(IReadOnlyList<string> args, string prefix)
    {
        string? raw = args.FirstOrDefault(arg => arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (raw is null)
        {
            return null;
        }

        string value = raw[prefix.Length..];
        return double.TryParse(value, out double parsed) && parsed > 0 ? parsed : null;
    }

    private static int? ResolveOptionalIntArgument(IReadOnlyList<string> args, string prefix)
    {
        string? raw = args.FirstOrDefault(arg => arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (raw is null)
        {
            return null;
        }

        string value = raw[prefix.Length..];
        return int.TryParse(value, out int parsed) && parsed > 0 ? parsed : null;
    }

    private static int? ResolveOptionalNonNegativeIntArgument(IReadOnlyList<string> args, string prefix)
    {
        string? raw = args.FirstOrDefault(arg => arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (raw is null)
        {
            return null;
        }
        string value = raw[prefix.Length..];
        return int.TryParse(value, out int parsed) && parsed >= 0
            ? parsed
            : throw new ArgumentOutOfRangeException(prefix);
    }

    private static string? ResolveOptionalStringArgument(IReadOnlyList<string> args, string prefix)
    {
        string? raw = args.FirstOrDefault(arg => arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (raw is null)
        {
            return null;
        }

        string value = raw[prefix.Length..].Trim();
        return value.Length == 0 ? null : value;
    }
}
