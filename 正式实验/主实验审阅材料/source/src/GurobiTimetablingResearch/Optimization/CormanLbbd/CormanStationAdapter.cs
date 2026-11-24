using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

internal sealed record CormanEventBinding(string StopId,TimetableEventSide Side);
internal sealed record CormanRouteChoice(int Variable,StationMicroRequirement Requirement,RouteRecord? Incoming,RouteRecord? Outgoing,string Track);
internal sealed record CormanStationProblem(string Station,CormanProblem Problem,
    IReadOnlyDictionary<int,CormanEventBinding> Events,IReadOnlyDictionary<int,CormanBoundary> Boundaries,
    IReadOnlyDictionary<string,int> Modes,IReadOnlyList<CormanRouteChoice> Choices)
{
    internal IReadOnlyList<VerifiedMicroAssignmentWitness> Witnesses(CormanSmtResult result,string fingerprint)
    {
        if(result.Status!=CormanStatus.Feasible)throw new InvalidOperationException("No complete station selection");
        return Choices.Where(c=>result.Assignment[c.Variable]>0).Select(c=>new VerifiedMicroAssignmentWitness(
            Station,c.Requirement.StopId,c.Requirement.TrainIndex,c.Requirement.IsStop,c.Track,c.Incoming?.RouteId,c.Outgoing?.RouteId,
            fingerprint,c.Requirement.ArrivalSeconds,c.Requirement.DepartureSeconds)).ToArray();
    }
}

/// <summary>
/// Project adaptation: master fixes commercial event times and stop modes;
/// station alternatives remain complete. Conditional difference arcs encode
/// route-pair orders without introducing unconstrained unused route clocks.
/// A learned cut is guarded by this entire station's stop-mode pattern.
/// </summary>
internal static class CormanStationAdapter
{
    internal static CormanStationProblem Build(TimetablingInstance instance,MasterSolutionSnapshot snapshot,string station,
        Func<bool> withinBudget,int maximumArcs=2_000_000)
    {
        var builder=new CormanProblemBuilder();Dictionary<int,CormanEventBinding> events=[];
        Dictionary<int,CormanBoundary> boundaries=[];Dictionary<string,int> modes=new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string,(int Arrival,int Departure)> nodeByStop=new(StringComparer.OrdinalIgnoreCase);
        List<CormanRouteChoice> choices=[];int arcCount=0;
        foreach(var train in instance.Trains.OrderBy(t=>t.TrainIndex))
            foreach(var stop in train.OrderedStops.Where(s=>s.StationName.Equals(station,StringComparison.OrdinalIgnoreCase)))
            {
                CheckBudget();int mode=snapshot.StopByStopId[stop.StopId];modes.Add(stop.StopId,mode);
                var req=StationLevelMicroFeasibilitySlave.BuildRequirementForMode(instance,train,stop,mode==1,
                    snapshot.ArrivalSecondsByStopId[stop.StopId],snapshot.DepartureSecondsByStopId[stop.StopId]);
                int arrival=AddEvent(stop.StopId,TimetableEventSide.Arrival,req.ArrivalSeconds);
                int departure=AddEvent(stop.StopId,TimetableEventSide.Departure,req.DepartureSeconds);
                nodeByStop.Add(stop.StopId,(arrival,departure));
                RouteRecord?[] incoming=req.RequiresIncomingRoute?req.IncomingRoutes.Cast<RouteRecord?>().ToArray():[null];
                RouteRecord?[] outgoing=req.RequiresOutgoingRoute?req.OutgoingRoutes.Cast<RouteRecord?>().ToArray():[null];
                var domain=(from a in incoming from d in outgoing
                            let track=a?.TrackName??d?.TrackName
                            where track is not null && (a is null || d is null || a.TrackName.Equals(d.TrackName,StringComparison.OrdinalIgnoreCase))
                                && req.FeasibleTracks.Contains(track,StringComparer.OrdinalIgnoreCase)
                            select (a,d,track)).Distinct().ToArray();
                int[] variables=builder.Choices(domain.Length);
                for(int i=0;i<domain.Length;i++)
                {
                    var item=domain[i];var choice=new CormanRouteChoice(variables[i],req,item.a,item.d,item.track!);choices.Add(choice);
                }
                // Same-train route compatibility is already enforced by domain.
                // Inter-train headways apply only to distinct trains.

            }
        for(int i=0;i<choices.Count;i++)for(int j=i+1;j<choices.Count;j++)
        {
            if((j&31)==0)CheckBudget();var a=choices[i];var b=choices[j];
            if(a.Requirement.TrainIndex==b.Requirement.TrainIndex)continue;
            int[] guards=[a.Variable,b.Variable];
            if(a.Track.Equals(b.Track,StringComparison.OrdinalIgnoreCase))
            {
                var aa=Occupation(a.Requirement);var bb=Occupation(b.Requirement);
                Order(aa.EndNode,bb.StartNode,aa.EndOffset-bb.StartOffset,
                    bb.EndNode,aa.StartNode,bb.EndOffset-aa.StartOffset,guards);
            }
            foreach(var ae in RouteEvents(a))foreach(var be in RouteEvents(b))Headway(ae.Node,ae.Route,be.Node,be.Route,guards);
        }
        CheckBudget();return new(station,builder.Build(),events,boundaries,modes,choices);

        int AddEvent(string id,TimetableEventSide side,double value)
        {
            int n=builder.Node(id+"::"+side);events.Add(n,new(id,side));boundaries.Add(n,new(value,value));return n;
        }
        void CheckBudget(){if(!withinBudget())throw new TimeoutException("Station graph build budget exhausted");}
        void Arc(int from,int to,double weight,int[] when)
        {
            if(++arcCount>maximumArcs)throw new CormanConstructionLimitException("Station difference arc limit");
            builder.Arc(from,to,weight,when);
        }
        void Order(int from1,int to1,double length1,int from2,int to2,double length2,int[] guards)
        {
            int order=builder.Boolean();Arc(from1,to1,length1,[..guards,order]);Arc(from2,to2,length2,[..guards,-order]);
        }
        void Headway(int first,int firstRoute,int second,int secondRoute,int[] guards)
        {
            int forward=HeadwayValue(firstRoute,secondRoute),reverse=HeadwayValue(secondRoute,firstRoute);
            if(forward==0 && reverse==0)return;
            Order(first,second,forward,second,first,reverse,guards);
        }
        int HeadwayValue(int leading,int following)=>instance.TryGetRouteHeadwayRule(leading,following,out var rule)?rule!.MinHeadwaySeconds:0;
        IEnumerable<(int Node,int Route)> RouteEvents(CormanRouteChoice c)
        {
            var nodes=nodeByStop[c.Requirement.StopId];
            if(c.Incoming is not null)yield return(nodes.Arrival,c.Incoming.RouteId);
            if(c.Outgoing is not null)yield return(nodes.Departure,c.Outgoing.RouteId);
        }
        (int StartNode,double StartOffset,int EndNode,double EndOffset) Occupation(StationMicroRequirement req)
        {
            var nodes=nodeByStop[req.StopId];
            return req.StopRole switch
            {
                TrainStopRole.Origin=>(nodes.Departure,0,nodes.Departure,0),
                TrainStopRole.Destination=>(nodes.Arrival,0,nodes.Arrival,0),
                _=>(nodes.Arrival,0,nodes.Departure,0)
            };
        }
    }
}
internal sealed class CormanConstructionLimitException(string message):Exception(message);
