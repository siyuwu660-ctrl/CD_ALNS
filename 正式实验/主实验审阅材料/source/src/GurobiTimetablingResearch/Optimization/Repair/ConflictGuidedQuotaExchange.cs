using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

internal sealed record ConflictQuotaProposal(IReadOnlyDictionary<string,int> Stops,IReadOnlyList<string> ChangedStopIds,double EstimatedGain,string PatternKey);
internal static class ConflictGuidedQuotaExchange
{
    internal static IReadOnlyList<ConflictQuotaProposal> Propose(TimetablingInstance instance,IReadOnlyDictionary<string,int> pattern,
        IReadOnlyDictionary<string,double> pressure,IReadOnlyList<StopLinearCut> cuts,IReadOnlySet<string>? excluded=null,
        int maxLength=8,int maxProposals=12,int maxNodes=100000,bool allowZeroGain=false,IReadOnlySet<string>? targetStops=null,bool staticQuotaRowsOnly=false,Func<double>? remaining=null)
    {
        if(maxLength is <1 or >8 || maxProposals<1 || maxNodes<1 || pressure.Values.Any(v=>!double.IsFinite(v)))
            throw new ArgumentException("Invalid cycle search limits or pressure.");
        BottleneckExperimentControls.ValidatePattern(instance,pattern);
        var domains=StructuralStopDomains.Build(instance);
        var stationRows=instance.StationStopQuotaRules.Select(r=>new { Rule=r,Stops=instance.TrainStops.Where(s=>
            s.StationName.Equals(r.StationName,StringComparison.OrdinalIgnoreCase) && s.TrainType.Equals(r.TrainType,StringComparison.OrdinalIgnoreCase)).ToArray() }).ToArray();
        var staticRows=stationRows.Where(r=>r.Stops.All(s=>r.Rule.StartTimeSeconds<=(s.StopRole==TrainStopRole.Origin?s.EarliestDepSeconds:s.EarliestArrSeconds) &&
            r.Rule.EndTimeSeconds>=(s.StopRole==TrainStopRole.Origin?s.LatestDepSeconds:s.LatestArrSeconds))).ToArray();
        if(!staticQuotaRowsOnly && staticRows.Length!=stationRows.Length) return [];
        // Dynamic window membership remains enforced by the original macro MIP.
        stationRows=staticRows;
        var rows=TrainSectionStopQuotaConstraintBuilder.Plan(instance).Select(r=>(Ids:r.CountedStops.Select(s=>s.StopId).ToArray(),Min:r.MinStops,Max:r.MaxStops))
            .Concat(stationRows.Select(r=>(Ids:r.Stops.Select(s=>s.StopId).ToArray(),Min:r.Rule.MinStops,Max:r.Rule.MaxStops))).ToArray();
        if(rows.Any(r=>r.Ids.Sum(id=>pattern[id])<r.Min || r.Ids.Sum(id=>pattern[id])>r.Max) || cuts.Any(c=>!c.Satisfied(pattern)))
            throw new InvalidDataException("Cycle input violates an original quota or travel cut.");
        var optional=instance.TrainStops.Where(s=>domains[s.StopId].Lower==0 && domains[s.StopId].Upper==1).ToArray();
        string StationKey(TrainStopRecord s)=>s.TrainType.ToUpperInvariant()+"::"+s.StationName.ToUpperInvariant();
        var targetStations=optional.Where(s=>targetStops?.Contains(s.StopId)==true && pattern[s.StopId]==1).Select(StationKey).ToHashSet();
        var removeAt=optional.Where(s=>pattern[s.StopId]==1).GroupBy(StationKey).ToDictionary(g=>g.Key,g=>g.OrderBy(s=>pressure.GetValueOrDefault(s.StopId)).ThenBy(s=>s.TrainIndex).ToArray());
        var addOn=optional.Where(s=>pattern[s.StopId]==0).GroupBy(s=>s.TrainIndex).ToDictionary(g=>g.Key,g=>g.OrderByDescending(s=>pressure.GetValueOrDefault(s.StopId)).ThenBy(s=>s.StationOrder).ToArray());
        List<ConflictQuotaProposal> result=[];HashSet<string> seen=[];int nodes=0;
        // Slack can permit one flip; exact balance and a four-change minimum are unnecessary.
        foreach(var stop in optional.OrderByDescending(s=>Math.Abs(pressure.GetValueOrDefault(s.StopId))))
        {
            if(++nodes>maxNodes || remaining?.Invoke()<=0)break;
            int change=1-2*pattern[stop.StopId];
            double gain=targetStops is { Count:>0 }?(targetStops.Contains(stop.StopId)?599:double.NegativeInfinity):pressure.GetValueOrDefault(stop.StopId)*change;
            if(gain<0 || !allowZeroGain && gain==0 || rows.Any(r=> {
                int count=r.Ids.Sum(id=>pattern[id])+(r.Ids.Contains(stop.StopId,StringComparer.OrdinalIgnoreCase)?change:0);
                return count<r.Min || count>r.Max; }))continue;
            var next=pattern.ToDictionary(p=>p.Key,p=>p.Value,StringComparer.OrdinalIgnoreCase);next[stop.StopId]+=change;
            string key=Key(next);
            if(cuts.Any(c=>!c.Satisfied(next)) || excluded?.Contains(key)==true || !seen.Add(key))continue;
            result.Add(new(next,new[]{stop.StopId},gain,key));
        }
        // Rotate a core-hitting cycle so its first added stop is either the target
        // itself or the quota replacement at a target-removal station.
        foreach(var start in optional.Where(s=>pattern[s.StopId]==0 && (targetStops is not { Count:>0 } ||
            targetStops.Contains(s.StopId) || targetStations.Contains(StationKey(s))))
            .OrderByDescending(s=>pressure.GetValueOrDefault(s.StopId)).ThenBy(s=>s.StopId,StringComparer.Ordinal).Take(128))
        {
            if(nodes>=maxNodes || remaining?.Invoke()<=0) break;
            int startLimit=Math.Min(maxNodes,nodes+4000);
            List<TrainStopRecord> changed=[start];HashSet<int> trains=[start.TrainIndex];HashSet<string> stations=[StationKey(start)];
            Extend(start);
            void Extend(TrainStopRecord added)
            {
                if(!removeAt.TryGetValue(StationKey(added),out var removals)) return;
                foreach(var removed in removals)
                {
                    if(++nodes>startLimit || remaining?.Invoke()<=0) return;
                    if(removed.TrainIndex==start.TrainIndex)
                    {
                        if(changed.Count>=1 && changed.Count+1<=maxLength) { changed.Add(removed);Accept();changed.RemoveAt(changed.Count-1); }
                        continue;
                    }
                    if(changed.Count+3>maxLength || trains.Contains(removed.TrainIndex) || !addOn.TryGetValue(removed.TrainIndex,out var additions)) continue;
                    trains.Add(removed.TrainIndex);changed.Add(removed);
                    foreach(var next in additions)
                    {
                        string station=StationKey(next);if(stations.Contains(station)) continue;
                        stations.Add(station);changed.Add(next);Extend(next);changed.RemoveAt(changed.Count-1);stations.Remove(station);
                        if(nodes>=startLimit) break;
                    }
                    changed.RemoveAt(changed.Count-1);trains.Remove(removed.TrainIndex);
                }
            }
            void Accept()
            {
                var delta=changed.ToDictionary(s=>s.StopId,s=>1-2*pattern[s.StopId]);
                // A bounded search proxy, NOT measured conflict reduction or a bound.
                // Reward hitting the target core once and charge for each change.
                double gain=targetStops is { Count:>0 }?
                    (changed.Any(s=>targetStops.Contains(s.StopId))?600-changed.Count:double.NegativeInfinity):
                    delta.Sum(p=>pressure.GetValueOrDefault(p.Key)*p.Value);
                if(gain<0 || (!allowZeroGain && gain==0) || rows.Any(r=> { int count=r.Ids.Sum(id=>pattern[id]+delta.GetValueOrDefault(id)); return count<r.Min || count>r.Max; })) return;
                var next=pattern.ToDictionary(p=>p.Key,p=>p.Value,StringComparer.OrdinalIgnoreCase);
                foreach(var d in delta) next[d.Key]+=d.Value;
                if(cuts.Any(c=>!c.Satisfied(next))) return;
                string key=Key(next);if(excluded?.Contains(key)==true || !seen.Add(key)) return;
                result.Add(new(next,changed.Select(s=>s.StopId).ToArray(),gain,key));
                if(result.Count>maxProposals*2) result=result.OrderByDescending(p=>p.EstimatedGain).ThenBy(p=>p.ChangedStopIds.Count).ThenBy(p=>p.PatternKey,StringComparer.Ordinal).Take(maxProposals).ToList();
            }
        }
        return result.OrderByDescending(p=>p.EstimatedGain).ThenBy(p=>p.ChangedStopIds.Count).ThenBy(p=>p.PatternKey,StringComparer.Ordinal).Take(maxProposals).ToArray();
    }

    internal static string Key(IReadOnlyDictionary<string,int> pattern)=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(string.Join(";",pattern.OrderBy(p=>p.Key,StringComparer.Ordinal).Select(p=>p.Key+"="+p.Value)))));
}
