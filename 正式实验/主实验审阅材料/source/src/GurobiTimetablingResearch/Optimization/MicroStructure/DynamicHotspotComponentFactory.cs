using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

public sealed record DynamicHotspotStationAudit(
    string StationName,
    int RequirementCount,
    int AlternativeCount,
    int EstimatedOrderVariables,
    int EstimatedConstraints,
    IReadOnlyList<string> RequirementIds);

public sealed record DynamicHotspotComponentBuildResult(
    bool IsComplete,
    string Status,
    IReadOnlyList<PromotedMicroComponent> Components,
    IReadOnlyList<DynamicHotspotStationAudit> StationAudits);

public static class DynamicHotspotComponentFactory
{
    public static DynamicHotspotComponentBuildResult Build(
        TimetablingInstance instance,
        MasterSolutionSnapshot reference,
        IReadOnlyList<string> stationNames,
        int endpointOccupationSeconds,
        string sourceProofIdentity)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(stationNames);
        if (endpointOccupationSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(endpointOccupationSeconds));

        List<PromotedMicroComponent> components = [];
        List<DynamicHotspotStationAudit> audits = [];
        Dictionary<int, TrainRecord> trains = instance.Trains.ToDictionary(train => train.TrainIndex);
        foreach (string stationName in stationNames
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            TrainStopRecord[] stops = instance.TrainStops
                .Where(stop => string.Equals(stop.StationName, stationName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(stop => stop.TrainIndex)
                .ThenBy(stop => stop.StationOrder)
                .ToArray();
            if (stops.Length == 0)
                return new(false, $"station_not_in_instance::{stationName}", components, audits);

            List<PromotedStationRequirement> requirements = [];
            foreach (TrainStopRecord stop in stops)
            {
                if (!reference.ArrivalSecondsByStopId.TryGetValue(stop.StopId, out double arrival) ||
                    !reference.DepartureSecondsByStopId.TryGetValue(stop.StopId, out double departure) ||
                    !reference.StopByStopId.TryGetValue(stop.StopId, out int referenceStop))
                    return new(false, $"reference_missing_stop::{stop.StopId}", components, audits);

                TrainRecord train = trains[stop.TrainIndex];
                IReadOnlyList<PromotedRouteTrackAlternative> stopping = BuildAlternatives(
                    instance, train, stop, true, arrival, departure, endpointOccupationSeconds);
                IReadOnlyList<PromotedRouteTrackAlternative> passing = BuildAlternatives(
                    instance, train, stop, false, arrival, departure, endpointOccupationSeconds);
                StopModeDomainAnalysis domain = StopModeDomainResolver.Analyze(instance, train, stop);
                bool originalSingleMode =
                    (domain.Resolution == StopModeResolution.ForceStop && stopping.Count > 0 && passing.Count == 0) ||
                    (domain.Resolution == StopModeResolution.ForcePass && stopping.Count == 0 && passing.Count > 0);
                HotspotStopModeSelection selection = HotspotStopModePolicy.Resolve(
                    stop.StopRequirement,
                    stop.StopRole,
                    stopping.Count,
                    passing.Count,
                    originalSingleMode);
                if (selection.IncludedModes.Count == 0)
                    return new(false, $"empty_original_micro_domain::{stop.StopId}", components, audits);

                List<PromotedRouteTrackAlternative> alternatives = [];
                if (selection.IncludedModes.Contains(true)) alternatives.AddRange(stopping);
                if (selection.IncludedModes.Contains(false)) alternatives.AddRange(passing);
                requirements.Add(new PromotedStationRequirement(
                    stop.StopId,
                    stop.TrainIndex,
                    stop.StationOrder,
                    selection.PreserveAllLegalStopModes ? referenceStop >= 1 : selection.IncludedModes.Single(),
                    alternatives.Distinct().ToArray(),
                    selection.PreserveAllLegalStopModes));
            }

            PromotedMicroCompactRelationEstimate relationEstimate = PromotedMicroCompactRelationEstimator.Estimate(
                requirements,
                (leading, following) => instance.TryGetRouteHeadwayRule(
                    leading, following, out RouteHeadwayRule? rule) && rule is not null);
            int fixedStopRows = requirements.Count(requirement =>
                !PromotedStopModeLinkPlanBuilder.Build(requirement).IsFlexible);
            int estimatedConstraints = checked(
                2 * requirements.Count + relationEstimate.ConditionalConstraintCount + fixedStopRows);
            PromotedMicroComponent component = new(
                $"dynamic_hotspot::{stationName}",
                stationName,
                requirements.Select(requirement => requirement.TrainIndex).Distinct().Order().ToArray(),
                requirements,
                "proof_selected_full_station_original_domain",
                sourceProofIdentity,
                0,
                1,
                true);
            components.Add(component);
            audits.Add(new DynamicHotspotStationAudit(
                stationName,
                requirements.Count,
                requirements.Sum(requirement => requirement.AllowedAlternatives.Count),
                relationEstimate.OrderVariableCount,
                estimatedConstraints,
                requirements.Select(requirement => requirement.RequirementId).ToArray()));
        }

        return new(true, "complete_original_micro_domains", components, audits);
    }

    private static IReadOnlyList<PromotedRouteTrackAlternative> BuildAlternatives(
        TimetablingInstance instance,
        TrainRecord train,
        TrainStopRecord stop,
        bool isStop,
        double arrivalSeconds,
        double departureSeconds,
        int endpointOccupationSeconds)
    {
        StationMicroRequirement requirement = StationLevelMicroFeasibilitySlave.BuildRequirementForMode(
            instance,
            train,
            stop,
            isStop,
            arrivalSeconds,
            departureSeconds);
        StationOccupationInterval occupation = StationEndpointOccupationPolicy.Resolve(
            requirement.StopRole,
            requirement.IsStop,
            requirement.ArrivalSeconds,
            requirement.DepartureSeconds,
            endpointOccupationSeconds,
            0.0,
            86400.0);
        StationAssignmentRequirement assignment = new(
            requirement.StopId,
            requirement.TrainIndex,
            requirement.IsStop,
            requirement.ArrivalSeconds,
            requirement.DepartureSeconds,
            occupation.StartSeconds,
            occupation.EndSeconds,
            requirement.IncomingRoutes,
            requirement.OutgoingRoutes,
            requirement.FeasibleTracks);
        return StationRouteTrackAssignmentSolver.BuildAlternatives(assignment)
            .Select(alternative => new PromotedRouteTrackAlternative(
                alternative.TrackName,
                alternative.IncomingRoute?.RouteId,
                alternative.OutgoingRoute?.RouteId,
                isStop))
            .Distinct()
            .ToArray();
    }
}
