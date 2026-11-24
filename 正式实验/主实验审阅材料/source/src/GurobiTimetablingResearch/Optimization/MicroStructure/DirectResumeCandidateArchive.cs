using System.Text.Json.Serialization;

namespace GurobiTimetablingResearch.Optimization;

/// <summary>
/// A comparable score is produced only by a complete full-line Micro check with no Unknown
/// station.  This deliberately prevents a small hotspot result, or an interrupted check, from
/// looking better merely because fewer constraints were inspected.
/// </summary>
internal sealed record DirectResumeCandidateScore(
    bool IsCompleteFullLine,
    bool IsMacroValidated,
    int UnknownCount,
    int TotalConflictCount,
    int StationAssignmentConflictCount,
    int ExactHeadwayConflictCount,
    double TotalHeadwayDeficitSeconds,
    int AffectedStationCount,
    int DecisionChangeCount)
{
    internal bool IsEligibleForBest => IsCompleteFullLine && IsMacroValidated && UnknownCount == 0;

    internal static DirectResumeCandidateScore FromAnalysis(
        StationLevelMicroFeasibilitySlaveResult analysis,
        string recheckStage,
        bool macroValidated,
        int decisionChangeCount)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        int unknownCount = analysis.StationResults.Count(result =>
            result.AssignmentResult.Status == MicroSolveStatus.Unknown);
        int stationAssignmentCount = analysis.Conflicts.Count(conflict => string.Equals(
            conflict.ConflictKind,
            "station_route_track_assignment_infeasible",
            StringComparison.OrdinalIgnoreCase));
        int exactHeadwayCount = analysis.Conflicts.Count(conflict => string.Equals(
            conflict.ConflictKind,
            "exact_headway_infeasible",
            StringComparison.OrdinalIgnoreCase));
        double totalDeficit = analysis.Conflicts.Sum(conflict =>
            conflict.RequiredGapSeconds.HasValue && conflict.RealizedGapSeconds.HasValue
                ? Math.Max(0.0,
                    conflict.RequiredGapSeconds.Value - conflict.RealizedGapSeconds.Value)
                : 0.0);
        int affectedStations = analysis.Conflicts
            .Select(conflict => conflict.StationName)
            .Where(station => !string.IsNullOrWhiteSpace(station))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        return new DirectResumeCandidateScore(
            string.Equals(recheckStage, "full_line", StringComparison.OrdinalIgnoreCase),
            macroValidated,
            unknownCount,
            analysis.Conflicts.Count,
            stationAssignmentCount,
            exactHeadwayCount,
            totalDeficit,
            affectedStations,
            decisionChangeCount);
    }

    internal bool IsStrictlyBetterThan(DirectResumeCandidateScore other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (!IsEligibleForBest) return false;
        if (!other.IsEligibleForBest) return true;
        return CompareQuality(this, other) < 0;
    }

    internal static int CompareForSearch(
        DirectResumeCandidateScore left,
        DirectResumeCandidateScore right)
    {
        int comparison = right.IsCompleteFullLine.CompareTo(left.IsCompleteFullLine);
        if (comparison != 0) return comparison;
        comparison = right.IsMacroValidated.CompareTo(left.IsMacroValidated);
        if (comparison != 0) return comparison;
        comparison = left.UnknownCount.CompareTo(right.UnknownCount);
        if (comparison != 0) return comparison;
        return CompareQuality(left, right);
    }

    private static int CompareQuality(
        DirectResumeCandidateScore left,
        DirectResumeCandidateScore right)
    {
        // An irreducible route-track assignment core still needs a discrete Macro/Micro change;
        // an exact headway conflict already has a sound guarded projection and is normally
        // repairable by time.  Preserve progress on the harder resource cores first, then compare
        // the aggregate time deficit and the remaining exact relations.
        int comparison = left.StationAssignmentConflictCount.CompareTo(
            right.StationAssignmentConflictCount);
        if (comparison != 0) return comparison;
        comparison = left.TotalHeadwayDeficitSeconds.CompareTo(
            right.TotalHeadwayDeficitSeconds);
        if (comparison != 0) return comparison;
        comparison = left.ExactHeadwayConflictCount.CompareTo(right.ExactHeadwayConflictCount);
        if (comparison != 0) return comparison;
        comparison = left.TotalConflictCount.CompareTo(right.TotalConflictCount);
        if (comparison != 0) return comparison;
        comparison = left.AffectedStationCount.CompareTo(right.AffectedStationCount);
        if (comparison != 0) return comparison;
        return left.DecisionChangeCount.CompareTo(right.DecisionChangeCount);
    }
}

internal sealed record HotspotCandidateScoreEntry(
    string CandidateId,
    DirectResumeCandidateScore Score,
    double TimetableSpanSeconds);

internal static class DynamicHotspotCandidateSelectionPolicy
{
    internal static HotspotCandidateScoreEntry SelectBest(
        IReadOnlyList<HotspotCandidateScoreEntry> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0)
            throw new ArgumentException("At least one candidate is required.", nameof(candidates));
        if (candidates.Any(candidate => string.IsNullOrWhiteSpace(candidate.CandidateId) ||
                !double.IsFinite(candidate.TimetableSpanSeconds) || candidate.TimetableSpanSeconds < 0.0))
            throw new ArgumentException("Candidate identity and timetable span must be valid.", nameof(candidates));
        return candidates.Order(Comparer<HotspotCandidateScoreEntry>.Create((left, right) =>
        {
            int comparison = DirectResumeCandidateScore.CompareForSearch(left.Score, right.Score);
            if (comparison != 0) return comparison;
            comparison = left.TimetableSpanSeconds.CompareTo(right.TimetableSpanSeconds);
            if (comparison != 0) return comparison;
            return string.Compare(left.CandidateId, right.CandidateId, StringComparison.OrdinalIgnoreCase);
        })).First();
    }
}

internal sealed record DirectResumeCandidateObservation(
    string CandidateFingerprint,
    DirectResumeCandidateScore Score,
    int Iteration,
    string CandidatePath,
    [property: JsonIgnore] MasterSolutionSnapshot? Snapshot = null);

internal sealed record DirectResumeCandidateArchiveDecision(
    bool BecameBest,
    string Reason,
    DirectResumeCandidateObservation? Best,
    DirectResumeCandidateObservation Current);

internal sealed record MacroMicroBestCandidateState(
    string CandidateFingerprint,
    DirectResumeCandidateScore Score,
    int Iteration,
    string CandidatePath,
    string SelectionReason);

/// <summary>
/// Keeps the incumbent-quality record separate from the branch currently being explored.  A
/// worse candidate remains observable and may provide new proof, but cannot erase the best
/// complete result.
/// </summary>
internal sealed class DirectResumeCandidateArchive
{
    private const int MaximumEntries = 8;
    private readonly List<DirectResumeCandidateObservation> _entries = [];

    internal DirectResumeCandidateObservation? Best { get; private set; }
    internal DirectResumeCandidateObservation? Current { get; private set; }
    internal IReadOnlyList<DirectResumeCandidateObservation> Entries => _entries;

    internal DirectResumeCandidateArchiveDecision Observe(
        DirectResumeCandidateObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (string.IsNullOrWhiteSpace(observation.CandidateFingerprint))
            throw new ArgumentException("Candidate fingerprint is required.", nameof(observation));
        Current = observation;
        _entries.RemoveAll(item => string.Equals(
            item.CandidateFingerprint,
            observation.CandidateFingerprint,
            StringComparison.OrdinalIgnoreCase));
        _entries.Add(observation);
        while (_entries.Count > MaximumEntries)
        {
            DirectResumeCandidateObservation removable = _entries
                .First(item => Best is null || !string.Equals(
                    item.CandidateFingerprint,
                    Best.CandidateFingerprint,
                    StringComparison.OrdinalIgnoreCase));
            _entries.Remove(removable);
        }

        bool becameBest = observation.Score.IsEligibleForBest &&
            (Best is null || observation.Score.IsStrictlyBetterThan(Best.Score));
        if (becameBest) Best = observation;
        string reason = becameBest
            ? "complete_full_line_candidate_improved_best"
            : !observation.Score.IsCompleteFullLine
                ? "partial_check_not_comparable"
                : !observation.Score.IsMacroValidated
                    ? "macro_validation_failed_not_comparable"
                : observation.Score.UnknownCount > 0
                    ? "unknown_check_not_comparable"
                    : "candidate_did_not_improve_best";
        return new DirectResumeCandidateArchiveDecision(becameBest, reason, Best, observation);
    }

    internal DirectResumeCandidateObservation? RollbackToBest()
    {
        if (Best is not null) Current = Best;
        return Current;
    }
}

internal enum DirectResumeReleaseLevel
{
    EvidencePremises,
    EvidenceCorridor,
    EvidenceCorridorExpanded,
    EvidenceTrainDependencies,
    FullMacro
}

internal sealed record DirectResumeBranchRecovery(
    string ReferenceFingerprint,
    DirectResumeReleaseLevel NextReleaseLevel,
    string Reason);

internal sealed record DirectResumeExplorationDecision(
    bool ShouldExplore,
    int NextDepth,
    string Reason);

/// <summary>
/// Allows a small, explicitly bounded detour through a fully checked non-best candidate.  This is
/// a search policy only: it never promotes the candidate, weakens a business row, or turns an
/// Unknown result into progress.  A structurally different complete candidate may use a bounded
/// branch before its score improves.  Exploration is bounded by branch depth and the distinct
/// candidate-feedback identity, not by a monotone conflict-count requirement.
/// </summary>
internal sealed class DirectResumeExplorationPolicy
{
    private readonly int _maximumDepth;
    private readonly HashSet<string> _startedBranches = new(StringComparer.OrdinalIgnoreCase);

    internal DirectResumeExplorationPolicy(int maximumDepth)
    {
        if (maximumDepth < 0) throw new ArgumentOutOfRangeException(nameof(maximumDepth));
        _maximumDepth = maximumDepth;
    }

    internal DirectResumeExplorationDecision Consider(
        DirectResumeCandidateObservation best,
        DirectResumeCandidateObservation candidate,
        string feedbackId,
        int currentDepth)
    {
        ArgumentNullException.ThrowIfNull(best);
        ArgumentNullException.ThrowIfNull(candidate);
        if (string.IsNullOrWhiteSpace(feedbackId))
            return new(false, currentDepth, "no_mapped_feedback_for_exploratory_branch");
        if (currentDepth >= _maximumDepth)
            return new(false, currentDepth, "bounded_exploration_depth_reached");
        if (!candidate.Score.IsEligibleForBest)
            return new(false, currentDepth, "candidate_has_no_complete_known_full_line_check");
        if (string.Equals(best.CandidateFingerprint, candidate.CandidateFingerprint,
                StringComparison.OrdinalIgnoreCase))
            return new(false, currentDepth, "best_candidate_needs_no_detour");

        string identity = $"{candidate.CandidateFingerprint}::{feedbackId}";
        if (!_startedBranches.Add(identity))
            return new(false, currentDepth, "candidate_feedback_branch_already_explored");
        return new(true, currentDepth + 1,
            "bounded_non_best_branch_has_distinct_structure_and_mapped_feedback");
    }
}

internal static class DirectResumeBranchPolicy
{
    internal static DirectResumeBranchRecovery RecoverAfterFailure(
        DirectResumeCandidateArchive archive,
        DirectResumeReleaseLevel attemptedLevel)
    {
        ArgumentNullException.ThrowIfNull(archive);
        DirectResumeCandidateObservation? reference = archive.RollbackToBest();
        if (reference is null)
            throw new InvalidOperationException(
                "A failed branch cannot be recovered before one complete candidate is archived.");
        DirectResumeBranchRecovery recovery = ExpandFromReference(reference, attemptedLevel);
        return recovery with
        {
            Reason = $"rollback_to_best_after_{attemptedLevel.ToString().ToLowerInvariant()}"
        };
    }

    internal static DirectResumeBranchRecovery ExpandFromReference(
        DirectResumeCandidateObservation reference,
        DirectResumeReleaseLevel attemptedLevel)
    {
        ArgumentNullException.ThrowIfNull(reference);
        DirectResumeReleaseLevel next = attemptedLevel switch
        {
            DirectResumeReleaseLevel.EvidencePremises =>
                DirectResumeReleaseLevel.EvidenceCorridor,
            DirectResumeReleaseLevel.EvidenceCorridor =>
                DirectResumeReleaseLevel.EvidenceCorridorExpanded,
            DirectResumeReleaseLevel.EvidenceCorridorExpanded =>
                DirectResumeReleaseLevel.EvidenceTrainDependencies,
            DirectResumeReleaseLevel.EvidenceTrainDependencies => DirectResumeReleaseLevel.FullMacro,
            _ => DirectResumeReleaseLevel.FullMacro
        };
        return new DirectResumeBranchRecovery(
            reference.CandidateFingerprint,
            next,
            $"expand_from_reference_after_{attemptedLevel.ToString().ToLowerInvariant()}");
    }
}
