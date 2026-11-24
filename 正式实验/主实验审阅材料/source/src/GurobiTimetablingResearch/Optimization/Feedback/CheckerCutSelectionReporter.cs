// 候选选择报告器输出每轮候选的排序分、冲突簇、所属 episode 和最终选择状态。
// 表格保留规范候选键与选择原因，使加割策略的输入集合和实际试验顺序可以从产物中复现。
using System.Globalization;
using System.Text;

namespace GurobiTimetablingResearch.Optimization;

internal static class CheckerCutSelectionReporter
{
    internal static void Write(
        IReadOnlyList<CheckerCutSelectionDiagnostic> diagnostics,
        string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        WriteCsv(diagnostics, Path.Combine(outputDirectory, "checker_loop_cut_selection.csv"));
        WriteSummaryCsv(diagnostics, Path.Combine(outputDirectory, "checker_loop_cut_selection_summary.csv"));
        WriteMarkdown(diagnostics, Path.Combine(outputDirectory, "checker_loop_cut_selection.md"));
    }

    private static void WriteSummaryCsv(
        IReadOnlyList<CheckerCutSelectionDiagnostic> diagnostics,
        string path)
    {
        using StreamWriter writer = new(path, append: false, new UTF8Encoding(true));
        writer.WriteLine("iteration_index,selected_episode_count,selected_station_count,selected_pair_count,raw_conflicts_covered");
        foreach (IGrouping<int, CheckerCutSelectionDiagnostic> iteration in diagnostics.GroupBy(item => item.IterationIndex))
        {
            int rawCovered = iteration.GroupBy(item => item.EpisodeId, StringComparer.OrdinalIgnoreCase)
                .Sum(group => group.Max(item => item.RawConflictsCovered));
            writer.WriteLine(string.Join(',',
                iteration.Key,
                iteration.Select(item => item.EpisodeId).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                iteration.Select(item => item.StationName).Where(item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                iteration.Select(item => (Math.Min(item.FirstTrainIndex, item.SecondTrainIndex), Math.Max(item.FirstTrainIndex, item.SecondTrainIndex))).Distinct().Count(),
                rawCovered));
        }
    }

    private static void WriteCsv(IReadOnlyList<CheckerCutSelectionDiagnostic> diagnostics, string path)
    {
        using StreamWriter writer = new(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        writer.WriteLine("iteration_index,candidate_key,episode_id,resource_key,first_train_index,second_train_index,score,persistence_score,coverage_score,propagation_score,conflict_graph_score,scarcity_score,adaptive_batch_size,repair_neighbor_padding,adaptive_policy_reason");
        foreach (CheckerCutSelectionDiagnostic item in diagnostics)
        {
            writer.WriteLine(string.Join(',',
                item.IterationIndex.ToString(CultureInfo.InvariantCulture),
                Escape(item.CandidateKey),
                Escape(item.EpisodeId),
                Escape(item.ResourceKey),
                item.FirstTrainIndex.ToString(CultureInfo.InvariantCulture),
                item.SecondTrainIndex.ToString(CultureInfo.InvariantCulture),
                item.Score.ToString("F6", CultureInfo.InvariantCulture),
                item.PersistenceScore.ToString("F6", CultureInfo.InvariantCulture),
                item.CoverageScore.ToString("F6", CultureInfo.InvariantCulture),
                item.PropagationScore.ToString("F6", CultureInfo.InvariantCulture),
                item.ConflictGraphScore.ToString("F6", CultureInfo.InvariantCulture),
                item.ScarcityScore.ToString("F6", CultureInfo.InvariantCulture),
                item.AdaptiveBatchSize.ToString(CultureInfo.InvariantCulture),
                item.RepairNeighborPadding.ToString(CultureInfo.InvariantCulture),
                Escape(item.AdaptivePolicyReason)));
        }
    }

    private static void WriteMarkdown(IReadOnlyList<CheckerCutSelectionDiagnostic> diagnostics, string path)
    {
        StringBuilder builder = new();
        builder.AppendLine("# Checker Loop Cut Selection");
        builder.AppendLine();
        builder.AppendLine("Candidates are ranked by persistent conflict episodes and selected with train-pair diversity before repeat coverage.");
        builder.AppendLine();
        builder.AppendLine("| iter | episode | resource | trains | score | batch | padding | policy | candidate |");
        builder.AppendLine("|---:|---|---|---|---:|---:|---:|---|---|");
        foreach (CheckerCutSelectionDiagnostic item in diagnostics)
        {
            builder.AppendLine($"| {item.IterationIndex} | `{item.EpisodeId}` | `{item.ResourceKey}` | {item.FirstTrainIndex}-{item.SecondTrainIndex} | {item.Score:F1} | {item.AdaptiveBatchSize} | {item.RepairNeighborPadding} | `{item.AdaptivePolicyReason}` | `{item.CandidateKey}` |");
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static string Escape(string value)
    {
        if (!value.Contains(',') && !value.Contains('"') && !value.Contains('\n') && !value.Contains('\r'))
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }
}
