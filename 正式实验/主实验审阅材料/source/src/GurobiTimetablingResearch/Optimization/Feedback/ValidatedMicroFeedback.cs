using System.Security.Cryptography;
using System.Text;

namespace GurobiTimetablingResearch.Optimization;

/// <summary>
/// 表示微观不可行信息相对于原问题的证明强度，决定它能否永久收紧主问题。
/// </summary>
public enum MicroFeedbackValidity
{
    ProvenNecessary,
    ConditionalNecessary,
    HeuristicProjection,
    AttemptLocalIisOverlay,
    Unknown
}

public enum MicroFeedbackPersistence
{
    Permanent,
    AttemptLocal,
    DiagnosticOnly,
    ExactificationRequired
}

/// <summary>
/// 描述条件证明依赖的主问题文字变量，例如停站模式、先后次序或作业模式。
/// </summary>
public sealed record FeedbackSupportCondition(
    string VariableKey,
    int RequiredBinaryValue,
    string ConditionKind);

/// <summary>
/// 将反馈载荷与证明来源、支持条件和稳定标识绑定，供批量选择、checkpoint 和审计共同使用。
/// </summary>
public sealed record ValidatedMicroFeedback(
    string FeedbackId,
    string StationName,
    string PairKey,
    MicroFeedbackValidity Validity,
    string ProofFingerprint,
    IReadOnlyList<FeedbackSupportCondition>? SupportConditions = null,
    bool ConditionsRepresentedInMaster = false,
    string ProofSource = "",
    string MathematicalConstraint = "",
    string ApplicabilityConditions = "",
    string EvidenceSummary = "",
    int? GeneratedIteration = null,
    bool AppliedToModel = false,
    int? AppliedIteration = null,
    bool ValidatedBySnapshot = false,
    int? ValidatedIteration = null,
    string ValidatedSnapshotFingerprint = "",
    bool? ReturnedSnapshotSatisfiesFeedback = null)
{
    public IReadOnlyList<FeedbackSupportCondition> Conditions { get; } =
        SupportConditions ?? Array.Empty<FeedbackSupportCondition>();
}

public static class ValidatedMicroFeedbackPolicy
{
    public static MicroFeedbackPersistence ResolvePersistence(ValidatedMicroFeedback feedback)
    {
        ArgumentNullException.ThrowIfNull(feedback);
        ArgumentException.ThrowIfNullOrWhiteSpace(feedback.FeedbackId);

        return feedback.Validity switch
        {
            MicroFeedbackValidity.ProvenNecessary => RequireProof(
                feedback,
                MicroFeedbackPersistence.Permanent),
            MicroFeedbackValidity.ConditionalNecessary => ResolveConditional(feedback),
            MicroFeedbackValidity.HeuristicProjection => MicroFeedbackPersistence.AttemptLocal,
            MicroFeedbackValidity.AttemptLocalIisOverlay => MicroFeedbackPersistence.AttemptLocal,
            MicroFeedbackValidity.Unknown => MicroFeedbackPersistence.DiagnosticOnly,
            _ => throw new ArgumentOutOfRangeException(nameof(feedback), feedback.Validity, null)
        };
    }

    private static MicroFeedbackPersistence ResolveConditional(ValidatedMicroFeedback feedback)
    {
        _ = RequireProof(feedback, MicroFeedbackPersistence.Permanent);
        return feedback.Conditions.Count > 0 && feedback.ConditionsRepresentedInMaster
            ? MicroFeedbackPersistence.Permanent
            : MicroFeedbackPersistence.ExactificationRequired;
    }

    private static MicroFeedbackPersistence RequireProof(
        ValidatedMicroFeedback feedback,
        MicroFeedbackPersistence persistence)
    {
        if (string.IsNullOrWhiteSpace(feedback.ProofFingerprint))
        {
            throw new InvalidOperationException(
                $"Feedback '{feedback.FeedbackId}' has no proof fingerprint.");
        }

        return persistence;
    }
}

internal static class ValidatedMicroFeedbackFactory
{
    internal static ValidatedMicroFeedback FromExactHeadwayCandidate(
        HeadwayConflictCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        StructuralCandidateEvidence evidence = candidate.Evidence
            ?? throw new InvalidOperationException(
                $"Exact candidate '{candidate.CandidateKey}' has no route-domain evidence.");
        if (!string.Equals(
                evidence.MicroClassification,
                "UnavoidableConflict",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Candidate '{candidate.CandidateKey}' is not an unavoidable route-domain conflict.");
        }

        string proofPayload = string.Join('|',
            candidate.CandidateKey,
            candidate.EpisodeId,
            candidate.ResourceKey,
            candidate.FirstTrainIndex,
            candidate.SecondTrainIndex,
            evidence.CandidateType,
            evidence.StationName,
            evidence.SectionName,
            evidence.FirstEvent,
            evidence.SecondEvent,
            evidence.MicroClassification,
            evidence.RequiredGapSeconds?.ToString("R", System.Globalization.CultureInfo.InvariantCulture) ?? "");
        string proofFingerprint = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(proofPayload))).ToLowerInvariant();

        string mathematicalConstraint = string.Equals(
            evidence.CandidateType,
            "section",
            StringComparison.OrdinalIgnoreCase)
            ? $"Binary precedence z[{candidate.FirstTrainIndex},{candidate.SecondTrainIndex},{evidence.SectionName}] " +
              "selects one of two safe headway directions; departure and arrival inequalities use " +
              "stop-mode-dependent route-domain headways for both train orders."
            : $"Binary precedence z[{candidate.FirstTrainIndex},{candidate.SecondTrainIndex},{evidence.StationName}," +
              $"{evidence.FirstEvent},{evidence.SecondEvent}] selects one of two station-event time inequalities " +
              "with stop-mode-dependent route/track-domain headway.";
        string applicabilityConditions =
            $"Applies only to resource={candidate.ResourceKey}, trains={candidate.FirstTrainIndex}/" +
            $"{candidate.SecondTrainIndex}, events={evidence.FirstEvent}/{evidence.SecondEvent}; " +
            "all legal stop-mode binaries remain linked to Master stop variables, so the constraint is not tied " +
            "to the fixed times of the diagnostic snapshot.";
        string evidenceSummary = string.Join(';',
            $"candidate_type={evidence.CandidateType}",
            $"station={evidence.StationName}",
            $"section={evidence.SectionName}",
            $"micro_classification={evidence.MicroClassification}",
            $"realized_gap={FormatEvidence(evidence.RealizedGapSeconds)}",
            $"required_gap={FormatEvidence(evidence.RequiredGapSeconds)}",
            $"headway_deficit={FormatEvidence(evidence.HeadwayDeficitSeconds)}",
            $"episode={candidate.EpisodeId}",
            $"resource={candidate.ResourceKey}");

        return new ValidatedMicroFeedback(
            candidate.CandidateKey,
            evidence.StationName,
            $"{Math.Min(candidate.FirstTrainIndex, candidate.SecondTrainIndex)}-" +
            $"{Math.Max(candidate.FirstTrainIndex, candidate.SecondTrainIndex)}",
            MicroFeedbackValidity.ProvenNecessary,
            proofFingerprint,
            ProofSource:
                "GlobalMicro exact_headway_infeasible -> " +
                "RoutePairHeadwayClassifier.UnavoidableConflict -> HeadwayConflictCandidateSelector.SelectExact",
            MathematicalConstraint: mathematicalConstraint,
            ApplicabilityConditions: applicabilityConditions,
            EvidenceSummary: evidenceSummary);
    }

    private static string FormatEvidence(double? value) =>
        value?.ToString("R", System.Globalization.CultureInfo.InvariantCulture) ?? "n/a";
}

/// <summary>
/// 以稳定反馈标识保存所有已证明的永久约束；冲突变化只记录诊断，不影响约束有效性。
/// </summary>
public sealed class PermanentFeedbackLedger
{
    private readonly Dictionary<string, ValidatedMicroFeedback> _items =
        new(StringComparer.OrdinalIgnoreCase);

    public PermanentFeedbackLedger(IEnumerable<ValidatedMicroFeedback>? items = null)
    {
        foreach (ValidatedMicroFeedback item in items ?? Array.Empty<ValidatedMicroFeedback>())
        {
            TryAdd(item);
        }
    }

    public int Count => _items.Count;
    public IReadOnlyList<ValidatedMicroFeedback> Items => _items.Values
        .OrderBy(item => item.FeedbackId, StringComparer.OrdinalIgnoreCase)
        .ToList();
    public int? LastConflictCountBefore { get; private set; }
    public int? LastConflictCountAfter { get; private set; }

    public bool TryGet(string feedbackId, out ValidatedMicroFeedback? feedback) =>
        _items.TryGetValue(feedbackId, out feedback);

    public bool TryAdd(ValidatedMicroFeedback feedback, int? generatedIteration = null)
    {
        if (ValidatedMicroFeedbackPolicy.ResolvePersistence(feedback) !=
            MicroFeedbackPersistence.Permanent)
        {
            throw new InvalidOperationException(
                $"Feedback '{feedback.FeedbackId}' is not eligible for the permanent ledger.");
        }

        return _items.TryAdd(
            feedback.FeedbackId,
            generatedIteration.HasValue
                ? feedback with { GeneratedIteration = generatedIteration }
                : feedback);
    }

    /// <summary>
    /// 标记反馈已经进入某次构造完成的 Master；该状态不表示模型已求解或快照已满足反馈。
    /// </summary>
    public void MarkAppliedToModel(IEnumerable<string> feedbackIds, int appliedIteration)
    {
        foreach (string feedbackId in feedbackIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (_items.TryGetValue(feedbackId, out ValidatedMicroFeedback? feedback))
            {
                _items[feedbackId] = feedback with
                {
                    AppliedToModel = true,
                    AppliedIteration = appliedIteration
                };
            }
        }
    }

    /// <summary>
    /// 仅在增强 Master 返回 incumbent 且完成 Validator/Micro 检查后绑定快照指纹。
    /// </summary>
    public void MarkValidatedBySnapshot(
        IEnumerable<string> feedbackIds,
        int validatedIteration,
        string snapshotFingerprint,
        bool returnedSnapshotSatisfiesFeedback)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotFingerprint);
        foreach (string feedbackId in feedbackIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (_items.TryGetValue(feedbackId, out ValidatedMicroFeedback? feedback) && feedback.AppliedToModel)
            {
                _items[feedbackId] = feedback with
                {
                    ValidatedBySnapshot = true,
                    ValidatedIteration = validatedIteration,
                    ValidatedSnapshotFingerprint = snapshotFingerprint,
                    ReturnedSnapshotSatisfiesFeedback = returnedSnapshotSatisfiesFeedback
                };
            }
        }
    }

    public void RecordMasterOutcome(int conflictCountBefore, int conflictCountAfter)
    {
        if (conflictCountBefore < 0 || conflictCountAfter < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(conflictCountBefore));
        }

        LastConflictCountBefore = conflictCountBefore;
        LastConflictCountAfter = conflictCountAfter;
    }
}
