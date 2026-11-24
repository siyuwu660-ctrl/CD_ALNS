using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

public enum TimetableEventSide
{
    Arrival,
    Departure
}

public readonly record struct TimetableEventKey(string StopId, TimetableEventSide Side);

public sealed record HardEventBound(
    double Lower,
    double Upper,
    IReadOnlyList<string> LowerSources,
    IReadOnlyList<string> UpperSources);

public sealed record HardEventBoundSet(
    IReadOnlyDictionary<TimetableEventKey, HardEventBound> Bounds,
    int TightenedLowerCount,
    int TightenedUpperCount,
    IReadOnlyList<string> Contradictions)
{
    public static HardEventBoundSet Empty { get; } = new(
        new Dictionary<TimetableEventKey, HardEventBound>(), 0, 0, []);

    public HardEventBound Arrival(string stopId) =>
        Bounds[new TimetableEventKey(stopId, TimetableEventSide.Arrival)];

    public HardEventBound Departure(string stopId) =>
        Bounds[new TimetableEventKey(stopId, TimetableEventSide.Departure)];
}

/// <summary>
/// Propagates only implications that are valid for every solution of the
/// original Macro domain. Candidate-local windows and incumbent values are
/// intentionally not accepted by this API.
/// </summary>
public static class HardEventBoundPropagator
{
    private const double Tolerance = TimetableTimePolicy.NumericToleranceSeconds;

    public static HardEventBoundSet Build(TimetablingInstance instance, double timingToleranceSeconds = 0,
        double? runningTimeSupplementRatio = null)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (!double.IsFinite(timingToleranceSeconds) || timingToleranceSeconds < 0) throw new ArgumentOutOfRangeException(nameof(timingToleranceSeconds));
        // Only propagate a runtime maximum when the caller supplies its actual model
        // policy. An unspecified ratio must never silently impose a tighter model.
        var supplementPolicy = runningTimeSupplementRatio.HasValue
            ? RunningTimeSupplementPolicy.Create(runningTimeSupplementRatio.Value) : null;
        Dictionary<TimetableEventKey, MutableBound> bounds = [];
        foreach (TrainStopRecord stop in instance.TrainStops
                     .OrderBy(item => item.TrainIndex)
                     .ThenBy(item => item.StationOrder))
        {
            Add(bounds, new TimetableEventKey(stop.StopId, TimetableEventSide.Arrival),
                stop.EarliestArrSeconds - timingToleranceSeconds, stop.LatestArrSeconds + timingToleranceSeconds,
                $"window::{stop.StopId}::arrival");
            Add(bounds, new TimetableEventKey(stop.StopId, TimetableEventSide.Departure),
                stop.EarliestDepSeconds - timingToleranceSeconds, stop.LatestDepSeconds + timingToleranceSeconds,
                $"window::{stop.StopId}::departure");
        }

        List<DifferenceEdge> edges = [];
        foreach (TrainRecord train in instance.Trains.OrderBy(item => item.TrainIndex))
        {
            TrainStopRecord[] stops = train.OrderedStops.OrderBy(item => item.StationOrder).ToArray();
            foreach (TrainStopRecord stop in stops.Where(item => item.StopRole == TrainStopRole.Intermediate))
            {
                var normalDwell = NormalDwellTimeResolver.Resolve(instance.NormalDwellTimeRules, stop);
                int dwell = IsForcedStop(stop)
                    ? normalDwell.NormalMinDwellSeconds
                    : 0;
                edges.Add(DifferenceEdge.Minimum(
                    new TimetableEventKey(stop.StopId, TimetableEventSide.Arrival),
                    new TimetableEventKey(stop.StopId, TimetableEventSide.Departure),
                    dwell - timingToleranceSeconds,
                    $"dwell::{stop.StopId}::minimum::{dwell}"));
                // Waiting is a possible original-domain choice: use its legal maximum,
                // never the incumbent's selected waiting status or current dwell.
                int maximumDwell = stop.StopRequirement == StopRequirement.MustPass
                    ? 0 : Math.Max(stop.MaxDwellSeconds, normalDwell.NormalMaxDwellSeconds);
                edges.Add(DifferenceEdge.Maximum(
                    new TimetableEventKey(stop.StopId, TimetableEventSide.Arrival),
                    new TimetableEventKey(stop.StopId, TimetableEventSide.Departure),
                    maximumDwell + timingToleranceSeconds,
                    $"dwell::{stop.StopId}::maximum::{maximumDwell}"));
            }

            for (int index = 0; index < stops.Length - 1; index++)
            {
                TrainStopRecord current = stops[index];
                TrainStopRecord next = stops[index + 1];
                RunTimeRule rule = ResolveRunTimeRule(instance, current, next);
                double minimum = rule.RunTimeSeconds +
                                 MinimumConditionalAddition(rule.StartAddSeconds, current) +
                                 MinimumConditionalAddition(rule.StopAddSeconds, next);
                edges.Add(DifferenceEdge.Minimum(
                    new TimetableEventKey(current.StopId, TimetableEventSide.Departure),
                    new TimetableEventKey(next.StopId, TimetableEventSide.Arrival),
                    minimum - timingToleranceSeconds,
                    $"runtime::{current.TrainIndex}::{next.IncomingSectionName}::{minimum}"));
                if (supplementPolicy is not null)
                {
                    double maximum = rule.RunTimeSeconds +
                        MaximumConditionalAddition(rule.StartAddSeconds, current) +
                        MaximumConditionalAddition(rule.StopAddSeconds, next) +
                        supplementPolicy.ResolveMaximumSeconds(rule.RunTimeSeconds);
                    edges.Add(DifferenceEdge.Maximum(
                        new TimetableEventKey(current.StopId, TimetableEventSide.Departure),
                        new TimetableEventKey(next.StopId, TimetableEventSide.Arrival),
                        maximum + timingToleranceSeconds,
                        $"runtime-max::{current.TrainIndex}::{next.IncomingSectionName}::{maximum}"));
                }
            }
        }

        Dictionary<int, TrainRecord> trainByIndex = instance.Trains.ToDictionary(item => item.TrainIndex);
        foreach ((TrainSectionTravelTimeLimitRule rule, int index) in
                 instance.TrainSectionTravelTimeLimitRules.Select((value, index) => (value, index)))
        {
            if (!trainByIndex.TryGetValue(rule.TrainIndex, out TrainRecord? train))
                continue;
            (TrainStopRecord start, TrainStopRecord end) = TrainSectionTravelTimeResolver.Resolve(train, rule);
            edges.Add(DifferenceEdge.Maximum(
                new TimetableEventKey(start.StopId, TimetableEventSide.Departure),
                new TimetableEventKey(end.StopId, TimetableEventSide.Arrival),
                rule.MaxTravelTimeSeconds + timingToleranceSeconds,
                $"travel-time-max::{index + 1:D3}::train-{rule.TrainIndex}"));
        }

        int maximumPasses = Math.Max(1, bounds.Count * Math.Max(1, edges.Count));
        for (int pass = 0; pass < maximumPasses; pass++)
        {
            bool changed = false;
            foreach (DifferenceEdge edge in edges.OrderBy(item => item.Source, StringComparer.Ordinal))
                changed |= Apply(edge, bounds);
            if (!changed || bounds.Values.Any(bound => bound.Lower > bound.Upper + Tolerance)) break;
        }

        List<string> contradictions = [];
        foreach ((TimetableEventKey key, MutableBound bound) in bounds
                     .OrderBy(item => item.Key.StopId, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.Key.Side))
        {
            if (bound.Lower <= bound.Upper + Tolerance) continue;
            contradictions.Add(
                $"{key.StopId}::{key.Side}: lower={bound.Lower:F6} from [{string.Join(" > ", bound.LowerSources)}] " +
                $"exceeds upper={bound.Upper:F6} from [{string.Join(" > ", bound.UpperSources)}]");
        }

        Dictionary<TimetableEventKey, HardEventBound> immutable = bounds.ToDictionary(
            item => item.Key,
            item => new HardEventBound(
                item.Value.Lower,
                item.Value.Upper,
                item.Value.LowerSources.ToArray(),
                item.Value.UpperSources.ToArray()));
        return new HardEventBoundSet(
            immutable,
            bounds.Values.Count(item => item.Lower > item.OriginalLower + Tolerance),
            bounds.Values.Count(item => item.Upper < item.OriginalUpper - Tolerance),
            contradictions);
    }

    private static bool Apply(
        DifferenceEdge edge,
        IDictionary<TimetableEventKey, MutableBound> bounds)
    {
        MutableBound from = bounds[edge.From];
        MutableBound to = bounds[edge.To];
        bool changed = false;
        if (edge.MinimumDelta.HasValue)
        {
            double lower = from.Lower + edge.MinimumDelta.Value;
            if (lower > to.Lower + Tolerance)
            {
                to.Lower = lower;
                to.LowerSources = Append(from.LowerSources, edge.Source);
                changed = true;
            }
            double upper = to.Upper - edge.MinimumDelta.Value;
            if (upper < from.Upper - Tolerance)
            {
                from.Upper = upper;
                from.UpperSources = Append(to.UpperSources, edge.Source);
                changed = true;
            }
        }
        if (edge.MaximumDelta.HasValue)
        {
            double upper = from.Upper + edge.MaximumDelta.Value;
            if (upper < to.Upper - Tolerance)
            {
                to.Upper = upper;
                to.UpperSources = Append(from.UpperSources, edge.Source);
                changed = true;
            }
            double lower = to.Lower - edge.MaximumDelta.Value;
            if (lower > from.Lower + Tolerance)
            {
                from.Lower = lower;
                from.LowerSources = Append(to.LowerSources, edge.Source);
                changed = true;
            }
        }
        return changed;
    }

    private static IReadOnlyList<string> Append(IReadOnlyList<string> sources, string source) =>
        sources.Concat([source]).Distinct(StringComparer.Ordinal).ToArray();

    private static void Add(
        IDictionary<TimetableEventKey, MutableBound> bounds,
        TimetableEventKey key,
        double lower,
        double upper,
        string source)
    {
        bounds.Add(key, new MutableBound(lower, upper, source));
    }

    private static bool IsForcedStop(TrainStopRecord stop) =>
        stop.StopRequirement == StopRequirement.MustStop ||
        stop.StopRole is TrainStopRole.Origin or TrainStopRole.Destination;

    private static double MinimumConditionalAddition(int addition, TrainStopRecord stop)
    {
        if (IsForcedStop(stop)) return addition;
        if (stop.StopRequirement == StopRequirement.MustPass) return 0.0;
        return Math.Min(0.0, addition);
    }

    private static double MaximumConditionalAddition(int addition, TrainStopRecord stop)
    {
        if (IsForcedStop(stop)) return addition;
        if (stop.StopRequirement == StopRequirement.MustPass) return 0.0;
        return Math.Max(0.0, addition);
    }

    private static RunTimeRule ResolveRunTimeRule(
        TimetablingInstance instance,
        TrainStopRecord current,
        TrainStopRecord next)
    {
        if (string.IsNullOrWhiteSpace(next.IncomingSectionName) ||
            !instance.SectionByName.TryGetValue(next.IncomingSectionName, out SectionRecord? section) ||
            string.IsNullOrWhiteSpace(next.RunScaleName))
            throw new InvalidDataException(
                $"Cannot construct original runtime bound for {current.StopId}->{next.StopId}.");
        TravelDirection direction = ResolveDirection(section, current.StationName, next.StationName);
        return instance.RunTimeRules.FirstOrDefault(rule =>
                   string.Equals(rule.ScaleName, next.RunScaleName, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(rule.SectionName, next.IncomingSectionName, StringComparison.OrdinalIgnoreCase) &&
                   rule.Direction == direction)
               ?? throw new InvalidDataException(
                   $"Runtime rule missing for {current.StopId}->{next.StopId}.");
    }

    private static TravelDirection ResolveDirection(
        SectionRecord section,
        string from,
        string to)
    {
        if (string.Equals(section.FromStation, from, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(section.ToStation, to, StringComparison.OrdinalIgnoreCase))
            return section.DirectionTag;
        if (string.Equals(section.ToStation, from, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(section.FromStation, to, StringComparison.OrdinalIgnoreCase))
            return section.DirectionTag switch
            {
                TravelDirection.Up => TravelDirection.Down,
                TravelDirection.Down => TravelDirection.Up,
                _ => TravelDirection.Unknown
            };
        throw new InvalidDataException(
            $"Section {section.SectionName} does not connect {from}->{to}.");
    }

    private sealed class MutableBound
    {
        internal MutableBound(double lower, double upper, string source)
        {
            OriginalLower = Lower = lower;
            OriginalUpper = Upper = upper;
            LowerSources = [source];
            UpperSources = [source];
        }

        internal double OriginalLower { get; }
        internal double OriginalUpper { get; }
        internal double Lower { get; set; }
        internal double Upper { get; set; }
        internal IReadOnlyList<string> LowerSources { get; set; }
        internal IReadOnlyList<string> UpperSources { get; set; }
    }

    private sealed record DifferenceEdge(
        TimetableEventKey From,
        TimetableEventKey To,
        double? MinimumDelta,
        double? MaximumDelta,
        string Source)
    {
        internal static DifferenceEdge Minimum(
            TimetableEventKey from,
            TimetableEventKey to,
            double value,
            string source) => new(from, to, value, null, source);

        internal static DifferenceEdge Maximum(
            TimetableEventKey from,
            TimetableEventKey to,
            double value,
            string source) => new(from, to, null, value, source);
    }
}
