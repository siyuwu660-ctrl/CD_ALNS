using System.Globalization;
using System.Text;

namespace GurobiTimetablingResearch.Optimization;

public sealed record MacroCandidateClosureRound(
    int ValidationSequence,
    int IterationIndex,
    string Phase,
    int? SolveSequence,
    string GurobiStatus,
    string ApplicationStopReason,
    double? FirstIntegerCandidateSeconds,
    double? FirstEligibleCandidateSeconds,
    double? SolveReturnElapsedGlobalSeconds,
    double FullCheckStartElapsedGlobalSeconds,
    double FullCheckCompletedElapsedGlobalSeconds,
    double FullCheckRuntimeSeconds,
    bool CandidateSnapshotComplete,
    bool FullMacroFeasible,
    int ViolationCount,
    int MissingRelationViolationCount,
    int ModeledRelationViolationCount,
    int AddedRelationCount,
    int CumulativeAddedRelationCount,
    int ModeledSectionRelationCount,
    int ModelRows,
    int ModelVariables,
    int ModelBinaryVariables,
    long? ModelNonzeros,
    double RemainingGlobalBudgetSeconds,
    string ClosureDecision);

public static class MacroCandidateClosureReporter
{
    private static readonly object Gate = new();

    public static void Append(string outputDirectory, MacroCandidateClosureRound row)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(row);
        Directory.CreateDirectory(outputDirectory);
        string path = Path.Combine(outputDirectory, "macro_candidate_closure.csv");
        lock (Gate)
        {
            bool writeHeader = !File.Exists(path) || new FileInfo(path).Length == 0;
            using StreamWriter writer = new(path, append: true, new UTF8Encoding(false));
            if (writeHeader)
            {
                writer.WriteLine(
                    "validation_sequence,iteration,phase,solve_sequence,gurobi_status,application_stop_reason," +
                    "first_integer_candidate_seconds,first_eligible_candidate_seconds," +
                    "solve_return_elapsed_global_seconds,full_check_start_elapsed_global_seconds," +
                    "full_check_completed_elapsed_global_seconds,full_check_runtime_seconds," +
                    "candidate_snapshot_complete,full_macro_feasible,violation_count," +
                    "missing_relation_violation_count,modeled_relation_violation_count," +
                    "added_relation_count,cumulative_added_relation_count,modeled_section_relation_count," +
                    "model_rows,model_variables,model_binary_variables,model_nonzeros," +
                    "remaining_global_budget_seconds,closure_decision");
            }
            writer.WriteLine(string.Join(',',
                row.ValidationSequence.ToString(CultureInfo.InvariantCulture),
                row.IterationIndex.ToString(CultureInfo.InvariantCulture),
                Escape(row.Phase),
                Format(row.SolveSequence),
                Escape(row.GurobiStatus),
                Escape(row.ApplicationStopReason),
                Format(row.FirstIntegerCandidateSeconds),
                Format(row.FirstEligibleCandidateSeconds),
                Format(row.SolveReturnElapsedGlobalSeconds),
                row.FullCheckStartElapsedGlobalSeconds.ToString("R", CultureInfo.InvariantCulture),
                row.FullCheckCompletedElapsedGlobalSeconds.ToString("R", CultureInfo.InvariantCulture),
                row.FullCheckRuntimeSeconds.ToString("R", CultureInfo.InvariantCulture),
                row.CandidateSnapshotComplete.ToString(CultureInfo.InvariantCulture),
                row.FullMacroFeasible.ToString(CultureInfo.InvariantCulture),
                row.ViolationCount.ToString(CultureInfo.InvariantCulture),
                row.MissingRelationViolationCount.ToString(CultureInfo.InvariantCulture),
                row.ModeledRelationViolationCount.ToString(CultureInfo.InvariantCulture),
                row.AddedRelationCount.ToString(CultureInfo.InvariantCulture),
                row.CumulativeAddedRelationCount.ToString(CultureInfo.InvariantCulture),
                row.ModeledSectionRelationCount.ToString(CultureInfo.InvariantCulture),
                row.ModelRows.ToString(CultureInfo.InvariantCulture),
                row.ModelVariables.ToString(CultureInfo.InvariantCulture),
                row.ModelBinaryVariables.ToString(CultureInfo.InvariantCulture),
                Format(row.ModelNonzeros),
                row.RemainingGlobalBudgetSeconds.ToString("R", CultureInfo.InvariantCulture),
                Escape(row.ClosureDecision)));
        }
    }

    private static string Format(double? value) => value?.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty;
    private static string Format(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
    private static string Format(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Escape(string value) =>
        value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
}
