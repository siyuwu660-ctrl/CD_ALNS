using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Gurobi;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

internal readonly record struct ContinuousInterfaceKey(string StopId, StationEventSide EventSide)
{
    public override string ToString() => $"{StopId}:{EventSide}";
}

internal sealed record ContinuousInterfaceValue(ContinuousInterfaceKey Key, double Value);

internal sealed record ContinuousInterfaceTerm(ContinuousInterfaceKey Key, double Coefficient);

internal sealed record ContinuousExtendabilityContext(
    string MathematicalModelIdentity,
    string MacroDiscreteIdentity,
    string OutsidePatternIdentity,
    IReadOnlyList<string> ActiveStations,
    IReadOnlyDictionary<string, int> ActiveStopState,
    string ContextIdentity)
{
    internal static ContinuousExtendabilityContext Create(
        string mathematicalModelIdentity,
        string macroDiscreteIdentity,
        string outsidePatternIdentity,
        IReadOnlyList<string> activeStations,
        IReadOnlyDictionary<string, int> activeStopState)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mathematicalModelIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(macroDiscreteIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(outsidePatternIdentity);
        ArgumentNullException.ThrowIfNull(activeStations);
        ArgumentNullException.ThrowIfNull(activeStopState);
        string[] stations = activeStations.Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        Dictionary<string, int> stops = activeStopState
            .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
        if (stops.Values.Any(value => value is not 0 and not 1))
            throw new InvalidDataException("An extendability context contains a non-binary stop state.");
        string identity = Hash(string.Join('\n',
            mathematicalModelIdentity,
            macroDiscreteIdentity,
            outsidePatternIdentity,
            string.Join('|', stations),
            string.Join('|', stops.Select(item => $"{item.Key}={item.Value}"))));
        return new(mathematicalModelIdentity, macroDiscreteIdentity, outsidePatternIdentity,
            stations, stops, identity);
    }

    internal bool HasValidIdentity() => string.Equals(
        ContextIdentity,
        Create(MathematicalModelIdentity, MacroDiscreteIdentity, OutsidePatternIdentity,
            ActiveStations, ActiveStopState).ContextIdentity,
        StringComparison.Ordinal);

    internal static string Hash(string text) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}

internal sealed record ContinuousExtendabilityFeedback(
    string FeedbackId,
    string ProofIdentity,
    ContinuousExtendabilityContext Context,
    IReadOnlyList<ContinuousInterfaceTerm> Terms,
    double RightHandSide,
    IReadOnlyList<ContinuousInterfaceValue> RejectedInterface,
    double ViolationAtRejectedPoint,
    int RayRowCount,
    double MaximumEliminationResidual,
    bool UsesExplicitBoundRows)
{
    internal static ContinuousExtendabilityFeedback Create(
        string proofIdentity,
        ContinuousExtendabilityContext context,
        IReadOnlyList<ContinuousInterfaceTerm> terms,
        double rightHandSide,
        IReadOnlyDictionary<ContinuousInterfaceKey, double> rejectedInterface,
        int rayRowCount,
        double maximumEliminationResidual,
        bool usesExplicitBoundRows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(proofIdentity);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(terms);
        ArgumentNullException.ThrowIfNull(rejectedInterface);
        ContinuousInterfaceTerm[] canonicalTerms = terms
            .Where(term => Math.Abs(term.Coefficient) > 1e-12)
            .OrderBy(term => term.Key.StopId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(term => term.Key.EventSide)
            .ToArray();
        if (canonicalTerms.Length == 0)
            throw new InvalidDataException("An extendability feedback row must contain an interface term.");
        if (canonicalTerms.Select(term => term.Key).Distinct().Count() != canonicalTerms.Length)
            throw new InvalidDataException("An extendability feedback row contains duplicate interface keys.");
        ContinuousInterfaceValue[] rejected = rejectedInterface
            .OrderBy(item => item.Key.StopId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Key.EventSide)
            .Select(item => new ContinuousInterfaceValue(item.Key, item.Value)).ToArray();
        Dictionary<ContinuousInterfaceKey, double> rejectedMap = rejected.ToDictionary(item => item.Key, item => item.Value);
        double violation = canonicalTerms.Sum(term => term.Coefficient * rejectedMap[term.Key]) - rightHandSide;
        string id = ContinuousExtendabilityContext.Hash(string.Join('\n',
            proofIdentity,
            context.ContextIdentity,
            string.Join('|', canonicalTerms.Select(term => $"{term.Key}={term.Coefficient:R}")),
            rightHandSide.ToString("R"),
            string.Join('|', rejected.Select(value => $"{value.Key}={value.Value:R}")),
            rayRowCount.ToString(),
            maximumEliminationResidual.ToString("R"),
            usesExplicitBoundRows.ToString()));
        return new(id, proofIdentity, context, canonicalTerms, rightHandSide, rejected,
            violation, rayRowCount, maximumEliminationResidual, usesExplicitBoundRows);
    }

    internal bool AppliesTo(ContinuousExtendabilityContext context) =>
        Context.HasValidIdentity() && context.HasValidIdentity() &&
        string.Equals(Context.ContextIdentity, context.ContextIdentity, StringComparison.Ordinal);

    /// <summary>
    /// Tests whether the proof row can be installed in a rebuilt local model.  The stop
    /// vector is intentionally not part of this structural test: every row already carries
    /// an exact stop guard below.  All matrix-changing identities remain exact.
    /// </summary>
    internal ContinuousFeedbackApplicability AssessApplicability(ContinuousExtendabilityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!HasValidIdentity() || !Context.HasValidIdentity() || !context.HasValidIdentity())
            return new(false, false, ContinuousFeedbackApplicabilityReason.InvalidIdentity,
                "feedback or context content identity is invalid");
        if (!Context.ActiveStations.ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(context.ActiveStations))
            return new(false, false, ContinuousFeedbackApplicabilityReason.ActiveStationsChanged,
                "active station set changed");
        if (!string.Equals(Context.OutsidePatternIdentity, context.OutsidePatternIdentity, StringComparison.Ordinal))
            return new(false, false, ContinuousFeedbackApplicabilityReason.OutsidePatternChanged,
                "outside resource-pattern identity changed");
        if (!string.Equals(Context.MacroDiscreteIdentity, context.MacroDiscreteIdentity, StringComparison.Ordinal))
            return new(false, false, ContinuousFeedbackApplicabilityReason.MacroDiscreteChanged,
                "retained Macro discrete identity changed");
        if (!string.Equals(Context.MathematicalModelIdentity, context.MathematicalModelIdentity, StringComparison.Ordinal))
            return new(false, false, ContinuousFeedbackApplicabilityReason.MathematicalModelChanged,
                "continuous mathematical model identity changed");
        bool active = Context.ActiveStopState.All(expected =>
            context.ActiveStopState.TryGetValue(expected.Key, out int actual) && actual == expected.Value);
        return active
            ? new(true, true, ContinuousFeedbackApplicabilityReason.EligibleAndActive,
                "structural dependencies and stop guard match")
            : new(true, false, ContinuousFeedbackApplicabilityReason.EligibleButStopGuardInactive,
                "structural dependencies match; the exact stop guard is currently inactive");
    }

    internal bool AppliesToModel(ContinuousExtendabilityContext context) =>
        AssessApplicability(context).ModelEligible;

    internal bool IsSatisfied(
        IReadOnlyDictionary<ContinuousInterfaceKey, double> values,
        double tolerance = 1e-7) =>
        Terms.Sum(term => term.Coefficient * values[term.Key]) <= RightHandSide + tolerance;

    internal bool HasValidIdentity()
    {
        try
        {
            Dictionary<ContinuousInterfaceKey, double> rejected = RejectedInterface.ToDictionary(
                item => item.Key, item => item.Value);
            ContinuousExtendabilityFeedback rebuilt = Create(
                ProofIdentity, Context, Terms, RightHandSide, rejected,
                RayRowCount, MaximumEliminationResidual, UsesExplicitBoundRows);
            return string.Equals(FeedbackId, rebuilt.FeedbackId, StringComparison.Ordinal) &&
                   Math.Abs(ViolationAtRejectedPoint - rebuilt.ViolationAtRejectedPoint) <= 1e-9;
        }
        catch
        {
            return false;
        }
    }
}

internal enum ContinuousFeedbackApplicabilityReason
{
    InvalidIdentity,
    ActiveStationsChanged,
    OutsidePatternChanged,
    MacroDiscreteChanged,
    MathematicalModelChanged,
    EligibleButStopGuardInactive,
    EligibleAndActive
}

internal sealed record ContinuousFeedbackApplicability(
    bool ModelEligible,
    bool StopGuardActive,
    ContinuousFeedbackApplicabilityReason ReasonCode,
    string Reason);

internal sealed record ContinuousInterfaceFixBinding(
    ContinuousInterfaceKey Key,
    GRBVar Variable,
    GRBConstr FixConstraint,
    double Value);

internal sealed record FarkasBoundaryGenerationResult(
    ContinuousExtendabilityFeedback? Feedback,
    string Reason,
    int NonzeroRayRowCount,
    double MaximumEliminationResidual,
    double RejectedPointViolation);

internal static class FarkasBoundaryFeedbackGenerator
{
    internal static FarkasBoundaryGenerationResult TryGenerate(
        GRBModel model,
        ContinuousExtendabilityContext context,
        IReadOnlyList<ContinuousInterfaceFixBinding> interfaceFixes,
        double coefficientTolerance = 1e-8,
        double eliminationTolerance = 1e-6)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(interfaceFixes);
        if (model.Get(GRB.IntAttr.Status) != GRB.Status.INFEASIBLE)
            return new(null, "status_does_not_prove_infeasibility", 0, double.NaN, double.NaN);
        if (interfaceFixes.Count == 0)
            return new(null, "no_interface_fix_rows", 0, double.NaN, double.NaN);
        if (interfaceFixes.Select(item => item.Key).Distinct().Count() != interfaceFixes.Count)
            return new(null, "duplicate_interface_keys", 0, double.NaN, double.NaN);

        // Every finite variable bound must already be represented by an ordinary row.
        // Leaving a native bound in place would make the row multipliers incomplete.
        foreach (GRBVar variable in model.GetVars())
        {
            if (variable.Get(GRB.DoubleAttr.LB) > -GRB.INFINITY / 2.0 ||
                variable.Get(GRB.DoubleAttr.UB) < GRB.INFINITY / 2.0)
                return new(null, $"native_bound_not_materialized:{variable.Get(GRB.StringAttr.VarName)}",
                    0, double.NaN, double.NaN);
        }

        Dictionary<string, ContinuousInterfaceFixBinding> interfaceByVariable = interfaceFixes.ToDictionary(
            item => item.Variable.Get(GRB.StringAttr.VarName), StringComparer.Ordinal);
        HashSet<string> fixNames = interfaceFixes.Select(item =>
            item.FixConstraint.Get(GRB.StringAttr.ConstrName)).ToHashSet(StringComparer.Ordinal);
        Dictionary<string, double> aggregateByVariable = new(StringComparer.Ordinal);
        double aggregateRightHandSide = 0;
        int nonzeroRows = 0;
        List<string> signFailures = [];
        foreach (GRBConstr constraint in model.GetConstrs())
        {
            double multiplier;
            try { multiplier = constraint.Get(GRB.DoubleAttr.FarkasDual); }
            catch (GRBException exception)
            {
                return new(null, $"farkas_dual_unavailable:{exception.ErrorCode}", 0, double.NaN, double.NaN);
            }
            if (Math.Abs(multiplier) <= coefficientTolerance) continue;
            nonzeroRows++;
            char sense = constraint.Get(GRB.CharAttr.Sense);
            if ((sense == GRB.LESS_EQUAL && multiplier < -coefficientTolerance) ||
                (sense == GRB.GREATER_EQUAL && multiplier > coefficientTolerance))
                signFailures.Add(constraint.Get(GRB.StringAttr.ConstrName));
            if (fixNames.Contains(constraint.Get(GRB.StringAttr.ConstrName))) continue;
            GRBLinExpr row = model.GetRow(constraint);
            for (int index = 0; index < row.Size; index++)
            {
                string variableName = row.GetVar(index).Get(GRB.StringAttr.VarName);
                aggregateByVariable[variableName] = aggregateByVariable.GetValueOrDefault(variableName) +
                                                    multiplier * row.GetCoeff(index);
            }
            aggregateRightHandSide += multiplier * constraint.Get(GRB.DoubleAttr.RHS);
        }
        if (nonzeroRows == 0)
            return new(null, "no_nonzero_farkas_multipliers", 0, double.NaN, double.NaN);
        if (signFailures.Count > 0)
            return new(null, $"farkas_multiplier_sign_mismatch:{string.Join('|', signFailures.Take(5))}",
                nonzeroRows, double.NaN, double.NaN);

        double maximumEliminationResidual = aggregateByVariable
            .Where(item => !interfaceByVariable.ContainsKey(item.Key))
            .Select(item => Math.Abs(item.Value)).DefaultIfEmpty(0).Max();
        if (maximumEliminationResidual > eliminationTolerance)
            return new(null, $"outside_variable_not_eliminated:{maximumEliminationResidual:R}",
                nonzeroRows, maximumEliminationResidual, double.NaN);

        List<ContinuousInterfaceTerm> terms = [];
        Dictionary<ContinuousInterfaceKey, double> rejected = [];
        foreach ((string variableName, ContinuousInterfaceFixBinding binding) in interfaceByVariable)
        {
            double coefficient = aggregateByVariable.GetValueOrDefault(variableName);
            if (Math.Abs(coefficient) > coefficientTolerance)
                terms.Add(new(binding.Key, coefficient));
            rejected[binding.Key] = binding.Value;
        }
        if (terms.Count == 0)
            return new(null, "infeasibility_ray_has_no_active_interface_component",
                nonzeroRows, maximumEliminationResidual, double.NaN);

        double scale = terms.Max(term => Math.Abs(term.Coefficient));
        terms = terms.Select(term => term with { Coefficient = term.Coefficient / scale }).ToList();
        double rhs = aggregateRightHandSide / scale;
        double violation = terms.Sum(term => term.Coefficient * rejected[term.Key]) - rhs;
        if (violation <= eliminationTolerance)
            return new(null, $"ray_does_not_separate_rejected_interface:{violation:R}",
                nonzeroRows, maximumEliminationResidual, violation);

        string proofIdentity = ContinuousExtendabilityContext.Hash(string.Join('\n',
            context.ContextIdentity,
            model.Get(GRB.StringAttr.ModelName),
            nonzeroRows.ToString(),
            string.Join('|', terms.Select(term => $"{term.Key}={term.Coefficient:R}")),
            rhs.ToString("R")));
        ContinuousExtendabilityFeedback feedback = ContinuousExtendabilityFeedback.Create(
            proofIdentity, context, terms, rhs, rejected, nonzeroRows,
            maximumEliminationResidual, usesExplicitBoundRows: true);
        return new(feedback, "valid_bound_complete_farkas_boundary", nonzeroRows,
            maximumEliminationResidual, feedback.ViolationAtRejectedPoint);
    }
}

internal static class ContinuousExtendabilityFeedbackStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    internal static void Save(string path, IReadOnlyList<ContinuousExtendabilityFeedback> feedback)
    {
        ArgumentNullException.ThrowIfNull(feedback);
        if (feedback.Any(item => !item.HasValidIdentity()))
            throw new InvalidDataException("Refusing to save extendability feedback with an invalid content identity.");
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(path, JsonSerializer.Serialize(feedback, Options), new UTF8Encoding(false));
    }

    internal static IReadOnlyList<ContinuousExtendabilityFeedback> Load(string path)
    {
        List<ContinuousExtendabilityFeedback> result = JsonSerializer.Deserialize<List<ContinuousExtendabilityFeedback>>(
            File.ReadAllText(path), Options) ?? [];
        if (result.Any(item => !item.HasValidIdentity()))
            throw new InvalidDataException("Saved extendability feedback failed its mathematical content identity check.");
        return result;
    }
}

internal sealed record LocalExtendabilityFeedbackApplication(
    IReadOnlyList<string> AddedConstraintIds,
    IReadOnlyList<string> EligibleFeedbackIds,
    IReadOnlyList<string> AlreadyPresentFeedbackIds,
    IReadOnlyList<string> SkippedFeedbackIds,
    IReadOnlyDictionary<ContinuousFeedbackApplicabilityReason, int> SkippedByReason,
    double MaximumDeactivationM);

internal static class LocalExtendabilityFeedbackBinder
{
    internal static LocalExtendabilityFeedbackApplication Apply(
        BasicMilpModel model,
        ContinuousExtendabilityContext context,
        IReadOnlyList<ContinuousExtendabilityFeedback> feedback)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(feedback);
        model.Model.Update();
        List<string> added = [];
        List<string> eligible = [];
        List<string> skipped = [];
        Dictionary<ContinuousFeedbackApplicabilityReason, int> skippedByReason = [];
        double maximumM = 0;
        foreach (ContinuousExtendabilityFeedback item in feedback)
        {
            ContinuousFeedbackApplicability applicability = item.AssessApplicability(context);
            if (!applicability.ModelEligible)
            {
                skipped.Add(item.FeedbackId);
                skippedByReason[applicability.ReasonCode] =
                    skippedByReason.GetValueOrDefault(applicability.ReasonCode) + 1;
                continue;
            }
            eligible.Add(item.FeedbackId);
            GRBLinExpr expression = new();
            double maximumLeftHandSide = 0;
            foreach (ContinuousInterfaceTerm term in item.Terms)
            {
                IReadOnlyDictionary<string, GRBVar> source = term.Key.EventSide == StationEventSide.Incoming
                    ? model.ArrivalVars
                    : model.DepartureVars;
                if (!source.TryGetValue(term.Key.StopId, out GRBVar? variable))
                    throw new InvalidDataException(
                        $"Extendability feedback '{item.FeedbackId}' references interface '{term.Key}' outside the local model.");
                double lower = variable.Get(GRB.DoubleAttr.LB);
                double upper = variable.Get(GRB.DoubleAttr.UB);
                if (lower <= -GRB.INFINITY / 2.0 || upper >= GRB.INFINITY / 2.0)
                    throw new InvalidDataException(
                        $"Local interface '{term.Key}' needs finite original bounds for a conditional feedback row.");
                expression.AddTerm(term.Coefficient, variable);
                maximumLeftHandSide += term.Coefficient >= 0
                    ? term.Coefficient * upper
                    : term.Coefficient * lower;
            }

            double deactivationM = Math.Max(0, maximumLeftHandSide - item.RightHandSide);
            maximumM = Math.Max(maximumM, deactivationM);
            double rhs = item.RightHandSide;
            foreach ((string stopId, int expected) in item.Context.ActiveStopState)
            {
                if (!model.StopVars.TryGetValue(stopId, out GRBVar? stopVariable))
                    throw new InvalidDataException(
                        $"Extendability feedback '{item.FeedbackId}' stop guard '{stopId}' is outside the local model.");
                if (expected == 1)
                {
                    // lhs <= rhs + M(1-s)  <=>  lhs + M s <= rhs + M
                    expression.AddTerm(deactivationM, stopVariable);
                    rhs += deactivationM;
                }
                else
                {
                    // lhs <= rhs + M s  <=>  lhs - M s <= rhs
                    expression.AddTerm(-deactivationM, stopVariable);
                }
            }
            string constraintId = $"extend_feedback_{item.FeedbackId[..Math.Min(20, item.FeedbackId.Length)]}";
            model.Model.AddConstr(expression, GRB.LESS_EQUAL, rhs, constraintId);
            added.Add(constraintId);
        }
        model.Model.Update();
        return new(added, eligible, [], skipped, skippedByReason, maximumM);
    }
}
