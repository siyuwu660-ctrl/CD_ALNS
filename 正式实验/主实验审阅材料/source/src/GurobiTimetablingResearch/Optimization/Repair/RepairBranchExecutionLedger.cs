// 修复分支台账把“计划动作”“实际数学模型”和“具体一次搜索”分开记录。
// 模型身份同时使用 Gurobi 对最终模型数据计算的指纹和稳定业务上下文，避免只凭动作标签或单个短指纹判重。
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Gurobi;

namespace GurobiTimetablingResearch.Optimization;

public enum RepairBranchExecutionState
{
    Generated,
    NotApplicable,
    Pending,
    Running,
    CandidateFound,
    ProvenInfeasible,
    TimedOutWithoutCandidate,
    UnknownWithoutCandidate,
    BudgetInsufficient,
    MappedToEquivalentBranch,
    EngineeringFailure
}

public sealed record RepairBranchModelContext(
    string CandidateFingerprint,
    string RepairReferenceFingerprint,
    IReadOnlyList<string> AppliedFeedbackIds,
    string ScopeFingerprint,
    string PlanId,
    RepairActionType ActionType,
    double RequestedSolveSeconds,
    ValidityFirstSolveParameterPlan SolveParameters,
    string ConfigurationFingerprint,
    IReadOnlyList<string> ActualFixedRequirementIds,
    IReadOnlyList<string> ActualReleasedRequirementIds,
    string MipStartFingerprint = "no_mip_start");

public sealed record RepairBranchModelIdentity(
    string PlanId,
    RepairActionType ActionType,
    string CandidateFingerprint,
    string RepairReferenceFingerprint,
    string AppliedFeedbackVersion,
    string ScopeFingerprint,
    string GurobiModelFingerprint,
    string MathematicalModelIdentity,
    string SearchAttemptIdentity,
    double RequestedSolveSeconds,
    ValidityFirstSolveParameterPlan SolveParameters,
    string ConfigurationFingerprint,
    IReadOnlyList<string> ActualFixedRequirementIds,
    IReadOnlyList<string> ActualReleasedRequirementIds,
    int VariableCount,
    int ConstraintCount,
    int NonzeroCount,
    string MipStartFingerprint = "no_mip_start");

/// <summary>
/// 在 Model.Update 之后读取 Gurobi 的最终模型指纹，并把候选、修复参考、反馈版本和实际临时界限共同纳入身份。
/// 数学模型身份不含求解时限；搜索身份再加入时限和求解参数，因此同一模型的更长预算可以作为有依据的新尝试。
/// </summary>
public static class RepairBranchModelIdentityBuilder
{
    public static RepairBranchModelIdentity Capture(
        BasicMilpModel model,
        RepairBranchModelContext context)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(context);
        Validate(context);
        model.Model.Update();
        string gurobiFingerprint = unchecked((uint)model.Model.Get(GRB.IntAttr.Fingerprint))
            .ToString("x8", CultureInfo.InvariantCulture);
        string feedbackVersion = Hash(string.Join('|', context.AppliedFeedbackIds
            .Order(StringComparer.OrdinalIgnoreCase)));
        IReadOnlyList<string> actualFixed = context.ActualFixedRequirementIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        IReadOnlyList<string> actualReleased = context.ActualReleasedRequirementIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        string mathematicalIdentity = BuildMathematicalIdentity(
            context.CandidateFingerprint,
            context.RepairReferenceFingerprint,
            feedbackVersion,
            context.ScopeFingerprint,
            gurobiFingerprint,
            actualFixed,
            actualReleased);
        string searchIdentity = BuildSearchIdentity(
            mathematicalIdentity,
            context.RequestedSolveSeconds,
            context.SolveParameters,
            context.ConfigurationFingerprint,
            context.MipStartFingerprint);
        return new RepairBranchModelIdentity(
            context.PlanId,
            context.ActionType,
            context.CandidateFingerprint,
            context.RepairReferenceFingerprint,
            feedbackVersion,
            context.ScopeFingerprint,
            gurobiFingerprint,
            mathematicalIdentity,
            searchIdentity,
            context.RequestedSolveSeconds,
            context.SolveParameters,
            context.ConfigurationFingerprint,
            actualFixed,
            actualReleased,
            model.Model.Get(GRB.IntAttr.NumVars),
            model.Model.Get(GRB.IntAttr.NumConstrs),
            model.Model.Get(GRB.IntAttr.NumNZs),
            context.MipStartFingerprint);
    }

    public static RepairBranchModelIdentity WithRequestedSolveSeconds(
        RepairBranchModelIdentity identity,
        double requestedSolveSeconds)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!double.IsFinite(requestedSolveSeconds) || requestedSolveSeconds <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(requestedSolveSeconds));
        }
        return identity with
        {
            RequestedSolveSeconds = requestedSolveSeconds,
            SearchAttemptIdentity = BuildSearchIdentity(
                identity.MathematicalModelIdentity,
                requestedSolveSeconds,
                identity.SolveParameters,
                identity.ConfigurationFingerprint,
                identity.MipStartFingerprint)
        };
    }

    public static RepairBranchModelIdentity WithCandidateFingerprint(
        RepairBranchModelIdentity identity,
        string candidateFingerprint)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateFingerprint);
        string mathematicalIdentity = BuildMathematicalIdentity(
            candidateFingerprint,
            identity.RepairReferenceFingerprint,
            identity.AppliedFeedbackVersion,
            identity.ScopeFingerprint,
            identity.GurobiModelFingerprint,
            identity.ActualFixedRequirementIds,
            identity.ActualReleasedRequirementIds);
        return identity with
        {
            CandidateFingerprint = candidateFingerprint,
            MathematicalModelIdentity = mathematicalIdentity,
            SearchAttemptIdentity = BuildSearchIdentity(
                mathematicalIdentity,
                identity.RequestedSolveSeconds,
                identity.SolveParameters,
                identity.ConfigurationFingerprint,
                identity.MipStartFingerprint)
        };
    }

    private static void Validate(RepairBranchModelContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(context.CandidateFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.RepairReferenceFingerprint);
        ArgumentNullException.ThrowIfNull(context.AppliedFeedbackIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.ScopeFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.PlanId);
        ArgumentNullException.ThrowIfNull(context.SolveParameters);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.ConfigurationFingerprint);
        ArgumentNullException.ThrowIfNull(context.ActualFixedRequirementIds);
        ArgumentNullException.ThrowIfNull(context.ActualReleasedRequirementIds);
        if (!double.IsFinite(context.RequestedSolveSeconds) || context.RequestedSolveSeconds <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(context.RequestedSolveSeconds));
        }
    }

    private static string BuildMathematicalIdentity(
        string candidateFingerprint,
        string repairReferenceFingerprint,
        string feedbackVersion,
        string scopeFingerprint,
        string gurobiFingerprint,
        IReadOnlyList<string> actualFixed,
        IReadOnlyList<string> actualReleased) =>
        Hash(string.Join('\n',
            candidateFingerprint,
            repairReferenceFingerprint,
            feedbackVersion,
            scopeFingerprint,
            gurobiFingerprint,
            string.Join('|', actualFixed),
            string.Join('|', actualReleased)));

    private static string BuildSearchIdentity(
        string mathematicalIdentity,
        double requestedSolveSeconds,
        ValidityFirstSolveParameterPlan parameters,
        string configurationFingerprint,
        string mipStartFingerprint) =>
        Hash(string.Join('|',
            mathematicalIdentity,
            requestedSolveSeconds.ToString("R", CultureInfo.InvariantCulture),
            parameters.ObjectiveKind,
            parameters.MipFocus.ToString(CultureInfo.InvariantCulture),
            parameters.MipGap.ToString("R", CultureInfo.InvariantCulture),
            parameters.SolutionLimit?.ToString(CultureInfo.InvariantCulture) ?? "default",
            configurationFingerprint,
            mipStartFingerprint));

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public sealed record RepairBranchExecutionDecision(
    bool ShouldExecute,
    string MappedToPlanId,
    string Reason);

/// <summary>
/// 只复用完全相同的搜索身份；同一数学模型若使用更长预算或不同参数，仍保留为独立搜索。
/// 候选、修复参考和反馈版本都属于数学身份，因此状态变化后不会继承旧分支结论。
/// </summary>
public sealed class RepairBranchAttemptRegistry
{
    private readonly Dictionary<string, string> _startedSearches = new(StringComparer.Ordinal);

    public RepairBranchExecutionDecision Evaluate(RepairBranchModelIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return _startedSearches.TryGetValue(identity.SearchAttemptIdentity, out string? planId)
            ? new RepairBranchExecutionDecision(false, planId, "same_model_state_parameters_start_and_budget")
            : new RepairBranchExecutionDecision(true, string.Empty, "new_search_attempt");
    }

    public void RecordStarted(RepairBranchModelIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        _startedSearches.TryAdd(identity.SearchAttemptIdentity, identity.PlanId);
    }
}

public sealed record RepairBranchExecutionLedgerEntry(
    DateTimeOffset Timestamp,
    int IterationIndex,
    string Phase,
    string CandidateFingerprint,
    string RepairReferenceFingerprint,
    string PlanId,
    RepairActionType Source,
    int EndpointOrderChangeBudget,
    IReadOnlyList<string> OriginalRequirementIds,
    IReadOnlyList<string> PlannedFixedRequirementIds,
    IReadOnlyList<string> PlannedReleasedRequirementIds,
    IReadOnlyList<string> ActualFixedRequirementIds,
    IReadOnlyList<string> ActualReleasedRequirementIds,
    IReadOnlyList<string> Preconditions,
    string MathematicalModelIdentity,
    string GurobiModelFingerprint,
    string SearchAttemptIdentity,
    string MappedToPlanId,
    RepairActionBudgetTier BudgetTier,
    double RequestedSolveSeconds,
    double? ActualSolveSeconds,
    RepairBranchExecutionState State,
    string SolverStatus,
    string Reason,
    int? VariableCount = null,
    int? ConstraintCount = null,
    int? NonzeroCount = null,
    int? ActualFixedChoiceVariableCount = null);

/// <summary>
/// 以追加事件的方式保存分支生命周期，使生成、映射、运行和终止状态不会互相覆盖。
/// </summary>
public static class RepairBranchExecutionLedgerReporter
{
    private static readonly object Sync = new();

    public static void AppendSelection(
        string outputDirectory,
        int iterationIndex,
        string phase,
        RepairActionSelectionResult selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        foreach (RepairActionPlan plan in selection.AllActionPlans)
        {
            RepairBranchExecutionState state = !string.IsNullOrWhiteSpace(plan.MappedToPlanId)
                ? RepairBranchExecutionState.MappedToEquivalentBranch
                : plan.Selected
                    ? RepairBranchExecutionState.Pending
                    : plan.Applicability is RepairActionApplicability.NotApplicable or
                        RepairActionApplicability.Undecided
                        ? RepairBranchExecutionState.NotApplicable
                        : RepairBranchExecutionState.Generated;
            RepairBranchExecutionLedgerEntry baseEntry = new(
                DateTimeOffset.Now,
                iterationIndex,
                phase,
                plan.CandidateFingerprint,
                plan.RepairReferenceFingerprint,
                plan.PlanId,
                plan.ActionType,
                plan.EndpointOrderChangeBudget,
                plan.TriggerBusinessIds
                    .Concat(plan.LegacyReleasedRequirementIds)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                (plan.PlannedFixedRequirementIds ?? new HashSet<string>())
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                (plan.ExecutablePlan?.ReleasedRequirementIds ?? new HashSet<string>())
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                [],
                [],
                plan.ApplicabilityConditions,
                string.Empty,
                string.Empty,
                string.Empty,
                plan.MappedToPlanId,
                plan.BudgetTier,
                plan.RequestedSolveSeconds,
                null,
                state,
                string.Empty,
                state == RepairBranchExecutionState.MappedToEquivalentBranch
                    ? "planned_model_change_is_covered_by_mapped_branch"
                    : plan.DecisionReason);
            Append(outputDirectory, baseEntry with
            {
                State = RepairBranchExecutionState.Generated,
                SolverStatus = string.Empty,
                Reason = "branch_plan_generated"
            });
            if (state != RepairBranchExecutionState.Generated)
            {
                Append(outputDirectory, baseEntry);
            }
        }
    }

    public static void Append(string outputDirectory, RepairBranchExecutionLedgerEntry entry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(entry);
        Directory.CreateDirectory(outputDirectory);
        string path = Path.Combine(outputDirectory, "repair_branch_execution_ledger.csv");
        lock (Sync)
        {
            bool writeHeader = !File.Exists(path);
            using StreamWriter writer = new(path, true, new UTF8Encoding(false));
            if (writeHeader)
            {
                writer.WriteLine(
                    "timestamp,iteration,phase,candidate_fingerprint,repair_reference_fingerprint,plan_id,source,k," +
                    "original_requirement_ids,planned_fixed_ids,planned_released_ids,actual_fixed_ids,actual_released_ids," +
                    "preconditions,mathematical_model_identity,gurobi_model_fingerprint,search_attempt_identity," +
                    "mapped_to_plan_id,budget_tier,requested_seconds,actual_solve_seconds,state,solver_status,reason," +
                    "variables,constraints,nonzeros,actual_fixed_choice_variables");
            }
            writer.WriteLine(string.Join(',',
                entry.Timestamp.ToString("O", CultureInfo.InvariantCulture),
                entry.IterationIndex.ToString(CultureInfo.InvariantCulture),
                Csv(entry.Phase),
                Csv(entry.CandidateFingerprint),
                Csv(entry.RepairReferenceFingerprint),
                Csv(entry.PlanId),
                entry.Source,
                entry.EndpointOrderChangeBudget.ToString(CultureInfo.InvariantCulture),
                Csv(Join(entry.OriginalRequirementIds)),
                Csv(Join(entry.PlannedFixedRequirementIds)),
                Csv(Join(entry.PlannedReleasedRequirementIds)),
                Csv(Join(entry.ActualFixedRequirementIds)),
                Csv(Join(entry.ActualReleasedRequirementIds)),
                Csv(Join(entry.Preconditions)),
                entry.MathematicalModelIdentity,
                entry.GurobiModelFingerprint,
                entry.SearchAttemptIdentity,
                Csv(entry.MappedToPlanId),
                entry.BudgetTier,
                entry.RequestedSolveSeconds.ToString("R", CultureInfo.InvariantCulture),
                entry.ActualSolveSeconds?.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty,
                entry.State,
                Csv(entry.SolverStatus),
                Csv(entry.Reason),
                entry.VariableCount?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                entry.ConstraintCount?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                entry.NonzeroCount?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                entry.ActualFixedChoiceVariableCount?.ToString(CultureInfo.InvariantCulture) ?? string.Empty));
        }
    }

    private static string Join(IEnumerable<string> values) =>
        string.Join('|', values.Order(StringComparer.OrdinalIgnoreCase));

    private static string Csv(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) < 0
            ? value
            : $"\"{value.Replace("\"", "\"\"")}\"";
}
