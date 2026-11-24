using System.Diagnostics;
using Gurobi;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

internal sealed record IndependentStationModelAudit(
    IReadOnlyList<string> ActiveStations,
    int RequirementCount,
    int SourceVariableCount,
    int SourceBinaryCount,
    int SourceConstraintCount,
    bool PresolveSizeAvailable,
    string PresolveStopReason,
    int PresolvedVariableCount,
    int PresolvedBinaryCount,
    int PresolvedConstraintCount,
    int ResourceChoiceBinaryCount,
    int ResourceOrderBinaryCount,
    int MacroBinaryCount,
    int ExternalStationResourceBinaryCount,
    int RuntimeCouplingCount,
    int ServiceCouplingCount);

internal sealed record IndependentStationPatternResult(
    bool HasPattern,
    int StatusCode,
    string Status,
    double RuntimeSeconds,
    double? FirstIncumbentSeconds,
    double CumulativeLimitSeconds,
    MasterSolutionSnapshot? CandidateSnapshot,
    IReadOnlyList<MicroWitnessCertificate> Patterns,
    IReadOnlyList<VerifiedMicroAssignmentWitness> Witnesses,
    IndependentStationModelAudit Audit,
    VerifiedMicroAssignmentValidationReport? FixedResourceValidation,
    string StopReason);

internal static class IndependentStationPatternSolver
{
    private const double QuotaBigM = 172800.0;

    internal static IndependentStationPatternResult Solve(
        TimetablingInstance instance,
        MasterSolutionSnapshot reference,
        IReadOnlyList<string> activeStations,
        int endpointOccupationSeconds,
        double initialTimeLimitSeconds,
        double cumulativeTimeLimitSeconds,
        string? logFile = null,
        double runningTimeSupplementRatio = 0.05)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(reference);
        string[] stations = activeStations.Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (stations.Length is < 1 or > 3)
            throw new ArgumentException("An independent resource-pattern solve must activate one to three stations.", nameof(activeStations));
        if (initialTimeLimitSeconds <= 0 || cumulativeTimeLimitSeconds < initialTimeLimitSeconds)
            throw new ArgumentOutOfRangeException(nameof(initialTimeLimitSeconds));

        HashSet<string> stationSet = stations.ToHashSet(StringComparer.OrdinalIgnoreCase);
        TrainStopRecord[] stops = instance.TrainStops.Where(stop => stationSet.Contains(stop.StationName))
            .OrderBy(stop => stop.StationName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(stop => stop.TrainIndex)
            .ThenBy(stop => stop.StationOrder)
            .ToArray();
        if (stops.Length == 0)
            throw new InvalidDataException($"No Micro requirements exist at active stations [{string.Join('|', stations)}].");

        GRBEnv environment = new();
        GRBModel raw = new(environment) { ModelName = $"independent_station_{string.Join('_', stations.Select(Sanitize))}" };
        Dictionary<string, GRBVar> arrivals = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, GRBVar> departures = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, GRBVar> stopVars = new(StringComparer.OrdinalIgnoreCase);
        foreach (TrainStopRecord stop in stops)
        {
            arrivals.Add(stop.StopId, raw.AddVar(stop.EarliestArrSeconds, stop.LatestArrSeconds, 0, GRB.CONTINUOUS,
                $"local_arr_t{stop.TrainIndex}_s{stop.StationOrder}"));
            departures.Add(stop.StopId, raw.AddVar(stop.EarliestDepSeconds, stop.LatestDepSeconds, 0, GRB.CONTINUOUS,
                $"local_dep_t{stop.TrainIndex}_s{stop.StationOrder}"));
            int stopValue = reference.StopByStopId.TryGetValue(stop.StopId, out int value)
                ? value
                : throw new InvalidDataException($"Reference snapshot lacks stop decision '{stop.StopId}'.");
            stopVars.Add(stop.StopId, raw.AddVar(stopValue, stopValue, 0, GRB.CONTINUOUS,
                $"local_stop_t{stop.TrainIndex}_s{stop.StationOrder}"));
        }

        using BasicMilpModel model = new(
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
        raw.Update();
        IReadOnlySet<string> overtakenStops = ResolveActuallyOvertakenStopIds(instance, reference);
        AddStationDwellConstraints(model, stops, overtakenStops);

        Dictionary<string, TrainRecord> trainByStop = instance.Trains
            .SelectMany(train => train.OrderedStops.Select(stop => (stop.StopId, train)))
            .ToDictionary(item => item.StopId, item => item.train, StringComparer.OrdinalIgnoreCase);
        List<StationMicroRequirement> requirements = stops.Select(stop =>
            StationLevelMicroFeasibilitySlave.BuildRequirementForMode(
                instance,
                trainByStop[stop.StopId],
                stop,
                reference.StopByStopId[stop.StopId] == 1,
                reference.ArrivalSecondsByStopId[stop.StopId],
                reference.DepartureSecondsByStopId[stop.StopId])).ToList();

        List<StationMicroRepairAugmentation> augmentations = [];
        foreach (string station in stations)
        {
            List<StationMicroRequirement> stationRequirements = requirements
                .Where(requirement => string.Equals(requirement.StationName, station, StringComparison.OrdinalIgnoreCase))
                .ToList();
            augmentations.Add(StationMicroRepairModelAugmenter.Apply(
                model,
                stationRequirements,
                endpointOccupationSeconds,
                variablePrefix: $"pattern_{Sanitize(station)}"));
        }

        int runtimeCouplings = AddRuntimeCouplings(
            model,
            instance,
            stops,
            stationSet,
            runningTimeSupplementRatio);
        int serviceCouplings = AddServiceCouplings(model, instance, stationSet, reference);
        AddRetimingObjective(model, reference);
        raw.Update();

        int choiceBinaries = augmentations.Sum(augmentation => augmentation.AlternativeCount);
        int orderBinaries = augmentations.Sum(augmentation => augmentation.OrderVariableCount);
        int sourceBinaries = raw.Get(GRB.IntAttr.NumBinVars);
        int macroBinaries = Math.Max(0, sourceBinaries - choiceBinaries - orderBinaries);
        int external = model.PromotedMicroChoiceBindings.Count(binding => !stationSet.Contains(binding.StationName));
        (bool presolveAvailable, string presolveStopReason, int presolvedVariables,
            int presolvedBinaries, int presolvedConstraints) = CapturePresolveSize(raw);
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

        if (!string.IsNullOrWhiteSpace(logFile))
        {
            string? directory = Path.GetDirectoryName(Path.GetFullPath(logFile));
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            raw.Set(GRB.StringParam.LogFile, logFile);
        }
        raw.Set(GRB.IntParam.Seed, 0);
        raw.Set(GRB.IntParam.Threads, 0);
        // A resource pattern only needs one independently valid assignment.  Continuing to
        // optimize the retiming preference would consume the closed-loop budget without
        // changing the feasibility acceptance rule.
        FirstIncumbentTrackingCallback incumbentCallback = new(stopAfterFirstIncumbent: true);
        raw.SetCallback(incumbentCallback);
        Stopwatch clock = Stopwatch.StartNew();
        double incumbentRuntimeOffset = 0;
        raw.Set(GRB.DoubleParam.TimeLimit, initialTimeLimitSeconds);
        model.Optimize();
        if (raw.Get(GRB.IntAttr.SolCount) == 0 &&
            raw.Get(GRB.IntAttr.Status) == GRB.Status.TIME_LIMIT &&
            cumulativeTimeLimitSeconds > initialTimeLimitSeconds)
        {
            incumbentRuntimeOffset = clock.Elapsed.TotalSeconds;
            raw.Set(GRB.DoubleParam.TimeLimit, cumulativeTimeLimitSeconds - initialTimeLimitSeconds);
            model.Optimize();
        }
        clock.Stop();
        int status = raw.Get(GRB.IntAttr.Status);
        double? firstIncumbentSeconds = incumbentCallback.FirstIncumbentTimeSeconds.HasValue
            ? incumbentRuntimeOffset + incumbentCallback.FirstIncumbentTimeSeconds.Value
            : null;
        if (raw.Get(GRB.IntAttr.SolCount) == 0)
            return new(false, status, StatusName(status), clock.Elapsed.TotalSeconds,
                firstIncumbentSeconds, cumulativeTimeLimitSeconds,
                null, [], [], audit, null, $"station_pattern_no_incumbent_status_{status}");

        Dictionary<string, double> candidateArrivals = new(reference.ArrivalSecondsByStopId, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, double> candidateDepartures = new(reference.DepartureSecondsByStopId, StringComparer.OrdinalIgnoreCase);
        foreach ((string stopId, GRBVar variable) in arrivals) candidateArrivals[stopId] = variable.Get(GRB.DoubleAttr.X);
        foreach ((string stopId, GRBVar variable) in departures) candidateDepartures[stopId] = variable.Get(GRB.DoubleAttr.X);
        MasterSolutionSnapshot candidate = reference with
        {
            ArrivalSecondsByStopId = candidateArrivals,
            DepartureSecondsByStopId = candidateDepartures,
            VerifiedMicroAssignments = null
        };
        string fingerprint = SnapshotFingerprint.Calculate(candidate);
        IReadOnlyList<LocalMicroRepairSelectedAlternative> selected = augmentations
            .SelectMany(augmentation => augmentation.CaptureSelectedAlternatives()).ToArray();
        Dictionary<string, StationMicroRequirement> requirementById = requirements.ToDictionary(
            requirement => requirement.StopId,
            StringComparer.OrdinalIgnoreCase);
        VerifiedMicroAssignmentWitness[] witnesses = selected.Select(choice => new VerifiedMicroAssignmentWitness(
            choice.StationName,
            choice.RequirementId,
            choice.TrainIndex,
            choice.IsStop ?? requirementById[choice.RequirementId].IsStop,
            choice.TrackName,
            choice.IncomingRouteId,
            choice.OutgoingRouteId,
            fingerprint,
            candidateArrivals[choice.RequirementId],
            candidateDepartures[choice.RequirementId],
            "station_micro_validated")).ToArray();
        candidate = candidate with { VerifiedMicroAssignments = witnesses };

        List<MicroWitnessCertificate> patterns = [];
        foreach (string station in stations)
        {
            MicroWitnessCertificateBuildResult build = MicroWitnessCertificateBuilder.Build(new(
                instance,
                candidate,
                witnesses.Where(witness => string.Equals(witness.StationName, station, StringComparison.OrdinalIgnoreCase)).ToArray(),
                $"{instance.Meta.InstanceName}:{instance.Trains.Count}:{instance.TrainStops.Count}",
                $"routes={instance.Routes.Count};headways={instance.RouteHeadwayRules.Count};endpoint={endpointOccupationSeconds}",
                CheckerToleranceSeconds: 0,
                endpointOccupationSeconds));
            if (!build.IsSupported || build.Certificate is null)
                return new(false, status, StatusName(status), clock.Elapsed.TotalSeconds,
                    firstIncumbentSeconds, cumulativeTimeLimitSeconds,
                    candidate, [], witnesses, audit, null, $"pattern_certificate_rejected:{station}:{build.Reason}");
            patterns.Add(build.Certificate);
        }

        List<StationLevelMicroSubproblemResult> stationResults = [];
        foreach (string station in stations)
        {
            List<StationMicroRequirement> updated = requirements.Where(requirement => string.Equals(
                    requirement.StationName, station, StringComparison.OrdinalIgnoreCase))
                .Select(requirement => requirement with
                {
                    ArrivalSeconds = candidateArrivals[requirement.StopId],
                    DepartureSeconds = candidateDepartures[requirement.StopId]
                }).ToList();
            StationRouteTrackAssignmentResult assignment = new(true, [], "fixed-selected-resources", MicroSolveStatus.Feasible);
            stationResults.Add(new(
                station,
                updated,
                new StationConflictCore(station, updated.Select(item => item.TrainIndex).Distinct().ToArray(), [],
                    updated.Select(item => item.StopId).ToArray(), "none", 0),
                true,
                [],
                [],
                assignment));
        }
        StationLevelMicroFeasibilitySlaveResult analysis = new(true, stationResults, [], [], MicroSolveStatus.Feasible);
        VerifiedMicroAssignmentValidationReport validation = VerifiedMicroAssignmentValidator.Validate(
            instance,
            candidate,
            witnesses,
            analysis,
            endpointOccupationSeconds);
        bool valid = validation.IsValid;
        return new(valid, status, StatusName(status), clock.Elapsed.TotalSeconds,
            firstIncumbentSeconds, cumulativeTimeLimitSeconds,
            candidate, valid ? patterns : [], witnesses, audit, validation,
            valid ? "station_pattern_validated" : "fixed_resource_validation_failed");
    }

    internal static void AddStationDwellConstraints(
        BasicMilpModel model,
        IReadOnlyList<TrainStopRecord> stops,
        IReadOnlySet<string> overtakenStops)
    {
        foreach (TrainStopRecord stop in stops.Where(stop => stop.StopRole == TrainStopRole.Intermediate))
        {
            NormalDwellResolution dwell = NormalDwellTimeResolver.Resolve(model.Instance.NormalDwellTimeRules, stop);
            GRBVar arrival = model.ArrivalVars[stop.StopId];
            GRBVar departure = model.DepartureVars[stop.StopId];
            GRBVar stopVar = model.StopVars[stop.StopId];
            model.Model.AddConstr(departure - arrival - dwell.NormalMinDwellSeconds * stopVar,
                GRB.GREATER_EQUAL, 0, $"local_min_dwell_t{stop.TrainIndex}_s{stop.StationOrder}");
            int maximum = overtakenStops.Contains(stop.StopId)
                ? dwell.WaitingMaxDwellSeconds
                : dwell.NormalMaxDwellSeconds;
            model.Model.AddConstr(departure - arrival - maximum * stopVar,
                GRB.LESS_EQUAL, 0, $"local_max_dwell_t{stop.TrainIndex}_s{stop.StationOrder}");
        }
    }

    internal static (bool Available, string StopReason, int Variables, int Binaries, int Constraints)
        CapturePresolveSize(GRBModel model)
    {
        try
        {
            using GRBModel presolved = model.Presolve();
            return (true, "presolved_model_available", presolved.Get(GRB.IntAttr.NumVars),
                presolved.Get(GRB.IntAttr.NumBinVars), presolved.Get(GRB.IntAttr.NumConstrs));
        }
        catch (GRBException exception)
        {
            return (false, $"presolved_model_unavailable:{exception.ErrorCode}:{exception.Message}", -1, -1, -1);
        }
    }

    internal static int AddRuntimeCouplings(
        BasicMilpModel model,
        TimetablingInstance instance,
        IReadOnlyList<TrainStopRecord> activeStops,
        IReadOnlySet<string> stationSet,
        double supplementRatio)
    {
        RunningTimeSupplementPolicy policy = RunningTimeSupplementPolicy.Create(supplementRatio);
        HashSet<string> activeIds = activeStops.Select(stop => stop.StopId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int count = 0;
        foreach (TrainRecord train in instance.Trains)
        for (int index = 0; index < train.OrderedStops.Count - 1; index++)
        {
            TrainStopRecord current = train.OrderedStops[index];
            TrainStopRecord next = train.OrderedStops[index + 1];
            if (!activeIds.Contains(current.StopId) || !activeIds.Contains(next.StopId) ||
                !stationSet.Contains(current.StationName) || !stationSet.Contains(next.StationName)) continue;
            RunTimeRule rule = ResolveRunTimeRule(instance, current, next);
            GRBVar supplement = model.Model.AddVar(0, policy.ResolveMaximumSeconds(rule.RunTimeSeconds), 0,
                GRB.CONTINUOUS, $"local_run_supp_t{train.TrainIndex}_{index}");
            GRBLinExpr expression = model.ArrivalVars[next.StopId] - model.DepartureVars[current.StopId] -
                                    rule.StartAddSeconds * model.StopVars[current.StopId] -
                                    rule.StopAddSeconds * model.StopVars[next.StopId] - supplement;
            model.Model.AddConstr(expression, GRB.EQUAL, rule.RunTimeSeconds,
                $"local_runtime_t{train.TrainIndex}_{index}");
            count++;
        }
        return count;
    }

    internal static int AddServiceCouplings(
        BasicMilpModel model,
        TimetablingInstance instance,
        IReadOnlySet<string> stationSet,
        MasterSolutionSnapshot reference)
    {
        int count = 0;
        foreach (TrainSectionStopQuotaConstruction rule in TrainSectionStopQuotaConstraintBuilder.Plan(instance))
        {
            TrainStopRecord[] active = rule.CountedStops.Where(stop => stationSet.Contains(stop.StationName) &&
                model.StopVars.ContainsKey(stop.StopId)).ToArray();
            if (active.Length == 0) continue;
            int fixedOutside = rule.CountedStops.Where(stop => !model.StopVars.ContainsKey(stop.StopId))
                .Sum(stop => reference.StopByStopId[stop.StopId]);
            GRBLinExpr sum = new();
            foreach (TrainStopRecord stop in active) sum.AddTerm(1, model.StopVars[stop.StopId]);
            model.Model.AddConstr(sum, GRB.GREATER_EQUAL, rule.MinStops - fixedOutside,
                $"local_service_min_{Sanitize(rule.RuleId)}");
            model.Model.AddConstr(sum, GRB.LESS_EQUAL, rule.MaxStops - fixedOutside,
                $"local_service_max_{Sanitize(rule.RuleId)}");
            count += 2;
        }

        foreach ((StationStopQuotaRule rule, int ruleIndex) in
                 instance.StationStopQuotaRules.Select((value, index) => (value, index)))
        {
            TrainStopRecord[] applicable = instance.TrainStops.Where(stop =>
                    string.Equals(stop.StationName, rule.StationName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(stop.TrainType, rule.TrainType, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            TrainStopRecord[] active = applicable.Where(stop =>
                    stationSet.Contains(stop.StationName) && model.StopVars.ContainsKey(stop.StopId))
                .ToArray();
            if (active.Length == 0) continue;

            int fixedOutside = 0;
            foreach (TrainStopRecord stop in applicable.Where(stop => !model.StopVars.ContainsKey(stop.StopId)))
            {
                bool isStop = reference.StopByStopId[stop.StopId] == 1;
                double eventTime = stop.StopRole == TrainStopRole.Origin
                    ? reference.DepartureSecondsByStopId[stop.StopId]
                    : reference.ArrivalSecondsByStopId[stop.StopId];
                if (MacroDiscreteStateResolver.ResolveQuotaCategory(
                        isStop, eventTime, rule.StartTimeSeconds, rule.EndTimeSeconds) ==
                    MacroQuotaCategory.InWindow)
                    fixedOutside++;
            }

            GRBLinExpr activeContribution = new();
            foreach (TrainStopRecord stop in active)
            {
                string suffix = $"r{ruleIndex + 1:D3}_t{stop.TrainIndex}_s{stop.StationOrder}";
                GRBVar inWindow = model.Model.AddVar(0, 1, 0, GRB.BINARY, $"local_quota_in_{suffix}");
                GRBVar leftOutside = model.Model.AddVar(0, 1, 0, GRB.BINARY, $"local_quota_left_{suffix}");
                GRBVar rightOutside = model.Model.AddVar(0, 1, 0, GRB.BINARY, $"local_quota_right_{suffix}");
                GRBVar eventTime = stop.StopRole == TrainStopRole.Origin
                    ? model.DepartureVars[stop.StopId]
                    : model.ArrivalVars[stop.StopId];
                GRBVar stopVariable = model.StopVars[stop.StopId];
                model.Model.AddConstr(inWindow + leftOutside + rightOutside - stopVariable,
                    GRB.EQUAL, 0, $"local_quota_class_{suffix}");
                model.Model.AddConstr(eventTime + QuotaBigM * leftOutside,
                    GRB.LESS_EQUAL, rule.StartTimeSeconds + QuotaBigM,
                    $"local_quota_left_ub_{suffix}");
                model.Model.AddConstr(eventTime - QuotaBigM * rightOutside,
                    GRB.GREATER_EQUAL, rule.EndTimeSeconds - QuotaBigM,
                    $"local_quota_right_lb_{suffix}");
                model.Model.AddConstr(eventTime - QuotaBigM * inWindow,
                    GRB.GREATER_EQUAL, rule.StartTimeSeconds - QuotaBigM,
                    $"local_quota_in_lb_{suffix}");
                model.Model.AddConstr(eventTime + QuotaBigM * inWindow,
                    GRB.LESS_EQUAL, rule.EndTimeSeconds + QuotaBigM,
                    $"local_quota_in_ub_{suffix}");
                activeContribution.AddTerm(1, inWindow);
                count += 5;
            }
            string ruleSuffix = $"{Sanitize(rule.StationName)}_{Sanitize(rule.TrainType)}_r{ruleIndex + 1:D3}";
            model.Model.AddConstr(activeContribution, GRB.GREATER_EQUAL,
                rule.MinStops - fixedOutside, $"local_station_quota_min_{ruleSuffix}");
            model.Model.AddConstr(activeContribution, GRB.LESS_EQUAL,
                rule.MaxStops - fixedOutside, $"local_station_quota_max_{ruleSuffix}");
            count += 2;
        }
        return count;
    }

    internal static void AddRetimingObjective(BasicMilpModel model, MasterSolutionSnapshot reference)
    {
        GRBLinExpr objective = new();
        foreach ((string stopId, GRBVar variable) in model.ArrivalVars)
            AddDeviation(model.Model, objective, variable, reference.ArrivalSecondsByStopId[stopId], $"arr_{stopId}");
        foreach ((string stopId, GRBVar variable) in model.DepartureVars)
            AddDeviation(model.Model, objective, variable, reference.DepartureSecondsByStopId[stopId], $"dep_{stopId}");
        model.Model.SetObjective(objective, GRB.MINIMIZE);
    }

    private static void AddDeviation(GRBModel model, GRBLinExpr objective, GRBVar variable, double reference, string id)
    {
        GRBVar deviation = model.AddVar(0, GRB.INFINITY, 0, GRB.CONTINUOUS, $"local_dev_{Sanitize(id)}");
        model.AddConstr(deviation - variable, GRB.GREATER_EQUAL, -reference, $"local_dev_pos_{Sanitize(id)}");
        model.AddConstr(deviation + variable, GRB.GREATER_EQUAL, reference, $"local_dev_neg_{Sanitize(id)}");
        objective.AddTerm(1, deviation);
    }

    /// <summary>Identify waiting visits with a resource-supported modeled order flip.</summary>
    /// <param name="instance">Original train visits and permanent route-track domains.</param>
    /// <param name="snapshot">Candidate order decisions by section and train pair.</param>
    /// <returns>The visit IDs that supply an overtaking dwell witness.</returns>
    internal static IReadOnlySet<string> ResolveActuallyOvertakenStopIds(
        TimetablingInstance instance,
        MasterSolutionSnapshot snapshot)
    {
        IReadOnlyList<SectionConflictCandidate> candidates = new InitialConflictGraphBuilder(instance, int.MaxValue).Build();
        Dictionary<string, SectionConflictCandidate> byKey = candidates.ToDictionary(
            BasicMilpModelBuilder.BuildSectionPrecedenceKey,
            StringComparer.OrdinalIgnoreCase);
        Dictionary<(int TrainIndex, string Station), TrainStopRecord> stopAtStation = instance.TrainStops
            .ToDictionary(stop => (stop.TrainIndex, stop.StationName.ToUpperInvariant()));
        HashSet<string> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (TrainRecord template in instance.Trains)
        for (int index = 1; index < template.OrderedStops.Count - 1; index++)
        {
            TrainStopRecord stationStop = template.OrderedStops[index];
            TrainStopRecord next = template.OrderedStops[index + 1];
            if (string.IsNullOrWhiteSpace(stationStop.IncomingSectionName) ||
                string.IsNullOrWhiteSpace(next.IncomingSectionName)) continue;
            foreach (SectionConflictCandidate incoming in candidates.Where(candidate => string.Equals(
                         candidate.SectionName, stationStop.IncomingSectionName, StringComparison.OrdinalIgnoreCase)))
            {
                string incomingKey = BasicMilpModelBuilder.BuildSectionPrecedenceKey(incoming);
                string outgoingKey = BasicMilpModelBuilder.BuildSectionPrecedenceKey(
                    next.IncomingSectionName!, incoming.FirstTrainIndex, incoming.SecondTrainIndex);
                if (!byKey.ContainsKey(outgoingKey) ||
                    !snapshot.SectionPrecedenceByKey.TryGetValue(incomingKey, out int incomingValue) ||
                    !snapshot.SectionPrecedenceByKey.TryGetValue(outgoingKey, out int outgoingValue)) continue;
                PairOvertakeState state = PairOvertakeResolver.Resolve(incomingValue, outgoingValue);
                string station = stationStop.StationName.ToUpperInvariant();
                if (!stopAtStation.TryGetValue((incoming.FirstTrainIndex, station), out TrainStopRecord? first) ||
                    !stopAtStation.TryGetValue((incoming.SecondTrainIndex, station), out TrainStopRecord? second)) continue;
                if (state.FirstBeingOvertaken && StationOvertakingResourcePolicy.CanOvertake(instance, first, second))
                    result.Add(first.StopId);
                if (state.SecondBeingOvertaken && StationOvertakingResourcePolicy.CanOvertake(instance, second, first))
                    result.Add(second.StopId);
            }
        }
        return result;
    }

    private static RunTimeRule ResolveRunTimeRule(
        TimetablingInstance instance,
        TrainStopRecord currentStop,
        TrainStopRecord nextStop)
    {
        if (string.IsNullOrWhiteSpace(nextStop.IncomingSectionName) ||
            !instance.SectionByName.TryGetValue(nextStop.IncomingSectionName, out SectionRecord? section) ||
            string.IsNullOrWhiteSpace(nextStop.RunScaleName))
            throw new InvalidDataException($"Cannot resolve runtime rule for '{currentStop.StopId}' -> '{nextStop.StopId}'.");
        TravelDirection direction = ResolveDirection(section, currentStop.StationName, nextStop.StationName);
        return instance.RunTimeRules.FirstOrDefault(rule =>
                   string.Equals(rule.ScaleName, nextStop.RunScaleName, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(rule.SectionName, nextStop.IncomingSectionName, StringComparison.OrdinalIgnoreCase) &&
                   rule.Direction == direction)
               ?? throw new InvalidDataException($"Runtime rule missing for '{currentStop.StopId}' -> '{nextStop.StopId}'.");
    }

    private static TravelDirection ResolveDirection(SectionRecord section, string from, string to)
    {
        if (string.Equals(section.FromStation, from, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(section.ToStation, to, StringComparison.OrdinalIgnoreCase)) return section.DirectionTag;
        if (string.Equals(section.ToStation, from, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(section.FromStation, to, StringComparison.OrdinalIgnoreCase))
            return section.DirectionTag == TravelDirection.Up ? TravelDirection.Down : TravelDirection.Up;
        throw new InvalidDataException($"Section '{section.SectionName}' does not connect '{from}' to '{to}'.");
    }

    internal static string StatusName(int status) => status switch
    {
        GRB.Status.OPTIMAL => "OPTIMAL",
        GRB.Status.INFEASIBLE => "INFEASIBLE",
        GRB.Status.TIME_LIMIT => "TIME_LIMIT",
        GRB.Status.INTERRUPTED => "INTERRUPTED",
        _ => status.ToString()
    };

    internal static string Sanitize(string value) => new(value.Select(character =>
        char.IsLetterOrDigit(character) ? character : '_').Take(120).ToArray());
}
