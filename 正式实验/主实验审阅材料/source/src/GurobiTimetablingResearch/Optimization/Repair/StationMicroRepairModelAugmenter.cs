// 车站微观修复增强器把子问题选出的径路股道替代方案加入局部 Gurobi 模型，并创建互斥选择变量。
// 增强结果保存新增变量、约束及选中方案的映射，便于求解后将可行站内分配准确写回主解快照。
using Gurobi;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

public sealed record LocalMicroRepairSelectedAlternative(
    string StationName,
    string RequirementId,
    int TrainIndex,
    string TrackName,
    int? IncomingRouteId,
    int? OutgoingRouteId,
    bool? IsStop = null);

internal sealed record PromotedMicroChoiceVariableBinding(
    string StationName,
    string RequirementId,
    int TrainIndex,
    bool IsStop,
    string TrackName,
    int? IncomingRouteId,
    int? OutgoingRouteId,
    GRBVar Variable);

internal enum PromotedMicroOrderKind { TrackOccupation, RouteHeadway }

internal sealed record PromotedMicroOrderVariableBinding(
    string StationName,
    string FirstRequirementId,
    string SecondRequirementId,
    PromotedMicroOrderKind Kind,
    StationEventSide FirstEventSide,
    StationEventSide SecondEventSide,
    GRBVar Variable,
    double FirstTimeOffsetSeconds = 0.0,
    double SecondTimeOffsetSeconds = 0.0);

internal sealed class StationMicroRepairAugmentation
{
    private readonly IReadOnlyList<(StationMicroRequirement Requirement, StationRouteTrackAlternative Alternative, GRBVar Variable)> _choices;

    internal StationMicroRepairAugmentation(
        IReadOnlyList<(StationMicroRequirement Requirement, StationRouteTrackAlternative Alternative, GRBVar Variable)> choices,
        int conditionalConstraintCount,
        int orderVariableCount,
        int legacyOrderVariableCount,
        int boundPrunedRelationCount,
        IReadOnlyList<PromotedMicroOrderVariableBinding> orderBindings)
    {
        _choices = choices;
        ConditionalConstraintCount = conditionalConstraintCount;
        OrderVariableCount = orderVariableCount;
        LegacyOrderVariableCount = legacyOrderVariableCount;
        BoundPrunedRelationCount = boundPrunedRelationCount;
        OrderBindings = orderBindings;
    }

    internal int AlternativeCount => _choices.Count;
    internal int ConditionalConstraintCount { get; }
    internal int OrderVariableCount { get; }
    internal int LegacyOrderVariableCount { get; }
    internal int BoundPrunedRelationCount { get; }
    internal IReadOnlyList<PromotedMicroOrderVariableBinding> OrderBindings { get; }

    internal IReadOnlyList<PromotedMicroChoiceVariableBinding> ChoiceBindings => _choices
        .Select(choice => new PromotedMicroChoiceVariableBinding(
            choice.Requirement.StationName,
            choice.Requirement.StopId,
            choice.Requirement.TrainIndex,
            choice.Requirement.IsStop,
            choice.Alternative.TrackName,
            choice.Alternative.IncomingRoute?.RouteId,
            choice.Alternative.OutgoingRoute?.RouteId,
            choice.Variable))
        .ToList();

    internal IReadOnlyList<LocalMicroRepairSelectedAlternative> CaptureSelectedAlternatives() => _choices
        .Where(choice => choice.Variable.Get(GRB.DoubleAttr.X) >= 0.5)
        .Select(choice => new LocalMicroRepairSelectedAlternative(
            choice.Requirement.StationName,
            choice.Requirement.StopId,
            choice.Requirement.TrainIndex,
            choice.Alternative.TrackName,
            choice.Alternative.IncomingRoute?.RouteId,
            choice.Alternative.OutgoingRoute?.RouteId,
            choice.Requirement.IsStop))
        .OrderBy(choice => choice.StationName, StringComparer.OrdinalIgnoreCase)
        .ThenBy(choice => choice.TrainIndex)
        .ThenBy(choice => choice.RequirementId, StringComparer.Ordinal)
        .ToList();

    internal PromotedMicroComponent BuildPromotedComponent(
        string stationName,
        string sourceCore,
        int promotionIteration,
        int persistentRounds)
    {
        IReadOnlyList<PromotedStationRequirement> requirements = _choices
            .GroupBy(choice => choice.Requirement.StopId, StringComparer.OrdinalIgnoreCase)
            .Select(group => new PromotedStationRequirement(
                group.Key,
                group.First().Requirement.TrainIndex,
                group.First().Requirement.StationOrder,
                group.First().Requirement.IsStop,
                group.Select(choice => new PromotedRouteTrackAlternative(
                        choice.Alternative.TrackName,
                        choice.Alternative.IncomingRoute?.RouteId,
                        choice.Alternative.OutgoingRoute?.RouteId,
                        choice.Requirement.IsStop))
                    .Distinct()
                    .ToList()))
            .ToList();
        string trainKey = string.Join('-', requirements.Select(item => item.TrainIndex).Distinct().Order());
        string componentId = $"promoted::{stationName}::{trainKey}";
        return new PromotedMicroComponent(
            componentId,
            stationName,
            requirements.Select(item => item.TrainIndex).Distinct().Order().ToList(),
            requirements,
            "proven_station_core_or_persistent_hotspot",
            sourceCore,
            promotionIteration,
            persistentRounds,
            true);
    }
}

internal static class StationMicroRepairModelAugmenter
{
    private const double BigM = 172800.0;

    internal static StationMicroRepairAugmentation Apply(
        BasicMilpModel model,
        IReadOnlyList<StationMicroRequirement> requirements,
        int endpointOccupationSeconds,
        IReadOnlyDictionary<string, IReadOnlyList<PromotedRouteTrackAlternative>>? allowedAlternativesByRequirement = null,
        string variablePrefix = "micro",Func<double>? remaining=null,int? maximumRows=null,int? maximumAlternatives=null,
        bool occupationOnlyForStops=false)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(requirements);
        if (requirements.Count == 0)
        {
            throw new ArgumentException("A station Micro repair requires at least one station requirement.", nameof(requirements));
        }

        void CheckBudget() { if(remaining?.Invoke()<=0) throw new TimeoutException("Micro augmentation deadline expired."); }
        CheckBudget();
        List<Choice> choices = [];
        foreach ((StationMicroRequirement requirement, int requirementIndex) in requirements.Select((value, index) => (value, index)))
        {
            CheckBudget();
            StationOccupationInterval occupation = StationEndpointOccupationPolicy.Resolve(
                requirement.StopRole,
                requirement.IsStop,
                requirement.ArrivalSeconds,
                requirement.DepartureSeconds,
                endpointOccupationSeconds,
                0.0,
                86400.0);
            StationAssignmentRequirement assignmentRequirement = new(
                requirement.StopId,
                requirement.TrainIndex,
                requirement.IsStop,
                requirement.ArrivalSeconds,
                requirement.DepartureSeconds,
                occupation.StartSeconds,
                occupation.EndSeconds,
                requirement.IncomingRoutes,
                requirement.OutgoingRoutes,
                requirement.FeasibleTracks);
            IReadOnlyList<StationRouteTrackAlternative> alternatives =
                StationRouteTrackAssignmentSolver.BuildAlternatives(assignmentRequirement);
            if (allowedAlternativesByRequirement is not null &&
                allowedAlternativesByRequirement.TryGetValue(requirement.StopId, out IReadOnlyList<PromotedRouteTrackAlternative>? allowed))
            {
                alternatives = alternatives.Where(alternative => allowed.Any(item =>
                    (item.IsStop ?? requirement.IsStop) == requirement.IsStop &&
                    string.Equals(item.TrackName, alternative.TrackName, StringComparison.OrdinalIgnoreCase) &&
                    item.IncomingRouteId == alternative.IncomingRoute?.RouteId &&
                    item.OutgoingRouteId == alternative.OutgoingRoute?.RouteId)).ToList();
            }
            if (alternatives.Count == 0)
            {
                // Flexible stop decisions are represented by two requirements with the same
                // StopId.  It is legitimate for one of the stop/pass modes to have no legal
                // route-track alternative; that mode must be removed from the domain rather
                // than aborting the entire station model.  The shared stop-mode equation below
                // then fixes the stop variable to the remaining legal mode.
                continue;
            }

            foreach ((StationRouteTrackAlternative alternative, int alternativeIndex) in alternatives.Select((value, index) => (value, index)))
            {
                CheckBudget();
                if(choices.Count>=maximumAlternatives) throw new ResourceNeighborhoodSizeException("Micro alternatives exceed the configured local limit.");
                GRBVar variable = model.Model.AddVar(
                    0.0,
                    1.0,
                    0.0,
                    GRB.BINARY,
                    $"{variablePrefix}_choice_r{requirementIndex}_a{alternativeIndex}");
                choices.Add(new Choice(requirement, alternative, variable));
            }
        }

        int conditionalConstraintCount = 0;
        int orderVariableCount = 0;
        int legacyOrderVariableCount = 0;
        int boundPrunedRelationCount = 0;
        List<PromotedMicroOrderVariableBinding> orderBindings = [];
        int reservedRows=0;
        void ReserveRows(int count)
        {
            CheckBudget();
            if(maximumRows is int limit && count>limit-reservedRows)
                throw new ResourceNeighborhoodSizeException($"Actual micro rows after bound pruning exceed local limit {limit} (next total {reservedRows+count}).");
            reservedRows+=count;
        }
        void ReserveOrder()
        {
            CheckBudget();
            if(choices.Count+orderBindings.Count>=maximumAlternatives)
                throw new ResourceNeighborhoodSizeException("Actual micro choice/order integers exceed the configured local limit.");
        }
        IReadOnlyList<RequirementChoiceGroup> choicesByRequirement = choices
            .GroupBy(choice => choice.Requirement.StopId, StringComparer.OrdinalIgnoreCase)
            .Select(group => new RequirementChoiceGroup(group.Key, group.ToList()))
            .ToList();
        string? emptyRequirement = requirements.Select(item => item.StopId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(stopId => choicesByRequirement.All(group => !string.Equals(
                group.RequirementId, stopId, StringComparison.OrdinalIgnoreCase)));
        if (emptyRequirement is not null)
            throw new InvalidOperationException(
                $"Station Micro repair has an empty alternative domain for every legal stop/pass mode " +
                $"of requirement '{emptyRequirement}'.");
        foreach ((RequirementChoiceGroup requirementChoices, int requirementIndex) in
                 choicesByRequirement.Select((value, index) => (value, index)))
        {
            GRBLinExpr exactlyOne = new();
            GRBLinExpr stoppingChoice = new();
            foreach (Choice choice in requirementChoices.Choices)
            {
                exactlyOne.AddTerm(1.0, choice.Variable);
                if (choice.Requirement.IsStop)
                {
                    stoppingChoice.AddTerm(1.0, choice.Variable);
                }
            }

            // 同一作业的全部停站和通过替代共用一个选择约束，并把停站替代之和绑定到 Macro 停站变量。
            model.Model.AddConstr(
                exactlyOne,
                GRB.EQUAL,
                1.0,
                $"{variablePrefix}_choice_one_r{requirementIndex}");
            model.Model.AddConstr(
                stoppingChoice - model.StopVars[requirementChoices.RequirementId],
                GRB.EQUAL,
                0.0,
                $"{variablePrefix}_stop_mode_link_r{requirementIndex}");
        }

        // 新增候选变量后先刷新一次模型索引，随后才能可靠读取到发变量的上下界来做安全筛除和收紧 Big-M。
        model.Model.Update();

        for (int firstRequirementIndex = 0; firstRequirementIndex < choicesByRequirement.Count; firstRequirementIndex++)
        {
            for (int secondRequirementIndex = firstRequirementIndex + 1; secondRequirementIndex < choicesByRequirement.Count; secondRequirementIndex++)
            {
                CheckBudget();
                string pairName = $"r{firstRequirementIndex}_{secondRequirementIndex}";
                CompactRelationBuildCounts occupationCounts = AddCompactOccupationDisjunctions(
                    model,
                    occupationOnlyForStops?Stopping(choicesByRequirement[firstRequirementIndex]):choicesByRequirement[firstRequirementIndex],
                    occupationOnlyForStops?Stopping(choicesByRequirement[secondRequirementIndex]):choicesByRequirement[secondRequirementIndex],
                    endpointOccupationSeconds,
                    pairName,
                    variablePrefix,
                    orderBindings,ReserveRows,ReserveOrder);
                CompactRelationBuildCounts routeCounts = AddCompactRouteHeadways(
                    model,
                    choicesByRequirement[firstRequirementIndex],
                    choicesByRequirement[secondRequirementIndex],
                    pairName,
                    variablePrefix,
                    orderBindings,ReserveRows,ReserveOrder,CheckBudget);
                conditionalConstraintCount += occupationCounts.ConstraintCount + routeCounts.ConstraintCount;
                orderVariableCount += occupationCounts.OrderVariableCount + routeCounts.OrderVariableCount;
                legacyOrderVariableCount += occupationCounts.LegacyOrderVariableCount + routeCounts.LegacyOrderVariableCount;
                boundPrunedRelationCount += occupationCounts.BoundPrunedRelationCount + routeCounts.BoundPrunedRelationCount;
            }
        }

        model.Model.Update();
        return new StationMicroRepairAugmentation(
            choices.Select(choice => (choice.Requirement, choice.Alternative, choice.Variable)).ToList(),
            conditionalConstraintCount,
            orderVariableCount,
            legacyOrderVariableCount,
            boundPrunedRelationCount,
            orderBindings);
    }

    private static CompactRelationBuildCounts AddCompactOccupationDisjunctions(
        BasicMilpModel model,
        RequirementChoiceGroup firstGroup,
        RequirementChoiceGroup secondGroup,
        int endpointOccupationSeconds,
        string pairName,
        string variablePrefix,
        ICollection<PromotedMicroOrderVariableBinding> orderBindings,Action<int> reserveRows,Action reserveOrder)
    {
        Dictionary<string, IReadOnlyList<Choice>> firstByTrack = firstGroup.Choices
            .GroupBy(choice => choice.Alternative.TrackName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<Choice>)group.ToList(), StringComparer.OrdinalIgnoreCase);
        Dictionary<string, IReadOnlyList<Choice>> secondByTrack = secondGroup.Choices
            .GroupBy(choice => choice.Alternative.TrackName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<Choice>)group.ToList(), StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<string> commonTracks = firstByTrack.Keys
            .Intersect(secondByTrack.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(track => track, StringComparer.OrdinalIgnoreCase)
            .ToList();
        int legacyCount = commonTracks.Sum(track =>
            firstByTrack[track].Count * secondByTrack[track].Count);
        if (commonTracks.Count == 0)
        {
            return new CompactRelationBuildCounts(0, 0, legacyCount, 0);
        }

        Choice first = firstByTrack[commonTracks[0]][0];
        Choice second = secondByTrack[commonTracks[0]][0];
        OccupationExpression firstOccupation = BuildOccupationExpressions(
            model,
            first.Requirement,
            endpointOccupationSeconds);
        OccupationExpression secondOccupation = BuildOccupationExpressions(
            model,
            second.Requirement,
            endpointOccupationSeconds);
        if (secondOccupation.StartLowerBound - firstOccupation.EndUpperBound >= 0.0 ||
            firstOccupation.StartLowerBound - secondOccupation.EndUpperBound >= 0.0)
        {
            return new CompactRelationBuildCounts(0, 0, legacyCount, commonTracks.Count);
        }

        reserveRows(2*commonTracks.Count);
        reserveOrder();
        GRBVar firstBeforeSecond = model.Model.AddVar(
            0.0,
            1.0,
            0.0,
            GRB.BINARY,
            $"{variablePrefix}_track_order_{pairName}");
        orderBindings.Add(new(
            first.Requirement.StationName,
            firstGroup.RequirementId,
            secondGroup.RequirementId,
            PromotedMicroOrderKind.TrackOccupation,
            ResolveOccupationStartSide(first.Requirement.StopRole),
            ResolveOccupationStartSide(second.Requirement.StopRole),
            firstBeforeSecond,
            first.Requirement.StopRole == TrainStopRole.Origin ? -endpointOccupationSeconds : 0.0,
            second.Requirement.StopRole == TrainStopRole.Origin ? -endpointOccupationSeconds : 0.0));
        int constraintCount = 0;
        foreach ((string track, int trackIndex) in commonTracks.Select((value, index) => (value, index)))
        {
            AddActivatedOrderDisjunction(
                model,
                secondOccupation.StartExpression - firstOccupation.EndExpression,
                secondOccupation.StartLowerBound - firstOccupation.EndUpperBound,
                0.0,
                firstOccupation.StartExpression - secondOccupation.EndExpression,
                firstOccupation.StartLowerBound - secondOccupation.EndUpperBound,
                0.0,
                firstBeforeSecond,
                firstByTrack[track],
                secondByTrack[track],
                $"{variablePrefix}_track_{pairName}_t{trackIndex}");
            constraintCount += 2;
        }

        return new CompactRelationBuildCounts(1, constraintCount, legacyCount, 0);
    }

    private static RequirementChoiceGroup Stopping(RequirementChoiceGroup group)
        =>new(group.RequirementId,group.Choices.Where(c=>c.Requirement.IsStop).ToList());

    private static CompactRelationBuildCounts AddCompactRouteHeadways(
        BasicMilpModel model,
        RequirementChoiceGroup firstGroup,
        RequirementChoiceGroup secondGroup,
        string pairName,
        string variablePrefix,
        ICollection<PromotedMicroOrderVariableBinding> orderBindings,Action<int> reserveRows,Action reserveOrder,Action checkBudget)
    {
        int constraintCount = 0;
        int orderVariableCount = 0;
        int legacyOrderVariableCount = 0;
        int boundPrunedRelationCount = 0;
        foreach (string firstSide in new[] { "in", "out" })
        {
            IReadOnlyList<RouteChoiceGroup> firstRoutes = BuildRouteChoiceGroups(firstGroup, firstSide);
            if (firstRoutes.Count == 0)
            {
                continue;
            }
            foreach (string secondSide in new[] { "in", "out" })
            {
                IReadOnlyList<RouteChoiceGroup> secondRoutes = BuildRouteChoiceGroups(secondGroup, secondSide);
                if (secondRoutes.Count == 0)
                {
                    continue;
                }

                GRBVar firstTime = ResolveEventTime(model, firstGroup, firstSide);
                GRBVar secondTime = ResolveEventTime(model, secondGroup, secondSide);
                List<RouteRelation> activeRelations = [];
                foreach (RouteChoiceGroup firstRoute in firstRoutes)
                {
                    checkBudget();
                    foreach (RouteChoiceGroup secondRoute in secondRoutes)
                    {
                        int? forward = FindHeadway(model.Instance, firstRoute.Route.RouteId, secondRoute.Route.RouteId);
                        int? reverse = FindHeadway(model.Instance, secondRoute.Route.RouteId, firstRoute.Route.RouteId);
                        if (!forward.HasValue && !reverse.HasValue)
                        {
                            continue;
                        }

                        legacyOrderVariableCount += firstRoute.Choices.Count * secondRoute.Choices.Count;
                        int forwardBoundary = forward ?? 0;
                        int reverseBoundary = reverse ?? 0;
                        if (secondTime.Get(GRB.DoubleAttr.LB) - firstTime.Get(GRB.DoubleAttr.UB) >= forwardBoundary ||
                            firstTime.Get(GRB.DoubleAttr.LB) - secondTime.Get(GRB.DoubleAttr.UB) >= reverseBoundary)
                        {
                            boundPrunedRelationCount++;
                            continue;
                        }
                        reserveRows(2);
                        activeRelations.Add(new RouteRelation(firstRoute, secondRoute, forwardBoundary, reverseBoundary));
                    }
                }

                if (activeRelations.Count == 0)
                {
                    continue;
                }

                string eventName = $"{pairName}_{firstSide}_{secondSide}";
                reserveOrder();
                GRBVar firstBeforeSecond = model.Model.AddVar(
                    0.0,
                    1.0,
                    0.0,
                    GRB.BINARY,
                    $"{variablePrefix}_route_order_{eventName}");
                orderBindings.Add(new(
                    firstGroup.Choices[0].Requirement.StationName,
                    firstGroup.RequirementId,
                    secondGroup.RequirementId,
                    PromotedMicroOrderKind.RouteHeadway,
                    firstSide == "in" ? StationEventSide.Incoming : StationEventSide.Outgoing,
                    secondSide == "in" ? StationEventSide.Incoming : StationEventSide.Outgoing,
                    firstBeforeSecond));
                orderVariableCount++;
                foreach (RouteRelation relation in activeRelations)
                {
                    AddActivatedOrderDisjunction(
                        model,
                        secondTime - firstTime,
                        secondTime.Get(GRB.DoubleAttr.LB) - firstTime.Get(GRB.DoubleAttr.UB),
                        relation.ForwardHeadwaySeconds,
                        firstTime - secondTime,
                        firstTime.Get(GRB.DoubleAttr.LB) - secondTime.Get(GRB.DoubleAttr.UB),
                        relation.ReverseHeadwaySeconds,
                        firstBeforeSecond,
                        relation.First.Choices,
                        relation.Second.Choices,
                        $"{variablePrefix}_route_{eventName}_q{relation.First.Route.RouteId}_{relation.Second.Route.RouteId}");
                    constraintCount += 2;
                }
            }
        }

        return new CompactRelationBuildCounts(
            orderVariableCount,
            constraintCount,
            legacyOrderVariableCount,
            boundPrunedRelationCount);
    }

    private static IReadOnlyList<RouteChoiceGroup> BuildRouteChoiceGroups(
        RequirementChoiceGroup requirementGroup,
        string side)
    {
        return requirementGroup.Choices
            .Where(choice => ResolveRoute(choice, side) is not null)
            .GroupBy(choice => ResolveRoute(choice, side)!.RouteId)
            .Select(group => new RouteChoiceGroup(
                ResolveRoute(group.First(), side)!,
                group.ToList()))
            .OrderBy(group => group.Route.RouteId)
            .ToList();
    }

    private static RouteRecord? ResolveRoute(Choice choice, string side) =>
        side == "in" ? choice.Alternative.IncomingRoute : choice.Alternative.OutgoingRoute;

    private static GRBVar ResolveEventTime(
        BasicMilpModel model,
        RequirementChoiceGroup group,
        string side) => side == "in"
        ? model.ArrivalVars[group.RequirementId]
        : model.DepartureVars[group.RequirementId];

    private static StationEventSide ResolveOccupationStartSide(TrainStopRole role) =>
        role == TrainStopRole.Origin ? StationEventSide.Outgoing : StationEventSide.Incoming;

    private static void AddActivatedOrderDisjunction(
        BasicMilpModel model,
        GRBLinExpr forwardExpression,
        double forwardLowerBound,
        double forwardRightHandSide,
        GRBLinExpr reverseExpression,
        double reverseLowerBound,
        double reverseRightHandSide,
        GRBVar firstBeforeSecond,
        IReadOnlyList<Choice> firstActivationChoices,
        IReadOnlyList<Choice> secondActivationChoices,
        string constraintPrefix)
    {
        double forwardRelaxation = SafeRelaxation(forwardRightHandSide, forwardLowerBound);
        double reverseRelaxation = SafeRelaxation(reverseRightHandSide, reverseLowerBound);
        AddActivationRelaxation(
            forwardExpression,
            forwardRelaxation,
            firstBeforeSecond,
            relaxWhenOrderIsOne: false,
            firstActivationChoices,
            secondActivationChoices);
        AddActivationRelaxation(
            reverseExpression,
            reverseRelaxation,
            firstBeforeSecond,
            relaxWhenOrderIsOne: true,
            firstActivationChoices,
            secondActivationChoices);
        model.Model.AddConstr(
            forwardExpression,
            GRB.GREATER_EQUAL,
            forwardRightHandSide,
            $"{constraintPrefix}_fwd");
        model.Model.AddConstr(
            reverseExpression,
            GRB.GREATER_EQUAL,
            reverseRightHandSide,
            $"{constraintPrefix}_rev");
    }

    private static void AddActivationRelaxation(
        GRBLinExpr expression,
        double relaxation,
        GRBVar orderVariable,
        bool relaxWhenOrderIsOne,
        IReadOnlyList<Choice> firstActivationChoices,
        IReadOnlyList<Choice> secondActivationChoices)
    {
        if (relaxation <= 0.0)
        {
            return;
        }

        if (relaxWhenOrderIsOne)
        {
            expression.AddTerm(relaxation, orderVariable);
        }
        else
        {
            expression.AddConstant(relaxation);
            expression.AddTerm(-relaxation, orderVariable);
        }
        expression.AddConstant(2.0 * relaxation);
        foreach (Choice choice in firstActivationChoices)
        {
            expression.AddTerm(-relaxation, choice.Variable);
        }
        foreach (Choice choice in secondActivationChoices)
        {
            expression.AddTerm(-relaxation, choice.Variable);
        }
    }

    private static double SafeRelaxation(double rightHandSide, double expressionLowerBound)
    {
        if (!double.IsFinite(expressionLowerBound))
        {
            return BigM;
        }
        return Math.Max(0.0, rightHandSide - expressionLowerBound);
    }

    private static OccupationExpression BuildOccupationExpressions(
        BasicMilpModel model,
        StationMicroRequirement requirement,
        int endpointOccupationSeconds)
    {
        GRBVar arrival = model.ArrivalVars[requirement.StopId];
        GRBVar departure = model.DepartureVars[requirement.StopId];
        return requirement.StopRole switch
        {
            TrainStopRole.Origin => new OccupationExpression(
                departure - endpointOccupationSeconds,
                departure,
                departure.Get(GRB.DoubleAttr.LB) - endpointOccupationSeconds,
                departure.Get(GRB.DoubleAttr.UB) - endpointOccupationSeconds,
                departure.Get(GRB.DoubleAttr.LB),
                departure.Get(GRB.DoubleAttr.UB)),
            TrainStopRole.Destination => new OccupationExpression(
                arrival,
                arrival + endpointOccupationSeconds,
                arrival.Get(GRB.DoubleAttr.LB),
                arrival.Get(GRB.DoubleAttr.UB),
                arrival.Get(GRB.DoubleAttr.LB) + endpointOccupationSeconds,
                arrival.Get(GRB.DoubleAttr.UB) + endpointOccupationSeconds),
            _ => new OccupationExpression(
                arrival,
                departure,
                arrival.Get(GRB.DoubleAttr.LB),
                arrival.Get(GRB.DoubleAttr.UB),
                departure.Get(GRB.DoubleAttr.LB),
                departure.Get(GRB.DoubleAttr.UB))
        };
    }

    private static int? FindHeadway(TimetablingInstance instance, int leadingRouteId, int followingRouteId) =>
        instance.TryGetRouteHeadwayRule(leadingRouteId, followingRouteId, out RouteHeadwayRule? rule) && rule is not null
            ? rule.MinHeadwaySeconds
            : null;

    private sealed record Choice(
        StationMicroRequirement Requirement,
        StationRouteTrackAlternative Alternative,
        GRBVar Variable);

    private sealed record RequirementChoiceGroup(
        string RequirementId,
        IReadOnlyList<Choice> Choices);

    private sealed record RouteChoiceGroup(
        RouteRecord Route,
        IReadOnlyList<Choice> Choices);

    private sealed record RouteRelation(
        RouteChoiceGroup First,
        RouteChoiceGroup Second,
        int ForwardHeadwaySeconds,
        int ReverseHeadwaySeconds);

    private sealed record OccupationExpression(
        GRBLinExpr StartExpression,
        GRBLinExpr EndExpression,
        double StartLowerBound,
        double StartUpperBound,
        double EndLowerBound,
        double EndUpperBound);

    private readonly record struct CompactRelationBuildCounts(
        int OrderVariableCount,
        int ConstraintCount,
        int LegacyOrderVariableCount,
        int BoundPrunedRelationCount);
}
