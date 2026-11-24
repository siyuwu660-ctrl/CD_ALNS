using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

internal sealed record QuotaDependencyRow(string Key,IReadOnlyList<string> StopIds,int Count,int Lower,int Upper,
    IReadOnlyDictionary<string,int> Contributions,IReadOnlySet<string> TimeCrossingStopIds)
{
    internal int Violation=>Math.Max(0,Lower-Count)+Math.Max(0,Count-Upper);
    internal int Slack=>Math.Min(Count-Lower,Upper-Count);
}
internal sealed record QuotaDependencyExpansion(IReadOnlySet<string> FreeStopIds,IReadOnlySet<string> FreeTimeStopIds,
    IReadOnlyList<QuotaDependencyRow> Rows,bool Bounded,
    IReadOnlyCollection<string>? GuidedAdditions = null, IReadOnlyCollection<string>? SelectedAdditions = null,
    bool NeedsReplacement = false);

/// <summary>Bounded dependency support, not a feasibility certificate. Original repair rows remain authoritative.</summary>
internal static class QuotaDependencySupport
{
    internal static IReadOnlyList<QuotaDependencyRow> Analyze(TimetablingInstance instance,MasterSolutionSnapshot snapshot)
    {
        List<QuotaDependencyRow> rows=[];int index=0;
        foreach(var row in TrainSectionStopQuotaConstraintBuilder.Plan(instance))
        {
            var ids=row.CountedStops.Select(s=>s.StopId).ToArray();
            var contributions=ids.ToDictionary(id=>id,id=>snapshot.StopByStopId[id],StringComparer.OrdinalIgnoreCase);
            rows.Add(new("section:"+index++,ids,contributions.Values.Sum(),row.MinStops,row.MaxStops,contributions,new HashSet<string>()));
        }
        index=0;
        foreach(var rule in instance.StationStopQuotaRules)
        {
            var stops=instance.TrainStops.Where(s=>s.StationName.Equals(rule.StationName,StringComparison.OrdinalIgnoreCase) &&
                s.TrainType.Equals(rule.TrainType,StringComparison.OrdinalIgnoreCase))
                .Where(s=>(s.StopRole==TrainStopRole.Origin?s.EarliestDepSeconds:s.EarliestArrSeconds)<=rule.EndTimeSeconds &&
                    (s.StopRole==TrainStopRole.Origin?s.LatestDepSeconds:s.LatestArrSeconds)>=rule.StartTimeSeconds ||
                    snapshot.StopByStopId[s.StopId]==1 && StationQuotaWindowPolicy.Contains(Time(s),rule.StartTimeSeconds,rule.EndTimeSeconds)).ToArray();
            var contributions=stops.ToDictionary(s=>s.StopId,s=>snapshot.StopByStopId[s.StopId]==1 &&
                StationQuotaWindowPolicy.Contains(Time(s),rule.StartTimeSeconds,rule.EndTimeSeconds)?1:0,StringComparer.OrdinalIgnoreCase);
            var crossing=stops.Where(s=>
            {
                double lower=s.StopRole==TrainStopRole.Origin?s.EarliestDepSeconds:s.EarliestArrSeconds;
                double upper=s.StopRole==TrainStopRole.Origin?s.LatestDepSeconds:s.LatestArrSeconds;
                return lower<=rule.EndTimeSeconds && upper>=rule.StartTimeSeconds &&
                    (lower<rule.StartTimeSeconds || upper>rule.EndTimeSeconds);
            }).Select(s=>s.StopId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            rows.Add(new("station:"+index++,stops.Select(s=>s.StopId).ToArray(),contributions.Values.Sum(),rule.MinStops,rule.MaxStops,contributions,crossing));
        }
        return rows;
        double Time(TrainStopRecord s)=>s.StopRole==TrainStopRole.Origin?snapshot.DepartureSecondsByStopId[s.StopId]:snapshot.ArrivalSecondsByStopId[s.StopId];
    }

    /// <summary>Follow quota deficits and record whether the selected scope can lose required stops.</summary>
    /// <param name="instance">Complete original stop domains and quota rules.</param>
    /// <param name="snapshot">Anchor stop pattern and event times in seconds.</param>
    /// <param name="seedStopIds">Initially released visit IDs.</param>
    /// <param name="maxStops">Maximum number of released cells.</param>
    /// <param name="maxTrains">Maximum number of released trains.</param>
    /// <param name="maxNodes">Maximum number of quota row visits.</param>
    /// <param name="remaining">Optional remaining construction budget in seconds.</param>
    /// <returns>Selected visit IDs, quota rows and bounded-expansion evidence.</returns>
    internal static QuotaDependencyExpansion Expand(TimetablingInstance instance,MasterSolutionSnapshot snapshot,
        IEnumerable<string> seedStopIds,int maxStops=64,int maxTrains=32,int maxNodes=40000,Func<double>? remaining=null)
    {
        var rows=Analyze(instance,snapshot);var domains=StructuralStopDomains.Build(instance);
        bool needsReplacement = false;
        HashSet<string> stops=new(StringComparer.OrdinalIgnoreCase),times=new(StringComparer.OrdinalIgnoreCase);
        HashSet<int> trains=[];int nodes=0;bool bounded=false;
        bool Add(string id)
        {
            if(times.Contains(id)) return false;
            int train=instance.StopsById[id].TrainIndex;
            if(times.Count>=maxStops || !trains.Contains(train) && trains.Count>=maxTrains){bounded=true;return false;}
            times.Add(id);trains.Add(train);
            if(domains[id].Lower==0 && domains[id].Upper==1)stops.Add(id);
            return true;
        }
        foreach(string id in seedStopIds)Add(id);
        bool changed=true;
        while(changed)
        {
            changed=false;
            foreach(var row in rows)
            {
                if(++nodes>maxNodes || remaining?.Invoke()<=0){bounded=true;return new(stops,times,rows,bounded, NeedsReplacement: needsReplacement);}
                // A released event can change membership even with its stop decision fixed.
                var affected=row.StopIds.Where(id=>stops.Contains(id) || times.Contains(id) &&
                    snapshot.StopByStopId[id]==1 && row.TimeCrossingStopIds.Contains(id)).ToArray();
                if(affected.Length==0 && row.Violation==0)continue;
                int minimum=row.Count-affected.Sum(id=>row.Contributions[id]);
                int maximum=row.Count+affected.Count(id=>row.Contributions[id]==0);
                // lower <= C+delta <= upper: positive slack needs no zero-net compensation.
                bool needAdd=minimum<row.Lower,needRemove=maximum>row.Upper;
                needsReplacement |= needAdd;
                if(!needAdd && !needRemove)continue;
                int addNeed=Math.Max(0,row.Lower-minimum),removeNeed=Math.Max(0,maximum-row.Upper);
                foreach(string id in row.StopIds.Where(id=>!times.Contains(id)).OrderBy(id=>instance.StopsById[id].TrainIndex).ThenBy(id=>id,StringComparer.Ordinal))
                {
                    bool canChange=domains[id].Lower==0 && domains[id].Upper==1 || snapshot.StopByStopId[id]==1 && row.TimeCrossingStopIds.Contains(id);
                    if(!canChange)continue;
                    if(row.Contributions[id]==0 && addNeed>0){if(Add(id)){changed=true;addNeed--;}}
                    else if(row.Contributions[id]==1 && removeNeed>0){if(Add(id)){changed=true;removeNeed--;}}
                    if(addNeed==0 && removeNeed==0)break;
                }
            }
        }
        return new(stops,times,rows,bounded, NeedsReplacement: needsReplacement);
    }
}
