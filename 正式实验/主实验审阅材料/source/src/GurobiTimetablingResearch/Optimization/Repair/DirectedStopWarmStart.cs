using Gurobi;

namespace GurobiTimetablingResearch.Optimization;

internal sealed record DirectedWarmStartAudit(bool Accepted,string Status,int NativeStatus,double Seconds,int FixedDiscreteStarts,
    int RemainingFreeIntegers,double? MaxRowViolation=null,double? MaxBoundViolation=null,
    string Scope="RestrictedRepairStart_NotFullMacroOrJointCertificate");

internal static class DirectedStopWarmStart
{
    internal static DirectedWarmStartAudit Prepare(GRBModel target,double seconds,string logPath)
    {
        var clock=System.Diagnostics.Stopwatch.StartNew();target.Update();
        var variables=target.GetVars();
        var skeleton=variables.Where(v=>v.VType!=GRB.CONTINUOUS && v.Start!=GRB.UNDEFINED &&
            double.IsFinite(v.Start) && Math.Abs(v.Start-Math.Round(v.Start))<=1e-8 && v.Start>=v.LB && v.Start<=v.UB)
            .ToDictionary(v=>v.VarName,v=>Math.Round(v.Start),StringComparer.Ordinal);
        // In particular, do not submit zero passing dwell after changing to stop.
        // A failed preparation leaves a partial discrete start only, never old times.
        foreach(var v in variables) v.Start=skeleton.GetValueOrDefault(v.VarName,GRB.UNDEFINED);
        if(seconds<=0) return new(false,"UnknownPreparationBudget",0,clock.Elapsed.TotalSeconds,0,variables.Count(v=>v.VType!=GRB.CONTINUOUS));
        target.Update();using var preparation=new GRBModel(target);
        int fixedCount=0;
        foreach(var v in preparation.GetVars())
            if(v.LB!=v.UB && skeleton.TryGetValue(v.VarName,out double value)) { v.LB=v.UB=value;fixedCount++; }
        GRBLinExpr movement=new();
        for(int i=0;i<preparation.NumObj;i++)
        {
            preparation.Set(GRB.IntParam.ObjNumber,i);
            if(preparation.ObjNName=="repair_time_movement") { movement=preparation.GetObjective(i);break; }
        }
        preparation.NumObj=0;preparation.SetObjective(movement,GRB.MINIMIZE);preparation.Update();
        int free=preparation.GetVars().Count(v=>v.VType!=GRB.CONTINUOUS && v.LB!=v.UB);
        double remaining=seconds-clock.Elapsed.TotalSeconds;
        if(remaining<=0) return new(false,"UnknownPreparationBudget",0,clock.Elapsed.TotalSeconds,fixedCount,free);
        DirectedFeasibilityRepair.ConfigurePolish(preparation,remaining);
        preparation.Set(GRB.IntParam.SolutionLimit,1);preparation.Set(GRB.IntParam.MIPFocus,1);
        preparation.Set(GRB.IntParam.OutputFlag,1);preparation.Set(GRB.IntParam.LogToConsole,0);preparation.Set(GRB.StringParam.LogFile,logPath);
        preparation.Optimize();int native=preparation.Status;
        if(preparation.SolCount==0) return new(false,native==GRB.Status.INFEASIBLE?"UnknownFixedSkeletonInfeasible":"UnknownNoPreparedStart",
            native,clock.Elapsed.TotalSeconds,fixedCount,free);
        var values=preparation.GetVars().ToDictionary(v=>v.VarName,v=>v.VType==GRB.CONTINUOUS?v.X:Math.Round(v.X),StringComparer.Ordinal);
        if(values.Values.Any(v=>!double.IsFinite(v))) return new(false,"UnknownNonfinitePreparedStart",native,clock.Elapsed.TotalSeconds,fixedCount,free);
        double bound=variables.Select(v=>Math.Max(0,Math.Max(v.LB-values[v.VarName],values[v.VarName]-v.UB))).DefaultIfEmpty().Max();
        double rowViolation=0;
        foreach(var row in target.GetConstrs())
        {
            var expression=target.GetRow(row);double lhs=expression.Constant;
            for(int i=0;i<expression.Size;i++) lhs+=expression.GetCoeff(i)*values[expression.GetVar(i).VarName];
            rowViolation=Math.Max(rowViolation,row.Sense==GRB.GREATER_EQUAL?row.RHS-lhs:
                row.Sense==GRB.LESS_EQUAL?lhs-row.RHS:Math.Abs(lhs-row.RHS));
        }
        bool accepted=bound<=1e-6 && rowViolation<=1e-6;
        if(accepted) foreach(var v in variables) v.Start=values[v.VarName];
        return new(accepted,accepted?"ValidatedRestrictedStart":"UnknownPreparedStartResidual",native,clock.Elapsed.TotalSeconds,fixedCount,free,rowViolation,bound);
    }
}
