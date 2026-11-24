// 分层修复诊断记录各决策释放层的变量数量、模型状态、冲突指标和选定结果。
// 报告用于比较仅释放先后关系、进一步释放时间或停站决策时的可行性变化，定位真正需要的自由度。
using System.Globalization;
using System.Text;

namespace GurobiTimetablingResearch.Optimization;

public sealed record HierarchicalRepairDiagnostic(
    int IterationIndex,
    int AttemptIndex,
    RepairDecisionLevel DecisionLevel,
    int ReleasedTrainCount,
    int? AffectedMinimumStationOrder,
    int? AffectedMaximumStationOrder,
    int ReleasedTimeVariableCount,
    int ReleasedRunningSupplementVariableCount,
    int ReleasedSectionPrecedenceVariableCount,
    int ReleasedStationEventPrecedenceVariableCount,
    int ReleasedStopVariableCount,
    double RepairRunningTimeSupplementRatio,
    double TotalSupplementBefore,
    double? TotalSupplementAfter,
    double? MaximumSupplementAfter,
    int? SectionsUsingSupplementAfter,
    int? SolverStatusCode,
    string? SolverStatusName,
    double? RuntimeSeconds,
    int? SolutionCount,
    int TotalConflictsBefore,
    int ExactConflictsBefore,
    int AssignmentConflictsBefore,
    int? TotalConflictsAfter,
    int? ExactConflictsAfter,
    int? AssignmentConflictsAfter,
    bool? Accepted,
    string? RejectionReason);

public static class HierarchicalRepairDiagnosticsReporter
{
    public static void WriteFromRecourseStages(
        IReadOnlyList<CheckerRecourseStageDiagnostic> stages,
        string outputDirectory)
    {
        ArgumentNullException.ThrowIfNull(stages);
        Dictionary<int, int> attemptsByIteration = [];
        List<HierarchicalRepairDiagnostic> diagnostics = [];
        foreach (CheckerRecourseStageDiagnostic stage in stages)
        {
            if (!stage.RepairDecisionLevel.HasValue)
            {
                continue;
            }
            int attemptIndex = attemptsByIteration.GetValueOrDefault(stage.IterationIndex) + 1;
            attemptsByIteration[stage.IterationIndex] = attemptIndex;
            diagnostics.Add(new HierarchicalRepairDiagnostic(
                stage.IterationIndex,
                attemptIndex,
                stage.RepairDecisionLevel.Value,
                stage.ReleasedTrainCount,
                stage.AffectedMinimumStationOrder,
                stage.AffectedMaximumStationOrder,
                stage.ReleasedTimeVariableCount,
                stage.ReleasedRunningSupplementVariableCount,
                stage.ReleasedSectionPrecedenceVariableCount,
                stage.ReleasedStationEventPrecedenceVariableCount,
                stage.ReleasedStopVariableCount,
                stage.RepairRunningTimeSupplementRatio,
                stage.TotalSupplementBefore,
                stage.TotalSupplementAfter,
                stage.MaximumSupplementAfter,
                stage.SectionsUsingSupplementAfter,
                stage.SolverStatusCode < 0 ? null : stage.SolverStatusCode,
                stage.SolverStatusCode < 0 ? null : stage.SolverStatusName,
                stage.SolverStatusCode < 0 ? null : stage.RuntimeSeconds,
                stage.SolverStatusCode < 0 ? null : stage.SolutionCount,
                stage.MicroConflictsBefore ?? 0,
                stage.ExactConflictsBefore,
                stage.AssignmentConflictsBefore,
                stage.MicroConflictsAfter,
                stage.ExactConflictsAfter,
                stage.AssignmentConflictsAfter,
                stage.SolverStatusCode < 0 ? null : stage.Accepted,
                stage.RejectionReason ?? stage.Outcome));
        }
        Write(diagnostics, outputDirectory);
    }

    public static void Write(
        IReadOnlyList<HierarchicalRepairDiagnostic> diagnostics,
        string outputDirectory)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        Directory.CreateDirectory(outputDirectory);
        WriteCsv(diagnostics, Path.Combine(outputDirectory, "hierarchical_repair_diagnostics.csv"));
        WriteMarkdown(diagnostics, Path.Combine(outputDirectory, "hierarchical_repair_diagnostics.md"));
    }

    private static void WriteCsv(
        IReadOnlyList<HierarchicalRepairDiagnostic> diagnostics,
        string path)
    {
        StringBuilder builder = new();
        builder.AppendLine(
            "checker_iteration,attempt_index,repair_decision_level,released_train_count," +
            "affected_minimum_station_order,affected_maximum_station_order,released_time_variable_count," +
            "released_running_supplement_variable_count,released_section_precedence_variable_count," +
            "released_station_event_precedence_variable_count,released_stop_variable_count," +
            "repair_running_time_supplement_ratio,total_supplement_before,total_supplement_after," +
            "maximum_supplement_after,sections_using_supplement_after,solver_status_code,solver_status_name," +
            "runtime_seconds,solution_count,total_conflicts_before,exact_conflicts_before," +
            "assignment_conflicts_before,total_conflicts_after,exact_conflicts_after," +
            "assignment_conflicts_after,accepted,rejection_reason");
        foreach (HierarchicalRepairDiagnostic item in diagnostics)
        {
            builder.Append(item.IterationIndex).Append(',')
                .Append(item.AttemptIndex).Append(',')
                .Append(item.DecisionLevel).Append(',')
                .Append(item.ReleasedTrainCount).Append(',')
                .Append(Format(item.AffectedMinimumStationOrder)).Append(',')
                .Append(Format(item.AffectedMaximumStationOrder)).Append(',')
                .Append(item.ReleasedTimeVariableCount).Append(',')
                .Append(item.ReleasedRunningSupplementVariableCount).Append(',')
                .Append(item.ReleasedSectionPrecedenceVariableCount).Append(',')
                .Append(item.ReleasedStationEventPrecedenceVariableCount).Append(',')
                .Append(item.ReleasedStopVariableCount).Append(',')
                .Append(Format(item.RepairRunningTimeSupplementRatio)).Append(',')
                .Append(Format(item.TotalSupplementBefore)).Append(',')
                .Append(Format(item.TotalSupplementAfter)).Append(',')
                .Append(Format(item.MaximumSupplementAfter)).Append(',')
                .Append(Format(item.SectionsUsingSupplementAfter)).Append(',')
                .Append(Format(item.SolverStatusCode)).Append(',')
                .Append(Escape(item.SolverStatusName)).Append(',')
                .Append(Format(item.RuntimeSeconds)).Append(',')
                .Append(Format(item.SolutionCount)).Append(',')
                .Append(item.TotalConflictsBefore).Append(',')
                .Append(item.ExactConflictsBefore).Append(',')
                .Append(item.AssignmentConflictsBefore).Append(',')
                .Append(Format(item.TotalConflictsAfter)).Append(',')
                .Append(Format(item.ExactConflictsAfter)).Append(',')
                .Append(Format(item.AssignmentConflictsAfter)).Append(',')
                .Append(item.Accepted.HasValue
                    ? item.Accepted.Value.ToString(CultureInfo.InvariantCulture)
                    : string.Empty).Append(',')
                .AppendLine(Escape(item.RejectionReason));
        }
        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(true));
    }

    private static void WriteMarkdown(
        IReadOnlyList<HierarchicalRepairDiagnostic> diagnostics,
        string path)
    {
        StringBuilder builder = new();
        builder.AppendLine("# Hierarchical Repair Diagnostics");
        builder.AppendLine();
        builder.AppendLine("| iter | attempt | level | trains | span | time | supplement | section prec | station prec | stops | status | conflicts | accepted | reason |");
        builder.AppendLine("|---:|---:|---|---:|---|---:|---:|---:|---:|---:|---|---|---|---|");
        foreach (HierarchicalRepairDiagnostic item in diagnostics)
        {
            builder.Append("| ").Append(item.IterationIndex)
                .Append(" | ").Append(item.AttemptIndex)
                .Append(" | ").Append(item.DecisionLevel)
                .Append(" | ").Append(item.ReleasedTrainCount)
                .Append(" | ").Append(Format(item.AffectedMinimumStationOrder)).Append('-')
                .Append(Format(item.AffectedMaximumStationOrder))
                .Append(" | ").Append(item.ReleasedTimeVariableCount)
                .Append(" | ").Append(item.ReleasedRunningSupplementVariableCount)
                .Append(" | ").Append(item.ReleasedSectionPrecedenceVariableCount)
                .Append(" | ").Append(item.ReleasedStationEventPrecedenceVariableCount)
                .Append(" | ").Append(item.ReleasedStopVariableCount)
                .Append(" | ").Append(item.SolverStatusName ?? string.Empty)
                .Append(" | ").Append(item.TotalConflictsBefore).Append(" -> ")
                .Append(Format(item.TotalConflictsAfter))
                .Append(" | ").Append(item.Accepted?.ToString() ?? string.Empty)
                .Append(" | ").Append(item.RejectionReason ?? string.Empty)
                .AppendLine(" |");
        }
        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(true));
    }

    private static string Format(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Format(double? value) => value.HasValue ? Format(value.Value) : string.Empty;

    private static string Format(int? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }
        return value.Contains(',') || value.Contains('"')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
    }
}
