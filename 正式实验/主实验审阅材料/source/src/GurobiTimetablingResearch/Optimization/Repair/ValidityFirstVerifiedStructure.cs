using System.Security.Cryptography;
using System.Text;
using Gurobi;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

/// <summary>
/// 将 Global Micro 已验证的每个车站作业映射为单一进路—股道备选，供阶段 3 固定离散结构后优化连续时刻。
/// </summary>
internal static class ValidityFirstFeasibleAssignmentPromotionBuilder
{
    internal static IReadOnlyList<PromotedMicroComponent> Build(
        StationLevelMicroFeasibilitySlaveResult result,
        int promotionIteration)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.IsFeasible || result.Conflicts.Count > 0)
        {
            throw new InvalidOperationException(
                "Stage-3 assignment fixation requires a complete conflict-free Micro result.");
        }

        List<PromotedMicroComponent> components = [];
        foreach (StationLevelMicroSubproblemResult station in result.StationResults)
        {
            if (!station.IsFeasible || !station.AssignmentResult.IsFeasible)
            {
                throw new InvalidOperationException(
                    $"Stage-3 assignment fixation is incomplete at station '{station.StationName}'.");
            }

            Dictionary<string, StationRouteTrackAlternative> selectedByRequirement =
                station.AssignmentResult.SelectedAlternatives
                    .GroupBy(alternative => alternative.RequirementId, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        group => group.Key,
                        group => group.Count() == 1
                            ? group.Single()
                            : throw new InvalidOperationException(
                                $"Station '{station.StationName}' has a non-unique verified assignment for '{group.Key}'."),
                        StringComparer.OrdinalIgnoreCase);
            List<PromotedStationRequirement> requirements = [];
            foreach (StationMicroRequirement requirement in station.Requirements)
            {
                if (!selectedByRequirement.TryGetValue(
                        requirement.StopId,
                        out StationRouteTrackAlternative? selected))
                {
                    throw new InvalidOperationException(
                        $"Station '{station.StationName}' is missing a verified assignment for '{requirement.StopId}'.");
                }

                requirements.Add(new PromotedStationRequirement(
                    requirement.StopId,
                    requirement.TrainIndex,
                    requirement.StationOrder,
                    requirement.IsStop,
                    [new PromotedRouteTrackAlternative(
                        selected.TrackName,
                        selected.IncomingRoute?.RouteId,
                        selected.OutgoingRoute?.RouteId,
                        requirement.IsStop)],
                    PreserveAllLegalStopModes: false));
            }

            if (requirements.Count == 0)
            {
                continue;
            }

            string canonicalAssignments = string.Join('|', requirements
                .OrderBy(requirement => requirement.RequirementId, StringComparer.OrdinalIgnoreCase)
                .Select(requirement =>
                {
                    PromotedRouteTrackAlternative selected = requirement.AllowedAlternatives.Single();
                    return $"{requirement.RequirementId}:{selected.TrackName}:" +
                           $"{selected.IncomingRouteId}:{selected.OutgoingRouteId}:{requirement.IsStop}";
                }));
            string hash = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(canonicalAssignments)))
                .ToLowerInvariant()[..16];
            components.Add(new PromotedMicroComponent(
                $"validity_stage3_assignment::{station.StationName}::{hash}",
                station.StationName,
                requirements.Select(requirement => requirement.TrainIndex).Distinct().Order().ToList(),
                requirements,
                "verified_joint_feasible_assignment_fixation",
                canonicalAssignments,
                promotionIteration,
                1,
                true));
        }

        if (components.Sum(component => component.Requirements.Count) !=
            result.StationResults.Sum(station => station.Requirements.Count))
        {
            throw new InvalidOperationException(
                "Stage-3 assignment fixation did not cover every checked Micro requirement.");
        }

        return components;
    }

    /// <summary>
    /// 使用已经随候选保存并带有候选指纹的见证构造阶段 3 单选组件。
    /// 站级求解结果仅提供当前需求域，不用它重新搜索得到的另一套选择替换原见证。
    /// </summary>
    internal static IReadOnlyList<PromotedMicroComponent> BuildFromVerifiedWitnesses(
        StationLevelMicroFeasibilitySlaveResult result,
        IReadOnlyList<VerifiedMicroAssignmentWitness> witnesses,
        int promotionIteration)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(witnesses);
        Dictionary<string, VerifiedMicroAssignmentWitness> witnessByRequirement = witnesses
            .GroupBy(item => item.RequirementId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Count() == 1
                    ? group.Single()
                    : throw new InvalidDataException($"Verified assignment '{group.Key}' is duplicated."),
                StringComparer.OrdinalIgnoreCase);
        List<PromotedMicroComponent> components = [];
        foreach (StationLevelMicroSubproblemResult station in result.StationResults)
        {
            List<PromotedStationRequirement> requirements = [];
            foreach (StationMicroRequirement requirement in station.Requirements)
            {
                if (!witnessByRequirement.TryGetValue(requirement.StopId, out VerifiedMicroAssignmentWitness? witness))
                {
                    throw new InvalidDataException(
                        $"Verified assignment is missing current requirement '{requirement.StopId}'.");
                }
                if (!string.Equals(witness.StationName, station.StationName, StringComparison.OrdinalIgnoreCase) ||
                    witness.TrainIndex != requirement.TrainIndex || witness.IsStop != requirement.IsStop)
                {
                    throw new InvalidDataException(
                        $"Verified assignment '{requirement.StopId}' does not match its current station/train/mode requirement.");
                }

                requirements.Add(new PromotedStationRequirement(
                    requirement.StopId,
                    requirement.TrainIndex,
                    requirement.StationOrder,
                    requirement.IsStop,
                    [new PromotedRouteTrackAlternative(
                        witness.TrackName,
                        witness.IncomingRouteId,
                        witness.OutgoingRouteId,
                        witness.IsStop)],
                    PreserveAllLegalStopModes: false));
            }
            if (requirements.Count == 0)
            {
                continue;
            }

            string canonicalAssignments = string.Join('|', requirements
                .OrderBy(item => item.RequirementId, StringComparer.OrdinalIgnoreCase)
                .Select(item =>
                {
                    PromotedRouteTrackAlternative selected = item.AllowedAlternatives.Single();
                    return $"{item.RequirementId}:{selected.TrackName}:" +
                           $"{selected.IncomingRouteId}:{selected.OutgoingRouteId}:{item.IsStop}";
                }));
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalAssignments)))
                .ToLowerInvariant()[..16];
            components.Add(new PromotedMicroComponent(
                $"validity_stage3_assignment::{station.StationName}::{hash}",
                station.StationName,
                requirements.Select(item => item.TrainIndex).Distinct().Order().ToList(),
                requirements,
                "verified_joint_feasible_assignment_fixation",
                canonicalAssignments,
                promotionIteration,
                1,
                true));
        }

        int requirementCount = result.StationResults.Sum(station => station.Requirements.Count);
        if (components.Sum(component => component.Requirements.Count) != requirementCount ||
            witnessByRequirement.Count != requirementCount)
        {
            throw new InvalidDataException(
                $"Verified assignment coverage mismatch: requirements={requirementCount}, witnesses={witnessByRequirement.Count}.");
        }
        return components;
    }
}

/// <summary>
/// 阶段 3 模型临时组合持久热点与当前候选的单选分配；该方法不修改持久组件集合。
/// </summary>
internal static class ValidityFirstStageThreeComponentSet
{
    internal static IReadOnlyList<PromotedMicroComponent> Compose(
        IReadOnlyList<PromotedMicroComponent> persistentComponents,
        IReadOnlyList<PromotedMicroComponent> transientAssignments)
    {
        ArgumentNullException.ThrowIfNull(persistentComponents);
        ArgumentNullException.ThrowIfNull(transientAssignments);
        List<PromotedMicroComponent> combined = [.. persistentComponents, .. transientAssignments];
        IReadOnlyList<string> duplicateIds = combined
            .GroupBy(component => component.ComponentId, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (duplicateIds.Count > 0)
        {
            throw new InvalidDataException(
                $"Stage-3 component IDs are duplicated: {string.Join('|', duplicateIds)}");
        }
        return combined;
    }
}

internal sealed record ValidityFirstVerifiedStructureFixationResult(
    int FixedStopCount,
    int FixedSectionPrecedenceCount,
    int FixedStationEventPrecedenceCount,
    int FixedTrackOrderCount,
    int FixedRouteOrderCount);

/// <summary>
/// 用等式固定已验证的停站和 Macro 资源次序，并用直接时序约束保持已选股道、进路的先后关系；到发时刻本身不固定。
/// </summary>
internal static class ValidityFirstVerifiedStructureFixer
{
    private const double ToleranceSeconds = GurobiTimetablingResearch.Domain.TimetableTimePolicy.NumericToleranceSeconds;

    internal static ValidityFirstVerifiedStructureFixationResult Apply(
        BasicMilpModel model,
        MasterSolutionSnapshot snapshot,
        IReadOnlyList<PromotedMicroComponent> verifiedAssignments,
        int endpointOccupationSeconds = StationEndpointOccupationPolicy.DefaultSeconds,
        bool allowRetimingOfCurrentOrderViolations = false)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(verifiedAssignments);
        int fixedStops = FixBinaryDictionary(
            model,
            model.StopVars,
            snapshot.StopByStopId,
            "stage3_stop");
        int fixedSections = FixBinaryDictionary(
            model,
            model.SectionPrecedenceVars,
            snapshot.SectionPrecedenceByKey,
            "stage3_section_order");
        int fixedStationEvents = FixBinaryDictionary(
            model,
            model.StationEventPrecedenceVars,
            snapshot.StationEventPrecedenceByKey,
            "stage3_station_order");
        int fixedTrackOrders = 0;
        int fixedRouteOrders = 0;
        int relationIndex = 0;
        Dictionary<string, TrainStopRecord> stopsById = model.Instance.TrainStops.ToDictionary(
            stop => stop.StopId,
            StringComparer.OrdinalIgnoreCase);

        foreach (PromotedMicroComponent component in verifiedAssignments)
        {
            IReadOnlyList<PromotedStationRequirement> requirements = component.Requirements;
            if (requirements.Any(requirement => requirement.AllowedAlternatives.Count != 1))
            {
                throw new InvalidOperationException(
                    $"Stage-3 verified component '{component.ComponentId}' is not fully fixed.");
            }

            for (int firstIndex = 0; firstIndex < requirements.Count; firstIndex++)
            {
                PromotedStationRequirement first = requirements[firstIndex];
                PromotedRouteTrackAlternative firstAlternative = first.AllowedAlternatives.Single();
                TrainStopRecord firstStop = ResolveStop(stopsById, first.RequirementId);
                for (int secondIndex = firstIndex + 1; secondIndex < requirements.Count; secondIndex++)
                {
                    PromotedStationRequirement second = requirements[secondIndex];
                    PromotedRouteTrackAlternative secondAlternative = second.AllowedAlternatives.Single();
                    TrainStopRecord secondStop = ResolveStop(stopsById, second.RequirementId);
                    relationIndex++;

                    if (first.IsStop && second.IsStop && string.Equals(
                            firstAlternative.TrackName,
                            secondAlternative.TrackName,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        AddFixedTrackOrder(
                            model,
                            snapshot,
                            firstStop,
                            secondStop,
                            endpointOccupationSeconds,
                            relationIndex,
                            allowRetimingOfCurrentOrderViolations);
                        fixedTrackOrders++;
                    }

                    foreach (VerifiedRouteEvent firstEvent in EnumerateRouteEvents(
                                 model,
                                 snapshot,
                                 firstStop,
                                 firstAlternative))
                    {
                        foreach (VerifiedRouteEvent secondEvent in EnumerateRouteEvents(
                                     model,
                                     snapshot,
                                     secondStop,
                                     secondAlternative))
                        {
                            if (AddFixedRouteOrder(
                                    model,
                                    firstEvent,
                                    secondEvent,
                                    relationIndex,
                                    allowRetimingOfCurrentOrderViolations))
                            {
                                fixedRouteOrders++;
                            }
                        }
                    }
                }
            }
        }

        model.Model.Update();
        return new ValidityFirstVerifiedStructureFixationResult(
            fixedStops,
            fixedSections,
            fixedStationEvents,
            fixedTrackOrders,
            fixedRouteOrders);
    }

    private static int FixBinaryDictionary(
        BasicMilpModel model,
        IReadOnlyDictionary<string, GRBVar> variables,
        IReadOnlyDictionary<string, int> values,
        string prefix)
    {
        int index = 0;
        foreach ((string key, GRBVar variable) in variables.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!values.TryGetValue(key, out int value))
            {
                throw new InvalidDataException(
                    $"Verified stage-3 snapshot is missing binary decision '{key}'.");
            }
            model.Model.AddConstr(variable, GRB.EQUAL, value, $"{prefix}_{index++}");
        }
        return index;
    }

    private static void AddFixedTrackOrder(
        BasicMilpModel model,
        MasterSolutionSnapshot snapshot,
        TrainStopRecord first,
        TrainStopRecord second,
        int endpointOccupationSeconds,
        int relationIndex,
        bool allowRetimingOfCurrentOrderViolations)
    {
        VerifiedOccupation firstOccupation = ResolveOccupation(snapshot, first, endpointOccupationSeconds);
        VerifiedOccupation secondOccupation = ResolveOccupation(snapshot, second, endpointOccupationSeconds);
        (GRBLinExpr firstStart, GRBLinExpr firstEnd) = BuildOccupationExpressions(
            model,
            first,
            endpointOccupationSeconds);
        (GRBLinExpr secondStart, GRBLinExpr secondEnd) = BuildOccupationExpressions(
            model,
            second,
            endpointOccupationSeconds);
        if (secondOccupation.StartSeconds + ToleranceSeconds >= firstOccupation.EndSeconds)
        {
            model.Model.AddConstr(
                secondStart - firstEnd,
                GRB.GREATER_EQUAL,
                0.0,
                $"stage3_track_order_{relationIndex}");
            return;
        }
        if (firstOccupation.StartSeconds + ToleranceSeconds >= secondOccupation.EndSeconds)
        {
            model.Model.AddConstr(
                firstStart - secondEnd,
                GRB.GREATER_EQUAL,
                0.0,
                $"stage3_track_order_{relationIndex}");
            return;
        }

        if (allowRetimingOfCurrentOrderViolations &&
            Math.Abs(firstOccupation.StartSeconds - secondOccupation.StartSeconds) > ToleranceSeconds)
        {
            if (firstOccupation.StartSeconds < secondOccupation.StartSeconds)
            {
                model.Model.AddConstr(
                    secondStart - firstEnd,
                    GRB.GREATER_EQUAL,
                    0.0,
                    $"stage3_track_order_{relationIndex}");
            }
            else
            {
                model.Model.AddConstr(
                    firstStart - secondEnd,
                    GRB.GREATER_EQUAL,
                    0.0,
                    $"stage3_track_order_{relationIndex}");
            }
            return;
        }

        throw new InvalidOperationException(
            $"Verified track assignment is internally inconsistent for '{first.StopId}' and '{second.StopId}'.");
    }

    private static bool AddFixedRouteOrder(
        BasicMilpModel model,
        VerifiedRouteEvent first,
        VerifiedRouteEvent second,
        int relationIndex,
        bool allowRetimingOfCurrentOrderViolations)
    {
        SelectedRouteEventHeadwayDecision decision = SelectedRouteEventHeadwayPolicy.Evaluate(
            first.RouteId,
            first.TimeSeconds,
            second.RouteId,
            second.TimeSeconds,
            (leading, following) => FindHeadway(model.Instance, leading, following));
        if (decision.Kind == SelectedRouteEventHeadwayDecisionKind.NoConstraint)
        {
            return false;
        }
        if ((decision.Kind == SelectedRouteEventHeadwayDecisionKind.Satisfied ||
             allowRetimingOfCurrentOrderViolations) &&
            decision.FixedOrder == SelectedRouteEventOrder.FirstBeforeSecond)
        {
            model.Model.AddConstr(
                second.TimeVariable - first.TimeVariable,
                GRB.GREATER_EQUAL,
                decision.RequiredHeadwaySeconds,
                $"stage3_route_order_{relationIndex}_{first.Side}_{second.Side}_{first.RouteId}_{second.RouteId}");
            return true;
        }
        if ((decision.Kind == SelectedRouteEventHeadwayDecisionKind.Satisfied ||
             allowRetimingOfCurrentOrderViolations) &&
            decision.FixedOrder == SelectedRouteEventOrder.SecondBeforeFirst)
        {
            model.Model.AddConstr(
                first.TimeVariable - second.TimeVariable,
                GRB.GREATER_EQUAL,
                decision.RequiredHeadwaySeconds,
                $"stage3_route_order_{relationIndex}_{second.Side}_{first.Side}_{second.RouteId}_{first.RouteId}");
            return true;
        }

        throw new InvalidOperationException(
            "Verified route assignment is internally inconsistent: " +
            $"station='{first.StationName}', first_stop='{first.StopId}', first_train={first.TrainIndex}/'{first.TrainNo}', " +
            $"first_event={first.Side}, first_route={first.RouteId}, first_time={first.TimeSeconds:F3}; " +
            $"second_stop='{second.StopId}', second_train={second.TrainIndex}/'{second.TrainNo}', " +
            $"second_event={second.Side}, second_route={second.RouteId}, second_time={second.TimeSeconds:F3}; " +
            $"forward={decision.ForwardHeadwaySeconds?.ToString() ?? "missing"}, " +
            $"reverse={decision.ReverseHeadwaySeconds?.ToString() ?? "missing"}, basis={decision.RuleBasis}.");
    }

    private static IEnumerable<VerifiedRouteEvent> EnumerateRouteEvents(
        BasicMilpModel model,
        MasterSolutionSnapshot snapshot,
        TrainStopRecord stop,
        PromotedRouteTrackAlternative alternative)
    {
        if (alternative.IncomingRouteId.HasValue)
        {
            yield return new VerifiedRouteEvent(
                alternative.IncomingRouteId.Value,
                model.ArrivalVars[stop.StopId],
                Read(snapshot.ArrivalSecondsByStopId, stop.StopId),
                "in",
                stop.StopId,
                stop.TrainIndex,
                stop.TrainNo,
                stop.StationName);
        }
        if (alternative.OutgoingRouteId.HasValue)
        {
            yield return new VerifiedRouteEvent(
                alternative.OutgoingRouteId.Value,
                model.DepartureVars[stop.StopId],
                Read(snapshot.DepartureSecondsByStopId, stop.StopId),
                "out",
                stop.StopId,
                stop.TrainIndex,
                stop.TrainNo,
                stop.StationName);
        }
    }

    private static (GRBLinExpr Start, GRBLinExpr End) BuildOccupationExpressions(
        BasicMilpModel model,
        TrainStopRecord stop,
        int endpointOccupationSeconds)
    {
        GRBVar arrival = model.ArrivalVars[stop.StopId];
        GRBVar departure = model.DepartureVars[stop.StopId];
        return stop.StopRole switch
        {
            TrainStopRole.Origin => (departure - endpointOccupationSeconds, departure),
            TrainStopRole.Destination => (arrival, arrival + endpointOccupationSeconds),
            _ => (arrival, departure)
        };
    }

    private static VerifiedOccupation ResolveOccupation(
        MasterSolutionSnapshot snapshot,
        TrainStopRecord stop,
        int endpointOccupationSeconds)
    {
        double arrival = Read(snapshot.ArrivalSecondsByStopId, stop.StopId);
        double departure = Read(snapshot.DepartureSecondsByStopId, stop.StopId);
        return stop.StopRole switch
        {
            TrainStopRole.Origin => new VerifiedOccupation(
                departure - endpointOccupationSeconds,
                departure),
            TrainStopRole.Destination => new VerifiedOccupation(
                arrival,
                arrival + endpointOccupationSeconds),
            _ => new VerifiedOccupation(arrival, departure)
        };
    }

    private static TrainStopRecord ResolveStop(
        IReadOnlyDictionary<string, TrainStopRecord> stopsById,
        string stopId) => stopsById.TryGetValue(stopId, out TrainStopRecord? stop)
        ? stop
        : throw new InvalidDataException($"Verified stage-3 assignment references missing stop '{stopId}'.");

    private static double Read(IReadOnlyDictionary<string, double> values, string stopId) =>
        values.TryGetValue(stopId, out double value)
            ? value
            : throw new InvalidDataException($"Verified stage-3 snapshot is missing time for '{stopId}'.");

    private static int? FindHeadway(TimetablingInstance instance, int leadingRouteId, int followingRouteId) =>
        instance.TryGetRouteHeadwayRule(leadingRouteId, followingRouteId, out RouteHeadwayRule? rule) && rule is not null
            ? rule.MinHeadwaySeconds
            : null;

    private sealed record VerifiedOccupation(double StartSeconds, double EndSeconds);

    private sealed record VerifiedRouteEvent(
        int RouteId,
        GRBVar TimeVariable,
        double TimeSeconds,
        string Side,
        string StopId,
        int TrainIndex,
        string TrainNo,
        string StationName);
}
