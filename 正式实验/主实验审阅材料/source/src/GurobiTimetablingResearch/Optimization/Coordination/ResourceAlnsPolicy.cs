namespace GurobiTimetablingResearch.Optimization;

internal enum ResourceAlnsOperator { Legacy,Joint,Time,Quota,Boundary,Blocking }
internal sealed record ResourceAlnsAction(ResourceAlnsOperator Operator,int Scale)
{
    internal string Id=>$"{Operator}_{Scale}";
}
internal sealed record ResourceAlnsSettings(double Exploration=.15,double Smoothing=.25,int SegmentLength=10,
    double MinimumWeight=.05,double ReferenceSeconds=60,double GainReward=1,double FirstFeasibleReward=8,
    double BestReward=4,double DiversityReward=1,int FallbackAfter=3,int FallbackRounds=1,int AdaptiveRounds=3);

/// <summary>Each phase learns a joint distribution over adjustment direction and scale.</summary>
internal sealed class ResourceAlnsPolicy
{
    private readonly Random random;
    private readonly bool improvedQuality;
    private readonly Dictionary<(bool Phase,string Action),double> weights=[];
    private readonly Dictionary<(bool Phase,string Action),(double Reward,double Seconds,int Calls)> segment=[];
    private readonly Dictionary<bool,int> calls=[];
    internal ResourceAlnsSettings Settings { get; }
    internal ResourceAlnsPolicy(int seed,ResourceAlnsSettings? settings=null,bool improvedQuality=false)
    {
        Settings=settings??new();random=new(seed);this.improvedQuality=improvedQuality;
        if(seed<0 || !double.IsFinite(Settings.Exploration) || Settings.Exploration<=0 || Settings.Exploration>=1 ||
            !double.IsFinite(Settings.Smoothing) || Settings.Smoothing<=0 || Settings.Smoothing>1 || Settings.SegmentLength<1 ||
            !double.IsFinite(Settings.MinimumWeight) || Settings.MinimumWeight<=0 ||
            !double.IsFinite(Settings.ReferenceSeconds) || Settings.ReferenceSeconds<=0 || Settings.FallbackAfter<1 ||
            Settings.FallbackRounds<1 || Settings.AdaptiveRounds<1 ||
            new[]{Settings.GainReward,Settings.FirstFeasibleReward,Settings.BestReward,Settings.DiversityReward}.Any(v=>!double.IsFinite(v)||v<0))
            throw new ArgumentException("Invalid ALNS settings.");
    }
    internal IReadOnlyDictionary<string,double> Probabilities(IReadOnlyList<ResourceAlnsAction> actions,bool quality)
    {
        if(actions.Count==0 || actions.Select(a=>a.Id).Distinct().Count()!=actions.Count) throw new ArgumentException("Unique applicable actions required.");
        foreach(var a in actions) weights.TryAdd((quality,a.Id),1);
        double total=actions.Sum(a=>weights[(quality,a.Id)]);
        return actions.ToDictionary(a=>a.Id,a=>(1-Settings.Exploration)*weights[(quality,a.Id)]/total+Settings.Exploration/actions.Count);
    }
    internal ResourceAlnsAction Select(IReadOnlyList<ResourceAlnsAction> actions,bool quality)
    {
        var p=Probabilities(actions,quality);double draw=random.NextDouble();
        foreach(var a in actions) { draw-=p[a.Id];if(draw<=0) return a; }
        return actions[^1];
    }
    internal void Record(ResourceAlnsAction action,bool quality,double reward,double seconds)
    {
        if(!double.IsFinite(reward)||reward<0||!double.IsFinite(seconds)||seconds<=0) throw new ArgumentException("Invalid ALNS observation.");
        // Feasibility fallback is the same joint regime. Quality fallback retains the
        // legacy alternating support and is recorded separately, not credited to a different action.
        if(action.Operator==ResourceAlnsOperator.Legacy && !quality) action=action with { Operator=ResourceAlnsOperator.Joint };
        var key=(quality,action.Id);weights.TryAdd(key,1);
        var old=segment.GetValueOrDefault(key);segment[key]=(old.Reward+reward,old.Seconds+seconds,old.Calls+1);
        calls[quality]=calls.GetValueOrDefault(quality)+1;
        if(calls[quality]%Settings.SegmentLength!=0) return;
        foreach(var k in segment.Keys.Where(k=>k.Phase==quality).ToArray())
        {
            var value=segment[k];double rate=quality && improvedQuality
                ?value.Reward/value.Calls/Math.Clamp(Math.Sqrt(value.Seconds/value.Calls/Settings.ReferenceSeconds),.5,2)
                :value.Reward/(value.Seconds/Settings.ReferenceSeconds);
            weights[k]=Math.Max(Settings.MinimumWeight,(1-Settings.Smoothing)*weights[k]+Settings.Smoothing*rate);
            segment.Remove(k);
        }
    }
    internal ResourceAlnsAction SelectForRound(IReadOnlyList<ResourceAlnsAction> actions,bool quality,int round,int stagnation)
    {
        // Preserve the known first move and bounded legacy recovery on stalls.
        // Resume weighted exploration even if recovery fails; keep learned weights.
        if(UsesLegacy(round,stagnation))
            return new(ResourceAlnsOperator.Legacy,ResourceAlnsNeighborhood.ScaleForStagnation(stagnation));
        return Select(actions,quality);
    }
    internal bool UsesLegacy(int round,int stagnation)
    {
        if(round<1 || stagnation<0) throw new ArgumentOutOfRangeException(nameof(round));
        if(round==1) return true;
        if(stagnation<Settings.FallbackAfter) return false;
        long cycle=(long)Settings.FallbackRounds+Settings.AdaptiveRounds;
        return (stagnation-Settings.FallbackAfter)%cycle<Settings.FallbackRounds;
    }
    internal object Snapshot()=>weights.OrderBy(p=>p.Key.Phase).ThenBy(p=>p.Key.Action,StringComparer.Ordinal)
        .Select(p=>new { quality=p.Key.Phase,action=p.Key.Action,weight=p.Value,pendingReward=segment.GetValueOrDefault(p.Key).Reward,pendingSeconds=segment.GetValueOrDefault(p.Key).Seconds,pendingCalls=segment.GetValueOrDefault(p.Key).Calls }).ToArray();
    internal static double Gain(IReadOnlyList<double> before,IReadOnlyList<double> after)
    {
        if(before.Count!=after.Count || before.Concat(after).Any(v=>!double.IsFinite(v)||v<0)) throw new ArgumentException("Invalid objective vector.");
        for(int i=0;i<before.Count;i++)
        {
            if(Math.Abs(before[i]-after[i])<=1e-6) continue;
            return after[i]<before[i]?Math.Min(1,(before[i]-after[i])/Math.Max(1,Math.Abs(before[i]))):0;
        }
        return 0;
    }
    internal double Reward(IReadOnlyList<double> before,IReadOnlyList<double> after,bool valid,bool first,bool best,bool diversity)
        =>!valid?0:Settings.GainReward*Gain(before,after)+(first?Settings.FirstFeasibleReward:best?Settings.BestReward:0)+
            (diversity?Settings.DiversityReward:0);
}

