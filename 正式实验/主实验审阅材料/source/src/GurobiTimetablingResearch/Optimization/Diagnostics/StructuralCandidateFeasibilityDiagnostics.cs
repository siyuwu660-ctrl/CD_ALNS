// 结构候选可行性诊断器在冻结的接受态上构建受控探测模型，分别判断候选本身、邻域和依赖释放是否足以获得可行解。
// 模型计划明确固定项与释放项，结果分类区分结构不可行、预算不足和可修复，为候选筛选提供独立证据。
using Gurobi;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

internal sealed record StructuralCandidateEvidence(
    string CandidateType,
    string StationName,
    string SectionName,
    string FirstEvent,
    string SecondEvent,
    string MicroClassification,
    double? RealizedGapSeconds,
    double? RequiredGapSeconds,
    double? HeadwayDeficitSeconds);

internal static class StructuralCandidateEvidenceFactory
{
    internal static StructuralCandidateEvidence Create(
        MicroConflictExplanation conflict,
        string candidateType,
        string resource)
    {
        double? realized = ResolveEvidenceNumber(conflict.Evidence, "realized_gap");
        double? required = ResolveEvidenceNumber(conflict.Evidence, "required_gap");
        return new StructuralCandidateEvidence(
            candidateType,
            conflict.StationName,
            candidateType == "section" ? resource : conflict.FirstSectionName ?? string.Empty,
            conflict.FirstEventSide?.ToString() ?? string.Empty,
            conflict.SecondEventSide?.ToString() ?? string.Empty,
            string.Equals(conflict.ConflictKind, "exact_headway_infeasible", StringComparison.OrdinalIgnoreCase)
                ? "UnavoidableConflict"
                : conflict.ConflictKind,
            realized,
            required,
            realized.HasValue && required.HasValue ? Math.Max(0.0, required.Value - realized.Value) : null);
    }

    private static double? ResolveEvidenceNumber(string evidence, string key)
    {
        string prefix = key + "=";
        string? token = evidence.Split(',', StringSplitOptions.TrimEntries)
            .FirstOrDefault(item => item.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return token is not null && double.TryParse(
            token[prefix.Length..],
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out double value)
            ? value
            : null;
    }
}

internal enum StructuralCandidateProbeOutcome
{
    Feasible,
    ProvenInfeasible,
    Unknown
}

internal static class StructuralCandidateProbeOutcomeClassifier
{
    internal static StructuralCandidateProbeOutcome Classify(int statusCode, int solutionCount)
    {
        if (solutionCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(solutionCount));
        }

        return solutionCount > 0
            ? StructuralCandidateProbeOutcome.Feasible
            : statusCode == GRB.Status.INFEASIBLE
                ? StructuralCandidateProbeOutcome.ProvenInfeasible
                : StructuralCandidateProbeOutcome.Unknown;
    }
}

internal static class StructuralFailedTrialDiagnosticTrigger
{
    internal static bool ShouldRun(
        RepairDecisionLevel decisionLevel,
        int currentWindowSeconds,
        int largestWindowSeconds,
        bool isLastNeighborhood,
        int solverStatusCode,
        int solutionCount)
    {
        if (currentWindowSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(currentWindowSeconds));
        }
        if (largestWindowSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(largestWindowSeconds));
        }
        if (solutionCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(solutionCount));
        }

        return decisionLevel == RepairDecisionLevel.FlexibleStop &&
               currentWindowSeconds == largestWindowSeconds &&
               isLastNeighborhood &&
               solverStatusCode == GRB.Status.INFEASIBLE &&
               solutionCount == 0;
    }
}

internal enum StructuralCandidateProbeMode
{
    Individual,
    Incremental
}

internal sealed record StructuralCandidateProbeRequest(
    string ProbeId,
    StructuralCandidateProbeMode Mode,
    IReadOnlyList<HeadwayConflictCandidate> Candidates,
    double TimeLimitSeconds);

internal sealed record StructuralCandidateProbeExecution(
    StructuralCandidateProbeOutcome Outcome,
    int SolverStatusCode,
    string SolverStatusName,
    double RuntimeSeconds,
    int SolutionCount,
    IReadOnlyList<StructuralTrialIisDiagnostic> IisDiagnostics,
    IisModelDependencyCatalog? DependencyCatalog = null);

internal sealed record StructuralCandidateProbeDiagnostic(
    StructuralCandidateProbeRequest Request,
    StructuralCandidateProbeExecution Execution);

internal sealed record StructuralCandidateFeasibilityRunResult(
    IReadOnlyList<StructuralCandidateProbeDiagnostic> Diagnostics,
    string StopReason);

internal sealed record StructuralCandidateProbeFrozenConfiguration(
    MasterSolutionSnapshot Snapshot,
    RepairDecisionNeighborhood Neighborhood,
    double RepairRunningTimeSupplementRatio,
    int LargestWindowSeconds);

internal sealed record StructuralCandidateProbeModelPlan(
    MasterSolutionSnapshot Snapshot,
    RepairDecisionNeighborhood Neighborhood,
    double RepairRunningTimeSupplementRatio,
    int WindowSeconds,
    double TimeLimitSeconds,
    IReadOnlyList<SectionConflictEdgeRequest> SectionEdges,
    IReadOnlyList<StationEventConflictCandidate> StationEdges);

internal static class StructuralCandidateProbeModelPlanBuilder
{
    internal static StructuralCandidateProbeModelPlan Build(
        StructuralCandidateProbeRequest request,
        StructuralCandidateProbeFrozenConfiguration configuration,
        IReadOnlyList<SectionConflictEdgeRequest> baselineSectionEdges,
        IReadOnlyList<StationEventConflictCandidate> baselineStationEdges)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(baselineSectionEdges);
        ArgumentNullException.ThrowIfNull(baselineStationEdges);
        if (configuration.Neighborhood.DecisionLevel != RepairDecisionLevel.FlexibleStop)
        {
            throw new InvalidOperationException(
                "Structural candidate probes require the failed FlexibleStop neighborhood.");
        }
        if (configuration.LargestWindowSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(configuration.LargestWindowSeconds));
        }
        _ = CheckerRepairSupplementOptions.Create(configuration.RepairRunningTimeSupplementRatio);
        if (Math.Abs(request.TimeLimitSeconds -
                     StructuralCandidateFeasibilityDiagnosticRunner.ProbeTimeLimitSeconds) > 1e-9)
        {
            throw new InvalidOperationException("Structural candidate probes require a 40-second time limit.");
        }

        List<SectionConflictEdgeRequest> sectionEdges = baselineSectionEdges.ToList();
        HashSet<string> sectionKeys = sectionEdges
            .Select(edge => InitialConflictGraphBuilder.BuildCanonicalPairKey(
                edge.SectionName, edge.FirstTrainIndex, edge.SecondTrainIndex))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<StationEventConflictCandidate> stationEdges = baselineStationEdges.ToList();
        HashSet<string> stationKeys = stationEdges
            .Select(BasicMilpModelBuilder.BuildStationEventPrecedenceKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (HeadwayConflictCandidate candidate in request.Candidates)
        {
            if (candidate.SectionEdge is not null)
            {
                string key = InitialConflictGraphBuilder.BuildCanonicalPairKey(
                    candidate.SectionEdge.SectionName,
                    candidate.SectionEdge.FirstTrainIndex,
                    candidate.SectionEdge.SecondTrainIndex);
                if (sectionKeys.Add(key))
                {
                    sectionEdges.Add(candidate.SectionEdge);
                }
            }
            if (candidate.StationEdge is not null)
            {
                string key = BasicMilpModelBuilder.BuildStationEventPrecedenceKey(candidate.StationEdge);
                if (stationKeys.Add(key))
                {
                    stationEdges.Add(candidate.StationEdge);
                }
            }
        }

        return new StructuralCandidateProbeModelPlan(
            configuration.Snapshot,
            configuration.Neighborhood,
            configuration.RepairRunningTimeSupplementRatio,
            configuration.LargestWindowSeconds,
            request.TimeLimitSeconds,
            sectionEdges,
            stationEdges);
    }
}

internal static class StructuralCandidateFeasibilityDiagnosticRunner
{
    internal const double ProbeTimeLimitSeconds = 40.0;
    internal const int MaximumCandidateCount = 40;

    internal static StructuralCandidateFeasibilityRunResult Run(
        IReadOnlyList<HeadwayConflictCandidate> orderedCandidates,
        Func<StructuralCandidateProbeRequest, StructuralCandidateProbeExecution> execute)
    {
        ArgumentNullException.ThrowIfNull(orderedCandidates);
        ArgumentNullException.ThrowIfNull(execute);
        if (orderedCandidates.Count > MaximumCandidateCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(orderedCandidates),
                $"Structural diagnostics support at most {MaximumCandidateCount} candidates.");
        }
        List<HeadwayConflictCandidate> candidates = orderedCandidates.ToList();
        List<StructuralCandidateProbeDiagnostic> diagnostics = [];
        bool hasProvenInfeasibleSingleton = false;

        for (int index = 0; index < candidates.Count; index++)
        {
            StructuralCandidateProbeDiagnostic diagnostic = Execute(
                new StructuralCandidateProbeRequest(
                    $"individual-{index + 1:D3}",
                    StructuralCandidateProbeMode.Individual,
                    [candidates[index]],
                    ProbeTimeLimitSeconds),
                execute);
            diagnostics.Add(diagnostic);
            if (diagnostic.Execution.Outcome == StructuralCandidateProbeOutcome.ProvenInfeasible)
            {
                hasProvenInfeasibleSingleton = true;
            }
        }

        if (hasProvenInfeasibleSingleton)
        {
            return new StructuralCandidateFeasibilityRunResult(
                diagnostics,
                "proven_infeasible_singletons_completed");
        }

        for (int size = 2; size <= candidates.Count; size++)
        {
            StructuralCandidateProbeDiagnostic diagnostic = Execute(
                new StructuralCandidateProbeRequest(
                    $"incremental-{size:D3}",
                    StructuralCandidateProbeMode.Incremental,
                    candidates.Take(size).ToList(),
                    ProbeTimeLimitSeconds),
                execute);
            diagnostics.Add(diagnostic);
            if (diagnostic.Execution.Outcome == StructuralCandidateProbeOutcome.ProvenInfeasible)
            {
                return new StructuralCandidateFeasibilityRunResult(
                    diagnostics,
                    "proven_infeasible_prefix");
            }
        }

        return new StructuralCandidateFeasibilityRunResult(
            diagnostics,
            candidates.Count == 0 ? "no_candidates" : "exhausted_without_proven_infeasibility");
    }

    internal static IReadOnlyList<StructuralCandidateProbeDiagnostic> RunIndividuals(
        IReadOnlyList<HeadwayConflictCandidate> orderedCandidates,
        Func<StructuralCandidateProbeRequest, StructuralCandidateProbeExecution> execute,
        string probeIdPrefix)
    {
        ArgumentNullException.ThrowIfNull(orderedCandidates);
        ArgumentNullException.ThrowIfNull(execute);
        ArgumentException.ThrowIfNullOrWhiteSpace(probeIdPrefix);
        if (orderedCandidates.Count > MaximumCandidateCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(orderedCandidates),
                $"Structural diagnostics support at most {MaximumCandidateCount} candidates.");
        }
        return orderedCandidates
            .Select((candidate, index) => Execute(
                new StructuralCandidateProbeRequest(
                    $"{probeIdPrefix}-{index + 1:D3}",
                    StructuralCandidateProbeMode.Individual,
                    [candidate],
                    ProbeTimeLimitSeconds),
                execute))
            .ToList();
    }

    private static StructuralCandidateProbeDiagnostic Execute(
        StructuralCandidateProbeRequest request,
        Func<StructuralCandidateProbeRequest, StructuralCandidateProbeExecution> execute)
    {
        StructuralCandidateProbeExecution result = execute(request)
            ?? throw new InvalidOperationException("A structural candidate probe returned no execution result.");
        if (result.Outcome != StructuralCandidateProbeOutcome.ProvenInfeasible &&
            result.IisDiagnostics.Count > 0)
        {
            throw new InvalidOperationException(
                "Only a proven-infeasible candidate probe may carry IIS diagnostics.");
        }
        return new StructuralCandidateProbeDiagnostic(request, result);
    }
}
