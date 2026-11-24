// Use adjacent section order to identify waiting visits and enforce the shared-route pair cap.
using Gurobi;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

public sealed record PairOvertakeState(bool FirstBeingOvertaken, bool SecondBeingOvertaken);

public static class PairOvertakeResolver
{
    public static PairOvertakeState Resolve(int incomingPrecedence, int outgoingPrecedence)
    {
        if (incomingPrecedence is not (0 or 1) || outgoingPrecedence is not (0 or 1))
        {
            throw new ArgumentOutOfRangeException(nameof(incomingPrecedence));
        }
        return new PairOvertakeState(
            incomingPrecedence == 0 && outgoingPrecedence == 1,
            incomingPrecedence == 1 && outgoingPrecedence == 0);
    }
}

public static class OvertakingDwellPolicy
{
    public static int ResolveAppliedMaximum(
        bool isStop,
        bool isBeingOvertaken,
        int normalMaximumSeconds,
        int waitingMaximumSeconds)
    {
        if (normalMaximumSeconds < 0 || waitingMaximumSeconds < normalMaximumSeconds)
        {
            throw new InvalidDataException("Waiting dwell maximum must be at least the normal dwell maximum.");
        }
        if (!isStop)
        {
            return 0;
        }
        return isBeingOvertaken ? waitingMaximumSeconds : normalMaximumSeconds;
    }
}

public sealed record OvertakingReversalVariableBinding(
    string StopId,
    GRBVar IncomingPrecedence,
    GRBVar OutgoingPrecedence,
    bool FirstTrainReversal,
    GRBVar ReversalVariable);

/// <summary>
/// Immutable projection of the original adjacent-section overtaking rule.  The keys use
/// the exact SectionConflictCandidate orientation, so 0-&gt;1 overtakes the first train and
/// 1-&gt;0 overtakes the second train.
/// </summary>
public sealed record OvertakingStopProjection(
    string ProjectionId,
    string StationName,
    int FirstTrainIndex,
    int SecondTrainIndex,
    string FirstStopId,
    string SecondStopId,
    string IncomingPrecedenceKey,
    string OutgoingPrecedenceKey);

public sealed record OvertakingStopProjectionViolation(
    string ProjectionId,
    string StationName,
    string StopId,
    int IncomingPrecedence,
    int OutgoingPrecedence,
    string BusinessRule);

/// <summary>
/// Builds common intermediate-visit projections for full-model and sparse order relations.
/// </summary>
public static class OvertakingStopProjectionPlanner
{
    /// <summary>Find same-direction pairs sharing the modeled incoming and outgoing legs.</summary>
    /// <param name="instance">Original train routes and visit roles.</param>
    /// <param name="candidates">Available section relations at the requested graph density.</param>
    /// <returns>One projection per comparable train pair at a shared intermediate station.</returns>
    public static IReadOnlyList<OvertakingStopProjection> Plan(
        TimetablingInstance instance,
        IReadOnlyList<SectionConflictCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(candidates);
        Dictionary<string, SectionConflictCandidate> byKey = candidates.ToDictionary(
            BasicMilpModelBuilder.BuildSectionPrecedenceKey,
            StringComparer.OrdinalIgnoreCase);
        Dictionary<(int TrainIndex, string StationName), TrainStopRecord> stopAtStation = instance.TrainStops
            .GroupBy(stop => (stop.TrainIndex, stop.StationName), new TrainStationComparer())
            .ToDictionary(group => group.Key, group => group.First(), new TrainStationComparer());
        List<OvertakingStopProjection> result = [];

        foreach ((string stationName, string incomingSection, string outgoingSection) in BuildAdjacentSections(instance))
        {
            foreach (SectionConflictCandidate incoming in candidates.Where(item => string.Equals(
                         item.SectionName, incomingSection, StringComparison.OrdinalIgnoreCase)))
            {
                string incomingKey = BasicMilpModelBuilder.BuildSectionPrecedenceKey(incoming);
                string outgoingKey = BasicMilpModelBuilder.BuildSectionPrecedenceKey(
                    outgoingSection, incoming.FirstTrainIndex, incoming.SecondTrainIndex);
                if (!byKey.ContainsKey(outgoingKey) ||
                    !stopAtStation.TryGetValue((incoming.FirstTrainIndex, stationName), out TrainStopRecord? firstStop) ||
                    !stopAtStation.TryGetValue((incoming.SecondTrainIndex, stationName), out TrainStopRecord? secondStop) ||
                    firstStop.StopRole != TrainStopRole.Intermediate || secondStop.StopRole != TrainStopRole.Intermediate)
                    continue;

                string projectionId =
                    $"overtake-stop::{stationName}::{incoming.FirstTrainIndex}::{incoming.SecondTrainIndex}";
                result.Add(new(projectionId, stationName, incoming.FirstTrainIndex, incoming.SecondTrainIndex,
                    firstStop.StopId, secondStop.StopId, incomingKey, outgoingKey));
            }
        }

        return result;
    }

    public static IReadOnlyList<OvertakingStopProjectionViolation> FindViolations(
        IReadOnlyList<OvertakingStopProjection> projections,
        IReadOnlyDictionary<string, int> stopByStopId,
        IReadOnlyDictionary<string, int> precedenceByKey)
    {
        List<OvertakingStopProjectionViolation> result = [];
        foreach (OvertakingStopProjection projection in projections)
        {
            if (!precedenceByKey.TryGetValue(projection.IncomingPrecedenceKey, out int incoming) ||
                !precedenceByKey.TryGetValue(projection.OutgoingPrecedenceKey, out int outgoing))
                continue;
            if (incoming is not (0 or 1) || outgoing is not (0 or 1))
                throw new InvalidDataException($"Non-binary precedence in '{projection.ProjectionId}'.");
            if (outgoing - incoming == 1 && stopByStopId.GetValueOrDefault(projection.FirstStopId) != 1)
                result.Add(new(projection.ProjectionId, projection.StationName, projection.FirstStopId,
                    incoming, outgoing, "s_first >= o_out - o_in"));
            if (incoming - outgoing == 1 && stopByStopId.GetValueOrDefault(projection.SecondStopId) != 1)
                result.Add(new(projection.ProjectionId, projection.StationName, projection.SecondStopId,
                    incoming, outgoing, "s_second >= o_in - o_out"));
        }
        return result;
    }

    private static IReadOnlyList<(string StationName, string IncomingSection, string OutgoingSection)> BuildAdjacentSections(
        TimetablingInstance instance)
    {
        Dictionary<string, (string Incoming, string Outgoing)> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (TrainRecord train in instance.Trains)
        for (int index = 1; index < train.OrderedStops.Count - 1; index++)
        {
            TrainStopRecord current = train.OrderedStops[index];
            TrainStopRecord next = train.OrderedStops[index + 1];
            if (!string.IsNullOrWhiteSpace(current.IncomingSectionName) &&
                !string.IsNullOrWhiteSpace(next.IncomingSectionName))
                result.TryAdd(current.StationName, (current.IncomingSectionName, next.IncomingSectionName));
        }
        return result.Select(item => (item.Key, item.Value.Incoming, item.Value.Outgoing)).ToArray();
    }

    private sealed class TrainStationComparer : IEqualityComparer<(int TrainIndex, string StationName)>
    {
        public bool Equals((int TrainIndex, string StationName) x, (int TrainIndex, string StationName) y) =>
            x.TrainIndex == y.TrainIndex && string.Equals(x.StationName, y.StationName, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((int TrainIndex, string StationName) obj) =>
            HashCode.Combine(obj.TrainIndex, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.StationName));
    }
}

internal static class OvertakingDwellConstraintBuilder
{
    /// <summary>Add directional reversal indicators, pair and visit caps, and waiting dwell support.</summary>
    /// <param name="model">Borrowed native model to augment.</param>
    /// <param name="instance">Original routes and dwell rules.</param>
    /// <param name="candidates">Available modeled section relations.</param>
    /// <param name="sectionPrecedenceVars">Binary order variables by section relation.</param>
    /// <param name="arrivalVars">Arrival time variables in seconds by visit.</param>
    /// <param name="departureVars">Departure time variables in seconds by visit.</param>
    /// <param name="stopVars">Stop indicators by visit.</param>
    /// <param name="beingOvertakenVars">Receives waiting indicators keyed by visit.</param>
    /// <param name="reversalBindings">Receives continuous reversal indicators linked to binary orders.</param>
    /// <param name="preserveOmittedSupport">Whether an omitted relation may supply relaxed waiting support.</param>
    internal static void Apply(
        GRBModel model,
        TimetablingInstance instance,
        IReadOnlyList<SectionConflictCandidate> candidates,
        IReadOnlyDictionary<string, GRBVar> sectionPrecedenceVars,
        IReadOnlyDictionary<string, GRBVar> arrivalVars,
        IReadOnlyDictionary<string, GRBVar> departureVars,
        IReadOnlyDictionary<string, GRBVar> stopVars,
        IDictionary<string, GRBVar> beingOvertakenVars,
        ICollection<OvertakingReversalVariableBinding> reversalBindings,
        bool preserveOmittedSupport = false)
    {
        Dictionary<string, List<GRBVar>> supportsByStopId = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, TrainStopRecord> stopsById = instance.TrainStops.ToDictionary(
            stop => stop.StopId, StringComparer.OrdinalIgnoreCase);

        Dictionary<(int First, int Second), GRBLinExpr> pairReversals = new();
        var modeledProjections = OvertakingStopProjectionPlanner.Plan(instance, candidates);
        foreach (OvertakingStopProjection projection in modeledProjections)
        {
            if (!sectionPrecedenceVars.TryGetValue(projection.IncomingPrecedenceKey, out GRBVar? incomingVar) ||
                !sectionPrecedenceVars.TryGetValue(projection.OutgoingPrecedenceKey, out GRBVar? outgoingVar) ||
                !stopsById.TryGetValue(projection.FirstStopId, out TrainStopRecord? firstStop) ||
                !stopsById.TryGetValue(projection.SecondStopId, out TrainStopRecord? secondStop))
                continue;

            GRBVar firstReversal = model.AddVar(0, 1, 0, GRB.CONTINUOUS,
                $"pair_overtake_first_t{projection.FirstTrainIndex}_t{projection.SecondTrainIndex}_s{firstStop.StationOrder}");
            GRBVar secondReversal = model.AddVar(0, 1, 0, GRB.CONTINUOUS,
                $"pair_overtake_second_t{projection.FirstTrainIndex}_t{projection.SecondTrainIndex}_s{firstStop.StationOrder}");
            reversalBindings.Add(new(firstStop.StopId, incomingVar, outgoingVar, true, firstReversal));
            reversalBindings.Add(new(secondStop.StopId, incomingVar, outgoingVar, false, secondReversal));
            // Each continuous reversal indicator is exactly zero or one for binary orders.
            string constraintSuffix =
                $"t{projection.FirstTrainIndex}_t{projection.SecondTrainIndex}_s{firstStop.StationOrder}";
            model.AddConstr(firstReversal + incomingVar, GRB.LESS_EQUAL, 1,
                $"pair_overtake_first_in_ub_{constraintSuffix}");
            model.AddConstr(firstReversal - outgoingVar, GRB.LESS_EQUAL, 0,
                $"pair_overtake_first_out_ub_{constraintSuffix}");
            model.AddConstr(firstReversal - outgoingVar + incomingVar, GRB.GREATER_EQUAL, 0,
                $"pair_overtake_first_lb_{constraintSuffix}");
            model.AddConstr(secondReversal - incomingVar, GRB.LESS_EQUAL, 0,
                $"pair_overtake_second_in_ub_{constraintSuffix}");
            model.AddConstr(secondReversal + outgoingVar, GRB.LESS_EQUAL, 1,
                $"pair_overtake_second_out_ub_{constraintSuffix}");
            model.AddConstr(secondReversal - incomingVar + outgoingVar, GRB.GREATER_EQUAL, 0,
                $"pair_overtake_second_lb_{constraintSuffix}");
            var pairKey = (Math.Min(firstStop.TrainIndex, secondStop.TrainIndex), Math.Max(firstStop.TrainIndex, secondStop.TrainIndex));
            if (!pairReversals.TryGetValue(pairKey, out GRBLinExpr? pairTotal))
                pairReversals[pairKey] = pairTotal = new GRBLinExpr();
            pairTotal.AddTerm(1, firstReversal);
            pairTotal.AddTerm(1, secondReversal);
            AddSupport(model, firstStop, firstReversal, stopVars, beingOvertakenVars, supportsByStopId);
            AddSupport(model, secondStop, secondReversal, stopVars, beingOvertakenVars, supportsByStopId);
        }

        // Sum both directional flip indicators across all shared visits for each train pair.
        foreach (var pair in pairReversals)
            model.AddConstr(pair.Value, GRB.LESS_EQUAL, 1,
                $"max_pair_overtakes_t{pair.Key.First}_t{pair.Key.Second}");

        if (preserveOmittedSupport)
        {
            // Deleting exclusion edges must not delete the possibility that an omitted train
            // supplies a legitimate waiting witness. This is a relaxation only; full verification
            // still requires a real reversal. When all projections are present no unknown is added.
            var known = modeledProjections.Select(p => p.ProjectionId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var full = OvertakingStopProjectionPlanner.Plan(instance,
                new InitialConflictGraphBuilder(instance, int.MaxValue).Build());
            foreach (string stopId in full.Where(p => !known.Contains(p.ProjectionId))
                         .SelectMany(p => new[] { p.FirstStopId, p.SecondStopId }).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var unknown = model.AddVar(0, 1, 0, GRB.CONTINUOUS, $"unknown_overtaking_support_{stopId}");
                AddSupport(model, stopsById[stopId], unknown, stopVars, beingOvertakenVars, supportsByStopId);
            }
        }

        foreach ((string stopId, GRBVar beingOvertaken) in beingOvertakenVars)
        {
            GRBLinExpr support = new();
            foreach (GRBVar reversal in supportsByStopId[stopId]) support.AddTerm(1, reversal);
            model.AddConstr(beingOvertaken - support, GRB.LESS_EQUAL, 0, $"being_overtaken_supported_{stopId}");
            model.AddConstr(support, GRB.LESS_EQUAL, 1, $"max_overtakers_{stopId}");
        }

        foreach (TrainStopRecord stop in instance.TrainStops.Where(item => item.StopRole == TrainStopRole.Intermediate))
        {
            NormalDwellResolution dwell = NormalDwellTimeResolver.Resolve(instance.NormalDwellTimeRules, stop);
            GRBLinExpr upper = departureVars[stop.StopId] - arrivalVars[stop.StopId] -
                dwell.NormalMaxDwellSeconds * stopVars[stop.StopId];
            if (beingOvertakenVars.TryGetValue(stop.StopId, out GRBVar? beingOvertaken))
            {
                upper.AddTerm(-(dwell.WaitingMaxDwellSeconds - dwell.NormalMaxDwellSeconds), beingOvertaken);
            }
            model.AddConstr(upper, GRB.LESS_EQUAL, 0, $"max_dwell_t{stop.TrainIndex}_s{stop.StationOrder}");
        }
    }

    private static void AddSupport(
        GRBModel model,
        TrainStopRecord stop,
        GRBVar reversal,
        IReadOnlyDictionary<string, GRBVar> stopVars,
        IDictionary<string, GRBVar> beingOvertakenVars,
        IDictionary<string, List<GRBVar>> supportsByStopId)
    {
        if (!beingOvertakenVars.TryGetValue(stop.StopId, out GRBVar? being))
        {
            being = model.AddVar(0, 1, 0, GRB.CONTINUOUS, $"being_overtaken_t{stop.TrainIndex}_s{stop.StationOrder}");
            beingOvertakenVars.Add(stop.StopId, being);
            supportsByStopId.Add(stop.StopId, []);
            model.AddConstr(being - stopVars[stop.StopId], GRB.LESS_EQUAL, 0, $"being_overtaken_requires_stop_{stop.StopId}");
        }
        supportsByStopId[stop.StopId].Add(reversal);
        model.AddConstr(being - reversal, GRB.GREATER_EQUAL, 0, $"being_overtaken_covers_reversal_{stop.StopId}_{supportsByStopId[stop.StopId].Count}");
    }

}

public static class OvertakingStateEvaluator
{
    public static IReadOnlySet<string> ResolveActuallyOvertakenStopIds(BasicMilpModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        Dictionary<string, SectionConflictCandidate> candidates = model.SectionConflictCandidates.ToDictionary(
            BasicMilpModelBuilder.BuildSectionPrecedenceKey,
            StringComparer.OrdinalIgnoreCase);
        Dictionary<(int TrainIndex, string Station), TrainStopRecord> stops = model.Instance.TrainStops.ToDictionary(
            stop => (stop.TrainIndex, stop.StationName.ToUpperInvariant()));
        HashSet<string> result = new(StringComparer.OrdinalIgnoreCase);

        foreach (TrainRecord routeTemplate in model.Instance.Trains)
        {
            for (int index = 1; index < routeTemplate.OrderedStops.Count - 1; index++)
            {
                TrainStopRecord stationStop = routeTemplate.OrderedStops[index];
                TrainStopRecord nextStop = routeTemplate.OrderedStops[index + 1];
                string? incomingSection = stationStop.IncomingSectionName;
                string? outgoingSection = nextStop.IncomingSectionName;
                if (string.IsNullOrWhiteSpace(incomingSection) || string.IsNullOrWhiteSpace(outgoingSection))
                {
                    continue;
                }

                foreach (SectionConflictCandidate incoming in model.SectionConflictCandidates.Where(candidate =>
                    string.Equals(candidate.SectionName, incomingSection, StringComparison.OrdinalIgnoreCase)))
                {
                    string incomingKey = BasicMilpModelBuilder.BuildSectionPrecedenceKey(incoming);
                    string outgoingKey = BasicMilpModelBuilder.BuildSectionPrecedenceKey(
                        outgoingSection, incoming.FirstTrainIndex, incoming.SecondTrainIndex);
                    if (!candidates.ContainsKey(outgoingKey) ||
                        !model.SectionPrecedenceVars.TryGetValue(incomingKey, out GRBVar? incomingVar) ||
                        !model.SectionPrecedenceVars.TryGetValue(outgoingKey, out GRBVar? outgoingVar))
                    {
                        continue;
                    }

                    PairOvertakeState state = PairOvertakeResolver.Resolve(
                        incomingVar.Get(GRB.DoubleAttr.X) >= 0.5 ? 1 : 0,
                        outgoingVar.Get(GRB.DoubleAttr.X) >= 0.5 ? 1 : 0);
                    string stationKey = stationStop.StationName.ToUpperInvariant();
                    if (state.FirstBeingOvertaken && stops.TryGetValue((incoming.FirstTrainIndex, stationKey), out TrainStopRecord? first))
                    {
                        result.Add(first.StopId);
                    }
                    if (state.SecondBeingOvertaken && stops.TryGetValue((incoming.SecondTrainIndex, stationKey), out TrainStopRecord? second))
                    {
                        result.Add(second.StopId);
                    }
                }
            }
        }

        return result;
    }
}
