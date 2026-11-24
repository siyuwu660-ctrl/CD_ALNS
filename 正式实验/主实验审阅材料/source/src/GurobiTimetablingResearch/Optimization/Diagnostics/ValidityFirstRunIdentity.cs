using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GurobiTimetablingResearch.Optimization;

/// <summary>
/// 为独立协调实验生成稳定配置指纹，使输出目录和 checkpoint 能验证策略及关键建模参数。
/// </summary>
public static class ValidityFirstConfigurationFingerprint
{
    public static string Calculate(CheckerLoopOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        string canonical = string.Join('|',
            $"strategy={options.CoordinationStrategy}",
            $"zero_conflict={options.ZeroConflictMode}",
            $"max_iterations={options.MaxIterations}",
            $"primary_limit={Format(options.TimeLimitSecondsPerSolve)}",
            $"secondary_limit={Format(options.SecondaryTimeLimitSecondsPerSolve)}",
            $"total_runtime_budget={Format(options.ValidityFirstTotalRuntimeBudgetSeconds)}",
            $"final_validation_reserve={Format(options.ValidityFirstFinalValidationReserveSeconds)}",
            $"exact_batch={options.ExactBatchCap}",
            $"exact_station={options.ExactPerStationCap}",
            $"exact_pair={options.ExactPerPairCap}",
            $"endpoint_occupation={options.EndpointOccupationSeconds}",
            $"repair_supplement={Format(options.RepairRunningTimeSupplementRatio)}",
            $"hotspot_limit={options.ValidityFirstHotspotMaximumRequirementCount}",
            $"feedback_only_round_limit={options.ValidityFirstMaximumFeedbackOnlyRounds}",
            $"recovery_mip_focus={options.ValidityFirstRecoveryMipFocus}",
            $"recovery_mip_gap={Format(options.ValidityFirstRecoveryMipGap)}",
            $"phased_feasibility_recovery={options.EnablePhasedFeasibilityRecovery}",
            $"recovery_hotspot_requirements={options.ValidityFirstRecoveryHotspotMaximumRequirementCount}",
            $"recovery_hotspot_alternatives={options.ValidityFirstRecoveryHotspotMaximumAlternativeCount}",
            $"recovery_hotspot_estimated_variables={options.ValidityFirstRecoveryHotspotMaximumEstimatedVariableCount}",
            $"recovery_hotspot_estimated_constraints={options.ValidityFirstRecoveryHotspotMaximumEstimatedConstraintCount}",
            $"recovery_hotspot_resource_relations={options.ValidityFirstRecoveryHotspotMaximumConflictRelationCount}",
            $"iis_rounds={options.IisMaxExpansionRounds}",
            $"iis_step={options.IisTemporalExpansionStepSeconds}",
            $"iis_extra={options.IisMaxExtraTemporalSeconds}");
        // 旧模式的规范串保持不变，使既有 checkpoint 仍能按原指纹严格回读；
        // 只有显式启用实验选择器或首个联合可行即停时才扩展指纹。
        if (options.RepairNeighborhoodSelector != RepairNeighborhoodSelectorKind.Legacy)
        {
            canonical += $"|repair_neighborhood_selector={options.RepairNeighborhoodSelector}";
        }
        if (options.RepairActionSelector != RepairActionSelectorKind.Legacy)
        {
            canonical += $"|repair_action_selector={options.RepairActionSelector}";
        }
        if (options.StopAfterFirstJointFeasible)
        {
            canonical += "|stop_after_first_joint_feasible=true";
        }
        if (options.MacroCandidateCheckStrategy != MacroCandidateCheckStrategy.CompleteObjectives)
        {
            canonical += $"|macro_candidate_strategy={options.MacroCandidateCheckStrategy}";
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}

public sealed record ValidityFirstRunIdentity(
    string StrategyId,
    string ConfigurationFingerprint,
    string InstanceName,
    DateTimeOffset CreatedAtUtc,
    string MacroSource,
    MicroCoordinationMode MicroCoordinationMode = MicroCoordinationMode.D0);

/// <summary>
/// 在独立实验目录写入策略标识和配置指纹，供实验对照与恢复检查使用。
/// </summary>
public static class ValidityFirstRunIdentityReporter
{
    public static void Write(
        string outputDirectory,
        string instanceName,
        CheckerLoopOptions options,
        DateTimeOffset createdAtUtc,
        string macroSource)
    {
        if (options.CoordinationStrategy != CheckerCoordinationStrategy.ValidityFirstBenders)
        {
            throw new ArgumentException("Validity-first run identity requires the validity-first coordination strategy.");
        }

        Directory.CreateDirectory(outputDirectory);
        ValidityFirstRunIdentity identity = new(
            "validity_first_benders",
            ValidityFirstConfigurationFingerprint.Calculate(options),
            instanceName,
            createdAtUtc,
            macroSource,
            options.MicroCoordinationMode);
        JsonSerializerOptions jsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        File.WriteAllText(
            Path.Combine(outputDirectory, "coordination_run_identity.json"),
            JsonSerializer.Serialize(identity, jsonOptions),
            new UTF8Encoding(false));
    }
}
