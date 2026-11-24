namespace GurobiTimetablingResearch.Optimization;

internal sealed class ResourceSpanBoundBudget
{
    private readonly double passSeconds,totalSeconds;
    private readonly int interval;
    private int lastRound;
    internal ResourceSpanBoundBudget(double passSeconds,double totalSeconds,int interval)
    {
        if(!double.IsFinite(passSeconds) || !double.IsFinite(totalSeconds) || passSeconds<=0 || totalSeconds<0 || interval<1)
            throw new ArgumentException("Invalid lower-bound budget.");
        this.passSeconds=passSeconds;this.totalSeconds=totalSeconds;this.interval=interval;
    }
    internal double UsedSeconds { get; private set; }
    internal double Allowance(bool jointFeasible,int qualityRound,double searchRemaining)
    {
        if(!jointFeasible || qualityRound<1 || searchRemaining<=30 || lastRound>0 && qualityRound-lastRound<interval) return 0;
        return Math.Max(0,Math.Min(passSeconds,Math.Min(totalSeconds-UsedSeconds,searchRemaining-30)));
    }
    internal void Record(int qualityRound,double elapsed)
    {
        if(qualityRound<1 || !double.IsFinite(elapsed) || elapsed<0) throw new ArgumentException("Invalid bound effort record.");
        lastRound=qualityRound;UsedSeconds+=elapsed;
    }
}
