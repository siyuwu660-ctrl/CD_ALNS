// 微观割候选生成器把冲突解释映射为主问题可表达的区间先后、车站事件或径路选择候选。
// 生成后以规范键去重、按家族及压力排序，并输出候选报告，供 Checker 在有限试探预算中挑选结构反馈。
using System.Globalization;
using System.Text;

namespace GurobiTimetablingResearch.Optimization;

/// <summary>
/// 将微观 Checker 输出的冲突解释转换为候选割家族。
/// 该组件先建立“冲突解释到候选割”的稳定映射，再由 LBBD/branch-and-check 循环选择并试探注入主问题。
/// </summary>
public sealed class MicroConflictCutCandidateGenerator
{
    public MicroCutCandidateReport Generate(MicroFeasibilityReport feasibilityReport)
    {
        List<MicroCutCandidate> candidates = new();

        candidates.AddRange(GeneratePairOrderCuts(feasibilityReport.Conflicts));
        candidates.AddRange(GenerateStationCliqueCuts(feasibilityReport.Conflicts));
        candidates.AddRange(GenerateTrackDomainCuts(feasibilityReport.Conflicts));
        candidates.AddRange(GenerateRouteAvailabilityCuts(feasibilityReport.Conflicts));

        IReadOnlyList<MicroCutFamilySummary> familySummaries = candidates
            .GroupBy(candidate => candidate.CutFamily, StringComparer.OrdinalIgnoreCase)
            .Select(group => new MicroCutFamilySummary(
                group.Key,
                group.Count(),
                group.Count(candidate => candidate.SupportTrainCount >= 3),
                string.Join('|', group.Select(candidate => candidate.StationName).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).Take(10))))
            .OrderByDescending(summary => summary.CandidateCount)
            .ThenBy(summary => summary.CutFamily, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new MicroCutCandidateReport(candidates, familySummaries);
    }

    public void WriteReports(MicroCutCandidateReport report, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        WriteCandidateCsv(report.Candidates, Path.Combine(outputDirectory, "micro_cut_candidates.csv"));
        WriteMarkdownSummary(report, Path.Combine(outputDirectory, "micro_cut_candidates.md"));
    }

    private static IEnumerable<MicroCutCandidate> GeneratePairOrderCuts(IReadOnlyList<MicroConflictExplanation> conflicts)
    {
        return conflicts
            .Where(conflict =>
                string.Equals(conflict.ConflictKind, "exact_headway_infeasible", StringComparison.OrdinalIgnoreCase) &&
                conflict.FirstTrainIndex.HasValue &&
                conflict.SecondTrainIndex.HasValue)
            .GroupBy(conflict => new
            {
                StationName = conflict.StationName,
                FirstTrainIndex = Math.Min(conflict.FirstTrainIndex!.Value, conflict.SecondTrainIndex!.Value),
                SecondTrainIndex = Math.Max(conflict.FirstTrainIndex!.Value, conflict.SecondTrainIndex!.Value),
                ResourceKey = conflict.ResourceKey
            })
            .Select(group =>
            {
                MicroConflictExplanation first = group.First();
                List<int> trains = new() { group.Key.FirstTrainIndex, group.Key.SecondTrainIndex };
                string rationale = $"Repeated exact headway infeasibility for train pair {group.Key.FirstTrainIndex}/{group.Key.SecondTrainIndex} at station {group.Key.StationName}.";
                string template = "pairwise_order_or_strengthened_station_headway";

                return new MicroCutCandidate(
                    "station_pair_order_cut",
                    group.Key.StationName,
                    trains,
                    first.StartSeconds,
                    group.Max(item => item.EndSeconds),
                    group.Count(),
                    first.ResourceKey,
                    template,
                    rationale,
                    string.Join(" || ", group.Take(5).Select(item => item.Evidence)));
            });
    }

    private static IEnumerable<MicroCutCandidate> GenerateStationCliqueCuts(IReadOnlyList<MicroConflictExplanation> conflicts)
    {
        return conflicts
            .Where(conflict => string.Equals(conflict.ConflictKind, "exact_headway_infeasible", StringComparison.OrdinalIgnoreCase))
            .GroupBy(conflict => conflict.StationName, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                HashSet<int> trainSet = new(group
                    .SelectMany(conflict => new[] { conflict.FirstTrainIndex, conflict.SecondTrainIndex })
                    .Where(trainIndex => trainIndex.HasValue)
                    .Select(trainIndex => trainIndex!.Value));

                return new
                {
                    StationName = group.Key,
                    TrainIndices = trainSet.OrderBy(train => train).ToList(),
                    ConflictCount = group.Count(),
                    StartSeconds = group.Min(item => item.StartSeconds),
                    EndSeconds = group.Max(item => item.EndSeconds),
                    Evidence = string.Join(" || ", group.Take(5).Select(item => item.Evidence))
                };
            })
            .Where(item => item.TrainIndices.Count >= 3 && item.ConflictCount >= 3)
            .Select(item => new MicroCutCandidate(
                "station_event_clique_cut",
                item.StationName,
                item.TrainIndices,
                item.StartSeconds,
                item.EndSeconds,
                item.ConflictCount,
                "station_clique",
                "station_time_clique_or_capacity_cut",
                $"Station {item.StationName} shows dense exact-headway conflict clustering involving {item.TrainIndices.Count} trains.",
                item.Evidence));
    }

    private static IEnumerable<MicroCutCandidate> GenerateTrackDomainCuts(IReadOnlyList<MicroConflictExplanation> conflicts)
    {
        return conflicts
            .Where(conflict =>
                string.Equals(conflict.ConflictKind, "empty_track_domain", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(conflict.ConflictKind, "track_assignment_infeasible", StringComparison.OrdinalIgnoreCase))
            .GroupBy(conflict => conflict.StationName, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                List<int> trains = group
                    .Select(conflict => conflict.FirstTrainIndex)
                    .Where(trainIndex => trainIndex.HasValue)
                    .Select(trainIndex => trainIndex!.Value)
                    .Distinct()
                    .OrderBy(trainIndex => trainIndex)
                    .ToList();

                MicroConflictExplanation first = group.First();
                return new MicroCutCandidate(
                    "station_track_capacity_cut",
                    group.Key,
                    trains,
                    group.Min(item => item.StartSeconds),
                    group.Max(item => item.EndSeconds),
                    group.Count(),
                    "track_domain",
                    "station_track_capacity_or_stop_pattern_cut",
                    $"Station {group.Key} has unresolved track-domain or assignment infeasibility.",
                    string.Join(" || ", group.Take(5).Select(item => item.Evidence)));
            });
    }

    private static IEnumerable<MicroCutCandidate> GenerateRouteAvailabilityCuts(IReadOnlyList<MicroConflictExplanation> conflicts)
    {
        return conflicts
            .Where(conflict =>
                string.Equals(conflict.ConflictKind, "missing_incoming_route", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(conflict.ConflictKind, "missing_outgoing_route", StringComparison.OrdinalIgnoreCase))
            .GroupBy(conflict => new { conflict.StationName, conflict.ConflictKind })
            .Select(group =>
            {
                List<int> trains = group
                    .Select(conflict => conflict.FirstTrainIndex)
                    .Where(trainIndex => trainIndex.HasValue)
                    .Select(trainIndex => trainIndex!.Value)
                    .Distinct()
                    .OrderBy(trainIndex => trainIndex)
                    .ToList();

                return new MicroCutCandidate(
                    "station_route_availability_cut",
                    group.Key.StationName,
                    trains,
                    group.Min(item => item.StartSeconds),
                    group.Max(item => item.EndSeconds),
                    group.Count(),
                    group.Key.ConflictKind,
                    "route_family_availability_cut",
                    $"Station {group.Key.StationName} has missing route realizability for conflict type {group.Key.ConflictKind}.",
                    string.Join(" || ", group.Take(5).Select(item => item.Evidence)));
            });
    }

    private static void WriteCandidateCsv(IReadOnlyList<MicroCutCandidate> candidates, string path)
    {
        using StreamWriter writer = CreateUtf8Writer(path);
        writer.WriteLine("cut_family,station_name,support_train_count,train_indices,start_seconds,end_seconds,support_conflict_count,resource_key,template_name,rationale,evidence");

        foreach (MicroCutCandidate item in candidates
            .OrderByDescending(item => item.SupportConflictCount)
            .ThenBy(item => item.CutFamily, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.StationName, StringComparer.OrdinalIgnoreCase))
        {
            writer.WriteLine(string.Join(
                ',',
                Escape(item.CutFamily),
                Escape(item.StationName),
                item.SupportTrainCount.ToString(CultureInfo.InvariantCulture),
                Escape(string.Join('|', item.TrainIndices)),
                item.StartSeconds.ToString("F3", CultureInfo.InvariantCulture),
                item.EndSeconds.ToString("F3", CultureInfo.InvariantCulture),
                item.SupportConflictCount.ToString(CultureInfo.InvariantCulture),
                Escape(item.ResourceKey),
                Escape(item.TemplateName),
                Escape(item.Rationale),
                Escape(item.Evidence)));
        }
    }

    private static void WriteMarkdownSummary(MicroCutCandidateReport report, string path)
    {
        StringBuilder builder = new();
        builder.AppendLine("# Micro Cut Candidate Report");
        builder.AppendLine();
        builder.AppendLine("## Overall");
        builder.AppendLine();
        builder.AppendLine($"- candidate_count: `{report.Candidates.Count}`");
        builder.AppendLine($"- cut_family_count: `{report.FamilySummaries.Count}`");
        builder.AppendLine();

        builder.AppendLine("## Families");
        builder.AppendLine();
        builder.AppendLine("| family | candidates | multi-train_candidates | sample_stations |");
        builder.AppendLine("|---|---:|---:|---|");
        foreach (MicroCutFamilySummary summary in report.FamilySummaries)
        {
            builder.AppendLine($"| {summary.CutFamily} | {summary.CandidateCount} | {summary.MultiTrainCandidateCount} | `{summary.SampleStations}` |");
        }

        builder.AppendLine();
        builder.AppendLine("## Top Candidates");
        builder.AppendLine();
        builder.AppendLine("| family | station | trains | support_conflicts | template | rationale |");
        builder.AppendLine("|---|---|---|---:|---|---|");
        foreach (MicroCutCandidate candidate in report.Candidates
            .OrderByDescending(item => item.SupportConflictCount)
            .ThenByDescending(item => item.SupportTrainCount)
            .Take(40))
        {
            builder.AppendLine($"| {candidate.CutFamily} | {candidate.StationName} | `{string.Join('|', candidate.TrainIndices)}` | {candidate.SupportConflictCount} | `{candidate.TemplateName}` | {candidate.Rationale} |");
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static StreamWriter CreateUtf8Writer(string path)
    {
        return new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static string Escape(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (!value.Contains(',') && !value.Contains('"') && !value.Contains('\n') && !value.Contains('\r'))
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }
}

public sealed record MicroCutCandidateReport(
    IReadOnlyList<MicroCutCandidate> Candidates,
    IReadOnlyList<MicroCutFamilySummary> FamilySummaries);

public sealed record MicroCutCandidate(
    string CutFamily,
    string StationName,
    IReadOnlyList<int> TrainIndices,
    double StartSeconds,
    double EndSeconds,
    int SupportConflictCount,
    string ResourceKey,
    string TemplateName,
    string Rationale,
    string Evidence)
{
    public int SupportTrainCount => TrainIndices.Count;
}

public sealed record MicroCutFamilySummary(
    string CutFamily,
    int CandidateCount,
    int MultiTrainCandidateCount,
    string SampleStations);
