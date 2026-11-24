using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GurobiTimetablingResearch.Optimization;

/// <summary>Bounded search history only. A failed local attempt is never an infeasibility proof.</summary>
internal sealed class ResourceFeasibilitySearchMemory
{
    private sealed record Failure(int Count,int Round,string Status);
    private readonly Dictionary<(string Anchor,string Scope),Failure> failures=[];
    private int fruitlessGuidance;

    internal bool CanTry(string anchor,string scope,int round)
    {
        if(round<1)throw new ArgumentOutOfRangeException(nameof(round));
        return !failures.TryGetValue((anchor,scope),out var old) || round-old.Round>=Math.Min(32,4*(1<<Math.Min(3,old.Count-1)));
    }
    internal void Record(string anchor,string scope,int round,bool productive,string status)
    {
        if(round<1)throw new ArgumentOutOfRangeException(nameof(round));
        var key=(anchor,scope);
        if(productive){failures.Remove(key);return;}
        if(failures.Count>=2048 && !failures.ContainsKey(key))
            failures.Remove(failures.MinBy(p=>p.Value.Round).Key);
        failures[key]=new(Math.Min(20,(failures.GetValueOrDefault(key)?.Count??0)+1),round,status);
    }
    internal int LastAttempt(string anchor,string scope)=>failures.GetValueOrDefault((anchor,scope))?.Round??0;
    internal (ResourceAlnsAction Action,string? Reason,string[] Skipped) Select(string anchor,
        IReadOnlyDictionary<ResourceAlnsAction,string> scopes,ResourceAlnsAction preferred,string preferredScope,
        int round,int stagnation,Func<IReadOnlyList<ResourceAlnsAction>,ResourceAlnsAction> choose)
    {
        if(scopes.Count==0)throw new ArgumentException("At least one feasible search action is required.",nameof(scopes));
        var eligible=scopes.Keys.Where(a=>CanTry(anchor,scopes[a],round)).ToArray();
        var skipped=scopes.Keys.Except(eligible).Select(a=>a.Id).ToArray();
        var blockers=eligible.Where(a=>a.Operator==ResourceAlnsOperator.Blocking).ToArray();
        if(stagnation>=3 && preferred.Operator==ResourceAlnsOperator.Legacy && blockers.Length>0)
        {
            int desired=ResourceAlnsNeighborhood.ScaleForStagnation(stagnation);
            return (blockers.OrderBy(a=>Math.Abs(a.Scale-desired)).First(),"StagnationBlocking",skipped);
        }
        if(!CanTry(anchor,preferredScope,round))
            return eligible.Length>0?(choose(eligible),"RepeatAvoidance",skipped):
                (scopes.Keys.OrderBy(a=>LastAttempt(anchor,scopes[a])).First(),"AllActionsCoolingRetry",skipped);
        return (preferred,null,skipped);
    }
    internal bool ShouldGuide(int stagnation,double before,double after)=>fruitlessGuidance<2 && stagnation==0 &&
        double.IsFinite(before) && double.IsFinite(after) && after>=0 && after<before-1e-5;
    internal void RecordGuidance(bool accepted)=>fruitlessGuidance=accepted?0:Math.Min(2,fruitlessGuidance+1);

    // Search windows limit the expensive all-day micro disjunctions. Periodic
    // unrestricted attempts preserve escape from every finite neighborhood.
    internal static double? RadiusCap(int stagnation)=>stagnation<3 || stagnation%16==15?null:stagnation<24?3600:7200;
    internal static string AnchorKey(MasterSolutionSnapshot snapshot)=>Hash(
        ResourceSearchPolicy.TimetableKey(snapshot)+"|"+ResourceSearchPolicy.StructureKey(snapshot));
    internal static string ScopeKey(ResourceNeighborhood n)
    {
        string[]? Ordered(IReadOnlySet<string>? values)=>values?.Select(s=>s.ToUpperInvariant()).Order(StringComparer.Ordinal).ToArray();
        var extraSeeds=n.SeedStopIds.Except(n.FreeTimeStopIds,StringComparer.OrdinalIgnoreCase)
            .Except(n.FreeStopIds,StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Hash(JsonSerializer.Serialize(new {Seeds=Ordered(extraSeeds),Times=Ordered(n.FreeTimeStopIds),Stops=Ordered(n.FreeStopIds),
            Changes=Math.Min(n.MaxStopChanges,n.FreeStopIds.Count),n.RadiusSeconds,
            SectionOrders=Ordered(n.FreeSectionOrderKeys),StationOrders=Ordered(n.FreeStationOrderKeys)}));
    }
    private static string Hash(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
