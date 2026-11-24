using System.Diagnostics;
using Gurobi;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

internal sealed record LocalRepairDecisionDomain(
    string DomainIdentity,
    IReadOnlyList<string> ActiveStations,
    IReadOnlyList<string> ResourceChoiceDecisionIds,
    IReadOnlyList<string> MicroOrderDecisionIds,
    IReadOnlyList<string> FlexibleStopDecisionIds,
    IReadOnlyList<string> MacroEndpointOrderDecisionIds,
    IReadOnlyList<string> InterfaceKeys,
    int RuntimeCouplingCount,
    int ServiceCouplingCount,
    int PresolvedBinaryCount)
{
    internal int FreeDiscreteDecisionCount =>
        ResourceChoiceDecisionIds.Count + MicroOrderDecisionIds.Count + FlexibleStopDecisionIds.Count;

    internal bool HasAdditionalFreedomBeyond(LocalRepairDecisionDomain other)
    {
        ArgumentNullException.ThrowIfNull(other);
        HashSet<string> existing = other.ResourceChoiceDecisionIds
            .Concat(other.MicroOrderDecisionIds)
            .Concat(other.FlexibleStopDecisionIds)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return ResourceChoiceDecisionIds.Concat(MicroOrderDecisionIds).Concat(FlexibleStopDecisionIds)
            .Any(id => !existing.Contains(id));
    }

    internal static LocalRepairDecisionDomain Create(
        IReadOnlyList<string> activeStations,
        IReadOnlyList<StationMicroRepairAugmentation> augmentations,
        IReadOnlyDictionary<string, GRBVar> stopVariables,
        IReadOnlyDictionary<string, GRBVar> arrivals,
        IReadOnlyDictionary<string, GRBVar> departures,
        int runtimeCouplingCount,
        int serviceCouplingCount,
        int presolvedBinaryCount)
    {
        string[] resources = augmentations.SelectMany(augmentation => augmentation.ChoiceBindings)
            .Select(binding => string.Join('|', binding.StationName, binding.RequirementId,
                binding.IsStop ? "stop" : "pass", binding.TrackName,
                binding.IncomingRouteId?.ToString() ?? "-", binding.OutgoingRouteId?.ToString() ?? "-"))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        string[] orders = augmentations.SelectMany(augmentation => augmentation.OrderBindings)
            .Select(binding => string.Join('|', binding.StationName, binding.Kind,
                binding.FirstRequirementId, binding.FirstEventSide,
                binding.SecondRequirementId, binding.SecondEventSide))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        string[] flexibleStops = stopVariables.Where(item =>
                item.Value.Get(GRB.CharAttr.VType) == GRB.BINARY &&
                item.Value.Get(GRB.DoubleAttr.LB) < item.Value.Get(GRB.DoubleAttr.UB) - 1e-12)
            .Select(item => $"stop|{item.Key}")
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        string[] interfaces = arrivals.Keys.Select(id => $"{id}:Incoming")
            .Concat(departures.Keys.Select(id => $"{id}:Outgoing"))
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        string[] stations = activeStations.Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        string identity = ContinuousExtendabilityContext.Hash(string.Join('\n',
            string.Join('|', resources), string.Join('|', orders), string.Join('|', flexibleStops),
            string.Join('|', interfaces), $"runtime={runtimeCouplingCount}", $"service={serviceCouplingCount}"));
        return new(identity, stations, resources, orders, flexibleStops, [], interfaces,
            runtimeCouplingCount, serviceCouplingCount, presolvedBinaryCount);
    }
}

/// <summary>
/// Maps a certificate's active temporal condition back to the compact order variable used
/// by the station generator.  Resource/stop choices alone are not a complete pattern: two
/// certificates may deliberately use the same resources with opposite legal event order.
/// </summary>
internal static class StationPatternOrderSignature
{
    internal static int? ResolveValue(
        string firstRequirementId,
        string secondRequirementId,
        PromotedMicroOrderKind kind,
        StationEventSide firstEventSide,
        StationEventSide secondEventSide,
        MicroCertificateCondition condition)
    {
        MicroCertificateConditionKind expected = kind == PromotedMicroOrderKind.TrackOccupation
            ? MicroCertificateConditionKind.TrackOccupationOrder
            : MicroCertificateConditionKind.RouteHeadway;
        if (condition.Kind != expected || condition.EarlierStopId is null || condition.EarlierEvent is null)
            return null;

        bool forward = string.Equals(condition.EarlierStopId, firstRequirementId, StringComparison.OrdinalIgnoreCase) &&
                       string.Equals(condition.LaterStopId, secondRequirementId, StringComparison.OrdinalIgnoreCase);
        bool reverse = string.Equals(condition.EarlierStopId, secondRequirementId, StringComparison.OrdinalIgnoreCase) &&
                       string.Equals(condition.LaterStopId, firstRequirementId, StringComparison.OrdinalIgnoreCase);
        if (!forward && !reverse) return null;

        // Track occupation bindings represent the whole occupation interval.  The certificate
        // therefore names earlier-end/later-start sides, which need not equal the stored start
        // sides for an intermediate stop.  Route bindings, in contrast, are event-specific.
        if (kind == PromotedMicroOrderKind.RouteHeadway)
        {
            bool sidesMatch = forward
                ? condition.EarlierEvent == firstEventSide && condition.LaterEvent == secondEventSide
                : condition.EarlierEvent == secondEventSide && condition.LaterEvent == firstEventSide;
            if (!sidesMatch) return null;
        }

        return forward ? 1 : 0;
    }
}

/// <summary>
/// Holds one station-pattern MIP across the prescribed 30-second and 120-second
/// cumulative tiers.  A second call to <see cref="Continue"/> invokes Optimize on
/// the same Gurobi model, so the continuation is not a rebuilt look-alike.
/// </summary>
internal sealed class IndependentStationPatternSession : IDisposable
{
    private readonly TimetablingInstance _instance;
    private readonly MasterSolutionSnapshot _reference;
    private readonly int _endpointOccupationSeconds;
    private readonly BasicMilpModel _model;
    private readonly GRBModel _raw;
    private readonly Dictionary<string, GRBVar> _arrivals;
    private readonly Dictionary<string, GRBVar> _departures;
    private readonly IReadOnlyList<StationMicroRequirement> _requirements;
    private readonly IReadOnlyList<StationMicroRepairAugmentation> _augmentations;
    private readonly IndependentStationModelAudit _audit;
    private readonly IReadOnlyList<string> _activeStations;
    private readonly LocalRepairDecisionDomain _decisionDomain;
    private readonly HashSet<string> _appliedFeedbackIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _generationTargetConstraintIds = new(StringComparer.Ordinal);
    private readonly bool _forceZeroTimeLimitOnFirstCallForTest;
    private double _runtimeSeconds;
    private double _cumulativeLimitSeconds;
    private double? _firstIncumbentSeconds;
    private bool _modelDirty;
    private int _generationOnlyExclusionCount;
    private bool _disposed;

    internal int OptimizeCallCount { get; private set; }

    internal LocalRepairDecisionDomain DecisionDomain => _decisionDomain;

    private IndependentStationPatternSession(
        TimetablingInstance instance,
        MasterSolutionSnapshot reference,
        int endpointOccupationSeconds,
        BasicMilpModel model,
        Dictionary<string, GRBVar> arrivals,
        Dictionary<string, GRBVar> departures,
        IReadOnlyList<StationMicroRequirement> requirements,
        IReadOnlyList<StationMicroRepairAugmentation> augmentations,
        IndependentStationModelAudit audit,
        IReadOnlyList<string> activeStations,
        LocalRepairDecisionDomain decisionDomain,
        bool forceZeroTimeLimitOnFirstCallForTest)
    {
        _instance = instance;
        _reference = reference;
        _endpointOccupationSeconds = endpointOccupationSeconds;
        _model = model;
        _raw = model.Model;
        _arrivals = arrivals;
        _departures = departures;
        _requirements = requirements;
        _augmentations = augmentations;
        _audit = audit;
        _activeStations = activeStations;
        _decisionDomain = decisionDomain;
        _forceZeroTimeLimitOnFirstCallForTest = forceZeroTimeLimitOnFirstCallForTest;
    }

    internal static IndependentStationPatternSession Open(
        TimetablingInstance instance,
        MasterSolutionSnapshot reference,
        string station,
        int endpointOccupationSeconds,
        string? logFile,
        double runningTimeSupplementRatio,
        bool disableHeuristicsForTest = false,
        bool forceZeroTimeLimitOnFirstCallForTest = false)
        => Open(instance, reference, [station], endpointOccupationSeconds, logFile,
            runningTimeSupplementRatio, disableHeuristicsForTest,
            forceZeroTimeLimitOnFirstCallForTest, allowFlexibleStopDecisions: false);

    internal static IndependentStationPatternSession Open(
        TimetablingInstance instance,
        MasterSolutionSnapshot reference,
        IReadOnlyList<string> activeStations,
        int endpointOccupationSeconds,
        string? logFile,
        double runningTimeSupplementRatio,
        bool disableHeuristicsForTest = false,
        bool forceZeroTimeLimitOnFirstCallForTest = false,
        bool allowFlexibleStopDecisions = false)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(activeStations);
        string[] stations = activeStations.Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (stations.Length is < 1 or > 3)
            throw new ArgumentException("A persistent local Micro session must activate one to three stations.", nameof(activeStations));
        HashSet<string> stationSet = stations.ToHashSet(StringComparer.OrdinalIgnoreCase);
        TrainStopRecord[] stops = instance.TrainStops.Where(stop => stationSet.Contains(stop.StationName))
            .OrderBy(stop => stop.TrainIndex)
            .ThenBy(stop => stop.StationOrder)
            .ToArray();
        if (stops.Length == 0)
            throw new InvalidDataException($"No Micro requirements exist at active stations [{string.Join('|', stations)}].");

        GRBEnv environment = new();
        GRBModel raw = new(environment)
        {
            ModelName = $"independent_station_{string.Join('_', stations.Select(IndependentStationPatternSolver.Sanitize))}"
        };
        Dictionary<string, GRBVar> arrivals = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, GRBVar> departures = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, GRBVar> stopVars = new(StringComparer.OrdinalIgnoreCase);
        foreach (TrainStopRecord stop in stops)
        {
            arrivals.Add(stop.StopId, raw.AddVar(stop.EarliestArrSeconds, stop.LatestArrSeconds, 0,
                GRB.CONTINUOUS, $"local_arr_t{stop.TrainIndex}_s{stop.StationOrder}"));
            departures.Add(stop.StopId, raw.AddVar(stop.EarliestDepSeconds, stop.LatestDepSeconds, 0,
                GRB.CONTINUOUS, $"local_dep_t{stop.TrainIndex}_s{stop.StationOrder}"));
            int stopValue = reference.StopByStopId.TryGetValue(stop.StopId, out int value)
                ? value
                : throw new InvalidDataException($"Reference snapshot lacks stop decision '{stop.StopId}'.");
            bool flexible = allowFlexibleStopDecisions && stop.StopRole == TrainStopRole.Intermediate &&
                            stop.StopRequirement == StopRequirement.OptionalStop;
            stopVars.Add(stop.StopId, raw.AddVar(flexible ? 0 : stopValue, flexible ? 1 : stopValue, 0,
                flexible ? GRB.BINARY : GRB.CONTINUOUS,
                $"local_stop_t{stop.TrainIndex}_s{stop.StationOrder}"));
        }

        BasicMilpModel model = new(
            environment,
            raw,
            instance,
            arrivals,
            departures,
            stopVars,
            [],
            new Dictionary<string, GRBVar>(),
            [],
            new Dictionary<string, GRBVar>());
        try
        {
            raw.Update();
            IndependentStationPatternSolver.AddStationDwellConstraints(
                model,
                stops,
                IndependentStationPatternSolver.ResolveActuallyOvertakenStopIds(instance, reference));
            Dictionary<string, TrainRecord> trainByStop = instance.Trains
                .SelectMany(train => train.OrderedStops.Select(stop => (stop.StopId, train)))
                .ToDictionary(item => item.StopId, item => item.train, StringComparer.OrdinalIgnoreCase);
            List<StationMicroRequirement> requirements = [];
            foreach (TrainStopRecord stop in stops)
            {
                bool flexible = allowFlexibleStopDecisions && stop.StopRole == TrainStopRole.Intermediate &&
                                stop.StopRequirement == StopRequirement.OptionalStop;
                IEnumerable<bool> modes = flexible
                    ? new[] { false, true }
                    : new[] { reference.StopByStopId[stop.StopId] == 1 };
                foreach (bool isStop in modes)
                    requirements.Add(StationLevelMicroFeasibilitySlave.BuildRequirementForMode(
                        instance, trainByStop[stop.StopId], stop, isStop,
                        reference.ArrivalSecondsByStopId[stop.StopId],
                        reference.DepartureSecondsByStopId[stop.StopId]));
            }
            List<StationMicroRepairAugmentation> augmentations = [];
            foreach (string activeStation in stations)
            {
                augmentations.Add(StationMicroRepairModelAugmenter.Apply(
                    model,
                    requirements.Where(requirement => string.Equals(
                        requirement.StationName, activeStation, StringComparison.OrdinalIgnoreCase)).ToArray(),
                    endpointOccupationSeconds,
                    variablePrefix: $"pattern_{IndependentStationPatternSolver.Sanitize(activeStation)}"));
            }
            int runtimeCouplings = IndependentStationPatternSolver.AddRuntimeCouplings(
                model, instance, stops, stationSet, runningTimeSupplementRatio);
            int serviceCouplings = IndependentStationPatternSolver.AddServiceCouplings(
                model, instance, stationSet, reference);
            IndependentStationPatternSolver.AddRetimingObjective(model, reference);
            raw.Update();

            int choiceBinaries = augmentations.Sum(augmentation => augmentation.AlternativeCount);
            int orderBinaries = augmentations.Sum(augmentation => augmentation.OrderVariableCount);
            int sourceBinaries = raw.Get(GRB.IntAttr.NumBinVars);
            int flexibleStopBinaries = stopVars.Values.Count(variable =>
                variable.Get(GRB.CharAttr.VType) == GRB.BINARY);
            int localQuotaBinaries = raw.GetVars().Count(variable =>
                variable.Get(GRB.StringAttr.VarName).StartsWith("local_quota_", StringComparison.Ordinal));
            int macroBinaries = Math.Max(0,
                sourceBinaries - choiceBinaries - orderBinaries - flexibleStopBinaries - localQuotaBinaries);
            int external = model.PromotedMicroChoiceBindings.Count(binding =>
                !stationSet.Contains(binding.StationName));
            (bool presolveAvailable, string presolveStopReason, int presolvedVariables,
                int presolvedBinaries, int presolvedConstraints) =
                IndependentStationPatternSolver.CapturePresolveSize(raw);
            IndependentStationModelAudit audit = new(
                stations,
                requirements.Count,
                raw.Get(GRB.IntAttr.NumVars),
                sourceBinaries,
                raw.Get(GRB.IntAttr.NumConstrs),
                presolveAvailable,
                presolveStopReason,
                presolvedVariables,
                presolvedBinaries,
                presolvedConstraints,
                choiceBinaries,
                orderBinaries,
                macroBinaries,
                external,
                runtimeCouplings,
                serviceCouplings);
            if (macroBinaries != 0 || external != 0)
                throw new InvalidDataException(
                    $"Independent station model leaked decision variables: macro={macroBinaries}, external-resource={external}.");

            LocalRepairDecisionDomain decisionDomain = LocalRepairDecisionDomain.Create(
                stations, augmentations, stopVars, arrivals, departures,
                runtimeCouplings, serviceCouplings, presolvedBinaries);

            if (!string.IsNullOrWhiteSpace(logFile))
            {
                string? directory = Path.GetDirectoryName(Path.GetFullPath(logFile));
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                raw.Set(GRB.StringParam.LogFile, logFile);
            }
            raw.Set(GRB.IntParam.Seed, 0);
            raw.Set(GRB.IntParam.Threads, 0);
            if (disableHeuristicsForTest) raw.Set(GRB.DoubleParam.Heuristics, 0);
            return new(instance, reference, endpointOccupationSeconds, model, arrivals, departures,
                requirements, augmentations, audit, stations, decisionDomain, forceZeroTimeLimitOnFirstCallForTest);
        }
        catch
        {
            model.Dispose();
            throw;
        }
    }

    internal IndependentStationPatternResult Continue(double additionalTimeLimitSeconds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (additionalTimeLimitSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(additionalTimeLimitSeconds));
        if (_raw.Get(GRB.IntAttr.SolCount) > 0 && !_modelDirty)
            return CaptureResult(_raw.Get(GRB.IntAttr.Status));

        FirstIncumbentTrackingCallback callback = new(stopAfterFirstIncumbent: true);
        _raw.SetCallback(callback);
        double appliedTimeLimit = _forceZeroTimeLimitOnFirstCallForTest && OptimizeCallCount == 0
            ? 0
            : additionalTimeLimitSeconds;
        _raw.Set(GRB.DoubleParam.TimeLimit, appliedTimeLimit);
        Stopwatch clock = Stopwatch.StartNew();
        OptimizeCallCount++;
        _model.Optimize();
        _modelDirty = false;
        clock.Stop();
        double priorRuntime = _runtimeSeconds;
        _runtimeSeconds += clock.Elapsed.TotalSeconds;
        _cumulativeLimitSeconds += additionalTimeLimitSeconds;
        if (!_firstIncumbentSeconds.HasValue && callback.FirstIncumbentTimeSeconds.HasValue)
            _firstIncumbentSeconds = priorRuntime + callback.FirstIncumbentTimeSeconds.Value;
        return CaptureResult(_raw.Get(GRB.IntAttr.Status));
    }

    internal LocalExtendabilityFeedbackApplication ApplyExtendabilityFeedback(
        ContinuousExtendabilityContext context,
        IReadOnlyList<ContinuousExtendabilityFeedback> feedback)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string[] already = feedback.Where(item => _appliedFeedbackIds.Contains(item.FeedbackId))
            .Select(item => item.FeedbackId).Distinct(StringComparer.Ordinal).ToArray();
        ContinuousExtendabilityFeedback[] pending = feedback
            .Where(item => !_appliedFeedbackIds.Contains(item.FeedbackId)).ToArray();
        LocalExtendabilityFeedbackApplication applied = LocalExtendabilityFeedbackBinder.Apply(
            _model, context, pending);
        foreach (string id in applied.EligibleFeedbackIds) _appliedFeedbackIds.Add(id);
        LocalExtendabilityFeedbackApplication result = applied with
        {
            AlreadyPresentFeedbackIds = already
        };
        if (result.AddedConstraintIds.Count > 0) _modelDirty = true;
        return result;
    }

    /// <summary>
    /// Adds a generation-only diversity row for each already pooled station mode.  The row
    /// requires at least one resource/stop alternative to change; it never modifies a business
    /// constraint and is not persisted as an infeasibility conclusion about the excluded mode.
    /// </summary>
    internal int ExcludeExistingResourcePatterns(IReadOnlyList<StationPattern> patterns)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(patterns);
        int added = 0;
        foreach (StationPattern pattern in patterns.Where(pattern =>
                     _activeStations.Contains(pattern.StationName, StringComparer.OrdinalIgnoreCase)))
        {
            Dictionary<string, VerifiedMicroAssignmentWitness> witnessByStop = pattern.Certificate.Witnesses
                .ToDictionary(witness => witness.RequirementId, StringComparer.OrdinalIgnoreCase);
            PromotedMicroChoiceVariableBinding[] selected = _augmentations
                .Where(augmentation => augmentation.ChoiceBindings.Any(binding => string.Equals(
                    binding.StationName, pattern.StationName, StringComparison.OrdinalIgnoreCase)))
                .SelectMany(augmentation => augmentation.ChoiceBindings)
                .Where(binding => witnessByStop.TryGetValue(binding.RequirementId, out VerifiedMicroAssignmentWitness? witness) &&
                                  witness.IsStop == binding.IsStop &&
                                  string.Equals(witness.TrackName, binding.TrackName, StringComparison.OrdinalIgnoreCase) &&
                                  witness.IncomingRouteId == binding.IncomingRouteId &&
                                  witness.OutgoingRouteId == binding.OutgoingRouteId)
                .ToArray();
            if (selected.Length != witnessByStop.Count)
                throw new InvalidDataException(
                    $"Cannot map pooled pattern '{pattern.PatternId}' into the local generation model: " +
                    $"mapped={selected.Length}, witnesses={witnessByStop.Count}.");
            List<(GRBVar Variable, int Value)> activeOrders = [];
            foreach (PromotedMicroOrderVariableBinding binding in _augmentations
                         .SelectMany(augmentation => augmentation.OrderBindings)
                         .Where(binding => string.Equals(binding.StationName, pattern.StationName,
                             StringComparison.OrdinalIgnoreCase)))
            {
                int[] values = pattern.Certificate.Conditions
                    .Select(condition => StationPatternOrderSignature.ResolveValue(
                        binding.FirstRequirementId, binding.SecondRequirementId, binding.Kind,
                        binding.FirstEventSide, binding.SecondEventSide, condition))
                    .Where(value => value.HasValue).Select(value => value!.Value).Distinct().ToArray();
                if (values.Length > 1)
                    throw new InvalidDataException(
                        $"Pattern '{pattern.PatternId}' has inconsistent conditions for order variable " +
                        $"'{binding.Variable.Get(GRB.StringAttr.VarName)}'.");
                if (values.Length == 1) activeOrders.Add((binding.Variable, values[0]));
            }

            GRBLinExpr sameChoices = new();
            foreach (PromotedMicroChoiceVariableBinding binding in selected)
                sameChoices.AddTerm(1.0, binding.Variable);
            foreach ((GRBVar variable, int value) in activeOrders)
            {
                if (value == 1) sameChoices.AddTerm(1.0, variable);
                else { sameChoices.AddConstant(1.0); sameChoices.AddTerm(-1.0, variable); }
            }
            int literalCount = selected.Length + activeOrders.Count;
            _raw.AddConstr(sameChoices, GRB.LESS_EQUAL, literalCount - 1,
                $"pattern_diversity_{IndependentStationPatternSolver.Sanitize(pattern.StationName)}_{pattern.PatternId[..12]}");
            added++;
        }
        if (added > 0)
        {
            _raw.Update();
            _modelDirty = true;
        }
        return added;
    }

    /// <summary>
    /// Adds an algorithmic generation target obtained from the current pattern-master proof.
    /// It does not alter the business domain: a value outside the original variable bounds is
    /// rejected and the caller must choose another proof-supported direction.
    /// </summary>
    internal string? RequireProofSupportedStop(string stopId, int value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (value is not (0 or 1)) throw new ArgumentOutOfRangeException(nameof(value));
        if (!_model.StopVars.TryGetValue(stopId, out GRBVar? variable)) return null;
        if (variable.Get(GRB.DoubleAttr.LB) > value + 1e-9 ||
            variable.Get(GRB.DoubleAttr.UB) < value - 1e-9) return null;
        string name = $"generation_proof_stop_{IndependentStationPatternSolver.Sanitize(stopId)}_{value}";
        if (_generationTargetConstraintIds.Contains(name)) return name;
        _raw.AddConstr(variable, GRB.EQUAL, value, name);
        _raw.Update();
        _generationTargetConstraintIds.Add(name);
        _modelDirty = true;
        return name;
    }

    /// <summary>
    /// Excludes the resource/stop assignment of the incumbent that is currently loaded in
    /// the retained generation model.  This is used only when the incumbent cannot be
    /// exported as a valid symbolic pattern; without the row, Continue would return the same
    /// rejected incumbent forever.  The row is generation diversity, not a business rule or
    /// a permanent infeasibility conclusion.
    /// </summary>
    internal int ExcludeCurrentResourceAssignment()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_raw.Get(GRB.IntAttr.SolCount) == 0) return 0;
        PromotedMicroChoiceVariableBinding[] selected = _augmentations
            .SelectMany(augmentation => augmentation.ChoiceBindings)
            .Where(binding => binding.Variable.Get(GRB.DoubleAttr.X) >= 0.5)
            .ToArray();
        if (selected.Length == 0)
            throw new InvalidDataException("A local incumbent contains no selected resource alternatives.");
        GRBLinExpr sameChoices = new();
        foreach (PromotedMicroChoiceVariableBinding binding in selected)
            sameChoices.AddTerm(1.0, binding.Variable);
        _generationOnlyExclusionCount++;
        _raw.AddConstr(sameChoices, GRB.LESS_EQUAL, selected.Length - 1,
            $"pattern_rejected_incumbent_{_generationOnlyExclusionCount:0000}");
        _raw.Update();
        _modelDirty = true;
        return selected.Length;
    }

    private IndependentStationPatternResult CaptureResult(int status)
    {
        if (_raw.Get(GRB.IntAttr.SolCount) == 0)
            return new(false, status, IndependentStationPatternSolver.StatusName(status), _runtimeSeconds,
                _firstIncumbentSeconds, _cumulativeLimitSeconds, null, [], [], _audit, null,
                $"station_pattern_no_incumbent_status_{status}");

        Dictionary<string, double> candidateArrivals = new(
            _reference.ArrivalSecondsByStopId, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, double> candidateDepartures = new(
            _reference.DepartureSecondsByStopId, StringComparer.OrdinalIgnoreCase);
        foreach ((string stopId, GRBVar variable) in _arrivals)
            candidateArrivals[stopId] = variable.Get(GRB.DoubleAttr.X);
        foreach ((string stopId, GRBVar variable) in _departures)
            candidateDepartures[stopId] = variable.Get(GRB.DoubleAttr.X);
        MasterSolutionSnapshot candidate = _reference with
        {
            ArrivalSecondsByStopId = candidateArrivals,
            DepartureSecondsByStopId = candidateDepartures,
            StopByStopId = new Dictionary<string, int>(_reference.StopByStopId, StringComparer.OrdinalIgnoreCase),
            VerifiedMicroAssignments = null
        };
        Dictionary<string, int> candidateStops = new(candidate.StopByStopId, StringComparer.OrdinalIgnoreCase);
        foreach ((string stopId, GRBVar variable) in _model.StopVars)
            candidateStops[stopId] = variable.Get(GRB.DoubleAttr.X) >= 0.5 ? 1 : 0;
        candidate = candidate with { StopByStopId = candidateStops };
        string fingerprint = SnapshotFingerprint.Calculate(candidate);
        IReadOnlyList<LocalMicroRepairSelectedAlternative> selected = _augmentations
            .SelectMany(augmentation => augmentation.CaptureSelectedAlternatives()).ToArray();
        VerifiedMicroAssignmentWitness[] witnesses = selected.Select(choice => new VerifiedMicroAssignmentWitness(
            choice.StationName,
            choice.RequirementId,
            choice.TrainIndex,
            choice.IsStop ?? candidateStops[choice.RequirementId] == 1,
            choice.TrackName,
            choice.IncomingRouteId,
            choice.OutgoingRouteId,
            fingerprint,
            candidateArrivals[choice.RequirementId],
            candidateDepartures[choice.RequirementId],
            "station_micro_validated")).ToArray();
        candidate = candidate with { VerifiedMicroAssignments = witnesses };
        List<MicroWitnessCertificate> patterns = [];
        foreach (string station in _activeStations)
        {
            MicroWitnessCertificateBuildResult build = MicroWitnessCertificateBuilder.Build(new(
                _instance, candidate,
                witnesses.Where(witness => string.Equals(
                    witness.StationName, station, StringComparison.OrdinalIgnoreCase)).ToArray(),
                $"{_instance.Meta.InstanceName}:{_instance.Trains.Count}:{_instance.TrainStops.Count}",
                $"routes={_instance.Routes.Count};headways={_instance.RouteHeadwayRules.Count};endpoint={_endpointOccupationSeconds}",
                CheckerToleranceSeconds: 0, _endpointOccupationSeconds));
            if (!build.IsSupported || build.Certificate is null)
                return new(false, status, IndependentStationPatternSolver.StatusName(status), _runtimeSeconds,
                    _firstIncumbentSeconds, _cumulativeLimitSeconds, candidate, [], witnesses, _audit, null,
                    $"pattern_certificate_rejected:{station}:{build.Reason}");
            patterns.Add(build.Certificate);
        }

        Dictionary<string, bool> selectedStopMode = selected.ToDictionary(
            choice => choice.RequirementId,
            choice => choice.IsStop ?? candidateStops[choice.RequirementId] == 1,
            StringComparer.OrdinalIgnoreCase);
        List<StationLevelMicroSubproblemResult> stationResults = [];
        foreach (string station in _activeStations)
        {
            List<StationMicroRequirement> updated = _requirements.Where(requirement =>
                    string.Equals(requirement.StationName, station, StringComparison.OrdinalIgnoreCase) &&
                    selectedStopMode.TryGetValue(requirement.StopId, out bool mode) && requirement.IsStop == mode)
                .Select(requirement => requirement with
                {
                    ArrivalSeconds = candidateArrivals[requirement.StopId],
                    DepartureSeconds = candidateDepartures[requirement.StopId]
                }).ToList();
            StationRouteTrackAssignmentResult assignment = new(
                true, [], "fixed-selected-resources", MicroSolveStatus.Feasible);
            stationResults.Add(new(
                station,
                updated,
                new StationConflictCore(station, updated.Select(item => item.TrainIndex).Distinct().ToArray(), [],
                    updated.Select(item => item.StopId).ToArray(), "none", 0),
                true, [], [], assignment));
        }
        StationLevelMicroFeasibilitySlaveResult analysis = new(
            true, stationResults, [], [], MicroSolveStatus.Feasible);
        VerifiedMicroAssignmentValidationReport validation = VerifiedMicroAssignmentValidator.Validate(
            _instance, candidate, witnesses, analysis, _endpointOccupationSeconds);
        bool valid = validation.IsValid;
        return new(valid, status, IndependentStationPatternSolver.StatusName(status), _runtimeSeconds,
            _firstIncumbentSeconds, _cumulativeLimitSeconds, candidate,
            valid ? patterns : [], witnesses, _audit, validation,
            valid ? "station_pattern_validated" : "fixed_resource_validation_failed");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _model.Dispose();
    }
}
