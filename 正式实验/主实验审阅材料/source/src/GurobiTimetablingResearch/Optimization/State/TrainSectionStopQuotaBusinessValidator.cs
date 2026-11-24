using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

public sealed record TrainSectionStopQuotaBusinessFinding(
    string RuleId,
    int TrainIndex,
    string SectionStartStation,
    string SectionEndStation,
    int MinStops,
    int MaxStops,
    int? ActualStops,
    int Shortfall,
    int Excess,
    string FindingKind,
    string Detail,
    IReadOnlyList<string> CountedStopIds);

/// <summary>
/// Solver-independent business check for train section stop quotas. This intentionally
/// does not call the modeling-side coverage resolver, so a shared extraction defect
/// cannot make both the constraint builder and the acceptance check pass together.
/// </summary>
public static class TrainSectionStopQuotaBusinessValidator
{
    public static IReadOnlyList<TrainSectionStopQuotaBusinessFinding> Validate(
        TimetablingInstance instance,
        IReadOnlyDictionary<string, int> stopValues)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(stopValues);
        Dictionary<int, TrainRecord> trains = instance.Trains.ToDictionary(train => train.TrainIndex);
        List<TrainSectionStopQuotaBusinessFinding> findings = [];
        for (int index = 0; index < instance.TrainSectionStopQuotaRules.Count; index++)
        {
            TrainSectionStopQuotaRule rule = instance.TrainSectionStopQuotaRules[index];
            string ruleId = $"section-stop-{index + 1:D3}";
            if (rule.MinStops < 0 || rule.MaxStops < rule.MinStops)
            {
                findings.Add(MappingFinding(ruleId, rule, "INVALID_BOUNDS",
                    $"Invalid bounds [{rule.MinStops}, {rule.MaxStops}]."));
                continue;
            }
            if (!trains.TryGetValue(rule.TrainIndex, out TrainRecord? train))
            {
                findings.Add(MappingFinding(ruleId, rule, "MISSING_TRAIN",
                    $"Train {rule.TrainIndex} is absent from the validation instance."));
                continue;
            }

            int[] startMatches = MatchingIndices(train, rule.SectionStartStation);
            int[] endMatches = MatchingIndices(train, rule.SectionEndStation);
            if (startMatches.Length != 1 || endMatches.Length != 1 ||
                string.Equals(rule.SectionStartStation, rule.SectionEndStation, StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(MappingFinding(ruleId, rule, "ENDPOINT_MAPPING_ERROR",
                    $"Named start matches={startMatches.Length}; named end matches={endMatches.Length}."));
                continue;
            }

            int start = startMatches[0];
            int end = endMatches[0];
            bool reversed = start > end;
            int lower = Math.Min(start, end);
            int upper = Math.Max(start, end);
            bool includeLower = reversed ? rule.IncludeEndStation : rule.IncludeStartStation;
            bool includeUpper = reversed ? rule.IncludeStartStation : rule.IncludeEndStation;
            int first = lower + (includeLower ? 0 : 1);
            int last = upper - (includeUpper ? 0 : 1);
            TrainStopRecord[] counted = first <= last
                ? train.OrderedStops.Skip(first).Take(last - first + 1).ToArray()
                : [];
            string[] missingValues = counted
                .Where(stop => !stopValues.ContainsKey(stop.StopId))
                .Select(stop => stop.StopId)
                .ToArray();
            if (missingValues.Length > 0)
            {
                findings.Add(new TrainSectionStopQuotaBusinessFinding(
                    ruleId, rule.TrainIndex, rule.SectionStartStation, rule.SectionEndStation,
                    rule.MinStops, rule.MaxStops, null, 0, 0, "MISSING_STOP_VALUE",
                    $"Missing stop values: {string.Join('|', missingValues)}.",
                    counted.Select(stop => stop.StopId).ToArray()));
                continue;
            }

            int actual = counted.Sum(stop => stopValues[stop.StopId] == 1 ? 1 : 0);
            int shortfall = Math.Max(0, rule.MinStops - actual);
            int excess = Math.Max(0, actual - rule.MaxStops);
            if (shortfall > 0 || excess > 0)
            {
                findings.Add(new TrainSectionStopQuotaBusinessFinding(
                    ruleId, rule.TrainIndex, rule.SectionStartStation, rule.SectionEndStation,
                    rule.MinStops, rule.MaxStops, actual, shortfall, excess, "QUOTA_VIOLATION",
                    $"Actual stop count {actual} is outside [{rule.MinStops}, {rule.MaxStops}].",
                    counted.Select(stop => stop.StopId).ToArray()));
            }
        }
        return findings;
    }

    private static int[] MatchingIndices(TrainRecord train, string station) => train.OrderedStops
        .Select((stop, index) => (stop, index))
        .Where(item => string.Equals(item.stop.StationName, station, StringComparison.OrdinalIgnoreCase))
        .Select(item => item.index)
        .ToArray();

    private static TrainSectionStopQuotaBusinessFinding MappingFinding(
        string ruleId,
        TrainSectionStopQuotaRule rule,
        string kind,
        string detail) => new(
            ruleId, rule.TrainIndex, rule.SectionStartStation, rule.SectionEndStation,
            rule.MinStops, rule.MaxStops, null, 0, 0, kind, detail, []);
}
