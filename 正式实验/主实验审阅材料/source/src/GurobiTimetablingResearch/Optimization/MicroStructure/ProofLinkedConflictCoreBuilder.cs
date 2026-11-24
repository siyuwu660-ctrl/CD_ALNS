using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

internal sealed record ProofLinkedConflictCore(
    string ProofIdentity,
    IReadOnlySet<string> StationNames,
    IReadOnlySet<string> StopIds,
    IReadOnlySet<string> SectionPrecedenceKeys,
    IReadOnlySet<string> StationEventPrecedenceKeys,
    IReadOnlySet<string> AffectedTimeStopIds,
    IReadOnlyList<string> DependencyReasons,
    bool IsComplete,
    string Status);

/// <summary>
/// Translates one candidate-bound proof into the smallest supported Macro interface.  It does
/// not infer that every stop of a named train is adjustable.  Original hard rows are retained as
/// explanatory reasons, while only explicit/structurally necessary Macro decisions are opened.
/// </summary>
internal static class ProofLinkedConflictCoreBuilder
{
    internal static ProofLinkedConflictCore Build(
        TimetablingInstance instance,
        MasterSolutionSnapshot reference,
        DynamicHotspotEvidence evidence,
        string expectedCandidateFingerprint)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedCandidateFingerprint);

        if (evidence.Status != DynamicHotspotProofStatus.HardProof)
            return Empty(evidence, "proof_not_hard");
        if (string.IsNullOrWhiteSpace(evidence.SourceCandidateFingerprint))
            return Empty(evidence, "proof_candidate_identity_missing");
        if (!string.Equals(evidence.SourceCandidateFingerprint, expectedCandidateFingerprint,
                StringComparison.OrdinalIgnoreCase))
            return Empty(evidence, "proof_belongs_to_another_candidate");

        HashSet<string> stations = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> stops = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> timeStops = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> sectionKeys = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> stationEventKeys = new(StringComparer.OrdinalIgnoreCase);
        List<string> reasons = [];
        bool mappingFailed = false;

        foreach (DynamicHotspotEvidenceRow row in evidence.Rows)
        {
            if (row.Disposition == DynamicHotspotEvidenceDisposition.ImmutableBusinessRule)
            {
                reasons.Add($"immutable::{row.ConstraintName}::{row.BusinessSource}");
                continue;
            }
            if (row.Disposition == DynamicHotspotEvidenceDisposition.Unmapped)
            {
                mappingFailed = true;
                reasons.Add($"unmapped::{row.ConstraintName}");
                continue;
            }
            if (!string.IsNullOrWhiteSpace(row.StationName)) stations.Add(row.StationName);
            if (string.IsNullOrWhiteSpace(row.StopId))
            {
                AddExplicitDecisionFromEvidenceRow(
                    reference, row, sectionKeys, stationEventKeys, reasons, ref mappingFailed);
                continue;
            }
            if (!instance.StopsById.TryGetValue(row.StopId, out TrainStopRecord? stop))
            {
                mappingFailed = true;
                reasons.Add($"missing_stop::{row.StopId}");
                continue;
            }

            stations.Add(stop.StationName);
            timeStops.Add(stop.StopId);
            if (IsStopAdjustable(stop)) stops.Add(stop.StopId);
            AddAdjacentTimeDependencies(instance, stop, timeStops, stations, reasons);
            AddOrderDependencies(instance, reference, stop, sectionKeys, stationEventKeys, reasons);
            if (MentionsQuota(row))
                AddChangeableQuotaPeers(instance, stop, stops, timeStops, stations, reasons);
        }

        foreach (string suggestedStopId in evidence.SuggestedStopIds)
        {
            if (!instance.StopsById.TryGetValue(suggestedStopId, out TrainStopRecord? stop))
            {
                mappingFailed = true;
                reasons.Add($"missing_suggested_stop::{suggestedStopId}");
                continue;
            }
            stations.Add(stop.StationName);
            timeStops.Add(stop.StopId);
            if (IsStopAdjustable(stop)) stops.Add(stop.StopId);
            AddAdjacentTimeDependencies(instance, stop, timeStops, stations, reasons);
            AddOrderDependencies(instance, reference, stop, sectionKeys, stationEventKeys, reasons);
        }
        foreach (string station in evidence.SuggestedStations.Where(value => !string.IsNullOrWhiteSpace(value)))
            stations.Add(station);

        bool complete = !mappingFailed && (stops.Count + sectionKeys.Count + stationEventKeys.Count > 0);
        string status = complete
            ? "proof_linked_core"
            : mappingFailed
                ? "proof_dependency_mapping_incomplete"
                : "proof_has_no_adjustable_macro_decision";
        return new ProofLinkedConflictCore(
            evidence.ProofIdentity, stations, stops, sectionKeys, stationEventKeys, timeStops,
            reasons.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            complete, status);
    }

    internal static ProofLinkedConflictCore ExpandOrderParticipants(
        TimetablingInstance instance,
        MasterSolutionSnapshot reference,
        ProofLinkedConflictCore core)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(core);
        if (!core.IsComplete) return core with { Status = "dependency_expansion_requires_complete_core" };

        HashSet<string> stations = core.StationNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> stops = core.StopIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> timeStops = core.AffectedTimeStopIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<string> reasons = core.DependencyReasons.ToList();
        foreach (SectionConflictCandidate candidate in new InitialConflictGraphBuilder(instance, int.MaxValue).Build())
        {
            string key = BasicMilpModelBuilder.BuildSectionPrecedenceKey(candidate);
            if (!core.SectionPrecedenceKeys.Contains(key)) continue;
            AddParticipant(instance, candidate.FirstTrainIndex, candidate.FirstStopOrder, stops, timeStops, stations, reasons, key);
            AddParticipant(instance, candidate.FirstTrainIndex, candidate.FirstEndStopOrder, stops, timeStops, stations, reasons, key);
            AddParticipant(instance, candidate.SecondTrainIndex, candidate.SecondStopOrder, stops, timeStops, stations, reasons, key);
            AddParticipant(instance, candidate.SecondTrainIndex, candidate.SecondEndStopOrder, stops, timeStops, stations, reasons, key);
        }
        return core with
        {
            StationNames = stations,
            StopIds = stops,
            AffectedTimeStopIds = timeStops,
            DependencyReasons = reasons.Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            Status = "dependency_expanded_core"
        };
    }

    private static void AddParticipant(
        TimetablingInstance instance,
        int trainIndex,
        int stationOrder,
        ISet<string> stops,
        ISet<string> timeStops,
        ISet<string> stations,
        ICollection<string> reasons,
        string precedenceKey)
    {
        TrainStopRecord? participant = instance.TrainStops.FirstOrDefault(stop =>
            stop.TrainIndex == trainIndex && stop.StationOrder == stationOrder);
        if (participant is null) return;
        timeStops.Add(participant.StopId);
        stations.Add(participant.StationName);
        if (IsStopAdjustable(participant)) stops.Add(participant.StopId);
        reasons.Add($"paired_order_participant::{precedenceKey}::{participant.StopId}");
    }

    private static void AddAdjacentTimeDependencies(
        TimetablingInstance instance,
        TrainStopRecord seed,
        ISet<string> timeStops,
        ISet<string> stations,
        ICollection<string> reasons)
    {
        TrainRecord? train = instance.Trains.FirstOrDefault(item => item.TrainIndex == seed.TrainIndex);
        if (train is null) return;
        foreach (TrainStopRecord adjacent in train.OrderedStops.Where(stop =>
                     Math.Abs(stop.StationOrder - seed.StationOrder) == 1))
        {
            timeStops.Add(adjacent.StopId);
            stations.Add(adjacent.StationName);
            reasons.Add($"runtime_boundary::{seed.StopId}::{adjacent.StopId}");
        }
    }

    private static void AddExplicitDecisionFromEvidenceRow(
        MasterSolutionSnapshot reference,
        DynamicHotspotEvidenceRow row,
        ISet<string> sectionKeys,
        ISet<string> stationEventKeys,
        ICollection<string> reasons,
        ref bool mappingFailed)
    {
        const string prefix = "integrated_dependency::";
        if (!row.ConstraintName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return;
        string payload = row.ConstraintName[prefix.Length..];
        int separator = payload.IndexOf("::", StringComparison.Ordinal);
        if (separator < 0)
        {
            mappingFailed = true;
            reasons.Add($"malformed_integrated_dependency::{row.ConstraintName}");
            return;
        }
        string family = payload[..separator];
        string key = payload[(separator + 2)..];
        if (family.Equals("section_precedence", StringComparison.OrdinalIgnoreCase) &&
            reference.SectionPrecedenceByKey.ContainsKey(key))
        {
            sectionKeys.Add(key);
            reasons.Add($"explicit_section_order::{key}");
            return;
        }
        if (family.Equals("station_event_precedence", StringComparison.OrdinalIgnoreCase) &&
            reference.StationEventPrecedenceByKey.ContainsKey(key))
        {
            stationEventKeys.Add(key);
            reasons.Add($"explicit_station_event_order::{key}");
            return;
        }
        mappingFailed = true;
        reasons.Add($"unresolved_integrated_dependency::{family}::{key}");
    }

    private static void AddOrderDependencies(
        TimetablingInstance instance,
        MasterSolutionSnapshot reference,
        TrainStopRecord seed,
        ISet<string> sectionKeys,
        ISet<string> stationEventKeys,
        ICollection<string> reasons)
    {
        foreach (SectionConflictCandidate candidate in new InitialConflictGraphBuilder(instance, int.MaxValue).Build())
        {
            bool touchesSeed = (candidate.FirstTrainIndex == seed.TrainIndex &&
                                SegmentTouches(candidate.FirstStopOrder, candidate.FirstEndStopOrder, seed.StationOrder)) ||
                               (candidate.SecondTrainIndex == seed.TrainIndex &&
                                SegmentTouches(candidate.SecondStopOrder, candidate.SecondEndStopOrder, seed.StationOrder));
            if (!touchesSeed) continue;
            string key = BasicMilpModelBuilder.BuildSectionPrecedenceKey(candidate);
            if (!reference.SectionPrecedenceByKey.ContainsKey(key)) continue;
            sectionKeys.Add(key);
            reasons.Add($"boundary_section_order::{seed.StopId}::{key}");
        }

        foreach (string key in reference.StationEventPrecedenceByKey.Keys)
        {
            int separator = key.IndexOf("::", StringComparison.Ordinal);
            string station = separator < 0 ? key : key[..separator];
            if (!string.Equals(station, seed.StationName, StringComparison.OrdinalIgnoreCase) ||
                !KeyMentionsTrain(key, seed.TrainIndex)) continue;
            stationEventKeys.Add(key);
            reasons.Add($"station_event_order::{seed.StopId}::{key}");
        }
    }

    private static void AddChangeableQuotaPeers(
        TimetablingInstance instance,
        TrainStopRecord seed,
        ISet<string> stops,
        ISet<string> timeStops,
        ISet<string> stations,
        ICollection<string> reasons)
    {
        foreach (StationStopQuotaRule rule in instance.StationStopQuotaRules.Where(rule =>
                     string.Equals(rule.StationName, seed.StationName, StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(rule.TrainType, seed.TrainType, StringComparison.OrdinalIgnoreCase)))
        {
            if (!CanChangeQuotaContribution(seed, rule)) continue;
            foreach (TrainStopRecord peer in instance.TrainStops.Where(stop =>
                         string.Equals(stop.StationName, rule.StationName, StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(stop.TrainType, rule.TrainType, StringComparison.OrdinalIgnoreCase) &&
                         CanChangeQuotaContribution(stop, rule)))
            {
                timeStops.Add(peer.StopId);
                stations.Add(peer.StationName);
                if (IsStopAdjustable(peer)) stops.Add(peer.StopId);
                reasons.Add($"changeable_quota_peer::{seed.StopId}::{peer.StopId}::" +
                            $"{rule.StartTimeSeconds}-{rule.EndTimeSeconds}");
            }
        }
    }

    private static bool CanChangeQuotaContribution(TrainStopRecord stop, StationStopQuotaRule rule) =>
        IsStopAdjustable(stop) || stop.EarliestArrSeconds < rule.StartTimeSeconds ||
        stop.LatestArrSeconds > rule.EndTimeSeconds;

    private static bool MentionsQuota(DynamicHotspotEvidenceRow row) =>
        row.ConstraintName.Contains("quota", StringComparison.OrdinalIgnoreCase) ||
        row.BusinessSource.Contains("quota", StringComparison.OrdinalIgnoreCase);

    private static bool IsStopAdjustable(TrainStopRecord stop) =>
        stop.StopRequirement == StopRequirement.OptionalStop && stop.StopRole == TrainStopRole.Intermediate;

    private static bool SegmentTouches(int first, int second, int order) =>
        order >= Math.Min(first, second) && order <= Math.Max(first, second);

    private static bool KeyMentionsTrain(string key, int trainIndex) =>
        key.Split("::", StringSplitOptions.None).Any(part =>
            int.TryParse(part, out int value) && value == trainIndex);

    private static ProofLinkedConflictCore Empty(DynamicHotspotEvidence evidence, string status) => new(
        evidence.ProofIdentity,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        [status], false, status);
}
