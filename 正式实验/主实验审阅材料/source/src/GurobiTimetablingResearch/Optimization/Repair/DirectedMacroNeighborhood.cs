using GurobiTimetablingResearch.Domain;
namespace GurobiTimetablingResearch.Optimization;

internal sealed record MacroPairPriority(string Pair,int FirstTrain,int SecondTrain,double Severity,double Distance);
internal static class DirectedMacroNeighborhood
{
    internal static HashSet<string> Select(IReadOnlyList<MacroPairPriority> pairs,int baseLimit,int expansion,int round,IReadOnlySet<string> required)
    {
        if(baseLimit<1 || baseLimit>10000 || expansion<0 || round<1) throw new ArgumentException("Invalid macro neighborhood controls.");
        int limit=Math.Min(pairs.Count,baseLimit*(1<<Math.Min(2,expansion)));
        var available=pairs.ToDictionary(p=>p.Pair,StringComparer.Ordinal);
        if(required.Any(p=>!available.ContainsKey(p))) throw new ArgumentException("Invalid mandatory macro witness pairs.");
        // A nominal scale is a search preference, never permission to drop physical dependencies.
        limit=Math.Max(limit,required.Count);
        HashSet<string> selected=new(required,StringComparer.Ordinal);
        var severe=pairs.Where(p=>p.Severity>0).OrderByDescending(p=>p.Severity).ThenBy(p=>p.Pair,StringComparer.Ordinal)
            .Take(Math.Max(1,limit/2)).ToArray();
        foreach(var p in severe) { if(selected.Count==limit) break;selected.Add(p.Pair); }
        var core=severe.SelectMany(p=>new[]{p.FirstTrain,p.SecondTrain})
            .Concat(required.SelectMany(p=>new[]{available[p].FirstTrain,available[p].SecondTrain})).ToHashSet();
        var boundary=pairs.Where(p=>!selected.Contains(p.Pair))
            .OrderBy(p=>core.Count==0 || core.Contains(p.FirstTrain) || core.Contains(p.SecondTrain)?0:1)
            .ThenBy(p=>p.Distance).ThenBy(p=>p.Pair,StringComparer.Ordinal).ToArray();
        int nearby=(limit-selected.Count)/2;
        foreach(var p in boundary.Take(nearby)) selected.Add(p.Pair);
        var rotating=boundary.Skip(nearby).ToArray();int slots=limit-selected.Count;
        if(rotating.Length>0)
            for(int i=0;i<slots;i++) selected.Add(rotating[(int)(((long)(round-1)*Math.Max(1,slots)+i)%rotating.Length)].Pair);
        return selected;
    }
}
internal sealed record JhMacroCandidate(MasterSolutionSnapshot Snapshot,string SourcePath,double Score,int Conflicts,int LongDwells)
{
    internal static JhMacroCandidate? SelectBest(IEnumerable<JhMacroCandidate> choices)
        =>choices.OrderBy(c=>c.Score).ThenBy(c=>c.Conflicts+c.LongDwells).FirstOrDefault();
}
