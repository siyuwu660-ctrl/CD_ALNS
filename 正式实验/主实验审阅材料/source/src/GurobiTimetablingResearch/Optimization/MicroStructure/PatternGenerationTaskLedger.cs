using System.Text;
using System.Text.Json;

namespace GurobiTimetablingResearch.Optimization;

internal enum PatternGenerationTaskOutcome
{
    Unknown,
    PatternAdded,
    DuplicatePattern,
    ProvenExhausted,
    EngineeringUnavailable
}

internal sealed record PatternGenerationTaskState(
    string StationName,
    double ConsumedSeconds,
    int DispatchCount,
    long LastDispatchOrdinal,
    PatternGenerationTaskOutcome LastOutcome,
    bool ProvenExhausted,
    string LastReason);

/// <summary>
/// Persistent accounting for the logical single-station generation tasks.  The ledger is
/// deliberately independent of a particular candidate/stop combination: rebuilding the
/// master or receiving another proof cannot silently renew a station's 300-second allowance.
/// </summary>
internal sealed class PatternGenerationTaskLedger
{
    private const int FormatVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly Dictionary<string, PatternGenerationTaskState> _states;
    private long _dispatchOrdinal;

    private PatternGenerationTaskLedger(
        IEnumerable<PatternGenerationTaskState> states,
        double maximumSecondsPerTask,
        long dispatchOrdinal)
    {
        if (maximumSecondsPerTask <= 0) throw new ArgumentOutOfRangeException(nameof(maximumSecondsPerTask));
        MaximumSecondsPerTask = maximumSecondsPerTask;
        _states = states.ToDictionary(item => item.StationName, StringComparer.OrdinalIgnoreCase);
        _dispatchOrdinal = dispatchOrdinal;
    }

    internal double MaximumSecondsPerTask { get; }
    internal IReadOnlyList<PatternGenerationTaskState> States => _states.Values
        .OrderBy(item => item.StationName, StringComparer.OrdinalIgnoreCase).ToArray();
    internal bool HasEligibleTask => _states.Values.Any(IsEligible);

    internal static PatternGenerationTaskLedger Create(
        IReadOnlyList<string> stations,
        double maximumSecondsPerTask)
    {
        string[] unique = stations.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (unique.Length == 0) throw new ArgumentException("At least one station is required.", nameof(stations));
        return new(unique.Select(station => new PatternGenerationTaskState(
            station, 0, 0, 0, PatternGenerationTaskOutcome.Unknown, false, "not_started")),
            maximumSecondsPerTask, 0);
    }

    internal string? SelectNext(
        string? suggestedStation,
        IReadOnlyList<string> priority,
        bool preferEvidenceRegardlessOfFairness = false)
    {
        PatternGenerationTaskState[] eligible = _states.Values.Where(IsEligible).ToArray();
        if (eligible.Length == 0) return null;
        if (preferEvidenceRegardlessOfFairness && !string.IsNullOrWhiteSpace(suggestedStation))
        {
            PatternGenerationTaskState? evidenceTarget = eligible.FirstOrDefault(item => string.Equals(
                item.StationName, suggestedStation, StringComparison.OrdinalIgnoreCase));
            if (evidenceTarget is not null) return evidenceTarget.StationName;
        }
        int minimumDispatches = eligible.Min(item => item.DispatchCount);
        long oldestDispatch = eligible.Where(item => item.DispatchCount == minimumDispatches)
            .Min(item => item.LastDispatchOrdinal);
        PatternGenerationTaskState[] fair = eligible.Where(item =>
            item.DispatchCount == minimumDispatches && item.LastDispatchOrdinal == oldestDispatch).ToArray();
        Dictionary<string, int> rank = priority.Select((station, index) => (station, index))
            .GroupBy(item => item.station, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().index, StringComparer.OrdinalIgnoreCase);
        return fair.OrderBy(item => rank.TryGetValue(item.StationName, out int value) ? value : int.MaxValue)
            .ThenBy(item => item.StationName, StringComparer.OrdinalIgnoreCase)
            .First().StationName;
    }

    internal void Record(
        string station,
        double consumedSeconds,
        PatternGenerationTaskOutcome outcome,
        string reason = "")
    {
        if (!_states.TryGetValue(station, out PatternGenerationTaskState? current))
            throw new KeyNotFoundException($"Unknown station generation task '{station}'.");
        if (consumedSeconds < 0 || double.IsNaN(consumedSeconds) || double.IsInfinity(consumedSeconds))
            throw new ArgumentOutOfRangeException(nameof(consumedSeconds));
        double cumulative = Math.Min(MaximumSecondsPerTask, current.ConsumedSeconds + consumedSeconds);
        bool proven = current.ProvenExhausted || outcome == PatternGenerationTaskOutcome.ProvenExhausted;
        _dispatchOrdinal++;
        _states[station] = current with
        {
            ConsumedSeconds = cumulative,
            DispatchCount = current.DispatchCount + 1,
            LastDispatchOrdinal = _dispatchOrdinal,
            LastOutcome = outcome,
            ProvenExhausted = proven,
            LastReason = reason
        };
    }

    internal double RemainingSeconds(string station)
    {
        if (!_states.TryGetValue(station, out PatternGenerationTaskState? state))
            throw new KeyNotFoundException($"Unknown station generation task '{station}'.");
        return Math.Max(0, MaximumSecondsPerTask - state.ConsumedSeconds);
    }

    internal void Save(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        Payload payload = new(FormatVersion, MaximumSecondsPerTask, _dispatchOrdinal, States.ToArray());
        File.WriteAllText(fullPath, JsonSerializer.Serialize(payload, JsonOptions), new UTF8Encoding(false));
    }

    internal static PatternGenerationTaskLedger Load(
        string path,
        IReadOnlyList<string> expectedStations,
        double expectedMaximumSecondsPerTask)
    {
        Payload payload = JsonSerializer.Deserialize<Payload>(
            File.ReadAllText(Path.GetFullPath(path), Encoding.UTF8), JsonOptions)
            ?? throw new InvalidDataException("Pattern-generation ledger is empty.");
        if (payload.FormatVersion != FormatVersion ||
            Math.Abs(payload.MaximumSecondsPerTask - expectedMaximumSecondsPerTask) > 1e-9)
            throw new InvalidDataException("Pattern-generation ledger configuration does not match this run.");
        HashSet<string> expected = expectedStations.ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> actual = payload.States.Select(item => item.StationName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!expected.SetEquals(actual))
            throw new InvalidDataException("Pattern-generation ledger station set does not match this run.");
        return new PatternGenerationTaskLedger(payload.States, payload.MaximumSecondsPerTask,
            payload.DispatchOrdinal);
    }

    private bool IsEligible(PatternGenerationTaskState state) =>
        !state.ProvenExhausted && state.ConsumedSeconds < MaximumSecondsPerTask - 1e-9;

    private sealed record Payload(
        int FormatVersion,
        double MaximumSecondsPerTask,
        long DispatchOrdinal,
        PatternGenerationTaskState[] States);
}
