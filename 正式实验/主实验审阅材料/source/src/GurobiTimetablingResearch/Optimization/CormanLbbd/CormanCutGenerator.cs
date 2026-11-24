using System.Diagnostics;

namespace GurobiTimetablingResearch.Optimization;

internal static class CormanCutGenerator
{
    // Equation (11): OR over paths obtained by removing coordination arcs.
    internal static CormanTerm[] Project(CormanSmtResult result)
    {
        if(result.Status!=CormanStatus.Infeasible)throw new InvalidOperationException("Only a complete infeasibility proof may generate a cut");
        List<CormanTerm> terms=[];
        foreach(var cycle in result.Cycles)
        {
            if(!cycle.Arcs.Any(a=>a.Coordination))continue;
            Dictionary<int,int> balance=[];double length=0;
            foreach(var arc in cycle.Arcs.Where(a=>!a.Coordination))
            {
                balance[arc.From]=balance.GetValueOrDefault(arc.From)-1;
                balance[arc.To]=balance.GetValueOrDefault(arc.To)+1;length+=arc.Weight;
            }
            var from=balance.Where(p=>p.Value<0).ToArray();var to=balance.Where(p=>p.Value>0).ToArray();
            if(from.Length!=1 || to.Length!=1 || from[0].Value!=-1 || to[0].Value!=1)
                throw new InvalidOperationException("Removing coordination arcs must leave a boundary path");
            terms.Add(new(from[0].Key,to[0].Key,length));
        }
        // In an OR, a weaker relation with the same endpoints absorbs a stronger one.
        return terms.GroupBy(t=>(t.From,t.To)).Select(g=>g.MinBy(t=>t.Length)!)
            .OrderBy(t=>t.From).ThenBy(t=>t.To).ThenBy(t=>t.Length).ToArray();
    }
    internal static bool Satisfied(IEnumerable<CormanTerm> terms,IReadOnlyDictionary<int,double> times,double tolerance=1e-7)
        =>terms.Any(t=>(t.To==0?0:times[t.To])-(t.From==0?0:times[t.From])+tolerance>=t.Length);

    internal static CormanAggregationResult Aggregate(CormanProblem problem,IReadOnlyDictionary<int,CormanBoundary> original,
        Func<bool> withinBudget,int maximumCuts=0,long maximumSearchNodes=long.MaxValue,
        double certificateSeconds=double.PositiveInfinity,Action<int,int,CormanSearchStatistics?>? progress=null)
    {
        // 1 = the paper's simple SMT variant; 0 = Algorithm 2 until relaxed
        // feasibility. A positive cap >1 is an explicitly labelled deviation.
        if(maximumCuts<0)throw new ArgumentOutOfRangeException(nameof(maximumCuts));
        if(double.IsNaN(certificateSeconds) || certificateSeconds<=0)throw new ArgumentOutOfRangeException(nameof(certificateSeconds));
        Dictionary<int,CormanBoundary> relaxed=original.ToDictionary(p=>p.Key,p=>p.Value);
        List<CormanTerm[]> cuts=[];List<CormanCycle[]> proofs=[];
        CormanSmtResult? first=null;int calls=0;var statistics=CormanSearchStatistics.Empty;var lastStatus=CormanStatus.Unknown;
        CormanAggregationResult Finish(bool finished,string reason)=>new(first!,cuts,proofs,calls,finished,reason)
            {Statistics=statistics,LastStatus=lastStatus};
        while(maximumCuts==0 || cuts.Count<maximumCuts)
        {
            // The paper's two-hour cutoff is for obtaining a certificate,
            // rather than for the sum of all calls in an aggregation batch.
            calls++;progress?.Invoke(calls,cuts.Count,null);var certificateClock=Stopwatch.StartNew();
            bool Budget()=>withinBudget() && certificateClock.Elapsed.TotalSeconds<certificateSeconds;
            var result=CormanSmtSolver.Solve(problem,relaxed,Budget,maximumSearchNodes);first??=result;
            lastStatus=result.Status;
            statistics=statistics.Plus(result.Statistics);
            if(result.Status!=CormanStatus.Infeasible)
            {progress?.Invoke(calls,cuts.Count,result.Statistics);return Finish(result.Status==CormanStatus.Feasible,result.Reason);}
            var cut=Project(result);cuts.Add(cut);proofs.Add(result.Cycles.ToArray());
            progress?.Invoke(calls,cuts.Count,result.Statistics);
            if(maximumCuts==1)return Finish(true,"SimpleSingleCut");
            // No coordination-dependent cycle: the subproblem is infeasible
            // independently of boundary times (the guarded business adapter may
            // still allow the master to change its stop pattern).
            var cycle=result.Cycles.Where(c=>c.Arcs.Any(a=>a.Coordination)).MinBy(c=>c.Length);
            if(cycle is null)return Finish(true,"NoTimeEscape");
            var arc=cycle.Arcs.Where(a=>a.Coordination).OrderBy(a=>a.From==0?0:1).ThenBy(a=>a.Id).First();
            int node=arc.From==0?arc.To:arc.From;var bound=relaxed[node];
            relaxed[node]=arc.From==0?bound with {Lower=bound.Lower-cycle.Length}:bound with {Upper=bound.Upper+cycle.Length};
        }
        return Finish(false,"AggregationCutLimit");
    }
}
