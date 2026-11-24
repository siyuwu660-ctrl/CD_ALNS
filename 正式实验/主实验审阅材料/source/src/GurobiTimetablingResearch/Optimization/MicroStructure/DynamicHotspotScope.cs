namespace GurobiTimetablingResearch.Optimization;

public enum DynamicHotspotProofStatus
{
    HardProof,
    EmptyProof,
    MappingFailed,
    Unknown
}

public enum DynamicHotspotEvidenceDisposition
{
    Adjustable,
    ImmutableBusinessRule,
    Unmapped
}

public sealed record DynamicHotspotEvidenceRow(
    string ConstraintName,
    string BusinessSource,
    string? StationName,
    string? StopId,
    DynamicHotspotEvidenceDisposition Disposition);

public sealed record DynamicHotspotEvidence(
    string ProofIdentity,
    DynamicHotspotProofStatus Status,
    IReadOnlyList<DynamicHotspotEvidenceRow> Rows,
    IReadOnlyList<string> SuggestedStations,
    IReadOnlyList<string> SuggestedStopIds)
{
    /// <summary>
    /// Candidate identity to which the proof belongs.  This is deliberately separate from
    /// ProofIdentity: two solve attempts may use the same business rows but different Macro
    /// values, and their repair scopes must never be exchanged.
    /// </summary>
    public string? SourceCandidateFingerprint { get; init; }
}

public sealed record DynamicHotspotStationCost(
    string StationName,
    int Variables,
    int ActiveBinaries,
    int Constraints);

public sealed record DynamicHotspotScopeBudget(
    int MaximumVariables,
    int MaximumActiveBinaries,
    int MaximumConstraints);

public sealed record DynamicHotspotScopeSelection(
    bool IsAccepted,
    string Status,
    string ProofIdentity,
    IReadOnlyList<string> StationNames,
    IReadOnlyList<string> RequiredStationNames,
    IReadOnlyList<string> StopIds,
    int EstimatedVariables,
    int EstimatedActiveBinaries,
    int EstimatedConstraints)
{
    public IReadOnlySet<string>? ReleasedSectionPrecedenceKeys { get; init; }
    public IReadOnlySet<string>? ReleasedStationEventPrecedenceKeys { get; init; }
    public IReadOnlySet<string>? AffectedTimeStopIds { get; init; }
}

public static class DynamicHotspotScopeSelector
{
    internal static DynamicHotspotScopeSelection SelectProofLinkedForExecution(
        ProofLinkedConflictCore core,
        IReadOnlyDictionary<string, DynamicHotspotStationCost> costsByStation,
        DynamicHotspotScopeBudget budget)
    {
        ArgumentNullException.ThrowIfNull(core);
        DynamicHotspotEvidence projected = new(
            core.ProofIdentity,
            core.IsComplete ? DynamicHotspotProofStatus.HardProof : DynamicHotspotProofStatus.MappingFailed,
            core.StationNames.Select(station => new DynamicHotspotEvidenceRow(
                $"proof-linked::{station}", "proof_linked_core", station, null,
                DynamicHotspotEvidenceDisposition.Adjustable)).ToArray(),
            core.StationNames.ToArray(),
            core.StopIds.ToArray());
        DynamicHotspotScopeSelection selected = Select(
            projected,
            costsByStation,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
            budget);
        if (selected.IsAccepted)
            return selected with
            {
                Status = "proof_linked_core",
                StopIds = core.StopIds.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
                ReleasedSectionPrecedenceKeys = core.SectionPrecedenceKeys,
                ReleasedStationEventPrecedenceKeys = core.StationEventPrecedenceKeys,
                AffectedTimeStopIds = core.AffectedTimeStopIds
            };
        if (!string.Equals(selected.Status, "required_dependency_closure_exceeds_budget",
                StringComparison.Ordinal))
            return selected;

        // A size estimate controls scheduling, not mathematical correctness.  Once a dependency
        // is part of a hard proof closure it may not be silently truncated by an engineering cap.
        return selected with
        {
            IsAccepted = true,
            Status = "proof_linked_core_over_budget_audit_only",
            StationNames = selected.RequiredStationNames,
            StopIds = core.StopIds.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
            ReleasedSectionPrecedenceKeys = core.SectionPrecedenceKeys,
            ReleasedStationEventPrecedenceKeys = core.StationEventPrecedenceKeys,
            AffectedTimeStopIds = core.AffectedTimeStopIds
        };
    }

    public static DynamicHotspotScopeSelection SelectContiguousForExecution(
        DynamicHotspotEvidence evidence,
        IReadOnlyList<string> orderedCorridorStations,
        IReadOnlyDictionary<string, DynamicHotspotStationCost> costsByStation,
        IReadOnlyDictionary<string, IReadOnlyList<string>> requiredDependenciesByStation,
        DynamicHotspotScopeBudget budget)
    {
        DynamicHotspotScopeSelection selected = SelectContiguous(
            evidence,
            orderedCorridorStations,
            costsByStation,
            requiredDependenciesByStation,
            budget);
        if (selected.IsAccepted || !string.Equals(
                selected.Status,
                "contiguous_proof_corridor_exceeds_budget",
                StringComparison.Ordinal))
            return selected;
        return selected with
        {
            IsAccepted = true,
            Status = "accepted_contiguous_proof_corridor_over_budget_audit_only",
            StationNames = selected.RequiredStationNames
        };
    }

    public static DynamicHotspotScopeSelection SelectContiguous(
        DynamicHotspotEvidence evidence,
        IReadOnlyList<string> orderedCorridorStations,
        IReadOnlyDictionary<string, DynamicHotspotStationCost> costsByStation,
        IReadOnlyDictionary<string, IReadOnlyList<string>> requiredDependenciesByStation,
        DynamicHotspotScopeBudget budget)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(orderedCorridorStations);
        ArgumentNullException.ThrowIfNull(costsByStation);
        ArgumentNullException.ThrowIfNull(requiredDependenciesByStation);
        ArgumentNullException.ThrowIfNull(budget);
        if (budget.MaximumVariables <= 0 || budget.MaximumActiveBinaries <= 0 || budget.MaximumConstraints <= 0)
            throw new ArgumentOutOfRangeException(nameof(budget), "All scope budgets must be positive.");

        string[] route = orderedCorridorStations
            .Where(station => !string.IsNullOrWhiteSpace(station))
            .ToArray();
        if (route.Length != orderedCorridorStations.Count ||
            route.Distinct(StringComparer.OrdinalIgnoreCase).Count() != route.Length)
            throw new ArgumentException("The corridor station order must contain unique nonempty station names.",
                nameof(orderedCorridorStations));
        if (evidence.Status != DynamicHotspotProofStatus.HardProof)
        {
            string status = evidence.Status switch
            {
                DynamicHotspotProofStatus.EmptyProof => "proof_rows_empty",
                DynamicHotspotProofStatus.MappingFailed => "proof_mapping_failed",
                _ => "proof_unavailable_unknown"
            };
            return Rejected(evidence, status, []);
        }

        HashSet<string> required = CollectAdjustableStations(evidence);
        if (required.Count == 0)
            return Rejected(evidence, "hard_proof_has_no_adjustable_station", []);
        Dictionary<string, int> routeIndex = route
            .Select((station, index) => (station, index))
            .ToDictionary(item => item.station, item => item.index, StringComparer.OrdinalIgnoreCase);

        bool changed;
        do
        {
            changed = false;
            Queue<string> dependencies = new(required);
            while (dependencies.Count > 0)
            {
                string station = dependencies.Dequeue();
                if (!requiredDependenciesByStation.TryGetValue(station, out IReadOnlyList<string>? values))
                    continue;
                foreach (string dependency in values.Where(value => !string.IsNullOrWhiteSpace(value)))
                {
                    if (!required.Add(dependency)) continue;
                    dependencies.Enqueue(dependency);
                    changed = true;
                }
            }

            string[] missingFromRoute = required.Where(station => !routeIndex.ContainsKey(station))
                .OrderBy(station => station, StringComparer.OrdinalIgnoreCase).ToArray();
            if (missingFromRoute.Length > 0)
                return Rejected(evidence,
                    $"proof_station_not_on_corridor::{string.Join('|', missingFromRoute)}",
                    missingFromRoute);

            int first = required.Min(station => routeIndex[station]);
            int last = required.Max(station => routeIndex[station]);
            for (int index = first; index <= last; index++)
                if (required.Add(route[index])) changed = true;
        } while (changed);

        string[] orderedRequired = required.OrderBy(station => routeIndex[station]).ToArray();
        List<string> missingCosts = orderedRequired.Where(station => !costsByStation.ContainsKey(station)).ToList();
        if (missingCosts.Count > 0)
            return Rejected(evidence, $"station_cost_missing::{string.Join('|', missingCosts)}", orderedRequired);

        int variables = checked(orderedRequired.Sum(station => costsByStation[station].Variables));
        int binaries = checked(orderedRequired.Sum(station => costsByStation[station].ActiveBinaries));
        int constraints = checked(orderedRequired.Sum(station => costsByStation[station].Constraints));
        if (variables > budget.MaximumVariables ||
            binaries > budget.MaximumActiveBinaries ||
            constraints > budget.MaximumConstraints)
        {
            return new DynamicHotspotScopeSelection(
                false,
                "contiguous_proof_corridor_exceeds_budget",
                evidence.ProofIdentity,
                [],
                orderedRequired,
                CollectStopIds(evidence),
                variables,
                binaries,
                constraints);
        }

        return new DynamicHotspotScopeSelection(
            true,
            "accepted_contiguous_proof_corridor",
            evidence.ProofIdentity,
            orderedRequired,
            orderedRequired,
            CollectStopIds(evidence),
            variables,
            binaries,
            constraints);
    }

    public static DynamicHotspotScopeSelection Select(
        DynamicHotspotEvidence evidence,
        IReadOnlyDictionary<string, DynamicHotspotStationCost> costsByStation,
        IReadOnlyDictionary<string, IReadOnlyList<string>> requiredDependenciesByStation,
        DynamicHotspotScopeBudget budget)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(costsByStation);
        ArgumentNullException.ThrowIfNull(requiredDependenciesByStation);
        ArgumentNullException.ThrowIfNull(budget);
        if (budget.MaximumVariables <= 0 || budget.MaximumActiveBinaries <= 0 || budget.MaximumConstraints <= 0)
            throw new ArgumentOutOfRangeException(nameof(budget), "All scope budgets must be positive.");
        if (costsByStation.Values.Any(cost => string.IsNullOrWhiteSpace(cost.StationName) ||
                                             cost.Variables < 0 || cost.ActiveBinaries < 0 || cost.Constraints < 0))
            throw new ArgumentOutOfRangeException(nameof(costsByStation), "Station identity and nonnegative costs are required.");

        if (evidence.Status != DynamicHotspotProofStatus.HardProof)
        {
            string status = evidence.Status switch
            {
                DynamicHotspotProofStatus.EmptyProof => "proof_rows_empty",
                DynamicHotspotProofStatus.MappingFailed => "proof_mapping_failed",
                _ => "proof_unavailable_unknown"
            };
            return Rejected(evidence, status, []);
        }

        HashSet<string> mappedStations = CollectAdjustableStations(evidence);
        if (mappedStations.Count == 0)
            return Rejected(evidence, "hard_proof_has_no_adjustable_station", []);

        Queue<string> pending = new(mappedStations.OrderBy(station => station, StringComparer.OrdinalIgnoreCase));
        HashSet<string> required = new(mappedStations, StringComparer.OrdinalIgnoreCase);
        while (pending.Count > 0)
        {
            string station = pending.Dequeue();
            if (!requiredDependenciesByStation.TryGetValue(station, out IReadOnlyList<string>? dependencies))
                continue;
            foreach (string dependency in dependencies.Where(value => !string.IsNullOrWhiteSpace(value)))
            {
                if (required.Add(dependency)) pending.Enqueue(dependency);
            }
        }

        string[] orderedRequired = required.OrderBy(station => station, StringComparer.OrdinalIgnoreCase).ToArray();
        List<string> missingCosts = orderedRequired.Where(station => !costsByStation.ContainsKey(station)).ToList();
        if (missingCosts.Count > 0)
            return Rejected(evidence, $"station_cost_missing::{string.Join('|', missingCosts)}", orderedRequired);

        int variables = checked(orderedRequired.Sum(station => costsByStation[station].Variables));
        int binaries = checked(orderedRequired.Sum(station => costsByStation[station].ActiveBinaries));
        int constraints = checked(orderedRequired.Sum(station => costsByStation[station].Constraints));
        if (variables > budget.MaximumVariables ||
            binaries > budget.MaximumActiveBinaries ||
            constraints > budget.MaximumConstraints)
        {
            return new DynamicHotspotScopeSelection(
                false,
                "required_dependency_closure_exceeds_budget",
                evidence.ProofIdentity,
                [],
                orderedRequired,
                CollectStopIds(evidence),
                variables,
                binaries,
                constraints);
        }

        return new DynamicHotspotScopeSelection(
            true,
            "accepted_complete_required_dependency_closure",
            evidence.ProofIdentity,
            orderedRequired,
            orderedRequired,
            CollectStopIds(evidence),
            variables,
            binaries,
            constraints);
    }

    private static string[] CollectStopIds(DynamicHotspotEvidence evidence) => evidence.Rows
        .Where(row => row.Disposition == DynamicHotspotEvidenceDisposition.Adjustable)
        .Select(row => row.StopId)
        .Where(stopId => !string.IsNullOrWhiteSpace(stopId))
        .Select(stopId => stopId!)
        .Concat(evidence.SuggestedStopIds.Where(stopId => !string.IsNullOrWhiteSpace(stopId)))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(stopId => stopId, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static HashSet<string> CollectAdjustableStations(DynamicHotspotEvidence evidence)
    {
        HashSet<string> mappedStations = evidence.Rows
            .Where(row => row.Disposition == DynamicHotspotEvidenceDisposition.Adjustable &&
                          !string.IsNullOrWhiteSpace(row.StationName))
            .Select(row => row.StationName!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string station in evidence.SuggestedStations.Where(station => !string.IsNullOrWhiteSpace(station)))
            mappedStations.Add(station);
        return mappedStations;
    }

    private static DynamicHotspotScopeSelection Rejected(
        DynamicHotspotEvidence evidence,
        string status,
        IReadOnlyList<string> required) => new(
        false,
        status,
        evidence.ProofIdentity,
        [],
        required,
        CollectStopIds(evidence),
        0,
        0,
        0);
}
