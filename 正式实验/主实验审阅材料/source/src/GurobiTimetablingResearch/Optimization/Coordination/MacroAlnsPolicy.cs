using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

internal enum MacroAlnsDirection { TimeOnly, OrderTime, WaitingSupportTimeOrder, StopQuotaTimeOrder, LegacyFallback }
internal sealed record MacroAlnsAction(MacroAlnsDirection Direction,int Scale,bool LegacyFallback=false)
{
    public int PairLimit=>new[]{160,320,640}[Scale];
    public double RadiusSeconds=>new[]{600.0,1200,2400}[Scale];
    public string Id=>$"{Direction}_{Scale}";
}
internal sealed record MacroAlnsSettings(double Exploration=.15,double Smoothing=.25,int UpdatePeriod=10,
    double WeightFloor=.05,double FirstFeasibleReward=8,double BestReward=4,double ImprovementReward=1);
internal sealed record MacroAlnsArm(string Id,double Weight=1,double Reward=0,int Calls=0);
internal sealed record MacroAlnsState(MacroAlnsArm[] Arms,int Selections=0,int Observations=0,bool FeasibleSeen=false,double? BestScore=null);
/// <summary>Macro-only joint direction/scale learning; never shares weights with resource ALNS.</summary>
internal sealed class MacroAlnsPolicy
{
    internal MacroAlnsSettings Settings { get; }
    private readonly Dictionary<string,MacroAlnsArm> arms;
    private readonly int seed;
    private int selections,observations;
    private bool feasibleSeen;
    private double? bestScore;
    internal MacroAlnsPolicy(int seed,MacroAlnsState? state=null,MacroAlnsSettings? settings=null)
    {
        this.seed=seed;Settings=settings??new();
        if(seed<0 || Settings.Exploration is <=0 or >=1 || Settings.Smoothing is <=0 or >1 ||
            Settings.UpdatePeriod<1 || Settings.WeightFloor<=0 ||
            new[]{Settings.Exploration,Settings.Smoothing,Settings.WeightFloor,Settings.FirstFeasibleReward,Settings.BestReward,Settings.ImprovementReward}.Any(x=>!double.IsFinite(x)||x<0))
            throw new ArgumentException("Invalid macro ALNS settings.");
        arms=(state?.Arms??[]).ToDictionary(a=>a.Id,StringComparer.Ordinal);
        if(arms.Values.Any(a=>!double.IsFinite(a.Weight)||a.Weight<Settings.WeightFloor||!double.IsFinite(a.Reward)||a.Reward<0||a.Calls<0) ||
            state is {Selections:<0} || state is {Observations:<0} || state?.BestScore is double b && (!double.IsFinite(b)||b<0))
            throw new ArgumentException("Invalid macro ALNS continuation.");
        selections=state?.Selections??0;observations=state?.Observations??0;feasibleSeen=state?.FeasibleSeen??false;bestScore=state?.BestScore;
    }
    internal MacroAlnsAction Select(int stagnation,bool waitingApplicable,MacroAlnsAction? requiredAction=null)
    {
        // One bounded legacy direction after three stalls, then three adaptive rounds.
        bool fallback=stagnation>=3 && (stagnation-3)%4==0;
        var choices=Enum.GetValues<MacroAlnsDirection>().Where(d=>d!=MacroAlnsDirection.LegacyFallback && (waitingApplicable || d!=MacroAlnsDirection.WaitingSupportTimeOrder))
            .SelectMany(d=>Enumerable.Range(0,3).Select(s=>new MacroAlnsAction(d,s))).ToArray();
        foreach(var a in choices) arms.TryAdd(a.Id,new(a.Id));
        int drawIndex=selections++;
        if(requiredAction is not null)
        {
            if(!choices.Any(a=>a.Id==requiredAction.Id)) throw new ArgumentException("Inapplicable required macro action.");
            return requiredAction;
        }
        if(fallback) return new(MacroAlnsDirection.LegacyFallback,Math.Min(2,stagnation/3),true);
        double total=choices.Sum(a=>arms[a.Id].Weight);
        double draw=new Random((int)(((long)seed+104729L*drawIndex)%int.MaxValue)).NextDouble();
        foreach(var a in choices)
        {
            draw-=(1-Settings.Exploration)*arms[a.Id].Weight/total+Settings.Exploration/choices.Length;
            if(draw<=0) return a;
        }
        return choices[^1];
    }
    internal double Record(MacroAlnsAction action,bool hasIncumbent,double before,double? after,bool verifiedMacro)
    {
        double reward=0;
        if(hasIncumbent && after.HasValue)
        {
            bool newBest=after.Value<(bestScore??before)-TimetableTimePolicy.NumericToleranceSeconds;
            reward=verifiedMacro && !feasibleSeen?Settings.FirstFeasibleReward:newBest?Settings.BestReward:
                after.Value<before-TimetableTimePolicy.NumericToleranceSeconds?Settings.ImprovementReward:0;
            bestScore=Math.Min(bestScore??before,after.Value);feasibleSeen|=verifiedMacro;
        }
        // Fallback outcomes are labelled and deliberately excluded from adaptive credit.
        if(!action.LegacyFallback)
        {
            var old=arms[action.Id];arms[action.Id]=old with {Reward=old.Reward+reward,Calls=old.Calls+1};
            observations++;
            if(observations%Settings.UpdatePeriod==0)
                foreach(var a in arms.Values.Where(a=>a.Calls>0).ToArray())
                    arms[a.Id]=a with {Weight=Math.Max(Settings.WeightFloor,(1-Settings.Smoothing)*a.Weight+Settings.Smoothing*a.Reward/a.Calls),Reward=0,Calls=0};
        }
        return reward;
    }
    // Unchanged-input verification and external recovery update the baseline without local-arm credit.
    internal void ObserveExternal(double? score,bool verifiedMacro)
    {
        if(score is double value) bestScore=Math.Min(bestScore??value,value);
        feasibleSeen|=verifiedMacro;
    }
    internal MacroAlnsState Snapshot()=>new(arms.Values.OrderBy(a=>a.Id,StringComparer.Ordinal).ToArray(),selections,observations,feasibleSeen,bestScore);
}
