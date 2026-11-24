using System.Security.Cryptography;
using System.Text;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

/// <summary>
/// Converts only an unavoidable unique-route exact station headway conflict into a Macro-visible
/// necessary relation.  A multiple-route context remains route-dependent even when every current
/// route pair is conflicting; it must not be collapsed into one permanent Macro lower bound.
/// </summary>
internal static class ExactHeadwayMacroFeedbackProjector
{
    internal static MacroRepairFeedback? TryCreate(
        TimetablingInstance instance,
        MasterSolutionSnapshot reference,
        string sourceCandidateFingerprint,
        string sourceModelIdentity,
        string sourceSolveResultIdentity,
        IReadOnlyList<MicroConflictExplanation> conflicts)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(conflicts);
        Dictionary<int, TrainRecord> trains = instance.Trains.ToDictionary(train => train.TrainIndex);
        List<ConditionalMacroTimeCut> cuts = [];
        foreach (MicroConflictExplanation conflict in conflicts.Where(conflict =>
                     string.Equals(conflict.ConflictKind, "exact_headway_infeasible",
                         StringComparison.OrdinalIgnoreCase)))
        {
            ConditionalMacroTimeCut? cut = TryProject(instance, reference, trains, conflict);
            if (cut is not null && cuts.All(existing => !string.Equals(
                    existing.CutId, cut.CutId, StringComparison.OrdinalIgnoreCase)))
                cuts.Add(cut);
        }
        if (cuts.Count == 0)
            return null;

        string feedbackId = Identity("exact_station_headway_projection",
            sourceCandidateFingerprint,
            cuts.Select(cut => cut.CutId));
        return MacroRepairFeedbackFactory.CreateProvenConditionalTimeConstraint(
            feedbackId,
            sourceCandidateFingerprint,
            sourceModelIdentity,
            sourceSolveResultIdentity,
            cuts.Select(cut => instance.StopsById[cut.EarlierEvent.StopId].StationName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            cuts,
            "For every legal route pair under each recorded stop and event-order premise, the " +
            "route table supplies the stated minimum gap. Changing the premise disables that row; " +
            "the route context itself is not excluded.");
    }

    private static ConditionalMacroTimeCut? TryProject(
        TimetablingInstance instance,
        MasterSolutionSnapshot reference,
        IReadOnlyDictionary<int, TrainRecord> trains,
        MicroConflictExplanation conflict)
    {
        if (!conflict.FirstTrainIndex.HasValue || !conflict.SecondTrainIndex.HasValue ||
            !conflict.StationOrder.HasValue || !conflict.SecondStationOrder.HasValue ||
            !conflict.FirstEventSide.HasValue || !conflict.SecondEventSide.HasValue ||
            !trains.TryGetValue(conflict.FirstTrainIndex.Value, out TrainRecord? firstTrain) ||
            !trains.TryGetValue(conflict.SecondTrainIndex.Value, out TrainRecord? secondTrain))
            return null;

        TrainStopRecord? firstStop = firstTrain.OrderedStops.FirstOrDefault(stop =>
            stop.StationOrder == conflict.StationOrder.Value && string.Equals(
                stop.StationName, conflict.StationName, StringComparison.OrdinalIgnoreCase));
        TrainStopRecord? secondStop = secondTrain.OrderedStops.FirstOrDefault(stop =>
            stop.StationOrder == conflict.SecondStationOrder.Value && string.Equals(
                stop.StationName, conflict.StationName, StringComparison.OrdinalIgnoreCase));
        if (firstStop is null || secondStop is null ||
            !reference.StopByStopId.TryGetValue(firstStop.StopId, out int firstStopValue) ||
            !reference.StopByStopId.TryGetValue(secondStop.StopId, out int secondStopValue))
            return null;

        StationMicroRequirement firstRequirement = StationLevelMicroFeasibilitySlave.BuildRequirementForMode(
            instance, firstTrain, firstStop, firstStopValue == 1,
            reference.ArrivalSecondsByStopId[firstStop.StopId],
            reference.DepartureSecondsByStopId[firstStop.StopId]);
        StationMicroRequirement secondRequirement = StationLevelMicroFeasibilitySlave.BuildRequirementForMode(
            instance, secondTrain, secondStop, secondStopValue == 1,
            reference.ArrivalSecondsByStopId[secondStop.StopId],
            reference.DepartureSecondsByStopId[secondStop.StopId]);
        RouteRecord[] leadingRoutes = ResolveEventRoutes(firstRequirement, conflict.FirstEventSide.Value);
        RouteRecord[] followingRoutes = ResolveEventRoutes(secondRequirement, conflict.SecondEventSide.Value);
        if (leadingRoutes.Length != 1 || followingRoutes.Length != 1)
            return null;

        double firstTime = ResolveEventTime(reference, firstStop.StopId, conflict.FirstEventSide.Value);
        double secondTime = ResolveEventTime(reference, secondStop.StopId, conflict.SecondEventSide.Value);
        double realizedGap = secondTime - firstTime;
        RoutePairHeadwayClassification classification = RoutePairHeadwayClassifier.Classify(
            leadingRoutes,
            followingRoutes,
            realizedGap,
            (leadingRouteId, followingRouteId) => instance.TryGetRouteHeadwayRule(
                leadingRouteId, followingRouteId, out RouteHeadwayRule? rule) && rule is not null
                ? rule.MinHeadwaySeconds
                : null);
        if (classification.Kind != RoutePairHeadwayClassificationKind.UnavoidableConflict ||
            classification.MissingPairCount != 0 ||
            !classification.MinimumRecordedHeadwaySeconds.HasValue)
            return null;
        if (!SameRouteDomain(conflict.LeadingRouteIds, leadingRoutes) ||
            !SameRouteDomain(conflict.FollowingRouteIds, followingRoutes))
            return null;

        StationEventConflictCandidate stationEdge = new(
            conflict.StationName,
            firstStop.TrainIndex,
            secondStop.TrainIndex,
            firstStop.StationOrder,
            secondStop.StationOrder,
            conflict.FirstEventSide.Value,
            conflict.SecondEventSide.Value);
        string stationOrderKey = BasicMilpModelBuilder.BuildStationEventPrecedenceKey(stationEdge);
        int gap = classification.MinimumRecordedHeadwaySeconds.Value;
        string[] businessRows = leadingRoutes.SelectMany(leading => followingRoutes.Select(following =>
        {
            instance.TryGetRouteHeadwayRule(leading.RouteId, following.RouteId, out RouteHeadwayRule? rule);
            return $"route_headway:{leading.RouteId}->{following.RouteId}:{rule!.MinHeadwaySeconds}";
        })).Order(StringComparer.Ordinal).ToArray();
        string cutId = Identity(
            "station_exact_headway",
            $"{conflict.StationName}:{firstStop.StopId}:{conflict.FirstEventSide}:" +
            $"{secondStop.StopId}:{conflict.SecondEventSide}:{gap}",
            businessRows);
        return new ConditionalMacroTimeCut(
            cutId,
            new MacroTimeEvent(secondStop.StopId, conflict.SecondEventSide.Value),
            new MacroTimeEvent(firstStop.StopId, conflict.FirstEventSide.Value),
            gap,
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                [firstStop.StopId] = firstStopValue,
                [secondStop.StopId] = secondStopValue
            },
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                [stationOrderKey] = 0
            },
            businessRows,
            $"station={conflict.StationName}; realized={realizedGap:R}; required={gap}; " +
            $"all_route_pairs_recorded={classification.RecordedPairCount}; missing=0");
    }

    private static RouteRecord[] ResolveEventRoutes(
        StationMicroRequirement requirement,
        StationEventSide side)
    {
        IReadOnlyList<RouteRecord> routes = side == StationEventSide.Incoming
            ? requirement.IncomingRoutes
            : requirement.OutgoingRoutes;
        HashSet<string> feasibleTracks = requirement.FeasibleTracks.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return routes.Where(route => feasibleTracks.Contains(route.TrackName))
            .OrderBy(route => route.RouteId)
            .ToArray();
    }

    private static bool SameRouteDomain(IReadOnlyList<int>? recorded, IReadOnlyList<RouteRecord> actual) =>
        recorded is null || recorded.Count == 0 || recorded.Order().SequenceEqual(actual.Select(route => route.RouteId));

    private static double ResolveEventTime(
        MasterSolutionSnapshot reference,
        string stopId,
        StationEventSide side) => side == StationEventSide.Incoming
        ? reference.ArrivalSecondsByStopId[stopId]
        : reference.DepartureSecondsByStopId[stopId];

    private static string Identity(string kind, string source, IEnumerable<string> details)
    {
        string payload = string.Join('\n', new[] { kind, source }.Concat(details.Order(StringComparer.Ordinal)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }
}
