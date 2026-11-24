using Gurobi;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

internal enum MacroQuotaCategory
{
    None,
    LeftOutside,
    InWindow,
    RightOutside
}

internal static class MacroDiscreteStateResolver
{
    internal static MacroQuotaCategory ResolveQuotaCategory(
        bool isStop,
        double eventTimeSeconds,
        double startTimeSeconds,
        double endTimeSeconds,
        double feasibilityTolerance = 1e-6)
    {
        return StationQuotaWindowPolicy.Classify(isStop, eventTimeSeconds, startTimeSeconds, endTimeSeconds, feasibilityTolerance);
    }

    internal static int ResolveMaintenanceBeforeValue(
        double arrivalAtSectionEndSeconds,
        double departureAtSectionStartSeconds,
        double startTimeSeconds,
        double endTimeSeconds,
        double feasibilityTolerance = 1e-6)
    {
        bool before = arrivalAtSectionEndSeconds <= startTimeSeconds + feasibilityTolerance;
        bool after = departureAtSectionStartSeconds >= endTimeSeconds - feasibilityTolerance;
        if (before)
            return 1;
        if (after)
            return 0;
        throw new InvalidDataException(
            $"Candidate overlaps maintenance window [{startTimeSeconds:R},{endTimeSeconds:R}]: " +
            $"departure={departureAtSectionStartSeconds:R}, arrival={arrivalAtSectionEndSeconds:R}.");
    }
}

internal sealed record MicroPatternTemporalCondition(
    string ConditionId,
    string StationName,
    string FirstStopId,
    StationEventSide FirstEventSide,
    double FirstOffsetSeconds,
    string SecondStopId,
    StationEventSide SecondEventSide,
    double SecondOffsetSeconds,
    double MinimumGapSeconds,
    string BusinessRuleId)
{
    internal bool IsSatisfied(
        IReadOnlyDictionary<string, double> arrivals,
        IReadOnlyDictionary<string, double> departures,
        double toleranceSeconds)
    {
        double first = Read(FirstStopId, FirstEventSide, arrivals, departures) + FirstOffsetSeconds;
        double second = Read(SecondStopId, SecondEventSide, arrivals, departures) + SecondOffsetSeconds;
        return second - first + toleranceSeconds >= MinimumGapSeconds;
    }

    private static double Read(
        string stopId,
        StationEventSide side,
        IReadOnlyDictionary<string, double> arrivals,
        IReadOnlyDictionary<string, double> departures)
    {
        IReadOnlyDictionary<string, double> source = side == StationEventSide.Incoming ? arrivals : departures;
        return source.TryGetValue(stopId, out double value)
            ? value
            : throw new InvalidDataException($"Temporal condition references missing {side} time for '{stopId}'.");
    }
}

internal enum ContinuousCoordinationProofDisposition
{
    HardProof,
    NeighborhoodHintOnly
}

internal static class ContinuousCoordinationProofPolicy
{
    internal static ContinuousCoordinationProofDisposition Classify(int statusCode) =>
        statusCode == GRB.Status.INFEASIBLE
            ? ContinuousCoordinationProofDisposition.HardProof
            : ContinuousCoordinationProofDisposition.NeighborhoodHintOnly;
}

internal sealed record MicroStationSupport(
    string StationName,
    IReadOnlySet<int> TrainIndices,
    IReadOnlyList<string> PatternIds);

internal sealed record MicroBusinessCoupling(
    string FirstStation,
    string SecondStation,
    string RuleId,
    int TrainIndex,
    string RuleKind);

internal sealed record MicroStationPairSelection(
    string FirstStation,
    string SecondStation,
    string RuleId,
    int TrainIndex,
    string RuleKind);

internal static class MicroBusinessCoupledPairSelector
{
    internal static MicroStationPairSelection? Select(
        IReadOnlyList<MicroStationSupport> support,
        IReadOnlyList<MicroBusinessCoupling> couplings,
        IReadOnlyDictionary<string, int>? propagationRankByStation = null)
    {
        Dictionary<string, MicroStationSupport> byStation = support.ToDictionary(
            item => item.StationName,
            StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, int> rank = propagationRankByStation ??
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        return couplings
            .Where(item => byStation.TryGetValue(item.FirstStation, out MicroStationSupport? first) &&
                           byStation.TryGetValue(item.SecondStation, out MicroStationSupport? second) &&
                           first.TrainIndices.Contains(item.TrainIndex) &&
                           second.TrainIndices.Contains(item.TrainIndex))
            .GroupBy(item => (First: item.FirstStation.ToUpperInvariant(), Second: item.SecondStation.ToUpperInvariant()))
            .OrderByDescending(group => group.Select(item => item.RuleId).Distinct(StringComparer.OrdinalIgnoreCase).Count())
            .ThenBy(group => byStation[group.First().FirstStation].TrainIndices.Count +
                             byStation[group.First().SecondStation].TrainIndices.Count)
            .ThenBy(group => rank.GetValueOrDefault(group.First().FirstStation, int.MaxValue / 4) +
                             rank.GetValueOrDefault(group.First().SecondStation, int.MaxValue / 4))
            .ThenBy(group => group.Key.First, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Key.Second, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(item => item.RuleId, StringComparer.Ordinal).First())
            .Select(item => new MicroStationPairSelection(
                item.FirstStation,
                item.SecondStation,
                item.RuleId,
                item.TrainIndex,
                item.RuleKind))
            .FirstOrDefault();
    }
}

internal static class MicroStationPairQueueBuilder
{
    internal static IReadOnlyList<MicroStationPairSelection> BuildFallbackQueue(
        IReadOnlyList<string> eligibleStations,
        IReadOnlyList<MicroBusinessCoupling> couplings,
        IReadOnlyDictionary<string, int>? propagationRankByStation = null)
    {
        HashSet<string> eligible = eligibleStations.ToHashSet(StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, int> rank = propagationRankByStation ??
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        return couplings
            .Where(item => eligible.Contains(item.FirstStation) && eligible.Contains(item.SecondStation))
            .GroupBy(item => PairKey(item.FirstStation, item.SecondStation), StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Min(item =>
                rank.GetValueOrDefault(item.FirstStation, int.MaxValue / 4) +
                rank.GetValueOrDefault(item.SecondStation, int.MaxValue / 4)))
            .ThenBy(group => group.Min(item => item.RuleId), StringComparer.Ordinal)
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(item => item.RuleId, StringComparer.Ordinal).First())
            .Select(item => new MicroStationPairSelection(
                item.FirstStation, item.SecondStation, item.RuleId, item.TrainIndex, item.RuleKind))
            .ToArray();
    }

    internal static IReadOnlyList<MicroStationPairSelection> PutGuidedFirst(
        MicroStationPairSelection? guided,
        IReadOnlyList<MicroStationPairSelection> fallback)
    {
        if (guided is null) return fallback;
        string guidedKey = PairKey(guided.FirstStation, guided.SecondStation);
        return new[] { guided }.Concat(fallback.Where(item => !string.Equals(
                PairKey(item.FirstStation, item.SecondStation), guidedKey, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

    private static string PairKey(string first, string second) =>
        StringComparer.OrdinalIgnoreCase.Compare(first, second) <= 0
            ? $"{first}|{second}"
            : $"{second}|{first}";
}
