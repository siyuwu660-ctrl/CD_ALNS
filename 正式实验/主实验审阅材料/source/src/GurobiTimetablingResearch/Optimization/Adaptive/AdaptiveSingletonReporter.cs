// 自适应报告器把每轮候选、质量指标、接受结果和终止原因序列化为稳定的诊断表。
// 输出按层级和扫描轮次组织，使实验能够追踪单个结构候选如何改变冲突数、目标值与累计预算。
using System.Globalization;
using System.Text;

namespace GurobiTimetablingResearch.Optimization;

internal sealed record AdaptiveSingletonSweepDiagnostic(
    int AcceptedTransitionIndex,
    int SweepIndex,
    bool Accepted,
    int RootConflictsBefore,
    int RootConflictsAfter,
    int Gain,
    double SweepRuntimeSeconds,
    double? RuntimePerRemovedConflict,
    int DirectFeasibleCount,
    int ProvenInfeasibleCount,
    int UnknownCount,
    int StrictImprovingCount,
    bool IisTriggered,
    bool ExpandedPoolTriggered,
    double StepTotalRetimingSeconds,
    double InitialTotalRetimingSeconds,
    double StepMaximumRetimingSeconds,
    double InitialMaximumRetimingSeconds,
    double IncrementalRunningSupplementSeconds,
    double CurrentTotalRunningSupplementSeconds,
    int CurrentSupplementSectionCount,
    double RollingMeanGain,
    double RollingMedianRuntimePerRemovedConflict,
    int RollingTotalGain,
    string AcceptedStateFingerprintBefore,
    string AcceptedStateFingerprintAfter,
    string ConflictFingerprintBefore,
    string ConflictFingerprintAfter,
    string CandidateUniverseFingerprint,
    string SearchConfigurationFingerprint,
    int CandidateUniverseSize,
    int InitialCandidateCount,
    int ExpandedCandidateCount,
    int EvaluatedCandidateCount,
    int SkippedCandidateCount,
    string TerminationReason,
    string AcceptedCandidateId = "",
    string AcceptedLayer = "",
    int BudgetRejectedCandidateCount = 0,
    int EpisodeConflictsBefore = 0,
    int EpisodeConflictsAfter = 0,
    int ExactConflictsBefore = 0,
    int ExactConflictsAfter = 0,
    int AssignmentConflictsBefore = 0,
    int AssignmentConflictsAfter = 0,
    int ResolvedConflictCount = 0,
    int IntroducedConflictCount = 0,
    int PersistentConflictCount = 0,
    string InitialDirectOutcome = "",
    string InitialIisOutcome = "",
    string ExpandedDirectOutcome = "",
    string ExpandedIisOutcome = "",
    string CheckpointPath = "",
    string InitialCandidatePoolFingerprint = "",
    string ExpandedCandidatePoolFingerprint = "",
    string EvaluatedCandidateIds = "",
    string SkippedCandidateIds = "",
    string UnknownCandidateIds = "",
    string QualityBlockedCandidateIds = "");

internal static class AdaptiveSingletonReporter
{
    private const string SweepHeader =
        "accepted_transition_index,sweep_index,accepted,root_conflicts_before,root_conflicts_after,gain,sweep_runtime_seconds,runtime_per_removed_conflict,direct_feasible_count,proven_infeasible_count,unknown_count,strict_improving_count,iis_triggered,expanded_pool_triggered,step_total_retiming_seconds,initial_total_retiming_seconds,step_maximum_retiming_seconds,initial_maximum_retiming_seconds,incremental_running_supplement_seconds,current_total_running_supplement_seconds,current_supplement_section_count,rolling_mean_gain,rolling_median_runtime_per_removed_conflict,rolling_total_gain,accepted_state_fingerprint_before,accepted_state_fingerprint_after,conflict_fingerprint_before,conflict_fingerprint_after,candidate_universe_fingerprint,search_configuration_fingerprint,candidate_universe_size,initial_candidate_count,expanded_candidate_count,evaluated_candidate_count,skipped_candidate_count,budget_rejected_candidate_count,accepted_candidate_id,accepted_layer,termination_reason,stagnation_scope";

    internal static void Write(
        IReadOnlyList<AdaptiveSingletonSweepDiagnostic> diagnostics,
        IReadOnlyList<AdaptiveUnknownEvidence> unknownEvidence,
        string outputDirectory)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(unknownEvidence);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        Directory.CreateDirectory(outputDirectory);
        WriteSweepsCsv(diagnostics, Path.Combine(outputDirectory, "adaptive_singleton_sweeps.csv"));
        WriteSweepsMarkdown(diagnostics, Path.Combine(outputDirectory, "adaptive_singleton_sweeps.md"));
        WriteUnknownCsv(unknownEvidence,
            Path.Combine(outputDirectory, "adaptive_singleton_unknown_evidence.csv"));
        WriteUnknownMarkdown(unknownEvidence,
            Path.Combine(outputDirectory, "adaptive_singleton_unknown_evidence.md"));
        WriteQualityCsv(diagnostics, Path.Combine(outputDirectory, "adaptive_singleton_quality.csv"));
        WriteCandidatePoolsCsv(diagnostics,
            Path.Combine(outputDirectory, "adaptive_singleton_candidate_pools.csv"));
    }

    private static void WriteSweepsCsv(
        IReadOnlyList<AdaptiveSingletonSweepDiagnostic> diagnostics,
        string path)
    {
        using StreamWriter writer = new(path, false, new UTF8Encoding(true));
        writer.WriteLine(SweepHeader);
        foreach (AdaptiveSingletonSweepDiagnostic item in diagnostics.OrderBy(item => item.SweepIndex))
        {
            writer.WriteLine(string.Join(',',
                item.AcceptedTransitionIndex,
                item.SweepIndex,
                item.Accepted,
                item.RootConflictsBefore,
                item.RootConflictsAfter,
                item.Gain,
                F(item.SweepRuntimeSeconds),
                item.RuntimePerRemovedConflict.HasValue ? F(item.RuntimePerRemovedConflict.Value) : string.Empty,
                item.DirectFeasibleCount,
                item.ProvenInfeasibleCount,
                item.UnknownCount,
                item.StrictImprovingCount,
                item.IisTriggered,
                item.ExpandedPoolTriggered,
                F(item.StepTotalRetimingSeconds),
                F(item.InitialTotalRetimingSeconds),
                F(item.StepMaximumRetimingSeconds),
                F(item.InitialMaximumRetimingSeconds),
                F(item.IncrementalRunningSupplementSeconds),
                F(item.CurrentTotalRunningSupplementSeconds),
                item.CurrentSupplementSectionCount,
                F(item.RollingMeanGain),
                F(item.RollingMedianRuntimePerRemovedConflict),
                item.RollingTotalGain,
                Csv(item.AcceptedStateFingerprintBefore),
                Csv(item.AcceptedStateFingerprintAfter),
                Csv(item.ConflictFingerprintBefore),
                Csv(item.ConflictFingerprintAfter),
                Csv(item.CandidateUniverseFingerprint),
                Csv(item.SearchConfigurationFingerprint),
                item.CandidateUniverseSize,
                item.InitialCandidateCount,
                item.ExpandedCandidateCount,
                item.EvaluatedCandidateCount,
                item.SkippedCandidateCount,
                item.BudgetRejectedCandidateCount,
                Csv(item.AcceptedCandidateId),
                Csv(item.AcceptedLayer),
                Csv(item.TerminationReason),
                "configured_candidate_policy"));
        }
    }

    private static void WriteSweepsMarkdown(
        IReadOnlyList<AdaptiveSingletonSweepDiagnostic> diagnostics,
        string path)
    {
        StringBuilder builder = new();
        builder.AppendLine("# Adaptive Singleton Convergence");
        builder.AppendLine();
        builder.AppendLine("- stagnation_scope: `configured_candidate_policy`");
        builder.AppendLine("- runtime_budget_semantics: `current_invocation_wall_clock`");
        builder.AppendLine($"- sweep_count: `{diagnostics.Count}`");
        builder.AppendLine();
        builder.AppendLine("| sweep | transition | accepted | root | gain | direct/P/I/U | IIS | expanded | step retiming | initial retiming | supplement delta/current | runtime/gain | termination |");
        builder.AppendLine("|---:|---:|:---:|---:|---:|---|:---:|:---:|---:|---:|---:|---:|---|");
        foreach (AdaptiveSingletonSweepDiagnostic item in diagnostics.OrderBy(item => item.SweepIndex))
        {
            builder.AppendLine($"| {item.SweepIndex} | {item.AcceptedTransitionIndex} | {item.Accepted} | {item.RootConflictsBefore}->{item.RootConflictsAfter} | {item.Gain} | {item.DirectFeasibleCount}/{item.ProvenInfeasibleCount}/{item.StrictImprovingCount}/{item.UnknownCount} | {item.IisTriggered} | {item.ExpandedPoolTriggered} | {F(item.StepTotalRetimingSeconds)}/{F(item.StepMaximumRetimingSeconds)} | {F(item.InitialTotalRetimingSeconds)}/{F(item.InitialMaximumRetimingSeconds)} | {F(item.IncrementalRunningSupplementSeconds)}/{F(item.CurrentTotalRunningSupplementSeconds)} | {(item.RuntimePerRemovedConflict.HasValue ? F(item.RuntimePerRemovedConflict.Value) : "-")} | `{item.TerminationReason}` |");
        }
        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(true));
    }

    private static void WriteUnknownCsv(IReadOnlyList<AdaptiveUnknownEvidence> evidence, string path)
    {
        using StreamWriter writer = new(path, false, new UTF8Encoding(true));
        writer.WriteLine("candidate_id,layer,source,solver_status,expansion_round,incomplete");
        foreach (AdaptiveUnknownEvidence item in evidence)
        {
            writer.WriteLine(string.Join(',',
                Csv(item.CandidateId), Csv(item.Layer), Csv(item.Source), Csv(item.SolverStatus),
                item.ExpansionRound?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                item.Incomplete));
        }
    }

    private static void WriteUnknownMarkdown(IReadOnlyList<AdaptiveUnknownEvidence> evidence, string path)
    {
        StringBuilder builder = new("# Adaptive Singleton Unknown Evidence\n\n");
        builder.AppendLine($"- count: `{evidence.Count}`");
        builder.AppendLine();
        builder.AppendLine("| candidate | layer | source | status | round | incomplete |");
        builder.AppendLine("|---|---|---|---|---:|:---:|");
        foreach (AdaptiveUnknownEvidence item in evidence)
        {
            builder.AppendLine($"| `{item.CandidateId}` | `{item.Layer}` | `{item.Source}` | `{item.SolverStatus}` | {item.ExpansionRound?.ToString(CultureInfo.InvariantCulture) ?? "-"} | {item.Incomplete} |");
        }
        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(true));
    }

    private static void WriteQualityCsv(
        IReadOnlyList<AdaptiveSingletonSweepDiagnostic> diagnostics,
        string path)
    {
        using StreamWriter writer = new(path, false, new UTF8Encoding(true));
        writer.WriteLine("accepted_transition_index,step_total_retiming_seconds,initial_total_retiming_seconds,step_maximum_retiming_seconds,initial_maximum_retiming_seconds,incremental_running_supplement_seconds,current_total_running_supplement_seconds,current_supplement_section_count");
        foreach (AdaptiveSingletonSweepDiagnostic item in diagnostics.Where(item => item.Accepted))
        {
            writer.WriteLine(string.Join(',', item.AcceptedTransitionIndex,
                F(item.StepTotalRetimingSeconds), F(item.InitialTotalRetimingSeconds),
                F(item.StepMaximumRetimingSeconds), F(item.InitialMaximumRetimingSeconds),
                F(item.IncrementalRunningSupplementSeconds),
                F(item.CurrentTotalRunningSupplementSeconds), item.CurrentSupplementSectionCount));
        }
    }

    private static void WriteCandidatePoolsCsv(
        IReadOnlyList<AdaptiveSingletonSweepDiagnostic> diagnostics,
        string path)
    {
        using StreamWriter writer = new(path, false, new UTF8Encoding(true));
        writer.WriteLine("sweep_index,accepted_state_fingerprint,candidate_universe_fingerprint,search_configuration_fingerprint,candidate_universe_size,initial_candidate_count,expanded_candidate_count,evaluated_candidate_count,skipped_candidate_count");
        foreach (AdaptiveSingletonSweepDiagnostic item in diagnostics)
        {
            writer.WriteLine(string.Join(',', item.SweepIndex,
                Csv(item.AcceptedStateFingerprintBefore), Csv(item.CandidateUniverseFingerprint),
                Csv(item.SearchConfigurationFingerprint), item.CandidateUniverseSize,
                item.InitialCandidateCount, item.ExpandedCandidateCount,
                item.EvaluatedCandidateCount, item.SkippedCandidateCount));
        }
    }

    private static string F(double value) => value.ToString("F6", CultureInfo.InvariantCulture);

    private static string Csv(string? value)
    {
        string text = value ?? string.Empty;
        return text.IndexOfAny([',', '"', '\r', '\n']) < 0
            ? text
            : $"\"{text.Replace("\"", "\"\"")}\"";
    }
}
