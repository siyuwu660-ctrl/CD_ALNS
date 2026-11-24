using System.Globalization;
using System.Text;

namespace GurobiTimetablingResearch.Optimization;

/// <summary>
/// 描述已知反馈与当前重复冲突之间的适用条件、激活状态和快照满足性核查结果。
/// </summary>
public sealed record ValidityFirstFeedbackRecurrenceDiagnostic(
    int Iteration,
    string FeedbackId,
    bool ActiveInModel,
    bool ApplicabilityMatches,
    bool ProofContextMatches,
    bool ReturnedSnapshotSatisfiesFeedback,
    bool RequiresInvestigation,
    string Reason);

public static class ValidityFirstFeedbackConsistencyAuditor
{
    public static ValidityFirstFeedbackRecurrenceDiagnostic Compare(
        int iteration,
        ValidatedMicroFeedback existing,
        ValidatedMicroFeedback current,
        bool activeInModel)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(current);
        if (!string.Equals(existing.FeedbackId, current.FeedbackId, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Feedback identities must match before applicability comparison.");
        }

        bool applicabilityMatches = string.Equals(
            existing.ApplicabilityConditions,
            current.ApplicabilityConditions,
            StringComparison.Ordinal);
        bool proofContextMatches = string.Equals(
            existing.ProofFingerprint,
            current.ProofFingerprint,
            StringComparison.Ordinal);
        bool requiresInvestigation = activeInModel && applicabilityMatches;
        return new ValidityFirstFeedbackRecurrenceDiagnostic(
            iteration,
            existing.FeedbackId,
            activeInModel,
            applicabilityMatches,
            proofContextMatches,
            !requiresInvestigation,
            requiresInvestigation,
            requiresInvestigation
                ? "active_feedback_same_applicability_recurred_check_mapping_constraint_and_tolerance"
                : activeInModel
                    ? "active_feedback_recurred_under_different_applicability"
                    : "matching_feedback_not_active_in_current_model");
    }
}

public static class ValidityFirstFeedbackConsistencyReporter
{
    public static void Append(
        string phase,
        IReadOnlyList<ValidityFirstFeedbackRecurrenceDiagnostic> diagnostics,
        string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (diagnostics.Count == 0)
        {
            return;
        }

        Directory.CreateDirectory(outputDirectory);
        string path = Path.Combine(outputDirectory, "validity_first_feedback_recurrence.csv");
        bool writeHeader = !File.Exists(path);
        using StreamWriter writer = new(path, true, new UTF8Encoding(false));
        if (writeHeader)
        {
            writer.WriteLine(
                "iteration,phase,feedback_id,active_in_model,applicability_matches,proof_context_matches," +
                "returned_snapshot_satisfies_feedback,requires_investigation,reason");
        }
        foreach (ValidityFirstFeedbackRecurrenceDiagnostic item in diagnostics)
        {
            writer.WriteLine(string.Join(',',
                item.Iteration.ToString(CultureInfo.InvariantCulture),
                Escape(phase),
                Escape(item.FeedbackId),
                item.ActiveInModel,
                item.ApplicabilityMatches,
                item.ProofContextMatches,
                item.ReturnedSnapshotSatisfiesFeedback,
                item.RequiresInvestigation,
                Escape(item.Reason)));
        }
    }

    private static string Escape(string? value)
    {
        value ??= string.Empty;
        return value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
    }
}
