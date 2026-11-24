// 被拒试验差量分析器以规范冲突身份比较候选应用前后的微观冲突集合，区分新增、消失和迁移冲突。
// 差量记录关联候选、episode 与冲突种子，用于解释总数未改善时结构割把压力转移到了何处。
using System.Globalization;
using System.Text;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

public enum RejectedTrialConflictDeltaType
{
    Resolved,
    Introduced,
    Persistent
}

internal sealed record StructuralCandidateConflictSeed(
    string CandidateId,
    string EpisodeId,
    string CandidateType,
    int FirstTrainIndex,
    int SecondTrainIndex,
    string StationName,
    string SectionName,
    StationEventSide? FirstEventSide,
    StationEventSide? SecondEventSide,
    IReadOnlyList<int> StationOrders,
    IReadOnlyList<string> StationNames);

internal static class StructuralCandidateConflictSeedFactory
{
    internal static StructuralCandidateConflictSeed Create(
        HeadwayConflictCandidate candidate,
        TimetablingInstance instance)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(instance);

        if (candidate.SectionEdge is not null)
        {
            SectionConflictEdgeRequest edge = candidate.SectionEdge;
            HashSet<string> endpointStations = instance.SectionByName.TryGetValue(
                    edge.SectionName,
                    out SectionRecord? section)
                ? new HashSet<string>([section.FromStation, section.ToStation], StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<int> stationOrders = instance.TrainStops
                .Where(stop =>
                    (stop.TrainIndex == edge.FirstTrainIndex || stop.TrainIndex == edge.SecondTrainIndex) &&
                    endpointStations.Contains(stop.StationName))
                .Select(stop => stop.StationOrder)
                .Distinct()
                .Order()
                .ToList();
            return new StructuralCandidateConflictSeed(
                candidate.CandidateKey,
                candidate.EpisodeId,
                "section",
                edge.FirstTrainIndex,
                edge.SecondTrainIndex,
                string.Empty,
                edge.SectionName,
                null,
                null,
                stationOrders,
                endpointStations.Order(StringComparer.OrdinalIgnoreCase).ToList());
        }

        StationEventConflictCandidate stationEdge = candidate.StationEdge
            ?? throw new InvalidOperationException(
                $"Structural candidate '{candidate.CandidateKey}' has neither a section nor station edge.");
        return new StructuralCandidateConflictSeed(
            candidate.CandidateKey,
            candidate.EpisodeId,
            "station",
            stationEdge.FirstTrainIndex,
            stationEdge.SecondTrainIndex,
            stationEdge.StationName,
            string.Empty,
            stationEdge.FirstSide,
            stationEdge.SecondSide,
            [stationEdge.FirstStationOrder, stationEdge.SecondStationOrder],
            [stationEdge.StationName]);
    }
}

internal sealed record CanonicalMicroConflictIdentity(
    string Key,
    string ConflictType,
    int? TrainI,
    int? TrainJ,
    int? CanonicalTrainI,
    int? CanonicalTrainJ,
    string Station,
    string Section,
    string EventI,
    string EventJ,
    string Direction,
    IReadOnlyList<int> StationOrders,
    string EpisodeId,
    double? RealizedGap,
    double? RequiredGap,
    double? Deficit,
    MicroConflictExplanation Source);

internal static class CanonicalMicroConflictIdentityFactory
{
    internal static CanonicalMicroConflictIdentity Create(MicroConflictExplanation conflict)
    {
        ArgumentNullException.ThrowIfNull(conflict);
        CanonicalTrainPair? pair = conflict.FirstTrainIndex.HasValue && conflict.SecondTrainIndex.HasValue
            ? CanonicalTrainPair.Create(conflict.FirstTrainIndex.Value, conflict.SecondTrainIndex.Value)
            : null;
        bool firstIsCanonicalLower = pair is not null &&
            conflict.FirstTrainIndex == pair.LowerTrainIndex;
        string lowerEvent = pair is null
            ? conflict.FirstEventSide?.ToString() ?? string.Empty
            : (firstIsCanonicalLower ? conflict.FirstEventSide : conflict.SecondEventSide)?.ToString() ?? string.Empty;
        string upperEvent = pair is null
            ? conflict.SecondEventSide?.ToString() ?? string.Empty
            : (firstIsCanonicalLower ? conflict.SecondEventSide : conflict.FirstEventSide)?.ToString() ?? string.Empty;
        string lowerSection = pair is null
            ? conflict.FirstSectionName ?? string.Empty
            : firstIsCanonicalLower ? conflict.FirstSectionName ?? string.Empty : conflict.SecondSectionName ?? string.Empty;
        string upperSection = pair is null
            ? conflict.SecondSectionName ?? string.Empty
            : firstIsCanonicalLower ? conflict.SecondSectionName ?? string.Empty : conflict.FirstSectionName ?? string.Empty;
        int? lowerOrder = pair is null
            ? conflict.StationOrder
            : firstIsCanonicalLower ? conflict.StationOrder : conflict.SecondStationOrder;
        int? upperOrder = pair is null
            ? conflict.SecondStationOrder
            : firstIsCanonicalLower ? conflict.SecondStationOrder : conflict.StationOrder;
        string section = string.Join('|', new[] { conflict.FirstSectionName, conflict.SecondSectionName }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase));
        double? realizedGap = ResolveEvidenceNumber(conflict.Evidence, "realized_gap");
        double? requiredGap = ResolveEvidenceNumber(conflict.Evidence, "required_gap");
        string episodeId = pair is not null && string.Equals(
                conflict.ConflictKind,
                "exact_headway_infeasible",
                StringComparison.OrdinalIgnoreCase)
            ? HeadwayConflictEpisodeAnalyzer.BuildEpisodeId(pair.LowerTrainIndex, pair.UpperTrainIndex)
            : MicroConflictMetricCalculator.BuildRootKey(conflict);
        string direction = conflict.FirstTrainIndex.HasValue
            ? $"T{conflict.FirstTrainIndex}:{conflict.FirstEventSide?.ToString() ?? "-"}->" +
              $"T{conflict.SecondTrainIndex?.ToString() ?? "-"}:{conflict.SecondEventSide?.ToString() ?? "-"}"
            : conflict.ResourceKey;
        string key = string.Join("::",
            Normalize(conflict.ConflictKind),
            Normalize(conflict.StationName),
            pair?.LowerTrainIndex.ToString(CultureInfo.InvariantCulture) ?? "-",
            pair?.UpperTrainIndex.ToString(CultureInfo.InvariantCulture) ?? "-",
            lowerOrder?.ToString(CultureInfo.InvariantCulture) ?? "-",
            upperOrder?.ToString(CultureInfo.InvariantCulture) ?? "-",
            Normalize(lowerEvent),
            Normalize(upperEvent),
            Normalize(lowerSection),
            Normalize(upperSection),
            Normalize(conflict.ResourceKey),
            Normalize(conflict.RootConflictKey ?? string.Empty));

        return new CanonicalMicroConflictIdentity(
            key,
            conflict.ConflictKind,
            conflict.FirstTrainIndex,
            conflict.SecondTrainIndex,
            pair?.LowerTrainIndex,
            pair?.UpperTrainIndex,
            conflict.StationName,
            section,
            conflict.FirstEventSide?.ToString() ?? string.Empty,
            conflict.SecondEventSide?.ToString() ?? string.Empty,
            direction,
            new[] { conflict.StationOrder, conflict.SecondStationOrder }
                .Where(order => order.HasValue)
                .Select(order => order!.Value)
                .Distinct()
                .Order()
                .ToList(),
            episodeId,
            realizedGap,
            requiredGap,
            realizedGap.HasValue && requiredGap.HasValue
                ? Math.Max(0.0, requiredGap.Value - realizedGap.Value)
                : null,
            conflict);
    }

    private static double? ResolveEvidenceNumber(string evidence, string key)
    {
        string prefix = key + "=";
        string? token = evidence.Split(',', StringSplitOptions.TrimEntries)
            .FirstOrDefault(item => item.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return token is not null && double.TryParse(
            token[prefix.Length..],
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out double value)
            ? value
            : null;
    }

    private static string Normalize(string value) => value.Trim().ToUpperInvariant();
}

public sealed record RejectedTrialConflictDeltaRow(
    int CheckerIteration,
    string TrialCandidateId,
    RejectedTrialConflictDeltaType DeltaType,
    string ConflictId,
    string ConflictType,
    int? TrainI,
    int? TrainJ,
    int? CanonicalTrainI,
    int? CanonicalTrainJ,
    string Station,
    string Section,
    string EventI,
    string EventJ,
    string Direction,
    double? RealizedGapBefore,
    double? RequiredGapBefore,
    double? DeficitBefore,
    double? RealizedGapAfter,
    double? RequiredGapAfter,
    double? DeficitAfter,
    string EpisodeIdBefore,
    string EpisodeIdAfter,
    bool SharesTrainWithSeed,
    string SharedTrainIndices,
    bool SameStation,
    bool SameSection,
    bool AdjacentStationOrSection,
    int? GeographicDistance,
    bool SameEpisodeComponent);

public sealed record RejectedTrialConflictDeltaSummary(
    int Iteration,
    string CandidateId,
    int TotalBefore,
    int TotalTrial,
    int ResolvedCount,
    int IntroducedCount,
    int PersistentCount,
    int NetChange,
    int ExactResolved,
    int ExactIntroduced,
    int AssignmentResolved,
    int AssignmentIntroduced,
    int ResolvedEpisodeCount,
    int IntroducedEpisodeCount,
    int ResolvedStationCount,
    int IntroducedStationCount,
    bool SeedCandidateConflictBefore,
    bool SeedCandidateConflictAfter,
    bool CandidateConflictResolved,
    bool CandidateConflictStillPresent);

public sealed record RejectedTrialConflictDelta(
    IReadOnlyList<RejectedTrialConflictDeltaRow> Rows,
    RejectedTrialConflictDeltaSummary Summary);

internal static class RejectedTrialConflictDeltaAnalyzer
{
    internal static RejectedTrialConflictDelta Analyze(
        int checkerIteration,
        StructuralCandidateConflictSeed seed,
        MicroFeasibilityReport acceptedReport,
        MicroFeasibilityReport trialReport)
    {
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentNullException.ThrowIfNull(acceptedReport);
        ArgumentNullException.ThrowIfNull(trialReport);
        Dictionary<string, CanonicalMicroConflictIdentity> before = BuildIdentityMap(acceptedReport.Conflicts);
        Dictionary<string, CanonicalMicroConflictIdentity> after = BuildIdentityMap(trialReport.Conflicts);
        List<RejectedTrialConflictDeltaRow> rows = [];

        foreach (string key in before.Keys.Except(after.Keys, StringComparer.OrdinalIgnoreCase)
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            rows.Add(BuildRow(checkerIteration, seed, RejectedTrialConflictDeltaType.Resolved, before[key], null));
        }
        foreach (string key in after.Keys.Except(before.Keys, StringComparer.OrdinalIgnoreCase)
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            rows.Add(BuildRow(checkerIteration, seed, RejectedTrialConflictDeltaType.Introduced, null, after[key]));
        }
        foreach (string key in before.Keys.Intersect(after.Keys, StringComparer.OrdinalIgnoreCase)
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            rows.Add(BuildRow(checkerIteration, seed, RejectedTrialConflictDeltaType.Persistent, before[key], after[key]));
        }

        bool seedBefore = before.Values.Any(conflict => MatchesSeed(conflict, seed));
        bool seedAfter = after.Values.Any(conflict => MatchesSeed(conflict, seed));
        IReadOnlyList<RejectedTrialConflictDeltaRow> resolved = rows
            .Where(row => row.DeltaType == RejectedTrialConflictDeltaType.Resolved).ToList();
        IReadOnlyList<RejectedTrialConflictDeltaRow> introduced = rows
            .Where(row => row.DeltaType == RejectedTrialConflictDeltaType.Introduced).ToList();
        RejectedTrialConflictDeltaSummary summary = new(
            checkerIteration,
            seed.CandidateId,
            before.Count,
            after.Count,
            resolved.Count,
            introduced.Count,
            rows.Count(row => row.DeltaType == RejectedTrialConflictDeltaType.Persistent),
            introduced.Count - resolved.Count,
            CountKind(resolved, "exact_headway_infeasible"),
            CountKind(introduced, "exact_headway_infeasible"),
            CountKind(resolved, "station_route_track_assignment_infeasible"),
            CountKind(introduced, "station_route_track_assignment_infeasible"),
            resolved.Select(row => row.EpisodeIdBefore).Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            introduced.Select(row => row.EpisodeIdAfter).Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            resolved.Select(row => row.Station).Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            introduced.Select(row => row.Station).Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            seedBefore,
            seedAfter,
            seedBefore && !seedAfter,
            seedAfter);
        return new RejectedTrialConflictDelta(rows, summary);
    }

    private static Dictionary<string, CanonicalMicroConflictIdentity> BuildIdentityMap(
        IReadOnlyList<MicroConflictExplanation> conflicts) => conflicts
        .Select(CanonicalMicroConflictIdentityFactory.Create)
        .GroupBy(identity => identity.Key, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

    private static RejectedTrialConflictDeltaRow BuildRow(
        int checkerIteration,
        StructuralCandidateConflictSeed seed,
        RejectedTrialConflictDeltaType deltaType,
        CanonicalMicroConflictIdentity? before,
        CanonicalMicroConflictIdentity? after)
    {
        CanonicalMicroConflictIdentity display = before ?? after
            ?? throw new InvalidOperationException("A conflict-delta row requires a before or after observation.");
        CanonicalTrainPair seedPair = CanonicalTrainPair.Create(seed.FirstTrainIndex, seed.SecondTrainIndex);
        List<int> sharedTrains = new[] { display.CanonicalTrainI, display.CanonicalTrainJ }
            .Where(train => train.HasValue)
            .Select(train => train!.Value)
            .Where(train => train == seedPair.LowerTrainIndex || train == seedPair.UpperTrainIndex)
            .Distinct()
            .Order()
            .ToList();
        int? distance = display.StationOrders.Count == 0 || seed.StationOrders.Count == 0
            ? null
            : display.StationOrders.SelectMany(conflictOrder =>
                    seed.StationOrders.Select(seedOrder => Math.Abs(conflictOrder - seedOrder)))
                .Min();
        bool sameStation = seed.StationNames.Any(station => string.Equals(
            display.Station, station, StringComparison.OrdinalIgnoreCase));
        bool sameSection = !string.IsNullOrWhiteSpace(seed.SectionName) &&
            display.Section.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Contains(seed.SectionName, StringComparer.OrdinalIgnoreCase);

        return new RejectedTrialConflictDeltaRow(
            checkerIteration,
            seed.CandidateId,
            deltaType,
            display.Key,
            display.ConflictType,
            display.TrainI,
            display.TrainJ,
            display.CanonicalTrainI,
            display.CanonicalTrainJ,
            display.Station,
            display.Section,
            display.EventI,
            display.EventJ,
            display.Direction,
            before?.RealizedGap,
            before?.RequiredGap,
            before?.Deficit,
            after?.RealizedGap,
            after?.RequiredGap,
            after?.Deficit,
            before?.EpisodeId ?? string.Empty,
            after?.EpisodeId ?? string.Empty,
            sharedTrains.Count > 0,
            string.Join('|', sharedTrains),
            sameStation,
            sameSection,
            sameStation || sameSection || distance is <= 1,
            distance,
            string.Equals(display.EpisodeId, seed.EpisodeId, StringComparison.OrdinalIgnoreCase));
    }

    private static bool MatchesSeed(
        CanonicalMicroConflictIdentity conflict,
        StructuralCandidateConflictSeed seed)
    {
        CanonicalTrainPair seedPair = CanonicalTrainPair.Create(seed.FirstTrainIndex, seed.SecondTrainIndex);
        if (conflict.CanonicalTrainI != seedPair.LowerTrainIndex ||
            conflict.CanonicalTrainJ != seedPair.UpperTrainIndex)
        {
            return false;
        }
        if (string.Equals(seed.CandidateType, "section", StringComparison.OrdinalIgnoreCase))
        {
            return conflict.Section.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Contains(seed.SectionName, StringComparer.OrdinalIgnoreCase);
        }
        if (!string.Equals(conflict.Station, seed.StationName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        StationEventSide? seedLowerSide = seed.FirstTrainIndex == seedPair.LowerTrainIndex
            ? seed.FirstEventSide
            : seed.SecondEventSide;
        StationEventSide? seedUpperSide = seed.FirstTrainIndex == seedPair.LowerTrainIndex
            ? seed.SecondEventSide
            : seed.FirstEventSide;
        MicroConflictExplanation source = conflict.Source;
        StationEventSide? conflictLowerSide = source.FirstTrainIndex == seedPair.LowerTrainIndex
            ? source.FirstEventSide
            : source.SecondEventSide;
        StationEventSide? conflictUpperSide = source.FirstTrainIndex == seedPair.LowerTrainIndex
            ? source.SecondEventSide
            : source.FirstEventSide;
        return (!seedLowerSide.HasValue || !conflictLowerSide.HasValue || seedLowerSide == conflictLowerSide) &&
               (!seedUpperSide.HasValue || !conflictUpperSide.HasValue || seedUpperSide == conflictUpperSide);
    }

    private static int CountKind(
        IEnumerable<RejectedTrialConflictDeltaRow> rows,
        string conflictKind) => rows.Count(row => string.Equals(
            row.ConflictType, conflictKind, StringComparison.OrdinalIgnoreCase));
}

internal static class RejectedTrialConflictDeltaTrigger
{
    internal static bool ShouldAnalyze(
        bool hasIncumbent,
        int masterValidatorViolationCount,
        bool globalMicroCheckCompleted,
        bool acceptedByExistingRule,
        int conflictCountBefore,
        int conflictCountAfter)
    {
        if (masterValidatorViolationCount < 0 || conflictCountBefore < 0 || conflictCountAfter < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(masterValidatorViolationCount));
        }
        return hasIncumbent &&
               masterValidatorViolationCount == 0 &&
               globalMicroCheckCompleted &&
               !acceptedByExistingRule &&
               conflictCountAfter >= conflictCountBefore;
    }
}

internal static class RejectedTrialConflictDeltaReporter
{
    private const string DetailCsvName = "rejected_trial_conflict_delta.csv";
    private const string DetailMarkdownName = "rejected_trial_conflict_delta.md";
    private const string SummaryCsvName = "rejected_trial_conflict_delta_summary.csv";
    private const string SummaryMarkdownName = "rejected_trial_conflict_delta_summary.md";

    internal static void Write(
        IReadOnlyList<RejectedTrialConflictDelta> diagnostics,
        string outputDirectory)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        Directory.CreateDirectory(outputDirectory);
        WriteDetailCsv(diagnostics.SelectMany(item => item.Rows).ToList(), Path.Combine(outputDirectory, DetailCsvName));
        WriteDetailMarkdown(diagnostics, Path.Combine(outputDirectory, DetailMarkdownName));
        WriteSummaryCsv(diagnostics.Select(item => item.Summary).ToList(), Path.Combine(outputDirectory, SummaryCsvName));
        WriteSummaryMarkdown(diagnostics.Select(item => item.Summary).ToList(), Path.Combine(outputDirectory, SummaryMarkdownName));
    }

    private static void WriteDetailCsv(IReadOnlyList<RejectedTrialConflictDeltaRow> rows, string path)
    {
        using StreamWriter writer = new(path, false, new UTF8Encoding(true));
        writer.WriteLine("checker_iteration,trial_candidate_id,delta_type,conflict_id,conflict_type,train_i,train_j,canonical_train_i,canonical_train_j,station,section,event_i,event_j,direction,realized_gap_before,required_gap_before,deficit_before,realized_gap_after,required_gap_after,deficit_after,episode_id_before,episode_id_after,shares_train_with_seed,shared_train_indices,same_station,same_section,adjacent_station_or_section,geographic_distance,same_episode_component");
        foreach (RejectedTrialConflictDeltaRow row in rows)
        {
            writer.WriteLine(string.Join(',',
                row.CheckerIteration,
                Escape(row.TrialCandidateId),
                row.DeltaType,
                Escape(row.ConflictId),
                Escape(row.ConflictType),
                Format(row.TrainI), Format(row.TrainJ),
                Format(row.CanonicalTrainI), Format(row.CanonicalTrainJ),
                Escape(row.Station), Escape(row.Section), Escape(row.EventI), Escape(row.EventJ), Escape(row.Direction),
                Format(row.RealizedGapBefore), Format(row.RequiredGapBefore), Format(row.DeficitBefore),
                Format(row.RealizedGapAfter), Format(row.RequiredGapAfter), Format(row.DeficitAfter),
                Escape(row.EpisodeIdBefore), Escape(row.EpisodeIdAfter),
                row.SharesTrainWithSeed, Escape(row.SharedTrainIndices), row.SameStation, row.SameSection,
                row.AdjacentStationOrSection, Format(row.GeographicDistance), row.SameEpisodeComponent));
        }
    }

    private static void WriteSummaryCsv(IReadOnlyList<RejectedTrialConflictDeltaSummary> summaries, string path)
    {
        using StreamWriter writer = new(path, false, new UTF8Encoding(true));
        writer.WriteLine("iteration,candidate_id,total_before,total_trial,resolved_count,introduced_count,persistent_count,net_change,exact_resolved,exact_introduced,assignment_resolved,assignment_introduced,resolved_episode_count,introduced_episode_count,resolved_station_count,introduced_station_count,seed_candidate_conflict_before,seed_candidate_conflict_after,candidate_conflict_resolved,candidate_conflict_still_present");
        foreach (RejectedTrialConflictDeltaSummary item in summaries)
        {
            writer.WriteLine(string.Join(',',
                item.Iteration, Escape(item.CandidateId), item.TotalBefore, item.TotalTrial,
                item.ResolvedCount, item.IntroducedCount, item.PersistentCount, item.NetChange,
                item.ExactResolved, item.ExactIntroduced, item.AssignmentResolved, item.AssignmentIntroduced,
                item.ResolvedEpisodeCount, item.IntroducedEpisodeCount,
                item.ResolvedStationCount, item.IntroducedStationCount,
                item.SeedCandidateConflictBefore, item.SeedCandidateConflictAfter,
                item.CandidateConflictResolved, item.CandidateConflictStillPresent));
        }
    }

    private static void WriteDetailMarkdown(
        IReadOnlyList<RejectedTrialConflictDelta> diagnostics,
        string path)
    {
        StringBuilder builder = new();
        builder.AppendLine("# Rejected Trial Conflict Delta").AppendLine();
        foreach (RejectedTrialConflictDelta diagnostic in diagnostics)
        {
            RejectedTrialConflictDeltaSummary summary = diagnostic.Summary;
            builder.AppendLine($"## Iteration {summary.Iteration}: `{summary.CandidateId}`").AppendLine();
            builder.AppendLine("| Delta | Conflict ID | Type | Train i | Train j | Canonical i | Canonical j | Station | Section | Event i | Event j | Direction | Realized before | Required before | Deficit before | Realized after | Required after | Deficit after | Episode before | Episode after | Shares seed train | Shared trains | Same station | Same section | Adjacent | Distance | Same episode |" );
            builder.AppendLine("|---|---|---|---:|---:|---:|---:|---|---|---|---|---|---:|---:|---:|---:|---:|---:|---|---|---|---|---|---|---|---:|---|");
            foreach (RejectedTrialConflictDeltaRow row in diagnostic.Rows)
            {
                builder.AppendLine(
                    $"| {row.DeltaType} | `{Markdown(row.ConflictId)}` | {Markdown(row.ConflictType)} | " +
                    $"{Format(row.TrainI)} | {Format(row.TrainJ)} | {Format(row.CanonicalTrainI)} | {Format(row.CanonicalTrainJ)} | " +
                    $"{Markdown(row.Station)} | {Markdown(row.Section)} | {Markdown(row.EventI)} | {Markdown(row.EventJ)} | {Markdown(row.Direction)} | " +
                    $"{Format(row.RealizedGapBefore)} | {Format(row.RequiredGapBefore)} | {Format(row.DeficitBefore)} | " +
                    $"{Format(row.RealizedGapAfter)} | {Format(row.RequiredGapAfter)} | {Format(row.DeficitAfter)} | " +
                    $"{Markdown(row.EpisodeIdBefore)} | {Markdown(row.EpisodeIdAfter)} | {row.SharesTrainWithSeed} | " +
                    $"{Markdown(row.SharedTrainIndices)} | {row.SameStation} | {row.SameSection} | " +
                    $"{row.AdjacentStationOrSection} | {Format(row.GeographicDistance)} | {row.SameEpisodeComponent} |");
            }
            builder.AppendLine();
        }
        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(true));
    }

    private static void WriteSummaryMarkdown(
        IReadOnlyList<RejectedTrialConflictDeltaSummary> summaries,
        string path)
    {
        StringBuilder builder = new();
        builder.AppendLine("# Rejected Trial Conflict Delta Summary").AppendLine();
        builder.AppendLine("| Iteration | Candidate | Before | Trial | Resolved | Introduced | Persistent | Net | Exact resolved | Exact introduced | Assignment resolved | Assignment introduced | Resolved episodes | Introduced episodes | Resolved stations | Introduced stations | Seed before | Seed after | Candidate resolved | Candidate still present |");
        builder.AppendLine("|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|---|---|---|");
        foreach (RejectedTrialConflictDeltaSummary item in summaries)
        {
            builder.AppendLine(
                $"| {item.Iteration} | `{item.CandidateId}` | {item.TotalBefore} | {item.TotalTrial} | " +
                $"{item.ResolvedCount} | {item.IntroducedCount} | {item.PersistentCount} | {item.NetChange} | " +
                $"{item.ExactResolved} | {item.ExactIntroduced} | {item.AssignmentResolved} | {item.AssignmentIntroduced} | " +
                $"{item.ResolvedEpisodeCount} | {item.IntroducedEpisodeCount} | {item.ResolvedStationCount} | {item.IntroducedStationCount} | " +
                $"{item.SeedCandidateConflictBefore} | {item.SeedCandidateConflictAfter} | " +
                $"{item.CandidateConflictResolved} | {item.CandidateConflictStillPresent} |");
        }
        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(true));
    }

    private static string Format(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
    private static string Format(double? value) => value?.ToString("F3", CultureInfo.InvariantCulture) ?? string.Empty;
    private static string Escape(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
    private static string Markdown(string value) => value
        .Replace("|", "\\|", StringComparison.Ordinal)
        .Replace("\r\n", "<br>", StringComparison.Ordinal)
        .Replace("\n", "<br>", StringComparison.Ordinal);
}
