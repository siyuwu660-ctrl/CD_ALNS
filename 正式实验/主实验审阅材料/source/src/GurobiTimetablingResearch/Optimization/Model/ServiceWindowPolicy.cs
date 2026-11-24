using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

public sealed record ServiceWindowOptions(bool Enabled = false, int DepartureHalfWidthSeconds = 900,
    int ArrivalHalfWidthSeconds = 1800, double ArrivalEarlyWeight = 1, double ArrivalLateWeight = 1);

public sealed record ServiceWindow(string StopId, TimetableEventSide Side, double Lower, double Upper,
    double EarlyWeight, double LateWeight, string Source);

/// <summary>Preferences only. Never passed to HardEventBoundPropagator.</summary>
public sealed record ServiceWindowPolicy(IReadOnlyList<ServiceWindow> Windows, IReadOnlyList<string> Diagnostics)
{
    public static ServiceWindowPolicy Disabled { get; } = new([], []);
    public string Identity => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(Windows.OrderBy(w => w.StopId, StringComparer.Ordinal).ThenBy(w => w.Side)))));

    public static ServiceWindowPolicy Create(TimetablingInstance instance, ServiceWindowOptions options,
        IReadOnlyList<ServiceWindow>? overrides = null)
    {
        if (options.DepartureHalfWidthSeconds < 0 || options.ArrivalHalfWidthSeconds < 0 ||
            !ValidWeight(options.ArrivalEarlyWeight) || !ValidWeight(options.ArrivalLateWeight))
            throw new ArgumentOutOfRangeException(nameof(options));
        if (!options.Enabled) return Disabled;
        Dictionary<TimetableEventKey, ServiceWindow> explicitWindows = (overrides ?? [])
            .ToDictionary(w => new TimetableEventKey(w.StopId, w.Side));
        List<ServiceWindow> windows = [];
        List<string> diagnostics = [];
        foreach (TrainRecord train in instance.Trains)
        {
            TrainStopRecord origin = train.OrderedStops.OrderBy(s => s.StationOrder).First();
            TrainStopRecord destination = train.OrderedStops.OrderBy(s => s.StationOrder).Last();
            Add(origin, TimetableEventSide.Departure, origin.RawDepTimeSeconds,
                options.DepartureHalfWidthSeconds, origin.LeftDepartureWeight, origin.RightDepartureWeight);
            Add(destination, TimetableEventSide.Arrival, destination.RawArrTimeSeconds,
                options.ArrivalHalfWidthSeconds, options.ArrivalEarlyWeight, options.ArrivalLateWeight);
        }
        if (explicitWindows.Count > 0)
            throw new InvalidDataException("Explicit service window must identify an origin departure or destination arrival: " +
                string.Join(",", explicitWindows.Keys));
        ServiceWindowPolicy policy = new(windows, diagnostics);
        policy.Validate();
        return policy;

        void Add(TrainStopRecord stop, TimetableEventSide side, int? reference, int halfWidth, double early, double late)
        {
            TimetableEventKey key = new(stop.StopId, side);
            if (explicitWindows.Remove(key, out ServiceWindow? businessWindow)) { windows.Add(businessWindow); return; }
            if (reference is null or < 0)
            {
                diagnostics.Add($"missing_reference::{stop.StopId}::{side}; preference omitted, hard domain unchanged");
                return;
            }
            // Negative lower preferences are harmless; do not reinterpret them as a cross-midnight hard window.
            windows.Add(new(stop.StopId, side, reference.Value-halfWidth, reference.Value+halfWidth, early, late, "experimental_reference_default"));
        }
    }

    public void Validate()
    {
        HashSet<TimetableEventKey> keys = [];
        foreach (ServiceWindow w in Windows)
            if (string.IsNullOrWhiteSpace(w.StopId) || !Enum.IsDefined(w.Side) ||
                !double.IsFinite(w.Lower) || !double.IsFinite(w.Upper) || w.Lower > w.Upper ||
                !ValidWeight(w.EarlyWeight) || !ValidWeight(w.LateWeight) ||
                !keys.Add(new(w.StopId,w.Side)))
                throw new InvalidDataException($"Malformed or duplicate service preference: {w}");
    }
    private static bool ValidWeight(double weight) => double.IsFinite(weight) && weight >= 0;
}

public sealed record ServiceObjectiveVector(double ServicePenalty, double GlobalSpanSeconds,
    double RunningTimeSupplementSeconds, string PolicyIdentity);

public static class ServiceBusinessObjectiveEvaluator
{
    public static double Penalty(ServiceWindowPolicy policy, IReadOnlyDictionary<string,double> arrivals,
        IReadOnlyDictionary<string,double> departures)
    {
        policy.Validate();
        double total = 0;
        foreach (ServiceWindow w in policy.Windows)
        {
            double t = (w.Side == TimetableEventSide.Arrival ? arrivals : departures)[w.StopId];
            if (!double.IsFinite(t)) throw new InvalidDataException("Nonfinite service event time.");
            total += w.EarlyWeight*Math.Max(0,w.Lower-t) + w.LateWeight*Math.Max(0,t-w.Upper);
        }
        if (!double.IsFinite(total)) throw new InvalidDataException("Service penalty overflow.");
        return total;
    }

    public static ServiceObjectiveVector Calculate(TimetablingInstance instance, MasterSolutionSnapshot snapshot,
        ServiceWindowPolicy policy)
    {
        BusinessObjectiveVector legacy = BusinessObjectiveCalculator.Calculate(
            instance.Trains.Select(t => snapshot.DepartureSecondsByStopId[t.OrderedStops[0].StopId]),
            instance.Trains.Select(t => snapshot.ArrivalSecondsByStopId[t.OrderedStops[^1].StopId]),
            (snapshot.RunningTimeSupplementSecondsByKey ?? new Dictionary<string,double>()).Values);
        return new(Penalty(policy,snapshot.ArrivalSecondsByStopId,snapshot.DepartureSecondsByStopId),
            legacy.PrimaryGlobalSpanSeconds,legacy.SecondaryRunningTimeSupplementSeconds,policy.Identity);
    }
}
