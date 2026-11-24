namespace GurobiTimetablingResearch.Optimization;

public sealed record MacroMicroFeedbackPatternUse(
    IReadOnlyList<MicroWitnessCertificate> HardConstraintPatterns,
    IReadOnlyList<MicroWitnessCertificate> StartPatterns);

public sealed record MacroMicroStationConflictSignal(
    string StationName,
    int ConflictCount,
    IReadOnlyList<string> SupportStopIds,
    IReadOnlyList<int>? SupportTrainIndices = null,
    IReadOnlyList<string>? SupportPrecedenceKeys = null);

/// <summary>
/// Defines the safety boundary between independently saved station patterns and
/// a feedback-loop solve.  Same-candidate certificates outside the active proof
/// corridor protect their validated resource relations; certificates in the
/// corridor are released and all saved patterns remain available as starts.
/// </summary>
public static class MacroMicroFeedbackPolicy
{
    public static MacroMicroFeedbackPatternUse SelectPatternUse(
        IReadOnlyList<MicroWitnessCertificate> savedPatterns)
    {
        ArgumentNullException.ThrowIfNull(savedPatterns);
        return new([], savedPatterns.ToArray());
    }

    public static MacroMicroFeedbackPatternUse SelectPatternUse(
        IReadOnlyList<MicroWitnessCertificate> savedPatterns,
        MasterSolutionSnapshot reference,
        IReadOnlySet<string> activeStations)
    {
        ArgumentNullException.ThrowIfNull(savedPatterns);
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(activeStations);
        string fingerprint = SnapshotFingerprint.Calculate(reference);
        MicroWitnessCertificate[] protections = savedPatterns
            .Where(pattern => string.Equals(
                pattern.CandidateSnapshotFingerprint,
                fingerprint,
                StringComparison.OrdinalIgnoreCase))
            .Where(pattern => !activeStations.Contains(pattern.StationName))
            .Where(pattern => pattern.Evaluate(reference) == CertificateValidityOutcome.Valid)
            .ToArray();
        return new(protections, savedPatterns.ToArray());
    }

    public static IReadOnlyList<MicroWitnessCertificate> SelectActiveProtections(
        IReadOnlyList<MicroWitnessCertificate> candidateProtections,
        MasterSolutionSnapshot reference,
        IReadOnlySet<string> activeStations)
    {
        ArgumentNullException.ThrowIfNull(candidateProtections);
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(activeStations);
        return candidateProtections
            .Where(pattern => !activeStations.Contains(pattern.StationName))
            .Where(pattern => pattern.Evaluate(reference) == CertificateValidityOutcome.Valid)
            .ToArray();
    }

    public static DynamicHotspotEvidence CreateGlobalConflictEvidence(
        string proofIdentity,
        MicroSolveStatus status,
        IReadOnlyList<MacroMicroStationConflictSignal> signals,
        IReadOnlyList<string> corridorStationOrder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(proofIdentity);
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(corridorStationOrder);

        if (status != MicroSolveStatus.ProvenInfeasible)
        {
            return new DynamicHotspotEvidence(
                proofIdentity,
                DynamicHotspotProofStatus.Unknown,
                [],
                [],
                []);
        }

        Dictionary<string, int> routeIndex = corridorStationOrder
            .Select((station, index) => (station, index))
            .ToDictionary(item => item.station, item => item.index, StringComparer.OrdinalIgnoreCase);
        MacroMicroStationConflictSignal? seed = signals
            .Where(signal => !string.IsNullOrWhiteSpace(signal.StationName) && signal.ConflictCount > 0)
            .OrderByDescending(signal => signal.ConflictCount)
            .ThenBy(signal => routeIndex.TryGetValue(signal.StationName, out int index) ? index : int.MaxValue)
            .ThenBy(signal => signal.StationName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (seed is null)
        {
            return new DynamicHotspotEvidence(
                proofIdentity,
                DynamicHotspotProofStatus.MappingFailed,
                [],
                [],
                []);
        }

        MacroMicroStationConflictSignal[] candidates = signals
            .Where(signal => !string.IsNullOrWhiteSpace(signal.StationName) && signal.ConflictCount > 0)
            .ToArray();
        HashSet<MacroMicroStationConflictSignal> connected = [seed];
        Queue<MacroMicroStationConflictSignal> frontier = new([seed]);
        while (frontier.TryDequeue(out MacroMicroStationConflictSignal? current))
        {
            foreach (MacroMicroStationConflictSignal candidate in candidates)
            {
                if (connected.Contains(candidate) || !SharesBusinessDependency(current, candidate))
                    continue;
                connected.Add(candidate);
                frontier.Enqueue(candidate);
            }
        }
        MacroMicroStationConflictSignal[] selected = connected
            .OrderBy(signal => routeIndex.TryGetValue(signal.StationName, out int index)
                ? index
                : int.MaxValue)
            .ThenBy(signal => signal.StationName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string[] stopIds = selected.SelectMany(signal => signal.SupportStopIds)
            .Where(stopId => !string.IsNullOrWhiteSpace(stopId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        DynamicHotspotEvidenceRow[] rows = selected.SelectMany(signal =>
        {
            string[] supports = signal.SupportStopIds
                .Where(stopId => !string.IsNullOrWhiteSpace(stopId))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return supports.Length == 0
                ?
                [
                    new DynamicHotspotEvidenceRow(
                        $"global_micro::{signal.StationName}",
                        "global_micro_proven_conflict",
                        signal.StationName,
                        null,
                        DynamicHotspotEvidenceDisposition.Adjustable)
                ]
                : supports.Select(stopId => new DynamicHotspotEvidenceRow(
                    $"global_micro::{signal.StationName}::{stopId}",
                    "global_micro_proven_conflict",
                    signal.StationName,
                    stopId,
                    DynamicHotspotEvidenceDisposition.Adjustable)).ToArray();
        }).ToArray();

        return new DynamicHotspotEvidence(
            proofIdentity,
            DynamicHotspotProofStatus.HardProof,
            rows,
            selected.Select(signal => signal.StationName).ToArray(),
            stopIds);
    }

    private static bool SharesBusinessDependency(
        MacroMicroStationConflictSignal left,
        MacroMicroStationConflictSignal right)
    {
        if (string.Equals(left.StationName, right.StationName, StringComparison.OrdinalIgnoreCase))
            return true;
        IReadOnlyList<int> leftTrains = left.SupportTrainIndices ?? [];
        IReadOnlyList<int> rightTrains = right.SupportTrainIndices ?? [];
        if (leftTrains.Intersect(rightTrains).Any()) return true;
        IReadOnlyList<string> leftKeys = left.SupportPrecedenceKeys ?? [];
        IReadOnlyList<string> rightKeys = right.SupportPrecedenceKeys ?? [];
        return leftKeys.Intersect(rightKeys, StringComparer.OrdinalIgnoreCase).Any();
    }

    internal static DynamicHotspotEvidence CreateIntegratedInfeasibilityEvidence(
        Domain.TimetablingInstance instance,
        string proofIdentity,
        IReadOnlyList<string> currentStations,
        IReadOnlyList<ContinuousProofRow> proofRows,
        IReadOnlyList<string>? currentStopIds = null,
        IReadOnlyList<string>? corridorStationOrder = null)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentException.ThrowIfNullOrWhiteSpace(proofIdentity);
        ArgumentNullException.ThrowIfNull(currentStations);
        ArgumentNullException.ThrowIfNull(proofRows);
        if (proofRows.Count == 0)
            return new(proofIdentity, DynamicHotspotProofStatus.EmptyProof, [], [], []);

        IReadOnlyList<ContinuousProofBusinessMapping> mappings =
            ContinuousProofDependencyMapper.Map(instance, currentStations, proofRows, []);
        HashSet<string> active = currentStations.ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] route = (corridorStationOrder ?? instance.TrainStops
                .GroupBy(stop => stop.StationOrder)
                .OrderBy(group => group.Key)
                .Select(group => group.Select(stop => stop.StationName)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Single()))
            .ToArray();
        Dictionary<string, int> routeIndex = route.Select((station, index) => (station, index))
            .ToDictionary(item => item.station, item => item.index, StringComparer.OrdinalIgnoreCase);
        int[] activeIndices = currentStations.Where(routeIndex.ContainsKey).Select(station => routeIndex[station]).ToArray();
        int activeMinimum = activeIndices.Length == 0 ? 0 : activeIndices.Min();
        int activeMaximum = activeIndices.Length == 0 ? 0 : activeIndices.Max();
        var outside = mappings.Select((mapping, index) => (Mapping: mapping, Index: index))
            .SelectMany(item => item.Mapping.Decisions.Select(decision =>
                (Decision: decision, item.Index)))
            .Where(item => !active.Contains(item.Decision.StationName))
            .Where(item => item.Decision.Reason.Contains("outside", StringComparison.OrdinalIgnoreCase))
            .Where(item => routeIndex.ContainsKey(item.Decision.StationName))
            .DistinctBy(item => (item.Decision.DecisionFamily.ToUpperInvariant(),
                item.Decision.DecisionKey.ToUpperInvariant()))
            .OrderByDescending(item => Math.Abs(proofRows[item.Index].FarkasDual))
            .ThenBy(item =>
            {
                int index = routeIndex[item.Decision.StationName];
                return Math.Max(activeMaximum, index) - Math.Min(activeMinimum, index);
            })
            .ThenBy(item => item.Index)
            .ThenBy(item => item.Decision.StationName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        ProofMappedDecision[] selected = outside.Length > 0
            ? [outside[0].Decision]
            : mappings.SelectMany(mapping => mapping.Decisions)
                .Where(decision => decision.IsAdjustable)
                .DistinctBy(decision => (decision.DecisionFamily.ToUpperInvariant(),
                    decision.DecisionKey.ToUpperInvariant()))
                .ToArray();
        if (selected.Length == 0)
            return new(proofIdentity, DynamicHotspotProofStatus.MappingFailed, [], [], []);

        DynamicHotspotEvidenceRow[] rows = selected.Select(decision => new DynamicHotspotEvidenceRow(
            $"integrated_dependency::{decision.DecisionFamily}::{decision.DecisionKey}",
            decision.Reason,
            decision.StationName,
            string.Equals(decision.DecisionFamily, "stop", StringComparison.OrdinalIgnoreCase)
                ? decision.DecisionKey
                : null,
            DynamicHotspotEvidenceDisposition.Adjustable)).ToArray();
        string[] stations = currentStations.Concat(selected.Select(decision => decision.StationName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string[] stopIds = (currentStopIds ?? []).Concat(selected
            .Where(decision => string.Equals(decision.DecisionFamily, "stop", StringComparison.OrdinalIgnoreCase))
            .Select(decision => decision.DecisionKey))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(stopId => stopId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new(proofIdentity, DynamicHotspotProofStatus.HardProof, rows, stations, stopIds);
    }
}
