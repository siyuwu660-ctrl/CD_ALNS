using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

internal sealed record JhMacroRecoverySettings(int CandidateCapacity=4,int StalledBatches=2,int StagnationRounds=6,
    double RepairBatchSeconds=300,int MaxFreshRestarts=2,double RestartSolveSeconds=600)
{
    internal void Validate()
    {
        if(CandidateCapacity is <2 or >32 || StalledBatches is <1 or >100 || StagnationRounds is <1 or >1000 ||
            MaxFreshRestarts is <0 or >100 || !double.IsFinite(RepairBatchSeconds) || RepairBatchSeconds<=5 ||
            !double.IsFinite(RestartSolveSeconds) || RestartSolveSeconds<=0)
            throw new ArgumentException("Invalid macro recovery controls.");
    }
}
internal sealed class JhMacroBranch(int id,JhMacroCandidate candidate)
{
    internal int Id { get; }=id;
    internal JhMacroCandidate Candidate { get; set; }=candidate;
    internal DirectedMacroSearchState Continuation { get; set; }=new([],[]);
    internal int SweepOffset { get; set; }
    internal int Attempts { get; set; }
    internal int FailedBatches { get; set; }
}
internal sealed record JhMacroGeneration(int Index,int Seed,double TotalSeconds,double SolveSeconds);
/// <summary>Same-run candidate portfolio. Failure history belongs to a trajectory, never to all stop patterns.</summary>
internal sealed class JhMacroRecovery
{
    private readonly JhMacroRecoverySettings settings;
    private readonly List<JhMacroBranch> branches=[];
    private readonly HashSet<string> seen=new(StringComparer.Ordinal);
    private readonly Dictionary<string,SectionConflictEdgeRequest> guards=new(StringComparer.OrdinalIgnoreCase);
    private JhMacroBranch? preferred,lastAttempted;
    private int nextId;
    internal JhMacroRecovery(JhMacroRecoverySettings settings) { settings.Validate();this.settings=settings; }
    internal IReadOnlyList<JhMacroBranch> Branches=>branches;
    internal JhMacroCandidate? Best=>JhMacroCandidate.SelectBest(branches.Select(b=>b.Candidate));
    internal int GenerationCount { get; private set; }

    internal void Add(JhMacroCandidate candidate)
    {
        // Diagnostic objective metadata is not a new timetable.
        if(!seen.Add(Identity(candidate))) return;
        if(branches.Count==settings.CandidateCapacity)
        {
            var best=Best;
            var victim=branches.Where(b=>!ReferenceEquals(b.Candidate,best))
                .OrderByDescending(b=>b.FailedBatches).ThenByDescending(b=>b.Candidate.Score).ThenBy(b=>b.Id).First();
            branches.Remove(victim);
            if(ReferenceEquals(preferred,victim)) preferred=null;
        }
        branches.Add(new(++nextId,candidate));
    }
    internal JhMacroBranch? Select(bool allowStalled=false)
    {
        bool Eligible(JhMacroBranch b)=>!DirectedRepairBudget.Blocked(b.Continuation) &&
            (allowStalled || b.FailedBatches<settings.StalledBatches || b.Continuation.Context is {NeedsProtectedAttempt:true} context &&
                context.Fingerprint==MacroRepairContext.Identity(b.Candidate.Snapshot));
        if(preferred is not null && Eligible(preferred)) return preferred;
        var eligible=branches.Where(Eligible).ToArray();
        // Switch after a failed batch even when one trajectory has earned many more improving batches.
        var alternatives=eligible.Where(b=>!ReferenceEquals(b,lastAttempted)).ToArray();
        return (alternatives.Length>0?alternatives:eligible)
            .OrderBy(b=>b.Attempts).ThenBy(b=>b.Candidate.Score).ThenBy(b=>b.Id).FirstOrDefault();
    }
    internal void MergeGuards(DirectedMacroSearchState state)
    {
        if(state.ActiveGuardVersion==1) return; // Sparse dependencies belong to this branch, never the global full-guard pool.
        foreach(var edge in state.Edges)
            guards.TryAdd(BasicMilpModelBuilder.BuildSectionPrecedenceKey(edge.SectionName,edge.FirstTrainIndex,edge.SecondTrainIndex),edge);
    }
    internal DirectedMacroSearchState StateFor(JhMacroBranch branch)
        =>branch.Continuation.ActiveGuardVersion==1?branch.Continuation:branch.Continuation with { Edges=guards.Values.ToArray() };
    internal void Record(JhMacroBranch branch,JhMacroCandidate candidate,DirectedMacroSearchState state,int rounds)
    {
        if(!branches.Contains(branch) || rounds<0) throw new ArgumentException("Invalid macro branch completion.");
        MergeGuards(state);
        branch.Continuation=state.ActiveGuardVersion==1?state:state with { Edges=[] };
        branch.SweepOffset=checked(branch.SweepOffset+Math.Max(1,rounds));
        branch.Attempts++;
        bool improved=candidate.Score<branch.Candidate.Score-1e-6;
        if(improved)
        {
            branch.Candidate=candidate;string identity=Identity(candidate);seen.Add(identity);
            // Convergence is one physical candidate. Keep this progressing trajectory's history only.
            branches.RemoveAll(other=>!ReferenceEquals(other,branch) && Identity(other.Candidate)==identity);
        }
        branch.FailedBatches=improved?0:branch.FailedBatches+1;
        lastAttempted=branch;preferred=improved?branch:null;
    }
    internal JhMacroGeneration? BeginGeneration(double remaining,double initialSolveSeconds,double repairAllowance,int baseSeed)
    {
        // remaining already excludes the end-to-end reserve; leave a repair batch after generation.
        if(!double.IsFinite(remaining) || !double.IsFinite(initialSolveSeconds) || initialSolveSeconds<=0 ||
            !double.IsFinite(repairAllowance) || repairAllowance<=0 || baseSeed is <0 or >2_000_000_000)
            throw new ArgumentException("Invalid macro generation budget.");
        // A restart cap protects time for retained candidates. An empty pool has nothing to
        // repair: keep trying within the same wall-clock budget, allowing harder seed solves.
        if(branches.Count>0 && GenerationCount>settings.MaxFreshRestarts || remaining<=repairAllowance+5) return null;
        double limit=branches.Count==0
            ?initialSolveSeconds*Math.Pow(2,Math.Min(GenerationCount,2))
            :GenerationCount==0?initialSolveSeconds:Math.Min(initialSolveSeconds,settings.RestartSolveSeconds);
        double solve=Math.Min(limit,remaining-repairAllowance-2);
        var plan=new JhMacroGeneration(GenerationCount,Seed(baseSeed,GenerationCount),Math.Min(remaining-repairAllowance,solve+300),solve);
        GenerationCount++;
        return plan;
    }
    internal static int Seed(int baseSeed,int index)=>(int)(((long)baseSeed+104729L*index)%2_000_000_001L);
    private static string Identity(JhMacroCandidate candidate)=>SnapshotFingerprint.Calculate(candidate.Snapshot with { ObjectiveValue=0 });
}
