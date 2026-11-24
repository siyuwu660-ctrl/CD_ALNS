using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Gurobi;

namespace GurobiTimetablingResearch.Optimization;

internal sealed record CompactContinuousCompileAudit(
    int SourceVariableCount,
    int SourceConstraintCount,
    int FixedVariableCount,
    int CompactVariableCount,
    int CompactConstraintCount,
    int DifferenceNodeCount,
    int DifferenceEdgeCount,
    int ResidualConstraintCount,
    int ConstantSatisfiedRowCount);

internal sealed record CompactDifferenceEdge(
    string FromVariableName,
    string ToVariableName,
    double UpperBound,
    string SourceConstraintName,
    string? PatternStation,
    IReadOnlyDictionary<string, double> FixedDependencies,
    CompactCompileProof? SourceProof = null);

internal sealed record CompactContinuousSolveResult(
    int StatusCode,
    string Status,
    bool HasSolution,
    double RuntimeSeconds,
    IReadOnlyDictionary<string, double> SourceValues,
    IReadOnlyList<CompactDifferenceEdge> NegativeCycleEdges,
    IReadOnlyList<ContinuousProofRow> ProofRows,
    string StopReason);

internal sealed record CompactFixedDecisionSource(
    string? DecisionLiteralKey,
    int? DecisionLiteralValue,
    string? PatternStation,
    string? PatternId);

internal sealed record CompactProofSourceRow(
    string ConstraintName,
    char Sense,
    double RightHandSide,
    IReadOnlyDictionary<string, double> Coefficients,
    double SubstitutedRightHandSide);

internal sealed record CompactCompileProof(
    string ProofIdentity,
    string ProofKind,
    string Trigger,
    double Violation,
    IReadOnlyList<CompactProofSourceRow> SourceRows,
    IReadOnlyDictionary<string, double> FixedValues,
    IReadOnlyDictionary<string, int> RequiredDiscreteLiterals,
    IReadOnlyDictionary<string, string> RequiredPatternsByStation);

/// <summary>
/// Exact fixed-discrete compiler. It substitutes only variables whose value follows from
/// explicit bounds and one-variable rows. Every remaining row is retained in the compact LP;
/// rows of the form x-y&lt;=c are additionally indexed as a difference graph. Consequently the
/// graph is useful for certificates without becoming a relaxation of the source model.
/// </summary>
internal static class CompactContinuousTimeCompiler
{
    private const double Tolerance = 1e-8;
    internal const string Origin = "@origin";

    internal static CompactContinuousTimeModel Compile(
        GRBModel source,
        IReadOnlyDictionary<string, string> patternStationByConstraint,
        IReadOnlyDictionary<string, CompactFixedDecisionSource>? fixedDecisionSources = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(patternStationByConstraint);
        fixedDecisionSources ??= new Dictionary<string, CompactFixedDecisionSource>(StringComparer.Ordinal);
        source.Update();
        if (source.NumSOS != 0 || source.NumGenConstrs != 0 || source.NumQConstrs != 0)
            throw new InvalidOperationException("Exact continuous compiler accepts linear rows only; unsupported source constraints cannot be omitted.");
        GRBVar[] sourceVariables = source.GetVars();
        GRBConstr[] sourceConstraints = source.GetConstrs();
        Dictionary<string, GRBVar> variableByName = sourceVariables.ToDictionary(
            variable => variable.Get(GRB.StringAttr.VarName), StringComparer.Ordinal);
        List<SourceRow> rows = sourceConstraints.Select(constraint => ReadRow(source, constraint)).ToList();
        Dictionary<string, SourceRow> proofRowsByName = rows.ToDictionary(row => row.Name, StringComparer.Ordinal);

        Dictionary<string, double> lower = new(StringComparer.Ordinal);
        Dictionary<string, double> upper = new(StringComparer.Ordinal);
        Dictionary<string, double> fixedValues = new(StringComparer.Ordinal);
        Dictionary<string, ProofSupport> lowerSupport = new(StringComparer.Ordinal);
        Dictionary<string, ProofSupport> upperSupport = new(StringComparer.Ordinal);
        Dictionary<string, ProofSupport> fixedSupport = new(StringComparer.Ordinal);
        foreach (GRBVar variable in sourceVariables)
        {
            string name = variable.Get(GRB.StringAttr.VarName);
            double lb = variable.Get(GRB.DoubleAttr.LB);
            double ub = variable.Get(GRB.DoubleAttr.UB);
            if (lb > -GRB.INFINITY / 2.0)
            {
                lower[name] = lb;
                string rowName = $"native_bound_lb:{name}";
                lowerSupport[name] = ProofSupport.FromRow(rowName);
                proofRowsByName[rowName] = new(rowName, GRB.GREATER_EQUAL, lb,
                    new Dictionary<string, double>(StringComparer.Ordinal) { [name] = 1 });
            }
            if (ub < GRB.INFINITY / 2.0)
            {
                upper[name] = ub;
                string rowName = $"native_bound_ub:{name}";
                upperSupport[name] = ProofSupport.FromRow(rowName);
                proofRowsByName[rowName] = new(rowName, GRB.LESS_EQUAL, ub,
                    new Dictionary<string, double>(StringComparer.Ordinal) { [name] = 1 });
            }
            if (IsFinite(lb) && IsFinite(ub) && Math.Abs(lb - ub) <= Tolerance)
            {
                fixedValues[name] = (lb + ub) / 2.0;
                fixedSupport[name] = ProofSupport.Combine(lowerSupport[name], upperSupport[name]).WithFixed(name);
            }
        }

        bool changed;
        do
        {
            changed = false;
            foreach (SourceRow row in rows)
            foreach (ReducedInequality inequality in Reduce(row, fixedValues))
            {
                if (inequality.Coefficients.Count != 1) continue;
                (string name, double coefficient) = inequality.Coefficients.Single();
                if (Math.Abs(coefficient) <= Tolerance) continue;
                double bound = inequality.RightHandSide / coefficient;
                ProofSupport support = BuildSupport(inequality, fixedSupport);
                if (coefficient > 0)
                {
                    if (Tighten(upper, name, bound, takeMaximum: false))
                    {
                        upperSupport[name] = support;
                        changed = true;
                    }
                }
                else
                {
                    if (Tighten(lower, name, bound, takeMaximum: true))
                    {
                        lowerSupport[name] = support;
                        changed = true;
                    }
                }
            }
            foreach (string name in variableByName.Keys)
            {
                if (fixedValues.ContainsKey(name) || !lower.TryGetValue(name, out double lb) ||
                    !upper.TryGetValue(name, out double ub)) continue;
                if (lb > ub + Tolerance)
                {
                    ProofSupport support = ProofSupport.Combine(
                        lowerSupport.GetValueOrDefault(name) ?? ProofSupport.Empty,
                        upperSupport.GetValueOrDefault(name) ?? ProofSupport.Empty).WithFixed(name);
                    CompactCompileProof proof = BuildCompileProof(
                        "derived_bound_contradiction", name, lb - ub, support,
                        proofRowsByName, fixedValues, fixedDecisionSources);
                    return CompactContinuousTimeModel.InfeasibleAtCompile(
                        source.Get(GRB.StringAttr.ModelName), sourceVariables.Length, sourceConstraints.Length,
                        fixedValues, $"inconsistent derived bounds for {name}: {lb:R}>{ub:R}", proof);
                }
                if (Math.Abs(lb - ub) <= Tolerance)
                {
                    fixedValues[name] = (lb + ub) / 2.0;
                    fixedSupport[name] = ProofSupport.Combine(
                        lowerSupport.GetValueOrDefault(name) ?? ProofSupport.Empty,
                        upperSupport.GetValueOrDefault(name) ?? ProofSupport.Empty).WithFixed(name);
                    changed = true;
                }
            }
        } while (changed);

        foreach (GRBVar variable in sourceVariables)
        {
            char type=variable.VType;
            if (type == GRB.CONTINUOUS) continue;
            string name=variable.VarName;
            if (type is GRB.SEMICONT or GRB.SEMIINT || !fixedValues.TryGetValue(name,out double value) ||
                Math.Abs(value-Math.Round(value))>Tolerance)
                throw new InvalidOperationException($"Exact fixed-discrete compilation refused unfixed/nonintegral variable '{name}'. Retain the MILP until all discrete domains are fixed.");
        }

        List<ReducedInequality> reducedRows = [];
        int constantSatisfied = 0;
        foreach (SourceRow row in rows)
        foreach (ReducedInequality inequality in Reduce(row, fixedValues))
        {
            if (inequality.Coefficients.Count == 0)
            {
                if (inequality.RightHandSide < -Tolerance)
                {
                    ProofSupport support = BuildSupport(inequality, fixedSupport);
                    CompactCompileProof proof = BuildCompileProof(
                        "constant_substitution_contradiction", row.Name, -inequality.RightHandSide,
                        support, proofRowsByName, fixedValues, fixedDecisionSources);
                    return CompactContinuousTimeModel.InfeasibleAtCompile(
                        source.Get(GRB.StringAttr.ModelName), sourceVariables.Length, sourceConstraints.Length,
                        fixedValues, $"constant row violated:{row.Name}", proof);
                }
                constantSatisfied++;
                continue;
            }
            reducedRows.Add(inequality);
        }

        HashSet<string> retainedNames = reducedRows.SelectMany(row => row.Coefficients.Keys)
            .ToHashSet(StringComparer.Ordinal);
        GRBEnv compactEnvironment = new();
        GRBModel compact = new(compactEnvironment)
        {
            ModelName = $"compact::{source.Get(GRB.StringAttr.ModelName)}"
        };
        Dictionary<string, GRBVar> compactVariables = retainedNames.ToDictionary(
            name => name,
            name => compact.AddVar(-GRB.INFINITY, GRB.INFINITY, 0, GRB.CONTINUOUS, name),
            StringComparer.Ordinal);
        List<CompactDifferenceEdge> edges = [];
        int residual = 0;
        Dictionary<string, int> duplicateNames = new(StringComparer.Ordinal);
        foreach (ReducedInequality row in reducedRows)
        {
            string compactName = UniqueName(row.SourceConstraintName, duplicateNames);
            GRBLinExpr expression = new();
            foreach ((string name, double coefficient) in row.Coefficients)
                expression.AddTerm(coefficient, compactVariables[name]);
            compact.AddConstr(expression, GRB.LESS_EQUAL, row.RightHandSide, compactName);
            CompactCompileProof rowProof = BuildCompileProof(
                "difference_edge_support", row.SourceConstraintName, 0,
                BuildSupport(row, fixedSupport), proofRowsByName, fixedValues, fixedDecisionSources);
            if (TryCreateEdge(row, patternStationByConstraint, rowProof, out CompactDifferenceEdge? edge))
                edges.Add(edge!);
            else
                residual++;
        }
        compact.Set(GRB.IntAttr.NumObj, 0);
        compact.SetObjective(new GRBLinExpr(), GRB.MINIMIZE);
        compact.Update();
        IReadOnlyList<CompactDifferenceEdge> negativeCycle = FindNegativeCycle(edges);
        CompactContinuousCompileAudit audit = new(
            sourceVariables.Length, sourceConstraints.Length, fixedValues.Count,
            compactVariables.Count, reducedRows.Count,
            edges.SelectMany(edge => new[] { edge.FromVariableName, edge.ToVariableName })
                .Distinct(StringComparer.Ordinal).Count(),
            edges.Count, residual, constantSatisfied);
        return new CompactContinuousTimeModel(compactEnvironment, compact, fixedValues,
            compactVariables, edges, negativeCycle, audit, null, null);
    }

    private static ProofSupport BuildSupport(
        ReducedInequality inequality,
        IReadOnlyDictionary<string, ProofSupport> fixedSupport)
    {
        ProofSupport support = ProofSupport.FromRow(inequality.SourceConstraintName);
        foreach (string dependency in inequality.FixedDependencies.Keys)
        {
            support = ProofSupport.Combine(support,
                fixedSupport.GetValueOrDefault(dependency) ?? ProofSupport.Empty).WithFixed(dependency);
        }
        return support;
    }

    private static CompactCompileProof BuildCompileProof(
        string kind,
        string trigger,
        double violation,
        ProofSupport support,
        IReadOnlyDictionary<string, SourceRow> rowsByName,
        IReadOnlyDictionary<string, double> fixedValues,
        IReadOnlyDictionary<string, CompactFixedDecisionSource> decisionSources)
    {
        Dictionary<string, double> proofFixed = support.FixedVariables
            .Where(fixedValues.ContainsKey)
            .ToDictionary(name => name, name => fixedValues[name], StringComparer.Ordinal);
        Dictionary<string, int> literals = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> patterns = new(StringComparer.OrdinalIgnoreCase);
        foreach (string variable in support.FixedVariables.Order(StringComparer.Ordinal))
        {
            if (!decisionSources.TryGetValue(variable, out CompactFixedDecisionSource? decision)) continue;
            if (!string.IsNullOrWhiteSpace(decision.DecisionLiteralKey) && decision.DecisionLiteralValue is int value)
                literals[decision.DecisionLiteralKey] = value;
            if (!string.IsNullOrWhiteSpace(decision.PatternStation) && !string.IsNullOrWhiteSpace(decision.PatternId))
                patterns[decision.PatternStation] = decision.PatternId;
        }
        CompactProofSourceRow[] rows = support.SourceRows.Where(rowsByName.ContainsKey)
            .Select(name => rowsByName[name])
            .OrderBy(row => row.Name, StringComparer.Ordinal)
            .Select(row => new CompactProofSourceRow(row.Name, row.Sense, row.RightHandSide,
                new Dictionary<string, double>(row.Coefficients, StringComparer.Ordinal),
                row.RightHandSide - row.Coefficients.Where(item => proofFixed.ContainsKey(item.Key))
                    .Sum(item => item.Value * proofFixed[item.Key])))
            .ToArray();
        string canonical = string.Join('\n', kind, trigger, violation.ToString("R"),
            string.Join('|', rows.Select(row => $"{row.ConstraintName}:{row.Sense}:{row.RightHandSide:R}:" +
                string.Join(',', row.Coefficients.OrderBy(item => item.Key, StringComparer.Ordinal)
                    .Select(item => $"{item.Key}={item.Value:R}")))),
            string.Join('|', proofFixed.OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => $"{item.Key}={item.Value:R}")),
            string.Join('|', literals.OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => $"{item.Key}={item.Value}")),
            string.Join('|', patterns.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                .Select(item => $"{item.Key}={item.Value}")));
        string identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
        return new(identity, kind, trigger, violation, rows, proofFixed, literals, patterns);
    }

    private static SourceRow ReadRow(GRBModel model, GRBConstr constraint)
    {
        GRBLinExpr expression = model.GetRow(constraint);
        Dictionary<string, double> coefficients = new(StringComparer.Ordinal);
        for (int index = 0; index < expression.Size; index++)
        {
            string name = expression.GetVar(index).Get(GRB.StringAttr.VarName);
            coefficients[name] = coefficients.GetValueOrDefault(name) + expression.GetCoeff(index);
        }
        return new(constraint.Get(GRB.StringAttr.ConstrName), constraint.Get(GRB.CharAttr.Sense),
            constraint.Get(GRB.DoubleAttr.RHS), coefficients);
    }

    private static IEnumerable<ReducedInequality> Reduce(
        SourceRow row,
        IReadOnlyDictionary<string, double> fixedValues)
    {
        Dictionary<string, double> coefficients = new(StringComparer.Ordinal);
        double rhs = row.RightHandSide;
        Dictionary<string, double> dependencies = new(StringComparer.Ordinal);
        foreach ((string name, double coefficient) in row.Coefficients)
        {
            if (Math.Abs(coefficient) <= Tolerance) continue;
            if (fixedValues.TryGetValue(name, out double value))
            {
                rhs -= coefficient * value;
                dependencies[name] = value;
            }
            else
                coefficients[name] = coefficient;
        }
        if (row.Sense is GRB.LESS_EQUAL or GRB.EQUAL)
            yield return new(row.Name, coefficients, rhs, dependencies);
        if (row.Sense is GRB.GREATER_EQUAL or GRB.EQUAL)
            yield return new(row.Name,
                coefficients.ToDictionary(item => item.Key, item => -item.Value, StringComparer.Ordinal),
                -rhs, dependencies);
    }

    private static bool TryCreateEdge(
        ReducedInequality row,
        IReadOnlyDictionary<string, string> patternStationByConstraint,
        CompactCompileProof sourceProof,
        out CompactDifferenceEdge? edge)
    {
        string? patternStation = patternStationByConstraint.GetValueOrDefault(row.SourceConstraintName);
        if (row.Coefficients.Count == 1)
        {
            (string variable, double coefficient) = row.Coefficients.Single();
            if (coefficient > Tolerance)
                edge = new(Origin, variable, row.RightHandSide / coefficient,
                    row.SourceConstraintName, patternStation, row.FixedDependencies, sourceProof);
            else if (coefficient < -Tolerance)
                edge = new(variable, Origin, row.RightHandSide / -coefficient,
                    row.SourceConstraintName, patternStation, row.FixedDependencies, sourceProof);
            else
                edge = null;
            return edge is not null;
        }
        if (row.Coefficients.Count == 2)
        {
            KeyValuePair<string, double> positive = row.Coefficients.FirstOrDefault(item => item.Value > Tolerance);
            KeyValuePair<string, double> negative = row.Coefficients.FirstOrDefault(item => item.Value < -Tolerance);
            if (!string.IsNullOrEmpty(positive.Key) && !string.IsNullOrEmpty(negative.Key) &&
                Math.Abs(positive.Value + negative.Value) <=
                Tolerance * Math.Max(1.0, Math.Abs(positive.Value)))
            {
                edge = new(negative.Key, positive.Key, row.RightHandSide / positive.Value,
                    row.SourceConstraintName, patternStation, row.FixedDependencies, sourceProof);
                return true;
            }
        }
        edge = null;
        return false;
    }

    private static IReadOnlyList<CompactDifferenceEdge> FindNegativeCycle(
        IReadOnlyList<CompactDifferenceEdge> edges)
    {
        string[] vertices = edges.SelectMany(edge => new[] { edge.FromVariableName, edge.ToVariableName })
            .Distinct(StringComparer.Ordinal).ToArray();
        if (vertices.Length == 0) return [];
        Dictionary<string, double> distance = vertices.ToDictionary(item => item, _ => 0.0, StringComparer.Ordinal);
        Dictionary<string, CompactDifferenceEdge> predecessor = new(StringComparer.Ordinal);
        string? changed = null;
        for (int iteration = 0; iteration < vertices.Length; iteration++)
        {
            changed = null;
            foreach (CompactDifferenceEdge edge in edges)
            {
                if (distance[edge.ToVariableName] <= distance[edge.FromVariableName] + edge.UpperBound + Tolerance)
                    continue;
                distance[edge.ToVariableName] = distance[edge.FromVariableName] + edge.UpperBound;
                predecessor[edge.ToVariableName] = edge;
                changed = edge.ToVariableName;
            }
            if (changed is null) return [];
        }
        string cursor = changed!;
        for (int index = 0; index < vertices.Length; index++)
            cursor = predecessor.TryGetValue(cursor, out CompactDifferenceEdge? edge)
                ? edge.FromVariableName
                : cursor;
        string start = cursor;
        List<CompactDifferenceEdge> cycle = [];
        do
        {
            if (!predecessor.TryGetValue(cursor, out CompactDifferenceEdge? edge)) return [];
            cycle.Add(edge);
            cursor = edge.FromVariableName;
        } while (!string.Equals(cursor, start, StringComparison.Ordinal) && cycle.Count <= vertices.Length + 1);
        return cycle;
    }

    private static bool Tighten(
        IDictionary<string, double> bounds,
        string name,
        double candidate,
        bool takeMaximum)
    {
        if (!IsFinite(candidate)) return false;
        if (!bounds.TryGetValue(name, out double current) ||
            (takeMaximum ? candidate > current + Tolerance : candidate < current - Tolerance))
        {
            bounds[name] = candidate;
            return true;
        }
        return false;
    }

    private static string UniqueName(string source, IDictionary<string, int> counts)
    {
        int count = counts.TryGetValue(source, out int existing) ? existing : 0;
        counts[source] = count + 1;
        return count == 0 ? source : $"{source}__compact_{count}";
    }

    private static bool IsFinite(double value) =>
        value > -GRB.INFINITY / 2.0 && value < GRB.INFINITY / 2.0 && !double.IsNaN(value);

    private sealed record SourceRow(
        string Name,
        char Sense,
        double RightHandSide,
        IReadOnlyDictionary<string, double> Coefficients);

    private sealed record ReducedInequality(
        string SourceConstraintName,
        IReadOnlyDictionary<string, double> Coefficients,
        double RightHandSide,
        IReadOnlyDictionary<string, double> FixedDependencies);

    private sealed record ProofSupport(HashSet<string> SourceRows, HashSet<string> FixedVariables)
    {
        internal static ProofSupport Empty => new([], []);
        internal static ProofSupport FromRow(string row) => new([row], []);
        internal static ProofSupport Combine(ProofSupport first, ProofSupport second) => new(
            first.SourceRows.Concat(second.SourceRows).ToHashSet(StringComparer.Ordinal),
            first.FixedVariables.Concat(second.FixedVariables).ToHashSet(StringComparer.Ordinal));
        internal ProofSupport WithFixed(string variable) => new(
            SourceRows.ToHashSet(StringComparer.Ordinal),
            FixedVariables.Append(variable).ToHashSet(StringComparer.Ordinal));
    }
}

internal sealed class CompactContinuousTimeModel : IDisposable
{
    private readonly GRBEnv? _environment;
    private readonly GRBModel? _model;
    private readonly IReadOnlyDictionary<string, GRBVar> _variables;
    private readonly string? _compileInfeasibility;
    private readonly CompactCompileProof? _compileProof;
    private bool _disposed;

    internal CompactContinuousTimeModel(
        GRBEnv environment,
        GRBModel model,
        IReadOnlyDictionary<string, double> fixedValues,
        IReadOnlyDictionary<string, GRBVar> variables,
        IReadOnlyList<CompactDifferenceEdge> edges,
        IReadOnlyList<CompactDifferenceEdge> negativeCycle,
        CompactContinuousCompileAudit audit,
        string? compileInfeasibility,
        CompactCompileProof? compileProof)
    {
        _environment = environment;
        _model = model;
        FixedValues = fixedValues;
        _variables = variables;
        Edges = edges;
        NegativeCycleEdges = negativeCycle;
        Audit = audit;
        _compileInfeasibility = compileInfeasibility;
        _compileProof = compileProof;
    }

    internal CompactContinuousCompileAudit Audit { get; }
    internal IReadOnlyDictionary<string, double> FixedValues { get; }
    internal IReadOnlyList<CompactDifferenceEdge> Edges { get; }
    internal IReadOnlyList<CompactDifferenceEdge> NegativeCycleEdges { get; }
    internal GRBModel? Model => _model;

    internal static CompactContinuousTimeModel InfeasibleAtCompile(
        string modelName,
        int sourceVariableCount,
        int sourceConstraintCount,
        IReadOnlyDictionary<string, double> fixedValues,
        string reason,
        CompactCompileProof proof)
    {
        CompactContinuousCompileAudit audit = new(sourceVariableCount, sourceConstraintCount,
            fixedValues.Count, 0, 0, 0, 0, 0, 0);
        return new(null!, null!, new Dictionary<string, double>(fixedValues, StringComparer.Ordinal),
            new Dictionary<string, GRBVar>(), [], [], audit, reason, proof);
    }

    internal CompactContinuousSolveResult Solve(double timeLimitSeconds, string? logFile = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (timeLimitSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(timeLimitSeconds));
        if (_compileInfeasibility is not null)
        {
            ContinuousProofRow[] compileProofRows = _compileProof?.SourceRows.Select(row =>
            {
                (string kind, string id) = Classify(row.ConstraintName);
                return new ContinuousProofRow(row.ConstraintName, 0,
                    $"compact_compile_{_compileProof.ProofKind}", kind, id,
                    row.Sense, row.RightHandSide,
                    new Dictionary<string, double>(row.Coefficients, StringComparer.Ordinal),
                    new Dictionary<string, double>(_compileProof.FixedValues, StringComparer.Ordinal),
                    new Dictionary<string, int>(_compileProof.RequiredDiscreteLiterals, StringComparer.OrdinalIgnoreCase),
                    new Dictionary<string, string>(_compileProof.RequiredPatternsByStation, StringComparer.OrdinalIgnoreCase),
                    _compileProof.ProofIdentity, _compileProof.ProofKind,
                    row.SubstitutedRightHandSide, _compileProof.Violation);
            }).ToArray() ?? [];
            return new(GRB.Status.INFEASIBLE, "INFEASIBLE", false, 0,
                FixedValues, [], compileProofRows, $"compact_compile_infeasible:{_compileInfeasibility}");
        }
        if (NegativeCycleEdges.Count > 0)
        {
            string cycleIdentity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|',
                NegativeCycleEdges.Select(edge => edge.SourceProof?.ProofIdentity ?? edge.SourceConstraintName)))))
                .ToLowerInvariant();
            ContinuousProofRow[] proof = NegativeCycleEdges.SelectMany(edge =>
            {
                CompactCompileProof? sourceProof = edge.SourceProof;
                if (sourceProof is null)
                    return [new ContinuousProofRow(edge.SourceConstraintName, 0,
                        "difference_negative_cycle", edge.PatternStation is null
                            ? "other_business_constraint" : "micro_pattern",
                        edge.SourceConstraintName, ProofIdentity: cycleIdentity,
                        ProofKind: "difference_negative_cycle")];
                return sourceProof.SourceRows.Select(row =>
                {
                    (string kind, string id) = Classify(row.ConstraintName);
                    return new ContinuousProofRow(row.ConstraintName, 0,
                        "difference_negative_cycle", kind, id,
                        row.Sense, row.RightHandSide,
                        new Dictionary<string, double>(row.Coefficients, StringComparer.Ordinal),
                        new Dictionary<string, double>(sourceProof.FixedValues, StringComparer.Ordinal),
                        new Dictionary<string, int>(sourceProof.RequiredDiscreteLiterals, StringComparer.OrdinalIgnoreCase),
                        new Dictionary<string, string>(sourceProof.RequiredPatternsByStation, StringComparer.OrdinalIgnoreCase),
                        cycleIdentity, "difference_negative_cycle", row.SubstitutedRightHandSide,
                        -NegativeCycleEdges.Sum(item => item.UpperBound));
                });
            }).GroupBy(row => (row.ConstraintName, row.ProofIdentity))
                .Select(group => group.First()).ToArray();
            return new(GRB.Status.INFEASIBLE, "INFEASIBLE", false, 0,
                FixedValues, NegativeCycleEdges, proof, "compact_difference_negative_cycle");
        }
        if (!string.IsNullOrWhiteSpace(logFile))
        {
            string? directory = Path.GetDirectoryName(Path.GetFullPath(logFile));
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            _model!.Set(GRB.StringParam.LogFile, logFile);
        }
        _model!.Set(GRB.DoubleParam.TimeLimit, timeLimitSeconds);
        _model.Set(GRB.IntParam.DualReductions, 0);
        _model.Set(GRB.IntParam.InfUnbdInfo, 1);
        _model.Set(GRB.IntParam.Seed, 0);
        _model.Set(GRB.IntParam.Threads, 0);
        Stopwatch clock = Stopwatch.StartNew();
        _model.Optimize();
        clock.Stop();
        int status = _model.Get(GRB.IntAttr.Status);
        bool hasSolution = _model.Get(GRB.IntAttr.SolCount) > 0;
        Dictionary<string, double> values = new(FixedValues, StringComparer.Ordinal);
        if (hasSolution)
            foreach ((string name, GRBVar variable) in _variables)
                values[name] = variable.Get(GRB.DoubleAttr.X);
        IReadOnlyList<ContinuousProofRow> proofRows = status == GRB.Status.INFEASIBLE
            ? ContinuousInfeasibilityEvidenceExtractor.Extract(_model)
            : [];
        return new(status, StatusName(status), hasSolution, clock.Elapsed.TotalSeconds,
            values, [], proofRows, hasSolution ? "compact_continuous_feasible" :
            status == GRB.Status.INFEASIBLE ? "compact_continuous_proven_infeasible" :
            $"compact_continuous_no_solution_status_{status}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _model?.Dispose();
        _environment?.Dispose();
    }

    private static string StatusName(int status) => status switch
    {
        GRB.Status.OPTIMAL => "OPTIMAL",
        GRB.Status.INFEASIBLE => "INFEASIBLE",
        GRB.Status.TIME_LIMIT => "TIME_LIMIT",
        GRB.Status.INF_OR_UNBD => "INF_OR_UNBD",
        _ => status.ToString()
    };

    private static (string Kind, string Id) Classify(string name)
    {
        if (name.StartsWith("micro_cert_", StringComparison.Ordinal)) return ("micro_pattern", name);
        if (name.StartsWith("fd_bound_", StringComparison.Ordinal) ||
            name.StartsWith("native_bound_", StringComparison.Ordinal)) return ("variable_bound", name);
        if (name.StartsWith("fd_fix_", StringComparison.Ordinal)) return ("macro_fixed_decision", name);
        if (name.StartsWith("station_quota_", StringComparison.Ordinal) ||
            name.StartsWith("quota_", StringComparison.Ordinal)) return ("station_quota", name);
        if (name.Contains("overtake", StringComparison.OrdinalIgnoreCase)) return ("overtaking_stop", name);
        return ("other_business_constraint", name);
    }
}

internal static class DifferenceCycleFeedbackProjector
{
    internal static FarkasBoundaryGenerationResult TryGenerate(
        CompactContinuousTimeModel compiled,
        string activeStation,
        ContinuousExtendabilityContext context,
        IReadOnlyDictionary<string, ContinuousInterfaceKey> interfaceEventsByVariable,
        IReadOnlyDictionary<ContinuousInterfaceKey, double> rejectedInterface,
        double tolerance = 1e-7)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        ArgumentException.ThrowIfNullOrWhiteSpace(activeStation);
        foreach (CompactDifferenceEdge activeEdge in compiled.NegativeCycleEdges.Where(edge =>
                     string.Equals(edge.PatternStation, activeStation, StringComparison.OrdinalIgnoreCase)))
        {
            if (!interfaceEventsByVariable.TryGetValue(activeEdge.FromVariableName, out ContinuousInterfaceKey from) ||
                !interfaceEventsByVariable.TryGetValue(activeEdge.ToVariableName, out ContinuousInterfaceKey to) ||
                !rejectedInterface.ContainsKey(from) || !rejectedInterface.ContainsKey(to))
                continue;
            ShortestPathResult path = ShortestPath(compiled.Edges, activeEdge.ToVariableName,
                activeEdge.FromVariableName, activeStation, tolerance);
            if (!path.IsBounded || path.Edges.Count == 0 ||
                path.Distance + activeEdge.UpperBound >= -tolerance) continue;
            ContinuousInterfaceTerm[] terms =
            [
                new(from, 1),
                new(to, -1)
            ];
            string proofIdentity = ContinuousExtendabilityContext.Hash(string.Join('|',
                activeEdge.SourceConstraintName,
                string.Join('>', path.Edges.Select(edge => edge.SourceConstraintName)),
                path.Distance.ToString("R")));
            ContinuousExtendabilityFeedback feedback = ContinuousExtendabilityFeedback.Create(
                proofIdentity, context, terms, path.Distance, rejectedInterface,
                path.Edges.Count, 0, usesExplicitBoundRows: true);
            if (feedback.ViolationAtRejectedPoint <= tolerance) continue;
            return new(feedback, "valid_condition_guarded_difference_path_boundary",
                path.Edges.Count, 0, feedback.ViolationAtRejectedPoint);
        }
        return new(null, "no_active_pattern_edge_with_valid_outside_difference_path",
            0, double.NaN, double.NaN);
    }

    private static ShortestPathResult ShortestPath(
        IReadOnlyList<CompactDifferenceEdge> edges,
        string start,
        string target,
        string excludedPatternStation,
        double tolerance)
    {
        CompactDifferenceEdge[] eligible = edges.Where(edge => !string.Equals(
            edge.PatternStation, excludedPatternStation, StringComparison.OrdinalIgnoreCase)).ToArray();
        string[] vertices = eligible.SelectMany(edge => new[] { edge.FromVariableName, edge.ToVariableName })
            .Append(start).Append(target).Distinct(StringComparer.Ordinal).ToArray();
        Dictionary<string, double> distance = vertices.ToDictionary(
            vertex => vertex,
            vertex => string.Equals(vertex, start, StringComparison.Ordinal) ? 0.0 : double.PositiveInfinity,
            StringComparer.Ordinal);
        Dictionary<string, CompactDifferenceEdge> predecessor = new(StringComparer.Ordinal);
        for (int iteration = 0; iteration < vertices.Length - 1; iteration++)
        {
            bool changed = false;
            foreach (CompactDifferenceEdge edge in eligible)
            {
                if (double.IsPositiveInfinity(distance[edge.FromVariableName])) continue;
                double candidate = distance[edge.FromVariableName] + edge.UpperBound;
                if (distance[edge.ToVariableName] <= candidate + tolerance) continue;
                distance[edge.ToVariableName] = candidate;
                predecessor[edge.ToVariableName] = edge;
                changed = true;
            }
            if (!changed) break;
        }
        foreach (CompactDifferenceEdge edge in eligible)
            if (!double.IsPositiveInfinity(distance[edge.FromVariableName]) &&
                distance[edge.ToVariableName] > distance[edge.FromVariableName] + edge.UpperBound + tolerance)
                return new(false, double.NaN, []);
        if (double.IsPositiveInfinity(distance[target])) return new(false, double.NaN, []);
        List<CompactDifferenceEdge> path = [];
        string cursor = target;
        while (!string.Equals(cursor, start, StringComparison.Ordinal))
        {
            if (!predecessor.TryGetValue(cursor, out CompactDifferenceEdge? edge))
                return new(false, double.NaN, []);
            path.Add(edge);
            cursor = edge.FromVariableName;
            if (path.Count > vertices.Length) return new(false, double.NaN, []);
        }
        path.Reverse();
        return new(true, distance[target], path);
    }

    private sealed record ShortestPathResult(
        bool IsBounded,
        double Distance,
        IReadOnlyList<CompactDifferenceEdge> Edges);
}
