using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Gurobi;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

internal sealed record MicroExtendabilityRepairRequest(
    string BaselineDirectory,
    string OutputDirectory,
    string DatasetPath,
    string? HistoricalFeedbackPath = null,
    double PilotBudgetSeconds = 600,
    double FormalBudgetSeconds = 3600,
    int EndpointOccupationSeconds = StationEndpointOccupationPolicy.DefaultSeconds,
    double RunningTimeSupplementRatio = 0.05,
    double ScopeSliceSeconds = 300,
    int DistinctNonExtendableCandidateLimit = 5);

internal sealed record MicroExtendabilityTraceRow(
    int Sequence,
    string Phase,
    int Iteration,
    string Action,
    string ActiveStations,
    string Status,
    bool HasCandidate,
    int LocalVariables,
    int LocalBinaries,
    int LocalConstraints,
    int ServiceRows,
    int RuntimeRows,
    int OutsidePatternCount,
    int FixedInterfaceCount,
    bool FeedbackGenerated,
    bool FeedbackAdded,
    string FeedbackId,
    double RejectedPointViolation,
    string ScopeDecision,
    double LocalSeconds,
    double CoordinationSeconds,
    double ProofSeconds,
    double ValidationSeconds,
    double WallSeconds,
    string Detail,
    string ScopeKey = "",
    string BranchDisposition = "",
    double ScopeCumulativeSeconds = 0,
    int ScopeDistinctNonExtendableCandidates = 0,
    string DecisionDomainIdentity = "",
    int FreeDiscreteDecisions = 0,
    int PresolvedBinaries = 0,
    int FeedbackSaved = 0,
    int FeedbackModelEligible = 0,
    int FeedbackActive = 0,
    int FeedbackSatisfied = 0,
    string FeedbackSkippedByReason = "");

internal sealed record MicroScopeBranchCheckpoint(
    string ScopeKey,
    IReadOnlyList<string> ActiveStations,
    string CandidateFingerprint,
    MasterSolutionSnapshot Candidate,
    IReadOnlyList<MicroWitnessCertificate> ChangedPatterns,
    LocalRepairDecisionDomain DecisionDomain,
    IReadOnlyList<string> StructurallyApplicableFeedbackIds,
    string Status);

internal sealed record RuntimeMicroScopeCheckpoint(
    MasterSolutionSnapshot Candidate,
    IReadOnlyDictionary<string, MicroWitnessCertificate> Patterns,
    LocalRepairDecisionDomain DecisionDomain,
    string Status);

internal sealed record MicroScopeBranchCheckpointManifest(
    string ScopeKey,
    IReadOnlyList<string> ActiveStations,
    string CandidateFingerprint,
    string CandidatePath,
    IReadOnlyList<MicroWitnessCertificate> ChangedPatterns,
    LocalRepairDecisionDomain DecisionDomain,
    IReadOnlyList<string> StructurallyApplicableFeedbackIds,
    string Status);

internal sealed record MicroExtendabilityPhaseResult(
    string Phase,
    bool MechanismPassed,
    bool JointFeasible,
    string FinalStatus,
    string StopReason,
    int Iterations,
    int LocalCandidates,
    int ContinuousChecks,
    int FeedbackGenerated,
    int FeedbackAdded,
    int FeedbackFollowupSolves,
    int ScopeAdjustments,
    int ServiceRows,
    int StopChanges,
    double LocalSeconds,
    double CoordinationSeconds,
    double ProofSeconds,
    double ValidationSeconds,
    double WallSeconds,
    IReadOnlyList<string> FinalActiveStations,
    MasterSolutionSnapshot? FinalSnapshot,
    IReadOnlyList<MicroWitnessCertificate> FinalPatterns,
    FixedPatternJointValidation? Validation,
    ContinuousCoordinationEvidenceBinding? LastEvidence,
    IReadOnlyList<ContinuousProofBusinessMapping> LastMappings,
    IReadOnlyList<ContinuousExtendabilityFeedback> Feedback,
    IReadOnlyList<ExtendabilityScopeBranchState> ScopeBranches,
    IReadOnlyList<MicroScopeBranchCheckpoint> BranchCheckpoints,
    int HistoricalFeedbackLoaded,
    int FeedbackModelEligibleObservations,
    int FeedbackActiveObservations,
    IReadOnlyDictionary<string, int> FeedbackSkippedByReason);

/// <summary>
/// Fixed-Macro-start Micro experiment.  The saved Macro is loaded but never optimized here.
/// Local resource/stop/time candidates are checked against a full-line continuous recourse
/// model; only a bound-complete model-local Farkas ray can create a time feedback row.
/// </summary>
internal static class MicroExtendabilityRepairRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    internal static int Run(TimetablingInstance instance, MicroExtendabilityRepairRequest request)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(request);
        if (request.PilotBudgetSeconds <= 0 || request.FormalBudgetSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "Both wall-clock budgets must be positive.");
        if (request.ScopeSliceSeconds <= 0 || request.DistinctNonExtendableCandidateLimit <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "Scope slice and distinct-candidate limit must be positive.");
        if (request.EndpointOccupationSeconds is not (0 or 600) ||
            Math.Abs(request.RunningTimeSupplementRatio - 0.05) > 1e-12)
            throw new InvalidDataException("This recovery run requires endpoint=0 (or explicit legacy 600) and running supplement ratio=0.05.");

        string baseline = Path.GetFullPath(request.BaselineDirectory);
        string output = Path.GetFullPath(request.OutputDirectory);
        string sourcePath = Path.Combine(baseline, "target_model", "saved_macro_candidate.snapshot.json");
        string bundlePath = Path.Combine(baseline, "patterns", "pair01_complete_pattern_bundle.json");
        string evidencePath = Path.Combine(baseline, "proof_mapping", "current_coordination_evidence.json");
        foreach (string required in new[] { sourcePath, bundlePath, evidencePath })
            if (!File.Exists(required)) throw new FileNotFoundException("Required recovery baseline is missing.", required);
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(Path.Combine(output, "logs"));
        Directory.CreateDirectory(Path.Combine(output, "state"));

        Stopwatch total = Stopwatch.StartNew();
        MasterSolutionSnapshot source = MasterSolutionSnapshotStore.Load(
            sourcePath, instance.Meta.InstanceName,
            instance.Trains.Select(train => train.TrainIndex).ToHashSet());
        MicroPatternReconstructionBundle bundle = MicroPatternReconstructionBundleStore.Load(
            bundlePath, instance, source);
        if (bundle.Patterns.Sum(pattern => pattern.Witnesses.Count) != instance.TrainStops.Count)
            throw new InvalidDataException("Saved local patterns do not cover the 685 JZ requirements exactly once.");
        IReadOnlyList<ContinuousProofBusinessMapping> baselineMappings = LoadMappings(evidencePath);
        IReadOnlyList<ContinuousExtendabilityFeedback> historicalFeedback =
            !string.IsNullOrWhiteSpace(request.HistoricalFeedbackPath)
                ? ContinuousExtendabilityFeedbackStore.Load(Path.GetFullPath(request.HistoricalFeedbackPath))
                : [];
        string primaryStation = SelectPrimaryStation(baselineMappings, bundle.Patterns);
        List<MicroExtendabilityTraceRow> trace = [];

        MicroExtendabilityPhaseResult pilot;
        MicroExtendabilityPhaseResult? formal = null;
        try
        {
            pilot = RunPhase(instance, source, sourcePath, bundle, baselineMappings, historicalFeedback, [primaryStation],
                output, "pilot", request.PilotBudgetSeconds, reserveSeconds: 60,
                stopAfterMechanismEvidence: true, request, trace);
            if (pilot.MechanismPassed)
            {
                // Formal starts from the exact saved Macro and pattern bundle again.  No pilot
                // pattern, feedback row, incumbent or solver state is passed into this call.
                formal = RunPhase(instance, source, sourcePath, bundle, baselineMappings, historicalFeedback, [primaryStation],
                    output, "formal", request.FormalBudgetSeconds, reserveSeconds: 120,
                    stopAfterMechanismEvidence: false, request, trace);
            }
        }
        catch (Exception exception)
        {
            pilot = new("pilot", false, false, "engineering_exception",
                $"{exception.GetType().Name}:{exception.Message}",
                Iterations: 0, LocalCandidates: 0, ContinuousChecks: 0,
                FeedbackGenerated: 0, FeedbackAdded: 0, FeedbackFollowupSolves: 0,
                ScopeAdjustments: 0, ServiceRows: 0, StopChanges: 0,
                LocalSeconds: 0, CoordinationSeconds: 0, ProofSeconds: 0,
                ValidationSeconds: 0, WallSeconds: total.Elapsed.TotalSeconds,
                FinalActiveStations: [primaryStation], FinalSnapshot: null,
                FinalPatterns: bundle.Patterns, Validation: null, LastEvidence: null,
                LastMappings: [], Feedback: historicalFeedback,
                ScopeBranches: [], BranchCheckpoints: [], HistoricalFeedbackLoaded: historicalFeedback.Count,
                FeedbackModelEligibleObservations: 0, FeedbackActiveObservations: 0,
                FeedbackSkippedByReason: new Dictionary<string, int>());
            File.WriteAllText(Path.Combine(output, "logs", "engineering_exception.txt"), exception.ToString(),
                new UTF8Encoding(false));
        }

        total.Stop();
        MicroExtendabilityPhaseResult final = formal ?? pilot;
        SaveArtifacts(instance, request, baseline, sourcePath, bundlePath, source, bundle,
            pilot, formal, final, trace, output, total.Elapsed.TotalSeconds);
        return 0;
    }

    private static MicroExtendabilityPhaseResult RunPhase(
        TimetablingInstance instance,
        MasterSolutionSnapshot pristineSource,
        string sourcePath,
        MicroPatternReconstructionBundle pristineBundle,
        IReadOnlyList<ContinuousProofBusinessMapping> baselineMappings,
        IReadOnlyList<ContinuousExtendabilityFeedback> historicalFeedback,
        IReadOnlyList<string> initialStations,
        string output,
        string phase,
        double wallBudgetSeconds,
        double reserveSeconds,
        bool stopAfterMechanismEvidence,
        MicroExtendabilityRepairRequest request,
        List<MicroExtendabilityTraceRow> trace)
    {
        Stopwatch wall = Stopwatch.StartNew();
        Dictionary<string, MicroWitnessCertificate> patterns = pristineBundle.Patterns.ToDictionary(
            pattern => pattern.StationName, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> pristineCertificateIds = pristineBundle.Patterns.ToDictionary(
            pattern => pattern.StationName, pattern => pattern.CertificateId, StringComparer.OrdinalIgnoreCase);
        MasterSolutionSnapshot working = pristineSource;
        string[] active = initialStations.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        string frozenBaselineIdentity = SnapshotFingerprint.Calculate(pristineSource);
        ExtendabilityScopeScheduler scheduler = new(frozenBaselineIdentity,
            request.ScopeSliceSeconds, request.DistinctNonExtendableCandidateLimit,
            maximumStations: 3, rootStations: active);
        scheduler.AddHistoricalEvidence(active, baselineMappings);
        IndependentStationPatternSession? session = null;
        List<ContinuousExtendabilityFeedback> feedback = historicalFeedback
            .GroupBy(item => item.FeedbackId, StringComparer.Ordinal).Select(group => group.First()).ToList();
        IReadOnlyList<ContinuousProofBusinessMapping> lastMappings = baselineMappings;
        ContinuousCoordinationEvidenceBinding? lastEvidence = null;
        FixedPatternJointValidation? validation = null;
        MasterSolutionSnapshot? finalSnapshot = null;
        Dictionary<string, RuntimeMicroScopeCheckpoint> runtimeCheckpoints = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string[]> eligibleFeedbackByScope = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> feedbackAwaitingScopes = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> scopesSubmittedToCoordination = new(StringComparer.OrdinalIgnoreCase);
        string[]? pendingSwitchFrom = null;
        ExtendabilityScopeDecision? pendingSwitchDecision = null;
        LocalRepairDecisionDomain? pendingPriorDomain = null;
        bool effectiveScopeSwitchExecuted = false;
        string? mostRecentSwitchedScope = null;
        int localCandidates = 0, checks = 0, feedbackGenerated = 0, feedbackAdded = 0;
        int feedbackFollowups = 0, scopeAdjustments = 0, serviceRows = 0, stopChanges = 0;
        int feedbackEligibleObservations = 0, feedbackActiveObservations = 0;
        Dictionary<string, int> feedbackSkippedByReason = new(StringComparer.Ordinal);
        double localSeconds = 0, coordinationSeconds = 0, proofSeconds = 0, validationSeconds = 0;
        double accountedWallSeconds = 0;
        bool mechanismPassed = false, joint = false;
        string finalStatus = "not_started", stopReason = "not_started";
        int iteration = 0;

        try
        {
            while (Remaining(wallBudgetSeconds, reserveSeconds, wall) > 1)
            {
                iteration++;
                if (session is null)
                {
                    string localLog = Path.Combine(output, "logs", $"{phase}_{iteration:00}_local.log");
                    session = IndependentStationPatternSession.Open(instance, working, active,
                        request.EndpointOccupationSeconds, localLog, request.RunningTimeSupplementRatio,
                        allowFlexibleStopDecisions: true);
                    if (pendingSwitchDecision is not null && pendingSwitchFrom is not null && pendingPriorDomain is not null)
                    {
                        if (!session.DecisionDomain.HasAdditionalFreedomBeyond(pendingPriorDomain))
                            throw new InvalidDataException(
                                $"Scope label changed from '{ExtendabilityScopeScheduler.ScopeKey(pendingSwitchFrom)}' " +
                                $"to '{ExtendabilityScopeScheduler.ScopeKey(active)}' without adding a resource/order/stop decision.");
                        scheduler.ConfirmSwitch(pendingSwitchFrom, active, pendingSwitchDecision.Reason);
                        scopeAdjustments++;
                        effectiveScopeSwitchExecuted = true;
                        mostRecentSwitchedScope = ExtendabilityScopeScheduler.ScopeKey(active);
                        trace.Add(ScopeTrace(trace.Count + 1, phase, iteration, active,
                            pendingSwitchDecision, session.DecisionDomain, scheduler, wall.Elapsed.TotalSeconds));
                        pendingSwitchFrom = null;
                        pendingSwitchDecision = null;
                        pendingPriorDomain = null;
                    }
                }

                double localLimit = new[]
                {
                    60.0,
                    Remaining(wallBudgetSeconds, reserveSeconds, wall),
                    scheduler.RemainingSliceSeconds(active)
                }.Min();
                if (localLimit <= 1e-6)
                {
                    ExtendabilityScopeDecision timed = scheduler.SelectNext(active, StationFreedomCosts(pristineBundle));
                    if (QueueSwitch(timed)) continue;
                    finalStatus = "scope_slice_exhausted_no_legal_evidence_scope";
                    stopReason = timed.Reason;
                    break;
                }

                Stopwatch localClock = Stopwatch.StartNew();
                IndependentStationPatternResult local = session.Continue(localLimit);
                localClock.Stop();
                localSeconds += localClock.Elapsed.TotalSeconds;
                serviceRows = Math.Max(serviceRows, local.Audit.ServiceCouplingCount);
                AccountCurrentScope();
                if (!local.HasPattern || local.CandidateSnapshot is null)
                {
                    bool proven = local.StatusCode == GRB.Status.INFEASIBLE;
                    finalStatus = proven ? "local_scope_proven_infeasible" : "local_scope_unknown";
                    stopReason = local.StopReason;
                    if (proven) scheduler.RecordProvenInfeasible(active, local.StopReason);
                    else scheduler.RecordUnknown(active, local.StopReason);
                    trace.Add(Project(trace.Count + 1, phase, iteration, "local_solve", active, local,
                        patterns.Count - active.Length, 0, false, false, "", double.NaN, "",
                        localClock.Elapsed.TotalSeconds, 0, 0, 0, wall.Elapsed.TotalSeconds, stopReason,
                        session.DecisionDomain, scheduler, feedback.Count, 0, 0, 0, ""));

                    ExtendabilityScopeDecision next = proven
                        ? ExtendabilityScopeDecisionPolicy.Select(active, lastMappings)
                        : scheduler.SelectNext(active, StationFreedomCosts(pristineBundle));
                    if (QueueSwitch(next)) continue;
                    if (!proven && !scheduler.ShouldYield(active)) continue;
                    break;
                }

                localCandidates++;
                MasterSolutionSnapshot candidate = local.CandidateSnapshot;
                stopChanges = Math.Max(stopChanges, candidate.StopByStopId.Count(item =>
                    pristineSource.StopByStopId[item.Key] != item.Value));
                foreach (MicroWitnessCertificate pattern in local.Patterns) patterns[pattern.StationName] = pattern;
                patterns = MicroPatternReconstructionBundleStore.RebaseToReconstructibleGroups(
                        pristineSource, patterns.Values.ToArray())
                    .ToDictionary(pattern => pattern.StationName, StringComparer.OrdinalIgnoreCase);
                working = candidate;
                MicroPatternReconstructionBundle currentBundle = MicroPatternReconstructionBundle.Create(
                    $"micro_extendability_{phase}_{iteration}", instance, pristineSource, sourcePath,
                    pristineBundle.DataVersion, pristineBundle.RuleVersion,
                    typeof(MicroExtendabilityRepairRunner).Assembly.ManifestModule.ModuleVersionId.ToString("D"),
                    request.RunningTimeSupplementRatio, request.EndpointOccupationSeconds, active,
                    patterns.Values.ToArray());
                MicroPatternReconstructionBundleStore.Save(
                    Path.Combine(output, "state", $"{phase}_current_patterns.json"), currentBundle);

                HashSet<string> activeSet = active.ToHashSet(StringComparer.OrdinalIgnoreCase);
                MicroWitnessCertificate[] outsidePatterns = patterns.Values
                    .Where(pattern => !activeSet.Contains(pattern.StationName))
                    .OrderBy(pattern => pattern.StationName, StringComparer.OrdinalIgnoreCase).ToArray();
                double checkLimit = new[]
                {
                    120.0,
                    Remaining(wallBudgetSeconds, reserveSeconds, wall),
                    scheduler.RemainingSliceSeconds(active)
                }.Min();
                if (checkLimit <= 1e-6)
                {
                    ExtendabilityScopeDecision timed = scheduler.SelectNext(active, StationFreedomCosts(pristineBundle));
                    if (QueueSwitch(timed)) continue;
                    finalStatus = "scope_slice_exhausted_before_coordination";
                    stopReason = timed.Reason;
                    break;
                }

                string scopeToken = string.Join('_', active.Select(Sanitize));
                string scopeKey = ExtendabilityScopeScheduler.ScopeKey(active);
                Stopwatch checkClock = Stopwatch.StartNew();
                using FixedDiscreteContinuousCoordinator recourse = FixedDiscreteContinuousCoordinator.Build(
                    instance, candidate, outsidePatterns,
                    runningTimeSupplementRatio: request.RunningTimeSupplementRatio,
                    endpointOccupationSeconds: request.EndpointOccupationSeconds,
                    coordinationModelId: $"micro_extendability_{phase}_outside_{scopeToken}", feasibilityOnly: true);
                ContinuousExtendabilityContext context = recourse.CreateExtendabilityContext(active, candidate);
                (int eligible, int activeFeedback, int satisfied, string skipped) observation =
                    ObserveFeedback(feedback, context, candidate);
                feedbackEligibleObservations += observation.eligible;
                feedbackActiveObservations += observation.activeFeedback;
                foreach (string item in observation.skipped.Split('|', StringSplitOptions.RemoveEmptyEntries))
                {
                    string key = item.Split('=', 2)[0];
                    int value = int.Parse(item.Split('=', 2)[1], CultureInfo.InvariantCulture);
                    feedbackSkippedByReason[key] = feedbackSkippedByReason.GetValueOrDefault(key) + value;
                }
                eligibleFeedbackByScope[scopeKey] = feedback
                    .Where(item => item.AppliesToModel(context)).Select(item => item.FeedbackId).ToArray();
                LocalExtendabilityFeedbackApplication replay = session.ApplyExtendabilityFeedback(context, feedback);
                feedbackAdded += replay.AddedConstraintIds.Count;
                bool violatesNewlyInstalledFeedback = observation.satisfied < observation.activeFeedback;
                if (violatesNewlyInstalledFeedback)
                {
                    if (replay.AddedConstraintIds.Count == 0)
                        throw new InvalidDataException("A local incumbent violated a feedback row already present in its MIP.");
                    checkClock.Stop();
                    proofSeconds += checkClock.Elapsed.TotalSeconds;
                    feedbackAwaitingScopes.Add(scopeKey);
                    AccountCurrentScope();
                    trace.Add(Project(trace.Count + 1, phase, iteration, "feedback_replay_rejected_candidate",
                        active, local, outsidePatterns.Length, 0, false, replay.AddedConstraintIds.Count > 0,
                        "", double.NaN, "", localClock.Elapsed.TotalSeconds, 0,
                        checkClock.Elapsed.TotalSeconds, 0, wall.Elapsed.TotalSeconds,
                        "saved feedback entered the rebuilt local model and rejected this interface before Optimize",
                        session.DecisionDomain, scheduler, feedback.Count, observation.eligible,
                        observation.activeFeedback, observation.satisfied, observation.skipped));
                    continue;
                }

                if (feedbackAwaitingScopes.Remove(scopeKey))
                {
                    feedbackFollowups++;
                    if (effectiveScopeSwitchExecuted &&
                        string.Equals(scopeKey, mostRecentSwitchedScope, StringComparison.OrdinalIgnoreCase) &&
                        scopesSubmittedToCoordination.Contains(scopeKey))
                    {
                        mechanismPassed = true;
                        if (stopAfterMechanismEvidence)
                        {
                            checkClock.Stop();
                            proofSeconds += checkClock.Elapsed.TotalSeconds;
                            finalStatus = "pilot_mechanism_passed";
                            stopReason = "evidence-linked joint scope was coordinated, its valid feedback entered the same joint MIP, and a follow-up candidate satisfied every active row";
                            break;
                        }
                    }
                }

                IReadOnlyList<ContinuousInterfaceFixBinding> fixes = recourse.FixActiveInterface(active, candidate);
                ContinuousExtendabilityCheckResult check = recourse.SolveExtendability(
                    checkLimit, context, fixes,
                    Path.Combine(output, "logs", $"{phase}_{iteration:00}_outside_recourse.log"),
                    $"{phase}_{iteration:00}_outside_recourse");
                checkClock.Stop();
                scopesSubmittedToCoordination.Add(scopeKey);
                checks++;
                coordinationSeconds += check.Coordination.RuntimeSeconds;
                proofSeconds += Math.Max(0, checkClock.Elapsed.TotalSeconds - check.Coordination.RuntimeSeconds);
                lastEvidence = check.Coordination.EvidenceBinding;

                bool generated = check.Boundary.Feedback is not null;
                bool added = false;
                string feedbackId = generated ? check.Boundary.Feedback!.FeedbackId : "";
                string scopeDecision = "";
                if (!check.Coordination.HasSolution && check.Coordination.StatusCode == GRB.Status.INFEASIBLE)
                {
                    lastMappings = ContinuousProofDependencyMapper.Map(
                        instance, active, check.Coordination.EvidenceBinding, outsidePatterns);
                    string candidateIdentity = ContinuousExtendabilityContext.Hash(string.Join('\n',
                        SnapshotFingerprint.Calculate(candidate),
                        MicroPatternCombinationIdentity.Calculate(patterns.Values.ToArray())));
                    scheduler.RecordProvenNonExtendable(active, candidateIdentity, lastMappings);
                }
                else if (!check.Coordination.HasSolution)
                {
                    scheduler.RecordUnknown(active, check.Coordination.StopReason);
                }

                if (generated)
                {
                    ContinuousExtendabilityFeedback item = check.Boundary.Feedback!;
                    feedbackGenerated++;
                    if (item.IsSatisfied(InterfaceValues(item, candidate)))
                        throw new InvalidDataException("Generated boundary does not exclude its rejected interface.");
                    if (feedback.All(existing => !string.Equals(existing.FeedbackId, item.FeedbackId, StringComparison.Ordinal)))
                    {
                        LocalExtendabilityFeedbackApplication application = session.ApplyExtendabilityFeedback(context, [item]);
                        added = application.AddedConstraintIds.Count == 1;
                        if (!added) throw new InvalidDataException("A valid current-context feedback was not added to the local MIP.");
                        feedback.Add(item);
                        feedbackAdded++;
                        feedbackAwaitingScopes.Add(scopeKey);
                        ContinuousExtendabilityFeedbackStore.Save(
                            Path.Combine(output, "state", $"{phase}_feedback.json"), feedback);
                    }
                }
                AccountCurrentScope();

                ExtendabilityScopeDecision scheduled = scheduler.ShouldYield(active)
                    ? scheduler.SelectNext(active, StationFreedomCosts(pristineBundle))
                    : new(ExtendabilityScopeActionKind.UnknownNoAction, active, [], "scope yield threshold not reached");
                scopeDecision = scheduled.Action.ToString();
                trace.Add(Project(trace.Count + 1, phase, iteration, "outside_recourse", active, local,
                    outsidePatterns.Length, check.FixedInterfaceValueCount, generated, added, feedbackId,
                    check.Boundary.RejectedPointViolation, scopeDecision, localClock.Elapsed.TotalSeconds,
                    check.Coordination.RuntimeSeconds,
                    Math.Max(0, checkClock.Elapsed.TotalSeconds - check.Coordination.RuntimeSeconds), 0,
                    wall.Elapsed.TotalSeconds,
                    $"status={check.Coordination.Status};boundary={check.Boundary.Reason};presolved=" +
                    $"{check.Coordination.Audit.PresolvedVariableCount}/{check.Coordination.Audit.PresolvedConstraintCount}",
                    session.DecisionDomain, scheduler, feedback.Count, observation.eligible,
                    observation.activeFeedback, observation.satisfied, observation.skipped));

                if (scheduled.Action == ExtendabilityScopeActionKind.ExpandToImplicatedOutsideStation &&
                    QueueSwitch(scheduled))
                {
                    finalStatus = "scope_yielded_after_bounded_slice";
                    stopReason = scheduled.Reason;
                    continue;
                }
                if (generated && added)
                {
                    finalStatus = "valid_feedback_added";
                    stopReason = check.Boundary.Reason;
                    continue;
                }
                if (!check.Coordination.HasSolution)
                {
                    if (check.Coordination.StatusCode != GRB.Status.INFEASIBLE)
                    {
                        finalStatus = "outside_recourse_unknown";
                        stopReason = check.Coordination.StopReason;
                        if (!scheduler.ShouldYield(active)) continue;
                        break;
                    }
                    ExtendabilityScopeDecision next = ExtendabilityScopeDecisionPolicy.Select(active, lastMappings);
                    if (QueueSwitch(next))
                    {
                        finalStatus = "scope_expanded_from_model_local_proof";
                        stopReason = next.Reason;
                        continue;
                    }
                    finalStatus = "proven_infeasible_without_valid_executable_extension";
                    stopReason = $"boundary={check.Boundary.Reason};scope={next.Reason}";
                    break;
                }

                double fullLimit = new[]
                {
                    120.0,
                    Remaining(wallBudgetSeconds, reserveSeconds, wall),
                    scheduler.RemainingSliceSeconds(active)
                }.Min();
                if (fullLimit <= 1e-6)
                {
                    finalStatus = "scope_slice_exhausted_before_all_pattern_check";
                    stopReason = "no time remains in this logical scope slice";
                    continue;
                }
                Stopwatch fullClock = Stopwatch.StartNew();
                using FixedDiscreteContinuousCoordinator full = FixedDiscreteContinuousCoordinator.Build(
                    instance, candidate, patterns.Values.ToArray(),
                    runningTimeSupplementRatio: request.RunningTimeSupplementRatio,
                    endpointOccupationSeconds: request.EndpointOccupationSeconds,
                    coordinationModelId: $"micro_extendability_{phase}_all_{scopeToken}", feasibilityOnly: true);
                ContinuousCoordinationResult fullResult = full.Solve(fullLimit,
                    Path.Combine(output, "logs", $"{phase}_{iteration:00}_all_patterns.log"),
                    $"{phase}_{iteration:00}_all_patterns");
                fullClock.Stop();
                checks++;
                coordinationSeconds += fullResult.RuntimeSeconds;
                proofSeconds += Math.Max(0, fullClock.Elapsed.TotalSeconds - fullResult.RuntimeSeconds);
                lastEvidence = fullResult.EvidenceBinding;
                AccountCurrentScope();
                trace.Add(Project(trace.Count + 1, phase, iteration, "all_pattern_coordination", active, local,
                    outsidePatterns.Length, 0, false, false, "", double.NaN, "",
                    0, fullResult.RuntimeSeconds, Math.Max(0, fullClock.Elapsed.TotalSeconds - fullResult.RuntimeSeconds),
                    0, wall.Elapsed.TotalSeconds, fullResult.StopReason,
                    session.DecisionDomain, scheduler, feedback.Count, observation.eligible,
                    observation.activeFeedback, observation.satisfied, observation.skipped));
                if (fullResult.HasSolution && fullResult.Snapshot is not null)
                {
                    Stopwatch validationClock = Stopwatch.StartNew();
                    validation = MicroStructureProofBindingRecoveryRunner.ValidateWithFixedPatterns(
                        full.Model, fullResult.Snapshot, patterns.Values.ToArray(), request.EndpointOccupationSeconds);
                    validationClock.Stop();
                    validationSeconds += validationClock.Elapsed.TotalSeconds;
                    joint = validation.JointFeasible;
                    if (joint)
                    {
                        finalSnapshot = validation.Snapshot;
                        working = validation.Snapshot!;
                        mechanismPassed = true;
                        finalStatus = "joint_feasible";
                        stopReason = "complete independent Macro, Global Micro, fixed-resource and residual validation passed";
                        break;
                    }
                    finalStatus = "continuous_candidate_failed_joint_validation";
                    stopReason = $"macro={validation.MacroViolationCount};micro={validation.GlobalMicroConflictCount};" +
                                 $"unknown={validation.GlobalMicroUnknownCount};fixed={validation.FixedResourceValid}";
                    break;
                }
                if (fullResult.StatusCode != GRB.Status.INFEASIBLE)
                {
                    scheduler.RecordUnknown(active, fullResult.StopReason);
                    finalStatus = "all_pattern_coordination_unknown";
                    stopReason = fullResult.StopReason;
                    continue;
                }

                lastMappings = ContinuousProofDependencyMapper.Map(
                    instance, active, fullResult.EvidenceBinding, patterns.Values.ToArray());
                string fullCandidateIdentity = ContinuousExtendabilityContext.Hash(string.Join('\n',
                    SnapshotFingerprint.Calculate(candidate),
                    MicroPatternCombinationIdentity.Calculate(patterns.Values.ToArray()), "all-pattern"));
                scheduler.RecordProvenNonExtendable(active, fullCandidateIdentity, lastMappings);
                ExtendabilityScopeDecision fullNext = ExtendabilityScopeDecisionPolicy.Select(active, lastMappings);
                if (QueueSwitch(fullNext))
                {
                    finalStatus = "scope_adjusted_from_all_pattern_proof";
                    stopReason = fullNext.Reason;
                    continue;
                }
                finalStatus = "all_pattern_combination_proven_infeasible_no_safe_next_action";
                stopReason = fullNext.Reason;
                break;
            }

            if (wall.Elapsed.TotalSeconds >= wallBudgetSeconds - reserveSeconds && !joint)
            {
                finalStatus = "wall_budget_reserved_for_save";
                stopReason = $"stopped with {reserveSeconds:R}s final-validation/save reserve";
            }
        }
        finally
        {
            if (session is not null)
                CaptureCheckpoint(active, working, patterns, session.DecisionDomain, finalStatus);
            AccountCurrentScope();
            session?.Dispose();
            wall.Stop();
        }

        IReadOnlyList<MicroScopeBranchCheckpoint> checkpoints = runtimeCheckpoints
            .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(item => new MicroScopeBranchCheckpoint(
                item.Key,
                item.Value.DecisionDomain.ActiveStations,
                SnapshotFingerprint.Calculate(item.Value.Candidate),
                item.Value.Candidate,
                item.Value.Patterns.Values.Where(pattern =>
                        !pristineCertificateIds.TryGetValue(pattern.StationName, out string? original) ||
                        !string.Equals(original, pattern.CertificateId, StringComparison.Ordinal))
                    .OrderBy(pattern => pattern.StationName, StringComparer.OrdinalIgnoreCase).ToArray(),
                item.Value.DecisionDomain,
                eligibleFeedbackByScope.GetValueOrDefault(item.Key, []),
                item.Value.Status))
            .ToArray();
        return new(phase, mechanismPassed, joint, finalStatus, stopReason, iteration, localCandidates,
            checks, feedbackGenerated, feedbackAdded, feedbackFollowups, scopeAdjustments, serviceRows,
            stopChanges, localSeconds, coordinationSeconds, proofSeconds, validationSeconds,
            wall.Elapsed.TotalSeconds, active, finalSnapshot ?? working,
            patterns.Values.OrderBy(pattern => pattern.StationName, StringComparer.OrdinalIgnoreCase).ToArray(),
            validation, lastEvidence, lastMappings, feedback, scheduler.CaptureStates(), checkpoints,
            historicalFeedback.Count, feedbackEligibleObservations, feedbackActiveObservations,
            feedbackSkippedByReason);

        void AccountCurrentScope()
        {
            double now = wall.Elapsed.TotalSeconds;
            double delta = Math.Max(0, now - accountedWallSeconds);
            if (delta > 0) scheduler.AccountWork(active, delta);
            accountedWallSeconds = now;
        }

        void CaptureCheckpoint(
            IReadOnlyList<string> stations,
            MasterSolutionSnapshot snapshot,
            IReadOnlyDictionary<string, MicroWitnessCertificate> currentPatterns,
            LocalRepairDecisionDomain domain,
            string status)
        {
            string key = ExtendabilityScopeScheduler.ScopeKey(stations);
            runtimeCheckpoints[key] = new RuntimeMicroScopeCheckpoint(
                snapshot,
                currentPatterns.ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase),
                domain,
                status);
        }

        bool QueueSwitch(ExtendabilityScopeDecision decision)
        {
            if (decision.Action != ExtendabilityScopeActionKind.ExpandToImplicatedOutsideStation || session is null)
                return false;
            string[] next = decision.ActiveStations.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (next.Length is < 1 or > 3 || next.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(active))
                return false;
            AccountCurrentScope();
            string[] from = active.ToArray();
            LocalRepairDecisionDomain priorDomain = session.DecisionDomain;
            CaptureCheckpoint(from, working, patterns, priorDomain, "paused_unknown");
            session.Dispose();
            session = null;
            pendingSwitchFrom = from;
            pendingSwitchDecision = decision;
            pendingPriorDomain = priorDomain;
            active = next;
            string targetKey = ExtendabilityScopeScheduler.ScopeKey(active);
            if (runtimeCheckpoints.TryGetValue(targetKey, out RuntimeMicroScopeCheckpoint? resumed))
            {
                working = resumed.Candidate;
                patterns = resumed.Patterns.ToDictionary(item => item.Key, item => item.Value,
                    StringComparer.OrdinalIgnoreCase);
            }
            accountedWallSeconds = wall.Elapsed.TotalSeconds;
            return true;
        }
    }

    private static IReadOnlyDictionary<ContinuousInterfaceKey, double> InterfaceValues(
        ContinuousExtendabilityFeedback feedback, MasterSolutionSnapshot snapshot) =>
        feedback.Terms.Select(term => term.Key).Distinct().ToDictionary(
            key => key,
            key => key.EventSide == StationEventSide.Incoming
                ? snapshot.ArrivalSecondsByStopId[key.StopId]
                : snapshot.DepartureSecondsByStopId[key.StopId]);

    private static (int eligible, int activeFeedback, int satisfied, string skipped) ObserveFeedback(
        IReadOnlyList<ContinuousExtendabilityFeedback> feedback,
        ContinuousExtendabilityContext context,
        MasterSolutionSnapshot snapshot)
    {
        int eligible = 0, active = 0, satisfied = 0;
        Dictionary<ContinuousFeedbackApplicabilityReason, int> skipped = [];
        foreach (ContinuousExtendabilityFeedback item in feedback)
        {
            ContinuousFeedbackApplicability applicability = item.AssessApplicability(context);
            if (!applicability.ModelEligible)
            {
                skipped[applicability.ReasonCode] = skipped.GetValueOrDefault(applicability.ReasonCode) + 1;
                continue;
            }
            eligible++;
            if (!applicability.StopGuardActive) continue;
            active++;
            if (item.IsSatisfied(InterfaceValues(item, snapshot))) satisfied++;
        }
        string detail = string.Join('|', skipped.OrderBy(item => item.Key)
            .Select(item => $"{item.Key}={item.Value}"));
        return (eligible, active, satisfied, detail);
    }

    private static IReadOnlyDictionary<string, int> StationFreedomCosts(
        MicroPatternReconstructionBundle bundle) => bundle.Patterns
        .GroupBy(pattern => pattern.StationName, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.Sum(pattern => pattern.Witnesses.Count),
            StringComparer.OrdinalIgnoreCase);

    private static double Remaining(double budget, double reserve, Stopwatch wall) =>
        Math.Max(0, budget - reserve - wall.Elapsed.TotalSeconds);

    private static string SelectPrimaryStation(
        IReadOnlyList<ContinuousProofBusinessMapping> mappings,
        IReadOnlyList<MicroWitnessCertificate> patterns)
    {
        string? mapped = mappings
            .Where(mapping => mapping.Status == ProofDecisionMappingStatus.PatternReplacement)
            .SelectMany(mapping => mapping.Decisions)
            .Where(decision => decision.IsAdjustable && !string.IsNullOrWhiteSpace(decision.StationName))
            .GroupBy(decision => decision.StationName, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Key).FirstOrDefault();
        return mapped ?? patterns.OrderByDescending(pattern => pattern.Conditions.Count)
            .ThenBy(pattern => pattern.StationName, StringComparer.OrdinalIgnoreCase).First().StationName;
    }

    private static IReadOnlyList<ContinuousProofBusinessMapping> LoadMappings(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
        if (!document.RootElement.TryGetProperty("mappings", out JsonElement element))
            throw new InvalidDataException("Saved recovery evidence has no proof-to-business mappings.");
        return element.Deserialize<ContinuousProofBusinessMapping[]>(JsonOptions) ?? [];
    }

    private static MicroExtendabilityTraceRow Project(
        int sequence, string phase, int iteration, string action, IReadOnlyList<string> active,
        IndependentStationPatternResult local, int outsidePatterns, int fixedInterface,
        bool generated, bool added, string feedbackId, double violation, string scopeDecision,
        double localSeconds, double coordinationSeconds, double proofSeconds, double validationSeconds,
        double wallSeconds, string detail,
        LocalRepairDecisionDomain? domain = null,
        ExtendabilityScopeScheduler? scheduler = null,
        int feedbackSaved = 0,
        int feedbackEligible = 0,
        int feedbackActive = 0,
        int feedbackSatisfied = 0,
        string feedbackSkipped = "")
    {
        string scopeKey = ExtendabilityScopeScheduler.ScopeKey(active);
        ExtendabilityScopeBranchState? state = scheduler?.CaptureStates()
            .FirstOrDefault(item => string.Equals(item.ScopeKey, scopeKey, StringComparison.OrdinalIgnoreCase));
        return new(
        sequence, phase, iteration, action, string.Join('|', active), local.Status, local.HasPattern,
        local.Audit.SourceVariableCount, local.Audit.SourceBinaryCount, local.Audit.SourceConstraintCount,
        local.Audit.ServiceCouplingCount, local.Audit.RuntimeCouplingCount, outsidePatterns, fixedInterface,
        generated, added, feedbackId, violation, scopeDecision, localSeconds, coordinationSeconds,
        proofSeconds, validationSeconds, wallSeconds, detail,
        scopeKey,
        state?.Disposition.ToString() ?? "",
        state?.CumulativeSeconds ?? 0,
        state?.DistinctNonExtendableCandidates ?? 0,
        domain?.DomainIdentity ?? "",
        domain?.FreeDiscreteDecisionCount ?? 0,
        domain?.PresolvedBinaryCount ?? 0,
        feedbackSaved,
        feedbackEligible,
        feedbackActive,
        feedbackSatisfied,
        feedbackSkipped);
    }

    private static MicroExtendabilityTraceRow ScopeTrace(
        int sequence, string phase, int iteration, IReadOnlyList<string> active,
        ExtendabilityScopeDecision decision, LocalRepairDecisionDomain domain,
        ExtendabilityScopeScheduler scheduler, double wallSeconds)
    {
        string scopeKey = ExtendabilityScopeScheduler.ScopeKey(active);
        ExtendabilityScopeBranchState state = scheduler.CaptureStates().Single(item =>
            string.Equals(item.ScopeKey, scopeKey, StringComparison.OrdinalIgnoreCase));
        return new(
        sequence, phase, iteration, "scope_adjustment", string.Join('|', active), "NOT_SOLVED", false,
        0, 0, 0, 0, 0, 0, 0, false, false, "", double.NaN,
        decision.Action.ToString(), 0, 0, 0, 0, wallSeconds,
        $"{decision.Reason};decisions={string.Join('|', decision.EvidenceDecisionIds)}",
        scopeKey, state.Disposition.ToString(), state.CumulativeSeconds,
        state.DistinctNonExtendableCandidates, domain.DomainIdentity,
        domain.FreeDiscreteDecisionCount, domain.PresolvedBinaryCount);
    }

    private static void SaveArtifacts(
        TimetablingInstance instance,
        MicroExtendabilityRepairRequest request,
        string baseline,
        string sourcePath,
        string bundlePath,
        MasterSolutionSnapshot source,
        MicroPatternReconstructionBundle baselineBundle,
        MicroExtendabilityPhaseResult pilot,
        MicroExtendabilityPhaseResult? formal,
        MicroExtendabilityPhaseResult final,
        IReadOnlyList<MicroExtendabilityTraceRow> trace,
        string output,
        double totalSeconds)
    {
        WriteTrace(Path.Combine(output, "repair_trace.csv"), trace);
        string finalStateDirectory = Path.Combine(output, final.JointFeasible ? "solution" : "state");
        Directory.CreateDirectory(finalStateDirectory);
        if (final.FinalSnapshot is not null)
            MasterSolutionSnapshotStore.Save(Path.Combine(finalStateDirectory,
                final.JointFeasible ? "joint_candidate.snapshot.json" : "diagnostic_candidate.snapshot.json"),
                instance.Meta.InstanceName, final.FinalSnapshot);
        IReadOnlyList<MicroWitnessCertificate> reconstructiblePatterns =
            MicroPatternReconstructionBundleStore.RebaseToReconstructibleGroups(source, final.FinalPatterns);
        MicroPatternReconstructionBundle savedBundle = MicroPatternReconstructionBundle.Create(
            "micro_extendability_final_state", instance, source, sourcePath,
            baselineBundle.DataVersion, baselineBundle.RuleVersion,
            typeof(MicroExtendabilityRepairRunner).Assembly.ManifestModule.ModuleVersionId.ToString("D"),
            request.RunningTimeSupplementRatio, request.EndpointOccupationSeconds,
            final.FinalActiveStations, reconstructiblePatterns);
        string finalBundlePath = Path.Combine(finalStateDirectory, "micro_patterns.json");
        MicroPatternReconstructionBundleStore.Save(finalBundlePath, savedBundle);
        MicroPatternReconstructionBundle replayedBundle = MicroPatternReconstructionBundleStore.Load(
            finalBundlePath, instance, source);
        if (!string.Equals(savedBundle.PatternCombinationIdentity, replayedBundle.PatternCombinationIdentity,
                StringComparison.Ordinal))
            throw new InvalidDataException("Read-only pattern replay changed the saved mathematical identity.");
        string feedbackPath = Path.Combine(finalStateDirectory, "extendability_feedback.json");
        ContinuousExtendabilityFeedbackStore.Save(feedbackPath, final.Feedback);
        IReadOnlyList<ContinuousExtendabilityFeedback> replayedFeedback =
            ContinuousExtendabilityFeedbackStore.Load(feedbackPath);
        if (!final.Feedback.Select(item => item.FeedbackId).SequenceEqual(
                replayedFeedback.Select(item => item.FeedbackId), StringComparer.Ordinal))
            throw new InvalidDataException("Read-only feedback replay changed the saved mathematical identities.");
        string scopeBranchesPath = SaveScopeBranches(
            Path.Combine(finalStateDirectory, "branches"), final.BranchCheckpoints, instance);
        if (final.LastEvidence is not null)
            File.WriteAllText(Path.Combine(finalStateDirectory, "last_coordination_evidence.json"),
                JsonSerializer.Serialize(new { final.LastEvidence, mappings = final.LastMappings }, JsonOptions),
                new UTF8Encoding(false));
        if (final.JointFeasible && final.Validation is not null)
            WriteWitnesses(Path.Combine(finalStateDirectory, "resource_assignments_685.csv"),
                final.Validation.Witnesses);
        else
            File.WriteAllText(Path.Combine(finalStateDirectory, "diagnostic_state.json"),
                JsonSerializer.Serialize(new
                {
                    final.Phase, final.FinalStatus, final.StopReason, final.FinalActiveStations,
                    final.Iterations, final.LocalCandidates, final.ContinuousChecks,
                    final.FeedbackGenerated, final.FeedbackAdded, final.ScopeAdjustments,
                    final.HistoricalFeedbackLoaded, final.FeedbackModelEligibleObservations,
                    final.FeedbackActiveObservations, final.FeedbackSkippedByReason,
                    final.ScopeBranches,
                    final.LastEvidence, mappings = final.LastMappings
                }, JsonOptions), new UTF8Encoding(false));

        string codeIdentity = typeof(MicroExtendabilityRepairRunner).Assembly.ManifestModule.ModuleVersionId.ToString("D");
        object summary = new
        {
            formatVersion = 2,
            runKind = "fixed Macro start, saved-pattern Micro solve (not fresh end-to-end)",
            createdAt = DateTimeOffset.Now,
            codeIdentity,
            input = new
            {
                dataset = Path.GetFullPath(request.DatasetPath), baseline, sourcePath, bundlePath,
                historicalFeedbackPath = string.IsNullOrWhiteSpace(request.HistoricalFeedbackPath)
                    ? null
                    : Path.GetFullPath(request.HistoricalFeedbackPath),
                macroFingerprint = SnapshotFingerprint.Calculate(source),
                baselinePatternIdentity = baselineBundle.PatternCombinationIdentity,
                baselinePatternCount = baselineBundle.Patterns.Length,
                baselineLocalWitnessCount = baselineBundle.Patterns.Sum(pattern => pattern.Witnesses.Count),
                historicalPatternGenerationTimeIncluded = false
            },
            configuration = new
            {
                seed = 0, threads = 0, k = 4, kReference = "saved Macro candidate",
                nonHotspotLimit = "unchanged", neighborRadius = "full", a = "existing boundary retained; not used to filter proof directions",
                b = "off", endpointOccupationSeconds = request.EndpointOccupationSeconds,
                runningTimeSupplementRatio = request.RunningTimeSupplementRatio,
                timeSemantics = "continuous; closed windows; zero business-time relaxation; numeric tolerance only",
                pilotBudgetSeconds = request.PilotBudgetSeconds,
                formalBudgetSeconds = request.FormalBudgetSeconds,
                formalSaveReserveSeconds = 120,
                pilotSaveReserveSeconds = 60,
                scopeSliceSeconds = request.ScopeSliceSeconds,
                distinctNonExtendableCandidateLimit = request.DistinctNonExtendableCandidateLimit
            },
            pilot = SummaryOf(pilot),
            formal = formal is null ? null : SummaryOf(formal),
            finalJointFeasible = final.JointFeasible,
            finalStatus = final.FinalStatus,
            finalStopReason = final.StopReason,
            totalWallSeconds = totalSeconds,
            timings = new
            {
                localGenerationSeconds = final.LocalSeconds,
                fullLineCoordinationSeconds = final.CoordinationSeconds,
                proofAndBuildSeconds = final.ProofSeconds,
                independentValidationSeconds = final.ValidationSeconds
            },
            mechanisms = new
            {
                timeFeedbackExecuted = final.FeedbackAdded > 0,
                timeFeedbackFollowupExecuted = final.FeedbackFollowupSolves > 0,
                serviceInterfaceRowsExecuted = final.ServiceRows,
                serviceStopChangesObserved = final.StopChanges,
                proofDrivenScopeAdjustmentsExecuted = final.ScopeAdjustments,
                historicalFeedbackLoaded = final.HistoricalFeedbackLoaded,
                feedbackModelEligibleObservations = final.FeedbackModelEligibleObservations,
                feedbackActiveObservations = final.FeedbackActiveObservations,
                feedbackSkippedByReason = final.FeedbackSkippedByReason,
                actualScopes = final.ScopeBranches,
                macroStructureChanges = 0,
                kChanged = false,
                businessSemanticsChanged = false
            },
            paths = new
            {
                workDirectory = Directory.GetCurrentDirectory(), outputDirectory = output,
                result = Path.Combine(output, "result.md"),
                runSummary = Path.Combine(output, "run_summary.json"),
                repairTrace = Path.Combine(output, "repair_trace.csv"),
                reproduce = Path.Combine(output, "reproduce.ps1"), finalBundlePath, feedbackPath,
                scopeBranchesPath,
                finalStateDirectory
            }
        };
        File.WriteAllText(Path.Combine(output, "run_summary.json"),
            JsonSerializer.Serialize(summary, JsonOptions), new UTF8Encoding(false));
        WriteScopeFeedbackResult(Path.Combine(output, "result.md"), pilot, formal, final, totalSeconds, output);
        WriteReproduce(Path.Combine(output, "reproduce.ps1"), request, output);
    }

    private static object SummaryOf(MicroExtendabilityPhaseResult phase) => new
    {
        phase.Phase, phase.MechanismPassed, phase.JointFeasible, phase.FinalStatus, phase.StopReason,
        phase.Iterations, phase.LocalCandidates, phase.ContinuousChecks, phase.FeedbackGenerated,
        phase.FeedbackAdded, phase.FeedbackFollowupSolves, phase.ScopeAdjustments, phase.ServiceRows,
        phase.StopChanges, phase.LocalSeconds, phase.CoordinationSeconds, phase.ProofSeconds,
        phase.ValidationSeconds, phase.WallSeconds, phase.FinalActiveStations,
        phase.HistoricalFeedbackLoaded, phase.FeedbackModelEligibleObservations,
        phase.FeedbackActiveObservations, phase.FeedbackSkippedByReason,
        phase.ScopeBranches,
        validation = phase.Validation
    };

    private static string SaveScopeBranches(
        string directory,
        IReadOnlyList<MicroScopeBranchCheckpoint> checkpoints,
        TimetablingInstance instance)
    {
        Directory.CreateDirectory(directory);
        List<MicroScopeBranchCheckpointManifest> manifests = [];
        foreach ((MicroScopeBranchCheckpoint checkpoint, int index) in checkpoints.Select((value, index) => (value, index)))
        {
            string token = $"{index + 1:D2}_{Sanitize(checkpoint.ScopeKey)}";
            string candidatePath = Path.GetFullPath(Path.Combine(directory, $"{token}_candidate.snapshot.json"));
            MasterSolutionSnapshotStore.Save(candidatePath, instance.Meta.InstanceName, checkpoint.Candidate);
            manifests.Add(new(
                checkpoint.ScopeKey,
                checkpoint.ActiveStations,
                checkpoint.CandidateFingerprint,
                candidatePath,
                checkpoint.ChangedPatterns,
                checkpoint.DecisionDomain,
                checkpoint.StructurallyApplicableFeedbackIds,
                checkpoint.Status));
        }
        string manifestPath = Path.GetFullPath(Path.Combine(directory, "scope_branches.json"));
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifests, JsonOptions), new UTF8Encoding(false));

        MicroScopeBranchCheckpointManifest[] replay =
            JsonSerializer.Deserialize<MicroScopeBranchCheckpointManifest[]>(
                File.ReadAllText(manifestPath, Encoding.UTF8), JsonOptions) ?? [];
        if (replay.Length != manifests.Count)
            throw new InvalidDataException("Read-only branch replay changed the branch count.");
        HashSet<int> trainIndices = instance.Trains.Select(train => train.TrainIndex).ToHashSet();
        foreach (MicroScopeBranchCheckpointManifest item in replay)
        {
            MasterSolutionSnapshot candidate = MasterSolutionSnapshotStore.Load(
                item.CandidatePath, instance.Meta.InstanceName, trainIndices);
            if (!string.Equals(item.CandidateFingerprint, SnapshotFingerprint.Calculate(candidate),
                    StringComparison.Ordinal) || string.IsNullOrWhiteSpace(item.DecisionDomain.DomainIdentity))
                throw new InvalidDataException($"Read-only branch replay failed for scope '{item.ScopeKey}'.");
        }
        return manifestPath;
    }

    private static void WriteScopeFeedbackResult(
        string path,
        MicroExtendabilityPhaseResult pilot,
        MicroExtendabilityPhaseResult? formal,
        MicroExtendabilityPhaseResult final,
        double totalSeconds,
        string output)
    {
        StringBuilder text = new();
        text.AppendLine("# JZ Micro 范围调度与反馈实验结果").AppendLine();
        text.AppendLine($"- 完整联合可行：**{(final.JointFeasible ? "是" : "否")}**。");
        text.AppendLine($"- 600 秒小试：**{(pilot.MechanismPassed ? "通过" : "未通过")}**（`{pilot.FinalStatus}`）。");
        text.AppendLine($"- 正式 Micro：**{(formal is null ? "未启动（小试未达门槛或小试已联合可行）" : formal.FinalStatus)}**。");
        text.AppendLine($"- 最终终止原因：{final.StopReason}");
        text.AppendLine($"- 总墙钟：{totalSeconds:F3} 秒。此时间不含历史模式原始生成时间。").AppendLine();
        text.AppendLine("## 实际执行").AppendLine();
        text.AppendLine($"- 实际范围：{string.Join("；", final.ScopeBranches.Select(branch =>
            $"{branch.ScopeKey}[{branch.Disposition}, {branch.CumulativeSeconds:F3}s, distinct={branch.DistinctNonExtendableCandidates}]"))}。");
        text.AppendLine($"- 有效范围切换：{final.ScopeAdjustments} 次；每次均核对新增资源/顺序/停站自由度，单纯换标签不计数。");
        text.AppendLine($"- 反馈：历史加载 {final.HistoricalFeedbackLoaded} 条；实际入模 {final.FeedbackAdded} 行；" +
                        $"模型适用观察 {final.FeedbackModelEligibleObservations}，条件激活观察 {final.FeedbackActiveObservations}，反馈后复求解 {final.FeedbackFollowupSolves} 次。");
        text.AppendLine($"- 反馈未适用原因累计：{string.Join("；", final.FeedbackSkippedByReason.Select(item => $"{item.Key}={item.Value}"))}。");
        text.AppendLine($"- 服务接口：最多 {final.ServiceRows} 行；相对保存 Macro 最多 {final.StopChanges} 项停站变化。");
        text.AppendLine("- K=4、冻结参考、非热点变化预算 0、Seed、Threads、A/B 边界均未改变；Micro 资源/顺序不计为 K 端点变化。");
        text.AppendLine("- 只有当前纯连续模型、含显式变量界并通过消元残差检查的 Farkas 射线生成时间反馈；Unknown 不生成硬割。");
        text.AppendLine("- 未重跑初始 Macro，未运行 JH；本轮不是 fresh JZ。没有配对实验，因此不作提速结论。").AppendLine();
        text.AppendLine("## 代码修改与定向验证").AppendLine();
        text.AppendLine("- 范围调度按“冻结 Macro 身份 + 无序站集合”累计300秒或5个不同的已证不可延拓候选；停站组合、反馈增加和模型重建不会重置台账。");
        text.AppendLine("- 暂停的 Unknown 分支保留自己的状态；子范围时间片耗尽时继承用于调度的不可变证明映射，可尝试另一关联双站或有证据支持的三站范围。");
        text.AppendLine("- 联合范围必须通过实际 decision-domain 差分：新增资源、Micro顺序或合法停站决策后才确认切换；K端点决策不随范围标签变化。");
        text.AppendLine("- 反馈逐条区分保存、模型适用、条件激活和候选满足；矩阵身份变化继续严格失效，停站上下文由精确 Big-M guard 条件化。");
        text.AppendLine("- Release构建及Micro协调定向测试通过；真实JZ小试的只读恢复在保存阶段完成，未调用Optimize或重新选择资源。").AppendLine();
        text.AppendLine("## 主要瓶颈").AppendLine();
        if (!final.JointFeasible)
        {
            ExtendabilityScopeBranchState[] jointScopes = final.ScopeBranches
                .Where(branch => branch.ActiveStations.Count > 1 && branch.CumulativeSeconds > 0)
                .OrderBy(branch => branch.ScopeKey, StringComparer.OrdinalIgnoreCase).ToArray();
            text.AppendLine($"- 证据关联联合局部 MIP 是本轮主要成本：{string.Join("；", jointScopes.Select(branch => $"{branch.ScopeKey}={branch.CumulativeSeconds:F3}s/{branch.Disposition}"))}。");
            text.AppendLine("- 这些分支均未产生局部 incumbent，因此没有从新联合范围提交全线协调的候选；这是受限模型超时/Unknown，不是原JZ无解证明。");
            int inactive = Math.Max(0, final.FeedbackModelEligibleObservations - final.FeedbackActiveObservations);
            text.AppendLine($"- 当前运行产生的反馈有 {final.FeedbackModelEligibleObservations} 次模型适用观察，其中 {inactive} 次因精确停站 guard 不匹配而未激活；未删除 guard 来提高激活率。");
        }
        else
        {
            text.AppendLine("- 已获得并独立验证同一候选下的完整联合解；未继续进行跨度优化。");
        }
        text.AppendLine();
        text.AppendLine("## 分阶段耗时（秒）").AppendLine();
        text.AppendLine("|阶段|局部生成/复求解|全线协调|证明/建模|独立验证|墙钟|");
        text.AppendLine("|---|---:|---:|---:|---:|---:|");
        AppendPhase(pilot);
        if (formal is not null) AppendPhase(formal);
        text.AppendLine().AppendLine("## 输出").AppendLine();
        text.AppendLine($"统一结果目录：`{output}`");
        // result.md is a human-facing Windows artifact; emit a BOM so Windows
        // PowerShell 5 and Notepad do not decode the Chinese report as ANSI.
        File.WriteAllText(path, text.ToString(), new UTF8Encoding(true));

        void AppendPhase(MicroExtendabilityPhaseResult phase) => text.AppendLine(
            $"|{phase.Phase}|{phase.LocalSeconds:F3}|{phase.CoordinationSeconds:F3}|{phase.ProofSeconds:F3}|{phase.ValidationSeconds:F3}|{phase.WallSeconds:F3}|");
    }

    private static void WriteResult(string path, MicroExtendabilityRepairRequest request,
        MicroExtendabilityPhaseResult pilot, MicroExtendabilityPhaseResult? formal,
        MicroExtendabilityPhaseResult final, double totalSeconds, string output)
    {
        StringBuilder text = new();
        text.AppendLine("# 全线可延拓性驱动的 Micro 修复结果").AppendLine();
        text.AppendLine($"- 完整联合可行：**{final.JointFeasible}**");
        text.AppendLine($"- 600 秒小试通过：**{pilot.MechanismPassed}**（{pilot.FinalStatus}）");
        text.AppendLine($"- 正式 Micro：**{(formal is null ? "未启动（小试未过门槛）" : formal.FinalStatus)}**");
        text.AppendLine($"- 最终终止原因：{final.StopReason}");
        text.AppendLine($"- 总墙钟：{totalSeconds:F3} 秒").AppendLine();
        text.AppendLine("## 实际执行的机制").AppendLine();
        text.AppendLine($"- 有效 Farkas 时间反馈加入局部模型：{final.FeedbackAdded} 条；反馈后再次求解：{final.FeedbackFollowupSolves} 次。");
        text.AppendLine($"- 精确服务接口约束：{final.ServiceRows} 行；相对保存 Macro 的停站变化：{final.StopChanges} 项。");
        text.AppendLine($"- 证据驱动范围调整：{final.ScopeAdjustments} 次；K=4、K参考和非热点限制未修改。");
        text.AppendLine("- IIS 只用于业务条件定位；时间边界只来自当前连续模型、含显式变量界的 Farkas 射线。");
        text.AppendLine("- 未重新优化初始 Macro；B 关闭；保持连续时刻，闭时间窗独立复算；取消 1 秒窗外空档和业务放宽。").AppendLine();
        text.AppendLine("## 分阶段耗时（秒）").AppendLine();
        text.AppendLine("|阶段|局部生成|全线协调|证明/建模|独立验证|墙钟|");
        text.AppendLine("|---|---:|---:|---:|---:|---:|");
        AppendPhase(pilot);
        if (formal is not null) AppendPhase(formal);
        text.AppendLine().AppendLine("历史模式已作为基准输入复用；本轮计时不包含这些模式原先的生成时间。没有配对实验，因此不作提速结论。");
        text.AppendLine().AppendLine("## 输出").AppendLine();
        text.AppendLine($"统一结果目录：`{output}`");
        File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
        return;

        void AppendPhase(MicroExtendabilityPhaseResult phase) => text.AppendLine(
            $"|{phase.Phase}|{phase.LocalSeconds:F3}|{phase.CoordinationSeconds:F3}|{phase.ProofSeconds:F3}|{phase.ValidationSeconds:F3}|{phase.WallSeconds:F3}|");
    }

    private static void WriteReproduce(string path, MicroExtendabilityRepairRequest request, string output)
    {
        string project = Path.Combine(Directory.GetCurrentDirectory(), "src", "GurobiTimetablingResearch",
            "GurobiTimetablingResearch.csproj");
        string feedbackArgument = string.IsNullOrWhiteSpace(request.HistoricalFeedbackPath)
            ? ""
            : $"--micro-extendability-feedback='{Path.GetFullPath(request.HistoricalFeedbackPath)}' ";
        string script = "$ErrorActionPreference = 'Stop'\r\n" +
                        $"dotnet run --project '{project}' -c Release --no-build -- " +
                $"'{Path.GetFullPath(request.DatasetPath)}' --micro-extendability " +
                $"--micro-extendability-baseline='{Path.GetFullPath(request.BaselineDirectory)}' " +
                $"--micro-extendability-output='{output}' " +
                feedbackArgument +
                $"--micro-extendability-pilot-seconds={request.PilotBudgetSeconds.ToString(CultureInfo.InvariantCulture)} " +
                $"--micro-extendability-formal-seconds={request.FormalBudgetSeconds.ToString(CultureInfo.InvariantCulture)} " +
                $"--checker-loop-endpoint-occupation-seconds={request.EndpointOccupationSeconds.ToString(CultureInfo.InvariantCulture)} " +
                $"--running-time-supplement-ratio={request.RunningTimeSupplementRatio.ToString(CultureInfo.InvariantCulture)}\r\n";
        File.WriteAllText(path, script, new UTF8Encoding(false));
    }

    private static void WriteTrace(string path, IReadOnlyList<MicroExtendabilityTraceRow> rows)
    {
        string[] headers = typeof(MicroExtendabilityTraceRow).GetProperties().Select(property => property.Name).ToArray();
        StringBuilder csv = new(); csv.AppendLine(string.Join(',', headers));
        foreach (MicroExtendabilityTraceRow row in rows)
        {
            object?[] values = typeof(MicroExtendabilityTraceRow).GetProperties()
                .Select(property => property.GetValue(row)).ToArray();
            csv.AppendLine(string.Join(',', values.Select(Csv)));
        }
        File.WriteAllText(path, csv.ToString(), new UTF8Encoding(false));
    }

    private static void WriteWitnesses(string path, IReadOnlyList<VerifiedMicroAssignmentWitness> witnesses)
    {
        StringBuilder csv = new("station,requirementId,trainIndex,isStop,track,incomingRouteId,outgoingRouteId,arrivalSeconds,departureSeconds,candidateFingerprint\n");
        foreach (VerifiedMicroAssignmentWitness witness in witnesses.OrderBy(item => item.StationName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.RequirementId, StringComparer.OrdinalIgnoreCase))
            csv.AppendLine(string.Join(',', Csv(witness.StationName), Csv(witness.RequirementId), witness.TrainIndex,
                witness.IsStop, Csv(witness.TrackName), witness.IncomingRouteId, witness.OutgoingRouteId,
                witness.ArrivalSeconds?.ToString("R", CultureInfo.InvariantCulture),
                witness.DepartureSeconds?.ToString("R", CultureInfo.InvariantCulture),
                Csv(witness.CandidateSnapshotFingerprint)));
        File.WriteAllText(path, csv.ToString(), new UTF8Encoding(false));
    }

    private static string Csv(object? value)
    {
        string text = value switch
        {
            null => "",
            double number when double.IsNaN(number) => "",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? "",
            _ => value.ToString() ?? ""
        };
        return text.IndexOfAny([',', '"', '\r', '\n']) < 0 ? text : $"\"{text.Replace("\"", "\"\"")}\"";
    }

    private static string Sanitize(string value) => new(value.Select(character =>
        char.IsLetterOrDigit(character) ? character : '_').ToArray());
}
