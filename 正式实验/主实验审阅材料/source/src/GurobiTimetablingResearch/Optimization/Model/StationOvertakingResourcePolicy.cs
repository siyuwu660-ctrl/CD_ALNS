using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

/// <summary>Necessary pair-specific resource support, independent of heuristic station scores.</summary>
public static class StationOvertakingResourcePolicy
{
    /// <summary>Test whether either train can wait while its partner uses a different legal track.</summary>
    /// <param name="instance">Original route/track and stop-mode domains.</param>
    /// <param name="first">First intermediate visit.</param>
    /// <param name="second">Second intermediate visit at the same station.</param>
    /// <returns>Whether at least one overtaking direction has resource support.</returns>
    public static bool CanReverse(TimetablingInstance instance, TrainStopRecord first, TrainStopRecord second)
        => CanOvertake(instance, first, second) || CanOvertake(instance, second, first);

    /// <summary>Test the necessary route/track domains for a specified waiting train.</summary>
    /// <param name="instance">Original instance; no permission-table restriction is added.</param>
    /// <param name="waiting">Visit required to provide a legal stopping track.</param>
    /// <param name="passing">Visit allowed to stop or pass according to its original requirement.</param>
    /// <returns>True when distinct legal tracks exist; timing and conflicts still require validation.</returns>
    public static bool CanOvertake(TimetablingInstance instance, TrainStopRecord waiting, TrainStopRecord passing)
    {
        if (waiting.StopRole != TrainStopRole.Intermediate || passing.StopRole != TrainStopRole.Intermediate ||
            waiting.StopRequirement == StopRequirement.MustPass || waiting.TrainIndex == passing.TrainIndex ||
            !waiting.StationName.Equals(passing.StationName, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(waiting.IncomingSectionName) ||
            !string.Equals(waiting.IncomingSectionName, passing.IncomingSectionName, StringComparison.OrdinalIgnoreCase)) return false;
        TrainRecord waitingTrain = instance.Trains.Single(train => train.TrainIndex == waiting.TrainIndex);
        TrainRecord passingTrain = instance.Trains.Single(train => train.TrainIndex == passing.TrainIndex);
        string? waitingOutgoing = waitingTrain.OrderedStops.FirstOrDefault(stop => stop.StationOrder == waiting.StationOrder + 1)?.IncomingSectionName;
        string? passingOutgoing = passingTrain.OrderedStops.FirstOrDefault(stop => stop.StationOrder == passing.StationOrder + 1)?.IncomingSectionName;
        if (string.IsNullOrWhiteSpace(waitingOutgoing) ||
            !string.Equals(waitingOutgoing, passingOutgoing, StringComparison.OrdinalIgnoreCase)) return false;

        StopModeDomainAnalysis waitingMode = StopModeDomainResolver.Analyze(instance, waitingTrain, waiting);
        StopModeDomainAnalysis passingMode = StopModeDomainResolver.Analyze(instance, passingTrain, passing);
        return CanOvertake(waiting, passing, waitingMode, passingMode);
    }

    /// <summary>Check distinct tracks from precomputed modes of two comparable intermediate visits.</summary>
    /// <param name="waiting">Visit that must stop during the overtake.</param>
    /// <param name="passing">Visit that may stop or pass according to its stop requirement.</param>
    /// <param name="waitingMode">Permanent modes of the waiting visit.</param>
    /// <param name="passingMode">Permanent modes of the overtaking visit.</param>
    /// <returns>Whether the visits can use distinct legal tracks.</returns>
    internal static bool CanOvertake(TrainStopRecord waiting, TrainStopRecord passing,
        StopModeDomainAnalysis waitingMode, StopModeDomainAnalysis passingMode)
    {
        if (waiting.StopRequirement == StopRequirement.MustPass) return false;
        IEnumerable<string> passingTracks = Array.Empty<string>();
        if (passing.StopRequirement != StopRequirement.MustPass)
            passingTracks = passingTracks.Concat(passingMode.Stopping.FeasibleTracks);
        if (passing.StopRequirement != StopRequirement.MustStop)
            passingTracks = passingTracks.Concat(passingMode.Passing.FeasibleTracks);
        return waitingMode.Stopping.FeasibleTracks.Any(waitingTrack => passingTracks.Any(passingTrack =>
            !waitingTrack.Equals(passingTrack, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Give low-score stations less search priority without removing feasible candidates.</summary>
    /// <param name="instance">Instance supplying the original station score.</param>
    /// <param name="stationName">Candidate station.</param>
    /// <returns>A positive search multiplier, one half below score three and one otherwise.</returns>
    public static double SearchWeight(TimetablingInstance instance, string stationName)
        => instance.StationOvertakingProfileMap.TryGetValue(stationName, out var profile) &&
            profile.CapabilityScore < 3 ? 0.5 : 1.0;
}
