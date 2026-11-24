// 受控候选修复定义单个指定结构候选的执行结果及报告格式，用于可重复地隔离验证某条候选边。
// 结果同时保存修复前后快照、微观报告和依赖释放信息，便于确认改善确由该候选及其受控邻域产生。
using System.Globalization;
using System.Text;

namespace GurobiTimetablingResearch.Optimization;

internal sealed record ControlledCandidateRepairResult(
    string CandidateId,
    StructuralCandidateProbeOutcome InitialOutcome,
    string InitialSolverStatus,
    double InitialRuntimeSeconds,
    int InitialSolutionCount,
    int NeighborhoodLevel,
    string NeighborhoodMode,
    int TemporalWindowSeconds,
    double RepairSupplementRatio,
    string BaselineSnapshotIdentity,
    int BaselineSectionEdgeCount,
    int BaselineStationEventEdgeCount,
    bool DependencyClosureFeasible,
    bool? MicroAccepted,
    string TerminationReason,
    MicroFeasibilityReport MicroBefore,
    MicroFeasibilityReport? MicroAfter,
    MasterValidationReport BaselineValidation,
    MasterValidationReport FinalValidation,
    IReadOnlyList<StructuralTrialIisDiagnostic> IisDiagnostics,
    IReadOnlyList<IisGuidedRepairTraceDiagnostic> RepairTrace,
    IReadOnlyList<IisDependencyPlanDiagnostic> DependencyPlans,
    BasicMilpModel? CandidateModel) : IDisposable
{
    public void Dispose() => CandidateModel?.Dispose();
}

internal static class ControlledCandidateRepairReporter
{
    internal static void Write(ControlledCandidateRepairResult result, string outputDirectory)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        Directory.CreateDirectory(outputDirectory);

        WriteSummaryCsv(result, Path.Combine(outputDirectory, "controlled_candidate_summary.csv"));
        WriteSummaryMarkdown(result, Path.Combine(outputDirectory, "controlled_candidate_summary.md"));
        IisGuidedRepairDiagnosticsReporter.Write(
            result.RepairTrace,
            result.DependencyPlans,
            outputDirectory);
        StructuralTrialIisDiagnosticsReporter.Write(result.IisDiagnostics, outputDirectory);
        WritePerRoundIis(result.IisDiagnostics, outputDirectory);
        WriteMicroConflictSummary(result, outputDirectory);

        MasterSolutionValidator validator = new();
        validator.WriteReport(
            result.FinalValidation,
            Path.Combine(outputDirectory, "final_master_validation.md"));
    }

    private static void WriteSummaryCsv(ControlledCandidateRepairResult result, string path)
    {
        using StreamWriter writer = new(path, false, new UTF8Encoding(true));
        writer.WriteLine("candidate_id,initial_outcome,initial_solver_status,initial_runtime,initial_solution_count,neighborhood_level,neighborhood_mode,temporal_window_seconds,repair_supplement_ratio,baseline_snapshot_identity,baseline_section_edge_count,baseline_station_event_edge_count,baseline_master_validator_feasible,final_master_validator_feasible,final_master_violation_count,dependency_closure_feasible,micro_accepted,termination_reason,iis_element_count,expansion_round_count");
        writer.WriteLine(string.Join(',',
            Escape(result.CandidateId),
            result.InitialOutcome,
            Escape(result.InitialSolverStatus),
            result.InitialRuntimeSeconds.ToString("F6", CultureInfo.InvariantCulture),
            result.InitialSolutionCount,
            result.NeighborhoodLevel,
            Escape(result.NeighborhoodMode),
            result.TemporalWindowSeconds,
            result.RepairSupplementRatio.ToString("F6", CultureInfo.InvariantCulture),
            Escape(result.BaselineSnapshotIdentity),
            result.BaselineSectionEdgeCount,
            result.BaselineStationEventEdgeCount,
            result.BaselineValidation.IsFeasible,
            result.FinalValidation.IsFeasible,
            result.FinalValidation.Violations.Count,
            result.DependencyClosureFeasible,
            result.MicroAccepted?.ToString() ?? string.Empty,
            Escape(result.TerminationReason),
            result.IisDiagnostics.Count,
            result.RepairTrace.Count));
    }

    private static void WriteSummaryMarkdown(ControlledCandidateRepairResult result, string path)
    {
        StringBuilder builder = new();
        builder.AppendLine("# Controlled Candidate Repair");
        builder.AppendLine();
        builder.AppendLine($"- candidate_id: `{result.CandidateId}`");
        builder.AppendLine($"- baseline_snapshot_identity: `{result.BaselineSnapshotIdentity}`");
        builder.AppendLine($"- structural_state: `accepted baseline ({result.BaselineSectionEdgeCount} section + {result.BaselineStationEventEdgeCount} station-event) + specified candidate only`");
        builder.AppendLine($"- baseline_master_validator_feasible: `{result.BaselineValidation.IsFeasible}`");
        builder.AppendLine($"- final_master_validator_feasible: `{result.FinalValidation.IsFeasible}` ({result.FinalValidation.Violations.Count} violations)");
        builder.AppendLine($"- initial_attempt: `{result.InitialOutcome}` / `{result.InitialSolverStatus}` / `{result.InitialRuntimeSeconds:F3}s`");
        builder.AppendLine($"- repair_configuration: `FlexibleStop`, level `{result.NeighborhoodLevel}` (`{result.NeighborhoodMode}`), window `{result.TemporalWindowSeconds}s`, budget `40s`, supplement `{result.RepairSupplementRatio:F3}`");
        builder.AppendLine($"- dependency_closure_feasible: `{result.DependencyClosureFeasible}`");
        builder.AppendLine($"- micro_accepted: `{result.MicroAccepted?.ToString() ?? "not_run"}`");
        builder.AppendLine($"- termination_reason: `{result.TerminationReason}`");
        builder.AppendLine($"- IIS_element_count: `{result.IisDiagnostics.Count}`");
        builder.AppendLine($"- expansion_round_count: `{result.RepairTrace.Count}`");
        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(true));
    }

    private static void WritePerRoundIis(
        IReadOnlyList<StructuralTrialIisDiagnostic> diagnostics,
        string outputDirectory)
    {
        foreach (IGrouping<string, StructuralTrialIisDiagnostic> group in diagnostics
                     .GroupBy(item => item.Context.ProbeId, StringComparer.OrdinalIgnoreCase))
        {
            string safeProbeId = string.Concat(group.Key.Select(character =>
                Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
            string roundDirectory = Path.Combine(outputDirectory, "iis_rounds", safeProbeId);
            StructuralTrialIisDiagnosticsReporter.Write(group.ToList(), roundDirectory);
        }
    }

    private static void WriteMicroConflictSummary(
        ControlledCandidateRepairResult result,
        string outputDirectory)
    {
        MicroConflictMetrics before = MicroConflictMetricCalculator.Calculate(result.MicroBefore.Conflicts);
        MicroConflictMetrics? after = result.MicroAfter is null
            ? null
            : MicroConflictMetricCalculator.Calculate(result.MicroAfter.Conflicts);
        string csvPath = Path.Combine(outputDirectory, "micro_before_after_conflict_summary.csv");
        using (StreamWriter writer = new(csvPath, false, new UTF8Encoding(true)))
        {
            writer.WriteLine("stage,status,raw_conflicts,canonical_conflicts,root_conflicts,episode_conflicts,conflicted_stations,affected_trains,connected_components");
            WriteMicroCsvRow(writer, "before", result.MicroBefore.Status.ToString(), before);
            if (after is not null)
            {
                WriteMicroCsvRow(writer, "after", result.MicroAfter!.Status.ToString(), after);
            }
        }

        StringBuilder markdown = new();
        markdown.AppendLine("# Global Micro Before/After Conflict Summary");
        markdown.AppendLine();
        markdown.AppendLine("| stage | status | raw | canonical | root | episodes | stations | trains | components |");
        markdown.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|");
        AppendMicroMarkdownRow(markdown, "before", result.MicroBefore.Status.ToString(), before);
        if (after is not null)
        {
            AppendMicroMarkdownRow(markdown, "after", result.MicroAfter!.Status.ToString(), after);
        }
        File.WriteAllText(
            Path.Combine(outputDirectory, "micro_before_after_conflict_summary.md"),
            markdown.ToString(),
            new UTF8Encoding(true));
    }

    private static void WriteMicroCsvRow(
        TextWriter writer,
        string stage,
        string status,
        MicroConflictMetrics metrics) => writer.WriteLine(string.Join(',',
            stage, Escape(status), metrics.RawConflictCount, metrics.CanonicalConflictCount,
            metrics.RootConflictCount, metrics.EpisodeConflictCount, metrics.ConflictedStationCount,
            metrics.AffectedTrainCount, metrics.ConnectedComponentCount));

    private static void AppendMicroMarkdownRow(
        StringBuilder builder,
        string stage,
        string status,
        MicroConflictMetrics metrics) => builder.AppendLine(
            $"| {stage} | `{status}` | {metrics.RawConflictCount} | {metrics.CanonicalConflictCount} | {metrics.RootConflictCount} | {metrics.EpisodeConflictCount} | {metrics.ConflictedStationCount} | {metrics.AffectedTrainCount} | {metrics.ConnectedComponentCount} |");

    private static string Escape(string value) =>
        !value.Contains(',') && !value.Contains('"') && !value.Contains('\n') && !value.Contains('\r')
            ? value
            : $"\"{value.Replace("\"", "\"\"")}\"";
}
