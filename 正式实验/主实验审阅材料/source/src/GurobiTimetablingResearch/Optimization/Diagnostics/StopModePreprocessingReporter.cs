// 停站模式预处理报告器输出每个停站的原始选择域、收紧结果、固定决策及空域原因。
// 报告把数据规则与最终模型边界关联起来，用于审计预处理是否误删可行的径路股道组合。
using System.Globalization;
using System.Text;

namespace GurobiTimetablingResearch.Optimization;

internal static class StopModePreprocessingReporter
{
    internal static void Write(StopModePreprocessingResult result, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        WriteCsv(result, Path.Combine(outputDirectory, "stop_mode_preprocessing.csv"));
        WriteMarkdown(result, Path.Combine(outputDirectory, "stop_mode_preprocessing.md"));
    }

    private static void WriteCsv(StopModePreprocessingResult result, string path)
    {
        using StreamWriter writer = CreateWriter(path);
        writer.WriteLine("stop_id,train_index,train_no,station_order,station_name,stop_requirement,resolution,fixed_stop_value,has_contradiction,stop_feasible,stop_failure_reason,stop_feasible_tracks,pass_feasible,pass_failure_reason,pass_feasible_tracks,evidence");
        foreach (StopModePreprocessingDecision decision in result.Decisions
                     .OrderBy(item => item.TrainIndex)
                     .ThenBy(item => item.StationOrder))
        {
            writer.WriteLine(string.Join(',',
                Escape(decision.StopId),
                decision.TrainIndex.ToString(CultureInfo.InvariantCulture),
                Escape(decision.TrainNo),
                decision.StationOrder.ToString(CultureInfo.InvariantCulture),
                Escape(decision.StationName),
                decision.StopRequirement,
                decision.Resolution,
                decision.FixedStopValue?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                decision.HasContradiction,
                decision.Analysis.Stopping.IsFeasible,
                decision.Analysis.Stopping.FailureReason,
                Escape(string.Join('|', decision.Analysis.Stopping.FeasibleTracks)),
                decision.Analysis.Passing.IsFeasible,
                decision.Analysis.Passing.FailureReason,
                Escape(string.Join('|', decision.Analysis.Passing.FeasibleTracks)),
                Escape(decision.Analysis.Evidence)));
        }
    }

    private static void WriteMarkdown(StopModePreprocessingResult result, string path)
    {
        StringBuilder builder = new();
        builder.AppendLine("# Stop Mode Preprocessing");
        builder.AppendLine();
        builder.AppendLine($"- decision_count: `{result.Decisions.Count}`");
        builder.AppendLine($"- fixed_stop_count: `{result.FixedStopValuesByStopId.Values.Count(value => value == 1)}`");
        builder.AppendLine($"- fixed_pass_count: `{result.FixedStopValuesByStopId.Values.Count(value => value == 0)}`");
        builder.AppendLine($"- contradiction_count: `{result.Contradictions.Count}`");
        builder.AppendLine();
        builder.AppendLine("| train | station | requirement | resolution | fixed | stop reason | pass reason |");
        builder.AppendLine("|---:|---|---|---|---:|---|---|");
        foreach (StopModePreprocessingDecision decision in result.Decisions
                     .Where(item => item.Resolution != StopModeResolution.Flexible || item.HasContradiction)
                     .OrderBy(item => item.StationName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.TrainIndex))
        {
            builder.AppendLine($"| {decision.TrainIndex} | {decision.StationName} | {decision.StopRequirement} | {decision.Resolution} | {decision.FixedStopValue?.ToString(CultureInfo.InvariantCulture) ?? "-"} | {decision.Analysis.Stopping.FailureReason} | {decision.Analysis.Passing.FailureReason} |");
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
    }

    private static StreamWriter CreateWriter(string path)
    {
        return new StreamWriter(path, false, new UTF8Encoding(false));
    }

    private static string Escape(string value)
    {
        return $"\"{value.Replace("\"", "\"\"")}\"";
    }
}
