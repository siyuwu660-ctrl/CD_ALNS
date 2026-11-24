using System.Diagnostics;
using Gurobi;

namespace GurobiTimetablingResearch.Optimization;

internal sealed record RetimingSourceFeasibilityCheck(
    string Status,int? NativeStatus,bool Feasible,bool Inconsistent,double Seconds,
    int FixedPhysicalValueCount,string? Detail=null);

/// <summary>Checks membership of the physical source in this exact fixed structure; never a global infeasibility proof.</summary>
internal static class RetimingSourceFeasibilityChecker
{
    internal static RetimingSourceFeasibilityCheck Check(BasicMilpModel source,MasterSolutionSnapshot reference,
        double seconds,Func<double> remaining,string? logFile=null)
    {
        var clock=Stopwatch.StartNew();
        double Left()=>Math.Min(seconds-clock.Elapsed.TotalSeconds,remaining());
        int physicalCount=0;
        RetimingSourceFeasibilityCheck Result(string status,int? native=null,bool feasible=false,bool inconsistent=false,string? detail=null)
            =>new(status,native,feasible,inconsistent,clock.Elapsed.TotalSeconds,physicalCount,detail);
        if(Left()<=0)return Result("UnknownRetimingSourceBudget");
        var families=new (string Name,IReadOnlyDictionary<string,GRBVar> Variables,IReadOnlyDictionary<string,double>? Values,bool Physical)[] {
            ("arrival",source.ArrivalVars,reference.ArrivalSecondsByStopId,true),
            ("departure",source.DepartureVars,reference.DepartureSecondsByStopId,true),
            ("supplement",source.RunningTimeSupplementVars,reference.RunningTimeSupplementSecondsByKey,true),
            ("stop",source.StopVars,reference.StopByStopId.ToDictionary(p=>p.Key,p=>(double)p.Value),false),
            ("section_order",source.SectionPrecedenceVars,reference.SectionPrecedenceByKey.ToDictionary(p=>p.Key,p=>(double)p.Value),false),
            ("station_order",source.StationEventPrecedenceVars,reference.StationEventPrecedenceByKey.ToDictionary(p=>p.Key,p=>(double)p.Value),false)
        };
        foreach(var family in families)
        {
            if(family.Values is null || family.Variables.Count!=family.Values.Count || family.Variables.Keys.Any(key=>!family.Values.ContainsKey(key)))
                return Result("RetimingSourceInconsistent",inconsistent:true,detail:"Incomplete physical source mapping: "+family.Name);
            if(family.Values.Values.Any(value=>!double.IsFinite(value)))
                return Result("RetimingSourceInconsistent",inconsistent:true,detail:"Nonfinite physical source value: "+family.Name);
        }
        var model=source.Model;model.Update();
        int originalSense=model.ModelSense,originalObjectiveNumber=model.Parameters.ObjNumber;
        var objectives=new List<(GRBLinExpr Expression,string Name,int Priority,double Weight,double AbsoluteTolerance,double RelativeTolerance)>();
        for(int index=0;index<model.NumObj;index++)
        {
            model.Set(GRB.IntParam.ObjNumber,index);
            objectives.Add((model.GetObjective(index),model.ObjNName,model.ObjNPriority,model.ObjNWeight,model.ObjNAbsTol,model.ObjNRelTol));
        }
        List<GRBConstr> fixes=[];
        try
        {
            foreach(var family in families)
            foreach(var variable in family.Variables)
            {
                fixes.Add(model.AddConstr(variable.Value,GRB.EQUAL,family.Values![variable.Key],
                    "retiming_source_"+family.Name+"_"+variable.Value.VarName));
                if(family.Physical)physicalCount++;
            }
            model.Set(GRB.IntAttr.NumObj,0);
            model.SetObjective(new GRBLinExpr(),GRB.MINIMIZE);
            model.Update();
            double allowed=Left();
            if(allowed<=0)return Result("UnknownRetimingSourceBudget");
            if(logFile is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(logFile))!);
                model.Set(GRB.StringParam.LogFile,logFile);
            }
            model.Set(GRB.IntParam.DualReductions,0);
            // The full carrier and saved-resource readback accept physical floating values
            // under this same contract. A tighter source-only tolerance excludes valid anchors.
            model.Set(GRB.DoubleParam.FeasibilityTol,SolverNumericalValueNormalizer.BoundTolerance);
            model.Set(GRB.DoubleParam.TimeLimit,allowed);
            source.Optimize();
            int status=model.Status;
            if(status==GRB.Status.INFEASIBLE)
                return Result("RetimingSourceInconsistent",status,inconsistent:true,detail:"The physical source violates the retained fixed structure or its certificate rows.");
            if(model.SolCount>0)
                return Result("RetimingSourceFeasible",status,feasible:true);
            return Result(Left()<=0 || status==GRB.Status.TIME_LIMIT?"UnknownRetimingSourceBudget":"UnknownRetimingSourceNoIncumbent",status);
        }
        finally
        {
            foreach(var row in fixes)model.Remove(row);
            model.Set(GRB.IntAttr.NumObj,0);
            for(int index=0;index<objectives.Count;index++)
            {
                var objective=objectives[index];
                model.SetObjectiveN(objective.Expression,index,objective.Priority,objective.Weight,
                    objective.AbsoluteTolerance,objective.RelativeTolerance,objective.Name);
            }
            model.ModelSense=originalSense;
            model.Update();
            model.Set(GRB.IntParam.ObjNumber,originalObjectiveNumber);
        }
    }
}
