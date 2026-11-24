using Gurobi;

namespace GurobiTimetablingResearch.Optimization;

internal static class CormanMilpCuts
{
    // Exact disjunction, using finite bounds for each implication's big-M.
    // Guard terms release the cut when a business stop-mode pattern changes.
    internal static void Add(GRBModel model,IReadOnlyDictionary<int,GRBVar> events,IReadOnlyList<CormanTerm> terms,
        string name,IReadOnlyDictionary<string,int>? modes=null,IReadOnlyDictionary<string,GRBVar>? stops=null)
    {
        model.Update();GRBLinExpr disjunction=new();int index=0;
        foreach(var term in terms)
        {
            GRBLinExpr difference=new();double lower=0;
            if(term.To!=0){difference.AddTerm(1,events[term.To]);lower+=events[term.To].LB;}
            if(term.From!=0){difference.AddTerm(-1,events[term.From]);lower-=events[term.From].UB;}
            double bigM=Math.Max(0,term.Length-lower);
            if(!double.IsFinite(bigM) || bigM>=GRB.INFINITY/2)throw new InvalidOperationException("Cut requires finite original event bounds");
            var selected=model.AddVar(0,1,0,GRB.BINARY,$"{name}_term_{index}");
            model.AddConstr(difference+bigM*(1-selected),GRB.GREATER_EQUAL,term.Length,$"{name}_relation_{index++}");
            disjunction.AddTerm(1,selected);
        }
        foreach(var (id,mode) in modes??new Dictionary<string,int>())
        {
            if(mode is not (0 or 1) || stops is null)throw new ArgumentException("Invalid cut mode guard");
            disjunction.AddTerm(mode==0?1:-1,stops[id]);if(mode==1)disjunction.AddConstant(1);
        }
        model.AddConstr(disjunction,GRB.GREATER_EQUAL,1,name+"_or");
    }
}
