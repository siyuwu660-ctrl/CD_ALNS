namespace GurobiTimetablingResearch.Optimization;

/// <summary>
/// 分离当前增强 Master 的工作解、冲突最少的诊断解和完整 Macro/Micro 可行解。
/// </summary>
internal sealed class ValidityFirstSolutionStateTracker
{
    private int _bestDiagnosticRootConflictCount = int.MaxValue;

    internal ValidityFirstSolutionStateTracker(
        MasterSolutionSnapshot? bestDiagnosticSolution = null,
        MasterSolutionSnapshot? bestJointFeasibleSolution = null)
    {
        BestDiagnosticSolution = bestDiagnosticSolution;
        BestJointFeasibleSolution = bestJointFeasibleSolution;
    }

    internal MasterSolutionSnapshot? WorkingSolution { get; private set; }
    internal MasterSolutionSnapshot? BestDiagnosticSolution { get; private set; }
    internal MasterSolutionSnapshot? BestJointFeasibleSolution { get; private set; }

    internal void InvalidateWorkingSolution()
    {
        WorkingSolution = null;
    }

    internal MasterSolutionSnapshot Register(
        MasterSolutionSnapshot snapshot,
        bool macroValidated,
        bool microFeasible,
        int rootConflictCount,
        SolverBoundScope boundScope,
        double? globalLowerBound)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        BusinessObjectiveVector objective = snapshot.BusinessObjective
            ?? throw new ArgumentException("Validity-first solution state requires the original business objective.");

        WorkingSolution = snapshot with
        {
            SolutionRole = TimetableSolutionRole.WorkingSolution,
            BoundReport = OptimizationBoundPolicy.Evaluate(
                TimetableSolutionRole.WorkingSolution,
                boundScope,
                macroValidated,
                microFeasible,
                objective,
                globalLowerBound)
        };

        if (macroValidated && microFeasible)
        {
            MasterSolutionSnapshot joint = snapshot with
            {
                SolutionRole = TimetableSolutionRole.BestJointFeasibleSolution,
                BoundReport = OptimizationBoundPolicy.Evaluate(
                    TimetableSolutionRole.BestJointFeasibleSolution,
                    boundScope,
                    true,
                    true,
                    objective,
                    globalLowerBound)
            };
            if (BestJointFeasibleSolution is null || IsBusinessObjectiveBetter(
                    joint.BusinessObjective!,
                    BestJointFeasibleSolution.BusinessObjective!) ||
                IsBusinessObjectiveEqual(joint.BusinessObjective!, BestJointFeasibleSolution.BusinessObjective!) &&
                joint.BoundReport?.GlobalLowerBound is not null &&
                BestJointFeasibleSolution.BoundReport?.GlobalLowerBound is null)
            {
                BestJointFeasibleSolution = joint;
            }
        }
        else if (BestDiagnosticSolution is null ||
                 rootConflictCount < _bestDiagnosticRootConflictCount ||
                 rootConflictCount == _bestDiagnosticRootConflictCount && IsBusinessObjectiveBetter(
                     objective,
                     BestDiagnosticSolution.BusinessObjective!))
        {
            BestDiagnosticSolution = snapshot with
            {
                SolutionRole = TimetableSolutionRole.BestDiagnosticSolution,
                BoundReport = OptimizationBoundPolicy.Evaluate(
                    TimetableSolutionRole.BestDiagnosticSolution,
                    boundScope,
                    macroValidated,
                    microFeasible,
                    objective,
                    globalLowerBound)
            };
            _bestDiagnosticRootConflictCount = rootConflictCount;
        }

        return WorkingSolution;
    }

    internal static bool IsBusinessObjectiveBetterOrEqual(
        BusinessObjectiveVector candidate,
        BusinessObjectiveVector incumbent) =>
        IsBusinessObjectiveBetter(candidate, incumbent) ||
        IsBusinessObjectiveEqual(candidate, incumbent);

    private static bool IsBusinessObjectiveEqual(
        BusinessObjectiveVector candidate,
        BusinessObjectiveVector incumbent) =>
        Math.Abs(candidate.PrimaryGlobalSpanSeconds - incumbent.PrimaryGlobalSpanSeconds) <= 1e-6 &&
        Math.Abs(candidate.SecondaryRunningTimeSupplementSeconds -
                 incumbent.SecondaryRunningTimeSupplementSeconds) <= 1e-6;

    internal static bool IsBusinessObjectiveBetter(
        BusinessObjectiveVector candidate,
        BusinessObjectiveVector incumbent)
    {
        const double tolerance = 1e-6;
        if (candidate.PrimaryGlobalSpanSeconds < incumbent.PrimaryGlobalSpanSeconds - tolerance)
        {
            return true;
        }
        return Math.Abs(candidate.PrimaryGlobalSpanSeconds - incumbent.PrimaryGlobalSpanSeconds) <= tolerance &&
               candidate.SecondaryRunningTimeSupplementSeconds <
               incumbent.SecondaryRunningTimeSupplementSeconds - tolerance;
    }
}
