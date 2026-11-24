using Gurobi;
using GurobiTimetablingResearch.Domain;
namespace GurobiTimetablingResearch.Optimization;

internal sealed record ResourceTightLink(string First,string Second);
internal static class ResourceTightSupport
{
    internal static IReadOnlyList<ResourceTightLink> FromSolvedModel(GRBModel model,BasicMilpModel view,Func<double>? remaining=null)
    {
        var names=view.ArrivalVars.Concat(view.DepartureVars).ToDictionary(p=>p.Value.VarName,p=>p.Key,StringComparer.Ordinal);
        HashSet<ResourceTightLink> links=[];
        foreach(var row in model.GetConstrs())
        {
            if(remaining?.Invoke()<=0)break;
            if(Math.Abs(row.Slack)>30)continue;
            var expression=model.GetRow(row);
            AddLinks(links,Enumerable.Range(0,expression.Size).Select(i=>names.GetValueOrDefault(expression.GetVar(i).VarName)).OfType<string>());
        }
        return links.ToArray();
    }
    internal static void AddLinks(HashSet<ResourceTightLink> links,IEnumerable<string> ids)
    {
        var stops=ids.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
        // Capacity aggregates are not temporal blockers. Pairwise timing rows and train chains are.
        if(stops.Length is <2 or >4)return;
        for(int i=0;i<stops.Length;i++)for(int j=i+1;j<stops.Length;j++)links.Add(new(stops[i],stops[j]));
    }

    /// <summary>Follow blocking links, then expand stop support in the selected experimental mode.</summary>
    /// <param name="instance">Original visits and event domains.</param>
    /// <param name="snapshot">Anchor event times in seconds.</param>
    /// <param name="core">Base neighborhood.</param>
    /// <param name="links">Tight timing links between visits.</param>
    /// <param name="scale">One-based expansion tier.</param>
    /// <param name="remaining">Optional remaining wall-clock seconds.</param>
    /// <param name="fromReleasedEvents">Whether released times also seed the link traversal.</param>
    /// <param name="quotaControl">Whether proximity fills the expansion's sized slots.</param>
    /// <returns>The expanded local repair scope.</returns>
    internal static ResourceNeighborhood ExpandWithQuota(TimetablingInstance instance,MasterSolutionSnapshot snapshot,
        ResourceNeighborhood core,IReadOnlyList<ResourceTightLink> links,int scale,Func<double>? remaining=null,bool fromReleasedEvents=false, bool quotaControl = false)
    {
        void CheckBudget() { if(remaining?.Invoke()<=0)throw new TimeoutException("Coupled blocking deadline expired."); }
        CheckBudget();
        if(scale is <1 or >3)throw new ArgumentOutOfRangeException(nameof(scale));
        var expanded=Expand(instance,core,links,scale,remaining,fromReleasedEvents);
        if(links.Count==0)return expanded;
        var support = QuotaAblation.Expand(instance, snapshot, expanded.FreeTimeStopIds.Concat(core.FreeStopIds),
            Math.Max(64,core.FreeStopIds.Count), Math.Max(new[]{12,20,32}[scale-1],
                expanded.FreeTimeStopIds.Select(id=>instance.StopsById[id].TrainIndex).Distinct().Count()), remaining, quotaControl);
        var times=expanded.FreeTimeStopIds.Concat(support.FreeTimeStopIds).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var free=core.FreeStopIds.Concat(support.FreeStopIds).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int maxChanges=Math.Max(core.MaxStopChanges,new[]{4,8,12}[scale-1]);
        // Consecutive timing support lets changed dwell/running times propagate to compensation stops.
        foreach(var train in instance.Trains.Where(t=>t.OrderedStops.Any(s=>free.Contains(s.StopId))))
        {
            CheckBudget();
            var positions=Enumerable.Range(0,train.OrderedStops.Count).Where(i=>times.Contains(train.OrderedStops[i].StopId)).ToArray();
            int first=Math.Max(0,positions.Min()-1),last=Math.Min(train.OrderedStops.Count-1,positions.Max()+1);
            times.UnionWith(train.OrderedStops.Skip(first).Take(last-first+1).Select(s=>s.StopId));
        }
        return expanded with {FreeTimeStopIds=times,FreeStopIds=free,MaxStopChanges=maxChanges,
            QuotaExpansions = [..core.QuotaExpansions ?? [], support]};
    }

    internal static ResourceNeighborhood Expand(TimetablingInstance instance,ResourceNeighborhood core,IReadOnlyList<ResourceTightLink> links,int scale,Func<double>? remaining=null,bool fromReleasedEvents=false)
    {
        var times=core.FreeTimeStopIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var trains=times.Select(id=>instance.StopsById[id].TrainIndex).ToHashSet();
        int limit=Math.Max(trains.Count,new[]{12,20,32}[scale-1]);
        var adjacency=new Dictionary<string,HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach(var link in links)
        {
            if(!instance.StopsById.ContainsKey(link.First)||!instance.StopsById.ContainsKey(link.Second))continue;
            Add(link.First,link.Second);Add(link.Second,link.First);
        }
        // A conflict can be blocked far upstream from its station. Feasibility
        // recovery follows tight rows from the already released train paths too.
        var frontier=(fromReleasedEvents?core.FreeTimeStopIds.Concat(core.SeedStopIds):core.SeedStopIds)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);HashSet<string> visited=[];
        for(int depth=0;depth<new[]{2,4,8}[scale-1];depth++)
        {
            if(remaining?.Invoke()<=0)throw new TimeoutException("Tight support deadline expired.");
            HashSet<string> next=new(StringComparer.OrdinalIgnoreCase);
            foreach(string id in frontier.Order(StringComparer.Ordinal))
            {
                if(!visited.Add(id))continue;
                foreach(string target in (adjacency.GetValueOrDefault(id)??[]).Order(StringComparer.Ordinal))
                {
                    int train=instance.StopsById[target].TrainIndex;
                    if(!trains.Contains(train) && trains.Count>=limit)continue;
                    trains.Add(train);times.Add(target);if(!visited.Contains(target))next.Add(target);
                }
            }
            frontier=next;
        }
        return core with { FreeTimeStopIds=times };
        void Add(string a,string b)
        { if(!adjacency.TryGetValue(a,out var neighbors))adjacency[a]=neighbors=new(StringComparer.OrdinalIgnoreCase);neighbors.Add(b); }
    }
}
