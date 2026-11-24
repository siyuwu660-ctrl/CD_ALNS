// 主从 Checker 循环以主 MILP 快照为工作状态，复用同一套 Macro 构建、Global Micro 校验、资源管理和报告流程。
// Legacy 策略通过局部试探和冲突下降规则提交候选；Validity-first 策略只持久化有证明的必要反馈，并把重复或不可安全投影的 core 提升为同站热点联合模型。
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Gurobi;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

/// <summary>
/// 第一版 checker-guided re-solve 闭环。
/// 当前实现重点放在三件事：
/// 1. 每轮只尝试接受一个 pairwise precedence cut，避免批量 hard cuts 直接顶到无解；
/// 2. 所有 cut 都先走试探求解，若无 incumbent 或微观冲突不降，则自动回滚；
/// 3. 在试探求解时叠加一个 hotspot-aware precedence trust region，把扰动压在局部。
/// </summary>
public sealed partial class MasterCheckerLoopSolver
{
    private readonly TimetablingInstance _instance;
    private readonly int? _neighborRadius;
    private readonly CheckerLoopOptions _options;
    private readonly MasterSolutionSnapshot? _initialSeed;
    private readonly CheckerCheckpoint? _initialCheckpoint;
    private readonly CheckerResumeState _resumeState;
    private readonly string? _checkpointOutputPath;
    private readonly List<SectionConflictEdgeRequest> _activeSectionEdges = new();
    private readonly List<StationEventConflictCandidate> _activeStationEventEdges = new();
    private readonly HashSet<string> _acceptedStructuralCandidateIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CheckerRecourseStageDiagnostic> _recourseStageDiagnostics = new();
    private readonly List<CheckerNeighborhoodEscalationDiagnostic> _neighborhoodEscalationDiagnostics = new();
    private readonly List<StructuralTrialIisDiagnostic> _structuralTrialIisDiagnostics = new();
    private readonly List<StructuralCandidateProbeDiagnostic> _structuralCandidateProbeDiagnostics = new();
    private readonly List<IisGuidedRepairTraceDiagnostic> _iisGuidedRepairTraceDiagnostics = new();
    private readonly List<IisDependencyPlanDiagnostic> _iisDependencyPlanDiagnostics = new();
    private readonly List<RejectedTrialConflictDelta> _rejectedTrialConflictDeltas = new();
    private readonly List<StructuralSingletonGlobalTrialDiagnostic> _structuralSingletonGlobalTrialDiagnostics = new();
    private string _structuralCandidateProbeStopReason = string.Empty;
    private readonly PromotedMicroComponentStore _promotedMicroComponentStore;
    private readonly PermanentFeedbackLedger _permanentFeedbackLedger;
    private readonly ValidityFirstSavedRepairNeighborhood? _validityFirstSavedRepairNeighborhood;
    private readonly double _validityFirstProcessElapsedBeforeSolveSeconds;
    private readonly Func<double>? _validityFirstProcessElapsedSecondsProvider;
    private Stopwatch? _validityFirstRunClock;
    private readonly ValidityFirstIisBudgetTracker _validityFirstIisBudgetTracker = new();
    private int _validityFirstEnhancedMasterSolveSequence;
    private int _validityFirstMacroValidationSequence;
    private ValidityFirstEnhancedMasterSolveDiagnostic? _lastValidityFirstEnhancedMasterDiagnostic;
    private MasterSolutionSnapshot? _validityFirstImmutableInitialReference;
    private bool _validityFirstSavedRepairNeighborhoodConsumed;

    public MasterCheckerLoopSolver(
        TimetablingInstance instance,
        int? neighborRadius,
        CheckerLoopOptions options,
        MasterSolutionSnapshot? initialSeed = null,
        CheckerCheckpoint? initialCheckpoint = null,
        string? checkpointOutputPath = null,
        double validityFirstProcessElapsedBeforeSolveSeconds = 0.0,
        Func<double>? validityFirstProcessElapsedSecondsProvider = null)
    {
        _instance = instance;
        _neighborRadius = neighborRadius;
        _options = options;
        _resumeState = CheckerResumeStateFactory.Create(initialSeed, initialCheckpoint);
        _initialSeed = _resumeState.InitialSnapshot;
        _initialCheckpoint = initialCheckpoint;
        _checkpointOutputPath = checkpointOutputPath;
        if (!double.IsFinite(validityFirstProcessElapsedBeforeSolveSeconds) ||
            validityFirstProcessElapsedBeforeSolveSeconds < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(validityFirstProcessElapsedBeforeSolveSeconds));
        }
        _validityFirstProcessElapsedBeforeSolveSeconds = validityFirstProcessElapsedBeforeSolveSeconds;
        _validityFirstProcessElapsedSecondsProvider = validityFirstProcessElapsedSecondsProvider;
        _promotedMicroComponentStore = new PromotedMicroComponentStore(initialCheckpoint?.PromotedMicroComponents);
        _permanentFeedbackLedger = new PermanentFeedbackLedger(initialCheckpoint?.PermanentMicroFeedback);
        _validityFirstSavedRepairNeighborhood = string.IsNullOrWhiteSpace(
            options.ValidityFirstReplayNeighborhoodInputPath)
            ? null
            : ValidityFirstSavedRepairNeighborhoodLoader.Load(
                options.ValidityFirstReplayNeighborhoodInputPath);
    }

    public bool HasInitialSeed => _initialSeed is not null;

    internal ControlledCandidateRepairResult RunControlledCandidateRepair(string candidateId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);
        MasterSolutionSnapshot baselineSnapshot = _initialSeed
            ?? throw new InvalidOperationException(
                "ControlledCandidateRepair requires the explicitly loaded Master snapshot.");
        if (!_options.IisGuidedOptions.Enabled)
        {
            throw new InvalidOperationException(
                "ControlledCandidateRepair requires --checker-iis-guided-repair.");
        }
        if (_options.EnableLocalMicroRepair)
        {
            throw new InvalidOperationException(
                "ControlledCandidateRepair forbids LocalMicroExact repair.");
        }
        if (Math.Abs(_options.RepairRunningTimeSupplementRatio - 0.05) > 1e-12)
        {
            throw new InvalidOperationException(
                "ControlledCandidateRepair requires repair supplement ratio 0.05.");
        }

        _activeSectionEdges.Clear();
        _activeStationEventEdges.Clear();
        _activeSectionEdges.AddRange(_resumeState.SectionEdges);
        _activeStationEventEdges.AddRange(_resumeState.StationEdges);
        IReadOnlyList<SectionConflictEdgeRequest> baselineSectionEdges = _activeSectionEdges.ToList();
        IReadOnlyList<StationEventConflictCandidate> baselineStationEdges = _activeStationEventEdges.ToList();
        using BasicMilpModel baselineModel = BuildFixedSnapshotCarrier(baselineSnapshot);
        if (!baselineModel.HasIncumbentSolution)
        {
            throw new InvalidOperationException(
                "The supplied Master snapshot did not produce a feasible fixed carrier.");
        }

        CheckerMicroValidationComponents validationComponents = CheckerMicroValidationFactory.Create(
            _options.EndpointOccupationSeconds);
        MasterMicroFeasibilityChecker microChecker = validationComponents.Checker;
        MicroFeasibilityReport microBefore = microChecker.Validate(baselineModel);
        MasterValidationReport baselineValidation = new MasterSolutionValidator().Validate(baselineModel);
        IReadOnlyList<MicroConflictExplanation> structuralConflicts =
            CheckerStructuralConflictScopeResolver.Resolve(
                microBefore.Conflicts,
                zeroConflictMode: true,
                focusStationName: null);
        IReadOnlyList<HeadwayConflictEpisode> episodes = HeadwayConflictEpisodeAnalyzer.Analyze(
            structuralConflicts,
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));
        HashSet<string> activeSectionKeys = baselineSectionEdges
            .Select(edge => InitialConflictGraphBuilder.BuildCanonicalPairKey(
                edge.SectionName, edge.FirstTrainIndex, edge.SecondTrainIndex))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> activeStationKeys = baselineStationEdges
            .Select(BasicMilpModelBuilder.BuildStationEventPrecedenceKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<HeadwayConflictCandidate> availableCandidates =
            HeadwayConflictCandidateSelector.ResolveAvailableCandidates(
                episodes,
                activeSectionKeys,
                activeStationKeys);
        HeadwayConflictCandidate candidate = ControlledStructuralCandidateResolver.ResolveExact(
            availableCandidates,
            candidateId,
            item => item.CandidateKey);

        HeadwayConflictEdgeExpansion candidateExpansion = new(
            candidate.SectionEdge is null
                ? Array.Empty<SectionConflictEdgeRequest>()
                : [candidate.SectionEdge],
            candidate.StationEdge is null
                ? Array.Empty<StationEventConflictCandidate>()
                : [candidate.StationEdge]);
        IReadOnlySet<int> propagationTrainIndices =
            HeadwayConflictPropagationResolver.ResolveTrainIndices([candidate], episodes);
        IReadOnlyList<StructuralRepairNeighborhoodResolution> neighborhoodLevels =
            StructuralRepairNeighborhoodEscalationResolver.ResolveLevels(
                _instance,
                candidateExpansion,
                [candidate],
                episodes,
                propagationTrainIndices,
                _options.StructuralRepairNeighborPadding,
                _options.EscalationOptions);
        StructuralRepairNeighborhoodResolution neighborhood = neighborhoodLevels[^1];
        IReadOnlyList<HeadwayConflictEpisode> selectedEpisodes = episodes
            .Where(episode => string.Equals(
                episode.EpisodeId,
                candidate.EpisodeId,
                StringComparison.OrdinalIgnoreCase))
            .ToList();
        RepairDecisionNeighborhood decisionNeighborhood = RepairDecisionNeighborhoodFactory.Create(
            _instance,
            RepairDecisionLevel.FlexibleStop,
            neighborhood.ReleasedTrainIndices,
            selectedEpisodes);
        int largestWindowSeconds = neighborhood.Level == 0
            ? _options.RecourseWindowSeconds.Max()
            : _options.EscalationOptions.EscalationWindowSeconds.Max();
        StructuralCandidateProbeFrozenConfiguration frozenConfiguration = new(
            baselineSnapshot,
            decisionNeighborhood,
            _options.RepairRunningTimeSupplementRatio,
            largestWindowSeconds);

        const int controlledIterationIndex = 0;
        const string controlledBatchId = "controlled-candidate-repair";
        IisGuidedMasterAttempt initialAttempt = ExecuteIisGuidedMasterAttempt(
            candidate,
            0,
            IisDependencyOverlay.Empty,
            frozenConfiguration,
            baselineSectionEdges,
            baselineStationEdges,
            Array.Empty<LocalPrecedenceNogoodCut>(),
            controlledIterationIndex,
            controlledBatchId,
            neighborhood.Level);
        List<StructuralTrialIisDiagnostic> allIis = initialAttempt.IisDiagnostics.ToList();
        List<IisGuidedRepairTraceDiagnostic> trace = [];
        List<IisDependencyPlanDiagnostic> plans = [];
        BasicMilpModel? candidateModel = null;
        bool dependencyClosureFeasible = false;
        string terminationReason;

        if (initialAttempt.Outcome == StructuralCandidateProbeOutcome.Unknown)
        {
            initialAttempt.Model.Dispose();
            terminationReason = "controlled_initial_unknown";
            return CreateControlledResult(
                candidate, initialAttempt, neighborhood, largestWindowSeconds,
                baselineSnapshot, baselineSectionEdges, baselineStationEdges,
                dependencyClosureFeasible, null, terminationReason,
                microBefore, null, baselineValidation, baselineValidation,
                allIis, trace, plans, null);
        }

        if (initialAttempt.Outcome == StructuralCandidateProbeOutcome.ProvenInfeasible)
        {
            IisModelDependencyCatalog catalog = IisModelDependencyCatalog.Build(initialAttempt.Model);
            initialAttempt.Model.Dispose();
            IisGuidedDependencyClosureResult closure = IisGuidedDependencyClosureRunner.Run(
                new IisGuidedDependencyClosureRequest(
                    candidate,
                    allIis,
                    catalog,
                    baselineSnapshot,
                    decisionNeighborhood,
                    largestWindowSeconds,
                    _options.IisGuidedOptions),
                (round, overlay) => ExecuteIisGuidedMasterAttempt(
                    candidate,
                    round,
                    overlay,
                    frozenConfiguration,
                    baselineSectionEdges,
                    baselineStationEdges,
                    Array.Empty<LocalPrecedenceNogoodCut>(),
                    controlledIterationIndex,
                    controlledBatchId,
                    neighborhood.Level));
            trace.AddRange(closure.Trace);
            plans.AddRange(closure.PlanDiagnostics);
            allIis.AddRange(closure.IisDiagnostics);
            if (!closure.HasMasterIncumbent)
            {
                return CreateControlledResult(
                    candidate, initialAttempt, neighborhood, largestWindowSeconds,
                    baselineSnapshot, baselineSectionEdges, baselineStationEdges,
                    false, null, closure.TerminationReason,
                    microBefore, null, baselineValidation, baselineValidation,
                    allIis, trace, plans, null);
            }
            candidateModel = closure.FeasibleModel!;
            dependencyClosureFeasible = true;
        }
        else
        {
            candidateModel = initialAttempt.Model;
        }

        MasterValidationReport finalValidation = new MasterSolutionValidator().Validate(candidateModel!);
        MicroFeasibilityReport microAfter = microChecker.Validate(candidateModel!);
        MicroConflictMetrics beforeMetrics = MicroConflictMetricCalculator.Calculate(microBefore.Conflicts);
        MicroConflictMetrics afterMetrics = MicroConflictMetricCalculator.Calculate(microAfter.Conflicts);
        bool microAccepted = CheckerStructuralIncumbentAcceptancePolicy.Accept(
            beforeMetrics.RootConflictCount,
            afterMetrics.RootConflictCount,
            _options.RequireStrictConflictImprovement);
        terminationReason = dependencyClosureFeasible
            ? IisGuidedRepairTransitionPolicy.ResolveAfterMicro(microAccepted)
            : microAccepted
                ? "controlled_singleton_accepted"
                : "controlled_singleton_feasible_but_micro_rejected";
        if (trace.Count > 0)
        {
            trace[^1] = trace[^1] with
            {
                Accepted = microAccepted,
                TerminationReason = terminationReason
            };
        }
        return CreateControlledResult(
            candidate, initialAttempt, neighborhood, largestWindowSeconds,
            baselineSnapshot, baselineSectionEdges, baselineStationEdges,
            dependencyClosureFeasible, microAccepted, terminationReason,
            microBefore, microAfter, baselineValidation, finalValidation,
            allIis, trace, plans, candidateModel);
    }

    private ControlledCandidateRepairResult CreateControlledResult(
        HeadwayConflictCandidate candidate,
        IisGuidedMasterAttempt initialAttempt,
        StructuralRepairNeighborhoodResolution neighborhood,
        int largestWindowSeconds,
        MasterSolutionSnapshot baselineSnapshot,
        IReadOnlyList<SectionConflictEdgeRequest> baselineSectionEdges,
        IReadOnlyList<StationEventConflictCandidate> baselineStationEdges,
        bool dependencyClosureFeasible,
        bool? microAccepted,
        string terminationReason,
        MicroFeasibilityReport microBefore,
        MicroFeasibilityReport? microAfter,
        MasterValidationReport baselineValidation,
        MasterValidationReport finalValidation,
        IReadOnlyList<StructuralTrialIisDiagnostic> iis,
        IReadOnlyList<IisGuidedRepairTraceDiagnostic> trace,
        IReadOnlyList<IisDependencyPlanDiagnostic> plans,
        BasicMilpModel? candidateModel) => new(
            candidate.CandidateKey,
            initialAttempt.Outcome,
            initialAttempt.SolverStatusName,
            initialAttempt.RuntimeSeconds,
            initialAttempt.SolutionCount,
            neighborhood.Level,
            neighborhood.Mode,
            largestWindowSeconds,
            _options.RepairRunningTimeSupplementRatio,
            SnapshotFingerprint.Calculate(baselineSnapshot),
            baselineSectionEdges.Count,
            baselineStationEdges.Count,
            dependencyClosureFeasible,
            microAccepted,
            terminationReason,
            microBefore,
            microAfter,
            baselineValidation,
            finalValidation,
            iis,
            trace,
            plans,
            candidateModel);

    public CheckerLoopResult Solve()
    {
        _validityFirstRunClock = _options.CoordinationStrategy == CheckerCoordinationStrategy.ValidityFirstBenders
            ? Stopwatch.StartNew()
            : null;
        _validityFirstEnhancedMasterSolveSequence = 0;
        _validityFirstMacroValidationSequence = 0;
        _activeSectionEdges.Clear();
        _activeStationEventEdges.Clear();
        _acceptedStructuralCandidateIds.Clear();
        _recourseStageDiagnostics.Clear();
        _neighborhoodEscalationDiagnostics.Clear();
        _structuralTrialIisDiagnostics.Clear();
        _structuralCandidateProbeDiagnostics.Clear();
        _iisGuidedRepairTraceDiagnostics.Clear();
        _iisDependencyPlanDiagnostics.Clear();
        _structuralCandidateProbeStopReason = string.Empty;
        _activeSectionEdges.AddRange(_resumeState.SectionEdges);
        _activeStationEventEdges.AddRange(_resumeState.StationEdges);
        _acceptedStructuralCandidateIds.UnionWith(_resumeState.AcceptedCandidateIds);
        List<CheckerLoopIteration> iterations = new();
        List<TrialCutDiagnostic> diagnostics = new();
        List<CheckerCutSelectionDiagnostic> selectionDiagnostics = new();
        List<LocalMicroRepairDiagnostic> localMicroRepairDiagnostics = new();
        List<LocalPrecedenceNogoodCut> activeCuts = new();
        HashSet<string> screenedCutIds = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> attemptedStationCoreProjectionIds = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> episodePersistence = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> stationHotspotPersistence = new(StringComparer.OrdinalIgnoreCase);
        List<CheckerConflictTrendPoint> conflictTrend = new();
        AssignmentCorePersistenceTracker assignmentCorePersistence = new();
        IReadOnlyList<AssignmentCoreOccurrence> currentAssignmentCoreOccurrences =
            Array.Empty<AssignmentCoreOccurrence>();
        List<ValidityFirstHotspotAttemptDiagnostic> hotspotAttemptDiagnostics = new();
        int consecutiveFeedbackOnlyRounds = 0;

        CheckerMicroValidationComponents validationComponents = CheckerMicroValidationFactory.Create(
            _options.EndpointOccupationSeconds);
        MasterMicroFeasibilityChecker microChecker = validationComponents.Checker;
        StationLevelMicroFeasibilitySlave microSlave = validationComponents.Slave;
        MicroFeedbackGenerator feedbackGenerator = new();
        CheckerInitialCarrierMode initialCarrierMode = CheckerInitialCarrierModeResolver.Resolve(_resumeState);
        BasicMilpModel currentModel;
        MasterSolutionSnapshot? currentSnapshot;
        bool initialSolveStarted = true;
        if (initialCarrierMode == CheckerInitialCarrierMode.FixedCheckpointSnapshot)
        {
            currentModel = _options.CoordinationStrategy == CheckerCoordinationStrategy.ValidityFirstBenders
                ? BuildValidityFirstFixedSnapshotCarrier(_initialSeed!)
                : BuildFixedSnapshotCarrier(_initialSeed!);
            currentSnapshot = _initialSeed!;
        }
        else
        {
            currentModel = _options.CoordinationStrategy == CheckerCoordinationStrategy.ValidityFirstBenders
                ? BuildValidityFirstModel(
                    activeCuts,
                    iterationIndex: 0,
                    phase: "initial_master_model_construction")
                : BuildModel(activeCuts, precedenceTrustRegion: null);
            if (_initialSeed is not null)
            {
                MasterSolutionSnapshotApplier.ApplyMipStart(currentModel, _initialSeed);
            }
            if (_options.CoordinationStrategy == CheckerCoordinationStrategy.ValidityFirstBenders)
            {
                initialSolveStarted = TryOptimizeValidityFirstMaster(
                    currentModel,
                    iterationIndex: 0,
                    phase: "initial_master",
                    _options.TimeLimitSecondsPerSolve);
            }
            else
            {
                currentModel.Model.Set(GRB.DoubleParam.TimeLimit, _options.TimeLimitSecondsPerSolve);
                currentModel.Optimize();
            }
            currentSnapshot = _options.CoordinationStrategy == CheckerCoordinationStrategy.ValidityFirstBenders
                ? TryLoadAuditedValidityFirstCandidate(currentModel, "initial_master")
                : currentModel.HasIncumbentSolution
                    ? MasterSolutionSnapshot.Capture(currentModel)
                    : null;
        }

        if (currentSnapshot is null)
        {
            string terminationReason = initialCarrierMode == CheckerInitialCarrierMode.FixedCheckpointSnapshot
                ? "checkpoint_snapshot_has_no_feasible_carrier"
                : ResolveValidityFirstNoCandidateTermination(
                    "initial_master",
                    initialSolveStarted,
                    "initial_master_has_no_incumbent");
            return new CheckerLoopResult(
                currentModel,
                iterations,
                diagnostics,
                activeCuts,
                terminationReason,
                selectionDiagnostics,
                _options.EndpointOccupationSeconds,
                localMicroRepairDiagnostics,
                _recourseStageDiagnostics.ToList(),
                CoordinationStrategy: _options.CoordinationStrategy,
                ConfigurationFingerprint: _options.CoordinationStrategy == CheckerCoordinationStrategy.ValidityFirstBenders
                    ? ValidityFirstConfigurationFingerprint.Calculate(_options)
                    : string.Empty,
                PermanentMicroFeedback: _permanentFeedbackLedger.Items);
        }

        MasterValidationReport? initialValidityFirstValidation = null;
        MacroClosureDecision? initialClosureDecision = null;
        if (_options.CoordinationStrategy == CheckerCoordinationStrategy.ValidityFirstBenders)
        {
            string validationPhase = "initial_snapshot";
            while (true)
            {
                int activeEdgeCountBeforeValidation = _activeSectionEdges.Count;
                initialValidityFirstValidation = RunTimedMasterValidation(
                    currentModel,
                    iterationIndex: 0,
                    phase: validationPhase);
                initialClosureDecision = MacroClosureDecisionPolicy.Resolve(initialValidityFirstValidation);
                if (initialValidityFirstValidation.IsFeasible ||
                    initialClosureDecision.Kind != MacroClosureDecisionKind.AddRelationsAndRebuild ||
                    _activeSectionEdges.Count == activeEdgeCountBeforeValidation)
                {
                    break;
                }

                currentModel.Dispose();
                validationPhase = "initial_macro_full_check_edge_rebuild";
                currentModel = BuildValidityFirstModel(
                    activeCuts,
                    iterationIndex: 0,
                    phase: validationPhase);
                bool rebuildSolveStarted = TryOptimizeValidityFirstMaster(
                    currentModel,
                    iterationIndex: 0,
                    phase: validationPhase,
                    _options.TimeLimitSecondsPerSolve);
                currentSnapshot = TryLoadAuditedValidityFirstCandidate(currentModel, validationPhase);
                if (currentSnapshot is null)
                {
                    return new CheckerLoopResult(
                        currentModel,
                        iterations,
                        diagnostics,
                        activeCuts,
                        ResolveValidityFirstNoCandidateTermination(
                            validationPhase,
                            rebuildSolveStarted,
                            "initial_macro_full_check_edge_rebuild_no_incumbent"),
                        selectionDiagnostics,
                        _options.EndpointOccupationSeconds,
                        localMicroRepairDiagnostics,
                        _recourseStageDiagnostics.ToList(),
                        _neighborhoodEscalationDiagnostics.ToList(),
                        CoordinationStrategy: _options.CoordinationStrategy,
                        ConfigurationFingerprint: ValidityFirstConfigurationFingerprint.Calculate(_options),
                        PermanentMicroFeedback: _permanentFeedbackLedger.Items);
                }
            }
            if (!initialValidityFirstValidation.IsFeasible)
            {
                string validationFailureReason = initialClosureDecision?.Kind switch
                {
                    MacroClosureDecisionKind.StopModeledRelationViolation =>
                        "initial_macro_modeled_relation_violation",
                    MacroClosureDecisionKind.AddRelationsAndRebuild =>
                        "initial_macro_relation_not_persisted_or_unbuildable",
                    _ => "initial_macro_business_validation_failed"
                };
                return new CheckerLoopResult(
                    currentModel,
                    iterations,
                    diagnostics,
                    activeCuts,
                    validationFailureReason,
                    selectionDiagnostics,
                    _options.EndpointOccupationSeconds,
                    localMicroRepairDiagnostics,
                    _recourseStageDiagnostics.ToList(),
                    _neighborhoodEscalationDiagnostics.ToList(),
                    CoordinationStrategy: _options.CoordinationStrategy,
                    ConfigurationFingerprint: ValidityFirstConfigurationFingerprint.Calculate(_options),
                    PermanentMicroFeedback: _permanentFeedbackLedger.Items);
            }
        }
        MicroFeasibilityReport currentReport = RunTimedGlobalMicroValidation(
            microChecker,
            currentModel,
            iterationIndex: 0,
            phase: "initial_snapshot");
        if (_options.CoordinationStrategy == CheckerCoordinationStrategy.ValidityFirstBenders &&
            _options.EnablePhasedFeasibilityRecovery)
        {
            currentSnapshot = AttachVerifiedMicroAssignments(
                currentModel,
                currentSnapshot,
                microSlave,
                _resumeState.IterationOffset,
                "initial_snapshot");
        }
        MicroFeasibilityReport c0Report = currentReport;
        if (_options.CoordinationStrategy == CheckerCoordinationStrategy.ValidityFirstBenders)
        {
            currentAssignmentCoreOccurrences = assignmentCorePersistence.Observe(
                AssignmentCoreIdentityAnalyzer.Analyze(currentReport));
            AssignmentCoreIdentityReporter.Write(
                0,
                "initial_snapshot",
                currentAssignmentCoreOccurrences,
                ResolveValidityFirstOutputDirectory());
        }
        MicroConflictMetrics initialMetrics = MicroConflictMetricCalculator.Calculate(currentReport.Conflicts);
        conflictTrend.Add(new CheckerConflictTrendPoint(initialMetrics.RootConflictCount, initialMetrics.EpisodeConflictCount));
        ValidityFirstSolutionStateTracker validityFirstState = new(
            _initialCheckpoint?.BestDiagnosticSolution,
            _initialCheckpoint?.BestJointFeasibleSolution);
        if (_options.CoordinationStrategy == CheckerCoordinationStrategy.ValidityFirstBenders)
        {
            currentSnapshot = MasterSolutionSnapshotObjectiveResolver.Ensure(
                currentSnapshot,
                _instance.Trains);
            MasterValidationReport initialValidation = initialValidityFirstValidation!;
            currentSnapshot = validityFirstState.Register(
                currentSnapshot,
                initialValidation.IsFeasible,
                currentReport.IsFeasible,
                initialMetrics.RootConflictCount,
                SolverBoundScope.GlobalStrengthenedMaster,
                globalLowerBound: null);
        }

        _validityFirstImmutableInitialReference = _initialCheckpoint?.InitialReferenceSnapshot ?? currentSnapshot;
        MasterSolutionSnapshot validityFirstRepairReference =
            validityFirstState.BestDiagnosticSolution ?? currentSnapshot;
        if ((validityFirstRepairReference.VerifiedMicroAssignments is null ||
             validityFirstRepairReference.VerifiedMicroAssignments.Count == 0) &&
            currentSnapshot.VerifiedMicroAssignments is { Count: > 0 } &&
            string.Equals(
                SnapshotFingerprint.Calculate(validityFirstRepairReference),
                SnapshotFingerprint.Calculate(currentSnapshot),
                StringComparison.Ordinal))
        {
            validityFirstRepairReference = validityFirstRepairReference with
            {
                VerifiedMicroAssignments = currentSnapshot.VerifiedMicroAssignments
            };
        }
        int validityFirstRepairReferenceRootConflictCount = initialMetrics.RootConflictCount;
        HashSet<string> validityFirstMigrationPriorityStations = new(StringComparer.OrdinalIgnoreCase);
        ValidityFirstRepairAttemptRegistry validityFirstRepairAttemptRegistry = new();
        RepairBranchAttemptRegistry repairBranchAttemptRegistry = new();
        if (_options.CoordinationStrategy == CheckerCoordinationStrategy.ValidityFirstBenders)
        {
            SaveCheckpointIfConfigured(_resumeState.IterationOffset, currentSnapshot, validityFirstState);
        }

        CheckerIterationBudget iterationBudget = new(_options.MaxIterations);
        string? expectedAcceptedSnapshotFingerprint = null;
        for (int localIterationIndex = 1; localIterationIndex <= _options.MaxIterations + 1; localIterationIndex++)
        {
            if (localIterationIndex > _options.MaxIterations)
            {
                // 新策略允许在最后一次协调成功后进入一次不消耗反馈轮次的目标改善阶段。
                if (_options.CoordinationStrategy != CheckerCoordinationStrategy.ValidityFirstBenders ||
                    !currentReport.IsFeasible)
                {
                    break;
                }
            }
            else
            {
            iterationBudget.EnsureCanStart(localIterationIndex - 1);
            }
            int iterationIndex = _resumeState.IterationOffset + localIterationIndex;
            string iterationInputSnapshotFingerprint = SnapshotFingerprint.Calculate(currentSnapshot);
            if (expectedAcceptedSnapshotFingerprint is not null)
            {
                CheckerAcceptedStateContinuityGuard.EnsureNextInput(
                    expectedAcceptedSnapshotFingerprint,
                    iterationInputSnapshotFingerprint,
                    iterationIndex);
            }
            int iterationIisTraceStartIndex = _iisGuidedRepairTraceDiagnostics.Count;
            int iterationSingletonGlobalTrialStartIndex = _structuralSingletonGlobalTrialDiagnostics.Count;
            UpdateStationHotspotPersistence(currentReport, stationHotspotPersistence);
            if (_options.ZeroConflictMode && currentReport.IsFeasible)
            {
                if (_options.CoordinationStrategy == CheckerCoordinationStrategy.ValidityFirstBenders &&
                    _options.StopAfterFirstJointFeasible &&
                    validityFirstState.BestJointFeasibleSolution is not null)
                {
                    SaveCheckpointIfConfigured(iterationIndex, currentSnapshot, validityFirstState);
                    return new CheckerLoopResult(
                        currentModel,
                        iterations,
                        diagnostics,
                        activeCuts,
                        "joint_feasible_first_hit",
                        selectionDiagnostics,
                        _options.EndpointOccupationSeconds,
                        localMicroRepairDiagnostics,
                        _recourseStageDiagnostics.ToList(),
                        _neighborhoodEscalationDiagnostics.ToList(),
                        c0Report,
                        currentReport,
                        localIterationIndex,
                        WorkingSolution: validityFirstState.WorkingSolution,
                        BestDiagnosticSolution: validityFirstState.BestDiagnosticSolution,
                        BestJointFeasibleSolution: validityFirstState.BestJointFeasibleSolution,
                        CoordinationStrategy: _options.CoordinationStrategy,
                        ConfigurationFingerprint: ValidityFirstConfigurationFingerprint.Calculate(_options),
                        PermanentMicroFeedback: _permanentFeedbackLedger.Items);
                }
                if (_options.CoordinationStrategy == CheckerCoordinationStrategy.ValidityFirstBenders &&
                    validityFirstState.BestJointFeasibleSolution is not null)
                {
                    MasterSolutionSnapshot incumbentJoint = validityFirstState.BestJointFeasibleSolution;
                    IReadOnlyList<PromotedMicroComponent> stageThreeVerifiedAssignments =
                        Array.Empty<PromotedMicroComponent>();
                    if (_options.EnablePhasedFeasibilityRecovery)
                    {
                        Stopwatch assignmentStopwatch = Stopwatch.StartNew();
                        using BasicMilpModel verifiedCarrier = BuildFixedSnapshotCarrier(incumbentJoint);
                        StationLevelMicroFeasibilitySlaveResult verifiedAssignmentResult =
                            microSlave.Solve(verifiedCarrier);
                        stageThreeVerifiedAssignments =
                            ValidityFirstFeasibleAssignmentPromotionBuilder.BuildFromVerifiedWitnesses(
                                verifiedAssignmentResult,
                                incumbentJoint.VerifiedMicroAssignments ?? throw new InvalidDataException(
                                    "Stage-3 preparation requires the saved assignments of the accepted joint-feasible candidate."),
                                iterationIndex);
                        assignmentStopwatch.Stop();
                        ValidityFirstPhaseTimingReporter.Append(
                            iterationIndex,
                            "business_objective_improvement",
                            "verified_assignment_capture",
                            assignmentStopwatch.Elapsed.TotalSeconds,
                            GetValidityFirstCumulativeElapsedSeconds(),
                            ResolveValidityFirstOutputDirectory());
                    }

                    IReadOnlyList<PromotedMicroComponent>? stageThreeModelComponents =
                        _options.EnablePhasedFeasibilityRecovery
                            ? ValidityFirstStageThreeComponentSet.Compose(
                                _promotedMicroComponentStore.ActiveComponents,
                                stageThreeVerifiedAssignments)
                            : null;
                    BasicMilpModel improvementModel = BuildValidityFirstModel(
                        activeCuts,
                        iterationIndex,
                        "business_objective_improvement_model_construction",
                        promotedComponentsOverride: stageThreeModelComponents);
                    if (_options.EnablePhasedFeasibilityRecovery)
                    {
                        Stopwatch fixationStopwatch = Stopwatch.StartNew();
                        _ = ValidityFirstVerifiedStructureFixer.Apply(
                            improvementModel,
                            incumbentJoint,
                            stageThreeVerifiedAssignments,
                            _options.EndpointOccupationSeconds);
                        fixationStopwatch.Stop();
                        ValidityFirstPhaseTimingReporter.Append(
                            iterationIndex,
                            "business_objective_improvement",
                            "verified_structure_fixation",
                            fixationStopwatch.Elapsed.TotalSeconds,
                            GetValidityFirstCumulativeElapsedSeconds(),
                            ResolveValidityFirstOutputDirectory());
                    }
                    ApplyMipStart(improvementModel, incumbentJoint);
                    bool improvementSolveStarted = TryOptimizeValidityFirstMaster(
                        improvementModel,
                        iterationIndex,
                        "business_objective_improvement",
                        _options.EnablePhasedFeasibilityRecovery
                            ? _options.ValidityFirstTotalRuntimeBudgetSeconds
                            : _options.SecondaryTimeLimitSecondsPerSolve);
                    if (improvementSolveStarted && improvementModel.HasIncumbentSolution)
                    {
                        MasterValidationReport improvementValidation = RunTimedMasterValidation(
                            improvementModel,
                            iterationIndex,
                            "business_objective_improvement");
                        MicroFeasibilityReport improvementReport = RunTimedGlobalMicroValidation(
                            microChecker,
                            improvementModel,
                            iterationIndex,
                            "business_objective_improvement");
                        MasterSolutionSnapshot improvementSnapshot = MasterSolutionSnapshot.Capture(improvementModel);
                        if (improvementValidation.IsFeasible && improvementReport.IsFeasible &&
                            ValidityFirstSolutionStateTracker.IsBusinessObjectiveBetterOrEqual(
                                improvementSnapshot.BusinessObjective!,
                                incumbentJoint.BusinessObjective!))
                        {
                            double? provenPrimaryLowerBound =
                                improvementModel.Model.Get(GRB.IntAttr.Status) == GRB.Status.OPTIMAL
                                    ? improvementSnapshot.BusinessObjective!.PrimaryGlobalSpanSeconds
                                    : null;
                            MicroConflictMetrics improvementMetrics =
                                MicroConflictMetricCalculator.Calculate(improvementReport.Conflicts);
                            improvementSnapshot = validityFirstState.Register(
                                improvementSnapshot,
                                true,
                                true,
                                improvementMetrics.RootConflictCount,
                                SolverBoundScope.GlobalStrengthenedMaster,
                                provenPrimaryLowerBound);
                            CheckerLoopIteration objectiveIteration = new(
                                iterationIndex,
                                currentSnapshot.ObjectiveValue,
                                improvementSnapshot.ObjectiveValue,
                                true,
                                0,
                                0,
                                0,
                                0,
                                improvementModel.StatusName,
                                "validity_first_objective_improvement",
                                string.Empty,
                                string.Empty,
                                CanonicalConflictCountBefore: 0,
                                CanonicalConflictCountAfter: 0,
                                RootConflictCountBefore: 0,
                                RootConflictCountAfter: 0,
                                ConflictedStationCountBefore: 0,
                                ConflictedStationCountAfter: 0,
                                ActiveSectionEdgeCountBefore: _activeSectionEdges.Count,
                                ActiveSectionEdgeCountAfter: _activeSectionEdges.Count,
                                ActiveStationEventEdgeCountBefore: _activeStationEventEdges.Count,
                                ActiveStationEventEdgeCountAfter: _activeStationEventEdges.Count,
                                AdaptivePolicyReason: "remaining_secondary_time_business_objective",
                                ZeroConflictMode: true);
                            objectiveIteration = AttachIterationContinuityDiagnostics(
                                objectiveIteration,
                                currentSnapshot,
                                improvementSnapshot,
                                currentReport,
                                improvementReport,
                                improvementModel,
                                accepted: true,
                                Array.Empty<string>(),
                                "ValidityFirstBusinessObjective",
                                iterationIisTraceStartIndex);
                            iterations.Add(objectiveIteration);
                            currentModel.Dispose();
                            currentModel = improvementModel;
                            currentSnapshot = improvementSnapshot;
                            currentReport = improvementReport;
                            SaveCheckpointIfConfigured(iterationIndex, currentSnapshot, validityFirstState);
                        }
                        else
                        {
                            improvementModel.Dispose();
                        }
                    }
                    else
                    {
                        improvementModel.Dispose();
                    }
                }

                string terminationReason = _options.FixedSnapshotMultiIterationMode
                    ? "MICRO_FEASIBLE_SUCCESS"
                    : "micro_feasible";
                return new CheckerLoopResult(
                    currentModel, iterations, diagnostics, activeCuts, terminationReason,
                    selectionDiagnostics, _options.EndpointOccupationSeconds, localMicroRepairDiagnostics,
                    _recourseStageDiagnostics.ToList(),
                    WorkingSolution: validityFirstState.WorkingSolution,
                    BestDiagnosticSolution: validityFirstState.BestDiagnosticSolution,
                    BestJointFeasibleSolution: validityFirstState.BestJointFeasibleSolution,
                    CoordinationStrategy: _options.CoordinationStrategy,
                    ConfigurationFingerprint: _options.CoordinationStrategy == CheckerCoordinationStrategy.ValidityFirstBenders
                        ? ValidityFirstConfigurationFingerprint.Calculate(_options)
                        : string.Empty,
                    PermanentMicroFeedback: _permanentFeedbackLedger.Items);
            }

            if (_options.ZeroConflictMode && _options.EnableLocalMicroRepair)
            {
                LocalMicroRepairEvaluation repair = EvaluateLocalMicroRepair(
                    currentModel,
                    currentSnapshot,
                    currentReport,
                    activeCuts,
                    iterationIndex,
                    microChecker,
                    microSlave,
                    stationHotspotPersistence);
                localMicroRepairDiagnostics.AddRange(repair.Diagnostics);
                if (repair.Accepted)
                {
                    if (repair.PromotedComponent is not null)
                    {
                        _promotedMicroComponentStore.Upsert(repair.PromotedComponent);
                    }
                    BasicMilpModel repairedModel = repair.Model
                        ?? throw new InvalidOperationException("An accepted local Micro repair must return its carrier model.");
                    MasterSolutionSnapshot repairedSnapshot = repair.Snapshot
                        ?? throw new InvalidOperationException("An accepted local Micro repair must return its snapshot.");
                    MicroFeasibilityReport repairedReport = repair.Report
                        ?? throw new InvalidOperationException("An accepted local Micro repair must return its Micro report.");
                    MicroConflictMetrics beforeMetrics = MicroConflictMetricCalculator.Calculate(currentReport.Conflicts);
                    MicroConflictMetrics afterMetrics = MicroConflictMetricCalculator.Calculate(repairedReport.Conflicts);
                    LocalMicroRepairDiagnostic acceptedDiagnostic = repair.Diagnostics.Last(diagnostic =>
                        string.Equals(diagnostic.Outcome, "accepted", StringComparison.OrdinalIgnoreCase));
                    iterations.Add(new CheckerLoopIteration(
                        iterationIndex,
                        currentSnapshot.ObjectiveValue,
                        repairedSnapshot.ObjectiveValue,
                        repairedReport.IsFeasible,
                        currentReport.Conflicts.Count,
                        repairedReport.Conflicts.Count,
                        0,
                        0,
                        acceptedDiagnostic.SolverStatus,
                        "local_micro_feasibility_repair",
                        string.Empty,
                        acceptedDiagnostic.StationName,
                        CanonicalConflictCountBefore: beforeMetrics.CanonicalConflictCount,
                        CanonicalConflictCountAfter: afterMetrics.CanonicalConflictCount,
                        RootConflictCountBefore: beforeMetrics.RootConflictCount,
                        RootConflictCountAfter: afterMetrics.RootConflictCount,
                        ConflictedStationCountBefore: beforeMetrics.ConflictedStationCount,
                        ConflictedStationCountAfter: afterMetrics.ConflictedStationCount,
                        ActiveSectionEdgeCountBefore: _activeSectionEdges.Count,
                        ActiveSectionEdgeCountAfter: _activeSectionEdges.Count,
                        ActiveStationEventEdgeCountBefore: _activeStationEventEdges.Count,
                        ActiveStationEventEdgeCountAfter: _activeStationEventEdges.Count,
                        TemporaryHeuristicCutCountAfter: activeCuts.Count,
                        EpisodeConflictCountBefore: beforeMetrics.EpisodeConflictCount,
                        EpisodeConflictCountAfter: afterMetrics.EpisodeConflictCount,
                        AdaptivePolicyReason: "local_micro_feasibility_repair",
                        AcceptedRecourseWindowSeconds: acceptedDiagnostic.RecourseWindowSeconds,
                        RecourseAttemptCount: repair.Diagnostics.Count,
                        TotalRetimingSeconds: acceptedDiagnostic.TotalRetimingSeconds,
                        MaximumRetimingSeconds: acceptedDiagnostic.MaximumRetimingSeconds,
                        ZeroConflictMode: true));
                    iterations[^1] = AttachIterationContinuityDiagnostics(
                        iterations[^1],
                        currentSnapshot,
                        repairedSnapshot,
                        currentReport,
                        repairedReport,
                        repairedModel,
                        accepted: true,
                        Array.Empty<string>(),
                        RepairDecisionLevel.LocalMicroExact.ToString(),
                        iterationIisTraceStartIndex);
                    expectedAcceptedSnapshotFingerprint =
                        iterations[^1].AcceptedSnapshotFingerprint;
                    conflictTrend.Add(new CheckerConflictTrendPoint(
                        afterMetrics.RootConflictCount,
                        afterMetrics.EpisodeConflictCount));
                    currentModel.Dispose();
                    currentModel = repairedModel;
                    currentSnapshot = repairedSnapshot;
                    currentReport = repairedReport;
                    SaveCheckpointIfConfigured(iterationIndex, currentSnapshot, validityFirstState);
                    string? localMicroTerminationReason = CheckerAcceptedRoundTerminationResolver.Resolve(
                        _options.FixedSnapshotMultiIterationMode,
                        currentReport.IsFeasible);
                    if (localMicroTerminationReason is not null)
                    {
                        return new CheckerLoopResult(
                            currentModel, iterations, diagnostics, activeCuts, localMicroTerminationReason,
                            selectionDiagnostics, _options.EndpointOccupationSeconds, localMicroRepairDiagnostics,
                            _recourseStageDiagnostics.ToList(), _neighborhoodEscalationDiagnostics.ToList(),
                            c0Report, currentReport, localIterationIndex);
                    }
                    continue;
                }
            }

            IReadOnlyList<MicroConflictExplanation> structuralConflictSource =
                CheckerStructuralConflictScopeResolver.Resolve(
                    currentReport.Conflicts,
                    _options.ZeroConflictMode,
                    _options.FocusStationName);
            IReadOnlyList<HeadwayConflictEpisode> episodes = HeadwayConflictEpisodeAnalyzer.Analyze(
                structuralConflictSource,
                episodePersistence);
            foreach (HeadwayConflictEpisode episode in episodes)
            {
                episodePersistence[episode.EpisodeId] = episode.PersistenceCount;
            }

            CheckerAdaptiveCutDecision adaptiveDecision = CheckerAdaptiveCutPolicy.Resolve(
                conflictTrend,
                _options.MaxNewCutsPerIteration,
                _options.StructuralRepairNeighborPadding);
            HashSet<string> activeSectionKeys = _activeSectionEdges
                .Select(edge => InitialConflictGraphBuilder.BuildCanonicalPairKey(
                    edge.SectionName, edge.FirstTrainIndex, edge.SecondTrainIndex))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            HashSet<string> activeStationKeys = _activeStationEventEdges
                .Select(BasicMilpModelBuilder.BuildStationEventPrecedenceKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            int selectionBatchSize = _options.ZeroConflictMode
                ? _options.ExactBatchCap
                : adaptiveDecision.BatchSize;
            int repairNeighborPadding = _options.ZeroConflictMode
                ? _options.StructuralRepairNeighborPadding
                : adaptiveDecision.RepairNeighborPadding;
            string selectionPolicyReason = _options.ZeroConflictMode
                ? _options.EnablePhasedFeasibilityRecovery
                    ? "validity_first_all_current_exact_feedback"
                    : "exact_zero_conflict_separation"
                : adaptiveDecision.Reason;
            bool permanentExactFeedbackAllowed = PermanentMicroFeedbackEligibilityPolicy.Allows(
                currentReport.Status,
                hasProvenCore: episodes.Count > 0);
            HeadwayConflictCandidateBatch selectedBatch = _options.ZeroConflictMode
                ? _options.EnablePhasedFeasibilityRecovery && permanentExactFeedbackAllowed
                    ? HeadwayConflictCandidateSelector.SelectAllExact(
                        episodes,
                        activeSectionKeys,
                        activeStationKeys)
                    : HeadwayConflictCandidateSelector.SelectExact(
                        episodes,
                        activeSectionKeys,
                        activeStationKeys,
                        permanentExactFeedbackAllowed ? _options.ExactBatchCap : 0,
                        _options.ExactPerStationCap,
                        _options.ExactPerPairCap)
                : HeadwayConflictCandidateSelector.Select(
                    episodes,
                    activeSectionKeys,
                    activeStationKeys,
                    permanentExactFeedbackAllowed ? adaptiveDecision.BatchSize : 0);
            if (_options.ZeroConflictMode && _options.EnablePhasedFeasibilityRecovery)
            {
                selectionBatchSize = selectedBatch.Candidates.Count;
            }
            HeadwayConflictEdgeExpansion expansion = selectedBatch.Expansion;
            IReadOnlyList<HeadwayConflictCandidate> acceptedStructuralCandidates = selectedBatch.Candidates;
            selectionDiagnostics.AddRange(selectedBatch.Candidates.Select(candidate => new CheckerCutSelectionDiagnostic(
                iterationIndex,
                candidate.CandidateKey,
                candidate.EpisodeId,
                candidate.ResourceKey,
                candidate.FirstTrainIndex,
                candidate.SecondTrainIndex,
                candidate.Score,
                candidate.PersistenceScore,
                candidate.CoverageScore,
                candidate.PropagationScore,
                candidate.ConflictGraphScore,
                candidate.ScarcityScore,
                selectionBatchSize,
                repairNeighborPadding,
                selectionPolicyReason,
                candidate.StationEdge?.StationName ??
                    episodes.First(episode => string.Equals(
                        episode.EpisodeId, candidate.EpisodeId, StringComparison.OrdinalIgnoreCase))
                        .StationNames.FirstOrDefault() ?? candidate.ResourceKey,
                episodes.First(episode => string.Equals(
                    episode.EpisodeId, candidate.EpisodeId, StringComparison.OrdinalIgnoreCase))
                    .ConflictRowCount)));
            int sectionEdgeCountBefore = _activeSectionEdges.Count;
            int stationEdgeCountBefore = _activeStationEventEdges.Count;
            IReadOnlyList<SectionConflictEdgeRequest> baselineSectionEdges = _activeSectionEdges.ToList();
            IReadOnlyList<StationEventConflictCandidate> baselineStationEdges = _activeStationEventEdges.ToList();
            IReadOnlyList<ValidatedMicroFeedback> validatedPermanentBatch =
                _options.CoordinationStrategy == CheckerCoordinationStrategy.ValidityFirstBenders
                    ? selectedBatch.Candidates
                        .Select(ValidatedMicroFeedbackFactory.FromExactHeadwayCandidate)
                        .Where(feedback => ValidatedMicroFeedbackPolicy.ResolvePersistence(feedback) ==
                                           MicroFeedbackPersistence.Permanent)
                        .ToList()
                    : Array.Empty<ValidatedMicroFeedback>();
            int newEdgeCount = AddNewHeadwayEdges(expansion);
            if (newEdgeCount > 0 &&
                _options.CoordinationStrategy == CheckerCoordinationStrategy.ValidityFirstBenders)
            {
                foreach (ValidatedMicroFeedback feedback in validatedPermanentBatch)
                {
                    _permanentFeedbackLedger.TryAdd(feedback, iterationIndex);
                }
                // 在增强 Master 启动前落盘证明来源、数学形式和适用条件，超时或外部中断也不会丢失本批反馈审计。
                WriteValidityFirstFeedbackLedger(
                    _permanentFeedbackLedger.Items,
                    Path.Combine(
                        ResolveValidityFirstOutputDirectory(),
                        "validity_first_feedback_ledger.csv"));

                PromotedMicroComponent? combinedHotspotComponent = null;
                HashSet<int> selectedHotspotCoreTrainIndices = [];
                int? selectedHotspotDiagnosticIndex = null;
                string? selectedRepairAttemptFingerprint = null;
                ValidityFirstHotspotEscalationDecision escalationDecision =
                    ValidityFirstHotspotEscalationPolicy.Decide(
                        microFeasible: false,
                        newPermanentFeedbackCount: validatedPermanentBatch.Count,
                        consecutiveFeedbackOnlyRounds,
                        _options.ValidityFirstMaximumFeedbackOnlyRounds,
                        requireJointRepairScope: _options.EnablePhasedFeasibilityRecovery);
                if (escalationDecision.MustEvaluateHotspot)
                {
                    StationLevelMicroFeasibilitySlaveResult currentSlaveResult = microSlave.Solve(currentModel);
                    IReadOnlyList<ValidityFirstHotspotCandidate> hotspotCandidates =
                        ResolveValidityFirstHotspotCandidates(
                            currentSlaveResult,
                            currentReport,
                            currentAssignmentCoreOccurrences,
                            stationHotspotPersistence,
                            validityFirstMigrationPriorityStations,
                            !_validityFirstSavedRepairNeighborhoodConsumed
                                ? _options.ValidityFirstReplayHotspotStationName
                                : null,
                            requireJointRepairScope: _options.EnablePhasedFeasibilityRecovery);
                    if (hotspotCandidates.Count == 0)
                    {
                        hotspotAttemptDiagnostics.Add(new ValidityFirstHotspotAttemptDiagnostic(
                            iterationIndex,
                            consecutiveFeedbackOnlyRounds,
                            escalationDecision.Reason,
                            "none",
                            string.Empty,
                            string.Empty,
                            true,
                            false,
                            "no_persistent_assignment_or_correlated_exact_headway_hotspot",
                            0,
                            0,
                            false,
                            false,
                            false,
                            "not_started",
                            0,
                            null,
                            currentReport.Conflicts.Count,
                            null,
                            0.0,
                            0.0));
                    }

                    foreach (ValidityFirstHotspotCandidate hotspotCandidate in hotspotCandidates)
                    {
                        Stopwatch buildStopwatch = Stopwatch.StartNew();
                        ValidityFirstHotspotBuildResult hotspotBuild =
                            BuildValidityFirstHotspotComponent(
                                currentModel,
                                hotspotCandidate.StationResult,
                                iterationIndex,
                                hotspotCandidate.PersistentRounds);
                        buildStopwatch.Stop();
                        ValidityFirstHotspotDomainAuditReporter.Append(
                            iterationIndex,
                            hotspotCandidate.StationResult.StationName,
                            hotspotBuild.DomainAudits,
                            ResolveValidityFirstOutputDirectory());
                        hotspotAttemptDiagnostics.Add(new ValidityFirstHotspotAttemptDiagnostic(
                            iterationIndex,
                            consecutiveFeedbackOnlyRounds,
                            escalationDecision.Reason,
                            hotspotCandidate.Priority,
                            hotspotCandidate.StationResult.StationName,
                            hotspotCandidate.AssignmentCoreIdentity,
                            true,
                            hotspotBuild.IsComplete,
                            hotspotBuild.Status,
                            hotspotBuild.CandidateRequirementCount,
                            hotspotBuild.ClosureRequirementCount,
                            hotspotBuild.IsComplete && hotspotBuild.Component is not null,
                            false,
                            false,
                            "not_started",
                            hotspotCandidate.StationResult.Conflicts.Count,
                            null,
                            currentReport.Conflicts.Count,
                            null,
                            buildStopwatch.Elapsed.TotalSeconds,
                            0.0,
                            CoreRequirementCount: hotspotBuild.CoreRequirementIds?.Count ?? 0,
                            ProtectionRequirementCount: hotspotBuild.ProtectionRequirementIds?.Count ?? 0,
                            AlternativeCount: hotspotBuild.ScopeEstimate?.AlternativeCount ?? 0,
                            EstimatedVariableCount: hotspotBuild.ScopeEstimate?.EstimatedVariableCount ?? 0,
                            EstimatedConstraintCount: hotspotBuild.ScopeEstimate?.EstimatedConstraintCount ?? 0,
                            ResourceConflictRelationCount: hotspotBuild.ScopeEstimate?.ResourceConflictRelationCount ?? 0,
                            TrainCount: hotspotBuild.TrainCount,
                            OrderVariableCount: hotspotBuild.ScopeEstimate?.OrderVariableCount ?? 0,
                            LegacyEstimatedVariableCount: hotspotBuild.ScopeEstimate?.LegacyEstimatedVariableCount ?? 0,
                            LegacyOrderVariableCount: hotspotBuild.ScopeEstimate?.LegacyOrderVariableCount ?? 0));
                        if (!hotspotBuild.IsComplete || hotspotBuild.Component is null)
                        {
                            continue;
                        }

                        if (_options.EnablePhasedFeasibilityRecovery)
                        {
                            string attemptFingerprint = ValidityFirstRepairAttemptKeyBuilder.Build(
                                SnapshotFingerprint.Calculate(validityFirstRepairReference),
                                _permanentFeedbackLedger.Items.Select(item => item.FeedbackId).ToList(),
                                hotspotBuild.ScopeFingerprint,
                                ValidityFirstSolveParameterPolicy.Resolve("hotspot_joint_master", _options));
                            int diagnosticIndex = hotspotAttemptDiagnostics.Count - 1;
                            hotspotAttemptDiagnostics[diagnosticIndex] =
                                hotspotAttemptDiagnostics[diagnosticIndex] with
                                {
                                    RepairAttemptFingerprint = attemptFingerprint
                                };
                            if (!validityFirstRepairAttemptRegistry.ShouldAttempt(attemptFingerprint))
                            {
                                hotspotAttemptDiagnostics[diagnosticIndex] =
                                    hotspotAttemptDiagnostics[diagnosticIndex] with
                                    {
                                        PromotionPersisted = false,
                                        BuildStatus = $"duplicate_failed_attempt_skipped::{attemptFingerprint}"
                                    };
                                continue;
                            }

                            selectedRepairAttemptFingerprint = attemptFingerprint;
                        }

                        combinedHotspotComponent = hotspotBuild.Component;
                        HashSet<string> coreRequirementIds = (hotspotBuild.CoreRequirementIds ?? [])
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
                        selectedHotspotCoreTrainIndices = combinedHotspotComponent.Requirements
                            .Where(requirement => coreRequirementIds.Contains(requirement.RequirementId))
                            .Select(requirement => requirement.TrainIndex)
                            .ToHashSet();
                        if (selectedHotspotCoreTrainIndices.Count == 0)
                        {
                            selectedHotspotCoreTrainIndices.UnionWith(combinedHotspotComponent.TrainIndices);
                        }
                        _promotedMicroComponentStore.Upsert(combinedHotspotComponent);
                        selectedHotspotDiagnosticIndex = hotspotAttemptDiagnostics.Count - 1;
                        break;
                    }
                    ValidityFirstHotspotAttemptReporter.Write(
                        hotspotAttemptDiagnostics,
                        ResolveValidityFirstOutputDirectory());
                }

                if (_options.EnablePhasedFeasibilityRecovery && combinedHotspotComponent is null)
                {
                    string scopeStatus = hotspotAttemptDiagnostics.Count == 0
                        ? "no_hotspot_candidate"
                        : hotspotAttemptDiagnostics[^1].BuildStatus;
                    return new CheckerLoopResult(
                        currentModel,
                        iterations,
                        diagnostics,
                        activeCuts,
                        $"validity_first_repair_scope_unavailable::{scopeStatus}",
                        selectionDiagnostics,
                        _options.EndpointOccupationSeconds,
                        localMicroRepairDiagnostics,
                        _recourseStageDiagnostics.ToList(),
                        _neighborhoodEscalationDiagnostics.ToList(),
                        c0Report,
                        currentReport,
                        localIterationIndex,
                        WorkingSolution: validityFirstState.WorkingSolution,
                        BestDiagnosticSolution: validityFirstState.BestDiagnosticSolution,
                        BestJointFeasibleSolution: validityFirstState.BestJointFeasibleSolution,
                        CoordinationStrategy: _options.CoordinationStrategy,
                        ConfigurationFingerprint: ValidityFirstConfigurationFingerprint.Calculate(_options),
                        PermanentMicroFeedback: _permanentFeedbackLedger.Items);
                }

                MicroConflictMetrics beforeMetrics =
                    MicroConflictMetricCalculator.Calculate(currentReport.Conflicts);
                MasterSolutionSnapshot strengthenedRepairReference = validityFirstRepairReference;
                // 新增有效约束后，旧 incumbent 不再自动属于增强 Master 的可行域。
                validityFirstState.InvalidateWorkingSolution();
                _permanentFeedbackLedger.MarkAppliedToModel(
                    _permanentFeedbackLedger.Items.Select(item => item.FeedbackId),
                    iterationIndex);
                WriteValidityFirstFeedbackLedger(
                    _permanentFeedbackLedger.Items,
                    Path.Combine(
                        ResolveValidityFirstOutputDirectory(),
                        "validity_first_feedback_ledger.csv"));
                string strengthenedPhase = combinedHotspotComponent is null
                    ? "feedback_batch"
                    : "hotspot_joint_master";
                HashSet<int> endpointReleaseEvidenceTrains = currentReport.Conflicts
                    .SelectMany(conflict => new[] { conflict.FirstTrainIndex, conflict.SecondTrainIndex })
                    .Where(trainIndex => trainIndex.HasValue)
                    .Select(trainIndex => trainIndex!.Value)
                    .ToHashSet();
                if (combinedHotspotComponent is not null)
                {
                    endpointReleaseEvidenceTrains.UnionWith(combinedHotspotComponent.TrainIndices);
                }

                HashSet<string> releasedMicroStations = combinedHotspotComponent is null
                    ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(
                        [combinedHotspotComponent.StationName],
                        StringComparer.OrdinalIgnoreCase);
                BasicMilpModel strengthenedModel = null!;
                bool strengthenedSolveStarted = false;
                PrecedenceTrustRegion? appliedEndpointTrustRegion = null;
                int appliedEndpointOrderChangeBudget = 0;
                HashSet<int> directlyRelatedTrainIndices = selectedHotspotCoreTrainIndices.ToHashSet();
                foreach (MicroConflictExplanation conflict in currentReport.Conflicts)
                {
                    if (!conflict.FirstTrainIndex.HasValue || !conflict.SecondTrainIndex.HasValue)
                    {
                        continue;
                    }
                    int firstTrain = conflict.FirstTrainIndex.Value;
                    int secondTrain = conflict.SecondTrainIndex.Value;
                    if (selectedHotspotCoreTrainIndices.Contains(firstTrain) ||
                        selectedHotspotCoreTrainIndices.Contains(secondTrain))
                    {
                        directlyRelatedTrainIndices.Add(firstTrain);
                        directlyRelatedTrainIndices.Add(secondTrain);
                    }
                }
                IReadOnlyList<ValidityFirstRepairNeighborhoodPlan> neighborhoodPlans =
                    _options.EnablePhasedFeasibilityRecovery
                        ? BuildValidityFirstRepairNeighborhoodPlans(
                            strengthenedRepairReference,
                            selectedHotspotCoreTrainIndices,
                            directlyRelatedTrainIndices,
                            endpointReleaseEvidenceTrains,
                            combinedHotspotComponent)
                        :
                        [
                            new ValidityFirstRepairNeighborhoodPlan(
                                "legacy_single_master", 0,
                                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                                _options.TimeLimitSecondsPerSolve,
                                ValidityFirstNeighborhoodTransitionKind.InitialRestricted,
                                "未启用分阶段恢复时保持原有单次增强 Master 调度。",
                                false)
                        ];
                neighborhoodPlans = ApplyRepairNeighborhoodSelector(
                    strengthenedRepairReference,
                    currentReport,
                    neighborhoodPlans,
                    combinedHotspotComponent,
                    iterationIndex,
                    "strengthened_master");
                neighborhoodPlans = ApplyRepairActionSelector(
                    strengthenedRepairReference,
                    currentReport,
                    neighborhoodPlans,
                    strengthenedRepairReference.VerifiedMicroAssignments ?? [],
                    false,
                    combinedHotspotComponent is null
                        ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                        : new HashSet<string>([combinedHotspotComponent.StationName],
                            StringComparer.OrdinalIgnoreCase),
                    iterationIndex,
                    "strengthened_master");
                MicroCoordinationRoundContext coordinationRound =
                    _options.MicroCoordinationMode == MicroCoordinationMode.D0
                        ? MicroCoordinationRuntime.DisabledRound(strengthenedRepairReference)
                        : MicroCoordinationRuntime.PrepareRound(
                            currentModel,
                            strengthenedRepairReference,
                            _options.EndpointOccupationSeconds,
                            iterationIndex);
                if (_options.MicroCoordinationMode != MicroCoordinationMode.D0)
                {
                    foreach (CompleteStationMicroComponentBuildResult componentBuild in
                             coordinationRound.ReopeningComponents.Where(build => build.IsComplete && build.Component is not null))
                    {
                        _promotedMicroComponentStore.Upsert(componentBuild.Component! with { Active = false });
                    }
                }
                int previousStatusCode = GRB.Status.LOADED;
                int previousSolutionCount = 0;
                bool previousReliableIisMembers = false;
                for (int neighborhoodIndex = 0;
                     neighborhoodIndex < neighborhoodPlans.Count;
                     neighborhoodIndex++)
                {
                    ValidityFirstRepairNeighborhoodPlan plan = neighborhoodPlans[neighborhoodIndex];
                    MicroCoordinationBranchDecision coordinationDecision = MicroCoordinationRuntime.Decide(
                        _instance, coordinationRound, plan, _options.MicroCoordinationMode);
                    _promotedMicroComponentStore.SetCoordinationReopenedStations(
                        coordinationDecision.ReopenedStations);
                    HashSet<string> branchReleasedMicroStations = new(
                        releasedMicroStations,
                        StringComparer.OrdinalIgnoreCase);
                    branchReleasedMicroStations.UnionWith(coordinationDecision.ReopenedStations);
                    appliedEndpointOrderChangeBudget = plan.EndpointOrderChangeBudget;
                    ValidityFirstNeighborhoodTransitionKind actualTransition = neighborhoodIndex == 0
                        ? ValidityFirstNeighborhoodTransitionKind.InitialRestricted
                        : ValidityFirstNeighborhoodTransitionPolicy.Resolve(
                            previousStatusCode,
                            previousSolutionCount,
                            previousReliableIisMembers,
                            plan.ReleasesAllVerifiedAssignments);
                    PrecedenceTrustRegion? endpointTrustRegion =
                        _options.EnablePhasedFeasibilityRecovery
                            ? ValidityFirstEndpointOrderTrustRegionBuilder.Build(
                                _instance,
                                strengthenedRepairReference,
                                endpointReleaseEvidenceTrains,
                                plan.EndpointOrderChangeBudget,
                                $"validity_endpoint_i{iterationIndex}_{plan.NeighborhoodId}")
                            : null;
                    appliedEndpointTrustRegion = endpointTrustRegion;
                    strengthenedModel = BuildValidityFirstModel(
                        activeCuts,
                        iterationIndex,
                        (combinedHotspotComponent is null
                            ? "feedback_batch_model_construction"
                            : "hotspot_joint_master_model_construction") +
                        $"_{plan.NeighborhoodId}",
                        endpointTrustRegion);
                    MicroCertificateBindingResult certificateBinding =
                        MicroCertificateModelBinder.Apply(
                            strengthenedModel,
                            coordinationDecision.RetainedCertificates);
                    VerifiedMicroAssignmentMipStartResult microStartResult =
                        MasterSolutionSnapshotApplier.ApplyMipStart(
                            strengthenedModel,
                            _options.EnablePhasedFeasibilityRecovery
                                ? strengthenedRepairReference
                                : currentSnapshot);
                    ValidityFirstTemporaryFixationAudit temporaryAudit = new(
                        plan.NeighborhoodId,
                        actualTransition.ToString(),
                        plan.Reason,
                        [],
                        plan.ReleasedRequirementIds.Order(StringComparer.OrdinalIgnoreCase).ToList(),
                        plan.EndpointOrderChangeBudget,
                        plan.RequestedSolveSeconds);
                    IReadOnlyList<VerifiedMicroAssignmentWitness> strengthenedFixationWitnesses =
                        _options.RepairActionSelector == RepairActionSelectorKind.RuleBased &&
                        plan.ActionType != RepairActionType.LegacyFallback
                            ? RepairActionCurrentWitnessPolicy.SelectCurrent(
                                strengthenedRepairReference.VerifiedMicroAssignments ?? [],
                                strengthenedRepairReference)
                            : strengthenedRepairReference.VerifiedMicroAssignments ?? [];
                    VerifiedMicroAssignmentTemporaryFixResult temporaryFixResult =
                        _options.EnablePhasedFeasibilityRecovery
                            ? VerifiedMicroAssignmentTemporaryFixer.Apply(
                                strengthenedModel,
                                strengthenedFixationWitnesses,
                                branchReleasedMicroStations,
                                plan.ReleasedRequirementIds,
                                temporaryAudit)
                            : new VerifiedMicroAssignmentTemporaryFixResult(0, 0, 0);
                    MicroCoordinationTraceReporter.Append(
                        ResolveValidityFirstOutputDirectory(),
                        iterationIndex,
                        plan,
                        coordinationDecision,
                        certificateBinding,
                        strengthenedModel,
                        microStartResult);
                    ValidityFirstVerifiedStructureFixationResult? timeActionStructureFixation = null;
                    if (_options.EnablePhasedFeasibilityRecovery &&
                        plan.ActionType == RepairActionType.TimeRetiming)
                    {
                        timeActionStructureFixation = ValidityFirstVerifiedStructureFixer.Apply(
                            strengthenedModel,
                            strengthenedRepairReference,
                            RepairActionVerifiedStructureBuilder.Build(strengthenedFixationWitnesses),
                            _options.EndpointOccupationSeconds,
                            allowRetimingOfCurrentOrderViolations: true);
                    }
                    RepairBranchModelIdentity branchIdentity = CaptureRepairBranchModelIdentity(
                        strengthenedModel,
                        currentSnapshot,
                        strengthenedRepairReference,
                        plan,
                        temporaryFixResult,
                        strengthenedPhase);
                    RepairBranchExecutionDecision branchDecision = repairBranchAttemptRegistry.Evaluate(branchIdentity);
                    if (!branchDecision.ShouldExecute)
                    {
                        AppendRepairBranchRuntimeState(
                            iterationIndex,
                            strengthenedPhase,
                            plan,
                            temporaryFixResult,
                            branchIdentity,
                            RepairBranchExecutionState.MappedToEquivalentBranch,
                            "not_started",
                            branchDecision.Reason,
                            mappedToPlanId: branchDecision.MappedToPlanId);
                        ValidityFirstRepairNeighborhoodReporter.Append(
                            ResolveValidityFirstOutputDirectory(),
                            iterationIndex,
                            neighborhoodIndex + 1,
                            plan,
                            actualTransition,
                            temporaryFixResult,
                            false,
                            "mapped_to_equivalent_branch",
                            0,
                            null,
                            null);
                        if (neighborhoodIndex + 1 < neighborhoodPlans.Count)
                        {
                            strengthenedModel.Dispose();
                            strengthenedModel = null!;
                            continue;
                        }
                        break;
                    }
                    if (_options.EnablePhasedFeasibilityRecovery)
                    {
                        Console.WriteLine(
                            $"Validity-first Micro MIP Start: phase={strengthenedPhase}, iteration={iterationIndex}, " +
                            $"neighborhood={plan.NeighborhoodId}, transition={actualTransition}, " +
                            $"endpoint_order_k={plan.EndpointOrderChangeBudget}, witnesses={microStartResult.WitnessCount}, " +
                            $"matched_requirements={microStartResult.MatchedRequirementCount}, " +
                            $"started_choice_variables={microStartResult.StartedChoiceVariableCount}, " +
                            $"started_order_variables={microStartResult.StartedOrderVariableCount}, " +
                            $"unstarted_order_variables={microStartResult.UnstartedOrderVariableCount}, " +
                            $"started_stop_products={microStartResult.StartedStopProductVariableCount}, " +
                            $"started_reversals={microStartResult.StartedReversalVariableCount}, " +
                            $"started_being_overtaken={microStartResult.StartedBeingOvertakenVariableCount}, " +
                            $"coverage={microStartResult.CoverageRatio:R}, " +
                            $"temporarily_fixed_requirements={temporaryFixResult.FixedRequirementCount}, " +
                            $"temporarily_released_requirements={temporaryFixResult.ReleasedRequirementIds?.Count ?? 0}, " +
                            $"temporarily_fixed_choice_variables={temporaryFixResult.FixedChoiceVariableCount}, " +
                            $"action_fixed_section_orders={timeActionStructureFixation?.FixedSectionPrecedenceCount ?? 0}, " +
                            $"action_fixed_station_orders={timeActionStructureFixation?.FixedStationEventPrecedenceCount ?? 0}, " +
                            $"action_fixed_track_orders={timeActionStructureFixation?.FixedTrackOrderCount ?? 0}, " +
                            $"action_fixed_route_orders={timeActionStructureFixation?.FixedRouteOrderCount ?? 0}, " +
                            $"requested_seconds={plan.RequestedSolveSeconds:R}, reason={plan.Reason}.");
                    }
                    _lastValidityFirstEnhancedMasterDiagnostic = null;
                    AppendRepairBranchRuntimeState(
                        iterationIndex,
                        strengthenedPhase,
                        plan,
                        temporaryFixResult,
                        branchIdentity,
                        RepairBranchExecutionState.Running,
                        "running",
                        "optimize_started");
                    try
                    {
                        strengthenedSolveStarted = TryOptimizeValidityFirstMaster(
                            strengthenedModel,
                            iterationIndex,
                            strengthenedPhase,
                            plan.RequestedSolveSeconds,
                            strengthenedRepairReference);
                    }
                    catch (Exception exception)
                    {
                        AppendRepairBranchRuntimeState(
                            iterationIndex,
                            strengthenedPhase,
                            plan,
                            temporaryFixResult,
                            branchIdentity,
                            RepairBranchExecutionState.EngineeringFailure,
                            "exception",
                            $"{exception.GetType().Name}: {exception.Message}");
                        throw;
                    }
                    ValidityFirstEnhancedMasterSolveDiagnostic? solveDiagnostic =
                        _lastValidityFirstEnhancedMasterDiagnostic;
                    int statusCode = strengthenedSolveStarted
                        ? strengthenedModel.Model.Get(GRB.IntAttr.Status)
                        : GRB.Status.LOADED;
                    int solutionCount = strengthenedModel.Model.Get(GRB.IntAttr.SolCount);
                    if (strengthenedSolveStarted)
                    {
                        repairBranchAttemptRegistry.RecordStarted(branchIdentity);
                    }
                    AppendRepairBranchRuntimeState(
                        iterationIndex,
                        strengthenedPhase,
                        plan,
                        temporaryFixResult,
                        branchIdentity,
                        ResolveRepairBranchTerminalState(strengthenedSolveStarted, statusCode, solutionCount),
                        strengthenedSolveStarted ? strengthenedModel.StatusName : "budget_insufficient",
                        strengthenedSolveStarted ? "optimize_finished" : "global_budget_reserved",
                        solveDiagnostic?.RuntimeSeconds);
                    ValidityFirstRepairNeighborhoodReporter.Append(
                        ResolveValidityFirstOutputDirectory(),
                        iterationIndex,
                        neighborhoodIndex + 1,
                        plan,
                        actualTransition,
                        temporaryFixResult,
                        strengthenedSolveStarted,
                        strengthenedSolveStarted ? strengthenedModel.StatusName : "budget_insufficient",
                        solutionCount,
                        solveDiagnostic?.RuntimeSeconds,
                        solveDiagnostic?.IisDiagnostic);
                    if (strengthenedModel.HasIncumbentSolution ||
                        !_options.EnablePhasedFeasibilityRecovery)
                    {
                        break;
                    }
                    if (neighborhoodIndex + 1 >= neighborhoodPlans.Count ||
                        !ValidityFirstNeighborhoodTransitionPolicy.CanTryNext(statusCode, solutionCount))
                    {
                        break;
                    }

                    Console.WriteLine(
                        $"Validity-first switches bounded repair neighborhood: iteration={iterationIndex}, " +
                        $"previous={plan.NeighborhoodId}, status={strengthenedModel.StatusName}, " +
                        $"next={neighborhoodPlans[neighborhoodIndex + 1].NeighborhoodId}; " +
                        "this changes only temporary search restrictions and creates no permanent cut or proof.");
                    previousStatusCode = statusCode;
                    previousSolutionCount = solutionCount;
                    // 当前 IIS 成员尚未建立到“应释放哪一个 Micro 见证”的逐对象证明映射，
                    // 因此即使 IIS 可读，本次按冲突列车形成的释放组仍如实标记为启发式切换。
                    previousReliableIisMembers = false;
                    strengthenedModel.Dispose();
                }

                if (selectedHotspotDiagnosticIndex.HasValue)
                {
                    int diagnosticIndex = selectedHotspotDiagnosticIndex.Value;
                    hotspotAttemptDiagnostics[diagnosticIndex] = hotspotAttemptDiagnostics[diagnosticIndex] with
                    {
                        JointSolveStarted = strengthenedSolveStarted,
                        JointSolveHasIncumbent = strengthenedModel.HasIncumbentSolution,
                        JointSolverStatus = strengthenedSolveStarted
                            ? strengthenedModel.StatusName
                            : "budget_insufficient"
                    };
                    ValidityFirstHotspotAttemptReporter.Write(
                        hotspotAttemptDiagnostics,
                        ResolveValidityFirstOutputDirectory());
                }

                if (!strengthenedModel.HasIncumbentSolution)
                {
                    if (selectedRepairAttemptFingerprint is not null)
                    {
                        validityFirstRepairAttemptRegistry.RecordFailure(selectedRepairAttemptFingerprint);
                    }
                    string noIncumbentReason = !strengthenedSolveStarted
                        ? "validity_first_global_budget_reserved_no_solve"
                        : strengthenedModel.Model.Get(GRB.IntAttr.Status) ==
                                               GRB.Status.INFEASIBLE
                        ? "valid_strengthened_master_proven_infeasible"
                        : "valid_strengthened_master_unknown_no_incumbent";
                    iterations.Add(new CheckerLoopIteration(
                        iterationIndex,
                        currentSnapshot.ObjectiveValue,
                        currentSnapshot.ObjectiveValue,
                        false,
                        currentReport.Conflicts.Count,
                        currentReport.Conflicts.Count,
                        newEdgeCount,
                        newEdgeCount,
                        strengthenedModel.StatusName,
                        noIncumbentReason,
                        BuildCandidateSummary(selectedBatch.Candidates),
                        string.Join('|', selectedBatch.Candidates
                            .Select(candidate => candidate.Evidence?.StationName ?? candidate.ResourceKey)
                            .Distinct(StringComparer.OrdinalIgnoreCase)),
                        ZeroConflictMode: true));
                    currentModel.Dispose();
                    currentModel = strengthenedModel;
                    return new CheckerLoopResult(
                        currentModel,
                        iterations,
                        diagnostics,
                        activeCuts,
                        noIncumbentReason,
                        selectionDiagnostics,
                        _options.EndpointOccupationSeconds,
                        localMicroRepairDiagnostics,
                        _recourseStageDiagnostics.ToList(),
                        _neighborhoodEscalationDiagnostics.ToList(),
                        c0Report,
                        currentReport,
                        localIterationIndex,
                        WorkingSolution: validityFirstState.WorkingSolution,
                        BestDiagnosticSolution: validityFirstState.BestDiagnosticSolution,
                        BestJointFeasibleSolution: validityFirstState.BestJointFeasibleSolution,
                        CoordinationStrategy: _options.CoordinationStrategy,
                        ConfigurationFingerprint: ValidityFirstConfigurationFingerprint.Calculate(_options),
                        PermanentMicroFeedback: _permanentFeedbackLedger.Items);
                }

                MasterValidationReport strengthenedValidation = RunTimedMasterValidation(
                    strengthenedModel,
                    iterationIndex,
                    strengthenedPhase);
                MicroFeasibilityReport strengthenedReport = RunTimedGlobalMicroValidation(
                    microChecker,
                    strengthenedModel,
                    iterationIndex,
                    strengthenedPhase);
                MicroConflictMetrics afterMetrics =
                    MicroConflictMetricCalculator.Calculate(strengthenedReport.Conflicts);
                if (_options.EnablePhasedFeasibilityRecovery)
                {
                    ValidityFirstConflictMigration migration =
                        ValidityFirstConflictMigrationAnalyzer.Analyze(
                            iterationIndex,
                            currentReport,
                            strengthenedReport);
                    ValidityFirstConflictMigrationReporter.Append(
                        migration,
                        ResolveValidityFirstOutputDirectory());
                    validityFirstMigrationPriorityStations.Clear();
                    validityFirstMigrationPriorityStations.UnionWith(migration.PriorityStationNames);
                }
                TimetableSolutionRole solutionRole =
                    strengthenedValidation.IsFeasible && strengthenedReport.IsFeasible
                        ? TimetableSolutionRole.BestJointFeasibleSolution
                        : TimetableSolutionRole.BestDiagnosticSolution;
                MasterSolutionSnapshot unclassifiedSnapshot =
                    MasterSolutionSnapshot.Capture(strengthenedModel);
                if (_options.EnablePhasedFeasibilityRecovery)
                {
                    unclassifiedSnapshot = AttachVerifiedMicroAssignments(
                        strengthenedModel,
                        unclassifiedSnapshot,
                        microSlave,
                        iterationIndex,
                        strengthenedPhase);
                    if (appliedEndpointTrustRegion is not null)
                    {
                        ValidityFirstEndpointOrderAuditReporter.Append(
                            iterationIndex,
                            strengthenedPhase,
                            appliedEndpointOrderChangeBudget,
                            appliedEndpointTrustRegion,
                            strengthenedRepairReference,
                            _validityFirstImmutableInitialReference!,
                            unclassifiedSnapshot,
                            ResolveValidityFirstOutputDirectory());
                    }
                }
                OptimizationBoundReport boundReport = OptimizationBoundPolicy.Evaluate(
                    solutionRole,
                    SolverBoundScope.GlobalStrengthenedMaster,
                    strengthenedValidation.IsFeasible,
                    strengthenedReport.IsFeasible,
                    unclassifiedSnapshot.BusinessObjective!,
                    solverLowerBound: null);
                MasterSolutionSnapshot strengthenedSnapshot = unclassifiedSnapshot with
                {
                    SolutionRole = solutionRole,
                    BoundReport = boundReport
                };
                strengthenedSnapshot = validityFirstState.Register(
                    strengthenedSnapshot,
                    strengthenedValidation.IsFeasible,
                    strengthenedReport.IsFeasible,
                    afterMetrics.RootConflictCount,
                    SolverBoundScope.GlobalStrengthenedMaster,
                    globalLowerBound: null);
                bool strengthenedImprovesRepairReference =
                    afterMetrics.RootConflictCount < validityFirstRepairReferenceRootConflictCount ||
                    afterMetrics.RootConflictCount == validityFirstRepairReferenceRootConflictCount &&
                    ValidityFirstSolutionStateTracker.IsBusinessObjectiveBetter(
                        strengthenedSnapshot.BusinessObjective!,
                        validityFirstRepairReference.BusinessObjective!);
                if (_options.EnablePhasedFeasibilityRecovery && strengthenedValidation.IsFeasible &&
                    strengthenedImprovesRepairReference)
                {
                    validityFirstRepairReference = strengthenedSnapshot;
                    validityFirstRepairReferenceRootConflictCount = afterMetrics.RootConflictCount;
                }
                else if (_options.EnablePhasedFeasibilityRecovery &&
                         selectedRepairAttemptFingerprint is not null &&
                         !strengthenedReport.IsFeasible)
                {
                    validityFirstRepairAttemptRegistry.RecordFailure(selectedRepairAttemptFingerprint);
                }
                if (_options.EnablePhasedFeasibilityRecovery)
                {
                    ValidityFirstRecoveryRoundReporter.Append(
                        iterationIndex,
                        strengthenedPhase,
                        strengthenedModel,
                        _validityFirstImmutableInitialReference!,
                        strengthenedRepairReference,
                        currentSnapshot,
                        strengthenedSnapshot,
                        beforeMetrics.RootConflictCount,
                        afterMetrics.RootConflictCount,
                        strengthenedImprovesRepairReference && strengthenedValidation.IsFeasible,
                        _permanentFeedbackLedger.Items.Count,
                        combinedHotspotComponent?.ComponentId ?? string.Empty,
                        ResolveValidityFirstOutputDirectory());
                }

                string strengthenedSnapshotFingerprint = SnapshotFingerprint.Calculate(strengthenedSnapshot);
                IReadOnlyList<ValidityFirstFeedbackRecurrenceDiagnostic> feedbackRecurrenceDiagnostics =
                    BuildValidityFirstFeedbackRecurrenceDiagnostics(strengthenedReport, iterationIndex);
                ValidityFirstFeedbackConsistencyReporter.Append(
                    strengthenedPhase,
                    feedbackRecurrenceDiagnostics,
                    ResolveValidityFirstOutputDirectory());
                if (_options.EnablePhasedFeasibilityRecovery &&
                    feedbackRecurrenceDiagnostics.Any(item => item.RequiresInvestigation))
                {
                    throw new InvalidOperationException(
                        "A returned enhanced-Master incumbent repeats a fully modeled forbidden state " +
                        "under the same applicability conditions. See validity_first_feedback_recurrence.csv.");
                }
                HashSet<string> inconsistentFeedbackIds = feedbackRecurrenceDiagnostics
                    .Where(item => item.RequiresInvestigation)
                    .Select(item => item.FeedbackId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (ValidatedMicroFeedback feedback in _permanentFeedbackLedger.Items)
                {
                    _permanentFeedbackLedger.MarkValidatedBySnapshot(
                        [feedback.FeedbackId],
                        iterationIndex,
                        strengthenedSnapshotFingerprint,
                        strengthenedValidation.IsFeasible &&
                        !inconsistentFeedbackIds.Contains(feedback.FeedbackId));
                }
                WriteValidityFirstFeedbackLedger(
                    _permanentFeedbackLedger.Items,
                    Path.Combine(
                        ResolveValidityFirstOutputDirectory(),
                        "validity_first_feedback_ledger.csv"));
                currentAssignmentCoreOccurrences = assignmentCorePersistence.Observe(
                    AssignmentCoreIdentityAnalyzer.Analyze(strengthenedReport));
                AssignmentCoreIdentityReporter.Write(
                    iterationIndex,
                    strengthenedPhase,
                    currentAssignmentCoreOccurrences,
                    ResolveValidityFirstOutputDirectory());

                if (selectedHotspotDiagnosticIndex.HasValue && combinedHotspotComponent is not null)
                {
                    Stopwatch localMicroStopwatch = Stopwatch.StartNew();
                    StationLevelMicroFeasibilitySlaveResult postHotspotLocal = microSlave.Solve(
                        strengthenedModel,
                        new MicroFeasibilitySlaveRequest(
                            new HashSet<string>(
                                [combinedHotspotComponent.StationName],
                                StringComparer.OrdinalIgnoreCase)));
                    localMicroStopwatch.Stop();
                    int diagnosticIndex = selectedHotspotDiagnosticIndex.Value;
                    hotspotAttemptDiagnostics[diagnosticIndex] = hotspotAttemptDiagnostics[diagnosticIndex] with
                    {
                        LocalConflictsAfter = postHotspotLocal.Conflicts.Count,
                        GlobalConflictsAfter = strengthenedReport.Conflicts.Count,
                        LocalMicroSeconds = localMicroStopwatch.Elapsed.TotalSeconds
                    };
                    ValidityFirstHotspotAttemptReporter.Write(
                        hotspotAttemptDiagnostics,
                        ResolveValidityFirstOutputDirectory());
                }

                consecutiveFeedbackOnlyRounds = combinedHotspotComponent is null
                    ? Math.Min(
                        _options.ValidityFirstMaximumFeedbackOnlyRounds,
                        consecutiveFeedbackOnlyRounds + 1)
                    : 0;

                _permanentFeedbackLedger.RecordMasterOutcome(
                    beforeMetrics.RootConflictCount,
                    afterMetrics.RootConflictCount);
                CheckerLoopIteration strengthenedIteration = new(
                    iterationIndex,
                    currentSnapshot.ObjectiveValue,
                    strengthenedSnapshot.ObjectiveValue,
                    strengthenedReport.IsFeasible,
                    currentReport.Conflicts.Count,
                    strengthenedReport.Conflicts.Count,
                    newEdgeCount,
                    newEdgeCount,
                    strengthenedModel.StatusName,
                    combinedHotspotComponent is null
                        ? "validity_first_global_resolve"
                        : "validity_first_global_resolve_with_hotspot",
                    BuildCandidateSummary(selectedBatch.Candidates),
                    string.Join('|', selectedBatch.Candidates
                        .Select(candidate => candidate.Evidence?.StationName ?? candidate.ResourceKey)
                        .Distinct(StringComparer.OrdinalIgnoreCase)),
                    CanonicalConflictCountBefore: beforeMetrics.CanonicalConflictCount,
                    CanonicalConflictCountAfter: afterMetrics.CanonicalConflictCount,
                    RootConflictCountBefore: beforeMetrics.RootConflictCount,
                    RootConflictCountAfter: afterMetrics.RootConflictCount,
                    ConflictedStationCountBefore: beforeMetrics.ConflictedStationCount,
                    ConflictedStationCountAfter: afterMetrics.ConflictedStationCount,
                    ActiveSectionEdgeCountBefore: sectionEdgeCountBefore,
                    ActiveSectionEdgeCountAfter: _activeSectionEdges.Count,
                    ActiveStationEventEdgeCountBefore: stationEdgeCountBefore,
                    ActiveStationEventEdgeCountAfter: _activeStationEventEdges.Count,
                    EpisodeConflictCountBefore: beforeMetrics.EpisodeConflictCount,
                    EpisodeConflictCountAfter: afterMetrics.EpisodeConflictCount,
                    AdaptivePolicyReason: combinedHotspotComponent is null
                        ? "validity_first_permanent_feedback_batch"
                        : "validity_first_permanent_feedback_and_repeated_hotspot",
                    ZeroConflictMode: true);
                strengthenedIteration = AttachIterationContinuityDiagnostics(
                    strengthenedIteration,
                    currentSnapshot,
                    strengthenedSnapshot,
                    currentReport,
                    strengthenedReport,
                    strengthenedModel,
                    accepted: true,
                    selectedBatch.Candidates.Select(candidate => candidate.CandidateKey).ToList(),
                    "GlobalStrengthenedMaster",
                    iterationIisTraceStartIndex);
                iterations.Add(strengthenedIteration);
                expectedAcceptedSnapshotFingerprint = strengthenedIteration.AcceptedSnapshotFingerprint;
                conflictTrend.Add(new CheckerConflictTrendPoint(
                    afterMetrics.RootConflictCount,
                    afterMetrics.EpisodeConflictCount));
                currentModel.Dispose();
                currentModel = strengthenedModel;
                currentSnapshot = strengthenedSnapshot;
                currentReport = strengthenedReport;
                _acceptedStructuralCandidateIds.UnionWith(
                    selectedBatch.Candidates.Select(candidate => candidate.CandidateKey));
                SaveCheckpointIfConfigured(iterationIndex, currentSnapshot, validityFirstState);
                continue;
            }
            if (newEdgeCount > 0)
            {
                MicroConflictMetrics structuralMetricsBefore = MicroConflictMetricCalculator.Calculate(currentReport.Conflicts);
                MicroConflictCategoryCounts conflictCategoriesBefore = MicroConflictCategoryCounter.Calculate(currentReport.Conflicts);
                IReadOnlySet<int> propagationTrainIndices = HeadwayConflictPropagationResolver.ResolveTrainIndices(
                    selectedBatch.Candidates, episodes);
                IReadOnlyList<StructuralRepairNeighborhoodResolution> neighborhoodLevels =
                    StructuralRepairNeighborhoodEscalationResolver.ResolveLevels(
                        _instance,
                        expansion,
                        selectedBatch.Candidates,
                        episodes,
                        propagationTrainIndices,
                        repairNeighborPadding,
                        _options.EscalationOptions);
                int initialNeighborhoodLevel = _options.EscalationOptions.ResolveInitialLevel(
                    _resumeState.IterationOffset,
                    localIterationIndex);
                neighborhoodLevels = neighborhoodLevels
                    .Where(level => level.Level >= initialNeighborhoodLevel)
                    .ToList();
                if (!_options.ZeroConflictMode)
                {
                    neighborhoodLevels =
                    [new StructuralRepairNeighborhoodResolution(
                        0,
                        "reference_order",
                        repairNeighborPadding,
                        StructuralRepairNeighborhoodResolver.ResolveTrainIndices(
                            _instance,
                            expansion,
                            repairNeighborPadding,
                            propagationTrainIndices),
                        0,
                        0)];
                }
                BasicMilpModel? expandedModel = null;
                MasterSolutionSnapshot? expandedSnapshot = null;
                MicroFeasibilityReport? expandedReport = null;
                TemporalRecourseApplicationResult? recourseApplication = null;
                StructuralRepairRetimingMetrics retimingMetrics = new(0.0, 0.0);
                int recourseAttemptCount = 0;
                string lastSolverStatus = string.Empty;
                string structuralDiagnosticTerminationOverride = string.Empty;
                string recourseBatchId = $"iteration-{iterationIndex}-structural";
                string candidateSummary = BuildCandidateSummary(selectedBatch.Candidates);
                List<CheckerRecourseStageDiagnostic> currentStageDiagnostics = new();
                List<NeighborhoodEscalationAttemptOutcome> escalationOutcomes = new();
                HashSet<string> selectedEpisodeIds = selectedBatch.Candidates
                    .Select(candidate => candidate.EpisodeId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                IReadOnlyList<HeadwayConflictEpisode> selectedEpisodes = episodes
                    .Where(episode => selectedEpisodeIds.Contains(episode.EpisodeId))
                    .ToList();
                foreach (StructuralRepairNeighborhoodResolution neighborhood in neighborhoodLevels)
                {
                    IReadOnlyList<int> recourseStages = !_options.ZeroConflictMode
                        ? [0]
                        : neighborhood.Level == 0
                            ? _options.RecourseWindowSeconds
                            : _options.EscalationOptions.EscalationWindowSeconds;
                    IReadOnlyList<RepairDecisionLevel> decisionLevels = _options.ZeroConflictMode
                        ? HierarchicalRepairPolicy.OrderedLevels
                            .Where(level => level != RepairDecisionLevel.LocalMicroExact)
                            .ToList()
                        : [RepairDecisionLevel.LocalPrecedence];
                    foreach (RepairDecisionLevel decisionLevel in decisionLevels)
                    {
                    RepairDecisionNeighborhood decisionNeighborhood = RepairDecisionNeighborhoodFactory.Create(
                        _instance,
                        decisionLevel,
                        neighborhood.ReleasedTrainIndices,
                        selectedEpisodes);
                    foreach (int recourseWindowSeconds in recourseStages)
                    {
                    recourseAttemptCount++;
                    BasicMilpModel attemptModel = BuildModel(activeCuts, precedenceTrustRegion: null);
                    RepairDecisionReleaseCounts releaseCounts = RepairDecisionReleaseCounter.Count(
                        attemptModel,
                        decisionNeighborhood);
                    ApplyMipStart(attemptModel, currentSnapshot);
                    HierarchicalRepairAttemptPreparation attemptPreparation =
                        HierarchicalRepairAttemptDecisionSpacePreparer.Prepare(
                        attemptModel,
                        currentSnapshot,
                        decisionNeighborhood,
                        _options.RepairRunningTimeSupplementRatio,
                        recourseWindowSeconds,
                        _options.ZeroConflictMode);
                    recourseApplication = attemptPreparation.RecourseApplication;
                    (double totalSupplementBefore, _, _) = CalculateRunningSupplementStatistics(currentSnapshot);
                    if (recourseApplication?.HasEmptyIntersection == true)
                    {
                            lastSolverStatus = $"EMPTY_TEMPORAL_INTERSECTION:{recourseApplication.EmptyIntersectionEventKey}";
                            CheckerRecourseStageDiagnostic emptyIntersectionDiagnostic = new(
                                iterationIndex,
                                recourseBatchId,
                                recourseWindowSeconds,
                                0.0,
                                -1,
                                lastSolverStatus,
                                0.0,
                                0,
                                false,
                                null,
                                null,
                                recourseApplication.FixedEventCount,
                                recourseApplication.BoundedEventCount,
                                "empty_temporal_intersection",
                                decisionLevel,
                                neighborhood.ReleasedTrainIndices.Count,
                                releaseCounts.StationSpanDescription,
                                releaseCounts.ReleasedPrecedenceVariableCount,
                                releaseCounts.ReleasedStopVariableCount,
                                attemptModel.PromotedMicroAlternativeVariableCount,
                                currentReport.Conflicts.Count,
                                null,
                                false,
                                ReleasedRunningSupplementVariableCount: releaseCounts.ReleasedRunningSupplementVariableCount,
                                ReleasedSectionPrecedenceVariableCount: releaseCounts.ReleasedSectionPrecedenceVariableCount,
                                ReleasedStationEventPrecedenceVariableCount: releaseCounts.ReleasedStationEventPrecedenceVariableCount,
                                AffectedMinimumStationOrder: releaseCounts.AffectedMinimumStationOrder,
                                AffectedMaximumStationOrder: releaseCounts.AffectedMaximumStationOrder,
                                ReleasedTimeVariableCount: releaseCounts.ReleasedTimeVariableCount,
                                RepairRunningTimeSupplementRatio: _options.RepairRunningTimeSupplementRatio,
                                TotalSupplementBefore: totalSupplementBefore,
                                ExactConflictsBefore: conflictCategoriesBefore.ExactHeadwayConflictCount,
                                AssignmentConflictsBefore: conflictCategoriesBefore.StationAssignmentConflictCount,
                                RejectionReason: "empty_temporal_intersection");
                            currentStageDiagnostics.Add(emptyIntersectionDiagnostic);
                            _recourseStageDiagnostics.Add(emptyIntersectionDiagnostic);
                            escalationOutcomes.Add(new(
                                neighborhood.Level, recourseWindowSeconds, -1, false, false,
                                "empty_temporal_intersection"));
                            _neighborhoodEscalationDiagnostics.Add(new(
                                iterationIndex, recourseBatchId, candidateSummary,
                                neighborhood.Level, neighborhood.Mode, neighborhood.ReferenceOrderPadding,
                                neighborhood.ReleasedTrainIndices.Count,
                                neighborhood.ComponentCount, neighborhood.ComponentTrainCount,
                                recourseWindowSeconds, 0.0, -1, lastSolverStatus, 0, 0.0,
                                currentReport.Conflicts.Count, null,
                                conflictCategoriesBefore.ExactHeadwayConflictCount, null,
                                conflictCategoriesBefore.StationAssignmentConflictCount, null,
                                false, "empty_temporal_intersection"));
                            attemptModel.Dispose();
                            continue;
                    }

                    RepairDecisionSpaceAuditResult decisionSpaceAudit = attemptPreparation.DecisionSpaceAudit
                        ?? throw new InvalidOperationException(
                            "A non-empty hierarchical repair attempt must include a decision-space audit.");
                    if (_options.ZeroConflictMode)
                    {
                        StructuralRepairObjectiveBuilder.Apply(attemptModel, currentSnapshot);
                    }

                    double effectiveTimeLimit = CheckerStructuralSolveTimeLimitResolver.Resolve(
                        _options.ZeroConflictMode,
                        _options.TimeLimitSecondsPerSolve,
                        _options.SecondaryTimeLimitSecondsPerSolve);
                    attemptModel.Model.Set(GRB.DoubleParam.TimeLimit, effectiveTimeLimit);
                    attemptModel.Optimize();
                    int solverStatusCode = attemptModel.Model.Get(GRB.IntAttr.Status);
                    lastSolverStatus = CheckerSolverStatusNameResolver.Resolve(solverStatusCode);
                    int solutionCount = attemptModel.Model.Get(GRB.IntAttr.SolCount);
                    bool hasIncumbent = attemptModel.HasIncumbentSolution;
                    bool acceptedIncumbent = false;
                    string stageOutcome = "no_incumbent";
                    MicroConflictCategoryCounts? candidateCategories = null;
                    int? candidateConflictCount = null;
                    double? totalSupplementAfter = null;
                    double? maximumSupplementAfter = null;
                    int? sectionsUsingSupplementAfter = null;
                    if (hasIncumbent)
                    {
                        MasterSolutionSnapshot candidateSnapshot = MasterSolutionSnapshot.Capture(attemptModel);
                        (totalSupplementAfter, maximumSupplementAfter, sectionsUsingSupplementAfter) =
                            CalculateRunningSupplementStatistics(candidateSnapshot);
                        MicroFeasibilityReport candidateReport = microChecker.Validate(attemptModel);
                        MicroConflictMetrics candidateMetrics = MicroConflictMetricCalculator.Calculate(candidateReport.Conflicts);
                        candidateCategories = MicroConflictCategoryCounter.Calculate(candidateReport.Conflicts);
                        candidateConflictCount = candidateReport.Conflicts.Count;
                        acceptedIncumbent = CheckerStructuralIncumbentAcceptancePolicy.Accept(
                            structuralMetricsBefore.RootConflictCount,
                            candidateMetrics.RootConflictCount,
                            _options.RequireStrictConflictImprovement);
                        if (!acceptedIncumbent)
                        {
                            RecordRejectedTrialConflictDelta(
                                iterationIndex,
                                selectedBatch.Candidates,
                                currentReport,
                                candidateReport,
                                attemptModel,
                                currentModel);
                        }
                        stageOutcome = acceptedIncumbent
                            ? "accepted_incumbent"
                            : "rejected_no_global_conflict_improvement";
                        if (acceptedIncumbent)
                        {
                            expandedModel = attemptModel;
                            expandedSnapshot = candidateSnapshot;
                            expandedReport = candidateReport;
                            if (_options.ZeroConflictMode)
                            {
                                retimingMetrics = StructuralRepairRetimingMetrics.Calculate(attemptModel, currentSnapshot);
                            }
                        }
                    }
                    CheckerRecourseStageDiagnostic stageDiagnostic = new(
                        iterationIndex,
                        recourseBatchId,
                        recourseWindowSeconds,
                        effectiveTimeLimit,
                        solverStatusCode,
                        lastSolverStatus,
                        attemptModel.Model.Get(GRB.DoubleAttr.Runtime),
                        solutionCount,
                        hasIncumbent,
                        hasIncumbent ? attemptModel.Model.Get(GRB.DoubleAttr.ObjVal) : null,
                        null,
                        recourseApplication?.FixedEventCount ?? 0,
                        recourseApplication?.BoundedEventCount ?? 0,
                        stageOutcome,
                        decisionLevel,
                        neighborhood.ReleasedTrainIndices.Count,
                        releaseCounts.StationSpanDescription,
                        releaseCounts.ReleasedPrecedenceVariableCount,
                        releaseCounts.ReleasedStopVariableCount,
                        attemptModel.PromotedMicroAlternativeVariableCount,
                        currentReport.Conflicts.Count,
                        candidateConflictCount,
                        acceptedIncumbent,
                        ReleasedRunningSupplementVariableCount: decisionSpaceAudit.ReleasedRunningSupplementVariableCount,
                        ReleasedSectionPrecedenceVariableCount: decisionSpaceAudit.ReleasedSectionPrecedenceVariableCount,
                        ReleasedStationEventPrecedenceVariableCount: decisionSpaceAudit.ReleasedStationEventPrecedenceVariableCount,
                        AffectedMinimumStationOrder: decisionSpaceAudit.AffectedMinimumStationOrder,
                        AffectedMaximumStationOrder: decisionSpaceAudit.AffectedMaximumStationOrder,
                        ReleasedTimeVariableCount: decisionSpaceAudit.ReleasedTimeVariableCount,
                        RepairRunningTimeSupplementRatio: _options.RepairRunningTimeSupplementRatio,
                        TotalSupplementBefore: totalSupplementBefore,
                        TotalSupplementAfter: totalSupplementAfter,
                        MaximumSupplementAfter: maximumSupplementAfter,
                        SectionsUsingSupplementAfter: sectionsUsingSupplementAfter,
                        ExactConflictsBefore: conflictCategoriesBefore.ExactHeadwayConflictCount,
                        AssignmentConflictsBefore: conflictCategoriesBefore.StationAssignmentConflictCount,
                        ExactConflictsAfter: candidateCategories?.ExactHeadwayConflictCount,
                        AssignmentConflictsAfter: candidateCategories?.StationAssignmentConflictCount,
                        RejectionReason: acceptedIncumbent ? null : stageOutcome);
                    currentStageDiagnostics.Add(stageDiagnostic);
                    _recourseStageDiagnostics.Add(stageDiagnostic);
                    escalationOutcomes.Add(new(
                        neighborhood.Level,
                        recourseWindowSeconds,
                        solverStatusCode,
                        hasIncumbent,
                        acceptedIncumbent,
                        stageOutcome));
                    _neighborhoodEscalationDiagnostics.Add(new(
                        iterationIndex,
                        recourseBatchId,
                        candidateSummary,
                        neighborhood.Level,
                        neighborhood.Mode,
                        neighborhood.ReferenceOrderPadding,
                        neighborhood.ReleasedTrainIndices.Count,
                        neighborhood.ComponentCount,
                        neighborhood.ComponentTrainCount,
                        recourseWindowSeconds,
                        effectiveTimeLimit,
                        solverStatusCode,
                        lastSolverStatus,
                        solutionCount,
                        attemptModel.Model.Get(GRB.DoubleAttr.Runtime),
                        currentReport.Conflicts.Count,
                        candidateConflictCount,
                        conflictCategoriesBefore.ExactHeadwayConflictCount,
                        candidateCategories?.ExactHeadwayConflictCount,
                        conflictCategoriesBefore.StationAssignmentConflictCount,
                        candidateCategories?.StationAssignmentConflictCount,
                        acceptedIncumbent,
                        stageOutcome));
                    bool isLastNeighborhood = ReferenceEquals(neighborhood, neighborhoodLevels[^1]) ||
                                              neighborhood == neighborhoodLevels[^1];
                    if (_options.ZeroConflictMode &&
                        StructuralFailedTrialDiagnosticTrigger.ShouldRun(
                            decisionLevel,
                            recourseWindowSeconds,
                            recourseStages[^1],
                            isLastNeighborhood,
                            solverStatusCode,
                            solutionCount))
                    {
                        StructuralTrialIisContext failedTrialContext = new(
                            iterationIndex,
                            recourseBatchId,
                            neighborhood.Level,
                            decisionLevel,
                            recourseWindowSeconds,
                            "failed_trial",
                            "failed-trial",
                            selectedBatch.Candidates.Select(candidate => candidate.CandidateKey).ToList());
                        _structuralTrialIisDiagnostics.AddRange(
                            StructuralTrialIisExtractor.ComputeAndExtract(
                                attemptModel,
                                failedTrialContext,
                                selectedBatch.Candidates,
                                _instance,
                                currentSnapshot));

                        StructuralCandidateProbeFrozenConfiguration frozenConfiguration = new(
                            currentSnapshot,
                            decisionNeighborhood,
                            _options.RepairRunningTimeSupplementRatio,
                            recourseWindowSeconds);
                        StructuralCandidateFeasibilityRunResult probeResult =
                            StructuralCandidateFeasibilityDiagnosticRunner.Run(
                                selectedBatch.Candidates,
                                request => ExecuteStructuralCandidateProbe(
                                    request,
                                    frozenConfiguration,
                                    baselineSectionEdges,
                                    baselineStationEdges,
                                    activeCuts,
                                    iterationIndex,
                                    recourseBatchId,
                                    neighborhood.Level));
                        _structuralCandidateProbeDiagnostics.AddRange(probeResult.Diagnostics);
                        _structuralCandidateProbeStopReason = probeResult.StopReason;
                        _structuralTrialIisDiagnostics.AddRange(
                            probeResult.Diagnostics.SelectMany(diagnostic => diagnostic.Execution.IisDiagnostics));
                        bool directSingletonAccepted = false;
                        if (_options.EnableFeasibleSingletonGlobalEvaluation)
                        {
                            using StructuralSingletonGlobalTrialRunResult globalTrialResult =
                                StructuralSingletonGlobalTrialRunner.Run(
                                    probeResult.Diagnostics,
                                    candidate => ExecuteStructuralSingletonGlobalTrial(
                                        candidate,
                                        frozenConfiguration,
                                        baselineSectionEdges,
                                        baselineStationEdges,
                                        activeCuts,
                                        iterationIndex,
                                        recourseBatchId,
                                        currentReport,
                                        microChecker));
                            _structuralSingletonGlobalTrialDiagnostics.AddRange(globalTrialResult.Diagnostics);
                            _structuralSingletonGlobalTrialDiagnostics.AddRange(
                                CreateNotEvaluatedSingletonGlobalDiagnostics(
                                    probeResult.Diagnostics,
                                    iterationIndex,
                                    recourseBatchId,
                                    currentReport,
                                    currentSnapshot));
                            structuralDiagnosticTerminationOverride = globalTrialResult.TerminationReason;
                            if (globalTrialResult.Winner is not null)
                            {
                                StructuralSingletonGlobalTrialExecution winner = globalTrialResult.Winner;
                                HeadwayConflictCandidate candidate = selectedBatch.Candidates.Single(item =>
                                    string.Equals(item.CandidateKey, winner.Diagnostic.CandidateId,
                                        StringComparison.OrdinalIgnoreCase));
                                expandedModel = winner.TransferModel();
                                expandedSnapshot = winner.Snapshot
                                    ?? throw new InvalidOperationException("A selected singleton trial has no snapshot.");
                                expandedReport = winner.MicroReport
                                    ?? throw new InvalidOperationException("A selected singleton trial has no Micro report.");
                                retimingMetrics = new StructuralRepairRetimingMetrics(
                                    winner.Diagnostic.TotalRetimingSeconds,
                                    winner.Diagnostic.MaximumRetimingSeconds);
                                StructuralSingletonGlobalTrialCommitPlan commitPlan =
                                    StructuralSingletonGlobalTrialCommitPlanner.Build(
                                        baselineSectionEdges, baselineStationEdges, candidate);
                                _activeSectionEdges.Clear();
                                _activeSectionEdges.AddRange(commitPlan.SectionEdges);
                                _activeStationEventEdges.Clear();
                                _activeStationEventEdges.AddRange(commitPlan.StationEdges);
                                newEdgeCount = commitPlan.SectionEdges.Count - baselineSectionEdges.Count +
                                               commitPlan.StationEdges.Count - baselineStationEdges.Count;
                                acceptedStructuralCandidates = [candidate];
                                lastSolverStatus = expandedModel.StatusName;
                                directSingletonAccepted = true;
                            }
                        }
                        if (!directSingletonAccepted && _options.IisGuidedOptions.Enabled)
                        {
                            foreach (StructuralCandidateProbeDiagnostic provenSingleton in probeResult.Diagnostics.Where(item =>
                                         item.Request.Mode == StructuralCandidateProbeMode.Individual &&
                                         item.Execution.Outcome == StructuralCandidateProbeOutcome.ProvenInfeasible))
                            {
                                HeadwayConflictCandidate candidate = provenSingleton.Request.Candidates.Single();
                                IisModelDependencyCatalog catalog = provenSingleton.Execution.DependencyCatalog
                                    ?? throw new InvalidOperationException(
                                        "A proven-infeasible singleton must retain its dependency catalog.");
                                IisGuidedDependencyClosureResult closure =
                                    IisGuidedDependencyClosureRunner.Run(
                                        new IisGuidedDependencyClosureRequest(
                                            candidate,
                                            provenSingleton.Execution.IisDiagnostics,
                                            catalog,
                                            currentSnapshot,
                                            decisionNeighborhood,
                                            recourseWindowSeconds,
                                            _options.IisGuidedOptions),
                                        (round, overlay) => ExecuteIisGuidedMasterAttempt(
                                            candidate,
                                            round,
                                            overlay,
                                            frozenConfiguration,
                                            baselineSectionEdges,
                                            baselineStationEdges,
                                            activeCuts,
                                            iterationIndex,
                                            recourseBatchId,
                                            neighborhood.Level));
                                _iisDependencyPlanDiagnostics.AddRange(closure.PlanDiagnostics);
                                _structuralTrialIisDiagnostics.AddRange(closure.IisDiagnostics);
                                List<IisGuidedRepairTraceDiagnostic> closureTrace = closure.Trace.ToList();
                                if (!closure.HasMasterIncumbent)
                                {
                                    _iisGuidedRepairTraceDiagnostics.AddRange(closureTrace);
                                    structuralDiagnosticTerminationOverride = closure.TerminationReason;
                                    continue;
                                }

                                BasicMilpModel feasibleClosureModel = closure.FeasibleModel!;
                                MasterSolutionSnapshot feasibleClosureSnapshot =
                                    MasterSolutionSnapshot.Capture(feasibleClosureModel);
                                MicroFeasibilityReport feasibleClosureReport = microChecker.Validate(feasibleClosureModel);
                                MicroConflictMetrics feasibleClosureMetrics =
                                    MicroConflictMetricCalculator.Calculate(feasibleClosureReport.Conflicts);
                                bool microAccepted = CheckerStructuralIncumbentAcceptancePolicy.Accept(
                                    structuralMetricsBefore.RootConflictCount,
                                    feasibleClosureMetrics.RootConflictCount,
                                    _options.RequireStrictConflictImprovement);
                                if (!microAccepted)
                                {
                                    RecordRejectedTrialConflictDelta(
                                        iterationIndex,
                                        [candidate],
                                        currentReport,
                                        feasibleClosureReport,
                                        feasibleClosureModel,
                                        currentModel);
                                }
                                string guidedTermination =
                                    IisGuidedRepairTransitionPolicy.ResolveAfterMicro(microAccepted);
                                structuralDiagnosticTerminationOverride = microAccepted
                                    ? guidedTermination
                                    : "iis_guided_feasible_but_micro_rejected";
                                if (closureTrace.Count > 0)
                                {
                                    closureTrace[^1] = closureTrace[^1] with
                                    {
                                        Accepted = microAccepted,
                                        TerminationReason = guidedTermination
                                    };
                                }
                                _iisGuidedRepairTraceDiagnostics.AddRange(closureTrace);
                                if (!microAccepted)
                                {
                                    feasibleClosureModel.Dispose();
                                    continue;
                                }

                                expandedModel = feasibleClosureModel;
                                expandedSnapshot = feasibleClosureSnapshot;
                                expandedReport = feasibleClosureReport;
                                retimingMetrics = StructuralRepairRetimingMetrics.Calculate(
                                    feasibleClosureModel, currentSnapshot);
                                _activeSectionEdges.RemoveRange(
                                    sectionEdgeCountBefore,
                                    _activeSectionEdges.Count - sectionEdgeCountBefore);
                                _activeStationEventEdges.RemoveRange(
                                    stationEdgeCountBefore,
                                    _activeStationEventEdges.Count - stationEdgeCountBefore);
                                newEdgeCount = AddNewHeadwayEdges(new HeadwayConflictEdgeExpansion(
                                    candidate.SectionEdge is null
                                        ? Array.Empty<SectionConflictEdgeRequest>()
                                        : [candidate.SectionEdge],
                                    candidate.StationEdge is null
                                        ? Array.Empty<StationEventConflictCandidate>()
                                        : [candidate.StationEdge]));
                                acceptedStructuralCandidates = [candidate];
                                lastSolverStatus = feasibleClosureModel.StatusName;
                                break;
                            }
                        }
                        SynchronizeStructuralDiagnostics(expandedModel ?? currentModel);
                    }
                    if (acceptedIncumbent)
                    {
                        break;
                    }

                    attemptModel.Dispose();
                    }
                    if (expandedModel is not null)
                    {
                        break;
                    }
                    }
                    if (expandedModel is not null)
                    {
                        break;
                    }
                }

                if (expandedModel is null)
                {
                    _activeSectionEdges.RemoveRange(sectionEdgeCountBefore, _activeSectionEdges.Count - sectionEdgeCountBefore);
                    _activeStationEventEdges.RemoveRange(stationEdgeCountBefore, _activeStationEventEdges.Count - stationEdgeCountBefore);
                    string terminationReason = !_options.ZeroConflictMode
                        ? "structural_edge_trial_no_incumbent"
                        : _options.EnableFeasibleSingletonGlobalEvaluation &&
                          !string.IsNullOrWhiteSpace(structuralDiagnosticTerminationOverride)
                            ? structuralDiagnosticTerminationOverride
                        : _options.EscalationOptions.Enabled
                            ? NeighborhoodEscalationTerminationClassifier.Classify(escalationOutcomes)
                            : CheckerRecourseTerminationClassifier.Classify(currentStageDiagnostics);
                    iterations.Add(new CheckerLoopIteration(
                        iterationIndex,
                        currentSnapshot.ObjectiveValue,
                        currentSnapshot.ObjectiveValue,
                        currentReport.IsFeasible,
                        currentReport.Conflicts.Count,
                        currentReport.Conflicts.Count,
                        newEdgeCount,
                        newEdgeCount,
                        lastSolverStatus,
                        terminationReason,
                        string.Empty,
                        expansion.StationEdges.FirstOrDefault()?.StationName ?? string.Empty,
                        structuralMetricsBefore.CanonicalConflictCount,
                        structuralMetricsBefore.CanonicalConflictCount,
                        structuralMetricsBefore.RootConflictCount,
                        structuralMetricsBefore.RootConflictCount,
                        structuralMetricsBefore.ConflictedStationCount,
                        structuralMetricsBefore.ConflictedStationCount,
                        sectionEdgeCountBefore,
                        _activeSectionEdges.Count,
                        stationEdgeCountBefore,
                        _activeStationEventEdges.Count,
                        activeCuts.Count,
                        structuralMetricsBefore.EpisodeConflictCount,
                        structuralMetricsBefore.EpisodeConflictCount,
                        selectionBatchSize,
                        repairNeighborPadding,
                        selectionPolicyReason,
                        BuildCandidateSummary(selectedBatch.Candidates),
                        0,
                        recourseAttemptCount,
                        recourseApplication?.FixedEventCount ?? 0,
                        recourseApplication?.BoundedEventCount ?? 0,
                        0.0,
                        0.0,
                        selectedBatch.Candidates.Count,
                        _options.ZeroConflictMode));
                    iterations[^1] = AttachIterationContinuityDiagnostics(
                        iterations[^1],
                        currentSnapshot,
                        currentSnapshot,
                        currentReport,
                        currentReport,
                        currentModel,
                        accepted: false,
                        Array.Empty<string>(),
                        string.Empty,
                        iterationIisTraceStartIndex);
                    return new CheckerLoopResult(currentModel, iterations, diagnostics, activeCuts, terminationReason, selectionDiagnostics, _options.EndpointOccupationSeconds, localMicroRepairDiagnostics, _recourseStageDiagnostics.ToList(), _neighborhoodEscalationDiagnostics.ToList());
                }

                expandedSnapshot ??= MasterSolutionSnapshot.Capture(expandedModel);
                expandedReport ??= microChecker.Validate(expandedModel);
                MicroConflictMetrics structuralMetricsAfter = MicroConflictMetricCalculator.Calculate(expandedReport.Conflicts);
                iterations.Add(new CheckerLoopIteration(
                    iterationIndex,
                    currentSnapshot.ObjectiveValue,
                    expandedSnapshot.ObjectiveValue,
                    expandedReport.IsFeasible,
                    currentReport.Conflicts.Count,
                    expandedReport.Conflicts.Count,
                    newEdgeCount,
                    newEdgeCount,
                    expandedModel.StatusName,
                    structuralDiagnosticTerminationOverride == "feasible_singleton_global_trial_accepted"
                        ? structuralDiagnosticTerminationOverride
                        : "expanded_headway_edges",
                    string.Empty,
                    string.Empty,
                    structuralMetricsBefore.CanonicalConflictCount,
                    structuralMetricsAfter.CanonicalConflictCount,
                    structuralMetricsBefore.RootConflictCount,
                    structuralMetricsAfter.RootConflictCount,
                    structuralMetricsBefore.ConflictedStationCount,
                    structuralMetricsAfter.ConflictedStationCount,
                    sectionEdgeCountBefore,
                    _activeSectionEdges.Count,
                    stationEdgeCountBefore,
                    _activeStationEventEdges.Count,
                    activeCuts.Count,
                    structuralMetricsBefore.EpisodeConflictCount,
                    structuralMetricsAfter.EpisodeConflictCount,
                    selectionBatchSize,
                    repairNeighborPadding,
                    selectionPolicyReason,
                    BuildCandidateSummary(selectedBatch.Candidates),
                    recourseApplication?.WindowSeconds ?? 0,
                    recourseAttemptCount,
                    recourseApplication?.FixedEventCount ?? 0,
                    recourseApplication?.BoundedEventCount ?? 0,
                    retimingMetrics.TotalAbsoluteDeviationSeconds,
                    retimingMetrics.MaximumAbsoluteDeviationSeconds,
                    selectedBatch.Candidates.Count,
                    _options.ZeroConflictMode));
                CheckerRecourseStageDiagnostic? acceptedStage = currentStageDiagnostics
                    .LastOrDefault(item => item.Accepted);
                bool usedIisGuidedClosure = _iisGuidedRepairTraceDiagnostics.Count > iterationIisTraceStartIndex;
                bool usedDirectSingletonGlobalTrial = _structuralSingletonGlobalTrialDiagnostics
                    .Skip(iterationSingletonGlobalTrialStartIndex)
                    .Any(item => item.Selected);
                string acceptedRepairType = usedDirectSingletonGlobalTrial
                    ? "FeasibleSingletonGlobalTrial"
                    : usedIisGuidedClosure
                    ? $"{_iisGuidedRepairTraceDiagnostics[^1].RepairLevel}+IISGuidedDependencyClosure"
                    : acceptedStage?.RepairDecisionLevel?.ToString() ?? "HierarchicalRepair";
                iterations[^1] = AttachIterationContinuityDiagnostics(
                    iterations[^1],
                    currentSnapshot,
                    expandedSnapshot,
                    currentReport,
                    expandedReport,
                    expandedModel,
                    accepted: true,
                    acceptedStructuralCandidates.Select(candidate => candidate.CandidateKey).ToList(),
                    acceptedRepairType,
                    iterationIisTraceStartIndex);
                expectedAcceptedSnapshotFingerprint =
                    iterations[^1].AcceptedSnapshotFingerprint;
                conflictTrend.Add(new CheckerConflictTrendPoint(
                    structuralMetricsAfter.RootConflictCount,
                    structuralMetricsAfter.EpisodeConflictCount));
                currentModel.Dispose();
                SynchronizeStructuralDiagnostics(expandedModel);
                currentModel = expandedModel;
                currentSnapshot = expandedSnapshot;
                currentReport = expandedReport;
                _acceptedStructuralCandidateIds.UnionWith(
                    acceptedStructuralCandidates.Select(candidate => candidate.CandidateKey));
                SaveCheckpointIfConfigured(iterationIndex, currentSnapshot, validityFirstState);
                string? structuralTerminationReason = CheckerAcceptedRoundTerminationResolver.Resolve(
                    _options.FixedSnapshotMultiIterationMode,
                    currentReport.IsFeasible);
                if (structuralTerminationReason is not null)
                {
                    return new CheckerLoopResult(
                        currentModel, iterations, diagnostics, activeCuts, structuralTerminationReason,
                        selectionDiagnostics, _options.EndpointOccupationSeconds, localMicroRepairDiagnostics,
                        _recourseStageDiagnostics.ToList(), _neighborhoodEscalationDiagnostics.ToList(),
                        c0Report, currentReport, localIterationIndex);
                }
                continue;
            }

            if (_options.ZeroConflictMode)
            {
                if (_options.CoordinationStrategy == CheckerCoordinationStrategy.ValidityFirstBenders)
                {
                    StationLevelMicroFeasibilitySlaveResult slaveResult = microSlave.Solve(currentModel);
                    IReadOnlyList<ValidityFirstHotspotCandidate> hotspotCandidates =
                        ResolveValidityFirstHotspotCandidates(
                            slaveResult,
                            currentReport,
                            currentAssignmentCoreOccurrences,
                            stationHotspotPersistence,
                            validityFirstMigrationPriorityStations,
                            requireJointRepairScope: _options.EnablePhasedFeasibilityRecovery);
                    if (hotspotCandidates.Count == 0)
                    {
                        hotspotAttemptDiagnostics.Add(new ValidityFirstHotspotAttemptDiagnostic(
                            iterationIndex,
                            consecutiveFeedbackOnlyRounds,
                            "no_new_valid_feedback",
                            "none",
                            string.Empty,
                            string.Empty,
                            true,
                            false,
                            "no_persistent_assignment_or_correlated_exact_headway_hotspot",
                            0,
                            0,
                            false,
                            false,
                            false,
                            "not_started",
                            0,
                            null,
                            currentReport.Conflicts.Count,
                            null,
                            0.0,
                            0.0));
                        ValidityFirstHotspotAttemptReporter.Write(
                            hotspotAttemptDiagnostics,
                            ResolveValidityFirstOutputDirectory());
                        return new CheckerLoopResult(
                            currentModel,
                            iterations,
                            diagnostics,
                            activeCuts,
                            "validity_first_no_proven_hotspot_core",
                            selectionDiagnostics,
                            _options.EndpointOccupationSeconds,
                            localMicroRepairDiagnostics,
                            _recourseStageDiagnostics.ToList(),
                            _neighborhoodEscalationDiagnostics.ToList(),
                            c0Report,
                            currentReport,
                            localIterationIndex,
                            WorkingSolution: validityFirstState.WorkingSolution,
                            BestDiagnosticSolution: validityFirstState.BestDiagnosticSolution,
                            BestJointFeasibleSolution: validityFirstState.BestJointFeasibleSolution,
                            CoordinationStrategy: _options.CoordinationStrategy,
                            ConfigurationFingerprint: ValidityFirstConfigurationFingerprint.Calculate(_options),
                            PermanentMicroFeedback: _permanentFeedbackLedger.Items);
                    }

                    ValidityFirstHotspotBuildResult? hotspotBuild = null;
                    StationLevelMicroSubproblemResult? selectedHotspotStationResult = null;
                    int selectedHotspotDiagnosticIndex = -1;
                    string? selectedRepairAttemptFingerprint = null;
                    foreach (ValidityFirstHotspotCandidate hotspotCandidate in hotspotCandidates)
                    {
                        Stopwatch buildStopwatch = Stopwatch.StartNew();
                        ValidityFirstHotspotBuildResult candidateBuild = BuildValidityFirstHotspotComponent(
                            currentModel,
                            hotspotCandidate.StationResult,
                            iterationIndex,
                            hotspotCandidate.PersistentRounds);
                        buildStopwatch.Stop();
                        ValidityFirstHotspotDomainAuditReporter.Append(
                            iterationIndex,
                            hotspotCandidate.StationResult.StationName,
                            candidateBuild.DomainAudits,
                            ResolveValidityFirstOutputDirectory());
                        hotspotAttemptDiagnostics.Add(new ValidityFirstHotspotAttemptDiagnostic(
                            iterationIndex,
                            consecutiveFeedbackOnlyRounds,
                            "no_new_valid_feedback",
                            hotspotCandidate.Priority,
                            hotspotCandidate.StationResult.StationName,
                            hotspotCandidate.AssignmentCoreIdentity,
                            true,
                            candidateBuild.IsComplete,
                            candidateBuild.Status,
                            candidateBuild.CandidateRequirementCount,
                            candidateBuild.ClosureRequirementCount,
                            candidateBuild.IsComplete && candidateBuild.Component is not null,
                            false,
                            false,
                            "not_started",
                            hotspotCandidate.StationResult.Conflicts.Count,
                            null,
                            currentReport.Conflicts.Count,
                            null,
                            buildStopwatch.Elapsed.TotalSeconds,
                            0.0,
                            CoreRequirementCount: candidateBuild.CoreRequirementIds?.Count ?? 0,
                            ProtectionRequirementCount: candidateBuild.ProtectionRequirementIds?.Count ?? 0,
                            AlternativeCount: candidateBuild.ScopeEstimate?.AlternativeCount ?? 0,
                            EstimatedVariableCount: candidateBuild.ScopeEstimate?.EstimatedVariableCount ?? 0,
                            EstimatedConstraintCount: candidateBuild.ScopeEstimate?.EstimatedConstraintCount ?? 0,
                            ResourceConflictRelationCount: candidateBuild.ScopeEstimate?.ResourceConflictRelationCount ?? 0,
                            TrainCount: candidateBuild.TrainCount,
                            OrderVariableCount: candidateBuild.ScopeEstimate?.OrderVariableCount ?? 0,
                            LegacyEstimatedVariableCount: candidateBuild.ScopeEstimate?.LegacyEstimatedVariableCount ?? 0,
                            LegacyOrderVariableCount: candidateBuild.ScopeEstimate?.LegacyOrderVariableCount ?? 0));
                        if (candidateBuild.IsComplete && candidateBuild.Component is not null)
                        {
                            if (_options.EnablePhasedFeasibilityRecovery)
                            {
                                string attemptFingerprint = ValidityFirstRepairAttemptKeyBuilder.Build(
                                    SnapshotFingerprint.Calculate(validityFirstRepairReference),
                                    _permanentFeedbackLedger.Items.Select(item => item.FeedbackId).ToList(),
                                    candidateBuild.ScopeFingerprint,
                                    ValidityFirstSolveParameterPolicy.Resolve("hotspot_exactification", _options));
                                int diagnosticIndex = hotspotAttemptDiagnostics.Count - 1;
                                hotspotAttemptDiagnostics[diagnosticIndex] =
                                    hotspotAttemptDiagnostics[diagnosticIndex] with
                                    {
                                        RepairAttemptFingerprint = attemptFingerprint
                                    };
                                if (!validityFirstRepairAttemptRegistry.ShouldAttempt(attemptFingerprint))
                                {
                                    hotspotAttemptDiagnostics[diagnosticIndex] =
                                        hotspotAttemptDiagnostics[diagnosticIndex] with
                                        {
                                            PromotionPersisted = false,
                                            BuildStatus = $"duplicate_failed_attempt_skipped::{attemptFingerprint}"
                                        };
                                    continue;
                                }

                                selectedRepairAttemptFingerprint = attemptFingerprint;
                            }

                            hotspotBuild = candidateBuild;
                            selectedHotspotStationResult = hotspotCandidate.StationResult;
                            selectedHotspotDiagnosticIndex = hotspotAttemptDiagnostics.Count - 1;
                            break;
                        }
                    }
                    ValidityFirstHotspotAttemptReporter.Write(
                        hotspotAttemptDiagnostics,
                        ResolveValidityFirstOutputDirectory());
                    if (hotspotBuild?.Component is null)
                    {
                        string buildFailure = hotspotAttemptDiagnostics.Last().BuildStatus;
                        string status = $"validity_first_hotspot_not_exactifiable::{buildFailure}";
                        return new CheckerLoopResult(
                            currentModel,
                            iterations,
                            diagnostics,
                            activeCuts,
                            status,
                            selectionDiagnostics,
                            _options.EndpointOccupationSeconds,
                            localMicroRepairDiagnostics,
                            _recourseStageDiagnostics.ToList(),
                            _neighborhoodEscalationDiagnostics.ToList(),
                            c0Report,
                            currentReport,
                            localIterationIndex,
                            WorkingSolution: validityFirstState.WorkingSolution,
                            BestDiagnosticSolution: validityFirstState.BestDiagnosticSolution,
                            BestJointFeasibleSolution: validityFirstState.BestJointFeasibleSolution,
                            CoordinationStrategy: _options.CoordinationStrategy,
                            ConfigurationFingerprint: ValidityFirstConfigurationFingerprint.Calculate(_options),
                            PermanentMicroFeedback: _permanentFeedbackLedger.Items);
                    }

                    _promotedMicroComponentStore.Upsert(hotspotBuild.Component);
                    MicroConflictMetrics beforeMetrics = MicroConflictMetricCalculator.Calculate(currentReport.Conflicts);
                    MasterSolutionSnapshot hotspotRepairReference = validityFirstRepairReference;
                    validityFirstState.InvalidateWorkingSolution();
                    _permanentFeedbackLedger.MarkAppliedToModel(
                        _permanentFeedbackLedger.Items.Select(item => item.FeedbackId),
                        iterationIndex);
                    WriteValidityFirstFeedbackLedger(
                        _permanentFeedbackLedger.Items,
                        Path.Combine(
                            ResolveValidityFirstOutputDirectory(),
                            "validity_first_feedback_ledger.csv"));

                    string repairReferenceFingerprint = SnapshotFingerprint.Calculate(hotspotRepairReference);
                    HotspotMicroAssignmentCandidate? assignmentCandidate = null;
                    string assignmentCandidateFailure = "phased_recovery_disabled";
                    bool hasCompleteAssignmentCandidate = _options.EnablePhasedFeasibilityRecovery &&
                        selectedHotspotStationResult is not null &&
                        HotspotMicroAssignmentCandidateBuilder.TryCapture(
                            selectedHotspotStationResult,
                            repairReferenceFingerprint,
                            out assignmentCandidate,
                            out assignmentCandidateFailure);
                    IReadOnlyList<VerifiedMicroAssignmentWitness> repairWitnesses =
                        MergeRepairWitnesses(
                            hotspotRepairReference.VerifiedMicroAssignments ?? [],
                            assignmentCandidate?.Witnesses ?? []);
                    Console.WriteLine(
                        $"Validity-first hotspot assignment candidate: iteration={iterationIndex}, " +
                        $"station={hotspotBuild.Component.StationName}, complete={hasCompleteAssignmentCandidate}, " +
                        $"candidate_witnesses={assignmentCandidate?.Witnesses.Count ?? 0}, " +
                        $"verified_witnesses={hotspotRepairReference.VerifiedMicroAssignments?.Count ?? 0}, " +
                        $"scope={assignmentCandidate?.VerificationScope ?? "none"}, " +
                        $"reason={(hasCompleteAssignmentCandidate ? "complete_assignment_candidate" : assignmentCandidateFailure)}.");

                    HashSet<int> hotspotConflictTrainIndices = currentReport.Conflicts
                        .Where(conflict => string.Equals(
                            conflict.StationName,
                            hotspotBuild.Component.StationName,
                            StringComparison.OrdinalIgnoreCase))
                        .SelectMany(conflict => new[] { conflict.FirstTrainIndex, conflict.SecondTrainIndex })
                        .Where(trainIndex => trainIndex.HasValue)
                        .Select(trainIndex => trainIndex!.Value)
                        .ToHashSet();
                    HashSet<int> hotspotFallbackRelatedTrainIndices =
                        ResolveHotspotFallbackRelatedTrainIndices(
                            selectedHotspotStationResult!,
                            currentReport.Conflicts,
                            hotspotConflictTrainIndices);
                    IReadOnlyList<ValidityFirstRepairNeighborhoodPlan> hotspotPlans =
                        _options.EnablePhasedFeasibilityRecovery
                            ? ValidityFirstRepairNeighborhoodPlanner.BuildHotspotExactificationRecovery(
                                repairWitnesses,
                                hotspotBuild.Component.StationName,
                                hotspotConflictTrainIndices,
                                hotspotFallbackRelatedTrainIndices,
                                hasCompleteAssignmentCandidate,
                                _options.ValidityFirstFixedAssignmentRetimingTimeLimitSeconds,
                                _options.ValidityFirstConflictLinkedRetimingTimeLimitSeconds,
                                _options.TimeLimitSecondsPerSolve)
                            :
                            [
                                new ValidityFirstRepairNeighborhoodPlan(
                                    "legacy_hotspot_exactification",
                                    0,
                                    new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                                    _options.TimeLimitSecondsPerSolve,
                                    ValidityFirstNeighborhoodTransitionKind.InitialRestricted,
                                    "未启用分阶段恢复时保持原热点精确化求解方式。",
                                    false)
                            ];
                    hotspotPlans = ApplyRepairNeighborhoodSelector(
                        hotspotRepairReference,
                        currentReport,
                        hotspotPlans,
                        hotspotBuild.Component,
                        iterationIndex,
                        "hotspot_exactification");
                    hotspotPlans = ApplyRepairActionSelector(
                        hotspotRepairReference,
                        currentReport,
                        hotspotPlans,
                        repairWitnesses,
                        true,
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                        iterationIndex,
                        "hotspot_exactification");

                    BasicMilpModel? hotspotModel = null;
                    bool hotspotSolveStarted = false;
                    PrecedenceTrustRegion? appliedHotspotEndpointTrustRegion = null;
                    int appliedHotspotEndpointOrderChangeBudget = 0;
                    int previousHotspotStatusCode = GRB.Status.LOADED;
                    int previousHotspotSolutionCount = 0;
                    for (int hotspotPlanIndex = 0;
                         hotspotPlanIndex < hotspotPlans.Count;
                         hotspotPlanIndex++)
                    {
                        ValidityFirstRepairNeighborhoodPlan plan = hotspotPlans[hotspotPlanIndex];
                        ValidityFirstRuntimeBudget buildBudget = ValidityFirstRuntimeBudgetPolicy.Resolve(
                            plan.RequestedSolveSeconds,
                            _options.ValidityFirstTotalRuntimeBudgetSeconds,
                            GetValidityFirstCumulativeElapsedSeconds(),
                            _options.ValidityFirstFinalValidationReserveSeconds);
                        if (!buildBudget.CanStart)
                        {
                            Console.WriteLine(
                                $"Validity-first hotspot recovery skipped before construction: " +
                                $"iteration={iterationIndex}, neighborhood={plan.NeighborhoodId}, " +
                                $"elapsed={GetValidityFirstCumulativeElapsedSeconds():R}, " +
                                $"remaining={buildBudget.RemainingGlobalSeconds:R}, " +
                                $"reserved_validation={buildBudget.ReservedValidationSeconds:R}.");
                            break;
                        }

                        if (hotspotModel is not null)
                        {
                            hotspotModel.Dispose();
                        }
                        ValidityFirstNeighborhoodTransitionKind actualTransition = hotspotPlanIndex == 0
                            ? ValidityFirstNeighborhoodTransitionKind.InitialRestricted
                            : ValidityFirstNeighborhoodTransitionPolicy.Resolve(
                                previousHotspotStatusCode,
                                previousHotspotSolutionCount,
                                reliableIisMembers: false,
                                fullReleaseFallback: false);
                        PrecedenceTrustRegion? endpointTrustRegion =
                            _options.EnablePhasedFeasibilityRecovery
                                ? ValidityFirstEndpointOrderTrustRegionBuilder.Build(
                                    _instance,
                                    hotspotRepairReference,
                                    hotspotConflictTrainIndices,
                                    plan.EndpointOrderChangeBudget,
                                    $"validity_hotspot_i{iterationIndex}_{plan.NeighborhoodId}")
                                : null;
                        appliedHotspotEndpointTrustRegion = endpointTrustRegion;
                        appliedHotspotEndpointOrderChangeBudget = plan.EndpointOrderChangeBudget;
                        hotspotModel = BuildValidityFirstModel(
                            activeCuts,
                            iterationIndex,
                            $"hotspot_exactification_model_construction_{plan.NeighborhoodId}",
                            endpointTrustRegion);
                        MasterSolutionSnapshot repairStart = hotspotRepairReference with
                        {
                            VerifiedMicroAssignments = repairWitnesses
                        };
                        VerifiedMicroAssignmentMipStartResult microStartResult =
                            MasterSolutionSnapshotApplier.ApplyMipStart(hotspotModel, repairStart);
                        ValidityFirstTemporaryFixationAudit temporaryAudit = new(
                            plan.NeighborhoodId,
                            actualTransition.ToString(),
                            plan.Reason,
                            [],
                            plan.ReleasedRequirementIds.Order(StringComparer.OrdinalIgnoreCase).ToList(),
                            plan.EndpointOrderChangeBudget,
                            plan.RequestedSolveSeconds);
                        IReadOnlyList<VerifiedMicroAssignmentWitness> hotspotFixationWitnesses =
                            _options.RepairActionSelector == RepairActionSelectorKind.RuleBased &&
                            plan.ActionType != RepairActionType.LegacyFallback
                                ? RepairActionCurrentWitnessPolicy.SelectCurrent(
                                    repairWitnesses,
                                    hotspotRepairReference,
                                    repairReferenceFingerprint,
                                    allowCurrentAssignmentCandidate: true)
                                : repairWitnesses;
                        VerifiedMicroAssignmentTemporaryFixResult temporaryFixResult =
                            _options.EnablePhasedFeasibilityRecovery
                                ? VerifiedMicroAssignmentTemporaryFixer.Apply(
                                    hotspotModel,
                                    hotspotFixationWitnesses,
                                    new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                                    plan.ReleasedRequirementIds,
                                    temporaryAudit)
                                : new VerifiedMicroAssignmentTemporaryFixResult(0, 0, 0);
                        ValidityFirstVerifiedStructureFixationResult? timeActionStructureFixation = null;
                        if (_options.EnablePhasedFeasibilityRecovery &&
                            plan.ActionType == RepairActionType.TimeRetiming)
                        {
                            timeActionStructureFixation = ValidityFirstVerifiedStructureFixer.Apply(
                                hotspotModel,
                                hotspotRepairReference,
                                RepairActionVerifiedStructureBuilder.Build(hotspotFixationWitnesses),
                                _options.EndpointOccupationSeconds,
                                allowRetimingOfCurrentOrderViolations: true);
                        }
                        RepairBranchModelIdentity branchIdentity = CaptureRepairBranchModelIdentity(
                            hotspotModel,
                            currentSnapshot,
                            hotspotRepairReference,
                            plan,
                            temporaryFixResult,
                            "hotspot_exactification");
                        RepairBranchExecutionDecision branchDecision =
                            repairBranchAttemptRegistry.Evaluate(branchIdentity);
                        if (!branchDecision.ShouldExecute)
                        {
                            AppendRepairBranchRuntimeState(
                                iterationIndex,
                                "hotspot_exactification",
                                plan,
                                temporaryFixResult,
                                branchIdentity,
                                RepairBranchExecutionState.MappedToEquivalentBranch,
                                "not_started",
                                branchDecision.Reason,
                                mappedToPlanId: branchDecision.MappedToPlanId);
                            ValidityFirstRepairNeighborhoodReporter.Append(
                                ResolveValidityFirstOutputDirectory(),
                                iterationIndex,
                                hotspotPlanIndex + 1,
                                plan,
                                actualTransition,
                                temporaryFixResult,
                                false,
                                "mapped_to_equivalent_branch",
                                0,
                                null,
                                null);
                            if (hotspotPlanIndex + 1 < hotspotPlans.Count)
                            {
                                hotspotModel.Dispose();
                                hotspotModel = null;
                                continue;
                            }
                            break;
                        }
                        Console.WriteLine(
                            $"Validity-first hotspot recovery: iteration={iterationIndex}, " +
                            $"neighborhood={plan.NeighborhoodId}, transition={actualTransition}, " +
                            $"endpoint_order_k={plan.EndpointOrderChangeBudget}, " +
                            $"witnesses={microStartResult.WitnessCount}, " +
                            $"matched_requirements={microStartResult.MatchedRequirementCount}, " +
                            $"temporarily_fixed_requirements={temporaryFixResult.FixedRequirementCount}, " +
                            $"temporarily_released_requirements={temporaryFixResult.ReleasedRequirementIds?.Count ?? 0}, " +
                            $"action_fixed_section_orders={timeActionStructureFixation?.FixedSectionPrecedenceCount ?? 0}, " +
                            $"action_fixed_station_orders={timeActionStructureFixation?.FixedStationEventPrecedenceCount ?? 0}, " +
                            $"action_fixed_track_orders={timeActionStructureFixation?.FixedTrackOrderCount ?? 0}, " +
                            $"action_fixed_route_orders={timeActionStructureFixation?.FixedRouteOrderCount ?? 0}, " +
                            $"requested_seconds={plan.RequestedSolveSeconds:R}, reason={plan.Reason}.");

                        _lastValidityFirstEnhancedMasterDiagnostic = null;
                        AppendRepairBranchRuntimeState(
                            iterationIndex,
                            "hotspot_exactification",
                            plan,
                            temporaryFixResult,
                            branchIdentity,
                            RepairBranchExecutionState.Running,
                            "running",
                            "optimize_started");
                        try
                        {
                            hotspotSolveStarted = TryOptimizeValidityFirstMaster(
                                hotspotModel,
                                iterationIndex,
                                "hotspot_exactification",
                                plan.RequestedSolveSeconds,
                                hotspotRepairReference);
                        }
                        catch (Exception exception)
                        {
                            AppendRepairBranchRuntimeState(
                                iterationIndex,
                                "hotspot_exactification",
                                plan,
                                temporaryFixResult,
                                branchIdentity,
                                RepairBranchExecutionState.EngineeringFailure,
                                "exception",
                                $"{exception.GetType().Name}: {exception.Message}");
                            throw;
                        }
                        ValidityFirstEnhancedMasterSolveDiagnostic? solveDiagnostic =
                            _lastValidityFirstEnhancedMasterDiagnostic;
                        int statusCode = hotspotSolveStarted
                            ? hotspotModel.Model.Get(GRB.IntAttr.Status)
                            : GRB.Status.LOADED;
                        int solutionCount = hotspotModel.Model.Get(GRB.IntAttr.SolCount);
                        if (hotspotSolveStarted)
                        {
                            repairBranchAttemptRegistry.RecordStarted(branchIdentity);
                        }
                        AppendRepairBranchRuntimeState(
                            iterationIndex,
                            "hotspot_exactification",
                            plan,
                            temporaryFixResult,
                            branchIdentity,
                            ResolveRepairBranchTerminalState(hotspotSolveStarted, statusCode, solutionCount),
                            hotspotSolveStarted ? hotspotModel.StatusName : "budget_insufficient",
                            hotspotSolveStarted ? "optimize_finished" : "global_budget_reserved",
                            solveDiagnostic?.RuntimeSeconds);
                        ValidityFirstRepairNeighborhoodReporter.Append(
                            ResolveValidityFirstOutputDirectory(),
                            iterationIndex,
                            hotspotPlanIndex + 1,
                            plan,
                            actualTransition,
                            temporaryFixResult,
                            hotspotSolveStarted,
                            hotspotSolveStarted ? hotspotModel.StatusName : "budget_insufficient",
                            solutionCount,
                            solveDiagnostic?.RuntimeSeconds,
                            solveDiagnostic?.IisDiagnostic);
                        if (hotspotModel.HasIncumbentSolution)
                        {
                            break;
                        }
                        if (hotspotPlanIndex + 1 >= hotspotPlans.Count ||
                            !ValidityFirstNeighborhoodTransitionPolicy.CanTryNext(statusCode, solutionCount))
                        {
                            break;
                        }

                        Console.WriteLine(
                            $"Validity-first hotspot recovery switches bounded neighborhood: " +
                            $"iteration={iterationIndex}, previous={plan.NeighborhoodId}, " +
                            $"status={hotspotModel.StatusName}, next={hotspotPlans[hotspotPlanIndex + 1].NeighborhoodId}; " +
                            "the switch changes temporary restrictions only and creates no permanent cut or infeasibility proof.");
                        previousHotspotStatusCode = statusCode;
                        previousHotspotSolutionCount = solutionCount;
                    }
                    hotspotAttemptDiagnostics[selectedHotspotDiagnosticIndex] =
                        hotspotAttemptDiagnostics[selectedHotspotDiagnosticIndex] with
                        {
                            JointSolveStarted = hotspotSolveStarted,
                            JointSolveHasIncumbent = hotspotModel?.HasIncumbentSolution == true,
                            JointSolverStatus = hotspotSolveStarted
                                ? hotspotModel?.StatusName ?? "not_constructed"
                                : "budget_insufficient"
                        };
                    ValidityFirstHotspotAttemptReporter.Write(
                        hotspotAttemptDiagnostics,
                        ResolveValidityFirstOutputDirectory());
                    if (hotspotModel?.HasIncumbentSolution != true)
                    {
                        if (selectedRepairAttemptFingerprint is not null)
                        {
                            validityFirstRepairAttemptRegistry.RecordFailure(selectedRepairAttemptFingerprint);
                        }
                        string status = !hotspotSolveStarted
                            ? "validity_first_global_budget_reserved_no_solve"
                            : hotspotModel!.Model.Get(GRB.IntAttr.Status) == GRB.Status.INFEASIBLE
                            ? "validity_first_hotspot_master_proven_infeasible"
                            : "validity_first_hotspot_master_unknown_no_incumbent";
                        if (hotspotModel is not null)
                        {
                            currentModel.Dispose();
                            currentModel = hotspotModel;
                        }
                        return new CheckerLoopResult(
                            currentModel,
                            iterations,
                            diagnostics,
                            activeCuts,
                            status,
                            selectionDiagnostics,
                            _options.EndpointOccupationSeconds,
                            localMicroRepairDiagnostics,
                            _recourseStageDiagnostics.ToList(),
                            _neighborhoodEscalationDiagnostics.ToList(),
                            c0Report,
                            currentReport,
                            localIterationIndex,
                            WorkingSolution: validityFirstState.WorkingSolution,
                            BestDiagnosticSolution: validityFirstState.BestDiagnosticSolution,
                            BestJointFeasibleSolution: validityFirstState.BestJointFeasibleSolution,
                            CoordinationStrategy: _options.CoordinationStrategy,
                            ConfigurationFingerprint: ValidityFirstConfigurationFingerprint.Calculate(_options),
                            PermanentMicroFeedback: _permanentFeedbackLedger.Items);
                    }

                    MasterValidationReport hotspotValidation = RunTimedMasterValidation(
                        hotspotModel,
                        iterationIndex,
                        "hotspot_exactification");
                    MicroFeasibilityReport hotspotReport = RunTimedGlobalMicroValidation(
                        microChecker,
                        hotspotModel,
                        iterationIndex,
                        "hotspot_exactification");
                    MicroConflictMetrics afterMetrics = MicroConflictMetricCalculator.Calculate(hotspotReport.Conflicts);
                    if (_options.EnablePhasedFeasibilityRecovery)
                    {
                        ValidityFirstConflictMigration migration =
                            ValidityFirstConflictMigrationAnalyzer.Analyze(
                                iterationIndex,
                                currentReport,
                                hotspotReport);
                        ValidityFirstConflictMigrationReporter.Append(
                            migration,
                            ResolveValidityFirstOutputDirectory());
                        validityFirstMigrationPriorityStations.Clear();
                        validityFirstMigrationPriorityStations.UnionWith(migration.PriorityStationNames);
                    }
                    TimetableSolutionRole hotspotRole = hotspotValidation.IsFeasible && hotspotReport.IsFeasible
                        ? TimetableSolutionRole.BestJointFeasibleSolution
                        : TimetableSolutionRole.BestDiagnosticSolution;
                    MasterSolutionSnapshot hotspotUnclassified = MasterSolutionSnapshot.Capture(hotspotModel);
                    if (_options.EnablePhasedFeasibilityRecovery)
                    {
                        hotspotUnclassified = AttachVerifiedMicroAssignments(
                            hotspotModel,
                            hotspotUnclassified,
                            microSlave,
                            iterationIndex,
                            "hotspot_exactification");
                        if (appliedHotspotEndpointTrustRegion is not null)
                        {
                            ValidityFirstEndpointOrderAuditReporter.Append(
                                iterationIndex,
                                "hotspot_exactification",
                                appliedHotspotEndpointOrderChangeBudget,
                                appliedHotspotEndpointTrustRegion,
                                hotspotRepairReference,
                                _validityFirstImmutableInitialReference!,
                                hotspotUnclassified,
                                ResolveValidityFirstOutputDirectory());
                        }
                    }
                    OptimizationBoundReport hotspotBoundReport = OptimizationBoundPolicy.Evaluate(
                        hotspotRole,
                        SolverBoundScope.GlobalStrengthenedMaster,
                        hotspotValidation.IsFeasible,
                        hotspotReport.IsFeasible,
                        hotspotUnclassified.BusinessObjective!,
                        solverLowerBound: null);
                    MasterSolutionSnapshot hotspotSnapshot = hotspotUnclassified with
                    {
                        SolutionRole = hotspotRole,
                        BoundReport = hotspotBoundReport
                    };
                    hotspotSnapshot = validityFirstState.Register(
                        hotspotSnapshot,
                        hotspotValidation.IsFeasible,
                        hotspotReport.IsFeasible,
                        afterMetrics.RootConflictCount,
                        SolverBoundScope.GlobalStrengthenedMaster,
                        globalLowerBound: null);
                    bool hotspotImprovesRepairReference =
                        afterMetrics.RootConflictCount < validityFirstRepairReferenceRootConflictCount ||
                        afterMetrics.RootConflictCount == validityFirstRepairReferenceRootConflictCount &&
                        ValidityFirstSolutionStateTracker.IsBusinessObjectiveBetter(
                            hotspotSnapshot.BusinessObjective!,
                            validityFirstRepairReference.BusinessObjective!);
                    if (_options.EnablePhasedFeasibilityRecovery && hotspotValidation.IsFeasible &&
                        hotspotImprovesRepairReference)
                    {
                        validityFirstRepairReference = hotspotSnapshot;
                        validityFirstRepairReferenceRootConflictCount = afterMetrics.RootConflictCount;
                    }
                    else if (_options.EnablePhasedFeasibilityRecovery &&
                             selectedRepairAttemptFingerprint is not null &&
                             !hotspotReport.IsFeasible)
                    {
                        validityFirstRepairAttemptRegistry.RecordFailure(selectedRepairAttemptFingerprint);
                    }
                    if (_options.EnablePhasedFeasibilityRecovery)
                    {
                        ValidityFirstRecoveryRoundReporter.Append(
                            iterationIndex,
                            "hotspot_exactification",
                            hotspotModel,
                            _validityFirstImmutableInitialReference!,
                            hotspotRepairReference,
                            currentSnapshot,
                            hotspotSnapshot,
                            beforeMetrics.RootConflictCount,
                            afterMetrics.RootConflictCount,
                            hotspotImprovesRepairReference && hotspotValidation.IsFeasible,
                            _permanentFeedbackLedger.Items.Count,
                            hotspotBuild.Component.ComponentId,
                            ResolveValidityFirstOutputDirectory());
                    }
                    Stopwatch hotspotLocalStopwatch = Stopwatch.StartNew();
                    StationLevelMicroFeasibilitySlaveResult hotspotLocalAfter = microSlave.Solve(
                        hotspotModel,
                        new MicroFeasibilitySlaveRequest(
                            new HashSet<string>(
                                [hotspotBuild.Component.StationName],
                                StringComparer.OrdinalIgnoreCase)));
                    hotspotLocalStopwatch.Stop();
                    hotspotAttemptDiagnostics[selectedHotspotDiagnosticIndex] =
                        hotspotAttemptDiagnostics[selectedHotspotDiagnosticIndex] with
                        {
                            LocalConflictsAfter = hotspotLocalAfter.Conflicts.Count,
                            GlobalConflictsAfter = hotspotReport.Conflicts.Count,
                            LocalMicroSeconds = hotspotLocalStopwatch.Elapsed.TotalSeconds
                        };
                    ValidityFirstHotspotAttemptReporter.Write(
                        hotspotAttemptDiagnostics,
                        ResolveValidityFirstOutputDirectory());
                    currentAssignmentCoreOccurrences = assignmentCorePersistence.Observe(
                        AssignmentCoreIdentityAnalyzer.Analyze(hotspotReport));
                    AssignmentCoreIdentityReporter.Write(
                        iterationIndex,
                        "hotspot_exactification",
                        currentAssignmentCoreOccurrences,
                        ResolveValidityFirstOutputDirectory());
                    string hotspotSnapshotFingerprint = SnapshotFingerprint.Calculate(hotspotSnapshot);
                    IReadOnlyList<ValidityFirstFeedbackRecurrenceDiagnostic> hotspotRecurrence =
                        BuildValidityFirstFeedbackRecurrenceDiagnostics(hotspotReport, iterationIndex);
                    ValidityFirstFeedbackConsistencyReporter.Append(
                        "hotspot_exactification",
                        hotspotRecurrence,
                        ResolveValidityFirstOutputDirectory());
                    if (_options.EnablePhasedFeasibilityRecovery &&
                        hotspotRecurrence.Any(item => item.RequiresInvestigation))
                    {
                        throw new InvalidOperationException(
                            "A returned hotspot-Master incumbent repeats a fully modeled forbidden state " +
                            "under the same applicability conditions. See validity_first_feedback_recurrence.csv.");
                    }
                    HashSet<string> hotspotInconsistentFeedbackIds = hotspotRecurrence
                        .Where(item => item.RequiresInvestigation)
                        .Select(item => item.FeedbackId)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (ValidatedMicroFeedback feedback in _permanentFeedbackLedger.Items)
                    {
                        _permanentFeedbackLedger.MarkValidatedBySnapshot(
                            [feedback.FeedbackId],
                            iterationIndex,
                            hotspotSnapshotFingerprint,
                            hotspotValidation.IsFeasible &&
                            !hotspotInconsistentFeedbackIds.Contains(feedback.FeedbackId));
                    }
                    WriteValidityFirstFeedbackLedger(
                        _permanentFeedbackLedger.Items,
                        Path.Combine(
                            ResolveValidityFirstOutputDirectory(),
                            "validity_first_feedback_ledger.csv"));
                    consecutiveFeedbackOnlyRounds = 0;
                    CheckerLoopIteration hotspotIteration = new(
                        iterationIndex,
                        currentSnapshot.ObjectiveValue,
                        hotspotSnapshot.ObjectiveValue,
                        hotspotReport.IsFeasible,
                        currentReport.Conflicts.Count,
                        hotspotReport.Conflicts.Count,
                        0,
                        0,
                        hotspotModel.StatusName,
                        "validity_first_hotspot_exactification",
                        hotspotBuild.Component.ComponentId,
                        hotspotBuild.Component.StationName,
                        CanonicalConflictCountBefore: beforeMetrics.CanonicalConflictCount,
                        CanonicalConflictCountAfter: afterMetrics.CanonicalConflictCount,
                        RootConflictCountBefore: beforeMetrics.RootConflictCount,
                        RootConflictCountAfter: afterMetrics.RootConflictCount,
                        ConflictedStationCountBefore: beforeMetrics.ConflictedStationCount,
                        ConflictedStationCountAfter: afterMetrics.ConflictedStationCount,
                        ActiveSectionEdgeCountBefore: _activeSectionEdges.Count,
                        ActiveSectionEdgeCountAfter: _activeSectionEdges.Count,
                        ActiveStationEventEdgeCountBefore: _activeStationEventEdges.Count,
                        ActiveStationEventEdgeCountAfter: _activeStationEventEdges.Count,
                        EpisodeConflictCountBefore: beforeMetrics.EpisodeConflictCount,
                        EpisodeConflictCountAfter: afterMetrics.EpisodeConflictCount,
                        AdaptivePolicyReason: "no_new_valid_projection_complete_hotspot_closure",
                        ZeroConflictMode: true);
                    hotspotIteration = AttachIterationContinuityDiagnostics(
                        hotspotIteration,
                        currentSnapshot,
                        hotspotSnapshot,
                        currentReport,
                        hotspotReport,
                        hotspotModel,
                        accepted: true,
                        [hotspotBuild.Component.ComponentId],
                        "ValidityFirstHotspotExactification",
                        iterationIisTraceStartIndex);
                    iterations.Add(hotspotIteration);
                    expectedAcceptedSnapshotFingerprint = hotspotIteration.AcceptedSnapshotFingerprint;
                    conflictTrend.Add(new CheckerConflictTrendPoint(
                        afterMetrics.RootConflictCount,
                        afterMetrics.EpisodeConflictCount));
                    currentModel.Dispose();
                    currentModel = hotspotModel;
                    currentSnapshot = hotspotSnapshot;
                    currentReport = hotspotReport;
                    SaveCheckpointIfConfigured(iterationIndex, currentSnapshot, validityFirstState);
                    continue;
                }

                if (selectedBatch.Candidates.Count > 0)
                {
                    throw new InvalidOperationException(
                        "Exact structural candidates were selected but produced no new active edge; station-core fallback cannot bypass them.");
                }

                StationCoreFallbackEvaluation fallback = EvaluateStationCoreFallback(
                    currentModel,
                    currentSnapshot,
                    currentReport,
                    activeCuts,
                    attemptedStationCoreProjectionIds,
                    iterationIndex,
                    microChecker,
                    microSlave,
                    feedbackGenerator,
                    diagnostics);
                if (!fallback.Handled)
                {
                    return new CheckerLoopResult(
                        currentModel,
                        iterations,
                        diagnostics,
                        activeCuts,
                        "no_unseen_structural_candidates",
                        selectionDiagnostics,
                        _options.EndpointOccupationSeconds,
                        localMicroRepairDiagnostics,
                        _recourseStageDiagnostics.ToList());
                }

                MicroConflictMetrics stationCoreMetricsBefore = MicroConflictMetricCalculator.Calculate(currentReport.Conflicts);
                if (fallback.Accepted)
                {
                    BasicMilpModel acceptedModel = fallback.Model
                        ?? throw new InvalidOperationException("An accepted station-core trial must return its model.");
                    MasterSolutionSnapshot acceptedSnapshot = fallback.Snapshot
                        ?? throw new InvalidOperationException("An accepted station-core trial must return its snapshot.");
                    MicroFeasibilityReport acceptedReport = fallback.Report
                        ?? throw new InvalidOperationException("An accepted station-core trial must return its report.");
                    LocalPrecedenceNogoodCut acceptedCut = fallback.Cut
                        ?? throw new InvalidOperationException("An accepted station-core trial must return its cut.");
                    using StationCoreTrialModelLease<BasicMilpModel> acceptedModelLease = new(acceptedModel);
                    MicroConflictMetrics stationCoreMetricsAfter = MicroConflictMetricCalculator.Calculate(acceptedReport.Conflicts);

                    if (CheckerStationCoreCommitResolver.Resolve(
                            hasIncumbent: true,
                            passesAcceptanceRule: true) != CheckerStationCoreCommitAction.CommitAcceptedCarrier)
                    {
                        throw new InvalidOperationException("The station-core transactional lifecycle rejected an accepted trial state.");
                    }

                    List<LocalPrecedenceNogoodCut> nextActiveCuts = activeCuts
                        .Append(acceptedCut)
                        .ToList();
                    CheckerLoopIteration acceptedIteration = new(
                        iterationIndex,
                        currentSnapshot.ObjectiveValue,
                        acceptedSnapshot.ObjectiveValue,
                        acceptedReport.IsFeasible,
                        currentReport.Conflicts.Count,
                        acceptedReport.Conflicts.Count,
                        fallback.SupportedCandidateCount,
                        fallback.TrialCount,
                        acceptedModel.StatusName,
                        "accepted_projected_station_core",
                        acceptedCut.CutId,
                        acceptedCut.StationName,
                        stationCoreMetricsBefore.CanonicalConflictCount,
                        stationCoreMetricsAfter.CanonicalConflictCount,
                        stationCoreMetricsBefore.RootConflictCount,
                        stationCoreMetricsAfter.RootConflictCount,
                        stationCoreMetricsBefore.ConflictedStationCount,
                        stationCoreMetricsAfter.ConflictedStationCount,
                        _activeSectionEdges.Count,
                        _activeSectionEdges.Count,
                        _activeStationEventEdges.Count,
                        _activeStationEventEdges.Count,
                        nextActiveCuts.Count,
                        stationCoreMetricsBefore.EpisodeConflictCount,
                        stationCoreMetricsAfter.EpisodeConflictCount,
                        0,
                        _options.StructuralRepairNeighborPadding,
                        "projected_station_core_trial",
                        acceptedCut.CutId,
                        fallback.AcceptedRecourseWindowSeconds,
                        fallback.RecourseAttemptCount,
                        fallback.FixedTemporalEventCount,
                        fallback.BoundedTemporalEventCount,
                        fallback.RetimingMetrics.TotalAbsoluteDeviationSeconds,
                        fallback.RetimingMetrics.MaximumAbsoluteDeviationSeconds,
                        0,
                        true);

                    acceptedIteration = AttachIterationContinuityDiagnostics(
                        acceptedIteration,
                        currentSnapshot,
                        acceptedSnapshot,
                        currentReport,
                        acceptedReport,
                        acceptedModel,
                        accepted: true,
                        [acceptedCut.CutId],
                        "ProjectedStationCore",
                        iterationIisTraceStartIndex);
                    iterations.Add(acceptedIteration);
                    expectedAcceptedSnapshotFingerprint =
                        acceptedIteration.AcceptedSnapshotFingerprint;
                    currentModel.Dispose();
                    currentModel = acceptedModelLease.Transfer();
                    activeCuts = nextActiveCuts;
                    currentSnapshot = acceptedSnapshot;
                    currentReport = acceptedReport;
                    SaveCheckpointIfConfigured(iterationIndex, currentSnapshot, validityFirstState);
                    conflictTrend.Add(new CheckerConflictTrendPoint(
                        stationCoreMetricsAfter.RootConflictCount,
                        stationCoreMetricsAfter.EpisodeConflictCount));
                    string? stationCoreTerminationReason = CheckerAcceptedRoundTerminationResolver.Resolve(
                        _options.FixedSnapshotMultiIterationMode,
                        currentReport.IsFeasible);
                    if (stationCoreTerminationReason is not null)
                    {
                        return new CheckerLoopResult(
                            currentModel, iterations, diagnostics, activeCuts, stationCoreTerminationReason,
                            selectionDiagnostics, _options.EndpointOccupationSeconds, localMicroRepairDiagnostics,
                            _recourseStageDiagnostics.ToList(), _neighborhoodEscalationDiagnostics.ToList(),
                            c0Report, currentReport, localIterationIndex);
                    }
                    continue;
                }

                iterations.Add(new CheckerLoopIteration(
                    iterationIndex,
                    currentSnapshot.ObjectiveValue,
                    currentSnapshot.ObjectiveValue,
                    currentReport.IsFeasible,
                    currentReport.Conflicts.Count,
                    currentReport.Conflicts.Count,
                    fallback.SupportedCandidateCount,
                    fallback.TrialCount,
                    fallback.SolverStatus,
                    fallback.TerminationReason,
                    string.Empty,
                    string.Empty,
                    stationCoreMetricsBefore.CanonicalConflictCount,
                    stationCoreMetricsBefore.CanonicalConflictCount,
                    stationCoreMetricsBefore.RootConflictCount,
                    stationCoreMetricsBefore.RootConflictCount,
                    stationCoreMetricsBefore.ConflictedStationCount,
                    stationCoreMetricsBefore.ConflictedStationCount,
                    _activeSectionEdges.Count,
                    _activeSectionEdges.Count,
                    _activeStationEventEdges.Count,
                    _activeStationEventEdges.Count,
                    activeCuts.Count,
                    stationCoreMetricsBefore.EpisodeConflictCount,
                    stationCoreMetricsBefore.EpisodeConflictCount,
                    0,
                    _options.StructuralRepairNeighborPadding,
                    "projected_station_core_trial",
                    string.Empty,
                    0,
                    fallback.RecourseAttemptCount,
                    fallback.FixedTemporalEventCount,
                    fallback.BoundedTemporalEventCount,
                    0.0,
                    0.0,
                    0,
                    true));
                iterations[^1] = AttachIterationContinuityDiagnostics(
                    iterations[^1],
                    currentSnapshot,
                    currentSnapshot,
                    currentReport,
                    currentReport,
                    currentModel,
                    accepted: false,
                    Array.Empty<string>(),
                    string.Empty,
                    iterationIisTraceStartIndex);
                return new CheckerLoopResult(
                    currentModel,
                    iterations,
                    diagnostics,
                    activeCuts,
                    fallback.TerminationReason,
                    selectionDiagnostics,
                    _options.EndpointOccupationSeconds,
                    localMicroRepairDiagnostics,
                    _recourseStageDiagnostics.ToList());
            }

            int baselineConflictCount = currentReport.Conflicts.Count;
            double baselineObjectiveValue = currentSnapshot.ObjectiveValue;

            if (currentReport.IsFeasible)
            {
                iterations.Add(new CheckerLoopIteration(
                    iterationIndex,
                    baselineObjectiveValue,
                    baselineObjectiveValue,
                    currentReport.IsFeasible,
                    baselineConflictCount,
                    baselineConflictCount,
                    0,
                    0,
                    currentModel.StatusName,
                    "already_micro_feasible",
                    string.Empty,
                    string.Empty));
                iterations[^1] = AttachIterationContinuityDiagnostics(
                    iterations[^1],
                    currentSnapshot,
                    currentSnapshot,
                    currentReport,
                    currentReport,
                    currentModel,
                    accepted: false,
                    Array.Empty<string>(),
                    string.Empty,
                    iterationIisTraceStartIndex);
                string terminationReason = _options.FixedSnapshotMultiIterationMode
                    ? "MICRO_FEASIBLE_SUCCESS"
                    : "micro_feasible";
                return new CheckerLoopResult(currentModel, iterations, diagnostics, activeCuts, terminationReason, selectionDiagnostics, _options.EndpointOccupationSeconds, localMicroRepairDiagnostics, _recourseStageDiagnostics.ToList());
            }

            StationLevelMicroFeasibilitySlaveResult focusedSlaveResult = SolveFocusedSlave(
                microSlave,
                currentModel,
                currentReport);
            MicroFeasibilityReport focusedReport = BuildFocusedReport(focusedSlaveResult);

            List<LocalPrecedenceNogoodCut> candidatePool = BuildLocalCoreCuts(
                    feedbackGenerator,
                    currentModel,
                    focusedSlaveResult,
                    screenedCutIds,
                    _options.MaxHeuristicTrialsPerIteration)
                .ToList();

            BasicMilpModel workingModel = currentModel;
            MasterSolutionSnapshot workingSnapshot = currentSnapshot;
            MicroFeasibilityReport workingReport = currentReport;
            StationLevelMicroFeasibilitySlaveResult workingFocusedSlaveResult = focusedSlaveResult;
            MicroFeasibilityReport workingFocusedReport = focusedReport;
            int screenedCandidateCount = 0;
            int acceptedCandidateCount = 0;
            int unknownCandidateCount = 0;

            foreach (LocalPrecedenceNogoodCut rawCut in candidatePool)
            {
                screenedCandidateCount++;

                LocalPrecedenceNogoodCut hydratedCut = RehydrateCutAgainstCurrentModel(rawCut, workingModel);
                if (hydratedCut.PrecedenceIncumbentValuesByKey.Count == 0 &&
                    hydratedCut.StopIncumbentValuesByStopId.Count == 0)
                {
                    continue;
                }

                PrecedenceTrustRegion trustRegion = BuildHotspotTrustRegion(
                    workingModel,
                    workingSnapshot,
                    workingFocusedSlaveResult,
                    hydratedCut,
                    iterationIndex);

                LocalPrecedenceNogoodCut shrunkenCut = ShrinkCutSupport(
                    workingModel,
                    activeCuts,
                    workingSnapshot,
                    workingReport.Conflicts.Count,
                    workingFocusedReport.Conflicts.Count,
                    workingFocusedSlaveResult,
                    hydratedCut,
                    trustRegion,
                    microChecker,
                    microSlave);

                TrialCutEvaluation evaluation = EvaluateCut(
                    activeCuts,
                    workingSnapshot,
                    workingReport.Conflicts.Count,
                    workingFocusedReport.Conflicts.Count,
                    workingFocusedSlaveResult,
                    shrunkenCut,
                    trustRegion,
                    microChecker,
                    microSlave);

                MicroConflictMetrics workingMetrics = MicroConflictMetricCalculator.Calculate(workingReport.Conflicts);
                MicroConflictMetrics? trialMetrics = evaluation.Report is null
                    ? null
                    : MicroConflictMetricCalculator.Calculate(evaluation.Report.Conflicts);

                diagnostics.Add(new TrialCutDiagnostic(
                    iterationIndex,
                    screenedCandidateCount,
                    evaluation.Cut.CutId,
                    evaluation.Cut.StationName,
                    evaluation.Cut.SourceFamily,
                    baselineConflictCount,
                    focusedReport.Conflicts.Count,
                    workingReport.Conflicts.Count,
                    workingFocusedReport.Conflicts.Count,
                    evaluation.TrialGlobalConflictCount,
                    evaluation.TrialFocusedConflictCount,
                    evaluation.HasIncumbent,
                    evaluation.Model.StatusName,
                    evaluation.IsAccepted,
                    evaluation.AcceptanceReason,
                    workingMetrics.CanonicalConflictCount,
                    trialMetrics?.CanonicalConflictCount,
                    workingMetrics.RootConflictCount,
                    trialMetrics?.RootConflictCount,
                    workingMetrics.ConflictedStationCount,
                    trialMetrics?.ConflictedStationCount,
                    workingSnapshot.ObjectiveValue,
                    evaluation.Snapshot?.ObjectiveValue,
                    _activeSectionEdges.Count,
                    _activeStationEventEdges.Count));

                if (!evaluation.HasIncumbent &&
                    evaluation.AcceptanceReason.Contains("time_limit", StringComparison.OrdinalIgnoreCase))
                {
                    unknownCandidateCount++;
                }

                if (evaluation.IsAccepted)
                {
                    activeCuts.Add(shrunkenCut);
                    acceptedCandidateCount++;

                    MasterSolutionSnapshot acceptedSnapshot = evaluation.Snapshot
                        ?? throw new InvalidOperationException("Accepted cut evaluation must contain a solution snapshot.");
                    MicroFeasibilityReport acceptedReport = evaluation.Report
                        ?? throw new InvalidOperationException("Accepted cut evaluation must contain a micro feasibility report.");

                    if (!ReferenceEquals(workingModel, currentModel))
                    {
                        workingModel.Dispose();
                    }

                    workingModel = evaluation.Model;
                    workingSnapshot = acceptedSnapshot;
                    workingReport = acceptedReport;
                    workingFocusedSlaveResult = SolveFocusedSlave(microSlave, workingModel, workingReport);
                    workingFocusedReport = BuildFocusedReport(workingFocusedSlaveResult);

                    if (workingReport.IsFeasible)
                    {
                        break;
                    }

                    continue;
                }

                evaluation.Dispose();
            }

            if (acceptedCandidateCount == 0)
            {
                string terminationReason = CheckerHeuristicTerminationResolver.Resolve(
                    screenedCandidateCount,
                    acceptedCandidateCount,
                    unknownCandidateCount);
                iterations.Add(new CheckerLoopIteration(
                    iterationIndex,
                    baselineObjectiveValue,
                    baselineObjectiveValue,
                    false,
                    baselineConflictCount,
                    baselineConflictCount,
                    focusedSlaveResult.StationResults.Count,
                    screenedCandidateCount,
                    currentModel.StatusName,
                    terminationReason,
                    string.Empty,
                    string.Empty));

                iterations[^1] = AttachIterationContinuityDiagnostics(
                    iterations[^1],
                    currentSnapshot,
                    currentSnapshot,
                    currentReport,
                    currentReport,
                    currentModel,
                    accepted: false,
                    Array.Empty<string>(),
                    string.Empty,
                    iterationIisTraceStartIndex);

                return new CheckerLoopResult(currentModel, iterations, diagnostics, activeCuts, terminationReason, selectionDiagnostics, _options.EndpointOccupationSeconds, localMicroRepairDiagnostics, _recourseStageDiagnostics.ToList());
            }

            iterations.Add(new CheckerLoopIteration(
                iterationIndex,
                baselineObjectiveValue,
                workingSnapshot.ObjectiveValue,
                workingReport.IsFeasible,
                baselineConflictCount,
                workingReport.Conflicts.Count,
                focusedSlaveResult.StationResults.Count,
                screenedCandidateCount,
                workingModel.StatusName,
                acceptedCandidateCount == 1 ? "accepted_single_cut_batch" : "accepted_multi_cut_batch",
                string.Empty,
                string.Empty));
            iterations[^1] = AttachIterationContinuityDiagnostics(
                iterations[^1],
                currentSnapshot,
                workingSnapshot,
                currentReport,
                workingReport,
                workingModel,
                accepted: true,
                activeCuts.Select(cut => cut.CutId).ToList(),
                "HeuristicLocalPrecedence",
                iterationIisTraceStartIndex);
            expectedAcceptedSnapshotFingerprint =
                iterations[^1].AcceptedSnapshotFingerprint;

            // 局部 no-good 只用于引导本轮启发式搜索：保留改进后的 incumbent，
            // 下一轮 Checker 开始前用固定快照载体移除这些临时约束。
            if (activeCuts.Count > 0)
            {
                BasicMilpModel cleanCarrier = BuildFixedSnapshotCarrier(workingSnapshot);
                if (cleanCarrier.HasIncumbentSolution)
                {
                    if (!ReferenceEquals(workingModel, currentModel))
                    {
                        workingModel.Dispose();
                    }

                    workingModel = cleanCarrier;
                    activeCuts.Clear();
                }
                else
                {
                    cleanCarrier.Dispose();
                }
            }

            if (!ReferenceEquals(workingModel, currentModel))
            {
                currentModel.Dispose();
            }

            currentModel = workingModel;
            currentSnapshot = workingSnapshot;
            currentReport = workingReport;
                SaveCheckpointIfConfigured(iterationIndex, currentSnapshot, validityFirstState);
            string? heuristicTerminationReason = CheckerAcceptedRoundTerminationResolver.Resolve(
                _options.FixedSnapshotMultiIterationMode,
                currentReport.IsFeasible);
            if (heuristicTerminationReason is not null)
            {
                return new CheckerLoopResult(
                    currentModel, iterations, diagnostics, activeCuts, heuristicTerminationReason,
                    selectionDiagnostics, _options.EndpointOccupationSeconds, localMicroRepairDiagnostics,
                    _recourseStageDiagnostics.ToList(), _neighborhoodEscalationDiagnostics.ToList(),
                    c0Report, currentReport, localIterationIndex);
            }
        }

        return new CheckerLoopResult(
            currentModel,
            iterations,
            diagnostics,
            activeCuts,
            "max_iterations_reached",
            selectionDiagnostics,
            _options.EndpointOccupationSeconds,
            localMicroRepairDiagnostics,
            _recourseStageDiagnostics.ToList(),
            _neighborhoodEscalationDiagnostics.ToList(),
            c0Report,
            currentReport,
            _options.MaxIterations,
            WorkingSolution: validityFirstState.WorkingSolution,
            BestDiagnosticSolution: validityFirstState.BestDiagnosticSolution,
            BestJointFeasibleSolution: validityFirstState.BestJointFeasibleSolution,
            CoordinationStrategy: _options.CoordinationStrategy,
            ConfigurationFingerprint: _options.CoordinationStrategy == CheckerCoordinationStrategy.ValidityFirstBenders
                ? ValidityFirstConfigurationFingerprint.Calculate(_options)
                : string.Empty,
            PermanentMicroFeedback: _permanentFeedbackLedger.Items);
    }

    private void SaveCheckpointIfConfigured(
        int iterationIndex,
        MasterSolutionSnapshot snapshot,
        ValidityFirstSolutionStateTracker? validityFirstState = null)
    {
        if (string.IsNullOrWhiteSpace(_checkpointOutputPath))
        {
            return;
        }

        CheckerCheckpointStore.SaveAtomic(
            _checkpointOutputPath,
            _instance.Meta.InstanceName,
            new CheckerCheckpoint(
                iterationIndex,
                snapshot,
                _activeSectionEdges.ToList(),
                _activeStationEventEdges.ToList(),
                _acceptedStructuralCandidateIds.ToHashSet(StringComparer.OrdinalIgnoreCase),
                _options.ZeroConflictMode,
                _options.EndpointOccupationSeconds,
                "MasterCheckerLoopSolver",
                DateTimeOffset.UtcNow,
                _options.RepairRunningTimeSupplementRatio,
                _promotedMicroComponentStore.Components.ToList(),
                _promotedMicroComponentStore.ActiveComponents.Count > 0
                    ? RepairDecisionLevel.LocalMicroExact
                    : null,
                "normal-dwell-v1",
                _instance.DatasetDiagnostics.Select(item => item.DiagnosticType).Distinct().ToList(),
                SearchConfigurationFingerprint:
                    _options.CoordinationStrategy == CheckerCoordinationStrategy.ValidityFirstBenders
                        ? ValidityFirstConfigurationFingerprint.Calculate(_options)
                        : string.Empty,
                InitialReferenceSnapshot: _options.EnablePhasedFeasibilityRecovery
                    ? _validityFirstImmutableInitialReference
                    : null,
                InitialReferenceSnapshotFingerprint: _options.EnablePhasedFeasibilityRecovery &&
                                                     _validityFirstImmutableInitialReference is not null
                    ? SnapshotFingerprint.Calculate(_validityFirstImmutableInitialReference)
                    : string.Empty,
                CoordinationStrategy: _options.CoordinationStrategy,
                BestDiagnosticSolution: validityFirstState?.BestDiagnosticSolution,
                BestJointFeasibleSolution: validityFirstState?.BestJointFeasibleSolution,
                PermanentMicroFeedback: _permanentFeedbackLedger.Items));
    }

    private CheckerLoopIteration AttachIterationContinuityDiagnostics(
        CheckerLoopIteration iteration,
        MasterSolutionSnapshot inputSnapshot,
        MasterSolutionSnapshot outputSnapshot,
        MicroFeasibilityReport inputReport,
        MicroFeasibilityReport outputReport,
        BasicMilpModel outputModel,
        bool accepted,
        IReadOnlyList<string> acceptedCandidateIds,
        string acceptedRepairType,
        int iisTraceStartIndex)
    {
        MicroConflictCategoryCounts beforeCategories =
            MicroConflictCategoryCounter.Calculate(inputReport.Conflicts);
        MicroConflictCategoryCounts afterCategories =
            MicroConflictCategoryCounter.Calculate(outputReport.Conflicts);
        IReadOnlyList<IisGuidedRepairTraceDiagnostic> iterationIisTrace =
            _iisGuidedRepairTraceDiagnostics.Skip(iisTraceStartIndex).ToList();
        string inputFingerprint = SnapshotFingerprint.Calculate(inputSnapshot);
        string outputFingerprint = SnapshotFingerprint.Calculate(outputSnapshot);
        MasterValidationReport validation = new MasterSolutionValidator().Validate(outputModel);

        return iteration with
        {
            ExactConflictCountBefore = beforeCategories.ExactHeadwayConflictCount,
            ExactConflictCountAfter = afterCategories.ExactHeadwayConflictCount,
            AssignmentConflictCountBefore = beforeCategories.StationAssignmentConflictCount,
            AssignmentConflictCountAfter = afterCategories.StationAssignmentConflictCount,
            Accepted = accepted,
            AcceptedCandidateIds = accepted
                ? string.Join('|', acceptedCandidateIds.Order(StringComparer.OrdinalIgnoreCase))
                : string.Empty,
            AcceptedRepairType = accepted ? acceptedRepairType : string.Empty,
            IisGuidedUsed = iterationIisTrace.Count > 0,
            IisExpansionRounds = iterationIisTrace
                .Select(item => item.ExpansionRound)
                .Distinct()
                .Count(),
            MasterValidatorViolationCount = validation.Violations.Count,
            InputSnapshotFingerprint = inputFingerprint,
            AcceptedSnapshotFingerprint = accepted ? outputFingerprint : string.Empty,
            ConflictFingerprintBefore = MicroConflictFingerprint.Calculate(inputReport.Conflicts),
            ConflictFingerprintAfter = MicroConflictFingerprint.Calculate(outputReport.Conflicts),
            ContinuityVerified = true
        };
    }

    private static string BuildCandidateSummary(IReadOnlyList<HeadwayConflictCandidate> candidates) =>
        string.Join('|', candidates.Select(candidate => $"{candidate.CandidateKey}@{candidate.Score:F1}"));

    private IReadOnlyList<ValidityFirstRepairNeighborhoodPlan> ApplyRepairNeighborhoodSelector(
        MasterSolutionSnapshot repairReference,
        MicroFeasibilityReport conflictReport,
        IReadOnlyList<ValidityFirstRepairNeighborhoodPlan> legacyPlans,
        PromotedMicroComponent? hotspotComponent,
        int iterationIndex,
        string phase)
    {
        ArgumentNullException.ThrowIfNull(repairReference);
        ArgumentNullException.ThrowIfNull(conflictReport);
        ArgumentNullException.ThrowIfNull(legacyPlans);
        IReadOnlyList<VerifiedMicroAssignmentWitness> witnesses =
            repairReference.VerifiedMicroAssignments ?? [];
        HashSet<string> temporarilyFixedRequirementIds = witnesses
            .Select(item => item.RequirementId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string candidateFingerprint = SnapshotFingerprint.Calculate(repairReference);
        if (_options.RepairNeighborhoodSelector == RepairNeighborhoodSelectorKind.Legacy)
        {
            foreach (ValidityFirstRepairNeighborhoodPlan plan in legacyPlans)
            {
                ConflictPropagationSelectionResult directLegacyAudit = new(
                    plan.ReleasedRequirementIds,
                    [new ConflictPropagationSelectionAuditRow(
                        candidateFingerprint,
                        $"{phase}::{plan.NeighborhoodId}",
                        "LegacyDirectBypass",
                        string.Empty,
                        string.Empty,
                        string.Empty,
                        string.Empty,
                        null,
                        0.0,
                        "原范围选择器直接保留既有释放集合，未调用传播分析。",
                        false,
                        string.Empty,
                        "legacy_direct_bypass",
                        string.Empty,
                        string.Empty,
                        string.Empty,
                        string.Empty)],
                    "legacy_direct_bypass",
                    0.0,
                    0,
                    0,
                    0);
                ConflictPropagationSelectionReporter.Append(
                    ResolveValidityFirstOutputDirectory(),
                    iterationIndex,
                    directLegacyAudit);
                Console.WriteLine(
                    $"Repair-neighborhood selector: iteration={iterationIndex}, phase={phase}, " +
                    $"branch={plan.NeighborhoodId}, selector=Legacy, " +
                    $"legacy_release={plan.ReleasedRequirementIds.Count}, " +
                    $"selected_release={plan.ReleasedRequirementIds.Count}, events=0, relations=0, " +
                    "seeds=0, analysis_seconds=0, fallback=legacy_direct_bypass.");
            }
            return legacyPlans;
        }
        ConflictPropagationGraph graph = _options.RepairNeighborhoodSelector ==
                                         RepairNeighborhoodSelectorKind.Propagation
            ? ConflictPropagationGraphBuilder.Build(
                _instance,
                repairReference,
                conflictReport,
                witnesses,
                hotspotComponent?.Requirements
                    .Select(item => item.RequirementId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase))
            : new ConflictPropagationGraph(
                [], [], [], temporarilyFixedRequirementIds);
        List<ValidityFirstRepairNeighborhoodPlan> selectedPlans = [];
        foreach (ValidityFirstRepairNeighborhoodPlan plan in legacyPlans)
        {
            string branchId = $"{phase}::{plan.NeighborhoodId}";
            ConflictPropagationSelectionResult selection =
                ConflictPropagationRepairNeighborhoodSelector.Select(
                    new ConflictPropagationSelectionRequest(
                        _options.RepairNeighborhoodSelector,
                        candidateFingerprint,
                        branchId,
                        plan.EndpointOrderChangeBudget,
                        plan.ReleasedRequirementIds.Count,
                        plan.ReleasedRequirementIds,
                        graph.TemporarilyFixedRequirementIds,
                        graph.Events,
                        graph.Relations,
                        graph.Seeds));
            ConflictPropagationSelectionReporter.Append(
                ResolveValidityFirstOutputDirectory(),
                iterationIndex,
                selection);
            Console.WriteLine(
                $"Repair-neighborhood selector: iteration={iterationIndex}, phase={phase}, " +
                $"branch={plan.NeighborhoodId}, selector={_options.RepairNeighborhoodSelector}, " +
                $"legacy_release={plan.ReleasedRequirementIds.Count}, " +
                $"selected_release={selection.ReleasedRequirementIds.Count}, " +
                $"events={selection.EventCount}, relations={selection.RelationCount}, " +
                $"seeds={selection.SeedCount}, analysis_seconds={selection.AnalysisSeconds:R}, " +
                $"fallback={selection.FallbackReason}.");
            selectedPlans.Add(plan with
            {
                NeighborhoodId = $"{plan.NeighborhoodId}_propagation",
                ReleasedRequirementIds = selection.ReleasedRequirementIds,
                ReleasesAllVerifiedAssignments = temporarilyFixedRequirementIds.Count > 0 &&
                                                 selection.ReleasedRequirementIds.Count ==
                                                 temporarilyFixedRequirementIds.Count,
                Reason = string.IsNullOrWhiteSpace(selection.FallbackReason)
                    ? $"机制A按事件时移、时间边界和资源域传播选择{selection.ReleasedRequirementIds.Count}项临时微观释放；{plan.Reason}"
                    : $"机制A回退到原释放集合：{selection.FallbackReason} {plan.Reason}"
            });
        }
        return selectedPlans;
    }

    private IReadOnlyList<ValidityFirstRepairNeighborhoodPlan> ApplyRepairActionSelector(
        MasterSolutionSnapshot repairReference,
        MicroFeasibilityReport conflictReport,
        IReadOnlyList<ValidityFirstRepairNeighborhoodPlan> scopeSelectedPlans,
        IReadOnlyList<VerifiedMicroAssignmentWitness> actionWitnesses,
        bool allowCurrentAssignmentCandidate,
        IReadOnlySet<string> temporarilyReleasedStationNames,
        int iterationIndex,
        string phase)
    {
        ArgumentNullException.ThrowIfNull(repairReference);
        ArgumentNullException.ThrowIfNull(conflictReport);
        ArgumentNullException.ThrowIfNull(scopeSelectedPlans);
        ArgumentNullException.ThrowIfNull(actionWitnesses);
        ArgumentNullException.ThrowIfNull(temporarilyReleasedStationNames);
        RepairActionSelectionRequest request = RepairActionSelectionContextBuilder.Build(
            _options.RepairActionSelector,
            _instance,
            repairReference,
            conflictReport,
            actionWitnesses,
            _promotedMicroComponentStore.ActiveComponents,
            scopeSelectedPlans,
            temporarilyReleasedStationNames,
            allowCurrentAssignmentCandidate,
            _options.ValidityFirstLocalNeighborhoodTimeLimitSeconds,
            _options.ValidityFirstFixedAssignmentRetimingTimeLimitSeconds,
            _options.TimeLimitSecondsPerSolve);
        RepairActionSelectionResult selection = RuleBasedRepairActionSelector.Select(request);
        RepairActionSelectionReporter.Append(
            ResolveValidityFirstOutputDirectory(),
            iterationIndex,
            phase,
            selection);
        RepairBranchExecutionLedgerReporter.AppendSelection(
            ResolveValidityFirstOutputDirectory(),
            iterationIndex,
            phase,
            selection);
        string order = string.Join('|', selection.ExecutablePlans.Select(plan =>
            $"{plan.ActionType}:{plan.NeighborhoodId}"));
        Console.WriteLine(
            $"Repair-action selector: iteration={iterationIndex}, phase={phase}, " +
            $"selector={_options.RepairActionSelector}, candidates={selection.AllActionPlans.Count}, " +
            $"attempts={selection.ExecutablePlans.Count}, order={order}, " +
            $"analysis_seconds={selection.AnalysisSeconds:R}, " +
            $"uses_propagation={selection.UsesPropagationAnalysis}, " +
            $"fallback={selection.FallbackReason}.");
        return selection.ExecutablePlans;
    }

    private RepairBranchModelIdentity CaptureRepairBranchModelIdentity(
        BasicMilpModel model,
        MasterSolutionSnapshot candidate,
        MasterSolutionSnapshot repairReference,
        ValidityFirstRepairNeighborhoodPlan plan,
        VerifiedMicroAssignmentTemporaryFixResult fixation,
        string phase)
    {
        string planId = string.IsNullOrWhiteSpace(plan.ActionPlanId)
            ? $"{plan.ActionType}::{plan.NeighborhoodId}"
            : plan.ActionPlanId;
        string scopeFingerprint = string.Join('|',
            _promotedMicroComponentStore.ActiveComponents
                .Where(component => component.Active)
                .OrderBy(component => component.ComponentId, StringComparer.OrdinalIgnoreCase)
                .Select(component =>
                    $"{component.ComponentId}:{component.StationName}:" +
                    string.Join(';', component.Requirements
                        .Select(requirement => requirement.RequirementId)
                        .Order(StringComparer.OrdinalIgnoreCase))));
        if (string.IsNullOrWhiteSpace(scopeFingerprint))
        {
            scopeFingerprint = "no_promoted_micro_component";
        }
        return RepairBranchModelIdentityBuilder.Capture(
            model,
            new RepairBranchModelContext(
                SnapshotFingerprint.Calculate(candidate),
                SnapshotFingerprint.Calculate(repairReference),
                _permanentFeedbackLedger.Items
                    .Select(item => item.FeedbackId)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                $"{phase}::{scopeFingerprint}",
                planId,
                plan.ActionType,
                plan.RequestedSolveSeconds,
                ValidityFirstSolveParameterPolicy.Resolve(phase, _options),
                ValidityFirstConfigurationFingerprint.Calculate(_options),
                fixation.FixedRequirementIds ?? [],
                fixation.ReleasedRequirementIds ?? [],
                CalculateMipStartFingerprint(model)));
    }

    private static string CalculateMipStartFingerprint(BasicMilpModel model)
    {
        model.Model.Update();
        List<string> assigned = [];
        foreach (GRBVar variable in model.Model.GetVars())
        {
            double value = variable.Get(GRB.DoubleAttr.Start);
            if (!double.IsFinite(value) || Math.Abs(value) >= GRB.UNDEFINED / 2.0) continue;
            assigned.Add($"{variable.Get(GRB.StringAttr.VarName)}={value:R}");
        }
        if (assigned.Count == 0) return "no_mip_start";
        byte[] bytes = System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(string.Join('\n', assigned.Order(StringComparer.Ordinal))));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private void AppendRepairBranchRuntimeState(
        int iterationIndex,
        string phase,
        ValidityFirstRepairNeighborhoodPlan plan,
        VerifiedMicroAssignmentTemporaryFixResult fixation,
        RepairBranchModelIdentity identity,
        RepairBranchExecutionState state,
        string solverStatus,
        string reason,
        double? solveSeconds = null,
        string mappedToPlanId = "")
    {
        RepairBranchExecutionLedgerReporter.Append(
            ResolveValidityFirstOutputDirectory(),
            new RepairBranchExecutionLedgerEntry(
                DateTimeOffset.Now,
                iterationIndex,
                phase,
                identity.CandidateFingerprint,
                identity.RepairReferenceFingerprint,
                identity.PlanId,
                plan.ActionType,
                plan.EndpointOrderChangeBudget,
                plan.ReleasedRequirementIds.Order(StringComparer.OrdinalIgnoreCase).ToList(),
                (fixation.FixedRequirementIds ?? []).Order(StringComparer.OrdinalIgnoreCase).ToList(),
                plan.ReleasedRequirementIds.Order(StringComparer.OrdinalIgnoreCase).ToList(),
                identity.ActualFixedRequirementIds,
                identity.ActualReleasedRequirementIds,
                [plan.ActionApplicability, plan.Reason],
                identity.MathematicalModelIdentity,
                identity.GurobiModelFingerprint,
                identity.SearchAttemptIdentity,
                mappedToPlanId,
                ResolveRepairBranchBudgetTier(plan),
                plan.RequestedSolveSeconds,
                solveSeconds,
                state,
                solverStatus,
                reason,
                identity.VariableCount,
                identity.ConstraintCount,
                identity.NonzeroCount,
                fixation.FixedChoiceVariableCount));
    }

    private RepairActionBudgetTier ResolveRepairBranchBudgetTier(
        ValidityFirstRepairNeighborhoodPlan plan)
    {
        if (plan.ActionType == RepairActionType.LegacyFallback)
        {
            return RepairActionBudgetTier.Legacy;
        }
        if (plan.RequestedSolveSeconds <= _options.ValidityFirstLocalNeighborhoodTimeLimitSeconds + 1e-9)
        {
            return RepairActionBudgetTier.Short;
        }
        if (plan.RequestedSolveSeconds <= _options.ValidityFirstFixedAssignmentRetimingTimeLimitSeconds + 1e-9)
        {
            return RepairActionBudgetTier.FixedAssignmentRetiming;
        }
        return RepairActionBudgetTier.Long;
    }

    private static RepairBranchExecutionState ResolveRepairBranchTerminalState(
        bool solveStarted,
        int statusCode,
        int solutionCount)
    {
        if (!solveStarted)
        {
            return RepairBranchExecutionState.BudgetInsufficient;
        }
        if (solutionCount > 0)
        {
            return RepairBranchExecutionState.CandidateFound;
        }
        return statusCode switch
        {
            GRB.Status.INFEASIBLE => RepairBranchExecutionState.ProvenInfeasible,
            GRB.Status.TIME_LIMIT => RepairBranchExecutionState.TimedOutWithoutCandidate,
            _ => RepairBranchExecutionState.UnknownWithoutCandidate
        };
    }

    private IReadOnlyList<ValidityFirstRepairNeighborhoodPlan> BuildValidityFirstRepairNeighborhoodPlans(
        MasterSolutionSnapshot repairReference,
        IReadOnlySet<int> coreTrainIndices,
        IReadOnlySet<int> directlyRelatedTrainIndices,
        IReadOnlySet<int> currentConflictTrainIndices,
        PromotedMicroComponent? hotspotComponent)
    {
        IReadOnlyList<VerifiedMicroAssignmentWitness> witnesses =
            repairReference.VerifiedMicroAssignments ?? [];
        double longLocalSeconds = _options.TimeLimitSecondsPerSolve;
        double fullFallbackSeconds = _options.ValidityFirstLocalNeighborhoodTimeLimitSeconds;

        if (!_validityFirstSavedRepairNeighborhoodConsumed &&
            _validityFirstSavedRepairNeighborhood is not null)
        {
            if (hotspotComponent is null || !string.Equals(
                    hotspotComponent.StationName,
                    _options.ValidityFirstReplayHotspotStationName,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Saved neighborhood requires hotspot '{_options.ValidityFirstReplayHotspotStationName}', " +
                    $"but the constructed hotspot is '{hotspotComponent?.StationName ?? "none"}'.");
            }
            IReadOnlyList<ValidityFirstRepairNeighborhoodPlan> replayPlans =
                ValidityFirstRepairNeighborhoodPlanner.BuildPrioritizedContinuation(
                    witnesses,
                    _validityFirstSavedRepairNeighborhood,
                    currentConflictTrainIndices,
                    longLocalSeconds,
                    fullFallbackSeconds);
            _validityFirstSavedRepairNeighborhoodConsumed = true;
            ValidityFirstSavedNeighborhoodReplayReporter.Write(
                ResolveValidityFirstOutputDirectory(),
                _options.ValidityFirstReplayNeighborhoodInputPath!,
                SnapshotFingerprint.Calculate(repairReference),
                hotspotComponent.StationName,
                _validityFirstSavedRepairNeighborhood,
                witnesses.Count,
                replayPlans[0].RequestedSolveSeconds);
            Console.WriteLine(
                $"Validity-first saved neighborhood replay: station={hotspotComponent.StationName}, " +
                $"repair_reference={SnapshotFingerprint.Calculate(repairReference)}, " +
                $"released_business_ids={_validityFirstSavedRepairNeighborhood.ReleasedRequirementIds.Count}, " +
                $"K={_validityFirstSavedRepairNeighborhood.EndpointOrderChangeBudget}, " +
                $"requested_seconds={replayPlans[0].RequestedSolveSeconds:R}.");
            return replayPlans;
        }

        HashSet<string> correlatedRequirementIds = witnesses
            .Where(witness => directlyRelatedTrainIndices.Contains(witness.TrainIndex))
            .Select(witness => witness.RequirementId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (correlatedRequirementIds.Count == 0)
        {
            return ValidityFirstRepairNeighborhoodPlanner.Build(
                witnesses,
                coreTrainIndices,
                directlyRelatedTrainIndices,
                _options.ValidityFirstLocalNeighborhoodTimeLimitSeconds,
                _options.TimeLimitSecondsPerSolve);
        }
        ValidityFirstSavedRepairNeighborhood dynamicPrimary = new(
            "dynamic_correlated_trains_k4",
            4,
            correlatedRequirementIds);
        IReadOnlyList<ValidityFirstRepairNeighborhoodPlan> prioritized =
            ValidityFirstRepairNeighborhoodPlanner.BuildPrioritizedContinuation(
                witnesses,
                dynamicPrimary,
                currentConflictTrainIndices,
                longLocalSeconds,
                fullFallbackSeconds);
        return prioritized
            .Select((plan, index) => index == 0
                ? plan with
                {
                    NeighborhoodId = "release_correlated_trains_k4",
                    Reason = "优先释放热点核心及直接冲突列车，并给予保留多数见证的K=4局部模型较长预算。"
                }
                : plan)
            .ToList();
    }

    private static (double Total, double Maximum, int SectionsUsingSupplement)
        CalculateRunningSupplementStatistics(MasterSolutionSnapshot snapshot)
    {
        IReadOnlyCollection<double> values =
            snapshot.RunningTimeSupplementSecondsByKey?.Values.ToList() ?? [];
        return values.Count == 0
            ? (0.0, 0.0, 0)
            : (
                values.Sum(),
                values.Max(),
                values.Count(value => value > 1e-6));
    }

    private static void UpdateStationHotspotPersistence(
        MicroFeasibilityReport report,
        IDictionary<string, int> persistenceByStation)
    {
        HashSet<string> activeStations = report.Conflicts
            .Select(conflict => conflict.StationName)
            .Where(station => !string.IsNullOrWhiteSpace(station))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string station in activeStations)
        {
            persistenceByStation.TryGetValue(station, out int priorRounds);
            persistenceByStation[station] = priorRounds + 1;
        }
        foreach (string stale in persistenceByStation.Keys.Where(station => !activeStations.Contains(station)).ToList())
        {
            persistenceByStation.Remove(stale);
        }
    }

    /// <summary>
    /// 先选择身份连续的联合进路—股道核心，再选择同站聚集的 exact-headway 冲突。
    /// 返回完整优先序列，使调用方可在某个资源闭包过大时继续评估下一个候选，而不是缩小闭包。
    /// </summary>
    private static IReadOnlyList<ValidityFirstHotspotCandidate> ResolveValidityFirstHotspotCandidates(
        StationLevelMicroFeasibilitySlaveResult slaveResult,
        MicroFeasibilityReport globalReport,
        IReadOnlyList<AssignmentCoreOccurrence> assignmentOccurrences,
        IReadOnlyDictionary<string, int> stationPersistence,
        IReadOnlySet<string>? migrationPriorityStations = null,
        string? replayPreferredStationName = null,
        bool requireJointRepairScope = false)
    {
        Dictionary<string, StationLevelMicroSubproblemResult> stationResults = slaveResult.StationResults
            .Where(result => !result.IsFeasible)
            .ToDictionary(result => result.StationName, StringComparer.OrdinalIgnoreCase);
        HashSet<string> selectedStations = new(StringComparer.OrdinalIgnoreCase);
        List<ValidityFirstHotspotCandidate> candidates = [];

        foreach (AssignmentCoreOccurrence occurrence in assignmentOccurrences
                     .Where(item => item.ConsecutiveRounds >= (requireJointRepairScope ? 1 : 2))
                     .OrderByDescending(item => item.ConsecutiveRounds)
                     .ThenBy(item => item.Identity.StationName, StringComparer.OrdinalIgnoreCase))
        {
            if (!stationResults.TryGetValue(
                    occurrence.Identity.StationName,
                    out StationLevelMicroSubproblemResult? stationResult) ||
                !selectedStations.Add(stationResult.StationName))
            {
                continue;
            }
            candidates.Add(new ValidityFirstHotspotCandidate(
                stationResult,
                occurrence.ConsecutiveRounds >= 2
                    ? "persistent_assignment_core"
                    : "current_assignment_core",
                occurrence.Identity.IdentityKey,
                occurrence.ConsecutiveRounds));
        }

        IEnumerable<IGrouping<string, MicroConflictExplanation>> exactHeadwayByStation = globalReport.Conflicts
            .Where(conflict => string.Equals(
                                   conflict.ConflictKind,
                                   "exact_headway_infeasible",
                                   StringComparison.OrdinalIgnoreCase) &&
                               !string.IsNullOrWhiteSpace(conflict.StationName))
            .GroupBy(conflict => conflict.StationName, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase);
        foreach (IGrouping<string, MicroConflictExplanation> stationGroup in exactHeadwayByStation)
        {
            if (!stationResults.TryGetValue(
                    stationGroup.Key,
                    out StationLevelMicroSubproblemResult? stationResult) ||
                !selectedStations.Add(stationResult.StationName))
            {
                continue;
            }
            candidates.Add(new ValidityFirstHotspotCandidate(
                stationResult,
                "correlated_exact_headway",
                string.Empty,
                Math.Max(1, stationPersistence.GetValueOrDefault(stationResult.StationName))));
        }

        return candidates
            .Select((candidate, index) => (Candidate: candidate, OriginalIndex: index))
            .OrderByDescending(item => !string.IsNullOrWhiteSpace(replayPreferredStationName) &&
                                       string.Equals(
                                           item.Candidate.StationResult.StationName,
                                           replayPreferredStationName,
                                           StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(item => migrationPriorityStations?.Contains(
                item.Candidate.StationResult.StationName) == true)
            .ThenBy(item => item.OriginalIndex)
            .Select(item => migrationPriorityStations?.Contains(
                    item.Candidate.StationResult.StationName) == true
                ? item.Candidate with { Priority = $"migration_linked::{item.Candidate.Priority}" }
                : item.Candidate)
            .ToList();
    }

    private ValidityFirstHotspotBuildResult BuildValidityFirstHotspotComponent(
        BasicMilpModel model,
        StationLevelMicroSubproblemResult stationResult,
        int iterationIndex,
        int persistentRounds) => _options.EnablePhasedFeasibilityRecovery
        ? ValidityFirstHotspotComponentBuilder.Build(
            model,
            stationResult,
            ValidityFirstRepairScopeBudget.FromOptions(_options),
            _options.EndpointOccupationSeconds,
            iterationIndex,
            persistentRounds)
        : ValidityFirstHotspotComponentBuilder.Build(
            model,
            stationResult,
            _options.ValidityFirstHotspotMaximumRequirementCount,
            _options.EndpointOccupationSeconds,
            iterationIndex,
            persistentRounds);

    private IReadOnlyList<ValidityFirstFeedbackRecurrenceDiagnostic>
        BuildValidityFirstFeedbackRecurrenceDiagnostics(
            MicroFeasibilityReport report,
            int iterationIndex)
    {
        IReadOnlyList<HeadwayConflictEpisode> episodes = HeadwayConflictEpisodeAnalyzer.Analyze(
            report.Conflicts,
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));
        IReadOnlyList<HeadwayConflictCandidate> allCurrentCandidates =
            HeadwayConflictCandidateSelector.ResolveAvailableCandidates(
                episodes,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        List<ValidityFirstFeedbackRecurrenceDiagnostic> diagnostics = [];
        foreach (HeadwayConflictCandidate candidate in allCurrentCandidates.Where(candidate =>
                     string.Equals(
                         candidate.Evidence?.MicroClassification,
                         "UnavoidableConflict",
                         StringComparison.OrdinalIgnoreCase)))
        {
            ValidatedMicroFeedback current = ValidatedMicroFeedbackFactory.FromExactHeadwayCandidate(candidate);
            if (!_permanentFeedbackLedger.TryGet(
                    current.FeedbackId,
                    out ValidatedMicroFeedback? existing) || existing is null)
            {
                continue;
            }
            diagnostics.Add(ValidityFirstFeedbackConsistencyAuditor.Compare(
                iterationIndex,
                existing,
                current,
                existing.AppliedToModel));
        }

        return diagnostics
            .GroupBy(item => item.FeedbackId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.RequiresInvestigation).First())
            .OrderBy(item => item.FeedbackId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private sealed record ValidityFirstHotspotCandidate(
        StationLevelMicroSubproblemResult StationResult,
        string Priority,
        string AssignmentCoreIdentity,
        int PersistentRounds);

    private BasicMilpModel BuildFixedSnapshotCarrier(MasterSolutionSnapshot snapshot)
    {
        ValidatedSnapshotCarrier carrier = new ValidatedSnapshotCarrierBuilder().Build(
            _instance,
            _neighborRadius,
            snapshot,
            _options.RepairRunningTimeSupplementRatio,
            _activeSectionEdges,
            _activeStationEventEdges,
            _promotedMicroComponentStore.ActiveComponents,
            _options.EndpointOccupationSeconds);
        return carrier.Model;
    }

    private BasicMilpModel BuildValidityFirstFixedSnapshotCarrier(MasterSolutionSnapshot snapshot)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        BasicMilpModel model = BuildFixedSnapshotCarrier(snapshot);
        stopwatch.Stop();
        ValidityFirstPhaseTimingReporter.Append(
            0,
            "initial_snapshot_carrier_construction",
            "model_construction",
            stopwatch.Elapsed.TotalSeconds,
                    GetValidityFirstCumulativeElapsedSeconds(),
            ResolveValidityFirstOutputDirectory());
        return model;
    }

    public static void WriteProgressReport(CheckerLoopResult result, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        WriteIterationsCsv(
            result.Iterations,
            result.TerminationReason,
            result.EndpointOccupationSeconds,
            Path.Combine(outputDirectory, "checker_loop_progress.csv"));
        WriteIterationsMarkdown(result, Path.Combine(outputDirectory, "checker_loop_progress.md"));
        WriteTrialDiagnosticsCsv(result.Diagnostics, Path.Combine(outputDirectory, "checker_loop_trial_diagnostics.csv"));
        WriteTrialDiagnosticsMarkdown(result.Diagnostics, Path.Combine(outputDirectory, "checker_loop_trial_diagnostics.md"));
        WriteFeedbackFamilySummaryCsv(result.Diagnostics, Path.Combine(outputDirectory, "checker_loop_feedback_family_summary.csv"));
        WriteFeedbackFamilySummaryMarkdown(result.Diagnostics, Path.Combine(outputDirectory, "checker_loop_feedback_family_summary.md"));
        WriteLocalMicroRepairDiagnostics(
            result.LocalMicroRepairDiagnostics ?? Array.Empty<LocalMicroRepairDiagnostic>(),
            Path.Combine(outputDirectory, "local_micro_repair_diagnostics.csv"),
            Path.Combine(outputDirectory, "local_micro_repair_diagnostics.md"));
        WriteFeedbackArbitrationDiagnostics(
            result.SelectionDiagnostics ?? Array.Empty<CheckerCutSelectionDiagnostic>(),
            result.LocalMicroRepairDiagnostics ?? Array.Empty<LocalMicroRepairDiagnostic>(),
            Path.Combine(outputDirectory, "feedback_diagnostics.csv"));
        CheckerRecourseStageDiagnosticWriter.Write(
            result.RecourseStageDiagnostics ?? Array.Empty<CheckerRecourseStageDiagnostic>(),
            outputDirectory);
        HierarchicalRepairDiagnosticsReporter.WriteFromRecourseStages(
            result.RecourseStageDiagnostics ?? Array.Empty<CheckerRecourseStageDiagnostic>(),
            outputDirectory);
        CheckerNeighborhoodEscalationDiagnosticWriter.Write(
            result.NeighborhoodEscalationDiagnostics ?? Array.Empty<CheckerNeighborhoodEscalationDiagnostic>(),
            outputDirectory);
        StructuralTrialIisDiagnosticsReporter.Write(
            result.StructuralTrialIisDiagnostics,
            outputDirectory);
        StructuralCandidateFeasibilityDiagnosticsReporter.Write(
            result.StructuralCandidateProbeDiagnostics,
            result.StructuralCandidateProbeStopReason,
            outputDirectory);
        IisGuidedRepairDiagnosticsReporter.Write(
            result.IisGuidedRepairTraceDiagnostics,
            result.IisDependencyPlanDiagnostics,
            outputDirectory);
        RejectedTrialConflictDeltaReporter.Write(
            result.EffectiveRejectedTrialConflictDeltas,
            outputDirectory);
        StructuralSingletonGlobalTrialReporter.Write(
            result.StructuralSingletonGlobalTrialDiagnostics,
            outputDirectory);
        IReadOnlyList<CheckerCutAuditRecord> audit = CheckerCutAuditBuilder.Build(result.Iterations, result.Diagnostics);
        WriteCutAuditCsv(audit, Path.Combine(outputDirectory, "checker_loop_cut_audit.csv"));
        WriteCutAuditMarkdown(audit, Path.Combine(outputDirectory, "checker_loop_cut_audit.md"));
        CheckerCutSelectionReporter.Write(result.SelectionDiagnostics ?? Array.Empty<CheckerCutSelectionDiagnostic>(), outputDirectory);
        if (result.CoordinationStrategy == CheckerCoordinationStrategy.ValidityFirstBenders)
        {
            WriteValidityFirstSolutionStates(
                result,
                Path.Combine(outputDirectory, "validity_first_solution_states.md"));
            WriteValidityFirstFeedbackLedger(
                result.PermanentMicroFeedback ?? Array.Empty<ValidatedMicroFeedback>(),
                Path.Combine(outputDirectory, "validity_first_feedback_ledger.csv"));
        }
    }

    private static void WriteValidityFirstFeedbackLedger(
        IReadOnlyList<ValidatedMicroFeedback> feedback,
        string path)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
        using StreamWriter writer = new(path, false, new UTF8Encoding(false));
        writer.WriteLine("feedback_id,station_name,pair_key,validity,persistence,proof_fingerprint,proof_source,mathematical_constraint,applicability_conditions,evidence_summary,condition_count,conditions_represented_in_master,generated_iteration,applied_to_model,applied_iteration,validated_by_snapshot,validated_iteration,validated_snapshot_fingerprint,returned_snapshot_satisfies_feedback");
        foreach (ValidatedMicroFeedback item in feedback.OrderBy(
                     item => item.FeedbackId,
                     StringComparer.OrdinalIgnoreCase))
        {
            writer.WriteLine(string.Join(',',
                Escape(item.FeedbackId),
                Escape(item.StationName),
                Escape(item.PairKey),
                item.Validity,
                ValidatedMicroFeedbackPolicy.ResolvePersistence(item),
                Escape(item.ProofFingerprint),
                Escape(item.ProofSource),
                Escape(item.MathematicalConstraint),
                Escape(item.ApplicabilityConditions),
                Escape(item.EvidenceSummary),
                item.Conditions.Count.ToString(CultureInfo.InvariantCulture),
                item.ConditionsRepresentedInMaster,
                item.GeneratedIteration?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                item.AppliedToModel,
                item.AppliedIteration?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                item.ValidatedBySnapshot,
                item.ValidatedIteration?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                item.ValidatedSnapshotFingerprint,
                item.ReturnedSnapshotSatisfiesFeedback?.ToString() ?? string.Empty));
        }
    }

    private static void WriteValidityFirstSolutionStates(CheckerLoopResult result, string path)
    {
        using StreamWriter writer = new(path, false, new UTF8Encoding(false));
        writer.WriteLine("# Validity-first solution states");
        writer.WriteLine();
        writer.WriteLine($"- coordination_strategy: `{result.CoordinationStrategy}`");
        writer.WriteLine($"- configuration_fingerprint: `{result.ConfigurationFingerprint}`");
        WriteSolutionState(writer, "working_solution", result.WorkingSolution);
        WriteSolutionState(writer, "best_diagnostic_solution", result.BestDiagnosticSolution);
        WriteSolutionState(writer, "best_joint_feasible_solution", result.BestJointFeasibleSolution);
    }

    private static void WriteSolutionState(
        TextWriter writer,
        string label,
        MasterSolutionSnapshot? snapshot)
    {
        writer.WriteLine();
        writer.WriteLine($"## {label}");
        writer.WriteLine();
        if (snapshot is null)
        {
            writer.WriteLine("- available: `false`");
            return;
        }

        writer.WriteLine("- available: `true`");
        writer.WriteLine($"- role: `{snapshot.SolutionRole}`");
        writer.WriteLine($"- primary_global_span_seconds: `{snapshot.BusinessObjective?.PrimaryGlobalSpanSeconds.ToString("F3", CultureInfo.InvariantCulture) ?? ""}`");
        writer.WriteLine($"- secondary_running_time_supplement_seconds: `{snapshot.BusinessObjective?.SecondaryRunningTimeSupplementSeconds.ToString("F3", CultureInfo.InvariantCulture) ?? ""}`");
        writer.WriteLine($"- global_lower_bound: `{snapshot.BoundReport?.GlobalLowerBound?.ToString("F3", CultureInfo.InvariantCulture) ?? ""}`");
        writer.WriteLine($"- global_upper_bound: `{snapshot.BoundReport?.GlobalUpperBound?.ToString("F3", CultureInfo.InvariantCulture) ?? ""}`");
        writer.WriteLine($"- global_relative_gap: `{snapshot.BoundReport?.GlobalRelativeGap?.ToString("G17", CultureInfo.InvariantCulture) ?? ""}`");
    }

    private static void WriteFeedbackArbitrationDiagnostics(
        IReadOnlyList<CheckerCutSelectionDiagnostic> structural,
        IReadOnlyList<LocalMicroRepairDiagnostic> stationCore,
        string path)
    {
        using StreamWriter writer = new(path, false, new UTF8Encoding(true));
        writer.WriteLine("checker_iteration,feedback_type,evidence_type,proven_infeasible,persistent_round_count,selected_action");
        foreach (CheckerCutSelectionDiagnostic item in structural)
        {
            writer.WriteLine(string.Join(',', item.IterationIndex, "exact_headway", "unavoidable_conflict", true,
                Math.Max(1, (int)Math.Round(item.PersistenceScore / 1000.0)), "structural_separation"));
        }
        foreach (LocalMicroRepairDiagnostic item in stationCore)
        {
            writer.WriteLine(string.Join(',', item.IterationIndex, "station_assignment_core", "proven_infeasible_core", true,
                1, item.Outcome == "accepted" ? "promotion" : "local_micro_exact"));
        }
    }

    private BasicMilpModel BuildModel(
        IReadOnlyList<LocalPrecedenceNogoodCut> activeCuts,
        PrecedenceTrustRegion? precedenceTrustRegion,
        IReadOnlyList<PromotedMicroComponent>? promotedComponentsOverride = null)
    {
        return BuildModel(
            activeCuts,
            precedenceTrustRegion,
            _activeSectionEdges,
            _activeStationEventEdges,
            promotedComponentsOverride);
    }

    private BasicMilpModel BuildModel(
        IReadOnlyList<LocalPrecedenceNogoodCut> activeCuts,
        PrecedenceTrustRegion? precedenceTrustRegion,
        IReadOnlyList<SectionConflictEdgeRequest> sectionEdges,
        IReadOnlyList<StationEventConflictCandidate> stationEdges,
        IReadOnlyList<PromotedMicroComponent>? promotedComponentsOverride = null)
    {
        BasicMilpModelBuilder builder = new(
            _instance,
            _neighborRadius,
            activeCuts,
            precedenceTrustRegion,
            sectionEdges,
            stationEdges,
            _options.RepairRunningTimeSupplementRatio,
            promotedComponentsOverride ?? _promotedMicroComponentStore.ActiveComponents,
            _options.EndpointOccupationSeconds);
        return builder.Build();
    }

    /// <summary>
    /// 复用同一 Macro builder，并单独记录模型构造耗时；该包装不改变模型变量、约束或资源所有权。
    /// </summary>
    private BasicMilpModel BuildValidityFirstModel(
        IReadOnlyList<LocalPrecedenceNogoodCut> activeCuts,
        int iterationIndex,
        string phase,
        PrecedenceTrustRegion? precedenceTrustRegion = null,
        IReadOnlyList<PromotedMicroComponent>? promotedComponentsOverride = null)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        BasicMilpModel model = BuildModel(activeCuts, precedenceTrustRegion, promotedComponentsOverride);
        stopwatch.Stop();
        ValidityFirstPhaseTimingReporter.Append(
            iterationIndex,
            phase,
            "model_construction",
            stopwatch.Elapsed.TotalSeconds,
                    GetValidityFirstCumulativeElapsedSeconds(),
            ResolveValidityFirstOutputDirectory());
        return model;
    }

    private StructuralCandidateProbeExecution ExecuteStructuralCandidateProbe(
        StructuralCandidateProbeRequest request,
        StructuralCandidateProbeFrozenConfiguration frozenConfiguration,
        IReadOnlyList<SectionConflictEdgeRequest> baselineSectionEdges,
        IReadOnlyList<StationEventConflictCandidate> baselineStationEdges,
        IReadOnlyList<LocalPrecedenceNogoodCut> activeCuts,
        int iterationIndex,
        string batchId,
        int neighborhoodLevel,
        bool computeIis = true)
    {
        StructuralCandidateProbeModelPlan plan = StructuralCandidateProbeModelPlanBuilder.Build(
            request,
            frozenConfiguration,
            baselineSectionEdges,
            baselineStationEdges);
        using BasicMilpModel probeModel = BuildModel(
            activeCuts,
            precedenceTrustRegion: null,
            plan.SectionEdges,
            plan.StationEdges);
        ApplyMipStart(probeModel, plan.Snapshot);
        HierarchicalRepairAttemptPreparation preparation =
            HierarchicalRepairAttemptDecisionSpacePreparer.Prepare(
                probeModel,
                plan.Snapshot,
                plan.Neighborhood,
                plan.RepairRunningTimeSupplementRatio,
                plan.WindowSeconds,
                applyTemporalRecourse: true);
        if (preparation.RecourseApplication?.HasEmptyIntersection == true)
        {
            return new StructuralCandidateProbeExecution(
                StructuralCandidateProbeOutcome.Unknown,
                -1,
                "EMPTY_TEMPORAL_INTERSECTION",
                0.0,
                0,
                Array.Empty<StructuralTrialIisDiagnostic>());
        }

        StructuralRepairObjectiveBuilder.Apply(probeModel, plan.Snapshot);
        probeModel.Model.Set(GRB.DoubleParam.TimeLimit, plan.TimeLimitSeconds);
        probeModel.Optimize();
        int statusCode = probeModel.Model.Get(GRB.IntAttr.Status);
        int solutionCount = probeModel.Model.Get(GRB.IntAttr.SolCount);
        StructuralCandidateProbeOutcome outcome =
            StructuralCandidateProbeOutcomeClassifier.Classify(statusCode, solutionCount);
        IReadOnlyList<StructuralTrialIisDiagnostic> iisDiagnostics =
            Array.Empty<StructuralTrialIisDiagnostic>();
        if (outcome == StructuralCandidateProbeOutcome.ProvenInfeasible && computeIis)
        {
            StructuralTrialIisContext context = new(
                iterationIndex,
                batchId,
                neighborhoodLevel,
                plan.Neighborhood.DecisionLevel,
                plan.WindowSeconds,
                request.Mode == StructuralCandidateProbeMode.Individual
                    ? "individual_probe"
                    : "incremental_probe",
                request.ProbeId,
                request.Candidates.Select(candidate => candidate.CandidateKey).ToList());
            iisDiagnostics = StructuralTrialIisExtractor.ComputeAndExtract(
                probeModel,
                context,
                request.Candidates,
                _instance,
                plan.Snapshot);
        }

        return new StructuralCandidateProbeExecution(
            outcome,
            statusCode,
            CheckerSolverStatusNameResolver.Resolve(statusCode),
            probeModel.Model.Get(GRB.DoubleAttr.Runtime),
            solutionCount,
            iisDiagnostics,
            outcome == StructuralCandidateProbeOutcome.ProvenInfeasible && computeIis
                ? IisModelDependencyCatalog.Build(probeModel)
                : null);
    }

    private StructuralSingletonGlobalTrialExecution ExecuteStructuralSingletonGlobalTrial(
        StructuralSingletonGlobalTrialCandidate trialCandidate,
        StructuralCandidateProbeFrozenConfiguration frozenConfiguration,
        IReadOnlyList<SectionConflictEdgeRequest> baselineSectionEdges,
        IReadOnlyList<StationEventConflictCandidate> baselineStationEdges,
        IReadOnlyList<LocalPrecedenceNogoodCut> activeCuts,
        int iterationIndex,
        string batchId,
        MicroFeasibilityReport acceptedReport,
        MasterMicroFeasibilityChecker microChecker)
    {
        HeadwayConflictCandidate candidate = trialCandidate.Candidate;
        StructuralCandidateProbeRequest request = new(
            trialCandidate.ProbeId,
            StructuralCandidateProbeMode.Individual,
            [candidate],
            StructuralCandidateFeasibilityDiagnosticRunner.ProbeTimeLimitSeconds);
        StructuralCandidateProbeModelPlan plan = StructuralCandidateProbeModelPlanBuilder.Build(
            request, frozenConfiguration, baselineSectionEdges, baselineStationEdges);
        BasicMilpModel model = BuildModel(
            activeCuts, precedenceTrustRegion: null, plan.SectionEdges, plan.StationEdges);
        ApplyMipStart(model, plan.Snapshot);
        HierarchicalRepairAttemptPreparation preparation =
            HierarchicalRepairAttemptDecisionSpacePreparer.Prepare(
                model, plan.Snapshot, plan.Neighborhood,
                plan.RepairRunningTimeSupplementRatio, plan.WindowSeconds,
                applyTemporalRecourse: true);
        MicroConflictMetrics beforeMetrics = MicroConflictMetricCalculator.Calculate(acceptedReport.Conflicts);
        MicroConflictCategoryCounts beforeCategories = MicroConflictCategoryCounter.Calculate(acceptedReport.Conflicts);
        string inputFingerprint = SnapshotFingerprint.Calculate(plan.Snapshot);
        if (preparation.RecourseApplication?.HasEmptyIntersection == true)
        {
            StructuralSingletonGlobalTrialDiagnostic emptyDiagnostic = CreateSingletonGlobalDiagnostic(
                trialCandidate, iterationIndex, batchId, beforeMetrics, beforeCategories,
                "EMPTY_TEMPORAL_INTERSECTION", 0.0, 0, false, 0,
                null, null, null, 0.0, 0.0, 0.0, 0.0, 0,
                false, "empty_temporal_intersection", "empty_temporal_intersection",
                inputFingerprint, string.Empty);
            model.Dispose();
            return new StructuralSingletonGlobalTrialExecution(emptyDiagnostic);
        }

        StructuralRepairObjectiveBuilder.Apply(model, plan.Snapshot);
        model.Model.Set(GRB.DoubleParam.TimeLimit, plan.TimeLimitSeconds);
        model.Optimize();
        int statusCode = model.Model.Get(GRB.IntAttr.Status);
        int solutionCount = model.Model.Get(GRB.IntAttr.SolCount);
        string statusName = CheckerSolverStatusNameResolver.Resolve(statusCode);
        double runtime = model.Model.Get(GRB.DoubleAttr.Runtime);
        if (solutionCount == 0)
        {
            StructuralSingletonGlobalTrialDiagnostic noIncumbentDiagnostic = CreateSingletonGlobalDiagnostic(
                trialCandidate, iterationIndex, batchId, beforeMetrics, beforeCategories,
                statusName, runtime, 0, false, 0,
                null, null, null, 0.0, 0.0, 0.0, 0.0, 0,
                false, "no_master_incumbent_on_global_rebuild",
                "no_master_incumbent_on_global_rebuild", inputFingerprint, string.Empty);
            model.Dispose();
            return new StructuralSingletonGlobalTrialExecution(noIncumbentDiagnostic);
        }

        MasterSolutionSnapshot trialSnapshot = MasterSolutionSnapshot.Capture(model);
        string trialFingerprint = SnapshotFingerprint.Calculate(trialSnapshot);
        MasterValidationReport validation = new MasterSolutionValidator().Validate(model);
        if (!StructuralSingletonGlobalTrialExecutionPolicy.ShouldRunMicro(
                solutionCount, validation.Violations.Count))
        {
            StructuralSingletonGlobalTrialDiagnostic invalidDiagnostic = CreateSingletonGlobalDiagnostic(
                trialCandidate, iterationIndex, batchId, beforeMetrics, beforeCategories,
                statusName, runtime, solutionCount, false, validation.Violations.Count,
                null, null, null, 0.0, 0.0, 0.0, 0.0, 0,
                false, "rejected_master_validator_violation",
                "rejected_master_validator_violation", inputFingerprint, trialFingerprint);
            model.Dispose();
            return new StructuralSingletonGlobalTrialExecution(invalidDiagnostic);
        }

        MicroFeasibilityReport trialReport = microChecker.Validate(model);
        MicroConflictMetrics afterMetrics = MicroConflictMetricCalculator.Calculate(trialReport.Conflicts);
        MicroConflictCategoryCounts afterCategories = MicroConflictCategoryCounter.Calculate(trialReport.Conflicts);
        StructuralCandidateConflictSeed seed = StructuralCandidateConflictSeedFactory.Create(candidate, _instance);
        RejectedTrialConflictDelta delta = RejectedTrialConflictDeltaAnalyzer.Analyze(
            iterationIndex, seed, acceptedReport, trialReport);
        StructuralRepairRetimingMetrics retiming = StructuralRepairRetimingMetrics.Calculate(model, plan.Snapshot);
        (double totalSupplement, double maximumSupplement, int supplementSections) =
            CalculateRunningSupplementStatistics(trialSnapshot);
        bool strictImprovement = CheckerStructuralIncumbentAcceptancePolicy.Accept(
            beforeMetrics.RootConflictCount,
            afterMetrics.RootConflictCount,
            requireStrictImprovement: true);
        if (!strictImprovement)
        {
            _rejectedTrialConflictDeltas.Add(delta);
        }
        StructuralSingletonGlobalTrialDiagnostic diagnostic = CreateSingletonGlobalDiagnostic(
            trialCandidate, iterationIndex, batchId, beforeMetrics, beforeCategories,
            statusName, runtime, solutionCount, true, 0,
            afterMetrics, afterCategories, delta.Summary,
            retiming.TotalAbsoluteDeviationSeconds, retiming.MaximumAbsoluteDeviationSeconds,
            totalSupplement, maximumSupplement, supplementSections,
            strictImprovement,
            strictImprovement ? "strict_improvement_eligible" : "rejected_no_strict_micro_improvement",
            strictImprovement ? string.Empty : "rejected_no_strict_micro_improvement",
            inputFingerprint, trialFingerprint);
        return new StructuralSingletonGlobalTrialExecution(diagnostic, model, trialSnapshot, trialReport);
    }

    internal AdaptiveInitialDirectExecutionResult<StructuralSingletonGlobalTrialExecution>
        EvaluateAdaptiveInitialDirectLayer(
            AdaptiveInitialDirectExecutionRequest request,
            StructuralCandidateProbeFrozenConfiguration frozenConfiguration,
            IReadOnlyList<LocalPrecedenceNogoodCut> activeCuts,
            int iterationIndex,
            string batchId,
            MicroFeasibilityReport acceptedReport,
            MasterMicroFeasibilityChecker microChecker)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(frozenConfiguration);
        ArgumentNullException.ThrowIfNull(activeCuts);
        ArgumentNullException.ThrowIfNull(acceptedReport);
        ArgumentNullException.ThrowIfNull(microChecker);
        string frozenFingerprint = SnapshotFingerprint.Calculate(frozenConfiguration.Snapshot);
        if (!string.Equals(
                frozenFingerprint,
                request.AcceptedState.SnapshotFingerprint,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Adaptive Initial Direct must use the frozen accepted Master snapshot.");
        }
        if (!_acceptedStructuralCandidateIds.SetEquals(
                request.AcceptedState.AcceptedCandidateIds))
        {
            throw new InvalidDataException(
                "Adaptive Initial Direct accepted candidate IDs differ from the Solver accepted state.");
        }
        HashSet<string> requestedPromotedComponents = request.AcceptedState.PromotedComponents
            .Where(item => item.Active)
            .Select(item => item.ComponentId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> solverPromotedComponents = _promotedMicroComponentStore.ActiveComponents
            .Select(item => item.ComponentId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!requestedPromotedComponents.SetEquals(solverPromotedComponents))
        {
            throw new InvalidDataException(
                "Adaptive Initial Direct promoted components differ from the Solver accepted state.");
        }

        IReadOnlyList<SectionConflictEdgeRequest> baselineSectionEdges =
            request.AcceptedState.SectionEdges.ToList();
        IReadOnlyList<StationEventConflictCandidate> baselineStationEdges =
            request.AcceptedState.StationEdges.ToList();
        return AdaptiveInitialDirectExecutor.Run(
            request,
            candidateRequest =>
            {
                StructuralSingletonGlobalTrialExecution execution =
                    ExecuteStructuralSingletonGlobalTrial(
                        new StructuralSingletonGlobalTrialCandidate(
                            candidateRequest.CandidateIndex,
                            candidateRequest.ProbeId,
                            candidateRequest.Candidate),
                        frozenConfiguration,
                        baselineSectionEdges,
                        baselineStationEdges,
                        activeCuts,
                        iterationIndex,
                        batchId,
                        acceptedReport,
                        microChecker);
                try
                {
                    _structuralSingletonGlobalTrialDiagnostics.Add(execution.Diagnostic);
                    return new AdaptiveInitialDirectTrial<StructuralSingletonGlobalTrialExecution>(
                        execution.Diagnostic,
                        execution.Snapshot,
                        execution);
                }
                catch
                {
                    execution.Dispose();
                    throw;
                }
            });
    }

    internal AdaptiveInitialIisExecutionResult<StructuralSingletonGlobalTrialExecution>
        EvaluateAdaptiveInitialIisLayer(
            AdaptiveInitialIisExecutionRequest request,
            StructuralCandidateProbeFrozenConfiguration frozenConfiguration,
            IReadOnlyList<LocalPrecedenceNogoodCut> activeCuts,
            int neighborhoodLevel,
            int iterationIndex,
            string batchId,
            MicroFeasibilityReport acceptedReport,
            MasterMicroFeasibilityChecker microChecker)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(frozenConfiguration);
        ArgumentNullException.ThrowIfNull(activeCuts);
        ArgumentNullException.ThrowIfNull(acceptedReport);
        ArgumentNullException.ThrowIfNull(microChecker);
        string frozenFingerprint = SnapshotFingerprint.Calculate(frozenConfiguration.Snapshot);
        if (!string.Equals(
                frozenFingerprint,
                request.AcceptedState.SnapshotFingerprint,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Adaptive Initial IIS must use the frozen accepted Master snapshot.");
        }
        if (!_acceptedStructuralCandidateIds.SetEquals(
                request.AcceptedState.AcceptedCandidateIds))
        {
            throw new InvalidDataException(
                "Adaptive Initial IIS accepted candidate IDs differ from the Solver accepted state.");
        }
        HashSet<string> requestedPromotedComponents = request.AcceptedState.PromotedComponents
            .Where(item => item.Active)
            .Select(item => item.ComponentId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> solverPromotedComponents = _promotedMicroComponentStore.ActiveComponents
            .Select(item => item.ComponentId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!requestedPromotedComponents.SetEquals(solverPromotedComponents))
        {
            throw new InvalidDataException(
                "Adaptive Initial IIS promoted components differ from the Solver accepted state.");
        }
        if (RepairDecisionDependencyOverlayResolver.Resolve(frozenConfiguration.Neighborhood)
            .ReleasesByIdentity.Count > 0)
        {
            throw new InvalidDataException(
                "Adaptive Initial IIS must start from a base neighborhood without a persisted IIS overlay.");
        }

        IReadOnlyList<SectionConflictEdgeRequest> baselineSectionEdges =
            request.AcceptedState.SectionEdges.ToList();
        IReadOnlyList<StationEventConflictCandidate> baselineStationEdges =
            request.AcceptedState.StationEdges.ToList();
        MicroConflictMetrics beforeMetrics =
            MicroConflictMetricCalculator.Calculate(acceptedReport.Conflicts);
        MicroConflictCategoryCounts beforeCategories =
            MicroConflictCategoryCounter.Calculate(acceptedReport.Conflicts);
        return AdaptiveInitialIisExecutor.Run(
            request,
            candidateRequest =>
            {
                AdaptiveInitialIisCandidateExecution<StructuralSingletonGlobalTrialExecution> execution =
                    ExecuteAdaptiveIisCandidate(
                        candidateRequest,
                        request.InitialReference,
                        request.QualityBudgets,
                        request.IisOptions,
                        frozenConfiguration,
                        baselineSectionEdges,
                        baselineStationEdges,
                        activeCuts,
                        neighborhoodLevel,
                        iterationIndex,
                        batchId,
                        acceptedReport,
                        beforeMetrics,
                        beforeCategories,
                        microChecker);
                RecordAdaptiveIisDiagnostics(execution);
                return execution;
            });
    }

    internal AdaptiveExpandedDirectExecutionResult<StructuralSingletonGlobalTrialExecution>
        EvaluateAdaptiveExpandedDirectLayer(
            AdaptiveExpandedDirectExecutionRequest request,
            StructuralCandidateProbeFrozenConfiguration frozenConfiguration,
            IReadOnlyList<LocalPrecedenceNogoodCut> activeCuts,
            int neighborhoodLevel,
            int iterationIndex,
            string batchId,
            MicroFeasibilityReport acceptedReport,
            MasterMicroFeasibilityChecker microChecker)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(frozenConfiguration);
        ArgumentNullException.ThrowIfNull(activeCuts);
        ArgumentNullException.ThrowIfNull(acceptedReport);
        ArgumentNullException.ThrowIfNull(microChecker);
        string frozenFingerprint = SnapshotFingerprint.Calculate(frozenConfiguration.Snapshot);
        if (!string.Equals(
                frozenFingerprint,
                request.AcceptedState.SnapshotFingerprint,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Adaptive Expanded Direct must use the frozen accepted Master snapshot.");
        }
        if (!_acceptedStructuralCandidateIds.SetEquals(
                request.AcceptedState.AcceptedCandidateIds))
        {
            throw new InvalidDataException(
                "Adaptive Expanded Direct accepted candidate IDs differ from the Solver accepted state.");
        }
        HashSet<string> requestedPromotedComponents = request.AcceptedState.PromotedComponents
            .Where(item => item.Active)
            .Select(item => item.ComponentId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> solverPromotedComponents = _promotedMicroComponentStore.ActiveComponents
            .Select(item => item.ComponentId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!requestedPromotedComponents.SetEquals(solverPromotedComponents))
        {
            throw new InvalidDataException(
                "Adaptive Expanded Direct promoted components differ from the Solver accepted state.");
        }
        if (RepairDecisionDependencyOverlayResolver.Resolve(frozenConfiguration.Neighborhood)
            .ReleasesByIdentity.Count > 0)
        {
            throw new InvalidDataException(
                "Adaptive Expanded Direct must start without a persisted IIS overlay.");
        }

        IReadOnlyList<SectionConflictEdgeRequest> baselineSectionEdges =
            request.AcceptedState.SectionEdges.ToList();
        IReadOnlyList<StationEventConflictCandidate> baselineStationEdges =
            request.AcceptedState.StationEdges.ToList();
        return AdaptiveExpandedDirectExecutor.Run(
            request,
            probeRequest => ExecuteStructuralCandidateProbe(
                probeRequest,
                frozenConfiguration,
                baselineSectionEdges,
                baselineStationEdges,
                activeCuts,
                iterationIndex,
                batchId,
                neighborhoodLevel,
                computeIis: false),
            candidateRequest =>
            {
                StructuralSingletonGlobalTrialExecution execution =
                    ExecuteStructuralSingletonGlobalTrial(
                        new StructuralSingletonGlobalTrialCandidate(
                            candidateRequest.CandidateIndex,
                            candidateRequest.ProbeId,
                            candidateRequest.Candidate),
                        frozenConfiguration,
                        baselineSectionEdges,
                        baselineStationEdges,
                        activeCuts,
                        iterationIndex,
                        batchId,
                        acceptedReport,
                        microChecker);
                try
                {
                    _structuralSingletonGlobalTrialDiagnostics.Add(execution.Diagnostic);
                    return new AdaptiveInitialDirectTrial<StructuralSingletonGlobalTrialExecution>(
                        execution.Diagnostic,
                        execution.Snapshot,
                        execution);
                }
                catch
                {
                    execution.Dispose();
                    throw;
                }
            });
    }

    internal AdaptiveExpandedIisExecutionResult<StructuralSingletonGlobalTrialExecution>
        EvaluateAdaptiveExpandedIisLayer(
            AdaptiveExpandedIisExecutionRequest request,
            StructuralCandidateProbeFrozenConfiguration frozenConfiguration,
            IReadOnlyList<LocalPrecedenceNogoodCut> activeCuts,
            int neighborhoodLevel,
            int iterationIndex,
            string batchId,
            MicroFeasibilityReport acceptedReport,
            MasterMicroFeasibilityChecker microChecker)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(frozenConfiguration);
        ArgumentNullException.ThrowIfNull(activeCuts);
        ArgumentNullException.ThrowIfNull(acceptedReport);
        ArgumentNullException.ThrowIfNull(microChecker);
        string frozenFingerprint = SnapshotFingerprint.Calculate(frozenConfiguration.Snapshot);
        if (!string.Equals(
                frozenFingerprint,
                request.AcceptedState.SnapshotFingerprint,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Adaptive Expanded IIS must use the frozen accepted Master snapshot.");
        }
        if (!_acceptedStructuralCandidateIds.SetEquals(
                request.AcceptedState.AcceptedCandidateIds))
        {
            throw new InvalidDataException(
                "Adaptive Expanded IIS accepted candidate IDs differ from the Solver accepted state.");
        }
        HashSet<string> requestedPromotedComponents = request.AcceptedState.PromotedComponents
            .Where(item => item.Active)
            .Select(item => item.ComponentId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> solverPromotedComponents = _promotedMicroComponentStore.ActiveComponents
            .Select(item => item.ComponentId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!requestedPromotedComponents.SetEquals(solverPromotedComponents))
        {
            throw new InvalidDataException(
                "Adaptive Expanded IIS promoted components differ from the Solver accepted state.");
        }
        if (RepairDecisionDependencyOverlayResolver.Resolve(frozenConfiguration.Neighborhood)
            .ReleasesByIdentity.Count > 0)
        {
            throw new InvalidDataException(
                "Adaptive Expanded IIS must start without a persisted IIS overlay.");
        }

        IReadOnlyList<SectionConflictEdgeRequest> baselineSectionEdges =
            request.AcceptedState.SectionEdges.ToList();
        IReadOnlyList<StationEventConflictCandidate> baselineStationEdges =
            request.AcceptedState.StationEdges.ToList();
        MicroConflictMetrics beforeMetrics =
            MicroConflictMetricCalculator.Calculate(acceptedReport.Conflicts);
        MicroConflictCategoryCounts beforeCategories =
            MicroConflictCategoryCounter.Calculate(acceptedReport.Conflicts);
        if (beforeMetrics.RootConflictCount != request.AcceptedRootConflictCount)
        {
            throw new InvalidDataException(
                "Adaptive Expanded IIS accepted Micro baseline differs from its request evidence.");
        }
        return AdaptiveExpandedIisExecutor.Run(
            request,
            candidateRequest =>
            {
                AdaptiveInitialIisCandidateExecution<StructuralSingletonGlobalTrialExecution> execution =
                    ExecuteAdaptiveIisCandidate(
                        candidateRequest,
                        request.InitialReference,
                        request.QualityBudgets,
                        request.IisOptions,
                        frozenConfiguration,
                        baselineSectionEdges,
                        baselineStationEdges,
                        activeCuts,
                        neighborhoodLevel,
                        iterationIndex,
                        batchId,
                        acceptedReport,
                        beforeMetrics,
                        beforeCategories,
                        microChecker);
                RecordAdaptiveIisDiagnostics(execution);
                return execution;
            });
    }

    private void RecordAdaptiveIisDiagnostics(
        AdaptiveInitialIisCandidateExecution<StructuralSingletonGlobalTrialExecution> execution)
    {
        if (execution.Diagnostic is not null)
        {
            _structuralSingletonGlobalTrialDiagnostics.Add(execution.Diagnostic);
        }
        _iisGuidedRepairTraceDiagnostics.AddRange(execution.Trace);
        _iisDependencyPlanDiagnostics.AddRange(execution.PlanDiagnostics);
        _structuralTrialIisDiagnostics.AddRange(execution.IisDiagnostics);
    }

    private AdaptiveInitialIisCandidateExecution<StructuralSingletonGlobalTrialExecution>
        ExecuteAdaptiveIisCandidate(
            AdaptiveInitialIisTrialRequest candidateRequest,
            InitialReferenceDescriptor initialReference,
            AdaptiveQualityBudgetOptions qualityBudgets,
            IisGuidedRepairOptions iisOptions,
            StructuralCandidateProbeFrozenConfiguration frozenConfiguration,
            IReadOnlyList<SectionConflictEdgeRequest> baselineSectionEdges,
            IReadOnlyList<StationEventConflictCandidate> baselineStationEdges,
            IReadOnlyList<LocalPrecedenceNogoodCut> activeCuts,
            int neighborhoodLevel,
            int iterationIndex,
            string batchId,
            MicroFeasibilityReport acceptedReport,
            MicroConflictMetrics beforeMetrics,
            MicroConflictCategoryCounts beforeCategories,
            MasterMicroFeasibilityChecker microChecker)
    {
        ArgumentNullException.ThrowIfNull(initialReference);
        ArgumentNullException.ThrowIfNull(qualityBudgets);
        ArgumentNullException.ThrowIfNull(iisOptions);
        IReadOnlyList<StructuralTrialIisDiagnostic> initialIis =
            candidateRequest.ProbeExecution.IisDiagnostics;
        IisModelDependencyCatalog? catalog = candidateRequest.ProbeExecution.DependencyCatalog;
        IisGuidedDependencyClosureResult closure;
        if (catalog is null || initialIis.Count == 0)
        {
            IisGuidedMasterAttempt seedAttempt = ExecuteIisGuidedMasterAttempt(
                candidateRequest.Candidate,
                0,
                IisDependencyOverlay.Empty,
                frozenConfiguration,
                baselineSectionEdges,
                baselineStationEdges,
                activeCuts,
                iterationIndex,
                batchId,
                neighborhoodLevel);
            if (seedAttempt.Outcome == StructuralCandidateProbeOutcome.Unknown)
            {
                seedAttempt.Model.Dispose();
                return new AdaptiveInitialIisCandidateExecution<StructuralSingletonGlobalTrialExecution>(
                    StructuralCandidateProbeOutcome.Unknown,
                    seedAttempt.SolverStatusName,
                    0,
                    "iis_guided_dependency_seed_unknown",
                    null,
                    null,
                    Array.Empty<IisGuidedRepairTraceDiagnostic>(),
                    Array.Empty<IisDependencyPlanDiagnostic>(),
                    Array.Empty<StructuralTrialIisDiagnostic>(),
                    Array.Empty<string>(),
                    null);
            }
            if (seedAttempt.Outcome == StructuralCandidateProbeOutcome.Feasible)
            {
                closure = new IisGuidedDependencyClosureResult(
                    seedAttempt.Model,
                    IisDependencyOverlay.Empty,
                    Array.Empty<IisGuidedRepairTraceDiagnostic>(),
                    Array.Empty<IisDependencyPlanDiagnostic>(),
                    Array.Empty<StructuralTrialIisDiagnostic>(),
                    "dependency_closure_feasible");
                initialIis = Array.Empty<StructuralTrialIisDiagnostic>();
            }
            else
            {
                try
                {
                    initialIis = seedAttempt.IisDiagnostics;
                    catalog = IisModelDependencyCatalog.Build(seedAttempt.Model);
                }
                finally
                {
                    seedAttempt.Model.Dispose();
                }
                closure = RunIisDependencyClosure(
                    candidateRequest,
                    initialIis,
                    catalog,
                    iisOptions,
                    frozenConfiguration,
                    baselineSectionEdges,
                    baselineStationEdges,
                    activeCuts,
                    iterationIndex,
                    batchId,
                    neighborhoodLevel);
            }
        }
        else
        {
            closure = RunIisDependencyClosure(
                candidateRequest,
                initialIis,
                catalog,
                iisOptions,
                frozenConfiguration,
                baselineSectionEdges,
                baselineStationEdges,
                activeCuts,
                iterationIndex,
                batchId,
                neighborhoodLevel);
        }
        IReadOnlyList<StructuralTrialIisDiagnostic> allIis = initialIis
            .Concat(closure.IisDiagnostics)
            .ToList();
        IReadOnlyList<string> overlayIdentities = closure.FinalOverlay.ReleasesByIdentity.Keys
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        IisGuidedRepairTraceDiagnostic? finalTrace = closure.Trace.LastOrDefault();
        if (!closure.HasMasterIncumbent)
        {
            StructuralCandidateProbeOutcome finalOutcome = string.Equals(
                    closure.TerminationReason,
                    "iis_guided_dependency_expansion_unknown",
                    StringComparison.OrdinalIgnoreCase)
                ? StructuralCandidateProbeOutcome.Unknown
                : StructuralCandidateProbeOutcome.ProvenInfeasible;
            return new AdaptiveInitialIisCandidateExecution<StructuralSingletonGlobalTrialExecution>(
                finalOutcome,
                finalTrace?.SolverStatus ?? "NOT_SOLVED",
                finalTrace?.ExpansionRound,
                closure.TerminationReason,
                null,
                null,
                closure.Trace,
                closure.PlanDiagnostics,
                allIis,
                overlayIdentities,
                null);
        }

        BasicMilpModel? feasibleModel = closure.FeasibleModel;
        StructuralSingletonGlobalTrialExecution? executionLease = null;
        try
        {
            MasterSolutionSnapshot trialSnapshot = MasterSolutionSnapshot.Capture(feasibleModel!);
            string trialFingerprint = SnapshotFingerprint.Calculate(trialSnapshot);
            MasterValidationReport validation = new MasterSolutionValidator().Validate(feasibleModel!);
            StructuralSingletonGlobalTrialCandidate trialCandidate = new(
                candidateRequest.CandidateIndex,
                candidateRequest.ProbeId,
                candidateRequest.Candidate);
            List<IisGuidedRepairTraceDiagnostic> trace = closure.Trace.ToList();
            string masterStatus = finalTrace?.SolverStatus ?? feasibleModel!.StatusName;
            double runtime = finalTrace?.RuntimeSeconds ?? 0.0;
            int solutionCount = Math.Max(1, finalTrace?.SolutionCount ?? 1);
            if (validation.Violations.Count > 0)
            {
                const string validatorTermination =
                    "dependency_closure_feasible_but_master_validator_rejected";
                if (trace.Count > 0)
                {
                    trace[^1] = trace[^1] with
                    {
                        Accepted = false,
                        TerminationReason = validatorTermination
                    };
                }
                StructuralSingletonGlobalTrialDiagnostic invalidDiagnostic =
                    CreateSingletonGlobalDiagnostic(
                        trialCandidate,
                        iterationIndex,
                        batchId,
                        beforeMetrics,
                        beforeCategories,
                        masterStatus,
                        runtime,
                        solutionCount,
                        false,
                        validation.Violations.Count,
                        null,
                        null,
                        null,
                        0.0,
                        0.0,
                        0.0,
                        0.0,
                        0,
                        false,
                        "rejected_master_validator_violation",
                        "rejected_master_validator_violation",
                        candidateRequest.AcceptedState.SnapshotFingerprint,
                        trialFingerprint);
                executionLease = new StructuralSingletonGlobalTrialExecution(
                    invalidDiagnostic,
                    feasibleModel,
                    trialSnapshot);
                feasibleModel = null;
                AdaptiveInitialIisCandidateExecution<StructuralSingletonGlobalTrialExecution> invalidResult = new(
                    StructuralCandidateProbeOutcome.Feasible,
                    masterStatus,
                    finalTrace?.ExpansionRound,
                    validatorTermination,
                    invalidDiagnostic,
                    trialSnapshot,
                    trace,
                    closure.PlanDiagnostics,
                    allIis,
                    overlayIdentities,
                    executionLease);
                executionLease = null;
                return invalidResult;
            }

            MicroFeasibilityReport trialReport = microChecker.Validate(feasibleModel!);
            MicroConflictMetrics afterMetrics =
                MicroConflictMetricCalculator.Calculate(trialReport.Conflicts);
            MicroConflictCategoryCounts afterCategories =
                MicroConflictCategoryCounter.Calculate(trialReport.Conflicts);
            StructuralCandidateConflictSeed seed = StructuralCandidateConflictSeedFactory.Create(
                candidateRequest.Candidate,
                _instance);
            RejectedTrialConflictDelta delta = RejectedTrialConflictDeltaAnalyzer.Analyze(
                iterationIndex,
                seed,
                acceptedReport,
                trialReport);
            StructuralRepairRetimingMetrics retiming =
                StructuralRepairRetimingMetrics.Calculate(
                    feasibleModel!,
                    candidateRequest.AcceptedState.Snapshot);
            (double totalSupplement, double maximumSupplement, int supplementSections) =
                CalculateRunningSupplementStatistics(trialSnapshot);
            bool strictImprovement = CheckerStructuralIncumbentAcceptancePolicy.Accept(
                beforeMetrics.RootConflictCount,
                afterMetrics.RootConflictCount,
                requireStrictImprovement: true);
            AdaptiveSingletonQualityMetrics quality = AdaptiveSingletonQualityCalculator.Calculate(
                candidateRequest.AcceptedState.Snapshot,
                trialSnapshot,
                initialReference.Snapshot);
            bool qualityEligible = AdaptiveQualityBudgetPolicy.Evaluate(
                quality,
                qualityBudgets).Eligible;
            bool accepted = strictImprovement && qualityEligible;
            string termination = !strictImprovement
                ? "dependency_closure_feasible_but_micro_rejected"
                : qualityEligible
                    ? "dependency_closure_accepted"
                    : "dependency_closure_feasible_but_quality_rejected";
            if (trace.Count > 0)
            {
                trace[^1] = trace[^1] with
                {
                    Accepted = accepted,
                    TerminationReason = termination
                };
            }
            StructuralSingletonGlobalTrialDiagnostic diagnostic = CreateSingletonGlobalDiagnostic(
                trialCandidate,
                iterationIndex,
                batchId,
                beforeMetrics,
                beforeCategories,
                masterStatus,
                runtime,
                solutionCount,
                true,
                0,
                afterMetrics,
                afterCategories,
                delta.Summary,
                retiming.TotalAbsoluteDeviationSeconds,
                retiming.MaximumAbsoluteDeviationSeconds,
                totalSupplement,
                maximumSupplement,
                supplementSections,
                strictImprovement,
                strictImprovement ? "strict_improvement_eligible" :
                    "rejected_no_strict_micro_improvement",
                strictImprovement ? string.Empty : "rejected_no_strict_micro_improvement",
                candidateRequest.AcceptedState.SnapshotFingerprint,
                trialFingerprint);
            executionLease = new StructuralSingletonGlobalTrialExecution(
                diagnostic,
                feasibleModel,
                trialSnapshot,
                trialReport);
            feasibleModel = null;
            AdaptiveInitialIisCandidateExecution<StructuralSingletonGlobalTrialExecution> result = new(
                StructuralCandidateProbeOutcome.Feasible,
                masterStatus,
                finalTrace?.ExpansionRound,
                termination,
                diagnostic,
                trialSnapshot,
                trace,
                closure.PlanDiagnostics,
                allIis,
                overlayIdentities,
                executionLease);
            executionLease = null;
            return result;
        }
        catch
        {
            executionLease?.Dispose();
            feasibleModel?.Dispose();
            throw;
        }
    }

    private IisGuidedDependencyClosureResult RunIisDependencyClosure(
        AdaptiveInitialIisTrialRequest candidateRequest,
        IReadOnlyList<StructuralTrialIisDiagnostic> initialIis,
        IisModelDependencyCatalog catalog,
        IisGuidedRepairOptions iisOptions,
        StructuralCandidateProbeFrozenConfiguration frozenConfiguration,
        IReadOnlyList<SectionConflictEdgeRequest> baselineSectionEdges,
        IReadOnlyList<StationEventConflictCandidate> baselineStationEdges,
        IReadOnlyList<LocalPrecedenceNogoodCut> activeCuts,
        int iterationIndex,
        string batchId,
        int neighborhoodLevel) => IisGuidedDependencyClosureRunner.Run(
        new IisGuidedDependencyClosureRequest(
            candidateRequest.Candidate,
            initialIis,
            catalog,
            candidateRequest.AcceptedState.Snapshot,
            frozenConfiguration.Neighborhood,
            frozenConfiguration.LargestWindowSeconds,
            iisOptions),
        (round, overlay) => ExecuteIisGuidedMasterAttempt(
            candidateRequest.Candidate,
            round,
            overlay,
            frozenConfiguration,
            baselineSectionEdges,
            baselineStationEdges,
            activeCuts,
            iterationIndex,
            batchId,
            neighborhoodLevel));

    private static IReadOnlyList<StructuralSingletonGlobalTrialDiagnostic>
        CreateNotEvaluatedSingletonGlobalDiagnostics(
            IReadOnlyList<StructuralCandidateProbeDiagnostic> probeDiagnostics,
            int iterationIndex,
            string batchId,
            MicroFeasibilityReport acceptedReport,
            MasterSolutionSnapshot acceptedSnapshot)
    {
        MicroConflictMetrics beforeMetrics = MicroConflictMetricCalculator.Calculate(acceptedReport.Conflicts);
        MicroConflictCategoryCounts beforeCategories = MicroConflictCategoryCounter.Calculate(acceptedReport.Conflicts);
        string fingerprint = SnapshotFingerprint.Calculate(acceptedSnapshot);
        List<StructuralSingletonGlobalTrialDiagnostic> result = [];
        for (int index = 0; index < probeDiagnostics.Count; index++)
        {
            StructuralCandidateProbeDiagnostic probe = probeDiagnostics[index];
            if (probe.Request.Mode != StructuralCandidateProbeMode.Individual ||
                probe.Execution.Outcome == StructuralCandidateProbeOutcome.Feasible)
            {
                continue;
            }
            HeadwayConflictCandidate candidate = probe.Request.Candidates.Single();
            StructuralSingletonGlobalTrialCandidate trial = new(index, probe.Request.ProbeId, candidate);
            result.Add(CreateSingletonGlobalDiagnostic(
                trial, iterationIndex, batchId, beforeMetrics, beforeCategories,
                probe.Execution.SolverStatusName, probe.Execution.RuntimeSeconds,
                probe.Execution.SolutionCount, false, 0,
                null, null, null, 0.0, 0.0, 0.0, 0.0, 0,
                false, "not_evaluated_non_feasible_probe",
                $"probe_outcome_{probe.Execution.Outcome.ToString().ToLowerInvariant()}",
                fingerprint, string.Empty,
                probe.Execution.Outcome));
        }
        return result;
    }

    private static StructuralSingletonGlobalTrialDiagnostic CreateSingletonGlobalDiagnostic(
        StructuralSingletonGlobalTrialCandidate trial,
        int iterationIndex,
        string batchId,
        MicroConflictMetrics beforeMetrics,
        MicroConflictCategoryCounts beforeCategories,
        string masterStatus,
        double runtime,
        int solutionCount,
        bool validatorFeasible,
        int validatorViolationCount,
        MicroConflictMetrics? afterMetrics,
        MicroConflictCategoryCounts? afterCategories,
        RejectedTrialConflictDeltaSummary? delta,
        double totalRetiming,
        double maximumRetiming,
        double totalSupplement,
        double maximumSupplement,
        int supplementSections,
        bool strictImprovement,
        string outcome,
        string rejectionReason,
        string inputFingerprint,
        string trialFingerprint,
        StructuralCandidateProbeOutcome probeOutcome = StructuralCandidateProbeOutcome.Feasible)
    {
        HeadwayConflictCandidate candidate = trial.Candidate;
        StationEventConflictCandidate? station = candidate.StationEdge;
        return new StructuralSingletonGlobalTrialDiagnostic(
            iterationIndex, batchId, trial.ProbeId, trial.CandidateIndex,
            candidate.CandidateKey, candidate.SectionEdge is null ? "station" : "section",
            candidate.FirstTrainIndex, candidate.SecondTrainIndex,
            station?.StationName ?? string.Empty,
            candidate.SectionEdge?.SectionName ?? string.Empty,
            station?.FirstSide.ToString() ?? string.Empty,
            station?.SecondSide.ToString() ?? string.Empty,
            probeOutcome,
            masterStatus, runtime, solutionCount, validatorFeasible, validatorViolationCount,
            beforeMetrics.RootConflictCount, afterMetrics?.RootConflictCount,
            beforeMetrics.EpisodeConflictCount, afterMetrics?.EpisodeConflictCount,
            beforeCategories.ExactHeadwayConflictCount, afterCategories?.ExactHeadwayConflictCount,
            beforeCategories.StationAssignmentConflictCount, afterCategories?.StationAssignmentConflictCount,
            delta?.ResolvedCount ?? 0, delta?.IntroducedCount ?? 0, delta?.PersistentCount ?? 0,
            delta?.NetChange ?? 0, totalRetiming, maximumRetiming, totalSupplement,
            maximumSupplement, supplementSections, strictImprovement, null, false,
            outcome, rejectionReason, inputFingerprint, trialFingerprint);
    }

    private IisGuidedMasterAttempt ExecuteIisGuidedMasterAttempt(
        HeadwayConflictCandidate candidate,
        int expansionRound,
        IisDependencyOverlay overlay,
        StructuralCandidateProbeFrozenConfiguration frozenConfiguration,
        IReadOnlyList<SectionConflictEdgeRequest> baselineSectionEdges,
        IReadOnlyList<StationEventConflictCandidate> baselineStationEdges,
        IReadOnlyList<LocalPrecedenceNogoodCut> activeCuts,
        int iterationIndex,
        string batchId,
        int neighborhoodLevel)
    {
        StructuralCandidateProbeRequest request = new(
            $"iis-guided-{expansionRound:D2}",
            StructuralCandidateProbeMode.Individual,
            [candidate],
            StructuralCandidateFeasibilityDiagnosticRunner.ProbeTimeLimitSeconds);
        RepairDecisionNeighborhood overlayNeighborhood =
            frozenConfiguration.Neighborhood with { DependencyOverlay = overlay };
        StructuralCandidateProbeModelPlan plan = StructuralCandidateProbeModelPlanBuilder.Build(
            request,
            frozenConfiguration with { Neighborhood = overlayNeighborhood },
            baselineSectionEdges,
            baselineStationEdges);
        BasicMilpModel model = BuildModel(
            activeCuts,
            precedenceTrustRegion: null,
            plan.SectionEdges,
            plan.StationEdges);
        try
        {
            ApplyMipStart(model, plan.Snapshot);
            HierarchicalRepairAttemptPreparation preparation =
                HierarchicalRepairAttemptDecisionSpacePreparer.Prepare(
                    model,
                    plan.Snapshot,
                    plan.Neighborhood,
                    plan.RepairRunningTimeSupplementRatio,
                    plan.WindowSeconds,
                    applyTemporalRecourse: true);
            RepairDecisionReleaseCounts releaseCounts = RepairDecisionReleaseCounter.Count(
                model, plan.Neighborhood);
            if (preparation.RecourseApplication?.HasEmptyIntersection == true)
            {
                return new IisGuidedMasterAttempt(
                    model,
                    StructuralCandidateProbeOutcome.Unknown,
                    -1,
                    "EMPTY_TEMPORAL_INTERSECTION",
                    0.0,
                    0,
                    Array.Empty<StructuralTrialIisDiagnostic>(),
                    releaseCounts);
            }

            StructuralRepairObjectiveBuilder.Apply(model, plan.Snapshot);
            model.Model.Set(GRB.DoubleParam.TimeLimit, plan.TimeLimitSeconds);
            model.Optimize();
            int statusCode = model.Model.Get(GRB.IntAttr.Status);
            int solutionCount = model.Model.Get(GRB.IntAttr.SolCount);
            StructuralCandidateProbeOutcome outcome =
                StructuralCandidateProbeOutcomeClassifier.Classify(statusCode, solutionCount);
            IReadOnlyList<StructuralTrialIisDiagnostic> iis = Array.Empty<StructuralTrialIisDiagnostic>();
            if (outcome == StructuralCandidateProbeOutcome.ProvenInfeasible)
            {
                iis = StructuralTrialIisExtractor.ComputeAndExtract(
                    model,
                    new StructuralTrialIisContext(
                        iterationIndex,
                        batchId,
                        neighborhoodLevel,
                        plan.Neighborhood.DecisionLevel,
                        plan.WindowSeconds,
                        "iis_guided_probe",
                        request.ProbeId,
                        [candidate.CandidateKey]),
                    [candidate],
                    _instance,
                    plan.Snapshot);
            }
            return new IisGuidedMasterAttempt(
                model,
                outcome,
                statusCode,
                CheckerSolverStatusNameResolver.Resolve(statusCode),
                model.Model.Get(GRB.DoubleAttr.Runtime),
                solutionCount,
                iis,
                releaseCounts);
        }
        catch
        {
            model.Dispose();
            throw;
        }
    }

    private void SynchronizeStructuralDiagnostics(BasicMilpModel model)
    {
        model.StructuralTrialIisDiagnostics.Clear();
        foreach (StructuralTrialIisDiagnostic item in _structuralTrialIisDiagnostics)
        {
            model.StructuralTrialIisDiagnostics.Add(item);
        }
        model.StructuralCandidateProbeDiagnostics.Clear();
        foreach (StructuralCandidateProbeDiagnostic item in _structuralCandidateProbeDiagnostics)
        {
            model.StructuralCandidateProbeDiagnostics.Add(item);
        }
        model.StructuralCandidateProbeStopReason = _structuralCandidateProbeStopReason;
        model.IisGuidedRepairTraceDiagnostics.Clear();
        foreach (IisGuidedRepairTraceDiagnostic item in _iisGuidedRepairTraceDiagnostics)
        {
            model.IisGuidedRepairTraceDiagnostics.Add(item);
        }
        model.IisDependencyPlanDiagnostics.Clear();
        foreach (IisDependencyPlanDiagnostic item in _iisDependencyPlanDiagnostics)
        {
            model.IisDependencyPlanDiagnostics.Add(item);
        }
        model.RejectedTrialConflictDeltas.Clear();
        foreach (RejectedTrialConflictDelta item in _rejectedTrialConflictDeltas)
        {
            model.RejectedTrialConflictDeltas.Add(item);
        }
        model.StructuralSingletonGlobalTrialDiagnostics.Clear();
        foreach (StructuralSingletonGlobalTrialDiagnostic item in _structuralSingletonGlobalTrialDiagnostics)
        {
            model.StructuralSingletonGlobalTrialDiagnostics.Add(item);
        }
    }

    private void RecordRejectedTrialConflictDelta(
        int iterationIndex,
        IReadOnlyList<HeadwayConflictCandidate> candidates,
        MicroFeasibilityReport acceptedReport,
        MicroFeasibilityReport trialReport,
        BasicMilpModel trialModel,
        BasicMilpModel diagnosticsCarrier)
    {
        MasterValidationReport validation = new MasterSolutionValidator().Validate(trialModel);
        MicroConflictMetrics beforeMetrics = MicroConflictMetricCalculator.Calculate(acceptedReport.Conflicts);
        MicroConflictMetrics afterMetrics = MicroConflictMetricCalculator.Calculate(trialReport.Conflicts);
        if (!RejectedTrialConflictDeltaTrigger.ShouldAnalyze(
                trialModel.HasIncumbentSolution,
                validation.Violations.Count,
                globalMicroCheckCompleted: true,
                acceptedByExistingRule: false,
                beforeMetrics.RootConflictCount,
                afterMetrics.RootConflictCount))
        {
            return;
        }

        foreach (HeadwayConflictCandidate candidate in candidates)
        {
            StructuralCandidateConflictSeed seed =
                StructuralCandidateConflictSeedFactory.Create(candidate, _instance);
            _rejectedTrialConflictDeltas.Add(RejectedTrialConflictDeltaAnalyzer.Analyze(
                iterationIndex,
                seed,
                acceptedReport,
                trialReport));
        }
        SynchronizeStructuralDiagnostics(diagnosticsCarrier);
    }

    private int AddNewHeadwayEdges(HeadwayConflictEdgeExpansion expansion)
    {
        HashSet<string> sectionKeys = _activeSectionEdges
            .Select(edge => InitialConflictGraphBuilder.BuildCanonicalPairKey(edge.SectionName, edge.FirstTrainIndex, edge.SecondTrainIndex))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> stationKeys = _activeStationEventEdges
            .Select(BasicMilpModelBuilder.BuildStationEventPrecedenceKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        int added = 0;

        foreach (SectionConflictEdgeRequest edge in expansion.SectionEdges)
        {
            string key = InitialConflictGraphBuilder.BuildCanonicalPairKey(edge.SectionName, edge.FirstTrainIndex, edge.SecondTrainIndex);
            if (sectionKeys.Add(key))
            {
                _activeSectionEdges.Add(edge);
                added++;
            }
        }

        foreach (StationEventConflictCandidate edge in expansion.StationEdges)
        {
            string key = BasicMilpModelBuilder.BuildStationEventPrecedenceKey(edge);
            if (stationKeys.Add(key))
            {
                _activeStationEventEdges.Add(edge);
                added++;
            }
        }

        return added;
    }

    private LocalMicroRepairEvaluation EvaluateLocalMicroRepair(
        BasicMilpModel currentModel,
        MasterSolutionSnapshot currentSnapshot,
        MicroFeasibilityReport currentReport,
        IReadOnlyList<LocalPrecedenceNogoodCut> activeCuts,
        int iterationIndex,
        MasterMicroFeasibilityChecker microChecker,
        StationLevelMicroFeasibilitySlave microSlave,
        IReadOnlyDictionary<string, int> stationHotspotPersistence)
    {
        if (_options.LocalMicroRepairStationLimit <= 0)
        {
            return new LocalMicroRepairEvaluation(false, false, null, null, null, Array.Empty<LocalMicroRepairDiagnostic>());
        }

        StationLevelMicroFeasibilitySlaveResult baselineSlave = microSlave.Solve(currentModel);
        LocalMicroRepairTarget? target = LocalMicroRepairStationSelector.Select(
            baselineSlave,
            stationHotspotPersistence,
            _options.MicroPromotionPersistenceRounds);
        if (target is null)
        {
            return new LocalMicroRepairEvaluation(false, false, null, null, null, Array.Empty<LocalMicroRepairDiagnostic>());
        }

        IReadOnlySet<int> releasedTrainIndices = StructuralRepairNeighborhoodResolver.ResolveTrainIndices(
            _instance,
            new HeadwayConflictEdgeExpansion(
                Array.Empty<SectionConflictEdgeRequest>(),
                Array.Empty<StationEventConflictCandidate>()),
            _options.StructuralRepairNeighborPadding,
            target.CoreTrainIndices);
        IReadOnlySet<string> targetStationNames = new HashSet<string>(
            new[] { target.StationName },
            StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<StationMicroRequirement> focusedRequirements =
            StationLevelMicroFeasibilitySlave.BuildRequirementsForRepair(currentModel, targetStationNames);
        List<LocalMicroRepairDiagnostic> attemptDiagnostics = [];

        foreach (int windowSeconds in _options.RecourseWindowSeconds)
        {
            BasicMilpModel trialModel = BuildModel(activeCuts, precedenceTrustRegion: null);
            bool ownershipTransferred = false;
            try
            {
                ApplyMipStart(trialModel, currentSnapshot);
                StructuralRepairNeighborhoodApplier.FixOutside(trialModel, currentSnapshot, releasedTrainIndices);
                LocalMicroRepairStopDecisionApplier.FixAll(trialModel, currentSnapshot);
                TemporalRecourseApplicationResult recourse = TemporalRecourseNeighborhoodApplier.Apply(
                    trialModel,
                    currentSnapshot,
                    releasedTrainIndices,
                    windowSeconds);
                if (recourse.HasEmptyIntersection)
                {
                    attemptDiagnostics.Add(new LocalMicroRepairDiagnostic(
                        iterationIndex,
                        target.StationName,
                        windowSeconds,
                        $"EMPTY_TEMPORAL_INTERSECTION:{recourse.EmptyIntersectionEventKey}",
                        "empty_temporal_intersection",
                        releasedTrainIndices.Count,
                        0,
                        0,
                        currentReport.Conflicts.Count,
                        null,
                        target.BaselineFocusedConflictCount,
                        null,
                        0.0,
                        0.0,
                        Array.Empty<LocalMicroRepairSelectedAlternative>()));
                    continue;
                }

                StationMicroRepairAugmentation augmentation = StationMicroRepairModelAugmenter.Apply(
                    trialModel,
                    focusedRequirements,
                    _options.EndpointOccupationSeconds);
                StructuralRepairObjectiveBuilder.Apply(trialModel, currentSnapshot);
                trialModel.Model.Set(
                    GRB.DoubleParam.TimeLimit,
                    _options.LocalMicroRepairTimeLimitSeconds ?? _options.SecondaryTimeLimitSecondsPerSolve);
                trialModel.Optimize();
                if (!trialModel.HasIncumbentSolution)
                {
                    attemptDiagnostics.Add(new LocalMicroRepairDiagnostic(
                        iterationIndex,
                        target.StationName,
                        windowSeconds,
                        trialModel.StatusName,
                        trialModel.Model.Status == GRB.Status.INFEASIBLE ? "proven_infeasible" : "no_incumbent_or_unknown",
                        releasedTrainIndices.Count,
                        augmentation.AlternativeCount,
                        augmentation.ConditionalConstraintCount,
                        currentReport.Conflicts.Count,
                        null,
                        target.BaselineFocusedConflictCount,
                        null,
                        0.0,
                        0.0,
                        Array.Empty<LocalMicroRepairSelectedAlternative>()));
                    continue;
                }

                MasterSolutionSnapshot trialSnapshot = MasterSolutionSnapshot.Capture(trialModel);
                StationLevelMicroFeasibilitySlaveResult focusedSlave = microSlave.Solve(
                    trialModel,
                    new MicroFeasibilitySlaveRequest(targetStationNames));
                MicroFeasibilityReport trialReport = microChecker.Validate(trialModel);
                StructuralRepairRetimingMetrics retiming = StructuralRepairRetimingMetrics.Calculate(
                    trialModel,
                    currentSnapshot);
                IReadOnlyList<LocalMicroRepairSelectedAlternative> selectedAlternatives =
                    augmentation.CaptureSelectedAlternatives();
                bool accepted = focusedSlave.Conflicts.Count < target.BaselineFocusedConflictCount &&
                    trialReport.Conflicts.Count <= currentReport.Conflicts.Count;
                string outcome = accepted
                    ? "accepted"
                    : focusedSlave.Conflicts.Count >= target.BaselineFocusedConflictCount
                        ? "rejected_no_focused_improvement"
                        : "rejected_global_regression";
                attemptDiagnostics.Add(new LocalMicroRepairDiagnostic(
                    iterationIndex,
                    target.StationName,
                    windowSeconds,
                    trialModel.StatusName,
                    outcome,
                    releasedTrainIndices.Count,
                    augmentation.AlternativeCount,
                    augmentation.ConditionalConstraintCount,
                    currentReport.Conflicts.Count,
                    trialReport.Conflicts.Count,
                    target.BaselineFocusedConflictCount,
                    focusedSlave.Conflicts.Count,
                    retiming.TotalAbsoluteDeviationSeconds,
                    retiming.MaximumAbsoluteDeviationSeconds,
                    selectedAlternatives));
                if (accepted)
                {
                    bool permanentPromotionAllowed = PermanentMicroFeedbackEligibilityPolicy.Allows(
                        target.BaselineStatus,
                        target.ProvenStationCore);
                    PromotedMicroComponent? promoted = permanentPromotionAllowed &&
                        MicroPromotionEligibilityPolicy.ShouldPromote(
                        target.ProvenStationCore,
                        persistentHotspot: target.PersistentRounds >= _options.MicroPromotionPersistenceRounds,
                        persistentRounds: target.PersistentRounds,
                        threshold: _options.MicroPromotionPersistenceRounds,
                        enabled: _options.EnableMicroPromotion)
                        ? augmentation.BuildPromotedComponent(
                            target.StationName,
                            target.BaselineStatus.ToString(),
                            iterationIndex,
                            Math.Max(1, target.PersistentRounds))
                        : null;
                    ownershipTransferred = true;
                    return new LocalMicroRepairEvaluation(
                        true,
                        true,
                        trialModel,
                        trialSnapshot,
                        trialReport,
                        attemptDiagnostics,
                        promoted);
                }

            }
            catch (InvalidOperationException exception)
            {
                attemptDiagnostics.Add(new LocalMicroRepairDiagnostic(
                    iterationIndex,
                    target.StationName,
                    windowSeconds,
                    "FORMULATION_REJECTED",
                    $"formulation_rejected:{exception.Message}",
                    releasedTrainIndices.Count,
                    0,
                    0,
                    currentReport.Conflicts.Count,
                    null,
                    target.BaselineFocusedConflictCount,
                    null,
                    0.0,
                    0.0,
                    Array.Empty<LocalMicroRepairSelectedAlternative>()));
                break;
            }
            finally
            {
                if (!ownershipTransferred)
                {
                    trialModel.Dispose();
                }
            }
        }

        return new LocalMicroRepairEvaluation(true, false, null, null, null, attemptDiagnostics);
    }

    private StationCoreFallbackEvaluation EvaluateStationCoreFallback(
        BasicMilpModel currentModel,
        MasterSolutionSnapshot currentSnapshot,
        MicroFeasibilityReport currentReport,
        IReadOnlyList<LocalPrecedenceNogoodCut> activeCuts,
        ISet<string> attemptedProjectionIds,
        int iterationIndex,
        MasterMicroFeasibilityChecker microChecker,
        StationLevelMicroFeasibilitySlave microSlave,
        MicroFeedbackGenerator feedbackGenerator,
        IList<TrialCutDiagnostic> diagnostics)
    {
        StationLevelMicroFeasibilitySlaveResult fullSlaveResult = microSlave.Solve(currentModel);
        HashSet<string> generationIds = new(StringComparer.OrdinalIgnoreCase);
        List<MicroFeedback> projectedFeedback = feedbackGenerator.Generate(currentModel, fullSlaveResult, generationIds)
            .Where(feedback => feedback.Kind == MicroFeedbackKind.ProjectedStationCore && feedback.Projection is not null)
            .OrderBy(feedback => feedback.StationName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(feedback => feedback.FeedbackId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        List<MicroFeedback> supportedFeedback = projectedFeedback
            .Where(feedback => feedback.Projection!.HasCut)
            .ToList();
        List<MicroFeedback> unsupportedFeedback = projectedFeedback
            .Where(feedback => !feedback.Projection!.HasCut)
            .ToList();
        List<MicroFeedback> unseenSupportedFeedback = supportedFeedback
            .Where(feedback => !attemptedProjectionIds.Contains(feedback.FeedbackId))
            .ToList();
        MicroConflictMetrics workingMetrics = MicroConflictMetricCalculator.Calculate(currentReport.Conflicts);

        foreach (MicroFeedback feedback in unsupportedFeedback)
        {
            StationCoreProjection projection = feedback.Projection!;
            StationInfeasibleCore core = ResolveInfeasibleCore(fullSlaveResult, projection);
            int focusedConflictCount = fullSlaveResult.StationResults
                .Where(result => string.Equals(result.StationName, projection.StationName, StringComparison.OrdinalIgnoreCase))
                .Sum(result => result.Conflicts.Count);
            diagnostics.Add(StationCoreAuditDiagnosticFactory.BuildUnsupported(
                iterationIndex,
                projection,
                core,
                _options.EndpointOccupationSeconds,
                currentReport.Conflicts.Count,
                focusedConflictCount,
                workingMetrics.CanonicalConflictCount,
                workingMetrics.RootConflictCount,
                workingMetrics.ConflictedStationCount,
                currentSnapshot.ObjectiveValue,
                _activeSectionEdges.Count,
                _activeStationEventEdges.Count));
        }

        bool allTrialsExhausted = supportedFeedback.Count > 0 && unseenSupportedFeedback.Count == 0;
        if (unseenSupportedFeedback.Count == 0 && unsupportedFeedback.Count == 0 && !allTrialsExhausted)
        {
            return StationCoreFallbackEvaluation.NotHandled;
        }

        CheckerStationCoreAction action = CheckerStationCoreActionResolver.Resolve(
            microFeasible: false,
            unseenExactCandidateCount: 0,
            projectedCoreCandidateCount: unseenSupportedFeedback.Count,
            unsupportedCoreCount: unsupportedFeedback.Count,
            allTrialsExhausted);
        if (action == CheckerStationCoreAction.MasterDecisionSpaceInsufficient)
        {
            return StationCoreFallbackEvaluation.Terminal(
                "master_decision_space_insufficient",
                supportedFeedback.Count,
                trialCount: 0,
                solverStatus: currentModel.StatusName);
        }

        if (action == CheckerStationCoreAction.StationCoreTrialsExhausted)
        {
            return StationCoreFallbackEvaluation.Terminal(
                "station_core_trials_exhausted",
                supportedFeedback.Count,
                trialCount: 0,
                solverStatus: currentModel.StatusName);
        }

        if (action != CheckerStationCoreAction.TrialProjectedCore)
        {
            throw new InvalidOperationException($"Unexpected station-core fallback action '{action}'.");
        }

        int trialCount = 0;
        int noIncumbentTrialCount = 0;
        int totalRecourseAttemptCount = 0;
        int lastFixedEventCount = 0;
        int lastBoundedEventCount = 0;
        string lastSolverStatus = currentModel.StatusName;
        List<StationCorePrecedenceCandidateInput> currentPrecedenceInputs = currentModel.SectionConflictCandidates
            .Select(candidate => new StationCorePrecedenceCandidateInput(
                candidate.FirstTrainIndex,
                candidate.SecondTrainIndex,
                currentModel.SectionPrecedenceVars.ContainsKey(
                    BasicMilpModelBuilder.BuildSectionPrecedenceKey(candidate))))
            .Concat(currentModel.StationEventConflictCandidates.Select(candidate =>
                new StationCorePrecedenceCandidateInput(
                    candidate.FirstTrainIndex,
                    candidate.SecondTrainIndex,
                    currentModel.StationEventPrecedenceVars.ContainsKey(
                        BasicMilpModelBuilder.BuildStationEventPrecedenceKey(candidate)))))
            .ToList();
        IReadOnlyList<(int FirstTrainIndex, int SecondTrainIndex)> currentPrecedencePairs =
            StationCoreCurrentPrecedencePairResolver.Resolve(currentPrecedenceInputs);

        foreach (MicroFeedback feedback in unseenSupportedFeedback)
        {
            StationCoreProjection projection = feedback.Projection!;
            StationInfeasibleCore core = ResolveInfeasibleCore(fullSlaveResult, projection);
            if (!string.Equals(projection.SourceFamily, "heuristic_projected_benders", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Projected station-core feedback '{feedback.FeedbackId}' has invalid source family '{projection.SourceFamily}'.");
            }

            attemptedProjectionIds.Add(feedback.FeedbackId);
            trialCount++;
            LocalPrecedenceNogoodCut candidateCut = MicroFeedbackGenerator.ToMasterCut(feedback);
            int baselineFocusedConflictCount = fullSlaveResult.StationResults
                .Where(result => string.Equals(result.StationName, projection.StationName, StringComparison.OrdinalIgnoreCase))
                .Sum(result => result.Conflicts.Count);
            StationCoreTrialOrchestrationResult<BasicMilpModel, MasterSolutionSnapshot, StationCoreLiveEvaluatedState>
                orchestration = StationCoreTrialOrchestrator.Execute(
                    projection.CoreTrainIndices,
                    currentPrecedencePairs,
                    activeCuts,
                    candidateCut,
                    _options.RecourseWindowSeconds,
                    currentSnapshot,
                    cuts => BuildModel(cuts, precedenceTrustRegion: null),
                    ApplyMipStart,
                    (model, windowSeconds, plan) =>
                    {
                        IReadOnlySet<int> releasedTrainIndices = plan.ReleasedTrainIndices.ToHashSet();
                        StructuralRepairNeighborhoodApplier.FixOutside(
                            model,
                            currentSnapshot,
                            releasedTrainIndices);
                        TemporalRecourseApplicationResult application = TemporalRecourseNeighborhoodApplier.Apply(
                            model,
                            currentSnapshot,
                            releasedTrainIndices,
                            windowSeconds);
                        if (application.HasEmptyIntersection)
                        {
                            return new StationCoreRecoursePreparation(
                                false,
                                $"EMPTY_TEMPORAL_INTERSECTION:{application.EmptyIntersectionEventKey}",
                                application.FixedEventCount,
                                application.BoundedEventCount);
                        }

                        StructuralRepairObjectiveBuilder.Apply(model, currentSnapshot);
                        ApplyStationCoreTimeLimit(model);
                        return new StationCoreRecoursePreparation(
                            true,
                            "READY",
                            application.FixedEventCount,
                            application.BoundedEventCount);
                    },
                    (model, _) => EvaluateStationCoreLiveStage(
                        model,
                        projection.StationName,
                        baselineFocusedConflictCount,
                        currentReport,
                        microChecker,
                        microSlave),
                    (model, snapshot) =>
                    {
                        StructuralRepairObjectiveBuilder.Apply(model, snapshot);
                        ApplyStationCoreTimeLimit(model);
                    },
                    (model, _) => EvaluateStationCoreLiveStage(
                        model,
                        projection.StationName,
                        baselineFocusedConflictCount,
                        currentReport,
                        microChecker,
                        microSlave),
                    state => state.Snapshot);

            totalRecourseAttemptCount += orchestration.Attempts.Count;
            StationCoreTrialAttempt<StationCoreLiveEvaluatedState> lastAttempt =
                orchestration.Attempts.Last();
            lastFixedEventCount = lastAttempt.RecoursePreparation.FixedEventCount;
            lastBoundedEventCount = lastAttempt.RecoursePreparation.BoundedEventCount;
            lastSolverStatus = lastAttempt.CarrierEvaluation?.SolverStatus ??
                lastAttempt.BoundedEvaluation?.SolverStatus ??
                lastAttempt.RecoursePreparation.SolverStatus;

            if (orchestration.Accepted)
            {
                BasicMilpModel acceptedModel = orchestration.Model ?? throw new InvalidOperationException(
                    "Accepted station-core orchestration returned no carrier model.");
                using StationCoreTrialModelLease<BasicMilpModel> acceptedLease = new(acceptedModel);
                StationCoreLiveEvaluatedState acceptedState = orchestration.AcceptedState ?? throw new InvalidOperationException(
                    "Accepted station-core orchestration returned no evaluated carrier state.");
                StationCoreTrialStageEvaluation<StationCoreLiveEvaluatedState> acceptedStage =
                    lastAttempt.CarrierEvaluation ?? throw new InvalidOperationException(
                        "Accepted station-core orchestration returned no carrier evaluation.");
                diagnostics.Add(BuildStationCoreDiagnostic(
                    iterationIndex,
                    trialCount,
                    candidateCut,
                    currentReport,
                    baselineFocusedConflictCount,
                    acceptedState.Report.Conflicts.Count,
                    acceptedState.FocusedConflictCount,
                    acceptedStage.SolverStatus,
                    accepted: true,
                    acceptedStage.Decision,
                    workingMetrics,
                    acceptedState.Metrics,
                    currentSnapshot.ObjectiveValue,
                    acceptedState.Snapshot.ObjectiveValue,
                    projection,
                    core,
                    orchestration.AcceptedWindowSeconds,
                    orchestration.Attempts.Count,
                    lastFixedEventCount,
                    lastBoundedEventCount));
                StructuralRepairRetimingMetrics retimingMetrics =
                    StructuralRepairRetimingMetrics.Calculate(acceptedModel, currentSnapshot);
                StationCoreFallbackEvaluation acceptedEvaluation = StationCoreFallbackEvaluation.Accept(
                    acceptedModel,
                    acceptedState.Snapshot,
                    acceptedState.Report,
                    candidateCut,
                    supportedFeedback.Count,
                    trialCount,
                    totalRecourseAttemptCount,
                    orchestration.AcceptedWindowSeconds,
                    lastFixedEventCount,
                    lastBoundedEventCount,
                    retimingMetrics);
                _ = acceptedLease.Transfer();
                return acceptedEvaluation;
            }

            if (orchestration.RecourseExhausted)
            {
                noIncumbentTrialCount++;
                diagnostics.Add(new TrialCutDiagnostic(
                    iterationIndex,
                    trialCount,
                    candidateCut.CutId,
                    candidateCut.StationName,
                    candidateCut.SourceFamily,
                    currentReport.Conflicts.Count,
                    baselineFocusedConflictCount,
                    currentReport.Conflicts.Count,
                    baselineFocusedConflictCount,
                    null,
                    null,
                    false,
                    lastSolverStatus,
                    false,
                    "station_core_recourse_exhausted",
                    workingMetrics.CanonicalConflictCount,
                    null,
                    workingMetrics.RootConflictCount,
                    null,
                    workingMetrics.ConflictedStationCount,
                    null,
                    currentSnapshot.ObjectiveValue,
                    null,
                    _activeSectionEdges.Count,
                    _activeStationEventEdges.Count,
                    StationCoreCutDiagnosticMapper.Map(
                        projection,
                        core,
                        _options.EndpointOccupationSeconds,
                        attempted: true,
                        accepted: false,
                        retained: false,
                        trialOutcome: "station_core_recourse_exhausted",
                        acceptedRecourseWindowSeconds: 0,
                        recourseAttemptCount: orchestration.Attempts.Count,
                        fixedTemporalEventCount: lastFixedEventCount,
                        boundedTemporalEventCount: lastBoundedEventCount,
                        certificateReason: string.Empty)));
                continue;
            }

            StationCoreTrialAttempt<StationCoreLiveEvaluatedState> rejectedAttempt = orchestration.Attempts
                .Last(attempt => attempt.Outcome is StationCoreTrialAttemptOutcome.BoundedRejected or
                    StationCoreTrialAttemptOutcome.CarrierRejected);
            StationCoreTrialStageEvaluation<StationCoreLiveEvaluatedState> rejectedStage =
                rejectedAttempt.Outcome == StationCoreTrialAttemptOutcome.CarrierRejected
                    ? rejectedAttempt.CarrierEvaluation!
                    : rejectedAttempt.BoundedEvaluation!;
            StationCoreLiveEvaluatedState rejectedState = rejectedStage.State ?? throw new InvalidOperationException(
                "A rejected incumbent must preserve its evaluated diagnostic state.");
            diagnostics.Add(BuildStationCoreDiagnostic(
                iterationIndex,
                trialCount,
                candidateCut,
                currentReport,
                baselineFocusedConflictCount,
                rejectedState.Report.Conflicts.Count,
                rejectedState.FocusedConflictCount,
                rejectedStage.SolverStatus,
                accepted: false,
                rejectedStage.Decision,
                workingMetrics,
                rejectedState.Metrics,
                currentSnapshot.ObjectiveValue,
                rejectedState.Snapshot.ObjectiveValue,
                projection,
                core,
                acceptedRecourseWindowSeconds: 0,
                orchestration.Attempts.Count,
                lastFixedEventCount,
                lastBoundedEventCount));
        }

        string terminationReason = CheckerStationCoreTerminalReasonResolver.Resolve(
            trialCount,
            acceptedTrialCount: 0,
            noIncumbentTrialCount,
            unsupportedFeedback.Count);
        for (int index = 0; index < diagnostics.Count; index++)
        {
            TrialCutDiagnostic diagnostic = diagnostics[index];
            if (diagnostic.IterationIndex == iterationIndex &&
                diagnostic.StationCore is { Attempted: true, Accepted: false } stationCore)
            {
                diagnostics[index] = diagnostic with
                {
                    StationCore = stationCore with { CertificateReason = terminationReason }
                };
            }
        }

        return StationCoreFallbackEvaluation.Terminal(
            terminationReason,
            supportedFeedback.Count,
            trialCount,
            lastSolverStatus,
            totalRecourseAttemptCount,
            lastFixedEventCount,
            lastBoundedEventCount);
    }

    private void ApplyStationCoreTimeLimit(BasicMilpModel model)
    {
        model.Model.Set(
            GRB.DoubleParam.TimeLimit,
            CheckerStructuralSolveTimeLimitResolver.Resolve(
                zeroConflictMode: true,
                _options.TimeLimitSecondsPerSolve,
                _options.SecondaryTimeLimitSecondsPerSolve));
    }

    private StationCoreTrialStageEvaluation<StationCoreLiveEvaluatedState> EvaluateStationCoreLiveStage(
        BasicMilpModel model,
        string stationName,
        int baselineFocusedConflictCount,
        MicroFeasibilityReport currentReport,
        MasterMicroFeasibilityChecker microChecker,
        StationLevelMicroFeasibilitySlave microSlave)
    {
        model.Optimize();
        if (!model.HasIncumbentSolution)
        {
            return new StationCoreTrialStageEvaluation<StationCoreLiveEvaluatedState>(
                false,
                false,
                null,
                model.StatusName,
                ResolveNoIncumbentReason(model));
        }

        MasterSolutionSnapshot snapshot = MasterSolutionSnapshot.Capture(model);
        StationLevelMicroFeasibilitySlaveResult focusedSlaveResult = microSlave.Solve(
            model,
            new MicroFeasibilitySlaveRequest(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { stationName }));
        int focusedConflictCount = focusedSlaveResult.Conflicts.Count;
        MicroFeasibilityReport report = microChecker.Validate(model);
        bool focusedImproved = _options.RequireStrictConflictImprovement
            ? focusedConflictCount < baselineFocusedConflictCount
            : focusedConflictCount <= baselineFocusedConflictCount;
        bool globalGuardSatisfied = report.IsFeasible ||
            report.Conflicts.Count <= currentReport.Conflicts.Count;
        bool accepted = focusedImproved && globalGuardSatisfied;
        string decision = report.IsFeasible && focusedImproved
            ? "accepted_micro_feasible"
            : accepted
                ? "accepted_focused_conflict_reduction"
                : !focusedImproved
                    ? "rejected_no_focused_conflict_improvement"
                    : "rejected_global_conflict_regression";
        StationCoreLiveEvaluatedState state = new(
            snapshot,
            report,
            focusedConflictCount,
            MicroConflictMetricCalculator.Calculate(report.Conflicts));
        return new StationCoreTrialStageEvaluation<StationCoreLiveEvaluatedState>(
            true,
            accepted,
            state,
            model.StatusName,
            decision);
    }

    private TrialCutDiagnostic BuildStationCoreDiagnostic(
        int iterationIndex,
        int trialIndex,
        LocalPrecedenceNogoodCut candidateCut,
        MicroFeasibilityReport currentReport,
        int baselineFocusedConflictCount,
        int trialGlobalConflictCount,
        int trialFocusedConflictCount,
        string solverStatus,
        bool accepted,
        string acceptanceReason,
        MicroConflictMetrics workingMetrics,
        MicroConflictMetrics trialMetrics,
        double currentObjective,
        double trialObjective,
        StationCoreProjection projection,
        StationInfeasibleCore core,
        int acceptedRecourseWindowSeconds,
        int recourseAttemptCount,
        int fixedTemporalEventCount,
        int boundedTemporalEventCount) =>
        new(
            iterationIndex,
            trialIndex,
            candidateCut.CutId,
            candidateCut.StationName,
            candidateCut.SourceFamily,
            currentReport.Conflicts.Count,
            baselineFocusedConflictCount,
            currentReport.Conflicts.Count,
            baselineFocusedConflictCount,
            trialGlobalConflictCount,
            trialFocusedConflictCount,
            true,
            solverStatus,
            accepted,
            acceptanceReason,
            workingMetrics.CanonicalConflictCount,
            trialMetrics.CanonicalConflictCount,
            workingMetrics.RootConflictCount,
            trialMetrics.RootConflictCount,
            workingMetrics.ConflictedStationCount,
            trialMetrics.ConflictedStationCount,
            currentObjective,
            trialObjective,
            _activeSectionEdges.Count,
            _activeStationEventEdges.Count,
            StationCoreCutDiagnosticMapper.Map(
                projection,
                core,
                _options.EndpointOccupationSeconds,
                attempted: true,
                accepted,
                retained: accepted,
                trialOutcome: acceptanceReason,
                acceptedRecourseWindowSeconds,
                recourseAttemptCount,
                fixedTemporalEventCount,
                boundedTemporalEventCount,
                certificateReason: string.Empty));

    private static StationInfeasibleCore ResolveInfeasibleCore(
        StationLevelMicroFeasibilitySlaveResult slaveResult,
        StationCoreProjection projection)
    {
        string[] expectedRequirementIds = projection.CoreStopIds
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        List<StationInfeasibleCore> matches = slaveResult.StationResults
            .Where(result => string.Equals(result.StationName, projection.StationName, StringComparison.OrdinalIgnoreCase))
            .Select(result => result.AssignmentResult.InfeasibleCore)
            .Where(core => core is not null)
            .Select(core => core!)
            .Where(core => core.RequirementIds
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .SequenceEqual(expectedRequirementIds, StringComparer.Ordinal))
            .ToList();

        return matches.Count == 1
            ? matches[0]
            : throw new InvalidOperationException(
                $"Expected exactly one infeasible core for projection '{projection.ProjectionId}', found {matches.Count}.");
    }

    private TrialCutEvaluation EvaluateCut(
        IReadOnlyList<LocalPrecedenceNogoodCut> activeCuts,
        MasterSolutionSnapshot currentSnapshot,
        int baselineGlobalConflictCount,
        int baselineFocusedConflictCount,
        StationLevelMicroFeasibilitySlaveResult baselineFocusedSlaveResult,
        LocalPrecedenceNogoodCut candidateCut,
        PrecedenceTrustRegion trustRegion,
        MasterMicroFeasibilityChecker microChecker,
        StationLevelMicroFeasibilitySlave microSlave)
    {
        List<LocalPrecedenceNogoodCut> trialCuts = activeCuts
            .Concat(new[] { candidateCut })
            .ToList();

        BasicMilpModel trialModel = BuildModel(trialCuts, trustRegion);
        ApplyMipStart(trialModel, currentSnapshot);
        OptimizeTrialModel(trialModel);

        if (!trialModel.HasIncumbentSolution)
        {
            return new TrialCutEvaluation(
                candidateCut,
                trustRegion,
                trialModel,
                Snapshot: null,
                FocusedSlaveResult: null,
                Report: null,
                TrialGlobalConflictCount: null,
                TrialFocusedConflictCount: null,
                IsAccepted: false,
                AcceptanceReason: ResolveNoIncumbentReason(trialModel));
        }

        MasterSolutionSnapshot trialSnapshot = MasterSolutionSnapshot.Capture(trialModel);
        StationLevelMicroFeasibilitySlaveResult trialFocusedSlaveResult = microSlave.Solve(
            trialModel,
            new MicroFeasibilitySlaveRequest(
                baselineFocusedSlaveResult.StationResults
                    .Select(result => result.StationName)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)));
        int trialFocusedConflictCount = trialFocusedSlaveResult.Conflicts.Count;
        MicroFeasibilityReport trialReport = microChecker.Validate(trialModel);
        int trialGlobalConflictCount = trialReport.Conflicts.Count;

        bool focusedImproved = _options.RequireStrictConflictImprovement
            ? trialFocusedConflictCount < baselineFocusedConflictCount
            : trialFocusedConflictCount <= baselineFocusedConflictCount;
        bool globalGuardSatisfied = trialReport.IsFeasible ||
            (_options.RequireStrictConflictImprovement
                ? trialGlobalConflictCount <= baselineGlobalConflictCount
                : trialGlobalConflictCount <= baselineGlobalConflictCount);
        bool accepted = focusedImproved && globalGuardSatisfied;

        string acceptanceReason = trialReport.IsFeasible && focusedImproved
            ? "accepted_micro_feasible"
            : accepted
                ? "accepted_focused_conflict_reduction"
                : !focusedImproved
                    ? "rejected_no_focused_conflict_improvement"
                    : "rejected_global_conflict_regression";

        return new TrialCutEvaluation(
            candidateCut,
            trustRegion,
            trialModel,
            trialSnapshot,
            trialFocusedSlaveResult,
            trialReport,
            trialGlobalConflictCount,
            trialFocusedConflictCount,
            accepted,
            acceptanceReason);
    }

    private void OptimizeTrialModel(BasicMilpModel trialModel)
    {
        trialModel.Model.Set(GRB.DoubleParam.TimeLimit, _options.TimeLimitSecondsPerSolve);
        trialModel.Optimize();

        if (trialModel.HasIncumbentSolution)
        {
            return;
        }

        if (trialModel.Model.Status == GRB.Status.TIME_LIMIT &&
            _options.SecondaryTimeLimitSecondsPerSolve > _options.TimeLimitSecondsPerSolve)
        {
            trialModel.Model.Set(GRB.DoubleParam.TimeLimit, _options.SecondaryTimeLimitSecondsPerSolve);
            trialModel.Optimize();
        }
    }

    private static string ResolveNoIncumbentReason(BasicMilpModel trialModel)
    {
        return trialModel.Model.Status switch
        {
            GRB.Status.INFEASIBLE => "rejected_infeasible_after_cut",
            GRB.Status.TIME_LIMIT => "deferred_time_limit_no_incumbent",
            GRB.Status.NODE_LIMIT => "deferred_node_limit_no_incumbent",
            _ => $"rejected_no_incumbent_status_{trialModel.Model.Status}"
        };
    }

    private static LocalPrecedenceNogoodCut RehydrateCutAgainstCurrentModel(
        LocalPrecedenceNogoodCut rawCut,
        BasicMilpModel currentModel)
    {
        Dictionary<string, int> precedenceValues = rawCut.PrecedenceIncumbentValuesByKey
            .Where(item => currentModel.SectionPrecedenceVars.ContainsKey(item.Key))
            .ToDictionary(
                item => item.Key,
                item => currentModel.SectionPrecedenceVars[item.Key].Get(GRB.DoubleAttr.X) >= 0.5 ? 1 : 0,
                StringComparer.OrdinalIgnoreCase);

        Dictionary<string, int> stopValues = rawCut.StopIncumbentValuesByStopId
            .Where(item => currentModel.StopVars.ContainsKey(item.Key))
            .ToDictionary(
                item => item.Key,
                item => currentModel.StopVars[item.Key].Get(GRB.DoubleAttr.X) >= 0.5 ? 1 : 0,
                StringComparer.OrdinalIgnoreCase);

        return rawCut with
        {
            CutId = BuildRepairCutId(rawCut.StationName, rawCut.FirstTrainIndex, rawCut.SecondTrainIndex, precedenceValues, stopValues),
            PrecedenceIncumbentValuesByKey = precedenceValues,
            StopIncumbentValuesByStopId = stopValues
        };
    }

    private LocalPrecedenceNogoodCut ShrinkCutSupport(
        BasicMilpModel currentModel,
        IReadOnlyList<LocalPrecedenceNogoodCut> activeCuts,
        MasterSolutionSnapshot currentSnapshot,
        int baselineGlobalConflictCount,
        int baselineFocusedConflictCount,
        StationLevelMicroFeasibilitySlaveResult baselineFocusedSlaveResult,
        LocalPrecedenceNogoodCut rawCut,
        PrecedenceTrustRegion trustRegion,
        MasterMicroFeasibilityChecker microChecker,
        StationLevelMicroFeasibilitySlave microSlave)
    {
        Dictionary<string, int> support = rawCut.PrecedenceIncumbentValuesByKey
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);

        if (support.Count <= 1)
        {
            return rawCut;
        }

        List<string> orderedKeys = support.Keys
            .OrderBy(key => trustRegion.HotspotKeys.Contains(key) ? 0 : 1)
            .ThenBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (string key in orderedKeys)
        {
            if (support.Count <= 1 || !support.ContainsKey(key))
            {
                continue;
            }

            Dictionary<string, int> candidateSupport = support
                .Where(item => !string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);

            LocalPrecedenceNogoodCut candidateCut = rawCut with
            {
                CutId = BuildRepairCutId(rawCut.StationName, rawCut.FirstTrainIndex, rawCut.SecondTrainIndex, candidateSupport, rawCut.StopIncumbentValuesByStopId),
                PrecedenceIncumbentValuesByKey = candidateSupport,
                Rationale = $"{rawCut.Rationale}; shrunk_support={candidateSupport.Count}"
            };

            TrialCutEvaluation evaluation = EvaluateCut(
                activeCuts,
                currentSnapshot,
                baselineGlobalConflictCount,
                baselineFocusedConflictCount,
                baselineFocusedSlaveResult,
                candidateCut,
                trustRegion,
                microChecker,
                microSlave);

            if (evaluation.IsAccepted)
            {
                support = candidateSupport;
            }

            evaluation.Dispose();
        }

        return rawCut with
        {
            CutId = BuildRepairCutId(rawCut.StationName, rawCut.FirstTrainIndex, rawCut.SecondTrainIndex, support, rawCut.StopIncumbentValuesByStopId),
            PrecedenceIncumbentValuesByKey = support,
            Rationale = $"{rawCut.Rationale}; final_support={support.Count}"
        };
    }

    private PrecedenceTrustRegion BuildHotspotTrustRegion(
        BasicMilpModel model,
        MasterSolutionSnapshot snapshot,
        StationLevelMicroFeasibilitySlaveResult focusedSlaveResult,
        LocalPrecedenceNogoodCut cut,
        int iterationIndex)
    {
        HashSet<string> hotspotStations = new(StringComparer.OrdinalIgnoreCase)
        {
            cut.StationName
        };

        foreach (string stationName in focusedSlaveResult.StationResults
            .OrderByDescending(item => item.ConflictCore.ConflictCount)
            .ThenBy(item => item.StationName, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.StationName)
            .Take(_options.HotspotStationCount))
        {
            hotspotStations.Add(stationName);
        }

        HashSet<string> hotspotSectionNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (string stationName in hotspotStations)
        {
            foreach ((string incomingSectionName, string outgoingSectionName) in BuildAdjacentSectionsForStation(_instance, stationName))
            {
                hotspotSectionNames.Add(incomingSectionName);
                hotspotSectionNames.Add(outgoingSectionName);
            }
        }

        HashSet<string> hotspotKeys = model.SectionConflictCandidates
            .Where(candidate => hotspotSectionNames.Contains(candidate.SectionName))
            .Select(BasicMilpModelBuilder.BuildSectionPrecedenceKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (string key in cut.PrecedenceIncumbentValuesByKey.Keys)
        {
            hotspotKeys.Add(key);
        }

        return new PrecedenceTrustRegion(
            $"iter{iterationIndex}_{BasicMilpModelBuilder.BuildSectionPrecedenceKey(cut.StationName, cut.FirstTrainIndex, cut.SecondTrainIndex)}",
            snapshot.SectionPrecedenceByKey,
            hotspotKeys,
            _options.MaxTotalPrecedenceChanges,
            _options.MaxNonHotspotPrecedenceChanges);
    }

    private static IReadOnlyList<LocalPrecedenceNogoodCut> BuildLocalCoreCuts(
        MicroFeedbackGenerator feedbackGenerator,
        BasicMilpModel model,
        StationLevelMicroFeasibilitySlaveResult focusedSlaveResult,
        ISet<string> screenedCutIds,
        int maxCandidateCount)
    {
        bool hasProvenCore = focusedSlaveResult.StationResults.Any(result =>
            !result.IsFeasible && result.ConflictCore.ConflictCount > 0);
        if (maxCandidateCount <= 0 ||
            !PermanentMicroFeedbackEligibilityPolicy.Allows(focusedSlaveResult.Status, hasProvenCore))
        {
            return Array.Empty<LocalPrecedenceNogoodCut>();
        }

        List<LocalPrecedenceNogoodCut> rankedCuts = feedbackGenerator.Generate(model, focusedSlaveResult, screenedCutIds)
            .Where(feedback => feedback.Kind is MicroFeedbackKind.StationPressure or MicroFeedbackKind.RepairPattern)
            .OrderByDescending(feedback => feedback.PriorityScore)
            .ThenBy(feedback => feedback.Kind == MicroFeedbackKind.StationPressure ? 0 : 1)
            .ThenBy(feedback => feedback.StationName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(feedback => feedback.FeedbackId, StringComparer.OrdinalIgnoreCase)
            .Select(MicroFeedbackGenerator.ToMasterCut)
            .ToList();

        return BuildFamilyBalancedCandidatePool(rankedCuts, maxCandidateCount);
    }

    private static IReadOnlyList<LocalPrecedenceNogoodCut> BuildFamilyBalancedCandidatePool(
        IReadOnlyList<LocalPrecedenceNogoodCut> rankedCuts,
        int maxCandidateCount)
    {
        Dictionary<string, Queue<LocalPrecedenceNogoodCut>> queuesByFamily = rankedCuts
            .GroupBy(cut => cut.SourceFamily, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => new Queue<LocalPrecedenceNogoodCut>(group),
                StringComparer.OrdinalIgnoreCase);

        List<string> familyOrder = queuesByFamily.Keys
            .OrderBy(FamilyPriority)
            .ThenBy(family => family, StringComparer.OrdinalIgnoreCase)
            .ToList();
        List<LocalPrecedenceNogoodCut> selectedCuts = new();

        while (selectedCuts.Count < maxCandidateCount && queuesByFamily.Values.Any(queue => queue.Count > 0))
        {
            foreach (string family in familyOrder)
            {
                if (selectedCuts.Count >= maxCandidateCount)
                {
                    break;
                }

                Queue<LocalPrecedenceNogoodCut> queue = queuesByFamily[family];
                if (queue.Count == 0)
                {
                    continue;
                }

                selectedCuts.Add(queue.Dequeue());
            }
        }

        return selectedCuts;
    }

    private static int FamilyPriority(string sourceFamily)
    {
        return sourceFamily switch
        {
            "micro_feedback_station_pressure_relief" => 0,
            "micro_feedback_repair_pattern" => 1,
            _ => 10
        };
    }

    private StationLevelMicroFeasibilitySlaveResult SolveFocusedSlave(
        StationLevelMicroFeasibilitySlave microSlave,
        BasicMilpModel currentModel,
        MicroFeasibilityReport currentReport)
    {
        HashSet<string> hotspotStations = currentReport.StationSummaries
            .Where(summary => summary.ConflictCount > 0)
            .OrderByDescending(summary => summary.ConflictCount)
            .ThenByDescending(summary => summary.StopRequirementCount)
            .ThenBy(summary => summary.StationName, StringComparer.OrdinalIgnoreCase)
            .Take(_options.SlaveHotspotStationCount)
            .Select(summary => summary.StationName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (hotspotStations.Count == 0)
        {
            return new StationLevelMicroFeasibilitySlaveResult(
                true,
                Array.Empty<StationLevelMicroSubproblemResult>(),
                Array.Empty<MicroConflictExplanation>(),
                Array.Empty<MicroConflictExplanation>());
        }

        return microSlave.Solve(currentModel, new MicroFeasibilitySlaveRequest(hotspotStations));
    }

    private static MicroFeasibilityReport BuildFocusedReport(
        StationLevelMicroFeasibilitySlaveResult focusedSlaveResult)
    {
        IReadOnlyList<MicroStationSummary> stationSummaries = focusedSlaveResult.StationResults
            .Select(result => new MicroStationSummary(
                result.StationName,
                result.Requirements.Count,
                result.Requirements.Count(requirement => requirement.IsStop),
                result.Requirements.SelectMany(requirement => requirement.FeasibleTracks).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                result.Conflicts.Count,
                string.Join('|', result.Conflicts
                    .Select(conflict => conflict.ConflictKind)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(kind => kind, StringComparer.OrdinalIgnoreCase)),
                result.AssignmentResult.IsFeasible,
                result.AssignmentResult.SelectedAlternatives.Count,
                result.AssignmentResult.FailureEvidence))
            .OrderByDescending(item => item.ConflictCount)
            .ThenBy(item => item.StationName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new MicroFeasibilityReport(
            focusedSlaveResult.IsFeasible,
            focusedSlaveResult.Conflicts,
            stationSummaries,
            focusedSlaveResult.RouteChoiceDiagnostics);
    }

    private bool TryOptimizeValidityFirstMaster(
        BasicMilpModel model,
        int iterationIndex,
        string phase,
        double requestedTimeLimitSeconds,
        MasterSolutionSnapshot? repairReference = null)
    {
        _ = _validityFirstRunClock
            ?? throw new InvalidOperationException("Validity-first runtime clock was not initialized.");
        ValidityFirstRuntimeBudget budget = ValidityFirstRuntimeBudgetPolicy.Resolve(
            requestedTimeLimitSeconds,
            _options.ValidityFirstTotalRuntimeBudgetSeconds,
            GetValidityFirstCumulativeElapsedSeconds(),
            _options.ValidityFirstFinalValidationReserveSeconds);
        if (!budget.CanStart)
        {
            Console.WriteLine(
                $"Validity-first enhanced Master skipped: phase={phase}, iteration={iterationIndex}, " +
                $"elapsed={GetValidityFirstCumulativeElapsedSeconds():R}, remaining={budget.RemainingGlobalSeconds:R}, " +
                $"reserved_validation={budget.ReservedValidationSeconds:R}.");
            return false;
        }

        ValidityFirstSolveParameterPlan solvePlan =
            ValidityFirstSolveParameterPolicy.Resolve(phase, _options);
        if (solvePlan.ObjectiveKind == ValidityFirstSolveObjectiveKind.FeasibilityRecovery)
        {
            StructuralRepairObjectiveBuilder.ApplyFeasibilityRecovery(
                model,
                repairReference ?? throw new InvalidOperationException(
                    $"Validity-first phase '{phase}' requires an explicit repair reference."));
        }

        _validityFirstEnhancedMasterSolveSequence++;
        _lastValidityFirstEnhancedMasterDiagnostic = ValidityFirstEnhancedMasterSolveRunner.Optimize(
            model,
            ResolveValidityFirstOutputDirectory(),
            _validityFirstEnhancedMasterSolveSequence,
            iterationIndex,
            phase,
            budget,
            ValidityFirstConfigurationFingerprint.Calculate(_options),
            solvePlan,
            new ValidityFirstIisExecutionContext(
                _options.ValidityFirstIisTimeLimitSeconds,
                _options.ValidityFirstIisCumulativeBudgetSeconds,
                _options.ValidityFirstTotalRuntimeBudgetSeconds,
                _options.ValidityFirstFinalValidationReserveSeconds,
                GetValidityFirstCumulativeElapsedSeconds,
                _validityFirstIisBudgetTracker));
        return true;
    }

    private MasterSolutionSnapshot? TryLoadAuditedValidityFirstCandidate(
        BasicMilpModel model,
        string phase)
    {
        if (!model.HasIncumbentSolution)
        {
            return null;
        }
        bool requiresEarlyAudit =
            _options.MacroCandidateCheckStrategy == MacroCandidateCheckStrategy.EarlyCheck &&
            MacroCandidatePhasePolicy.IsEarlyCheckPhase(phase);
        if (!requiresEarlyAudit)
        {
            return MasterSolutionSnapshot.Capture(model);
        }
        ValidityFirstEnhancedMasterSolveDiagnostic diagnostic =
            _lastValidityFirstEnhancedMasterDiagnostic
            ?? throw new InvalidOperationException(
                $"Early-check phase '{phase}' has no solve diagnostic.");
        if (!string.Equals(diagnostic.Phase, phase, StringComparison.OrdinalIgnoreCase) ||
            !diagnostic.CandidateCanProceedToFullCheck ||
            !diagnostic.CandidateEligibility.Passed ||
            string.IsNullOrWhiteSpace(diagnostic.CandidateSnapshotPath) ||
            !File.Exists(diagnostic.CandidateSnapshotPath))
        {
            return null;
        }
        return MasterSolutionSnapshotStore.Load(
            diagnostic.CandidateSnapshotPath,
            _instance.Meta.InstanceName,
            _instance.Trains.Select(train => train.TrainIndex).ToHashSet());
    }

    private string ResolveValidityFirstNoCandidateTermination(
        string phase,
        bool solveStarted,
        string fallback)
    {
        if (_options.CoordinationStrategy != CheckerCoordinationStrategy.ValidityFirstBenders)
        {
            return fallback;
        }
        if (!solveStarted)
        {
            return $"validity_first_global_budget_exhausted_before_{SanitizeFilePart(phase)}";
        }
        return _lastValidityFirstEnhancedMasterDiagnostic?.ApplicationStopReason ?? fallback;
    }

    private double GetValidityFirstCumulativeElapsedSeconds()
    {
        double currentProcessElapsedSeconds = _validityFirstProcessElapsedSecondsProvider?.Invoke() ??
            (_validityFirstProcessElapsedBeforeSolveSeconds +
             (_validityFirstRunClock?.Elapsed.TotalSeconds ?? 0.0));
        return ValidityFirstCumulativeRuntimePolicy.ResolveElapsedSeconds(
            _options.ValidityFirstPreviouslyConsumedRuntimeSeconds,
            currentProcessElapsedSeconds);
    }

    private MasterValidationReport RunTimedMasterValidation(
        BasicMilpModel model,
        int iterationIndex,
        string phase)
    {
        double validationStartElapsed = GetValidityFirstCumulativeElapsedSeconds();
        Stopwatch stopwatch = Stopwatch.StartNew();
        MasterValidationReport report = new MasterSolutionValidator().Validate(model);
        stopwatch.Stop();
        MacroRelationMergeResult merge = MergeSuggestedSectionEdges(report.SuggestedSectionEdges ?? []);
        MacroClosureDecision closureDecision = MacroClosureDecisionPolicy.Resolve(report);
        string validationDirectory = Path.Combine(
            ResolveValidityFirstOutputDirectory(),
            "macro_validation");
        Directory.CreateDirectory(validationDirectory);
        _validityFirstMacroValidationSequence++;
        new MasterSolutionValidator().WriteReport(
            report,
            Path.Combine(
                validationDirectory,
                $"{_validityFirstMacroValidationSequence:D3}_iteration_{iterationIndex:D3}_{SanitizeFilePart(phase)}.md"));
        double validationCompletedElapsed = GetValidityFirstCumulativeElapsedSeconds();
        ValidityFirstEnhancedMasterSolveDiagnostic? solve =
            _lastValidityFirstEnhancedMasterDiagnostic;
        double? solveReturnElapsed = solve is null
            ? null
            : _options.ValidityFirstTotalRuntimeBudgetSeconds -
              solve.RemainingGlobalBudgetSecondsBeforeSolve + solve.RuntimeSeconds;
        MacroCandidateClosureReporter.Append(
            ResolveValidityFirstOutputDirectory(),
            new MacroCandidateClosureRound(
                _validityFirstMacroValidationSequence,
                iterationIndex,
                phase,
                solve?.Sequence,
                solve?.Status ?? model.StatusName,
                solve?.ApplicationStopReason ?? "fixed_snapshot_candidate",
                solve?.FirstIncumbentTimeSeconds,
                solve?.FirstEligibleCandidateTimeSeconds,
                solveReturnElapsed,
                validationStartElapsed,
                validationCompletedElapsed,
                stopwatch.Elapsed.TotalSeconds,
                solve?.CandidateEligibility.SnapshotComplete ?? true,
                report.IsFeasible,
                report.Violations.Count,
                report.SectionRelationFindings?.Count(item => !item.WasModeled) ?? 0,
                report.SectionRelationFindings?.Count(item => item.WasModeled) ?? 0,
                merge.AddedCount,
                merge.CumulativeCount,
                model.SectionConflictCandidates.Count,
                model.Model.Get(GRB.IntAttr.NumConstrs),
                model.Model.Get(GRB.IntAttr.NumVars),
                model.Model.Get(GRB.IntAttr.NumBinVars),
                model.Model.Get(GRB.IntAttr.NumNZs),
                Math.Max(0.0,
                    _options.ValidityFirstTotalRuntimeBudgetSeconds - validationCompletedElapsed),
                closureDecision.Reason));
        WriteValidityFirstValidationTiming(
            iterationIndex,
            phase,
            "Validator",
            stopwatch.Elapsed.TotalSeconds,
            report.IsFeasible,
            report.Violations.Count);
        return report;
    }

    private MacroRelationMergeResult MergeSuggestedSectionEdges(
        IReadOnlyList<SectionConflictEdgeRequest> suggestedEdges)
    {
        MacroRelationMergeResult result = MacroRelationAccumulator.Merge(
            _activeSectionEdges,
            suggestedEdges);
        foreach (SectionConflictEdgeRequest edge in result.AddedEdges)
        {
            Console.WriteLine(
                $"Macro full-check added section edge: section={edge.SectionName}, " +
                $"trains={edge.FirstTrainIndex}/{edge.SecondTrainIndex}.");
        }
        return result;
    }

    private static string SanitizeFilePart(string value) => new(
        value.Select(character => char.IsLetterOrDigit(character) || character is '-' or '_'
            ? character
            : '_').ToArray());

    private MicroFeasibilityReport RunTimedGlobalMicroValidation(
        MasterMicroFeasibilityChecker checker,
        BasicMilpModel model,
        int iterationIndex,
        string phase)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        MicroFeasibilityReport report = checker.Validate(model);
        stopwatch.Stop();
        WriteValidityFirstValidationTiming(
            iterationIndex,
            phase,
            "GlobalMicro",
            stopwatch.Elapsed.TotalSeconds,
            report.IsFeasible,
            report.Conflicts.Count);
        if (_options.CoordinationStrategy == CheckerCoordinationStrategy.ValidityFirstBenders)
        {
            string detailedDirectory = Path.Combine(
                ResolveValidityFirstOutputDirectory(),
                "micro_validation",
                $"{iterationIndex:D3}_{phase}");
            checker.WriteReports(report, detailedDirectory);
            foreach (MicroConflictExplanation conflict in report.Conflicts)
            {
                Console.WriteLine(
                    $"Validity-first Micro conflict detail: iteration={iterationIndex}, phase={phase}, " +
                    $"kind={conflict.ConflictKind}, station={conflict.StationName}, " +
                    $"first_train={conflict.FirstTrainIndex?.ToString(CultureInfo.InvariantCulture) ?? "none"}, " +
                    $"second_train={conflict.SecondTrainIndex?.ToString(CultureInfo.InvariantCulture) ?? "none"}, " +
                    $"first_side={conflict.FirstEventSide?.ToString() ?? "none"}, " +
                    $"second_side={conflict.SecondEventSide?.ToString() ?? "none"}, " +
                    $"first_section={conflict.FirstSectionName ?? "none"}, " +
                    $"second_section={conflict.SecondSectionName ?? "none"}, " +
                    $"start={conflict.StartSeconds:R}, end={conflict.EndSeconds:R}, " +
                    $"resource={conflict.ResourceKey}, evidence={conflict.Evidence}.");
            }
        }
        return report;
    }

    private void WriteValidityFirstValidationTiming(
        int iterationIndex,
        string phase,
        string validator,
        double runtimeSeconds,
        bool feasible,
        int findingCount)
    {
        if (_options.CoordinationStrategy != CheckerCoordinationStrategy.ValidityFirstBenders)
        {
            return;
        }

        string outputDirectory = ResolveValidityFirstOutputDirectory();
        Directory.CreateDirectory(outputDirectory);
        string path = Path.Combine(outputDirectory, "validity_first_validation_timings.csv");
        if (!File.Exists(path))
        {
            File.AppendAllText(
                path,
                "iteration,phase,validator,runtime_seconds,feasible,finding_count,elapsed_global_seconds" +
                Environment.NewLine,
                new UTF8Encoding(false));
        }
        File.AppendAllText(
            path,
            string.Join(',',
                iterationIndex.ToString(CultureInfo.InvariantCulture),
                Escape(phase),
                Escape(validator),
                runtimeSeconds.ToString("R", CultureInfo.InvariantCulture),
                feasible,
                findingCount.ToString(CultureInfo.InvariantCulture),
            GetValidityFirstCumulativeElapsedSeconds()
                    .ToString("R", CultureInfo.InvariantCulture)) + Environment.NewLine,
            new UTF8Encoding(false));
        Console.WriteLine(
            $"Validity-first validation timing: iteration={iterationIndex}, phase={phase}, " +
            $"validator={validator}, runtime={runtimeSeconds:R}, feasible={feasible}, findings={findingCount}.");
    }

    private string ResolveValidityFirstOutputDirectory()
    {
        if (!string.IsNullOrWhiteSpace(_checkpointOutputPath))
        {
            string fullCheckpointPath = Path.GetFullPath(_checkpointOutputPath);
            return Path.GetDirectoryName(fullCheckpointPath)
                ?? throw new InvalidOperationException("The checkpoint path has no parent directory.");
        }

        return Path.Combine(
            Path.GetTempPath(),
            "gurobi-validity-first-diagnostics",
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
    }

    private MasterSolutionSnapshot AttachVerifiedMicroAssignments(
        BasicMilpModel model,
        MasterSolutionSnapshot snapshot,
        StationLevelMicroFeasibilitySlave microSlave,
        int iterationIndex,
        string phase)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        StationLevelMicroFeasibilitySlaveResult assignmentResult = microSlave.Solve(model);
        MasterSolutionSnapshot refreshed = VerifiedMicroAssignmentSnapshotRefresher.Refresh(
            snapshot,
            assignmentResult,
            SnapshotFingerprint.Calculate(snapshot));
        IReadOnlyList<VerifiedMicroAssignmentWitness> witnesses =
            refreshed.VerifiedMicroAssignments ?? [];
        stopwatch.Stop();
        int feasibleStationCount = assignmentResult.StationResults.Count(result => result.IsFeasible);
        int unknownStationCount = assignmentResult.StationResults.Count(result =>
            result.AssignmentResult.Status == MicroSolveStatus.Unknown);
        Console.WriteLine(
            $"Validity-first Micro witness capture: phase={phase}, iteration={iterationIndex}, " +
            $"witnesses={witnesses.Count}, feasible_stations={feasibleStationCount}, " +
            $"unknown_stations={unknownStationCount}, seconds={stopwatch.Elapsed.TotalSeconds:R}.");
        ValidityFirstPhaseTimingReporter.Append(
            iterationIndex,
            phase,
            $"micro_witness_capture::{witnesses.Count}::{feasibleStationCount}::{unknownStationCount}",
            stopwatch.Elapsed.TotalSeconds,
            GetValidityFirstCumulativeElapsedSeconds(),
            ResolveValidityFirstOutputDirectory());
        return refreshed;
    }

    private static void ApplyMipStart(BasicMilpModel model, MasterSolutionSnapshot snapshot)
    {
        _ = MasterSolutionSnapshotApplier.ApplyMipStart(model, snapshot);
    }

    private static IReadOnlyList<VerifiedMicroAssignmentWitness> MergeRepairWitnesses(
        IReadOnlyList<VerifiedMicroAssignmentWitness> verified,
        IReadOnlyList<VerifiedMicroAssignmentWitness> hotspotCandidates)
    {
        ArgumentNullException.ThrowIfNull(verified);
        ArgumentNullException.ThrowIfNull(hotspotCandidates);
        List<IGrouping<string, VerifiedMicroAssignmentWitness>> groups = verified
            .Concat(hotspotCandidates)
            .GroupBy(witness => witness.RequirementId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        IGrouping<string, VerifiedMicroAssignmentWitness>? duplicate = groups
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidDataException(
                $"Micro repair witness business ID is duplicated across verified and candidate sets: " +
                $"{duplicate.Key}.");
        }

        return groups
            .Select(group => group.Single())
            .OrderBy(witness => witness.StationName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(witness => witness.TrainIndex)
            .ThenBy(witness => witness.RequirementId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static HashSet<int> ResolveHotspotFallbackRelatedTrainIndices(
        StationLevelMicroSubproblemResult stationResult,
        IReadOnlyList<MicroConflictExplanation> conflicts,
        IReadOnlySet<int> conflictTrainIndices)
    {
        HashSet<int> related = conflictTrainIndices.ToHashSet();
        IReadOnlyList<MicroConflictExplanation> stationConflicts = conflicts
            .Where(conflict => string.Equals(
                conflict.StationName,
                stationResult.StationName,
                StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (MicroConflictExplanation conflict in stationConflicts)
        {
            AddAdjacentUsers(conflict.FirstTrainIndex, conflict.FirstEventSide);
            AddAdjacentUsers(conflict.SecondTrainIndex, conflict.SecondEventSide);
        }
        return related;

        void AddAdjacentUsers(int? trainIndex, StationEventSide? eventSide)
        {
            if (!trainIndex.HasValue)
            {
                return;
            }
            List<int> ordered = stationResult.Requirements
                .OrderBy(requirement => eventSide == StationEventSide.Incoming
                    ? requirement.ArrivalSeconds
                    : requirement.DepartureSeconds)
                .ThenBy(requirement => requirement.TrainIndex)
                .Select(requirement => requirement.TrainIndex)
                .Distinct()
                .ToList();
            int index = ordered.IndexOf(trainIndex.Value);
            if (index > 0)
            {
                related.Add(ordered[index - 1]);
            }
            if (index >= 0 && index + 1 < ordered.Count)
            {
                related.Add(ordered[index + 1]);
            }
        }
    }

    private static List<(string IncomingSectionName, string OutgoingSectionName)> BuildAdjacentSectionsForStation(
        TimetablingInstance instance,
        string stationName)
    {
        List<(string IncomingSectionName, string OutgoingSectionName)> pairs = new();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (TrainRecord train in instance.Trains)
        {
            for (int index = 1; index < train.OrderedStops.Count - 1; index++)
            {
                TrainStopRecord currentStop = train.OrderedStops[index];
                TrainStopRecord nextStop = train.OrderedStops[index + 1];
                if (!string.Equals(currentStop.StationName, stationName, StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(currentStop.IncomingSectionName) ||
                    string.IsNullOrWhiteSpace(nextStop.IncomingSectionName))
                {
                    continue;
                }

                string key = $"{currentStop.IncomingSectionName}::{nextStop.IncomingSectionName}";
                if (seen.Add(key))
                {
                    pairs.Add((currentStop.IncomingSectionName, nextStop.IncomingSectionName));
                }
            }
        }

        return pairs;
    }

    private static string BuildRepairCutId(
        string stationName,
        int firstTrainIndex,
        int secondTrainIndex,
        IReadOnlyDictionary<string, int> precedenceIncumbentValues,
        IReadOnlyDictionary<string, int> stopIncumbentValues)
    {
        string precedenceSignature = string.Join(
            "|",
            precedenceIncumbentValues
                .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                .Select(item => $"{item.Key}={item.Value}"));

        string stopSignature = string.Join(
            "|",
            stopIncumbentValues
                .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                .Select(item => $"{item.Key}={item.Value}"));

        return $"{stationName}::{firstTrainIndex}::{secondTrainIndex}::prec[{precedenceSignature}]::stop[{stopSignature}]";
    }

    private static void WriteIterationsCsv(
        IReadOnlyList<CheckerLoopIteration> iterations,
        string terminationReason,
        int endpointOccupationSeconds,
        string path)
    {
        using StreamWriter writer = new(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        writer.WriteLine("iteration_index,objective_before,objective_after,micro_feasible_after,raw_conflicts_before,raw_conflicts_after,canonical_conflicts_before,canonical_conflicts_after,root_conflicts_before,root_conflicts_after,episode_conflicts_before,episode_conflicts_after,conflicted_stations_before,conflicted_stations_after,candidate_count,screened_count,adaptive_batch_size,adaptive_repair_padding,adaptive_policy_reason,selected_candidate_summary,active_section_edges_before,active_section_edges_after,active_station_edges_before,active_station_edges_after,temporary_heuristic_cuts_after,zero_conflict_mode,accepted_recourse_window_seconds,recourse_attempt_count,fixed_temporal_event_count,bounded_temporal_event_count,total_retiming_seconds,maximum_retiming_seconds,unseen_structural_candidate_count,solver_status,decision,accepted_cut_id,accepted_station,record_type,report_termination_reason,endpoint_occupation_seconds,exact_conflicts_before,exact_conflicts_after,assignment_conflicts_before,assignment_conflicts_after,accepted,accepted_candidate_ids,accepted_repair_type,iis_guided_used,iis_expansion_rounds,master_validator_violations,input_snapshot_fingerprint,accepted_snapshot_fingerprint,input_conflict_fingerprint,accepted_conflict_fingerprint,continuity_verified");

        if (iterations.Count == 0)
        {
            string[] summaryFields = new string[55];
            Array.Fill(summaryFields, string.Empty);
            summaryFields[37] = "report_summary";
            summaryFields[38] = Escape(terminationReason);
            summaryFields[39] = endpointOccupationSeconds.ToString(CultureInfo.InvariantCulture);
            writer.WriteLine(string.Join(',', summaryFields));
        }

        foreach (CheckerLoopIteration item in iterations)
        {
            writer.WriteLine(string.Join(
                ',',
                item.IterationIndex.ToString(CultureInfo.InvariantCulture),
                item.ObjectiveBefore.ToString("F6", CultureInfo.InvariantCulture),
                item.ObjectiveAfter.ToString("F6", CultureInfo.InvariantCulture),
                item.MicroFeasibleAfter ? "True" : "False",
                item.MicroConflictCountBefore.ToString(CultureInfo.InvariantCulture),
                item.MicroConflictCountAfter.ToString(CultureInfo.InvariantCulture),
                item.CanonicalConflictCountBefore?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                item.CanonicalConflictCountAfter?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                item.RootConflictCountBefore?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                item.RootConflictCountAfter?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                item.EpisodeConflictCountBefore?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                item.EpisodeConflictCountAfter?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                item.ConflictedStationCountBefore?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                item.ConflictedStationCountAfter?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                item.CandidateCutCount.ToString(CultureInfo.InvariantCulture),
                item.ScreenedCutCount.ToString(CultureInfo.InvariantCulture),
                item.AdaptiveBatchSize.ToString(CultureInfo.InvariantCulture),
                item.AdaptiveRepairPadding.ToString(CultureInfo.InvariantCulture),
                Escape(item.AdaptivePolicyReason),
                Escape(item.SelectedCandidateSummary),
                item.ActiveSectionEdgeCountBefore.ToString(CultureInfo.InvariantCulture),
                item.ActiveSectionEdgeCountAfter.ToString(CultureInfo.InvariantCulture),
                item.ActiveStationEventEdgeCountBefore.ToString(CultureInfo.InvariantCulture),
                item.ActiveStationEventEdgeCountAfter.ToString(CultureInfo.InvariantCulture),
                item.TemporaryHeuristicCutCountAfter.ToString(CultureInfo.InvariantCulture),
                item.ZeroConflictMode ? "True" : "False",
                item.AcceptedRecourseWindowSeconds.ToString(CultureInfo.InvariantCulture),
                item.RecourseAttemptCount.ToString(CultureInfo.InvariantCulture),
                item.FixedTemporalEventCount.ToString(CultureInfo.InvariantCulture),
                item.BoundedTemporalEventCount.ToString(CultureInfo.InvariantCulture),
                item.TotalRetimingSeconds.ToString("F3", CultureInfo.InvariantCulture),
                item.MaximumRetimingSeconds.ToString("F3", CultureInfo.InvariantCulture),
                item.UnseenStructuralCandidateCount.ToString(CultureInfo.InvariantCulture),
                item.SolverStatus,
                item.Decision,
                Escape(item.AcceptedCutId),
                Escape(item.AcceptedStation),
                "iteration",
                Escape(terminationReason),
                endpointOccupationSeconds.ToString(CultureInfo.InvariantCulture),
                item.ExactConflictCountBefore?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                item.ExactConflictCountAfter?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                item.AssignmentConflictCountBefore?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                item.AssignmentConflictCountAfter?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                item.Accepted ? "True" : "False",
                Escape(item.AcceptedCandidateIds),
                Escape(item.AcceptedRepairType),
                item.IisGuidedUsed ? "True" : "False",
                item.IisExpansionRounds.ToString(CultureInfo.InvariantCulture),
                item.MasterValidatorViolationCount?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                item.InputSnapshotFingerprint,
                item.AcceptedSnapshotFingerprint,
                item.ConflictFingerprintBefore,
                item.ConflictFingerprintAfter,
                item.ContinuityVerified ? "True" : "False"));
        }
    }

    private static void WriteIterationsMarkdown(CheckerLoopResult result, string path)
    {
        StringBuilder builder = new();
        builder.AppendLine("# Checker Loop Progress");
        builder.AppendLine();
        builder.AppendLine($"- termination_reason: `{result.TerminationReason}`");
        builder.AppendLine($"- total_iterations: `{result.Iterations.Count}`");
        IReadOnlyList<CheckerCutAuditRecord> audit = CheckerCutAuditBuilder.Build(result.Iterations, result.Diagnostics);
        CheckerCutAuditSummary summary = CheckerCutAuditBuilder.Summarize(audit);
        builder.AppendLine($"- retained_structural_batch_count: `{summary.RetainedStructuralBatchCount}`");
        builder.AppendLine($"- accepted_heuristic_cut_count: `{summary.AcceptedHeuristicCutCount}`");
        builder.AppendLine($"- unknown_heuristic_trial_count: `{summary.UnknownHeuristicTrialCount}`");
        builder.AppendLine($"- architectural_certificate_count: `{summary.CertificateCount}`");
        builder.AppendLine($"- active_section_edge_count: `{result.Iterations.Select(item => item.ActiveSectionEdgeCountAfter).DefaultIfEmpty(0).Max()}`");
        builder.AppendLine($"- active_station_event_edge_count: `{result.Iterations.Select(item => item.ActiveStationEventEdgeCountAfter).DefaultIfEmpty(0).Max()}`");
        builder.AppendLine($"- temporary_active_heuristic_cut_count: `{result.ActiveCuts.Count}`");
        builder.AppendLine($"- zero_conflict_mode: `{result.Iterations.Any(item => item.ZeroConflictMode)}`");
        builder.AppendLine($"- endpoint_occupation_seconds: `{result.EndpointOccupationSeconds.ToString(CultureInfo.InvariantCulture)}`");
        builder.AppendLine();
        builder.AppendLine("| iter | obj_before | obj_after | raw | exact | assignment | root | episodes | accepted | IIS | continuity | active edges | decision |");
        builder.AppendLine("|---:|---:|---:|---|---|---|---|---|---|---|---|---|---|");

        foreach (CheckerLoopIteration item in result.Iterations)
        {
            builder.AppendLine($"| {item.IterationIndex} | {item.ObjectiveBefore:F3} | {item.ObjectiveAfter:F3} | {item.MicroConflictCountBefore}->{item.MicroConflictCountAfter} | {FormatDelta(item.ExactConflictCountBefore, item.ExactConflictCountAfter)} | {FormatDelta(item.AssignmentConflictCountBefore, item.AssignmentConflictCountAfter)} | {FormatDelta(item.RootConflictCountBefore, item.RootConflictCountAfter)} | {FormatDelta(item.EpisodeConflictCountBefore, item.EpisodeConflictCountAfter)} | {item.Accepted} `{EscapeMarkdown(item.AcceptedCandidateIds)}` `{EscapeMarkdown(item.AcceptedRepairType)}` | used={item.IisGuidedUsed}, rounds={item.IisExpansionRounds} | verified={item.ContinuityVerified}<br>`{item.InputSnapshotFingerprint}` -> `{item.AcceptedSnapshotFingerprint}`<br>`{item.ConflictFingerprintBefore}` -> `{item.ConflictFingerprintAfter}` | S {item.ActiveSectionEdgeCountBefore}->{item.ActiveSectionEdgeCountAfter}; E {item.ActiveStationEventEdgeCountBefore}->{item.ActiveStationEventEdgeCountAfter} | `{item.Decision}` |");
        }

        CheckerNeighborhoodEscalationDiagnosticWriter.AppendMarkdownTable(
            builder,
            result.NeighborhoodEscalationDiagnostics ?? Array.Empty<CheckerNeighborhoodEscalationDiagnostic>());

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static void WriteCutAuditCsv(IReadOnlyList<CheckerCutAuditRecord> audit, string path)
    {
        using StreamWriter writer = new(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        writer.WriteLine("candidate_id,batch_id,iteration_index,feedback_class,action,source_family,validity,outcome,solver_status,raw_before,raw_after,canonical_before,canonical_after,root_before,root_after,episode_before,episode_after,stations_before,stations_after,objective_before,objective_after,section_edges_before,section_edges_after,station_edges_before,station_edges_after,adaptive_batch_size,adaptive_repair_padding,adaptive_policy_reason,zero_conflict_mode,accepted_recourse_window_seconds,recourse_attempt_count,fixed_temporal_event_count,bounded_temporal_event_count,total_retiming_seconds,maximum_retiming_seconds,unseen_structural_candidate_count,decision,station_name,station_core_id,projection_id,core_train_indices,core_requirement_ids,core_minimality_status,core_reduction_explored_nodes,projected_literal_count,projected_positive_literal_count,projected_negative_literal_count,projected_stop_literal_count,projected_stop_positive_literal_count,projected_stop_negative_literal_count,projected_section_literal_count,projected_section_positive_literal_count,projected_section_negative_literal_count,projected_station_event_literal_count,projected_station_event_positive_literal_count,projected_station_event_negative_literal_count,endpoint_occupation_seconds,attempted,accepted,retained,trial_outcome,station_core_accepted_recourse_window_seconds,station_core_recourse_attempt_count,station_core_fixed_temporal_event_count,station_core_bounded_temporal_event_count,certificate_reason");
        foreach (CheckerCutAuditRecord item in audit)
        {
            StationCoreCutDiagnostic? core = item.StationCore;
            writer.WriteLine(string.Join(',',
                Escape(item.CandidateId), Escape(item.BatchId), item.IterationIndex,
                item.FeedbackClass, Escape(item.Action), Escape(item.SourceFamily), Escape(item.Validity),
                Escape(item.OutcomeLabel), Escape(item.SolverStatus), item.RawConflictsBefore,
                item.RawConflictsAfter?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                item.CanonicalConflictsBefore?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                item.CanonicalConflictsAfter?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                item.RootConflictsBefore?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                item.RootConflictsAfter?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                item.EpisodeConflictsBefore?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                item.EpisodeConflictsAfter?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                item.ConflictedStationsBefore?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                item.ConflictedStationsAfter?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                item.ObjectiveBefore?.ToString("F6", CultureInfo.InvariantCulture) ?? string.Empty,
                item.ObjectiveAfter?.ToString("F6", CultureInfo.InvariantCulture) ?? string.Empty,
                item.ActiveSectionEdgesBefore, item.ActiveSectionEdgesAfter,
                item.ActiveStationEdgesBefore, item.ActiveStationEdgesAfter,
                item.AdaptiveBatchSize,
                item.AdaptiveRepairPadding,
                Escape(item.AdaptivePolicyReason),
                item.ZeroConflictMode ? "True" : "False",
                item.AcceptedRecourseWindowSeconds,
                item.RecourseAttemptCount,
                item.FixedTemporalEventCount,
                item.BoundedTemporalEventCount,
                item.TotalRetimingSeconds.ToString("F3", CultureInfo.InvariantCulture),
                item.MaximumRetimingSeconds.ToString("F3", CultureInfo.InvariantCulture),
                item.UnseenStructuralCandidateCount,
                Escape(item.Decision),
                Escape(core?.StationName ?? string.Empty),
                Escape(core?.CoreId ?? string.Empty),
                Escape(core?.ProjectionId ?? string.Empty),
                Escape(FormatList(core?.CoreTrainIndices)),
                Escape(FormatList(core?.CoreRequirementIds)),
                Escape(core?.CoreMinimalityStatus ?? string.Empty),
                core?.CoreReductionExploredNodes.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                core?.ProjectedLiteralCount.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                core?.ProjectedPositiveLiteralCount.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                core?.ProjectedNegativeLiteralCount.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                core?.ProjectedStopLiteralCount.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                core?.ProjectedStopPositiveLiteralCount.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                core?.ProjectedStopNegativeLiteralCount.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                core?.ProjectedSectionLiteralCount.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                core?.ProjectedSectionPositiveLiteralCount.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                core?.ProjectedSectionNegativeLiteralCount.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                core?.ProjectedStationEventLiteralCount.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                core?.ProjectedStationEventPositiveLiteralCount.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                core?.ProjectedStationEventNegativeLiteralCount.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                core?.EndpointOccupationSeconds.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                core is null ? string.Empty : core.Attempted ? "True" : "False",
                core is null ? string.Empty : core.Accepted ? "True" : "False",
                core is null ? string.Empty : core.Retained ? "True" : "False",
                Escape(core?.TrialOutcome ?? string.Empty),
                core?.AcceptedRecourseWindowSeconds.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                core?.RecourseAttemptCount.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                core?.FixedTemporalEventCount.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                core?.BoundedTemporalEventCount.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                Escape(core?.CertificateReason ?? string.Empty)));
        }
    }

    private static void WriteCutAuditMarkdown(IReadOnlyList<CheckerCutAuditRecord> audit, string path)
    {
        CheckerCutAuditSummary summary = CheckerCutAuditBuilder.Summarize(audit);
        StringBuilder builder = new();
        builder.AppendLine("# Checker Loop Cutting Audit");
        builder.AppendLine();
        builder.AppendLine("Structural rows are evaluated at batch level; they do not claim a marginal effect for each edge.");
        builder.AppendLine();
        builder.AppendLine($"- retained_structural_batches: `{summary.RetainedStructuralBatchCount}`");
        builder.AppendLine($"- rejected_structural_batches: `{summary.RejectedStructuralBatchCount}`");
        builder.AppendLine($"- accepted_heuristic_cuts: `{summary.AcceptedHeuristicCutCount}`");
        builder.AppendLine($"- rejected_heuristic_trials: `{summary.RejectedHeuristicTrialCount}`");
        builder.AppendLine($"- unknown_heuristic_trials: `{summary.UnknownHeuristicTrialCount}`");
        builder.AppendLine($"- architectural_certificates: `{summary.CertificateCount}`");
        builder.AppendLine();
        builder.AppendLine("| iter | class/source | candidate/batch | station/core/projection/trains/requirements/minimality | literals (+/-; stop; section; event) | endpoint_s | attempted/accepted/retained | outcome/certificate | raw | root | episodes | adaptive | recourse | edge delta | decision |");
        builder.AppendLine("|---:|---|---|---|---|---:|---|---|---|---|---|---|---|---|---|");
        foreach (CheckerCutAuditRecord item in audit)
        {
            StationCoreCutDiagnostic? core = item.StationCore;
            string coreIdentity = core is null
                ? "-"
                : $"{EscapeMarkdown(core.StationName)} / {EscapeMarkdown(core.CoreId)} / {EscapeMarkdown(core.ProjectionId)} / {EscapeMarkdown(FormatList(core.CoreTrainIndices))} / {EscapeMarkdown(FormatList(core.CoreRequirementIds))} / {EscapeMarkdown(core.CoreMinimalityStatus)}/{core.CoreReductionExploredNodes.ToString(CultureInfo.InvariantCulture)}";
            string literalSummary = core is null
                ? "-"
                : $"{core.ProjectedLiteralCount} ({core.ProjectedPositiveLiteralCount}/{core.ProjectedNegativeLiteralCount}); {core.ProjectedStopLiteralCount}; {core.ProjectedSectionLiteralCount}; {core.ProjectedStationEventLiteralCount}";
            string retentionSummary = core is null
                ? "-"
                : $"{core.Attempted}/{core.Accepted}/{core.Retained}";
            string outcomeSummary = core is null
                ? item.OutcomeLabel
                : $"{EscapeMarkdown(item.OutcomeLabel)} / {EscapeMarkdown(core.CertificateReason)}";
            builder.AppendLine($"| {item.IterationIndex} | {item.FeedbackClass}/{EscapeMarkdown(item.SourceFamily)} | {EscapeMarkdown(item.CandidateId)} / {EscapeMarkdown(item.BatchId)} | {coreIdentity} | {literalSummary} | {(core?.EndpointOccupationSeconds.ToString(CultureInfo.InvariantCulture) ?? "-")} | {retentionSummary} | {outcomeSummary} | {FormatDelta(item.RawConflictsBefore, item.RawConflictsAfter)} | {FormatDelta(item.RootConflictsBefore, item.RootConflictsAfter)} | {FormatDelta(item.EpisodeConflictsBefore, item.EpisodeConflictsAfter)} | B={item.AdaptiveBatchSize}, P={item.AdaptiveRepairPadding}, {EscapeMarkdown(item.AdaptivePolicyReason)} | W={item.AcceptedRecourseWindowSeconds}s, tries={item.RecourseAttemptCount}, total={item.TotalRetimingSeconds:F1}s, max={item.MaximumRetimingSeconds:F1}s | S {item.ActiveSectionEdgesBefore}->{item.ActiveSectionEdgesAfter}; E {item.ActiveStationEdgesBefore}->{item.ActiveStationEdgesAfter} | {EscapeMarkdown(item.Decision)} |");
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static string FormatDelta(int? before, int? after) =>
        before.HasValue ? $"{before.Value}->{(after?.ToString(CultureInfo.InvariantCulture) ?? "?")}" : "-";

    private static void WriteTrialDiagnosticsCsv(IReadOnlyList<TrialCutDiagnostic> diagnostics, string path)
    {
        using StreamWriter writer = new(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        writer.WriteLine("iteration_index,trial_index,cut_id,station_name,source_family,outer_global_conflicts,outer_focused_conflicts,working_global_conflicts,working_focused_conflicts,trial_global_conflicts,trial_focused_conflicts,has_incumbent,solver_status,accepted,decision,working_canonical_conflicts,trial_canonical_conflicts,working_root_conflicts,trial_root_conflicts,working_conflicted_stations,trial_conflicted_stations,working_objective,trial_objective,active_section_edge_count,active_station_event_edge_count,station_core_id,projection_id,core_train_indices,core_requirement_ids,core_requirement_count,core_minimality_status,core_reduction_explored_nodes,projected_literal_count,projected_positive_literal_count,projected_negative_literal_count,projected_stop_literal_count,projected_stop_positive_literal_count,projected_stop_negative_literal_count,projected_section_literal_count,projected_section_positive_literal_count,projected_section_negative_literal_count,projected_station_event_literal_count,projected_station_event_positive_literal_count,projected_station_event_negative_literal_count,endpoint_occupation_seconds,attempted,retained,trial_outcome,accepted_recourse_window_seconds,recourse_attempt_count,fixed_temporal_event_count,bounded_temporal_event_count,certificate_reason");

        foreach (TrialCutDiagnostic item in diagnostics)
        {
            StationCoreCutDiagnostic? core = item.StationCore;
            writer.WriteLine(string.Join(
                ',',
                item.IterationIndex.ToString(CultureInfo.InvariantCulture),
                item.TrialIndex.ToString(CultureInfo.InvariantCulture),
                Escape(item.CutId),
                Escape(item.StationName),
                Escape(item.SourceFamily),
                item.OuterGlobalConflicts.ToString(CultureInfo.InvariantCulture),
                item.OuterFocusedConflicts.ToString(CultureInfo.InvariantCulture),
                item.WorkingGlobalConflicts.ToString(CultureInfo.InvariantCulture),
                item.WorkingFocusedConflicts.ToString(CultureInfo.InvariantCulture),
                item.TrialGlobalConflicts?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                item.TrialFocusedConflicts?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                item.HasIncumbent.HasValue ? item.HasIncumbent.Value ? "True" : "False" : string.Empty,
                Escape(item.SolverStatus),
                item.Accepted ? "True" : "False",
                Escape(item.Decision),
                item.WorkingCanonicalConflicts?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                item.TrialCanonicalConflicts?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                item.WorkingRootConflicts?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                item.TrialRootConflicts?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                item.WorkingConflictedStations?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                item.TrialConflictedStations?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                item.WorkingObjective?.ToString("F6", CultureInfo.InvariantCulture) ?? string.Empty,
                item.TrialObjective?.ToString("F6", CultureInfo.InvariantCulture) ?? string.Empty,
                item.ActiveSectionEdgeCount.ToString(CultureInfo.InvariantCulture),
                item.ActiveStationEventEdgeCount.ToString(CultureInfo.InvariantCulture),
                Escape(core?.CoreId ?? string.Empty),
                Escape(core?.ProjectionId ?? string.Empty),
                Escape(FormatList(core?.CoreTrainIndices)),
                Escape(FormatList(core?.CoreRequirementIds)),
                core?.CoreRequirementIds.Count.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                Escape(core?.CoreMinimalityStatus ?? string.Empty),
                core?.CoreReductionExploredNodes.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                core?.ProjectedLiteralCount.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                core?.ProjectedPositiveLiteralCount.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                core?.ProjectedNegativeLiteralCount.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                core?.ProjectedStopLiteralCount.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                core?.ProjectedStopPositiveLiteralCount.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                core?.ProjectedStopNegativeLiteralCount.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                core?.ProjectedSectionLiteralCount.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                core?.ProjectedSectionPositiveLiteralCount.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                core?.ProjectedSectionNegativeLiteralCount.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                core?.ProjectedStationEventLiteralCount.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                core?.ProjectedStationEventPositiveLiteralCount.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                core?.ProjectedStationEventNegativeLiteralCount.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                core?.EndpointOccupationSeconds.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                core is null ? string.Empty : core.Attempted ? "True" : "False",
                core is null ? string.Empty : core.Retained ? "True" : "False",
                Escape(core?.TrialOutcome ?? string.Empty),
                core?.AcceptedRecourseWindowSeconds.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                core?.RecourseAttemptCount.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                core?.FixedTemporalEventCount.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                core?.BoundedTemporalEventCount.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                Escape(core?.CertificateReason ?? string.Empty)));
        }
    }

    private static void WriteTrialDiagnosticsMarkdown(IReadOnlyList<TrialCutDiagnostic> diagnostics, string path)
    {
        StringBuilder builder = new();
        builder.AppendLine("# Checker Loop Trial Diagnostics");
        builder.AppendLine();
        builder.AppendLine($"- trial_count: `{diagnostics.Count(item => item.StationCore is not { Attempted: false })}`");
        builder.AppendLine($"- certificate_count: `{diagnostics.Count(item => item.StationCore is { Attempted: false })}`");
        builder.AppendLine();
        builder.AppendLine("| iter | trial | station | source | core | projection | trains | requirements | minimality/nodes | literals (+/-) | stop (+/-) | section (+/-) | station_event (+/-) | endpoint_s | attempted | has_incumbent | accepted | retained | objectives | conflicts working/trial | recourse | outcome | certificate | decision |");
        builder.AppendLine("|---:|---:|---|---|---|---|---|---|---|---|---|---|---|---:|---|---|---|---|---|---|---|---|---|---|");

        foreach (TrialCutDiagnostic item in diagnostics)
        {
            StationCoreCutDiagnostic? core = item.StationCore;
            builder.AppendLine($"| {item.IterationIndex} | {item.TrialIndex} | {EscapeMarkdown(item.StationName)} | {EscapeMarkdown(item.SourceFamily)} | {EscapeMarkdown(core?.CoreId ?? "-")} | {EscapeMarkdown(core?.ProjectionId ?? "-")} | {EscapeMarkdown(FormatList(core?.CoreTrainIndices))} | {EscapeMarkdown(FormatList(core?.CoreRequirementIds))} | {EscapeMarkdown(core is null ? "-" : $"{core.CoreMinimalityStatus}/{core.CoreReductionExploredNodes.ToString(CultureInfo.InvariantCulture)}")} | {FormatLiteralCounts(core?.ProjectedLiteralCount, core?.ProjectedPositiveLiteralCount, core?.ProjectedNegativeLiteralCount)} | {FormatLiteralCounts(core?.ProjectedStopLiteralCount, core?.ProjectedStopPositiveLiteralCount, core?.ProjectedStopNegativeLiteralCount)} | {FormatLiteralCounts(core?.ProjectedSectionLiteralCount, core?.ProjectedSectionPositiveLiteralCount, core?.ProjectedSectionNegativeLiteralCount)} | {FormatLiteralCounts(core?.ProjectedStationEventLiteralCount, core?.ProjectedStationEventPositiveLiteralCount, core?.ProjectedStationEventNegativeLiteralCount)} | {(core?.EndpointOccupationSeconds.ToString(CultureInfo.InvariantCulture) ?? "-")} | {(core?.Attempted.ToString() ?? "-")} | {(item.HasIncumbent?.ToString() ?? "-")} | {item.Accepted} | {(core?.Retained.ToString() ?? "-")} | {(item.WorkingObjective?.ToString("F3", CultureInfo.InvariantCulture) ?? "-")}/{(item.TrialObjective?.ToString("F3", CultureInfo.InvariantCulture) ?? "-")} | raw {item.WorkingGlobalConflicts}/{(item.TrialGlobalConflicts?.ToString(CultureInfo.InvariantCulture) ?? "-")}; root {(item.WorkingRootConflicts?.ToString(CultureInfo.InvariantCulture) ?? "-")}/{(item.TrialRootConflicts?.ToString(CultureInfo.InvariantCulture) ?? "-")} | W={(core?.AcceptedRecourseWindowSeconds.ToString(CultureInfo.InvariantCulture) ?? "-")}s, tries={(core?.RecourseAttemptCount.ToString(CultureInfo.InvariantCulture) ?? "-")}, fixed={(core?.FixedTemporalEventCount.ToString(CultureInfo.InvariantCulture) ?? "-")}, bounded={(core?.BoundedTemporalEventCount.ToString(CultureInfo.InvariantCulture) ?? "-")} | {EscapeMarkdown(core?.TrialOutcome ?? "-")} | {EscapeMarkdown(core?.CertificateReason ?? "-")} | {EscapeMarkdown(item.Decision)} |");
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static void WriteFeedbackFamilySummaryCsv(IReadOnlyList<TrialCutDiagnostic> diagnostics, string path)
    {
        using StreamWriter writer = new(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        writer.WriteLine("source_family,trial_count,accepted_count,acceptance_rate,timeout_no_incumbent_count,infeasible_count,focused_improvement_count,global_regression_count,avg_global_delta,avg_focused_delta");

        foreach (FeedbackFamilySummary item in BuildFeedbackFamilySummaries(diagnostics))
        {
            writer.WriteLine(string.Join(
                ',',
                Escape(item.SourceFamily),
                item.TrialCount.ToString(CultureInfo.InvariantCulture),
                item.AcceptedCount.ToString(CultureInfo.InvariantCulture),
                item.AcceptanceRate.ToString("F6", CultureInfo.InvariantCulture),
                item.TimeoutNoIncumbentCount.ToString(CultureInfo.InvariantCulture),
                item.InfeasibleCount.ToString(CultureInfo.InvariantCulture),
                item.FocusedImprovementCount.ToString(CultureInfo.InvariantCulture),
                item.GlobalRegressionCount.ToString(CultureInfo.InvariantCulture),
                item.AverageGlobalDelta?.ToString("F6", CultureInfo.InvariantCulture) ?? string.Empty,
                item.AverageFocusedDelta?.ToString("F6", CultureInfo.InvariantCulture) ?? string.Empty));
        }
    }

    private static void WriteFeedbackFamilySummaryMarkdown(IReadOnlyList<TrialCutDiagnostic> diagnostics, string path)
    {
        StringBuilder builder = new();
        builder.AppendLine("# Checker Loop Feedback Family Summary");
        builder.AppendLine();
        builder.AppendLine($"- trial_count: `{BuildFeedbackFamilySummaries(diagnostics).Sum(item => item.TrialCount)}`");
        builder.AppendLine();
        builder.AppendLine("| source_family | trials | accepted | acceptance_rate | timeout_no_incumbent | infeasible | focused_improved | global_regression | avg_global_delta | avg_focused_delta |");
        builder.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");

        foreach (FeedbackFamilySummary item in BuildFeedbackFamilySummaries(diagnostics))
        {
            builder.AppendLine($"| `{item.SourceFamily}` | {item.TrialCount} | {item.AcceptedCount} | {item.AcceptanceRate:F3} | {item.TimeoutNoIncumbentCount} | {item.InfeasibleCount} | {item.FocusedImprovementCount} | {item.GlobalRegressionCount} | {(item.AverageGlobalDelta?.ToString("F3", CultureInfo.InvariantCulture) ?? "-")} | {(item.AverageFocusedDelta?.ToString("F3", CultureInfo.InvariantCulture) ?? "-")} |");
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static IReadOnlyList<FeedbackFamilySummary> BuildFeedbackFamilySummaries(IReadOnlyList<TrialCutDiagnostic> diagnostics)
    {
        return diagnostics
            .Where(item => item.StationCore is not { Attempted: false })
            .GroupBy(item => item.SourceFamily, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                List<TrialCutDiagnostic> items = group.ToList();
                List<int> globalDeltas = items
                    .Where(item => item.TrialGlobalConflicts.HasValue)
                    .Select(item => item.TrialGlobalConflicts!.Value - item.WorkingGlobalConflicts)
                    .ToList();
                List<int> focusedDeltas = items
                    .Where(item => item.TrialFocusedConflicts.HasValue)
                    .Select(item => item.TrialFocusedConflicts!.Value - item.WorkingFocusedConflicts)
                    .ToList();

                return new FeedbackFamilySummary(
                    group.Key,
                    items.Count,
                    items.Count(item => item.Accepted),
                    items.Count == 0 ? 0.0 : (double)items.Count(item => item.Accepted) / items.Count,
                    items.Count(item => string.Equals(item.Decision, "deferred_time_limit_no_incumbent", StringComparison.OrdinalIgnoreCase)),
                    items.Count(item => string.Equals(item.Decision, "rejected_infeasible_after_cut", StringComparison.OrdinalIgnoreCase)),
                    items.Count(item => item.TrialFocusedConflicts.HasValue && item.TrialFocusedConflicts.Value < item.WorkingFocusedConflicts),
                    items.Count(item => item.TrialGlobalConflicts.HasValue && item.TrialGlobalConflicts.Value > item.WorkingGlobalConflicts),
                    globalDeltas.Count == 0 ? null : globalDeltas.Average(),
                    focusedDeltas.Count == 0 ? null : focusedDeltas.Average());
            })
            .ToList();
    }

    private static void WriteLocalMicroRepairDiagnostics(
        IReadOnlyList<LocalMicroRepairDiagnostic> diagnostics,
        string csvPath,
        string markdownPath)
    {
        using (StreamWriter writer = new(csvPath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)))
        {
            writer.WriteLine("iteration_index,source_family,station_name,recourse_window_seconds,solver_status,outcome,released_train_count,alternative_count,conditional_constraint_count,global_conflicts_before,global_conflicts_after,focused_conflicts_before,focused_conflicts_after,total_retiming_seconds,maximum_retiming_seconds,selected_alternatives");
            foreach (LocalMicroRepairDiagnostic item in diagnostics)
            {
                string alternatives = string.Join('|', item.SelectedAlternatives.Select(choice =>
                    $"t{choice.TrainIndex}:{choice.RequirementId}:track={choice.TrackName}:in={choice.IncomingRouteId?.ToString(CultureInfo.InvariantCulture) ?? "-"}:out={choice.OutgoingRouteId?.ToString(CultureInfo.InvariantCulture) ?? "-"}"));
                writer.WriteLine(string.Join(',',
                    item.IterationIndex.ToString(CultureInfo.InvariantCulture),
                    "local_micro_feasibility_repair",
                    Escape(item.StationName),
                    item.RecourseWindowSeconds.ToString(CultureInfo.InvariantCulture),
                    Escape(item.SolverStatus),
                    Escape(item.Outcome),
                    item.ReleasedTrainCount.ToString(CultureInfo.InvariantCulture),
                    item.AlternativeCount.ToString(CultureInfo.InvariantCulture),
                    item.ConditionalConstraintCount.ToString(CultureInfo.InvariantCulture),
                    item.GlobalConflictCountBefore.ToString(CultureInfo.InvariantCulture),
                    item.GlobalConflictCountAfter?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                    item.FocusedConflictCountBefore.ToString(CultureInfo.InvariantCulture),
                    item.FocusedConflictCountAfter?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                    item.TotalRetimingSeconds.ToString("F3", CultureInfo.InvariantCulture),
                    item.MaximumRetimingSeconds.ToString("F3", CultureInfo.InvariantCulture),
                    Escape(alternatives)));
            }
        }

        StringBuilder builder = new();
        builder.AppendLine("# Local Micro Feasibility Repair Diagnostics");
        builder.AppendLine();
        builder.AppendLine("- source_family: `local_micro_feasibility_repair`");
        builder.AppendLine($"- attempt_count: `{diagnostics.Count}`");
        builder.AppendLine($"- accepted_count: `{diagnostics.Count(item => string.Equals(item.Outcome, "accepted", StringComparison.OrdinalIgnoreCase))}`");
        builder.AppendLine();
        builder.AppendLine("| iteration | station | window | status | outcome | released | alternatives/conditional | global | focused | retiming total/max | selected | ");
        builder.AppendLine("|---:|---|---:|---|---|---:|---:|---:|---:|---:|---|");
        foreach (LocalMicroRepairDiagnostic item in diagnostics)
        {
            string selected = string.Join("<br>", item.SelectedAlternatives.Select(choice =>
                $"t{choice.TrainIndex} {EscapeMarkdown(choice.TrackName)} ({choice.IncomingRouteId?.ToString(CultureInfo.InvariantCulture) ?? "-"}/{choice.OutgoingRouteId?.ToString(CultureInfo.InvariantCulture) ?? "-"})"));
            builder.AppendLine($"| {item.IterationIndex} | {EscapeMarkdown(item.StationName)} | {item.RecourseWindowSeconds} | {EscapeMarkdown(item.SolverStatus)} | {EscapeMarkdown(item.Outcome)} | {item.ReleasedTrainCount} | {item.AlternativeCount}/{item.ConditionalConstraintCount} | {item.GlobalConflictCountBefore}->{item.GlobalConflictCountAfter?.ToString(CultureInfo.InvariantCulture) ?? "-"} | {item.FocusedConflictCountBefore}->{item.FocusedConflictCountAfter?.ToString(CultureInfo.InvariantCulture) ?? "-"} | {item.TotalRetimingSeconds:F1}/{item.MaximumRetimingSeconds:F1} | {selected} |");
        }
        File.WriteAllText(markdownPath, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static string Escape(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (!value.Contains(',') && !value.Contains('"') && !value.Contains('\n') && !value.Contains('\r'))
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }

    private static string FormatList<T>(IReadOnlyList<T>? values) =>
        values is null ? string.Empty : string.Join('|', values);

    private static string FormatLiteralCounts(int? total, int? positive, int? negative) =>
        total.HasValue ? $"{total.Value} ({positive.GetValueOrDefault()}/{negative.GetValueOrDefault()})" : "-";

    private static string EscapeMarkdown(string value) =>
        value
            .Replace("\r\n", "<br>", StringComparison.Ordinal)
            .Replace("\r", "<br>", StringComparison.Ordinal)
            .Replace("\n", "<br>", StringComparison.Ordinal)
            .Replace("|", "\\|", StringComparison.Ordinal);
}

internal sealed record StationCoreFallbackEvaluation(
    bool Handled,
    bool Accepted,
    BasicMilpModel? Model,
    MasterSolutionSnapshot? Snapshot,
    MicroFeasibilityReport? Report,
    LocalPrecedenceNogoodCut? Cut,
    string TerminationReason,
    int SupportedCandidateCount,
    int TrialCount,
    int RecourseAttemptCount,
    int AcceptedRecourseWindowSeconds,
    int FixedTemporalEventCount,
    int BoundedTemporalEventCount,
    StructuralRepairRetimingMetrics RetimingMetrics,
    string SolverStatus)
{
    internal static StationCoreFallbackEvaluation NotHandled { get; } = new(
        false,
        false,
        null,
        null,
        null,
        null,
        string.Empty,
        0,
        0,
        0,
        0,
        0,
        0,
        new StructuralRepairRetimingMetrics(0.0, 0.0),
        string.Empty);

    internal static StationCoreFallbackEvaluation Terminal(
        string terminationReason,
        int supportedCandidateCount,
        int trialCount,
        string solverStatus,
        int recourseAttemptCount = 0,
        int fixedTemporalEventCount = 0,
        int boundedTemporalEventCount = 0) =>
        new(
            true,
            false,
            null,
            null,
            null,
            null,
            terminationReason,
            supportedCandidateCount,
            trialCount,
            recourseAttemptCount,
            0,
            fixedTemporalEventCount,
            boundedTemporalEventCount,
            new StructuralRepairRetimingMetrics(0.0, 0.0),
            solverStatus);

    internal static StationCoreFallbackEvaluation Accept(
        BasicMilpModel model,
        MasterSolutionSnapshot snapshot,
        MicroFeasibilityReport report,
        LocalPrecedenceNogoodCut cut,
        int supportedCandidateCount,
        int trialCount,
        int recourseAttemptCount,
        int acceptedRecourseWindowSeconds,
        int fixedTemporalEventCount,
        int boundedTemporalEventCount,
        StructuralRepairRetimingMetrics retimingMetrics) =>
        new(
            true,
            true,
            model,
            snapshot,
            report,
            cut,
            string.Empty,
            supportedCandidateCount,
            trialCount,
            recourseAttemptCount,
            acceptedRecourseWindowSeconds,
            fixedTemporalEventCount,
            boundedTemporalEventCount,
            retimingMetrics,
            model.StatusName);
}

internal sealed record StationCoreLiveEvaluatedState(
    MasterSolutionSnapshot Snapshot,
    MicroFeasibilityReport Report,
    int FocusedConflictCount,
    MicroConflictMetrics Metrics);

internal static class CheckerHeuristicTerminationResolver
{
    internal static string Resolve(int screenedCount, int acceptedCount, int unknownCount)
    {
        if (acceptedCount > 0)
        {
            return string.Empty;
        }

        return screenedCount > 0 && unknownCount == screenedCount
            ? "heuristic_trials_unknown_budget_exhausted"
            : "no_acceptable_cut";
    }
}

public sealed record CheckerLoopOptions(
    int MaxIterations,
    int MaxNewCutsPerIteration,
    double TimeLimitSecondsPerSolve,
    double SecondaryTimeLimitSecondsPerSolve = 45.0,
    int SlaveHotspotStationCount = 3,
    int MaxTotalPrecedenceChanges = 6,
    int MaxNonHotspotPrecedenceChanges = 0,
    int HotspotStationCount = 3,
    bool RequireStrictConflictImprovement = true,
    int StructuralRepairNeighborPadding = 2,
    string? FocusStationName = null,
    int MaxHeuristicTrialsPerIteration = 3,
    bool ZeroConflictMode = false,
    IReadOnlyList<int>? ConfiguredRecourseWindowSeconds = null,
    int ExactBatchCap = 20,
    int ExactPerStationCap = 4,
    int ExactPerPairCap = 2,
    int EndpointOccupationSeconds = StationEndpointOccupationPolicy.DefaultSeconds,
    double RepairRunningTimeSupplementRatio = 0.05,
    bool EnableLocalMicroRepair = false,
    bool EnableMicroPromotion = true,
    int MicroPromotionPersistenceRounds = 2,
    int LocalMicroRepairStationLimit = 1,
    double? LocalMicroRepairTimeLimitSeconds = null,
    bool EnableNeighborhoodEscalation = false,
    IReadOnlyList<int>? NeighborhoodEscalationPaddings = null,
    IReadOnlyList<int>? NeighborhoodEscalationWindowSeconds = null,
    int InitialNeighborhoodLevel = 0,
    bool EnableIisGuidedRepair = false,
    int IisMaxExpansionRounds = 5,
    int IisTemporalExpansionStepSeconds = 300,
    int IisMaxExtraTemporalSeconds = 1800,
    bool FixedSnapshotMultiIterationMode = false,
    bool EnableFeasibleSingletonGlobalEvaluation = false,
    AdaptiveSingletonOptions? ConfiguredAdaptiveSingletonOptions = null,
    CheckerCoordinationStrategy CoordinationStrategy = CheckerCoordinationStrategy.Legacy,
    int ValidityFirstHotspotMaximumRequirementCount = 40,
    double ValidityFirstTotalRuntimeBudgetSeconds = 3600.0,
    double ValidityFirstFinalValidationReserveSeconds = 120.0,
    int ValidityFirstMaximumFeedbackOnlyRounds = 2,
    int ValidityFirstRecoveryMipFocus = 1,
    double ValidityFirstRecoveryMipGap = 0.05,
    bool EnablePhasedFeasibilityRecovery = false,
    int ValidityFirstRecoveryHotspotMaximumRequirementCount = 80,
    int ValidityFirstRecoveryHotspotMaximumAlternativeCount = 2000,
    int ValidityFirstRecoveryHotspotMaximumEstimatedVariableCount = 200000,
    int ValidityFirstRecoveryHotspotMaximumEstimatedConstraintCount = 500000,
    int ValidityFirstRecoveryHotspotMaximumConflictRelationCount = 150000,
    double ValidityFirstIisTimeLimitSeconds = 10.0,
    double ValidityFirstIisCumulativeBudgetSeconds = 60.0,
    double ValidityFirstLocalNeighborhoodTimeLimitSeconds = 60.0,
    double ValidityFirstFixedAssignmentRetimingTimeLimitSeconds = 120.0,
    double ValidityFirstConflictLinkedRetimingTimeLimitSeconds = 300.0,
    double ValidityFirstPreviouslyConsumedRuntimeSeconds = 0.0,
    string? ValidityFirstReplayNeighborhoodInputPath = null,
    string? ValidityFirstReplayHotspotStationName = null,
    RepairNeighborhoodSelectorKind RepairNeighborhoodSelector = RepairNeighborhoodSelectorKind.Legacy,
    RepairActionSelectorKind RepairActionSelector = RepairActionSelectorKind.Legacy,
    bool StopAfterFirstJointFeasible = false,
    MacroCandidateCheckStrategy MacroCandidateCheckStrategy =
        MacroCandidateCheckStrategy.CompleteObjectives,
    MicroCoordinationMode MicroCoordinationMode = MicroCoordinationMode.D0)
{
    public IReadOnlyList<int> RecourseWindowSeconds { get; } =
        ConfiguredRecourseWindowSeconds ?? Array.AsReadOnly([300, 600, 1200, 1800]);

    internal StructuralRepairNeighborhoodEscalationOptions EscalationOptions { get; } =
        StructuralRepairNeighborhoodEscalationOptions.Create(
            EnableNeighborhoodEscalation,
            StructuralRepairNeighborPadding,
            NeighborhoodEscalationPaddings ?? Array.AsReadOnly([4, 6]),
            NeighborhoodEscalationWindowSeconds ?? Array.AsReadOnly([600, 1200, 1800]),
            InitialNeighborhoodLevel);

    internal IisGuidedRepairOptions IisGuidedOptions { get; } =
        IisGuidedRepairOptions.Create(
            EnableIisGuidedRepair,
            IisMaxExpansionRounds,
            IisTemporalExpansionStepSeconds,
            IisMaxExtraTemporalSeconds);

    internal AdaptiveSingletonOptions AdaptiveSingletonOptions { get; } =
        (ConfiguredAdaptiveSingletonOptions ??
         GurobiTimetablingResearch.Optimization.AdaptiveSingletonOptions.Disabled).Validate();
}

public sealed record CheckerLoopIteration(
    int IterationIndex,
    double ObjectiveBefore,
    double ObjectiveAfter,
    bool MicroFeasibleAfter,
    int MicroConflictCountBefore,
    int MicroConflictCountAfter,
    int CandidateCutCount,
    int ScreenedCutCount,
    string SolverStatus,
    string Decision,
    string AcceptedCutId,
    string AcceptedStation,
    int? CanonicalConflictCountBefore = null,
    int? CanonicalConflictCountAfter = null,
    int? RootConflictCountBefore = null,
    int? RootConflictCountAfter = null,
    int? ConflictedStationCountBefore = null,
    int? ConflictedStationCountAfter = null,
    int ActiveSectionEdgeCountBefore = 0,
    int ActiveSectionEdgeCountAfter = 0,
    int ActiveStationEventEdgeCountBefore = 0,
    int ActiveStationEventEdgeCountAfter = 0,
    int TemporaryHeuristicCutCountAfter = 0,
    int? EpisodeConflictCountBefore = null,
    int? EpisodeConflictCountAfter = null,
    int AdaptiveBatchSize = 0,
    int AdaptiveRepairPadding = 0,
    string AdaptivePolicyReason = "",
    string SelectedCandidateSummary = "",
    int AcceptedRecourseWindowSeconds = 0,
    int RecourseAttemptCount = 0,
    int FixedTemporalEventCount = 0,
    int BoundedTemporalEventCount = 0,
    double TotalRetimingSeconds = 0.0,
    double MaximumRetimingSeconds = 0.0,
    int UnseenStructuralCandidateCount = 0,
    bool ZeroConflictMode = false,
    int? ExactConflictCountBefore = null,
    int? ExactConflictCountAfter = null,
    int? AssignmentConflictCountBefore = null,
    int? AssignmentConflictCountAfter = null,
    bool Accepted = false,
    string AcceptedCandidateIds = "",
    string AcceptedRepairType = "",
    bool IisGuidedUsed = false,
    int IisExpansionRounds = 0,
    int? MasterValidatorViolationCount = null,
    string InputSnapshotFingerprint = "",
    string AcceptedSnapshotFingerprint = "",
    string ConflictFingerprintBefore = "",
    string ConflictFingerprintAfter = "",
    bool ContinuityVerified = false);

public sealed record CheckerCutSelectionDiagnostic(
    int IterationIndex,
    string CandidateKey,
    string EpisodeId,
    string ResourceKey,
    int FirstTrainIndex,
    int SecondTrainIndex,
    double Score,
    double PersistenceScore,
    double CoverageScore,
    double PropagationScore,
    double ConflictGraphScore,
    double ScarcityScore,
    int AdaptiveBatchSize,
    int RepairNeighborPadding,
    string AdaptivePolicyReason,
    string StationName = "",
    int RawConflictsCovered = 0);

public sealed record CheckerLoopResult(
    BasicMilpModel FinalModel,
    IReadOnlyList<CheckerLoopIteration> Iterations,
    IReadOnlyList<TrialCutDiagnostic> Diagnostics,
    IReadOnlyList<LocalPrecedenceNogoodCut> ActiveCuts,
    string TerminationReason,
    IReadOnlyList<CheckerCutSelectionDiagnostic>? SelectionDiagnostics = null,
    int EndpointOccupationSeconds = StationEndpointOccupationPolicy.DefaultSeconds,
    IReadOnlyList<LocalMicroRepairDiagnostic>? LocalMicroRepairDiagnostics = null,
    IReadOnlyList<CheckerRecourseStageDiagnostic>? RecourseStageDiagnostics = null,
    IReadOnlyList<CheckerNeighborhoodEscalationDiagnostic>? NeighborhoodEscalationDiagnostics = null,
    MicroFeasibilityReport? C0Report = null,
    MicroFeasibilityReport? C1Report = null,
    int CompletedIterationCount = 0,
    IReadOnlyList<RejectedTrialConflictDelta>? RejectedTrialConflictDeltas = null,
    MasterSolutionSnapshot? WorkingSolution = null,
    MasterSolutionSnapshot? BestDiagnosticSolution = null,
    MasterSolutionSnapshot? BestJointFeasibleSolution = null,
    CheckerCoordinationStrategy CoordinationStrategy = CheckerCoordinationStrategy.Legacy,
    string ConfigurationFingerprint = "",
    IReadOnlyList<ValidatedMicroFeedback>? PermanentMicroFeedback = null) : IDisposable
{
    internal IReadOnlyList<StructuralTrialIisDiagnostic> StructuralTrialIisDiagnostics =>
        FinalModel?.StructuralTrialIisDiagnostics.ToList() ?? new List<StructuralTrialIisDiagnostic>();

    internal IReadOnlyList<StructuralCandidateProbeDiagnostic> StructuralCandidateProbeDiagnostics =>
        FinalModel?.StructuralCandidateProbeDiagnostics.ToList() ?? new List<StructuralCandidateProbeDiagnostic>();

    internal string StructuralCandidateProbeStopReason =>
        FinalModel?.StructuralCandidateProbeStopReason ?? string.Empty;

    internal IReadOnlyList<IisGuidedRepairTraceDiagnostic> IisGuidedRepairTraceDiagnostics =>
        FinalModel?.IisGuidedRepairTraceDiagnostics.ToList() ?? new List<IisGuidedRepairTraceDiagnostic>();

    internal IReadOnlyList<IisDependencyPlanDiagnostic> IisDependencyPlanDiagnostics =>
        FinalModel?.IisDependencyPlanDiagnostics.ToList() ?? new List<IisDependencyPlanDiagnostic>();

    internal IReadOnlyList<RejectedTrialConflictDelta> EffectiveRejectedTrialConflictDeltas =>
        RejectedTrialConflictDeltas ??
        FinalModel?.RejectedTrialConflictDeltas.ToList() ??
        new List<RejectedTrialConflictDelta>();

    internal IReadOnlyList<StructuralSingletonGlobalTrialDiagnostic> StructuralSingletonGlobalTrialDiagnostics =>
        FinalModel?.StructuralSingletonGlobalTrialDiagnostics.ToList() ??
        new List<StructuralSingletonGlobalTrialDiagnostic>();

    public void Dispose()
    {
        FinalModel.Dispose();
    }
}

internal sealed record TrialCutEvaluation(
    LocalPrecedenceNogoodCut Cut,
    PrecedenceTrustRegion TrustRegion,
    BasicMilpModel Model,
    MasterSolutionSnapshot? Snapshot,
    StationLevelMicroFeasibilitySlaveResult? FocusedSlaveResult,
    MicroFeasibilityReport? Report,
    int? TrialGlobalConflictCount,
    int? TrialFocusedConflictCount,
    bool IsAccepted,
    string AcceptanceReason) : IDisposable
{
    public bool HasIncumbent => Snapshot is not null;

    public void Dispose()
    {
        Model.Dispose();
    }
}

public sealed record TrialCutDiagnostic(
    int IterationIndex,
    int TrialIndex,
    string CutId,
    string StationName,
    string SourceFamily,
    int OuterGlobalConflicts,
    int OuterFocusedConflicts,
    int WorkingGlobalConflicts,
    int WorkingFocusedConflicts,
    int? TrialGlobalConflicts,
    int? TrialFocusedConflicts,
    bool? HasIncumbent,
    string SolverStatus,
    bool Accepted,
    string Decision,
    int? WorkingCanonicalConflicts = null,
    int? TrialCanonicalConflicts = null,
    int? WorkingRootConflicts = null,
    int? TrialRootConflicts = null,
    int? WorkingConflictedStations = null,
    int? TrialConflictedStations = null,
    double? WorkingObjective = null,
    double? TrialObjective = null,
    int ActiveSectionEdgeCount = 0,
    int ActiveStationEventEdgeCount = 0,
    StationCoreCutDiagnostic? StationCore = null);

public sealed record StationCoreCutDiagnostic(
    string StationName,
    string CoreId,
    string ProjectionId,
    IReadOnlyList<int> CoreTrainIndices,
    IReadOnlyList<string> CoreRequirementIds,
    string CoreMinimalityStatus,
    long CoreReductionExploredNodes,
    int ProjectedLiteralCount,
    int ProjectedPositiveLiteralCount,
    int ProjectedNegativeLiteralCount,
    int ProjectedStopLiteralCount,
    int ProjectedStopPositiveLiteralCount,
    int ProjectedStopNegativeLiteralCount,
    int ProjectedSectionLiteralCount,
    int ProjectedSectionPositiveLiteralCount,
    int ProjectedSectionNegativeLiteralCount,
    int ProjectedStationEventLiteralCount,
    int ProjectedStationEventPositiveLiteralCount,
    int ProjectedStationEventNegativeLiteralCount,
    int EndpointOccupationSeconds,
    bool Attempted,
    bool Accepted,
    bool Retained,
    string TrialOutcome,
    int AcceptedRecourseWindowSeconds,
    int RecourseAttemptCount,
    int FixedTemporalEventCount,
    int BoundedTemporalEventCount,
    string CertificateReason);

internal static class StationCoreCutDiagnosticMapper
{
    internal static StationCoreCutDiagnostic Map(
        StationCoreProjection projection,
        StationInfeasibleCore core,
        int endpointOccupationSeconds,
        bool attempted,
        bool accepted,
        bool retained,
        string trialOutcome,
        int acceptedRecourseWindowSeconds,
        int recourseAttemptCount,
        int fixedTemporalEventCount,
        int boundedTemporalEventCount,
        string certificateReason)
    {
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(core);

        static int CountValue(IReadOnlyDictionary<string, int> values, int expected) =>
            values.Count(item => item.Value == expected);

        int stopPositiveCount = CountValue(projection.StopLiteralsByStopId, 1);
        int stopNegativeCount = CountValue(projection.StopLiteralsByStopId, 0);
        int sectionPositiveCount = CountValue(projection.SectionLiteralsByKey, 1);
        int sectionNegativeCount = CountValue(projection.SectionLiteralsByKey, 0);
        int stationEventPositiveCount = CountValue(projection.StationEventLiteralsByKey, 1);
        int stationEventNegativeCount = CountValue(projection.StationEventLiteralsByKey, 0);

        return new StationCoreCutDiagnostic(
            projection.StationName,
            projection.CoreId,
            projection.ProjectionId,
            core.TrainIndices.Distinct().OrderBy(value => value).ToList(),
            core.RequirementIds.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToList(),
            core.MinimalityStatus.ToString(),
            core.ReductionExploredNodes,
            projection.LiteralCount,
            stopPositiveCount + sectionPositiveCount + stationEventPositiveCount,
            stopNegativeCount + sectionNegativeCount + stationEventNegativeCount,
            projection.StopLiteralCount,
            stopPositiveCount,
            stopNegativeCount,
            projection.SectionLiteralCount,
            sectionPositiveCount,
            sectionNegativeCount,
            projection.StationEventLiteralCount,
            stationEventPositiveCount,
            stationEventNegativeCount,
            endpointOccupationSeconds,
            attempted,
            accepted,
            retained,
            trialOutcome,
            acceptedRecourseWindowSeconds,
            recourseAttemptCount,
            fixedTemporalEventCount,
            boundedTemporalEventCount,
            certificateReason);
    }
}

internal static class StationCoreAuditDiagnosticFactory
{
    internal static TrialCutDiagnostic BuildUnsupported(
        int iterationIndex,
        StationCoreProjection projection,
        StationInfeasibleCore core,
        int endpointOccupationSeconds,
        int outerGlobalConflicts,
        int focusedConflicts,
        int workingCanonicalConflicts,
        int workingRootConflicts,
        int workingConflictedStations,
        double workingObjective,
        int activeSectionEdgeCount,
        int activeStationEventEdgeCount)
    {
        string certificateReason = projection.CertificateReason ??
            StationCoreProjectedCutBuilder.InsufficientMasterSupportCertificate;
        return new TrialCutDiagnostic(
            iterationIndex,
            0,
            string.Empty,
            projection.StationName,
            projection.SourceFamily,
            outerGlobalConflicts,
            focusedConflicts,
            outerGlobalConflicts,
            focusedConflicts,
            null,
            null,
            null,
            string.Empty,
            false,
            certificateReason,
            workingCanonicalConflicts,
            null,
            workingRootConflicts,
            null,
            workingConflictedStations,
            null,
            workingObjective,
            null,
            activeSectionEdgeCount,
            activeStationEventEdgeCount,
            StationCoreCutDiagnosticMapper.Map(
                projection,
                core,
                endpointOccupationSeconds,
                attempted: false,
                accepted: false,
                retained: false,
                trialOutcome: "unsupported",
                acceptedRecourseWindowSeconds: 0,
                recourseAttemptCount: 0,
                fixedTemporalEventCount: 0,
                boundedTemporalEventCount: 0,
                certificateReason));
    }
}

internal sealed record FeedbackFamilySummary(
    string SourceFamily,
    int TrialCount,
    int AcceptedCount,
    double AcceptanceRate,
    int TimeoutNoIncumbentCount,
    int InfeasibleCount,
    int FocusedImprovementCount,
    int GlobalRegressionCount,
    double? AverageGlobalDelta,
    double? AverageFocusedDelta);
