// 结构修复邻域解析器根据候选边、episode 和传播列车计算需要释放的列车—区间上下文。
// 应用器将范围外变量固定到接受快照，仅放开邻域内的时间、停站和先后变量，从而形成局部可回滚修复模型。
using Gurobi;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

internal static class StructuralRepairNeighborhoodResolver
{
    internal static IReadOnlySet<int> ResolveTrainIndices(
        TimetablingInstance instance,
        HeadwayConflictEdgeExpansion expansion,
        int referenceOrderPadding,
        IReadOnlyCollection<int>? propagationTrainIndices = null)
    {
        HashSet<int> core = expansion.SectionEdges
            .SelectMany(edge => new[] { edge.FirstTrainIndex, edge.SecondTrainIndex })
            .Concat(expansion.StationEdges.SelectMany(edge => new[] { edge.FirstTrainIndex, edge.SecondTrainIndex }))
            .ToHashSet();
        if (propagationTrainIndices is not null)
        {
            core.UnionWith(propagationTrainIndices);
        }
        if (referenceOrderPadding <= 0 || core.Count == 0)
        {
            return core;
        }

        List<int> order = instance.Trains
            .OrderBy(train => train.OrderedStops[0].RawDepTimeSeconds ?? train.OrderedStops[0].EarliestDepSeconds)
            .ThenBy(train => train.TrainIndex)
            .Select(train => train.TrainIndex)
            .ToList();
        HashSet<int> released = new(core);
        for (int index = 0; index < order.Count; index++)
        {
            if (!core.Contains(order[index]))
            {
                continue;
            }

            int first = Math.Max(0, index - referenceOrderPadding);
            int last = Math.Min(order.Count - 1, index + referenceOrderPadding);
            for (int neighbor = first; neighbor <= last; neighbor++)
            {
                released.Add(order[neighbor]);
            }
        }

        return released;
    }
}

internal static class StructuralRepairNeighborhoodApplier
{
    internal static void FixOutside(
        BasicMilpModel model,
        MasterSolutionSnapshot snapshot,
        IReadOnlySet<int> releasedTrainIndices)
    {
        foreach (TrainStopRecord stop in model.Instance.TrainStops.Where(stop => !releasedTrainIndices.Contains(stop.TrainIndex)))
        {
            FixIfPresent(model.StopVars, snapshot.StopByStopId, stop.StopId);
        }

        foreach (SectionConflictCandidate candidate in model.SectionConflictCandidates)
        {
            if (releasedTrainIndices.Contains(candidate.FirstTrainIndex) || releasedTrainIndices.Contains(candidate.SecondTrainIndex))
            {
                continue;
            }

            string key = BasicMilpModelBuilder.BuildSectionPrecedenceKey(candidate);
            FixIfPresent(model.SectionPrecedenceVars, snapshot.SectionPrecedenceByKey, key);
        }

        foreach (StationEventConflictCandidate candidate in model.StationEventConflictCandidates)
        {
            if (releasedTrainIndices.Contains(candidate.FirstTrainIndex) || releasedTrainIndices.Contains(candidate.SecondTrainIndex))
            {
                continue;
            }

            string key = BasicMilpModelBuilder.BuildStationEventPrecedenceKey(candidate);
            FixIfPresent(model.StationEventPrecedenceVars, snapshot.StationEventPrecedenceByKey, key);
        }
    }

    internal static void ApplyDecisionNeighborhood(
        BasicMilpModel model,
        MasterSolutionSnapshot snapshot,
        RepairDecisionNeighborhood neighborhood,
        double repairRatio)
    {
        ArgumentNullException.ThrowIfNull(neighborhood);
        IisDependencyOverlay overlay = RepairDecisionDependencyOverlayResolver.Resolve(neighborhood);
        double validatedRepairRatio = CheckerRepairSupplementOptions.Create(repairRatio).Ratio;
        foreach (TrainStopRecord stop in model.Instance.TrainStops)
        {
            bool insideSpan = IsInsideSpan(neighborhood, stop.TrainIndex, stop.StationOrder);
            if (!overlay.FlexibleStopKeys.Contains(stop.StopId) &&
                !RepairDecisionReleasePolicy.ReleasesStop(
                    neighborhood.DecisionLevel, stop.StopRequirement, stop.StopRole, insideSpan))
            {
                FixIfPresent(model.StopVars, snapshot.StopByStopId, stop.StopId);
            }
        }

        foreach (SectionConflictCandidate candidate in model.SectionConflictCandidates)
        {
            string key = BasicMilpModelBuilder.BuildSectionPrecedenceKey(candidate);
            bool insideSpan = IsInsideSpan(neighborhood, candidate.FirstTrainIndex, candidate.FirstStopOrder) &&
                IsInsideSpan(neighborhood, candidate.FirstTrainIndex, candidate.FirstEndStopOrder) &&
                IsInsideSpan(neighborhood, candidate.SecondTrainIndex, candidate.SecondStopOrder) &&
                IsInsideSpan(neighborhood, candidate.SecondTrainIndex, candidate.SecondEndStopOrder);
            if (!overlay.SectionPrecedenceKeys.Contains(key) &&
                !RepairDecisionReleasePolicy.ReleasesPrecedence(neighborhood.DecisionLevel, insideSpan))
            {
                int value = snapshot.SectionPrecedenceByKey.TryGetValue(key, out int snapshotValue)
                    ? snapshotValue
                    : ResolveSectionPrecedenceFromSnapshot(model.Instance, snapshot, candidate);
                FixValue(model.SectionPrecedenceVars, key, value);
            }
        }

        foreach (StationEventConflictCandidate candidate in model.StationEventConflictCandidates)
        {
            string key = BasicMilpModelBuilder.BuildStationEventPrecedenceKey(candidate);
            bool insideSpan = IsInsideSpan(neighborhood, candidate.FirstTrainIndex, candidate.FirstStationOrder) &&
                IsInsideSpan(neighborhood, candidate.SecondTrainIndex, candidate.SecondStationOrder);
            if (!overlay.StationEventPrecedenceKeys.Contains(key) &&
                !RepairDecisionReleasePolicy.ReleasesPrecedence(neighborhood.DecisionLevel, insideSpan))
            {
                int value = snapshot.StationEventPrecedenceByKey.TryGetValue(key, out int snapshotValue)
                    ? snapshotValue
                    : ResolveStationEventPrecedenceFromSnapshot(model.Instance, snapshot, candidate);
                FixValue(model.StationEventPrecedenceVars, key, value);
            }
        }

        IReadOnlyDictionary<string, double> supplementValues =
            snapshot.RunningTimeSupplementSecondsByKey ?? new Dictionary<string, double>();
        foreach ((string key, GRBVar variable) in model.RunningTimeSupplementVars)
        {
            TrainSectionRepairContext section = TrainSectionRepairContextResolver.Resolve(model.Instance, key);
            double snapshotValue = supplementValues.GetValueOrDefault(key, 0.0);
            if (overlay.SupplementSectionKeys.Contains(key))
            {
                variable.Set(GRB.DoubleAttr.LB, 0.0);
                variable.Set(
                    GRB.DoubleAttr.UB,
                    RunningTimeSupplementPolicy.Create(validatedRepairRatio)
                        .ResolveMaximumSeconds(section.BaseRuntimeSeconds));
                continue;
            }
            RepairRunningTimeSupplementBounds bounds = RepairRunningTimeSupplementScope.Resolve(new(
                neighborhood.DecisionLevel,
                section.TrainIndex,
                section.SectionName,
                section.FromStationOrder,
                section.ToStationOrder,
                section.BaseRuntimeSeconds,
                snapshotValue,
                validatedRepairRatio,
                neighborhood.ReleasedTrainIndices.Contains(section.TrainIndex),
                neighborhood.SpansByTrainIndex.GetValueOrDefault(section.TrainIndex)));
            variable.Set(GRB.DoubleAttr.LB, bounds.LowerBound);
            variable.Set(GRB.DoubleAttr.UB, bounds.UpperBound);
        }
    }

    internal static bool IsInsideSpan(
        RepairDecisionNeighborhood neighborhood,
        int trainIndex,
        int stationOrder) =>
        neighborhood.ReleasedTrainIndices.Contains(trainIndex) &&
        neighborhood.SpansByTrainIndex.TryGetValue(trainIndex, out RepairGeographicSpan? span) &&
        span.Contains(stationOrder);

    internal static bool IsTimeEventReleased(
        RepairDecisionNeighborhood neighborhood,
        string eventKey,
        int trainIndex,
        int stationOrder) =>
        IsInsideSpan(neighborhood, trainIndex, stationOrder) ||
        RepairDecisionDependencyOverlayResolver.Resolve(neighborhood).TemporalEventKeys.Contains(eventKey);

    private static void FixIfPresent(
        IReadOnlyDictionary<string, GRBVar> variables,
        IReadOnlyDictionary<string, int> values,
        string key)
    {
        if (variables.TryGetValue(key, out GRBVar? variable) && values.TryGetValue(key, out int value))
        {
            variable.Set(GRB.DoubleAttr.LB, value);
            variable.Set(GRB.DoubleAttr.UB, value);
        }
    }

    private static void FixValue(
        IReadOnlyDictionary<string, GRBVar> variables,
        string key,
        int value)
    {
        if (variables.TryGetValue(key, out GRBVar? variable))
        {
            variable.Set(GRB.DoubleAttr.LB, value);
            variable.Set(GRB.DoubleAttr.UB, value);
        }
    }

    private static int ResolveSectionPrecedenceFromSnapshot(
        TimetablingInstance instance,
        MasterSolutionSnapshot snapshot,
        SectionConflictCandidate candidate)
    {
        TrainStopRecord first = instance.TrainStops.Single(stop =>
            stop.TrainIndex == candidate.FirstTrainIndex && stop.StationOrder == candidate.FirstStopOrder);
        TrainStopRecord second = instance.TrainStops.Single(stop =>
            stop.TrainIndex == candidate.SecondTrainIndex && stop.StationOrder == candidate.SecondStopOrder);
        return snapshot.DepartureSecondsByStopId[first.StopId] <= snapshot.DepartureSecondsByStopId[second.StopId]
            ? 0
            : 1;
    }

    private static int ResolveStationEventPrecedenceFromSnapshot(
        TimetablingInstance instance,
        MasterSolutionSnapshot snapshot,
        StationEventConflictCandidate candidate)
    {
        TrainStopRecord first = instance.TrainStops.Single(stop =>
            stop.TrainIndex == candidate.FirstTrainIndex && stop.StationOrder == candidate.FirstStationOrder);
        TrainStopRecord second = instance.TrainStops.Single(stop =>
            stop.TrainIndex == candidate.SecondTrainIndex && stop.StationOrder == candidate.SecondStationOrder);
        double firstTime = candidate.FirstSide == StationEventSide.Incoming
            ? snapshot.ArrivalSecondsByStopId[first.StopId]
            : snapshot.DepartureSecondsByStopId[first.StopId];
        double secondTime = candidate.SecondSide == StationEventSide.Incoming
            ? snapshot.ArrivalSecondsByStopId[second.StopId]
            : snapshot.DepartureSecondsByStopId[second.StopId];
        return firstTime <= secondTime ? 0 : 1;
    }

    private static void FixIfPresent(
        IReadOnlyDictionary<string, GRBVar> variables,
        IReadOnlyDictionary<string, double> values,
        string key)
    {
        if (variables.TryGetValue(key, out GRBVar? variable) && values.TryGetValue(key, out double value))
        {
            variable.Set(GRB.DoubleAttr.LB, value);
            variable.Set(GRB.DoubleAttr.UB, value);
        }
    }
}

internal sealed record TrainSectionRepairContext(
    int TrainIndex,
    string SectionName,
    int FromStationOrder,
    int ToStationOrder,
    int BaseRuntimeSeconds);

internal static class TrainSectionRepairContextResolver
{
    internal static TrainSectionRepairContext Resolve(TimetablingInstance instance, string key)
    {
        int separator = key.IndexOf("::", StringComparison.Ordinal);
        if (separator <= 0 || !int.TryParse(key[..separator], out int trainIndex))
        {
            throw new InvalidDataException($"Malformed running-time supplement key '{key}'.");
        }

        string sectionName = key[(separator + 2)..];
        TrainRecord train = instance.Trains.FirstOrDefault(item => item.TrainIndex == trainIndex)
            ?? throw new InvalidDataException(
                $"Running-time supplement key '{key}' references an unknown train.");
        for (int index = 0; index < train.OrderedStops.Count - 1; index++)
        {
            TrainStopRecord current = train.OrderedStops[index];
            TrainStopRecord next = train.OrderedStops[index + 1];
            if (!string.Equals(next.IncomingSectionName, sectionName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            SectionRecord section = instance.SectionByName.TryGetValue(sectionName, out SectionRecord? found)
                ? found
                : throw new InvalidDataException($"Section '{sectionName}' was not found.");
            TravelDirection direction = ResolveDirection(section, current.StationName, next.StationName);
            if (string.IsNullOrWhiteSpace(next.RunScaleName))
            {
                throw new InvalidDataException(
                    $"Stop '{next.StopId}' has no run scale for section '{sectionName}'.");
            }
            RunTimeRule runtime = instance.RunTimeRules.FirstOrDefault(candidate =>
                    string.Equals(candidate.ScaleName, next.RunScaleName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(candidate.SectionName, sectionName, StringComparison.OrdinalIgnoreCase) &&
                    candidate.Direction == direction)
                ?? throw new InvalidDataException(
                    $"Runtime rule was not found for train {trainIndex}, section '{sectionName}'.");
            return new TrainSectionRepairContext(
                trainIndex,
                sectionName,
                current.StationOrder,
                next.StationOrder,
                runtime.RunTimeSeconds);
        }

        throw new InvalidDataException(
            $"Running-time supplement key '{key}' does not match a train section traversal.");
    }

    private static TravelDirection ResolveDirection(
        SectionRecord section,
        string previousStation,
        string nextStation)
    {
        bool sameOrientation =
            string.Equals(section.FromStation, previousStation, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(section.ToStation, nextStation, StringComparison.OrdinalIgnoreCase);
        if (sameOrientation)
        {
            return section.DirectionTag;
        }

        bool reverseOrientation =
            string.Equals(section.ToStation, previousStation, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(section.FromStation, nextStation, StringComparison.OrdinalIgnoreCase);
        if (reverseOrientation)
        {
            return section.DirectionTag switch
            {
                TravelDirection.Up => TravelDirection.Down,
                TravelDirection.Down => TravelDirection.Up,
                _ => TravelDirection.Unknown
            };
        }

        throw new InvalidDataException(
            $"Section '{section.SectionName}' does not connect '{previousStation}' to '{nextStation}'.");
    }
}
