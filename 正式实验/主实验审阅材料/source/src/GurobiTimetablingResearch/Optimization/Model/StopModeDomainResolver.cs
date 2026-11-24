// Resolve stopping and passing modes from the original route sides and permanent track restrictions.
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

internal enum StopModeResolution
{
    Flexible,
    ForceStop,
    ForcePass,
    Infeasible
}

public enum RouteDomainFailureReason
{
    None,
    RequiredRouteMissing,
    StationRouteDataMissing,
    SectionRouteMissing,
    MovementTypeUnsupported,
    IncomingOutgoingTrackIncompatible,
    PreferredTrackIntersectionEmpty
}

/// <summary>Route-side and track inputs for both operating modes of a station visit.</summary>
internal sealed record StopModeDomainInput(
    bool RequiresIncomingRoute,
    bool RequiresOutgoingRoute,
    IReadOnlyList<RouteRecord> StoppingIncomingRoutes,
    IReadOnlyList<RouteRecord> StoppingOutgoingRoutes,
    IReadOnlyList<RouteRecord> PassingIncomingRoutes,
    IReadOnlyList<RouteRecord> PassingOutgoingRoutes,
    IReadOnlyList<string> StoppingPreferredTracks,
    IReadOnlyList<string> PassingPreferredTracks,
    bool HasBenchmarkRestriction = false);

internal sealed record RouteTrackModeCapability(
    bool IsFeasible,
    IReadOnlyList<string> FeasibleTracks,
    RouteDomainFailureReason FailureReason,
    string Evidence);

internal sealed record StopModeDomainAnalysis(
    StopModeResolution Resolution,
    RouteTrackModeCapability Stopping,
    RouteTrackModeCapability Passing,
    string Evidence);

internal static class StopModeDomainResolver
{
    /// <summary>Resolve the legal operating modes and route tracks for a visit.</summary>
    /// <param name="instance">Original route and track restrictions.</param>
    /// <param name="train">Train supplying the outgoing leg.</param>
    /// <param name="stop">Visit to analyze.</param>
    /// <returns>Stopping and passing domains with evidence for any empty domain.</returns>
    internal static StopModeDomainAnalysis Analyze(
        TimetablingInstance instance,
        TrainRecord train,
        TrainStopRecord stop)
    {
        TrainStopRecord? nextStop = train.OrderedStops
            .FirstOrDefault(candidate => candidate.StationOrder == stop.StationOrder + 1);
        bool requiresIncoming = stop.StopRole != TrainStopRole.Origin;
        bool requiresOutgoing = stop.StopRole != TrainStopRole.Destination && nextStop is not null;

        List<RouteRecord> stoppingIncoming = ResolveRoutes(
            instance, stop.StationName, stop.IncomingSectionName, MovementEvent.Arrival, incoming: true);
        List<RouteRecord> stoppingOutgoing = ResolveRoutes(
            instance, stop.StationName, nextStop?.IncomingSectionName, MovementEvent.Departure, incoming: false);
        List<RouteRecord> passingIncoming = ResolveRoutes(
            instance, stop.StationName, stop.IncomingSectionName, MovementEvent.Passing, incoming: true);
        List<RouteRecord> passingOutgoing = ResolveRoutes(
            instance, stop.StationName, nextStop?.IncomingSectionName, MovementEvent.Passing, incoming: false);

        instance.TryGetBenchmarkTrackRestriction(stop.TrainIndex, stop.StationOrder, out BenchmarkTrackRestriction? restriction);
        var stoppingDomain = BenchmarkTrackDomainResolver.Resolve(ResolvePreferredTracks(instance, stop, true), restriction?.AllowedTrackNames);
        var passingDomain = BenchmarkTrackDomainResolver.Resolve(ResolvePreferredTracks(instance, stop, false), restriction?.AllowedTrackNames);
        StopModeDomainAnalysis analysis = Analyze(new StopModeDomainInput(
            requiresIncoming,
            requiresOutgoing,
            stoppingIncoming,
            stoppingOutgoing,
            passingIncoming,
            passingOutgoing,
            stoppingDomain.AllowedTracks,
            passingDomain.AllowedTracks,
            stoppingDomain.HasBenchmarkRestriction));

        RouteTrackModeCapability stopping = RefineMissingRouteReason(
            instance, stop, nextStop, true, analysis.Stopping);
        RouteTrackModeCapability passing = RefineMissingRouteReason(
            instance, stop, nextStop, false, analysis.Passing);
        return analysis with
        {
            Stopping = stopping,
            Passing = passing,
            Evidence = BuildEvidence(analysis.Resolution, stopping, passing)
        };
    }

    /// <summary>Intersect the required route sides and permanent track domains for each mode.</summary>
    /// <param name="input">Required route sides and ordinary or benchmark track domains.</param>
    /// <returns>The two mode domains and resulting structural stop-mode resolution.</returns>
    internal static StopModeDomainAnalysis Analyze(StopModeDomainInput input)
    {
        RouteTrackModeCapability stopping = AnalyzeMode(
            input.RequiresIncomingRoute,
            input.RequiresOutgoingRoute,
            input.StoppingIncomingRoutes,
            input.StoppingOutgoingRoutes,
            input.StoppingPreferredTracks,
            input.HasBenchmarkRestriction);
        RouteTrackModeCapability passing = AnalyzeMode(
            input.RequiresIncomingRoute,
            input.RequiresOutgoingRoute,
            input.PassingIncomingRoutes,
            input.PassingOutgoingRoutes,
            input.PassingPreferredTracks,
            input.HasBenchmarkRestriction);

        StopModeResolution resolution = (stopping.IsFeasible, passing.IsFeasible) switch
        {
            (true, true) => StopModeResolution.Flexible,
            (true, false) => StopModeResolution.ForceStop,
            (false, true) => StopModeResolution.ForcePass,
            _ => StopModeResolution.Infeasible
        };

        return new StopModeDomainAnalysis(
            resolution,
            stopping,
            passing,
            BuildEvidence(resolution, stopping, passing));
    }

    /// <summary>Find tracks common to the required route sides and applicable track domain.</summary>
    /// <param name="requiresIncoming">Whether an incoming route must be available.</param>
    /// <param name="requiresOutgoing">Whether an outgoing route must be available.</param>
    /// <param name="incomingRoutes">Routes for the incoming movement.</param>
    /// <param name="outgoingRoutes">Routes for the outgoing movement.</param>
    /// <param name="preferredTracks">Track names after applying the benchmark restriction, if any.</param>
    /// <param name="hasBenchmarkRestriction">Whether an empty preferred domain forbids all tracks.</param>
    /// <returns>The feasible track set or evidence of an empty domain.</returns>
    private static RouteTrackModeCapability AnalyzeMode(
        bool requiresIncoming,
        bool requiresOutgoing,
        IReadOnlyList<RouteRecord> incomingRoutes,
        IReadOnlyList<RouteRecord> outgoingRoutes,
        IReadOnlyList<string> preferredTracks,
        bool hasBenchmarkRestriction)
    {
        if (requiresIncoming && incomingRoutes.Count == 0)
        {
            return Infeasible(RouteDomainFailureReason.RequiredRouteMissing, "required incoming route domain is empty");
        }

        if (requiresOutgoing && outgoingRoutes.Count == 0)
        {
            return Infeasible(RouteDomainFailureReason.RequiredRouteMissing, "required outgoing route domain is empty");
        }

        HashSet<string>? routeTracks = null;
        if (requiresIncoming)
        {
            routeTracks = RouteTracks(incomingRoutes);
        }

        if (requiresOutgoing)
        {
            HashSet<string> outgoingTracks = RouteTracks(outgoingRoutes);
            routeTracks = routeTracks is null
                ? outgoingTracks
                : routeTracks.Intersect(outgoingTracks, StringComparer.OrdinalIgnoreCase)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        routeTracks ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (routeTracks.Count == 0)
        {
            return Infeasible(
                RouteDomainFailureReason.IncomingOutgoingTrackIncompatible,
                "required route sides have no common track");
        }

        if (preferredTracks.Count > 0 || hasBenchmarkRestriction)
        {
            routeTracks.IntersectWith(preferredTracks);
            if (routeTracks.Count == 0)
            {
                return Infeasible(
                    RouteDomainFailureReason.PreferredTrackIntersectionEmpty,
                    "route tracks and the applicable track restriction have no common track");
            }
        }

        List<string> feasibleTracks = routeTracks
            .OrderBy(track => track, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new RouteTrackModeCapability(
            true,
            feasibleTracks,
            RouteDomainFailureReason.None,
            $"feasible_tracks={string.Join('|', feasibleTracks)}");
    }

    private static RouteTrackModeCapability RefineMissingRouteReason(
        TimetablingInstance instance,
        TrainStopRecord stop,
        TrainStopRecord? nextStop,
        bool isStopping,
        RouteTrackModeCapability capability)
    {
        if (capability.FailureReason != RouteDomainFailureReason.RequiredRouteMissing)
        {
            return capability;
        }

        MovementEvent incomingEvent = isStopping ? MovementEvent.Arrival : MovementEvent.Passing;
        MovementEvent outgoingEvent = isStopping ? MovementEvent.Departure : MovementEvent.Passing;
        RouteDomainFailureReason reason = DiagnoseRequiredRoutes(
            instance,
            stop.StationName,
            stop.StopRole != TrainStopRole.Origin ? stop.IncomingSectionName : null,
            stop.StopRole != TrainStopRole.Destination && nextStop is not null ? nextStop.IncomingSectionName : null,
            incomingEvent,
            outgoingEvent);
        return capability with
        {
            FailureReason = reason,
            Evidence = $"{capability.Evidence}; diagnosed_reason={reason}"
        };
    }

    private static RouteDomainFailureReason DiagnoseRequiredRoutes(
        TimetablingInstance instance,
        string stationName,
        string? incomingSection,
        string? outgoingSection,
        MovementEvent incomingEvent,
        MovementEvent outgoingEvent)
    {
        List<RouteRecord> stationRoutes = instance.Routes
            .Where(route => string.Equals(route.StationName, stationName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (stationRoutes.Count == 0)
        {
            return RouteDomainFailureReason.StationRouteDataMissing;
        }

        foreach ((string? section, MovementEvent movement) in new[]
                 {
                     (incomingSection, incomingEvent),
                     (outgoingSection, outgoingEvent)
                 })
        {
            if (string.IsNullOrWhiteSpace(section))
            {
                continue;
            }

            List<RouteRecord> sectionRoutes = stationRoutes
                .Where(route => string.Equals(route.SectionName, section, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (sectionRoutes.Count == 0)
            {
                return RouteDomainFailureReason.SectionRouteMissing;
            }

            if (sectionRoutes.All(route => route.MovementEvent != movement))
            {
                return RouteDomainFailureReason.MovementTypeUnsupported;
            }
        }

        return RouteDomainFailureReason.RequiredRouteMissing;
    }

    private static List<RouteRecord> ResolveRoutes(
        TimetablingInstance instance,
        string stationName,
        string? sectionName,
        MovementEvent movementEvent, bool incoming)
    {
        if (string.IsNullOrWhiteSpace(sectionName))
        {
            return new List<RouteRecord>();
        }

        return instance.Routes
            .Where(route =>
                string.Equals(route.StationName, stationName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(route.SectionName, sectionName, StringComparison.OrdinalIgnoreCase) &&
                route.MovementEvent == movementEvent && RouteEventDirectionPolicy.Matches(route, incoming))
            .ToList();
    }

    private static IReadOnlyList<string> ResolvePreferredTracks(
        TimetablingInstance instance,
        TrainStopRecord stop,
        bool isStopping)
    {
        return instance.TryGetTrackChoiceRecord(stop.TrainIndex, stop.StationOrder, isStopping, out TrainTrackChoiceRecord? record) &&
               record is not null
            ? record.AllowedTrackNames
            : Array.Empty<string>();
    }

    private static HashSet<string> RouteTracks(IEnumerable<RouteRecord> routes)
    {
        return routes
            .Select(route => route.TrackName)
            .Where(track => !string.IsNullOrWhiteSpace(track))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static RouteTrackModeCapability Infeasible(RouteDomainFailureReason reason, string evidence)
    {
        return new RouteTrackModeCapability(false, Array.Empty<string>(), reason, evidence);
    }

    private static string BuildEvidence(
        StopModeResolution resolution,
        RouteTrackModeCapability stopping,
        RouteTrackModeCapability passing)
    {
        return $"resolution={resolution}; stop={stopping.IsFeasible}:{stopping.FailureReason}; pass={passing.IsFeasible}:{passing.FailureReason}";
    }
}

internal sealed record StopModePreprocessingDecision(
    string StopId,
    int TrainIndex,
    string TrainNo,
    int StationOrder,
    string StationName,
    StopRequirement StopRequirement,
    StopModeResolution Resolution,
    int? FixedStopValue,
    bool HasContradiction,
    StopModeDomainAnalysis Analysis);

internal sealed record StopModePreprocessingResult(
    IReadOnlyList<StopModePreprocessingDecision> Decisions,
    IReadOnlyDictionary<string, int> FixedStopValuesByStopId,
    IReadOnlyList<StopModePreprocessingDecision> Contradictions);

internal static class StopModePreprocessor
{
    internal static StopModePreprocessingResult Analyze(TimetablingInstance instance)
    {
        List<StopModePreprocessingDecision> decisions = new();
        Dictionary<string, int> fixedValues = new(StringComparer.OrdinalIgnoreCase);

        foreach (TrainRecord train in instance.Trains)
        {
            foreach (TrainStopRecord stop in train.OrderedStops)
            {
                StopModeDomainAnalysis analysis = StopModeDomainResolver.Analyze(instance, train, stop);
                int? fixedValue = ResolveFixedValue(stop.StopRequirement, analysis.Resolution);
                bool contradiction = IsContradiction(stop.StopRequirement, analysis);
                if (fixedValue.HasValue && !contradiction)
                {
                    fixedValues[stop.StopId] = fixedValue.Value;
                }

                decisions.Add(new StopModePreprocessingDecision(
                    stop.StopId,
                    stop.TrainIndex,
                    stop.TrainNo,
                    stop.StationOrder,
                    stop.StationName,
                    stop.StopRequirement,
                    analysis.Resolution,
                    fixedValue,
                    contradiction,
                    analysis));
            }
        }

        return new StopModePreprocessingResult(
            decisions,
            fixedValues,
            decisions.Where(decision => decision.HasContradiction).ToList());
    }

    private static int? ResolveFixedValue(StopRequirement requirement, StopModeResolution resolution)
    {
        return requirement switch
        {
            StopRequirement.MustStop => 1,
            StopRequirement.MustPass => 0,
            _ => resolution switch
            {
                StopModeResolution.ForceStop => 1,
                StopModeResolution.ForcePass => 0,
                _ => null
            }
        };
    }

    private static bool IsContradiction(
        StopRequirement requirement,
        StopModeDomainAnalysis analysis)
    {
        return requirement switch
        {
            StopRequirement.MustStop => !analysis.Stopping.IsFeasible,
            StopRequirement.MustPass => !analysis.Passing.IsFeasible,
            _ => analysis.Resolution == StopModeResolution.Infeasible
        };
    }
}

internal readonly record struct StopVariableBounds(double LowerBound, double UpperBound);

internal static class StopModeMasterBoundsResolver
{
    internal static StopVariableBounds Resolve(
        string stopId,
        StopModePreprocessingResult preprocessing)
    {
        return preprocessing.FixedStopValuesByStopId.TryGetValue(stopId, out int fixedValue)
            ? new StopVariableBounds(fixedValue, fixedValue)
            : new StopVariableBounds(0.0, 1.0);
    }
}
