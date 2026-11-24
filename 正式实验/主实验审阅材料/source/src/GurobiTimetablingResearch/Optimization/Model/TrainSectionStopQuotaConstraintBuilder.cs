using System.Globalization;
using System.Text;
using Gurobi;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

public sealed record TrainSectionStopQuotaCoverage(
    string RuleId,
    TrainSectionStopQuotaRule Rule,
    TrainRecord Train,
    IReadOnlyList<TrainStopRecord> CountedStops,
    bool ReversedInTrainOrder,
    int NamedStartIndex,
    int NamedEndIndex);

public sealed record TrainSectionStopQuotaConstruction(
    string RuleId,
    int TrainIndex,
    string SectionStartStation,
    string SectionEndStation,
    bool IncludeStartStation,
    bool IncludeEndStation,
    bool ReversedInTrainOrder,
    int MinStops,
    int MaxStops,
    IReadOnlyList<TrainStopRecord> CountedStops,
    string MinimumConstraintName,
    string MaximumConstraintName);

/// <summary>
/// Resolves the two named section boundaries against a train's actual ordered traversal.
/// The CSV has no direction field: inclusion flags stay attached to the named boundary
/// even when the train encounters the named end before the named start.
/// </summary>
public static class TrainSectionStopQuotaCoverageResolver
{
    public static TrainSectionStopQuotaCoverage Resolve(
        TrainRecord train,
        TrainSectionStopQuotaRule rule,
        string ruleId)
    {
        ArgumentNullException.ThrowIfNull(train);
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleId);
        if (train.TrainIndex != rule.TrainIndex)
        {
            throw new InvalidDataException(
                $"Section-stop rule '{ruleId}' targets train {rule.TrainIndex}, but was resolved against train {train.TrainIndex}.");
        }
        if (rule.MinStops < 0 || rule.MaxStops < rule.MinStops)
        {
            throw new InvalidDataException(
                $"Section-stop rule '{ruleId}' has invalid bounds [{rule.MinStops}, {rule.MaxStops}].");
        }
        if (string.Equals(
                rule.SectionStartStation,
                rule.SectionEndStation,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Section-stop rule '{ruleId}' uses the same station for both boundaries: '{rule.SectionStartStation}'.");
        }

        int[] namedStartMatches = train.OrderedStops
            .Select((stop, index) => (stop, index))
            .Where(item => string.Equals(
                item.stop.StationName,
                rule.SectionStartStation,
                StringComparison.OrdinalIgnoreCase))
            .Select(item => item.index)
            .ToArray();
        int[] namedEndMatches = train.OrderedStops
            .Select((stop, index) => (stop, index))
            .Where(item => string.Equals(
                item.stop.StationName,
                rule.SectionEndStation,
                StringComparison.OrdinalIgnoreCase))
            .Select(item => item.index)
            .ToArray();
        if (namedStartMatches.Length != 1 || namedEndMatches.Length != 1)
        {
            throw new InvalidDataException(
                $"Section-stop rule '{ruleId}' cannot be mapped uniquely for train {train.TrainIndex}: " +
                $"named_start='{rule.SectionStartStation}' matches={namedStartMatches.Length}, " +
                $"named_end='{rule.SectionEndStation}' matches={namedEndMatches.Length}.");
        }

        int namedStartIndex = namedStartMatches[0];
        int namedEndIndex = namedEndMatches[0];
        bool reversed = namedStartIndex > namedEndIndex;
        int lowerBoundaryIndex = Math.Min(namedStartIndex, namedEndIndex);
        int upperBoundaryIndex = Math.Max(namedStartIndex, namedEndIndex);
        bool includeLowerBoundary = reversed ? rule.IncludeEndStation : rule.IncludeStartStation;
        bool includeUpperBoundary = reversed ? rule.IncludeStartStation : rule.IncludeEndStation;
        int first = lowerBoundaryIndex + (includeLowerBoundary ? 0 : 1);
        int last = upperBoundaryIndex - (includeUpperBoundary ? 0 : 1);
        IReadOnlyList<TrainStopRecord> countedStops = first <= last
            ? train.OrderedStops.Skip(first).Take(last - first + 1).ToArray()
            : Array.Empty<TrainStopRecord>();

        return new TrainSectionStopQuotaCoverage(
            ruleId,
            rule,
            train,
            countedStops,
            reversed,
            namedStartIndex,
            namedEndIndex);
    }
}

public static class TrainSectionStopQuotaConstraintBuilder
{
    public static IReadOnlyList<TrainSectionStopQuotaConstruction> Plan(TimetablingInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        Dictionary<int, TrainRecord> trains = instance.Trains.ToDictionary(train => train.TrainIndex);
        List<TrainSectionStopQuotaConstruction> result = [];
        for (int index = 0; index < instance.TrainSectionStopQuotaRules.Count; index++)
        {
            TrainSectionStopQuotaRule rule = instance.TrainSectionStopQuotaRules[index];
            string ruleId = $"section-stop-{index + 1:D3}";
            if (!trains.TryGetValue(rule.TrainIndex, out TrainRecord? train))
            {
                throw new InvalidDataException(
                    $"Section-stop rule '{ruleId}' references missing train {rule.TrainIndex}.");
            }
            TrainSectionStopQuotaCoverage coverage =
                TrainSectionStopQuotaCoverageResolver.Resolve(train, rule, ruleId);
            string suffix = $"t{rule.TrainIndex}_{Sanitize(rule.SectionStartStation)}_{Sanitize(rule.SectionEndStation)}_r{index + 1:D3}";
            result.Add(new TrainSectionStopQuotaConstruction(
                ruleId,
                rule.TrainIndex,
                rule.SectionStartStation,
                rule.SectionEndStation,
                rule.IncludeStartStation,
                rule.IncludeEndStation,
                coverage.ReversedInTrainOrder,
                rule.MinStops,
                rule.MaxStops,
                coverage.CountedStops,
                $"section_stop_min_{suffix}",
                $"section_stop_max_{suffix}"));
        }
        return result;
    }

    public static IReadOnlyList<TrainSectionStopQuotaConstruction> Apply(
        GRBModel model,
        TimetablingInstance instance,
        IReadOnlyDictionary<string, GRBVar> stopVars)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(stopVars);
        IReadOnlyList<TrainSectionStopQuotaConstruction> plan = Plan(instance);
        foreach (TrainSectionStopQuotaConstruction construction in plan)
        {
            GRBLinExpr stopCount = new();
            foreach (TrainStopRecord stop in construction.CountedStops)
            {
                if (!stopVars.TryGetValue(stop.StopId, out GRBVar? stopVariable))
                {
                    throw new InvalidDataException(
                        $"Section-stop rule '{construction.RuleId}' has no stop variable for '{stop.StopId}'.");
                }
                stopCount.AddTerm(1.0, stopVariable);
            }
            model.AddConstr(
                stopCount,
                GRB.GREATER_EQUAL,
                construction.MinStops,
                construction.MinimumConstraintName);
            model.AddConstr(
                stopCount,
                GRB.LESS_EQUAL,
                construction.MaxStops,
                construction.MaximumConstraintName);
        }
        return plan;
    }

    private static string Sanitize(string value) => new(
        value.Select(character => char.IsLetterOrDigit(character) ? character : '_').ToArray());
}

public static class TrainSectionStopQuotaConstructionReporter
{
    public static IReadOnlyList<TrainSectionStopQuotaConstruction> Write(
        TimetablingInstance instance,
        string path)
    {
        IReadOnlyList<TrainSectionStopQuotaConstruction> rows =
            TrainSectionStopQuotaConstraintBuilder.Plan(instance);
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
        using StreamWriter writer = new(path, false, new UTF8Encoding(true));
        WriteRow(
            writer,
            "rule_id",
            "train_index",
            "original_start_station",
            "original_end_station",
            "include_start_station",
            "include_end_station",
            "reversed_in_train_order",
            "counted_stop_ids",
            "counted_station_names",
            "min_stops",
            "max_stops",
            "applicability",
            "minimum_constraint",
            "maximum_constraint");
        foreach (TrainSectionStopQuotaConstruction row in rows)
        {
            WriteRow(
                writer,
                row.RuleId,
                row.TrainIndex,
                row.SectionStartStation,
                row.SectionEndStation,
                row.IncludeStartStation,
                row.IncludeEndStation,
                row.ReversedInTrainOrder,
                string.Join('|', row.CountedStops.Select(stop => stop.StopId)),
                string.Join('|', row.CountedStops.Select(stop => stop.StationName)),
                row.MinStops,
                row.MaxStops,
                "APPLICABLE",
                row.MinimumConstraintName,
                row.MaximumConstraintName);
        }
        return rows;
    }

    private static void WriteRow(StreamWriter writer, params object?[] values) =>
        writer.WriteLine(string.Join(',', values.Select(value =>
            $"\"{Convert.ToString(value, CultureInfo.InvariantCulture)?.Replace("\"", "\"\"") ?? string.Empty}\"")));
}
