// 加割审计把每个候选从生成、试探求解到接受或拒绝的结果整理为记录，并按反馈类别汇总效果。
// 审计结果区分适用性、冲突变化和目标变化，用于识别无效硬割、重复候选及真正改善微观可行性的结构约束。
namespace GurobiTimetablingResearch.Optimization;

internal enum CheckerFeedbackClass
{
    Structural,
    Conditional,
    Heuristic,
    Certificate
}

internal enum CheckerAuditOutcomeApplicability
{
    Applicable,
    NotApplicable
}

internal sealed record CheckerCutAuditRecord(
    string CandidateId,
    string BatchId,
    int IterationIndex,
    CheckerFeedbackClass FeedbackClass,
    string Action,
    string SourceFamily,
    string Validity,
    CutAuditOutcome Outcome,
    string SolverStatus,
    int RawConflictsBefore,
    int? RawConflictsAfter,
    int? CanonicalConflictsBefore,
    int? CanonicalConflictsAfter,
    int? RootConflictsBefore,
    int? RootConflictsAfter,
    int? ConflictedStationsBefore,
    int? ConflictedStationsAfter,
    double? ObjectiveBefore,
    double? ObjectiveAfter,
    int ActiveSectionEdgesBefore,
    int ActiveSectionEdgesAfter,
    int ActiveStationEdgesBefore,
    int ActiveStationEdgesAfter,
    string Decision,
    int? EpisodeConflictsBefore = null,
    int? EpisodeConflictsAfter = null,
    int AdaptiveBatchSize = 0,
    int AdaptiveRepairPadding = 0,
    string AdaptivePolicyReason = "",
    int AcceptedRecourseWindowSeconds = 0,
    int RecourseAttemptCount = 0,
    int FixedTemporalEventCount = 0,
    int BoundedTemporalEventCount = 0,
    double TotalRetimingSeconds = 0.0,
    double MaximumRetimingSeconds = 0.0,
    int UnseenStructuralCandidateCount = 0,
    bool ZeroConflictMode = false,
    StationCoreCutDiagnostic? StationCore = null,
    CheckerAuditOutcomeApplicability OutcomeApplicability = CheckerAuditOutcomeApplicability.Applicable)
{
    internal string OutcomeLabel => OutcomeApplicability == CheckerAuditOutcomeApplicability.NotApplicable
        ? "Certificate/NotApplicable"
        : Outcome.ToString();
}

internal sealed record CheckerCutAuditSummary(
    int RetainedStructuralBatchCount,
    int RejectedStructuralBatchCount,
    int AcceptedHeuristicCutCount,
    int RejectedHeuristicTrialCount,
    int UnknownHeuristicTrialCount,
    int CertificateCount);

internal static class CheckerCutAuditBuilder
{
    internal static IReadOnlyList<CheckerCutAuditRecord> Build(
        IReadOnlyList<CheckerLoopIteration> iterations,
        IReadOnlyList<TrialCutDiagnostic> diagnostics)
    {
        List<CheckerCutAuditRecord> records = new();

        foreach (CheckerLoopIteration iteration in iterations.Where(IsStructuralIteration))
        {
            bool retained = string.Equals(
                iteration.Decision,
                "expanded_headway_edges",
                StringComparison.OrdinalIgnoreCase);
            records.Add(new CheckerCutAuditRecord(
                $"structural-batch-{iteration.IterationIndex}",
                $"iteration-{iteration.IterationIndex}-structural",
                iteration.IterationIndex,
                CheckerFeedbackClass.Structural,
                "headway_edge_batch",
                "exact_headway_infeasible",
                "model_valid",
                retained ? CutAuditOutcome.Retained : ResolveUnknownOrRejected(iteration.SolverStatus),
                iteration.SolverStatus,
                iteration.MicroConflictCountBefore,
                retained ? iteration.MicroConflictCountAfter : null,
                iteration.CanonicalConflictCountBefore,
                retained ? iteration.CanonicalConflictCountAfter : null,
                iteration.RootConflictCountBefore,
                retained ? iteration.RootConflictCountAfter : null,
                iteration.ConflictedStationCountBefore,
                retained ? iteration.ConflictedStationCountAfter : null,
                iteration.ObjectiveBefore,
                retained ? iteration.ObjectiveAfter : null,
                iteration.ActiveSectionEdgeCountBefore,
                iteration.ActiveSectionEdgeCountAfter,
                iteration.ActiveStationEventEdgeCountBefore,
                iteration.ActiveStationEventEdgeCountAfter,
                iteration.Decision,
                iteration.EpisodeConflictCountBefore,
                retained ? iteration.EpisodeConflictCountAfter : null,
                iteration.AdaptiveBatchSize,
                iteration.AdaptiveRepairPadding,
                iteration.AdaptivePolicyReason,
                iteration.AcceptedRecourseWindowSeconds,
                iteration.RecourseAttemptCount,
                iteration.FixedTemporalEventCount,
                iteration.BoundedTemporalEventCount,
                iteration.TotalRetimingSeconds,
                iteration.MaximumRetimingSeconds,
                iteration.UnseenStructuralCandidateCount,
                iteration.ZeroConflictMode));
        }

        records.AddRange(diagnostics.Select(item =>
        {
            bool isUnsupportedCertificate = item.StationCore is { Attempted: false };
            return new CheckerCutAuditRecord(
                item.CutId,
                isUnsupportedCertificate
                    ? $"iteration-{item.IterationIndex}-certificate"
                    : $"iteration-{item.IterationIndex}-heuristic-{item.TrialIndex}",
                item.IterationIndex,
                isUnsupportedCertificate ? CheckerFeedbackClass.Certificate : CheckerFeedbackClass.Heuristic,
                isUnsupportedCertificate ? "architectural_certificate" : "local_precedence_nogood",
                item.SourceFamily,
                isUnsupportedCertificate ? "unsupported_master_projection" : "heuristic_screened",
                ResolveTrialOutcome(item),
                item.SolverStatus,
                item.WorkingGlobalConflicts,
                item.TrialGlobalConflicts,
                item.WorkingCanonicalConflicts,
                item.TrialCanonicalConflicts,
                item.WorkingRootConflicts,
                item.TrialRootConflicts,
                item.WorkingConflictedStations,
                item.TrialConflictedStations,
                item.WorkingObjective,
                item.TrialObjective,
                item.ActiveSectionEdgeCount,
                item.ActiveSectionEdgeCount,
                item.ActiveStationEventEdgeCount,
                item.ActiveStationEventEdgeCount,
                item.Decision,
                StationCore: item.StationCore,
                OutcomeApplicability: isUnsupportedCertificate
                    ? CheckerAuditOutcomeApplicability.NotApplicable
                    : CheckerAuditOutcomeApplicability.Applicable);
        }));

        return records
            .OrderBy(item => item.IterationIndex)
            .ThenBy(item => item.FeedbackClass)
            .ThenBy(item => item.CandidateId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static CheckerCutAuditSummary Summarize(IReadOnlyList<CheckerCutAuditRecord> records)
    {
        return new CheckerCutAuditSummary(
            records.Count(item => item.FeedbackClass == CheckerFeedbackClass.Structural && item.Outcome == CutAuditOutcome.Retained),
            records.Count(item => item.FeedbackClass == CheckerFeedbackClass.Structural && item.Outcome is CutAuditOutcome.Rejected or CutAuditOutcome.NoIncumbent),
            records.Count(item => item.FeedbackClass == CheckerFeedbackClass.Heuristic && item.Outcome == CutAuditOutcome.Accepted),
            records.Count(item => item.FeedbackClass == CheckerFeedbackClass.Heuristic && item.Outcome == CutAuditOutcome.Rejected),
            records.Count(item => item.FeedbackClass == CheckerFeedbackClass.Heuristic && item.Outcome is CutAuditOutcome.UnknownTimeLimit or CutAuditOutcome.UnknownNodeLimit),
            records.Count(item => item.FeedbackClass == CheckerFeedbackClass.Certificate));
    }

    private static bool IsStructuralIteration(CheckerLoopIteration iteration) =>
        string.Equals(iteration.Decision, "expanded_headway_edges", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(iteration.Decision, "structural_edge_trial_no_incumbent", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(iteration.Decision, "temporal_recourse_exhausted", StringComparison.OrdinalIgnoreCase);

    private static CutAuditOutcome ResolveTrialOutcome(TrialCutDiagnostic item)
    {
        if (!item.HasIncumbent.HasValue)
        {
            return CutAuditOutcome.NoIncumbent;
        }

        if (item.Accepted)
        {
            return CutAuditOutcome.Accepted;
        }

        if (!item.HasIncumbent.Value)
        {
            return ResolveUnknownOrRejected(item.SolverStatus);
        }

        return CutAuditOutcome.Rejected;
    }

    private static CutAuditOutcome ResolveUnknownOrRejected(string solverStatus)
    {
        if (int.TryParse(solverStatus, out int numericStatus))
        {
            return CutAuditOutcomeResolver.Resolve(hasIncumbent: false, numericStatus, accepted: false);
        }

        if (solverStatus.Contains("TIME_LIMIT", StringComparison.OrdinalIgnoreCase))
        {
            return CutAuditOutcome.UnknownTimeLimit;
        }

        if (solverStatus.Contains("NODE_LIMIT", StringComparison.OrdinalIgnoreCase))
        {
            return CutAuditOutcome.UnknownNodeLimit;
        }

        return CutAuditOutcome.NoIncumbent;
    }
}
