// 结构试验 IIS 提取器读取失败模型的 IIS 标记，并通过约束命名规则及元数据映射为区间、车站、时间和停站元素。
// 诊断对象保留候选上下文与阻塞元素集合，为依赖扩张规划器提供机器可用的不可行解释。
using System.Text.RegularExpressions;
using Gurobi;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

internal enum StructuralIisElementKind
{
    LinearConstraint,
    VariableLowerBound,
    VariableUpperBound
}

internal sealed record StructuralIisElement(
    StructuralIisElementKind ElementKind,
    string ElementName,
    string ConstraintFamily,
    IReadOnlyList<string> StructuralCandidateIds,
    IReadOnlyList<int> TrainIndices,
    string StationName,
    string ResourceKey,
    IisBlockerKind BlockerKind = IisBlockerKind.Unknown,
    string VariableKey = "",
    string SectionName = "",
    string EventType = "",
    IisBoundSource BoundSource = IisBoundSource.Unknown,
    double? SnapshotValue = null,
    double? CurrentLowerBound = null,
    double? CurrentUpperBound = null,
    int? DirectedFirstTrainIndex = null,
    int? DirectedSecondTrainIndex = null,
    StationEventSide? DirectedFirstEventSide = null,
    StationEventSide? DirectedSecondEventSide = null);

internal sealed record StructuralTrialIisContext(
    int IterationIndex,
    string BatchId,
    int NeighborhoodLevel,
    RepairDecisionLevel DecisionLevel,
    int WindowSeconds,
    string Source,
    string ProbeId,
    IReadOnlyList<string> TestedCandidateIds);

internal sealed record StructuralTrialIisDiagnostic(
    StructuralTrialIisContext Context,
    StructuralIisElement Element);

internal static class StructuralTrialIisExtractor
{
    internal static IReadOnlyList<StructuralTrialIisDiagnostic> ComputeAndExtract(
        BasicMilpModel model,
        StructuralTrialIisContext context,
        IReadOnlyList<HeadwayConflictCandidate> candidates,
        TimetablingInstance instance,
        MasterSolutionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(model);
        return ComputeAndExtractCore(
            model.Model,
            context,
            candidates,
            instance,
            IisModelDependencyCatalog.Build(model),
            snapshot);
    }

    internal static IReadOnlyList<StructuralTrialIisDiagnostic> ComputeAndExtract(
        GRBModel model,
        StructuralTrialIisContext context,
        IReadOnlyList<HeadwayConflictCandidate> candidates,
        TimetablingInstance instance)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(instance);
        return ComputeAndExtractCore(model, context, candidates, instance, null, null);
    }

    private static IReadOnlyList<StructuralTrialIisDiagnostic> ComputeAndExtractCore(
        GRBModel model,
        StructuralTrialIisContext context,
        IReadOnlyList<HeadwayConflictCandidate> candidates,
        TimetablingInstance instance,
        IisModelDependencyCatalog? catalog,
        MasterSolutionSnapshot? snapshot)
    {
        if (model.Get(GRB.IntAttr.Status) != GRB.Status.INFEASIBLE ||
            model.Get(GRB.IntAttr.SolCount) != 0)
        {
            throw new InvalidOperationException(
                "IIS extraction requires a proven-infeasible model with no incumbent.");
        }

        _ = GurobiModelIdentityAuditor.ValidateAndCapture(
            model,
            $"StructuralTrialIisExtractor::{context.Source}::before_iis");
        model.ComputeIIS();
        List<StructuralTrialIisDiagnostic> diagnostics = [];
        foreach (GRBConstr constraint in model.GetConstrs())
        {
            if (constraint.Get(GRB.IntAttr.IISConstr) == 0)
            {
                continue;
            }
            StructuralIisElement mapped = StructuralIisMetadataMapper.Map(
                    StructuralIisElementKind.LinearConstraint,
                    constraint.Get(GRB.StringAttr.ConstrName),
                    candidates,
                    instance);
            diagnostics.Add(new StructuralTrialIisDiagnostic(
                context,
                IisBlockerClassifier.Classify(mapped, null, context, snapshot, catalog)));
        }
        foreach (GRBVar variable in model.GetVars())
        {
            string variableName = variable.Get(GRB.StringAttr.VarName);
            if (variable.Get(GRB.IntAttr.IISLB) != 0)
            {
                StructuralIisElement mapped = StructuralIisMetadataMapper.Map(
                        StructuralIisElementKind.VariableLowerBound,
                        variableName,
                        candidates,
                        instance);
                diagnostics.Add(new StructuralTrialIisDiagnostic(
                    context,
                    IisBlockerClassifier.Classify(mapped, variable, context, snapshot, catalog)));
            }
            if (variable.Get(GRB.IntAttr.IISUB) != 0)
            {
                StructuralIisElement mapped = StructuralIisMetadataMapper.Map(
                        StructuralIisElementKind.VariableUpperBound,
                        variableName,
                        candidates,
                        instance);
                diagnostics.Add(new StructuralTrialIisDiagnostic(
                    context,
                    IisBlockerClassifier.Classify(mapped, variable, context, snapshot, catalog)));
            }
        }
        return diagnostics;
    }
}

internal static partial class StructuralIisMetadataMapper
{
    internal static StructuralIisElement Map(
        StructuralIisElementKind kind,
        string elementName,
        IReadOnlyList<HeadwayConflictCandidate> candidates,
        TimetablingInstance instance)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(elementName);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(instance);

        List<HeadwayConflictCandidate> owners = candidates
            .Where(candidate => StructuralCandidateIdentityMatcher.IsOwnedBy(elementName, candidate))
            .ToList();
        List<int> trains = owners
            .SelectMany(candidate => new[] { candidate.FirstTrainIndex, candidate.SecondTrainIndex })
            .Concat(TrainTokenRegex().Matches(elementName).Select(match => int.Parse(match.Groups[1].Value)))
            .Distinct()
            .Order()
            .ToList();
        string stationName = owners
            .Select(candidate => candidate.StationEdge?.StationName ?? string.Empty)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ??
            ResolveStationName(elementName, trains, instance);
        string resourceKey = owners.Select(candidate => candidate.ResourceKey).FirstOrDefault() ?? string.Empty;
        DirectedStructuralElementIdentity? directed = StructuralCandidateIdentityMatcher.ParseDirectedIdentity(elementName);
        return new StructuralIisElement(
            kind,
            elementName,
            ResolveFamily(kind, elementName),
            owners.Select(candidate => candidate.CandidateKey)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            trains,
            stationName,
            resourceKey,
            SectionName: owners.Select(candidate => candidate.SectionEdge?.SectionName ?? string.Empty)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty,
            DirectedFirstTrainIndex: directed?.DirectedFirstTrainIndex,
            DirectedSecondTrainIndex: directed?.DirectedSecondTrainIndex,
            DirectedFirstEventSide: directed?.DirectedFirstEventSide,
            DirectedSecondEventSide: directed?.DirectedSecondEventSide);
    }

    private static string ResolveFamily(StructuralIisElementKind kind, string name)
    {
        if (kind != StructuralIisElementKind.LinearConstraint)
        {
            return name.StartsWith("stop_", StringComparison.OrdinalIgnoreCase)
                ? "stop_bound"
                : name.StartsWith("arr_", StringComparison.OrdinalIgnoreCase) ||
                  name.StartsWith("dep_", StringComparison.OrdinalIgnoreCase)
                    ? "time_bound"
                    : name.Contains("supplement", StringComparison.OrdinalIgnoreCase)
                        ? "running_supplement_bound"
                        : name.StartsWith("prec_", StringComparison.OrdinalIgnoreCase)
                            ? "precedence_bound"
                            : "variable_bound";
        }

        if (name.StartsWith("station_headway_", StringComparison.OrdinalIgnoreCase)) return "station_headway";
        if (name.StartsWith("arrival_headway_", StringComparison.OrdinalIgnoreCase)) return "section_arrival_headway";
        if (name.StartsWith("headway_", StringComparison.OrdinalIgnoreCase)) return "section_headway";
        if (name.StartsWith("fam_", StringComparison.OrdinalIgnoreCase)) return "stop_family_link";
        if (name.StartsWith("min_dwell_", StringComparison.OrdinalIgnoreCase)) return "dwell";
        if (name.StartsWith("run_time_", StringComparison.OrdinalIgnoreCase)) return "section_runtime";
        if (name.StartsWith("fix_stop_", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("fix_pass_", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("terminal_stop_", StringComparison.OrdinalIgnoreCase)) return "stop_requirement";
        if (name.Contains("quota", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("stop_count", StringComparison.OrdinalIgnoreCase)) return "station_quota";
        if (name.Contains("travel", StringComparison.OrdinalIgnoreCase)) return "travel_time";
        if (name.Contains("maint", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("window", StringComparison.OrdinalIgnoreCase)) return "maintenance_window";
        if (name.StartsWith("repair_", StringComparison.OrdinalIgnoreCase)) return "repair_objective";
        return "other";
    }

    private static string ResolveStationName(
        string elementName,
        IReadOnlyList<int> trains,
        TimetablingInstance instance)
    {
        Match stationOrderMatch = StationOrderRegex().Match(elementName);
        if (!stationOrderMatch.Success || trains.Count == 0)
        {
            return string.Empty;
        }
        int stationOrder = int.Parse(stationOrderMatch.Groups[1].Value);
        return instance.TrainStops.FirstOrDefault(stop =>
            trains.Contains(stop.TrainIndex) && stop.StationOrder == stationOrder)?.StationName ?? string.Empty;
    }

    [GeneratedRegex(@"(?:^|_)t(-?\d+)(?:_|$)", RegexOptions.IgnoreCase)]
    private static partial Regex TrainTokenRegex();

    [GeneratedRegex(@"(?:^|_)s(-?\d+)(?:_|$)", RegexOptions.IgnoreCase)]
    private static partial Regex StationOrderRegex();
}
