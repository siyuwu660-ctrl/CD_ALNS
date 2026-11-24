using Gurobi;

namespace GurobiTimetablingResearch.Optimization;

internal static class DirectedRestrictedIis
{
    internal static object Capture(GRBModel compact,GRBModel source,double seconds,object restrictions)
    {
        var clock=System.Diagnostics.Stopwatch.StartNew();
        string status="NotComputed";string? error=null;bool? minimal=null;
        string[] rows=[];List<object> bounds=[];Dictionary<string,object> substituted=[];
        int native=compact.Status;
        if(native==GRB.Status.INFEASIBLE && seconds>0)
        {
            try
            {
                compact.SetCallback(null);
                compact.Set(GRB.DoubleParam.TimeLimit,seconds);compact.Set(GRB.IntParam.OutputFlag,0);
                compact.ComputeIIS();minimal=compact.Get(GRB.IntAttr.IISMinimal)==1;
                rows=compact.GetConstrs().Where(r=>r.IISConstr!=0).Select(r=>r.ConstrName).ToArray();
                foreach(var v in compact.GetVars().Where(v=>v.IISLB!=0 || v.IISUB!=0))
                    bounds.Add(new { name=v.VarName,lower=v.LB,upper=v.UB,lowerInIis=v.IISLB!=0,upperInIis=v.IISUB!=0 });
                foreach(string name in rows)
                {
                    var row=source.GetConstrByName(name);if(row is null) continue;
                    var expression=source.GetRow(row);
                    for(int i=0;i<expression.Size;i++)
                    {
                        var v=expression.GetVar(i);
                        if(v.LB==v.UB) substituted.TryAdd(v.VarName,new { name=v.VarName,value=v.LB });
                    }
                }
                status=minimal==true?"MinimalRestrictedIis":"NonminimalRestrictedSubsystem";
            }
            catch(GRBException ex) { status="UnknownIisUnavailable";error=ex.Message; }
        }
        return new { status,nativeStatus=native,budgetSeconds=seconds,seconds=clock.Elapsed.TotalSeconds,minimal,rows,bounds,
            substitutedFixedDependencies=substituted.Values,restrictions,error,globalInfeasibilityProof=false,
            scope="RestrictedCompiledRepairOnly",
            note="Fixed dependencies include search fixations and propagated bounds; this is not an IIS of the unrestricted original problem." };
    }
}
