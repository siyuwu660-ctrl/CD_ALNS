// IIS 依赖闭包运行器反复构建修复模型、提取 IIS、扩张 Overlay 并重试，直到获得 incumbent 或触发终止策略。
// 每次尝试都保留模型结果和新增依赖，使有限预算内的渐进式解冻过程可追踪且不会重复释放同一决策。
namespace GurobiTimetablingResearch.Optimization;

internal sealed record IisGuidedMasterAttempt(
    BasicMilpModel Model,
    StructuralCandidateProbeOutcome Outcome,
    int SolverStatusCode,
    string SolverStatusName,
    double RuntimeSeconds,
    int SolutionCount,
    IReadOnlyList<StructuralTrialIisDiagnostic> IisDiagnostics,
    RepairDecisionReleaseCounts ReleaseCounts);

internal sealed record IisGuidedDependencyClosureRequest(
    HeadwayConflictCandidate Candidate,
    IReadOnlyList<StructuralTrialIisDiagnostic> InitialIisDiagnostics,
    IisModelDependencyCatalog Catalog,
    MasterSolutionSnapshot Snapshot,
    RepairDecisionNeighborhood BaseNeighborhood,
    int BaseWindowSeconds,
    IisGuidedRepairOptions Options);

internal sealed record IisGuidedDependencyClosureResult(
    BasicMilpModel? FeasibleModel,
    IisDependencyOverlay FinalOverlay,
    IReadOnlyList<IisGuidedRepairTraceDiagnostic> Trace,
    IReadOnlyList<IisDependencyPlanDiagnostic> PlanDiagnostics,
    IReadOnlyList<StructuralTrialIisDiagnostic> IisDiagnostics,
    string TerminationReason)
{
    internal bool HasMasterIncumbent => FeasibleModel is not null;
}

internal static class IisGuidedDependencyClosureRunner
{
    internal static IisGuidedDependencyClosureResult Run(
        IisGuidedDependencyClosureRequest request,
        Func<int, IisDependencyOverlay, IisGuidedMasterAttempt> execute)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(execute);
        if (!request.Options.Enabled)
        {
            return new IisGuidedDependencyClosureResult(
                null,
                IisDependencyOverlay.Empty,
                Array.Empty<IisGuidedRepairTraceDiagnostic>(),
                Array.Empty<IisDependencyPlanDiagnostic>(),
                Array.Empty<StructuralTrialIisDiagnostic>(),
                "iis_guided_repair_disabled");
        }

        IisDependencyOverlay overlay = IisDependencyOverlay.Empty;
        IReadOnlyList<StructuralTrialIisDiagnostic> currentIis = request.InitialIisDiagnostics;
        List<IisGuidedRepairTraceDiagnostic> trace = [];
        List<IisDependencyPlanDiagnostic> planDiagnostics = [];
        List<StructuralTrialIisDiagnostic> generatedIis = [];
        for (int round = 1; round <= request.Options.MaxExpansionRounds; round++)
        {
            DependencyExpansionPlan plan = IisGuidedDependencyExpansionPlanner.Build(
                new IisGuidedDependencyExpansionRequest(
                    request.Candidate,
                    currentIis.Select(item => item.Element).ToList(),
                    overlay,
                    request.Catalog,
                    request.Snapshot,
                    request.BaseNeighborhood.DecisionLevel,
                    request.BaseWindowSeconds,
                    round,
                    request.Options));
            planDiagnostics.AddRange(IisGuidedRepairDiagnosticsReporter.FromPlan(
                request.Candidate.CandidateKey, round, plan));
            if (!plan.ProposedReleases.Any(item => item.WillRelease))
            {
                trace.Add(CreateUnsolvedTrace(
                    request, round, overlay, currentIis, plan,
                    "iis_guided_dependency_expansion_exhausted"));
                return new IisGuidedDependencyClosureResult(
                    null, overlay, trace, planDiagnostics, generatedIis,
                    "iis_guided_dependency_expansion_exhausted");
            }

            overlay = overlay.Union(plan);
            IisGuidedMasterAttempt attempt = execute(round, overlay);
            try
            {
                if (attempt.Outcome == StructuralCandidateProbeOutcome.ProvenInfeasible)
                {
                    generatedIis.AddRange(attempt.IisDiagnostics);
                }
                IisGuidedRepairAction action = IisGuidedRepairTransitionPolicy.ResolveAfterMaster(
                    attempt.Outcome, round, request.Options.MaxExpansionRounds);
                string termination = action switch
                {
                    IisGuidedRepairAction.DependencyClosureFeasible => "dependency_closure_feasible",
                    IisGuidedRepairAction.StopUnknown => "iis_guided_dependency_expansion_unknown",
                    IisGuidedRepairAction.StopExhausted => "iis_guided_dependency_expansion_exhausted",
                    _ => string.Empty
                };
                trace.Add(CreateSolvedTrace(request, round, overlay, attempt, plan, termination));
                if (action == IisGuidedRepairAction.DependencyClosureFeasible)
                {
                    return new IisGuidedDependencyClosureResult(
                        attempt.Model, overlay, trace, planDiagnostics, generatedIis,
                        "dependency_closure_feasible");
                }
                if (action == IisGuidedRepairAction.StopUnknown ||
                    action == IisGuidedRepairAction.StopExhausted)
                {
                    attempt.Model.Dispose();
                    return new IisGuidedDependencyClosureResult(
                        null, overlay, trace, planDiagnostics, generatedIis, termination);
                }

                currentIis = attempt.IisDiagnostics;
                attempt.Model.Dispose();
            }
            catch
            {
                attempt.Model.Dispose();
                throw;
            }
        }

        throw new InvalidOperationException("The bounded IIS-guided lifecycle exited without a terminal outcome.");
    }

    private static IisGuidedRepairTraceDiagnostic CreateUnsolvedTrace(
        IisGuidedDependencyClosureRequest request,
        int round,
        IisDependencyOverlay overlay,
        IReadOnlyList<StructuralTrialIisDiagnostic> iis,
        DependencyExpansionPlan plan,
        string termination) => new(
            request.Candidate.CandidateKey,
            round,
            request.BaseNeighborhood.DecisionLevel,
            request.BaseWindowSeconds,
            plan.AdditionalTrainIndices.Count,
            overlay.TemporalEventKeys.Count,
            overlay.SectionPrecedenceKeys.Count,
            overlay.StationEventPrecedenceKeys.Count,
            overlay.FlexibleStopKeys.Count,
            overlay.SupplementSectionKeys.Count,
            iis.Count(item => item.Element.ElementKind == StructuralIisElementKind.LinearConstraint),
            iis.Count(item => item.Element.ElementKind != StructuralIisElementKind.LinearConstraint),
            CountNew(plan, IisDependencyType.SectionPrecedence),
            CountNew(plan, IisDependencyType.StationEventPrecedence),
            CountNew(plan, IisDependencyType.TemporalEvent),
            CountNew(plan, IisDependencyType.FlexibleStop),
            CountNew(plan, IisDependencyType.RunningSupplement),
            plan.EffectiveBlockedDependencies.Count,
            "NOT_SOLVED",
            0.0,
            0,
            null,
            termination);

    private static IisGuidedRepairTraceDiagnostic CreateSolvedTrace(
        IisGuidedDependencyClosureRequest request,
        int round,
        IisDependencyOverlay overlay,
        IisGuidedMasterAttempt attempt,
        DependencyExpansionPlan plan,
        string termination) => new(
            request.Candidate.CandidateKey,
            round,
            request.BaseNeighborhood.DecisionLevel,
            request.BaseWindowSeconds,
            attempt.ReleaseCounts.ReleasedTimeVariableCount == 0
                ? plan.AdditionalTrainIndices.Count
                : request.BaseNeighborhood.ReleasedTrainIndices.Union(plan.AdditionalTrainIndices).Count(),
            attempt.ReleaseCounts.ReleasedTimeVariableCount,
            attempt.ReleaseCounts.ReleasedSectionPrecedenceVariableCount,
            attempt.ReleaseCounts.ReleasedStationEventPrecedenceVariableCount,
            attempt.ReleaseCounts.ReleasedStopVariableCount,
            attempt.ReleaseCounts.ReleasedRunningSupplementVariableCount,
            attempt.IisDiagnostics.Count(item => item.Element.ElementKind == StructuralIisElementKind.LinearConstraint),
            attempt.IisDiagnostics.Count(item => item.Element.ElementKind != StructuralIisElementKind.LinearConstraint),
            CountNew(plan, IisDependencyType.SectionPrecedence),
            CountNew(plan, IisDependencyType.StationEventPrecedence),
            CountNew(plan, IisDependencyType.TemporalEvent),
            CountNew(plan, IisDependencyType.FlexibleStop),
            CountNew(plan, IisDependencyType.RunningSupplement),
            plan.EffectiveBlockedDependencies.Count,
            attempt.SolverStatusName,
            attempt.RuntimeSeconds,
            attempt.SolutionCount,
            null,
            termination);

    private static int CountNew(DependencyExpansionPlan plan, IisDependencyType type) =>
        plan.ProposedReleases.Count(item =>
            item.DependencyType == type && item.WillRelease && !item.WasAlreadyReleased);
}
