// 车站径路股道分配器把每个列车事件的备选方案视为离散域，利用冲突图连通分量和回溯搜索寻找无冲突组合。
// 搜索按最受限变量优先并进行占用冲突剪枝；失败时从搜索证据提取车站不可行核心，供主问题生成投影割。
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

internal sealed record StationAssignmentRequirement(
    string RequirementId,
    int TrainIndex,
    bool IsStop,
    double ArrivalSeconds,
    double DepartureSeconds,
    double OccupationStartSeconds,
    double OccupationEndSeconds,
    IReadOnlyList<RouteRecord> IncomingRoutes,
    IReadOnlyList<RouteRecord> OutgoingRoutes,
    IReadOnlyList<string> FeasibleTracks);

internal sealed record StationRouteTrackAlternative(
    string RequirementId,
    int TrainIndex,
    bool IsStop,
    double ArrivalSeconds,
    double DepartureSeconds,
    double OccupationStartSeconds,
    double OccupationEndSeconds,
    RouteRecord? IncomingRoute,
    RouteRecord? OutgoingRoute,
    string TrackName);

internal enum StationCoreMinimalityStatus
{
    Irreducible,
    NonMinimalUnknown
}

internal sealed record StationInfeasibleCore(
    IReadOnlyList<string> RequirementIds,
    IReadOnlyList<int> TrainIndices,
    StationCoreMinimalityStatus MinimalityStatus,
    long ReductionExploredNodes);

internal sealed record StationRouteTrackAssignmentResult(
    bool IsFeasible,
    IReadOnlyList<StationRouteTrackAlternative> SelectedAlternatives,
    string FailureEvidence,
    MicroSolveStatus Status = MicroSolveStatus.ProvenInfeasible,
    long ExploredNodes = 0,
    int ComponentCount = 1,
    StationInfeasibleCore? InfeasibleCore = null);

internal static class StationRouteTrackAssignmentSolver
{
    private const double ToleranceSeconds = GurobiTimetablingResearch.Domain.TimetableTimePolicy.NumericToleranceSeconds;

    internal static StationRouteTrackAssignmentResult Solve(
        IReadOnlyList<StationAssignmentRequirement> requirements,
        Func<int, int, int?> findRecordedHeadway,
        MicroSearchOptions? options = null,IReadOnlyList<VerifiedMicroAssignmentWitness>? fixedAssignments=null,Func<double>? remaining=null)
    {
        if(fixedAssignments is not null) return CheckFixed(requirements,findRecordedHeadway,fixedAssignments,remaining);
        options ??= MicroSearchOptions.Default;
        List<(StationAssignmentRequirement Requirement, List<StationRouteTrackAlternative> Alternatives)> domains = requirements
            .Select(requirement => (requirement, BuildAlternatives(requirement)))
            .OrderBy(item => item.Item2.Count)
            .ThenBy(item => item.requirement.TrainIndex)
            .ToList();

        IReadOnlyList<IReadOnlyList<(StationAssignmentRequirement Requirement, List<StationRouteTrackAlternative> Alternatives)>> components =
            ConflictConnectedComponentBuilder.Build(
                domains,
                (first, second) => DomainsMayConflict(first.Alternatives, second.Alternatives, findRecordedHeadway));
        List<StationRouteTrackAlternative> selected = new();
        SearchContext context = new(options, System.Diagnostics.Stopwatch.StartNew());
        foreach (IReadOnlyList<(StationAssignmentRequirement Requirement, List<StationRouteTrackAlternative> Alternatives)> component in components)
        {
            ComponentSearchResult componentResult = SearchComponent(component, findRecordedHeadway, context);
            if (componentResult.Outcome == SearchOutcome.Found)
            {
                selected.AddRange(componentResult.SelectedAlternatives);
                continue;
            }

            if (componentResult.Outcome == SearchOutcome.LimitReached)
            {
                return new StationRouteTrackAssignmentResult(
                    false,
                    Array.Empty<StationRouteTrackAlternative>(),
                    $"search limit reached after {context.ExploredNodes} nodes and {context.Stopwatch.Elapsed.TotalSeconds:F3} seconds",
                    MicroSolveStatus.Unknown,
                    context.ExploredNodes,
                    components.Count);
            }

            StationInfeasibleCore infeasibleCore = ReduceInfeasibleCore(
                component,
                findRecordedHeadway,
                context);

            return new StationRouteTrackAssignmentResult(
                false,
                Array.Empty<StationRouteTrackAlternative>(),
                BuildFailureEvidence(component.Count, requirements.Count, component, infeasibleCore),
                MicroSolveStatus.ProvenInfeasible,
                context.ExploredNodes,
                components.Count,
                infeasibleCore);
        }

        return new StationRouteTrackAssignmentResult(
            true,
            selected.OrderBy(item => item.TrainIndex).ToList(),
            string.Empty,
            MicroSolveStatus.Feasible,
            context.ExploredNodes,
            components.Count);
    }

    // Check a supplied assignment against original domains and every resource pair.
    // A rejected assignment is not evidence that the full station problem is infeasible.
    private static StationRouteTrackAssignmentResult CheckFixed(
        IReadOnlyList<StationAssignmentRequirement> requirements,Func<int,int,int?> headway,
        IReadOnlyList<VerifiedMicroAssignmentWitness> witnesses,Func<double>? remaining)
    {
        StationRouteTrackAssignmentResult Rejected(string detail)=>new(false,[],detail,MicroSolveStatus.Unknown);
        if(witnesses.Count!=requirements.Count || witnesses.Select(w=>w.RequirementId).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=witnesses.Count)
            return Rejected("Incomplete or duplicate fixed resource assignment.");
        var byId=witnesses.ToDictionary(w=>w.RequirementId,StringComparer.OrdinalIgnoreCase);
        List<StationRouteTrackAlternative> selected=[];
        foreach(var req in requirements)
        {
            if(remaining?.Invoke()<=0) return Rejected("Fixed resource validation deadline expired.");
            if(!byId.TryGetValue(req.RequirementId,out var w) || w.TrainIndex!=req.TrainIndex || w.IsStop!=req.IsStop ||
                w.ArrivalSeconds is double arrival && (!double.IsFinite(arrival) || Math.Abs(arrival-req.ArrivalSeconds)>1e-6) ||
                w.DepartureSeconds is double departure && (!double.IsFinite(departure) || Math.Abs(departure-req.DepartureSeconds)>1e-6))
                return Rejected("Fixed resource assignment has wrong event identity.");
            var choice=BuildAlternatives(req).FirstOrDefault(a=>a.TrackName.Equals(w.TrackName,StringComparison.OrdinalIgnoreCase) &&
                a.IncomingRoute?.RouteId==w.IncomingRouteId && a.OutgoingRoute?.RouteId==w.OutgoingRouteId);
            if(choice is null) return Rejected("Fixed resource assignment lies outside original route/track domain.");
            foreach(var other in selected)
            {
                if(remaining?.Invoke()<=0) return Rejected("Fixed resource validation deadline expired.");
                if(!AreCompatible(other,choice,headway)) return Rejected("Fixed resource assignments conflict.");
            }
            selected.Add(choice);
        }
        return new(true,selected,"",MicroSolveStatus.Feasible,0);
    }

    private static ComponentSearchResult SearchComponent(
        IReadOnlyList<(StationAssignmentRequirement Requirement, List<StationRouteTrackAlternative> Alternatives)> component,
        Func<int, int, int?> findRecordedHeadway,
        SearchContext context)
    {
        List<StationRouteTrackAlternative> selected = [];
        SearchOutcome outcome = Search(component, 0, selected, findRecordedHeadway, context);
        return new ComponentSearchResult(outcome, selected);
    }

    private static StationInfeasibleCore ReduceInfeasibleCore(
        IReadOnlyList<(StationAssignmentRequirement Requirement, List<StationRouteTrackAlternative> Alternatives)> provenInfeasibleComponent,
        Func<int, int, int?> findRecordedHeadway,
        SearchContext context)
    {
        List<(StationAssignmentRequirement Requirement, List<StationRouteTrackAlternative> Alternatives)> retained =
            provenInfeasibleComponent
                .OrderBy(item => item.Requirement.RequirementId, StringComparer.Ordinal)
                .ThenBy(item => item.Requirement.TrainIndex)
                .ToList();
        long reductionStartNodes = context.ExploredNodes;

        while (true)
        {
            bool removedRequirement = false;
            foreach ((StationAssignmentRequirement Requirement, List<StationRouteTrackAlternative> Alternatives) candidate in retained.ToList())
            {
                List<(StationAssignmentRequirement Requirement, List<StationRouteTrackAlternative> Alternatives)> reduced = retained
                    .Where(item => !ReferenceEquals(item.Requirement, candidate.Requirement))
                    .ToList();
                ComponentSearchResult reductionResult = SearchComponent(reduced, findRecordedHeadway, context);
                if (reductionResult.Outcome == SearchOutcome.LimitReached)
                {
                    return CreateCore(
                        retained,
                        StationCoreMinimalityStatus.NonMinimalUnknown,
                        context.ExploredNodes - reductionStartNodes);
                }

                if (reductionResult.Outcome == SearchOutcome.Exhausted)
                {
                    retained = reduced;
                    removedRequirement = true;
                    break;
                }
            }

            if (!removedRequirement)
            {
                return CreateCore(
                    retained,
                    StationCoreMinimalityStatus.Irreducible,
                    context.ExploredNodes - reductionStartNodes);
            }
        }
    }

    private static StationInfeasibleCore CreateCore(
        IReadOnlyList<(StationAssignmentRequirement Requirement, List<StationRouteTrackAlternative> Alternatives)> retained,
        StationCoreMinimalityStatus minimalityStatus,
        long reductionExploredNodes)
    {
        return new StationInfeasibleCore(
            retained
                .Select(item => item.Requirement.RequirementId)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(requirementId => requirementId, StringComparer.Ordinal)
                .ToList(),
            retained
                .Select(item => item.Requirement.TrainIndex)
                .Distinct()
                .OrderBy(trainIndex => trainIndex)
                .ToList(),
            minimalityStatus,
            reductionExploredNodes);
    }

    private static string BuildFailureEvidence(
        int componentRequirementCount,
        int totalRequirementCount,
        IReadOnlyList<(StationAssignmentRequirement Requirement, List<StationRouteTrackAlternative> Alternatives)> component,
        StationInfeasibleCore core)
    {
        Dictionary<string, StationAssignmentRequirement> requirementsById = component
            .Select(item => item.Requirement)
            .GroupBy(requirement => requirement.RequirementId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        IReadOnlyList<StationAssignmentRequirement> coreRequirements = core.RequirementIds
            .Select(requirementId => requirementsById[requirementId])
            .ToList();

        string trackDomains = string.Join('|', coreRequirements.Select(requirement =>
            $"{requirement.RequirementId}:[{JoinSortedTracks(requirement.FeasibleTracks)}]"));
        string incomingRouteIds = string.Join('|', coreRequirements.Select(requirement =>
            $"{requirement.RequirementId}:[{JoinSortedRouteIds(requirement.IncomingRoutes)}]"));
        string outgoingRouteIds = string.Join('|', coreRequirements.Select(requirement =>
            $"{requirement.RequirementId}:[{JoinSortedRouteIds(requirement.OutgoingRoutes)}]"));

        return $"no coherent assignment exists in a conflict component containing {componentRequirementCount} of {totalRequirementCount} station requirements; " +
               $"core_trains={string.Join('|', core.TrainIndices)}; " +
               $"core_requirement_ids={string.Join('|', core.RequirementIds)}; " +
               $"core_track_domains={trackDomains}; " +
               $"core_incoming_route_ids={incomingRouteIds}; " +
               $"core_outgoing_route_ids={outgoingRouteIds}; " +
               $"core_minimality={core.MinimalityStatus}; " +
               $"reduction_explored_nodes={core.ReductionExploredNodes}";
    }

    private static string JoinSortedTracks(IReadOnlyList<string> tracks) =>
        string.Join('|', tracks
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(track => track, StringComparer.OrdinalIgnoreCase)
            .ThenBy(track => track, StringComparer.Ordinal));

    private static string JoinSortedRouteIds(IReadOnlyList<RouteRecord> routes) =>
        string.Join('|', routes
            .Select(route => route.RouteId)
            .Distinct()
            .OrderBy(routeId => routeId));

    private static bool DomainsMayConflict(
        IReadOnlyList<StationRouteTrackAlternative> first,
        IReadOnlyList<StationRouteTrackAlternative> second,
        Func<int, int, int?> findRecordedHeadway) =>
        first.Any(firstAlternative => second.Any(secondAlternative =>
            !AreCompatible(firstAlternative, secondAlternative, findRecordedHeadway)));

    internal static List<StationRouteTrackAlternative> BuildAlternatives(StationAssignmentRequirement requirement)
    {
        if (requirement.FeasibleTracks.Count == 0)
        {
            return new List<StationRouteTrackAlternative>();
        }

        IReadOnlyList<RouteRecord?> incoming = requirement.IncomingRoutes.Count == 0
            ? new RouteRecord?[] { null }
            : requirement.IncomingRoutes.Cast<RouteRecord?>().ToList();
        IReadOnlyList<RouteRecord?> outgoing = requirement.OutgoingRoutes.Count == 0
            ? new RouteRecord?[] { null }
            : requirement.OutgoingRoutes.Cast<RouteRecord?>().ToList();
        HashSet<string> allowedTracks = requirement.FeasibleTracks.ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<StationRouteTrackAlternative> alternatives = new();

        foreach (RouteRecord? incomingRoute in incoming)
        {
            foreach (RouteRecord? outgoingRoute in outgoing)
            {
                if (incomingRoute is null && outgoingRoute is null)
                {
                    continue;
                }

                if (incomingRoute is not null && outgoingRoute is not null &&
                    !string.Equals(incomingRoute.TrackName, outgoingRoute.TrackName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string trackName = incomingRoute?.TrackName ?? outgoingRoute!.TrackName;
                if (!allowedTracks.Contains(trackName))
                {
                    continue;
                }

                alternatives.Add(new StationRouteTrackAlternative(
                    requirement.RequirementId,
                    requirement.TrainIndex,
                    requirement.IsStop,
                    requirement.ArrivalSeconds,
                    requirement.DepartureSeconds,
                    requirement.OccupationStartSeconds,
                    requirement.OccupationEndSeconds,
                    incomingRoute,
                    outgoingRoute,
                    trackName));
            }
        }

        return alternatives
            .DistinctBy(item => (item.IncomingRoute?.RouteId, item.OutgoingRoute?.RouteId, item.TrackName))
            .OrderBy(item => item.TrackName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.IncomingRoute?.RouteId ?? int.MinValue)
            .ThenBy(item => item.OutgoingRoute?.RouteId ?? int.MinValue)
            .ToList();
    }

    private static SearchOutcome Search(
        IReadOnlyList<(StationAssignmentRequirement Requirement, List<StationRouteTrackAlternative> Alternatives)> domains,
        int index,
        List<StationRouteTrackAlternative> selected,
        Func<int, int, int?> findRecordedHeadway,
        SearchContext context)
    {
        if (context.ExploredNodes >= context.Options.MaxNodes || context.Stopwatch.Elapsed >= context.Options.TimeLimit)
        {
            return SearchOutcome.LimitReached;
        }

        context.ExploredNodes++;
        if (index >= domains.Count)
        {
            return SearchOutcome.Found;
        }

        foreach (StationRouteTrackAlternative alternative in domains[index].Alternatives)
        {
            if (selected.All(existing => AreCompatible(existing, alternative, findRecordedHeadway)))
            {
                selected.Add(alternative);
                SearchOutcome outcome = Search(domains, index + 1, selected, findRecordedHeadway, context);
                if (outcome == SearchOutcome.Found)
                {
                    return SearchOutcome.Found;
                }

                selected.RemoveAt(selected.Count - 1);
                if (outcome == SearchOutcome.LimitReached)
                {
                    return SearchOutcome.LimitReached;
                }
            }
        }

        return SearchOutcome.Exhausted;
    }

    private enum SearchOutcome
    {
        Found,
        Exhausted,
        LimitReached
    }

    private sealed record ComponentSearchResult(
        SearchOutcome Outcome,
        IReadOnlyList<StationRouteTrackAlternative> SelectedAlternatives);

    private sealed class SearchContext(MicroSearchOptions options, System.Diagnostics.Stopwatch stopwatch)
    {
        internal MicroSearchOptions Options { get; } = options;
        internal System.Diagnostics.Stopwatch Stopwatch { get; } = stopwatch;
        internal long ExploredNodes { get; set; }
    }

    private static bool AreCompatible(
        StationRouteTrackAlternative first,
        StationRouteTrackAlternative second,
        Func<int, int, int?> findRecordedHeadway)
    {
        if (            string.Equals(first.TrackName, second.TrackName, StringComparison.OrdinalIgnoreCase) &&
            first.OccupationStartSeconds < second.OccupationEndSeconds - ToleranceSeconds &&
            first.OccupationEndSeconds > second.OccupationStartSeconds + ToleranceSeconds)
        {
            return false;
        }

        foreach ((RouteRecord Route, double Time) firstEvent in BuildEvents(first))
        {
            foreach ((RouteRecord Route, double Time) secondEvent in BuildEvents(second))
            {
                if (!EventsAreCompatible(firstEvent, secondEvent, findRecordedHeadway))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static IReadOnlyList<(RouteRecord Route, double Time)> BuildEvents(StationRouteTrackAlternative alternative)
    {
        List<(RouteRecord Route, double Time)> events = new();
        if (alternative.IncomingRoute is not null)
        {
            events.Add((alternative.IncomingRoute, alternative.ArrivalSeconds));
        }
        if (alternative.OutgoingRoute is not null)
        {
            events.Add((alternative.OutgoingRoute, alternative.DepartureSeconds));
        }
        return events;
    }

    private static bool EventsAreCompatible(
        (RouteRecord Route, double Time) first,
        (RouteRecord Route, double Time) second,
        Func<int, int, int?> findRecordedHeadway)
    {
        SelectedRouteEventHeadwayDecision decision = SelectedRouteEventHeadwayPolicy.Evaluate(
            first.Route.RouteId,
            first.Time,
            second.Route.RouteId,
            second.Time,
            findRecordedHeadway);
        return decision.Kind != SelectedRouteEventHeadwayDecisionKind.Violated;
    }
}
