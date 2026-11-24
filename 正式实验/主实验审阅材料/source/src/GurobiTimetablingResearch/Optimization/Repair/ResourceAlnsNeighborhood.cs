using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

/// <summary>Build adaptive conflict neighborhoods for the shared local MIP repair.</summary>
/// <param name="instance">Original visits, topology and structural stop domains.</param>
/// <param name="quotaControl">Whether ordinary proximity fills the measured quota support slots.</param>
internal sealed class ResourceAlnsNeighborhood(TimetablingInstance instance, bool quotaControl = false)
{
    private readonly Dictionary<ResourceAlnsAction,ResourceNeighborhood> prepared=[];
    internal IReadOnlyList<ResourceTightLink> BlockingLinks { get; set; }=[];
    internal double? FeasibilityRadiusCap { get; set; }
    private MasterSolutionSnapshot? preparedSnapshot;
    private ResourceConflictProfile? preparedProfile;
    private int preparedRound;
    private bool preparedQuality;
    private double? preparedRadiusCap;
    internal void PreparePhase(bool quality,bool improveQuality)
    {
        if(!quality)return;
        FeasibilityRadiusCap=null;
        if(!improveQuality)BlockingLinks=[];
        prepared.Clear();
    }
    internal IReadOnlyList<ResourceAlnsAction> Available(MasterSolutionSnapshot snapshot,ResourceConflictProfile profile,
        bool quality,int round,double seconds,Func<double>? remaining=null)
    {
        CheckBudget(remaining);
        prepared.Clear();preparedSnapshot=snapshot;preparedProfile=profile;preparedRound=round;preparedQuality=quality;
        preparedRadiusCap=FeasibilityRadiusCap;
        int maximum=seconds>=60?3:seconds>=25?2:1;
        for(int scale=1;scale<=maximum;scale++)
        {
            var joint=new ResourceAlnsAction(ResourceAlnsOperator.Joint,scale);
            var core=Original(snapshot,profile,round,Regime(scale),quality,remaining);
            prepared.Add(joint,core);
            foreach(var direction in new[]{ResourceAlnsOperator.Time,ResourceAlnsOperator.Quota,ResourceAlnsOperator.Boundary}
                .Concat(BlockingLinks.Count>0?new[]{ResourceAlnsOperator.Blocking}:[]))
            {
                CheckBudget(remaining);
                var candidate=Focused(snapshot,profile,core,direction,scale,remaining,quality);
                // An inapplicable or identical direction must not receive its own lottery tickets.
                if(candidate is not null && !prepared.Where(p=>p.Key.Scale==scale).Any(p=>Same(p.Value,candidate)))
                    prepared.Add(new(direction,scale),candidate);
            }
        }
        CheckBudget(remaining);
        return prepared.Keys.ToArray();
    }

    internal ResourceNeighborhood Build(MasterSolutionSnapshot snapshot,ResourceConflictProfile profile,
        ResourceAlnsAction action,int round,Func<double>? remaining=null,int stagnation=0,bool quality=false)
    {
        CheckBudget(remaining);
        if(action.Scale is <1 or >3 || !Enum.IsDefined(action.Operator)) throw new ArgumentOutOfRangeException(nameof(action));
        if(stagnation<0) throw new ArgumentOutOfRangeException(nameof(stagnation));
        if(action.Operator!=ResourceAlnsOperator.Legacy && ReferenceEquals(snapshot,preparedSnapshot) &&
            ReferenceEquals(profile,preparedProfile) && round==preparedRound && quality==preparedQuality &&
            FeasibilityRadiusCap==preparedRadiusCap && prepared.TryGetValue(action,out var cached))
            return cached;
        var core=Original(snapshot,profile,round,action.Operator==ResourceAlnsOperator.Legacy?stagnation:Regime(action.Scale),quality,remaining,
            legacy:action.Operator==ResourceAlnsOperator.Legacy);
        var result=action.Operator is ResourceAlnsOperator.Joint or ResourceAlnsOperator.Legacy?core:
            Focused(snapshot,profile,core,action.Operator,action.Scale,remaining,quality)
                ??throw new InvalidOperationException("Adjustment direction is not applicable to this conflict neighborhood.");
        CheckBudget(remaining);
        return result;
    }

    /// <summary>Build the base neighborhood for the current adaptive search phase.</summary>
    /// <param name="snapshot">Current candidate timetable with times in seconds.</param>
    /// <param name="profile">Current resource issues used to seed feasibility search.</param>
    /// <param name="round">One-based adaptive round.</param>
    /// <param name="regime">Neighborhood expansion level supplied by the operator.</param>
    /// <param name="quality">Whether to start from the extreme trains of a feasible timetable.</param>
    /// <param name="remaining">Optional remaining construction budget in seconds.</param>
    /// <param name="legacy">Whether to retain the original alternating stop release in the quality phase.</param>
    /// <returns>The base neighborhood before operator-specific decisions are fixed.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The configured feasibility radius is invalid.</exception>
    private ResourceNeighborhood Original(MasterSolutionSnapshot snapshot,ResourceConflictProfile profile,int round,int regime,bool quality,
        Func<double>? remaining,bool legacy=false)
    {
        if(FeasibilityRadiusCap is double invalid && (!double.IsFinite(invalid) || invalid<=0))
            throw new ArgumentOutOfRangeException(nameof(FeasibilityRadiusCap));
        var core=quality?ResourceSpanNeighborhood.Select(instance,snapshot,round,regime,remaining,allowStopChanges:legacy?null:true, quotaControl: quotaControl):
            ResourceNeighborhood.Select(instance,snapshot,profile,round,regime,remaining, prioritizeOvertaking: true, quotaControl: quotaControl);
        return !quality && FeasibilityRadiusCap is double cap
            ?core with {RadiusSeconds=Math.Min(core.RadiusSeconds??cap,cap)}:core;
    }

    private ResourceNeighborhood? Focused(MasterSolutionSnapshot snapshot,ResourceConflictProfile profile,ResourceNeighborhood core,
        ResourceAlnsOperator direction,int scale,Func<double>? remaining,bool quality)
        =>direction switch
        {
            // Time-only fixes both stop pattern and macro order; background station
            // resource choices still follow the normal complete local micro model.
            ResourceAlnsOperator.Time=>core with { FreeStopIds=new HashSet<string>(StringComparer.OrdinalIgnoreCase),MaxStopChanges=0,
                FreeSectionOrderKeys=new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                FreeStationOrderKeys=new HashSet<string>(StringComparer.OrdinalIgnoreCase) },
            ResourceAlnsOperator.Quota=>Quota(snapshot,profile,core,scale,remaining),
            ResourceAlnsOperator.Boundary=>Boundary(snapshot,profile,core,scale,remaining),
            ResourceAlnsOperator.Blocking=>ResourceTightSupport.ExpandWithQuota(instance,snapshot,core,BlockingLinks,scale,remaining,
                fromReleasedEvents:!quality, quotaControl: quotaControl),
            _=>throw new ArgumentOutOfRangeException(nameof(direction))
        };

    /// <summary>Focus on stop changes using the run's expansion mode.</summary>
    /// <param name="snapshot">Current timetable times in seconds and stop pattern.</param>
    /// <param name="profile">Current station conflict profile.</param>
    /// <param name="core">Base neighborhood.</param>
    /// <param name="scale">One-based neighborhood size tier.</param>
    /// <param name="remaining">Optional remaining wall-clock seconds.</param>
    /// <returns>The stop-focused scope, or null if no decisions can be changed.</returns>
    private ResourceNeighborhood? Quota(MasterSolutionSnapshot snapshot,ResourceConflictProfile profile,ResourceNeighborhood core,int scale,Func<double>? remaining)
    {
        var support = QuotaAblation.Expand(instance, snapshot, core.SeedStopIds.Concat(core.FreeStopIds),
            new[]{32,48,64}[scale-1], new[]{12,20,32}[scale-1], remaining, quotaControl);
        CheckBudget(remaining);
        if(support.FreeStopIds.Count==0 && support.FreeTimeStopIds.Count==0)return null;
        return core with {
            FreeStopIds=support.FreeStopIds,
            QuotaExpansions = [..core.QuotaExpansions ?? [], support],
            FreeTimeStopIds=core.FreeTimeStopIds.Concat(support.FreeTimeStopIds).ToHashSet(StringComparer.OrdinalIgnoreCase),
            MaxStopChanges=Math.Max(core.MaxStopChanges,Math.Min(support.FreeStopIds.Count,new[]{4,8,12}[scale-1])) };
    }

    private ResourceNeighborhood? Boundary(MasterSolutionSnapshot snapshot,ResourceConflictProfile profile,ResourceNeighborhood core,int scale,Func<double>? remaining)
    {
        var trains=core.FreeTimeStopIds.Select(id=>instance.StopsById[id].TrainIndex).ToHashSet();
        int extra=new[]{2,4,6}[scale-1],limit=Math.Min(32,Math.Max(new[]{12,20,32}[scale-1],trains.Count)+extra);
        if(trains.Count>=limit) return null;
        var seeds=core.SeedStopIds.Select(id=>instance.StopsById[id]).ToArray();
        var crossing=profile.Conflicts.Where(c=>core.FreeTimeStopIds.Contains(c.FirstStopId)!=core.FreeTimeStopIds.Contains(c.SecondStopId))
            .Select(c=>(Id:core.FreeTimeStopIds.Contains(c.FirstStopId)?c.SecondStopId:c.FirstStopId,c.Seconds))
            .GroupBy(p=>p.Id).ToDictionary(g=>g.Key,g=>g.Sum(p=>p.Seconds),StringComparer.OrdinalIgnoreCase);
        var neighbors=instance.TrainStops.Where(s=>!trains.Contains(s.TrainIndex))
            .Select(s=>new { Stop=s,Distance=seeds.Where(seed=>seed.StationName.Equals(s.StationName,StringComparison.OrdinalIgnoreCase))
                .Select(seed=>Math.Abs(Time(seed.StopId)-Time(s.StopId))).DefaultIfEmpty(double.PositiveInfinity).Min() })
            .Where(p=>double.IsFinite(p.Distance)).OrderByDescending(p=>crossing.GetValueOrDefault(p.Stop.StopId))
            .ThenBy(p=>p.Distance).ThenBy(p=>p.Stop.StopId,StringComparer.Ordinal).ToArray();
        var times=core.FreeTimeStopIds.ToHashSet(StringComparer.OrdinalIgnoreCase);int added=0;
        foreach(var neighbor in neighbors)
        {
            CheckBudget(remaining);
            if(trains.Contains(neighbor.Stop.TrainIndex)) continue;
            if(added>=extra || trains.Count>=limit) break;
            trains.Add(neighbor.Stop.TrainIndex);added++;
            var path=instance.Trains.Single(t=>t.TrainIndex==neighbor.Stop.TrainIndex).OrderedStops;
            int at=Enumerable.Range(0,path.Count).First(i=>path[i].StopId==neighbor.Stop.StopId),depth=scale==1?2:4;
            times.UnionWith((scale==3?path:path.Skip(Math.Max(0,at-depth)).Take(Math.Min(path.Count-1,at+depth)-Math.Max(0,at-depth)+1))
                .Select(s=>s.StopId));
        }
        // Boundary support is additive; the complete original quota and timing support survives.
        return added==0?null:core with { FreeTimeStopIds=times };
        double Time(string id)=>instance.StopsById[id].StopRole==TrainStopRole.Origin?snapshot.DepartureSecondsByStopId[id]:snapshot.ArrivalSecondsByStopId[id];
    }

    private static int Regime(int scale)=>scale switch { 1=>0,2=>3,3=>8,_=>throw new ArgumentOutOfRangeException(nameof(scale)) };
    private static bool Same(ResourceNeighborhood a,ResourceNeighborhood b)=>a.RadiusSeconds==b.RadiusSeconds &&
        Math.Min(a.MaxStopChanges,a.FreeStopIds.Count)==Math.Min(b.MaxStopChanges,b.FreeStopIds.Count) &&
        a.FreeTimeStopIds.SetEquals(b.FreeTimeStopIds) && a.FreeStopIds.SetEquals(b.FreeStopIds) &&
        SameOrderScope(a.FreeSectionOrderKeys,b.FreeSectionOrderKeys) && SameOrderScope(a.FreeStationOrderKeys,b.FreeStationOrderKeys);

    private static bool SameOrderScope(IReadOnlySet<string>? a,IReadOnlySet<string>? b)=>
        a is null?b is null:b is not null && a.SetEquals(b);

    internal static int ScaleForStagnation(int stagnation)=>stagnation<3?1:stagnation<8?2:3;
    private static void CheckBudget(Func<double>? remaining)
    {
        if(remaining?.Invoke()<=0) throw new TimeoutException("ALNS neighborhood deadline expired.");
    }
}
