namespace GurobiTimetablingResearch.Optimization;

internal sealed class ResourcePhaseBudget(double total,double reserve,Func<double> elapsed)
{
    internal double Remaining()=>Math.Max(0,total-elapsed());
    internal double SearchRemaining()=>Math.Max(0,total-reserve-elapsed());
}
