using Gurobi;

namespace GurobiTimetablingResearch.Optimization;

public sealed record PartialDiscreteCompileAudit(int SourceVariables,int CompactVariables,int EliminatedFixedVariables,
    int SourceRows,int CompactRows,int SourceFreeIntegers,int CompactFreeIntegers);

/// <summary>Exact substitution of bound-fixed variables. Unknown integer variables retain their type.</summary>
internal sealed class PartialDiscreteModelCompiler : IDisposable
{
    private readonly GRBEnv _environment;
    private readonly Dictionary<string,double> _fixed;
    private readonly Dictionary<string,GRBVar> _variables;
    internal GRBModel Model { get; }
    internal PartialDiscreteCompileAudit Audit { get; }

    internal PartialDiscreteModelCompiler(GRBModel source)
    {
        source.Update();
        if(source.NumSOS!=0 || source.NumGenConstrs!=0 || source.NumQConstrs!=0)
            throw new InvalidOperationException("Partial compiler only supports linear MILP source models.");
        GRBVar[] all=source.GetVars();
        if(all.Any(v=>v.VType is GRB.SEMICONT or GRB.SEMIINT)) throw new InvalidOperationException("Unsupported semi-continuous domain.");
        if(all.Any(v=>v.LB==v.UB && v.VType!=GRB.CONTINUOUS && v.LB!=Math.Round(v.LB)))
            throw new InvalidOperationException("Cannot eliminate a fixed discrete variable at a nonintegral value.");
        _fixed=all.Where(v=>v.LB==v.UB).ToDictionary(v=>v.VarName,v=>v.LB,StringComparer.Ordinal);
        _environment=new GRBEnv(true); _environment.Set(GRB.IntParam.OutputFlag,0); _environment.Start();
        Model=new(_environment) { ModelName="exact_partial::"+source.ModelName,ModelSense=source.ModelSense };
        _variables=all.Where(v=>!_fixed.ContainsKey(v.VarName)).ToDictionary(v=>v.VarName,
            v=>Model.AddVar(v.LB,v.UB,0,v.VType,v.VarName),StringComparer.Ordinal);
        foreach(GRBConstr row in source.GetConstrs())
        {
            GRBLinExpr reduced=Substitute(source.GetRow(row));
            bool satisfiedConstant=reduced.Size==0 && (row.Sense==GRB.LESS_EQUAL ? reduced.Constant<=row.RHS :
                row.Sense==GRB.GREATER_EQUAL ? reduced.Constant>=row.RHS : reduced.Constant==row.RHS);
            if(satisfiedConstant) continue;
            Model.AddConstr(reduced,row.Sense,row.RHS,row.ConstrName);
        }
        int originalObjectiveNumber=source.Parameters.ObjNumber;
        if(source.NumObj==1)
            Model.SetObjective(Substitute(source.GetObjective() as GRBLinExpr ??
                throw new InvalidOperationException("Partial compiler requires a linear objective.")),source.ModelSense);
        else for(int i=0;i<source.NumObj;i++)
        {
            source.Set(GRB.IntParam.ObjNumber,i);
            Model.SetObjectiveN(Substitute(source.GetObjective(i)),i,source.ObjNPriority,
                source.ObjNWeight,source.ObjNAbsTol,source.ObjNRelTol,source.ObjNName);
        }
        source.Set(GRB.IntParam.ObjNumber,originalObjectiveNumber);
        Model.Update();
        foreach(GRBVar variable in all.Where(v=>_variables.ContainsKey(v.VarName)))
        {
            double start=variable.Start;
            if(start!=GRB.UNDEFINED) _variables[variable.VarName].Start=start;
        }
        int sourceIntegers=all.Count(v=>v.LB<v.UB && v.VType!=GRB.CONTINUOUS);
        Audit=new(all.Length,_variables.Count,_fixed.Count,source.NumConstrs,Model.NumConstrs,
            sourceIntegers,Model.GetVars().Count(v=>v.LB<v.UB && v.VType!=GRB.CONTINUOUS));
    }

    private GRBLinExpr Substitute(GRBLinExpr expression)
    {
        GRBLinExpr result=new(); result.AddConstant(expression.Constant);
        for(int j=0;j<expression.Size;j++)
        {
            string name=expression.GetVar(j).VarName; double c=expression.GetCoeff(j);
            if(_fixed.TryGetValue(name,out double value)) result.AddConstant(c*value);
            else result.AddTerm(c,_variables[name]);
        }
        return result;
    }

    internal void FixSourceToIncumbent(GRBModel source)
    {
        if(Model.SolCount==0) throw new InvalidOperationException("No compact incumbent to validate.");
        foreach(GRBVar variable in source.GetVars())
        {
            double value=_fixed.TryGetValue(variable.VarName,out double fixedValue)?fixedValue:_variables[variable.VarName].X;
            if(variable.VType!=GRB.CONTINUOUS) value=Math.Round(value);
            // Preserve the source domain. Candidate-only equality rows cannot overwrite an original bound.
            source.AddConstr(variable,GRB.EQUAL,value,"compact_candidate::"+variable.VarName);
        }
        source.Update();
    }
    public void Dispose() { Model.Dispose(); _environment.Dispose(); }
}
