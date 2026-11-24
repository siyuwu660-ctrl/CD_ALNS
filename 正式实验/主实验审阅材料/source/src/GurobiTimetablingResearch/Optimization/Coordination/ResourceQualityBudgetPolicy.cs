namespace GurobiTimetablingResearch.Optimization;

internal sealed record ResourceQualityBudgetChoice(int TierSeconds,double Seconds);
/// <summary>Per-direction/scale effort learning; never a global bound or feasibility rule.</summary>
internal sealed class ResourceQualityBudgetPolicy
{
    private readonly Random random;
    private readonly Dictionary<(string Action,int Tier),double> weights=[];
    private static readonly int[] tiers=[60,180,600];
    internal ResourceQualityBudgetPolicy(int seed)
    {
        if(seed<0)throw new ArgumentOutOfRangeException(nameof(seed));
        random=new(seed^0x51A37); // independent of the feasibility/direction random stream
    }
    internal IReadOnlyDictionary<int,double> Probabilities(string action,double maximumSeconds)
    {
        if(string.IsNullOrWhiteSpace(action)||!double.IsFinite(maximumSeconds)||maximumSeconds<=0)
            throw new ArgumentException("Positive remaining repair budget and action required.");
        List<int> available=[];
        foreach(int tier in tiers)
        {
            available.Add(tier);
            if(tier>=maximumSeconds)break; // only one truncated final tier
        }
        foreach(int tier in available)weights.TryAdd((action,tier),1);
        double sum=available.Sum(t=>weights[(action,t)]);
        return available.ToDictionary(t=>t,t=>.85*weights[(action,t)]/sum+.15/available.Count);
    }
    internal ResourceQualityBudgetChoice Select(string action,double maximumSeconds)
    {
        var probabilities=Probabilities(action,maximumSeconds);double draw=random.NextDouble();
        foreach(var p in probabilities)
        {
            draw-=p.Value;
            if(draw<=0)return new(p.Key,Math.Min(p.Key,maximumSeconds));
        }
        int last=probabilities.Keys.Last();return new(last,Math.Min(last,maximumSeconds));
    }
    internal void Record(string action,int tier,double verifiedReward,double elapsedSeconds)
    {
        if(string.IsNullOrWhiteSpace(action)||!tiers.Contains(tier)||!double.IsFinite(verifiedReward)||verifiedReward<0||
            !double.IsFinite(elapsedSeconds)||elapsedSeconds<=0)throw new ArgumentException("Invalid effort observation.");
        var key=(action,tier);
        double rate=verifiedReward/Math.Clamp(Math.Sqrt(elapsedSeconds/60),.5,2);
        weights[key]=Math.Max(.05,.75*weights.GetValueOrDefault(key,1)+.25*rate);
    }
    internal object Snapshot()=>weights.OrderBy(p=>p.Key.Action,StringComparer.Ordinal).ThenBy(p=>p.Key.Tier)
        .Select(p=>new {action=p.Key.Action,tierSeconds=p.Key.Tier,weight=p.Value}).ToArray();
}
