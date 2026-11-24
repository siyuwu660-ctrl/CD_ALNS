namespace GurobiTimetablingResearch.Optimization;

internal enum ExtendabilityScopeActionKind
{
    ApplyTimeFeedback,
    ExpandToImplicatedOutsideStation,
    CurrentScopeIncompatible,
    UnknownNoAction
}

internal sealed record ExtendabilityScopeDecision(
    ExtendabilityScopeActionKind Action,
    IReadOnlyList<string> ActiveStations,
    IReadOnlyList<string> EvidenceDecisionIds,
    string Reason);

/// <summary>
/// Selects the smallest next Micro scope supported by proof-to-business mappings.
/// It never treats an IIS row as a cut and never releases a whole train/station merely
/// because its name occurred in a proof.  The caller is responsible for preserving K
/// and all original business limits when it rebuilds the selected one/two-station MIP.
/// </summary>
internal static class ExtendabilityScopeDecisionPolicy
{
    internal static ExtendabilityScopeDecision Select(
        IReadOnlyList<string> currentStations,
        IReadOnlyList<ContinuousProofBusinessMapping> mappings,
        bool hasValidTimeFeedback = false,
        bool coordinationWasUnknown = false)
    {
        ArgumentNullException.ThrowIfNull(currentStations);
        ArgumentNullException.ThrowIfNull(mappings);
        string[] current = currentStations.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (current.Length is < 1 or > 3)
            throw new ArgumentException("The current Micro scope must contain one to three stations.", nameof(currentStations));

        if (hasValidTimeFeedback)
            return new(ExtendabilityScopeActionKind.ApplyTimeFeedback, current, [],
                "a model-local bound-complete Farkas boundary can be added to the retained local MIP");
        if (coordinationWasUnknown)
            return new(ExtendabilityScopeActionKind.UnknownNoAction, current, [],
                "continuous coordination ended Unknown; no infeasibility action is valid");

        HashSet<string> currentSet = current.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var outside = mappings
            .Where(mapping => mapping.Status == ProofDecisionMappingStatus.OutsideMacroScope)
            .SelectMany(mapping => mapping.Decisions.Select(decision => new { mapping, decision }))
            .Where(item => !string.IsNullOrWhiteSpace(item.decision.StationName) &&
                           !currentSet.Contains(item.decision.StationName) &&
                           item.decision.DecisionFamily is "micro_pattern" or "stop" or "section_precedence")
            .GroupBy(item => item.decision.StationName, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                Station = group.Key,
                RowCount = group.Select(item => item.mapping.ConstraintName)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                PatternCount = group.Count(item => item.decision.DecisionFamily == "micro_pattern"),
                DecisionIds = group.Select(item => $"{item.decision.DecisionFamily}:{item.decision.DecisionKey}")
                    .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray()
            })
            .OrderByDescending(item => item.PatternCount)
            .ThenByDescending(item => item.RowCount)
            .ThenBy(item => item.Station, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (outside is not null)
        {
            string anchor = current
                .OrderByDescending(station => mappings.Count(mapping => mapping.Decisions.Any(decision =>
                    string.Equals(decision.StationName, station, StringComparison.OrdinalIgnoreCase))))
                .ThenBy(station => station, StringComparer.OrdinalIgnoreCase)
                .First();
            string[] expanded = current.Length < 3
                ? current.Concat([outside.Station]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                : [anchor, outside.Station];
            return new(ExtendabilityScopeActionKind.ExpandToImplicatedOutsideStation,
                expanded, outside.DecisionIds,
                $"proof maps {outside.RowCount} rows ({outside.PatternCount} retained-pattern rows) to outside station '{outside.Station}'");
        }

        string[] adjustable = mappings
            .SelectMany(mapping => mapping.Decisions)
            .Where(decision => decision.IsAdjustable)
            .Select(decision => $"{decision.DecisionFamily}:{decision.DecisionKey}")
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        return new(ExtendabilityScopeActionKind.CurrentScopeIncompatible, current, adjustable,
            adjustable.Length > 0
                ? "proof contains only current-scope adjustable conditions; replace that exact local condition set"
                : "proof contains no permitted adjustable condition in or outside the current scope");
    }
}

internal enum ExtendabilityScopeBranchDisposition
{
    Active,
    PausedUnknown,
    ProvenInfeasible
}

internal sealed record ExtendabilityScopeBranchState(
    string FrozenBaselineIdentity,
    string ScopeKey,
    IReadOnlyList<string> ActiveStations,
    double CumulativeSeconds,
    int DistinctNonExtendableCandidates,
    int UnknownCount,
    int ScheduleCount,
    ExtendabilityScopeBranchDisposition Disposition,
    IReadOnlyList<string> CandidateIdentities,
    IReadOnlyDictionary<string, int> EvidenceStationHits,
    string LastReason);

/// <summary>
/// Owns the heuristic time slice for one frozen Macro reference.  Its key deliberately
/// excludes the current stop vector and the generated feedback set: rebuilding the same
/// logical station scope must not reset either elapsed time or distinct-candidate evidence.
/// </summary>
internal sealed class ExtendabilityScopeScheduler
{
    private readonly string _frozenBaselineIdentity;
    private readonly double _scopeSliceSeconds;
    private readonly int _distinctCandidateLimit;
    private readonly int _maximumStations;
    private string[] _rootStations;
    private readonly Dictionary<string, MutableBranch> _branches = new(StringComparer.OrdinalIgnoreCase);

    internal ExtendabilityScopeScheduler(
        string frozenBaselineIdentity,
        double scopeSliceSeconds = 300,
        int distinctCandidateLimit = 5,
        int maximumStations = 3,
        IReadOnlyList<string>? rootStations = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(frozenBaselineIdentity);
        if (scopeSliceSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(scopeSliceSeconds));
        if (distinctCandidateLimit <= 0) throw new ArgumentOutOfRangeException(nameof(distinctCandidateLimit));
        if (maximumStations is < 2 or > 3) throw new ArgumentOutOfRangeException(nameof(maximumStations));
        _frozenBaselineIdentity = frozenBaselineIdentity;
        _scopeSliceSeconds = scopeSliceSeconds;
        _distinctCandidateLimit = distinctCandidateLimit;
        _maximumStations = maximumStations;
        _rootStations = rootStations is null
            ? []
            : rootStations.Where(station => !string.IsNullOrWhiteSpace(station))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (_rootStations.Length > maximumStations)
            throw new ArgumentException("The root scope exceeds the maximum station count.", nameof(rootStations));
    }

    internal static string ScopeKey(IReadOnlyList<string> stations)
    {
        ArgumentNullException.ThrowIfNull(stations);
        string[] canonical = stations.Where(station => !string.IsNullOrWhiteSpace(station))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (canonical.Length is < 1 or > 3)
            throw new ArgumentException("A logical Micro scope must contain one to three stations.", nameof(stations));
        return string.Join('|', canonical);
    }

    internal void AccountWork(IReadOnlyList<string> stations, double elapsedSeconds)
    {
        if (elapsedSeconds < 0 || double.IsNaN(elapsedSeconds) || double.IsInfinity(elapsedSeconds))
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        Branch(stations).CumulativeSeconds += elapsedSeconds;
    }

    internal bool RecordProvenNonExtendable(
        IReadOnlyList<string> stations,
        string candidateIdentity,
        IReadOnlyList<ContinuousProofBusinessMapping> mappings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateIdentity);
        ArgumentNullException.ThrowIfNull(mappings);
        MutableBranch branch = Branch(stations);
        if (!branch.CandidateIdentities.Add(candidateIdentity)) return false;
        branch.Mappings.AddRange(mappings.Select(mapping => mapping with
        {
            Decisions = mapping.Decisions.Select(decision => decision with { }).ToArray()
        }));
        foreach (string station in mappings.SelectMany(mapping => mapping.Decisions)
                     .Where(decision => !string.IsNullOrWhiteSpace(decision.StationName) &&
                                        !branch.ActiveStations.Contains(decision.StationName!, StringComparer.OrdinalIgnoreCase))
                     .Select(decision => decision.StationName!))
            branch.EvidenceStationHits[station] = branch.EvidenceStationHits.GetValueOrDefault(station) + 1;
        branch.LastReason = "model-local proof rejected a distinct local candidate";
        return true;
    }

    internal void AddHistoricalEvidence(
        IReadOnlyList<string> stations,
        IReadOnlyList<ContinuousProofBusinessMapping> mappings)
    {
        ArgumentNullException.ThrowIfNull(mappings);
        MutableBranch branch = Branch(stations);
        branch.Mappings.AddRange(mappings.Select(mapping => mapping with
        {
            Decisions = mapping.Decisions.Select(decision => decision with { }).ToArray()
        }));
        foreach (string station in mappings.SelectMany(mapping => mapping.Decisions)
                     .Where(decision => !string.IsNullOrWhiteSpace(decision.StationName) &&
                                        !branch.ActiveStations.Contains(decision.StationName!, StringComparer.OrdinalIgnoreCase))
                     .Select(decision => decision.StationName!))
            branch.EvidenceStationHits[station] = branch.EvidenceStationHits.GetValueOrDefault(station) + 1;
    }

    internal void RecordUnknown(IReadOnlyList<string> stations, string reason)
    {
        MutableBranch branch = Branch(stations);
        branch.UnknownCount++;
        branch.LastReason = string.IsNullOrWhiteSpace(reason) ? "unknown" : reason;
    }

    internal void RecordProvenInfeasible(IReadOnlyList<string> stations, string reason)
    {
        MutableBranch branch = Branch(stations);
        branch.Disposition = ExtendabilityScopeBranchDisposition.ProvenInfeasible;
        branch.LastReason = reason;
    }

    internal bool ShouldYield(IReadOnlyList<string> stations)
    {
        MutableBranch branch = Branch(stations);
        return branch.Disposition != ExtendabilityScopeBranchDisposition.ProvenInfeasible &&
               (branch.CumulativeSeconds >= _scopeSliceSeconds ||
                branch.CandidateIdentities.Count >= _distinctCandidateLimit);
    }

    internal double RemainingSliceSeconds(IReadOnlyList<string> stations) =>
        Math.Max(0, _scopeSliceSeconds - Branch(stations).CumulativeSeconds);

    internal ExtendabilityScopeDecision SelectNext(
        IReadOnlyList<string> currentStations,
        IReadOnlyDictionary<string, int>? addedFreedomCostByStation = null)
    {
        MutableBranch current = Branch(currentStations);
        if (!ShouldYield(currentStations))
            return new(ExtendabilityScopeActionKind.UnknownNoAction, current.ActiveStations, [],
                $"scope ledger below yield threshold: seconds={current.CumulativeSeconds:R};distinct={current.CandidateIdentities.Count}");

        HashSet<string> currentSet = current.ActiveStations.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var options = current.Mappings
            .Where(mapping => mapping.Status == ProofDecisionMappingStatus.OutsideMacroScope)
            .SelectMany(mapping => mapping.Decisions.Select(decision => new { mapping, decision }))
            .Where(item => !string.IsNullOrWhiteSpace(item.decision.StationName) &&
                           !currentSet.Contains(item.decision.StationName!) &&
                           item.decision.DecisionFamily is "micro_pattern" or "stop" or "section_precedence")
            .GroupBy(item => item.decision.StationName!, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                string[] proposed = BuildProposedScope(current.ActiveStations, group.Key, current.Mappings);
                MutableBranch target = Branch(proposed);
                return new
                {
                    Station = group.Key,
                    Proposed = proposed,
                    PatternHits = group.Count(item => item.decision.DecisionFamily == "micro_pattern"),
                    TotalHits = group.Count(),
                    TargetAttempts = target.ScheduleCount,
                    FreedomCost = addedFreedomCostByStation?.GetValueOrDefault(group.Key, int.MaxValue) ?? int.MaxValue,
                    Decisions = group.Select(item => $"{item.decision.DecisionFamily}:{item.decision.DecisionKey}")
                        .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray()
                };
            })
            .Where(option => !option.Proposed.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(currentSet) &&
                             option.TargetAttempts == 0)
            .OrderByDescending(option => option.PatternHits)
            .ThenByDescending(option => option.TotalHits)
            .ThenBy(option => option.TargetAttempts)
            .ThenBy(option => option.FreedomCost)
            .ThenBy(option => option.Station, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (options is null)
            return new(ExtendabilityScopeActionKind.UnknownNoAction, current.ActiveStations, [],
                "yield threshold reached but no proof-related legal outside station is mapped");

        string trigger = current.CandidateIdentities.Count >= _distinctCandidateLimit
            ? $"{current.CandidateIdentities.Count} distinct candidates proven non-extendable"
            : $"scope cumulative wall time {current.CumulativeSeconds:R}s reached {_scopeSliceSeconds:R}s";
        return new(ExtendabilityScopeActionKind.ExpandToImplicatedOutsideStation,
            options.Proposed, options.Decisions,
            $"{trigger}; evidence selected station '{options.Station}' with pattern_hits={options.PatternHits}, total_hits={options.TotalHits}, prior_scope_attempts={options.TargetAttempts}");
    }

    internal void ConfirmSwitch(IReadOnlyList<string> fromStations, IReadOnlyList<string> toStations, string reason)
    {
        MutableBranch from = Branch(fromStations);
        MutableBranch to = Branch(toStations);
        if (string.Equals(from.ScopeKey, to.ScopeKey, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A scope switch must change the stable station set.");
        if (_rootStations.Length == 0) _rootStations = from.ActiveStations.ToArray();
        // A time-limited child has no model-local infeasibility proof of its own.  Retain
        // the immutable proof mappings that selected it so another evidence-linked legal
        // scope can still be scheduled after this child consumes its logical time slice.
        // These rows guide scheduling only; feedback activation remains independently
        // bound to its exact mathematical context by ContinuousExtendabilityFeedback.
        AddHistoricalEvidence(to.ActiveStations, from.Mappings);
        if (from.Disposition != ExtendabilityScopeBranchDisposition.ProvenInfeasible)
            from.Disposition = ExtendabilityScopeBranchDisposition.PausedUnknown;
        from.LastReason = reason;
        to.Disposition = ExtendabilityScopeBranchDisposition.Active;
        to.ScheduleCount++;
        to.LastReason = $"scheduled from {from.ScopeKey}: {reason}";
    }

    internal IReadOnlyList<ExtendabilityScopeBranchState> CaptureStates() => _branches.Values
        .OrderBy(branch => branch.ScopeKey, StringComparer.OrdinalIgnoreCase)
        .Select(branch => new ExtendabilityScopeBranchState(
            _frozenBaselineIdentity,
            branch.ScopeKey,
            branch.ActiveStations.ToArray(),
            branch.CumulativeSeconds,
            branch.CandidateIdentities.Count,
            branch.UnknownCount,
            branch.ScheduleCount,
            branch.Disposition,
            branch.CandidateIdentities.Order(StringComparer.Ordinal).ToArray(),
            new Dictionary<string, int>(branch.EvidenceStationHits, StringComparer.OrdinalIgnoreCase),
            branch.LastReason))
        .ToArray();

    private string[] BuildProposedScope(
        IReadOnlyList<string> current,
        string outsideStation,
        IReadOnlyList<ContinuousProofBusinessMapping> mappings)
    {
        if (current.Count == 1)
            return current.Concat([outsideStation]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        if (_rootStations.Length > 0)
        {
            string[] sibling = _rootStations.Concat([outsideStation])
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (sibling.Length <= _maximumStations &&
                Branch(sibling).ScheduleCount == 0)
                return sibling;
        }

        if (current.Count < _maximumStations)
            return current.Concat([outsideStation]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        string anchor = current
            .OrderByDescending(station => mappings.Count(mapping => mapping.Decisions.Any(decision =>
                string.Equals(decision.StationName, station, StringComparison.OrdinalIgnoreCase))))
            .ThenBy(station => station, StringComparer.OrdinalIgnoreCase)
            .First();
        return [anchor, outsideStation];
    }

    private MutableBranch Branch(IReadOnlyList<string> stations)
    {
        string key = ScopeKey(stations);
        if (_branches.TryGetValue(key, out MutableBranch? branch)) return branch;
        branch = new MutableBranch(key, key.Split('|', StringSplitOptions.RemoveEmptyEntries));
        _branches.Add(key, branch);
        return branch;
    }

    private sealed class MutableBranch(string scopeKey, IReadOnlyList<string> activeStations)
    {
        internal string ScopeKey { get; } = scopeKey;
        internal IReadOnlyList<string> ActiveStations { get; } = activeStations;
        internal double CumulativeSeconds { get; set; }
        internal HashSet<string> CandidateIdentities { get; } = new(StringComparer.Ordinal);
        internal int UnknownCount { get; set; }
        internal int ScheduleCount { get; set; }
        internal ExtendabilityScopeBranchDisposition Disposition { get; set; } = ExtendabilityScopeBranchDisposition.Active;
        internal List<ContinuousProofBusinessMapping> Mappings { get; } = [];
        internal Dictionary<string, int> EvidenceStationHits { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal string LastReason { get; set; } = "registered";
    }
}
