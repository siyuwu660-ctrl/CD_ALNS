// 主模型诊断报告器汇总变量、约束、目标、停站、径路及规则命中情况，输出便于人工审查的 Markdown 文档。
// 报告从 BasicMilpModel 的索引结构读取统计信息，不重新推导业务决策，因而与实际送入 Gurobi 的模型保持一致。
using System.Text;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

/// <summary>
/// 输出 master 构建阶段的结构性诊断信息。
/// 这些信息不依赖求解器是否找到可行解，主要用于判断当前宏观模型是不是过松、过紧或规模异常。
/// </summary>
public sealed class MasterDiagnosticsReporter
{
    public void WriteReport(BasicMilpModel model, string path)
    {
        StringBuilder builder = new();
        builder.AppendLine("# Master Diagnostics Report");
        builder.AppendLine();
        builder.AppendLine("## Instance Scale");
        builder.AppendLine();
        builder.AppendLine($"- instance: `{model.Instance.Meta.InstanceName}`");
        builder.AppendLine($"- trains: `{model.Instance.Trains.Count}`");
        builder.AppendLine($"- stops: `{model.Instance.TrainStops.Count}`");
        builder.AppendLine($"- sections: `{model.Instance.Sections.Count}`");
        builder.AppendLine($"- routes: `{model.Instance.Routes.Count}`");
        builder.AppendLine($"- movement_headway_rules: `{model.Instance.MovementHeadwayRules.Count}`");
        builder.AppendLine($"- section_conflict_candidates: `{model.SectionConflictCandidates.Count}`");
        builder.AppendLine();

        WriteConflictGraphSummary(model, builder);
        WriteStationOvertakingCapabilitySummary(model, builder);
        WriteMovementHeadwaySummary(model, builder);
        WriteFallbackSummary(model, builder);

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static void WriteConflictGraphSummary(BasicMilpModel model, StringBuilder builder)
    {
        builder.AppendLine("## Initial Conflict Graph");
        builder.AppendLine();

        var bySection = model.SectionConflictCandidates
            .GroupBy(candidate => candidate.SectionName, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                SectionName = group.Key,
                Count = group.Count(),
                AverageRankGap = group.Average(candidate => candidate.RankGap),
                MaxRankGap = group.Max(candidate => candidate.RankGap)
            })
            .OrderByDescending(item => item.Count)
            .ToList();

        builder.AppendLine($"- active_sections_with_candidates: `{bySection.Count}`");
        builder.AppendLine($"- max_candidates_on_one_section: `{bySection.Select(item => item.Count).DefaultIfEmpty(0).Max()}`");
        builder.AppendLine();
        builder.AppendLine("| section | candidates | avg_rank_gap | max_rank_gap |");
        builder.AppendLine("|---|---:|---:|---:|");

        foreach (var item in bySection.Take(30))
        {
            builder.AppendLine($"| {item.SectionName} | {item.Count} | {item.AverageRankGap:F2} | {item.MaxRankGap} |");
        }

        builder.AppendLine();
    }

    /// <summary>Write station scoring inputs and search priorities to the diagnostics report.</summary>
    /// <param name="model">Model supplying original station scoring profiles.</param>
    /// <param name="builder">Destination diagnostic report buffer.</param>
    private static void WriteStationOvertakingCapabilitySummary(BasicMilpModel model, StringBuilder builder)
    {
        builder.AppendLine("## Station Overtaking Search Scores");
        builder.AppendLine();

        int normalPriorityCount = model.Instance.StationOvertakingProfiles.Count(profile => !profile.IsBoundaryStation && profile.CapabilityScore >= 3);
        int reducedPriorityCount = model.Instance.StationOvertakingProfiles.Count(profile => !profile.IsBoundaryStation && profile.CapabilityScore < 3);
        int boundaryCount = model.Instance.StationOvertakingProfiles.Count(profile => profile.IsBoundaryStation);

        builder.AppendLine($"- normal_priority_internal_stations: `{normalPriorityCount}`");
        builder.AppendLine($"- reduced_priority_internal_stations: `{reducedPriorityCount}`");
        builder.AppendLine($"- boundary_stations: `{boundaryCount}`");
        builder.AppendLine();
        builder.AppendLine("| station | boundary | score | tracks | route_tracks | dep_event | pass_event | stop_track | pass_track | must_stop | optional | must_pass |");
        builder.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");

        foreach (StationOvertakingProfile profile in model.Instance.StationOvertakingProfiles
            .OrderBy(profile => profile.IsBoundaryStation)
            .ThenBy(profile => profile.CapabilityScore)
            .ThenBy(profile => profile.StationName, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine(
                $"| {profile.StationName} | {profile.IsBoundaryStation} | {profile.CapabilityScore} | {profile.TrackCount} | {profile.RouteTrackCount} | {profile.HasDepartureEvent} | {profile.HasPassingEvent} | {profile.HasStopTrackEvidence} | {profile.HasPassTrackEvidence} | {profile.MustStopCount} | {profile.OptionalStopCount} | {profile.MustPassCount} |");
        }

        builder.AppendLine();
    }

    private static void WriteMovementHeadwaySummary(BasicMilpModel model, StringBuilder builder)
    {
        builder.AppendLine("## Movement Headway Rules");
        builder.AppendLine();
        builder.AppendLine("| event_pair | station_rule_count | max_proxy_seconds |");
        builder.AppendLine("|---|---:|---:|");

        var byPair = model.Instance.MovementHeadwayRules
            .GroupBy(rule => $"{rule.LeadingEvent}->{rule.FollowingEvent}")
            .Select(group => new
            {
                Pair = group.Key,
                Count = group.Count(),
                MaxProxySeconds = group.Max(rule => rule.ProxyHeadwaySeconds)
            })
            .OrderBy(item => item.Pair);

        foreach (var item in byPair)
        {
            builder.AppendLine($"| `{item.Pair}` | {item.Count} | {item.MaxProxySeconds} |");
        }

        builder.AppendLine();
    }

    private static void WriteFallbackSummary(BasicMilpModel model, StringBuilder builder)
    {
        Dictionary<(int TrainIndex, int StationOrder), TrainStopRecord> stopByTrainAndOrder = model.Instance.TrainStops
            .ToDictionary(stop => (stop.TrainIndex, stop.StationOrder));

        List<string> fallbackItems = new();

        foreach (SectionConflictCandidate candidate in model.SectionConflictCandidates)
        {
            if (!stopByTrainAndOrder.TryGetValue((candidate.FirstTrainIndex, candidate.FirstStopOrder), out TrainStopRecord? firstStop))
            {
                continue;
            }

            string stationName = firstStop.StationName;
            CheckRule(model.Instance, stationName, MovementEvent.Departure, MovementEvent.Departure, fallbackItems);
            CheckRule(model.Instance, stationName, MovementEvent.Departure, MovementEvent.Passing, fallbackItems);
            CheckRule(model.Instance, stationName, MovementEvent.Passing, MovementEvent.Departure, fallbackItems);
            CheckRule(model.Instance, stationName, MovementEvent.Passing, MovementEvent.Passing, fallbackItems);
        }

        List<string> distinctFallbackItems = fallbackItems
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .ToList();

        builder.AppendLine("## Departure-Side Headway Fallback");
        builder.AppendLine();
        builder.AppendLine($"- missing_station_event_pairs_used_by_candidate_edges: `{distinctFallbackItems.Count}`");

        if (distinctFallbackItems.Count > 0)
        {
            builder.AppendLine();
            foreach (string item in distinctFallbackItems.Take(50))
            {
                builder.AppendLine($"- `{item}`");
            }
        }

        builder.AppendLine();
    }

    private static void CheckRule(
        TimetablingInstance instance,
        string stationName,
        MovementEvent leadingEvent,
        MovementEvent followingEvent,
        ICollection<string> fallbackItems)
    {
        if (!instance.TryGetMovementHeadwayRule(stationName, leadingEvent, followingEvent, out MovementHeadwayRule? rule) || rule is null)
        {
            fallbackItems.Add($"{stationName}: {leadingEvent}->{followingEvent}");
        }
    }
}
