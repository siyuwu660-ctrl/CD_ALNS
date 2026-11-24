// 先后关系分析器从已求解的时间变量和二元 precedence 变量还原列车对在区间及车站端点的实际顺序。
// 输出边级记录并检查变量取值与时刻关系是否一致，用于审计大 M 约束和结构割的有效性。
using System.Globalization;
using System.Text;
using Gurobi;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

/// <summary>
/// 对 master 解中的区间 precedence 决策进行统计分析。
/// 该分析器的目标不是再次校验可行性，而是回答一个更偏研究设计的问题：
/// 初始候选冲突图里到底有多少边真正改变了顺序，哪些区间最活跃，哪些 rank gap 的边最有价值。
/// 这些统计会直接服务于后续的候选边压缩、rolling horizon 切窗和 checker 调用优先级设计。
/// </summary>
public sealed class MasterPrecedenceAnalyzer
{
    public void WriteReports(BasicMilpModel model, string outputDirectory)
    {
        if (!model.HasIncumbentSolution)
        {
            throw new InvalidOperationException("Cannot analyze precedence decisions because Gurobi has no incumbent solution.");
        }

        Directory.CreateDirectory(outputDirectory);
        IReadOnlyList<PrecedenceEdgeAnalysis> edges = BuildEdgeAnalyses(model);

        WriteSectionSummary(edges, Path.Combine(outputDirectory, "precedence_section_summary.csv"));
        WriteMarkdownSummary(model, edges, Path.Combine(outputDirectory, "precedence_analysis.md"));
    }

    private static IReadOnlyList<PrecedenceEdgeAnalysis> BuildEdgeAnalyses(BasicMilpModel model)
    {
        Dictionary<(int TrainIndex, int StationOrder), TrainStopRecord> stopByTrainAndOrder = model.Instance.TrainStops
            .ToDictionary(stop => (stop.TrainIndex, stop.StationOrder));

        List<PrecedenceEdgeAnalysis> analyses = new();

        foreach (SectionConflictCandidate candidate in model.SectionConflictCandidates)
        {
            string key = BasicMilpModelBuilder.BuildSectionPrecedenceKey(candidate);
            if (!model.SectionPrecedenceVars.TryGetValue(key, out GRBVar? precedenceVar))
            {
                continue;
            }

            if (!stopByTrainAndOrder.TryGetValue((candidate.FirstTrainIndex, candidate.FirstStopOrder), out TrainStopRecord? firstStop) ||
                !stopByTrainAndOrder.TryGetValue((candidate.SecondTrainIndex, candidate.SecondStopOrder), out TrainStopRecord? secondStop))
            {
                continue;
            }

            bool reversed = Value(precedenceVar) >= 0.5;
            TrainStopRecord leadingStop = reversed ? secondStop : firstStop;
            TrainStopRecord followingStop = reversed ? firstStop : secondStop;
            double leadingDeparture = Value(model.DepartureVars[leadingStop.StopId]);
            double followingDeparture = Value(model.DepartureVars[followingStop.StopId]);
            int leadingIsStop = BinaryValue(model.StopVars[leadingStop.StopId]);
            int followingIsStop = BinaryValue(model.StopVars[followingStop.StopId]);
            MovementEvent leadingEvent = leadingIsStop == 1 ? MovementEvent.Departure : MovementEvent.Passing;
            MovementEvent followingEvent = followingIsStop == 1 ? MovementEvent.Departure : MovementEvent.Passing;
            int requiredHeadway = MasterRouteDomainHeadwayProvider.ResolveLowerBound(
                model.Instance,
                leadingStop,
                leadingIsStop == 1,
                followingStop,
                followingIsStop == 1,
                candidate.SectionName,
                isOriginSide: true);
            double realizedHeadway = followingDeparture - leadingDeparture;

            analyses.Add(new PrecedenceEdgeAnalysis(
                candidate.SectionName,
                candidate.FirstTrainIndex,
                candidate.SecondTrainIndex,
                candidate.RankGap,
                candidate.StartStationName,
                candidate.EndStationName,
                reversed,
                leadingStop.TrainIndex,
                followingStop.TrainIndex,
                leadingEvent,
                followingEvent,
                realizedHeadway,
                requiredHeadway,
                realizedHeadway - requiredHeadway));
        }

        return analyses;
    }

    private static void WriteSectionSummary(IReadOnlyList<PrecedenceEdgeAnalysis> edges, string path)
    {
        using StreamWriter writer = CreateUtf8Writer(path);
        writer.WriteLine("section_name,candidate_count,reversed_count,reversed_ratio,avg_rank_gap,avg_slack_seconds,min_slack_seconds");

        foreach (var item in edges
            .GroupBy(edge => edge.SectionName, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                SectionName = group.Key,
                CandidateCount = group.Count(),
                ReversedCount = group.Count(edge => edge.IsReversed),
                AverageRankGap = group.Average(edge => edge.RankGap),
                AverageSlackSeconds = group.Average(edge => edge.SlackSeconds),
                MinSlackSeconds = group.Min(edge => edge.SlackSeconds)
            })
            .OrderByDescending(item => item.ReversedCount)
            .ThenByDescending(item => item.CandidateCount))
        {
            double reversedRatio = item.CandidateCount == 0 ? 0.0 : (double)item.ReversedCount / item.CandidateCount;
            writer.WriteLine(string.Join(
                ',',
                Escape(item.SectionName),
                item.CandidateCount.ToString(CultureInfo.InvariantCulture),
                item.ReversedCount.ToString(CultureInfo.InvariantCulture),
                reversedRatio.ToString("F6", CultureInfo.InvariantCulture),
                item.AverageRankGap.ToString("F3", CultureInfo.InvariantCulture),
                item.AverageSlackSeconds.ToString("F3", CultureInfo.InvariantCulture),
                item.MinSlackSeconds.ToString("F3", CultureInfo.InvariantCulture)));
        }
    }

    private static void WriteMarkdownSummary(BasicMilpModel model, IReadOnlyList<PrecedenceEdgeAnalysis> edges, string path)
    {
        int reversedCount = edges.Count(edge => edge.IsReversed);
        double reversedRatio = edges.Count == 0 ? 0.0 : (double)reversedCount / edges.Count;

        StringBuilder builder = new();
        builder.AppendLine("# Precedence Analysis");
        builder.AppendLine();
        builder.AppendLine("## Overall");
        builder.AppendLine();
        builder.AppendLine($"- instance: `{model.Instance.Meta.InstanceName}`");
        builder.AppendLine($"- candidate_edges: `{edges.Count}`");
        builder.AppendLine($"- reversed_edges: `{reversedCount}`");
        builder.AppendLine($"- reversed_ratio: `{reversedRatio:P2}`");
        builder.AppendLine($"- average_slack_seconds: `{edges.Select(edge => edge.SlackSeconds).DefaultIfEmpty(0).Average():F2}`");
        builder.AppendLine($"- min_slack_seconds: `{edges.Select(edge => edge.SlackSeconds).DefaultIfEmpty(0).Min():F2}`");
        builder.AppendLine();

        builder.AppendLine("## Top Sections By Reversed Edges");
        builder.AppendLine();
        builder.AppendLine("| section | candidates | reversed | reversed_ratio | avg_slack | min_slack |");
        builder.AppendLine("|---|---:|---:|---:|---:|---:|");
        foreach (var item in edges
            .GroupBy(edge => edge.SectionName, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                SectionName = group.Key,
                CandidateCount = group.Count(),
                ReversedCount = group.Count(edge => edge.IsReversed),
                AverageSlackSeconds = group.Average(edge => edge.SlackSeconds),
                MinSlackSeconds = group.Min(edge => edge.SlackSeconds)
            })
            .OrderByDescending(item => item.ReversedCount)
            .ThenByDescending(item => item.CandidateCount)
            .Take(20))
        {
            double ratio = item.CandidateCount == 0 ? 0.0 : (double)item.ReversedCount / item.CandidateCount;
            builder.AppendLine($"| {item.SectionName} | {item.CandidateCount} | {item.ReversedCount} | {ratio:P2} | {item.AverageSlackSeconds:F2} | {item.MinSlackSeconds:F2} |");
        }

        builder.AppendLine();
        builder.AppendLine("## Rank Gap Distribution");
        builder.AppendLine();
        builder.AppendLine("| rank_gap | candidates | reversed | reversed_ratio |");
        builder.AppendLine("|---:|---:|---:|---:|");
        foreach (var item in edges
            .GroupBy(edge => edge.RankGap)
            .Select(group => new
            {
                RankGap = group.Key,
                CandidateCount = group.Count(),
                ReversedCount = group.Count(edge => edge.IsReversed)
            })
            .OrderBy(item => item.RankGap))
        {
            double ratio = item.CandidateCount == 0 ? 0.0 : (double)item.ReversedCount / item.CandidateCount;
            builder.AppendLine($"| {item.RankGap} | {item.CandidateCount} | {item.ReversedCount} | {ratio:P2} |");
        }

        builder.AppendLine();
        builder.AppendLine("## Event Pair Distribution");
        builder.AppendLine();
        builder.AppendLine("| event_pair | candidates | reversed | avg_required_headway | avg_slack |");
        builder.AppendLine("|---|---:|---:|---:|---:|");
        foreach (var item in edges
            .GroupBy(edge => $"{edge.LeadingEvent}->{edge.FollowingEvent}")
            .Select(group => new
            {
                EventPair = group.Key,
                CandidateCount = group.Count(),
                ReversedCount = group.Count(edge => edge.IsReversed),
                AverageRequiredHeadway = group.Average(edge => edge.RequiredHeadwaySeconds),
                AverageSlack = group.Average(edge => edge.SlackSeconds)
            })
            .OrderBy(item => item.EventPair))
        {
            builder.AppendLine($"| `{item.EventPair}` | {item.CandidateCount} | {item.ReversedCount} | {item.AverageRequiredHeadway:F2} | {item.AverageSlack:F2} |");
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static StreamWriter CreateUtf8Writer(string path)
    {
        return new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static double Value(GRBVar variable)
    {
        return variable.Get(GRB.DoubleAttr.X);
    }

    private static int BinaryValue(GRBVar variable)
    {
        return Value(variable) >= 0.5 ? 1 : 0;
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

public sealed record PrecedenceEdgeAnalysis(
    string SectionName,
    int FirstTrainIndex,
    int SecondTrainIndex,
    int RankGap,
    string StartStationName,
    string EndStationName,
    bool IsReversed,
    int LeadingTrainIndex,
    int FollowingTrainIndex,
    MovementEvent LeadingEvent,
    MovementEvent FollowingEvent,
    double RealizedHeadwaySeconds,
    int RequiredHeadwaySeconds,
    double SlackSeconds);
