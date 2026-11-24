// 本 partial 文件把通用 Checker 能力适配为自适应单候选生产扫描，包括候选池构造、分层执行和接受态事务提交。
// AdaptiveProductionSweepSession 保存一次扫描所需的基线模型、微观报告和预算计时，确保候选之间共享只读基线而互不污染。
using System.Diagnostics;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

public sealed partial class MasterCheckerLoopSolver
{
    private bool _adaptiveResumeCandidateContinuityValidated;

    internal AdaptiveSingletonInvocationResult RunAdaptiveSingletonConvergence(
        Func<bool>? cancelled = null,
        int? expectedInitialRootConflicts = null)
    {
        Stopwatch invocationStopwatch = Stopwatch.StartNew();
        AdaptiveSingletonOptions adaptiveOptions = _options.AdaptiveSingletonOptions.Validate();
        if (!adaptiveOptions.Enabled)
        {
            throw new InvalidOperationException(
                "The Adaptive Singleton production entry requires its explicit CLI option.");
        }
        if (!_options.FixedSnapshotMultiIterationMode ||
            !_options.ZeroConflictMode ||
            !_options.EnableFeasibleSingletonGlobalEvaluation ||
            !_options.IisGuidedOptions.Enabled ||
            _options.EnableLocalMicroRepair)
        {
            throw new InvalidOperationException(
                "The Adaptive Singleton production entry received an incompatible Checker configuration.");
        }
        if (string.IsNullOrWhiteSpace(_checkpointOutputPath))
        {
            throw new InvalidOperationException(
                "Adaptive execution requires a durable checkpoint output path.");
        }
        MasterSolutionSnapshot initialSnapshot = _initialSeed
            ?? throw new InvalidOperationException(
                "Adaptive Singleton execution requires the explicitly loaded Master snapshot.");

        ResetAdaptiveProductionState();
        _activeSectionEdges.AddRange(_resumeState.SectionEdges);
        _activeStationEventEdges.AddRange(_resumeState.StationEdges);
        _acceptedStructuralCandidateIds.UnionWith(_resumeState.AcceptedCandidateIds);

        CheckerMicroValidationComponents validationComponents = CheckerMicroValidationFactory.Create(
            _options.EndpointOccupationSeconds);
        MasterMicroFeasibilityChecker microChecker = validationComponents.Checker;
        using AdaptiveSingletonInitialState<BasicMilpModel> initialState =
            CreateAdaptiveInitialState(initialSnapshot, microChecker);
        int initialRootConflicts = initialState.AcceptedState.ConflictMetrics.RootConflictCount;
        MicroFeasibilityReport invocationInitialMicroReport =
            AdaptiveAcceptedStateCopies.Copy(initialState.AcceptedState.MicroReport);
        string initialAcceptedStateFingerprint = initialState.AcceptedState.AcceptedStateFingerprint;
        string initialReferenceFingerprint = initialState.InitialReference.Fingerprint;
        AdaptiveProductionSweepSession? currentSweep = null;
        AdaptiveSingletonCheckpointObserver checkpointObserver = new(
            _checkpointOutputPath,
            _instance.Meta.InstanceName,
            _options,
            () => CreateAdaptiveCheckpointCandidateContext(currentSweep),
            _initialCheckpoint is null ? "adaptive_fresh_snapshot" : "adaptive_checkpoint_resume");
        MasterValidationReport initialValidation = new MasterSolutionValidator().Validate(
            initialState.AcceptedState.Model);
        AdaptiveBaselinePreflightResult? baselinePreflight = expectedInitialRootConflicts.HasValue
            ? AdaptiveBaselinePreflight.Evaluate(
                expectedInitialRootConflicts.Value,
                initialRootConflicts,
                initialValidation.Violations.Count,
                initialState.AcceptedState.AcceptedTransitionIndex,
                invocationInitialMicroReport.Status)
            : null;
        if (baselinePreflight is { Passed: false })
        {
            AdaptiveAcceptedStateSnapshot baselineState = initialState.AcceptedState.CreateSnapshot();
            checkpointObserver.OnTerminated(
                baselineState,
                initialState.InitialReference,
                new AdaptiveSingletonRunTermination(
                    AdaptiveSingletonTerminationReasons.BaselineMismatch,
                    0,
                    [],
                    [],
                    baselinePreflight.Evidence,
                    "baseline_preflight"));
            invocationStopwatch.Stop();
            return new AdaptiveSingletonInvocationResult(
                initialRootConflicts,
                initialRootConflicts,
                baselineState.AcceptedTransitionIndex,
                AdaptiveSingletonTerminationReasons.BaselineMismatch,
                baselineState.AcceptedStateFingerprint,
                invocationInitialMicroReport.Status == MicroSolveStatus.Unknown ? 1 : 0,
                0,
                AdaptiveAcceptedStateCopies.Copy(baselineState.Snapshot),
                AdaptiveAcceptedStateCopies.Copy(invocationInitialMicroReport),
                AdaptiveAcceptedStateCopies.Copy(invocationInitialMicroReport),
                initialReferenceFingerprint,
                initialAcceptedStateFingerprint,
                initialValidation,
                baselineState.SectionEdges.ToList(),
                baselineState.StationEdges.ToList(),
                baselineState.AcceptedCandidateIds.ToList(),
                [],
                [],
                [],
                [],
                invocationStopwatch.Elapsed.TotalSeconds);
        }
        AdaptiveSingletonProductionSliceAdapter productionAdapter = new(
            (acceptedState, initialReference) => PrepareAdaptiveSweep(
                acceptedState,
                initialReference,
                microChecker,
                ref currentSweep),
            (acceptedState, initialReference, sweep) => EvaluateAdaptiveInitialDirect(
                acceptedState,
                initialReference,
                sweep,
                microChecker,
                RequireAdaptiveSweep(currentSweep, sweep)),
            (acceptedState, initialReference, sweep) => EvaluateAdaptiveInitialIis(
                acceptedState,
                initialReference,
                sweep,
                microChecker,
                RequireAdaptiveSweep(currentSweep, sweep)),
            (acceptedState, initialReference, sweep) => EvaluateAdaptiveExpandedDirect(
                acceptedState,
                initialReference,
                sweep,
                microChecker,
                RequireAdaptiveSweep(currentSweep, sweep)),
            (acceptedState, initialReference, sweep) => EvaluateAdaptiveExpandedIis(
                acceptedState,
                initialReference,
                sweep,
                microChecker,
                RequireAdaptiveSweep(currentSweep, sweep)),
            candidateId => RequireAdaptiveCandidate(currentSweep, candidateId));
        AdaptiveSingletonRunEngine<BasicMilpModel> engine = new(
            productionAdapter,
            new AdaptiveSingletonStopwatchClock(),
            new AdaptiveAcceptedStateTransaction<BasicMilpModel>(),
            cancelled,
            checkpointObserver);
        AdaptiveAcceptedState<BasicMilpModel> acceptedState = initialState.TransferAcceptedState();
        using AdaptiveSingletonRunResult<BasicMilpModel> runResult = engine.Run(
            acceptedState,
            initialState.InitialReference,
            AdaptiveSingletonRunOptionsFactory.Create(_options));

        AdaptiveAcceptedState<BasicMilpModel> finalState = runResult.FinalAcceptedState;
        _activeSectionEdges.Clear();
        _activeSectionEdges.AddRange(finalState.SectionEdges);
        _activeStationEventEdges.Clear();
        _activeStationEventEdges.AddRange(finalState.StationEdges);
        _acceptedStructuralCandidateIds.Clear();
        _acceptedStructuralCandidateIds.UnionWith(finalState.AcceptedCandidateIds);
        int qualityBlockedCount = runResult.SweepHistory
            .SelectMany(item => item.LayerSummaries)
            .Sum(item => item.BudgetRejectedCandidateCount);
        IReadOnlyList<AdaptiveSingletonSweepDiagnostic> sweepDiagnostics =
            BuildAdaptiveSweepDiagnostics(
                runResult,
                _checkpointOutputPath,
                runResult.TerminationReason);
        MasterSolutionSnapshot finalSnapshot = AdaptiveAcceptedStateCopies.Copy(finalState.Snapshot);
        MicroFeasibilityReport finalMicroReport;
        MasterValidationReport finalValidation;
        using (BasicMilpModel finalCarrier = BuildFixedSnapshotCarrier(finalSnapshot))
        {
            finalValidation = new MasterSolutionValidator().Validate(finalCarrier);
            finalMicroReport = microChecker.Validate(finalCarrier);
        }
        if (!finalValidation.IsFeasible ||
            !string.Equals(
                SnapshotFingerprint.Calculate(finalSnapshot),
                finalState.AcceptedStateFingerprint,
                StringComparison.Ordinal) ||
            !string.Equals(
                MicroConflictFingerprint.Calculate(finalMicroReport.Conflicts),
                finalState.ConflictFingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Independent final Adaptive Master/Micro validation does not match the accepted state.");
        }
        invocationStopwatch.Stop();
        return new AdaptiveSingletonInvocationResult(
            initialRootConflicts,
            finalState.ConflictMetrics.RootConflictCount,
            runResult.AcceptedTransitionCount,
            runResult.TerminationReason,
            finalState.AcceptedStateFingerprint,
            runResult.TerminalUnknownEvidence.Count,
            qualityBlockedCount,
            finalSnapshot,
            AdaptiveAcceptedStateCopies.Copy(finalMicroReport),
            invocationInitialMicroReport,
            initialReferenceFingerprint,
            initialAcceptedStateFingerprint,
            finalValidation,
            finalState.SectionEdges.ToList(),
            finalState.StationEdges.ToList(),
            finalState.AcceptedCandidateIds.ToList(),
            sweepDiagnostics,
            runResult.SweepHistory.ToList(),
            _structuralSingletonGlobalTrialDiagnostics.ToList(),
            runResult.SweepHistory.SelectMany(item => item.UnknownEvidence).ToList(),
            invocationStopwatch.Elapsed.TotalSeconds);
    }

    private static IReadOnlyList<AdaptiveSingletonSweepDiagnostic> BuildAdaptiveSweepDiagnostics(
        AdaptiveSingletonRunResult<BasicMilpModel> runResult,
        string checkpointPath,
        string terminationReason)
    {
        List<AdaptiveSingletonSweepDiagnostic> diagnostics = [];
        List<int> acceptedGains = [];
        List<double> acceptedRuntimePerConflict = [];
        int rollingTotalGain = 0;
        for (int index = 0; index < runResult.SweepHistory.Count; index++)
        {
            AdaptiveSingletonCompletedSweep sweep = runResult.SweepHistory[index];
            int gain = sweep.MetricsBefore.RootConflictCount - sweep.MetricsAfter.RootConflictCount;
            double? runtimePerConflict = gain > 0
                ? sweep.SweepRuntimeSeconds / gain
                : null;
            if (sweep.Accepted)
            {
                acceptedGains.Add(gain);
                rollingTotalGain += gain;
                if (runtimePerConflict.HasValue)
                {
                    acceptedRuntimePerConflict.Add(runtimePerConflict.Value);
                }
            }
            AdaptiveSingletonQualityMetrics quality = sweep.AcceptedQuality ?? new(
                0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0);
            int evaluated = sweep.LayerSummaries.Sum(item => item.EvaluatedCandidateCount);
            diagnostics.Add(new AdaptiveSingletonSweepDiagnostic(
                sweep.AcceptedTransitionIndexAfter,
                index + 1,
                sweep.Accepted,
                sweep.MetricsBefore.RootConflictCount,
                sweep.MetricsAfter.RootConflictCount,
                gain,
                sweep.SweepRuntimeSeconds,
                runtimePerConflict,
                sweep.LayerSummaries.Sum(item => item.FeasibleProbeCount),
                sweep.LayerSummaries.Sum(item => item.ProvenInfeasibleProbeCount),
                sweep.UnknownEvidence.Count,
                sweep.LayerSummaries.Sum(item => item.StrictImprovingCandidateCount),
                sweep.LayerSummaries.Any(item => IsIisLayer(item.Layer)),
                sweep.LayerSummaries.Any(item => IsExpandedLayer(item.Layer)),
                quality.StepTotalRetimingSeconds,
                quality.InitialTotalRetimingSeconds,
                quality.StepMaximumRetimingSeconds,
                quality.InitialMaximumRetimingSeconds,
                quality.IncrementalRunningSupplementSeconds,
                quality.CurrentTotalRunningSupplementSeconds,
                quality.CurrentSupplementSectionCount,
                acceptedGains.Count == 0 ? 0.0 : acceptedGains.Average(),
                Median(acceptedRuntimePerConflict),
                rollingTotalGain,
                sweep.AcceptedStateFingerprintBefore,
                sweep.AcceptedStateFingerprintAfter,
                sweep.ConflictFingerprintBefore,
                sweep.ConflictFingerprintAfter,
                sweep.CandidateUniverseFingerprint,
                sweep.SearchConfigurationFingerprint,
                sweep.CandidateUniverseSize,
                sweep.InitialCandidateCount,
                sweep.ExpandedCandidateCount,
                evaluated,
                Math.Max(0, sweep.CandidateUniverseSize - Math.Min(sweep.CandidateUniverseSize, evaluated)),
                index == runResult.SweepHistory.Count - 1 ? terminationReason : string.Empty,
                sweep.WinnerCandidateId,
                sweep.WinnerLayer,
                sweep.LayerSummaries.Sum(item => item.BudgetRejectedCandidateCount),
                sweep.MetricsBefore.EpisodeConflictCount,
                sweep.MetricsAfter.EpisodeConflictCount,
                sweep.CategoriesBefore.ExactHeadwayConflictCount,
                sweep.CategoriesAfter.ExactHeadwayConflictCount,
                sweep.CategoriesBefore.StationAssignmentConflictCount,
                sweep.CategoriesAfter.StationAssignmentConflictCount,
                sweep.ResolvedConflictCount,
                sweep.IntroducedConflictCount,
                sweep.PersistentConflictCount,
                LayerOutcome(sweep, AdaptiveSingletonLayerNames.InitialDirect),
                LayerOutcome(sweep, AdaptiveSingletonLayerNames.InitialIis),
                LayerOutcome(sweep, AdaptiveSingletonLayerNames.ExpandedDirect),
                LayerOutcome(sweep, AdaptiveSingletonLayerNames.ExpandedIis),
                checkpointPath,
                sweep.InitialCandidatePoolFingerprint,
                sweep.ExpandedCandidatePoolFingerprint,
                JoinCandidateIds(sweep.LayerSummaries.SelectMany(item => item.EvaluatedCandidateIds ?? [])),
                JoinCandidateIds(sweep.LayerSummaries.SelectMany(item => item.SkippedCandidateIds ?? [])),
                JoinCandidateIds(sweep.UnknownEvidence.Select(item => item.CandidateId)),
                JoinCandidateIds(sweep.LayerSummaries.SelectMany(item => item.QualityBlockedCandidateIds ?? []))));
        }
        return diagnostics;
    }

    private static string LayerOutcome(AdaptiveSingletonCompletedSweep sweep, string layer)
    {
        AdaptiveLayerSummary? summary = sweep.LayerSummaries.FirstOrDefault(item =>
            string.Equals(item.Layer, layer, StringComparison.Ordinal));
        if (summary is null)
        {
            return !string.IsNullOrWhiteSpace(sweep.WinnerLayer)
                ? "not_executed_due_to_earlier_layer_winner"
                : "not_executed_due_to_termination";
        }
        if (string.Equals(sweep.WinnerLayer, layer, StringComparison.Ordinal))
        {
            return "winner_accepted";
        }
        if (summary.UnknownCandidateCount > 0)
        {
            return "completed_with_unknown";
        }
        if (summary.BudgetRejectedCandidateCount > 0)
        {
            return "completed_quality_blocked";
        }
        return "completed_without_winner";
    }

    private static bool IsIisLayer(string layer) =>
        string.Equals(layer, AdaptiveSingletonLayerNames.InitialIis, StringComparison.Ordinal) ||
        string.Equals(layer, AdaptiveSingletonLayerNames.ExpandedIis, StringComparison.Ordinal);

    private static bool IsExpandedLayer(string layer) =>
        string.Equals(layer, AdaptiveSingletonLayerNames.ExpandedDirect, StringComparison.Ordinal) ||
        string.Equals(layer, AdaptiveSingletonLayerNames.ExpandedIis, StringComparison.Ordinal);

    private static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return 0.0;
        }
        double[] ordered = values.Order().ToArray();
        int middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2.0
            : ordered[middle];
    }

    private static string JoinCandidateIds(IEnumerable<string> candidateIds) => string.Join('|',
        candidateIds.Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase));

    private AdaptiveSingletonInitialState<BasicMilpModel> CreateAdaptiveInitialState(
        MasterSolutionSnapshot initialSnapshot,
        MasterMicroFeasibilityChecker microChecker)
    {
        BasicMilpModel? unownedInitialModel = BuildFixedSnapshotCarrier(initialSnapshot);
        try
        {
            if (!unownedInitialModel.HasIncumbentSolution)
            {
                throw new InvalidDataException(
                    "The supplied Adaptive Master snapshot did not produce a feasible fixed carrier.");
            }
            MicroFeasibilityReport initialReport = microChecker.Validate(unownedInitialModel);
            AdaptiveSingletonInitialState<BasicMilpModel> initialState = _initialCheckpoint is null
                ? AdaptiveSingletonInitialStateFactory.Create(
                    unownedInitialModel,
                    initialSnapshot,
                    initialReport,
                    _activeSectionEdges,
                    _activeStationEventEdges,
                    _acceptedStructuralCandidateIds.ToList(),
                    _promotedMicroComponentStore.ActiveComponents)
                : AdaptiveSingletonInitialStateFactory.Resume(
                    unownedInitialModel,
                    _initialCheckpoint,
                    initialReport,
                    AdaptiveCheckpointResumeConfiguration.From(_options));
            unownedInitialModel = null;
            return initialState;
        }
        finally
        {
            unownedInitialModel?.Dispose();
        }
    }

    private AdaptiveSingletonSweepContext PrepareAdaptiveSweep(
        AdaptiveAcceptedStateSnapshot acceptedState,
        InitialReferenceDescriptor initialReference,
        MasterMicroFeasibilityChecker microChecker,
        ref AdaptiveProductionSweepSession? session)
    {
        ArgumentNullException.ThrowIfNull(acceptedState);
        ArgumentNullException.ThrowIfNull(initialReference);
        ArgumentNullException.ThrowIfNull(microChecker);
        SynchronizeAdaptiveAcceptedCollections(acceptedState);

        IReadOnlyList<MicroConflictExplanation> structuralConflicts =
            CheckerStructuralConflictScopeResolver.Resolve(
                acceptedState.MicroReport.Conflicts,
                zeroConflictMode: true,
                focusStationName: null);
        IReadOnlyList<HeadwayConflictEpisode> episodes = HeadwayConflictEpisodeAnalyzer.Analyze(
            structuralConflicts,
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));
        HashSet<string> activeSectionKeys = acceptedState.SectionEdges
            .Select(edge => InitialConflictGraphBuilder.BuildCanonicalPairKey(
                edge.SectionName,
                edge.FirstTrainIndex,
                edge.SecondTrainIndex))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> activeStationKeys = acceptedState.StationEdges
            .Select(BasicMilpModelBuilder.BuildStationEventPrecedenceKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<HeadwayConflictCandidate> availableCandidates =
            HeadwayConflictCandidateSelector.ResolveAvailableCandidates(
                episodes,
                activeSectionKeys,
                activeStationKeys);
        AdaptiveSingletonOptions adaptive = _options.AdaptiveSingletonOptions;
        HeadwayConflictCandidateBatch initialSelection = HeadwayConflictCandidateSelector.SelectExact(
            episodes,
            activeSectionKeys,
            activeStationKeys,
            adaptive.InitialCandidateCap,
            _options.ExactPerStationCap,
            _options.ExactPerPairCap);
        List<AdaptiveCandidateDescriptor> universeDescriptors = availableCandidates
            .Select(ToAdaptiveCandidateDescriptor)
            .ToList();
        List<AdaptiveCandidateDescriptor> initialDescriptors = initialSelection.Candidates
            .Select(ToAdaptiveCandidateDescriptor)
            .ToList();
        AdaptiveCandidateSelectionConfiguration selectionConfiguration = new(
            adaptive.InitialCandidateCap,
            adaptive.ExpandedCandidateCap,
            _options.ExactPerStationCap,
            _options.ExactPerPairCap,
            "adaptive-singleton-production-v1");
        AdaptiveCandidatePoolDescriptor candidatePools = AdaptiveCandidatePoolBuilder.BuildNested(
            universeDescriptors,
            initialDescriptors,
            selectionConfiguration);
        ValidateAdaptiveResumeCandidateContinuity(candidatePools);
        IReadOnlyList<HeadwayConflictCandidate> expandedCandidates = candidatePools.Expanded
            .Select(descriptor => ResolveAdaptiveCandidate(availableCandidates, descriptor.CandidateId))
            .ToList();
        HeadwayConflictEdgeExpansion expansion = new(
            expandedCandidates.Where(item => item.SectionEdge is not null)
                .Select(item => item.SectionEdge!)
                .ToList(),
            expandedCandidates.Where(item => item.StationEdge is not null)
                .Select(item => item.StationEdge!)
                .ToList());
        IReadOnlySet<int> propagationTrainIndices =
            HeadwayConflictPropagationResolver.ResolveTrainIndices(expandedCandidates, episodes);
        IReadOnlyList<StructuralRepairNeighborhoodResolution> neighborhoodLevels =
            StructuralRepairNeighborhoodEscalationResolver.ResolveLevels(
                _instance,
                expansion,
                expandedCandidates,
                episodes,
                propagationTrainIndices,
                _options.StructuralRepairNeighborPadding,
                _options.EscalationOptions);
        StructuralRepairNeighborhoodResolution neighborhood = neighborhoodLevels[^1];
        HashSet<string> selectedEpisodeIds = expandedCandidates
            .Select(item => item.EpisodeId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<HeadwayConflictEpisode> selectedEpisodes = episodes
            .Where(item => selectedEpisodeIds.Contains(item.EpisodeId))
            .ToList();
        RepairDecisionNeighborhood decisionNeighborhood = RepairDecisionNeighborhoodFactory.Create(
            _instance,
            RepairDecisionLevel.FlexibleStop,
            neighborhood.ReleasedTrainIndices,
            selectedEpisodes);
        int largestWindowSeconds = Math.Max(
            _options.RecourseWindowSeconds.Max(),
            _options.EscalationOptions.EscalationWindowSeconds.Max());
        StructuralCandidateProbeFrozenConfiguration frozenConfiguration = new(
            acceptedState.Snapshot,
            decisionNeighborhood,
            _options.RepairRunningTimeSupplementRatio,
            largestWindowSeconds);
        AdaptiveInitialDirectAcceptedState sliceAcceptedState = new(
            acceptedState.AcceptedStateFingerprint,
            acceptedState.Snapshot,
            acceptedState.SectionEdges,
            acceptedState.StationEdges,
            acceptedState.AcceptedCandidateIds,
            acceptedState.PromotedComponents);
        string sweepEvidenceKey = string.Join(
            "::",
            "adaptive-sweep",
            acceptedState.AcceptedTransitionIndex,
            acceptedState.AcceptedStateFingerprint,
            candidatePools.CandidateUniverseFingerprint,
            candidatePools.SelectionConfigurationFingerprint);
        session = new AdaptiveProductionSweepSession(
            acceptedState,
            initialReference,
            episodes,
            availableCandidates,
            candidatePools,
            selectionConfiguration,
            frozenConfiguration,
            neighborhood.Level,
            sliceAcceptedState,
            sweepEvidenceKey);
        return new AdaptiveSingletonSweepContext(
            sweepEvidenceKey,
            candidatePools.CandidateUniverseFingerprint,
            acceptedState.AcceptedStateFingerprint,
            candidatePools.SelectionConfigurationFingerprint,
            candidatePools.Universe.Count,
            candidatePools.Initial.Count,
            candidatePools.Expanded.Count,
            AdaptiveCandidatePoolBuilder.FingerprintCandidateIds(candidatePools.Initial),
            AdaptiveCandidatePoolBuilder.FingerprintCandidateIds(candidatePools.Expanded));
    }

    private AdaptiveInitialDirectExecutionResult<StructuralSingletonGlobalTrialExecution>
        EvaluateAdaptiveInitialDirect(
            AdaptiveAcceptedStateSnapshot acceptedState,
            InitialReferenceDescriptor initialReference,
            AdaptiveSingletonSweepContext sweep,
            MasterMicroFeasibilityChecker microChecker,
            AdaptiveProductionSweepSession session)
    {
        EnsureAdaptiveLayerInput(acceptedState, initialReference, sweep, session);
        IReadOnlyList<HeadwayConflictCandidate> initialCandidates = session.CandidatePools.Initial
            .Select(item => ResolveAdaptiveCandidate(session.CandidateUniverse, item.CandidateId))
            .ToList();
        session.InitialProbeDiagnostics ??= StructuralCandidateFeasibilityDiagnosticRunner.RunIndividuals(
            initialCandidates,
            request => ExecuteStructuralCandidateProbe(
                request,
                session.FrozenConfiguration,
                session.AcceptedState.SectionEdges,
                session.AcceptedState.StationEdges,
                Array.Empty<LocalPrecedenceNogoodCut>(),
                session.IterationIndex,
                session.BatchId("initial-probe"),
                session.NeighborhoodLevel),
            "adaptive-initial-individual");
        _structuralCandidateProbeDiagnostics.AddRange(session.InitialProbeDiagnostics);
        _structuralTrialIisDiagnostics.AddRange(
            session.InitialProbeDiagnostics.SelectMany(item => item.Execution.IisDiagnostics));
        AdaptiveInitialDirectExecutionRequest request = new(
            session.SliceAcceptedState,
            initialReference,
            session.CandidatePools,
            session.InitialProbeDiagnostics,
            session.QualityBudgets(_options));
        AdaptiveInitialDirectExecutionResult<StructuralSingletonGlobalTrialExecution> result =
            EvaluateAdaptiveInitialDirectLayer(
                request,
                session.FrozenConfiguration,
                Array.Empty<LocalPrecedenceNogoodCut>(),
                session.IterationIndex,
                session.BatchId("initial-direct"),
                acceptedState.MicroReport,
                microChecker);
        session.InitialDirectCompletion = result.CreateInitialIisHandoff(request);
        return result;
    }

    private AdaptiveInitialIisExecutionResult<StructuralSingletonGlobalTrialExecution>
        EvaluateAdaptiveInitialIis(
            AdaptiveAcceptedStateSnapshot acceptedState,
            InitialReferenceDescriptor initialReference,
            AdaptiveSingletonSweepContext sweep,
            MasterMicroFeasibilityChecker microChecker,
            AdaptiveProductionSweepSession session)
    {
        EnsureAdaptiveLayerInput(acceptedState, initialReference, sweep, session);
        AdaptiveInitialIisExecutionRequest request = new(
            session.SliceAcceptedState,
            initialReference,
            session.CandidatePools,
            session.InitialProbeDiagnostics ?? throw new InvalidOperationException(
                "Adaptive Initial IIS requires completed Initial Direct probes."),
            session.QualityBudgets(_options),
            session.InitialDirectCompletion ?? throw new InvalidOperationException(
                "Adaptive Initial IIS requires the Initial Direct handoff."),
            _options.IisGuidedOptions);
        AdaptiveInitialIisExecutionResult<StructuralSingletonGlobalTrialExecution> result =
            EvaluateAdaptiveInitialIisLayer(
                request,
                session.FrozenConfiguration,
                Array.Empty<LocalPrecedenceNogoodCut>(),
                session.NeighborhoodLevel,
                session.IterationIndex,
                session.BatchId("initial-iis"),
                acceptedState.MicroReport,
                microChecker);
        session.InitialIisCompletion = result.CreateExpandedDirectHandoff(request);
        return result;
    }

    private AdaptiveExpandedDirectExecutionResult<StructuralSingletonGlobalTrialExecution>
        EvaluateAdaptiveExpandedDirect(
            AdaptiveAcceptedStateSnapshot acceptedState,
            InitialReferenceDescriptor initialReference,
            AdaptiveSingletonSweepContext sweep,
            MasterMicroFeasibilityChecker microChecker,
            AdaptiveProductionSweepSession session)
    {
        EnsureAdaptiveLayerInput(acceptedState, initialReference, sweep, session);
        AdaptiveExpandedDirectExecutionRequest request = new(
            session.SliceAcceptedState,
            initialReference,
            session.CandidatePools,
            session.CandidateUniverse,
            session.SelectionConfiguration,
            session.QualityBudgets(_options),
            session.InitialDirectCompletion ?? throw new InvalidOperationException(
                "Adaptive Expanded Direct requires the Initial Direct handoff."),
            session.InitialIisCompletion ?? throw new InvalidOperationException(
                "Adaptive Expanded Direct requires the Initial IIS handoff."),
            acceptedState.ConflictMetrics.RootConflictCount);
        AdaptiveExpandedDirectExecutionResult<StructuralSingletonGlobalTrialExecution> result =
            EvaluateAdaptiveExpandedDirectLayer(
                request,
                session.FrozenConfiguration,
                Array.Empty<LocalPrecedenceNogoodCut>(),
                session.NeighborhoodLevel,
                session.IterationIndex,
                session.BatchId("expanded-direct"),
                acceptedState.MicroReport,
                microChecker);
        _structuralCandidateProbeDiagnostics.AddRange(result.ProbeDiagnostics);
        _structuralTrialIisDiagnostics.AddRange(
            result.ProbeDiagnostics.SelectMany(item => item.Execution.IisDiagnostics));
        session.ExpandedDirectCompletion = result.CreateExpandedIisHandoff(request);
        return result;
    }

    private AdaptiveExpandedIisExecutionResult<StructuralSingletonGlobalTrialExecution>
        EvaluateAdaptiveExpandedIis(
            AdaptiveAcceptedStateSnapshot acceptedState,
            InitialReferenceDescriptor initialReference,
            AdaptiveSingletonSweepContext sweep,
            MasterMicroFeasibilityChecker microChecker,
            AdaptiveProductionSweepSession session)
    {
        EnsureAdaptiveLayerInput(acceptedState, initialReference, sweep, session);
        AdaptiveExpandedIisExecutionRequest request = new(
            session.SliceAcceptedState,
            initialReference,
            session.CandidatePools,
            session.SelectionConfiguration,
            session.QualityBudgets(_options),
            _options.IisGuidedOptions,
            session.InitialDirectCompletion ?? throw new InvalidOperationException(
                "Adaptive Expanded IIS requires the Initial Direct handoff."),
            session.InitialIisCompletion ?? throw new InvalidOperationException(
                "Adaptive Expanded IIS requires the Initial IIS handoff."),
            session.ExpandedDirectCompletion ?? throw new InvalidOperationException(
                "Adaptive Expanded IIS requires the Expanded Direct handoff."),
            acceptedState.ConflictMetrics.RootConflictCount);
        return EvaluateAdaptiveExpandedIisLayer(
            request,
            session.FrozenConfiguration,
            Array.Empty<LocalPrecedenceNogoodCut>(),
            session.NeighborhoodLevel,
            session.IterationIndex,
            session.BatchId("expanded-iis"),
            acceptedState.MicroReport,
            microChecker);
    }

    private void ResetAdaptiveProductionState()
    {
        _activeSectionEdges.Clear();
        _activeStationEventEdges.Clear();
        _acceptedStructuralCandidateIds.Clear();
        _recourseStageDiagnostics.Clear();
        _neighborhoodEscalationDiagnostics.Clear();
        _structuralTrialIisDiagnostics.Clear();
        _structuralCandidateProbeDiagnostics.Clear();
        _iisGuidedRepairTraceDiagnostics.Clear();
        _iisDependencyPlanDiagnostics.Clear();
        _rejectedTrialConflictDeltas.Clear();
        _structuralSingletonGlobalTrialDiagnostics.Clear();
        _structuralCandidateProbeStopReason = string.Empty;
    }

    private void SynchronizeAdaptiveAcceptedCollections(AdaptiveAcceptedStateSnapshot state)
    {
        _activeSectionEdges.Clear();
        _activeSectionEdges.AddRange(state.SectionEdges);
        _activeStationEventEdges.Clear();
        _activeStationEventEdges.AddRange(state.StationEdges);
        _acceptedStructuralCandidateIds.Clear();
        _acceptedStructuralCandidateIds.UnionWith(state.AcceptedCandidateIds);
        HashSet<string> statePromoted = state.PromotedComponents
            .Where(item => item.Active)
            .Select(item => item.ComponentId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> solverPromoted = _promotedMicroComponentStore.ActiveComponents
            .Select(item => item.ComponentId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!statePromoted.SetEquals(solverPromoted))
        {
            throw new InvalidDataException(
                "Adaptive accepted promoted components differ from the production Solver state.");
        }
    }

    private static AdaptiveCandidateDescriptor ToAdaptiveCandidateDescriptor(
        HeadwayConflictCandidate candidate,
        int sourceOrder = -1)
    {
        bool section = candidate.SectionEdge is not null;
        string resource = section
            ? candidate.SectionEdge!.SectionName
            : candidate.StationEdge!.StationName;
        string station = candidate.StationEdge?.StationName ?? resource;
        return new AdaptiveCandidateDescriptor(
            candidate.CandidateKey,
            section ? "section" : "station",
            resource,
            station,
            candidate.FirstTrainIndex,
            candidate.SecondTrainIndex,
            candidate.StationEdge?.FirstSide.ToString(),
            candidate.StationEdge?.SecondSide.ToString(),
            candidate.Score,
            sourceOrder >= 0 ? sourceOrder : 0);
    }

    private static HeadwayConflictCandidate ResolveAdaptiveCandidate(
        IReadOnlyList<HeadwayConflictCandidate> candidates,
        string candidateId) => candidates.Single(item => string.Equals(
            item.CandidateKey,
            candidateId,
            StringComparison.OrdinalIgnoreCase));

    private static HeadwayConflictCandidate RequireAdaptiveCandidate(
        AdaptiveProductionSweepSession? session,
        string candidateId) => ResolveAdaptiveCandidate(
            session?.CandidateUniverse ?? throw new InvalidOperationException(
                "Adaptive candidate resolution requires a prepared sweep."),
            candidateId);

    private AdaptiveCheckpointCandidateContext CreateAdaptiveCheckpointCandidateContext(
        AdaptiveProductionSweepSession? session)
    {
        if (session is null)
        {
            return _initialCheckpoint is null
                ? AdaptiveCheckpointCandidateContext.Empty
                : new AdaptiveCheckpointCandidateContext(
                    _initialCheckpoint.CandidateUniverseFingerprint,
                    _initialCheckpoint.InitialCandidatePoolFingerprint,
                    _initialCheckpoint.ExpandedCandidatePoolFingerprint,
                    _initialCheckpoint.InitialCandidateIds?.ToList() ?? [],
                    _initialCheckpoint.ExpandedCandidateIds?.ToList() ?? []);
        }
        return new AdaptiveCheckpointCandidateContext(
            session.CandidatePools.CandidateUniverseFingerprint,
            AdaptiveCandidatePoolBuilder.FingerprintCandidateIds(session.CandidatePools.Initial),
            AdaptiveCandidatePoolBuilder.FingerprintCandidateIds(session.CandidatePools.Expanded),
            session.CandidatePools.Initial.Select(item => item.CanonicalCandidateId).ToList(),
            session.CandidatePools.Expanded.Select(item => item.CanonicalCandidateId).ToList());
    }

    private void ValidateAdaptiveResumeCandidateContinuity(
        AdaptiveCandidatePoolDescriptor candidatePools)
    {
        if (_initialCheckpoint is null || _adaptiveResumeCandidateContinuityValidated)
        {
            return;
        }
        string initialFingerprint = AdaptiveCandidatePoolBuilder.FingerprintCandidateIds(
            candidatePools.Initial);
        string expandedFingerprint = AdaptiveCandidatePoolBuilder.FingerprintCandidateIds(
            candidatePools.Expanded);
        bool mismatch =
            !string.IsNullOrWhiteSpace(_initialCheckpoint.CandidateUniverseFingerprint) &&
            !string.Equals(
                _initialCheckpoint.CandidateUniverseFingerprint,
                candidatePools.CandidateUniverseFingerprint,
                StringComparison.Ordinal) ||
            !string.IsNullOrWhiteSpace(_initialCheckpoint.InitialCandidatePoolFingerprint) &&
            !string.Equals(
                _initialCheckpoint.InitialCandidatePoolFingerprint,
                initialFingerprint,
                StringComparison.Ordinal) ||
            !string.IsNullOrWhiteSpace(_initialCheckpoint.ExpandedCandidatePoolFingerprint) &&
            !string.Equals(
                _initialCheckpoint.ExpandedCandidatePoolFingerprint,
                expandedFingerprint,
                StringComparison.Ordinal);
        if (mismatch)
        {
            throw new InvalidDataException(
                "Adaptive resume candidate universe or pool fingerprint does not match the checkpoint.");
        }
        _adaptiveResumeCandidateContinuityValidated = true;
    }

    private static AdaptiveProductionSweepSession RequireAdaptiveSweep(
        AdaptiveProductionSweepSession? session,
        AdaptiveSingletonSweepContext sweep)
    {
        if (session is null || !string.Equals(
                session.SweepEvidenceKey,
                sweep.SweepEvidenceKey,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Adaptive production slice received a stale or foreign sweep session.");
        }
        return session;
    }

    private static void EnsureAdaptiveLayerInput(
        AdaptiveAcceptedStateSnapshot acceptedState,
        InitialReferenceDescriptor initialReference,
        AdaptiveSingletonSweepContext sweep,
        AdaptiveProductionSweepSession session)
    {
        if (!string.Equals(
                acceptedState.AcceptedStateFingerprint,
                session.AcceptedState.AcceptedStateFingerprint,
                StringComparison.Ordinal) ||
            !string.Equals(initialReference.Fingerprint, session.InitialReference.Fingerprint,
                StringComparison.Ordinal) ||
            !string.Equals(sweep.SweepEvidenceKey, session.SweepEvidenceKey,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Adaptive production layer inputs do not belong to the prepared immutable sweep.");
        }
    }

    private sealed class AdaptiveProductionSweepSession
    {
        internal AdaptiveProductionSweepSession(
            AdaptiveAcceptedStateSnapshot acceptedState,
            InitialReferenceDescriptor initialReference,
            IReadOnlyList<HeadwayConflictEpisode> episodes,
            IReadOnlyList<HeadwayConflictCandidate> candidateUniverse,
            AdaptiveCandidatePoolDescriptor candidatePools,
            AdaptiveCandidateSelectionConfiguration selectionConfiguration,
            StructuralCandidateProbeFrozenConfiguration frozenConfiguration,
            int neighborhoodLevel,
            AdaptiveInitialDirectAcceptedState sliceAcceptedState,
            string sweepEvidenceKey)
        {
            AcceptedState = acceptedState;
            InitialReference = initialReference;
            Episodes = episodes;
            CandidateUniverse = candidateUniverse;
            CandidatePools = candidatePools;
            SelectionConfiguration = selectionConfiguration;
            FrozenConfiguration = frozenConfiguration;
            NeighborhoodLevel = neighborhoodLevel;
            SliceAcceptedState = sliceAcceptedState;
            SweepEvidenceKey = sweepEvidenceKey;
        }

        internal AdaptiveAcceptedStateSnapshot AcceptedState { get; }
        internal InitialReferenceDescriptor InitialReference { get; }
        internal IReadOnlyList<HeadwayConflictEpisode> Episodes { get; }
        internal IReadOnlyList<HeadwayConflictCandidate> CandidateUniverse { get; }
        internal AdaptiveCandidatePoolDescriptor CandidatePools { get; }
        internal AdaptiveCandidateSelectionConfiguration SelectionConfiguration { get; }
        internal StructuralCandidateProbeFrozenConfiguration FrozenConfiguration { get; }
        internal int NeighborhoodLevel { get; }
        internal AdaptiveInitialDirectAcceptedState SliceAcceptedState { get; }
        internal string SweepEvidenceKey { get; }
        internal int IterationIndex => AcceptedState.AcceptedTransitionIndex + 1;
        internal IReadOnlyList<StructuralCandidateProbeDiagnostic>? InitialProbeDiagnostics { get; set; }
        internal AdaptiveInitialDirectLayerCompletion? InitialDirectCompletion { get; set; }
        internal AdaptiveInitialIisLayerCompletion? InitialIisCompletion { get; set; }
        internal AdaptiveExpandedDirectLayerCompletion? ExpandedDirectCompletion { get; set; }

        internal string BatchId(string layer) =>
            $"adaptive-{AcceptedState.AcceptedTransitionIndex:D3}-{layer}";

        internal AdaptiveQualityBudgetOptions QualityBudgets(CheckerLoopOptions options) => new(
            options.AdaptiveSingletonOptions.MaxInitialTotalRetimingSeconds,
            options.AdaptiveSingletonOptions.MaxInitialPointRetimingSeconds);
    }
}
