using System.Security.Cryptography;
using System.Text;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

/// <summary>
/// Projects a two-requirement irreducible resource core only when both stopping requirements are
/// forced onto the same single track.  In that domain, non-overlap of the two occupation intervals
/// is a necessary business relation for every route assignment, so Macro may choose either order
/// but may not return another overlapping timetable.
/// </summary>
internal static class UniqueTrackOccupationMacroFeedbackProjector
{
    private const double CheckerToleranceSeconds = 0.0;
    private const double TimeDomainLowerBound = 0.0;
    private const double TimeDomainUpperBound = 86400.0;

    internal static MacroRepairFeedback? TryCreate(
        TimetablingInstance instance,
        MasterSolutionSnapshot reference,
        string sourceCandidateFingerprint,
        string sourceModelIdentity,
        string sourceSolveResultIdentity,
        MicroSolveStatus status,
        StationConflictCore core,
        int endpointOccupationSeconds,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(core);
        if (status != MicroSolveStatus.ProvenInfeasible)
        {
            reason = $"{status.ToString().ToLowerInvariant()}_does_not_create_track_occupation_cut";
            return null;
        }
        if (!string.Equals(core.PrimaryConflictKind,
                "station_route_track_assignment_infeasible", StringComparison.OrdinalIgnoreCase))
        {
            reason = $"unsupported_conflict_core_kind::{core.PrimaryConflictKind}";
            return null;
        }
        string actualFingerprint = SnapshotFingerprint.Calculate(reference);
        if (!string.Equals(actualFingerprint, sourceCandidateFingerprint,
                StringComparison.OrdinalIgnoreCase))
        {
            reason = "source_candidate_fingerprint_mismatch";
            return null;
        }

        string[] stopIds = core.SupportStopIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(stopId => stopId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (stopIds.Length != 2)
        {
            reason = $"track_occupation_projection_requires_two_core_requirements;actual={stopIds.Length}";
            return null;
        }

        Dictionary<int, TrainRecord> trains = instance.Trains.ToDictionary(train => train.TrainIndex);
        List<(TrainStopRecord Stop, StationMicroRequirement Requirement, StationOccupationInterval Occupation)> items = [];
        foreach (string stopId in stopIds)
        {
            if (!instance.StopsById.TryGetValue(stopId, out TrainStopRecord? stop) ||
                !trains.TryGetValue(stop.TrainIndex, out TrainRecord? train) ||
                !reference.StopByStopId.TryGetValue(stopId, out int stopValue) ||
                !reference.ArrivalSecondsByStopId.TryGetValue(stopId, out double arrival) ||
                !reference.DepartureSecondsByStopId.TryGetValue(stopId, out double departure))
            {
                reason = $"track_occupation_core_stop_mapping_incomplete::{stopId}";
                return null;
            }
            if (stopValue != 1)
            {
                reason = $"track_occupation_core_requires_current_stop::{stopId}={stopValue}";
                return null;
            }
            // Endpoint occupation uses max/min clipping at the day boundary.  Until that piecewise
            // expression is represented exactly in Macro, keep those cores on the retractable path.
            if (stop.StopRole != TrainStopRole.Intermediate)
            {
                reason = $"endpoint_track_occupation_requires_exact_clipped_projection::{stopId}";
                return null;
            }
            StationMicroRequirement requirement = StationLevelMicroFeasibilitySlave.BuildRequirementForMode(
                instance, train, stop, true, arrival, departure);
            if (requirement.FeasibleTracks.Count != 1)
            {
                reason = $"track_domain_not_unique::{stopId}:{requirement.FeasibleTracks.Count}";
                return null;
            }
            StationOccupationInterval occupation = StationEndpointOccupationPolicy.Resolve(
                stop.StopRole,
                true,
                arrival,
                departure,
                endpointOccupationSeconds,
                TimeDomainLowerBound,
                TimeDomainUpperBound);
            items.Add((stop, requirement, occupation));
        }

        if (!string.Equals(items[0].Stop.StationName, items[1].Stop.StationName,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(items[0].Stop.StationName, core.StationName,
                StringComparison.OrdinalIgnoreCase))
        {
            reason = "track_occupation_core_spans_multiple_stations";
            return null;
        }
        string firstTrack = items[0].Requirement.FeasibleTracks.Single();
        string secondTrack = items[1].Requirement.FeasibleTracks.Single();
        if (!string.Equals(firstTrack, secondTrack, StringComparison.OrdinalIgnoreCase))
        {
            reason = $"unique_tracks_do_not_conflict::{firstTrack}|{secondTrack}";
            return null;
        }
        bool overlaps = items[0].Occupation.StartSeconds <
                items[1].Occupation.EndSeconds - CheckerToleranceSeconds &&
            items[0].Occupation.EndSeconds >
                items[1].Occupation.StartSeconds + CheckerToleranceSeconds;
        if (!overlaps)
        {
            reason = "current_unique_track_occupations_do_not_overlap";
            return null;
        }

        string cutId = Identity(
            "unique_track_occupation",
            core.StationName,
            firstTrack,
            stopIds[0],
            stopIds[1]);
        ConditionalMacroTrackOccupationCut cut = new(
            cutId,
            core.StationName,
            firstTrack,
            new MacroAffineTimeEvent(new MacroTimeEvent(stopIds[0], StationEventSide.Incoming), 0.0),
            new MacroAffineTimeEvent(new MacroTimeEvent(stopIds[0], StationEventSide.Outgoing), 0.0),
            new MacroAffineTimeEvent(new MacroTimeEvent(stopIds[1], StationEventSide.Incoming), 0.0),
            new MacroAffineTimeEvent(new MacroTimeEvent(stopIds[1], StationEventSide.Outgoing), 0.0),
            CheckerToleranceSeconds,
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                [stopIds[0]] = 1,
                [stopIds[1]] = 1
            },
            [$"station_track_non_overlap:{core.StationName}:{firstTrack}"],
            $"station={core.StationName};track={firstTrack};" +
            $"first=[{items[0].Occupation.StartSeconds:R},{items[0].Occupation.EndSeconds:R}];" +
            $"second=[{items[1].Occupation.StartSeconds:R},{items[1].Occupation.EndSeconds:R}];" +
            $"checker_tolerance={CheckerToleranceSeconds:R};both_track_domains_are_singletons");
        reason = $"projected_unique_track_occupation_disjunction;station={core.StationName};" +
                 $"track={firstTrack};stops={string.Join('|', stopIds)}";
        return MacroRepairFeedbackFactory.CreateProvenConditionalTrackOccupationConstraint(
            Identity("unique_track_occupation_feedback", cutId),
            sourceCandidateFingerprint,
            sourceModelIdentity,
            sourceSolveResultIdentity,
            [core.StationName],
            [cut],
            "Both stopping requirements have the same immutable singleton feasible-track domain. " +
            "Their occupation intervals must be ordered, but Macro may choose either order.");
    }

    private static string Identity(params string[] values)
    {
        string payload = string.Join('\n', values);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }
}
