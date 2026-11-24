// 基础模型构建器把标准化实例转换为 Gurobi MILP：以连续时间变量和二元停站、径路、先后变量表达运行图决策。
// 构建过程叠加时窗、停时、区间运行、天窗、配额、追踪间隔与越行约束，并用复合键字典保存变量供修复模型复用。
using Gurobi;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

/// <summary>
/// 构建当前阶段的基础宏观 MILP。
/// 这版实现的重点已经从“只有基础时间约束”推进到：
/// 1. 基于 route family 代理间隔的区间级 headway；
/// 2. 基于顺序结构的初始候选冲突图；
/// 3. 仅对稀疏候选对建立 precedence 二元变量。
///
/// 需要特别说明：
/// 1. 这里的区间间隔不是精确进路间隔，而是由站内精确进路对压缩得到的中观代理值；
/// 2. 初始冲突图不是完备冲突图，后续仍需要 checker 动态补边；
/// 3. 当前模型仍属于 macro master，而不是完整的 master-slave-checker 框架。
/// </summary>
public sealed class BasicMilpModelBuilder
{
    private readonly TimetablingInstance _instance;
    private readonly int? _neighborRadius;
    private readonly IReadOnlyList<LocalPrecedenceNogoodCut> _injectedNogoodCuts;
    private readonly PrecedenceTrustRegion? _precedenceTrustRegion;
    private readonly IReadOnlyList<SectionConflictEdgeRequest> _additionalSectionEdges;
    private readonly IReadOnlyList<StationEventConflictCandidate> _stationEventEdges;
    private readonly RunningTimeSupplementPolicy _runningTimeSupplementPolicy;
    private readonly IReadOnlyList<PromotedMicroComponent> _promotedMicroComponents;
    private readonly int _endpointOccupationSeconds;
    private readonly List<ConditionalBigMAuditRow> _conditionalBigMAuditRows = [];
    private HardEventBoundSet _hardEventBounds = HardEventBoundSet.Empty;
    private readonly ServiceWindowPolicy _serviceWindows;
    private readonly bool _screenTemporalPrecedence;
    private readonly bool _preserveOmittedOvertakingSupport;
    private readonly bool _boundTimingOuterRelaxation;
    private readonly List<TemporalPrecedenceAudit> _temporalAudit = [];

    public BasicMilpModelBuilder(
        TimetablingInstance instance,
        int? neighborRadius = null,
        IReadOnlyList<LocalPrecedenceNogoodCut>? injectedNogoodCuts = null,
        PrecedenceTrustRegion? precedenceTrustRegion = null,
        IReadOnlyList<SectionConflictEdgeRequest>? additionalSectionEdges = null,
        IReadOnlyList<StationEventConflictCandidate>? stationEventEdges = null,
        double runningTimeSupplementRatio = 0.05,
        IReadOnlyList<PromotedMicroComponent>? promotedMicroComponents = null,
        int endpointOccupationSeconds = StationEndpointOccupationPolicy.DefaultSeconds,
        ServiceWindowPolicy? serviceWindows = null,
        bool screenTemporalPrecedence = false,
        bool preserveOmittedOvertakingSupport = false,
        bool boundTimingOuterRelaxation = false)
    {
        _instance = instance;
        _boundTimingOuterRelaxation = boundTimingOuterRelaxation;
        if (boundTimingOuterRelaxation && (screenTemporalPrecedence || serviceWindows?.Windows.Count > 0 ||
            injectedNogoodCuts?.Count > 0 || precedenceTrustRegion is not null ||
            promotedMicroComponents?.Count > 0 || stationEventEdges?.Count > 0))
            throw new ArgumentException("Bound outer relaxation forbids local cuts, exact screening and prebuilt micro rows.");
        _serviceWindows = serviceWindows ?? ServiceWindowPolicy.Disabled;
        _screenTemporalPrecedence = screenTemporalPrecedence;
        _preserveOmittedOvertakingSupport = preserveOmittedOvertakingSupport;
        _neighborRadius = neighborRadius;
        _injectedNogoodCuts = injectedNogoodCuts ?? Array.Empty<LocalPrecedenceNogoodCut>();
        _precedenceTrustRegion = precedenceTrustRegion;
        _additionalSectionEdges = additionalSectionEdges ?? Array.Empty<SectionConflictEdgeRequest>();
        _stationEventEdges = stationEventEdges ?? Array.Empty<StationEventConflictCandidate>();
        _runningTimeSupplementPolicy = RunningTimeSupplementPolicy.Create(runningTimeSupplementRatio);
        _promotedMicroComponents = promotedMicroComponents?.Where(item => item.Active).ToList() ?? [];
        _endpointOccupationSeconds = endpointOccupationSeconds >= 0
            ? endpointOccupationSeconds
            : throw new ArgumentOutOfRangeException(nameof(endpointOccupationSeconds));
    }

    /// <summary>Validate hard input domains and construct the native timetable model.</summary>
    /// <returns>The model owning its native environment and variables.</returns>
    /// <exception cref="InvalidDataException">The input contains contradictory hard bounds, modes or quotas.</exception>
    public BasicMilpModel Build()
    {
        _conditionalBigMAuditRows.Clear();
        _temporalAudit.Clear();
        StopModePreprocessingResult stopModePreprocessing = StopModePreprocessor.Analyze(_instance);
        _hardEventBounds = HardEventBoundPropagator.Build(_instance,
            runningTimeSupplementRatio: _runningTimeSupplementPolicy.Ratio);
        StationQuotaPreflight.Validate(_instance, stopModePreprocessing, _hardEventBounds);
        if (stopModePreprocessing.Contradictions.Count > 0)
        {
            string evidence = string.Join(
                "; ",
                stopModePreprocessing.Contradictions.Take(20).Select(decision =>
                    $"{decision.StopId}:{decision.StopRequirement}:{decision.Analysis.Evidence}"));
            throw new InvalidDataException($"Stop-mode preprocessing found static route-track contradictions: {evidence}");
        }

        if (_hardEventBounds.Contradictions.Count > 0)
            throw new InvalidDataException(
                "Original hard event bounds are contradictory: " +
                string.Join(" | ", _hardEventBounds.Contradictions.Take(20)));

        GRBEnv environment = new();
        GRBModel model = new(environment)
        {
            ModelName = $"basic_milp_{_instance.Meta.InstanceName}"
        };

        Dictionary<string, GRBVar> arrivalVars = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, GRBVar> departureVars = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, GRBVar> stopVars = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, TrainStopRecord> stopById = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<(int TrainIndex, int StationOrder), TrainStopRecord> stopByTrainAndOrder = new();
        Dictionary<string, GRBVar> sectionPrecedenceVars = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, GRBVar> stationEventPrecedenceVars = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, GRBVar> runningTimeSupplementVars = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, GRBVar> beingOvertakenVars = new(StringComparer.OrdinalIgnoreCase);
        List<OvertakingReversalVariableBinding> overtakingReversalBindings = [];
        List<MaintenanceWindowBinaryBinding> maintenanceWindowBinaryBindings = [];
        List<StationQuotaCategoryBinding> stationQuotaCategoryBindings = [];

        foreach (TrainRecord train in _instance.Trains)
        {
            foreach (TrainStopRecord stop in train.OrderedStops)
            {
                string arrName = $"arr_t{stop.TrainIndex}_s{stop.StationOrder}";
                string depName = $"dep_t{stop.TrainIndex}_s{stop.StationOrder}";
                string stopName = $"stop_t{stop.TrainIndex}_s{stop.StationOrder}";

                HardEventBound arrivalBound = _hardEventBounds.Arrival(stop.StopId);
                HardEventBound departureBound = _hardEventBounds.Departure(stop.StopId);
                arrivalVars[stop.StopId] = model.AddVar(arrivalBound.Lower, arrivalBound.Upper, 0.0, GRB.CONTINUOUS, arrName);
                departureVars[stop.StopId] = model.AddVar(departureBound.Lower, departureBound.Upper, 0.0, GRB.CONTINUOUS, depName);
                StopVariableBounds stopBounds = StopModeMasterBoundsResolver.Resolve(stop.StopId, stopModePreprocessing);
                stopVars[stop.StopId] = model.AddVar(
                    stopBounds.LowerBound,
                    stopBounds.UpperBound,
                    0.0,
                    GRB.BINARY,
                    stopName);

                stopById[stop.StopId] = stop;
                stopByTrainAndOrder[(stop.TrainIndex, stop.StationOrder)] = stop;
            }

            AddPerTrainConstraints(
                model, train.OrderedStops, arrivalVars, departureVars, stopVars, runningTimeSupplementVars);
        }

        StopPairProductRegistry stopPairProducts = new(model, stopVars);

        AddMaintenanceWindowConstraints(model, arrivalVars, departureVars, maintenanceWindowBinaryBindings);
        TrainSectionStopQuotaConstraintBuilder.Apply(model, _instance, stopVars);
        AddStationStopQuotaConstraints(
            model, arrivalVars, departureVars, stopVars, stopById, stationQuotaCategoryBindings,
            stopModePreprocessing.FixedStopValuesByStopId);
        AddTrainSectionTravelTimeLimitConstraints(model, arrivalVars, departureVars);
        IReadOnlyList<SectionConflictCandidate> sectionConflictCandidates =
            AddSectionPrecedenceAndHeadwayConstraints(model, arrivalVars, departureVars, stopVars, stopByTrainAndOrder, sectionPrecedenceVars, stopPairProducts);
        if (!_boundTimingOuterRelaxation)
        {
            AddStationCapabilityOvertakingConstraints(model, sectionConflictCandidates, sectionPrecedenceVars, stopModePreprocessing);
            OvertakingDwellConstraintBuilder.Apply(
                model,
                _instance,
                sectionConflictCandidates,
                sectionPrecedenceVars,
                arrivalVars,
                departureVars,
                stopVars,
                beingOvertakenVars,
                overtakingReversalBindings,
                _preserveOmittedOvertakingSupport);
        }
        else
        {
            // Conservative outer projection: near-tied endpoint orders need not share
            // one binary. Omit overtaking/order coupling, allow the original waiting
            // maximum at any stop. This is only a bound model, never a primal solver.
            foreach (var stop in _instance.TrainStops.Where(s => s.StopRole == TrainStopRole.Intermediate))
            {
                var normal = NormalDwellTimeResolver.Resolve(_instance.NormalDwellTimeRules, stop);
                int maximum = Math.Max(stop.MaxDwellSeconds, normal.NormalMaxDwellSeconds);
                model.AddConstr(departureVars[stop.StopId] - arrivalVars[stop.StopId] - maximum * stopVars[stop.StopId],
                    GRB.LESS_EQUAL, 0, $"max_dwell_t{stop.TrainIndex}_s{stop.StationOrder}");
            }
        }
        AddStationEventHeadwayConstraints(
            model, arrivalVars, departureVars, stopVars, stopByTrainAndOrder, stationEventPrecedenceVars, stopPairProducts);
        AddInjectedNogoodCuts(model, sectionPrecedenceVars, stopVars, stationEventPrecedenceVars);
        AddPrecedenceTrustRegionConstraints(model, sectionPrecedenceVars);

        GRBVar firstDeparture = model.AddVar(0, 86399, 0.0, GRB.CONTINUOUS, "global_first_departure");
        GRBVar lastArrival = model.AddVar(0, 86399, 0.0, GRB.CONTINUOUS, "global_last_arrival");

        AddGlobalSpanConstraints(model, firstDeparture, lastArrival, arrivalVars, departureVars);
        SetObjective(model, firstDeparture, lastArrival, runningTimeSupplementVars.Values);
        ServiceWindowModelBuilder.Apply(model, arrivalVars, departureVars, _serviceWindows);
        BasicMilpModel result = new(
            environment,
            model,
            _instance,
            arrivalVars,
            departureVars,
            stopVars,
            sectionConflictCandidates,
            sectionPrecedenceVars,
            _stationEventEdges,
            stationEventPrecedenceVars,
            runningTimeSupplementVars,
            _runningTimeSupplementPolicy.Ratio,
            beingOvertakenVars,
            _promotedMicroComponents,
            stopPairProducts.Bindings,
            overtakingReversalBindings,
            maintenanceWindowBinaryBindings,
            stationQuotaCategoryBindings,
            _hardEventBounds,
            _conditionalBigMAuditRows.ToArray());
        result.ServiceWindows = _serviceWindows;
        result.TemporalPrecedenceAudit = _temporalAudit.ToArray();
        foreach (PromotedMicroComponent component in _promotedMicroComponents)
        {
            StationMicroRepairAugmentation augmentation = PromotedMicroComponentModelAugmenter.Apply(
                result,
                component,
                _endpointOccupationSeconds);
            result.PromotedMicroAlternativeVariableCount += augmentation.AlternativeCount;
            result.PromotedMicroOrderVariableCount += augmentation.OrderVariableCount;
            result.PromotedMicroLegacyOrderVariableCount += augmentation.LegacyOrderVariableCount;
            result.PromotedMicroBoundPrunedRelationCount += augmentation.BoundPrunedRelationCount;
            result.PromotedMicroConditionalConstraintCount += augmentation.ConditionalConstraintCount;
            foreach (PromotedMicroChoiceVariableBinding binding in augmentation.ChoiceBindings)
            {
                result.PromotedMicroChoiceBindings.Add(binding);
            }
            foreach (PromotedMicroOrderVariableBinding binding in augmentation.OrderBindings)
            {
                result.PromotedMicroOrderBindings.Add(binding);
            }
            result.PromotedMicroBuildDiagnostics.Add(new PromotedMicroBuildDiagnostic(
                component.StationName,
                component.ComponentId,
                component.TrainIndices.Count,
                augmentation.AlternativeCount,
                augmentation.ConditionalConstraintCount,
                component.SourceCore,
                component.PromotionIteration,
                component.PersistentRounds,
                augmentation.OrderVariableCount,
                augmentation.LegacyOrderVariableCount));
        }
        model.Update();
        if (_boundTimingOuterRelaxation)
        {
            foreach (var row in model.GetConstrs())
            {
                string name = row.ConstrName;
                bool headway = name.StartsWith("headway_") || name.StartsWith("arrival_headway_");
                bool time = headway || name.StartsWith("min_dwell_") || name.StartsWith("max_dwell_") ||
                    name.StartsWith("mw_before_constr_") || name.StartsWith("mw_after_constr_") ||
                    name.StartsWith("quota_inwin_lb_") || name.StartsWith("quota_inwin_ub_") || name.StartsWith("travel_time_max_");
                if (!time) continue;
                double tolerance = TimetableTimePolicy.BusinessRelaxationSeconds;
                if (row.Sense == GRB.GREATER_EQUAL) row.RHS -= tolerance;
                else if (row.Sense == GRB.LESS_EQUAL) row.RHS += tolerance;
                else throw new InvalidOperationException("Unexpected time-row sense");
            }
            model.Update();
        }
        result.AppliedPrecedenceTrustRegion = _precedenceTrustRegion;
        result.IdentityAudit = GurobiModelIdentityAuditor.ValidateAndCapture(
            model,
            "basic_model_build");
        return result;
    }

    private void AddPerTrainConstraints(
        GRBModel model,
        IReadOnlyList<TrainStopRecord> stops,
        IReadOnlyDictionary<string, GRBVar> arrivalVars,
        IReadOnlyDictionary<string, GRBVar> departureVars,
        IReadOnlyDictionary<string, GRBVar> stopVars,
        IDictionary<string, GRBVar> runningTimeSupplementVars)
    {
        foreach (TrainStopRecord stop in stops)
        {
            GRBVar stopVar = stopVars[stop.StopId];
            GRBVar arrVar = arrivalVars[stop.StopId];
            GRBVar depVar = departureVars[stop.StopId];

            switch (stop.StopRequirement)
            {
                case StopRequirement.MustStop:
                    model.AddConstr(stopVar, GRB.EQUAL, 1.0, $"fix_stop_must_t{stop.TrainIndex}_s{stop.StationOrder}");
                    break;
                case StopRequirement.MustPass:
                    model.AddConstr(stopVar, GRB.EQUAL, 0.0, $"fix_pass_must_t{stop.TrainIndex}_s{stop.StationOrder}");
                    break;
            }

            if (stop.StopRole is TrainStopRole.Origin or TrainStopRole.Destination)
            {
                model.AddConstr(stopVar, GRB.EQUAL, 1.0, $"terminal_stop_t{stop.TrainIndex}_s{stop.StationOrder}");
            }

            if (stop.StopRole == TrainStopRole.Intermediate)
            {
                // 只有中间站同时具有有效到达与有效出发事件，才适用停站时间约束。
                // 始发站的到达时刻、终到站的出发时刻在数据中常用 -999 表示不存在；
                // 若对首末站套用 dwell 约束，会把合法的终端作业错误地建成不可行。
                GRBLinExpr minDwellExpr = new();
                minDwellExpr.AddTerm(1.0, depVar);
                minDwellExpr.AddTerm(-1.0, arrVar);
                NormalDwellResolution normalDwell = NormalDwellTimeResolver.Resolve(
                    _instance.NormalDwellTimeRules, stop);
                minDwellExpr.AddTerm(-normalDwell.NormalMinDwellSeconds, stopVar);
                model.AddConstr(minDwellExpr, GRB.GREATER_EQUAL, 0.0, $"min_dwell_t{stop.TrainIndex}_s{stop.StationOrder}");
            }
        }

        for (int index = 0; index < stops.Count - 1; index++)
        {
            TrainStopRecord currentStop = stops[index];
            TrainStopRecord nextStop = stops[index + 1];
            RunTimeRule runTimeRule = ResolveRunTimeRule(currentStop, nextStop);
            string supplementKey = RunningTimeSupplementPolicy.BuildKey(
                currentStop.TrainIndex,
                nextStop.IncomingSectionName ?? $"{currentStop.StationOrder}-{nextStop.StationOrder}");
            GRBVar supplement = model.AddVar(
                0.0,
                _runningTimeSupplementPolicy.ResolveMaximumSeconds(runTimeRule.RunTimeSeconds),
                0.0,
                GRB.CONTINUOUS,
                $"run_supp_t{currentStop.TrainIndex}_from{currentStop.StationOrder}_to{nextStop.StationOrder}");
            runningTimeSupplementVars.Add(supplementKey, supplement);

            GRBLinExpr runExpr = new();
            runExpr.AddTerm(1.0, arrivalVars[nextStop.StopId]);
            runExpr.AddTerm(-1.0, departureVars[currentStop.StopId]);
            runExpr.AddTerm(-runTimeRule.StartAddSeconds, stopVars[currentStop.StopId]);
            runExpr.AddTerm(-runTimeRule.StopAddSeconds, stopVars[nextStop.StopId]);
            runExpr.AddTerm(-1.0, supplement);
            if (_boundTimingOuterRelaxation)
                runExpr.AddTerm(-1, model.AddVar(0, 0, 0, GRB.CONTINUOUS,
                    $"bound_runtime_error_t{currentStop.TrainIndex}_s{currentStop.StationOrder}"));
            model.AddConstr(runExpr, GRB.EQUAL, runTimeRule.RunTimeSeconds,
                $"run_t{currentStop.TrainIndex}_from{currentStop.StationOrder}_to{nextStop.StationOrder}");
        }
    }

    private IReadOnlyList<SectionConflictCandidate> AddSectionPrecedenceAndHeadwayConstraints(
        GRBModel model,
        IReadOnlyDictionary<string, GRBVar> arrivalVars,
        IReadOnlyDictionary<string, GRBVar> departureVars,
        IReadOnlyDictionary<string, GRBVar> stopVars,
        IReadOnlyDictionary<(int TrainIndex, int StationOrder), TrainStopRecord> stopByTrainAndOrder,
        IDictionary<string, GRBVar> sectionPrecedenceVars,
        StopPairProductRegistry stopPairProducts)
    {
        InitialConflictGraphBuilder graphBuilder = new(_instance, _neighborRadius, _additionalSectionEdges);
        IReadOnlyList<SectionConflictCandidate> candidates = graphBuilder.Build();

        foreach (SectionConflictCandidate candidate in candidates)
        {
            if (!stopByTrainAndOrder.TryGetValue((candidate.FirstTrainIndex, candidate.FirstStopOrder), out TrainStopRecord? firstStartStop) ||
                !stopByTrainAndOrder.TryGetValue((candidate.SecondTrainIndex, candidate.SecondStopOrder), out TrainStopRecord? secondStartStop) ||
                !stopByTrainAndOrder.TryGetValue((candidate.FirstTrainIndex, candidate.FirstEndStopOrder), out TrainStopRecord? firstEndStop) ||
                !stopByTrainAndOrder.TryGetValue((candidate.SecondTrainIndex, candidate.SecondEndStopOrder), out TrainStopRecord? secondEndStop))
            {
                continue;
            }

            GRBVar firstDepVar = departureVars[firstStartStop.StopId];
            GRBVar secondDepVar = departureVars[secondStartStop.StopId];
            GRBVar firstStopVar = stopVars[firstStartStop.StopId];
            GRBVar secondStopVar = stopVars[secondStartStop.StopId];
            GRBVar firstEndStopVar = stopVars[firstEndStop.StopId];
            GRBVar secondEndStopVar = stopVars[secondEndStop.StopId];

            string baseName = BuildSectionConstraintBaseName(candidate);
            GRBVar precedenceVar = model.AddVar(0.0, 1.0, 0.0, GRB.BINARY, $"prec_{baseName}");
            sectionPrecedenceVars[BuildSectionPrecedenceKey(candidate)] = precedenceVar;

            // 四个组合变量分别表示：
            // DD: 前车停站-后车停站
            // DP: 前车停站-后车通过
            // PD: 前车通过-后车停站
            // PP: 前车通过-后车通过
            OrderedStopPairState originState = stopPairProducts.GetOrdered(
                firstStartStop.StopId, secondStartStop.StopId);

            HeadwayValues forwardHeadways = ResolveSectionHeadways(
                firstStartStop,
                secondStartStop,
                candidate.SectionName,
                true);
            GRBLinExpr forwardHeadwayExpr = forwardHeadways.Weighted(originState, -1.0);

            HeadwayValues reverseHeadways = ResolveSectionHeadways(
                secondStartStop,
                firstStartStop,
                candidate.SectionName,
                true);
            GRBLinExpr reverseHeadwayExpr = reverseHeadways.Weighted(
                stopPairProducts.GetOrdered(secondStartStop.StopId, firstStartStop.StopId), -1.0);

            double forwardDepartureM = AddLowerBoundBigMAudit(
                $"headway_fwd_{baseName}",
                _hardEventBounds.Departure(firstStartStop.StopId),
                _hardEventBounds.Departure(secondStartStop.StopId),
                forwardHeadways.Maximum);
            double reverseDepartureM = AddLowerBoundBigMAudit(
                $"headway_rev_{baseName}",
                _hardEventBounds.Departure(secondStartStop.StopId),
                _hardEventBounds.Departure(firstStartStop.StopId),
                reverseHeadways.Maximum);

            GRBLinExpr firstBeforeSecondExpr = new();
            firstBeforeSecondExpr.AddTerm(1.0, secondDepVar);
            firstBeforeSecondExpr.AddTerm(-1.0, firstDepVar);
            firstBeforeSecondExpr.AddTerm(forwardDepartureM, precedenceVar);
            firstBeforeSecondExpr.Add(forwardHeadwayExpr);
            model.AddConstr(firstBeforeSecondExpr, GRB.GREATER_EQUAL, 0.0, $"headway_fwd_{baseName}");

            GRBLinExpr secondBeforeFirstExpr = new();
            secondBeforeFirstExpr.AddTerm(1.0, firstDepVar);
            secondBeforeFirstExpr.AddTerm(-1.0, secondDepVar);
            secondBeforeFirstExpr.AddTerm(-reverseDepartureM, precedenceVar);
            secondBeforeFirstExpr.AddConstant(reverseDepartureM);
            secondBeforeFirstExpr.Add(reverseHeadwayExpr);
            model.AddConstr(secondBeforeFirstExpr, GRB.GREATER_EQUAL, 0.0, $"headway_rev_{baseName}");

            OrderedStopPairState arrivalState = stopPairProducts.GetOrdered(
                firstEndStop.StopId, secondEndStop.StopId);

            HeadwayValues forwardArrivalHeadways = ResolveSectionHeadways(
                firstEndStop, secondEndStop, candidate.SectionName, false);
            GRBLinExpr forwardArrivalHeadwayExpr = forwardArrivalHeadways.Weighted(arrivalState, -1.0);
            HeadwayValues reverseArrivalHeadways = ResolveSectionHeadways(
                secondEndStop, firstEndStop, candidate.SectionName, false);
            ScreenPrecedence(precedenceVar, BuildSectionPrecedenceKey(candidate),
                new(_hardEventBounds.Departure(firstStartStop.StopId), _hardEventBounds.Departure(secondStartStop.StopId),
                    forwardHeadways.Minimum, forwardHeadways.Maximum, reverseHeadways.Minimum, reverseHeadways.Maximum),
                new(_hardEventBounds.Arrival(firstEndStop.StopId), _hardEventBounds.Arrival(secondEndStop.StopId),
                    forwardArrivalHeadways.Minimum, forwardArrivalHeadways.Maximum, reverseArrivalHeadways.Minimum, reverseArrivalHeadways.Maximum));
            GRBLinExpr reverseArrivalHeadwayExpr = reverseArrivalHeadways.Weighted(
                stopPairProducts.GetOrdered(secondEndStop.StopId, firstEndStop.StopId), -1.0);
            double forwardArrivalM = AddLowerBoundBigMAudit(
                $"arrival_headway_fwd_{baseName}",
                _hardEventBounds.Arrival(firstEndStop.StopId),
                _hardEventBounds.Arrival(secondEndStop.StopId),
                forwardArrivalHeadways.Maximum);
            double reverseArrivalM = AddLowerBoundBigMAudit(
                $"arrival_headway_rev_{baseName}",
                _hardEventBounds.Arrival(secondEndStop.StopId),
                _hardEventBounds.Arrival(firstEndStop.StopId),
                reverseArrivalHeadways.Maximum);

            GRBVar arrivalOrder = _boundTimingOuterRelaxation
                ? model.AddVar(0, 1, 0, GRB.BINARY, $"bound_arrival_order_{baseName}") : precedenceVar;
            GRBLinExpr firstArrivesBeforeSecondExpr = new();
            firstArrivesBeforeSecondExpr.AddTerm(1.0, arrivalVars[secondEndStop.StopId]);
            firstArrivesBeforeSecondExpr.AddTerm(-1.0, arrivalVars[firstEndStop.StopId]);
            firstArrivesBeforeSecondExpr.AddTerm(forwardArrivalM, arrivalOrder);
            firstArrivesBeforeSecondExpr.Add(forwardArrivalHeadwayExpr);
            model.AddConstr(firstArrivesBeforeSecondExpr, GRB.GREATER_EQUAL, 0.0, $"arrival_headway_fwd_{baseName}");

            GRBLinExpr secondArrivesBeforeFirstExpr = new();
            secondArrivesBeforeFirstExpr.AddTerm(1.0, arrivalVars[firstEndStop.StopId]);
            secondArrivesBeforeFirstExpr.AddTerm(-1.0, arrivalVars[secondEndStop.StopId]);
            secondArrivesBeforeFirstExpr.AddTerm(-reverseArrivalM, arrivalOrder);
            secondArrivesBeforeFirstExpr.AddConstant(reverseArrivalM);
            secondArrivesBeforeFirstExpr.Add(reverseArrivalHeadwayExpr);
            model.AddConstr(secondArrivesBeforeFirstExpr, GRB.GREATER_EQUAL, 0.0, $"arrival_headway_rev_{baseName}");
        }

        return candidates;
    }

    public static string BuildSectionPrecedenceKey(SectionConflictCandidate candidate)
    {
        return $"{candidate.SectionName}::{candidate.FirstTrainIndex}::{candidate.SecondTrainIndex}";
    }

    public static string BuildSectionPrecedenceKey(string sectionName, int firstTrainIndex, int secondTrainIndex)
    {
        return $"{sectionName}::{firstTrainIndex}::{secondTrainIndex}";
    }

    internal static string BuildSectionConstraintBaseName(SectionConflictCandidate candidate) =>
        BuildSectionConstraintBaseName(
            candidate.SectionName,
            candidate.FirstTrainIndex,
            candidate.SecondTrainIndex);

    internal static string BuildSectionConstraintBaseName(SectionConflictEdgeRequest edge) =>
        BuildSectionConstraintBaseName(edge.SectionName, edge.FirstTrainIndex, edge.SecondTrainIndex);

    internal static string BuildSectionConstraintBaseName(
        string sectionName,
        int firstTrainIndex,
        int secondTrainIndex) =>
        $"sec_{Sanitize(sectionName)}_t{firstTrainIndex}_t{secondTrainIndex}";

    public static string BuildStationEventPrecedenceKey(StationEventConflictCandidate candidate)
    {
        return $"{candidate.StationName}::{candidate.FirstTrainIndex}::{candidate.FirstSide}::" +
               $"{candidate.SecondTrainIndex}::{candidate.SecondSide}";
    }

    internal static string BuildStationEventConstraintBaseName(StationEventConflictCandidate candidate) =>
        BuildStationEventConstraintBaseName(
            candidate.StationName,
            candidate.FirstTrainIndex,
            candidate.FirstSide,
            candidate.SecondTrainIndex,
            candidate.SecondSide);

    internal static string BuildStationEventConstraintBaseName(
        string stationName,
        int firstTrainIndex,
        StationEventSide firstSide,
        int secondTrainIndex,
        StationEventSide secondSide) =>
        $"sta_{Sanitize(stationName)}_t{firstTrainIndex}_{firstSide}_" +
        $"t{secondTrainIndex}_{secondSide}";

    private void AddStationEventHeadwayConstraints(
        GRBModel model,
        IReadOnlyDictionary<string, GRBVar> arrivalVars,
        IReadOnlyDictionary<string, GRBVar> departureVars,
        IReadOnlyDictionary<string, GRBVar> stopVars,
        IReadOnlyDictionary<(int TrainIndex, int StationOrder), TrainStopRecord> stopByTrainAndOrder,
        IDictionary<string, GRBVar> stationEventPrecedenceVars,
        StopPairProductRegistry stopPairProducts)
    {
        foreach (StationEventConflictCandidate candidate in _stationEventEdges.DistinctBy(BuildStationEventPrecedenceKey))
        {
            if (!stopByTrainAndOrder.TryGetValue((candidate.FirstTrainIndex, candidate.FirstStationOrder), out TrainStopRecord? firstStop) ||
                !stopByTrainAndOrder.TryGetValue((candidate.SecondTrainIndex, candidate.SecondStationOrder), out TrainStopRecord? secondStop))
            {
                continue;
            }

            stopByTrainAndOrder.TryGetValue((candidate.FirstTrainIndex, candidate.FirstStationOrder + 1), out TrainStopRecord? firstNextStop);
            stopByTrainAndOrder.TryGetValue((candidate.SecondTrainIndex, candidate.SecondStationOrder + 1), out TrainStopRecord? secondNextStop);
            string baseName = BuildStationEventConstraintBaseName(candidate);
            GRBVar precedenceVar = model.AddVar(0.0, 1.0, 0.0, GRB.BINARY, $"prec_{baseName}");
            stationEventPrecedenceVars[BuildStationEventPrecedenceKey(candidate)] = precedenceVar;

            OrderedStopPairState stationState = stopPairProducts.GetOrdered(firstStop.StopId, secondStop.StopId);

            HeadwayValues forwardHeadways = ResolveStationHeadways(
                firstStop, firstNextStop, candidate.FirstSide,
                secondStop, secondNextStop, candidate.SecondSide,
                true);
            GRBLinExpr forwardHeadway = forwardHeadways.Weighted(stationState, -1.0);
            HeadwayValues reverseHeadways = ResolveStationHeadways(
                secondStop, secondNextStop, candidate.SecondSide,
                firstStop, firstNextStop, candidate.FirstSide,
                true);
            GRBLinExpr reverseHeadway = reverseHeadways.Weighted(
                stopPairProducts.GetOrdered(secondStop.StopId, firstStop.StopId), -1.0);
            GRBVar firstTime = candidate.FirstSide == StationEventSide.Incoming
                ? arrivalVars[firstStop.StopId]
                : departureVars[firstStop.StopId];
            GRBVar secondTime = candidate.SecondSide == StationEventSide.Incoming
                ? arrivalVars[secondStop.StopId]
                : departureVars[secondStop.StopId];
            HardEventBound firstTimeBound = EventBound(firstStop.StopId, candidate.FirstSide);
            HardEventBound secondTimeBound = EventBound(secondStop.StopId, candidate.SecondSide);
            ScreenPrecedence(precedenceVar, BuildStationEventPrecedenceKey(candidate),
                new TemporalRelationRow(firstTimeBound, secondTimeBound,
                    forwardHeadways.Minimum, forwardHeadways.Maximum, reverseHeadways.Minimum, reverseHeadways.Maximum));
            double forwardM = AddLowerBoundBigMAudit(
                $"station_headway_fwd_{baseName}", firstTimeBound, secondTimeBound, forwardHeadways.Maximum);
            double reverseM = AddLowerBoundBigMAudit(
                $"station_headway_rev_{baseName}", secondTimeBound, firstTimeBound, reverseHeadways.Maximum);

            GRBLinExpr forwardExpr = secondTime - firstTime + forwardM * precedenceVar;
            forwardExpr.Add(forwardHeadway);
            model.AddConstr(forwardExpr, GRB.GREATER_EQUAL, 0.0, $"station_headway_fwd_{baseName}");

            GRBLinExpr reverseExpr = firstTime - secondTime - reverseM * precedenceVar + reverseM;
            reverseExpr.Add(reverseHeadway);
            model.AddConstr(reverseExpr, GRB.GREATER_EQUAL, 0.0, $"station_headway_rev_{baseName}");
        }
    }

    private HeadwayValues ResolveStationHeadways(
        TrainStopRecord leadingStop,
        TrainStopRecord? leadingNextStop,
        StationEventSide leadingSide,
        TrainStopRecord followingStop,
        TrainStopRecord? followingNextStop,
        StationEventSide followingSide,
        bool _)
    {
        int ss = MasterRouteDomainHeadwayProvider.ResolveStationLowerBound(
            _instance, leadingStop, leadingNextStop, leadingSide, true,
            followingStop, followingNextStop, followingSide, true);
        int sp = MasterRouteDomainHeadwayProvider.ResolveStationLowerBound(
            _instance, leadingStop, leadingNextStop, leadingSide, true,
            followingStop, followingNextStop, followingSide, false);
        int ps = MasterRouteDomainHeadwayProvider.ResolveStationLowerBound(
            _instance, leadingStop, leadingNextStop, leadingSide, false,
            followingStop, followingNextStop, followingSide, true);
        int pp = MasterRouteDomainHeadwayProvider.ResolveStationLowerBound(
            _instance, leadingStop, leadingNextStop, leadingSide, false,
            followingStop, followingNextStop, followingSide, false);

        return new HeadwayValues(ss, sp, ps, pp);
    }

    /// <summary>Restrict section-order changes to directions with legal waiting and overtaking tracks.</summary>
    /// <param name="model">Borrowed native model to receive the order constraints.</param>
    /// <param name="candidates">Modeled same-direction section relations.</param>
    /// <param name="sectionPrecedenceVars">Order variables keyed by section and canonical train pair.</param>
    /// <param name="preprocessing">Precomputed legal stop and pass domains by visit.</param>
    private void AddStationCapabilityOvertakingConstraints(
        GRBModel model,
        IReadOnlyList<SectionConflictCandidate> candidates,
        IReadOnlyDictionary<string, GRBVar> sectionPrecedenceVars,
        StopModePreprocessingResult preprocessing)
    {
        var domainsMap = preprocessing.Decisions.ToDictionary(decision => decision.StopId,
            decision => decision.Analysis, StringComparer.OrdinalIgnoreCase);
        foreach (var projection in OvertakingStopProjectionPlanner.Plan(_instance, candidates))
        {
            TrainStopRecord firstVisit = _instance.StopsById[projection.FirstStopId];
            TrainStopRecord secondVisit = _instance.StopsById[projection.SecondStopId];
            StopModeDomainAnalysis firstDomain = domainsMap[firstVisit.StopId];
            StopModeDomainAnalysis secondDomain = domainsMap[secondVisit.StopId];
            bool firstCanWait = StationOvertakingResourcePolicy.CanOvertake(firstVisit, secondVisit, firstDomain, secondDomain);
            bool secondCanWait = StationOvertakingResourcePolicy.CanOvertake(secondVisit, firstVisit, secondDomain, firstDomain);
            if (firstCanWait && secondCanWait) continue;

            // Order zero places the first train ahead. Forbid its wait by requiring
            // incoming - outgoing >= 0, and the other train's wait by requiring <= 0.
            char sense = (firstCanWait, secondCanWait) switch
            {
                (false, false) => GRB.EQUAL,
                (false, true) => GRB.GREATER_EQUAL,
                _ => GRB.LESS_EQUAL
            };
            model.AddConstr(
                sectionPrecedenceVars[projection.IncomingPrecedenceKey] - sectionPrecedenceVars[projection.OutgoingPrecedenceKey],
                sense, 0.0,
                $"no_overtake_{Sanitize(projection.StationName)}_{Sanitize(firstVisit.IncomingSectionName!)}_t{firstVisit.TrainIndex}_t{secondVisit.TrainIndex}");
        }
    }



    private HeadwayValues ResolveSectionHeadways(
        TrainStopRecord leadingStop,
        TrainStopRecord followingStop,
        string sectionName,
        bool isOriginSide)
    {
        // 区间 headway 发生在区间起点侧，查表只需要起点站的出发/通过事件。
        // 起点站可能是列车始发站，此时 IncomingSectionName 为空是合法情况，不能据此判为数据错误。

        int ddHeadway = MasterRouteDomainHeadwayProvider.ResolveLowerBound(
            _instance, leadingStop, true, followingStop, true, sectionName, isOriginSide);
        int dpHeadway = MasterRouteDomainHeadwayProvider.ResolveLowerBound(
            _instance, leadingStop, true, followingStop, false, sectionName, isOriginSide);
        int pdHeadway = MasterRouteDomainHeadwayProvider.ResolveLowerBound(
            _instance, leadingStop, false, followingStop, true, sectionName, isOriginSide);
        int ppHeadway = MasterRouteDomainHeadwayProvider.ResolveLowerBound(
            _instance, leadingStop, false, followingStop, false, sectionName, isOriginSide);

        return new HeadwayValues(ddHeadway, dpHeadway, pdHeadway, ppHeadway);
    }

    private HardEventBound EventBound(string stopId, StationEventSide side) =>
        side == StationEventSide.Incoming
            ? _hardEventBounds.Arrival(stopId)
            : _hardEventBounds.Departure(stopId);

    private double AddLowerBoundBigMAudit(
        string constraintName,
        HardEventBound earlier,
        HardEventBound later,
        double maximumRequiredSeparation)
    {
        double value = ConditionalBigMCalculator.ForLowerBound(
            earlier,
            later,
            maximumRequiredSeparation);
        _conditionalBigMAuditRows.Add(new ConditionalBigMAuditRow(
            constraintName,
            value,
            $"max(0,{maximumRequiredSeparation}+{earlier.Upper}-{later.Lower})",
            earlier.UpperSources.Concat(later.LowerSources).Distinct(StringComparer.Ordinal).ToArray()));
        return value;
    }

    private double AddRelaxUpperBigMAudit(
        string constraintName,
        HardEventBound bound,
        double activeUpper)
    {
        double value = ConditionalBigMCalculator.RelaxUpperBound(bound, activeUpper);
        _conditionalBigMAuditRows.Add(new ConditionalBigMAuditRow(
            constraintName,
            value,
            $"max(0,{bound.Upper}-{activeUpper})",
            bound.UpperSources.ToArray()));
        return value;
    }

    private double AddRelaxLowerBigMAudit(
        string constraintName,
        HardEventBound bound,
        double activeLower)
    {
        double value = ConditionalBigMCalculator.RelaxLowerBound(bound, activeLower);
        _conditionalBigMAuditRows.Add(new ConditionalBigMAuditRow(
            constraintName,
            value,
            $"max(0,{activeLower}-{bound.Lower})",
            bound.LowerSources.ToArray()));
        return value;
    }

    private readonly record struct HeadwayValues(double SS, double SP, double PS, double PP)
    {
        internal double Minimum => Math.Min(Math.Min(SS, SP), Math.Min(PS, PP));
        internal double Maximum => Math.Max(Math.Max(SS, SP), Math.Max(PS, PP));

        internal GRBLinExpr Weighted(OrderedStopPairState state, double coefficientSign) =>
            state.Weighted(
                coefficientSign * SS,
                coefficientSign * SP,
                coefficientSign * PS,
                coefficientSign * PP);
    }

    private void ScreenPrecedence(GRBVar variable, string key, params TemporalRelationRow[] rows)
    {
        if (!_screenTemporalPrecedence) return;
        TemporalRelationDecision decision = TemporalConflictGraphBuilder.Classify(rows,TemporalBoundScope.OriginalHard);
        _temporalAudit.Add(new(key,decision));
        if (decision.IsContradiction)
            throw new InvalidDataException($"No safe ordering exists in original hard domain: {key}; {string.Join(" | ",decision.Premises)}");
        if (decision.FixedValue is int value)
        {
            // Keep the witness in all dictionaries: no-overtake, snapshots and repair depend on it.
            variable.LB=value; variable.UB=value; variable.VType=GRB.CONTINUOUS;
        }
    }

    private int ResolveProxyHeadwaySeconds(
        string stationName,
        MovementEvent leadingEvent,
        MovementEvent followingEvent)
    {
        if (_instance.TryGetMovementHeadwayRule(stationName, leadingEvent, followingEvent, out MovementHeadwayRule? exactRule) &&
            exactRule is not null)
        {
            return exactRule.ProxyHeadwaySeconds;
        }

        // 若精确 family 组合缺失，则退化到同站的 departure-family 最大代理值，
        // 目的是保证 master 仍能构造出可运行的保守间隔。
        int? fallback = _instance.MovementHeadwayRules
            .Where(rule =>
                string.Equals(rule.StationName, stationName, StringComparison.OrdinalIgnoreCase) &&
                rule.LeadingEvent is MovementEvent.Departure or MovementEvent.Passing &&
                rule.FollowingEvent is MovementEvent.Departure or MovementEvent.Passing)
            .Select(rule => (int?)rule.ProxyHeadwaySeconds)
            .DefaultIfEmpty(null)
            .Max();

        return fallback ?? 0;
    }

    private void AddGlobalSpanConstraints(
        GRBModel model,
        GRBVar firstDeparture,
        GRBVar lastArrival,
        IReadOnlyDictionary<string, GRBVar> arrivalVars,
        IReadOnlyDictionary<string, GRBVar> departureVars)
    {
        foreach (TrainRecord train in _instance.Trains)
        {
            TrainStopRecord origin = train.OrderedStops[0];
            TrainStopRecord destination = train.OrderedStops[^1];

            GRBLinExpr firstDepartureExpr = new();
            firstDepartureExpr.AddTerm(1.0, firstDeparture);
            firstDepartureExpr.AddTerm(-1.0, departureVars[origin.StopId]);
            model.AddConstr(firstDepartureExpr, GRB.LESS_EQUAL, 0.0, $"global_first_t{train.TrainIndex}");

            GRBLinExpr lastArrivalExpr = new();
            lastArrivalExpr.AddTerm(1.0, lastArrival);
            lastArrivalExpr.AddTerm(-1.0, arrivalVars[destination.StopId]);
            model.AddConstr(lastArrivalExpr, GRB.GREATER_EQUAL, 0.0, $"global_last_t{train.TrainIndex}");
        }
    }

    private static void SetObjective(
        GRBModel model,
        GRBVar firstDeparture,
        GRBVar lastArrival,
        IEnumerable<GRBVar> runningTimeSupplementVars)
    {
        GRBLinExpr objective = new();
        objective.AddTerm(1.0, lastArrival);
        objective.AddTerm(-1.0, firstDeparture);
        GRBLinExpr supplement = new();
        foreach (GRBVar variable in runningTimeSupplementVars)
        {
            supplement.AddTerm(1.0, variable);
        }
        model.SetObjective(new GRBLinExpr(), GRB.MINIMIZE);
        model.SetObjectiveN(objective, 0, 2, 1.0, 0.0, 0.0, "global_span");
        model.SetObjectiveN(supplement, 1, 1, 1.0, 0.0, 0.0, "running_time_supplement");
    }

    private void AddInjectedNogoodCuts(
        GRBModel model,
        IReadOnlyDictionary<string, GRBVar> sectionPrecedenceVars,
        IReadOnlyDictionary<string, GRBVar> stopVars,
        IReadOnlyDictionary<string, GRBVar> stationEventPrecedenceVars)
    {
        foreach (LocalPrecedenceNogoodCut cut in _injectedNogoodCuts)
        {
            Dictionary<string, int> supportedSectionValues = cut.PrecedenceIncumbentValuesByKey
                .Where(item => sectionPrecedenceVars.ContainsKey(item.Key))
                .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> supportedStopValues = cut.StopIncumbentValuesByStopId
                .Where(item => stopVars.ContainsKey(item.Key))
                .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> supportedStationEventValues =
                (cut.StationEventPrecedenceIncumbentValuesByKey ?? new Dictionary<string, int>())
                .Where(item => stationEventPrecedenceVars.ContainsKey(item.Key))
                .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);

            if (supportedSectionValues.Count == 0 &&
                supportedStopValues.Count == 0 &&
                supportedStationEventValues.Count == 0)
            {
                continue;
            }

            LocalPrecedenceNogoodRow row = LocalPrecedenceNogoodRowBuilder.Build(cut with
            {
                PrecedenceIncumbentValuesByKey = supportedSectionValues,
                StopIncumbentValuesByStopId = supportedStopValues,
                StationEventPrecedenceIncumbentValuesByKey = supportedStationEventValues
            });
            GRBLinExpr matchingLiteralExpr = new();
            foreach ((string key, double coefficient) in row.SectionCoefficientsByKey)
            {
                matchingLiteralExpr.AddTerm(coefficient, sectionPrecedenceVars[key]);
            }

            foreach ((string key, double coefficient) in row.StopCoefficientsByStopId)
            {
                matchingLiteralExpr.AddTerm(coefficient, stopVars[key]);
            }

            foreach ((string key, double coefficient) in row.StationEventCoefficientsByKey)
            {
                matchingLiteralExpr.AddTerm(coefficient, stationEventPrecedenceVars[key]);
            }

            model.AddConstr(
                matchingLiteralExpr,
                GRB.LESS_EQUAL,
                row.RightHandSide,
                $"inj_nogood_{Sanitize(cut.CutId)}");
        }
    }

    private void AddPrecedenceTrustRegionConstraints(
        GRBModel model,
        IReadOnlyDictionary<string, GRBVar> sectionPrecedenceVars)
    {
        if (_precedenceTrustRegion is null)
        {
            return;
        }

        List<(string Key, GRBVar Variable, int CenterValue)> supportedTerms = _precedenceTrustRegion.CenterValuesByKey
            .Where(item => sectionPrecedenceVars.ContainsKey(item.Key))
            .Select(item => (item.Key, sectionPrecedenceVars[item.Key], item.Value))
            .ToList();

        if (supportedTerms.Count == 0)
        {
            return;
        }

        if (_precedenceTrustRegion.MaxTotalChanges >= 0)
        {
            GRBLinExpr totalMismatchExpr = BuildMismatchExpression(supportedTerms);
            model.AddConstr(
                totalMismatchExpr,
                GRB.LESS_EQUAL,
                _precedenceTrustRegion.MaxTotalChanges,
                $"tr_total_{Sanitize(_precedenceTrustRegion.RegionId)}");
        }

        if (_precedenceTrustRegion.MaxNonHotspotChanges >= 0)
        {
            List<(string Key, GRBVar Variable, int CenterValue)> nonHotspotTerms = supportedTerms
                .Where(item => !_precedenceTrustRegion.HotspotKeys.Contains(item.Key))
                .ToList();

            if (nonHotspotTerms.Count > 0)
            {
                GRBLinExpr nonHotspotMismatchExpr = BuildMismatchExpression(nonHotspotTerms);
                model.AddConstr(
                    nonHotspotMismatchExpr,
                    GRB.LESS_EQUAL,
                    _precedenceTrustRegion.MaxNonHotspotChanges,
                    $"tr_nonhot_{Sanitize(_precedenceTrustRegion.RegionId)}");
            }
        }
    }

    private static GRBLinExpr BuildMismatchExpression(
        IReadOnlyList<(string Key, GRBVar Variable, int CenterValue)> supportedTerms)
    {
        GRBLinExpr mismatchExpr = new();
        foreach ((_, GRBVar variable, int centerValue) in supportedTerms)
        {
            if (centerValue == 0)
            {
                mismatchExpr.AddTerm(1.0, variable);
            }
            else
            {
                mismatchExpr.AddConstant(1.0);
                mismatchExpr.AddTerm(-1.0, variable);
            }
        }

        return mismatchExpr;
    }

    private void AddMaintenanceWindowConstraints(
        GRBModel model,
        IReadOnlyDictionary<string, GRBVar> arrivalVars,
        IReadOnlyDictionary<string, GRBVar> departureVars,
        IList<MaintenanceWindowBinaryBinding> binaryBindings)
    {
        foreach (TrainRecord train in _instance.Trains)
        {
            for (int index = 0; index < train.OrderedStops.Count - 1; index++)
            {
                TrainStopRecord currentStop = train.OrderedStops[index];
                TrainStopRecord nextStop = train.OrderedStops[index + 1];

                if (string.IsNullOrWhiteSpace(nextStop.IncomingSectionName) ||
                    !_instance.SectionByName.TryGetValue(nextStop.IncomingSectionName, out SectionRecord? section))
                {
                    continue;
                }

                TravelDirection direction = DetermineOperationalDirection(section, currentStop.StationName, nextStop.StationName);
                List<MaintenanceWindowRule> matchedWindows = _instance.MaintenanceWindows
                    .Where(window =>
                        string.Equals(window.SectionName, section.SectionName, StringComparison.OrdinalIgnoreCase) &&
                        window.Direction == direction)
                    .ToList();

                foreach (MaintenanceWindowRule window in matchedWindows)
                {
                    string beforeConstraintName =
                        $"mw_before_constr_t{train.TrainIndex}_s{nextStop.StationOrder}_{window.StartTimeSeconds}_{window.EndTimeSeconds}";
                    string afterConstraintName =
                        $"mw_after_constr_t{train.TrainIndex}_s{currentStop.StationOrder}_{window.StartTimeSeconds}_{window.EndTimeSeconds}";
                    double beforeM = AddRelaxUpperBigMAudit(
                        beforeConstraintName,
                        _hardEventBounds.Arrival(nextStop.StopId),
                        window.StartTimeSeconds);
                    double afterM = AddRelaxLowerBigMAudit(
                        afterConstraintName,
                        _hardEventBounds.Departure(currentStop.StopId),
                        window.EndTimeSeconds);
                    GRBVar beforeWindow = model.AddVar(0.0, 1.0, 0.0, GRB.BINARY,
                        $"mw_before_t{train.TrainIndex}_from{currentStop.StationOrder}_to{nextStop.StationOrder}_{window.StartTimeSeconds}_{window.EndTimeSeconds}");
                    binaryBindings.Add(new(
                        $"maintenance::{section.SectionName}::{train.TrainIndex}::{window.StartTimeSeconds}::{window.EndTimeSeconds}",
                        currentStop.StopId,
                        nextStop.StopId,
                        window.StartTimeSeconds,
                        window.EndTimeSeconds,
                        beforeWindow));

                    GRBLinExpr beforeExpr = new();
                    beforeExpr.AddTerm(1.0, arrivalVars[nextStop.StopId]);
                    beforeExpr.AddTerm(beforeM, beforeWindow);
                    model.AddConstr(beforeExpr, GRB.LESS_EQUAL, window.StartTimeSeconds + beforeM,
                        beforeConstraintName);

                    GRBLinExpr afterExpr = new();
                    afterExpr.AddTerm(1.0, departureVars[currentStop.StopId]);
                    afterExpr.AddTerm(afterM, beforeWindow);
                    model.AddConstr(afterExpr, GRB.GREATER_EQUAL, window.EndTimeSeconds,
                        afterConstraintName);
                }
            }
        }
    }

    /// <summary>Add closed-window station quota counts for visits that can contribute.</summary>
    /// <param name="model">Borrowed native model to augment.</param>
    /// <param name="arrivalVars">Arrival event variables in seconds by visit.</param>
    /// <param name="departureVars">Departure event variables in seconds by visit.</param>
    /// <param name="stopVars">Stop indicators bounded by permanent route-track domains.</param>
    /// <param name="stopById">Original visit records keyed by visit ID.</param>
    /// <param name="categoryBindings">Receives category handles used for saved timetable readback.</param>
    /// <param name="fixedStopValues">Permanent stopping or passing values from preprocessing.</param>
    private void AddStationStopQuotaConstraints(
        GRBModel model,
        IReadOnlyDictionary<string, GRBVar> arrivalVars,
        IReadOnlyDictionary<string, GRBVar> departureVars,
        IReadOnlyDictionary<string, GRBVar> stopVars,
        IReadOnlyDictionary<string, TrainStopRecord> stopById,
        IList<StationQuotaCategoryBinding> categoryBindings,
        IReadOnlyDictionary<string, int> fixedStopValues)
    {
        foreach ((StationStopQuotaRule rule, int ruleIndex) in _instance.StationStopQuotaRules.Select((value, index) => (value, index)))
        {
            List<GRBVar> countedVars = new();

            foreach ((string stopId, TrainStopRecord stop) in stopById)
            {
                if (!string.Equals(stop.StationName, rule.StationName, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(stop.TrainType, rule.TrainType, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                HardEventBound eventTimeBound = stop.StopRole == TrainStopRole.Origin
                    ? _hardEventBounds.Departure(stopId) : _hardEventBounds.Arrival(stopId);
                double tolerance = TimetableTimePolicy.NumericToleranceSeconds;
                if ((fixedStopValues.TryGetValue(stopId, out int fixedValue) && fixedValue == 0) ||
                    eventTimeBound.Lower > rule.EndTimeSeconds + tolerance ||
                    eventTimeBound.Upper < rule.StartTimeSeconds - tolerance) continue;

                GRBVar inWindowVar = model.AddVar(0.0, 1.0, 0.0, GRB.BINARY,
                    $"quota_inwin_t{stop.TrainIndex}_s{stop.StationOrder}_{rule.StartTimeSeconds}_{rule.EndTimeSeconds}");
                GRBVar leftOutsideVar = model.AddVar(0.0, 1.0, 0.0, GRB.BINARY,
                    $"quota_left_t{stop.TrainIndex}_s{stop.StationOrder}_{rule.StartTimeSeconds}_{rule.EndTimeSeconds}");
                GRBVar rightOutsideVar = model.AddVar(0.0, 1.0, 0.0, GRB.BINARY,
                    $"quota_right_t{stop.TrainIndex}_s{stop.StationOrder}_{rule.StartTimeSeconds}_{rule.EndTimeSeconds}");
                string ruleId = $"station-stop-{ruleIndex + 1:D3}";
                categoryBindings.Add(new(
                    $"quota-category::{ruleId}::{stopId}",
                    ruleId,
                    stopId,
                    rule.StartTimeSeconds,
                    rule.EndTimeSeconds,
                    inWindowVar,
                    leftOutsideVar,
                    rightOutsideVar));

                // Arrival counts at intermediate and destination visits. Origins use departure.
                GRBVar eventTimeVar = stop.StopRole == TrainStopRole.Origin ? departureVars[stopId] : arrivalVars[stopId];
                GRBVar stopVar = stopVars[stopId];
                StationQuotaWindowPolicy.RestrictToPermanentBounds(inWindowVar, leftOutsideVar, rightOutsideVar,
                    eventTimeBound.Lower, eventTimeBound.Upper, rule.StartTimeSeconds, rule.EndTimeSeconds);
                string quotaSuffix =
                    $"t{stop.TrainIndex}_s{stop.StationOrder}_{rule.StartTimeSeconds}_{rule.EndTimeSeconds}";
                string leftConstraintName = $"quota_left_ub_{quotaSuffix}";
                string rightConstraintName = $"quota_right_lb_{quotaSuffix}";
                string lowerConstraintName = $"quota_inwin_lb_{quotaSuffix}";
                string upperConstraintName = $"quota_inwin_ub_{quotaSuffix}";
                double leftM = AddRelaxUpperBigMAudit(
                    leftConstraintName, eventTimeBound, rule.StartTimeSeconds);
                double rightM = AddRelaxLowerBigMAudit(
                    rightConstraintName, eventTimeBound, rule.EndTimeSeconds);
                double lowerM = AddRelaxLowerBigMAudit(
                    lowerConstraintName, eventTimeBound, rule.StartTimeSeconds);
                double upperM = AddRelaxUpperBigMAudit(
                    upperConstraintName, eventTimeBound, rule.EndTimeSeconds);

                // Exactly one category is selected for a stop. Passing selects none.
                model.AddConstr(inWindowVar + leftOutsideVar + rightOutsideVar, GRB.EQUAL, stopVar,
                    $"quota_class_sum_t{stop.TrainIndex}_s{stop.StationOrder}_{rule.StartTimeSeconds}_{rule.EndTimeSeconds}");

                // Continuous outer branches meet the closed window at its endpoints; no artificial time gap.
                // Final business validation counts endpoints inside and rejects any inconsistent quota count.
                // leftOutside = 1 allows the left endpoint in the linear relaxation.
                GRBLinExpr leftUpperExpr = new();
                leftUpperExpr.AddTerm(1.0, eventTimeVar);
                leftUpperExpr.AddTerm(leftM, leftOutsideVar);
                model.AddConstr(leftUpperExpr, GRB.LESS_EQUAL, rule.StartTimeSeconds + leftM,
                    leftConstraintName);

                // rightOutside = 1 allows the right endpoint in the linear relaxation.
                GRBLinExpr rightLowerExpr = new();
                rightLowerExpr.AddTerm(1.0, eventTimeVar);
                rightLowerExpr.AddTerm(-rightM, rightOutsideVar);
                model.AddConstr(rightLowerExpr, GRB.GREATER_EQUAL, rule.EndTimeSeconds - rightM,
                    rightConstraintName);

                // The counted category uses the closed quota interval.
                GRBLinExpr inWindowLowerExpr = new();
                inWindowLowerExpr.AddTerm(1.0, eventTimeVar);
                inWindowLowerExpr.AddTerm(-lowerM, inWindowVar);
                model.AddConstr(inWindowLowerExpr, GRB.GREATER_EQUAL, rule.StartTimeSeconds - lowerM,
                    lowerConstraintName);

                GRBLinExpr inWindowUpperExpr = new();
                inWindowUpperExpr.AddTerm(1.0, eventTimeVar);
                inWindowUpperExpr.AddTerm(upperM, inWindowVar);
                model.AddConstr(inWindowUpperExpr, GRB.LESS_EQUAL, rule.EndTimeSeconds + upperM,
                    upperConstraintName);

                countedVars.Add(inWindowVar);
            }

            // Preflight rejects any positive minimum for an empty count.
            if (countedVars.Count == 0) continue;

            GRBLinExpr countExpr = new();
            foreach (GRBVar countedVar in countedVars)
            {
                countExpr.AddTerm(1.0, countedVar);
            }

            model.AddConstr(countExpr, GRB.GREATER_EQUAL, rule.MinStops,
                $"station_quota_min_{Sanitize(rule.StationName)}_{Sanitize(rule.TrainType)}_{rule.StartTimeSeconds}_{rule.EndTimeSeconds}");
            model.AddConstr(countExpr, GRB.LESS_EQUAL, rule.MaxStops,
                $"station_quota_max_{Sanitize(rule.StationName)}_{Sanitize(rule.TrainType)}_{rule.StartTimeSeconds}_{rule.EndTimeSeconds}");
        }
    }

    private void AddTrainSectionTravelTimeLimitConstraints(
        GRBModel model,
        IReadOnlyDictionary<string, GRBVar> arrivalVars,
        IReadOnlyDictionary<string, GRBVar> departureVars)
    {
        Dictionary<int, TrainRecord> trainByIndex = _instance.Trains.ToDictionary(train => train.TrainIndex);
        foreach (TrainSectionTravelTimeLimitRule rule in _instance.TrainSectionTravelTimeLimitRules)
        {
            if (!trainByIndex.TryGetValue(rule.TrainIndex, out TrainRecord? train))
            {
                throw new InvalidDataException($"Travel-time rule references missing train {rule.TrainIndex}.");
            }

            (TrainStopRecord startStop, TrainStopRecord endStop) =
                TrainSectionTravelTimeResolver.Resolve(train, rule);
            model.AddConstr(
                arrivalVars[endStop.StopId] - departureVars[startStop.StopId],
                GRB.LESS_EQUAL,
                rule.MaxTravelTimeSeconds,
                $"travel_time_max_t{rule.TrainIndex}_{Sanitize(rule.SectionStartStation)}_{Sanitize(rule.SectionEndStation)}");
        }
    }

    private RunTimeRule ResolveRunTimeRule(TrainStopRecord currentStop, TrainStopRecord nextStop)
    {
        if (string.IsNullOrWhiteSpace(nextStop.IncomingSectionName))
        {
            throw new InvalidOperationException($"Next stop '{nextStop.StopId}' has no incoming section name.");
        }

        if (!_instance.SectionByName.TryGetValue(nextStop.IncomingSectionName, out SectionRecord? section))
        {
            throw new InvalidOperationException($"Section '{nextStop.IncomingSectionName}' was not found in the loaded section list.");
        }

        TravelDirection direction = DetermineOperationalDirection(section, currentStop.StationName, nextStop.StationName);
        string? scaleName = nextStop.RunScaleName;
        if (string.IsNullOrWhiteSpace(scaleName))
        {
            throw new InvalidOperationException($"Stop '{nextStop.StopId}' has no run scale assigned for section '{nextStop.IncomingSectionName}'.");
        }

        RunTimeRule? rule = _instance.RunTimeRules.FirstOrDefault(candidate =>
            string.Equals(candidate.ScaleName, scaleName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidate.SectionName, nextStop.IncomingSectionName, StringComparison.OrdinalIgnoreCase) &&
            candidate.Direction == direction);

        return rule ?? throw new InvalidOperationException(
            $"Runtime rule not found for scale '{scaleName}', section '{nextStop.IncomingSectionName}', direction '{direction}'.");
    }

    private static string Sanitize(string value)
    {
        char[] invalidChars = Path.GetInvalidFileNameChars();
        string sanitized = value;
        foreach (char invalidChar in invalidChars)
        {
            sanitized = sanitized.Replace(invalidChar, '_');
        }

        return sanitized.Replace(' ', '_');
    }

    private static TravelDirection DetermineOperationalDirection(SectionRecord section, string previousStation, string nextStation)
    {
        bool sameOrientation =
            string.Equals(section.FromStation, previousStation, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(section.ToStation, nextStation, StringComparison.OrdinalIgnoreCase);

        bool reverseOrientation =
            string.Equals(section.ToStation, previousStation, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(section.FromStation, nextStation, StringComparison.OrdinalIgnoreCase);

        if (sameOrientation)
        {
            return section.DirectionTag;
        }

        if (reverseOrientation)
        {
            return section.DirectionTag switch
            {
                TravelDirection.Up => TravelDirection.Down,
                TravelDirection.Down => TravelDirection.Up,
                _ => TravelDirection.Unknown
            };
        }

        throw new InvalidOperationException(
            $"Cannot determine operational direction for section '{section.SectionName}' between '{previousStation}' and '{nextStation}'.");
    }
}

public sealed record LocalPrecedenceNogoodCut(
    string CutId,
    string StationName,
    int FirstTrainIndex,
    int SecondTrainIndex,
    IReadOnlyDictionary<string, int> PrecedenceIncumbentValuesByKey,
    IReadOnlyDictionary<string, int> StopIncumbentValuesByStopId,
    string SourceFamily,
    string Rationale,
    IReadOnlyDictionary<string, int>? StationEventPrecedenceIncumbentValuesByKey = null);

internal sealed record LocalPrecedenceNogoodRow(
    IReadOnlyDictionary<string, double> SectionCoefficientsByKey,
    IReadOnlyDictionary<string, double> StopCoefficientsByStopId,
    IReadOnlyDictionary<string, double> StationEventCoefficientsByKey,
    double RightHandSide,
    int LiteralCount);

internal static class LocalPrecedenceNogoodRowBuilder
{
    internal static LocalPrecedenceNogoodRow Build(LocalPrecedenceNogoodCut cut)
    {
        ArgumentNullException.ThrowIfNull(cut);
        IReadOnlyDictionary<string, int> stationEventValues =
            cut.StationEventPrecedenceIncumbentValuesByKey ?? new Dictionary<string, int>();
        Dictionary<string, double> sectionCoefficients = BuildCoefficients(
            cut.PrecedenceIncumbentValuesByKey,
            nameof(cut.PrecedenceIncumbentValuesByKey));
        Dictionary<string, double> stopCoefficients = BuildCoefficients(
            cut.StopIncumbentValuesByStopId,
            nameof(cut.StopIncumbentValuesByStopId));
        Dictionary<string, double> stationEventCoefficients = BuildCoefficients(
            stationEventValues,
            nameof(cut.StationEventPrecedenceIncumbentValuesByKey));
        int oneCount = CountOnes(cut.PrecedenceIncumbentValuesByKey) +
            CountOnes(cut.StopIncumbentValuesByStopId) +
            CountOnes(stationEventValues);

        return new LocalPrecedenceNogoodRow(
            sectionCoefficients,
            stopCoefficients,
            stationEventCoefficients,
            oneCount - 1.0,
            sectionCoefficients.Count + stopCoefficients.Count + stationEventCoefficients.Count);
    }

    private static Dictionary<string, double> BuildCoefficients(
        IReadOnlyDictionary<string, int> values,
        string parameterName)
    {
        Dictionary<string, double> coefficients = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, int value) in values.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            if (value is not 0 and not 1)
            {
                throw new ArgumentOutOfRangeException(parameterName, value, "No-good incumbent values must be binary.");
            }

            coefficients.Add(key, value == 1 ? 1.0 : -1.0);
        }

        return coefficients;
    }

    private static int CountOnes(IReadOnlyDictionary<string, int> values)
    {
        return values.Count(item => item.Value == 1);
    }
}

public sealed record PrecedenceTrustRegion(
    string RegionId,
    IReadOnlyDictionary<string, int> CenterValuesByKey,
    IReadOnlySet<string> HotspotKeys,
    int MaxTotalChanges,
    int MaxNonHotspotChanges);
