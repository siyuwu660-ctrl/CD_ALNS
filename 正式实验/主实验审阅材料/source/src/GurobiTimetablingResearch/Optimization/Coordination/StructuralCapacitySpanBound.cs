using System.Text.Json;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

public sealed record StructuralCapacityEvent(int TrainIndex,string StopId,string SectionName,
    string FromStation,string ToStation,bool IsOriginSide,double MinimumPrefixSeconds,double MinimumSuffixSeconds);
public sealed record StructuralCapacityWitness(string SectionName,string FromStation,string ToStation,string EventSide,
    int EventCount,int MinimumDomainHeadwaySeconds,double EffectiveHeadwaySeconds,
    double MinimumPrefixSeconds,double MinimumSuffixSeconds,double LowerBoundSeconds,
    string MinimumHeadwayWitness,IReadOnlyList<StructuralCapacityEvent> Events);
public sealed record StructuralCapacityBoundReport(double? LowerBoundSeconds,
    double TimingToleranceSeconds,IReadOnlyList<StructuralCapacityWitness> Sections);

/// <summary>
/// Sort n same-direction events at one end of a section. Every consecutive pair is
/// separated by at least max(0,h-eps), where h minimizes over BOTH temporal orders,
/// all possible stop modes and the full permanent route domains. Then
/// L-F >= min(prefix) + (n-1)*max(0,h-eps) + min(suffix).
/// </summary>
public static class StructuralCapacitySpanBound
{
    public static StructuralCapacityBoundReport Build(TimetablingInstance instance,double timingToleranceSeconds=0)
        =>Build(instance,StructuralSpanBound.Build(instance,timingToleranceSeconds));

    internal static StructuralCapacityBoundReport Build(TimetablingInstance instance,StructuralSpanBoundReport paths)
    {
        double eps=paths.TimingToleranceSeconds;
        var proofs=paths.Trains.ToDictionary(t=>t.TrainIndex);
        List<StructuralCapacityEvent> events=[];
        foreach(var train in instance.Trains)
        {
            var path=train.OrderedStops;var proof=proofs[train.TrainIndex];
            var cumulative=new double[proof.Edges.Count+1];
            for(int k=0;k<proof.Edges.Count;k++)cumulative[k+1]=cumulative[k]+proof.Edges[k].MinimumSeconds;
            // The original all-pair validator uses the first traversal of a section per train.
            HashSet<string> seenSections=new(StringComparer.OrdinalIgnoreCase);
            for(int j=1;j<path.Count;j++)
            {
                string section=path[j].IncomingSectionName!;
                if(!seenSections.Add(section))continue;
                Add(path[j-1],true,2*j-2);
                Add(path[j],false,2*j-1);
                void Add(TrainStopRecord stop,bool originSide,int at)=>events.Add(new(
                    train.TrainIndex,stop.StopId,section,path[j-1].StationName,path[j].StationName,originSide,
                    cumulative[at],cumulative[^1]-cumulative[at]));
            }
        }
        List<StructuralCapacityWitness> witnesses=[];
        foreach(var group in events.GroupBy(e=>(e.SectionName.ToUpperInvariant(),e.FromStation.ToUpperInvariant(),
            e.ToStation.ToUpperInvariant(),e.IsOriginSide)))
        {
            var items=group.OrderBy(e=>e.TrainIndex).ToArray();
            if(items.Length<2)continue;
            var first=items[0];
            var options=items.Select(e=>{
                var stop=instance.StopsById[e.StopId];
                bool[] modes=proofs[e.TrainIndex].StopDomains[e.StopId] switch {
                    "1"=>[true],"0"=>[false],_=>[false,true]
                };
                return modes.Select(mode=>(Stop:stop,Mode:mode,Key:DomainSignature(instance,stop,mode))).ToArray();
            }).ToArray();
            var cache=new Dictionary<(string Leading,string Following),int>();
            int h=int.MaxValue;string reason="";
            for(int a=0;a<items.Length;a++)for(int b=a+1;b<items.Length;b++)
                foreach(var x in options[a])foreach(var y in options[b])
                {
                    Consider(x,y);Consider(y,x);
                }
            void Consider((TrainStopRecord Stop,bool Mode,string Key) lead,(TrainStopRecord Stop,bool Mode,string Key) follow)
            {
                var key=(lead.Key,follow.Key);
                if(!cache.TryGetValue(key,out int value))
                {
                    value=MasterRouteDomainHeadwayProvider.ResolveLowerBound(instance,lead.Stop,lead.Mode,
                        follow.Stop,follow.Mode,first.SectionName,first.IsOriginSide);
                    cache.Add(key,value);
                }
                if(value<h) {
                    h=value;reason=$"{lead.Stop.StopId}(stop={lead.Mode}) -> {follow.Stop.StopId}(stop={follow.Mode})";
                }
            }
            double effective=Math.Max(0,h-eps);
            double prefix=items.Min(e=>e.MinimumPrefixSeconds),suffix=items.Min(e=>e.MinimumSuffixSeconds);
            witnesses.Add(new(first.SectionName,first.FromStation,first.ToStation,first.IsOriginSide?"Departure":"Arrival",
                items.Length,h,effective,prefix,suffix,prefix+(items.Length-1)*effective+suffix-1e-6,reason,items));
        }
        return new(witnesses.Count==0?null:witnesses.Max(w=>w.LowerBoundSeconds),eps,witnesses);
    }

    private static string DomainSignature(TimetablingInstance instance,TrainStopRecord stop,bool isStop)
    {
        // Exactly the permanent track intersection used by MasterRouteDomainHeadwayProvider.
        // Cache only equivalent route domains within one station/section/event-side group.
        HashSet<string>? tracks=null;
        if(instance.TryGetTrackChoiceRecord(stop.TrainIndex,stop.StationOrder,isStop,out var choice) && choice is not null)
            tracks=choice.AllowedTrackNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if(instance.TryGetBenchmarkTrackRestriction(stop.TrainIndex,stop.StationOrder,out var restriction) && restriction is not null) {
            if(tracks is null)tracks=restriction.AllowedTrackNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
            else tracks.IntersectWith(restriction.AllowedTrackNames);
        }
        return JsonSerializer.Serialize(new {isStop,tracks=tracks?.Select(t=>t.ToUpperInvariant()).Order(StringComparer.Ordinal).ToArray()});
    }
}
