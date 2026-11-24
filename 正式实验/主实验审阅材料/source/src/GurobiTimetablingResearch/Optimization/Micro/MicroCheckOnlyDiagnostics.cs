// 只检查模式诊断器把微观冲突按车站、类别和列车对聚合，生成无需重新求解即可阅读的统计报告。
// 聚合对象保留原始冲突计数与热点排名，用于快速评估外部快照的微观质量和确定修复入口。
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace GurobiTimetablingResearch.Optimization;

public sealed record MicroCheckOnlyStationSummary(
    string StationName,
    int TotalConflictCount,
    int ExactConflictCount,
    int AssignmentConflictCount,
    int EpisodeCount,
    int AffectedTrainCount);

public sealed record MicroCheckOnlyDiagnosticsSummary(
    int RawConflictCount,
    int CanonicalConflictCount,
    int RootConflictCount,
    int EpisodeConflictCount,
    int ConnectedComponentCount,
    int AffectedTrainCount,
    int AffectedStationCount,
    int ProvenInfeasibleConflictCount,
    int UnknownConflictCount,
    int ExactConflictCount,
    int AssignmentConflictCount,
    IReadOnlyDictionary<string, int> ByConflictKind,
    IReadOnlyList<MicroCheckOnlyStationSummary> StationSummaries);

public static class MicroCheckOnlyDiagnosticsAggregator
{
    public static MicroCheckOnlyDiagnosticsSummary Summarize(MicroFeasibilityReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        MicroConflictMetrics metrics = MicroConflictMetricCalculator.Calculate(report.Conflicts);
        Dictionary<string, int> byKind = report.Conflicts
            .GroupBy(item => item.ConflictKind, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        List<MicroCheckOnlyStationSummary> stations = report.Conflicts
            .Where(item => !string.IsNullOrWhiteSpace(item.StationName))
            .GroupBy(item => item.StationName, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                IReadOnlyList<MicroConflictExplanation> conflicts = group.ToList();
                return new MicroCheckOnlyStationSummary(
                    group.Key,
                    conflicts.Count,
                    conflicts.Count(IsExact),
                    conflicts.Count(IsAssignment),
                    MicroConflictMetricCalculator.Calculate(conflicts).EpisodeConflictCount,
                    conflicts.SelectMany(item => new[] { item.FirstTrainIndex, item.SecondTrainIndex })
                        .Where(item => item.HasValue)
                        .Select(item => item!.Value)
                        .Distinct()
                        .Count());
            })
            .OrderBy(item => item.StationName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new MicroCheckOnlyDiagnosticsSummary(
            metrics.RawConflictCount,
            metrics.CanonicalConflictCount,
            metrics.RootConflictCount,
            metrics.EpisodeConflictCount,
            metrics.ConnectedComponentCount,
            metrics.AffectedTrainCount,
            metrics.ConflictedStationCount,
            report.Status == MicroSolveStatus.ProvenInfeasible ? metrics.RawConflictCount : 0,
            report.Status == MicroSolveStatus.Unknown ? metrics.RawConflictCount : 0,
            report.Conflicts.Count(IsExact),
            report.Conflicts.Count(IsAssignment),
            byKind,
            stations);
    }

    private static bool IsExact(MicroConflictExplanation conflict) =>
        string.Equals(conflict.ConflictKind, "exact_headway_infeasible", StringComparison.OrdinalIgnoreCase);

    private static bool IsAssignment(MicroConflictExplanation conflict) =>
        string.Equals(
            conflict.ConflictKind,
            "station_route_track_assignment_infeasible",
            StringComparison.OrdinalIgnoreCase);
}

public static class MicroCheckOnlyDiagnosticsReporter
{
    public static void Write(
        MicroCheckOnlyDiagnosticsSummary summary,
        MicroCheckOnlyResult result,
        string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        WriteSummaryCsv(summary, Path.Combine(outputDirectory, "micro_c0_summary.csv"));
        WriteSummaryMarkdown(summary, Path.Combine(outputDirectory, "micro_c0_summary.md"));
        WriteStationCsv(summary.StationSummaries, Path.Combine(outputDirectory, "micro_c0_station_summary.csv"));
        WriteManifest(result, Path.Combine(outputDirectory, "micro_check_only_manifest.json"));
    }

    private static void WriteSummaryCsv(MicroCheckOnlyDiagnosticsSummary summary, string path)
    {
        using StreamWriter writer = new(path, false, new UTF8Encoding(true));
        writer.WriteLine("raw_conflict_count,canonical_conflict_count,root_conflict_count,episode_conflict_count,connected_component_count,affected_train_count,affected_station_count,proven_infeasible_conflict_count,unknown_conflict_count,exact_conflict_count,assignment_conflict_count");
        writer.WriteLine(string.Join(',',
            summary.RawConflictCount,
            summary.CanonicalConflictCount,
            summary.RootConflictCount,
            summary.EpisodeConflictCount,
            summary.ConnectedComponentCount,
            summary.AffectedTrainCount,
            summary.AffectedStationCount,
            summary.ProvenInfeasibleConflictCount,
            summary.UnknownConflictCount,
            summary.ExactConflictCount,
            summary.AssignmentConflictCount));
        writer.WriteLine();
        writer.WriteLine("conflict_kind,count");
        foreach ((string kind, int count) in summary.ByConflictKind)
        {
            writer.WriteLine($"{Escape(kind)},{count.ToString(CultureInfo.InvariantCulture)}");
        }
    }

    private static void WriteSummaryMarkdown(MicroCheckOnlyDiagnosticsSummary summary, string path)
    {
        StringBuilder builder = new();
        builder.AppendLine("# Micro C0 Summary");
        builder.AppendLine();
        builder.AppendLine($"- raw: `{summary.RawConflictCount}`");
        builder.AppendLine($"- canonical: `{summary.CanonicalConflictCount}`");
        builder.AppendLine($"- root: `{summary.RootConflictCount}`");
        builder.AppendLine($"- episodes: `{summary.EpisodeConflictCount}`");
        builder.AppendLine($"- connected_components: `{summary.ConnectedComponentCount}`");
        builder.AppendLine($"- affected_trains: `{summary.AffectedTrainCount}`");
        builder.AppendLine($"- affected_stations: `{summary.AffectedStationCount}`");
        builder.AppendLine($"- proven_infeasible_conflicts: `{summary.ProvenInfeasibleConflictCount}`");
        builder.AppendLine($"- unknown_conflicts: `{summary.UnknownConflictCount}`");
        builder.AppendLine();
        builder.AppendLine("| conflict kind | count |");
        builder.AppendLine("|---|---:|");
        foreach ((string kind, int count) in summary.ByConflictKind)
        {
            builder.AppendLine($"| {kind} | {count} |");
        }
        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(true));
    }

    private static void WriteStationCsv(
        IReadOnlyList<MicroCheckOnlyStationSummary> stations,
        string path)
    {
        using StreamWriter writer = new(path, false, new UTF8Encoding(true));
        writer.WriteLine("station_name,total_conflict_count,exact_conflict_count,assignment_conflict_count,episode_count,affected_train_count");
        foreach (MicroCheckOnlyStationSummary station in stations)
        {
            writer.WriteLine(string.Join(',',
                Escape(station.StationName),
                station.TotalConflictCount,
                station.ExactConflictCount,
                station.AssignmentConflictCount,
                station.EpisodeCount,
                station.AffectedTrainCount));
        }
    }

    private static void WriteManifest(MicroCheckOnlyResult result, string path)
    {
        var manifest = new
        {
            mode = "micro_check_only",
            snapshotFingerprint = result.SnapshotFingerprint,
            masterValidationFeasible = result.Validation.IsFeasible,
            masterValidationViolationCount = result.Validation.Violations.Count,
            structuralEdgeDelta = result.StructuralEdgeDelta,
            cutDelta = result.CutDelta,
            promotionDelta = result.PromotionDelta,
            microStatus = result.Report.Status.ToString()
        };
        File.WriteAllText(path, JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            WriteIndented = true
        }), new UTF8Encoding(true));
    }

    private static string Escape(string value) =>
        value.Contains(',') || value.Contains('"')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
}
