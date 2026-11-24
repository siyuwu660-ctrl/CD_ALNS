using Gurobi;

namespace GurobiTimetablingResearch.Optimization;

internal sealed class ResourceLinearTemplate
{
    private sealed record Variable(string Name,char Type,double Lower,double Upper);
    private sealed record Expression(int[] Indices,double[] Coefficients,double Constant);
    private sealed record Row(string Name,char Sense,double Rhs,Expression Expression);
    private sealed record Objective(string Name,int Priority,double Weight,double AbsTol,double RelTol,Expression Expression);
    private readonly Variable[] variables;
    private readonly Row[] rows;
    private readonly Objective[] objectives;
    private readonly int sense;
    internal int SourceRows=>rows.Length;
    internal int SourceVariables=>variables.Length;

    internal ResourceLinearTemplate(GRBModel source,Func<double>? remaining=null)
    {
        source.Update();
        if(source.NumSOS!=0 || source.NumGenConstrs!=0 || source.NumQConstrs!=0)
            throw new InvalidOperationException("Resource template requires a linear model.");
        variables=source.GetVars().Select(v=>new Variable(v.VarName,v.VType,v.LB,v.UB)).ToArray();
        if(variables.Any(v=>v.Type is GRB.SEMICONT or GRB.SEMIINT)) throw new InvalidOperationException("Unsupported semi-continuous domain.");
        Expression Capture(GRBLinExpr e)=>new(Enumerable.Range(0,e.Size).Select(i=>e.GetVar(i).Index).ToArray(),
            Enumerable.Range(0,e.Size).Select(e.GetCoeff).ToArray(),e.Constant);
        rows=source.GetConstrs().Select((r,i)=>
        {
            if((i&2047)==0 && remaining?.Invoke()<=0) throw new TimeoutException("Macro row cache deadline expired.");
            return new Row(r.ConstrName,r.Sense,r.RHS,Capture(source.GetRow(r)));
        }).ToArray();
        sense=source.ModelSense;
        if(source.NumObj==1) objectives=[new("original",0,1,0,0,Capture(source.GetObjective() as GRBLinExpr??throw new InvalidOperationException("Nonlinear objective")))];
        else
        {
            int previous=source.Parameters.ObjNumber;List<Objective> list=[];
            for(int i=0;i<source.NumObj;i++)
            {
                source.Set(GRB.IntParam.ObjNumber,i);
                list.Add(new(source.ObjNName,source.ObjNPriority,source.ObjNWeight,source.ObjNAbsTol,source.ObjNRelTol,Capture(source.GetObjective(i))));
            }
            source.Set(GRB.IntParam.ObjNumber,previous);objectives=list.ToArray();
        }
    }

    internal IReadOnlyList<ResourceTightLink> TightLinks(IReadOnlyDictionary<string,double> values,IReadOnlyDictionary<string,string> events,Func<double>? remaining=null)
    {
        HashSet<ResourceTightLink> result=[];int count=0;
        foreach(var row in rows)
        {
            if((count++&2047)==0 && remaining?.Invoke()<=0)throw new TimeoutException("Tight row scan deadline expired.");
            double lhs=row.Expression.Constant;bool known=true;
            for(int j=0;j<row.Expression.Indices.Length;j++)
            {
                if(!values.TryGetValue(variables[row.Expression.Indices[j]].Name,out double value)) { known=false;break; }
                lhs+=row.Expression.Coefficients[j]*value;
            }
            if(!known || Math.Abs(lhs-row.Rhs)>30)continue;
            ResourceTightSupport.AddLinks(result,row.Expression.Indices.Select(i=>events.GetValueOrDefault(variables[i].Name)).OfType<string>());
        }
        return result.ToArray();
    }

    internal ResourceCompiledModel Build(IReadOnlyDictionary<string,(double Lower,double Upper)> restrictions,
        IReadOnlyDictionary<string,double> starts,IReadOnlySet<string>? retained=null,Func<double>? remaining=null)
    {
        void CheckBudget() { if(remaining?.Invoke()<=0) throw new TimeoutException("Local projection deadline expired."); }
        CheckBudget();
        double[] lower=variables.Select(v=>v.Lower).ToArray(),upper=variables.Select(v=>v.Upper).ToArray();
        for(int i=0;i<variables.Length;i++)
        {
            if(!restrictions.TryGetValue(variables[i].Name,out var domain)) continue;
            if(!double.IsFinite(domain.Lower) || !double.IsFinite(domain.Upper) || domain.Lower>domain.Upper ||
                domain.Lower<lower[i]-1e-6 || domain.Upper>upper[i]+1e-6) throw new InvalidDataException("Local restriction exceeds original domain: "+variables[i].Name);
            lower[i]=Math.Max(lower[i],domain.Lower);upper[i]=Math.Min(upper[i],domain.Upper);
            if(lower[i]>upper[i]) lower[i]=upper[i]; // original-bound numerical normalization only, at most 1e-6.
        }
        // Bound implications also eliminate continuous logical auxiliaries (stop products/reversals).
        // Unknown integers retain their type; only integer bounds are rounded.
        for(int pass=0;pass<5;pass++)
        {
            bool changed=false;
            int checkedRows=0;
            foreach(var row in rows)
            {
                if((checkedRows++&2047)==0) CheckBudget();
                if(row.Sense!=GRB.GREATER_EQUAL) Propagate(row,1);
                if(row.Sense!=GRB.LESS_EQUAL) Propagate(row,-1);
            }
            if(!changed) break;
            void Propagate(Row row,double sign)
            {
                var e=row.Expression;
                for(int target=0;target<e.Indices.Length;target++)
                {
                    int i=e.Indices[target];if(lower[i]==upper[i]) continue;
                    double a=e.Coefficients[target]*sign;if(Math.Abs(a)<1e-12) continue;
                    double minimum=e.Constant*sign;bool finite=true;
                    for(int j=0;j<e.Indices.Length;j++) if(j!=target)
                    {
                        int k=e.Indices[j];double c=e.Coefficients[j]*sign;
                        double bound=c>=0?lower[k]:upper[k];
                        if(Math.Abs(bound)>GRB.INFINITY/2) { finite=false;break; }
                        minimum+=c*bound;
                    }
                    if(!finite) continue;
                    double implied=(sign*row.Rhs-minimum)/a;
                    // A contradicting implication is left to the original retained row, never hidden by clamping.
                    if(a>0)
                    {
                        double bound=variables[i].Type==GRB.CONTINUOUS?implied:Math.Floor(implied+1e-6);
                        if(bound>=lower[i] && bound<upper[i]-1e-10) { upper[i]=bound;changed=true; }
                    }
                    else
                    {
                        double bound=variables[i].Type==GRB.CONTINUOUS?implied:Math.Ceiling(implied-1e-6);
                        if(bound<=upper[i] && bound>lower[i]+1e-10) { lower[i]=bound;changed=true; }
                    }
                }
            }
        }
        var environment=new GRBEnv(true);environment.Set(GRB.IntParam.OutputFlag,0);environment.Start();
        var model=new GRBModel(environment) { ModelName="resource_exact_local",ModelSense=sense };
        try
        {
            Dictionary<string,double> constantViolations=new(StringComparer.Ordinal);
            GRBVar?[] mapped=new GRBVar?[variables.Length];Dictionary<string,GRBVar> byName=new(StringComparer.Ordinal);
            Dictionary<string,double> fixedValues=new(StringComparer.Ordinal);
            for(int i=0;i<variables.Length;i++)
            {
                if((i&2047)==0) CheckBudget();
                var variable=variables[i];
                if(lower[i]==upper[i]) fixedValues.Add(variable.Name,lower[i]);
                if(lower[i]!=upper[i] || retained?.Contains(variable.Name)==true)
                    byName.Add(variable.Name,mapped[i]=model.AddVar(lower[i],upper[i],0,variable.Type,variable.Name));
            }
            int compiledRows=0;
            foreach(var row in rows)
            {
                if((compiledRows++&2047)==0) CheckBudget();
                var expression=Substitute(row.Expression);
                bool constantSatisfied=expression.Size==0 && (row.Sense==GRB.LESS_EQUAL?expression.Constant<=row.Rhs:
                    row.Sense==GRB.GREATER_EQUAL?expression.Constant>=row.Rhs:expression.Constant==row.Rhs);
                if(expression.Size==0 && !constantSatisfied && constantViolations.Count<64)
                    constantViolations.Add(row.Name,Math.Abs(expression.Constant-row.Rhs));
                if(!constantSatisfied) model.AddConstr(expression,row.Sense,row.Rhs,row.Name);
            }
            if(objectives.Length==1) model.SetObjective(Substitute(objectives[0].Expression),sense);
            else for(int i=0;i<objectives.Length;i++)
            {
                var o=objectives[i];model.SetObjectiveN(Substitute(o.Expression),i,o.Priority,o.Weight,o.AbsTol,o.RelTol,o.Name);
            }
            model.Update();
            foreach(var p in starts) if(byName.TryGetValue(p.Key,out var v) && double.IsFinite(p.Value) && p.Value!=GRB.UNDEFINED)
                v.Start=p.Value;
            return new(environment,model,byName,fixedValues,new(variables.Length,byName.Count,fixedValues.Count,rows.Length,
                model.NumConstrs,variables.Count(v=>v.Type!=GRB.CONTINUOUS && v.Lower<v.Upper),byName.Values.Count(v=>v.VType!=GRB.CONTINUOUS && v.LB<v.UB)),constantViolations);

            GRBLinExpr Substitute(Expression expression)
            {
                GRBLinExpr result=new();double constant=expression.Constant;
                for(int j=0;j<expression.Indices.Length;j++)
                {
                    int i=expression.Indices[j];double c=expression.Coefficients[j];
                    if(lower[i]==upper[i]) constant+=c*lower[i];else result.AddTerm(c,mapped[i]!);
                }
                result.AddConstant(constant);return result;
            }
        }
        catch { model.Dispose();environment.Dispose();throw; }
    }
}
internal sealed class ResourceCompiledModel : IDisposable
{
    private readonly IReadOnlyDictionary<string,double> fixedValues;
    internal GRBEnv Environment { get; }
    internal GRBModel Model { get; }
    internal IReadOnlyDictionary<string,GRBVar> Variables { get; }
    internal PartialDiscreteCompileAudit Audit { get; }
    internal IReadOnlyDictionary<string,double> ConstantViolations { get; }
    internal ResourceCompiledModel(GRBEnv environment,GRBModel model,IReadOnlyDictionary<string,GRBVar> variables,
        IReadOnlyDictionary<string,double> values,PartialDiscreteCompileAudit audit,IReadOnlyDictionary<string,double> constantViolations)
    { Environment=environment;Model=model;Variables=variables;fixedValues=values;Audit=audit;ConstantViolations=constantViolations; }
    internal double Value(string name)=>fixedValues.TryGetValue(name,out double value)?value:Variables[name].X;
    public void Dispose() { Model.Dispose();Environment.Dispose(); }
}
