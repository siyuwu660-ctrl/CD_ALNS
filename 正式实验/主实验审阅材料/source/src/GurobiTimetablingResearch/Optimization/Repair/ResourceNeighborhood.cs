using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

internal sealed record ResourceNeighborhood(IReadOnlySet<string> SeedStopIds,IReadOnlySet<string> FreeTimeStopIds,
    IReadOnlySet<string> FreeStopIds,int MaxStopChanges,double? RadiusSeconds,
    IReadOnlySet<string>? FreeSectionOrderKeys=null,IReadOnlySet<string>? FreeStationOrderKeys=null,
    [property: System.Text.Json.Serialization.JsonIgnore] IReadOnlyCollection<QuotaDependencyExpansion>? QuotaExpansions = null)
{
    // Null retains the legacy time/stop-dependent order scope. An explicit empty
    // set fixes every order, independently of which event clocks are released.
    /// <summary>Select a resource-conflict neighborhood with quota compensation and timing support.</summary>
    /// <param name="instance">Original train visits and station search scores.</param>
    /// <param name="snapshot">Current candidate with physical event times in seconds.</param>
    /// <param name="profile">Current station conflicts and deficit seconds.</param>
    /// <param name="round">One-based repair round.</param>
    /// <param name="stagnation">Number of rounds without improvement.</param>
    /// <param name="remaining">Optional remaining construction budget in seconds.</param>
    /// <param name="prioritizeOvertaking">Whether adaptive search weights eligible overtaking conflicts.</param>
    /// <param name="quotaControl">Whether ordinary proximity fills the quota expansion's sized slots.</param>
    /// <returns>The visit, time and stop decisions eligible for local repair.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Round or stagnation is out of range.</exception>
    /// <exception cref="TimeoutException">The construction budget expires.</exception>
    internal static ResourceNeighborhood Select(TimetablingInstance instance,MasterSolutionSnapshot snapshot,
        ResourceConflictProfile profile,int round,int stagnation,Func<double>? remaining=null, bool prioritizeOvertaking = false, bool quotaControl = false)
    {
        if(round<1 || stagnation<0) throw new ArgumentOutOfRangeException(nameof(round));
        void CheckBudget() { if(remaining?.Invoke()<=0) throw new TimeoutException("LNS neighborhood deadline expired."); }
        CheckBudget();
        HashSet<string> seeds=new(StringComparer.OrdinalIgnoreCase);
        var conflicts = profile.Conflicts.OrderByDescending(conflict =>
            {
                double weight = prioritizeOvertaking ? StationOvertakingResourcePolicy.SearchWeight(instance, conflict.StationName) : 1.0;
                // Ordinary conflicts retain their original priority when this pair cannot overtake.
                if (weight < 1 && StationOvertakingResourcePolicy.CanReverse(instance,
                    instance.StopsById[conflict.FirstStopId], instance.StopsById[conflict.SecondStopId]))
                    return conflict.Seconds * weight;
                return conflict.Seconds;
            }).ThenBy(c=>c.StationName,StringComparer.Ordinal)
            .ThenBy(c=>c.FirstStopId,StringComparer.Ordinal).ThenBy(c=>c.SecondStopId,StringComparer.Ordinal).ToArray();
        if(conflicts.Length>0)
        {
            var seed=conflicts[(round-1)%conflicts.Length];seeds.Add(seed.FirstStopId);seeds.Add(seed.SecondStopId);
            // Include overlapping conflicts in the same time component, not unrelated day-long cores.
            double time=Time(seed.FirstStopId);
            foreach(var other in conflicts.Where(c=>c.StationName==seed.StationName &&
                (seeds.Contains(c.FirstStopId) || seeds.Contains(c.SecondStopId)) && Math.Abs(Time(c.FirstStopId)-time)<600).Take(4))
            { seeds.Add(other.FirstStopId);seeds.Add(other.SecondStopId); }
        }
        else
        {
            var core=profile.Stations.Values.Where(s=>s.CoreStopIds.Count>0).OrderBy(s=>s.StationName,StringComparer.Ordinal).FirstOrDefault();
            if(core is not null) seeds.UnionWith(core.CoreStopIds);
            else
            {
                // Quality phase: rotate a small temporal train block with original objective priorities.
                foreach(var train in instance.Trains.Skip((round-1)%instance.Trains.Count).Take(4))
                    seeds.Add(train.OrderedStops[train.OrderedStops.Count/2].StopId);
            }
        }
        var domains=StructuralStopDomains.Build(instance);
        var freeStops=seeds.Where(id=>domains[id].Lower==0 && domains[id].Upper==1).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var trains=seeds.Select(id=>instance.StopsById[id].TrainIndex).ToHashSet();
        int trainLimit=Math.Max(trains.Count,stagnation<3?12:stagnation<8?20:32);
        var quotaSupport=QuotaAblation.Expand(instance, snapshot, seeds, 64, trainLimit, remaining, quotaControl);
        freeStops.UnionWith(quotaSupport.FreeStopIds);
        trains.UnionWith(quotaSupport.FreeTimeStopIds.Select(id=>instance.StopsById[id].TrainIndex));
        // Boundary trains are selected by physical time proximity, never by input/train-number order.
        var seedStations=seeds.Select(id=>instance.StopsById[id].StationName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach(var train in instance.TrainStops.Where(s=>seedStations.Contains(s.StationName) && !trains.Contains(s.TrainIndex))
            .Select(s=>new { s.TrainIndex,Distance=seeds.Where(id=>instance.StopsById[id].StationName==s.StationName).Min(id=>Math.Abs(Time(id)-Time(s.StopId))) })
            .GroupBy(x=>x.TrainIndex).OrderBy(g=>g.Min(x=>x.Distance)).ThenBy(g=>g.Key).Take(Math.Min(6,Math.Max(0,trainLimit-trains.Count))))
            trains.Add(train.Key);
        HashSet<string> events=new(StringComparer.OrdinalIgnoreCase);
        var focusOrders=seeds.Concat(freeStops).Select(id=>instance.StopsById[id].StationOrder).ToArray();
        int lower=focusOrders.Min()-(stagnation<3?2:4),upper=focusOrders.Max()+(stagnation<3?2:4);
        foreach(var stop in instance.TrainStops.Where(s=>trains.Contains(s.TrainIndex) && (stagnation>=8 || s.StationOrder>=lower && s.StationOrder<=upper)))
            events.Add(stop.StopId);
        events.UnionWith(seeds);events.UnionWith(freeStops);events.UnionWith(quotaSupport.FreeTimeStopIds);
        CheckBudget();
        return new(seeds,events,freeStops,stagnation<3?4:stagnation<8?8:12,
            stagnation<3?600:stagnation<8?1800:null, QuotaExpansions: [quotaSupport]);

        double Time(string id)=>instance.StopsById[id].StopRole==TrainStopRole.Origin?snapshot.DepartureSecondsByStopId[id]:snapshot.ArrivalSecondsByStopId[id];
    }
}
