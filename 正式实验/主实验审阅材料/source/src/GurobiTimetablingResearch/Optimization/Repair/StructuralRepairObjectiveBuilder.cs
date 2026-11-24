// 结构修复目标构建器以偏离接受快照的时间、停站和先后变化为惩罚，建立保持原解稳定的分层补救目标。
// 同文件编排车站核试验与时间补救：先规划可释放事件和 MIP Start，再按阶段求解、评估并返回可事务提交的模型租约。
using Gurobi;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

public sealed record TemporalRecourseEventInput(
    string EventKey,
    double InputLowerBound,
    double InputUpperBound,
    double SnapshotValue,
    bool Released);

public sealed record TemporalRecourseEventPlan(
    string EventKey,
    TemporalEventBounds Bounds);

public sealed record TemporalRecoursePlan(
    IReadOnlyList<TemporalRecourseEventPlan> Events,
    int FixedEventCount,
    int BoundedEventCount,
    bool HasEmptyIntersection);

public static class TemporalRecoursePlanBuilder
{
    public static TemporalRecoursePlan Build(
        IReadOnlyList<TemporalRecourseEventInput> events,
        int windowSeconds)
    {
        List<TemporalRecourseEventPlan> plans = events
            .Select(item => new TemporalRecourseEventPlan(
                item.EventKey,
                TemporalEventBoundsCalculator.Resolve(
                    item.InputLowerBound,
                    item.InputUpperBound,
                    item.SnapshotValue,
                    windowSeconds,
                    item.Released)))
            .ToList();

        return new TemporalRecoursePlan(
            plans,
            plans.Count(item => item.Bounds.IsFixed),
            plans.Count(item => !item.Bounds.IsFixed && !item.Bounds.HasEmptyIntersection),
            plans.Any(item => item.Bounds.HasEmptyIntersection));
    }
}

public static class StructuralRepairObjectiveSpec
{
    public static IReadOnlyList<int> LexicographicPriorities { get; } = Array.AsReadOnly([3, 2, 1, 0]);
}

public sealed record StationCorePrecedenceCandidateInput(
    int FirstTrainIndex,
    int SecondTrainIndex,
    bool HasCurrentVariable);

public static class StationCoreCurrentPrecedencePairResolver
{
    public static IReadOnlyList<(int FirstTrainIndex, int SecondTrainIndex)> Resolve(
        IReadOnlyList<StationCorePrecedenceCandidateInput> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return candidates
            .Where(candidate => candidate.HasCurrentVariable)
            .Select(candidate => (candidate.FirstTrainIndex, candidate.SecondTrainIndex))
            .Where(pair => pair.FirstTrainIndex != pair.SecondTrainIndex)
            .Distinct()
            .OrderBy(pair => pair.FirstTrainIndex)
            .ThenBy(pair => pair.SecondTrainIndex)
            .ToList();
    }
}

public sealed record StationCoreLiveTrialPlan(
    IReadOnlyList<int> ReleasedTrainIndices,
    IReadOnlyList<LocalPrecedenceNogoodCut> RetainedCuts,
    IReadOnlyList<LocalPrecedenceNogoodCut> TemporaryTrialCuts);

public static class StationCoreLiveTrialPlanner
{
    public static StationCoreLiveTrialPlan Build(
        IReadOnlyCollection<int> coreTrainIndices,
        IReadOnlyCollection<(int FirstTrainIndex, int SecondTrainIndex)> currentPrecedencePairs,
        IReadOnlyList<LocalPrecedenceNogoodCut> retainedCuts,
        LocalPrecedenceNogoodCut temporaryCandidate)
    {
        ArgumentNullException.ThrowIfNull(coreTrainIndices);
        ArgumentNullException.ThrowIfNull(currentPrecedencePairs);
        ArgumentNullException.ThrowIfNull(retainedCuts);
        ArgumentNullException.ThrowIfNull(temporaryCandidate);
        HashSet<int> core = coreTrainIndices.ToHashSet();
        if (core.Count == 0)
        {
            throw new ArgumentException("A station-core recourse neighborhood requires at least one core train.", nameof(coreTrainIndices));
        }

        if (retainedCuts.Any(cut => string.Equals(cut.CutId, temporaryCandidate.CutId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(
                $"Temporary station-core Cut '{temporaryCandidate.CutId}' is already retained.",
                nameof(temporaryCandidate));
        }

        HashSet<int> released = new(core);
        foreach ((int firstTrainIndex, int secondTrainIndex) in currentPrecedencePairs)
        {
            if (core.Contains(firstTrainIndex) || core.Contains(secondTrainIndex))
            {
                released.Add(firstTrainIndex);
                released.Add(secondTrainIndex);
            }
        }

        IReadOnlyList<LocalPrecedenceNogoodCut> retainedSnapshot = Array.AsReadOnly(retainedCuts.ToArray());
        return new StationCoreLiveTrialPlan(
            released.Order().ToList(),
            retainedSnapshot,
            Array.AsReadOnly(retainedSnapshot.Append(temporaryCandidate).ToArray()));
    }
}

public sealed record CheckerStationCoreMipStartPlan(
    IReadOnlyDictionary<string, double> ArrivalSecondsByStopId,
    IReadOnlyDictionary<string, double> DepartureSecondsByStopId,
    IReadOnlyDictionary<string, int> StopByStopId,
    IReadOnlyDictionary<string, int> SectionPrecedenceByKey,
    IReadOnlyDictionary<string, int> StationEventPrecedenceByKey);

public static class CheckerStationCoreMipStartPlanBuilder
{
    public static CheckerStationCoreMipStartPlan Build(MasterSolutionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new CheckerStationCoreMipStartPlan(
            snapshot.ArrivalSecondsByStopId,
            snapshot.DepartureSecondsByStopId,
            snapshot.StopByStopId,
            snapshot.SectionPrecedenceByKey,
            snapshot.StationEventPrecedenceByKey);
    }
}

public sealed class StationCoreTrialModelLease<T> : IDisposable where T : IDisposable
{
    private bool _transferred;

    public StationCoreTrialModelLease(T model)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
    }

    public T Model { get; }

    public T Transfer()
    {
        if (_transferred)
        {
            throw new InvalidOperationException("Station-core trial model ownership was already transferred.");
        }

        _transferred = true;
        return Model;
    }

    public void Dispose()
    {
        if (!_transferred)
        {
            Model.Dispose();
        }
    }
}

public enum StationCoreTrialAttemptOutcome
{
    BoundedNoIncumbent,
    BoundedRejected,
    CarrierNoIncumbent,
    CarrierRejected,
    CarrierAccepted
}

public sealed record StationCoreRecoursePreparation(
    bool CanOptimize,
    string SolverStatus,
    int FixedEventCount,
    int BoundedEventCount);

public sealed record StationCoreTrialStageEvaluation<TState>(
    bool HasIncumbent,
    bool PassesAcceptanceRule,
    TState? State,
    string SolverStatus,
    string Decision)
    where TState : class;

public sealed record StationCoreTrialAttempt<TState>(
    int WindowSeconds,
    StationCoreTrialAttemptOutcome Outcome,
    StationCoreRecoursePreparation RecoursePreparation,
    StationCoreTrialStageEvaluation<TState>? BoundedEvaluation,
    StationCoreTrialStageEvaluation<TState>? CarrierEvaluation)
    where TState : class;

public sealed record StationCoreTrialOrchestrationResult<TModel, TSnapshot, TState>(
    bool Accepted,
    TModel? Model,
    TState? AcceptedState,
    int AcceptedWindowSeconds,
    bool RecourseExhausted,
    StationCoreLiveTrialPlan TrialPlan,
    IReadOnlyList<StationCoreTrialAttempt<TState>> Attempts)
    where TModel : class, IDisposable
    where TSnapshot : class
    where TState : class;

public static class StationCoreTrialOrchestrator
{
    public static StationCoreTrialOrchestrationResult<TModel, TSnapshot, TState> Execute<TModel, TSnapshot, TState>(
        IReadOnlyCollection<int> coreTrainIndices,
        IReadOnlyCollection<(int FirstTrainIndex, int SecondTrainIndex)> currentPrecedencePairs,
        IReadOnlyList<LocalPrecedenceNogoodCut> retainedCuts,
        LocalPrecedenceNogoodCut temporaryCandidate,
        IReadOnlyList<int> recourseWindowSeconds,
        TSnapshot currentSnapshot,
        Func<IReadOnlyList<LocalPrecedenceNogoodCut>, TModel> buildModel,
        Action<TModel, TSnapshot> applyMipStart,
        Func<TModel, int, StationCoreLiveTrialPlan, StationCoreRecoursePreparation> prepareRecourse,
        Func<TModel, int, StationCoreTrialStageEvaluation<TState>> evaluateBounded,
        Action<TModel, TSnapshot> prepareProjectionCarrier,
        Func<TModel, int, StationCoreTrialStageEvaluation<TState>> evaluateProjectionCarrier,
        Func<TState, TSnapshot> resolveMipStartSnapshot)
        where TModel : class, IDisposable
        where TSnapshot : class
        where TState : class
    {
        ArgumentNullException.ThrowIfNull(currentSnapshot);
        ArgumentNullException.ThrowIfNull(buildModel);
        ArgumentNullException.ThrowIfNull(applyMipStart);
        ArgumentNullException.ThrowIfNull(prepareRecourse);
        ArgumentNullException.ThrowIfNull(evaluateBounded);
        ArgumentNullException.ThrowIfNull(prepareProjectionCarrier);
        ArgumentNullException.ThrowIfNull(evaluateProjectionCarrier);
        ArgumentNullException.ThrowIfNull(resolveMipStartSnapshot);

        StationCoreLiveTrialPlan trialPlan = StationCoreLiveTrialPlanner.Build(
            coreTrainIndices,
            currentPrecedencePairs,
            retainedCuts,
            temporaryCandidate);
        StationCoreTrialExecutionPlan executionPlan =
            StationCoreTrialExecutionPlanBuilder.Build(recourseWindowSeconds);
        if (!executionPlan.BuildFreshProjectionCarrier || executionPlan.ProjectionCarrierAppliesTemporalBounds)
        {
            throw new InvalidOperationException("Station-core orchestration requires a fresh structurally mutable carrier.");
        }

        List<StationCoreTrialAttempt<TState>> attempts = new();
        foreach (int windowSeconds in executionPlan.RecourseWindowSeconds)
        {
            using StationCoreTrialModelLease<TModel> boundedLease = new(buildModel(trialPlan.TemporaryTrialCuts));
            TModel boundedModel = boundedLease.Model;
            applyMipStart(boundedModel, currentSnapshot);
            StationCoreRecoursePreparation recoursePreparation = prepareRecourse(
                boundedModel,
                windowSeconds,
                trialPlan);
            if (!recoursePreparation.CanOptimize)
            {
                _ = CheckerStationCoreCommitResolver.Resolve(false, false);
                attempts.Add(new StationCoreTrialAttempt<TState>(
                    windowSeconds,
                    StationCoreTrialAttemptOutcome.BoundedNoIncumbent,
                    recoursePreparation,
                    null,
                    null));
                continue;
            }

            StationCoreTrialStageEvaluation<TState> boundedEvaluation = evaluateBounded(boundedModel, windowSeconds);
            CheckerStationCoreCommitAction boundedAction = CheckerStationCoreCommitResolver.Resolve(
                boundedEvaluation.HasIncumbent,
                boundedEvaluation.PassesAcceptanceRule);
            if (!boundedEvaluation.HasIncumbent)
            {
                attempts.Add(new StationCoreTrialAttempt<TState>(
                    windowSeconds,
                    StationCoreTrialAttemptOutcome.BoundedNoIncumbent,
                    recoursePreparation,
                    boundedEvaluation,
                    null));
                continue;
            }

            if (boundedAction == CheckerStationCoreCommitAction.PreservePriorState)
            {
                attempts.Add(new StationCoreTrialAttempt<TState>(
                    windowSeconds,
                    StationCoreTrialAttemptOutcome.BoundedRejected,
                    recoursePreparation,
                    boundedEvaluation,
                    null));
                continue;
            }

            TState boundedState = boundedEvaluation.State ?? throw new InvalidOperationException(
                "An accepted bounded station-core probe must provide evaluated state.");
            using StationCoreTrialModelLease<TModel> carrierLease = new(buildModel(trialPlan.TemporaryTrialCuts));
            TModel carrierModel = carrierLease.Model;
            TSnapshot carrierMipStart = resolveMipStartSnapshot(boundedState);
            applyMipStart(carrierModel, carrierMipStart);
            prepareProjectionCarrier(carrierModel, carrierMipStart);
            StationCoreTrialStageEvaluation<TState> carrierEvaluation =
                evaluateProjectionCarrier(carrierModel, windowSeconds);
            CheckerStationCoreCommitAction carrierAction = CheckerStationCoreCommitResolver.Resolve(
                carrierEvaluation.HasIncumbent,
                carrierEvaluation.PassesAcceptanceRule);
            if (!carrierEvaluation.HasIncumbent)
            {
                attempts.Add(new StationCoreTrialAttempt<TState>(
                    windowSeconds,
                    StationCoreTrialAttemptOutcome.CarrierNoIncumbent,
                    recoursePreparation,
                    boundedEvaluation,
                    carrierEvaluation));
                continue;
            }

            if (carrierAction == CheckerStationCoreCommitAction.PreservePriorState)
            {
                attempts.Add(new StationCoreTrialAttempt<TState>(
                    windowSeconds,
                    StationCoreTrialAttemptOutcome.CarrierRejected,
                    recoursePreparation,
                    boundedEvaluation,
                    carrierEvaluation));
                continue;
            }

            TState acceptedState = carrierEvaluation.State ?? throw new InvalidOperationException(
                "An accepted station-core carrier must provide evaluated state.");
            attempts.Add(new StationCoreTrialAttempt<TState>(
                windowSeconds,
                StationCoreTrialAttemptOutcome.CarrierAccepted,
                recoursePreparation,
                boundedEvaluation,
                carrierEvaluation));
            TModel acceptedModel = carrierLease.Transfer();
            return new StationCoreTrialOrchestrationResult<TModel, TSnapshot, TState>(
                true,
                acceptedModel,
                acceptedState,
                windowSeconds,
                false,
                trialPlan,
                attempts);
        }

        bool recourseExhausted = attempts.Count > 0 && attempts.All(attempt =>
            attempt.Outcome is StationCoreTrialAttemptOutcome.BoundedNoIncumbent or
                StationCoreTrialAttemptOutcome.CarrierNoIncumbent);
        return new StationCoreTrialOrchestrationResult<TModel, TSnapshot, TState>(
            false,
            null,
            null,
            0,
            recourseExhausted,
            trialPlan,
            attempts);
    }
}

internal sealed record TemporalRecourseApplicationResult(
    int WindowSeconds,
    int FixedEventCount,
    int BoundedEventCount,
    bool HasEmptyIntersection,
    string EmptyIntersectionEventKey);

internal static class TemporalRecourseNeighborhoodApplier
{
    internal static TemporalRecourseApplicationResult Apply(
        BasicMilpModel model,
        MasterSolutionSnapshot snapshot,
        IReadOnlySet<int> releasedTrainIndices,
        int windowSeconds)
    {
        Dictionary<int, RepairGeographicSpan> spans = releasedTrainIndices.ToDictionary(
            trainIndex => trainIndex,
            _ => new RepairGeographicSpan(int.MinValue, int.MaxValue));
        return Apply(
            model,
            snapshot,
            new RepairDecisionNeighborhood(RepairDecisionLevel.TimeOnly, releasedTrainIndices, spans),
            windowSeconds);
    }

    internal static TemporalRecourseApplicationResult Apply(
        BasicMilpModel model,
        MasterSolutionSnapshot snapshot,
        RepairDecisionNeighborhood neighborhood,
        int windowSeconds)
    {
        Dictionary<string, GRBVar> variables = new(StringComparer.OrdinalIgnoreCase);
        List<TemporalRecourseEventInput> inputs = new();
        IisDependencyOverlay overlay = RepairDecisionDependencyOverlayResolver.Resolve(neighborhood);
        foreach (TrainStopRecord stop in model.Instance.TrainStops)
        {
            bool arrivalReleased = StructuralRepairNeighborhoodApplier.IsTimeEventReleased(
                neighborhood, $"arrival::{stop.StopId}", stop.TrainIndex, stop.StationOrder);
            bool departureReleased = StructuralRepairNeighborhoodApplier.IsTimeEventReleased(
                neighborhood, $"departure::{stop.StopId}", stop.TrainIndex, stop.StationOrder);
            AddEvent(
                $"arrival::{stop.StopId}",
                model.ArrivalVars[stop.StopId],
                stop.EarliestArrSeconds,
                stop.LatestArrSeconds,
                ResolveSnapshot(snapshot.ArrivalSecondsByStopId, stop.StopId, "arrival"),
                arrivalReleased,
                variables,
                inputs);
            AddEvent(
                $"departure::{stop.StopId}",
                model.DepartureVars[stop.StopId],
                stop.EarliestDepSeconds,
                stop.LatestDepSeconds,
                ResolveSnapshot(snapshot.DepartureSecondsByStopId, stop.StopId, "departure"),
                departureReleased,
                variables,
                inputs);
        }

        List<TemporalRecourseEventPlan> eventPlans = inputs.Select(input =>
        {
            int extra = overlay.ExtraTemporalSecondsByEventKey.GetValueOrDefault(input.EventKey, 0);
            return new TemporalRecourseEventPlan(
                input.EventKey,
                TemporalEventBoundsCalculator.Resolve(
                    input.InputLowerBound,
                    input.InputUpperBound,
                    input.SnapshotValue,
                    checked(windowSeconds + extra),
                    input.Released));
        }).ToList();
        TemporalRecoursePlan plan = new(
            eventPlans,
            eventPlans.Count(item => item.Bounds.IsFixed),
            eventPlans.Count(item => !item.Bounds.IsFixed && !item.Bounds.HasEmptyIntersection),
            eventPlans.Any(item => item.Bounds.HasEmptyIntersection));
        TemporalRecourseEventPlan? invalid = plan.Events.FirstOrDefault(item => item.Bounds.HasEmptyIntersection);
        if (invalid is not null)
        {
            return new TemporalRecourseApplicationResult(
                windowSeconds,
                plan.FixedEventCount,
                plan.BoundedEventCount,
                true,
                invalid.EventKey);
        }

        foreach (TemporalRecourseEventPlan item in plan.Events)
        {
            GRBVar variable = variables[item.EventKey];
            variable.Set(GRB.DoubleAttr.LB, item.Bounds.LowerBound);
            variable.Set(GRB.DoubleAttr.UB, item.Bounds.UpperBound);
        }

        return new TemporalRecourseApplicationResult(
            windowSeconds,
            plan.FixedEventCount,
            plan.BoundedEventCount,
            false,
            string.Empty);
    }

    private static void AddEvent(
        string eventKey,
        GRBVar variable,
        double inputLower,
        double inputUpper,
        double snapshotValue,
        bool released,
        IDictionary<string, GRBVar> variables,
        ICollection<TemporalRecourseEventInput> inputs)
    {
        variables[eventKey] = variable;
        inputs.Add(new TemporalRecourseEventInput(
            eventKey,
            inputLower,
            inputUpper,
            snapshotValue,
            released));
    }

    private static double ResolveSnapshot(
        IReadOnlyDictionary<string, double> values,
        string stopId,
        string eventKind)
    {
        if (!values.TryGetValue(stopId, out double value))
        {
            throw new InvalidDataException($"The macro snapshot is missing {eventKind} time for stop '{stopId}'.");
        }

        return value;
    }
}

internal static class StructuralRepairObjectiveBuilder
{
    /// <summary>
    /// 用所有到发事件相对修复参考的绝对偏移总和构造单目标，只决定优先选择哪一个可行候选，
    /// 不收紧原始时间界、停站域、运行时分或 supplement 合法范围。
    /// </summary>
    internal static void ApplyFeasibilityRecovery(BasicMilpModel model, MasterSolutionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(snapshot);
        GRBLinExpr totalDeviation = new();
        int eventIndex = 0;
        foreach (TrainStopRecord stop in model.Instance.TrainStops)
        {
            if (stop.StopRole != TrainStopRole.Origin)
            {
                AddTotalDeviation(
                    model.Model,
                    model.ArrivalVars[stop.StopId],
                    snapshot.ArrivalSecondsByStopId[stop.StopId],
                    totalDeviation,
                    eventIndex++);
            }

            if (stop.StopRole != TrainStopRole.Destination)
            {
                AddTotalDeviation(
                    model.Model,
                    model.DepartureVars[stop.StopId],
                    snapshot.DepartureSecondsByStopId[stop.StopId],
                    totalDeviation,
                    eventIndex++);
            }
        }

        model.Model.Update();
        model.Model.Set(GRB.IntAttr.NumObj, 1);
        model.Model.SetObjective(totalDeviation, GRB.MINIMIZE);
    }

    internal static void Apply(BasicMilpModel model, MasterSolutionSnapshot snapshot)
    {
        GRBLinExpr totalDeviation = new();
        GRBVar maximumDeviation = model.Model.AddVar(
            0.0,
            GRB.INFINITY,
            0.0,
            GRB.CONTINUOUS,
            "repair_maximum_event_deviation");
        int eventIndex = 0;

        foreach (TrainStopRecord stop in model.Instance.TrainStops)
        {
            if (stop.StopRole != TrainStopRole.Origin)
            {
                AddDeviation(
                    model.Model,
                    model.ArrivalVars[stop.StopId],
                    snapshot.ArrivalSecondsByStopId[stop.StopId],
                    maximumDeviation,
                    totalDeviation,
                    eventIndex++);
            }

            if (stop.StopRole != TrainStopRole.Destination)
            {
                AddDeviation(
                    model.Model,
                    model.DepartureVars[stop.StopId],
                    snapshot.DepartureSecondsByStopId[stop.StopId],
                    maximumDeviation,
                    totalDeviation,
                    eventIndex++);
            }
        }

        model.Model.Update();
        GRBVar firstDeparture = model.Model.GetVarByName("global_first_departure");
        GRBVar lastArrival = model.Model.GetVarByName("global_last_arrival");
        GRBLinExpr span = lastArrival - firstDeparture;
        GRBLinExpr runningTimeSupplement = new();
        foreach (GRBVar variable in model.RunningTimeSupplementVars.Values)
        {
            runningTimeSupplement.AddTerm(1.0, variable);
        }

        model.Model.SetObjective(new GRBLinExpr(), GRB.MINIMIZE);
        model.Model.SetObjectiveN(totalDeviation, 0, 3, 1.0, 0.0, 0.0, "repair_total_deviation");
        model.Model.SetObjectiveN(maximumDeviation, 1, 2, 1.0, 0.0, 0.0, "repair_maximum_deviation");
        model.Model.SetObjectiveN(span, 2, 1, 1.0, 0.0, 0.0, "repair_global_span");
        model.Model.SetObjectiveN(runningTimeSupplement, 3, 0, 1.0, 0.0, 0.0, "repair_running_time_supplement");
    }

    private static void AddDeviation(
        GRBModel model,
        GRBVar eventTime,
        double snapshotValue,
        GRBVar maximumDeviation,
        GRBLinExpr totalDeviation,
        int eventIndex)
    {
        GRBVar deviation = model.AddVar(
            0.0,
            GRB.INFINITY,
            0.0,
            GRB.CONTINUOUS,
            $"repair_event_deviation_{eventIndex}");
        model.AddConstr(deviation - eventTime, GRB.GREATER_EQUAL, -snapshotValue,
            $"repair_event_deviation_pos_{eventIndex}");
        model.AddConstr(deviation + eventTime, GRB.GREATER_EQUAL, snapshotValue,
            $"repair_event_deviation_neg_{eventIndex}");
        model.AddConstr(maximumDeviation - deviation, GRB.GREATER_EQUAL, 0.0,
            $"repair_maximum_deviation_{eventIndex}");
        totalDeviation.AddTerm(1.0, deviation);
    }

    private static void AddTotalDeviation(
        GRBModel model,
        GRBVar eventTime,
        double snapshotValue,
        GRBLinExpr totalDeviation,
        int eventIndex)
    {
        GRBVar deviation = model.AddVar(
            0.0,
            GRB.INFINITY,
            0.0,
            GRB.CONTINUOUS,
            $"recovery_event_deviation_{eventIndex}");
        model.AddConstr(
            deviation - eventTime,
            GRB.GREATER_EQUAL,
            -snapshotValue,
            $"recovery_event_deviation_pos_{eventIndex}");
        model.AddConstr(
            deviation + eventTime,
            GRB.GREATER_EQUAL,
            snapshotValue,
            $"recovery_event_deviation_neg_{eventIndex}");
        totalDeviation.AddTerm(1.0, deviation);
    }
}

internal sealed record StructuralRepairRetimingMetrics(
    double TotalAbsoluteDeviationSeconds,
    double MaximumAbsoluteDeviationSeconds)
{
    internal static StructuralRepairRetimingMetrics Calculate(
        BasicMilpModel model,
        MasterSolutionSnapshot snapshot)
    {
        double total = 0.0;
        double maximum = 0.0;
        foreach (TrainStopRecord stop in model.Instance.TrainStops)
        {
            if (stop.StopRole != TrainStopRole.Origin)
            {
                Add(Math.Abs(
                    model.ArrivalVars[stop.StopId].Get(GRB.DoubleAttr.X) -
                    snapshot.ArrivalSecondsByStopId[stop.StopId]));
            }

            if (stop.StopRole != TrainStopRole.Destination)
            {
                Add(Math.Abs(
                    model.DepartureVars[stop.StopId].Get(GRB.DoubleAttr.X) -
                    snapshot.DepartureSecondsByStopId[stop.StopId]));
            }
        }

        return new StructuralRepairRetimingMetrics(total, maximum);

        void Add(double value)
        {
            total += value;
            maximum = Math.Max(maximum, value);
        }
    }
}
