using System.Text.Json;
using Gurobi;

namespace GurobiTimetablingResearch.Optimization;

internal sealed record DirectedTemporaryTimeBound(string StopId,bool LowerTightened,bool UpperTightened);

// Releases search restrictions only. Never a globally valid infeasibility cut.
internal sealed record DirectedIisGuidance(IReadOnlySet<string> ReleasedPairs,IReadOnlySet<string> StopIds,bool RemoveTemporaryRadius)
{
    internal static DirectedIisGuidance Analyze(object evidence,IReadOnlyDictionary<string,string> fixedPairByVariable,
        IReadOnlyDictionary<string,DirectedTemporaryTimeBound> timeBounds,IReadOnlyDictionary<string,string> stopIdsByVariable)
    {
        var root=JsonSerializer.SerializeToElement(evidence);
        HashSet<string> pairs=new(StringComparer.Ordinal),stops=new(StringComparer.OrdinalIgnoreCase);bool radius=false;
        if(!root.TryGetProperty("nativeStatus",out var native) || native.GetInt32()!=GRB.Status.INFEASIBLE) return new(pairs,stops,false);
        if(root.TryGetProperty("substitutedFixedDependencies",out var dependencies))
            foreach(var dependency in dependencies.EnumerateArray())
            {
                string name=dependency.GetProperty("name").GetString()!;
                if(pairs.Count<64 && fixedPairByVariable.TryGetValue(name,out string? pair)) pairs.Add(pair);
                if(stops.Count<64 && stopIdsByVariable.TryGetValue(name,out string? stop)) stops.Add(stop);
            }
        if(root.TryGetProperty("bounds",out var bounds))
            foreach(var bound in bounds.EnumerateArray())
            {
                string name=bound.GetProperty("name").GetString()!;
                if(!timeBounds.TryGetValue(name,out var temporary)) continue;
                if(temporary.LowerTightened && bound.GetProperty("lowerInIis").GetBoolean() ||
                    temporary.UpperTightened && bound.GetProperty("upperInIis").GetBoolean())
                { radius=true;stops.Add(temporary.StopId); }
            }
        return new(pairs,stops,radius);
    }
}
