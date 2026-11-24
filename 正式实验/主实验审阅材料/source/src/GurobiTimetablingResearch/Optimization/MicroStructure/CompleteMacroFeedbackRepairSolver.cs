using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Gurobi;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

internal enum MacroRepairFeedbackClass
{
    ProvenConstraint,
    TemporaryRepairProposal
}

public sealed record MacroTimeEvent(string StopId, StationEventSide Side)
{
    internal double Resolve(MasterSolutionSnapshot snapshot) => Side == StationEventSide.Incoming
        ? snapshot.ArrivalSecondsByStopId[StopId]
        : snapshot.DepartureSecondsByStopId[StopId];
}

/// <summary>
/// A necessary Micro separation projected into Macro under its exact Macro-visible premises.
/// It is deliberately not a route-context no-good: the same stops and orders remain legal when
/// Macro can retime the two events enough to satisfy the certified relation.
/// </summary>
public sealed record ConditionalMacroTimeCut(
    string CutId,
    MacroTimeEvent LaterEvent,
    MacroTimeEvent EarlierEvent,
    double MinimumGapSeconds,
    IReadOnlyDictionary<string, int> RequiredStopValuesByStopId,
    IReadOnlyDictionary<string, int> RequiredSectionPrecedenceValuesByKey,
    IReadOnlyDictionary<string, int> RequiredStationEventPrecedenceValuesByKey,
    IReadOnlyList<string> SourceBusinessRuleIds,
    string ValidityBasis)
{
    internal bool IsActive(MasterSolutionSnapshot snapshot) =>
        RequiredStopValuesByStopId.All(item => snapshot.StopByStopId[item.Key] == item.Value) &&
        RequiredSectionPrecedenceValuesByKey.All(item =>
            snapshot.SectionPrecedenceByKey[item.Key] == item.Value) &&
        RequiredStationEventPrecedenceValuesByKey.All(item =>
            snapshot.StationEventPrecedenceByKey[item.Key] == item.Value);

    internal bool IsSatisfied(MasterSolutionSnapshot snapshot, double tolerance = 1e-6) =>
        !IsActive(snapshot) ||
        LaterEvent.Resolve(snapshot) - EarlierEvent.Resolve(snapshot) >= MinimumGapSeconds - tolerance;
}

public sealed record MacroAffineTimeEvent(MacroTimeEvent Event, double OffsetSeconds)
{
    internal double Resolve(MasterSolutionSnapshot snapshot) => Event.Resolve(snapshot) + OffsetSeconds;
}

/// <summary>
/// A proven same-track stopping-occupation rule.  The order variable introduced by the Macro
/// builder belongs only to this resource disjunction; it is not a section precedence and does not
/// consume the endpoint-order K budget.
/// </summary>
public sealed record ConditionalMacroTrackOccupationCut(
    string CutId,
    string StationName,
    string TrackName,
    MacroAffineTimeEvent FirstStart,
    MacroAffineTimeEvent FirstEnd,
    MacroAffineTimeEvent SecondStart,
    MacroAffineTimeEvent SecondEnd,
    double ToleranceSeconds,
    IReadOnlyDictionary<string, int> RequiredStopValuesByStopId,
    IReadOnlyList<string> SourceBusinessRuleIds,
    string ValidityBasis)
{
    internal bool IsActive(MasterSolutionSnapshot snapshot) =>
        RequiredStopValuesByStopId.All(item => snapshot.StopByStopId[item.Key] == item.Value);

    internal bool IsSatisfied(MasterSolutionSnapshot snapshot, double tolerance = 1e-6) =>
        !IsActive(snapshot) ||
        SecondStart.Resolve(snapshot) - FirstEnd.Resolve(snapshot) >= -ToleranceSeconds - tolerance ||
        FirstStart.Resolve(snapshot) - SecondEnd.Resolve(snapshot) >= -ToleranceSeconds - tolerance;
}

/// <summary>
/// A Macro feedback item is always bound to the exact candidate and source solve that produced it.
/// Proven constraints may be retained.  A fixed-pattern IIS is only a retractable search proposal.
/// </summary>
internal sealed record MacroRepairFeedback(
    string FeedbackId,
    MacroRepairFeedbackClass Classification,
    string SourceCandidateFingerprint,
    string SourceModelIdentity,
    string SourceSolveResultIdentity,
    string SourceKind,
    IReadOnlyList<string> AffectedStations,
    IReadOnlyDictionary<string, int> StopValuesByStopId,
    IReadOnlyDictionary<string, int> SectionPrecedenceValuesByKey,
    IReadOnlyDictionary<string, int> StationEventPrecedenceValuesByKey,
    IReadOnlyList<ContinuousProofRow> SourceProofRows,
    string ValidityBasis)
{
    public IReadOnlyList<ConditionalMacroTimeCut> ConditionalTimeCuts { get; init; } = [];
    public IReadOnlyList<ConditionalMacroTrackOccupationCut> ConditionalTrackOccupationCuts { get; init; } = [];
    internal bool IsPermanent => Classification == MacroRepairFeedbackClass.ProvenConstraint;

    internal LocalPrecedenceNogoodCut? ToNogoodCut() =>
        IsPermanent && (ConditionalTimeCuts.Count > 0 || ConditionalTrackOccupationCuts.Count > 0)
            ? null
            : new LocalPrecedenceNogoodCut(
                FeedbackId,
                AffectedStations.FirstOrDefault() ?? "full_line",
                -1,
                -1,
                SectionPrecedenceValuesByKey,
                StopValuesByStopId,
                IsPermanent ? "macro_feedback_proven_constraint" : "macro_feedback_temporary_proposal",
                ValidityBasis,
                StationEventPrecedenceValuesByKey);
}

internal sealed record MacroFeedbackLayers(
    IReadOnlyList<MacroRepairFeedback> Permanent,
    IReadOnlyList<MacroRepairFeedback> RunLocal)
{
    internal const int DefaultMaximumRunLocalCandidateContexts = 8;

    internal IReadOnlyList<MacroRepairFeedback> Active => Permanent.Concat(RunLocal).ToArray();

    internal static MacroFeedbackLayers Create(
        IEnumerable<MacroRepairFeedback> feedback,
        int maximumRunLocalCandidateContexts = DefaultMaximumRunLocalCandidateContexts)
    {
        ArgumentNullException.ThrowIfNull(feedback);
        if (maximumRunLocalCandidateContexts < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumRunLocalCandidateContexts));
        MacroRepairFeedback[] distinct = feedback
            .GroupBy(item => item.FeedbackId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        MacroRepairFeedback[] permanent = distinct.Where(item => item.IsPermanent).ToArray();
        MacroRepairFeedback[] temporary = distinct.Where(item => !item.IsPermanent).ToArray();
        string[] retainedContexts = temporary
            .Select(item => item.SourceCandidateFingerprint)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .TakeLast(maximumRunLocalCandidateContexts)
            .ToArray();
        HashSet<string> retained = new(retainedContexts, StringComparer.OrdinalIgnoreCase);
        MacroRepairFeedback[] runLocal = temporary
            .Where(item => retained.Contains(item.SourceCandidateFingerprint))
            .ToArray();
        return new(permanent, runLocal);
    }
}

internal static class MacroRepairFeedbackFactory
{
    internal static MacroRepairFeedback? FromFixedPatternProof(
        TimetablingInstance instance,
        MasterSolutionSnapshot reference,
        string sourceCandidateFingerprint,
        string sourceModelIdentity,
        string sourceSolveResultIdentity,
        MicroSolveStatus status,
        IReadOnlyList<string> activeStations,
        IReadOnlyList<ContinuousProofRow> proofRows,
        IReadOnlyList<MicroWitnessCertificate> fixedPatterns)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(activeStations);
        ArgumentNullException.ThrowIfNull(proofRows);
        ArgumentNullException.ThrowIfNull(fixedPatterns);
        if (status != MicroSolveStatus.ProvenInfeasible || proofRows.Count == 0)
            return null;

        // Macro replotting is allowed to inspect every legally adjustable Macro decision named by
        // the proof.  The fixed-pattern premise is deliberately retained, so this never becomes a
        // permanent route-independent exclusion.
        string[] allStations = instance.Stations.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        IReadOnlyList<ContinuousProofBusinessMapping> mappings =
            ContinuousProofDependencyMapper.Map(instance, allStations, proofRows, fixedPatterns);
        Dictionary<string, int> stops = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> sections = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> stationEvents = new(StringComparer.OrdinalIgnoreCase);
        foreach (ProofMappedDecision decision in mappings.SelectMany(mapping => mapping.Decisions))
        {
            if (!decision.IsAdjustable)
                continue;
            if (string.Equals(decision.DecisionFamily, "stop", StringComparison.OrdinalIgnoreCase) &&
                reference.StopByStopId.TryGetValue(decision.DecisionKey, out int stopValue))
                stops[decision.DecisionKey] = stopValue;
            else if (string.Equals(decision.DecisionFamily, "section_precedence", StringComparison.OrdinalIgnoreCase) &&
                     reference.SectionPrecedenceByKey.TryGetValue(decision.DecisionKey, out int sectionValue))
                sections[decision.DecisionKey] = sectionValue;
        }

        // Some integrated constraints expose their condition only through a business id.  Preserve
        // that exact identity when it is a real Macro variable; do not infer values from names.
        foreach (ContinuousProofRow row in proofRows)
        {
            if (reference.StopByStopId.TryGetValue(row.BusinessId, out int stopValue))
                stops[row.BusinessId] = stopValue;
            if (reference.SectionPrecedenceByKey.TryGetValue(row.BusinessId, out int sectionValue))
                sections[row.BusinessId] = sectionValue;
            if (reference.StationEventPrecedenceByKey.TryGetValue(row.BusinessId, out int eventValue))
                stationEvents[row.BusinessId] = eventValue;
        }

        if (stops.Count + sections.Count + stationEvents.Count == 0)
            return null;
        string identity = CalculateIdentity(
            "fixed_pattern_iis_temporary",
            sourceCandidateFingerprint,
            proofRows.Select(row => $"{row.ConstraintName}:{row.BusinessKind}:{row.BusinessId}"));
        return CreateTemporaryProposal(
            identity,
            sourceCandidateFingerprint,
            sourceModelIdentity,
            sourceSolveResultIdentity,
            activeStations,
            stops,
            sections,
            stationEvents,
            "A locally fixed joint Micro model was proven infeasible. At least one mapped Macro premise is changed for this attempt; the proposal is withdrawn when the context changes.",
            proofRows);
    }

    internal static MacroRepairFeedback? FromStationFeedback(
        string sourceCandidateFingerprint,
        string sourceModelIdentity,
        string sourceSolveResultIdentity,
        MicroSolveStatus status,
        MicroFeedback feedback,
        bool continuousDomainExhausted = false)
    {
        ArgumentNullException.ThrowIfNull(feedback);
        if (status != MicroSolveStatus.ProvenInfeasible)
            return null;

        IReadOnlyDictionary<string, int> stationEvents =
            feedback.StationEventPrecedenceIncumbentValuesByKey ??
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        bool routeAndTimeDomainExhausted = continuousDomainExhausted &&
            feedback.Kind == MicroFeedbackKind.ProjectedStationCore &&
            feedback.Projection?.HasCut == true;
        return new MacroRepairFeedback(
            feedback.FeedbackId,
            routeAndTimeDomainExhausted
                ? MacroRepairFeedbackClass.ProvenConstraint
                : MacroRepairFeedbackClass.TemporaryRepairProposal,
            sourceCandidateFingerprint,
            sourceModelIdentity,
            sourceSolveResultIdentity,
            routeAndTimeDomainExhausted
                ? "complete_station_route_track_and_continuous_domain_infeasibility"
                : "fixed_timetable_or_route_specific_station_failure",
            [feedback.StationName],
            new Dictionary<string, int>(feedback.StopIncumbentValuesByStopId,
                StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, int>(feedback.PrecedenceIncumbentValuesByKey,
                StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, int>(stationEvents, StringComparer.OrdinalIgnoreCase),
            [],
            routeAndTimeDomainExhausted
                ? feedback.Rationale
                : $"Temporary search proposal only; the complete legal continuous-time domain was not eliminated. {feedback.Rationale}");
    }

    internal static MacroRepairFeedback CreateTemporaryProposal(
        string feedbackId,
        string sourceCandidateFingerprint,
        string sourceModelIdentity,
        string sourceSolveResultIdentity,
        IReadOnlyList<string> affectedStations,
        IReadOnlyDictionary<string, int> stopValues,
        IReadOnlyDictionary<string, int> sectionValues,
        IReadOnlyDictionary<string, int> stationEventValues,
        string validityBasis,
        IReadOnlyList<ContinuousProofRow>? proofRows = null) =>
        new(
            feedbackId,
            MacroRepairFeedbackClass.TemporaryRepairProposal,
            sourceCandidateFingerprint,
            sourceModelIdentity,
            sourceSolveResultIdentity,
            "fixed_pattern_or_bounded_joint_repair_failure",
            affectedStations.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            new Dictionary<string, int>(stopValues, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, int>(sectionValues, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, int>(stationEventValues, StringComparer.OrdinalIgnoreCase),
            proofRows?.ToArray() ?? [],
            validityBasis);

    internal static MacroRepairFeedback CreateProvenConditionalTimeConstraint(
        string feedbackId,
        string sourceCandidateFingerprint,
        string sourceModelIdentity,
        string sourceSolveResultIdentity,
        IReadOnlyList<string> affectedStations,
        IReadOnlyList<ConditionalMacroTimeCut> timeCuts,
        string validityBasis,
        IReadOnlyList<ContinuousProofRow>? proofRows = null)
    {
        ArgumentNullException.ThrowIfNull(timeCuts);
        if (timeCuts.Count == 0)
            throw new ArgumentException("At least one conditional time cut is required.", nameof(timeCuts));
        Dictionary<string, int> stops = MergePremises(
            timeCuts.Select(cut => cut.RequiredStopValuesByStopId), "stop");
        Dictionary<string, int> sections = MergePremises(
            timeCuts.Select(cut => cut.RequiredSectionPrecedenceValuesByKey), "section precedence");
        Dictionary<string, int> stationEvents = MergePremises(
            timeCuts.Select(cut => cut.RequiredStationEventPrecedenceValuesByKey), "station-event precedence");
        return new MacroRepairFeedback(
            feedbackId,
            MacroRepairFeedbackClass.ProvenConstraint,
            sourceCandidateFingerprint,
            sourceModelIdentity,
            sourceSolveResultIdentity,
            "proven_conditional_micro_time_relation",
            affectedStations.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            stops,
            sections,
            stationEvents,
            proofRows?.ToArray() ?? [],
            validityBasis)
        {
            ConditionalTimeCuts = timeCuts.ToArray()
        };
    }

    internal static MacroRepairFeedback CreateProvenConditionalTrackOccupationConstraint(
        string feedbackId,
        string sourceCandidateFingerprint,
        string sourceModelIdentity,
        string sourceSolveResultIdentity,
        IReadOnlyList<string> affectedStations,
        IReadOnlyList<ConditionalMacroTrackOccupationCut> occupationCuts,
        string validityBasis,
        IReadOnlyList<ContinuousProofRow>? proofRows = null)
    {
        ArgumentNullException.ThrowIfNull(occupationCuts);
        if (occupationCuts.Count == 0)
            throw new ArgumentException("At least one conditional track occupation cut is required.",
                nameof(occupationCuts));
        Dictionary<string, int> stops = MergePremises(
            occupationCuts.Select(cut => cut.RequiredStopValuesByStopId), "stop");
        return new MacroRepairFeedback(
            feedbackId,
            MacroRepairFeedbackClass.ProvenConstraint,
            sourceCandidateFingerprint,
            sourceModelIdentity,
            sourceSolveResultIdentity,
            "proven_conditional_unique_track_occupation",
            affectedStations.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            stops,
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            proofRows?.ToArray() ?? [],
            validityBasis)
        {
            ConditionalTrackOccupationCuts = occupationCuts.ToArray()
        };
    }

    private static Dictionary<string, int> MergePremises(
        IEnumerable<IReadOnlyDictionary<string, int>> sources,
        string family)
    {
        Dictionary<string, int> merged = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, int value) in sources.SelectMany(source => source))
        {
            if (value is not (0 or 1))
                throw new InvalidDataException($"Conditional {family} premise {key} is not binary.");
            if (merged.TryGetValue(key, out int prior) && prior != value)
                throw new InvalidDataException($"Conflicting conditional {family} premise for {key}.");
            merged[key] = value;
        }
        return merged;
    }

    private static string CalculateIdentity(string kind, string candidate, IEnumerable<string> details)
    {
        string payload = string.Join('\n', new[] { kind, candidate }
            .Concat(details.Order(StringComparer.Ordinal)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }
}

internal sealed record CompleteMacroFeedbackRepairResult(
    bool HasCandidate,
    int StatusCode,
    string Status,
    double RuntimeSeconds,
    double? FirstIncumbentSeconds,
    int Variables,
    int Binaries,
    int Constraints,
    bool PresolveSizeAvailable,
    int PresolvedVariables,
    int PresolvedBinaries,
    int PresolvedConstraints,
    int RetainedHardPatternCount,
    int AppliedPermanentFeedbackCount,
    int AppliedTemporaryFeedbackCount,
    string AppliedAttemptFeedbackId,
    string ParentCandidateFingerprint,
    string CandidateFingerprint,
    bool FeedbackConditionChanged,
    int StopChanges,
    int SectionPrecedenceChanges,
    int StationEventPrecedenceChanges,
    int ChangedArrivalCount,
    int ChangedDepartureCount,
    IReadOnlyList<string> ChangedDecisionIds,
    bool MacroValidated,
    int MacroViolationCount,
    MasterSolutionSnapshot? CandidateSnapshot,
    string StopReason);

internal sealed record MacroDiscreteReleaseScope(
    IReadOnlySet<string> StopIds,
    IReadOnlySet<string> SectionPrecedenceKeys,
    IReadOnlySet<string> StationEventPrecedenceKeys,
    IReadOnlySet<string> AffectedTimeStopIds,
    IReadOnlyList<string> SourceCutIds)
{
    internal bool HasReleasedMainDecision =>
        StopIds.Count + SectionPrecedenceKeys.Count + StationEventPrecedenceKeys.Count > 0;

    internal static MacroDiscreteReleaseScope Empty { get; } = new(
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        []);
}

internal enum MacroFeedbackRepairObjectiveMode
{
    FeasibilityFirst,
    MinimumDiscreteChange
}

/// <summary>
/// Rebuilds the complete Macro model independently of the failed Micro model.  Historical Micro
/// patterns are not hard constraints.  K=4 is retained for endpoint order changes and the complete
/// full-line continuous timetable is free inside the original business domain.
/// </summary>
internal static class CompleteMacroFeedbackRepairSolver
{
    internal static CompleteMacroFeedbackRepairResult Solve(
        TimetablingInstance instance,
        MasterSolutionSnapshot reference,
        MacroRepairFeedback attemptFeedback,
        IReadOnlyList<MacroRepairFeedback> retainedSearchFeedback,
        double timeLimitSeconds,
        string? logFile,
        double runningTimeSupplementRatio = 0.05,
        int endpointOccupationSeconds = StationEndpointOccupationPolicy.DefaultSeconds,
        int endpointOrderChangeBudget = 4) =>
        SolveCore(
            instance,
            reference,
            attemptFeedback,
            retainedSearchFeedback,
            timeLimitSeconds,
            logFile,
            runningTimeSupplementRatio,
            endpointOccupationSeconds,
            endpointOrderChangeBudget,
            fixMacroDiscreteDecisions: false,
            releaseScope: null);

    internal static CompleteMacroFeedbackRepairResult SolveFixedDiscreteTime(
        TimetablingInstance instance,
        MasterSolutionSnapshot reference,
        MacroRepairFeedback attemptFeedback,
        IReadOnlyList<MacroRepairFeedback> retainedSearchFeedback,
        double timeLimitSeconds,
        string? logFile,
        double runningTimeSupplementRatio = 0.05,
        int endpointOccupationSeconds = StationEndpointOccupationPolicy.DefaultSeconds) =>
        SolveCore(
            instance,
            reference,
            attemptFeedback,
            retainedSearchFeedback,
            timeLimitSeconds,
            logFile,
            runningTimeSupplementRatio,
            endpointOccupationSeconds,
            endpointOrderChangeBudget: 4,
            fixMacroDiscreteDecisions: true,
            releaseScope: MacroDiscreteReleaseScope.Empty);

    internal static CompleteMacroFeedbackRepairResult SolveEvidencePremiseRepair(
        TimetablingInstance instance,
        MasterSolutionSnapshot reference,
        MacroRepairFeedback attemptFeedback,
        IReadOnlyList<MacroRepairFeedback> retainedSearchFeedback,
        double timeLimitSeconds,
        string? logFile,
        double runningTimeSupplementRatio = 0.05,
        int endpointOccupationSeconds = StationEndpointOccupationPolicy.DefaultSeconds,
        int endpointOrderChangeBudget = 4)
    {
        MacroDiscreteReleaseScope scope = BuildEvidencePremiseReleaseScope(
            reference, attemptFeedback, retainedSearchFeedback);
        if (!scope.HasReleasedMainDecision)
            throw new InvalidOperationException(
                "Evidence-scoped Macro repair requires at least one violated conditional premise.");
        return SolveCore(
            instance,
            reference,
            attemptFeedback,
            retainedSearchFeedback,
            timeLimitSeconds,
            logFile,
            runningTimeSupplementRatio,
            endpointOccupationSeconds,
            endpointOrderChangeBudget,
            fixMacroDiscreteDecisions: true,
            releaseScope: scope);
    }

    internal static CompleteMacroFeedbackRepairResult SolveProofLinkedCoreRepair(
        TimetablingInstance instance,
        MasterSolutionSnapshot reference,
        MacroRepairFeedback attemptFeedback,
        IReadOnlyList<MacroRepairFeedback> retainedSearchFeedback,
        ProofLinkedConflictCore core,
        double timeLimitSeconds,
        string? logFile,
        double runningTimeSupplementRatio = 0.05,
        int endpointOccupationSeconds = StationEndpointOccupationPolicy.DefaultSeconds,
        int endpointOrderChangeBudget = 4)
    {
        ArgumentNullException.ThrowIfNull(core);
        if (!core.IsComplete)
            throw new InvalidOperationException($"Proof-linked Macro repair requires a complete core: {core.Status}.");
        MacroDiscreteReleaseScope scope = BuildProofLinkedReleaseScope(core);
        if (!scope.HasReleasedMainDecision)
            throw new InvalidOperationException("Proof-linked Macro repair has no adjustable main decision.");
        return SolveCore(
            instance,
            reference,
            attemptFeedback,
            retainedSearchFeedback,
            timeLimitSeconds,
            logFile,
            runningTimeSupplementRatio,
            endpointOccupationSeconds,
            endpointOrderChangeBudget,
            fixMacroDiscreteDecisions: true,
            releaseScope: scope);
    }

    internal static MacroDiscreteReleaseScope BuildProofLinkedReleaseScope(ProofLinkedConflictCore core)
    {
        ArgumentNullException.ThrowIfNull(core);
        return new MacroDiscreteReleaseScope(
            core.StopIds.ToHashSet(StringComparer.OrdinalIgnoreCase),
            core.SectionPrecedenceKeys.ToHashSet(StringComparer.OrdinalIgnoreCase),
            core.StationEventPrecedenceKeys.ToHashSet(StringComparer.OrdinalIgnoreCase),
            core.AffectedTimeStopIds.ToHashSet(StringComparer.OrdinalIgnoreCase),
            [core.ProofIdentity, core.Status]);
    }

    internal static CompleteMacroFeedbackRepairResult SolveEvidenceDependencyRepair(
        TimetablingInstance instance,
        MasterSolutionSnapshot reference,
        MacroRepairFeedback attemptFeedback,
        IReadOnlyList<MacroRepairFeedback> retainedSearchFeedback,
        double timeLimitSeconds,
        string? logFile,
        double runningTimeSupplementRatio = 0.05,
        int endpointOccupationSeconds = StationEndpointOccupationPolicy.DefaultSeconds,
        int endpointOrderChangeBudget = 4)
    {
        MacroDiscreteReleaseScope scope = BuildEvidenceDependencyReleaseScope(
            instance, reference, attemptFeedback, retainedSearchFeedback);
        if (!scope.HasReleasedMainDecision)
            throw new InvalidOperationException(
                "Evidence dependency repair requires at least one implicated Macro decision.");
        return SolveCore(
            instance,
            reference,
            attemptFeedback,
            retainedSearchFeedback,
            timeLimitSeconds,
            logFile,
            runningTimeSupplementRatio,
            endpointOccupationSeconds,
            endpointOrderChangeBudget,
            fixMacroDiscreteDecisions: true,
            releaseScope: scope);
    }

    internal static CompleteMacroFeedbackRepairResult SolveEvidenceCorridorRepair(
        TimetablingInstance instance,
        MasterSolutionSnapshot reference,
        MacroRepairFeedback attemptFeedback,
        IReadOnlyList<MacroRepairFeedback> retainedSearchFeedback,
        int stationPadding,
        double timeLimitSeconds,
        string? logFile,
        double runningTimeSupplementRatio = 0.05,
        int endpointOccupationSeconds = StationEndpointOccupationPolicy.DefaultSeconds,
        int endpointOrderChangeBudget = 4)
    {
        MacroDiscreteReleaseScope scope = BuildEvidenceCorridorReleaseScope(
            instance, reference, attemptFeedback, retainedSearchFeedback, stationPadding);
        if (!scope.HasReleasedMainDecision)
            throw new InvalidOperationException(
                "Evidence corridor repair requires at least one implicated Macro decision.");
        return SolveCore(
            instance,
            reference,
            attemptFeedback,
            retainedSearchFeedback,
            timeLimitSeconds,
            logFile,
            runningTimeSupplementRatio,
            endpointOccupationSeconds,
            endpointOrderChangeBudget,
            fixMacroDiscreteDecisions: true,
            releaseScope: scope);
    }

    internal static MacroDiscreteReleaseScope BuildEvidencePremiseReleaseScope(
        MasterSolutionSnapshot reference,
        MacroRepairFeedback attemptFeedback,
        IReadOnlyList<MacroRepairFeedback> retainedSearchFeedback)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(attemptFeedback);
        ArgumentNullException.ThrowIfNull(retainedSearchFeedback);
        ConditionalMacroTimeCut[] violatedTimeCuts = retainedSearchFeedback
            .Where(item => item.IsPermanent)
            .Append(attemptFeedback)
            .SelectMany(item => item.ConditionalTimeCuts)
            .GroupBy(cut => cut.CutId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Where(cut => IsActiveWithFeedbackFallback(cut, reference) &&
                cut.LaterEvent.Resolve(reference) - cut.EarlierEvent.Resolve(reference) <
                cut.MinimumGapSeconds - 1e-6)
            .ToArray();
        ConditionalMacroTrackOccupationCut[] violatedTrackCuts = retainedSearchFeedback
            .Where(item => item.IsPermanent)
            .Append(attemptFeedback)
            .SelectMany(item => item.ConditionalTrackOccupationCuts)
            .GroupBy(cut => cut.CutId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Where(cut => cut.IsActive(reference) && !cut.IsSatisfied(reference))
            .ToArray();
        HashSet<string> stops = violatedTimeCuts
            .SelectMany(cut => cut.RequiredStopValuesByStopId.Keys)
            .Concat(violatedTrackCuts.SelectMany(cut => cut.RequiredStopValuesByStopId.Keys))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> sections = violatedTimeCuts
            .SelectMany(cut => cut.RequiredSectionPrecedenceValuesByKey.Keys)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> stationEvents = violatedTimeCuts
            .SelectMany(cut => cut.RequiredStationEventPrecedenceValuesByKey.Keys)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> affectedTimeStops = violatedTimeCuts
            .SelectMany(cut => new[] { cut.LaterEvent.StopId, cut.EarlierEvent.StopId })
            .Concat(violatedTrackCuts.SelectMany(cut => new[]
            {
                cut.FirstStart.Event.StopId,
                cut.FirstEnd.Event.StopId,
                cut.SecondStart.Event.StopId,
                cut.SecondEnd.Event.StopId
            }))
            .Concat(stops)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] sourceCutIds = violatedTimeCuts.Select(cut => cut.CutId)
            .Concat(violatedTrackCuts.Select(cut => cut.CutId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new MacroDiscreteReleaseScope(
            stops, sections, stationEvents, affectedTimeStops, sourceCutIds);
    }

    internal static MacroDiscreteReleaseScope BuildEvidenceDependencyReleaseScope(
        TimetablingInstance instance,
        MasterSolutionSnapshot reference,
        MacroRepairFeedback attemptFeedback,
        IReadOnlyList<MacroRepairFeedback> retainedSearchFeedback)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(attemptFeedback);
        ArgumentNullException.ThrowIfNull(retainedSearchFeedback);
        MacroDiscreteReleaseScope premise = BuildEvidencePremiseReleaseScope(
            reference, attemptFeedback, retainedSearchFeedback);
        HashSet<int> evidenceTrains = ResolveEvidenceTrains(instance, attemptFeedback);
        foreach (string stopId in premise.AffectedTimeStopIds)
            if (instance.StopsById.TryGetValue(stopId, out TrainStopRecord? stop))
                evidenceTrains.Add(stop.TrainIndex);

        HashSet<string> stops = premise.StopIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> affectedTimeStops = premise.AffectedTimeStopIds.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        foreach (TrainStopRecord stop in instance.TrainStops.Where(stop =>
                     evidenceTrains.Contains(stop.TrainIndex)))
        {
            stops.Add(stop.StopId);
            affectedTimeStops.Add(stop.StopId);
        }

        HashSet<string> sections = premise.SectionPrecedenceKeys.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        foreach (string key in reference.SectionPrecedenceByKey.Keys.Where(key =>
                     KeyMentionsAnyTrain(key, evidenceTrains)))
            sections.Add(key);
        HashSet<string> stationEvents = premise.StationEventPrecedenceKeys.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        foreach (string key in reference.StationEventPrecedenceByKey.Keys.Where(key =>
                     KeyMentionsAnyTrain(key, evidenceTrains)))
            stationEvents.Add(key);

        string[] sourceCutIds = premise.SourceCutIds
            .Append("evidence_train_dependency_closure")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new MacroDiscreteReleaseScope(
            stops, sections, stationEvents, affectedTimeStops, sourceCutIds);
    }

    internal static MacroDiscreteReleaseScope BuildEvidenceCorridorReleaseScope(
        TimetablingInstance instance,
        MasterSolutionSnapshot reference,
        MacroRepairFeedback attemptFeedback,
        IReadOnlyList<MacroRepairFeedback> retainedSearchFeedback,
        int stationPadding)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(attemptFeedback);
        ArgumentNullException.ThrowIfNull(retainedSearchFeedback);
        if (stationPadding < 0)
            throw new ArgumentOutOfRangeException(nameof(stationPadding));

        MacroDiscreteReleaseScope premise = BuildEvidencePremiseReleaseScope(
            reference, attemptFeedback, retainedSearchFeedback);
        HashSet<int> evidenceTrains = ResolveEvidenceTrains(instance, attemptFeedback);
        foreach (string stopId in premise.AffectedTimeStopIds)
            if (instance.StopsById.TryGetValue(stopId, out TrainStopRecord? stop))
                evidenceTrains.Add(stop.TrainIndex);

        HashSet<string> evidenceStations = attemptFeedback.AffectedStations
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string stopId in premise.AffectedTimeStopIds)
            if (instance.StopsById.TryGetValue(stopId, out TrainStopRecord? stop))
                evidenceStations.Add(stop.StationName);

        Dictionary<int, (int Minimum, int Maximum)> spans = new();
        foreach (int trainIndex in evidenceTrains)
        {
            TrainRecord? train = instance.Trains.FirstOrDefault(item => item.TrainIndex == trainIndex);
            if (train is null || train.OrderedStops.Count == 0)
                continue;
            int[] seedOrders = train.OrderedStops
                .Where(stop => evidenceStations.Contains(stop.StationName) ||
                               premise.AffectedTimeStopIds.Contains(stop.StopId))
                .Select(stop => stop.StationOrder)
                .ToArray();
            if (seedOrders.Length == 0)
                continue;
            int pathMinimum = train.OrderedStops.Min(stop => stop.StationOrder);
            int pathMaximum = train.OrderedStops.Max(stop => stop.StationOrder);
            spans[trainIndex] = (
                Math.Max(pathMinimum, seedOrders.Min() - stationPadding),
                Math.Min(pathMaximum, seedOrders.Max() + stationPadding));
        }

        HashSet<string> stops = premise.StopIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> affectedTimeStops = premise.AffectedTimeStopIds.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        HashSet<string> activeStations = new(StringComparer.OrdinalIgnoreCase);
        foreach (TrainStopRecord stop in instance.TrainStops)
        {
            if (!spans.TryGetValue(stop.TrainIndex, out (int Minimum, int Maximum) span) ||
                stop.StationOrder < span.Minimum || stop.StationOrder > span.Maximum)
                continue;
            stops.Add(stop.StopId);
            affectedTimeStops.Add(stop.StopId);
            activeStations.Add(stop.StationName);
        }

        HashSet<string> sections = premise.SectionPrecedenceKeys.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        foreach (SectionConflictCandidate candidate in
                 new InitialConflictGraphBuilder(instance, int.MaxValue).Build())
        {
            bool firstIntersects = spans.TryGetValue(
                    candidate.FirstTrainIndex, out (int Minimum, int Maximum) firstSpan) &&
                SegmentIntersects(firstSpan, candidate.FirstStopOrder, candidate.FirstEndStopOrder);
            bool secondIntersects = spans.TryGetValue(
                    candidate.SecondTrainIndex, out (int Minimum, int Maximum) secondSpan) &&
                SegmentIntersects(secondSpan, candidate.SecondStopOrder, candidate.SecondEndStopOrder);
            if (!firstIntersects && !secondIntersects)
                continue;
            string key = BasicMilpModelBuilder.BuildSectionPrecedenceKey(candidate);
            if (reference.SectionPrecedenceByKey.ContainsKey(key))
                sections.Add(key);
        }

        HashSet<string> stationEvents = premise.StationEventPrecedenceKeys.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        foreach (string key in reference.StationEventPrecedenceByKey.Keys)
        {
            int separator = key.IndexOf("::", StringComparison.Ordinal);
            string station = separator < 0 ? key : key[..separator];
            if (activeStations.Contains(station) && KeyMentionsAnyTrain(key, evidenceTrains))
                stationEvents.Add(key);
        }

        string[] sourceCutIds = premise.SourceCutIds
            .Append($"evidence_corridor_padding_{stationPadding}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new MacroDiscreteReleaseScope(
            stops, sections, stationEvents, affectedTimeStops, sourceCutIds);
    }

    private static bool SegmentIntersects(
        (int Minimum, int Maximum) span,
        int firstOrder,
        int secondOrder)
    {
        int minimum = Math.Min(firstOrder, secondOrder);
        int maximum = Math.Max(firstOrder, secondOrder);
        return maximum >= span.Minimum && minimum <= span.Maximum;
    }

    private static bool KeyMentionsAnyTrain(string key, IReadOnlySet<int> trainIndices)
    {
        foreach (string part in key.Split("::", StringSplitOptions.None))
            if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out int trainIndex) && trainIndices.Contains(trainIndex))
                return true;
        return false;
    }

    private static bool IsActiveWithFeedbackFallback(
        ConditionalMacroTimeCut cut,
        MasterSolutionSnapshot reference)
    {
        static bool Matches(
            IReadOnlyDictionary<string, int> required,
            IReadOnlyDictionary<string, int> actual) =>
            required.All(item => !actual.TryGetValue(item.Key, out int value) || value == item.Value);

        // A station-event edge introduced by a feedback cut need not exist in the source
        // snapshot.  FixMacroDiscreteDecisions already uses the cut premise as the fallback value
        // for such an injected variable.  Scope analysis must use the same semantics; absence is
        // not evidence that the premise is false.
        return Matches(cut.RequiredStopValuesByStopId, reference.StopByStopId) &&
            Matches(cut.RequiredSectionPrecedenceValuesByKey, reference.SectionPrecedenceByKey) &&
            Matches(cut.RequiredStationEventPrecedenceValuesByKey,
                reference.StationEventPrecedenceByKey);
    }

    private static CompleteMacroFeedbackRepairResult SolveCore(
        TimetablingInstance instance,
        MasterSolutionSnapshot reference,
        MacroRepairFeedback attemptFeedback,
        IReadOnlyList<MacroRepairFeedback> retainedSearchFeedback,
        double timeLimitSeconds,
        string? logFile,
        double runningTimeSupplementRatio,
        int endpointOccupationSeconds,
        int endpointOrderChangeBudget,
        bool fixMacroDiscreteDecisions,
        MacroDiscreteReleaseScope? releaseScope)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(attemptFeedback);
        ArgumentNullException.ThrowIfNull(retainedSearchFeedback);
        if (timeLimitSeconds <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(timeLimitSeconds));
        string parentFingerprint = SnapshotFingerprint.Calculate(reference);
        if (!string.Equals(attemptFeedback.SourceCandidateFingerprint, parentFingerprint,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Attempt feedback belongs to a different Macro candidate.");
        MacroFeedbackLayers feedbackLayers = MacroFeedbackLayers.Create(retainedSearchFeedback);
        IReadOnlyList<MacroRepairFeedback> activeFeedback = feedbackLayers.Active;

        HashSet<int> evidenceTrains = ResolveEvidenceTrains(instance, attemptFeedback);
        if (releaseScope is not null)
            foreach (string stopId in releaseScope.AffectedTimeStopIds)
                if (instance.StopsById.TryGetValue(stopId, out TrainStopRecord? stop))
                    evidenceTrains.Add(stop.TrainIndex);
        PrecedenceTrustRegion trustRegion = ValidityFirstEndpointOrderTrustRegionBuilder.Build(
            instance,
            reference,
            evidenceTrains,
            endpointOrderChangeBudget,
            $"macro_feedback_{attemptFeedback.FeedbackId}");
        // Proven feedback may persist across runs.  Type-B feedback is retained only in this
        // in-memory search session as a retractable tabu; it is never promoted to a business cut.
        // Keeping it active prevents A -> B -> A candidate cycles while the shared budget remains.
        LocalPrecedenceNogoodCut[] cuts = activeFeedback
            .Select(feedback => feedback.ToNogoodCut())
            .Append(attemptFeedback.ToNogoodCut())
            .OfType<LocalPrecedenceNogoodCut>()
            .ToArray();
        ConditionalMacroTimeCut[] conditionalTimeCuts = feedbackLayers.Permanent
            .SelectMany(feedback => feedback.ConditionalTimeCuts)
            .Concat(attemptFeedback.ConditionalTimeCuts)
            .GroupBy(cut => cut.CutId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        ConditionalMacroTrackOccupationCut[] retainedTrackOccupationCuts = feedbackLayers.Permanent
            .SelectMany(feedback => feedback.ConditionalTrackOccupationCuts)
            .GroupBy(cut => cut.CutId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        HashSet<string> retainedTrackOccupationCutIds = retainedTrackOccupationCuts
            .Select(cut => cut.CutId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        ConditionalMacroTrackOccupationCut[] attemptTrackOccupationCuts = attemptFeedback
            .ConditionalTrackOccupationCuts
            .Where(cut => !retainedTrackOccupationCutIds.Contains(cut.CutId))
            .GroupBy(cut => cut.CutId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        ConditionalMacroTrackOccupationCut[] conditionalTrackOccupationCuts =
            retainedTrackOccupationCuts.Concat(attemptTrackOccupationCuts).ToArray();
        StationEventConflictCandidate[] feedbackStationEdges = ResolveConditionalStationEdges(
            instance, conditionalTimeCuts);
        using BasicMilpModel model = new BasicMilpModelBuilder(
            instance,
            neighborRadius: int.MaxValue,
            injectedNogoodCuts: cuts,
            precedenceTrustRegion: trustRegion,
            stationEventEdges: feedbackStationEdges,
            runningTimeSupplementRatio: runningTimeSupplementRatio,
            endpointOccupationSeconds: endpointOccupationSeconds).Build();
        foreach (MacroRepairFeedback retained in activeFeedback)
            ValidateExactFeedbackCoverage(model, retained);
        ValidateExactFeedbackCoverage(model, attemptFeedback);
        AddConditionalTimeCuts(model, conditionalTimeCuts);
        ConditionalMacroTrackOccupationCut[] realizedRetainedTrackOccupationCuts =
            fixMacroDiscreteDecisions
                ? retainedTrackOccupationCuts
                    .Where(cut =>
                        (!cut.IsActive(reference) || cut.IsSatisfied(reference)) &&
                        (releaseScope is null || cut.RequiredStopValuesByStopId.Keys.All(
                            stopId => !releaseScope.StopIds.Contains(stopId))))
                    .ToArray()
                : [];
        ConditionalMacroTrackOccupationCut[] unrealizedRetainedTrackOccupationCuts =
            fixMacroDiscreteDecisions
                ? retainedTrackOccupationCuts
                    .Where(cut => !realizedRetainedTrackOccupationCuts.Any(realized =>
                        string.Equals(realized.CutId, cut.CutId, StringComparison.OrdinalIgnoreCase)))
                    .ToArray()
                : retainedTrackOccupationCuts;
        AddConditionalTrackOccupationCuts(
            model,
            realizedRetainedTrackOccupationCuts,
            fixMacroDiscreteDecisions ? reference : null);
        // When all existing Macro discrete decisions are fixed, historical occupation cuts use
        // the order already realized by the reference.  The new feedback cuts are the reason for
        // this repair and must retain both legal orders; only their small order variables remain
        // free in the preliminary stage.
        AddConditionalTrackOccupationCuts(
            model,
            unrealizedRetainedTrackOccupationCuts.Concat(attemptTrackOccupationCuts));
        if (fixMacroDiscreteDecisions)
            FixMacroDiscreteDecisions(
                model,
                reference,
                activeFeedback
                    .Append(attemptFeedback)
                    .Where(feedback => feedback.IsPermanent)
                    .ToArray(),
                releaseScope);
        MasterSolutionSnapshotApplier.ApplyMipStart(
            model,
            reference,
            BuildEvidenceAwareMipStartScope(instance, attemptFeedback, releaseScope));
        MacroFeedbackRepairObjectiveMode objectiveMode = ResolveObjectiveMode(fixMacroDiscreteDecisions);
        model.Model.Set(GRB.IntAttr.NumObj, 1);
        model.Model.SetObjective(
            objectiveMode == MacroFeedbackRepairObjectiveMode.FeasibilityFirst
                ? new GRBLinExpr()
                : BuildMinimumDiscreteChangeObjective(model, reference),
            GRB.MINIMIZE);
        model.Model.Update();

        int variables = model.Model.Get(GRB.IntAttr.NumVars);
        int binaries = model.Model.Get(GRB.IntAttr.NumBinVars);
        int constraints = model.Model.Get(GRB.IntAttr.NumConstrs);
        (bool presolveAvailable, int presolvedVariables, int presolvedBinaries, int presolvedConstraints) =
            CapturePresolveSize(model.Model);
        if (!string.IsNullOrWhiteSpace(logFile))
        {
            string? directory = Path.GetDirectoryName(Path.GetFullPath(logFile));
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            model.Model.Set(GRB.StringParam.LogFile, logFile);
        }
        model.Model.Set(GRB.IntParam.Seed, 0);
        model.Model.Set(GRB.IntParam.Threads, 0);
        model.Model.Set(GRB.IntParam.MIPFocus, 1);
        model.Model.Set(GRB.IntParam.SolutionLimit, 1);
        model.Model.Set(GRB.DoubleParam.TimeLimit, timeLimitSeconds);
        FirstIncumbentTrackingCallback callback = new(stopAfterFirstIncumbent: true);
        model.Model.SetCallback(callback);
        Stopwatch clock = Stopwatch.StartNew();
        model.Optimize();
        clock.Stop();
        int status = model.Model.Get(GRB.IntAttr.Status);
        if (!model.HasIncumbentSolution)
            return Empty(status, StatusName(status), clock.Elapsed.TotalSeconds,
                callback.FirstIncumbentTimeSeconds, variables, binaries, constraints,
                presolveAvailable, presolvedVariables, presolvedBinaries, presolvedConstraints,
                feedbackLayers.Permanent.Count,
                feedbackLayers.RunLocal.Count,
                attemptFeedback.FeedbackId, parentFingerprint,
                status == GRB.Status.INFEASIBLE
                    ? "complete_macro_with_feedback_proven_infeasible"
                    : $"complete_macro_with_feedback_no_incumbent_status_{status}");

        MasterSolutionSnapshot candidate = MasterSolutionSnapshot.Capture(model) with
        {
            VerifiedMicroAssignments = null
        };
        string candidateFingerprint = SnapshotFingerprint.Calculate(candidate);
        List<string> changed = CollectChanges(reference, candidate);
        bool feedbackChanged = FeedbackConditionChanged(attemptFeedback, candidate);
        MasterValidationReport validation = new MasterSolutionValidator().Validate(model);
        bool accepted = validation.IsFeasible && feedbackChanged &&
            !string.Equals(parentFingerprint, candidateFingerprint, StringComparison.OrdinalIgnoreCase);
        return new CompleteMacroFeedbackRepairResult(
            accepted,
            status,
            StatusName(status),
            clock.Elapsed.TotalSeconds,
            callback.FirstIncumbentTimeSeconds,
            variables,
            binaries,
            constraints,
            presolveAvailable,
            presolvedVariables,
            presolvedBinaries,
            presolvedConstraints,
            0,
            feedbackLayers.Permanent.Count,
            feedbackLayers.RunLocal.Count,
            attemptFeedback.FeedbackId,
            parentFingerprint,
            candidateFingerprint,
            feedbackChanged,
            changed.Count(value => value.StartsWith("stop:", StringComparison.Ordinal)),
            changed.Count(value => value.StartsWith("section_precedence:", StringComparison.Ordinal)),
            changed.Count(value => value.StartsWith("station_event_precedence:", StringComparison.Ordinal)),
            changed.Count(value => value.StartsWith("arrival:", StringComparison.Ordinal)),
            changed.Count(value => value.StartsWith("departure:", StringComparison.Ordinal)),
            changed,
            validation.IsFeasible,
            validation.Violations.Count,
            accepted ? candidate : null,
            !validation.IsFeasible
                ? "complete_macro_candidate_failed_macro_validation"
                : !feedbackChanged
                    ? "complete_macro_returned_rejected_feedback_signature"
                    : string.Equals(parentFingerprint, candidateFingerprint, StringComparison.OrdinalIgnoreCase)
                        ? "complete_macro_returned_parent_candidate"
                        : "complete_macro_candidate_validated");
    }

    private static void FixMacroDiscreteDecisions(
        BasicMilpModel model,
        MasterSolutionSnapshot reference,
        IReadOnlyList<MacroRepairFeedback> feedback,
        MacroDiscreteReleaseScope? releaseScope)
    {
        Dictionary<string, int> feedbackStops = MergeFixedPremises(
            feedback.SelectMany(item => item.StopValuesByStopId));
        Dictionary<string, int> feedbackSections = MergeFixedPremises(
            feedback.SelectMany(item => item.SectionPrecedenceValuesByKey));
        Dictionary<string, int> feedbackStationEvents = MergeFixedPremises(
            feedback.SelectMany(item => item.StationEventPrecedenceValuesByKey));
        FixDiscreteFamily(
            model.StopVars,
            reference.StopByStopId,
            feedbackStops,
            "stop",
            releaseScope?.StopIds);
        FixDiscreteFamily(
            model.SectionPrecedenceVars,
            reference.SectionPrecedenceByKey,
            feedbackSections,
            "section_precedence",
            releaseScope?.SectionPrecedenceKeys);
        FixDiscreteFamily(
            model.StationEventPrecedenceVars,
            reference.StationEventPrecedenceByKey,
            feedbackStationEvents,
            "station_event_precedence",
            releaseScope?.StationEventPrecedenceKeys);
        foreach (MaintenanceWindowBinaryBinding binding in model.MaintenanceWindowBinaryBindings)
        {
            if (releaseScope is not null &&
                (releaseScope.AffectedTimeStopIds.Contains(binding.SectionStartStopId) ||
                 releaseScope.AffectedTimeStopIds.Contains(binding.SectionEndStopId)))
                continue;
            int value = MacroDiscreteStateResolver.ResolveMaintenanceBeforeValue(
                reference.ArrivalSecondsByStopId[binding.SectionEndStopId],
                reference.DepartureSecondsByStopId[binding.SectionStartStopId],
                binding.WindowStartSeconds,
                binding.WindowEndSeconds);
            FixDiscreteVariable(binding.BeforeWindowVariable, value, "maintenance_window");
        }
        foreach (StationQuotaCategoryBinding binding in model.StationQuotaCategoryBindings)
        {
            if (releaseScope is not null &&
                releaseScope.AffectedTimeStopIds.Contains(binding.StopId))
                continue;
            TrainStopRecord stop = model.Instance.StopsById[binding.StopId];
            MacroQuotaCategory category = MacroDiscreteStateResolver.ResolveQuotaCategory(
                reference.StopByStopId[binding.StopId] == 1,
                stop.StopRole == TrainStopRole.Origin
                    ? reference.DepartureSecondsByStopId[binding.StopId]
                    : reference.ArrivalSecondsByStopId[binding.StopId],
                binding.WindowStartSeconds,
                binding.WindowEndSeconds);
            FixDiscreteVariable(binding.InWindowVariable,
                category == MacroQuotaCategory.InWindow ? 1 : 0, "quota_in_window");
            FixDiscreteVariable(binding.LeftOutsideVariable,
                category == MacroQuotaCategory.LeftOutside ? 1 : 0, "quota_left_outside");
            FixDiscreteVariable(binding.RightOutsideVariable,
                category == MacroQuotaCategory.RightOutside ? 1 : 0, "quota_right_outside");
        }
        model.Model.Update();
    }

    private static Dictionary<string, int> MergeFixedPremises(
        IEnumerable<KeyValuePair<string, int>> premises)
    {
        Dictionary<string, int> merged = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, int value) in premises)
        {
            if (merged.TryGetValue(key, out int existing) && existing != value)
                throw new InvalidDataException(
                    $"Fixed-discrete time repair has conflicting feedback premises for '{key}'.");
            merged[key] = value;
        }
        return merged;
    }

    private static void FixDiscreteFamily(
        IReadOnlyDictionary<string, GRBVar> variables,
        IReadOnlyDictionary<string, int> referenceValues,
        IReadOnlyDictionary<string, int> feedbackFallbackValues,
        string family,
        IReadOnlySet<string>? releasedKeys)
    {
        foreach ((string key, GRBVar variable) in variables)
        {
            if (releasedKeys?.Contains(key) == true)
                continue;
            int value;
            if (!referenceValues.TryGetValue(key, out value) &&
                !feedbackFallbackValues.TryGetValue(key, out value))
                throw new InvalidDataException(
                    $"Cannot fix {family} '{key}' because the reference and feedback contain no value.");
            if (value is not 0 and not 1)
                throw new InvalidDataException($"Cannot fix {family} '{key}' to non-binary value {value}.");
            FixDiscreteVariable(variable, value, family);
        }
    }

    private static void FixDiscreteVariable(GRBVar variable, int value, string family)
    {
        if (value is not 0 and not 1)
            throw new InvalidDataException(
                $"Cannot fix {family} '{variable.Get(GRB.StringAttr.VarName)}' to non-binary value {value}.");
        variable.Set(GRB.DoubleAttr.LB, value);
        variable.Set(GRB.DoubleAttr.UB, value);
    }

    private static GRBLinExpr BuildMinimumDiscreteChangeObjective(
        BasicMilpModel model,
        MasterSolutionSnapshot reference)
    {
        GRBLinExpr objective = new();
        AddMismatchTerms(objective, model.StopVars, reference.StopByStopId, 4.0);
        AddMismatchTerms(objective, model.SectionPrecedenceVars, reference.SectionPrecedenceByKey, 2.0);
        AddMismatchTerms(objective, model.StationEventPrecedenceVars,
            reference.StationEventPrecedenceByKey, 1.0);
        return objective;
    }

    internal static MacroFeedbackRepairObjectiveMode ResolveObjectiveMode(
        bool fixMacroDiscreteDecisions) =>
        fixMacroDiscreteDecisions
            ? MacroFeedbackRepairObjectiveMode.FeasibilityFirst
            : MacroFeedbackRepairObjectiveMode.MinimumDiscreteChange;

    private static MasterSolutionMipStartScope BuildEvidenceAwareMipStartScope(
        TimetablingInstance instance,
        MacroRepairFeedback feedback,
        MacroDiscreteReleaseScope? releaseScope)
    {
        HashSet<string> stopIds = new(
            releaseScope?.StopIds ?? feedback.StopValuesByStopId.Keys.ToHashSet(
                StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        HashSet<string> timeStopIds = new(
            releaseScope?.AffectedTimeStopIds ?? stopIds,
            StringComparer.OrdinalIgnoreCase);
        foreach (ConditionalMacroTimeCut cut in feedback.ConditionalTimeCuts)
        {
            timeStopIds.Add(cut.EarlierEvent.StopId);
            timeStopIds.Add(cut.LaterEvent.StopId);
        }
        foreach (ConditionalMacroTrackOccupationCut cut in feedback.ConditionalTrackOccupationCuts)
        {
            timeStopIds.Add(cut.FirstStart.Event.StopId);
            timeStopIds.Add(cut.FirstEnd.Event.StopId);
            timeStopIds.Add(cut.SecondStart.Event.StopId);
            timeStopIds.Add(cut.SecondEnd.Event.StopId);
        }
        HashSet<string> sectionKeys = new(
            releaseScope?.SectionPrecedenceKeys ?? feedback.SectionPrecedenceValuesByKey.Keys.ToHashSet(
                StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        HashSet<string> stationEventKeys = new(
            releaseScope?.StationEventPrecedenceKeys ??
            feedback.StationEventPrecedenceValuesByKey.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        HashSet<string> stations = stopIds.Concat(timeStopIds)
            .Where(instance.StopsById.ContainsKey)
            .Select(stopId => instance.StopsById[stopId].StationName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> supplementKeys = ResolveAffectedSupplementKeys(instance, timeStopIds);
        return new MasterSolutionMipStartScope(
            timeStopIds,
            stopIds,
            sectionKeys,
            stationEventKeys,
            supplementKeys,
            stations);
    }

    private static HashSet<string> ResolveAffectedSupplementKeys(
        TimetablingInstance instance,
        IReadOnlySet<string> affectedStopIds)
    {
        HashSet<string> keys = new(StringComparer.OrdinalIgnoreCase);
        foreach (TrainRecord train in instance.Trains)
        {
            for (int index = 0; index < train.OrderedStops.Count - 1; index++)
            {
                TrainStopRecord current = train.OrderedStops[index];
                TrainStopRecord next = train.OrderedStops[index + 1];
                if (!affectedStopIds.Contains(current.StopId) && !affectedStopIds.Contains(next.StopId))
                    continue;
                keys.Add(RunningTimeSupplementPolicy.BuildKey(
                    current.TrainIndex,
                    next.IncomingSectionName ?? $"{current.StationOrder}-{next.StationOrder}"));
            }
        }
        return keys;
    }

    private static void AddMismatchTerms(
        GRBLinExpr expression,
        IReadOnlyDictionary<string, GRBVar> variables,
        IReadOnlyDictionary<string, int> reference,
        double weight)
    {
        foreach ((string key, GRBVar variable) in variables)
        {
            if (!reference.TryGetValue(key, out int value))
                continue;
            if (value == 0)
                expression.AddTerm(weight, variable);
            else
            {
                expression.AddConstant(weight);
                expression.AddTerm(-weight, variable);
            }
        }
    }

    private static void ValidateExactFeedbackCoverage(BasicMilpModel model, MacroRepairFeedback feedback)
    {
        string[] missingStops = feedback.StopValuesByStopId.Keys
            .Where(key => !model.StopVars.ContainsKey(key)).ToArray();
        string[] missingSections = feedback.SectionPrecedenceValuesByKey.Keys
            .Where(key => !model.SectionPrecedenceVars.ContainsKey(key)).ToArray();
        string[] missingStationEvents = feedback.StationEventPrecedenceValuesByKey.Keys
            .Where(key => !model.StationEventPrecedenceVars.ContainsKey(key)).ToArray();
        string[] missingEvents = feedback.ConditionalTimeCuts
            .SelectMany(cut => new[] { cut.LaterEvent, cut.EarlierEvent })
            .Concat(feedback.ConditionalTrackOccupationCuts.SelectMany(cut => new[]
            {
                cut.FirstStart.Event,
                cut.FirstEnd.Event,
                cut.SecondStart.Event,
                cut.SecondEnd.Event
            }))
            .Where(item => !ResolveEventVariables(model, item).ContainsKey(item.StopId))
            .Select(item => $"{item.StopId}:{item.Side}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (missingStops.Length + missingSections.Length + missingStationEvents.Length + missingEvents.Length > 0)
            throw new InvalidDataException(
                $"Feedback mapping is incomplete; missing stops=[{string.Join('|', missingStops)}], " +
                $"sections=[{string.Join('|', missingSections)}], station_events=[{string.Join('|', missingStationEvents)}], " +
                $"time_events=[{string.Join('|', missingEvents)}].");
        if (feedback.StopValuesByStopId.Count + feedback.SectionPrecedenceValuesByKey.Count +
            feedback.StationEventPrecedenceValuesByKey.Count + feedback.ConditionalTimeCuts.Count +
            feedback.ConditionalTrackOccupationCuts.Count == 0)
            throw new InvalidDataException("Feedback has no supported Macro condition and cannot drive a replot.");
    }

    internal static int AddConditionalTimeCuts(
        BasicMilpModel model,
        IEnumerable<ConditionalMacroTimeCut> cuts)
    {
        int added = 0;
        foreach (ConditionalMacroTimeCut cut in cuts
            .GroupBy(item => item.CutId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()))
        {
            GRBVar later = ResolveEventVariables(model, cut.LaterEvent)[cut.LaterEvent.StopId];
            GRBVar earlier = ResolveEventVariables(model, cut.EarlierEvent)[cut.EarlierEvent.StopId];
            double laterLowerBound = later.Get(GRB.DoubleAttr.LB);
            double earlierUpperBound = earlier.Get(GRB.DoubleAttr.UB);
            if (laterLowerBound <= -GRB.INFINITY / 2.0 || earlierUpperBound >= GRB.INFINITY / 2.0)
                throw new InvalidDataException($"Conditional time cut {cut.CutId} requires finite event bounds.");
            double bigM = Math.Max(0.0,
                cut.MinimumGapSeconds - (laterLowerBound - earlierUpperBound));
            GRBLinExpr relation = new();
            relation.AddTerm(1.0, later);
            relation.AddTerm(-1.0, earlier);
            AddFalsePremiseTerms(relation, model.StopVars, cut.RequiredStopValuesByStopId, bigM);
            AddFalsePremiseTerms(relation, model.SectionPrecedenceVars,
                cut.RequiredSectionPrecedenceValuesByKey, bigM);
            AddFalsePremiseTerms(relation, model.StationEventPrecedenceVars,
                cut.RequiredStationEventPrecedenceValuesByKey, bigM);
            model.Model.AddConstr(relation, GRB.GREATER_EQUAL, cut.MinimumGapSeconds,
                $"micro_time_feedback_{SanitizeName(cut.CutId)}");
            added++;
        }
        model.Model.Update();
        return added;
    }

    internal static int AddConditionalTrackOccupationCuts(
        BasicMilpModel model,
        IEnumerable<ConditionalMacroTrackOccupationCut> cuts,
        MasterSolutionSnapshot? fixedDiscreteReference = null)
    {
        int added = 0;
        foreach (ConditionalMacroTrackOccupationCut cut in cuts
            .GroupBy(item => item.CutId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()))
        {
            (GRBVar FirstStart, double FirstStartOffset) = ResolveAffineEventVariable(model, cut.FirstStart);
            (GRBVar FirstEnd, double FirstEndOffset) = ResolveAffineEventVariable(model, cut.FirstEnd);
            (GRBVar SecondStart, double SecondStartOffset) = ResolveAffineEventVariable(model, cut.SecondStart);
            (GRBVar SecondEnd, double SecondEndOffset) = ResolveAffineEventVariable(model, cut.SecondEnd);
            double forwardMinimum =
                SecondStart.Get(GRB.DoubleAttr.LB) + SecondStartOffset -
                FirstEnd.Get(GRB.DoubleAttr.UB) - FirstEndOffset;
            double reverseMinimum =
                FirstStart.Get(GRB.DoubleAttr.LB) + FirstStartOffset -
                SecondEnd.Get(GRB.DoubleAttr.UB) - SecondEndOffset;
            if (new[]
                {
                    FirstStart.Get(GRB.DoubleAttr.LB), FirstStart.Get(GRB.DoubleAttr.UB),
                    FirstEnd.Get(GRB.DoubleAttr.LB), FirstEnd.Get(GRB.DoubleAttr.UB),
                    SecondStart.Get(GRB.DoubleAttr.LB), SecondStart.Get(GRB.DoubleAttr.UB),
                    SecondEnd.Get(GRB.DoubleAttr.LB), SecondEnd.Get(GRB.DoubleAttr.UB)
                }.Any(value => value <= -GRB.INFINITY / 2.0 || value >= GRB.INFINITY / 2.0))
                throw new InvalidDataException(
                    $"Conditional track occupation cut {cut.CutId} requires finite event bounds.");

            if (fixedDiscreteReference is not null)
            {
                // In the time-only stage every Macro discrete variable is fixed to this exact
                // reference.  Therefore the occupation disjunction may use the order already
                // realized by that reference.  If the premise is false the guarded relation is
                // inactive; if the reference violates both orders this stage is not a valid
                // relaxation and must not silently choose a new discrete order.
                if (!cut.IsActive(fixedDiscreteReference))
                    continue;
                double forwardGap = cut.SecondStart.Resolve(fixedDiscreteReference) -
                    cut.FirstEnd.Resolve(fixedDiscreteReference);
                double reverseGap = cut.FirstStart.Resolve(fixedDiscreteReference) -
                    cut.SecondEnd.Resolve(fixedDiscreteReference);
                bool forwardSatisfied = forwardGap >= -cut.ToleranceSeconds - 1e-6;
                bool reverseSatisfied = reverseGap >= -cut.ToleranceSeconds - 1e-6;
                if (!forwardSatisfied && !reverseSatisfied)
                    throw new InvalidDataException(
                        $"Fixed-discrete reference violates retained track occupation cut {cut.CutId}.");

                bool useForward = forwardSatisfied &&
                    (!reverseSatisfied || forwardGap >= reverseGap);
                GRBLinExpr fixedOrder = useForward
                    ? SecondStart - FirstEnd
                    : FirstStart - SecondEnd;
                fixedOrder.AddConstant(useForward
                    ? SecondStartOffset - FirstEndOffset
                    : FirstStartOffset - SecondEndOffset);
                model.Model.AddConstr(
                    fixedOrder,
                    GRB.GREATER_EQUAL,
                    -cut.ToleranceSeconds,
                    $"micro_track_occ_fixed_{(useForward ? "fwd" : "rev")}_{SanitizeName(cut.CutId)}");
                added++;
                continue;
            }

            double forwardBigM = Math.Max(0.0, -cut.ToleranceSeconds - forwardMinimum);
            double reverseBigM = Math.Max(0.0, -cut.ToleranceSeconds - reverseMinimum);
            GRBVar firstBeforeSecond = model.Model.AddVar(
                0.0, 1.0, 0.0, GRB.BINARY,
                $"micro_track_order_{SanitizeName(cut.CutId)}");

            GRBLinExpr forward = SecondStart - FirstEnd;
            forward.AddConstant(SecondStartOffset - FirstEndOffset);
            forward.AddTerm(forwardBigM, firstBeforeSecond);
            AddFalsePremiseTerms(
                forward, model.StopVars, cut.RequiredStopValuesByStopId, forwardBigM);
            model.Model.AddConstr(
                forward,
                GRB.GREATER_EQUAL,
                -cut.ToleranceSeconds,
                $"micro_track_occ_fwd_{SanitizeName(cut.CutId)}");

            GRBLinExpr reverse = FirstStart - SecondEnd;
            reverse.AddConstant(FirstStartOffset - SecondEndOffset + reverseBigM);
            reverse.AddTerm(-reverseBigM, firstBeforeSecond);
            AddFalsePremiseTerms(
                reverse, model.StopVars, cut.RequiredStopValuesByStopId, reverseBigM);
            model.Model.AddConstr(
                reverse,
                GRB.GREATER_EQUAL,
                -cut.ToleranceSeconds,
                $"micro_track_occ_rev_{SanitizeName(cut.CutId)}");
            added++;
        }
        model.Model.Update();
        return added;
    }

    internal static StationEventConflictCandidate[] ResolveConditionalStationEdges(
        TimetablingInstance instance,
        IReadOnlyList<ConditionalMacroTimeCut> cuts)
    {
        List<StationEventConflictCandidate> edges = [];
        foreach (ConditionalMacroTimeCut cut in cuts)
        {
            if (!instance.StopsById.TryGetValue(cut.EarlierEvent.StopId, out TrainStopRecord? earlier) ||
                !instance.StopsById.TryGetValue(cut.LaterEvent.StopId, out TrainStopRecord? later))
                throw new InvalidDataException($"Conditional time cut {cut.CutId} names an unknown stop.");
            if (!string.Equals(earlier.StationName, later.StationName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"Conditional station time cut {cut.CutId} spans two stations without a cross-station proof.");
            StationEventConflictCandidate edge = new(
                earlier.StationName,
                earlier.TrainIndex,
                later.TrainIndex,
                earlier.StationOrder,
                later.StationOrder,
                cut.EarlierEvent.Side,
                cut.LaterEvent.Side);
            string key = BasicMilpModelBuilder.BuildStationEventPrecedenceKey(edge);
            if (!cut.RequiredStationEventPrecedenceValuesByKey.TryGetValue(key, out int value) || value != 0)
                throw new InvalidDataException(
                    $"Conditional time cut {cut.CutId} must carry its forward station-order premise {key}=0.");
            edges.Add(edge);
        }
        return edges.DistinctBy(BasicMilpModelBuilder.BuildStationEventPrecedenceKey).ToArray();
    }

    private static IReadOnlyDictionary<string, GRBVar> ResolveEventVariables(
        BasicMilpModel model,
        MacroTimeEvent item) => item.Side == StationEventSide.Incoming
        ? model.ArrivalVars
        : model.DepartureVars;

    private static (GRBVar Variable, double OffsetSeconds) ResolveAffineEventVariable(
        BasicMilpModel model,
        MacroAffineTimeEvent item) =>
        (ResolveEventVariables(model, item.Event)[item.Event.StopId], item.OffsetSeconds);

    private static void AddFalsePremiseTerms(
        GRBLinExpr expression,
        IReadOnlyDictionary<string, GRBVar> variables,
        IReadOnlyDictionary<string, int> requiredValues,
        double bigM)
    {
        foreach ((string key, int value) in requiredValues)
        {
            if (value == 1)
            {
                expression.AddConstant(bigM);
                expression.AddTerm(-bigM, variables[key]);
            }
            else if (value == 0)
                expression.AddTerm(bigM, variables[key]);
            else
                throw new InvalidDataException($"Conditional premise {key} is not binary.");
        }
    }

    private static string SanitizeName(string value) => string.Concat(value.Select(character =>
        char.IsLetterOrDigit(character) || character is '_' or '-' ? character : '_'));

    private static HashSet<int> ResolveEvidenceTrains(
        TimetablingInstance instance,
        MacroRepairFeedback feedback)
    {
        HashSet<int> trains = feedback.StopValuesByStopId.Keys
            .Where(instance.StopsById.ContainsKey)
            .Select(stopId => instance.StopsById[stopId].TrainIndex)
            .ToHashSet();
        foreach (string stopId in feedback.ConditionalTimeCuts
            .SelectMany(cut => new[] { cut.LaterEvent.StopId, cut.EarlierEvent.StopId })
            .Concat(feedback.ConditionalTimeCuts.SelectMany(cut => cut.RequiredStopValuesByStopId.Keys))
            .Concat(feedback.ConditionalTrackOccupationCuts.SelectMany(cut => new[]
            {
                cut.FirstStart.Event.StopId,
                cut.FirstEnd.Event.StopId,
                cut.SecondStart.Event.StopId,
                cut.SecondEnd.Event.StopId
            }))
            .Concat(feedback.ConditionalTrackOccupationCuts.SelectMany(
                cut => cut.RequiredStopValuesByStopId.Keys)))
            if (instance.StopsById.TryGetValue(stopId, out TrainStopRecord? stop))
                trains.Add(stop.TrainIndex);
        foreach (string key in feedback.SectionPrecedenceValuesByKey.Keys
            .Concat(feedback.StationEventPrecedenceValuesByKey.Keys))
        {
            string[] parts = key.Split("::", StringSplitOptions.None);
            foreach (string part in parts)
                if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int train) &&
                    instance.Trains.Any(item => item.TrainIndex == train))
                    trains.Add(train);
        }
        return trains;
    }

    private static bool FeedbackConditionChanged(
        MacroRepairFeedback feedback,
        MasterSolutionSnapshot candidate) =>
        feedback.StopValuesByStopId.Any(item => candidate.StopByStopId[item.Key] != item.Value) ||
        feedback.SectionPrecedenceValuesByKey.Any(item =>
            candidate.SectionPrecedenceByKey[item.Key] != item.Value) ||
        feedback.StationEventPrecedenceValuesByKey.Any(item =>
            candidate.StationEventPrecedenceByKey[item.Key] != item.Value) ||
        feedback.ConditionalTimeCuts.Any(cut => cut.IsSatisfied(candidate)) ||
        feedback.ConditionalTrackOccupationCuts.Any(cut => cut.IsSatisfied(candidate));

    private static List<string> CollectChanges(
        MasterSolutionSnapshot reference,
        MasterSolutionSnapshot candidate)
    {
        List<string> changed = [];
        foreach ((string key, int value) in candidate.StopByStopId)
            if (reference.StopByStopId.TryGetValue(key, out int before) && before != value)
                changed.Add($"stop:{key}:{before}->{value}");
        foreach ((string key, int value) in candidate.SectionPrecedenceByKey)
            if (reference.SectionPrecedenceByKey.TryGetValue(key, out int before) && before != value)
                changed.Add($"section_precedence:{key}:{before}->{value}");
        foreach ((string key, int value) in candidate.StationEventPrecedenceByKey)
            if (reference.StationEventPrecedenceByKey.TryGetValue(key, out int before) && before != value)
                changed.Add($"station_event_precedence:{key}:{before}->{value}");
        foreach ((string key, double value) in candidate.ArrivalSecondsByStopId)
            if (reference.ArrivalSecondsByStopId.TryGetValue(key, out double before) &&
                Math.Abs(before - value) > 1e-6)
                changed.Add($"arrival:{key}:{before:R}->{value:R}");
        foreach ((string key, double value) in candidate.DepartureSecondsByStopId)
            if (reference.DepartureSecondsByStopId.TryGetValue(key, out double before) &&
                Math.Abs(before - value) > 1e-6)
                changed.Add($"departure:{key}:{before:R}->{value:R}");
        return changed;
    }

    private static (bool Available, int Variables, int Binaries, int Constraints)
        CapturePresolveSize(GRBModel model)
    {
        try
        {
            using GRBModel presolved = model.Presolve();
            return (true,
                presolved.Get(GRB.IntAttr.NumVars),
                presolved.Get(GRB.IntAttr.NumBinVars),
                presolved.Get(GRB.IntAttr.NumConstrs));
        }
        catch (GRBException)
        {
            return (false, -1, -1, -1);
        }
    }

    private static CompleteMacroFeedbackRepairResult Empty(
        int statusCode,
        string status,
        double runtime,
        double? firstIncumbent,
        int variables,
        int binaries,
        int constraints,
        bool presolveAvailable,
        int presolvedVariables,
        int presolvedBinaries,
        int presolvedConstraints,
        int permanentCount,
        int temporaryCount,
        string feedbackId,
        string parentFingerprint,
        string reason) =>
        new(false, statusCode, status, runtime, firstIncumbent,
            variables, binaries, constraints, presolveAvailable,
            presolvedVariables, presolvedBinaries, presolvedConstraints,
            0, permanentCount, temporaryCount, feedbackId, parentFingerprint, string.Empty,
            false, 0, 0, 0, 0, 0, [], false, 0, null, reason);

    private static string StatusName(int status) => status switch
    {
        GRB.Status.OPTIMAL => "OPTIMAL",
        GRB.Status.SOLUTION_LIMIT => "SOLUTION_LIMIT",
        GRB.Status.INFEASIBLE => "INFEASIBLE",
        GRB.Status.TIME_LIMIT => "TIME_LIMIT",
        GRB.Status.INTERRUPTED => "INTERRUPTED",
        _ => status.ToString(CultureInfo.InvariantCulture)
    };
}

internal sealed record MacroMicroJointIdentityCheck(
    bool IsValid,
    int ExpectedRequirementCount,
    int AssignmentCount,
    int DistinctRequirementCount,
    int MissingRequirementCount,
    int ExtraRequirementCount,
    int MismatchedCandidateCount);

internal static class MacroMicroJointAcceptancePolicy
{
    internal static MacroMicroJointIdentityCheck CheckIdentity(
        string candidateFingerprint,
        IEnumerable<string> expectedRequirementIds,
        IReadOnlyList<VerifiedMicroAssignmentWitness> assignments)
    {
        HashSet<string> expected = expectedRequirementIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> actual = assignments.Select(item => item.RequirementId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        int mismatch = assignments.Count(item => !string.Equals(
            item.CandidateSnapshotFingerprint,
            candidateFingerprint,
            StringComparison.OrdinalIgnoreCase));
        int missing = expected.Except(actual, StringComparer.OrdinalIgnoreCase).Count();
        int extra = actual.Except(expected, StringComparer.OrdinalIgnoreCase).Count();
        bool valid = mismatch == 0 && missing == 0 && extra == 0 &&
            assignments.Count == expected.Count && actual.Count == expected.Count;
        return new(valid, expected.Count, assignments.Count, actual.Count, missing, extra, mismatch);
    }
}
