namespace GurobiTimetablingResearch.Optimization;

internal enum CormanStatus { Feasible, Infeasible, Unknown }
internal sealed record CormanBoundary(double Lower,double Upper);
// An arc is active iff every signed Boolean literal in When is true.
internal sealed record CormanArc(int Id,int From,int To,double Weight,int[] When,bool Coordination=false);
internal sealed record CormanProblem(string[] Nodes,int VariableCount,int[][] Clauses,CormanArc[] Arcs);
internal sealed record CormanCycle(CormanArc[] Arcs)
{
    internal double Length=>Arcs.Sum(a=>a.Weight);
}
internal sealed record CormanSmtResult(CormanStatus Status,double[] Times,sbyte[] Assignment,
    IReadOnlyList<CormanCycle> Cycles,long SearchNodes,string Reason)
{
    public CormanSearchStatistics Statistics {get;init;}=new(0,0,0,0,0,0,0,0,0);
}
internal sealed record CormanSearchStatistics(long Decisions,long Conflicts,long LearnedClauses,long TheoryCycles,
    long Backjumps,int LargestBackjump,long TheoryCalls,double TheorySeconds,int ProofCycles)
{
    internal static CormanSearchStatistics Empty=>new(0,0,0,0,0,0,0,0,0);
    internal CormanSearchStatistics Plus(CormanSearchStatistics other)=>new(Decisions+other.Decisions,
        Conflicts+other.Conflicts,LearnedClauses+other.LearnedClauses,TheoryCycles+other.TheoryCycles,
        Backjumps+other.Backjumps,Math.Max(LargestBackjump,other.LargestBackjump),TheoryCalls+other.TheoryCalls,
        TheorySeconds+other.TheorySeconds,ProofCycles+other.ProofCycles);
}
internal sealed record CormanTerm(int From,int To,double Length);
internal sealed record CormanAggregationResult(CormanSmtResult Original,IReadOnlyList<CormanTerm[]> Cuts,
    IReadOnlyList<CormanCycle[]> Proofs,int Calls,bool Finished,string Reason)
{
    public CormanStatus LastStatus {get;init;}
    public CormanSearchStatistics Statistics {get;init;}=CormanSearchStatistics.Empty;
}

internal sealed class CormanProblemBuilder
{
    private readonly List<string> _nodes=["0"];
    private readonly List<int[]> _clauses=[];
    private readonly List<CormanArc> _arcs=[];
    private int _variables;
    internal int Node(string name) {if(_nodes.Contains(name))throw new ArgumentException("Duplicate node");_nodes.Add(name);return _nodes.Count-1;}
    internal int Boolean()=>++_variables;
    internal void Clause(params int[] literals)=>_clauses.Add(literals.Distinct().ToArray());
    internal int[] Choices(int count)
    {
        int[] vars=Enumerable.Range(0,count).Select(_=>Boolean()).ToArray();Clause(vars);
        for(int i=0;i<count;i++)for(int j=i+1;j<count;j++)Clause(-vars[i],-vars[j]);
        return vars;
    }
    internal void Arc(int from,int to,double weight,int[]? when=null)
    {
        if(from<0 || to<0 || from>=_nodes.Count || to>=_nodes.Count || !double.IsFinite(weight))throw new ArgumentException("Invalid difference arc");
        _arcs.Add(new(_arcs.Count,from,to,weight,when??[]));
    }
    internal CormanProblem Build()
    {
        foreach(int lit in _clauses.SelectMany(c=>c).Concat(_arcs.SelectMany(a=>a.When)))
            if(lit==0 || Math.Abs((long)lit)>_variables)throw new ArgumentException("Invalid Boolean literal");
        return new(_nodes.ToArray(),_variables,_clauses.Select(c=>c.ToArray()).ToArray(),_arcs.ToArray());
    }
}
