using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

internal static class ResourceSpanNeighborhood
{
    /// <summary>Build a span-quality neighborhood with the selected quota expansion mode.</summary>
    /// <param name="instance">Original train visits.</param>
    /// <param name="snapshot">Current event times in seconds and stop pattern.</param>
    /// <param name="round">One-based quality round.</param>
    /// <param name="stagnation">Number of stagnant rounds.</param>
    /// <param name="remaining">Optional remaining construction budget in seconds.</param>
    /// <param name="allowStopChanges">Explicit stop-change decision or the legacy alternating schedule.</param>
    /// <param name="quotaControl">Whether ordinary proximity fills the quota expansion's sized slots.</param>
    /// <returns>Released times and stop decisions for the local repair.</returns>
    internal static ResourceNeighborhood Select(TimetablingInstance instance,MasterSolutionSnapshot snapshot,int round,int stagnation,
        Func<double>? remaining=null,bool? allowStopChanges=null, bool quotaControl = false)
    {
        if(round<1 || stagnation<0) throw new ArgumentOutOfRangeException(nameof(round));
        void CheckBudget() { if(remaining?.Invoke()<=0) throw new TimeoutException("LNS quality neighborhood deadline expired."); }
        CheckBudget();
        // The original LNS keeps its alternating schedule. ALNS explicitly requests a
        // quota-capable support each round, then chooses which decisions to release.
        bool releaseStops=allowStopChanges??round%2==0;
        int depth=stagnation<3?2:stagnation<8?4:8,trainLimit=stagnation<3?12:stagnation<8?20:32;
        var origins=instance.Trains.Select(t=>t.OrderedStops[0]).ToArray();
        var destinations=instance.Trains.Select(t=>t.OrderedStops[^1]).ToArray();
        double first=origins.Min(s=>snapshot.DepartureSecondsByStopId[s.StopId]);
        double last=destinations.Max(s=>snapshot.ArrivalSecondsByStopId[s.StopId]);
        var seeds=origins.Where(s=>Math.Abs(snapshot.DepartureSecondsByStopId[s.StopId]-first)<=1e-6)
            .Concat(destinations.Where(s=>Math.Abs(snapshot.ArrivalSecondsByStopId[s.StopId]-last)<=1e-6))
            .Select(s=>s.StopId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> events=new(seeds,StringComparer.OrdinalIgnoreCase);
        foreach(var train in instance.Trains)
        {
            if(seeds.Contains(train.OrderedStops[0].StopId)) events.UnionWith(train.OrderedStops.Take(depth+1).Select(s=>s.StopId));
            if(seeds.Contains(train.OrderedStops[^1].StopId)) events.UnionWith(train.OrderedStops.TakeLast(depth+1).Select(s=>s.StopId));
        }
        // Keep every tied extreme, even if the mandatory set exceeds the nominal train limit.
        var trains=events.Select(id=>instance.StopsById[id].TrainIndex).ToHashSet();
        var focus=events.Select(id=>instance.StopsById[id]).ToArray();
        var neighbors=instance.TrainStops.Where(s=>!trains.Contains(s.TrainIndex))
            .Select(s=>new { Stop=s,Distance=focus.Where(f=>f.StationName.Equals(s.StationName,StringComparison.OrdinalIgnoreCase))
                .Select(f=>Math.Abs(Time(f.StopId)-Time(s.StopId))).DefaultIfEmpty(double.PositiveInfinity).Min() })
            .Where(x=>double.IsFinite(x.Distance)).GroupBy(x=>x.Stop.TrainIndex)
            .OrderBy(g=>g.Min(x=>x.Distance)).ThenBy(g=>g.Key).Take(24).ToArray();
        int count=Math.Min(6,Math.Max(0,trainLimit-trains.Count));
        for(int i=0;i<Math.Min(count,neighbors.Length);i++)
        {
            var group=neighbors[((round-1)/2+i)%neighbors.Length];
            trains.Add(group.Key);
            foreach(var item in group.Where(x=>x.Distance<=1800 || x.Distance==group.Min(y=>y.Distance)))
            {
                var path=instance.Trains.First(t=>t.TrainIndex==group.Key).OrderedStops;
                int index=Enumerable.Range(0,path.Count).First(j=>path[j].StopId==item.Stop.StopId);
                events.UnionWith(path.Skip(Math.Max(0,index-1)).Take(3).Select(s=>s.StopId));
            }
        }
        HashSet<string> freeStops=new(StringComparer.OrdinalIgnoreCase);
        QuotaDependencyExpansion? quotaExpansion = null;
        if(releaseStops)
        {
            var domains=StructuralStopDomains.Build(instance);
            var targets=events.Where(id=>domains[id].Lower==0 && domains[id].Upper==1)
                .OrderBy(id=>id,StringComparer.Ordinal).Take(64).ToHashSet(StringComparer.OrdinalIgnoreCase);
            freeStops.UnionWith(targets);
            var support = QuotaAblation.Expand(instance, snapshot, events, 64, Math.Max(trainLimit, trains.Count), remaining, quotaControl);
            quotaExpansion = support;
            freeStops.UnionWith(support.FreeStopIds);events.UnionWith(support.FreeTimeStopIds);
            trains.UnionWith(support.FreeTimeStopIds.Select(id=>instance.StopsById[id].TrainIndex));
            events.UnionWith(freeStops);
            // Let quota compensators propagate times along the same train, not just at an isolated stop.
            foreach(var train in instance.Trains.Where(t=>t.OrderedStops.Any(s=>freeStops.Contains(s.StopId))))
            {
                var indices=Enumerable.Range(0,train.OrderedStops.Count).Where(i=>events.Contains(train.OrderedStops[i].StopId)).ToArray();
                events.UnionWith(train.OrderedStops.Skip(Math.Max(0,indices.Min()-1))
                    .Take(indices.Max()-Math.Max(0,indices.Min()-1)+2).Select(s=>s.StopId));
            }
        }
        CheckBudget();
        return new(seeds,events,freeStops,!releaseStops?0:stagnation<3?4:stagnation<8?8:12,
            stagnation<3?600:stagnation<8?1800:null, QuotaExpansions: quotaExpansion is null ? [] : [quotaExpansion]);

        double Time(string id)=>instance.StopsById[id].StopRole==TrainStopRole.Origin
            ?snapshot.DepartureSecondsByStopId[id]:snapshot.ArrivalSecondsByStopId[id];
    }
}
