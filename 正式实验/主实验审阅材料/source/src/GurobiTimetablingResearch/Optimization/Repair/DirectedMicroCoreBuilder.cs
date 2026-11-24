using System.Security.Cryptography;
using System.Text;

namespace GurobiTimetablingResearch.Optimization;

/// <summary>
/// Necessary resource constraints for a proven core only. Outside requirements
/// remain unmodelled here and MUST be checked again by the global Micro verifier.
/// Every included requirement retains its complete original route/track/stop domain.
/// </summary>
internal static class DirectedMicroCoreBuilder
{
    internal static CompleteStationMicroComponentBuildResult Build(BasicMilpModel model,
        StationLevelMicroSubproblemResult station,int round,IReadOnlyList<PromotedMicroComponent>? retained=null)
    {
        if(station.AssignmentResult.Status!=MicroSolveStatus.ProvenInfeasible)
            return new(false,null,"unknown_does_not_create_core_feedback",0,0);
        var ids=station.ConflictCore.SupportStopIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if(ids.Count==0) return new(false,null,"no_mapped_core_requirements",0,0);
        var previous=(retained ?? []).Where(c=>c.StationName.Equals(station.StationName,StringComparison.OrdinalIgnoreCase))
            .SelectMany(c=>c.Requirements).Select(r=>r.RequirementId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if(ids.IsSubsetOf(previous)) return new(false,null,"core_already_in_coherent_station_component",ids.Count,0);
        ids.UnionWith(previous);
        var all=ValidityFirstHotspotComponentBuilder.BuildCompleteStationComponent(model,station.StationName,StationEndpointOccupationPolicy.DefaultSeconds,round,"directed_core_resource_subset");
        if(!all.IsComplete || all.Component is null) return all;
        var requirements=all.Component.Requirements.Where(r=>ids.Contains(r.RequirementId)).ToArray();
        if(requirements.Length!=ids.Count) return new(false,null,"incomplete_core_mapping",0,0);
        string key=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|",ids.Order(StringComparer.Ordinal)))));
        var component=all.Component with { ComponentId="directed_core::"+station.StationName+"::"+key,
            Requirements=requirements,TrainIndices=requirements.Select(r=>r.TrainIndex).Distinct().Order().ToArray(),
            PromotionReason="proven_core_necessary_resource_subset_not_whole_station" };
        return new(true,component,"complete_domains_for_core_subset_requires_global_recheck",requirements.Length,requirements.Sum(r=>r.AllowedAlternatives.Count));
    }
}
