using System.Globalization;
using System.Text;

namespace GurobiTimetablingResearch.Optimization;

/// <summary>
/// 记录一次热点候选评估从身份选择、闭包构造到联合求解和两级 Micro 复核的完整状态。
/// </summary>
public sealed record ValidityFirstHotspotAttemptDiagnostic(
    int Iteration,
    int ConsecutiveFeedbackOnlyRounds,
    string TriggerReason,
    string CandidatePriority,
    string StationName,
    string AssignmentCoreIdentity,
    bool EvaluationAttempted,
    bool BuildComplete,
    string BuildStatus,
    int CandidateRequirementCount,
    int ClosureRequirementCount,
    bool PromotionPersisted,
    bool JointSolveStarted,
    bool JointSolveHasIncumbent,
    string JointSolverStatus,
    int LocalConflictsBefore,
    int? LocalConflictsAfter,
    int GlobalConflictsBefore,
    int? GlobalConflictsAfter,
    double BuildSeconds,
    double LocalMicroSeconds,
    int CoreRequirementCount = 0,
    int ProtectionRequirementCount = 0,
    int AlternativeCount = 0,
    int EstimatedVariableCount = 0,
    int EstimatedConstraintCount = 0,
    int ResourceConflictRelationCount = 0,
    string RepairAttemptFingerprint = "",
    int TrainCount = 0,
    int OrderVariableCount = 0,
    int LegacyEstimatedVariableCount = 0,
    int LegacyOrderVariableCount = 0);

public static class ValidityFirstHotspotAttemptReporter
{
    public static void Write(
        IReadOnlyList<ValidityFirstHotspotAttemptDiagnostic> attempts,
        string outputDirectory)
    {
        ArgumentNullException.ThrowIfNull(attempts);
        Directory.CreateDirectory(outputDirectory);
        string path = Path.Combine(outputDirectory, "validity_first_hotspot_attempts.csv");
        using StreamWriter writer = new(path, false, new UTF8Encoding(false));
        writer.WriteLine(
            "iteration,consecutive_feedback_only_rounds,trigger_reason,candidate_priority,station_name," +
            "assignment_core_identity,evaluation_attempted,build_complete,build_status," +
            "candidate_requirement_count,closure_requirement_count,promotion_persisted,joint_solve_started," +
            "joint_solve_has_incumbent,joint_solver_status,local_conflicts_before,local_conflicts_after," +
            "global_conflicts_before,global_conflicts_after,build_seconds,local_micro_seconds," +
            "core_requirement_count,protection_requirement_count,alternative_count,estimated_variable_count," +
            "estimated_constraint_count,resource_conflict_relation_count,repair_attempt_fingerprint,train_count," +
            "order_variable_count,legacy_estimated_variable_count,legacy_order_variable_count");
        foreach (ValidityFirstHotspotAttemptDiagnostic item in attempts)
        {
            writer.WriteLine(string.Join(',',
                item.Iteration.ToString(CultureInfo.InvariantCulture),
                item.ConsecutiveFeedbackOnlyRounds.ToString(CultureInfo.InvariantCulture),
                Escape(item.TriggerReason),
                Escape(item.CandidatePriority),
                Escape(item.StationName),
                Escape(item.AssignmentCoreIdentity),
                item.EvaluationAttempted,
                item.BuildComplete,
                Escape(item.BuildStatus),
                item.CandidateRequirementCount.ToString(CultureInfo.InvariantCulture),
                item.ClosureRequirementCount.ToString(CultureInfo.InvariantCulture),
                item.PromotionPersisted,
                item.JointSolveStarted,
                item.JointSolveHasIncumbent,
                Escape(item.JointSolverStatus),
                item.LocalConflictsBefore.ToString(CultureInfo.InvariantCulture),
                item.LocalConflictsAfter?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                item.GlobalConflictsBefore.ToString(CultureInfo.InvariantCulture),
                item.GlobalConflictsAfter?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                item.BuildSeconds.ToString("R", CultureInfo.InvariantCulture),
                item.LocalMicroSeconds.ToString("R", CultureInfo.InvariantCulture),
                item.CoreRequirementCount.ToString(CultureInfo.InvariantCulture),
                item.ProtectionRequirementCount.ToString(CultureInfo.InvariantCulture),
                item.AlternativeCount.ToString(CultureInfo.InvariantCulture),
                item.EstimatedVariableCount.ToString(CultureInfo.InvariantCulture),
                item.EstimatedConstraintCount.ToString(CultureInfo.InvariantCulture),
                item.ResourceConflictRelationCount.ToString(CultureInfo.InvariantCulture),
                item.RepairAttemptFingerprint,
                item.TrainCount.ToString(CultureInfo.InvariantCulture),
                item.OrderVariableCount.ToString(CultureInfo.InvariantCulture),
                item.LegacyEstimatedVariableCount.ToString(CultureInfo.InvariantCulture),
                item.LegacyOrderVariableCount.ToString(CultureInfo.InvariantCulture)));
        }
    }

    private static string Escape(string? value)
    {
        value ??= string.Empty;
        return value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
    }
}

/// <summary>
/// 逐个业务作业记录热点域从原始停站能力到最终进路—股道候选的筛选结果。
/// </summary>
public static class ValidityFirstHotspotDomainAuditReporter
{
    public static void Append(
        int iteration,
        string candidateStation,
        IReadOnlyList<HotspotRequirementDomainAudit>? audits,
        string outputDirectory)
    {
        IReadOnlyList<HotspotRequirementDomainAudit> rows = audits ?? [];
        if (rows.Count == 0)
        {
            return;
        }
        Directory.CreateDirectory(outputDirectory);
        string path = Path.Combine(outputDirectory, "validity_first_hotspot_domain_audit.csv");
        bool writeHeader = !File.Exists(path);
        using StreamWriter writer = new(path, true, new UTF8Encoding(false));
        if (writeHeader)
        {
            writer.WriteLine(
                "iteration,candidate_station,station_name,requirement_id,train_index,stop_requirement," +
                "original_resolution,stopping_alternative_count,passing_alternative_count," +
                "stopping_failure_reason,passing_failure_reason,included_modes,classification,evidence");
        }
        foreach (HotspotRequirementDomainAudit item in rows)
        {
            writer.WriteLine(string.Join(',',
                iteration.ToString(CultureInfo.InvariantCulture),
                Escape(candidateStation),
                Escape(item.StationName),
                Escape(item.RequirementId),
                item.TrainIndex.ToString(CultureInfo.InvariantCulture),
                item.StopRequirement,
                item.OriginalResolution,
                item.StoppingAlternativeCount.ToString(CultureInfo.InvariantCulture),
                item.PassingAlternativeCount.ToString(CultureInfo.InvariantCulture),
                item.StoppingFailureReason,
                item.PassingFailureReason,
                Escape(item.IncludedModes),
                item.Classification,
                Escape(item.Evidence)));
        }
    }

    private static string Escape(string? value)
    {
        value ??= string.Empty;
        return value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
    }
}

/// <summary>
/// 逐轮追加 assignment 核心身份，使相同数量但不同列车集、资源域或证明上下文可被区分。
/// </summary>
public static class AssignmentCoreIdentityReporter
{
    public static void Write(
        int iteration,
        string phase,
        IReadOnlyList<AssignmentCoreOccurrence> occurrences,
        string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);
        ArgumentNullException.ThrowIfNull(occurrences);
        Directory.CreateDirectory(outputDirectory);
        string path = Path.Combine(outputDirectory, "validity_first_assignment_core_identities.csv");
        bool writeHeader = !File.Exists(path);
        using StreamWriter writer = new(path, true, new UTF8Encoding(false));
        if (writeHeader)
        {
            writer.WriteLine(
                "iteration,phase,identity_key,station_name,canonical_train_set,resource_domain_signature," +
                "proof_context,consecutive_rounds");
        }
        foreach (AssignmentCoreOccurrence occurrence in occurrences)
        {
            AssignmentCoreIdentity identity = occurrence.Identity;
            writer.WriteLine(string.Join(',',
                iteration.ToString(CultureInfo.InvariantCulture),
                Escape(phase),
                identity.IdentityKey,
                Escape(identity.StationName),
                Escape(identity.CanonicalTrainSet),
                Escape(identity.ResourceDomainSignature),
                Escape(identity.ProofContext),
                occurrence.ConsecutiveRounds.ToString(CultureInfo.InvariantCulture)));
        }
    }

    private static string Escape(string? value)
    {
        value ??= string.Empty;
        return value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
    }
}

/// <summary>
/// 将模型构造等非 Optimize 阶段耗时独立记录，便于与 Master 和 Micro 累计时间分开汇总。
/// </summary>
public static class ValidityFirstPhaseTimingReporter
{
    public static void Append(
        int iteration,
        string phase,
        string category,
        double runtimeSeconds,
        double elapsedGlobalSeconds,
        string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        Directory.CreateDirectory(outputDirectory);
        string path = Path.Combine(outputDirectory, "validity_first_phase_timings.csv");
        bool writeHeader = !File.Exists(path);
        using StreamWriter writer = new(path, true, new UTF8Encoding(false));
        if (writeHeader)
        {
            writer.WriteLine("iteration,phase,category,runtime_seconds,elapsed_global_seconds");
        }
        writer.WriteLine(string.Join(',',
            iteration.ToString(CultureInfo.InvariantCulture),
            phase,
            category,
            runtimeSeconds.ToString("R", CultureInfo.InvariantCulture),
            elapsedGlobalSeconds.ToString("R", CultureInfo.InvariantCulture)));
    }
}
