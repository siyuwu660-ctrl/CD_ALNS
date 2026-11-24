using System.Globalization;
using Gurobi;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

public enum MacroCandidateCheckStrategy
{
    CompleteObjectives,
    EarlyCheck
}

public static class MacroCandidateCheckStrategyParser
{
    public static MacroCandidateCheckStrategy Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            string.Equals(value, "complete-objectives", StringComparison.OrdinalIgnoreCase))
        {
            return MacroCandidateCheckStrategy.CompleteObjectives;
        }
        if (string.Equals(value, "early-check", StringComparison.OrdinalIgnoreCase))
        {
            return MacroCandidateCheckStrategy.EarlyCheck;
        }
        throw new ArgumentException(
            $"Unsupported Macro candidate strategy '{value}'. Expected complete-objectives or early-check.",
            nameof(value));
    }
}

public enum MacroObjectiveKind
{
    Quality,
    NecessaryViolation
}

public sealed record MacroObjectiveDefinition(
    string Name,
    string Expression,
    int Priority,
    MacroObjectiveKind Kind,
    bool MustBeZeroBeforeFullCheck);

public static class MacroObjectiveCatalog
{
    public static IReadOnlyList<MacroObjectiveDefinition> Definitions { get; } =
    [
        new("global_span", "global_last_arrival - global_first_departure", 2,
            MacroObjectiveKind.Quality, false),
        new("running_time_supplement", "sum(run_supp)", 1,
            MacroObjectiveKind.Quality, false)
    ];

    public static IReadOnlyList<MacroObjectiveDefinition> NecessaryViolationDefinitions =>
        Definitions.Where(item => item.Kind == MacroObjectiveKind.NecessaryViolation).ToList();
}

public static class MacroCandidatePhasePolicy
{
    public static bool IsEarlyCheckPhase(string phase) =>
        string.Equals(phase, "initial_master", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(phase, "initial_macro_full_check_edge_rebuild", StringComparison.OrdinalIgnoreCase);
}

public sealed record MacroCandidateEligibilityAudit(
    bool Passed,
    bool HasIntegerIncumbent,
    bool SnapshotComplete,
    double? ConstraintViolation,
    double? BoundViolation,
    double? IntegerViolation,
    double FeasibilityTolerance,
    double IntegerTolerance,
    int NecessaryViolationVariableCount,
    double? NecessaryViolationAmount,
    int ArrivalValueCount,
    int DepartureValueCount,
    int StopValueCount,
    int SectionPrecedenceValueCount,
    int StationEventPrecedenceValueCount,
    int RunningTimeSupplementValueCount,
    string? SnapshotPath,
    string FailureReason);

public static class MacroCandidateSnapshotAuditor
{
    public static MacroCandidateEligibilityAudit CaptureAndSave(
        BasicMilpModel model,
        string snapshotPath)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotPath);
        int solutionCount = model.Model.Get(GRB.IntAttr.SolCount);
        double feasibilityTolerance = model.Model.Get(GRB.DoubleParam.FeasibilityTol);
        double integerTolerance = model.Model.Get(GRB.DoubleParam.IntFeasTol);
        if (solutionCount <= 0)
        {
            return Empty(false, feasibilityTolerance, integerTolerance, "no_integer_incumbent");
        }

        double constraintViolation = model.Model.Get(GRB.DoubleAttr.ConstrVio);
        double boundViolation = model.Model.Get(GRB.DoubleAttr.BoundVio);
        double integerViolation = model.Model.Get(GRB.DoubleAttr.IntVio);
        MasterSolutionSnapshot snapshot;
        try
        {
            snapshot = MasterSolutionSnapshot.Capture(model);
        }
        catch (Exception exception)
        {
            return Empty(
                true,
                feasibilityTolerance,
                integerTolerance,
                $"snapshot_capture_failed:{exception.GetType().Name}",
                constraintViolation,
                boundViolation,
                integerViolation);
        }

        bool complete =
            HasExactKeys(snapshot.ArrivalSecondsByStopId, model.ArrivalVars.Keys) &&
            HasExactKeys(snapshot.DepartureSecondsByStopId, model.DepartureVars.Keys) &&
            HasExactKeys(snapshot.StopByStopId, model.StopVars.Keys) &&
            HasExactKeys(snapshot.SectionPrecedenceByKey, model.SectionPrecedenceVars.Keys) &&
            HasExactKeys(snapshot.StationEventPrecedenceByKey, model.StationEventPrecedenceVars.Keys) &&
            HasExactKeys(
                snapshot.RunningTimeSupplementSecondsByKey ?? new Dictionary<string, double>(),
                model.RunningTimeSupplementVars.Keys) &&
            snapshot.AdmittedTrainIndices.SetEquals(
                model.Instance.Trains.Select(train => train.TrainIndex)) &&
            double.IsFinite(snapshot.ObjectiveValue) &&
            snapshot.ArrivalSecondsByStopId.Values.All(double.IsFinite) &&
            snapshot.DepartureSecondsByStopId.Values.All(double.IsFinite) &&
            (snapshot.RunningTimeSupplementSecondsByKey?.Values.All(double.IsFinite) ?? true);

        int violationVariableCount = MacroObjectiveCatalog.NecessaryViolationDefinitions.Count;
        double necessaryViolationAmount = 0.0;
        bool numericalFeasible =
            double.IsFinite(constraintViolation) && constraintViolation <= feasibilityTolerance &&
            double.IsFinite(boundViolation) && boundViolation <= feasibilityTolerance &&
            double.IsFinite(integerViolation) && integerViolation <= integerTolerance;
        bool passed = complete && numericalFeasible && necessaryViolationAmount == 0.0;
        string failureReason = passed
            ? string.Empty
            : !complete
                ? "candidate_snapshot_incomplete_or_nonfinite"
                : !numericalFeasible
                    ? "gurobi_solution_quality_exceeds_active_tolerance"
                    : "necessary_violation_amount_nonzero";
        string? savedPath = null;
        if (passed)
        {
            MasterSolutionSnapshotStore.Save(snapshotPath, model.Instance.Meta.InstanceName, snapshot);
            savedPath = Path.GetFullPath(snapshotPath);
        }

        return new MacroCandidateEligibilityAudit(
            passed,
            true,
            complete,
            constraintViolation,
            boundViolation,
            integerViolation,
            feasibilityTolerance,
            integerTolerance,
            violationVariableCount,
            necessaryViolationAmount,
            snapshot.ArrivalSecondsByStopId.Count,
            snapshot.DepartureSecondsByStopId.Count,
            snapshot.StopByStopId.Count,
            snapshot.SectionPrecedenceByKey.Count,
            snapshot.StationEventPrecedenceByKey.Count,
            snapshot.RunningTimeSupplementSecondsByKey?.Count ?? 0,
            savedPath,
            failureReason);
    }

    private static bool HasExactKeys<T>(
        IReadOnlyDictionary<string, T> values,
        IEnumerable<string> expectedKeys) =>
        values.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase)
            .SetEquals(expectedKeys);

    private static MacroCandidateEligibilityAudit Empty(
        bool hasIntegerIncumbent,
        double feasibilityTolerance,
        double integerTolerance,
        string reason,
        double? constraintViolation = null,
        double? boundViolation = null,
        double? integerViolation = null) => new(
            false,
            hasIntegerIncumbent,
            false,
            constraintViolation,
            boundViolation,
            integerViolation,
            feasibilityTolerance,
            integerTolerance,
            MacroObjectiveCatalog.NecessaryViolationDefinitions.Count,
            null,
            0, 0, 0, 0, 0, 0,
            null,
            reason);
}

public sealed record MacroCandidateSolveDisposition(
    string ApplicationStopReason,
    bool CanProceedToFullCheck);

public static class MacroCandidateSolveOutcomeClassifier
{
    public static MacroCandidateSolveDisposition Resolve(
        int statusCode,
        int solutionCount,
        bool auditPassed,
        bool earlyStopRequested,
        bool applicationEarlyStopTriggered = false)
    {
        if (statusCode == GRB.Status.INTERRUPTED)
        {
            return applicationEarlyStopTriggered && solutionCount > 0 && auditPassed
                ? new("saved_candidate_for_full_check", true)
                : new("user_cancelled", false);
        }
        if (statusCode == GRB.Status.INFEASIBLE)
        {
            return new("proven_infeasible", false);
        }
        if (statusCode == GRB.Status.TIME_LIMIT)
        {
            return solutionCount > 0 && auditPassed
                ? new("single_solve_time_limit_with_candidate", true)
                : new("single_solve_time_limit_no_candidate", false);
        }
        if (solutionCount > 0 && !auditPassed)
        {
            return new("candidate_snapshot_or_model_audit_failed", false);
        }
        if (solutionCount > 0 && auditPassed)
        {
            if (earlyStopRequested)
            {
                return new("saved_candidate_for_full_check", true);
            }
            return new("objectives_completed_with_candidate", true);
        }
        return new($"no_candidate_status_{statusCode.ToString(CultureInfo.InvariantCulture)}", false);
    }
}

public sealed record MacroRelationMergeResult(
    int AddedCount,
    int CumulativeCount,
    IReadOnlyList<SectionConflictEdgeRequest> AddedEdges);

public static class MacroRelationAccumulator
{
    public static MacroRelationMergeResult Merge(
        IList<SectionConflictEdgeRequest> activeEdges,
        IReadOnlyList<SectionConflictEdgeRequest> suggestedEdges)
    {
        ArgumentNullException.ThrowIfNull(activeEdges);
        ArgumentNullException.ThrowIfNull(suggestedEdges);
        HashSet<string> existing = activeEdges
            .Select(Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<SectionConflictEdgeRequest> added = [];
        foreach (SectionConflictEdgeRequest edge in suggestedEdges)
        {
            string key = Key(edge);
            if (!existing.Add(key))
            {
                continue;
            }
            activeEdges.Add(edge);
            added.Add(edge);
        }
        return new MacroRelationMergeResult(added.Count, activeEdges.Count, added);
    }

    private static string Key(SectionConflictEdgeRequest edge) =>
        InitialConflictGraphBuilder.BuildCanonicalPairKey(
            edge.SectionName,
            edge.FirstTrainIndex,
            edge.SecondTrainIndex);
}

public enum MacroClosureDecisionKind
{
    ProceedToMicro,
    AddRelationsAndRebuild,
    StopModeledRelationViolation,
    StopUnrepairableValidationFailure
}

public sealed record MacroClosureDecision(
    MacroClosureDecisionKind Kind,
    string Reason,
    int MissingRelationViolationCount,
    int ModeledRelationViolationCount);

public static class MacroClosureDecisionPolicy
{
    public static MacroClosureDecision Resolve(MasterValidationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.IsFeasible)
        {
            return new(MacroClosureDecisionKind.ProceedToMicro,
                "full_macro_validation_passed", 0, 0);
        }
        IReadOnlyList<SectionRelationValidationFinding> relationFindings =
            report.SectionRelationFindings ?? [];
        int missing = relationFindings.Count(item => !item.WasModeled);
        int modeled = relationFindings.Count(item => item.WasModeled);
        if ((report.SuggestedSectionEdges?.Count ?? 0) > 0)
        {
            return new(MacroClosureDecisionKind.AddRelationsAndRebuild,
                "new_applicable_section_relations", missing, modeled);
        }
        if (modeled > 0)
        {
            return new(MacroClosureDecisionKind.StopModeledRelationViolation,
                "already_modeled_section_relation_still_violated", missing, modeled);
        }
        return new(MacroClosureDecisionKind.StopUnrepairableValidationFailure,
            "macro_validation_failed_without_buildable_relation", missing, modeled);
    }
}
