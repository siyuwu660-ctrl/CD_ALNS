using System.Diagnostics;

namespace GurobiTimetablingResearch.Optimization;

/// <summary>
/// Corman Algorithm 1: CDCL(T), first-UIP resolution and nonchronological
/// backjumping. AnalyzeIP traverses immediate resolution parents from the empty
/// clause (Asin et al. 2008, section 4.1); only reachable theory leaves form the
/// Benders proof. Root unit reasons are retained, including at decision level 0.
/// </summary>
internal static class CormanSmtSolver
{
    internal const double Epsilon=1e-7;
    internal static CormanSmtResult Solve(CormanProblem problem,IReadOnlyDictionary<int,CormanBoundary> boundaries,
        Func<bool> withinBudget,long maximumSearchNodes=long.MaxValue)
        =>new Search(problem,boundaries,withinBudget,maximumSearchNodes).Run();

    private sealed class Clause(int[] literals,int[] parents,CormanCycle? cycle)
    {
        internal readonly int[] Literals=literals;
        internal readonly int[] Parents=parents;
        internal readonly CormanCycle? Cycle=cycle;
    }

    private sealed class Search
    {
        private readonly CormanProblem _problem;
        private readonly Func<bool> _budget;
        private readonly long _maximumSearchNodes;
        private readonly List<CormanArc> _arcs;
        private readonly List<Clause> _clauses=[];
        private readonly List<int>[] _watches;
        private readonly sbyte[] _assignment,_phase;
        private readonly int[] _level,_reason;
        private readonly double[] _activity;
        private readonly List<int> _trail=[],_limits=[];
        private int _head,_initialConflict=-1,_largestBackjump;
        private long _visits,_decisions,_conflicts,_learned,_theoryCycles,_backjumps,_theoryCalls,_theoryTicks;
        private double _increment=1;

        internal Search(CormanProblem problem,IReadOnlyDictionary<int,CormanBoundary> boundaries,Func<bool> budget,long maximumSearchNodes)
        {
            _problem=problem;_budget=budget;_maximumSearchNodes=maximumSearchNodes;_arcs=[..problem.Arcs];
            int n=problem.VariableCount+1;
            _assignment=new sbyte[n];_phase=Enumerable.Repeat((sbyte)1,n).ToArray();_level=new int[n];
            _reason=Enumerable.Repeat(-1,n).ToArray();_activity=new double[n];
            _watches=Enumerable.Range(0,2*n).Select(_=>new List<int>()).ToArray();
            int id=_arcs.Count;
            for(int node=1;node<problem.Nodes.Length;node++)_arcs.Add(new(id++,0,node,0,[]));
            foreach(var (node,b) in boundaries)
            {
                if(node<=0 || node>=problem.Nodes.Length || !double.IsFinite(b.Lower) || !double.IsFinite(b.Upper) || b.Lower>b.Upper)
                    throw new ArgumentException("Malformed coordination boundary");
                _arcs.Add(new(id++,0,node,b.Lower,[],true));_arcs.Add(new(id++,node,0,-b.Upper,[],true));
            }
            foreach(int[] literals in problem.Clauses)AddClause(literals,[],null);
            for(int ci=0;ci<_clauses.Count;ci++)
            {
                int[] lits=_clauses[ci].Literals;
                if(lits.Length==0 || (lits.Length==1 && !Enqueue(lits[0],ci))) {_initialConflict=ci;break;}
            }
        }

        internal CormanSmtResult Run()
        {
            int conflict=_initialConflict;
            while(true)
            {
                if(!_budget() || _visits>=_maximumSearchNodes)return Result(CormanStatus.Unknown,[],[],[],"BudgetOrSearchLimit");
                _visits++;
                if(conflict<0)conflict=Propagate();
                if(conflict==-2)return Result(CormanStatus.Unknown,[],[],[],"BudgetOrSearchLimit");
                double[] times=[];
                if(conflict<0)
                {
                    var active=_arcs.Where(a=>a.When.All(l=>Value(l)>0)).ToArray();
                    long start=Stopwatch.GetTimestamp();_theoryCalls++;
                    var theory=Evaluate(_problem.Nodes.Length,active,_budget);
                    _theoryTicks+=Stopwatch.GetTimestamp()-start;
                    if(theory.Unknown)return Result(CormanStatus.Unknown,[],[],[],"BudgetOrSearchLimit");
                    times=theory.Times;
                    if(theory.Cycle is not null)
                    {
                        _theoryCycles++;
                        // Newly discovered clauses are false on this trail.
                        // Watch the highest levels so backjumping restores the
                        // watched-literal invariant for the theory lemma too.
                        conflict=AddClause(theory.Cycle.Arcs.SelectMany(a=>a.When).Select(l=>-l)
                            .OrderByDescending(l=>_level[Math.Abs(l)]).ToArray(),[],theory.Cycle);
                        if(conflict<0)throw new InvalidOperationException("Active positive cycle cannot give a tautology");
                    }
                }
                if(conflict>=0)
                {
                    _conflicts++;
                    // A theory lemma can depend only on assignments below the
                    // latest decision. Analyse at its actual highest level.
                    int top=_clauses[conflict].Literals.Select(l=>_level[Math.Abs(l)]).DefaultIfEmpty(0).Max();
                    int oldLevel=_limits.Count;
                    Backtrack(top);
                    var analysis=Analyze(conflict,top);
                    if(analysis is null)return Result(CormanStatus.Unknown,[],[],[],"BudgetDuringConflictAnalysis");
                    var (literals,parents)=analysis.Value;
                    int learned=AddClause(literals,parents,null);_learned++;
                    if(top==0)
                    {
                        if(literals.Length!=0)throw new InvalidOperationException("Root resolution did not derive empty clause");
                        var proof=AnalyzeIP(learned);
                        if(proof is null)return Result(CormanStatus.Unknown,[],[],[],"BudgetDuringProofExtraction");
                        return Result(CormanStatus.Infeasible,[],[],proof,"RootUnsatWithResolutionProof");
                    }
                    int jump=literals.Skip(1).Select(l=>_level[Math.Abs(l)]).DefaultIfEmpty(0).Max();
                    int distance=oldLevel-jump;
                    if(distance>1)_backjumps++;
                    _largestBackjump=Math.Max(_largestBackjump,distance);
                    Backtrack(jump);
                    if(!Enqueue(literals[0],learned))throw new InvalidOperationException("First-UIP clause is not asserting");
                    _increment/=.95;
                    conflict=-1;continue;
                }
                int branch=Decide();
                if(branch==0)
                {
                    for(int v=1;v<_assignment.Length;v++)if(_assignment[v]==0)_assignment[v]=-1;
                    return Result(CormanStatus.Feasible,times,_assignment,[],"CompleteConsistentSelection");
                }
                _decisions++;_limits.Add(_trail.Count);Enqueue(branch,-1);
            }
        }

        private CormanSmtResult Result(CormanStatus status,double[] times,sbyte[] assignment,IReadOnlyList<CormanCycle> cycles,string reason)
            =>new(status,times,assignment,cycles,_visits,reason) {Statistics=new(_decisions,_conflicts,_learned,_theoryCycles,
                _backjumps,_largestBackjump,_theoryCalls,(double)_theoryTicks/Stopwatch.Frequency,cycles.Count)};
        private int Value(int lit)=>_assignment[Math.Abs(lit)]*Math.Sign(lit);
        private static int Index(int lit)=>2*Math.Abs(lit)+(lit<0?1:0);
        private bool Enqueue(int lit,int reason)
        {
            int v=Math.Abs(lit);if(_assignment[v]!=0)return Value(lit)>0;
            _assignment[v]=(sbyte)Math.Sign(lit);_phase[v]=_assignment[v];_level[v]=_limits.Count;_reason[v]=reason;_trail.Add(lit);return true;
        }
        private int AddClause(int[] literals,int[] parents,CormanCycle? cycle)
        {
            literals=literals.Distinct().ToArray();var unique=literals.ToHashSet();
            if(literals.Any(l=>unique.Contains(-l)))return -1;
            int id=_clauses.Count;_clauses.Add(new(literals,parents,cycle));
            if(literals.Length>=2){_watches[Index(literals[0])].Add(id);_watches[Index(literals[1])].Add(id);}
            return id;
        }
        private int Propagate()
        {
            // Watch the two first literals. Watch positions remain valid after
            // backjumping; clauses and proof parent lists are never discarded.
            while(_head<_trail.Count)
            {
                if(!_budget())return -2;
                int falsified=-_trail[_head++];var watching=_watches[Index(falsified)];
                for(int i=0;i<watching.Count;)
                {
                    if((i&255)==0 && !_budget())return -2;
                    int ci=watching[i];int[] c=_clauses[ci].Literals;
                    if(c[0]==falsified)(c[0],c[1])=(c[1],c[0]);
                    if(c[1]!=falsified)throw new InvalidOperationException("Broken watched literal");
                    if(Value(c[0])>0){i++;continue;}
                    int replacement=-1;
                    for(int k=2;k<c.Length;k++)if(Value(c[k])>=0){replacement=k;break;}
                    if(replacement>=0)
                    {
                        (c[1],c[replacement])=(c[replacement],c[1]);
                        watching[i]=watching[^1];watching.RemoveAt(watching.Count-1);_watches[Index(c[1])].Add(ci);continue;
                    }
                    if(!Enqueue(c[0],ci))return ci;
                    i++;
                }
            }
            return -1;
        }
        private (int[] Literals,int[] Parents)? Analyze(int conflict,int top)
        {
            var resolvent=_clauses[conflict].Literals.ToHashSet();List<int> parents=[conflict];
            int cursor=_trail.Count-1;
            // At level zero resolve ALL root reasons, not just current-level
            // implications. Dropping root literals without these parents would
            // lose theory lemmas needed by the final infeasibility certificate.
            while(top==0?resolvent.Count>0:resolvent.Count(l=>_level[Math.Abs(l)]==top)>1)
            {
                if(!_budget())return null;
                while(cursor>=0 && (!resolvent.Contains(-_trail[cursor]) || _level[Math.Abs(_trail[cursor])]!=top))cursor--;
                if(cursor<0)throw new InvalidOperationException("Missing implication during conflict analysis");
                int assigned=_trail[cursor--],reason=_reason[Math.Abs(assigned)];
                if(reason<0)throw new InvalidOperationException("Cannot resolve a decision before first UIP");
                resolvent.Remove(-assigned);parents.Add(reason);
                foreach(int lit in _clauses[reason].Literals)if(lit!=assigned)resolvent.Add(lit);
            }
            foreach(int lit in resolvent)
            {
                int v=Math.Abs(lit);_activity[v]+=_increment;
                if(_activity[v]>1e100)
                {for(int i=1;i<_activity.Length;i++)_activity[i]*=1e-100;_increment*=1e-100;}
            }
            // First literal is the UIP; the second has the greatest remaining
            // level, so the two watches work immediately after asserting it.
            return(resolvent.OrderByDescending(l=>_level[Math.Abs(l)]).ThenBy(l=>Math.Abs(l)).ToArray(),parents.Distinct().ToArray());
        }
        private CormanCycle[]? AnalyzeIP(int emptyClause)
        {
            Stack<int> pending=new();pending.Push(emptyClause);var seen=new HashSet<int>();List<(int Id,CormanCycle Cycle)> proof=[];
            while(pending.TryPop(out int ci))
            {
                if(!_budget())return null;
                if(!seen.Add(ci))continue;
                var clause=_clauses[ci];if(clause.Cycle is not null)proof.Add((ci,clause.Cycle));
                foreach(int parent in clause.Parents)pending.Push(parent);
            }
            return proof.OrderBy(p=>p.Id).Select(p=>p.Cycle).ToArray();
        }
        private void Backtrack(int target)
        {
            if(_limits.Count<=target)return;
            int start=_limits[target];
            for(int i=_trail.Count-1;i>=start;i--){int v=Math.Abs(_trail[i]);_assignment[v]=0;_reason[v]=-1;_level[v]=0;}
            _trail.RemoveRange(start,_trail.Count-start);_limits.RemoveRange(target,_limits.Count-target);_head=Math.Min(_head,start);
        }
        private int Decide()
        {
            // VSIDS-style decaying activity with saved polarity and stable ID
            // ties. Restrict candidates to unresolved choices, then potentially
            // active arcs, avoiding irrelevant route-pair ordering variables.
            var candidates=new HashSet<int>();
            foreach(var clause in _clauses)
            {
                if(clause.Literals.Any(l=>Value(l)>0))continue;
                foreach(int lit in clause.Literals)if(Value(lit)==0)candidates.Add(Math.Abs(lit));
            }
            if(candidates.Count==0)
                foreach(var arc in _arcs)
                {
                    if(arc.When.Any(l=>Value(l)<0))continue;
                    foreach(int lit in arc.When)if(Value(lit)==0)candidates.Add(Math.Abs(lit));
                }
            int chosen=0;
            foreach(int v in candidates)if(chosen==0 || _activity[v]>_activity[chosen] || (_activity[v]==_activity[chosen] && v<chosen))chosen=v;
            return chosen==0?0:chosen*_phase[chosen];
        }
    }

    internal static (double[] Times,CormanCycle? Cycle,bool Unknown) Evaluate(int nodes,IReadOnlyList<CormanArc> arcs,Func<bool> withinBudget)
    {
        double[] d=new double[nodes];CormanArc?[] predecessor=new CormanArc?[nodes];int updated=-1;
        for(int pass=0;pass<nodes;pass++)
        {
            if(!withinBudget())return([],null,true);
            updated=-1;
            for(int i=0;i<arcs.Count;i++)
            {
                if((i&1023)==0 && !withinBudget())return([],null,true);
                CormanArc a=arcs[i];double next=d[a.From]+a.Weight;
                if(!double.IsFinite(next))throw new ArithmeticException("Nonfinite difference propagation");
                if(next>d[a.To]+Epsilon){d[a.To]=next;predecessor[a.To]=a;updated=a.To;}
            }
            if(updated<0)
            {
                double origin=d[0];for(int i=0;i<nodes;i++)d[i]-=origin;
                return(d,null,false);
            }
        }
        int vertex=updated;
        for(int i=0;i<nodes;i++)vertex=(predecessor[vertex]??throw new InvalidOperationException("Broken cycle predecessor")).From;
        int start=vertex;List<CormanArc> cycle=[];
        do
        {
            var arc=predecessor[vertex]??throw new InvalidOperationException("Broken cycle");
            cycle.Add(arc);vertex=arc.From;
            if(cycle.Count>nodes)throw new InvalidOperationException("Cycle is not simple");
        }while(vertex!=start);
        cycle.Reverse();var proof=new CormanCycle(cycle.ToArray());
        if(proof.Length<=Epsilon)throw new InvalidOperationException("Cycle does not prove infeasibility");
        return([],proof,false);
    }
}
