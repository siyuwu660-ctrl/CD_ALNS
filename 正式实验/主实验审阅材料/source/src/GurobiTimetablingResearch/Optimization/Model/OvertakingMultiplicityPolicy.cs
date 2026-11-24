using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

/// <summary>Actual time-order reversals, independent of a sparse model's precedence edges.</summary>
public static class OvertakingMultiplicityPolicy
{
    public static IReadOnlyDictionary<string, IReadOnlyList<int>> FindOvertakers(
        TimetablingInstance instance,
        Func<string, double> arrival,
        Func<string, double> departure)
    {
        var visits = instance.Trains.SelectMany(train =>
            train.OrderedStops.Skip(1).SkipLast(1).Select((stop, offset) => new
            {
                Stop = stop,
                Incoming = stop.IncomingSectionName,
                Outgoing = train.OrderedStops[offset + 2].IncomingSectionName
            })).Where(v => v.Stop.StopRole == TrainStopRole.Intermediate &&
                !string.IsNullOrWhiteSpace(v.Incoming) && !string.IsNullOrWhiteSpace(v.Outgoing));
        var result = new Dictionary<string, IReadOnlyList<int>>(StringComparer.OrdinalIgnoreCase);
        double tolerance = TimetableTimePolicy.NumericToleranceSeconds;
        foreach (var station in visits.GroupBy(v => (
                     v.Stop.StationName.ToUpperInvariant(), v.Incoming!.ToUpperInvariant(), v.Outgoing!.ToUpperInvariant())))
        {
            var timed = station.Select(v => new { v.Stop, Arrival = arrival(v.Stop.StopId), Departure = departure(v.Stop.StopId) }).ToArray();
            if (timed.Any(v => !double.IsFinite(v.Arrival) || !double.IsFinite(v.Departure)))
                throw new InvalidDataException("Non-finite time in overtaking multiplicity check.");
            foreach (var first in timed)
            {
                int[] overtakers = timed.Where(second => second.Stop.TrainIndex != first.Stop.TrainIndex &&
                        second.Arrival > first.Arrival + tolerance && second.Departure < first.Departure - tolerance)
                    .Select(second => second.Stop.TrainIndex).Distinct().Order().ToArray();
                if (overtakers.Length > 0) result.Add(first.Stop.StopId, overtakers);
            }
        }
        return result;
    }
}
