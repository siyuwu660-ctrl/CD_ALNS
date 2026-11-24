// Checker 选项组合器把基础命令行配置、续跑状态和特定实验覆盖项合成为一个不可变选项对象。
// 集中组合避免不同执行入口遗漏时间预算、修复层级或诊断开关，并保持默认值只在一处定义。
namespace GurobiTimetablingResearch.Optimization;

internal static class CheckerLoopOptionsComposer
{
    public static CheckerLoopOptions Compose(
        int maxIterations,
        int maxNewCutsPerIteration,
        double timeLimitSecondsPerSolve,
        string? focusStationName,
        int maxHeuristicTrialsPerIteration,
        bool zeroConflictMode,
        IReadOnlyList<int> recourseWindowSeconds,
        int exactBatchCap,
        int endpointOccupationSeconds,
        bool enableLocalMicroRepair,
        double secondaryTimeLimitSecondsPerSolve,
        StructuralRepairNeighborhoodEscalationOptions neighborhoodEscalationOptions,
        double repairRunningTimeSupplementRatio = 0.05,
        int exactPerStationCap = 4,
        int exactPerPairCap = 2,
        bool enableMicroPromotion = true,
        int microPromotionPersistenceRounds = 2,
        bool enableIisGuidedRepair = false,
        int iisMaxExpansionRounds = 5,
        int iisTemporalExpansionStepSeconds = 300,
        int iisMaxExtraTemporalSeconds = 1800,
        bool fixedSnapshotMultiIterationMode = false,
        bool enableFeasibleSingletonGlobalEvaluation = false,
        AdaptiveSingletonOptions? adaptiveSingletonOptions = null,
        CheckerCoordinationStrategy coordinationStrategy = CheckerCoordinationStrategy.Legacy,
        int validityFirstHotspotMaximumRequirementCount = 40,
        double validityFirstTotalRuntimeBudgetSeconds = 3600.0,
        double validityFirstFinalValidationReserveSeconds = 120.0,
        int validityFirstMaximumFeedbackOnlyRounds = 2,
        int validityFirstRecoveryMipFocus = 1,
        double validityFirstRecoveryMipGap = 0.05,
        bool enablePhasedFeasibilityRecovery = false,
        int validityFirstRecoveryHotspotMaximumRequirementCount = 80,
        int validityFirstRecoveryHotspotMaximumAlternativeCount = 2000,
        int validityFirstRecoveryHotspotMaximumEstimatedVariableCount = 200000,
        int validityFirstRecoveryHotspotMaximumEstimatedConstraintCount = 500000,
        int validityFirstRecoveryHotspotMaximumConflictRelationCount = 150000,
        double validityFirstIisTimeLimitSeconds = 10.0,
        double validityFirstIisCumulativeBudgetSeconds = 60.0,
        double validityFirstLocalNeighborhoodTimeLimitSeconds = 60.0,
        double validityFirstFixedAssignmentRetimingTimeLimitSeconds = 120.0,
        double validityFirstConflictLinkedRetimingTimeLimitSeconds = 300.0,
        double validityFirstPreviouslyConsumedRuntimeSeconds = 0.0,
        string? validityFirstReplayNeighborhoodInputPath = null,
        string? validityFirstReplayHotspotStationName = null,
        RepairNeighborhoodSelectorKind repairNeighborhoodSelector = RepairNeighborhoodSelectorKind.Legacy,
        RepairActionSelectorKind repairActionSelector = RepairActionSelectorKind.Legacy,
        bool stopAfterFirstJointFeasible = false,
        MacroCandidateCheckStrategy macroCandidateCheckStrategy =
            MacroCandidateCheckStrategy.CompleteObjectives,
        MicroCoordinationMode microCoordinationMode = MicroCoordinationMode.D0)
    {
        ArgumentNullException.ThrowIfNull(neighborhoodEscalationOptions);
        AdaptiveSingletonOptions adaptive =
            (adaptiveSingletonOptions ?? AdaptiveSingletonOptions.Disabled).Validate();
        if (enableFeasibleSingletonGlobalEvaluation && !zeroConflictMode)
        {
            throw new ArgumentException(
                "Feasible singleton Global Micro evaluation requires zero-conflict Checker mode.");
        }
        if (adaptive.Enabled &&
            (!fixedSnapshotMultiIterationMode || !zeroConflictMode ||
             !enableFeasibleSingletonGlobalEvaluation || !enableIisGuidedRepair || enableLocalMicroRepair))
        {
            throw new ArgumentException(
                "Adaptive singleton convergence requires fixed-snapshot multi-iteration zero-conflict mode, feasible singleton Global Micro evaluation, IIS-guided repair, and LocalMicroExact disabled.");
        }
        if (coordinationStrategy == CheckerCoordinationStrategy.ValidityFirstBenders &&
            (!zeroConflictMode || enableLocalMicroRepair || adaptive.Enabled ||
             enableFeasibleSingletonGlobalEvaluation))
        {
            throw new ArgumentException(
                "Validity-first coordination requires zero-conflict mode and forbids Legacy local repair, feasible-singleton sweeps, and Adaptive Singleton convergence.");
        }
        if (coordinationStrategy == CheckerCoordinationStrategy.AdaptiveSingleton && !adaptive.Enabled)
        {
            throw new ArgumentException(
                "AdaptiveSingleton coordination requires enabled Adaptive Singleton options.");
        }
        if (validityFirstHotspotMaximumRequirementCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(validityFirstHotspotMaximumRequirementCount));
        }
        if (validityFirstMaximumFeedbackOnlyRounds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(validityFirstMaximumFeedbackOnlyRounds));
        }
        if (validityFirstRecoveryMipFocus is < 0 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(validityFirstRecoveryMipFocus));
        }
        if (!double.IsFinite(validityFirstRecoveryMipGap) ||
            validityFirstRecoveryMipGap < 0.0 || validityFirstRecoveryMipGap > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(validityFirstRecoveryMipGap));
        }
        if (validityFirstRecoveryHotspotMaximumRequirementCount <= 0 ||
            validityFirstRecoveryHotspotMaximumAlternativeCount <= 0 ||
            validityFirstRecoveryHotspotMaximumEstimatedVariableCount <= 0 ||
            validityFirstRecoveryHotspotMaximumEstimatedConstraintCount <= 0 ||
            validityFirstRecoveryHotspotMaximumConflictRelationCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(validityFirstRecoveryHotspotMaximumRequirementCount),
                "Phased recovery model-size budgets must all be greater than zero.");
        }
        _ = ValidityFirstRuntimeBudgetPolicy.Resolve(
            timeLimitSecondsPerSolve,
            validityFirstTotalRuntimeBudgetSeconds,
            elapsedSeconds: 0.0,
            validityFirstFinalValidationReserveSeconds);
        _ = ValidityFirstIisRuntimeBudgetPolicy.Resolve(
            validityFirstIisTimeLimitSeconds,
            validityFirstIisCumulativeBudgetSeconds,
            0.0,
            validityFirstTotalRuntimeBudgetSeconds,
            0.0,
            validityFirstFinalValidationReserveSeconds);
        if (!double.IsFinite(validityFirstLocalNeighborhoodTimeLimitSeconds) ||
            validityFirstLocalNeighborhoodTimeLimitSeconds <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(validityFirstLocalNeighborhoodTimeLimitSeconds));
        }
        if (!double.IsFinite(validityFirstFixedAssignmentRetimingTimeLimitSeconds) ||
            validityFirstFixedAssignmentRetimingTimeLimitSeconds <= 0.0 ||
            !double.IsFinite(validityFirstConflictLinkedRetimingTimeLimitSeconds) ||
            validityFirstConflictLinkedRetimingTimeLimitSeconds <= 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(validityFirstFixedAssignmentRetimingTimeLimitSeconds));
        }
        if (!double.IsFinite(validityFirstPreviouslyConsumedRuntimeSeconds) ||
            validityFirstPreviouslyConsumedRuntimeSeconds < 0.0 ||
            validityFirstPreviouslyConsumedRuntimeSeconds >= validityFirstTotalRuntimeBudgetSeconds)
        {
            throw new ArgumentOutOfRangeException(nameof(validityFirstPreviouslyConsumedRuntimeSeconds));
        }
        if (!string.IsNullOrWhiteSpace(validityFirstReplayNeighborhoodInputPath) &&
            string.IsNullOrWhiteSpace(validityFirstReplayHotspotStationName))
        {
            throw new ArgumentException(
                "A saved recovery neighborhood requires its replay hotspot station.");
        }
        CheckerRepairSupplementOptions repairSupplement =
            CheckerRepairSupplementOptions.Create(repairRunningTimeSupplementRatio);

        return new CheckerLoopOptions(
            maxIterations,
            maxNewCutsPerIteration,
            timeLimitSecondsPerSolve,
            FocusStationName: focusStationName,
            MaxHeuristicTrialsPerIteration: maxHeuristicTrialsPerIteration,
            ZeroConflictMode: zeroConflictMode,
            ConfiguredRecourseWindowSeconds: recourseWindowSeconds,
            ExactBatchCap: exactBatchCap,
            ExactPerStationCap: exactPerStationCap,
            ExactPerPairCap: exactPerPairCap,
            EndpointOccupationSeconds: endpointOccupationSeconds,
            RepairRunningTimeSupplementRatio: repairSupplement.Ratio,
            EnableLocalMicroRepair: enableLocalMicroRepair,
            EnableMicroPromotion: enableMicroPromotion,
            MicroPromotionPersistenceRounds: microPromotionPersistenceRounds,
            SecondaryTimeLimitSecondsPerSolve: secondaryTimeLimitSecondsPerSolve,
            EnableNeighborhoodEscalation: neighborhoodEscalationOptions.Enabled,
            NeighborhoodEscalationPaddings: neighborhoodEscalationOptions.EscalationPaddings,
            NeighborhoodEscalationWindowSeconds: neighborhoodEscalationOptions.EscalationWindowSeconds,
            InitialNeighborhoodLevel: neighborhoodEscalationOptions.InitialLevel,
            EnableIisGuidedRepair: enableIisGuidedRepair ||
                                   coordinationStrategy == CheckerCoordinationStrategy.ValidityFirstBenders,
            IisMaxExpansionRounds: iisMaxExpansionRounds,
            IisTemporalExpansionStepSeconds: iisTemporalExpansionStepSeconds,
            IisMaxExtraTemporalSeconds: iisMaxExtraTemporalSeconds,
            FixedSnapshotMultiIterationMode: fixedSnapshotMultiIterationMode,
            EnableFeasibleSingletonGlobalEvaluation: enableFeasibleSingletonGlobalEvaluation,
            ConfiguredAdaptiveSingletonOptions: adaptive,
            CoordinationStrategy: coordinationStrategy,
            ValidityFirstHotspotMaximumRequirementCount: validityFirstHotspotMaximumRequirementCount,
            ValidityFirstTotalRuntimeBudgetSeconds: validityFirstTotalRuntimeBudgetSeconds,
            ValidityFirstFinalValidationReserveSeconds: validityFirstFinalValidationReserveSeconds,
            ValidityFirstMaximumFeedbackOnlyRounds: validityFirstMaximumFeedbackOnlyRounds,
            ValidityFirstRecoveryMipFocus: validityFirstRecoveryMipFocus,
            ValidityFirstRecoveryMipGap: validityFirstRecoveryMipGap,
            EnablePhasedFeasibilityRecovery: enablePhasedFeasibilityRecovery,
            ValidityFirstRecoveryHotspotMaximumRequirementCount:
                validityFirstRecoveryHotspotMaximumRequirementCount,
            ValidityFirstRecoveryHotspotMaximumAlternativeCount:
                validityFirstRecoveryHotspotMaximumAlternativeCount,
            ValidityFirstRecoveryHotspotMaximumEstimatedVariableCount:
                validityFirstRecoveryHotspotMaximumEstimatedVariableCount,
            ValidityFirstRecoveryHotspotMaximumEstimatedConstraintCount:
                validityFirstRecoveryHotspotMaximumEstimatedConstraintCount,
            ValidityFirstRecoveryHotspotMaximumConflictRelationCount:
                validityFirstRecoveryHotspotMaximumConflictRelationCount,
            ValidityFirstIisTimeLimitSeconds: validityFirstIisTimeLimitSeconds,
            ValidityFirstIisCumulativeBudgetSeconds: validityFirstIisCumulativeBudgetSeconds,
            ValidityFirstLocalNeighborhoodTimeLimitSeconds: validityFirstLocalNeighborhoodTimeLimitSeconds,
            ValidityFirstFixedAssignmentRetimingTimeLimitSeconds:
                validityFirstFixedAssignmentRetimingTimeLimitSeconds,
            ValidityFirstConflictLinkedRetimingTimeLimitSeconds:
                validityFirstConflictLinkedRetimingTimeLimitSeconds,
            ValidityFirstPreviouslyConsumedRuntimeSeconds: validityFirstPreviouslyConsumedRuntimeSeconds,
            ValidityFirstReplayNeighborhoodInputPath: validityFirstReplayNeighborhoodInputPath,
            ValidityFirstReplayHotspotStationName: validityFirstReplayHotspotStationName,
            RepairNeighborhoodSelector: repairNeighborhoodSelector,
            RepairActionSelector: repairActionSelector,
            StopAfterFirstJointFeasible: stopAfterFirstJointFeasible,
            MacroCandidateCheckStrategy: macroCandidateCheckStrategy,
            MicroCoordinationMode: microCoordinationMode);
    }
}
