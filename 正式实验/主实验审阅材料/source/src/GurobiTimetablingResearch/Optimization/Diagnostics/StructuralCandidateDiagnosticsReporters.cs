// 结构候选诊断报告器分别序列化失败试验 IIS 和候选可行性探测结果。
// 输出通过候选规范键、模型阶段及阻塞元素关联多张诊断表，支持逐候选分析不可行根因。
using System.Globalization;
using System.Text;

namespace GurobiTimetablingResearch.Optimization;

internal static class StructuralTrialIisDiagnosticsReporter
{
    internal static void Write(
        IReadOnlyList<StructuralTrialIisDiagnostic> diagnostics,
        string outputDirectory)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        Directory.CreateDirectory(outputDirectory);
        WriteCsv(diagnostics, Path.Combine(outputDirectory, "structural_failed_trial_iis.csv"));
        WriteMarkdown(diagnostics, Path.Combine(outputDirectory, "structural_failed_trial_iis.md"));
    }

    private static void WriteCsv(IReadOnlyList<StructuralTrialIisDiagnostic> diagnostics, string path)
    {
        using StreamWriter writer = new(path, false, new UTF8Encoding(true));
        writer.WriteLine("iteration_index,batch_id,neighborhood_level,decision_level,window_seconds,source,probe_id,element_kind,element_name,constraint_family,structural_candidate_ids,train_indices,station_name,resource_key,tested_candidate_ids,element_id,blocker_kind,variable_key,section,event_type,bound_source,snapshot_value,current_lb,current_ub,directed_first_train,directed_second_train,directed_first_event,directed_second_event");
        foreach (StructuralTrialIisDiagnostic diagnostic in diagnostics)
        {
            StructuralTrialIisContext context = diagnostic.Context;
            StructuralIisElement element = diagnostic.Element;
            writer.WriteLine(string.Join(',',
                context.IterationIndex.ToString(CultureInfo.InvariantCulture),
                Escape(context.BatchId),
                context.NeighborhoodLevel.ToString(CultureInfo.InvariantCulture),
                context.DecisionLevel,
                context.WindowSeconds.ToString(CultureInfo.InvariantCulture),
                Escape(context.Source),
                Escape(context.ProbeId),
                element.ElementKind,
                Escape(element.ElementName),
                Escape(element.ConstraintFamily),
                Escape(string.Join('|', element.StructuralCandidateIds)),
                Escape(string.Join('|', element.TrainIndices)),
                Escape(element.StationName),
                Escape(element.ResourceKey),
                Escape(string.Join('|', context.TestedCandidateIds)),
                Escape($"{context.Source}::{context.ProbeId}::{element.ElementKind}::{element.ElementName}"),
                element.BlockerKind,
                Escape(element.VariableKey),
                Escape(element.SectionName),
                Escape(element.EventType),
                element.BoundSource,
                FormatNullable(element.SnapshotValue),
                FormatNullable(element.CurrentLowerBound),
                FormatNullable(element.CurrentUpperBound),
                element.DirectedFirstTrainIndex?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                element.DirectedSecondTrainIndex?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                element.DirectedFirstEventSide?.ToString() ?? string.Empty,
                element.DirectedSecondEventSide?.ToString() ?? string.Empty));
        }
    }

    private static void WriteMarkdown(IReadOnlyList<StructuralTrialIisDiagnostic> diagnostics, string path)
    {
        StringBuilder builder = new();
        builder.AppendLine("# Structural Failed-Trial IIS");
        builder.AppendLine();
        builder.AppendLine("| source | probe | level | decision | window | kind/blocker | family | element/key | candidates | trains/direction | station/resource | bound source/value/LB/UB |");
        builder.AppendLine("|---|---|---:|---|---:|---|---|---|---|---|---|---|");
        foreach (StructuralTrialIisDiagnostic diagnostic in diagnostics)
        {
            StructuralTrialIisContext context = diagnostic.Context;
            StructuralIisElement element = diagnostic.Element;
            builder.AppendLine($"| `{context.Source}` | `{context.ProbeId}` | {context.NeighborhoodLevel} | {context.DecisionLevel} | {context.WindowSeconds} | {element.ElementKind}/{element.BlockerKind} | `{element.ConstraintFamily}` | `{element.ElementName}` / `{element.VariableKey}` | `{string.Join(";", element.StructuralCandidateIds)}` | {string.Join(";", element.TrainIndices)} / {element.DirectedFirstTrainIndex}->{element.DirectedSecondTrainIndex} | `{element.StationName}/{element.ResourceKey}` | {element.BoundSource}/{FormatNullable(element.SnapshotValue)}/{FormatNullable(element.CurrentLowerBound)}/{FormatNullable(element.CurrentUpperBound)} |");
        }
        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(true));
    }

    private static string Escape(string value)
    {
        return !value.Contains(',') && !value.Contains('"') && !value.Contains('\n') && !value.Contains('\r')
            ? value
            : $"\"{value.Replace("\"", "\"\"")}\"";
    }

    private static string FormatNullable(double? value) =>
        value?.ToString("F6", CultureInfo.InvariantCulture) ?? string.Empty;
}

internal static class StructuralCandidateFeasibilityDiagnosticsReporter
{
    internal static void Write(
        IReadOnlyList<StructuralCandidateProbeDiagnostic> diagnostics,
        string stopReason,
        string outputDirectory)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(stopReason);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        Directory.CreateDirectory(outputDirectory);
        WriteCsv(diagnostics, stopReason, Path.Combine(outputDirectory, "structural_candidate_feasibility.csv"));
        WriteMarkdown(diagnostics, stopReason, Path.Combine(outputDirectory, "structural_candidate_feasibility.md"));
    }

    private static void WriteCsv(
        IReadOnlyList<StructuralCandidateProbeDiagnostic> diagnostics,
        string stopReason,
        string path)
    {
        using StreamWriter writer = new(path, false, new UTF8Encoding(true));
        writer.WriteLine("candidate_index,candidate_id,candidate_type,train_i,train_j,station,section,event_i,event_j,micro_classification,realized_gap,required_gap,headway_deficit,probe_id,mode,probe_status,solver_status,runtime,solution_count,time_limit_seconds,candidate_count,candidate_ids,iis_generated,iis_element_count,stop_reason");
        for (int index = 0; index < diagnostics.Count; index++)
        {
            StructuralCandidateProbeDiagnostic diagnostic = diagnostics[index];
            HeadwayConflictCandidate? singleton = diagnostic.Request.Candidates.Count == 1
                ? diagnostic.Request.Candidates[0]
                : null;
            StructuralCandidateEvidence? evidence = singleton?.Evidence;
            writer.WriteLine(string.Join(',',
                singleton is null ? string.Empty : (index + 1).ToString(CultureInfo.InvariantCulture),
                Escape(singleton?.CandidateKey ?? string.Empty),
                Escape(evidence?.CandidateType ?? string.Empty),
                singleton?.FirstTrainIndex.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                singleton?.SecondTrainIndex.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                Escape(evidence?.StationName ?? singleton?.StationEdge?.StationName ?? string.Empty),
                Escape(evidence?.SectionName ?? singleton?.SectionEdge?.SectionName ?? string.Empty),
                Escape(evidence?.FirstEvent ?? string.Empty),
                Escape(evidence?.SecondEvent ?? string.Empty),
                Escape(evidence?.MicroClassification ?? string.Empty),
                FormatNullable(evidence?.RealizedGapSeconds),
                FormatNullable(evidence?.RequiredGapSeconds),
                FormatNullable(evidence?.HeadwayDeficitSeconds),
                Escape(diagnostic.Request.ProbeId),
                diagnostic.Request.Mode,
                diagnostic.Execution.Outcome,
                Escape(diagnostic.Execution.SolverStatusName),
                diagnostic.Execution.RuntimeSeconds.ToString("F6", CultureInfo.InvariantCulture),
                diagnostic.Execution.SolutionCount.ToString(CultureInfo.InvariantCulture),
                diagnostic.Request.TimeLimitSeconds.ToString("F1", CultureInfo.InvariantCulture),
                diagnostic.Request.Candidates.Count.ToString(CultureInfo.InvariantCulture),
                Escape(string.Join('|', diagnostic.Request.Candidates.Select(candidate => candidate.CandidateKey))),
                (diagnostic.Execution.IisDiagnostics.Count > 0).ToString(CultureInfo.InvariantCulture),
                diagnostic.Execution.IisDiagnostics.Count.ToString(CultureInfo.InvariantCulture),
                Escape(stopReason)));
        }
    }

    private static void WriteMarkdown(
        IReadOnlyList<StructuralCandidateProbeDiagnostic> diagnostics,
        string stopReason,
        string path)
    {
        StringBuilder builder = new();
        builder.AppendLine("# Structural Candidate Feasibility");
        builder.AppendLine();
        builder.AppendLine($"Stop reason: `{stopReason}`");
        builder.AppendLine();
        builder.AppendLine("| probe | mode | outcome | status | runtime | sol | candidates | IIS rows |");
        builder.AppendLine("|---|---|---|---|---:|---:|---|---:|");
        foreach (StructuralCandidateProbeDiagnostic diagnostic in diagnostics)
        {
            builder.AppendLine($"| `{diagnostic.Request.ProbeId}` | {diagnostic.Request.Mode} | **{diagnostic.Execution.Outcome}** | `{diagnostic.Execution.SolverStatusName}` | {diagnostic.Execution.RuntimeSeconds:F3} | {diagnostic.Execution.SolutionCount} | `{string.Join(";", diagnostic.Request.Candidates.Select(candidate => candidate.CandidateKey))}` | {diagnostic.Execution.IisDiagnostics.Count} |");
        }
        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(true));
    }

    private static string Escape(string value)
    {
        return !value.Contains(',') && !value.Contains('"') && !value.Contains('\n') && !value.Contains('\r')
            ? value
            : $"\"{value.Replace("\"", "\"\"")}\"";
    }

    private static string FormatNullable(double? value) =>
        value?.ToString("F3", CultureInfo.InvariantCulture) ?? string.Empty;
}
