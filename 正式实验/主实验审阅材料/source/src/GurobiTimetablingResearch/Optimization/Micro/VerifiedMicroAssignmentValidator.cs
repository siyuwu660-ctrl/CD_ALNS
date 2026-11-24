using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

internal sealed record VerifiedMicroAssignmentValidationFinding(
    string Code,
    string Detail);

internal sealed record VerifiedMicroAssignmentValidationReport(
    bool IsValid,
    int ExpectedRequirementCount,
    int WitnessCount,
    int ValidatedRequirementCount,
    int ValidatedStationCount,
    int MissingOrderedRoutePairCount,
    IReadOnlyList<VerifiedMicroAssignmentValidationFinding> Findings);

/// <summary>
/// 直接验证随候选保存的微观分配，不用重新搜索得到的另一组分配替代原见证。
/// 它逐项检查业务域、候选版本、股道占用和已选进路事件间隔，用于封存可追溯基准。
/// </summary>
internal static class VerifiedMicroAssignmentValidator
{
    private const double ToleranceSeconds = GurobiTimetablingResearch.Domain.TimetableTimePolicy.NumericToleranceSeconds;

    internal static VerifiedMicroAssignmentValidationReport Validate(
        TimetablingInstance instance,
        MasterSolutionSnapshot snapshot,
        IReadOnlyList<VerifiedMicroAssignmentWitness> witnesses,
        StationLevelMicroFeasibilitySlaveResult stationAnalysis,
        int endpointOccupationSeconds)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(witnesses);
        ArgumentNullException.ThrowIfNull(stationAnalysis);

        List<VerifiedMicroAssignmentValidationFinding> findings = [];
        string fingerprint = SnapshotFingerprint.Calculate(snapshot);
        IReadOnlyList<StationMicroRequirement> allRequirements = stationAnalysis.StationResults
            .SelectMany(station => station.Requirements)
            .ToList();
        Dictionary<string, StationMicroRequirement> requirementById = BuildUniqueMap(
            allRequirements,
            requirement => requirement.StopId,
            "duplicate_current_requirement",
            findings);
        Dictionary<string, VerifiedMicroAssignmentWitness> witnessById = BuildUniqueMap(
            witnesses,
            witness => witness.RequirementId,
            "duplicate_saved_witness",
            findings);
        Dictionary<string, ValidatedChoice> validated = new(StringComparer.OrdinalIgnoreCase);

        foreach ((string requirementId, StationMicroRequirement requirement) in requirementById)
        {
            if (!witnessById.TryGetValue(requirementId, out VerifiedMicroAssignmentWitness? witness))
            {
                findings.Add(new VerifiedMicroAssignmentValidationFinding(
                    "missing_saved_witness",
                    $"requirement='{requirementId}', station='{requirement.StationName}', train={requirement.TrainIndex}/'{requirement.TrainNo}'"));
                continue;
            }
            if (!TryValidateChoice(instance, snapshot, fingerprint, requirement, witness, endpointOccupationSeconds,
                    findings, out ValidatedChoice? choice))
            {
                continue;
            }
            validated.Add(requirementId, choice!);
        }

        foreach (VerifiedMicroAssignmentWitness extra in witnesses.Where(
                     witness => !requirementById.ContainsKey(witness.RequirementId)))
        {
            findings.Add(new VerifiedMicroAssignmentValidationFinding(
                "orphan_saved_witness",
                $"requirement='{extra.RequirementId}', station='{extra.StationName}', train={extra.TrainIndex}"));
        }

        int missingOrderedRoutePairs = 0;
        foreach (IGrouping<string, ValidatedChoice> station in validated.Values
                     .GroupBy(choice => choice.Requirement.StationName, StringComparer.OrdinalIgnoreCase))
        {
            ValidatedChoice[] choices = station.ToArray();
            for (int firstIndex = 0; firstIndex < choices.Length; firstIndex++)
            {
                for (int secondIndex = firstIndex + 1; secondIndex < choices.Length; secondIndex++)
                {
                    ValidatedChoice first = choices[firstIndex];
                    ValidatedChoice second = choices[secondIndex];
                    if (                        string.Equals(first.Witness.TrackName, second.Witness.TrackName,
                            StringComparison.OrdinalIgnoreCase) &&
                        first.OccupationStartSeconds < second.OccupationEndSeconds - ToleranceSeconds &&
                        first.OccupationEndSeconds > second.OccupationStartSeconds + ToleranceSeconds)
                    {
                        findings.Add(new VerifiedMicroAssignmentValidationFinding(
                            "saved_track_occupation_overlap",
                            $"station='{station.Key}', track='{first.Witness.TrackName}', " +
                            $"first='{first.Requirement.StopId}'[{first.OccupationStartSeconds:F3},{first.OccupationEndSeconds:F3}], " +
                            $"second='{second.Requirement.StopId}'[{second.OccupationStartSeconds:F3},{second.OccupationEndSeconds:F3}]"));
                    }

                    foreach (SelectedEvent firstEvent in first.Events)
                    {
                        foreach (SelectedEvent secondEvent in second.Events)
                        {
                            SelectedRouteEventHeadwayDecision decision = SelectedRouteEventHeadwayPolicy.Evaluate(
                                firstEvent.RouteId,
                                firstEvent.TimeSeconds,
                                secondEvent.RouteId,
                                secondEvent.TimeSeconds,
                                (leading, following) => FindHeadway(instance, leading, following));
                            if (decision.RuleBasis.Contains("missing", StringComparison.Ordinal))
                            {
                                missingOrderedRoutePairs++;
                            }
                            if (decision.Kind == SelectedRouteEventHeadwayDecisionKind.Violated)
                            {
                                findings.Add(new VerifiedMicroAssignmentValidationFinding(
                                    "saved_route_headway_violation",
                                    $"station='{station.Key}', first='{first.Requirement.StopId}'/{firstEvent.Side}/route{firstEvent.RouteId}@{firstEvent.TimeSeconds:F3}, " +
                                    $"second='{second.Requirement.StopId}'/{secondEvent.Side}/route{secondEvent.RouteId}@{secondEvent.TimeSeconds:F3}, " +
                                    $"required={decision.RequiredHeadwaySeconds}, gap={decision.RealizedGapSeconds:F3}, basis={decision.RuleBasis}"));
                            }
                        }
                    }
                }
            }
        }

        return new VerifiedMicroAssignmentValidationReport(
            findings.Count == 0 && validated.Count == allRequirements.Count && witnesses.Count == allRequirements.Count,
            allRequirements.Count,
            witnesses.Count,
            validated.Count,
            validated.Values.Select(choice => choice.Requirement.StationName)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            missingOrderedRoutePairs,
            findings);
    }

    private static bool TryValidateChoice(
        TimetablingInstance instance,
        MasterSolutionSnapshot snapshot,
        string fingerprint,
        StationMicroRequirement requirement,
        VerifiedMicroAssignmentWitness witness,
        int endpointOccupationSeconds,
        List<VerifiedMicroAssignmentValidationFinding> findings,
        out ValidatedChoice? choice)
    {
        choice = null;
        List<string> errors = [];
        if (!string.Equals(witness.CandidateSnapshotFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"fingerprint={witness.CandidateSnapshotFingerprint}, expected={fingerprint}");
        }
        if (!string.Equals(witness.VerificationScope, "station_micro_validated", StringComparison.Ordinal))
        {
            errors.Add($"scope={witness.VerificationScope}");
        }
        if (!string.Equals(witness.StationName, requirement.StationName, StringComparison.OrdinalIgnoreCase) ||
            witness.TrainIndex != requirement.TrainIndex || witness.IsStop != requirement.IsStop)
        {
            errors.Add("station/train/stop-mode mismatch");
        }
        if (requirement.StopRequirement == StopRequirement.MustStop && !witness.IsStop ||
            requirement.StopRequirement == StopRequirement.MustPass && witness.IsStop)
        {
            errors.Add($"hard stop requirement={requirement.StopRequirement}");
        }
        if (!requirement.FeasibleTracks.Contains(witness.TrackName, StringComparer.OrdinalIgnoreCase))
        {
            errors.Add($"track='{witness.TrackName}' outside feasible domain");
        }
        RouteRecord? incoming = ResolveRoute(requirement.IncomingRoutes, witness.IncomingRouteId);
        RouteRecord? outgoing = ResolveRoute(requirement.OutgoingRoutes, witness.OutgoingRouteId);
        // Validate raw direction independently of the candidate-domain membership check.
        if (incoming is not null && !RouteEventDirectionPolicy.Matches(incoming, true) ||
            outgoing is not null && !RouteEventDirectionPolicy.Matches(outgoing, false))
            errors.Add("selected route has the wrong incoming/outgoing operation type");
        if (requirement.RequiresIncomingRoute && incoming is null ||
            !requirement.RequiresIncomingRoute && witness.IncomingRouteId.HasValue)
        {
            errors.Add($"incoming route={witness.IncomingRouteId?.ToString() ?? "missing"} outside current domain");
        }
        if (requirement.RequiresOutgoingRoute && outgoing is null ||
            !requirement.RequiresOutgoingRoute && witness.OutgoingRouteId.HasValue)
        {
            errors.Add($"outgoing route={witness.OutgoingRouteId?.ToString() ?? "missing"} outside current domain");
        }
        if (incoming is not null && !string.Equals(incoming.TrackName, witness.TrackName, StringComparison.OrdinalIgnoreCase) ||
            outgoing is not null && !string.Equals(outgoing.TrackName, witness.TrackName, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("selected route track does not match saved track");
        }
        double arrival = Read(snapshot.ArrivalSecondsByStopId, requirement.StopId, "arrival", errors);
        double departure = Read(snapshot.DepartureSecondsByStopId, requirement.StopId, "departure", errors);
        if (!witness.ArrivalSeconds.HasValue || Math.Abs(witness.ArrivalSeconds.Value - arrival) > ToleranceSeconds ||
            !witness.DepartureSeconds.HasValue || Math.Abs(witness.DepartureSeconds.Value - departure) > ToleranceSeconds ||
            Math.Abs(requirement.ArrivalSeconds - arrival) > ToleranceSeconds ||
            Math.Abs(requirement.DepartureSeconds - departure) > ToleranceSeconds)
        {
            errors.Add("saved/current requirement times do not match the candidate snapshot");
        }
        if (errors.Count > 0)
        {
            findings.Add(new VerifiedMicroAssignmentValidationFinding(
                "invalid_saved_witness",
                $"requirement='{requirement.StopId}', station='{requirement.StationName}', train={requirement.TrainIndex}/'{requirement.TrainNo}': {string.Join("; ", errors)}"));
            return false;
        }

        (double occupationStart, double occupationEnd) = requirement.StopRole switch
        {
            TrainStopRole.Origin => (departure - endpointOccupationSeconds, departure),
            TrainStopRole.Destination => (arrival, arrival + endpointOccupationSeconds),
            _ => (arrival, departure)
        };
        List<SelectedEvent> events = [];
        if (incoming is not null)
        {
            events.Add(new SelectedEvent(incoming.RouteId, arrival, "arrival"));
        }
        if (outgoing is not null)
        {
            events.Add(new SelectedEvent(outgoing.RouteId, departure, "departure"));
        }
        choice = new ValidatedChoice(requirement, witness, occupationStart, occupationEnd, events);
        return true;
    }

    private static Dictionary<string, T> BuildUniqueMap<T>(
        IEnumerable<T> source,
        Func<T, string> keySelector,
        string duplicateCode,
        List<VerifiedMicroAssignmentValidationFinding> findings)
    {
        Dictionary<string, T> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (IGrouping<string, T> group in source.GroupBy(keySelector, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Count() != 1)
            {
                findings.Add(new VerifiedMicroAssignmentValidationFinding(
                    duplicateCode,
                    $"business_id='{group.Key}', count={group.Count()}"));
                continue;
            }
            result.Add(group.Key, group.Single());
        }
        return result;
    }

    private static RouteRecord? ResolveRoute(IReadOnlyList<RouteRecord> routes, int? routeId) =>
        routeId.HasValue ? routes.SingleOrDefault(route => route.RouteId == routeId.Value) : null;

    private static double Read(
        IReadOnlyDictionary<string, double> values,
        string stopId,
        string side,
        List<string> errors)
    {
        if (values.TryGetValue(stopId, out double value))
        {
            return value;
        }
        errors.Add($"missing {side} time");
        return double.NaN;
    }

    private static int? FindHeadway(TimetablingInstance instance, int leadingRouteId, int followingRouteId) =>
        instance.TryGetRouteHeadwayRule(leadingRouteId, followingRouteId, out RouteHeadwayRule? rule) && rule is not null
            ? rule.MinHeadwaySeconds
            : null;

    private sealed record SelectedEvent(int RouteId, double TimeSeconds, string Side);

    private sealed record ValidatedChoice(
        StationMicroRequirement Requirement,
        VerifiedMicroAssignmentWitness Witness,
        double OccupationStartSeconds,
        double OccupationEndSeconds,
        IReadOnlyList<SelectedEvent> Events);
}
