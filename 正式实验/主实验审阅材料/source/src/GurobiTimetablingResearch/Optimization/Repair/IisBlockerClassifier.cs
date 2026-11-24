// IIS 阻塞分类器读取 Gurobi 不可约简不一致子系统中的约束和变量界，并借助元数据目录还原对应业务决策。
// 分类结果按区间、车站事件、时间、停站、附加时分和越行依赖组织，为修复模型精确释放阻塞变量。
using Gurobi;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

internal enum IisBlockerKind
{
    StructuralCandidateConstraint,
    AcceptedStructuralConstraint,
    OriginalEarliestLatestBound,
    RepairTemporalLowerBound,
    RepairTemporalUpperBound,
    FixedSectionPrecedence,
    FixedStationEventPrecedence,
    FixedStopDecision,
    RunningTimeBaseConstraint,
    RunningSupplementLowerBound,
    RunningSupplementUpperBound,
    NormalDwellConstraint,
    WaitingDwellConstraint,
    BeingOvertakenConstraint,
    StationQuotaConstraint,
    TravelTimeConstraint,
    MaintenanceConstraint,
    HeadwayConstraint,
    OtherHardConstraint,
    VariableOriginalLB,
    VariableOriginalUB,
    Unknown
}

internal enum IisBoundSource
{
    NotApplicable,
    OriginalHardBound,
    RepairTemporalBound,
    FixedSnapshotDecision,
    RunningSupplementBound,
    VariableOriginalBound,
    Unknown
}

internal enum IisVariableDecisionType
{
    ArrivalTime,
    DepartureTime,
    Stop,
    SectionPrecedence,
    StationEventPrecedence,
    RunningSupplement,
    BeingOvertaken,
    Other
}

internal sealed record IisVariableMetadata(
    string VariableName,
    IisVariableDecisionType DecisionType,
    string VariableKey,
    int? TrainIndex,
    int? StationOrder,
    string StationName,
    string SectionName,
    string EventType,
    double OriginalLowerBound,
    double OriginalUpperBound,
    StopRequirement? StopRequirement = null,
    TrainStopRole? StopRole = null);

internal sealed record SectionDependencyDecision(
    string Key,
    string SectionName,
    int FirstTrainIndex,
    int SecondTrainIndex,
    IReadOnlyList<string> StationNames);

internal sealed record StationEventDependencyDecision(
    string Key,
    string StationName,
    int FirstTrainIndex,
    int SecondTrainIndex,
    StationEventSide FirstSide,
    StationEventSide SecondSide,
    int FirstStationOrder,
    int SecondStationOrder);

internal sealed record TemporalDependencyEvent(
    string Key,
    string StopId,
    int TrainIndex,
    int StationOrder,
    string StationName,
    string EventType,
    double OriginalLowerBound,
    double OriginalUpperBound);

internal sealed record StopDependencyDecision(
    string StopId,
    int TrainIndex,
    int StationOrder,
    string StationName,
    StopRequirement Requirement,
    TrainStopRole Role);

internal sealed record SupplementDependencyDecision(
    string Key,
    int TrainIndex,
    string SectionName,
    int FromStationOrder,
    int ToStationOrder);

internal sealed record OvertakingDependencyPair(
    string StationName,
    int FirstTrainIndex,
    int SecondTrainIndex,
    string IncomingSectionPrecedenceKey,
    string OutgoingSectionPrecedenceKey,
    IReadOnlyList<string> DerivedAuxiliaryKeys);

internal sealed record IisModelDependencyCatalog(
    IReadOnlyList<SectionDependencyDecision> SectionDecisions,
    IReadOnlyList<StationEventDependencyDecision> StationEventDecisions,
    IReadOnlyList<TemporalDependencyEvent> TemporalEvents,
    IReadOnlyList<StopDependencyDecision> StopDecisions,
    IReadOnlyList<SupplementDependencyDecision> SupplementDecisions,
    IReadOnlyList<OvertakingDependencyPair> OvertakingPairs,
    IReadOnlyDictionary<string, IisVariableMetadata>? VariablesByName = null)
{
    internal IReadOnlyDictionary<string, IisVariableMetadata> EffectiveVariablesByName { get; } =
        VariablesByName ?? new Dictionary<string, IisVariableMetadata>(StringComparer.OrdinalIgnoreCase);

    internal static IisModelDependencyCatalog Build(BasicMilpModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        Dictionary<string, IisVariableMetadata> variables = new(StringComparer.OrdinalIgnoreCase);
        List<TemporalDependencyEvent> temporal = [];
        List<StopDependencyDecision> stops = [];
        foreach (TrainStopRecord stop in model.Instance.TrainStops)
        {
            string arrivalName = model.ArrivalVars[stop.StopId].Get(GRB.StringAttr.VarName);
            string departureName = model.DepartureVars[stop.StopId].Get(GRB.StringAttr.VarName);
            string stopName = model.StopVars[stop.StopId].Get(GRB.StringAttr.VarName);
            temporal.Add(new TemporalDependencyEvent(
                $"arrival::{stop.StopId}", stop.StopId, stop.TrainIndex, stop.StationOrder,
                stop.StationName, "arrival", stop.EarliestArrSeconds, stop.LatestArrSeconds));
            temporal.Add(new TemporalDependencyEvent(
                $"departure::{stop.StopId}", stop.StopId, stop.TrainIndex, stop.StationOrder,
                stop.StationName, "departure", stop.EarliestDepSeconds, stop.LatestDepSeconds));
            variables[arrivalName] = new IisVariableMetadata(
                arrivalName, IisVariableDecisionType.ArrivalTime, $"arrival::{stop.StopId}",
                stop.TrainIndex, stop.StationOrder, stop.StationName, string.Empty, "arrival",
                stop.EarliestArrSeconds, stop.LatestArrSeconds);
            variables[departureName] = new IisVariableMetadata(
                departureName, IisVariableDecisionType.DepartureTime, $"departure::{stop.StopId}",
                stop.TrainIndex, stop.StationOrder, stop.StationName, string.Empty, "departure",
                stop.EarliestDepSeconds, stop.LatestDepSeconds);
            variables[stopName] = new IisVariableMetadata(
                stopName, IisVariableDecisionType.Stop, stop.StopId,
                stop.TrainIndex, stop.StationOrder, stop.StationName, string.Empty, "stop",
                0.0, 1.0, stop.StopRequirement, stop.StopRole);
            stops.Add(new StopDependencyDecision(
                stop.StopId, stop.TrainIndex, stop.StationOrder, stop.StationName,
                stop.StopRequirement, stop.StopRole));
        }

        List<SectionDependencyDecision> sections = model.SectionConflictCandidates.Select(candidate =>
        {
            string key = BasicMilpModelBuilder.BuildSectionPrecedenceKey(candidate);
            GRBVar variable = model.SectionPrecedenceVars[key];
            string name = variable.Get(GRB.StringAttr.VarName);
            variables[name] = new IisVariableMetadata(
                name, IisVariableDecisionType.SectionPrecedence, key,
                null, null, string.Empty, candidate.SectionName, "section_precedence", 0.0, 1.0);
            IReadOnlyList<string> stationNames = model.Instance.TrainStops
                .Where(stop =>
                    (stop.TrainIndex == candidate.FirstTrainIndex &&
                     (stop.StationOrder == candidate.FirstStopOrder || stop.StationOrder == candidate.FirstEndStopOrder)) ||
                    (stop.TrainIndex == candidate.SecondTrainIndex &&
                     (stop.StationOrder == candidate.SecondStopOrder || stop.StationOrder == candidate.SecondEndStopOrder)))
                .Select(stop => stop.StationName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return new SectionDependencyDecision(
                key, candidate.SectionName, candidate.FirstTrainIndex, candidate.SecondTrainIndex, stationNames);
        }).ToList();

        List<StationEventDependencyDecision> stationEvents = model.StationEventConflictCandidates.Select(candidate =>
        {
            string key = BasicMilpModelBuilder.BuildStationEventPrecedenceKey(candidate);
            GRBVar variable = model.StationEventPrecedenceVars[key];
            string name = variable.Get(GRB.StringAttr.VarName);
            variables[name] = new IisVariableMetadata(
                name, IisVariableDecisionType.StationEventPrecedence, key,
                null, null, candidate.StationName, string.Empty, "station_event_precedence", 0.0, 1.0);
            return new StationEventDependencyDecision(
                key, candidate.StationName, candidate.FirstTrainIndex, candidate.SecondTrainIndex,
                candidate.FirstSide, candidate.SecondSide,
                candidate.FirstStationOrder, candidate.SecondStationOrder);
        }).ToList();

        List<SupplementDependencyDecision> supplements = [];
        foreach ((string key, GRBVar variable) in model.RunningTimeSupplementVars)
        {
            TrainSectionRepairContext context = TrainSectionRepairContextResolver.Resolve(model.Instance, key);
            string name = variable.Get(GRB.StringAttr.VarName);
            variables[name] = new IisVariableMetadata(
                name, IisVariableDecisionType.RunningSupplement, key,
                context.TrainIndex, null, string.Empty, context.SectionName, "running_supplement",
                0.0, variable.Get(GRB.DoubleAttr.UB));
            supplements.Add(new SupplementDependencyDecision(
                key, context.TrainIndex, context.SectionName,
                context.FromStationOrder, context.ToStationOrder));
        }
        foreach ((string stopId, GRBVar variable) in model.BeingOvertakenVars)
        {
            TrainStopRecord stop = model.Instance.TrainStops.Single(item =>
                string.Equals(item.StopId, stopId, StringComparison.OrdinalIgnoreCase));
            string name = variable.Get(GRB.StringAttr.VarName);
            variables[name] = new IisVariableMetadata(
                name, IisVariableDecisionType.BeingOvertaken, stopId,
                stop.TrainIndex, stop.StationOrder, stop.StationName, string.Empty,
                "being_overtaken", 0.0, 1.0);
        }

        return new IisModelDependencyCatalog(
            sections,
            stationEvents,
            temporal,
            stops,
            supplements,
            BuildOvertakingPairs(model, sections),
            variables);
    }

    /// <summary>Find comparable section decisions for possible overtaking dependencies.</summary>
    /// <param name="model">Borrowed model supplying train visits and resource domains.</param>
    /// <param name="sections">Section decisions implicated by the current diagnosis.</param>
    /// <returns>Adjacent pairs of section decisions with feasible resource support in at least one direction.</returns>
    private static IReadOnlyList<OvertakingDependencyPair> BuildOvertakingPairs(
        BasicMilpModel model,
        IReadOnlyList<SectionDependencyDecision> sections)
    {
        List<OvertakingDependencyPair> pairs = [];
        foreach (TrainRecord route in model.Instance.Trains)
        {
            for (int index = 1; index < route.OrderedStops.Count - 1; index++)
            {
                TrainStopRecord atStation = route.OrderedStops[index];
                TrainStopRecord next = route.OrderedStops[index + 1];
                if (string.IsNullOrWhiteSpace(atStation.IncomingSectionName) ||
                    string.IsNullOrWhiteSpace(next.IncomingSectionName))
                {
                    continue;
                }
                foreach (SectionDependencyDecision incoming in sections.Where(item =>
                             string.Equals(item.SectionName, atStation.IncomingSectionName, StringComparison.OrdinalIgnoreCase)))
                {
                    CanonicalTrainPair pair = CanonicalTrainPair.Create(
                        incoming.FirstTrainIndex, incoming.SecondTrainIndex);
                    SectionDependencyDecision? outgoing = sections.FirstOrDefault(item =>
                        string.Equals(item.SectionName, next.IncomingSectionName, StringComparison.OrdinalIgnoreCase) &&
                        CanonicalTrainPair.Create(item.FirstTrainIndex, item.SecondTrainIndex) == pair);
                    if (outgoing is null)
                    {
                        continue;
                    }
                    TrainStopRecord? firstVisit = model.Instance.TrainStops.FirstOrDefault(stop =>
                        stop.TrainIndex == incoming.FirstTrainIndex && stop.StationName.Equals(atStation.StationName, StringComparison.OrdinalIgnoreCase));
                    TrainStopRecord? secondVisit = model.Instance.TrainStops.FirstOrDefault(stop =>
                        stop.TrainIndex == incoming.SecondTrainIndex && stop.StationName.Equals(atStation.StationName, StringComparison.OrdinalIgnoreCase));
                    if (firstVisit is null || secondVisit is null ||
                        !StationOvertakingResourcePolicy.CanReverse(model.Instance, firstVisit, secondVisit)) continue;
                    pairs.Add(new OvertakingDependencyPair(
                        atStation.StationName,
                        incoming.FirstTrainIndex,
                        incoming.SecondTrainIndex,
                        incoming.Key,
                        outgoing.Key,
                        [
                            $"pair_overtake_first_t{incoming.FirstTrainIndex}_t{incoming.SecondTrainIndex}_s{atStation.StationOrder}",
                            $"pair_overtake_second_t{incoming.FirstTrainIndex}_t{incoming.SecondTrainIndex}_s{atStation.StationOrder}",
                            $"being_overtaken_t{incoming.FirstTrainIndex}_s{atStation.StationOrder}",
                            $"being_overtaken_t{incoming.SecondTrainIndex}_s{atStation.StationOrder}"
                        ]));
                }
            }
        }
        return pairs.DistinctBy(item =>
            $"{item.StationName}::{item.IncomingSectionPrecedenceKey}::{item.OutgoingSectionPrecedenceKey}",
            StringComparer.OrdinalIgnoreCase).ToList();
    }
}

internal static class IisBlockerClassifier
{
    private const double Tolerance = 1e-6;

    internal static StructuralIisElement Classify(
        StructuralIisElement element,
        GRBVar? variable,
        StructuralTrialIisContext context,
        MasterSolutionSnapshot? snapshot,
        IisModelDependencyCatalog? catalog)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (element.ElementKind == StructuralIisElementKind.LinearConstraint)
        {
            return element with
            {
                BlockerKind = ClassifyConstraint(element, catalog),
                BoundSource = IisBoundSource.NotApplicable
            };
        }

        if (variable is null || catalog is null ||
            !catalog.EffectiveVariablesByName.TryGetValue(element.ElementName, out IisVariableMetadata? metadata))
        {
            return element with
            {
                BlockerKind = element.ElementKind == StructuralIisElementKind.VariableLowerBound
                    ? IisBlockerKind.VariableOriginalLB
                    : IisBlockerKind.VariableOriginalUB,
                BoundSource = IisBoundSource.VariableOriginalBound,
                CurrentLowerBound = variable?.Get(GRB.DoubleAttr.LB),
                CurrentUpperBound = variable?.Get(GRB.DoubleAttr.UB)
            };
        }

        double lower = variable.Get(GRB.DoubleAttr.LB);
        double upper = variable.Get(GRB.DoubleAttr.UB);
        IisBlockerKind kind = ClassifyVariableBound(element.ElementKind, metadata, lower, upper);
        return element with
        {
            BlockerKind = kind,
            VariableKey = metadata.VariableKey,
            SectionName = metadata.SectionName,
            StationName = string.IsNullOrWhiteSpace(element.StationName) ? metadata.StationName : element.StationName,
            EventType = metadata.EventType,
            BoundSource = ResolveBoundSource(kind),
            SnapshotValue = ResolveSnapshotValue(metadata, snapshot),
            CurrentLowerBound = lower,
            CurrentUpperBound = upper,
            TrainIndices = metadata.TrainIndex.HasValue
                ? element.TrainIndices.Append(metadata.TrainIndex.Value).Distinct().Order().ToList()
                : element.TrainIndices
        };
    }

    private static IisBlockerKind ClassifyConstraint(
        StructuralIisElement element,
        IisModelDependencyCatalog? catalog)
    {
        string name = element.ElementName;
        if (element.StructuralCandidateIds.Count > 0) return IisBlockerKind.StructuralCandidateConstraint;
        if ((name.StartsWith("headway_", StringComparison.OrdinalIgnoreCase) ||
             name.StartsWith("arrival_headway_", StringComparison.OrdinalIgnoreCase) ||
             name.StartsWith("station_headway_", StringComparison.OrdinalIgnoreCase)) && catalog is not null &&
            StructuralCandidateIdentityMatcher.ParseDirectedIdentity(name) is not null)
            return IisBlockerKind.AcceptedStructuralConstraint;
        if (name.StartsWith("run_", StringComparison.OrdinalIgnoreCase)) return IisBlockerKind.RunningTimeBaseConstraint;
        if (name.StartsWith("min_dwell_", StringComparison.OrdinalIgnoreCase)) return IisBlockerKind.NormalDwellConstraint;
        if (name.StartsWith("max_dwell_", StringComparison.OrdinalIgnoreCase)) return IisBlockerKind.WaitingDwellConstraint;
        if (name.Contains("being_overtaken", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("pair_overtake", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("no_overtake_", StringComparison.OrdinalIgnoreCase)) return IisBlockerKind.BeingOvertakenConstraint;
        if (name.Contains("quota", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("stop_count", StringComparison.OrdinalIgnoreCase)) return IisBlockerKind.StationQuotaConstraint;
        if (name.Contains("travel", StringComparison.OrdinalIgnoreCase)) return IisBlockerKind.TravelTimeConstraint;
        if (name.Contains("maint", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("window", StringComparison.OrdinalIgnoreCase)) return IisBlockerKind.MaintenanceConstraint;
        if (name.Contains("headway", StringComparison.OrdinalIgnoreCase)) return IisBlockerKind.HeadwayConstraint;
        return IisBlockerKind.OtherHardConstraint;
    }

    private static IisBlockerKind ClassifyVariableBound(
        StructuralIisElementKind kind,
        IisVariableMetadata metadata,
        double lower,
        double upper) => metadata.DecisionType switch
    {
        IisVariableDecisionType.SectionPrecedence => IisBlockerKind.FixedSectionPrecedence,
        IisVariableDecisionType.StationEventPrecedence => IisBlockerKind.FixedStationEventPrecedence,
        IisVariableDecisionType.Stop => IisBlockerKind.FixedStopDecision,
        IisVariableDecisionType.RunningSupplement =>
            kind == StructuralIisElementKind.VariableLowerBound
                ? IisBlockerKind.RunningSupplementLowerBound
                : IisBlockerKind.RunningSupplementUpperBound,
        IisVariableDecisionType.ArrivalTime or IisVariableDecisionType.DepartureTime =>
            IsOriginalBound(kind, metadata, lower, upper)
                ? IisBlockerKind.OriginalEarliestLatestBound
                : kind == StructuralIisElementKind.VariableLowerBound
                    ? IisBlockerKind.RepairTemporalLowerBound
                    : IisBlockerKind.RepairTemporalUpperBound,
        _ => kind == StructuralIisElementKind.VariableLowerBound
            ? IisBlockerKind.VariableOriginalLB
            : IisBlockerKind.VariableOriginalUB
    };

    private static bool IsOriginalBound(
        StructuralIisElementKind kind,
        IisVariableMetadata metadata,
        double lower,
        double upper) => kind == StructuralIisElementKind.VariableLowerBound
        ? Math.Abs(lower - metadata.OriginalLowerBound) <= Tolerance
        : Math.Abs(upper - metadata.OriginalUpperBound) <= Tolerance;

    private static IisBoundSource ResolveBoundSource(IisBlockerKind kind) => kind switch
    {
        IisBlockerKind.OriginalEarliestLatestBound => IisBoundSource.OriginalHardBound,
        IisBlockerKind.RepairTemporalLowerBound or IisBlockerKind.RepairTemporalUpperBound =>
            IisBoundSource.RepairTemporalBound,
        IisBlockerKind.FixedSectionPrecedence or IisBlockerKind.FixedStationEventPrecedence or
            IisBlockerKind.FixedStopDecision => IisBoundSource.FixedSnapshotDecision,
        IisBlockerKind.RunningSupplementLowerBound or IisBlockerKind.RunningSupplementUpperBound =>
            IisBoundSource.RunningSupplementBound,
        _ => IisBoundSource.VariableOriginalBound
    };

    private static double? ResolveSnapshotValue(
        IisVariableMetadata metadata,
        MasterSolutionSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return null;
        }
        return metadata.DecisionType switch
        {
            IisVariableDecisionType.ArrivalTime =>
                snapshot.ArrivalSecondsByStopId.GetValueOrDefault(metadata.VariableKey["arrival::".Length..]),
            IisVariableDecisionType.DepartureTime =>
                snapshot.DepartureSecondsByStopId.GetValueOrDefault(metadata.VariableKey["departure::".Length..]),
            IisVariableDecisionType.Stop => snapshot.StopByStopId.GetValueOrDefault(metadata.VariableKey),
            IisVariableDecisionType.SectionPrecedence => snapshot.SectionPrecedenceByKey.GetValueOrDefault(metadata.VariableKey),
            IisVariableDecisionType.StationEventPrecedence => snapshot.StationEventPrecedenceByKey.GetValueOrDefault(metadata.VariableKey),
            IisVariableDecisionType.RunningSupplement =>
                snapshot.RunningTimeSupplementSecondsByKey?.GetValueOrDefault(metadata.VariableKey),
            _ => null
        };
    }
}
