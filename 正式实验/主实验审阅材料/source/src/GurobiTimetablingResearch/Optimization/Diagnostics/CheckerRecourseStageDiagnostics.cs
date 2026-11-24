// 补救阶段诊断把各层模型的 Gurobi 状态、incumbent、冲突变化和接受结论映射为统一记录。
// 分类策略区分可行改进、无解、超时及无有效解等终止形态，为后续升级邻域或回滚提供明确动作依据。
using System.Globalization;
using System.Text;
using Gurobi;

namespace GurobiTimetablingResearch.Optimization;

public sealed record CheckerRecourseStageDiagnostic(
    int IterationIndex,
    string BatchId,
    int WindowSeconds,
    double EffectiveTimeLimitSeconds,
    int SolverStatusCode,
    string SolverStatusName,
    double RuntimeSeconds,
    int SolutionCount,
    bool HasIncumbent,
    double? ObjectiveValue,
    double? BestBound,
    int FixedTemporalEventCount,
    int BoundedTemporalEventCount,
    string Outcome,
    RepairDecisionLevel? RepairDecisionLevel = null,
    int ReleasedTrainCount = 0,
    string ReleasedStationSpan = "",
    int ReleasedPrecedenceVariableCount = 0,
    int ReleasedStopVariableCount = 0,
    int PromotedMicroVariableCount = 0,
    int? MicroConflictsBefore = null,
    int? MicroConflictsAfter = null,
    bool Accepted = false,
    int ReleasedRunningSupplementVariableCount = 0,
    int ReleasedSectionPrecedenceVariableCount = 0,
    int ReleasedStationEventPrecedenceVariableCount = 0,
    int? AffectedMinimumStationOrder = null,
    int? AffectedMaximumStationOrder = null,
    int ReleasedTimeVariableCount = 0,
    double RepairRunningTimeSupplementRatio = 0.0,
    double TotalSupplementBefore = 0.0,
    double? TotalSupplementAfter = null,
    double? MaximumSupplementAfter = null,
    int? SectionsUsingSupplementAfter = null,
    int ExactConflictsBefore = 0,
    int AssignmentConflictsBefore = 0,
    int? ExactConflictsAfter = null,
    int? AssignmentConflictsAfter = null,
    string? RejectionReason = null);

public static class CheckerStructuralIncumbentAcceptancePolicy
{
    public static bool Accept(
        int baselineRootConflictCount,
        int candidateRootConflictCount,
        bool requireStrictImprovement) =>
        requireStrictImprovement
            ? candidateRootConflictCount < baselineRootConflictCount
            : candidateRootConflictCount <= baselineRootConflictCount;
}

public static class CheckerSolverStatusNameResolver
{
    public static string Resolve(int statusCode) => statusCode switch
    {
        GRB.Status.LOADED => "LOADED",
        GRB.Status.OPTIMAL => "OPTIMAL",
        GRB.Status.INFEASIBLE => "INFEASIBLE",
        GRB.Status.INF_OR_UNBD => "INF_OR_UNBD",
        GRB.Status.UNBOUNDED => "UNBOUNDED",
        GRB.Status.CUTOFF => "CUTOFF",
        GRB.Status.ITERATION_LIMIT => "ITERATION_LIMIT",
        GRB.Status.NODE_LIMIT => "NODE_LIMIT",
        GRB.Status.TIME_LIMIT => "TIME_LIMIT",
        GRB.Status.SOLUTION_LIMIT => "SOLUTION_LIMIT",
        GRB.Status.INTERRUPTED => "INTERRUPTED",
        GRB.Status.NUMERIC => "NUMERIC",
        GRB.Status.SUBOPTIMAL => "SUBOPTIMAL",
        GRB.Status.INPROGRESS => "INPROGRESS",
        GRB.Status.USER_OBJ_LIMIT => "USER_OBJ_LIMIT",
        GRB.Status.WORK_LIMIT => "WORK_LIMIT",
        GRB.Status.MEM_LIMIT => "MEM_LIMIT",
        _ => $"STATUS_{statusCode}"
    };
}

public static class CheckerRecourseTerminationClassifier
{
    public static string Classify(IReadOnlyList<CheckerRecourseStageDiagnostic> stages)
    {
        if (stages.Count == 0)
        {
            return "temporal_recourse_no_stage_attempted";
        }
        if (stages.All(stage => stage.SolverStatusCode == GRB.Status.INFEASIBLE))
        {
            return "temporal_recourse_proven_infeasible";
        }
        if (stages.Any(stage => stage.SolverStatusCode == GRB.Status.TIME_LIMIT && !stage.HasIncumbent))
        {
            return "temporal_recourse_unknown_budget_exhausted";
        }
        if (stages.Any(stage => stage.SolverStatusCode == GRB.Status.INF_OR_UNBD))
        {
            return "temporal_recourse_inf_or_unbd";
        }
        if (stages.Any(stage =>
                stage.HasIncumbent &&
                stage.Outcome == "rejected_no_global_conflict_improvement"))
        {
            return "temporal_recourse_incumbents_rejected_no_conflict_improvement";
        }

        return "temporal_recourse_no_incumbent";
    }
}

public static class CheckerRecourseStageDiagnosticWriter
{
    public static void Write(
        IReadOnlyList<CheckerRecourseStageDiagnostic> diagnostics,
        string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        WriteCsv(diagnostics, Path.Combine(outputDirectory, "checker_loop_recourse_stages.csv"));
        WriteMarkdown(diagnostics, Path.Combine(outputDirectory, "checker_loop_recourse_stages.md"));
        WriteRepairCsv(diagnostics, Path.Combine(outputDirectory, "repair_diagnostics.csv"));
    }

    private static void WriteRepairCsv(
        IReadOnlyList<CheckerRecourseStageDiagnostic> diagnostics,
        string path)
    {
        StringBuilder builder = new();
        builder.AppendLine("checker_iteration,repair_decision_level,released_train_count,released_station_span,affected_minimum_station_order,affected_maximum_station_order,released_time_variable_count,released_running_supplement_variable_count,released_section_precedence_variable_count,released_station_event_precedence_variable_count,released_precedence_variable_count,released_stop_variable_count,promoted_micro_variable_count,solver_status,runtime,incumbent_count,micro_conflicts_before,micro_conflicts_after,accepted");
        foreach (CheckerRecourseStageDiagnostic item in diagnostics)
        {
            builder.Append(item.IterationIndex).Append(',')
                .Append(item.RepairDecisionLevel?.ToString() ?? string.Empty).Append(',')
                .Append(item.ReleasedTrainCount).Append(',')
                .Append(Escape(item.ReleasedStationSpan)).Append(',')
                .Append(item.AffectedMinimumStationOrder?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append(',')
                .Append(item.AffectedMaximumStationOrder?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append(',')
                .Append(item.BoundedTemporalEventCount).Append(',')
                .Append(item.ReleasedRunningSupplementVariableCount).Append(',')
                .Append(item.ReleasedSectionPrecedenceVariableCount).Append(',')
                .Append(item.ReleasedStationEventPrecedenceVariableCount).Append(',')
                .Append(item.ReleasedPrecedenceVariableCount).Append(',')
                .Append(item.ReleasedStopVariableCount).Append(',')
                .Append(item.PromotedMicroVariableCount).Append(',')
                .Append(Escape(item.SolverStatusName)).Append(',')
                .Append(Format(item.RuntimeSeconds)).Append(',')
                .Append(item.SolutionCount).Append(',')
                .Append(item.MicroConflictsBefore?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append(',')
                .Append(item.MicroConflictsAfter?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append(',')
                .AppendLine(item.Accepted.ToString(CultureInfo.InvariantCulture));
        }
        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(true));
    }

    private static void WriteCsv(
        IReadOnlyList<CheckerRecourseStageDiagnostic> diagnostics,
        string path)
    {
        StringBuilder builder = new();
        builder.AppendLine("iteration_index,batch_id,window_seconds,effective_time_limit_seconds,solver_status_code,solver_status_name,runtime_seconds,solution_count,has_incumbent,objective_value,best_bound,fixed_temporal_event_count,bounded_temporal_event_count,outcome");
        foreach (CheckerRecourseStageDiagnostic item in diagnostics)
        {
            builder.Append(item.IterationIndex).Append(',')
                .Append(Escape(item.BatchId)).Append(',')
                .Append(item.WindowSeconds).Append(',')
                .Append(Format(item.EffectiveTimeLimitSeconds)).Append(',')
                .Append(item.SolverStatusCode).Append(',')
                .Append(Escape(item.SolverStatusName)).Append(',')
                .Append(Format(item.RuntimeSeconds)).Append(',')
                .Append(item.SolutionCount).Append(',')
                .Append(item.HasIncumbent).Append(',')
                .Append(item.ObjectiveValue.HasValue ? Format(item.ObjectiveValue.Value) : string.Empty).Append(',')
                .Append(item.BestBound.HasValue ? Format(item.BestBound.Value) : string.Empty).Append(',')
                .Append(item.FixedTemporalEventCount).Append(',')
                .Append(item.BoundedTemporalEventCount).Append(',')
                .AppendLine(Escape(item.Outcome));
        }
        File.WriteAllText(path, builder.ToString());
    }

    private static void WriteMarkdown(
        IReadOnlyList<CheckerRecourseStageDiagnostic> diagnostics,
        string path)
    {
        StringBuilder builder = new();
        builder.AppendLine("# Checker Recourse Stage Diagnostics");
        builder.AppendLine();
        builder.AppendLine("| iter | batch | window_s | limit_s | status | runtime_s | solutions | incumbent | objective | outcome |");
        builder.AppendLine("|---:|---|---:|---:|---|---:|---:|---|---:|---|");
        foreach (CheckerRecourseStageDiagnostic item in diagnostics)
        {
            builder.Append("| ").Append(item.IterationIndex)
                .Append(" | `").Append(item.BatchId).Append("` | ")
                .Append(item.WindowSeconds).Append(" | ")
                .Append(Format(item.EffectiveTimeLimitSeconds)).Append(" | `")
                .Append(item.SolverStatusName).Append("` | ")
                .Append(Format(item.RuntimeSeconds)).Append(" | ")
                .Append(item.SolutionCount).Append(" | ")
                .Append(item.HasIncumbent).Append(" | ")
                .Append(item.ObjectiveValue.HasValue ? Format(item.ObjectiveValue.Value) : string.Empty)
                .Append(" | `").Append(item.Outcome).AppendLine("` |");
        }
        File.WriteAllText(path, builder.ToString());
    }

    private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Escape(string value) =>
        value.Contains(',') || value.Contains('"')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
}
