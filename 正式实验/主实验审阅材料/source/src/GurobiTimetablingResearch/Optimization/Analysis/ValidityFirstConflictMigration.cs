using System.Globalization;
using System.Text;

namespace GurobiTimetablingResearch.Optimization;

public sealed record ValidityFirstConflictMigrationLink(
    string ResolvedConflictId,
    string IntroducedConflictId,
    string IntroducedStationName,
    IReadOnlyList<int> SharedTrainIndices,
    bool SameStation,
    bool AdjacentStationOrder,
    string ResolvedProofContext,
    string IntroducedProofContext);

public sealed record ValidityFirstConflictMigration(
    int Iteration,
    int ResolvedCount,
    int IntroducedCount,
    int PersistentCount,
    IReadOnlyList<ValidityFirstConflictMigrationLink> Links,
    IReadOnlyList<string> PriorityStationNames);

/// <summary>
/// 用不含具体时刻值的规范冲突身份比较相邻候选，并把共享列车或邻站关系保存为下一轮保护范围线索。
/// </summary>
public static class ValidityFirstConflictMigrationAnalyzer
{
    public static ValidityFirstConflictMigration Analyze(
        int iteration,
        MicroFeasibilityReport beforeReport,
        MicroFeasibilityReport afterReport)
    {
        ArgumentNullException.ThrowIfNull(beforeReport);
        ArgumentNullException.ThrowIfNull(afterReport);
        Dictionary<string, CanonicalMicroConflictIdentity> before = BuildMap(beforeReport.Conflicts);
        Dictionary<string, CanonicalMicroConflictIdentity> after = BuildMap(afterReport.Conflicts);
        IReadOnlyList<CanonicalMicroConflictIdentity> resolved = before.Keys
            .Except(after.Keys, StringComparer.OrdinalIgnoreCase)
            .Select(key => before[key])
            .ToList();
        IReadOnlyList<CanonicalMicroConflictIdentity> introduced = after.Keys
            .Except(before.Keys, StringComparer.OrdinalIgnoreCase)
            .Select(key => after[key])
            .ToList();
        List<ValidityFirstConflictMigrationLink> links = [];
        foreach (CanonicalMicroConflictIdentity oldConflict in resolved)
        {
            foreach (CanonicalMicroConflictIdentity newConflict in introduced)
            {
                List<int> sharedTrains = new[]
                    {
                        oldConflict.CanonicalTrainI,
                        oldConflict.CanonicalTrainJ
                    }
                    .Where(value => value.HasValue)
                    .Select(value => value!.Value)
                    .Intersect(new[]
                    {
                        newConflict.CanonicalTrainI,
                        newConflict.CanonicalTrainJ
                    }.Where(value => value.HasValue).Select(value => value!.Value))
                    .Distinct()
                    .Order()
                    .ToList();
                bool sameStation = !string.IsNullOrWhiteSpace(oldConflict.Station) &&
                    string.Equals(oldConflict.Station, newConflict.Station, StringComparison.OrdinalIgnoreCase);
                bool adjacentStationOrder = oldConflict.StationOrders.Count > 0 &&
                    newConflict.StationOrders.Count > 0 &&
                    oldConflict.StationOrders.SelectMany(oldOrder =>
                            newConflict.StationOrders.Select(newOrder => Math.Abs(oldOrder - newOrder)))
                        .Min() <= 1;
                if (sharedTrains.Count == 0 || (!sameStation && !adjacentStationOrder))
                {
                    continue;
                }

                links.Add(new ValidityFirstConflictMigrationLink(
                    oldConflict.Key,
                    newConflict.Key,
                    newConflict.Station,
                    sharedTrains,
                    sameStation,
                    adjacentStationOrder,
                    oldConflict.Source.Evidence,
                    newConflict.Source.Evidence));
            }
        }

        IReadOnlyList<string> priorityStations = links
            .Select(link => link.IntroducedStationName)
            .Where(station => !string.IsNullOrWhiteSpace(station))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new ValidityFirstConflictMigration(
            iteration,
            resolved.Count,
            introduced.Count,
            before.Keys.Intersect(after.Keys, StringComparer.OrdinalIgnoreCase).Count(),
            links,
            priorityStations);
    }

    private static Dictionary<string, CanonicalMicroConflictIdentity> BuildMap(
        IReadOnlyList<MicroConflictExplanation> conflicts) => conflicts
        .Select(CanonicalMicroConflictIdentityFactory.Create)
        .GroupBy(identity => identity.Key, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// 逐轮保存冲突差分和迁移证明，使下一轮范围选择与最终报告都能追溯冲突从何处移入。
/// </summary>
public static class ValidityFirstConflictMigrationReporter
{
    public static void Append(ValidityFirstConflictMigration migration, string outputDirectory)
    {
        ArgumentNullException.ThrowIfNull(migration);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        Directory.CreateDirectory(outputDirectory);
        string path = Path.Combine(outputDirectory, "validity_first_conflict_migration.csv");
        bool writeHeader = !File.Exists(path);
        using StreamWriter writer = new(path, true, new UTF8Encoding(false));
        if (writeHeader)
        {
            writer.WriteLine(
                "iteration,resolved_count,introduced_count,persistent_count,resolved_conflict_id," +
                "introduced_conflict_id,introduced_station,shared_trains,same_station,adjacent_station_order," +
                "resolved_proof_context,introduced_proof_context");
        }

        if (migration.Links.Count == 0)
        {
            writer.WriteLine(string.Join(',',
                migration.Iteration.ToString(CultureInfo.InvariantCulture),
                migration.ResolvedCount.ToString(CultureInfo.InvariantCulture),
                migration.IntroducedCount.ToString(CultureInfo.InvariantCulture),
                migration.PersistentCount.ToString(CultureInfo.InvariantCulture),
                "", "", "", "", false, false, "", ""));
            return;
        }

        foreach (ValidityFirstConflictMigrationLink link in migration.Links)
        {
            writer.WriteLine(string.Join(',',
                migration.Iteration.ToString(CultureInfo.InvariantCulture),
                migration.ResolvedCount.ToString(CultureInfo.InvariantCulture),
                migration.IntroducedCount.ToString(CultureInfo.InvariantCulture),
                migration.PersistentCount.ToString(CultureInfo.InvariantCulture),
                Escape(link.ResolvedConflictId),
                Escape(link.IntroducedConflictId),
                Escape(link.IntroducedStationName),
                Escape(string.Join('|', link.SharedTrainIndices)),
                link.SameStation,
                link.AdjacentStationOrder,
                Escape(link.ResolvedProofContext),
                Escape(link.IntroducedProofContext)));
        }
    }

    private static string Escape(string value) =>
        value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
}
