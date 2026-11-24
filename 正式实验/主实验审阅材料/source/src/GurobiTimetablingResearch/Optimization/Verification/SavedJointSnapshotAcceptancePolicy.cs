namespace GurobiTimetablingResearch.Optimization;

internal static class SavedJointSnapshotAcceptancePolicy
{
    internal static bool IsAccepted(
        int macroViolationCount,
        MicroSolveStatus globalMicroStatus,
        int globalMicroConflictCount,
        int globalMicroUnknownCount,
        bool independentMicroValid,
        bool fixedAssignmentValid,
        bool identityValid,
        bool serializedFingerprintMatches,
        int assignmentCount,
        int expectedAssignmentCount) =>
        macroViolationCount == 0 &&
        globalMicroStatus == MicroSolveStatus.Feasible &&
        globalMicroConflictCount == 0 &&
        globalMicroUnknownCount == 0 &&
        independentMicroValid &&
        fixedAssignmentValid &&
        identityValid &&
        serializedFingerprintMatches &&
        assignmentCount == expectedAssignmentCount;
}
