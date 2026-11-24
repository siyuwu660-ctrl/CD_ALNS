// 车站级微观从问题按站提取到发事件、径路股道备选和占用间隔，通过组合搜索检验主问题解能否落实为站内分配。
// 每个车站子问题返回可行赋值或带最小性状态的冲突核；全局结果聚合站级状态，为 Benders 风格反馈和热点修复提供证据。
using System.Globalization;
using Gurobi;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

/// <summary>
/// 站级微观可行性子问题。
/// 给定一个已经固定了 master incumbent 的宏观解，逐站判断：
/// 1. route domain 是否存在；
/// 2. exact route headway 是否满足；
/// 3. stopping occupation 是否存在可行股道分配。
/// 当前版本仍是 feasibility slave，不优化微观目标，只返回站级可行性与冲突解释。
/// </summary>
internal sealed class StationLevelMicroFeasibilitySlave
{
    private const double ToleranceSeconds = GurobiTimetablingResearch.Domain.TimetableTimePolicy.NumericToleranceSeconds;
    private const double TimeDomainLowerBound = 0.0;
    private const double TimeDomainUpperBound = 86400.0;
    private readonly int _endpointOccupationSeconds;

    internal StationLevelMicroFeasibilitySlave(int endpointOccupationSeconds = StationEndpointOccupationPolicy.DefaultSeconds)
    {
        _endpointOccupationSeconds = endpointOccupationSeconds;
    }

    internal int EndpointOccupationSeconds => _endpointOccupationSeconds;

    internal static IReadOnlyList<StationMicroRequirement> BuildRequirementsForRepair(
        BasicMilpModel model,
        IReadOnlySet<string> stationNames)
    {
        Dictionary<int, TrainRecord> trainByIndex = model.Instance.Trains
            .ToDictionary(train => train.TrainIndex);
        return BuildRequirements(model, trainByIndex)
            .Where(requirement => stationNames.Contains(requirement.StationName))
            .OrderBy(requirement => requirement.StationName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(requirement => requirement.TrainIndex)
            .ThenBy(requirement => requirement.StationOrder)
            .ToList();
    }

    internal StationLevelMicroFeasibilitySlaveResult Solve(
        BasicMilpModel model,
        MicroFeasibilitySlaveRequest? request = null)
    {
        if (!model.HasIncumbentSolution)
        {
            return new StationLevelMicroFeasibilitySlaveResult(
                false,
                Array.Empty<StationLevelMicroSubproblemResult>(),
                new[]
                {
                    new MicroConflictExplanation(
                        "no_incumbent_solution",
                        string.Empty,
                        null,
                        null,
                        null,
                        0.0,
                        0.0,
                        "solution",
                        "Gurobi has no incumbent solution.",
                        "No master incumbent is available for station-level micro feasibility solving.")
                },
                Array.Empty<MicroConflictExplanation>());
        }

        Dictionary<int, TrainRecord> trainByIndex = model.Instance.Trains.ToDictionary(train => train.TrainIndex);
        List<StationMicroRequirement> allRequirements = BuildRequirements(model, trainByIndex);
        IReadOnlySet<string>? targetStations = request?.StationNames;

        IEnumerable<IGrouping<string, StationMicroRequirement>> groupedRequirements = allRequirements
            .GroupBy(requirement => requirement.StationName, StringComparer.OrdinalIgnoreCase);

        if (targetStations is not null && targetStations.Count > 0)
        {
            groupedRequirements = groupedRequirements
                .Where(group => targetStations.Contains(group.Key));
        }

        List<StationLevelMicroSubproblemResult> stationResults = new();
        List<MicroConflictExplanation> allConflicts = new();
        List<MicroConflictExplanation> allRouteChoiceDiagnostics = new();

        foreach (IGrouping<string, StationMicroRequirement> stationGroup in groupedRequirements)
        {
            double remaining=request?.RemainingSeconds?.Invoke()??double.PositiveInfinity;
            if(remaining<=0) return new(false,stationResults,allConflicts,allRouteChoiceDiagnostics,MicroSolveStatus.Unknown);
            MicroSearchOptions? options=double.IsFinite(remaining)?new(250_000,TimeSpan.FromSeconds(Math.Min(5,remaining))):null;
            StationLevelMicroSubproblemResult result = SolveStation(model.Instance, stationGroup.Key, stationGroup.ToList(),options,request?.FixedAssignments?.Where(w=>w.StationName.Equals(stationGroup.Key,StringComparison.OrdinalIgnoreCase)).ToArray(),request?.RemainingSeconds);
            stationResults.Add(result);
            allConflicts.AddRange(result.Conflicts);
            allRouteChoiceDiagnostics.AddRange(result.RouteChoiceDiagnostics);
        }

        stationResults = stationResults
            .OrderByDescending(item => item.Conflicts.Count)
            .ThenBy(item => item.StationName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        MicroSolveStatus overallStatus = stationResults.Any(result => result.AssignmentResult.Status == MicroSolveStatus.Unknown)
            ? MicroSolveStatus.Unknown
            : allConflicts.Count == 0
                ? MicroSolveStatus.Feasible
                : MicroSolveStatus.ProvenInfeasible;

        return new StationLevelMicroFeasibilitySlaveResult(
            overallStatus == MicroSolveStatus.Feasible,
            stationResults,
            allConflicts,
            allRouteChoiceDiagnostics,
            overallStatus);
    }

    private StationLevelMicroSubproblemResult SolveStation(
        TimetablingInstance instance,
        string stationName,
        IReadOnlyList<StationMicroRequirement> requirements,MicroSearchOptions? options=null,IReadOnlyList<VerifiedMicroAssignmentWitness>? fixedAssignments=null,Func<double>? remaining=null)
    {
        List<MicroConflictExplanation> conflicts = new();
        List<MicroConflictExplanation> routeChoiceDiagnostics = new();

        foreach (StationMicroRequirement requirement in requirements)
        {
            if (requirement.IncomingRoutes.Count == 0 && requirement.RequiresIncomingRoute)
            {
                conflicts.Add(CreateRouteConflict(
                    "missing_incoming_route",
                    requirement,
                    requirement.ArrivalSeconds,
                    "incoming_route",
                    $"No candidate incoming route was found for section '{requirement.IncomingSectionName}' and movement '{requirement.IncomingEvent}'."));
            }

            if (requirement.OutgoingRoutes.Count == 0 && requirement.RequiresOutgoingRoute)
            {
                conflicts.Add(CreateRouteConflict(
                    "missing_outgoing_route",
                    requirement,
                    requirement.DepartureSeconds,
                    "outgoing_route",
                    $"No candidate outgoing route was found for section '{requirement.OutgoingSectionName}' and movement '{requirement.OutgoingEvent}'."));
            }

            if (requirement.FeasibleTracks.Count == 0)
            {
                RouteTrackModeCapability? selectedCapability = requirement.StopModeAnalysis is null
                    ? null
                    : requirement.IsStop
                        ? requirement.StopModeAnalysis.Stopping
                        : requirement.StopModeAnalysis.Passing;
                bool derivedFromMissingRoute = requirement.IncomingRoutes.Count == 0 || requirement.OutgoingRoutes.Count == 0;
                conflicts.Add(new MicroConflictExplanation(
                    requirement.HasBenchmarkRestriction && requirement.PreferredTracks.Count == 0
                        ? "benchmark_track_domain_empty"
                        : "empty_track_domain",
                    requirement.StationName,
                    requirement.TrainIndex,
                    null,
                    requirement.StationOrder,
                    requirement.ArrivalSeconds,
                    requirement.DepartureSeconds,
                    "track_domain",
                    $"No common feasible track remains for train {requirement.TrainIndex} at station {requirement.StationName}.",
                    requirement.DescribeTrackDomain(),
                    RootCause: selectedCapability?.FailureReason,
                    RootConflictKey: BuildRouteDomainRootKey(requirement),
                    RecommendedAction: ResolveRecommendedRouteDomainAction(requirement),
                    IsDerived: derivedFromMissingRoute));
            }
        }

        conflicts.AddRange(CheckExactHeadways(instance, requirements, routeChoiceDiagnostics));
        StationRouteTrackAssignmentResult assignmentResult = StationRouteTrackAssignmentSolver.Solve(
            requirements.Select(requirement =>
            {
                StationOccupationInterval occupation = StationEndpointOccupationPolicy.Resolve(
                    requirement.StopRole,
                    requirement.IsStop,
                    requirement.ArrivalSeconds,
                    requirement.DepartureSeconds,
                    _endpointOccupationSeconds,
                    TimeDomainLowerBound,
                    TimeDomainUpperBound);
                return new StationAssignmentRequirement(
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
            }).ToList(),
            (leadingRouteId, followingRouteId) =>
                instance.TryGetRouteHeadwayRule(leadingRouteId, followingRouteId, out RouteHeadwayRule? rule) && rule is not null
                    ? rule.MinHeadwaySeconds
                    : null,options,fixedAssignments,remaining);

        if (!assignmentResult.IsFeasible && assignmentResult.Status == MicroSolveStatus.ProvenInfeasible)
        {
            conflicts.Add(new MicroConflictExplanation(
                "station_route_track_assignment_infeasible",
                stationName,
                null,
                null,
                null,
                requirements.Min(requirement => requirement.ArrivalSeconds),
                requirements.Max(requirement => requirement.DepartureSeconds),
                "joint_route_track_assignment",
                $"No coherent route-track assignment exists at station {stationName}.",
                assignmentResult.FailureEvidence));
        }

        return new StationLevelMicroSubproblemResult(
            stationName,
            requirements,
            BuildConflictCore(stationName, requirements, conflicts, assignmentResult),
            conflicts.Count == 0,
            conflicts,
            routeChoiceDiagnostics,
            assignmentResult);
    }

    internal static StationConflictCore BuildConflictCore(
        string stationName,
        IReadOnlyList<StationMicroRequirement> requirements,
        IReadOnlyList<MicroConflictExplanation> conflicts,
        StationRouteTrackAssignmentResult assignmentResult)
    {
        bool hasJointAssignmentConflict = conflicts.Any(conflict => string.Equals(
            conflict.ConflictKind,
            "station_route_track_assignment_infeasible",
            StringComparison.OrdinalIgnoreCase));
        StationInfeasibleCore? suppliedCore = hasJointAssignmentConflict
            ? assignmentResult.InfeasibleCore
            : null;
        List<int> coreTrainIndices = suppliedCore is null
            ? ResolveCoreTrainIndices(requirements, conflicts)
            : suppliedCore.TrainIndices
                .Distinct()
                .OrderBy(trainIndex => trainIndex)
                .ToList();
        HashSet<int> coreTrainSet = coreTrainIndices.ToHashSet();
        HashSet<string>? coreRequirementIds = suppliedCore?.RequirementIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> supportKeys = new(StringComparer.OrdinalIgnoreCase);

        List<StationMicroRequirement> coreRequirements = requirements
            .Where(requirement => coreRequirementIds is not null
                ? coreRequirementIds.Contains(requirement.StopId)
                : coreTrainSet.Count == 0 || coreTrainSet.Contains(requirement.TrainIndex))
            .OrderBy(item => item.TrainIndex)
            .ThenBy(item => item.StationOrder)
            .ToList();

        for (int firstIndex = 0; firstIndex < coreRequirements.Count; firstIndex++)
        {
            StationMicroRequirement first = coreRequirements[firstIndex];
            for (int secondIndex = firstIndex + 1; secondIndex < coreRequirements.Count; secondIndex++)
            {
                StationMicroRequirement second = coreRequirements[secondIndex];
                AddPrecedenceKeyIfPresent(supportKeys, first.IncomingSectionName, first.TrainIndex, second.TrainIndex);
                AddPrecedenceKeyIfPresent(supportKeys, first.OutgoingSectionName, first.TrainIndex, second.TrainIndex);
                AddPrecedenceKeyIfPresent(supportKeys, second.IncomingSectionName, first.TrainIndex, second.TrainIndex);
                AddPrecedenceKeyIfPresent(supportKeys, second.OutgoingSectionName, first.TrainIndex, second.TrainIndex);
            }
        }

        // When the assignment solver supplied an actual infeasible core, the support stops and
        // trains below come from that core.  Keep its business kind attached to the same evidence;
        // an unrelated exact-headway row with a tied count must not relabel the resource core and
        // make the Macro feedback mapper silently discard it.
        string primaryConflictKind = suppliedCore is not null
            ? "station_route_track_assignment_infeasible"
            : conflicts
                .GroupBy(conflict => conflict.ConflictKind, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Key)
                .FirstOrDefault()
              ?? string.Empty;

        IReadOnlyList<string> supportStopIds = coreRequirements
            .Select(requirement => requirement.StopId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(stopId => stopId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new StationConflictCore(
            stationName,
            coreTrainIndices,
            supportKeys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ToList(),
            supportStopIds,
            primaryConflictKind,
            conflicts.Count);
    }

    private static List<int> ResolveCoreTrainIndices(
        IReadOnlyList<StationMicroRequirement> requirements,
        IReadOnlyList<MicroConflictExplanation> conflicts)
    {
        Dictionary<(int FirstTrain, int SecondTrain), int> pairWeights = new();
        Dictionary<int, int> singleWeights = new();

        foreach (MicroConflictExplanation conflict in conflicts)
        {
            if (conflict.FirstTrainIndex.HasValue && conflict.SecondTrainIndex.HasValue)
            {
                int first = Math.Min(conflict.FirstTrainIndex.Value, conflict.SecondTrainIndex.Value);
                int second = Math.Max(conflict.FirstTrainIndex.Value, conflict.SecondTrainIndex.Value);
                pairWeights[(first, second)] = pairWeights.TryGetValue((first, second), out int count) ? count + 1 : 1;
            }

            if (conflict.FirstTrainIndex.HasValue)
            {
                int trainIndex = conflict.FirstTrainIndex.Value;
                singleWeights[trainIndex] = singleWeights.TryGetValue(trainIndex, out int count) ? count + 1 : 1;
            }

            if (conflict.SecondTrainIndex.HasValue)
            {
                int trainIndex = conflict.SecondTrainIndex.Value;
                singleWeights[trainIndex] = singleWeights.TryGetValue(trainIndex, out int count) ? count + 1 : 1;
            }
        }

        if (pairWeights.Count > 0)
        {
            (int FirstTrain, int SecondTrain) selectedPair = pairWeights
                .OrderByDescending(item => item.Value)
                .ThenBy(item => item.Key.FirstTrain)
                .ThenBy(item => item.Key.SecondTrain)
                .Select(item => item.Key)
                .First();

            return new List<int> { selectedPair.FirstTrain, selectedPair.SecondTrain };
        }

        if (singleWeights.Count > 0)
        {
            int selectedTrain = singleWeights
                .OrderByDescending(item => item.Value)
                .ThenBy(item => item.Key)
                .Select(item => item.Key)
                .First();

            int fallbackPartner = requirements
                .Where(item => item.TrainIndex != selectedTrain)
                .OrderBy(item => Math.Abs(item.TrainIndex - selectedTrain))
                .ThenBy(item => item.TrainIndex)
                .Select(item => item.TrainIndex)
                .FirstOrDefault();

            return fallbackPartner == 0
                ? new List<int> { selectedTrain }
                : new List<int> { selectedTrain, fallbackPartner };
        }

        return requirements
            .Select(item => item.TrainIndex)
            .Distinct()
            .OrderBy(item => item)
            .Take(2)
            .ToList();
    }

    private static void AddPrecedenceKeyIfPresent(
        ISet<string> keys,
        string? sectionName,
        int firstTrainIndex,
        int secondTrainIndex)
    {
        if (string.IsNullOrWhiteSpace(sectionName) || firstTrainIndex == secondTrainIndex)
        {
            return;
        }

        keys.Add(BasicMilpModelBuilder.BuildSectionPrecedenceKey(sectionName, firstTrainIndex, secondTrainIndex));
    }

    private static List<StationMicroRequirement> BuildRequirements(
        BasicMilpModel model,
        IReadOnlyDictionary<int, TrainRecord> trainByIndex)
    {
        List<StationMicroRequirement> requirements = new();

        foreach (TrainStopRecord stop in model.Instance.TrainStops)
        {
            if (!trainByIndex.TryGetValue(stop.TrainIndex, out TrainRecord? train))
            {
                continue;
            }

            bool isStop = BinaryValue(model.StopVars[stop.StopId]) == 1;
            requirements.Add(BuildRequirementForMode(model, train, stop, isStop));
        }

        return requirements;
    }

    /// <summary>
    /// 使用与 Global Micro Checker 相同的进路和股道域规则，为指定停站模式建立一个站内作业要求。
    /// 热点精确化调用该方法枚举合法模式，从而避免另建一套 Micro 域解释逻辑。
    /// </summary>
    internal static StationMicroRequirement BuildRequirementForMode(
        BasicMilpModel model,
        TrainRecord train,
        TrainStopRecord stop,
        bool isStop)
    {
        return BuildRequirementForMode(
            model.Instance,
            train,
            stop,
            isStop,
            Value(model.ArrivalVars[stop.StopId]),
            Value(model.DepartureVars[stop.StopId]));
    }

    internal static StationMicroRequirement BuildRequirementForMode(
        TimetablingInstance instance,
        TrainRecord train,
        TrainStopRecord stop,
        bool isStop,
        double arrivalSeconds,
        double departureSeconds)
    {
        TrainStopRecord? nextStop = train.OrderedStops.FirstOrDefault(candidate =>
            candidate.StationOrder == stop.StationOrder + 1);
        MovementEvent incomingEvent = isStop ? MovementEvent.Arrival : MovementEvent.Passing;
        MovementEvent outgoingEvent = isStop ? MovementEvent.Departure : MovementEvent.Passing;

        List<RouteRecord> incomingRoutes = string.IsNullOrWhiteSpace(stop.IncomingSectionName)
            ? []
            : ResolveRoutes(instance, stop.StationName, stop.IncomingSectionName!, incomingEvent, incoming: true);
        List<RouteRecord> outgoingRoutes = nextStop is null || string.IsNullOrWhiteSpace(nextStop.IncomingSectionName)
            ? []
            : ResolveRoutes(instance, stop.StationName, nextStop.IncomingSectionName!, outgoingEvent, incoming: false);

        IReadOnlyList<string> ordinaryTracks = ResolvePreferredTracks(instance, stop, isStop);
        instance.TryGetBenchmarkTrackRestriction(
            stop.TrainIndex,
            stop.StationOrder,
            out BenchmarkTrackRestriction? benchmarkRestriction);
        BenchmarkTrackDomainResult benchmarkDomain = BenchmarkTrackDomainResolver.Resolve(
            ordinaryTracks,
            benchmarkRestriction?.AllowedTrackNames);
        IReadOnlyList<string> preferredTracks = benchmarkDomain.AllowedTracks;
        HashSet<string> feasibleTracks = ResolveFeasibleTracks(
            incomingRoutes,
            outgoingRoutes,
            preferredTracks,
            benchmarkDomain.HasBenchmarkRestriction);

        return new StationMicroRequirement(
            stop.StopId,
            stop.TrainIndex,
            stop.TrainNo,
            stop.StationOrder,
            stop.StationName,
            isStop,
            stop.StopRole,
            arrivalSeconds,
            departureSeconds,
            stop.IncomingSectionName,
            nextStop?.IncomingSectionName,
            incomingEvent,
            outgoingEvent,
            stop.StopRole != TrainStopRole.Origin,
            stop.StopRole != TrainStopRole.Destination && nextStop is not null,
            incomingRoutes,
            outgoingRoutes,
            preferredTracks,
            feasibleTracks.ToList(),
            benchmarkDomain.OrdinaryTracks,
            benchmarkDomain.BenchmarkTracks,
            benchmarkDomain.HasBenchmarkRestriction,
            stop.StopRequirement,
            StopModeDomainResolver.Analyze(instance, train, stop));
    }

    private static List<MicroConflictExplanation> CheckExactHeadways(
        TimetablingInstance instance,
        IReadOnlyList<StationMicroRequirement> requirements,
        ICollection<MicroConflictExplanation> routeChoiceDiagnostics)
    {
        List<StationMicroEvent> events = new();
        foreach (StationMicroRequirement requirement in requirements)
        {
            if (requirement.RequiresIncomingRoute && requirement.IncomingRoutes.Count > 0)
            {
                List<RouteRecord> feasibleIncomingRoutes = FilterRoutesByFeasibleTracks(
                    requirement.IncomingRoutes,
                    requirement.FeasibleTracks);
                events.Add(new StationMicroEvent(
                    requirement.StationName,
                    requirement.TrainIndex,
                    requirement.TrainNo,
                    requirement.StationOrder,
                    requirement.ArrivalSeconds,
                    requirement.IncomingEvent,
                    StationEventSide.Incoming,
                    requirement.IncomingSectionName ?? string.Empty,
                    feasibleIncomingRoutes,
                    requirement.DescribeIncomingRoutes()));
            }

            if (requirement.RequiresOutgoingRoute && requirement.OutgoingRoutes.Count > 0)
            {
                List<RouteRecord> feasibleOutgoingRoutes = FilterRoutesByFeasibleTracks(
                    requirement.OutgoingRoutes,
                    requirement.FeasibleTracks);
                events.Add(new StationMicroEvent(
                    requirement.StationName,
                    requirement.TrainIndex,
                    requirement.TrainNo,
                    requirement.StationOrder,
                    requirement.DepartureSeconds,
                    requirement.OutgoingEvent,
                    StationEventSide.Outgoing,
                    requirement.OutgoingSectionName ?? string.Empty,
                    feasibleOutgoingRoutes,
                    requirement.DescribeOutgoingRoutes()));
            }
        }

        List<MicroConflictExplanation> conflicts = new();
        List<StationMicroEvent> orderedEvents = events
            .OrderBy(item => item.EventTimeSeconds)
            .ThenBy(item => item.TrainIndex)
            .ToList();

        for (int firstIndex = 0; firstIndex < orderedEvents.Count; firstIndex++)
        {
            StationMicroEvent first = orderedEvents[firstIndex];
            for (int secondIndex = firstIndex + 1; secondIndex < orderedEvents.Count; secondIndex++)
            {
                StationMicroEvent second = orderedEvents[secondIndex];
                // Route headway tables separate trains, not one train's own events.
                if (first.TrainIndex == second.TrainIndex) continue;
                double realizedGap = second.EventTimeSeconds - first.EventTimeSeconds;
                RoutePairHeadwayClassification classification = RoutePairHeadwayClassifier.Classify(
                    first.CandidateRoutes,
                    second.CandidateRoutes,
                    realizedGap,
                    (leadingRouteId, followingRouteId) =>
                        instance.TryGetRouteHeadwayRule(leadingRouteId, followingRouteId, out RouteHeadwayRule? rule) && rule is not null
                            ? rule.MinHeadwaySeconds
                            : null);

                if (classification.Kind == RoutePairHeadwayClassificationKind.RouteChoiceRequired)
                {
                    routeChoiceDiagnostics.Add(new MicroConflictExplanation(
                        "route_choice_required",
                        first.StationName,
                        first.TrainIndex,
                        second.TrainIndex,
                        first.StationOrder,
                        first.EventTimeSeconds,
                        second.EventTimeSeconds,
                        $"{first.EventType}->{second.EventType}",
                        $"A concrete route choice is required for trains {first.TrainIndex} and {second.TrainIndex}.",
                        $"realized_gap={realizedGap.ToString("F3", CultureInfo.InvariantCulture)}, recorded_pairs={classification.RecordedPairCount}, missing_pairs={classification.MissingPairCount}, first_routes={first.RouteEvidence}, second_routes={second.RouteEvidence}"));
                    continue;
                }

                if (classification.Kind == RoutePairHeadwayClassificationKind.UnavoidableConflict)
                {
                    conflicts.Add(new MicroConflictExplanation(
                        "exact_headway_infeasible",
                        first.StationName,
                        first.TrainIndex,
                        second.TrainIndex,
                        first.StationOrder,
                        first.EventTimeSeconds,
                        second.EventTimeSeconds,
                        $"{first.EventType}->{second.EventType}",
                        $"Exact station headway is infeasible between train {first.TrainIndex} and train {second.TrainIndex}.",
                        $"realized_gap={realizedGap.ToString("F3", CultureInfo.InvariantCulture)}, required_gap={classification.MinimumRecordedHeadwaySeconds}, recorded_pairs={classification.RecordedPairCount}, missing_pairs={classification.MissingPairCount}, first_routes={first.RouteEvidence}, second_routes={second.RouteEvidence}",
                        second.StationOrder,
                        first.EventSide,
                        second.EventSide,
                        first.SectionName,
                        second.SectionName,
                        RealizedGapSeconds: realizedGap,
                        RequiredGapSeconds: classification.MinimumRecordedHeadwaySeconds,
                        LeadingRouteIds: first.CandidateRoutes.Select(route => route.RouteId).ToArray(),
                        FollowingRouteIds: second.CandidateRoutes.Select(route => route.RouteId).ToArray(),
                        RecordedRoutePairCount: classification.RecordedPairCount,
                        MissingRoutePairCount: classification.MissingPairCount));
                }
            }
        }

        return conflicts;
    }

    private static List<RouteRecord> FilterRoutesByFeasibleTracks(
        IReadOnlyList<RouteRecord> routes,
        IReadOnlyList<string> feasibleTracks)
    {
        HashSet<string> trackDomain = feasibleTracks.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return routes.Where(route => trackDomain.Contains(route.TrackName)).ToList();
    }

    private static MicroConflictExplanation? CheckTrackAssignment(IReadOnlyList<StationMicroRequirement> requirements)
    {
        List<StationTrackOccupation> occupations = requirements
            .Where(requirement => requirement.IsStop && requirement.FeasibleTracks.Count > 0)
            .Select(requirement => new StationTrackOccupation(
                requirement.TrainIndex,
                requirement.TrainNo,
                requirement.StationOrder,
                requirement.StationName,
                requirement.ArrivalSeconds,
                Math.Max(requirement.DepartureSeconds, requirement.ArrivalSeconds),
                requirement.FeasibleTracks))
            .OrderBy(occupation => occupation.FeasibleTracks.Count)
            .ThenBy(occupation => occupation.StartSeconds)
            .ThenBy(occupation => occupation.EndSeconds)
            .ToList();

        if (occupations.Count <= 1)
        {
            return null;
        }

        Dictionary<string, List<StationTrackOccupation>> assignments = new(StringComparer.OrdinalIgnoreCase);
        if (TryAssignTracks(occupations, 0, assignments))
        {
            return null;
        }

        StationTrackOccupation bottleneck = occupations
            .OrderBy(occupation => occupation.FeasibleTracks.Count)
            .ThenByDescending(occupation => occupations.Count(other =>
                other.TrainIndex != occupation.TrainIndex &&
                other.StartSeconds < occupation.EndSeconds - ToleranceSeconds &&
                other.EndSeconds > occupation.StartSeconds + ToleranceSeconds &&
                other.FeasibleTracks.Intersect(occupation.FeasibleTracks, StringComparer.OrdinalIgnoreCase).Any()))
            .First();

        string overlapEvidence = string.Join(
            "; ",
            occupations
                .Where(other =>
                    other.TrainIndex != bottleneck.TrainIndex &&
                    other.StartSeconds < bottleneck.EndSeconds - ToleranceSeconds &&
                    other.EndSeconds > bottleneck.StartSeconds + ToleranceSeconds &&
                    other.FeasibleTracks.Intersect(bottleneck.FeasibleTracks, StringComparer.OrdinalIgnoreCase).Any())
                .Select(other => $"train={other.TrainIndex},window=[{other.StartSeconds.ToString("F3", CultureInfo.InvariantCulture)},{other.EndSeconds.ToString("F3", CultureInfo.InvariantCulture)}],tracks={string.Join('|', other.FeasibleTracks)}"));

        return new MicroConflictExplanation(
            "track_assignment_infeasible",
            bottleneck.StationName,
            bottleneck.TrainIndex,
            null,
            bottleneck.StationOrder,
            bottleneck.StartSeconds,
            bottleneck.EndSeconds,
            "track_assignment",
            $"No feasible station-track assignment exists for stopping occupations at station {bottleneck.StationName}.",
            $"bottleneck_train={bottleneck.TrainIndex}, feasible_tracks={string.Join('|', bottleneck.FeasibleTracks)}, overlaps={overlapEvidence}");
    }

    private static bool TryAssignTracks(
        IReadOnlyList<StationTrackOccupation> occupations,
        int index,
        IDictionary<string, List<StationTrackOccupation>> assignments)
    {
        if (index >= occupations.Count)
        {
            return true;
        }

        StationTrackOccupation occupation = occupations[index];
        foreach (string trackName in occupation.FeasibleTracks.OrderBy(track => track, StringComparer.OrdinalIgnoreCase))
        {
            if (!assignments.TryGetValue(trackName, out List<StationTrackOccupation>? assigned))
            {
                assigned = new List<StationTrackOccupation>();
                assignments[trackName] = assigned;
            }

            if (assigned.Any(other =>
                other.StartSeconds < occupation.EndSeconds - ToleranceSeconds &&
                other.EndSeconds > occupation.StartSeconds + ToleranceSeconds))
            {
                continue;
            }

            assigned.Add(occupation);
            if (TryAssignTracks(occupations, index + 1, assignments))
            {
                return true;
            }

            assigned.RemoveAt(assigned.Count - 1);
        }

        return false;
    }

    private static MicroConflictExplanation CreateRouteConflict(
        string conflictKind,
        StationMicroRequirement requirement,
        double eventTimeSeconds,
        string resourceKey,
        string detail)
    {
        RouteTrackModeCapability? selectedCapability = requirement.StopModeAnalysis is null
            ? null
            : requirement.IsStop
                ? requirement.StopModeAnalysis.Stopping
                : requirement.StopModeAnalysis.Passing;
        return new MicroConflictExplanation(
            conflictKind,
            requirement.StationName,
            requirement.TrainIndex,
            null,
            requirement.StationOrder,
            eventTimeSeconds,
            eventTimeSeconds,
            resourceKey,
            detail,
            $"preferred_tracks={string.Join('|', requirement.PreferredTracks)}, feasible_tracks={string.Join('|', requirement.FeasibleTracks)}",
            RootCause: selectedCapability?.FailureReason,
            RootConflictKey: BuildRouteDomainRootKey(requirement),
            RecommendedAction: ResolveRecommendedRouteDomainAction(requirement));
    }

    private static string BuildRouteDomainRootKey(StationMicroRequirement requirement)
    {
        return $"route_domain::{requirement.StationName}::{requirement.TrainIndex}::{requirement.StationOrder}";
    }

    private static string ResolveRecommendedRouteDomainAction(StationMicroRequirement requirement)
    {
        if (requirement.StopModeAnalysis is null)
        {
            return "inspect_route_track_domain";
        }

        bool alternativeFeasible = requirement.IsStop
            ? requirement.StopModeAnalysis.Passing.IsFeasible
            : requirement.StopModeAnalysis.Stopping.IsFeasible;
        if (requirement.StopRequirement == StopRequirement.OptionalStop && alternativeFeasible)
        {
            return requirement.IsStop ? "force_pass_if_optional" : "force_stop_if_optional";
        }

        return "validate_hard_requirement_or_input_data";
    }

    private static List<RouteRecord> ResolveRoutes(
        TimetablingInstance instance,
        string stationName,
        string sectionName,
        MovementEvent movementEvent, bool incoming)
    {
        return instance.Routes
            .Where(route =>
                string.Equals(route.StationName, stationName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(route.SectionName, sectionName, StringComparison.OrdinalIgnoreCase) &&
                route.MovementEvent == movementEvent && RouteEventDirectionPolicy.Matches(route, incoming))
            .ToList();
    }

    private static IReadOnlyList<string> ResolvePreferredTracks(TimetablingInstance instance, TrainStopRecord stop, bool isStop)
    {
        if (instance.TryGetTrackChoiceRecord(stop.TrainIndex, stop.StationOrder, isStop, out TrainTrackChoiceRecord? record) && record is not null)
        {
            return record.AllowedTrackNames;
        }

        return Array.Empty<string>();
    }

    private static HashSet<string> ResolveFeasibleTracks(
        IReadOnlyList<RouteRecord> incomingRoutes,
        IReadOnlyList<RouteRecord> outgoingRoutes,
        IReadOnlyList<string> preferredTracks,
        bool enforcePreferredDomain = false)
    {
        HashSet<string>? domain = null;

        if (incomingRoutes.Count > 0)
        {
            domain = incomingRoutes
                .Select(route => route.TrackName)
                .Where(track => !string.IsNullOrWhiteSpace(track))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        if (outgoingRoutes.Count > 0)
        {
            HashSet<string> outgoingDomain = outgoingRoutes
                .Select(route => route.TrackName)
                .Where(track => !string.IsNullOrWhiteSpace(track))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            domain = domain is null
                ? outgoingDomain
                : domain.Intersect(outgoingDomain, StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        if (domain is null)
        {
            domain = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        if (preferredTracks.Count > 0)
        {
            if (domain.Count == 0)
            {
                domain = preferredTracks.ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                domain.IntersectWith(preferredTracks);
            }
        }
        else if (enforcePreferredDomain)
        {
            domain.Clear();
        }

        return domain;
    }

    private static int? ResolveRequiredExactHeadway(
        TimetablingInstance instance,
        StationMicroEvent first,
        StationMicroEvent second)
    {
        return RouteHeadwayResolver.ResolveMinimumRecordedHeadway(
            first.CandidateRoutes,
            second.CandidateRoutes,
            (leadingRouteId, followingRouteId) =>
                instance.TryGetRouteHeadwayRule(leadingRouteId, followingRouteId, out RouteHeadwayRule? rule) && rule is not null
                    ? rule.MinHeadwaySeconds
                    : null);
    }

    private static double Value(GRBVar variable)
    {
        return variable.Get(GRB.DoubleAttr.X);
    }

    private static int BinaryValue(GRBVar variable)
    {
        return Value(variable) >= 0.5 ? 1 : 0;
    }
}

public sealed record MicroFeasibilitySlaveRequest(
    IReadOnlySet<string>? StationNames = null,Func<double>? RemainingSeconds=null,IReadOnlyList<VerifiedMicroAssignmentWitness>? FixedAssignments=null);

internal sealed record StationLevelMicroFeasibilitySlaveResult(
    bool IsFeasible,
    IReadOnlyList<StationLevelMicroSubproblemResult> StationResults,
    IReadOnlyList<MicroConflictExplanation> Conflicts,
    IReadOnlyList<MicroConflictExplanation> RouteChoiceDiagnostics,
    MicroSolveStatus Status = MicroSolveStatus.ProvenInfeasible);

internal sealed record StationLevelMicroSubproblemResult(
    string StationName,
    IReadOnlyList<StationMicroRequirement> Requirements,
    StationConflictCore ConflictCore,
    bool IsFeasible,
    IReadOnlyList<MicroConflictExplanation> Conflicts,
    IReadOnlyList<MicroConflictExplanation> RouteChoiceDiagnostics,
    StationRouteTrackAssignmentResult AssignmentResult);

internal sealed record StationConflictCore(
    string StationName,
    IReadOnlyList<int> TrainIndices,
    IReadOnlyList<string> SupportPrecedenceKeys,
    IReadOnlyList<string> SupportStopIds,
    string PrimaryConflictKind,
    int ConflictCount);
