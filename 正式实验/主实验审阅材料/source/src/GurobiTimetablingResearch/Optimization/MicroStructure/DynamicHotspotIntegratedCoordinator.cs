using System.Diagnostics;
using Gurobi;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

public sealed record DynamicHotspotIntegratedBuildRequest(
    TimetablingInstance Instance,
    MasterSolutionSnapshot Reference,
    DynamicHotspotScopeSelection Scope,
    IReadOnlyList<PromotedMicroComponent> Components,
    IReadOnlyList<MicroWitnessCertificate> RetainedPatterns,
    DynamicHotspotModelGate Gate,
    IReadOnlyList<VerifiedMicroAssignmentWitness>? MipStartWitnesses = null,
    int EndpointOccupationSeconds = StationEndpointOccupationPolicy.DefaultSeconds,
    double RunningTimeSupplementRatio = 0.05,
    int EndpointOrderChangeBudget = 4,
    bool RunPresolveDiagnostics = false);

public sealed record DynamicHotspotIntegratedModelAudit(
    bool GatePassed,
    string GateStatus,
    double BuildSeconds,
    double PresolveSeconds,
    int SourceVariableCount,
    int SourceBinaryCount,
    int SourceConstraintCount,
    int ActiveVariableCount,
    int ActiveBinaryCount,
    int ActiveConstraintCount,
    bool PresolveAvailable,
    bool PresolveProvedInfeasible,
    string PresolveStatus,
    int PresolvedVariableCount,
    int PresolvedActiveBinaryCount,
    int PresolvedConstraintCount,
    int FixedOutsideStopCount,
    int ActiveHotspotStopCount,
    int FixedOriginalDomainHotspotStopCount,
    int FixedOutsideSectionPrecedenceCount,
    int ActiveEndpointPrecedenceCount,
    int FixedOutsideStationEventPrecedenceCount,
    int ActiveStationEventPrecedenceCount,
    int ActiveMaintenanceBinaryCount,
    int ActiveQuotaCategoryBinaryCount,
    int PromotedChoiceVariableCount,
    int PromotedOrderVariableCount,
    int RetainedPatternCount,
    int RetainedPatternConstraintCount,
    int CompatibleActiveRequirementCount,
    int StartedActiveChoiceBinaryCount,
    int StartedActiveOrderBinaryCount,
    int ActiveBinaryWithoutCompatibleParentCount,
    IReadOnlyList<string> FirstActiveStartGaps,
    VerifiedMicroAssignmentMipStartResult MipStart);

internal sealed record DynamicHotspotIntegratedSolveResult(
    bool WasRun,
    int StatusCode,
    string Status,
    double RuntimeSeconds,
    double? FirstIncumbentSeconds,
    bool HasCandidate,
    bool MacroValidated,
    int MacroViolationCount,
    MasterSolutionSnapshot? Candidate,
    IReadOnlyList<VerifiedMicroAssignmentWitness> ActiveAssignments,
    IReadOnlyList<ContinuousProofRow> ProofRows,
    string StopReason,
    string ProofExtractionStrategy = "not_requested",
    double ProofExtractionSeconds = 0.0);

internal sealed record DynamicHotspotIntegratedEvaluatedCandidate(
    string CandidateId,
    DynamicHotspotIntegratedSolveResult Solve,
    StationLevelMicroFeasibilitySlaveResult MicroAnalysis,
    DirectResumeCandidateScore Score,
    double TimetableSpanSeconds);

internal sealed record DynamicHotspotIntegratedBatchSolveResult(
    IReadOnlyList<DynamicHotspotIntegratedEvaluatedCandidate> Candidates,
    DynamicHotspotIntegratedEvaluatedCandidate? Selected,
    DynamicHotspotIntegratedSolveResult? TerminalResult,
    int StructuralDiversityConstraintCount);

public sealed class DynamicHotspotIntegratedCoordinator : IDisposable
{
    private readonly DynamicHotspotIntegratedBuildRequest _request;
    private readonly PrecedenceTrustRegion _trustRegion;

    private DynamicHotspotIntegratedCoordinator(
        DynamicHotspotIntegratedBuildRequest request,
        BasicMilpModel model,
        PrecedenceTrustRegion trustRegion,
        DynamicHotspotIntegratedModelAudit audit)
    {
        _request = request;
        Model = model;
        _trustRegion = trustRegion;
        Audit = audit;
    }

    public BasicMilpModel Model { get; }
    public DynamicHotspotIntegratedModelAudit Audit { get; }
    public PrecedenceTrustRegion TrustRegion => _trustRegion;

    public static DynamicHotspotIntegratedCoordinator Build(DynamicHotspotIntegratedBuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.Scope.IsAccepted)
            throw new InvalidOperationException($"Cannot build a rejected hotspot scope: {request.Scope.Status}");
        if (request.EndpointOccupationSeconds < 0 || request.EndpointOrderChangeBudget < 0)
            throw new ArgumentOutOfRangeException(nameof(request));

        Stopwatch buildClock = Stopwatch.StartNew();
        HashSet<string> activeStations = request.Scope.StationNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> releasedStopIds = request.Scope.StopIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, int> effectiveStopReference = DynamicHotspotOutsideStopPolicy.Resolve(
            request.Reference.StopByStopId,
            activeStations,
            request.RetainedPatterns);
        HashSet<int> evidenceTrains = request.Scope.StopIds
            .Where(request.Instance.StopsById.ContainsKey)
            .Select(stopId => request.Instance.StopsById[stopId].TrainIndex)
            .ToHashSet();
        PrecedenceTrustRegion trustRegion = ValidityFirstEndpointOrderTrustRegionBuilder.Build(
            request.Instance,
            request.Reference,
            evidenceTrains,
            request.EndpointOrderChangeBudget,
            $"dynamic_hotspot::{request.Scope.ProofIdentity}");

        BasicMilpModel model = new BasicMilpModelBuilder(
            request.Instance,
            neighborRadius: int.MaxValue,
            precedenceTrustRegion: trustRegion,
            runningTimeSupplementRatio: request.RunningTimeSupplementRatio,
            promotedMicroComponents: request.Components,
            endpointOccupationSeconds: request.EndpointOccupationSeconds).Build();
        try
        {
            int sourceVariables = model.Model.Get(GRB.IntAttr.NumVars);
            int sourceBinaries = model.Model.Get(GRB.IntAttr.NumBinVars);
            int sourceConstraints = model.Model.Get(GRB.IntAttr.NumConstrs);
            int fixedStops = 0;
            int activeStops = 0;
            int fixedOriginalDomainHotspotStops = 0;
            foreach ((string stopId, GRBVar variable) in model.StopVars)
            {
                bool inHotspot = activeStations.Contains(request.Instance.StopsById[stopId].StationName);
                bool explicitlyReleased = releasedStopIds.Contains(stopId);
                if (explicitlyReleased && IsActuallyFree(variable))
                {
                    activeStops++;
                    continue;
                }
                int fixedValue = inHotspot
                    ? ReadRequired(request.Reference.StopByStopId, stopId, "stop")
                    : ReadRequired(effectiveStopReference, stopId, "retained-pattern stop");
                FixAndRelax(variable, fixedValue);
                if (inHotspot) fixedOriginalDomainHotspotStops++; else fixedStops++;
            }

            int fixedSection = 0;
            int activeSection = 0;
            foreach ((string key, GRBVar variable) in model.SectionPrecedenceVars)
            {
                bool active = (request.Scope.ReleasedSectionPrecedenceKeys?.Contains(key) ??
                               trustRegion.HotspotKeys.Contains(key)) && IsActuallyFree(variable);
                if (active)
                {
                    activeSection++;
                    continue;
                }
                FixAndRelax(variable, ReadRequired(request.Reference.SectionPrecedenceByKey, key, "section precedence"));
                fixedSection++;
            }

            Dictionary<string, string> stationByEventKey = model.StationEventConflictCandidates
                .DistinctBy(BasicMilpModelBuilder.BuildStationEventPrecedenceKey)
                .ToDictionary(
                    BasicMilpModelBuilder.BuildStationEventPrecedenceKey,
                    candidate => candidate.StationName,
                    StringComparer.OrdinalIgnoreCase);
            int fixedStationEvent = 0;
            int activeStationEvent = 0;
            foreach ((string key, GRBVar variable) in model.StationEventPrecedenceVars)
            {
                bool active = (request.Scope.ReleasedStationEventPrecedenceKeys?.Contains(key) ??
                               (stationByEventKey.TryGetValue(key, out string? station) &&
                                activeStations.Contains(station))) && IsActuallyFree(variable);
                if (active)
                {
                    activeStationEvent++;
                    continue;
                }
                FixAndRelax(variable, ReadRequired(
                    request.Reference.StationEventPrecedenceByKey, key, "station-event precedence"));
                fixedStationEvent++;
            }

            string[] overlappingPatterns = request.RetainedPatterns
                .Where(pattern => activeStations.Contains(pattern.StationName))
                .Select(pattern => pattern.CertificateId)
                .ToArray();
            if (overlappingPatterns.Length > 0)
                throw new InvalidDataException(
                    $"Replaced active-station certificates were retained: {string.Join('|', overlappingPatterns)}");
            int patternRows = request.RetainedPatterns.Count == 0
                ? 0
                : MicroCertificateModelBinder.Apply(model, request.RetainedPatterns).AddedConstraintIds.Count;

            model.Model.Set(GRB.IntAttr.NumObj, 0);
            model.Model.SetObjective(new GRBLinExpr(), GRB.MINIMIZE);
            MasterSolutionSnapshot start = request.Reference with
            {
                StopByStopId = effectiveStopReference,
                VerifiedMicroAssignments = request.MipStartWitnesses?.ToArray()
            };
            HashSet<string> activeTimeStopIds = (request.Scope.AffectedTimeStopIds ?? releasedStopIds)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            HashSet<string> activeSectionKeys = (request.Scope.ReleasedSectionPrecedenceKeys ?? trustRegion.HotspotKeys)
                .Where(model.SectionPrecedenceVars.ContainsKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            HashSet<string> activeStationEventKeys = (request.Scope.ReleasedStationEventPrecedenceKeys ??
                    stationByEventKey.Where(item => activeStations.Contains(item.Value)).Select(item => item.Key)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            HashSet<string> activeSupplementKeys = ResolveAffectedSupplementKeys(
                request.Instance,
                activeTimeStopIds);
            MasterSolutionMipStartScope mipStartScope = new(
                activeTimeStopIds,
                releasedStopIds,
                activeSectionKeys,
                activeStationEventKeys,
                activeSupplementKeys,
                activeStations);
            VerifiedMicroAssignmentMipStartResult macroMipStart = MasterSolutionSnapshotApplier.ApplyMipStart(
                model,
                start,
                mipStartScope);
            ActiveMicroStartAudit activeMicroStart = ApplyCompatibleActiveMicroStarts(
                model,
                request.Components,
                request.MipStartWitnesses ?? []);
            VerifiedMicroAssignmentMipStartResult mipStart = macroMipStart with
            {
                WitnessCount = activeMicroStart.CompatibleRequirementCount,
                MatchedRequirementCount = activeMicroStart.Result.MatchedRequirementCount,
                StartedChoiceVariableCount = activeMicroStart.Result.StartedChoiceVariableCount,
                UnmatchedWitnessCount = activeMicroStart.Result.UnmatchedWitnessCount,
                StartedOrderVariableCount = activeMicroStart.Result.StartedOrderVariableCount,
                UnstartedOrderVariableCount = activeMicroStart.Result.UnstartedOrderVariableCount,
                SkippedMicroWitnessCount = activeMicroStart.RequirementWithoutCompatibleParentCount
            };
            model.Model.Update();
            int activeVariables = model.Model.Get(GRB.IntAttr.NumVars);
            int activeBinaries = model.Model.Get(GRB.IntAttr.NumBinVars);
            int activeConstraints = model.Model.Get(GRB.IntAttr.NumConstrs);
            buildClock.Stop();

            Stopwatch presolveClock = Stopwatch.StartNew();
            bool presolveAvailable = false;
            bool presolveProvedInfeasible = false;
            string presolveStatus = "presolve_diagnostics_disabled";
            int presolvedVariables = -1;
            int presolvedBinaries = -1;
            int presolvedConstraints = -1;
            if (request.RunPresolveDiagnostics)
            {
                try
                {
                    using GRBModel presolved = model.Model.Presolve();
                    presolvedVariables = presolved.Get(GRB.IntAttr.NumVars);
                    presolvedBinaries = presolved.Get(GRB.IntAttr.NumBinVars);
                    presolvedConstraints = presolved.Get(GRB.IntAttr.NumConstrs);
                    presolveAvailable = true;
                    presolveStatus = "presolved_model_available";
                }
                catch (GRBException exception)
                {
                    presolveProvedInfeasible = model.Model.Get(GRB.IntAttr.Status) == GRB.Status.INFEASIBLE;
                    presolveStatus = presolveProvedInfeasible
                        ? $"presolve_proved_infeasible::{exception.ErrorCode}::{exception.Message}"
                        : $"presolve_unavailable::{exception.ErrorCode}::{exception.Message}";
                }
            }
            presolveClock.Stop();

            (bool gatePassed, string gateStatus) = EvaluateGate(
                request.Gate,
                activeVariables,
                activeBinaries,
                activeConstraints,
                presolveAvailable,
                presolveProvedInfeasible,
                presolvedBinaries,
                buildClock.Elapsed.TotalSeconds + presolveClock.Elapsed.TotalSeconds);
            DynamicHotspotIntegratedModelAudit audit = new(
                gatePassed,
                gateStatus,
                buildClock.Elapsed.TotalSeconds,
                presolveClock.Elapsed.TotalSeconds,
                sourceVariables,
                sourceBinaries,
                sourceConstraints,
                activeVariables,
                activeBinaries,
                activeConstraints,
                presolveAvailable,
                presolveProvedInfeasible,
                presolveStatus,
                presolvedVariables,
                presolvedBinaries,
                presolvedConstraints,
                fixedStops,
                activeStops,
                fixedOriginalDomainHotspotStops,
                fixedSection,
                activeSection,
                fixedStationEvent,
                activeStationEvent,
                model.MaintenanceWindowBinaryBindings.Count,
                model.StationQuotaCategoryBindings.Count * 3,
                model.PromotedMicroAlternativeVariableCount,
                model.PromotedMicroOrderVariableCount,
                request.RetainedPatterns.Count,
                patternRows,
                activeMicroStart.CompatibleRequirementCount,
                activeMicroStart.Result.StartedChoiceVariableCount,
                activeMicroStart.Result.StartedOrderVariableCount,
                activeMicroStart.ActiveBinaryWithoutCompatibleParentCount,
                activeMicroStart.FirstGaps,
                mipStart);
            return new DynamicHotspotIntegratedCoordinator(request, model, trustRegion, audit);
        }
        catch
        {
            model.Dispose();
            throw;
        }
    }

    private sealed record ActiveMicroStartAudit(
        VerifiedMicroAssignmentMipStartResult Result,
        int CompatibleRequirementCount,
        int RequirementWithoutCompatibleParentCount,
        int ActiveBinaryWithoutCompatibleParentCount,
        IReadOnlyList<string> FirstGaps);

    private static ActiveMicroStartAudit ApplyCompatibleActiveMicroStarts(
        BasicMilpModel model,
        IReadOnlyList<PromotedMicroComponent> components,
        IReadOnlyList<VerifiedMicroAssignmentWitness> witnesses)
    {
        HashSet<string> requirementIds = components.SelectMany(component => component.Requirements)
            .Select(requirement => requirement.RequirementId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        VerifiedMicroAssignmentWitness[] compatible = witnesses
            .Where(witness => requirementIds.Contains(witness.RequirementId))
            .Where(witness => model.PromotedMicroChoiceBindings.Count(binding =>
                string.Equals(binding.RequirementId, witness.RequirementId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(binding.StationName, witness.StationName, StringComparison.OrdinalIgnoreCase) &&
                binding.TrainIndex == witness.TrainIndex &&
                binding.IsStop == witness.IsStop &&
                string.Equals(binding.TrackName, witness.TrackName, StringComparison.OrdinalIgnoreCase) &&
                binding.IncomingRouteId == witness.IncomingRouteId &&
                binding.OutgoingRouteId == witness.OutgoingRouteId) == 1)
            .GroupBy(witness => witness.RequirementId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        VerifiedMicroAssignmentMipStartResult result =
            VerifiedMicroAssignmentMipStartApplier.Apply(model, compatible);
        int compatibleRequirements = compatible.Length;
        int expectedChoiceStarts = model.PromotedMicroChoiceBindings.Count(binding =>
            compatible.Any(witness => string.Equals(
                witness.RequirementId, binding.RequirementId, StringComparison.OrdinalIgnoreCase)));
        Dictionary<string, VerifiedMicroAssignmentWitness> compatibleById = compatible
            .ToDictionary(witness => witness.RequirementId, StringComparer.OrdinalIgnoreCase);
        int expectedOrderStarts = model.PromotedMicroOrderBindings.Count(binding =>
            compatibleById.TryGetValue(binding.FirstRequirementId, out VerifiedMicroAssignmentWitness? first) &&
            compatibleById.TryGetValue(binding.SecondRequirementId, out VerifiedMicroAssignmentWitness? second) &&
            HasEventTime(first, binding.FirstEventSide) &&
            HasEventTime(second, binding.SecondEventSide));
        if (result.MatchedRequirementCount != compatibleRequirements ||
            result.StartedChoiceVariableCount != expectedChoiceStarts ||
            result.StartedOrderVariableCount != expectedOrderStarts)
        {
            string[] ids = compatible.Select(witness => witness.RequirementId)
                .Order(StringComparer.OrdinalIgnoreCase).Take(12).ToArray();
            throw new InvalidDataException(
                "Compatible parent Micro assignments were not fully mapped to active choice starts: " +
                $"compatible={compatibleRequirements},matched={result.MatchedRequirementCount}," +
                $"expected_choice={expectedChoiceStarts},started_choice={result.StartedChoiceVariableCount}," +
                $"expected_order={expectedOrderStarts},started_order={result.StartedOrderVariableCount}," +
                $"requirements={string.Join('|', ids)}");
        }

        HashSet<string> compatibleIds = compatible.Select(witness => witness.RequirementId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] missingRequirements = requirementIds.Except(compatibleIds, StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        int binariesWithoutParent = model.PromotedMicroChoiceBindings.Count(binding =>
                !compatibleIds.Contains(binding.RequirementId)) +
            model.PromotedMicroOrderBindings.Count(binding =>
                !compatibleIds.Contains(binding.FirstRequirementId) ||
                !compatibleIds.Contains(binding.SecondRequirementId));
        List<string> gaps = missingRequirements.Take(12)
            .Select(id => $"no_compatible_parent::{id}").ToList();
        if (result.UnstartedOrderVariableCount > 0)
            gaps.Add($"order_start_unavailable::{result.UnstartedOrderVariableCount}");
        return new ActiveMicroStartAudit(
            result,
            compatibleRequirements,
            missingRequirements.Length,
            binariesWithoutParent,
            gaps.Take(16).ToArray());
    }

    private static bool HasEventTime(
        VerifiedMicroAssignmentWitness witness,
        StationEventSide side) =>
        side == StationEventSide.Incoming
            ? witness.ArrivalSeconds.HasValue
            : witness.DepartureSeconds.HasValue;

    internal DynamicHotspotIntegratedSolveResult Solve(double timeLimitSeconds, string? logPath)
    {
        if (!Audit.GatePassed)
            return new(false, 0, "NOT_RUN", 0.0, null, false, false, 0, null, [], [], Audit.GateStatus);
        if (timeLimitSeconds <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(timeLimitSeconds));
        if (!string.IsNullOrWhiteSpace(logPath))
        {
            string full = Path.GetFullPath(logPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            Model.Model.Set(GRB.StringParam.LogFile, full);
        }
        Model.Model.Set(GRB.IntParam.Seed, 0);
        Model.Model.Set(GRB.IntParam.Threads, 0);
        Model.Model.Set(GRB.IntParam.MIPFocus, 1);
        Model.Model.Set(GRB.IntParam.SolutionLimit, 1);
        Model.Model.Set(GRB.DoubleParam.TimeLimit, timeLimitSeconds);
        FirstIncumbentTrackingCallback callback = new(true);
        Model.Model.SetCallback(callback);
        Stopwatch clock = Stopwatch.StartNew();
        Model.Optimize();
        clock.Stop();
        int status = Model.Model.Get(GRB.IntAttr.Status);
        if (!Model.HasIncumbentSolution)
        {
            IntegratedProofExtractionResult proof = status == GRB.Status.INFEASIBLE
                ? IntegratedInfeasibilityEvidenceExtractor.Extract(
                    Model.Model, Math.Min(30.0, timeLimitSeconds))
                : new IntegratedProofExtractionResult([], "not_infeasible", 0.0);
            return new(
                true,
                status,
                StatusName(status),
                clock.Elapsed.TotalSeconds,
                callback.FirstIncumbentTimeSeconds,
                false,
                false,
                0,
                null,
                [],
                proof.Rows,
                status == GRB.Status.INFEASIBLE
                    ? "restricted_integrated_hotspot_model_proven_infeasible"
                    : "restricted_integrated_hotspot_model_unknown_without_incumbent",
                proof.Strategy,
                proof.RuntimeSeconds);
        }

        MasterValidationReport macro = new MasterSolutionValidator().Validate(Model);
        MasterSolutionSnapshot candidate = MasterSolutionSnapshot.Capture(Model) with
        {
            VerifiedMicroAssignments = null
        };
        IReadOnlyList<VerifiedMicroAssignmentWitness> activeAssignments = CaptureActiveAssignments(candidate);
        candidate = candidate with { VerifiedMicroAssignments = activeAssignments };
        return new(
            true,
            status,
            StatusName(status),
            clock.Elapsed.TotalSeconds,
            callback.FirstIncumbentTimeSeconds,
            macro.IsFeasible,
            macro.IsFeasible,
            macro.Violations.Count,
            macro.IsFeasible ? candidate : null,
            activeAssignments,
            [],
            macro.IsFeasible
                ? "integrated_candidate_passed_complete_macro_validation"
                : "integrated_incumbent_failed_complete_macro_validation");
    }

    internal DynamicHotspotIntegratedBatchSolveResult SolveBatch(
        double timeLimitSeconds,
        string? logPath,
        int maximumCandidates,
        Func<DynamicHotspotIntegratedSolveResult, StationLevelMicroFeasibilitySlaveResult> evaluateMicro)
    {
        if (timeLimitSeconds <= 0.0) throw new ArgumentOutOfRangeException(nameof(timeLimitSeconds));
        if (maximumCandidates <= 0) throw new ArgumentOutOfRangeException(nameof(maximumCandidates));
        ArgumentNullException.ThrowIfNull(evaluateMicro);
        Stopwatch total = Stopwatch.StartNew();
        List<DynamicHotspotIntegratedEvaluatedCandidate> candidates = [];
        DynamicHotspotIntegratedSolveResult? terminal = null;
        int diversityRows = 0;
        for (int index = 0; index < maximumCandidates; index++)
        {
            double remaining = timeLimitSeconds - total.Elapsed.TotalSeconds;
            if (remaining <= 0.01) break;
            DynamicHotspotIntegratedSolveResult solve = Solve(
                remaining,
                index == 0 ? logPath : null);
            terminal = solve;
            if (solve.Candidate is null || !solve.MacroValidated) break;
            StationLevelMicroFeasibilitySlaveResult analysis = evaluateMicro(solve);
            int decisionChanges = CountMacroDecisionChanges(_request.Reference, solve.Candidate);
            DirectResumeCandidateScore score = DirectResumeCandidateScore.FromAnalysis(
                analysis,
                "full_line",
                solve.MacroValidated,
                decisionChanges);
            double span = ResolveTimetableSpanSeconds(_request.Instance, solve.Candidate);
            string candidateId = BuildCandidateId(solve);
            candidates.Add(new DynamicHotspotIntegratedEvaluatedCandidate(
                candidateId,
                solve,
                analysis,
                score,
                span));
            if (index + 1 >= maximumCandidates || !AddCurrentStructuralDiversityConstraint(index + 1))
                break;
            diversityRows++;
            Model.Model.Update();
            Model.Model.Reset();
        }
        DynamicHotspotIntegratedEvaluatedCandidate? selected = null;
        if (candidates.Count > 0)
        {
            HotspotCandidateScoreEntry best = DynamicHotspotCandidateSelectionPolicy.SelectBest(
                candidates.Select(candidate => new HotspotCandidateScoreEntry(
                    candidate.CandidateId,
                    candidate.Score,
                    candidate.TimetableSpanSeconds)).ToArray());
            selected = candidates.Single(candidate => string.Equals(
                candidate.CandidateId,
                best.CandidateId,
                StringComparison.OrdinalIgnoreCase));
        }
        return new(candidates, selected, terminal, diversityRows);
    }

    public void Dispose() => Model.Dispose();

    private IReadOnlyList<VerifiedMicroAssignmentWitness> CaptureActiveAssignments(
        MasterSolutionSnapshot candidate)
    {
        string fingerprint = SnapshotFingerprint.Calculate(candidate);
        List<VerifiedMicroAssignmentWitness> result = [];
        foreach (IGrouping<string, PromotedMicroChoiceVariableBinding> requirement in
                 Model.PromotedMicroChoiceBindings.GroupBy(
                     binding => binding.RequirementId,
                     StringComparer.OrdinalIgnoreCase))
        {
            PromotedMicroChoiceVariableBinding[] selected = requirement
                .Where(binding => binding.Variable.Get(GRB.DoubleAttr.X) >= 0.5)
                .ToArray();
            if (selected.Length != 1)
                throw new InvalidDataException(
                    $"Integrated solution selected {selected.Length} alternatives for '{requirement.Key}'.");
            PromotedMicroChoiceVariableBinding binding = selected[0];
            result.Add(new VerifiedMicroAssignmentWitness(
                binding.StationName,
                binding.RequirementId,
                binding.TrainIndex,
                binding.IsStop,
                binding.TrackName,
                binding.IncomingRouteId,
                binding.OutgoingRouteId,
                fingerprint,
                candidate.ArrivalSecondsByStopId[binding.RequirementId],
                candidate.DepartureSecondsByStopId[binding.RequirementId],
                "station_micro_validated"));
        }
        return result.OrderBy(item => item.StationName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.TrainIndex)
            .ThenBy(item => item.RequirementId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private bool AddCurrentStructuralDiversityConstraint(int ordinal)
    {
        GRBVar[] structuralVariables = GetStructuralDiversityVariables();
        if (structuralVariables.Length == 0) return false;
        GRBLinExpr difference = new();
        int selectedOnes = 0;
        foreach (GRBVar variable in structuralVariables)
        {
            if (variable.Get(GRB.DoubleAttr.X) >= 0.5)
            {
                difference.AddTerm(-1.0, variable);
                selectedOnes++;
            }
            else
            {
                difference.AddTerm(1.0, variable);
            }
        }
        Model.Model.AddConstr(
            difference,
            GRB.GREATER_EQUAL,
            1.0 - selectedOnes,
            $"hotspot_structural_diversity_{ordinal}");
        return true;
    }

    internal IReadOnlyList<string> GetStructuralDiversityVariableNamesForAudit() =>
        GetStructuralDiversityVariables()
            .Select(variable => variable.Get(GRB.StringAttr.VarName))
            .ToArray();

    private GRBVar[] GetStructuralDiversityVariables()
    {
        IEnumerable<GRBVar> macroStructure = Model.StopVars.Values
            .Concat(Model.SectionPrecedenceVars.Values)
            .Concat(Model.StationEventPrecedenceVars.Values);
        IEnumerable<GRBVar> microStructure = Model.PromotedMicroChoiceBindings
            .Select(binding => binding.Variable)
            .Concat(Model.PromotedMicroOrderBindings.Select(binding => binding.Variable));
        return macroStructure
            .Concat(microStructure)
            .Where(variable => variable.Get(GRB.CharAttr.VType) == GRB.BINARY &&
                               variable.Get(GRB.DoubleAttr.UB) - variable.Get(GRB.DoubleAttr.LB) > 0.5)
            .GroupBy(variable => variable.Get(GRB.StringAttr.VarName), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private string BuildCandidateId(DynamicHotspotIntegratedSolveResult solve)
    {
        string macro = SnapshotFingerprint.Calculate(solve.Candidate!);
        string resources = string.Join(';', solve.ActiveAssignments
            .OrderBy(item => item.StationName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.RequirementId, StringComparer.OrdinalIgnoreCase)
            .Select(item => $"{item.StationName}:{item.RequirementId}:{item.TrackName}:" +
                            $"{item.IncomingRouteId}:{item.OutgoingRouteId}"));
        string microOrders = string.Join(';', Model.PromotedMicroOrderBindings
            .OrderBy(item => item.StationName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.FirstRequirementId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.SecondRequirementId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Kind)
            .Select(item => $"{item.StationName}:{item.FirstRequirementId}:{item.SecondRequirementId}:" +
                            $"{item.Kind}:{item.FirstEventSide}:{item.SecondEventSide}:" +
                            $"{(item.Variable.Get(GRB.DoubleAttr.X) >= 0.5 ? 1 : 0)}"));
        return $"{macro}|{resources}|{microOrders}";
    }

    private static int CountMacroDecisionChanges(
        MasterSolutionSnapshot reference,
        MasterSolutionSnapshot candidate) =>
        reference.StopByStopId.Count(item => candidate.StopByStopId.TryGetValue(item.Key, out int value) &&
                                             value != item.Value) +
        reference.SectionPrecedenceByKey.Count(item =>
            candidate.SectionPrecedenceByKey.TryGetValue(item.Key, out int value) && value != item.Value) +
        reference.StationEventPrecedenceByKey.Count(item =>
            candidate.StationEventPrecedenceByKey.TryGetValue(item.Key, out int value) && value != item.Value);

    private static double ResolveTimetableSpanSeconds(
        TimetablingInstance instance,
        MasterSolutionSnapshot candidate)
    {
        if (candidate.BusinessObjective is not null)
            return candidate.BusinessObjective.PrimaryGlobalSpanSeconds;
        double first = instance.Trains.Min(train =>
            candidate.DepartureSecondsByStopId[train.OrderedStops[0].StopId]);
        double last = instance.Trains.Max(train =>
            candidate.ArrivalSecondsByStopId[train.OrderedStops[^1].StopId]);
        return Math.Max(0.0, last - first);
    }

    private static bool IsActuallyFree(GRBVar variable) =>
        variable.Get(GRB.DoubleAttr.UB) - variable.Get(GRB.DoubleAttr.LB) > 0.5;

    private static int ReadRequired(IReadOnlyDictionary<string, int> values, string key, string family)
    {
        if (!values.TryGetValue(key, out int value))
            throw new InvalidDataException($"Saved Macro snapshot is missing {family} '{key}'.");
        if (value is not 0 and not 1)
            throw new InvalidDataException($"Saved Macro {family} '{key}' is not binary: {value}.");
        return value;
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

    private static void FixAndRelax(GRBVar variable, int value)
    {
        variable.Set(GRB.DoubleAttr.LB, value);
        variable.Set(GRB.DoubleAttr.UB, value);
        variable.Set(GRB.CharAttr.VType, GRB.CONTINUOUS);
        variable.Set(GRB.DoubleAttr.Start, value);
    }

    private static (bool Passed, string Status) EvaluateGate(
        DynamicHotspotModelGate gate,
        int variables,
        int binaries,
        int constraints,
        bool presolveAvailable,
        bool presolveProvedInfeasible,
        int presolvedBinaries,
        double buildAndPresolveSeconds)
    {
        List<string> failures = [];
        if (variables > gate.MaximumVariables) failures.Add($"variables={variables}>{gate.MaximumVariables}");
        if (binaries > gate.MaximumActiveBinaries) failures.Add($"binaries={binaries}>{gate.MaximumActiveBinaries}");
        if (constraints > gate.MaximumConstraints) failures.Add($"constraints={constraints}>{gate.MaximumConstraints}");
        if (presolveAvailable && presolvedBinaries > gate.MaximumPresolvedActiveBinaries)
            failures.Add($"presolved_binaries={presolvedBinaries}>{gate.MaximumPresolvedActiveBinaries}");
        if (buildAndPresolveSeconds > gate.MaximumBuildAndPresolveSeconds)
            failures.Add($"build_presolve_seconds={buildAndPresolveSeconds:F3}>{gate.MaximumBuildAndPresolveSeconds:F3}");
        if (failures.Count > 0)
            return (true, $"model_size_audit_exceeded_execution_allowed::{string.Join(';', failures)}");
        return (true, presolveProvedInfeasible
            ? "model_size_audit_within_reference::presolve_proved_infeasible_no_branch_search"
            : presolveAvailable
                ? "model_size_audit_within_reference"
                : "model_size_audit_within_reference::presolve_artifact_unavailable");
    }

    private static string StatusName(int status) => status switch
    {
        GRB.Status.OPTIMAL => "OPTIMAL",
        GRB.Status.INFEASIBLE => "INFEASIBLE",
        GRB.Status.TIME_LIMIT => "TIME_LIMIT",
        GRB.Status.SOLUTION_LIMIT => "SOLUTION_LIMIT",
        GRB.Status.INTERRUPTED => "INTERRUPTED",
        _ => status.ToString()
    };
}
