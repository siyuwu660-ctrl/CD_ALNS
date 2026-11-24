// 主解校验器直接根据实例规则复算时间窗、停时、运行时分、配额、天窗和先后间隔，验证 incumbent 的业务可行性。
// 校验结果按规则类型保存误差和摘要，与 Gurobi 求解状态相互独立，可发现建模遗漏或导出数值异常。
using System.Globalization;
using System.Text;
using Gurobi;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

/// <summary>
/// 对主问题求解结果执行独立的业务规则复算。
/// 校验器把变量还原为列车、车站、区间和停站对象，并输出 violation 明细，
/// 用于区分宏观约束违反与微观资源不可行，也可直接形成实验诊断材料。
/// </summary>
public sealed class MasterSolutionValidator
{
    private const double ToleranceSeconds = GurobiTimetablingResearch.Domain.TimetableTimePolicy.NumericToleranceSeconds;

    public MasterValidationReport Validate(BasicMilpModel model)
    {
        if (!model.HasIncumbentSolution)
        {
            return new MasterValidationReport(false, new[] { "Gurobi has no incumbent solution." }, []);
        }

        List<string> violations = new();
        List<SectionConflictEdgeRequest> suggestedSectionEdges = [];
        List<SectionRelationValidationFinding> sectionRelationFindings = [];

        ValidateTimeWindows(model, violations);
        ValidateStopLogic(model, violations);
        ValidateDwellTimes(model, violations);
        ValidateRunTimes(model, violations);
        ValidateMaintenanceWindows(model, violations);
        ValidateStationStopQuotas(model, violations);
        ValidateTrainSectionStopQuotas(model, violations);
        ValidateTrainSectionTravelTimeLimits(model, violations);
        ValidateAllApplicableSectionRelations(
            model,
            violations,
            suggestedSectionEdges,
            sectionRelationFindings);
        ValidateStationEventHeadways(model, violations);
        ValidateStationCapabilityOvertaking(model, violations);
        ValidateActualStationOrders(model, violations, suggestedSectionEdges, sectionRelationFindings);

        return new MasterValidationReport(
            violations.Count == 0,
            violations,
            suggestedSectionEdges,
            sectionRelationFindings);
    }

    public void WriteReport(MasterValidationReport report, string path)
    {
        StringBuilder builder = new();
        builder.AppendLine("# Master Solution Validation Report");
        builder.AppendLine();
        builder.AppendLine($"- feasible_by_validator: `{report.IsFeasible}`");
        builder.AppendLine($"- violation_count: `{report.Violations.Count}`");
        builder.AppendLine($"- suggested_section_edge_count: `{report.SuggestedSectionEdges?.Count ?? 0}`");
        builder.AppendLine($"- missing_relation_violation_count: `{report.SectionRelationFindings?.Count(item => !item.WasModeled) ?? 0}`");
        builder.AppendLine($"- modeled_relation_violation_count: `{report.SectionRelationFindings?.Count(item => item.WasModeled) ?? 0}`");
        builder.AppendLine();

        if (report.Violations.Count == 0)
        {
            builder.AppendLine("No macro-level violations were detected by the current validator.");
        }
        else
        {
            builder.AppendLine("## Violations");
            builder.AppendLine();
            foreach (string violation in report.Violations)
            {
                builder.AppendLine($"- {violation}");
            }
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static void ValidateTimeWindows(BasicMilpModel model, ICollection<string> violations)
    {
        foreach (TrainStopRecord stop in model.Instance.TrainStops)
        {
            double arrival = Value(model.ArrivalVars[stop.StopId]);
            double departure = Value(model.DepartureVars[stop.StopId]);

            if (arrival < stop.EarliestArrSeconds - ToleranceSeconds || arrival > stop.LatestArrSeconds + ToleranceSeconds)
            {
                violations.Add($"Arrival time window violated: train={stop.TrainIndex}, station={stop.StationName}, value={arrival.ToString("F3", CultureInfo.InvariantCulture)}.");
            }

            if (departure < stop.EarliestDepSeconds - ToleranceSeconds || departure > stop.LatestDepSeconds + ToleranceSeconds)
            {
                violations.Add($"Departure time window violated: train={stop.TrainIndex}, station={stop.StationName}, value={departure.ToString("F3", CultureInfo.InvariantCulture)}.");
            }
        }
    }

    private static void ValidateStopLogic(BasicMilpModel model, ICollection<string> violations)
    {
        foreach (TrainStopRecord stop in model.Instance.TrainStops)
        {
            int isStop = BinaryValue(model.StopVars[stop.StopId]);

            if (stop.StopRequirement == StopRequirement.MustStop && isStop != 1)
            {
                violations.Add($"Must-stop violated: train={stop.TrainIndex}, station={stop.StationName}.");
            }

            if (stop.StopRequirement == StopRequirement.MustPass && isStop != 0)
            {
                violations.Add($"Must-pass violated: train={stop.TrainIndex}, station={stop.StationName}.");
            }

            if (stop.StopRole is TrainStopRole.Origin or TrainStopRole.Destination && isStop != 1)
            {
                violations.Add($"Terminal stop violated: train={stop.TrainIndex}, station={stop.StationName}, role={stop.StopRole}.");
            }
        }
    }

    private static void ValidateDwellTimes(BasicMilpModel model, ICollection<string> violations)
    {
        var overtakers = OvertakingMultiplicityPolicy.FindOvertakers(model.Instance,
            id => Value(model.ArrivalVars[id]), id => Value(model.DepartureVars[id]));
        foreach (TrainStopRecord stop in model.Instance.TrainStops)
        {
            if (stop.StopRole != TrainStopRole.Intermediate)
            {
                continue;
            }

            double dwell = Value(model.DepartureVars[stop.StopId]) - Value(model.ArrivalVars[stop.StopId]);
            int isStop = BinaryValue(model.StopVars[stop.StopId]);
            NormalDwellResolution normal = NormalDwellTimeResolver.Resolve(model.Instance.NormalDwellTimeRules, stop);
            bool isActuallyOvertaken = overtakers.TryGetValue(stop.StopId, out var overtakingTrains);
            if (overtakingTrains is { Count: > 1 })
                violations.Add($"multiple_overtaking: train={stop.TrainIndex}, station={stop.StationName}, " +
                    $"count={overtakingTrains.Count}, maximum=1, overtakers=[{string.Join(',', overtakingTrains)}].");
            bool waitingVariableSelected = model.BeingOvertakenVars.TryGetValue(stop.StopId, out GRBVar? waitingVariable) &&
                BinaryValue(waitingVariable) == 1;
            double minRequired = normal.NormalMinDwellSeconds * isStop;
            double maxAllowed = (isActuallyOvertaken
                ? normal.WaitingMaxDwellSeconds
                : normal.NormalMaxDwellSeconds) * isStop;

            if (dwell < minRequired - ToleranceSeconds || dwell > maxAllowed + ToleranceSeconds)
            {
                string violationType = isActuallyOvertaken
                    ? "waiting_dwell_violation"
                    : "normal_dwell_violation";
                violations.Add($"{violationType}: train={stop.TrainIndex}, station={stop.StationName}, dwell={dwell.ToString("F3", CultureInfo.InvariantCulture)}, is_stop={isStop}.");
            }
            if (waitingVariableSelected && (!isActuallyOvertaken || isStop == 0))
            {
                violations.Add($"invalid_waiting_without_overtake: train={stop.TrainIndex}, station={stop.StationName}, is_stop={isStop}.");
            }
        }
    }

    private static void ValidateRunTimes(BasicMilpModel model, ICollection<string> violations)
    {
        foreach (TrainRecord train in model.Instance.Trains)
        {
            for (int index = 0; index < train.OrderedStops.Count - 1; index++)
            {
                TrainStopRecord current = train.OrderedStops[index];
                TrainStopRecord next = train.OrderedStops[index + 1];
                RunTimeRule rule = ResolveRunTimeRule(model.Instance, current, next);
                double realizedRunTime = Value(model.ArrivalVars[next.StopId]) - Value(model.DepartureVars[current.StopId]);
                int requiredRunTime = SectionRuntimeCalculator.ResolveRequiredSeconds(
                    rule,
                    BinaryValue(model.StopVars[current.StopId]) == 1,
                    BinaryValue(model.StopVars[next.StopId]) == 1);

                int maximumSupplement = RunningTimeSupplementPolicy.Create(model.RunningTimeSupplementRatio)
                    .ResolveMaximumSeconds(rule.RunTimeSeconds);
                RunningTimeValidationOutcome outcome = RunningTimeSupplementValidator.Classify(
                    realizedRunTime,
                    requiredRunTime,
                    maximumSupplement,
                    ToleranceSeconds);
                if (outcome != RunningTimeValidationOutcome.Valid)
                {
                    string violationType = outcome == RunningTimeValidationOutcome.BelowMinimum
                        ? "running_time_below_minimum"
                        : "running_time_supplement_exceeded";
                    violations.Add($"{violationType}: train={train.TrainIndex}, section={next.IncomingSectionName}, value={realizedRunTime.ToString("F3", CultureInfo.InvariantCulture)}, required={requiredRunTime}, maximum_supplement={maximumSupplement}.");
                }
            }
        }
    }

    private static void ValidateTrainSectionStopQuotas(
        BasicMilpModel model,
        ICollection<string> violations)
    {
        Dictionary<string, int> stopValues = model.StopVars.ToDictionary(
            item => item.Key,
            item => BinaryValue(item.Value),
            StringComparer.OrdinalIgnoreCase);
        foreach (TrainSectionStopQuotaBusinessFinding finding in
                 TrainSectionStopQuotaBusinessValidator.Validate(model.Instance, stopValues))
        {
            violations.Add(
                $"section_stop_{finding.FindingKind.ToLowerInvariant()}: rule={finding.RuleId}, " +
                $"train={finding.TrainIndex}, boundaries={finding.SectionStartStation}/{finding.SectionEndStation}, " +
                $"expected=[{finding.MinStops},{finding.MaxStops}], actual={finding.ActualStops?.ToString(CultureInfo.InvariantCulture) ?? "missing"}, " +
                $"shortfall={finding.Shortfall}, excess={finding.Excess}, detail={finding.Detail}");
        }
    }

    private static void ValidateStationStopQuotas(
        BasicMilpModel model,
        ICollection<string> violations)
    {
        foreach (StationStopQuotaRule rule in model.Instance.StationStopQuotaRules)
        {
            int actual = 0;
            foreach (TrainStopRecord stop in model.Instance.TrainStops.Where(stop =>
                         string.Equals(stop.StationName, rule.StationName, StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(stop.TrainType, rule.TrainType, StringComparison.OrdinalIgnoreCase)))
            {
                if (BinaryValue(model.StopVars[stop.StopId]) != 1)
                {
                    continue;
                }
                double eventTime = stop.StopRole == TrainStopRole.Origin
                    ? Value(model.DepartureVars[stop.StopId])
                    : Value(model.ArrivalVars[stop.StopId]);
                if (StationQuotaWindowPolicy.Contains(eventTime, rule.StartTimeSeconds, rule.EndTimeSeconds))
                {
                    actual++;
                }
            }
            if (actual < rule.MinStops || actual > rule.MaxStops)
            {
                violations.Add(
                    $"station_stop_quota_violation: station={rule.StationName}, train_type={rule.TrainType}, " +
                    $"window=[{rule.StartTimeSeconds},{rule.EndTimeSeconds}], expected=[{rule.MinStops},{rule.MaxStops}], actual={actual}.");
            }
        }
    }

    private static void ValidateMaintenanceWindows(
        BasicMilpModel model,
        ICollection<string> violations)
    {
        foreach (TrainRecord train in model.Instance.Trains)
        {
            for (int index = 0; index < train.OrderedStops.Count - 1; index++)
            {
                TrainStopRecord current = train.OrderedStops[index];
                TrainStopRecord next = train.OrderedStops[index + 1];
                if (string.IsNullOrWhiteSpace(next.IncomingSectionName) ||
                    !model.Instance.SectionByName.TryGetValue(next.IncomingSectionName, out SectionRecord? section))
                {
                    violations.Add(
                        $"maintenance_rule_mapping_error: train={train.TrainIndex}, from={current.StationName}, to={next.StationName}, section={next.IncomingSectionName ?? "missing"}.");
                    continue;
                }
                TravelDirection direction;
                try
                {
                    direction = DetermineOperationalDirection(section, current.StationName, next.StationName);
                }
                catch (InvalidOperationException exception)
                {
                    violations.Add(
                        $"maintenance_rule_mapping_error: train={train.TrainIndex}, section={section.SectionName}, detail={exception.Message}");
                    continue;
                }
                double departure = Value(model.DepartureVars[current.StopId]);
                double arrival = Value(model.ArrivalVars[next.StopId]);
                foreach (MaintenanceWindowRule window in model.Instance.MaintenanceWindows.Where(window =>
                             string.Equals(window.SectionName, section.SectionName, StringComparison.OrdinalIgnoreCase) &&
                             window.Direction == direction))
                {
                    if (arrival > window.StartTimeSeconds + ToleranceSeconds &&
                        departure < window.EndTimeSeconds - ToleranceSeconds)
                    {
                        violations.Add(
                            $"maintenance_window_violation: train={train.TrainIndex}, section={section.SectionName}, " +
                            $"direction={direction}, traversal=[{departure.ToString("F3", CultureInfo.InvariantCulture)},{arrival.ToString("F3", CultureInfo.InvariantCulture)}], " +
                            $"window=[{window.StartTimeSeconds},{window.EndTimeSeconds}].");
                    }
                }
            }
        }
    }

    private static void ValidateSectionHeadways(BasicMilpModel model, ICollection<string> violations)
    {
        Dictionary<(int TrainIndex, int StationOrder), TrainStopRecord> stopByTrainAndOrder = model.Instance.TrainStops
            .ToDictionary(stop => (stop.TrainIndex, stop.StationOrder));

        foreach (SectionConflictCandidate candidate in model.SectionConflictCandidates)
        {
            string key = BasicMilpModelBuilder.BuildSectionPrecedenceKey(candidate);
            if (!model.SectionPrecedenceVars.TryGetValue(key, out GRBVar? precedenceVar))
            {
                continue;
            }

            if (!stopByTrainAndOrder.TryGetValue((candidate.FirstTrainIndex, candidate.FirstStopOrder), out TrainStopRecord? firstStart) ||
                !stopByTrainAndOrder.TryGetValue((candidate.SecondTrainIndex, candidate.SecondStopOrder), out TrainStopRecord? secondStart) ||
                !stopByTrainAndOrder.TryGetValue((candidate.FirstTrainIndex, candidate.FirstEndStopOrder), out TrainStopRecord? firstEnd) ||
                !stopByTrainAndOrder.TryGetValue((candidate.SecondTrainIndex, candidate.SecondEndStopOrder), out TrainStopRecord? secondEnd))
            {
                continue;
            }

            bool firstBeforeSecond = Value(precedenceVar) < 0.5;
            ValidateSectionHeadwaySide(model, candidate, firstStart, secondStart, firstBeforeSecond, true, violations);
            ValidateSectionHeadwaySide(model, candidate, firstEnd, secondEnd, firstBeforeSecond, false, violations);
        }
    }

    private static void ValidateAllApplicableSectionRelations(
        BasicMilpModel model,
        ICollection<string> violations,
        ICollection<SectionConflictEdgeRequest> suggestedSectionEdges,
        ICollection<SectionRelationValidationFinding> sectionRelationFindings)
    {
        HashSet<string> modeledPairs = model.SectionConflictCandidates
            .Select(candidate => InitialConflictGraphBuilder.BuildCanonicalPairKey(
                candidate.SectionName,
                candidate.FirstTrainIndex,
                candidate.SecondTrainIndex))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> suggestedPairs = new(StringComparer.OrdinalIgnoreCase);
        foreach (SectionRecord section in model.Instance.Sections)
        {
            List<(TrainRecord Train, TrainStopRecord Start, TrainStopRecord End, TravelDirection Direction)> traversals = [];
            foreach (TrainRecord train in model.Instance.Trains)
            {
                for (int index = 0; index < train.OrderedStops.Count - 1; index++)
                {
                    TrainStopRecord start = train.OrderedStops[index];
                    TrainStopRecord end = train.OrderedStops[index + 1];
                    if (!string.Equals(end.IncomingSectionName, section.SectionName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    traversals.Add((
                        train,
                        start,
                        end,
                        DetermineOperationalDirection(section, start.StationName, end.StationName)));
                    break;
                }
            }

            for (int firstIndex = 0; firstIndex < traversals.Count; firstIndex++)
            {
                for (int secondIndex = firstIndex + 1; secondIndex < traversals.Count; secondIndex++)
                {
                    var first = traversals[firstIndex];
                    var second = traversals[secondIndex];
                    string pairKey = InitialConflictGraphBuilder.BuildCanonicalPairKey(
                        section.SectionName,
                        first.Train.TrainIndex,
                        second.Train.TrainIndex);
                    int beforeViolationCount = violations.Count;
                    double firstDeparture = Value(model.DepartureVars[first.Start.StopId]);
                    double secondDeparture = Value(model.DepartureVars[second.Start.StopId]);
                    double firstArrival = Value(model.ArrivalVars[first.End.StopId]);
                    double secondArrival = Value(model.ArrivalVars[second.End.StopId]);
                    if (first.Direction == second.Direction)
                    {
                        int departureOrder = CompareTimes(firstDeparture, secondDeparture);
                        int arrivalOrder = CompareTimes(firstArrival, secondArrival);
                        if (departureOrder != 0 && arrivalOrder != 0 && departureOrder != arrivalOrder)
                        {
                            violations.Add(
                                $"section_order_continuity_violation: section={section.SectionName}, " +
                                $"pair={first.Train.TrainIndex}/{second.Train.TrainIndex}, " +
                                $"departures={firstDeparture.ToString("F3", CultureInfo.InvariantCulture)}/{secondDeparture.ToString("F3", CultureInfo.InvariantCulture)}, " +
                                $"arrivals={firstArrival.ToString("F3", CultureInfo.InvariantCulture)}/{secondArrival.ToString("F3", CultureInfo.InvariantCulture)}.");
                        }
                        ValidateAllPairHeadway(
                            model,
                            section.SectionName,
                            first.Start,
                            second.Start,
                            firstDeparture,
                            secondDeparture,
                            isOriginSide: true,
                            violations);
                        ValidateAllPairHeadway(
                            model,
                            section.SectionName,
                            first.End,
                            second.End,
                            firstArrival,
                            secondArrival,
                            isOriginSide: false,
                            violations);
                    }
                    else if (section.SectionType.Contains("单线", StringComparison.OrdinalIgnoreCase) &&
                             firstDeparture < secondArrival - ToleranceSeconds &&
                             firstArrival > secondDeparture + ToleranceSeconds)
                    {
                        violations.Add(
                            $"single_track_opposite_direction_violation: section={section.SectionName}, " +
                            $"pair={first.Train.TrainIndex}/{second.Train.TrainIndex}, " +
                            $"intervals=[{firstDeparture.ToString("F3", CultureInfo.InvariantCulture)},{firstArrival.ToString("F3", CultureInfo.InvariantCulture)}]/" +
                            $"[{secondDeparture.ToString("F3", CultureInfo.InvariantCulture)},{secondArrival.ToString("F3", CultureInfo.InvariantCulture)}].");
                    }

                    if (violations.Count > beforeViolationCount &&
                        !modeledPairs.Contains(pairKey) &&
                        suggestedPairs.Add(pairKey))
                    {
                        suggestedSectionEdges.Add(new SectionConflictEdgeRequest(
                            section.SectionName,
                            first.Train.TrainIndex,
                            second.Train.TrainIndex));
                    }
                    if (violations.Count > beforeViolationCount)
                    {
                        sectionRelationFindings.Add(new SectionRelationValidationFinding(
                            section.SectionName,
                            first.Train.TrainIndex,
                            second.Train.TrainIndex,
                            modeledPairs.Contains(pairKey),
                            violations.Skip(beforeViolationCount).ToList()));
                    }
                }
            }
        }
    }

    private static void ValidateAllPairHeadway(
        BasicMilpModel model,
        string sectionName,
        TrainStopRecord firstStop,
        TrainStopRecord secondStop,
        double firstTime,
        double secondTime,
        bool isOriginSide,
        ICollection<string> violations)
    {
        TrainStopRecord leading = firstTime <= secondTime ? firstStop : secondStop;
        TrainStopRecord following = firstTime <= secondTime ? secondStop : firstStop;
        double gap = Math.Abs(secondTime - firstTime);
        int required = MasterRouteDomainHeadwayProvider.ResolveLowerBound(
            model.Instance,
            leading,
            BinaryValue(model.StopVars[leading.StopId]) == 1,
            following,
            BinaryValue(model.StopVars[following.StopId]) == 1,
            sectionName,
            isOriginSide);
        if (gap < required - ToleranceSeconds)
        {
            violations.Add(
                $"section_all_pair_{(isOriginSide ? "origin" : "destination")}_headway_violation: " +
                $"section={sectionName}, pair={firstStop.TrainIndex}/{secondStop.TrainIndex}, " +
                $"leading={leading.TrainIndex}, following={following.TrainIndex}, " +
                $"realized={gap.ToString("F3", CultureInfo.InvariantCulture)}, required={required}.");
        }
    }

    private static int CompareTimes(double first, double second) =>
        second - first > ToleranceSeconds ? -1 : first - second > ToleranceSeconds ? 1 : 0;

    private static void ValidateSectionHeadwaySide(
        BasicMilpModel model,
        SectionConflictCandidate candidate,
        TrainStopRecord firstStop,
        TrainStopRecord secondStop,
        bool firstBeforeSecond,
        bool isOriginSide,
        ICollection<string> violations)
    {
        TrainStopRecord leadingStop = firstBeforeSecond ? firstStop : secondStop;
        TrainStopRecord followingStop = firstBeforeSecond ? secondStop : firstStop;
        bool leadingIsStop = BinaryValue(model.StopVars[leadingStop.StopId]) == 1;
        bool followingIsStop = BinaryValue(model.StopVars[followingStop.StopId]) == 1;
        int headway = MasterRouteDomainHeadwayProvider.ResolveLowerBound(
            model.Instance,
            leadingStop,
            leadingIsStop,
            followingStop,
            followingIsStop,
            candidate.SectionName,
            isOriginSide);
        double firstTime = Value(isOriginSide ? model.DepartureVars[firstStop.StopId] : model.ArrivalVars[firstStop.StopId]);
        double secondTime = Value(isOriginSide ? model.DepartureVars[secondStop.StopId] : model.ArrivalVars[secondStop.StopId]);
        double realizedHeadway = firstBeforeSecond ? secondTime - firstTime : firstTime - secondTime;

        if (realizedHeadway < headway - ToleranceSeconds)
        {
            string side = isOriginSide ? "origin" : "destination";
            violations.Add(
                $"Section {side} headway violated: section={candidate.SectionName}, " +
                $"pair={candidate.FirstTrainIndex}/{candidate.SecondTrainIndex}, " +
                $"realized={realizedHeadway.ToString("F3", CultureInfo.InvariantCulture)}, required={headway}.");
        }
    }

    private static void ValidateTrainSectionTravelTimeLimits(BasicMilpModel model, ICollection<string> violations)
    {
        Dictionary<int, TrainRecord> trainByIndex = model.Instance.Trains.ToDictionary(train => train.TrainIndex);
        foreach (TrainSectionTravelTimeLimitRule rule in model.Instance.TrainSectionTravelTimeLimitRules)
        {
            if (!trainByIndex.TryGetValue(rule.TrainIndex, out TrainRecord? train))
            {
                violations.Add($"Maximum travel-time rule references missing train: train={rule.TrainIndex}.");
                continue;
            }

            (TrainStopRecord startStop, TrainStopRecord endStop) =
                TrainSectionTravelTimeResolver.Resolve(train, rule);
            double departure = Value(model.DepartureVars[startStop.StopId]);
            double arrival = Value(model.ArrivalVars[endStop.StopId]);
            if (!TravelTimeLimitCalculator.IsSatisfied(departure, arrival, rule.MaxTravelTimeSeconds, ToleranceSeconds))
            {
                double elapsed = TravelTimeLimitCalculator.ResolveElapsedSeconds(departure, arrival);
                violations.Add(
                    $"Maximum travel time violated: train={rule.TrainIndex}, start={rule.SectionStartStation}, " +
                    $"end={rule.SectionEndStation}, elapsed={elapsed.ToString("F3", CultureInfo.InvariantCulture)}, " +
                    $"maximum={rule.MaxTravelTimeSeconds}.");
            }
        }
    }

    private static void ValidateStationEventHeadways(BasicMilpModel model, ICollection<string> violations)
    {
        Dictionary<(int TrainIndex, int StationOrder), TrainStopRecord> stops = model.Instance.TrainStops
            .ToDictionary(stop => (stop.TrainIndex, stop.StationOrder));
        foreach (StationEventConflictCandidate candidate in model.StationEventConflictCandidates)
        {
            string key = BasicMilpModelBuilder.BuildStationEventPrecedenceKey(candidate);
            if (!model.StationEventPrecedenceVars.TryGetValue(key, out GRBVar? precedence) ||
                !stops.TryGetValue((candidate.FirstTrainIndex, candidate.FirstStationOrder), out TrainStopRecord? firstStop) ||
                !stops.TryGetValue((candidate.SecondTrainIndex, candidate.SecondStationOrder), out TrainStopRecord? secondStop))
            {
                continue;
            }

            stops.TryGetValue((candidate.FirstTrainIndex, candidate.FirstStationOrder + 1), out TrainStopRecord? firstNext);
            stops.TryGetValue((candidate.SecondTrainIndex, candidate.SecondStationOrder + 1), out TrainStopRecord? secondNext);
            bool firstBeforeSecond = Value(precedence) < 0.5;
            TrainStopRecord leadingStop = firstBeforeSecond ? firstStop : secondStop;
            TrainStopRecord followingStop = firstBeforeSecond ? secondStop : firstStop;
            TrainStopRecord? leadingNext = firstBeforeSecond ? firstNext : secondNext;
            TrainStopRecord? followingNext = firstBeforeSecond ? secondNext : firstNext;
            StationEventSide leadingSide = firstBeforeSecond ? candidate.FirstSide : candidate.SecondSide;
            StationEventSide followingSide = firstBeforeSecond ? candidate.SecondSide : candidate.FirstSide;
            bool leadingIsStop = BinaryValue(model.StopVars[leadingStop.StopId]) == 1;
            bool followingIsStop = BinaryValue(model.StopVars[followingStop.StopId]) == 1;
            int required = MasterRouteDomainHeadwayProvider.ResolveStationLowerBound(
                model.Instance, leadingStop, leadingNext, leadingSide, leadingIsStop,
                followingStop, followingNext, followingSide, followingIsStop);
            double firstTime = Value(candidate.FirstSide == StationEventSide.Incoming
                ? model.ArrivalVars[firstStop.StopId]
                : model.DepartureVars[firstStop.StopId]);
            double secondTime = Value(candidate.SecondSide == StationEventSide.Incoming
                ? model.ArrivalVars[secondStop.StopId]
                : model.DepartureVars[secondStop.StopId]);
            double realized = firstBeforeSecond ? secondTime - firstTime : firstTime - secondTime;
            if (realized < required - ToleranceSeconds)
            {
                violations.Add(
                    $"Station event headway violated: station={candidate.StationName}, " +
                    $"pair={candidate.FirstTrainIndex}/{candidate.SecondTrainIndex}, " +
                    $"sides={candidate.FirstSide}/{candidate.SecondSide}, " +
                    $"realized={realized.ToString("F3", CultureInfo.InvariantCulture)}, required={required}.");
            }
        }
    }

    /// <summary>Check resource support for each modeled section-order flip.</summary>
    /// <param name="model">Solved model whose native variables are borrowed.</param>
    /// <param name="violations">Receives any unsupported modeled flip.</param>
    private static void ValidateStationCapabilityOvertaking(BasicMilpModel model, ICollection<string> violations)
    {
        Dictionary<string, SectionConflictCandidate> candidateByKey = model.SectionConflictCandidates.ToDictionary(
            BasicMilpModelBuilder.BuildSectionPrecedenceKey,
            StringComparer.OrdinalIgnoreCase);

        foreach ((string stationName, string incomingSectionName, string outgoingSectionName) in BuildStationAdjacentSectionPairs(model.Instance))
        {
            IEnumerable<SectionConflictCandidate> incomingCandidates = model.SectionConflictCandidates
                .Where(candidate => string.Equals(candidate.SectionName, incomingSectionName, StringComparison.OrdinalIgnoreCase));

            foreach (SectionConflictCandidate incomingCandidate in incomingCandidates)
            {
                string outgoingCandidateKey = $"{outgoingSectionName}::{incomingCandidate.FirstTrainIndex}::{incomingCandidate.SecondTrainIndex}";
                if (!candidateByKey.TryGetValue(outgoingCandidateKey, out SectionConflictCandidate? outgoingCandidate))
                {
                    continue;
                }

                string incomingKey = BasicMilpModelBuilder.BuildSectionPrecedenceKey(incomingCandidate);
                string outgoingKey = BasicMilpModelBuilder.BuildSectionPrecedenceKey(outgoingCandidate);
                if (!model.SectionPrecedenceVars.TryGetValue(incomingKey, out GRBVar? incomingVar) ||
                    !model.SectionPrecedenceVars.TryGetValue(outgoingKey, out GRBVar? outgoingVar))
                {
                    continue;
                }

                var firstVisit = model.Instance.TrainStops.Single(stop => stop.TrainIndex == incomingCandidate.FirstTrainIndex && stop.StationName.Equals(stationName, StringComparison.OrdinalIgnoreCase));
                var secondVisit = model.Instance.TrainStops.Single(stop => stop.TrainIndex == incomingCandidate.SecondTrainIndex && stop.StationName.Equals(stationName, StringComparison.OrdinalIgnoreCase));
                PairOvertakeState reversal = PairOvertakeResolver.Resolve(
                    (int)Math.Round(Value(incomingVar)), (int)Math.Round(Value(outgoingVar)));
                if ((reversal.FirstBeingOvertaken && !StationOvertakingResourcePolicy.CanOvertake(model.Instance, firstVisit, secondVisit)) ||
                    (reversal.SecondBeingOvertaken && !StationOvertakingResourcePolicy.CanOvertake(model.Instance, secondVisit, firstVisit)))
                {
                    violations.Add($"No-overtake station order consistency violated: station={stationName}, pair={incomingCandidate.FirstTrainIndex}/{incomingCandidate.SecondTrainIndex}, incoming={incomingSectionName}, outgoing={outgoingSectionName}.");
                }
            }
        }
    }

    /// <summary>Check resource support and pair flip totals from actual event times, including omitted relations.</summary>
    /// <param name="model">Solved model providing original visits and physical event times in seconds.</param>
    /// <param name="violations">Receives forbidden and repeated overtakes.</param>
    /// <param name="suggestions">Receives omitted section relations that can expose violating flips.</param>
    /// <param name="findings">Receives the affected modeled and omitted relations.</param>
    private static void ValidateActualStationOrders(BasicMilpModel model, ICollection<string> violations,
        ICollection<SectionConflictEdgeRequest> suggestions, ICollection<SectionRelationValidationFinding> findings)
    {
        HashSet<string> modeled=model.SectionConflictCandidates.Select(c=>InitialConflictGraphBuilder.BuildCanonicalPairKey(
            c.SectionName,c.FirstTrainIndex,c.SecondTrainIndex)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> suggested=suggestions.Select(c=>InitialConflictGraphBuilder.BuildCanonicalPairKey(
            c.SectionName,c.FirstTrainIndex,c.SecondTrainIndex)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Dictionary<(int FirstTrain, int SecondTrain), int> pairFlipCountsMap = new();
        foreach(var station in model.Instance.TrainStops.Where(s=>s.StopRole==TrainStopRole.Intermediate).GroupBy(s=>s.StationName, StringComparer.OrdinalIgnoreCase))
        {
            TrainStopRecord[] stops=station.OrderBy(s=>s.TrainIndex).ToArray();
            for(int i=0;i<stops.Length;i++) for(int j=i+1;j<stops.Length;j++)
            {
                TrainStopRecord first=stops[i],second=stops[j];
                if(first.TrainIndex==second.TrainIndex || first.IncomingSectionName!=second.IncomingSectionName) continue;
                var firstNext=model.Instance.Trains.First(t=>t.TrainIndex==first.TrainIndex).OrderedStops.FirstOrDefault(s=>s.StationOrder==first.StationOrder+1);
                var secondNext=model.Instance.Trains.First(t=>t.TrainIndex==second.TrainIndex).OrderedStops.FirstOrDefault(s=>s.StationOrder==second.StationOrder+1);
                if(firstNext?.IncomingSectionName is null || firstNext.IncomingSectionName!=secondNext?.IncomingSectionName) continue;
                int arrivalOrder=CompareTimes(Value(model.ArrivalVars[first.StopId]),Value(model.ArrivalVars[second.StopId]));
                int departureOrder=CompareTimes(Value(model.DepartureVars[first.StopId]),Value(model.DepartureVars[second.StopId]));
                if(arrivalOrder==0 || departureOrder==0 || arrivalOrder==departureOrder) continue;
                var pairKey = (first.TrainIndex, second.TrainIndex);
                int flipCount = pairFlipCountsMap[pairKey] = pairFlipCountsMap.GetValueOrDefault(pairKey) + 1;
                var waiting = arrivalOrder < 0 ? first : second;
                var passing = arrivalOrder < 0 ? second : first;
                bool resourceSupported = StationOvertakingResourcePolicy.CanOvertake(model.Instance, waiting, passing);
                bool hasWaitingStop = Value(model.StopVars[waiting.StopId]) > 0.5;
                if (!resourceSupported || !hasWaitingStop)
                    violations.Add($"station_overtaking_resource_or_stop_violation: station={station.Key}, pair={first.TrainIndex}/{second.TrainIndex}.");
                if (flipCount > 1)
                    violations.Add($"repeated_pair_overtaking: station={station.Key}, pair={first.TrainIndex}/{second.TrainIndex}, flips={flipCount}.");
                if (resourceSupported && hasWaitingStop && flipCount <= 1) continue;
                string violation = violations.Last();
                foreach(string section in new[]{first.IncomingSectionName!,firstNext.IncomingSectionName})
                {
                    string key=InitialConflictGraphBuilder.BuildCanonicalPairKey(section,first.TrainIndex,second.TrainIndex);
                    findings.Add(new(section,first.TrainIndex,second.TrainIndex,modeled.Contains(key),[violation]));
                    if(!modeled.Contains(key) && suggested.Add(key)) suggestions.Add(new(section,first.TrainIndex,second.TrainIndex));
                }
            }
        }
    }

    private static List<(string StationName, string IncomingSectionName, string OutgoingSectionName)> BuildStationAdjacentSectionPairs(TimetablingInstance instance)
    {
        Dictionary<string, (string IncomingSectionName, string OutgoingSectionName)> byStation = new(StringComparer.OrdinalIgnoreCase);

        foreach (TrainRecord train in instance.Trains)
        {
            for (int index = 1; index < train.OrderedStops.Count - 1; index++)
            {
                TrainStopRecord currentStop = train.OrderedStops[index];
                TrainStopRecord nextStop = train.OrderedStops[index + 1];
                if (string.IsNullOrWhiteSpace(currentStop.IncomingSectionName) ||
                    string.IsNullOrWhiteSpace(nextStop.IncomingSectionName))
                {
                    continue;
                }

                byStation.TryAdd(currentStop.StationName, (currentStop.IncomingSectionName, nextStop.IncomingSectionName));
            }
        }

        return byStation
            .Select(item => (item.Key, item.Value.IncomingSectionName, item.Value.OutgoingSectionName))
            .ToList();
    }

    private static RunTimeRule ResolveRunTimeRule(TimetablingInstance instance, TrainStopRecord currentStop, TrainStopRecord nextStop)
    {
        if (string.IsNullOrWhiteSpace(nextStop.IncomingSectionName))
        {
            throw new InvalidOperationException($"Next stop '{nextStop.StopId}' has no incoming section name.");
        }

        if (!instance.SectionByName.TryGetValue(nextStop.IncomingSectionName, out SectionRecord? section))
        {
            throw new InvalidOperationException($"Section '{nextStop.IncomingSectionName}' was not found.");
        }

        TravelDirection direction = DetermineOperationalDirection(section, currentStop.StationName, nextStop.StationName);
        string scaleName = nextStop.RunScaleName ?? throw new InvalidOperationException($"Stop '{nextStop.StopId}' has no run scale.");

        return instance.RunTimeRules.First(rule =>
            string.Equals(rule.ScaleName, scaleName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(rule.SectionName, nextStop.IncomingSectionName, StringComparison.OrdinalIgnoreCase) &&
            rule.Direction == direction);
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

        throw new InvalidOperationException($"Cannot determine direction for section '{section.SectionName}'.");
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

public sealed record MasterValidationReport(
    bool IsFeasible,
    IReadOnlyList<string> Violations,
    IReadOnlyList<SectionConflictEdgeRequest>? SuggestedSectionEdges = null,
    IReadOnlyList<SectionRelationValidationFinding>? SectionRelationFindings = null);

public sealed record SectionRelationValidationFinding(
    string SectionName,
    int FirstTrainIndex,
    int SecondTrainIndex,
    bool WasModeled,
    IReadOnlyList<string> Violations);
