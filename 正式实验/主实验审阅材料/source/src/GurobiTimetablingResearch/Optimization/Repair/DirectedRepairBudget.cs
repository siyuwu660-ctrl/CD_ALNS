namespace GurobiTimetablingResearch.Optimization;

internal static class DirectedRepairBudget
{
    internal static bool BuildExhausted(string status)=>status is "UnknownBudgetDuringBuild" or "UnknownBudgetDuringCompile";
    internal static bool Blocked(DirectedMacroSearchState state)=>state.ConsecutiveBuildBudgetFailures>=3;
    internal static double Iteration(double remaining,double configured,int timeoutStreak,double lastBuild,bool stopTrial)
    {
        double solveSlice=stopTrial?Math.Min(120,configured):configured;
        double retry=solveSlice*Math.Pow(2,Math.Min(timeoutStreak,2));
        // Reserve measured preparation cost plus a useful optimization slice.
        double requested=Math.Max(retry,lastBuild>0?lastBuild+solveSlice:solveSlice);
        return Math.Max(0,Math.Min(remaining,requested));
    }
    internal static double Batch(double baseline,double cap,double remaining,double iteration,double lastBuild,int buildFailures)
    {
        // A batch includes diagnostics, plan/state I/O and its own reserve.
        double target=lastBuild>0?(lastBuild+iteration*Math.Pow(2,Math.Min(buildFailures,2))+45)/.9:baseline;
        return Math.Max(0,Math.Min(remaining,Math.Min(cap,Math.Max(baseline,target))));
    }
}
