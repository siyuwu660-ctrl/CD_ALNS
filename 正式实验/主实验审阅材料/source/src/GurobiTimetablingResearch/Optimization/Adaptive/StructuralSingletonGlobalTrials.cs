// 全局单候选试验模块从结构候选中选择少量代表项，在完整实例上独立求解并比较微观质量与主目标。
// 可转移模型租约保证只有获胜试验的资源和快照进入提交计划，其余试验统一释放并保留诊断记录。
namespace GurobiTimetablingResearch.Optimization;

using System.Globalization;
using System.Text;
using GurobiTimetablingResearch.Domain;

internal sealed record StructuralSingletonGlobalTrialCandidate(
    int CandidateIndex,
    string ProbeId,
    HeadwayConflictCandidate Candidate);

internal static class StructuralSingletonGlobalTrialProbeSelector
{
    internal static IReadOnlyList<StructuralSingletonGlobalTrialCandidate> Select(
        IReadOnlyList<StructuralCandidateProbeDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        List<StructuralSingletonGlobalTrialCandidate> selected = [];
        for (int index = 0; index < diagnostics.Count; index++)
        {
            StructuralCandidateProbeDiagnostic diagnostic = diagnostics[index];
            if (diagnostic.Request.Mode != StructuralCandidateProbeMode.Individual ||
                diagnostic.Execution.Outcome != StructuralCandidateProbeOutcome.Feasible)
            {
                continue;
            }
            if (diagnostic.Request.Candidates.Count != 1)
            {
                throw new InvalidOperationException(
                    "An individual structural probe must contain exactly one candidate.");
            }
            selected.Add(new StructuralSingletonGlobalTrialCandidate(
                index,
                diagnostic.Request.ProbeId,
                diagnostic.Request.Candidates[0]));
        }
        return selected;
    }
}

internal sealed record StructuralSingletonGlobalTrialScore(
    int RootConflictCount,
    int EpisodeConflictCount,
    int ExactHeadwayConflictCount,
    int StationAssignmentConflictCount,
    double MaximumAbsoluteRetimingSeconds,
    double TotalAbsoluteRetimingSeconds,
    double TotalRunningTimeSupplementSeconds,
    int CandidateIndex,
    string CandidateKey);

internal static class StructuralSingletonGlobalTrialSelectionPolicy
{
    internal static bool IsStrictImprovement(int rootConflictCountBefore, int rootConflictCountAfter) =>
        rootConflictCountAfter < rootConflictCountBefore;

    internal static int Compare(
        StructuralSingletonGlobalTrialScore left,
        StructuralSingletonGlobalTrialScore right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        int comparison = left.RootConflictCount.CompareTo(right.RootConflictCount);
        if (comparison != 0) return comparison;
        comparison = left.EpisodeConflictCount.CompareTo(right.EpisodeConflictCount);
        if (comparison != 0) return comparison;
        comparison = left.ExactHeadwayConflictCount.CompareTo(right.ExactHeadwayConflictCount);
        if (comparison != 0) return comparison;
        comparison = left.StationAssignmentConflictCount.CompareTo(right.StationAssignmentConflictCount);
        if (comparison != 0) return comparison;
        comparison = left.MaximumAbsoluteRetimingSeconds.CompareTo(right.MaximumAbsoluteRetimingSeconds);
        if (comparison != 0) return comparison;
        comparison = left.TotalAbsoluteRetimingSeconds.CompareTo(right.TotalAbsoluteRetimingSeconds);
        if (comparison != 0) return comparison;
        comparison = left.TotalRunningTimeSupplementSeconds.CompareTo(right.TotalRunningTimeSupplementSeconds);
        if (comparison != 0) return comparison;
        comparison = left.CandidateIndex.CompareTo(right.CandidateIndex);
        return comparison != 0
            ? comparison
            : StringComparer.OrdinalIgnoreCase.Compare(left.CandidateKey, right.CandidateKey);
    }
}

internal sealed record StructuralSingletonGlobalTrialCommitPlan(
    IReadOnlyList<SectionConflictEdgeRequest> SectionEdges,
    IReadOnlyList<StationEventConflictCandidate> StationEdges,
    IReadOnlyList<string> AcceptedCandidateIds);

internal static class StructuralSingletonGlobalTrialCommitPlanner
{
    internal static StructuralSingletonGlobalTrialCommitPlan Build(
        IReadOnlyList<SectionConflictEdgeRequest> baselineSectionEdges,
        IReadOnlyList<StationEventConflictCandidate> baselineStationEdges,
        HeadwayConflictCandidate winner)
    {
        ArgumentNullException.ThrowIfNull(baselineSectionEdges);
        ArgumentNullException.ThrowIfNull(baselineStationEdges);
        ArgumentNullException.ThrowIfNull(winner);
        List<SectionConflictEdgeRequest> sectionEdges = baselineSectionEdges.ToList();
        List<StationEventConflictCandidate> stationEdges = baselineStationEdges.ToList();
        if (winner.SectionEdge is not null)
        {
            string winnerKey = InitialConflictGraphBuilder.BuildCanonicalPairKey(
                winner.SectionEdge.SectionName,
                winner.SectionEdge.FirstTrainIndex,
                winner.SectionEdge.SecondTrainIndex);
            if (!sectionEdges.Any(edge => string.Equals(
                    InitialConflictGraphBuilder.BuildCanonicalPairKey(
                        edge.SectionName, edge.FirstTrainIndex, edge.SecondTrainIndex),
                    winnerKey,
                    StringComparison.OrdinalIgnoreCase)))
            {
                sectionEdges.Add(winner.SectionEdge);
            }
        }
        if (winner.StationEdge is not null)
        {
            string winnerKey = BasicMilpModelBuilder.BuildStationEventPrecedenceKey(winner.StationEdge);
            if (!stationEdges.Any(edge => string.Equals(
                    BasicMilpModelBuilder.BuildStationEventPrecedenceKey(edge),
                    winnerKey,
                    StringComparison.OrdinalIgnoreCase)))
            {
                stationEdges.Add(winner.StationEdge);
            }
        }
        return new StructuralSingletonGlobalTrialCommitPlan(
            sectionEdges,
            stationEdges,
            [winner.CandidateKey]);
    }
}

internal static class StructuralSingletonGlobalTrialExecutionPolicy
{
    internal static bool ShouldRunMicro(int solutionCount, int masterValidatorViolationCount)
    {
        if (solutionCount < 0) throw new ArgumentOutOfRangeException(nameof(solutionCount));
        if (masterValidatorViolationCount < 0)
            throw new ArgumentOutOfRangeException(nameof(masterValidatorViolationCount));
        return solutionCount > 0 && masterValidatorViolationCount == 0;
    }

    internal static bool MayComputeIis(StructuralCandidateProbeOutcome outcome) => false;
}

internal sealed class TransferableDisposableLease<T> : IDisposable where T : class, IDisposable
{
    private T? _value;

    internal TransferableDisposableLease(T value)
    {
        _value = value ?? throw new ArgumentNullException(nameof(value));
    }

    internal bool HasValue => _value is not null;

    internal T Transfer()
    {
        T value = _value ?? throw new InvalidOperationException(
            "The disposable lease has already been transferred or disposed.");
        _value = null;
        return value;
    }

    public void Dispose()
    {
        _value?.Dispose();
        _value = null;
    }
}

internal sealed record StructuralSingletonGlobalTrialDiagnostic(
    int IterationIndex,
    string BatchId,
    string ProbeId,
    int CandidateIndex,
    string CandidateId,
    string CandidateType,
    int TrainI,
    int TrainJ,
    string Station,
    string Section,
    string EventI,
    string EventJ,
    StructuralCandidateProbeOutcome ProbeOutcome,
    string MasterStatus,
    double MasterRuntimeSeconds,
    int SolutionCount,
    bool MasterValidatorFeasible,
    int MasterValidatorViolationCount,
    int RootConflictsBefore,
    int? RootConflictsAfter,
    int EpisodeConflictsBefore,
    int? EpisodeConflictsAfter,
    int ExactConflictsBefore,
    int? ExactConflictsAfter,
    int AssignmentConflictsBefore,
    int? AssignmentConflictsAfter,
    int ResolvedCount,
    int IntroducedCount,
    int PersistentCount,
    int NetChange,
    double TotalRetimingSeconds,
    double MaximumRetimingSeconds,
    double TotalSupplementSeconds,
    double MaximumSupplementSeconds,
    int SectionsUsingSupplement,
    bool StrictImprovement,
    int? SelectionRank,
    bool Selected,
    string Outcome,
    string RejectionReason,
    string InputSnapshotFingerprint,
    string TrialSnapshotFingerprint)
{
    internal StructuralSingletonGlobalTrialScore? Score =>
        RootConflictsAfter.HasValue && EpisodeConflictsAfter.HasValue &&
        ExactConflictsAfter.HasValue && AssignmentConflictsAfter.HasValue
            ? new StructuralSingletonGlobalTrialScore(
                RootConflictsAfter.Value,
                EpisodeConflictsAfter.Value,
                ExactConflictsAfter.Value,
                AssignmentConflictsAfter.Value,
                MaximumRetimingSeconds,
                TotalRetimingSeconds,
                TotalSupplementSeconds,
                CandidateIndex,
                CandidateId)
            : null;
}

internal sealed class StructuralSingletonGlobalTrialExecution : IDisposable
{
    private readonly TransferableDisposableLease<BasicMilpModel>? _modelLease;

    internal StructuralSingletonGlobalTrialExecution(
        StructuralSingletonGlobalTrialDiagnostic diagnostic,
        BasicMilpModel? model = null,
        MasterSolutionSnapshot? snapshot = null,
        MicroFeasibilityReport? microReport = null)
    {
        Diagnostic = diagnostic ?? throw new ArgumentNullException(nameof(diagnostic));
        _modelLease = model is null ? null : new TransferableDisposableLease<BasicMilpModel>(model);
        Snapshot = snapshot;
        MicroReport = microReport;
    }

    internal StructuralSingletonGlobalTrialDiagnostic Diagnostic { get; set; }
    internal MasterSolutionSnapshot? Snapshot { get; }
    internal MicroFeasibilityReport? MicroReport { get; }
    internal bool HasTransferableModel => _modelLease?.HasValue == true;

    internal BasicMilpModel TransferModel() => _modelLease?.Transfer()
        ?? throw new InvalidOperationException("The singleton global trial has no transferable model.");

    public void Dispose() => _modelLease?.Dispose();
}

internal sealed record StructuralSingletonGlobalTrialRunResult(
    IReadOnlyList<StructuralSingletonGlobalTrialDiagnostic> Diagnostics,
    StructuralSingletonGlobalTrialExecution? Winner,
    string TerminationReason) : IDisposable
{
    public void Dispose() => Winner?.Dispose();
}

internal static class StructuralSingletonGlobalTrialRunner
{
    internal static StructuralSingletonGlobalTrialRunResult Run(
        IReadOnlyList<StructuralCandidateProbeDiagnostic> probeDiagnostics,
        Func<StructuralSingletonGlobalTrialCandidate, StructuralSingletonGlobalTrialExecution> execute,
        Func<StructuralSingletonGlobalTrialExecution, bool>? qualityBudgetEligible = null)
    {
        ArgumentNullException.ThrowIfNull(probeDiagnostics);
        ArgumentNullException.ThrowIfNull(execute);
        IReadOnlyList<StructuralSingletonGlobalTrialCandidate> candidates =
            StructuralSingletonGlobalTrialProbeSelector.Select(probeDiagnostics);
        List<StructuralSingletonGlobalTrialExecution> executions = [];
        try
        {
            foreach (StructuralSingletonGlobalTrialCandidate candidate in candidates)
            {
                executions.Add(execute(candidate) ?? throw new InvalidOperationException(
                    "A singleton global trial executor returned no result."));
            }
        }
        catch
        {
            foreach (StructuralSingletonGlobalTrialExecution execution in executions)
            {
                execution.Dispose();
            }
            throw;
        }

        qualityBudgetEligible ??= static _ => true;
        HashSet<StructuralSingletonGlobalTrialExecution> budgetRejected = executions
            .Where(item => item.Diagnostic.StrictImprovement &&
                item.Diagnostic.Score is not null && item.HasTransferableModel &&
                item.Snapshot is not null && item.MicroReport is not null &&
                !qualityBudgetEligible(item))
            .ToHashSet();
        List<StructuralSingletonGlobalTrialExecution> eligible = executions
            .Where(item => item.Diagnostic.StrictImprovement && item.Diagnostic.Score is not null &&
                           item.HasTransferableModel && item.Snapshot is not null && item.MicroReport is not null &&
                           !budgetRejected.Contains(item))
            .OrderBy(item => item.Diagnostic.Score!, Comparer<StructuralSingletonGlobalTrialScore>.Create(
                StructuralSingletonGlobalTrialSelectionPolicy.Compare))
            .ToList();
        StructuralSingletonGlobalTrialExecution? winner = eligible.FirstOrDefault();
        Dictionary<string, int> rankByCandidate = eligible
            .Select((item, index) => (item.Diagnostic.CandidateId, Rank: index + 1))
            .ToDictionary(item => item.CandidateId, item => item.Rank, StringComparer.OrdinalIgnoreCase);

        foreach (StructuralSingletonGlobalTrialExecution execution in executions)
        {
            bool selected = ReferenceEquals(execution, winner);
            int? rank = rankByCandidate.TryGetValue(execution.Diagnostic.CandidateId, out int value)
                ? value
                : null;
            if (execution.Diagnostic.StrictImprovement)
            {
                bool rejectedByBudget = budgetRejected.Contains(execution);
                execution.Diagnostic = execution.Diagnostic with
                {
                    SelectionRank = rank,
                    Selected = selected,
                    Outcome = rejectedByBudget
                        ? "quality_budget_rejected"
                        : selected
                        ? "selected_strict_improvement"
                        : "dominated_by_better_feasible_singleton",
                    RejectionReason = rejectedByBudget
                        ? "quality_budget_rejected"
                        : selected ? string.Empty : "dominated_by_better_feasible_singleton"
                };
            }
            if (!selected)
            {
                execution.Dispose();
            }
        }

        return new StructuralSingletonGlobalTrialRunResult(
            executions.Select(item => item.Diagnostic).ToList(),
            winner,
            winner is null
                ? budgetRejected.Count > 0 && executions.All(item =>
                    !item.Diagnostic.StrictImprovement || budgetRejected.Contains(item))
                    ? AdaptiveSingletonTerminationReasons.QualityBudget
                    : "feasible_singletons_no_strict_micro_improvement"
                : "feasible_singleton_global_trial_accepted");
    }
}

internal static class StructuralSingletonGlobalTrialReporter
{
    private const string Header = "iteration_index,batch_id,probe_id,candidate_index,candidate_id,candidate_type,train_i,train_j,station,section,event_i,event_j,probe_outcome,master_status,master_runtime_seconds,solution_count,master_validator_feasible,master_validator_violation_count,root_conflicts_before,root_conflicts_after,episode_conflicts_before,episode_conflicts_after,exact_conflicts_before,exact_conflicts_after,assignment_conflicts_before,assignment_conflicts_after,resolved_count,introduced_count,persistent_count,net_change,total_retiming_seconds,maximum_retiming_seconds,total_supplement_seconds,maximum_supplement_seconds,sections_using_supplement,strict_improvement,selection_rank,selected,outcome,rejection_reason,input_snapshot_fingerprint,trial_snapshot_fingerprint";

    internal static void Write(
        IReadOnlyList<StructuralSingletonGlobalTrialDiagnostic> diagnostics,
        string outputDirectory)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        Directory.CreateDirectory(outputDirectory);
        WriteCsv(diagnostics, Path.Combine(outputDirectory, "structural_singleton_global_trials.csv"));
        WriteMarkdown(diagnostics, Path.Combine(outputDirectory, "structural_singleton_global_trials.md"));
    }

    private static void WriteCsv(
        IReadOnlyList<StructuralSingletonGlobalTrialDiagnostic> diagnostics,
        string path)
    {
        using StreamWriter writer = new(path, false, new UTF8Encoding(true));
        writer.WriteLine(Header);
        foreach (StructuralSingletonGlobalTrialDiagnostic item in diagnostics
                     .OrderBy(item => item.IterationIndex)
                     .ThenBy(item => item.CandidateIndex)
                     .ThenBy(item => item.CandidateId, StringComparer.OrdinalIgnoreCase))
        {
            writer.WriteLine(string.Join(',',
                item.IterationIndex, Escape(item.BatchId), Escape(item.ProbeId), item.CandidateIndex,
                Escape(item.CandidateId), item.CandidateType, item.TrainI, item.TrainJ,
                Escape(item.Station), Escape(item.Section), item.EventI, item.EventJ,
                item.ProbeOutcome, Escape(item.MasterStatus), F(item.MasterRuntimeSeconds), item.SolutionCount,
                item.MasterValidatorFeasible, item.MasterValidatorViolationCount,
                item.RootConflictsBefore, N(item.RootConflictsAfter), item.EpisodeConflictsBefore,
                N(item.EpisodeConflictsAfter), item.ExactConflictsBefore, N(item.ExactConflictsAfter),
                item.AssignmentConflictsBefore, N(item.AssignmentConflictsAfter), item.ResolvedCount,
                item.IntroducedCount, item.PersistentCount, item.NetChange, F(item.TotalRetimingSeconds),
                F(item.MaximumRetimingSeconds), F(item.TotalSupplementSeconds),
                F(item.MaximumSupplementSeconds), item.SectionsUsingSupplement, item.StrictImprovement,
                item.SelectionRank?.ToString(CultureInfo.InvariantCulture) ?? string.Empty, item.Selected,
                Escape(item.Outcome), Escape(item.RejectionReason), item.InputSnapshotFingerprint,
                item.TrialSnapshotFingerprint));
        }
    }

    private static void WriteMarkdown(
        IReadOnlyList<StructuralSingletonGlobalTrialDiagnostic> diagnostics,
        string path)
    {
        StringBuilder builder = new();
        builder.AppendLine("# Structural Singleton Global Trials");
        builder.AppendLine();
        builder.AppendLine($"- evaluated_trial_count: `{diagnostics.Count}`");
        builder.AppendLine($"- strict_improvement_count: `{diagnostics.Count(item => item.StrictImprovement)}`");
        string selectedCandidates = string.Join('|', diagnostics
            .Where(item => item.Selected)
            .OrderBy(item => item.IterationIndex)
            .ThenBy(item => item.CandidateId, StringComparer.OrdinalIgnoreCase)
            .Select(item => $"{item.IterationIndex}:{item.CandidateId}"));
        builder.AppendLine($"- selected_candidates: `{selectedCandidates}`");
        builder.AppendLine();
        builder.AppendLine("| rank | candidate | Master | validator | root | episodes | exact | assignment | delta R/I/P | retiming max/total | supplement total | outcome |");
        builder.AppendLine("|---:|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (StructuralSingletonGlobalTrialDiagnostic item in diagnostics
                     .OrderBy(item => item.IterationIndex)
                     .ThenBy(item => item.CandidateIndex)
                     .ThenBy(item => item.CandidateId, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"| {item.SelectionRank?.ToString(CultureInfo.InvariantCulture) ?? "-"} | `{item.CandidateId}` | `{item.MasterStatus}` ({item.SolutionCount}) | {item.MasterValidatorFeasible} ({item.MasterValidatorViolationCount}) | {item.RootConflictsBefore}->{N(item.RootConflictsAfter)} | {item.EpisodeConflictsBefore}->{N(item.EpisodeConflictsAfter)} | {item.ExactConflictsBefore}->{N(item.ExactConflictsAfter)} | {item.AssignmentConflictsBefore}->{N(item.AssignmentConflictsAfter)} | {item.ResolvedCount}/{item.IntroducedCount}/{item.PersistentCount} | {F(item.MaximumRetimingSeconds)}/{F(item.TotalRetimingSeconds)} | {F(item.TotalSupplementSeconds)} | `{item.Outcome}` `{item.RejectionReason}` |");
        }
        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(true));
    }

    private static string F(double value) => value.ToString("F6", CultureInfo.InvariantCulture);
    private static string N(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
    private static string Escape(string value) =>
        !value.Contains(',') && !value.Contains('"') && !value.Contains('\n') && !value.Contains('\r')
            ? value
            : $"\"{value.Replace("\"", "\"\"")}\"";
}
