// 主解快照用按业务键索引的数值字典保存时间、停站、径路和先后变量，从底层 Gurobi 模型剥离可持久化解状态。
// 应用器把快照作为 MIP Start 或固定值写回新模型，用于滚动窗口衔接、候选试探、回滚和续跑。
using Gurobi;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

public sealed record MasterSolutionSnapshot(
    double ObjectiveValue,
    IReadOnlyDictionary<string, double> ArrivalSecondsByStopId,
    IReadOnlyDictionary<string, double> DepartureSecondsByStopId,
    IReadOnlyDictionary<string, int> StopByStopId,
    IReadOnlyDictionary<string, int> SectionPrecedenceByKey,
    IReadOnlyDictionary<string, int> StationEventPrecedenceByKey,
    IReadOnlySet<int> AdmittedTrainIndices,
    IReadOnlyDictionary<string, double>? RunningTimeSupplementSecondsByKey = null,
    BusinessObjectiveVector? BusinessObjective = null,
    TimetableSolutionRole SolutionRole = TimetableSolutionRole.WorkingSolution,
    OptimizationBoundReport? BoundReport = null,
    IReadOnlyList<VerifiedMicroAssignmentWitness>? VerifiedMicroAssignments = null,
    ServiceObjectiveVector? ServiceObjective = null)
{
    public static MasterSolutionSnapshot Capture(
        BasicMilpModel model,
        TimetableSolutionRole solutionRole = TimetableSolutionRole.WorkingSolution,
        OptimizationBoundReport? boundReport = null)
    {
        Dictionary<string, double> arrivals = model.ArrivalVars.ToDictionary(
            item => item.Key,
            item => item.Value.Get(GRB.DoubleAttr.X),
            StringComparer.OrdinalIgnoreCase);
        Dictionary<string, double> departures = model.DepartureVars.ToDictionary(
            item => item.Key,
            item => item.Value.Get(GRB.DoubleAttr.X),
            StringComparer.OrdinalIgnoreCase);
        Dictionary<string, double> supplements = model.RunningTimeSupplementVars.ToDictionary(
            item => item.Key,
            item => SolverNumericalValueNormalizer.NormalizeNonNegative(
                item.Value.Get(GRB.DoubleAttr.X)),
            StringComparer.OrdinalIgnoreCase);
        BusinessObjectiveVector businessObjective = BusinessObjectiveCalculator.Calculate(
            model.Instance.Trains.Select(train => departures[train.OrderedStops[0].StopId]),
            model.Instance.Trains.Select(train => arrivals[train.OrderedStops[^1].StopId]),
            supplements.Values);

        MasterSolutionSnapshot snapshot = new(
            model.Model.Get(GRB.DoubleAttr.ObjVal),
            arrivals,
            departures,
            model.StopVars.ToDictionary(item => item.Key, item => item.Value.Get(GRB.DoubleAttr.X) >= 0.5 ? 1 : 0, StringComparer.OrdinalIgnoreCase),
            model.SectionPrecedenceVars.ToDictionary(item => item.Key, item => item.Value.Get(GRB.DoubleAttr.X) >= 0.5 ? 1 : 0, StringComparer.OrdinalIgnoreCase),
            model.StationEventPrecedenceVars.ToDictionary(item => item.Key, item => item.Value.Get(GRB.DoubleAttr.X) >= 0.5 ? 1 : 0, StringComparer.OrdinalIgnoreCase),
            model.Instance.Trains.Select(train => train.TrainIndex).ToHashSet(),
            supplements,
            businessObjective,
            solutionRole,
            boundReport);
        return model.ServiceWindows.Windows.Count == 0 ? snapshot : snapshot with
        {
            ServiceObjective = ServiceBusinessObjectiveEvaluator.Calculate(model.Instance, snapshot, model.ServiceWindows)
        };
    }
}

/// <summary>
/// 使用稳定业务标识保存已经由站级 Micro 求解器验证的进路、股道和停站模式选择。
/// </summary>
public sealed record VerifiedMicroAssignmentWitness(
    string StationName,
    string RequirementId,
    int TrainIndex,
    bool IsStop,
    string TrackName,
    int? IncomingRouteId,
    int? OutgoingRouteId,
    string CandidateSnapshotFingerprint = "",
    double? ArrivalSeconds = null,
    double? DepartureSeconds = null,
    string VerificationScope = "station_micro_validated");

public sealed record VerifiedMicroAssignmentMipStartResult(
    int WitnessCount,
    int MatchedRequirementCount,
    int StartedChoiceVariableCount,
    int UnmatchedWitnessCount,
    int StartedOrderVariableCount = 0,
    int UnstartedOrderVariableCount = 0,
    int StartedStopProductVariableCount = 0,
    int StartedReversalVariableCount = 0,
    int StartedBeingOvertakenVariableCount = 0,
    int SkippedArrivalDepartureVariableCount = 0,
    int SkippedStopVariableCount = 0,
    int SkippedPrecedenceVariableCount = 0,
    int SkippedRunningTimeSupplementVariableCount = 0,
    int SkippedMicroWitnessCount = 0,
    int StartedDerivedSectionPrecedenceVariableCount = 0,
    MacroMipStartCoverageAudit? MacroCoverage = null)
{
    public double CoverageRatio => WitnessCount == 0
        ? 0.0
        : (double)MatchedRequirementCount / WitnessCount;
}

public sealed record MacroMipStartCoverageAudit(
    int EligibleBinaryCount,
    int StartedBinaryCount,
    int UnstartedBinaryCount,
    IReadOnlyDictionary<string, int> StartedByFamily,
    IReadOnlyDictionary<string, int> UnstartedByFamily,
    IReadOnlyList<string> FirstUnstartedVariables,
    IReadOnlyList<string> CandidateValidationViolations,
    int ExcludedBinaryCount = 0,
    IReadOnlyDictionary<string, int>? ExcludedByFamily = null,
    IReadOnlyList<string>? FirstExcludedVariables = null)
{
    public double CoverageRatio => EligibleBinaryCount == 0
        ? 1.0
        : (double)StartedBinaryCount / EligibleBinaryCount;
}

/// <summary>
/// 将可靠 Micro 见证映射到新模型的候选变量；找不到完整业务键时保持变量未定义，不使用默认零值。
/// </summary>
public static class VerifiedMicroAssignmentMipStartApplier
{
    public static VerifiedMicroAssignmentMipStartResult Apply(
        BasicMilpModel model,
        IReadOnlyList<VerifiedMicroAssignmentWitness>? witnesses)
    {
        ArgumentNullException.ThrowIfNull(model);
        IReadOnlyList<VerifiedMicroAssignmentWitness> reliableWitnesses = witnesses ?? [];
        int matchedRequirements = 0;
        int startedVariables = 0;
        HashSet<string> matchedIds = new(StringComparer.OrdinalIgnoreCase);

        foreach (VerifiedMicroAssignmentWitness witness in reliableWitnesses)
        {
            IReadOnlyList<PromotedMicroChoiceVariableBinding> bindings = model.PromotedMicroChoiceBindings
                .Where(binding =>
                    string.Equals(binding.StationName, witness.StationName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(binding.RequirementId, witness.RequirementId, StringComparison.OrdinalIgnoreCase) &&
                    binding.TrainIndex == witness.TrainIndex)
                .ToList();
            if (bindings.Count == 0)
            {
                continue;
            }

            IReadOnlyList<PromotedMicroChoiceVariableBinding> selected = bindings
                .Where(binding => Matches(binding, witness))
                .ToList();
            if (selected.Count != 1)
            {
                continue;
            }

            matchedRequirements++;
            matchedIds.Add(witness.RequirementId);
            foreach (PromotedMicroChoiceVariableBinding binding in bindings)
            {
                binding.Variable.Set(
                    GRB.DoubleAttr.Start,
                    ReferenceEquals(binding, selected[0]) ? 1.0 : 0.0);
                startedVariables++;
            }
        }

        Dictionary<string, VerifiedMicroAssignmentWitness> witnessById = reliableWitnesses
            .GroupBy(witness => witness.RequirementId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        int startedOrders = 0;
        foreach (PromotedMicroOrderVariableBinding binding in model.PromotedMicroOrderBindings)
        {
            if (!matchedIds.Contains(binding.FirstRequirementId) ||
                !matchedIds.Contains(binding.SecondRequirementId) ||
                !witnessById.TryGetValue(binding.FirstRequirementId, out VerifiedMicroAssignmentWitness? first) ||
                !witnessById.TryGetValue(binding.SecondRequirementId, out VerifiedMicroAssignmentWitness? second) ||
                !TryResolveOrderStart(model, binding, first, second, out double start))
                continue;
            binding.Variable.Set(GRB.DoubleAttr.Start, start);
            startedOrders++;
        }

        return new VerifiedMicroAssignmentMipStartResult(
            reliableWitnesses.Count,
            matchedRequirements,
            startedVariables,
            reliableWitnesses.Count - matchedRequirements,
            startedOrders,
            model.PromotedMicroOrderBindings.Count - startedOrders);
    }

    private static bool Matches(
        PromotedMicroChoiceVariableBinding binding,
        VerifiedMicroAssignmentWitness witness) =>
        binding.IsStop == witness.IsStop &&
        string.Equals(binding.TrackName, witness.TrackName, StringComparison.OrdinalIgnoreCase) &&
        binding.IncomingRouteId == witness.IncomingRouteId &&
        binding.OutgoingRouteId == witness.OutgoingRouteId;

    private static bool TryResolveOrderStart(
        BasicMilpModel model,
        PromotedMicroOrderVariableBinding binding,
        VerifiedMicroAssignmentWitness first,
        VerifiedMicroAssignmentWitness second,
        out double start)
    {
        start = 0.0;
        if (!TryRead(first, binding.FirstEventSide, out double firstTime) ||
            !TryRead(second, binding.SecondEventSide, out double secondTime))
            return false;
        firstTime += binding.FirstTimeOffsetSeconds;
        secondTime += binding.SecondTimeOffsetSeconds;
        start = firstTime <= secondTime ? 1.0 : 0.0;
        return true;
    }

    private static bool TryRead(
        VerifiedMicroAssignmentWitness witness,
        StationEventSide side,
        out double value)
    {
        double? candidate = side == StationEventSide.Incoming
            ? witness.ArrivalSeconds
            : witness.DepartureSeconds;
        value = candidate ?? 0.0;
        return candidate.HasValue;
    }
}

public sealed record VerifiedMicroAssignmentTemporaryFixResult(
    int FixedRequirementCount,
    int FixedChoiceVariableCount,
    int SkippedActiveStationWitnessCount,
    IReadOnlyList<string>? FixedRequirementIds = null,
    IReadOnlyList<string>? ReleasedRequirementIds = null);

/// <summary>
/// 记录一次修复模型中临时固定的业务范围。该记录只描述当前求解邻域，
/// 不会生成永久反馈，也不表示被释放或保留的邻域已经得到不可行性证明。
/// </summary>
public sealed record ValidityFirstTemporaryFixationAudit(
    string NeighborhoodId,
    string TransitionKind,
    string Reason,
    IReadOnlyList<string> FixedRequirementIds,
    IReadOnlyList<string> ReleasedRequirementIds,
    int EndpointOrderChangeBudget,
    double RequestedSolveSeconds);

/// <summary>
/// 首次受限修复把非热点站已经验证的候选变量临时固定；模型重建后这些界限自然释放，不会进入永久反馈。
/// </summary>
public static class VerifiedMicroAssignmentTemporaryFixer
{
    public static VerifiedMicroAssignmentTemporaryFixResult Apply(
        BasicMilpModel model,
        IReadOnlyList<VerifiedMicroAssignmentWitness>? witnesses,
        IReadOnlySet<string> releasedStationNames,
        IReadOnlySet<string>? releasedRequirementIds = null,
        ValidityFirstTemporaryFixationAudit? audit = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(releasedStationNames);
        int fixedRequirements = 0;
        int fixedVariables = 0;
        int skippedActiveStationWitnesses = 0;
        HashSet<string> fixedRequirementIds = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> actuallyReleasedRequirementIds = new(StringComparer.OrdinalIgnoreCase);
        foreach (VerifiedMicroAssignmentWitness witness in witnesses ?? [])
        {
            IReadOnlyList<PromotedMicroChoiceVariableBinding> bindings = model.PromotedMicroChoiceBindings
                .Where(binding =>
                    string.Equals(binding.StationName, witness.StationName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(binding.RequirementId, witness.RequirementId, StringComparison.OrdinalIgnoreCase) &&
                    binding.TrainIndex == witness.TrainIndex)
                .ToList();
            if (bindings.Count == 0)
            {
                continue;
            }

            if (releasedStationNames.Contains(witness.StationName) ||
                (releasedRequirementIds?.Contains(witness.RequirementId) ?? false))
            {
                skippedActiveStationWitnesses++;
                actuallyReleasedRequirementIds.Add(witness.RequirementId);
                continue;
            }

            IReadOnlyList<PromotedMicroChoiceVariableBinding> selected = bindings
                .Where(binding => binding.IsStop == witness.IsStop &&
                                  string.Equals(binding.TrackName, witness.TrackName, StringComparison.OrdinalIgnoreCase) &&
                                  binding.IncomingRouteId == witness.IncomingRouteId &&
                                  binding.OutgoingRouteId == witness.OutgoingRouteId)
                .ToList();
            if (selected.Count != 1)
            {
                continue;
            }

            fixedRequirements++;
            fixedRequirementIds.Add(witness.RequirementId);
            foreach (PromotedMicroChoiceVariableBinding binding in bindings)
            {
                double value = ReferenceEquals(binding, selected[0]) ? 1.0 : 0.0;
                binding.Variable.Set(GRB.DoubleAttr.LB, value);
                binding.Variable.Set(GRB.DoubleAttr.UB, value);
                fixedVariables++;
            }
        }
        model.Model.Update();
        model.TemporaryFixationAudit = audit is null
            ? new ValidityFirstTemporaryFixationAudit(
                "temporary_verified_micro_fixation",
                "initial_restricted",
                "保留已验证的非热点微观离散选择，并只在当前模型内通过变量上下界固定。",
                fixedRequirementIds.Order(StringComparer.OrdinalIgnoreCase).ToList(),
                actuallyReleasedRequirementIds.Order(StringComparer.OrdinalIgnoreCase).ToList(),
                0,
                0.0)
            : audit with
            {
                FixedRequirementIds = fixedRequirementIds.Order(StringComparer.OrdinalIgnoreCase).ToList(),
                ReleasedRequirementIds = actuallyReleasedRequirementIds.Order(StringComparer.OrdinalIgnoreCase).ToList()
            };
        return new VerifiedMicroAssignmentTemporaryFixResult(
            fixedRequirements,
            fixedVariables,
            skippedActiveStationWitnesses,
            fixedRequirementIds.Order(StringComparer.OrdinalIgnoreCase).ToList(),
            actuallyReleasedRequirementIds.Order(StringComparer.OrdinalIgnoreCase).ToList());
    }
}

internal static class VerifiedMicroAssignmentWitnessBuilder
{
    internal static IReadOnlyList<VerifiedMicroAssignmentWitness> Capture(
        StationLevelMicroFeasibilitySlaveResult result,
        string candidateSnapshotFingerprint = "")
    {
        ArgumentNullException.ThrowIfNull(result);
        List<VerifiedMicroAssignmentWitness> witnesses = [];
        foreach (StationLevelMicroSubproblemResult station in result.StationResults
                     .Where(station => station.IsFeasible &&
                                       station.AssignmentResult.Status == MicroSolveStatus.Feasible))
        {
            if (!TryCaptureCompleteAssignment(
                    station,
                    candidateSnapshotFingerprint,
                    "station_micro_validated",
                    out IReadOnlyList<VerifiedMicroAssignmentWitness> stationWitnesses,
                    out _))
            {
                continue;
            }
            witnesses.AddRange(stationWitnesses);
        }

        return witnesses
            .GroupBy(witness => witness.RequirementId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Single())
            .OrderBy(witness => witness.StationName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(witness => witness.TrainIndex)
            .ThenBy(witness => witness.RequirementId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static bool TryCaptureCompleteAssignment(
        StationLevelMicroSubproblemResult station,
        string candidateSnapshotFingerprint,
        string verificationScope,
        out IReadOnlyList<VerifiedMicroAssignmentWitness> witnesses,
        out string failureReason)
    {
        ArgumentNullException.ThrowIfNull(station);
        ArgumentException.ThrowIfNullOrWhiteSpace(verificationScope);
        if (station.AssignmentResult.Status != MicroSolveStatus.Feasible ||
            !station.AssignmentResult.IsFeasible)
        {
            witnesses = [];
            failureReason = $"assignment_status={station.AssignmentResult.Status}";
            return false;
        }

        Dictionary<string, StationRouteTrackAlternative> selectedByRequirement =
            station.AssignmentResult.SelectedAlternatives
                .GroupBy(alternative => alternative.RequirementId, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() == 1)
                .ToDictionary(group => group.Key, group => group.Single(), StringComparer.OrdinalIgnoreCase);
        List<string> missingRequirementIds = station.Requirements
            .Where(requirement => !selectedByRequirement.ContainsKey(requirement.StopId))
            .Select(requirement => requirement.StopId)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (missingRequirementIds.Count > 0 || selectedByRequirement.Count != station.Requirements.Count)
        {
            witnesses = [];
            failureReason = missingRequirementIds.Count > 0
                ? $"missing_requirement_ids={string.Join('|', missingRequirementIds)}"
                : $"assignment_count={selectedByRequirement.Count}, requirement_count={station.Requirements.Count}";
            return false;
        }

        witnesses = station.Requirements
            .Select(requirement =>
            {
                StationRouteTrackAlternative selected = selectedByRequirement[requirement.StopId];
                return new VerifiedMicroAssignmentWitness(
                    station.StationName,
                    requirement.StopId,
                    requirement.TrainIndex,
                    selected.IsStop,
                    selected.TrackName,
                    selected.IncomingRoute?.RouteId,
                    selected.OutgoingRoute?.RouteId,
                    candidateSnapshotFingerprint,
                    requirement.ArrivalSeconds,
                    requirement.DepartureSeconds,
                    verificationScope);
            })
            .OrderBy(witness => witness.TrainIndex)
            .ThenBy(witness => witness.RequirementId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        failureReason = string.Empty;
        return true;
    }
}

/// <summary>
/// 用当前候选时刻重新生成已验证见证并写回快照。调用方必须传入本次 Micro 求解结果，
/// 因此时刻改变后不会沿用旧快照中的“已验证”标记。
/// </summary>
internal static class VerifiedMicroAssignmentSnapshotRefresher
{
    internal static MasterSolutionSnapshot Refresh(
        MasterSolutionSnapshot snapshot,
        StationLevelMicroFeasibilitySlaveResult result,
        string candidateSnapshotFingerprint)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(result);
        IReadOnlyList<VerifiedMicroAssignmentWitness> witnesses =
            VerifiedMicroAssignmentWitnessBuilder.Capture(result, candidateSnapshotFingerprint);
        return snapshot with { VerifiedMicroAssignments = witnesses };
    }
}

internal sealed record HotspotMicroAssignmentCandidate(
    string StationName,
    string CandidateSnapshotFingerprint,
    IReadOnlyList<VerifiedMicroAssignmentWitness> Witnesses,
    string VerificationScope);

/// <summary>
/// 从热点站分配器已经求出的完整离散选择中提取“仅供本轮重定时固定”的候选。
/// 该候选可以仍有精确间隔冲突，因此不会写入 VerifiedMicroAssignments。
/// </summary>
internal static class HotspotMicroAssignmentCandidateBuilder
{
    internal static bool TryCapture(
        StationLevelMicroSubproblemResult station,
        string candidateSnapshotFingerprint,
        out HotspotMicroAssignmentCandidate? candidate,
        out string failureReason)
    {
        const string scope = "candidate_assignment_not_exact_headway_verified";
        if (!VerifiedMicroAssignmentWitnessBuilder.TryCaptureCompleteAssignment(
                station,
                candidateSnapshotFingerprint,
                scope,
                out IReadOnlyList<VerifiedMicroAssignmentWitness> witnesses,
                out failureReason))
        {
            candidate = null;
            return false;
        }

        candidate = new HotspotMicroAssignmentCandidate(
            station.StationName,
            candidateSnapshotFingerprint,
            witnesses,
            scope);
        return true;
    }
}

/// <summary>
/// 从旧版纯 Macro 快照保存的端点时刻和运行时分附加量恢复原始业务目标，使格式升级不依赖旧 Checker 状态。
/// </summary>
public static class MasterSolutionSnapshotObjectiveResolver
{
    public static MasterSolutionSnapshot Ensure(
        MasterSolutionSnapshot snapshot,
        IEnumerable<TrainRecord> trains)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(trains);
        if (snapshot.BusinessObjective is not null)
        {
            return snapshot;
        }

        TrainRecord[] admittedTrains = trains
            .Where(train => snapshot.AdmittedTrainIndices.Contains(train.TrainIndex))
            .ToArray();
        if (admittedTrains.Length == 0)
        {
            throw new InvalidDataException(
                "Cannot recover the business objective because the snapshot admits no known train.");
        }

        double[] originDepartures = admittedTrains
            .Select(train => ReadRequiredTime(
                snapshot.DepartureSecondsByStopId,
                train.OrderedStops[0].StopId,
                "origin departure"))
            .ToArray();
        double[] destinationArrivals = admittedTrains
            .Select(train => ReadRequiredTime(
                snapshot.ArrivalSecondsByStopId,
                train.OrderedStops[^1].StopId,
                "destination arrival"))
            .ToArray();
        BusinessObjectiveVector recovered = BusinessObjectiveCalculator.Calculate(
            originDepartures,
            destinationArrivals,
            snapshot.RunningTimeSupplementSecondsByKey?.Values ?? []);
        return snapshot with { BusinessObjective = recovered };
    }

    private static double ReadRequiredTime(
        IReadOnlyDictionary<string, double> values,
        string stopId,
        string role)
    {
        if (!values.TryGetValue(stopId, out double value))
        {
            throw new InvalidDataException(
                $"Cannot recover the business objective because {role} '{stopId}' is missing.");
        }
        return value;
    }
}

public sealed record MasterSolutionMipStartScope(
    IReadOnlySet<string> ExcludedTimeStopIds,
    IReadOnlySet<string> ExcludedStopIds,
    IReadOnlySet<string> ExcludedSectionPrecedenceKeys,
    IReadOnlySet<string> ExcludedStationEventPrecedenceKeys,
    IReadOnlySet<string> ExcludedRunningTimeSupplementKeys,
    IReadOnlySet<string> ExcludedMicroStations)
{
    public static MasterSolutionMipStartScope Full { get; } = new(
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    public bool ShouldStartArrivalOrDeparture(string stopId) => !ExcludedTimeStopIds.Contains(stopId);
    public bool ShouldStartStop(string stopId) => !ExcludedStopIds.Contains(stopId);
    public bool ShouldStartSectionPrecedence(string key) => !ExcludedSectionPrecedenceKeys.Contains(key);
    public bool ShouldStartStationEventPrecedence(string key) =>
        !ExcludedStationEventPrecedenceKeys.Contains(key);
    public bool ShouldStartRunningTimeSupplement(string key) =>
        !ExcludedRunningTimeSupplementKeys.Contains(key);
    public bool ShouldStartMicroStation(string stationName) => !ExcludedMicroStations.Contains(stationName);
}

public static class MasterSolutionSnapshotApplier
{
    public static VerifiedMicroAssignmentMipStartResult ApplyMipStart(
        BasicMilpModel model,
        MasterSolutionSnapshot snapshot) => ApplyMipStart(
        model,
        snapshot,
        MasterSolutionMipStartScope.Full,
        deriveMissingSectionPrecedence: false);

    public static VerifiedMicroAssignmentMipStartResult ApplyExpandableMacroMipStart(
        BasicMilpModel model,
        MasterSolutionSnapshot reducedMacroSnapshot,
        IReadOnlyList<string>? candidateValidationViolations = null) => ApplyMipStart(
        model,
        reducedMacroSnapshot,
        MasterSolutionMipStartScope.Full,
        deriveMissingSectionPrecedence: true,
        candidateValidationViolations);

    public static VerifiedMicroAssignmentMipStartResult ApplyMipStart(
        BasicMilpModel model,
        MasterSolutionSnapshot snapshot,
        MasterSolutionMipStartScope scope) => ApplyMipStart(
        model,
        snapshot,
        scope,
        deriveMissingSectionPrecedence: false);

    private static VerifiedMicroAssignmentMipStartResult ApplyMipStart(
        BasicMilpModel model,
        MasterSolutionSnapshot snapshot,
        MasterSolutionMipStartScope scope,
        bool deriveMissingSectionPrecedence,
        IReadOnlyList<string>? candidateValidationViolations = null)
    {
        ArgumentNullException.ThrowIfNull(scope);
        int skippedTimes = ApplyStarts(
            model.ArrivalVars, snapshot.ArrivalSecondsByStopId, scope.ShouldStartArrivalOrDeparture);
        skippedTimes += ApplyStarts(
            model.DepartureVars, snapshot.DepartureSecondsByStopId, scope.ShouldStartArrivalOrDeparture);
        int skippedStops = ApplyStarts(model.StopVars, snapshot.StopByStopId, scope.ShouldStartStop);
        int skippedPrecedence = ApplyStarts(
            model.SectionPrecedenceVars,
            snapshot.SectionPrecedenceByKey,
            scope.ShouldStartSectionPrecedence);
        int derivedSectionPrecedence = deriveMissingSectionPrecedence
            ? ApplyMissingSectionPrecedenceStarts(model, snapshot, scope)
            : 0;
        skippedPrecedence += ApplyStarts(
            model.StationEventPrecedenceVars,
            snapshot.StationEventPrecedenceByKey,
            scope.ShouldStartStationEventPrecedence);
        if (deriveMissingSectionPrecedence)
            ApplyMissingStationEventPrecedenceStarts(model, snapshot, scope);
        int skippedSupplements = ApplyStarts(
            model.RunningTimeSupplementVars,
            snapshot.RunningTimeSupplementSecondsByKey ?? new Dictionary<string, double>(),
            scope.ShouldStartRunningTimeSupplement);
        int startedProducts = 0;
        foreach (StopPairProductBinding binding in model.StopPairProductBindings)
        {
            if (!scope.ShouldStartStop(binding.FirstStopId) || !scope.ShouldStartStop(binding.SecondStopId))
            {
                binding.ProductVariable.Set(GRB.DoubleAttr.Start, GRB.UNDEFINED);
                continue;
            }
            if (!snapshot.StopByStopId.TryGetValue(binding.FirstStopId, out int first) ||
                !snapshot.StopByStopId.TryGetValue(binding.SecondStopId, out int second)) continue;
            binding.ProductVariable.Set(GRB.DoubleAttr.Start, first * second);
            startedProducts++;
        }
        int startedReversals = 0;
        Dictionary<string, double> beingByStop = new(StringComparer.OrdinalIgnoreCase);
        foreach (OvertakingReversalVariableBinding binding in model.OvertakingReversalBindings)
        {
            if (!scope.ShouldStartStop(binding.StopId))
            {
                binding.ReversalVariable.Set(GRB.DoubleAttr.Start, GRB.UNDEFINED);
                continue;
            }
            double incoming = binding.IncomingPrecedence.Get(GRB.DoubleAttr.Start);
            double outgoing = binding.OutgoingPrecedence.Get(GRB.DoubleAttr.Start);
            if (Math.Abs(incoming) >= GRB.UNDEFINED / 2.0 || Math.Abs(outgoing) >= GRB.UNDEFINED / 2.0) continue;
            double reversal = binding.FirstTrainReversal
                ? (1.0 - incoming) * outgoing
                : incoming * (1.0 - outgoing);
            binding.ReversalVariable.Set(GRB.DoubleAttr.Start, reversal);
            beingByStop[binding.StopId] = Math.Max(beingByStop.GetValueOrDefault(binding.StopId), reversal);
            startedReversals++;
        }
        int startedBeing = 0;
        foreach ((string stopId, double value) in beingByStop)
            if (model.BeingOvertakenVars.TryGetValue(stopId, out GRBVar? variable))
            {
                variable.Set(GRB.DoubleAttr.Start, value);
                startedBeing++;
            }
        if (deriveMissingSectionPrecedence)
        {
            ApplyMaintenanceWindowStarts(model, snapshot, scope);
            ApplyStationQuotaCategoryStarts(model, snapshot, scope);
        }
        IReadOnlyList<VerifiedMicroAssignmentWitness>? eligibleWitnesses = snapshot.VerifiedMicroAssignments?
            .Where(witness => scope.ShouldStartMicroStation(witness.StationName))
            .ToArray();
        int skippedMicro = (snapshot.VerifiedMicroAssignments?.Count ?? 0) -
            (eligibleWitnesses?.Count ?? 0);
        VerifiedMicroAssignmentMipStartResult micro = VerifiedMicroAssignmentMipStartApplier.Apply(
            model,
            eligibleWitnesses);
        VerifiedMicroAssignmentMipStartResult result = micro with
        {
            StartedStopProductVariableCount = startedProducts,
            StartedReversalVariableCount = startedReversals,
            StartedBeingOvertakenVariableCount = startedBeing,
            SkippedArrivalDepartureVariableCount = skippedTimes,
            SkippedStopVariableCount = skippedStops,
            SkippedPrecedenceVariableCount = skippedPrecedence,
            SkippedRunningTimeSupplementVariableCount = skippedSupplements,
            SkippedMicroWitnessCount = skippedMicro,
            StartedDerivedSectionPrecedenceVariableCount = derivedSectionPrecedence
        };
        return result with
        {
            MacroCoverage = BuildMacroCoverageAudit(
                model,
                scope,
                candidateValidationViolations ?? [])
        };
    }

    private static void ApplyMissingStationEventPrecedenceStarts(
        BasicMilpModel model,
        MasterSolutionSnapshot snapshot,
        MasterSolutionMipStartScope scope)
    {
        Dictionary<(int TrainIndex, int StationOrder), TrainStopRecord> stops = model.Instance.TrainStops
            .ToDictionary(stop => (stop.TrainIndex, stop.StationOrder));
        foreach (StationEventConflictCandidate candidate in model.StationEventConflictCandidates)
        {
            string key = BasicMilpModelBuilder.BuildStationEventPrecedenceKey(candidate);
            if (!scope.ShouldStartStationEventPrecedence(key) ||
                snapshot.StationEventPrecedenceByKey.ContainsKey(key) ||
                !model.StationEventPrecedenceVars.TryGetValue(key, out GRBVar? variable) ||
                !stops.TryGetValue((candidate.FirstTrainIndex, candidate.FirstStationOrder), out TrainStopRecord? first) ||
                !stops.TryGetValue((candidate.SecondTrainIndex, candidate.SecondStationOrder), out TrainStopRecord? second) ||
                !TryReadEventTime(snapshot, first.StopId, candidate.FirstSide, out double firstTime) ||
                !TryReadEventTime(snapshot, second.StopId, candidate.SecondSide, out double secondTime))
                continue;

            // Macro convention: zero activates first-before-second.
            variable.Set(GRB.DoubleAttr.Start, firstTime <= secondTime ? 0.0 : 1.0);
        }
    }

    private static void ApplyMaintenanceWindowStarts(
        BasicMilpModel model,
        MasterSolutionSnapshot snapshot,
        MasterSolutionMipStartScope scope)
    {
        foreach (MaintenanceWindowBinaryBinding binding in model.MaintenanceWindowBinaryBindings)
        {
            if (!scope.ShouldStartArrivalOrDeparture(binding.SectionStartStopId) ||
                !scope.ShouldStartArrivalOrDeparture(binding.SectionEndStopId))
            {
                binding.BeforeWindowVariable.Set(GRB.DoubleAttr.Start, GRB.UNDEFINED);
                continue;
            }
            if (!snapshot.ArrivalSecondsByStopId.TryGetValue(binding.SectionEndStopId, out double arrival) ||
                !snapshot.DepartureSecondsByStopId.TryGetValue(binding.SectionStartStopId, out double departure))
                continue;

            if (arrival <= binding.WindowStartSeconds)
                binding.BeforeWindowVariable.Set(GRB.DoubleAttr.Start, 1.0);
            else if (departure >= binding.WindowEndSeconds)
                binding.BeforeWindowVariable.Set(GRB.DoubleAttr.Start, 0.0);
            // An overlap is genuinely ambiguous/infeasible for this branch pair; keep it undefined.
        }
    }

    private static void ApplyStationQuotaCategoryStarts(
        BasicMilpModel model,
        MasterSolutionSnapshot snapshot,
        MasterSolutionMipStartScope scope)
    {
        Dictionary<string, TrainStopRecord> stopById = model.Instance.TrainStops.ToDictionary(
            stop => stop.StopId,
            StringComparer.OrdinalIgnoreCase);
        foreach (StationQuotaCategoryBinding binding in model.StationQuotaCategoryBindings)
        {
            if (!scope.ShouldStartStop(binding.StopId) ||
                !scope.ShouldStartArrivalOrDeparture(binding.StopId))
            {
                SetQuotaStarts(binding, GRB.UNDEFINED, GRB.UNDEFINED, GRB.UNDEFINED);
                continue;
            }
            if (!snapshot.StopByStopId.TryGetValue(binding.StopId, out int isStop) ||
                !stopById.TryGetValue(binding.StopId, out TrainStopRecord? stop))
                continue;
            if (isStop == 0)
            {
                SetQuotaStarts(binding, 0.0, 0.0, 0.0);
                continue;
            }
            IReadOnlyDictionary<string, double> times = stop.StopRole == TrainStopRole.Origin
                ? snapshot.DepartureSecondsByStopId
                : snapshot.ArrivalSecondsByStopId;
            if (!times.TryGetValue(binding.StopId, out double eventTime))
                continue;

            var category = StationQuotaWindowPolicy.Classify(true, eventTime, binding.WindowStartSeconds, binding.WindowEndSeconds);
            if (category == MacroQuotaCategory.LeftOutside)
                SetQuotaStarts(binding, 0.0, 1.0, 0.0);
            else if (category == MacroQuotaCategory.RightOutside)
                SetQuotaStarts(binding, 0.0, 0.0, 1.0);
            else
                SetQuotaStarts(binding, 1.0, 0.0, 0.0);
        }
    }

    private static void SetQuotaStarts(
        StationQuotaCategoryBinding binding,
        double inWindow,
        double leftOutside,
        double rightOutside)
    {
        binding.InWindowVariable.Set(GRB.DoubleAttr.Start, inWindow);
        binding.LeftOutsideVariable.Set(GRB.DoubleAttr.Start, leftOutside);
        binding.RightOutsideVariable.Set(GRB.DoubleAttr.Start, rightOutside);
    }

    private static bool TryReadEventTime(
        MasterSolutionSnapshot snapshot,
        string stopId,
        StationEventSide side,
        out double value) =>
        (side == StationEventSide.Incoming
            ? snapshot.ArrivalSecondsByStopId
            : snapshot.DepartureSecondsByStopId).TryGetValue(stopId, out value);

    private static MacroMipStartCoverageAudit BuildMacroCoverageAudit(
        BasicMilpModel model,
        MasterSolutionMipStartScope scope,
        IReadOnlyList<string> candidateValidationViolations)
    {
        model.Model.Update();
        HashSet<string> excluded = BuildExcludedBinaryNames(model, scope);
        Dictionary<string, int> startedByFamily = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> unstartedByFamily = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> excludedByFamily = new(StringComparer.OrdinalIgnoreCase);
        List<string> unstarted = [];
        List<string> excludedSamples = [];
        int startedCount = 0;
        int eligibleCount = 0;

        foreach (GRBVar variable in model.Model.GetVars()
                     .Where(item => item.Get(GRB.CharAttr.VType) == GRB.BINARY)
                     .OrderBy(item => item.Get(GRB.StringAttr.VarName), StringComparer.OrdinalIgnoreCase))
        {
            string name = variable.Get(GRB.StringAttr.VarName);
            string family = ClassifyBinaryFamily(name);
            if (excluded.Contains(name))
            {
                Increment(excludedByFamily, family);
                if (excludedSamples.Count < 50) excludedSamples.Add(name);
                continue;
            }

            eligibleCount++;
            double start = variable.Get(GRB.DoubleAttr.Start);
            if (double.IsFinite(start) && Math.Abs(start) < GRB.UNDEFINED / 2.0)
            {
                startedCount++;
                Increment(startedByFamily, family);
            }
            else
            {
                Increment(unstartedByFamily, family);
                if (unstarted.Count < 50) unstarted.Add(name);
            }
        }

        return new MacroMipStartCoverageAudit(
            eligibleCount,
            startedCount,
            eligibleCount - startedCount,
            startedByFamily,
            unstartedByFamily,
            unstarted,
            candidateValidationViolations.ToArray(),
            excluded.Count,
            excludedByFamily,
            excludedSamples);
    }

    private static HashSet<string> BuildExcludedBinaryNames(
        BasicMilpModel model,
        MasterSolutionMipStartScope scope)
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string stopId, GRBVar variable) in model.StopVars)
            if (!scope.ShouldStartStop(stopId)) names.Add(variable.Get(GRB.StringAttr.VarName));
        foreach ((string key, GRBVar variable) in model.SectionPrecedenceVars)
            if (!scope.ShouldStartSectionPrecedence(key)) names.Add(variable.Get(GRB.StringAttr.VarName));
        foreach ((string key, GRBVar variable) in model.StationEventPrecedenceVars)
            if (!scope.ShouldStartStationEventPrecedence(key)) names.Add(variable.Get(GRB.StringAttr.VarName));
        foreach (MaintenanceWindowBinaryBinding binding in model.MaintenanceWindowBinaryBindings)
            if (!scope.ShouldStartArrivalOrDeparture(binding.SectionStartStopId) ||
                !scope.ShouldStartArrivalOrDeparture(binding.SectionEndStopId))
                names.Add(binding.BeforeWindowVariable.Get(GRB.StringAttr.VarName));
        foreach (StationQuotaCategoryBinding binding in model.StationQuotaCategoryBindings)
            if (!scope.ShouldStartStop(binding.StopId) || !scope.ShouldStartArrivalOrDeparture(binding.StopId))
            {
                names.Add(binding.InWindowVariable.Get(GRB.StringAttr.VarName));
                names.Add(binding.LeftOutsideVariable.Get(GRB.StringAttr.VarName));
                names.Add(binding.RightOutsideVariable.Get(GRB.StringAttr.VarName));
            }
        foreach (PromotedMicroChoiceVariableBinding binding in model.PromotedMicroChoiceBindings)
            if (!scope.ShouldStartMicroStation(binding.StationName))
                names.Add(binding.Variable.Get(GRB.StringAttr.VarName));
        return names;
    }

    private static string ClassifyBinaryFamily(string name)
    {
        if (name.StartsWith("stop_", StringComparison.OrdinalIgnoreCase)) return "stop";
        if (name.StartsWith("prec_sec_", StringComparison.OrdinalIgnoreCase)) return "section_precedence";
        if (name.StartsWith("prec_sta_", StringComparison.OrdinalIgnoreCase)) return "station_event_precedence";
        if (name.StartsWith("mw_before_", StringComparison.OrdinalIgnoreCase)) return "maintenance_window";
        if (name.StartsWith("quota_", StringComparison.OrdinalIgnoreCase)) return "station_quota";
        if (name.Contains("micro", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("choice", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("order", StringComparison.OrdinalIgnoreCase)) return "promoted_micro";
        return "other";
    }

    private static void Increment(IDictionary<string, int> counts, string key)
    {
        counts.TryGetValue(key, out int current);
        counts[key] = current + 1;
    }

    private static int ApplyMissingSectionPrecedenceStarts(
        BasicMilpModel model,
        MasterSolutionSnapshot snapshot,
        MasterSolutionMipStartScope scope)
    {
        IReadOnlyDictionary<string, int> derived = DeriveMissingSectionPrecedenceStarts(
            model.Instance,
            model.SectionConflictCandidates,
            snapshot);
        int started = 0;
        foreach ((string key, int value) in derived)
        {
            if (!scope.ShouldStartSectionPrecedence(key) ||
                !model.SectionPrecedenceVars.TryGetValue(key, out GRBVar? variable))
                continue;

            // In the Macro formulation 0 activates first-before-second and 1 activates
            // second-before-first.  This is a start hint only: no variable is fixed.
            variable.Set(GRB.DoubleAttr.Start, value);
            started++;
        }
        return started;
    }

    internal static IReadOnlyDictionary<string, int> DeriveMissingSectionPrecedenceStarts(
        TimetablingInstance instance,
        IReadOnlyList<SectionConflictCandidate> completeRelations,
        MasterSolutionSnapshot reducedMacroSnapshot)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(completeRelations);
        ArgumentNullException.ThrowIfNull(reducedMacroSnapshot);
        Dictionary<(int TrainIndex, int StationOrder), TrainStopRecord> stopByTrainAndOrder =
            instance.TrainStops.ToDictionary(stop => (stop.TrainIndex, stop.StationOrder));
        Dictionary<string, int> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (SectionConflictCandidate candidate in completeRelations)
        {
            string key = BasicMilpModelBuilder.BuildSectionPrecedenceKey(candidate);
            if (reducedMacroSnapshot.SectionPrecedenceByKey.ContainsKey(key) ||
                !stopByTrainAndOrder.TryGetValue(
                    (candidate.FirstTrainIndex, candidate.FirstStopOrder), out TrainStopRecord? firstStart) ||
                !stopByTrainAndOrder.TryGetValue(
                    (candidate.SecondTrainIndex, candidate.SecondStopOrder), out TrainStopRecord? secondStart) ||
                !reducedMacroSnapshot.DepartureSecondsByStopId.TryGetValue(
                    firstStart.StopId, out double firstDeparture) ||
                !reducedMacroSnapshot.DepartureSecondsByStopId.TryGetValue(
                    secondStart.StopId, out double secondDeparture))
                continue;
            result[key] = firstDeparture <= secondDeparture ? 0 : 1;
        }
        return result;
    }

    private static int ApplyStarts<T>(
        IReadOnlyDictionary<string, GRBVar> variables,
        IReadOnlyDictionary<string, T> values,
        Func<string, bool> shouldStart)
        where T : struct, IConvertible
    {
        int skipped = 0;
        foreach ((string key, T value) in values)
        {
            if (variables.TryGetValue(key, out GRBVar? variable))
            {
                if (shouldStart(key))
                    variable.Set(GRB.DoubleAttr.Start, value.ToDouble(null));
                else
                {
                    variable.Set(GRB.DoubleAttr.Start, GRB.UNDEFINED);
                    skipped++;
                }
            }
        }
        return skipped;
    }
}
