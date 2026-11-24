using System.Diagnostics;
using Gurobi;

namespace GurobiTimetablingResearch.Optimization;

public sealed record FixedDiscreteReductionAudit(int SourceVariableCount, int InitiallyFixedVariableCount,
    int NewlyFixedVariableCount, int RemainingFreeDiscreteCount, int Passes, bool ReachedFixedPoint,
    double Seconds, IReadOnlyList<string> ImplicationRows);

public sealed class FixedDiscreteDomainContradictionException(string message) : InvalidOperationException(message);

/// <summary>Sound interval propagation over retained linear source rows. Does not relax unknown integers.</summary>
public static class FixedDiscreteModelReducer
{
    private const double Tol=1e-8;
    public static FixedDiscreteReductionAudit Apply(GRBModel model,int maxPasses=16,double budgetSeconds=10)
    {
        if (maxPasses<=0 || !double.IsFinite(budgetSeconds) || budgetSeconds<=0) throw new ArgumentOutOfRangeException(nameof(maxPasses));
        Stopwatch clock=Stopwatch.StartNew();
        model.Update();
        GRBVar[] vars=model.GetVars();
        Dictionary<int,int> positions=vars.Select((v,i)=>(v.Index,i)).ToDictionary(p=>p.Index,p=>p.i);
        double[] lower=vars.Select(v=>v.LB).ToArray(), upper=vars.Select(v=>v.UB).ToArray();
        bool[] integer=vars.Select(v=>v.VType is GRB.BINARY or GRB.INTEGER or GRB.SEMIINT).ToArray();
        if(vars.Any(v=>v.VType is GRB.SEMICONT or GRB.SEMIINT))
            throw new InvalidOperationException("Semi-continuous domains need explicit domain handling; reduction refused.");
        int initial=vars.Count(v=>Math.Abs(v.LB-v.UB)<=Tol);
        var rows=model.GetConstrs().Select(c=>
        {
            GRBLinExpr expression=model.GetRow(c);
            return (Name:c.ConstrName,Sense:c.Sense,Rhs:c.RHS,
                Terms:Enumerable.Range(0,expression.Size).Select(j=>(Index:positions[expression.GetVar(j).Index],Coefficient:expression.GetCoeff(j))).ToArray());
        }).ToArray();
        HashSet<string> implicationRows=new(StringComparer.Ordinal);
        bool changed=true;
        int passes=0;
        while(changed && passes<maxPasses && clock.Elapsed.TotalSeconds<budgetSeconds)
        {
            changed=false; passes++;
            foreach(var row in rows)
            {
                if (clock.Elapsed.TotalSeconds>=budgetSeconds) break;
                if(row.Sense!=GRB.GREATER_EQUAL) Propagate(row.Terms,row.Rhs,1,row.Name);
                if(row.Sense!=GRB.LESS_EQUAL) Propagate(row.Terms,row.Rhs,-1,row.Name);
            }
        }
        for(int i=0;i<vars.Length;i++) { vars[i].LB=lower[i]; vars[i].UB=upper[i]; }
        model.Update();
        int fixedCount=Enumerable.Range(0,vars.Length).Count(i=>Math.Abs(lower[i]-upper[i])<=Tol);
        int free=Enumerable.Range(0,vars.Length).Count(i=>integer[i] && upper[i]-lower[i]>Tol);
        return new(vars.Length,initial,fixedCount-initial,free,passes,!changed && clock.Elapsed.TotalSeconds<budgetSeconds,
            clock.Elapsed.TotalSeconds,implicationRows.Order(StringComparer.Ordinal).ToArray());

        void Propagate((int Index,double Coefficient)[] terms,double rhs,double sign,string rowName)
        {
            // Exclude target explicitly: avoids infinity-infinity and unstable cancellation.
            for(int target=0;target<terms.Length;target++)
            {
                (int i,double coefficient)=terms[target];
                double a=sign*coefficient;
                if(Math.Abs(a)<Tol) continue;
                double minimumOthers=0;
                bool finite=true;
                for(int j=0;j<terms.Length;j++)
                {
                    if(j==target) continue;
                    (int k,double c)=terms[j]; c*=sign;
                    double bound=c>=0?lower[k]:upper[k];
                    if(Math.Abs(bound)>=GRB.INFINITY/2) { finite=false;break; }
                    minimumOthers+=c*bound;
                }
                if(!finite || !double.IsFinite(minimumOthers)) continue;
                double implied=(sign*rhs-minimumOthers)/a;
                if(!double.IsFinite(implied)) continue;
                if(integer[i]) implied=a>0?Math.Floor(implied+Tol):Math.Ceiling(implied-Tol);
                if(a>0 && implied<upper[i]-Tol) { upper[i]=implied;changed=true;implicationRows.Add(rowName); }
                if(a<0 && implied>lower[i]+Tol) { lower[i]=implied;changed=true;implicationRows.Add(rowName); }
                if(lower[i]>upper[i]+Tol) throw new FixedDiscreteDomainContradictionException($"Derived domain contradiction: {vars[i].VarName}; source={rowName}");
            }
        }
    }
}
