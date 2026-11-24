// 微观可行性检查器把主问题解展开为车站事件和股道占用区间，逐站检测端点、径路、股道及追踪间隔冲突。
// 检查过程使用按站分组的事件列表和区间重叠比较，输出结构化冲突、站点汇总与类别计数，作为 Checker 反馈的事实来源。
using System.Globalization;
using System.Text;
using Gurobi;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

/// <summary>
/// 车站级微观可实施性检查器，作为 logic-based Benders 流程的可行性判定层。
/// 它把主问题解转换为站内到发或通过事件，检查径路链、股道域和精确进路间隔，
/// 再输出可供割生成器使用的结构化冲突解释。
/// </summary>
public sealed class MasterMicroFeasibilityChecker
{
    private const double ToleranceSeconds = GurobiTimetablingResearch.Domain.TimetableTimePolicy.NumericToleranceSeconds;
    private readonly int _endpointOccupationSeconds;

    public MasterMicroFeasibilityChecker(int endpointOccupationSeconds = StationEndpointOccupationPolicy.DefaultSeconds)
    {
        _endpointOccupationSeconds = endpointOccupationSeconds;
    }

    public int EndpointOccupationSeconds => _endpointOccupationSeconds;

    public MicroFeasibilityReport Validate(BasicMilpModel model)=>Validate(model,null);

    public MicroFeasibilityReport Validate(BasicMilpModel model,MicroFeasibilitySlaveRequest? request)
    {
        StationLevelMicroFeasibilitySlave slave = new(_endpointOccupationSeconds);
        StationLevelMicroFeasibilitySlaveResult slaveResult = slave.Solve(model,request);
        IReadOnlyList<MicroStationSummary> stationSummaries = BuildStationSummaries(
            slaveResult.StationResults,
            _endpointOccupationSeconds);
        return new MicroFeasibilityReport(
            slaveResult.IsFeasible,
            slaveResult.Conflicts,
            stationSummaries,
            slaveResult.RouteChoiceDiagnostics,
            slaveResult.Status);
    }

    public void WriteReports(MicroFeasibilityReport report, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        WriteConflictCsv(report.Conflicts, Path.Combine(outputDirectory, "micro_conflicts.csv"));
        WriteConflictCsv(report.RouteChoiceDiagnostics, Path.Combine(outputDirectory, "micro_route_choice_diagnostics.csv"));
        WriteStationSummaryCsv(
            report.StationSummaries,
            _endpointOccupationSeconds,
            Path.Combine(outputDirectory, "micro_station_summary.csv"));
        WriteConflictMetricsCsv(report, Path.Combine(outputDirectory, "micro_conflict_metrics.csv"));
        WriteRouteDomainDiagnosticsCsv(report.Conflicts, Path.Combine(outputDirectory, "micro_route_domain_diagnostics.csv"));
        WriteRouteDomainDiagnosticsMarkdown(report.Conflicts, Path.Combine(outputDirectory, "micro_route_domain_diagnostics.md"));
        WriteMarkdownReport(report, _endpointOccupationSeconds, Path.Combine(outputDirectory, "micro_feasibility.md"));
    }

    private static List<StationMicroRequirement> BuildRequirements(BasicMilpModel model, IReadOnlyDictionary<int, TrainRecord> trainByIndex)
    {
        List<StationMicroRequirement> requirements = new();

        foreach (TrainStopRecord stop in model.Instance.TrainStops)
        {
            if (!trainByIndex.TryGetValue(stop.TrainIndex, out TrainRecord? train))
            {
                continue;
            }

            TrainStopRecord? nextStop = train.OrderedStops.FirstOrDefault(candidate => candidate.StationOrder == stop.StationOrder + 1);
            bool isStop = BinaryValue(model.StopVars[stop.StopId]) == 1;
            MovementEvent incomingEvent = isStop ? MovementEvent.Arrival : MovementEvent.Passing;
            MovementEvent outgoingEvent = isStop ? MovementEvent.Departure : MovementEvent.Passing;

            List<RouteRecord> incomingRoutes = string.IsNullOrWhiteSpace(stop.IncomingSectionName)
                ? new List<RouteRecord>()
                : ResolveRoutes(model.Instance, stop.StationName, stop.IncomingSectionName!, incomingEvent, incoming: true);

            List<RouteRecord> outgoingRoutes = nextStop is null || string.IsNullOrWhiteSpace(nextStop.IncomingSectionName)
                ? new List<RouteRecord>()
                : ResolveRoutes(model.Instance, stop.StationName, nextStop.IncomingSectionName!, outgoingEvent, incoming: false);

            IReadOnlyList<string> preferredTracks = ResolvePreferredTracks(model.Instance, stop, isStop);
            HashSet<string> feasibleTracks = ResolveFeasibleTracks(incomingRoutes, outgoingRoutes, preferredTracks);

            requirements.Add(new StationMicroRequirement(
                stop.StopId,
                stop.TrainIndex,
                stop.TrainNo,
                stop.StationOrder,
                stop.StationName,
                isStop,
                stop.StopRole,
                Value(model.ArrivalVars[stop.StopId]),
                Value(model.DepartureVars[stop.StopId]),
                stop.IncomingSectionName,
                nextStop?.IncomingSectionName,
                incomingEvent,
                outgoingEvent,
                stop.StopRole != TrainStopRole.Origin,
                stop.StopRole != TrainStopRole.Destination && nextStop is not null,
                incomingRoutes,
                outgoingRoutes,
                preferredTracks,
                feasibleTracks.ToList()));
        }

        return requirements;
    }

    private static List<MicroConflictExplanation> CheckExactHeadways(
        TimetablingInstance instance,
        IReadOnlyList<StationMicroRequirement> requirements)
    {
        List<StationMicroEvent> events = new();
        foreach (StationMicroRequirement requirement in requirements)
        {
            if (requirement.RequiresIncomingRoute && requirement.IncomingRoutes.Count > 0)
            {
                events.Add(new StationMicroEvent(
                    requirement.StationName,
                    requirement.TrainIndex,
                    requirement.TrainNo,
                    requirement.StationOrder,
                    requirement.ArrivalSeconds,
                    requirement.IncomingEvent,
                    StationEventSide.Incoming,
                    requirement.IncomingSectionName ?? string.Empty,
                    requirement.IncomingRoutes,
                    requirement.DescribeIncomingRoutes()));
            }

            if (requirement.RequiresOutgoingRoute && requirement.OutgoingRoutes.Count > 0)
            {
                events.Add(new StationMicroEvent(
                    requirement.StationName,
                    requirement.TrainIndex,
                    requirement.TrainNo,
                    requirement.StationOrder,
                    requirement.DepartureSeconds,
                    requirement.OutgoingEvent,
                    StationEventSide.Outgoing,
                    requirement.OutgoingSectionName ?? string.Empty,
                    requirement.OutgoingRoutes,
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
                        $"realized_gap={realizedGap.ToString("F3", CultureInfo.InvariantCulture)}, required_gap={classification.MinimumRecordedHeadwaySeconds}, first_routes={first.RouteEvidence}, second_routes={second.RouteEvidence}",
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

    internal static IReadOnlyList<MicroStationSummary> BuildStationSummaries(
        IReadOnlyList<StationLevelMicroSubproblemResult> stationResults,
        int endpointOccupationSeconds)
    {
        return stationResults
            .Select(result =>
            {
                EndpointOccupationArtifactAnalysis endpointArtifacts = EndpointOccupationArtifactAnalyzer.Analyze(
                    result.Requirements,
                    endpointOccupationSeconds,
                    domainLowerBound: 0,
                    domainUpperBound: 86400);
                return new MicroStationSummary(
                    result.StationName,
                    result.Requirements.Count,
                    result.Requirements.Count(requirement => requirement.IsStop),
                    result.Requirements.SelectMany(requirement => requirement.FeasibleTracks).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    result.Conflicts.Count,
                    string.Join('|', result.Conflicts.Select(conflict => conflict.ConflictKind).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(kind => kind, StringComparer.OrdinalIgnoreCase)),
                    result.AssignmentResult.IsFeasible,
                    result.AssignmentResult.SelectedAlternatives.Count,
                    result.AssignmentResult.FailureEvidence,
                    endpointOccupationSeconds,
                    endpointArtifacts.ArtifactPairCount,
                    endpointArtifacts.Evidence);
            })
            .OrderByDescending(item => item.ConflictCount)
            .ThenBy(item => item.StationName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static MicroConflictExplanation CreateRouteConflict(
        string conflictKind,
        StationMicroRequirement requirement,
        double eventTimeSeconds,
        string resourceKey,
        string detail)
    {
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
            $"preferred_tracks={string.Join('|', requirement.PreferredTracks)}, feasible_tracks={string.Join('|', requirement.FeasibleTracks)}");
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
        IReadOnlyList<string> preferredTracks)
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

    private static void WriteConflictCsv(IReadOnlyList<MicroConflictExplanation> conflicts, string path)
    {
        using StreamWriter writer = CreateUtf8Writer(path);
        writer.WriteLine("conflict_kind,station_name,first_train_index,second_train_index,station_order,start_seconds,end_seconds,resource_key,detail,evidence,second_station_order,first_event_side,second_event_side,first_section_name,second_section_name,root_conflict_key");

        foreach (MicroConflictExplanation item in conflicts
            .OrderBy(item => item.StationName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.StartSeconds)
            .ThenBy(item => item.FirstTrainIndex))
        {
            writer.WriteLine(string.Join(
                ',',
                Escape(item.ConflictKind),
                Escape(item.StationName),
                item.FirstTrainIndex?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                item.SecondTrainIndex?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                item.StationOrder?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                item.StartSeconds.ToString("F3", CultureInfo.InvariantCulture),
                item.EndSeconds.ToString("F3", CultureInfo.InvariantCulture),
                Escape(item.ResourceKey),
                Escape(item.Detail),
                Escape(item.Evidence),
                item.SecondStationOrder?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                Escape(item.FirstEventSide?.ToString() ?? string.Empty),
                Escape(item.SecondEventSide?.ToString() ?? string.Empty),
                Escape(item.FirstSectionName ?? string.Empty),
                Escape(item.SecondSectionName ?? string.Empty),
                Escape(item.RootConflictKey ?? string.Empty)));
        }
    }

    private static void WriteStationSummaryCsv(
        IReadOnlyList<MicroStationSummary> summaries,
        int endpointOccupationSeconds,
        string path)
    {
        using StreamWriter writer = CreateUtf8Writer(path);
        writer.WriteLine("station_name,requirement_count,stop_requirement_count,distinct_feasible_track_count,conflict_count,conflict_kinds,joint_assignment_feasible,selected_alternative_count,assignment_failure_evidence,endpoint_occupation_seconds,endpoint_artifact_conflict_count,endpoint_artifact_evidence");

        foreach (MicroStationSummary item in summaries)
        {
            writer.WriteLine(string.Join(
                ',',
                Escape(item.StationName),
                item.RequirementCount.ToString(CultureInfo.InvariantCulture),
                item.StopRequirementCount.ToString(CultureInfo.InvariantCulture),
                item.DistinctFeasibleTrackCount.ToString(CultureInfo.InvariantCulture),
                item.ConflictCount.ToString(CultureInfo.InvariantCulture),
                Escape(item.ConflictKinds),
                item.JointAssignmentFeasible.ToString(CultureInfo.InvariantCulture),
                item.SelectedAlternativeCount.ToString(CultureInfo.InvariantCulture),
                Escape(item.AssignmentFailureEvidence),
                endpointOccupationSeconds.ToString(CultureInfo.InvariantCulture),
                item.EndpointArtifactConflictCount.ToString(CultureInfo.InvariantCulture),
                Escape(item.EndpointArtifactEvidence)));
        }
    }

    private static void WriteConflictMetricsCsv(
        MicroFeasibilityReport report,
        string path)
    {
        MicroConflictMetrics metrics = MicroConflictMetricCalculator.Calculate(report.Conflicts);
        MicroConflictCategoryCounts categoryCounts = MicroConflictCategoryCounter.Calculate(
            report.Conflicts,
            report.StationSummaries.Sum(item => item.EndpointArtifactConflictCount));
        using StreamWriter writer = CreateUtf8Writer(path);
        writer.WriteLine("raw_conflict_count,canonical_conflict_count,root_conflict_count,episode_conflict_count,conflicted_station_count,exact_headway_conflict_count,station_assignment_conflict_count,endpoint_artifact_conflict_count");
        writer.WriteLine(string.Join(',',
            metrics.RawConflictCount.ToString(CultureInfo.InvariantCulture),
            metrics.CanonicalConflictCount.ToString(CultureInfo.InvariantCulture),
            metrics.RootConflictCount.ToString(CultureInfo.InvariantCulture),
            metrics.EpisodeConflictCount.ToString(CultureInfo.InvariantCulture),
            metrics.ConflictedStationCount.ToString(CultureInfo.InvariantCulture),
            categoryCounts.ExactHeadwayConflictCount.ToString(CultureInfo.InvariantCulture),
            categoryCounts.StationAssignmentConflictCount.ToString(CultureInfo.InvariantCulture),
            categoryCounts.EndpointArtifactConflictCount.ToString(CultureInfo.InvariantCulture)));
    }

    private static void WriteRouteDomainDiagnosticsCsv(
        IReadOnlyList<MicroConflictExplanation> conflicts,
        string path)
    {
        using StreamWriter writer = CreateUtf8Writer(path);
        writer.WriteLine("root_conflict_key,conflict_kind,root_cause,recommended_action,is_derived,station_name,train_index,station_order,resource_key,detail,evidence");
        foreach (MicroConflictExplanation conflict in conflicts
                     .Where(IsRouteDomainConflict)
                     .OrderBy(item => item.StationName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.FirstTrainIndex)
                     .ThenBy(item => item.ConflictKind, StringComparer.OrdinalIgnoreCase))
        {
            writer.WriteLine(string.Join(',',
                Escape(MicroConflictMetricCalculator.BuildRootKey(conflict)),
                Escape(conflict.ConflictKind),
                Escape(conflict.RootCause?.ToString() ?? string.Empty),
                Escape(conflict.RecommendedAction ?? string.Empty),
                conflict.IsDerived.ToString(CultureInfo.InvariantCulture),
                Escape(conflict.StationName),
                conflict.FirstTrainIndex?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                conflict.StationOrder?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                Escape(conflict.ResourceKey),
                Escape(conflict.Detail),
                Escape(conflict.Evidence)));
        }
    }

    private static void WriteRouteDomainDiagnosticsMarkdown(
        IReadOnlyList<MicroConflictExplanation> conflicts,
        string path)
    {
        IReadOnlyList<MicroConflictExplanation> routeConflicts = conflicts.Where(IsRouteDomainConflict).ToList();
        StringBuilder builder = new();
        builder.AppendLine("# Micro Route Domain Diagnostics");
        builder.AppendLine();
        builder.AppendLine($"- raw_route_domain_rows: `{routeConflicts.Count}`");
        builder.AppendLine($"- root_route_domain_conflicts: `{routeConflicts.Select(MicroConflictMetricCalculator.BuildRootKey).Distinct(StringComparer.OrdinalIgnoreCase).Count()}`");
        builder.AppendLine();
        builder.AppendLine("| root | kind | station | train | order | detail |");
        builder.AppendLine("|---|---|---|---:|---:|---|");
        foreach (MicroConflictExplanation conflict in routeConflicts.Take(100))
        {
            builder.AppendLine($"| `{MicroConflictMetricCalculator.BuildRootKey(conflict)}` | {conflict.ConflictKind} | {conflict.StationName} | {conflict.FirstTrainIndex?.ToString(CultureInfo.InvariantCulture) ?? "-"} | {conflict.StationOrder?.ToString(CultureInfo.InvariantCulture) ?? "-"} | {conflict.Detail} |");
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
    }

    private static bool IsRouteDomainConflict(MicroConflictExplanation conflict)
    {
        return conflict.ConflictKind is "missing_incoming_route" or "missing_outgoing_route" or
            "empty_track_domain" or "benchmark_track_domain_empty";
    }

    private static void WriteMarkdownReport(
        MicroFeasibilityReport report,
        int endpointOccupationSeconds,
        string path)
    {
        StringBuilder builder = new();
        builder.AppendLine("# Micro Feasibility Checker Report");
        builder.AppendLine();
        builder.AppendLine("## Overall");
        builder.AppendLine();
        builder.AppendLine($"- feasible_by_micro_checker: `{report.IsFeasible}`");
        builder.AppendLine($"- conflict_count: `{report.Conflicts.Count}`");
        MicroConflictMetrics metrics = MicroConflictMetricCalculator.Calculate(report.Conflicts);
        MicroConflictCategoryCounts categoryCounts = MicroConflictCategoryCounter.Calculate(
            report.Conflicts,
            report.StationSummaries.Sum(item => item.EndpointArtifactConflictCount));
        builder.AppendLine($"- canonical_conflict_count: `{metrics.CanonicalConflictCount}`");
        builder.AppendLine($"- root_conflict_count: `{metrics.RootConflictCount}`");
        builder.AppendLine($"- episode_conflict_count: `{metrics.EpisodeConflictCount}`");
        builder.AppendLine($"- exact_headway_conflict_count: `{categoryCounts.ExactHeadwayConflictCount}`");
        builder.AppendLine($"- station_assignment_conflict_count: `{categoryCounts.StationAssignmentConflictCount}`");
        builder.AppendLine($"- endpoint_artifact_conflict_count: `{categoryCounts.EndpointArtifactConflictCount}`");
        builder.AppendLine($"- endpoint_occupation_seconds: `{endpointOccupationSeconds.ToString(CultureInfo.InvariantCulture)}`");
        builder.AppendLine($"- route_choice_required_count: `{report.RouteChoiceDiagnostics.Count}`");
        builder.AppendLine($"- conflicted_station_count: `{report.StationSummaries.Count(summary => summary.ConflictCount > 0)}`");
        builder.AppendLine();

        builder.AppendLine("## Conflict Types");
        builder.AppendLine();
        builder.AppendLine("| type | count |");
        builder.AppendLine("|---|---:|");
        foreach (var group in report.Conflicts
            .GroupBy(item => item.ConflictKind, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"| {group.Key} | {group.Count()} |");
        }

        builder.AppendLine();
        builder.AppendLine("## Station Summary");
        builder.AppendLine();
        builder.AppendLine("| station | requirements | stop_requirements | feasible_tracks | joint_assignment | conflicts | conflict_kinds | endpoint_occupation_seconds | endpoint_artifacts |");
        builder.AppendLine("|---|---:|---:|---:|---|---:|---|---:|---:|");
        foreach (MicroStationSummary item in report.StationSummaries.Take(40))
        {
            builder.AppendLine($"| {item.StationName} | {item.RequirementCount} | {item.StopRequirementCount} | {item.DistinctFeasibleTrackCount} | {item.JointAssignmentFeasible} | {item.ConflictCount} | `{item.ConflictKinds}` | {endpointOccupationSeconds.ToString(CultureInfo.InvariantCulture)} | {item.EndpointArtifactConflictCount} |");
        }

        if (report.Conflicts.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Top Conflicts");
            builder.AppendLine();
            builder.AppendLine("| type | station | trains | time_window | detail |");
            builder.AppendLine("|---|---|---|---|---|");
            foreach (MicroConflictExplanation item in report.Conflicts.Take(50))
            {
                string trainText = item.SecondTrainIndex.HasValue
                    ? $"{item.FirstTrainIndex}-{item.SecondTrainIndex}"
                    : item.FirstTrainIndex?.ToString(CultureInfo.InvariantCulture) ?? "-";
                string timeWindow = $"[{item.StartSeconds.ToString("F1", CultureInfo.InvariantCulture)},{item.EndSeconds.ToString("F1", CultureInfo.InvariantCulture)}]";
                builder.AppendLine($"| {item.ConflictKind} | {item.StationName} | {trainText} | `{timeWindow}` | {item.Detail} |");
            }
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static StreamWriter CreateUtf8Writer(string path)
    {
        return new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static double Value(GRBVar variable)
    {
        return variable.Get(GRB.DoubleAttr.X);
    }

    private static int BinaryValue(GRBVar variable)
    {
        return Value(variable) >= 0.5 ? 1 : 0;
    }

    private static string Escape(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (!value.Contains(',') && !value.Contains('"') && !value.Contains('\n') && !value.Contains('\r'))
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }
}

public sealed record MicroFeasibilityReport(
    bool IsFeasible,
    IReadOnlyList<MicroConflictExplanation> Conflicts,
    IReadOnlyList<MicroStationSummary> StationSummaries,
    IReadOnlyList<MicroConflictExplanation> RouteChoiceDiagnostics,
    MicroSolveStatus Status = MicroSolveStatus.ProvenInfeasible);

public sealed record MicroConflictExplanation(
    string ConflictKind,
    string StationName,
    int? FirstTrainIndex,
    int? SecondTrainIndex,
    int? StationOrder,
    double StartSeconds,
    double EndSeconds,
    string ResourceKey,
    string Detail,
    string Evidence,
    int? SecondStationOrder = null,
    StationEventSide? FirstEventSide = null,
    StationEventSide? SecondEventSide = null,
    string? FirstSectionName = null,
    string? SecondSectionName = null,
    RouteDomainFailureReason? RootCause = null,
    string? RootConflictKey = null,
    string? RecommendedAction = null,
    bool IsDerived = false,
    double? RealizedGapSeconds = null,
    int? RequiredGapSeconds = null,
    IReadOnlyList<int>? LeadingRouteIds = null,
    IReadOnlyList<int>? FollowingRouteIds = null,
    int? RecordedRoutePairCount = null,
    int? MissingRoutePairCount = null);

public sealed record MicroStationSummary(
    string StationName,
    int RequirementCount,
    int StopRequirementCount,
    int DistinctFeasibleTrackCount,
    int ConflictCount,
    string ConflictKinds,
    bool JointAssignmentFeasible,
    int SelectedAlternativeCount,
    string AssignmentFailureEvidence,
    int EndpointOccupationSeconds = StationEndpointOccupationPolicy.DefaultSeconds,
    int EndpointArtifactConflictCount = 0,
    string EndpointArtifactEvidence = "");

internal sealed record MicroConflictCategoryCounts(
    int ExactHeadwayConflictCount,
    int StationAssignmentConflictCount,
    int EndpointArtifactConflictCount);

internal static class MicroConflictCategoryCounter
{
    internal static MicroConflictCategoryCounts Calculate(
        IReadOnlyList<MicroConflictExplanation> conflicts,
        int endpointArtifactConflictCount = 0)
    {
        ArgumentNullException.ThrowIfNull(conflicts);
        return new MicroConflictCategoryCounts(
            conflicts.Count(conflict => string.Equals(
                conflict.ConflictKind,
                "exact_headway_infeasible",
                StringComparison.OrdinalIgnoreCase)),
            conflicts.Count(conflict => string.Equals(
                conflict.ConflictKind,
                "station_route_track_assignment_infeasible",
                StringComparison.OrdinalIgnoreCase)),
            endpointArtifactConflictCount);
    }
}

internal sealed record EndpointOccupationArtifactPair(
    string PairId,
    string FirstRequirementId,
    string SecondRequirementId,
    StationOccupationInterval FirstLegacyInterval,
    StationOccupationInterval SecondLegacyInterval,
    StationOccupationInterval FirstNormalizedInterval,
    StationOccupationInterval SecondNormalizedInterval,
    IReadOnlyList<string> SharedFeasibleTracks);

internal sealed record EndpointOccupationArtifactAnalysis(
    IReadOnlyList<EndpointOccupationArtifactPair> Pairs)
{
    internal int ArtifactPairCount => Pairs.Count;

    internal string Evidence => string.Join(
        ';',
        Pairs.Select(pair =>
            $"{pair.PairId}:legacy=[{Format(pair.FirstLegacyInterval)},{Format(pair.SecondLegacyInterval)}]," +
            $"normalized=[{Format(pair.FirstNormalizedInterval)},{Format(pair.SecondNormalizedInterval)}]," +
            $"tracks={string.Join('|', pair.SharedFeasibleTracks)}"));

    private static string Format(StationOccupationInterval interval) =>
        $"{interval.StartSeconds.ToString("F3", CultureInfo.InvariantCulture)}-" +
        interval.EndSeconds.ToString("F3", CultureInfo.InvariantCulture);
}

internal static class EndpointOccupationArtifactAnalyzer
{
    private const double ToleranceSeconds = GurobiTimetablingResearch.Domain.TimetableTimePolicy.NumericToleranceSeconds;

    internal static EndpointOccupationArtifactAnalysis Analyze(
        IReadOnlyList<StationMicroRequirement> requirements,
        int endpointOccupationSeconds,
        double domainLowerBound,
        double domainUpperBound)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        List<StationMicroRequirement> candidates = requirements
            .Where(requirement => requirement.IsStop && requirement.FeasibleTracks.Count > 0)
            .OrderBy(requirement => requirement.StopId, StringComparer.Ordinal)
            .ThenBy(requirement => requirement.TrainIndex)
            .ToList();
        List<EndpointOccupationArtifactPair> pairs = new();

        for (int firstIndex = 0; firstIndex < candidates.Count; firstIndex++)
        {
            StationMicroRequirement first = candidates[firstIndex];
            for (int secondIndex = firstIndex + 1; secondIndex < candidates.Count; secondIndex++)
            {
                StationMicroRequirement second = candidates[secondIndex];
                if (!string.Equals(first.StationName, second.StationName, StringComparison.OrdinalIgnoreCase) ||
                    (!HasEndpointPlaceholder(first) && !HasEndpointPlaceholder(second)))
                {
                    continue;
                }

                List<string> sharedTracks = first.FeasibleTracks
                    .Intersect(second.FeasibleTracks, StringComparer.OrdinalIgnoreCase)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(track => track, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (sharedTracks.Count == 0)
                {
                    continue;
                }

                if (!LegacyRawOverlaps(first, second))
                {
                    continue;
                }

                StationOccupationInterval firstLegacy = new(
                    first.ArrivalSeconds,
                    first.DepartureSeconds);
                StationOccupationInterval secondLegacy = new(
                    second.ArrivalSeconds,
                    second.DepartureSeconds);

                StationOccupationInterval firstNormalized = StationEndpointOccupationPolicy.Resolve(
                    first.StopRole,
                    first.IsStop,
                    first.ArrivalSeconds,
                    first.DepartureSeconds,
                    endpointOccupationSeconds,
                    domainLowerBound,
                    domainUpperBound);
                StationOccupationInterval secondNormalized = StationEndpointOccupationPolicy.Resolve(
                    second.StopRole,
                    second.IsStop,
                    second.ArrivalSeconds,
                    second.DepartureSeconds,
                    endpointOccupationSeconds,
                    domainLowerBound,
                    domainUpperBound);
                if (Overlaps(firstNormalized, secondNormalized))
                {
                    continue;
                }

                pairs.Add(new EndpointOccupationArtifactPair(
                    $"{first.StopId}|{second.StopId}",
                    first.StopId,
                    second.StopId,
                    firstLegacy,
                    secondLegacy,
                    firstNormalized,
                    secondNormalized,
                    sharedTracks));
            }
        }

        return new EndpointOccupationArtifactAnalysis(pairs);
    }

    private static bool HasEndpointPlaceholder(StationMicroRequirement requirement) =>
        requirement.StopRole switch
        {
            TrainStopRole.Origin => IsPlaceholder(requirement.ArrivalSeconds),
            TrainStopRole.Destination => IsPlaceholder(requirement.DepartureSeconds),
            _ => false
        };

    private static bool IsPlaceholder(double value) => value is 0 or -999;

    private static bool LegacyRawOverlaps(
        StationMicroRequirement first,
        StationMicroRequirement second) =>
        first.ArrivalSeconds < second.DepartureSeconds - ToleranceSeconds &&
        first.DepartureSeconds > second.ArrivalSeconds + ToleranceSeconds;

    private static bool Overlaps(StationOccupationInterval first, StationOccupationInterval second) =>
        first.StartSeconds < second.EndSeconds - ToleranceSeconds &&
        first.EndSeconds > second.StartSeconds + ToleranceSeconds;
}

internal sealed record StationMicroRequirement(
    string StopId,
    int TrainIndex,
    string TrainNo,
    int StationOrder,
    string StationName,
    bool IsStop,
    TrainStopRole StopRole,
    double ArrivalSeconds,
    double DepartureSeconds,
    string? IncomingSectionName,
    string? OutgoingSectionName,
    MovementEvent IncomingEvent,
    MovementEvent OutgoingEvent,
    bool RequiresIncomingRoute,
    bool RequiresOutgoingRoute,
    IReadOnlyList<RouteRecord> IncomingRoutes,
    IReadOnlyList<RouteRecord> OutgoingRoutes,
    IReadOnlyList<string> PreferredTracks,
    IReadOnlyList<string> FeasibleTracks,
    IReadOnlyList<string>? OrdinaryTracks = null,
    IReadOnlyList<string>? BenchmarkTracks = null,
    bool HasBenchmarkRestriction = false,
    StopRequirement StopRequirement = StopRequirement.OptionalStop,
    StopModeDomainAnalysis? StopModeAnalysis = null)
{
    public string DescribeIncomingRoutes() => string.Join('|', IncomingRoutes.Select(route => route.RouteId));

    public string DescribeOutgoingRoutes() => string.Join('|', OutgoingRoutes.Select(route => route.RouteId));

    public string DescribeTrackDomain()
    {
        return $"ordinary={string.Join('|', OrdinaryTracks ?? PreferredTracks)}, " +
               $"benchmark={string.Join('|', BenchmarkTracks ?? Array.Empty<string>())}, " +
               $"preferred={string.Join('|', PreferredTracks)}, " +
               $"incoming_tracks={string.Join('|', IncomingRoutes.Select(route => route.TrackName).Distinct(StringComparer.OrdinalIgnoreCase))}, " +
               $"outgoing_tracks={string.Join('|', OutgoingRoutes.Select(route => route.TrackName).Distinct(StringComparer.OrdinalIgnoreCase))}, " +
               $"feasible={string.Join('|', FeasibleTracks)}";
    }
}

internal sealed record StationMicroEvent(
    string StationName,
    int TrainIndex,
    string TrainNo,
    int StationOrder,
    double EventTimeSeconds,
    MovementEvent EventType,
    StationEventSide EventSide,
    string SectionName,
    IReadOnlyList<RouteRecord> CandidateRoutes,
    string RouteEvidence);

internal sealed record StationTrackOccupation(
    int TrainIndex,
    string TrainNo,
    int StationOrder,
    string StationName,
    double StartSeconds,
    double EndSeconds,
    IReadOnlyList<string> FeasibleTracks);
