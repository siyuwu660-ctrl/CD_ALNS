// 越行分析器比较列车对在相邻车站的先后次序，识别顺序翻转并判断相应车站是否具备越行能力。
// 分析结果按事件和车站汇总，既用于验证运行图物理合理性，也用于诊断越行停时约束是否生效。
using System.Globalization;
using System.Text;
using Gurobi;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

/// <summary>
/// 分析 master 解中的真实越行事件。
/// 这里的“越行”不是指某一个区间上的 precedence 是否相对原始顺序反向，
/// 而是指同一列车对在某个车站前后两个相邻区间上的顺序是否发生变化。
/// 这个定义更贴近运行图语义：只有顺序跨站发生反转，才说明该站承担了越行功能。
/// </summary>
public sealed class MasterOvertakingAnalyzer
{
    public void WriteReports(BasicMilpModel model, string outputDirectory)
    {
        if (!model.HasIncumbentSolution)
        {
            throw new InvalidOperationException("Cannot analyze overtaking events because Gurobi has no incumbent solution.");
        }

        Directory.CreateDirectory(outputDirectory);
        IReadOnlyList<OvertakingEventAnalysis> events = BuildEventAnalyses(model);

        WriteEventDetails(events, Path.Combine(outputDirectory, "overtaking_events.csv"));
        WriteStationSummary(events, Path.Combine(outputDirectory, "overtaking_station_summary.csv"));
        WriteMarkdownSummary(model, events, Path.Combine(outputDirectory, "overtaking_summary.md"));
    }

    /// <summary>Summarize each comparable station pair using its legal waiting directions.</summary>
    /// <param name="model">Solved model providing borrowed order variables.</param>
    /// <returns>Per-pair resource capability and observed order changes.</returns>
    private static IReadOnlyList<OvertakingEventAnalysis> BuildEventAnalyses(BasicMilpModel model)
    {
        Dictionary<string, SectionConflictCandidate> candidateByKey = model.SectionConflictCandidates.ToDictionary(
            candidate => BuildCandidateKey(candidate.SectionName, candidate.FirstTrainIndex, candidate.SecondTrainIndex),
            StringComparer.OrdinalIgnoreCase);

        List<OvertakingEventAnalysis> analyses = new();

        foreach ((string stationName, string incomingSectionName, string outgoingSectionName) in BuildStationAdjacentSectionPairs(model.Instance))
        {
            StationOvertakingProfile? profile = model.Instance.StationOvertakingProfileMap.TryGetValue(stationName, out StationOvertakingProfile? foundProfile)
                ? foundProfile
                : null;

            IEnumerable<SectionConflictCandidate> incomingCandidates = model.SectionConflictCandidates
                .Where(candidate => string.Equals(candidate.SectionName, incomingSectionName, StringComparison.OrdinalIgnoreCase));

            foreach (SectionConflictCandidate incomingCandidate in incomingCandidates)
            {
                string outgoingKey = BuildCandidateKey(
                    outgoingSectionName,
                    incomingCandidate.FirstTrainIndex,
                    incomingCandidate.SecondTrainIndex);

                if (!candidateByKey.TryGetValue(outgoingKey, out SectionConflictCandidate? outgoingCandidate))
                {
                    continue;
                }

                if (!TryReadPrecedence(model, incomingCandidate, out int incomingPrecedence) ||
                    !TryReadPrecedence(model, outgoingCandidate, out int outgoingPrecedence))
                {
                    continue;
                }

                bool isOvertaking = incomingPrecedence != outgoingPrecedence;
                string incomingOrder = FormatActualOrder(incomingCandidate, incomingPrecedence);
                string outgoingOrder = FormatActualOrder(outgoingCandidate, outgoingPrecedence);
                TrainStopRecord firstVisit = model.Instance.TrainStops.Single(stop =>
                    stop.TrainIndex == incomingCandidate.FirstTrainIndex && stop.StationName.Equals(stationName, StringComparison.OrdinalIgnoreCase));
                TrainStopRecord secondVisit = model.Instance.TrainStops.Single(stop =>
                    stop.TrainIndex == incomingCandidate.SecondTrainIndex && stop.StationName.Equals(stationName, StringComparison.OrdinalIgnoreCase));
                bool firstCanWait = StationOvertakingResourcePolicy.CanOvertake(model.Instance, firstVisit, secondVisit);
                bool secondCanWait = StationOvertakingResourcePolicy.CanOvertake(model.Instance, secondVisit, firstVisit);
                bool capable = firstCanWait || secondCanWait;
                bool isRestrictedStation = isOvertaking ? !(incomingPrecedence == 0 ? firstCanWait : secondCanWait) : !capable;

                analyses.Add(new OvertakingEventAnalysis(
                    stationName,
                    incomingSectionName,
                    outgoingSectionName,
                    incomingCandidate.FirstTrainIndex,
                    incomingCandidate.SecondTrainIndex,
                    incomingCandidate.RankGap,
                    incomingPrecedence,
                    outgoingPrecedence,
                    incomingOrder,
                    outgoingOrder,
                    isOvertaking,
                    profile?.IsBoundaryStation ?? false,
                    capable,
                    isRestrictedStation,
                    profile?.CapabilityScore ?? 0,
                    profile?.TrackCount ?? 0,
                    profile?.RouteTrackCount ?? 0));
            }
        }

        return analyses;
    }

    private static void WriteEventDetails(IReadOnlyList<OvertakingEventAnalysis> events, string path)
    {
        using StreamWriter writer = CreateUtf8Writer(path);
        writer.WriteLine("station_name,incoming_section,outgoing_section,first_train_index,second_train_index,rank_gap,incoming_precedence,outgoing_precedence,incoming_order,outgoing_order,is_overtaking,is_boundary_station,is_overtaking_capable,is_restricted_station,capability_score,track_count,route_track_count");

        foreach (OvertakingEventAnalysis item in events
            .OrderByDescending(item => item.IsOvertaking)
            .ThenBy(item => item.StationName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.FirstTrainIndex)
            .ThenBy(item => item.SecondTrainIndex))
        {
            writer.WriteLine(string.Join(
                ',',
                Escape(item.StationName),
                Escape(item.IncomingSectionName),
                Escape(item.OutgoingSectionName),
                item.FirstTrainIndex.ToString(CultureInfo.InvariantCulture),
                item.SecondTrainIndex.ToString(CultureInfo.InvariantCulture),
                item.RankGap.ToString(CultureInfo.InvariantCulture),
                item.IncomingPrecedence.ToString(CultureInfo.InvariantCulture),
                item.OutgoingPrecedence.ToString(CultureInfo.InvariantCulture),
                Escape(item.IncomingOrder),
                Escape(item.OutgoingOrder),
                FormatBool(item.IsOvertaking),
                FormatBool(item.IsBoundaryStation),
                FormatBool(item.IsOvertakingCapable),
                FormatBool(item.IsRestrictedStation),
                item.CapabilityScore.ToString(CultureInfo.InvariantCulture),
                item.TrackCount.ToString(CultureInfo.InvariantCulture),
                item.RouteTrackCount.ToString(CultureInfo.InvariantCulture)));
        }
    }

    private static void WriteStationSummary(IReadOnlyList<OvertakingEventAnalysis> events, string path)
    {
        using StreamWriter writer = CreateUtf8Writer(path);
        writer.WriteLine("station_name,comparable_pair_count,overtaking_count,overtaking_ratio,is_boundary_station,is_overtaking_capable,is_restricted_station,capability_score,track_count,route_track_count");

        foreach (var item in BuildStationSummaries(events))
        {
            writer.WriteLine(string.Join(
                ',',
                Escape(item.StationName),
                item.ComparablePairCount.ToString(CultureInfo.InvariantCulture),
                item.OvertakingCount.ToString(CultureInfo.InvariantCulture),
                item.OvertakingRatio.ToString("F6", CultureInfo.InvariantCulture),
                FormatBool(item.IsBoundaryStation),
                FormatBool(item.IsOvertakingCapable),
                FormatBool(item.IsRestrictedStation),
                item.CapabilityScore.ToString(CultureInfo.InvariantCulture),
                item.TrackCount.ToString(CultureInfo.InvariantCulture),
                item.RouteTrackCount.ToString(CultureInfo.InvariantCulture)));
        }
    }

    private static void WriteMarkdownSummary(BasicMilpModel model, IReadOnlyList<OvertakingEventAnalysis> events, string path)
    {
        IReadOnlyList<OvertakingStationSummary> stationSummaries = BuildStationSummaries(events);
        int overtakingCount = events.Count(item => item.IsOvertaking);
        int restrictedViolationCount = events.Count(item => item.IsOvertaking && item.IsRestrictedStation);
        double overtakingRatio = events.Count == 0 ? 0.0 : (double)overtakingCount / events.Count;

        StringBuilder builder = new();
        builder.AppendLine("# Overtaking Event Analysis");
        builder.AppendLine();
        builder.AppendLine("## Overall");
        builder.AppendLine();
        builder.AppendLine($"- instance: `{model.Instance.Meta.InstanceName}`");
        builder.AppendLine($"- comparable_station_pair_edges: `{events.Count}`");
        builder.AppendLine($"- overtaking_events: `{overtakingCount}`");
        builder.AppendLine($"- overtaking_ratio: `{overtakingRatio:P2}`");
        builder.AppendLine($"- restricted_station_overtaking_violations: `{restrictedViolationCount}`");
        builder.AppendLine();
        builder.AppendLine("说明：本报告中的越行定义为同一候选列车对在某站前后相邻区间的 precedence 值发生变化。");
        builder.AppendLine();

        builder.AppendLine("## Station Summary");
        builder.AppendLine();
        builder.AppendLine("| station | comparable_pairs | overtaking | ratio | capable | restricted | score | tracks | route_tracks |");
        builder.AppendLine("|---|---:|---:|---:|---|---|---:|---:|---:|");
        foreach (OvertakingStationSummary item in stationSummaries.Take(40))
        {
            builder.AppendLine($"| {item.StationName} | {item.ComparablePairCount} | {item.OvertakingCount} | {item.OvertakingRatio:P2} | {item.IsOvertakingCapable} | {item.IsRestrictedStation} | {item.CapabilityScore} | {item.TrackCount} | {item.RouteTrackCount} |");
        }

        builder.AppendLine();
        builder.AppendLine("## Top Overtaking Events");
        builder.AppendLine();
        builder.AppendLine("| station | incoming_section | outgoing_section | trains | incoming_order | outgoing_order | capable | restricted |");
        builder.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (OvertakingEventAnalysis item in events
            .Where(item => item.IsOvertaking)
            .OrderByDescending(item => item.IsRestrictedStation)
            .ThenBy(item => item.StationName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.FirstTrainIndex)
            .Take(40))
        {
            builder.AppendLine($"| {item.StationName} | {item.IncomingSectionName} | {item.OutgoingSectionName} | {item.FirstTrainIndex}-{item.SecondTrainIndex} | `{item.IncomingOrder}` | `{item.OutgoingOrder}` | {item.IsOvertakingCapable} | {item.IsRestrictedStation} |");
        }

        if (restrictedViolationCount > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Warning");
            builder.AppendLine();
            builder.AppendLine("弱能力站出现越行事件，需要检查 master 约束是否漏建候选边，或检查站点能力判定是否过严。");
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    /// <summary>Aggregate resource capability across all comparable pairs at each station.</summary>
    /// <param name="events">Modeled station-pair records.</param>
    /// <returns>Station statistics ranked by overtaking count and ratio.</returns>
    private static IReadOnlyList<OvertakingStationSummary> BuildStationSummaries(IReadOnlyList<OvertakingEventAnalysis> events)
    {
        return events
            .GroupBy(item => item.StationName, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                OvertakingEventAnalysis first = group.First();
                int comparablePairCount = group.Count();
                int overtakingCount = group.Count(item => item.IsOvertaking);
                double overtakingRatio = comparablePairCount == 0 ? 0.0 : (double)overtakingCount / comparablePairCount;

                return new OvertakingStationSummary(
                    first.StationName,
                    comparablePairCount,
                    overtakingCount,
                    overtakingRatio,
                    first.IsBoundaryStation,
                    group.Any(item => item.IsOvertakingCapable),
                    group.All(item => !item.IsOvertakingCapable),
                    first.CapabilityScore,
                    first.TrackCount,
                    first.RouteTrackCount);
            })
            .OrderByDescending(item => item.OvertakingCount)
            .ThenByDescending(item => item.OvertakingRatio)
            .ThenBy(item => item.StationName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<(string StationName, string IncomingSectionName, string OutgoingSectionName)> BuildStationAdjacentSectionPairs(TimetablingInstance instance)
    {
        Dictionary<string, (string IncomingSectionName, string OutgoingSectionName)> byStation = new(StringComparer.OrdinalIgnoreCase);

        foreach (TrainRecord train in instance.Trains)
        {
            for (int index = 1; index < train.OrderedStops.Count - 1; index++)
            {
                TrainStopRecord currentStop = train.OrderedStops[index];
                TrainStopRecord nextStop = train.OrderedStops[index + 1];
                if (string.IsNullOrWhiteSpace(currentStop.IncomingSectionName) ||
                    string.IsNullOrWhiteSpace(nextStop.IncomingSectionName))
                {
                    continue;
                }

                byStation.TryAdd(currentStop.StationName, (currentStop.IncomingSectionName, nextStop.IncomingSectionName));
            }
        }

        return byStation
            .Select(item => (item.Key, item.Value.IncomingSectionName, item.Value.OutgoingSectionName))
            .ToList();
    }

    private static bool TryReadPrecedence(BasicMilpModel model, SectionConflictCandidate candidate, out int precedence)
    {
        string key = BasicMilpModelBuilder.BuildSectionPrecedenceKey(candidate);
        if (!model.SectionPrecedenceVars.TryGetValue(key, out GRBVar? variable))
        {
            precedence = 0;
            return false;
        }

        precedence = variable.Get(GRB.DoubleAttr.X) >= 0.5 ? 1 : 0;
        return true;
    }

    private static string FormatActualOrder(SectionConflictCandidate candidate, int precedence)
    {
        return precedence == 0
            ? $"{candidate.FirstTrainIndex}_before_{candidate.SecondTrainIndex}"
            : $"{candidate.SecondTrainIndex}_before_{candidate.FirstTrainIndex}";
    }

    private static string BuildCandidateKey(string sectionName, int firstTrainIndex, int secondTrainIndex)
    {
        return $"{sectionName}::{firstTrainIndex}::{secondTrainIndex}";
    }

    private static StreamWriter CreateUtf8Writer(string path)
    {
        return new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static string FormatBool(bool value)
    {
        return value ? "True" : "False";
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

public sealed record OvertakingEventAnalysis(
    string StationName,
    string IncomingSectionName,
    string OutgoingSectionName,
    int FirstTrainIndex,
    int SecondTrainIndex,
    int RankGap,
    int IncomingPrecedence,
    int OutgoingPrecedence,
    string IncomingOrder,
    string OutgoingOrder,
    bool IsOvertaking,
    bool IsBoundaryStation,
    bool IsOvertakingCapable,
    bool IsRestrictedStation,
    int CapabilityScore,
    int TrackCount,
    int RouteTrackCount);

public sealed record OvertakingStationSummary(
    string StationName,
    int ComparablePairCount,
    int OvertakingCount,
    double OvertakingRatio,
    bool IsBoundaryStation,
    bool IsOvertakingCapable,
    bool IsRestrictedStation,
    int CapabilityScore,
    int TrackCount,
    int RouteTrackCount);
