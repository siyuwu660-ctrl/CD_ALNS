// 主问题径路域间隔提供器把停站的可选径路、径路对分类和追踪间隔解析组合为建模查询接口。
// 它为每对车站事件返回可证明安全的间隔或保守替代值，使先后约束覆盖整个离散径路选择域。
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

internal static class MasterRouteDomainHeadwayProvider
{
    internal static int ResolveLowerBound(
        TimetablingInstance instance,
        TrainStopRecord leadingStop,
        bool leadingIsStop,
        TrainStopRecord followingStop,
        bool followingIsStop,
        string sectionName,
        bool isOriginSide)
    {
        MovementEvent leadingEvent = ResolveEvent(leadingIsStop, isOriginSide);
        MovementEvent followingEvent = ResolveEvent(followingIsStop, isOriginSide);
        List<RouteRecord> leadingRoutes = ResolveRoutes(instance, leadingStop.StationName, sectionName, leadingEvent, !isOriginSide);
        List<RouteRecord> followingRoutes = ResolveRoutes(instance, followingStop.StationName, sectionName, followingEvent, !isOriginSide);

        return SafeRouteDomainHeadwayResolver.ResolveLowerBound(
            leadingRoutes,
            followingRoutes,
            (leadingRouteId, followingRouteId) =>
                instance.TryGetRouteHeadwayRule(leadingRouteId, followingRouteId, out RouteHeadwayRule? rule) && rule is not null
                    ? rule.MinHeadwaySeconds
                    : null,
            ResolvePermanentAllowedTracks(instance, leadingStop, leadingIsStop),
            ResolvePermanentAllowedTracks(instance, followingStop, followingIsStop));
    }

    internal static int ResolveStationLowerBound(
        TimetablingInstance instance,
        TrainStopRecord leadingStop,
        TrainStopRecord? leadingNextStop,
        StationEventSide leadingSide,
        bool leadingIsStop,
        TrainStopRecord followingStop,
        TrainStopRecord? followingNextStop,
        StationEventSide followingSide,
        bool followingIsStop)
    {
        string? leadingSection = ResolveStationEventSection(leadingStop, leadingNextStop, leadingSide);
        string? followingSection = ResolveStationEventSection(followingStop, followingNextStop, followingSide);
        if (string.IsNullOrWhiteSpace(leadingSection) || string.IsNullOrWhiteSpace(followingSection))
        {
            return 0;
        }

        MovementEvent leadingEvent = ResolveStationEvent(leadingIsStop, leadingSide);
        MovementEvent followingEvent = ResolveStationEvent(followingIsStop, followingSide);
        return SafeRouteDomainHeadwayResolver.ResolveLowerBound(
            ResolveRoutes(instance, leadingStop.StationName, leadingSection, leadingEvent, leadingSide == StationEventSide.Incoming),
            ResolveRoutes(instance, followingStop.StationName, followingSection, followingEvent, followingSide == StationEventSide.Incoming),
            (leadingRouteId, followingRouteId) =>
                instance.TryGetRouteHeadwayRule(leadingRouteId, followingRouteId, out RouteHeadwayRule? rule) && rule is not null
                    ? rule.MinHeadwaySeconds
                    : null,
            ResolvePermanentAllowedTracks(instance, leadingStop, leadingIsStop),
            ResolvePermanentAllowedTracks(instance, followingStop, followingIsStop));
    }

    private static string? ResolveStationEventSection(
        TrainStopRecord stop,
        TrainStopRecord? nextStop,
        StationEventSide side) =>
        side == StationEventSide.Incoming ? stop.IncomingSectionName : nextStop?.IncomingSectionName;

    private static MovementEvent ResolveStationEvent(bool isStop, StationEventSide side) =>
        isStop
            ? (side == StationEventSide.Incoming ? MovementEvent.Arrival : MovementEvent.Departure)
            : MovementEvent.Passing;

    private static MovementEvent ResolveEvent(bool isStop, bool isOriginSide) =>
        isStop ? (isOriginSide ? MovementEvent.Departure : MovementEvent.Arrival) : MovementEvent.Passing;

    private static List<RouteRecord> ResolveRoutes(
        TimetablingInstance instance,
        string stationName,
        string sectionName,
        MovementEvent movementEvent, bool incoming) =>
        instance.Routes.Where(route =>
            string.Equals(route.StationName, stationName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(route.SectionName, sectionName, StringComparison.OrdinalIgnoreCase) &&
            route.MovementEvent == movementEvent && RouteEventDirectionPolicy.Matches(route, incoming)).ToList();

    private static IReadOnlySet<string>? ResolvePermanentAllowedTracks(
        TimetablingInstance instance,
        TrainStopRecord stop,
        bool isStop)
    {
        HashSet<string>? domain = null;
        if (instance.TryGetTrackChoiceRecord(stop.TrainIndex, stop.StationOrder, isStop, out TrainTrackChoiceRecord? choice) && choice is not null)
        {
            domain = choice.AllowedTrackNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        if (instance.TryGetBenchmarkTrackRestriction(stop.TrainIndex, stop.StationOrder, out BenchmarkTrackRestriction? restriction) && restriction is not null)
        {
            if (domain is null)
            {
                domain = restriction.AllowedTrackNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                domain.IntersectWith(restriction.AllowedTrackNames);
            }
        }

        return domain;
    }
}
