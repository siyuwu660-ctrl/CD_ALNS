namespace GurobiTimetablingResearch.Optimization;

internal enum ConflictCoreRepairStage
{
    TimeFeedback,
    ProofLinkedCore,
    DependencyExpandedCore,
    ContiguousCorridorFallback
}

internal sealed record ConflictCoreRepairDecision(
    ConflictCoreRepairStage Stage,
    string Reason,
    string MathematicalStageIdentity);

internal static class ConflictCoreRepairEscalationPolicy
{
    internal static ConflictCoreRepairDecision Select(
        string candidateFingerprint,
        string proofIdentity,
        bool proofCoreComplete,
        IReadOnlyCollection<string> attemptedMathematicalStates)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(proofIdentity);
        ArgumentNullException.ThrowIfNull(attemptedMathematicalStates);
        string prefix = $"{candidateFingerprint}|{proofIdentity}|";
        bool proofAttempted = attemptedMathematicalStates.Any(value =>
            value.StartsWith(prefix + "proof_linked_core|", StringComparison.Ordinal));
        bool dependencyAttempted = attemptedMathematicalStates.Any(value =>
            value.StartsWith(prefix + "dependency_expanded_core|", StringComparison.Ordinal));

        if (proofCoreComplete && !proofAttempted)
            return new(ConflictCoreRepairStage.ProofLinkedCore,
                "smallest_current_candidate_proof_core_not_yet_attempted", "proof_linked_core");
        if (proofCoreComplete && !dependencyAttempted)
            return new(ConflictCoreRepairStage.DependencyExpandedCore,
                "proof_core_exhausted_expand_only_paired_order_dependencies", "dependency_expanded_core");
        return new(ConflictCoreRepairStage.ContiguousCorridorFallback,
            proofCoreComplete
                ? "proof_and_dependency_scopes_exhausted"
                : "proof_mapping_incomplete",
            "contiguous_corridor_fallback");
    }

    internal static string BuildStateIdentity(
        string candidateFingerprint,
        string proofIdentity,
        ConflictCoreRepairDecision decision,
        DynamicHotspotScopeSelection scope) =>
        $"{candidateFingerprint}|{proofIdentity}|{decision.MathematicalStageIdentity}|" +
        $"{string.Join('|', scope.StationNames)}|{string.Join('|', scope.StopIds)}";
}
