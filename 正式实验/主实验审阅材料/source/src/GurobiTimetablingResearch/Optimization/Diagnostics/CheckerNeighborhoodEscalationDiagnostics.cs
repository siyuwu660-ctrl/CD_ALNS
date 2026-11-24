// 邻域升级诊断记录每个结构候选在不同释放范围下的列车集合、时间窗、求解状态和终止动作。
// 报告按候选及层级顺序展开，用于判断不可行来自邻域过窄、时间预算不足还是候选自身矛盾。
using System.Globalization;
using System.Text;

namespace GurobiTimetablingResearch.Optimization;

public sealed record CheckerNeighborhoodEscalationDiagnostic(
    int CheckerIteration,
    string StructuralBatchId,
    string CandidateSummary,
    int NeighborhoodLevel,
    string NeighborhoodMode,
    int? ReferenceOrderPadding,
    int ReleasedTrainCount,
    int ComponentCount,
    int ComponentTrainCount,
    int TemporalWindowSeconds,
    double SolverTimeLimitSeconds,
    int SolverStatusCode,
    string SolverStatus,
    int SolutionCount,
    double RuntimeSeconds,
    int MicroConflictsBefore,
    int? MicroConflictsAfter,
    int ExactHeadwayConflictsBefore,
    int? ExactHeadwayConflictsAfter,
    int JointAssignmentConflictsBefore,
    int? JointAssignmentConflictsAfter,
    bool Accepted,
    string RejectionOrFailureReason);

public static class CheckerNeighborhoodEscalationDiagnosticWriter
{
    public static void Write(
        IReadOnlyList<CheckerNeighborhoodEscalationDiagnostic> diagnostics,
        string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        WriteCsv(diagnostics, Path.Combine(outputDirectory, "checker_loop_neighborhood_escalation.csv"));
        WriteMarkdown(diagnostics, Path.Combine(outputDirectory, "checker_loop_neighborhood_escalation.md"));
    }

    internal static void AppendMarkdownTable(
        StringBuilder builder,
        IReadOnlyList<CheckerNeighborhoodEscalationDiagnostic> diagnostics)
    {
        builder.AppendLine();
        builder.AppendLine("## Neighborhood Escalation Attempts");
        builder.AppendLine();
        builder.AppendLine("| iter | level | mode | padding | released | components/trains | window_s | status | runtime_s | solutions | conflicts | accepted | reason |");
        builder.AppendLine("|---:|---:|---|---:|---:|---:|---:|---|---:|---:|---:|---|---|");
        foreach (CheckerNeighborhoodEscalationDiagnostic item in diagnostics)
        {
            builder.Append("| ").Append(item.CheckerIteration)
                .Append(" | ").Append(item.NeighborhoodLevel)
                .Append(" | `").Append(item.NeighborhoodMode).Append("` | ")
                .Append(item.ReferenceOrderPadding?.ToString(CultureInfo.InvariantCulture) ?? string.Empty)
                .Append(" | ").Append(item.ReleasedTrainCount)
                .Append(" | ").Append(item.ComponentCount).Append('/').Append(item.ComponentTrainCount)
                .Append(" | ").Append(item.TemporalWindowSeconds)
                .Append(" | `").Append(item.SolverStatus).Append("` | ")
                .Append(Format(item.RuntimeSeconds)).Append(" | ")
                .Append(item.SolutionCount).Append(" | ")
                .Append(item.MicroConflictsBefore).Append("->")
                .Append(item.MicroConflictsAfter?.ToString(CultureInfo.InvariantCulture) ?? string.Empty)
                .Append(" | ").Append(item.Accepted)
                .Append(" | `").Append(item.RejectionOrFailureReason).AppendLine("` |");
        }
    }

    private static void WriteCsv(
        IReadOnlyList<CheckerNeighborhoodEscalationDiagnostic> diagnostics,
        string path)
    {
        StringBuilder builder = new();
        builder.AppendLine("checker_iteration,structural_batch_id,candidate_summary,neighborhood_level,neighborhood_mode,reference_order_padding,released_train_count,component_count,component_train_count,temporal_window_seconds,solver_time_limit_seconds,solver_status_code,solver_status,solution_count,runtime_seconds,micro_conflicts_before,micro_conflicts_after,exact_headway_conflicts_before,exact_headway_conflicts_after,joint_assignment_conflicts_before,joint_assignment_conflicts_after,accepted,rejection_or_failure_reason");
        foreach (CheckerNeighborhoodEscalationDiagnostic item in diagnostics)
        {
            string[] fields =
            [
                item.CheckerIteration.ToString(CultureInfo.InvariantCulture),
                item.StructuralBatchId,
                item.CandidateSummary,
                item.NeighborhoodLevel.ToString(CultureInfo.InvariantCulture),
                item.NeighborhoodMode,
                item.ReferenceOrderPadding?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                item.ReleasedTrainCount.ToString(CultureInfo.InvariantCulture),
                item.ComponentCount.ToString(CultureInfo.InvariantCulture),
                item.ComponentTrainCount.ToString(CultureInfo.InvariantCulture),
                item.TemporalWindowSeconds.ToString(CultureInfo.InvariantCulture),
                Format(item.SolverTimeLimitSeconds),
                item.SolverStatusCode.ToString(CultureInfo.InvariantCulture),
                item.SolverStatus,
                item.SolutionCount.ToString(CultureInfo.InvariantCulture),
                Format(item.RuntimeSeconds),
                item.MicroConflictsBefore.ToString(CultureInfo.InvariantCulture),
                item.MicroConflictsAfter?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                item.ExactHeadwayConflictsBefore.ToString(CultureInfo.InvariantCulture),
                item.ExactHeadwayConflictsAfter?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                item.JointAssignmentConflictsBefore.ToString(CultureInfo.InvariantCulture),
                item.JointAssignmentConflictsAfter?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                item.Accepted.ToString(),
                item.RejectionOrFailureReason
            ];
            builder.AppendLine(string.Join(',', fields.Select(Escape)));
        }
        File.WriteAllText(path, builder.ToString());
    }

    private static void WriteMarkdown(
        IReadOnlyList<CheckerNeighborhoodEscalationDiagnostic> diagnostics,
        string path)
    {
        StringBuilder builder = new("# Checker Neighborhood Escalation Diagnostics\n");
        AppendMarkdownTable(builder, diagnostics);
        File.WriteAllText(path, builder.ToString());
    }

    private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Escape(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) < 0
            ? value
            : $"\"{value.Replace("\"", "\"\"")}\"";
}
