using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

public sealed record StructuralTimingEdge(string FromEvent,string ToEvent,string Kind,double ConstantSeconds,
    string? FirstStopId,double FirstStopCoefficient,string? SecondStopId,double SecondStopCoefficient,
    double MinimumSeconds);
public sealed record StructuralTrainProof(int TrainIndex,string OriginStopId,string DestinationStopId,
    double MinimumDurationSeconds,IReadOnlyDictionary<string,string> StopDomains,IReadOnlyList<StructuralTimingEdge> Edges);
public sealed record StructuralStopProfile(string StopId,int TrainIndex,string StationName,string TrainType,
    string EventSide,double MinimumPrefixSeconds,double MinimumSuffixSeconds);
public sealed record StructuralQuotaProof(int RuleIndex,StationStopQuotaRule Rule,IReadOnlyList<string> CandidateStopIds,
    double KthPrefixSeconds,double KthSuffixSeconds,double FirstDepartureUpperSeconds,double LastArrivalLowerSeconds);
public sealed record StructuralSpanBoundReport(string InstanceName,string Scope,double TimingToleranceSeconds,
    double LowerBoundSeconds,double FirstDepartureUpperSeconds,double LastArrivalLowerSeconds,
    string FirstDepartureUpperSource,string LastArrivalLowerSource,double MaximumTrainDurationLowerSeconds,
    IReadOnlyList<StructuralTrainProof> Trains,IReadOnlyList<StructuralStopProfile> StopProfiles,
    IReadOnlyList<StructuralQuotaProof> Quotas);

/// <summary>
/// Solver-free necessary conditions of the ORIGINAL domain. No incumbent, fixed timetable,
/// local neighborhood, stop skeleton, soft reference time or target gap is accepted.
/// MinStops=k forces k witnesses in [a-eps,b+eps]. Their kth-smallest conditional
/// prefix/suffix imply F<=b+eps-p[k], L>=a-eps+q[k]. Independent witnesses are valid.
/// Every original runtime/dwell lower inequality is weakened by eps separately.
/// </summary>
public static class StructuralSpanBound
{
    public static StructuralSpanBoundReport Build(TimetablingInstance instance,double timingToleranceSeconds=0)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if(!double.IsFinite(timingToleranceSeconds) || timingToleranceSeconds<0)
            throw new ArgumentOutOfRangeException(nameof(timingToleranceSeconds));
        if(instance.Trains.Count==0 || instance.Meta.TimeUnit!="second")
            throw new InvalidDataException("Certificate requires nonempty second-based train data.");
        double eps=timingToleranceSeconds,firstUpper=double.PositiveInfinity,lastLower=double.NegativeInfinity;
        double longest=double.NegativeInfinity;string firstSource="",lastSource="";
        List<StructuralTrainProof> trains=[];List<StructuralStopProfile> profiles=[];List<StructuralQuotaProof> quotas=[];
        var stops=instance.Trains.SelectMany(t=>t.OrderedStops).ToArray();
        if(stops.Length!=instance.TrainStops.Count || stops.Select(s=>s.StopId).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=stops.Length ||
            stops.Any(s=>!instance.StopsById.TryGetValue(s.StopId,out var other) || s!=other))
            throw new InvalidDataException("Train paths and the quota stop universe must be identical.");
        foreach(var train in instance.Trains.OrderBy(t=>t.TrainIndex))
        {
            var path=train.OrderedStops;
            if(path.Count<2 || path[0].StopRole!=TrainStopRole.Origin || path[^1].StopRole!=TrainStopRole.Destination ||
                path.Skip(1).SkipLast(1).Any(s=>s.StopRole!=TrainStopRole.Intermediate) ||
                path.Any(s=>s.TrainIndex!=train.TrainIndex) ||
                path.Zip(path.Skip(1)).Any(pair=>pair.First.StationOrder>=pair.Second.StationOrder))
                throw new InvalidDataException("Certificate requires an ordered origin-to-destination path.");
            var domains=path.ToDictionary(s=>s.StopId,s=>Domain(s),StringComparer.OrdinalIgnoreCase);
            var terms=new List<StructuralTimingEdge>();var positions=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase) { [path[0].StopId]=0 };
            for(int j=1;j<path.Count;j++)
            {
                var before=path[j-1];var current=path[j];var rule=Runtime(instance,before,current);
                var run=new StructuralTimingEdge(before.StopId+":departure",current.StopId+":arrival","runtime",
                    rule.RunTimeSeconds,before.StopId,rule.StartAddSeconds,current.StopId,rule.StopAddSeconds,0);
                terms.Add(run with { MinimumSeconds=Minimum(run,null) });positions.Add(current.StopId,terms.Count);
                if(current.StopRole==TrainStopRole.Intermediate)
                {
                    int minimum=NormalDwellTimeResolver.Resolve(instance.NormalDwellTimeRules,current).NormalMinDwellSeconds;
                    if(minimum<0)throw new InvalidDataException("Negative normal dwell is unsupported.");
                    var dwell=new StructuralTimingEdge(current.StopId+":arrival",current.StopId+":departure","dwell",
                        0,current.StopId,minimum,null,0,0);
                    terms.Add(dwell with { MinimumSeconds=Minimum(dwell,null) });
                }
            }
            double total=terms.Sum(e=>e.MinimumSeconds);longest=Math.Max(longest,total);
            trains.Add(new(train.TrainIndex,path[0].StopId,path[^1].StopId,total,domains,terms));
            double upper=path[0].LatestDepSeconds+eps,lower=path[^1].EarliestArrSeconds-eps;
            if(upper<firstUpper) { firstUpper=upper;firstSource="origin-hard-window:"+path[0].StopId; }
            if(lower>lastLower) { lastLower=lower;lastSource="destination-hard-window:"+path[^1].StopId; }
            foreach(var stop in path.Where(s=>domains[s.StopId]!="0"))
            {
                int at=positions[stop.StopId];
                double prefix=terms.Take(at).Sum(e=>Minimum(e,stop.StopId));
                double suffix=terms.Skip(at).Sum(e=>Minimum(e,stop.StopId));
                profiles.Add(new(stop.StopId,stop.TrainIndex,stop.StationName,stop.TrainType,
                    stop.StopRole==TrainStopRole.Origin?"Departure":"Arrival",prefix,suffix));
            }
            double Minimum(StructuralTimingEdge edge,string? countedStop)
                =>edge.ConstantSeconds+Contribution(edge.FirstStopId,edge.FirstStopCoefficient,countedStop)
                    +Contribution(edge.SecondStopId,edge.SecondStopCoefficient,countedStop)-eps;
            double Contribution(string? id,double coefficient,string? countedStop)
            {
                if(id is null)return 0;
                if(string.Equals(id,countedStop,StringComparison.OrdinalIgnoreCase) || domains[id]=="1")return coefficient;
                return domains[id]=="0"?0:Math.Min(0,coefficient);
            }
        }
        foreach(var (rule,index) in instance.StationStopQuotaRules.Select((r,i)=>(r,i)))
        {
            if(rule.MinStops<0 || rule.MaxStops<rule.MinStops || rule.StartTimeSeconds>rule.EndTimeSeconds)
                throw new InvalidDataException("Malformed station quota.");
            if(rule.MinStops==0)continue;
            var candidates=profiles.Where(s=>string.Equals(s.StationName,rule.StationName,StringComparison.OrdinalIgnoreCase) &&
                string.Equals(s.TrainType,rule.TrainType,StringComparison.OrdinalIgnoreCase)).OrderBy(s=>s.StopId,StringComparer.Ordinal).ToArray();
            if(candidates.Length<rule.MinStops)
                throw new InvalidDataException($"Quota {index+1} has fewer possibly-stopping events than its required minimum.");
            double prefix=candidates.Select(s=>s.MinimumPrefixSeconds).Order().ElementAt(rule.MinStops-1);
            double suffix=candidates.Select(s=>s.MinimumSuffixSeconds).Order().ElementAt(rule.MinStops-1);
            double upper=rule.EndTimeSeconds+eps-prefix,lower=rule.StartTimeSeconds-eps+suffix;
            quotas.Add(new(index+1,rule,candidates.Select(s=>s.StopId).ToArray(),prefix,suffix,upper,lower));
            if(upper<firstUpper) { firstUpper=upper;firstSource="station-quota:"+(index+1); }
            if(lower>lastLower) { lastLower=lower;lastSource="station-quota:"+(index+1); }
        }
        // A tiny downward guard is conservative. Infeasible data or LB>an external UB
        // must be reported as contradictions; this API never sees or clamps to that UB.
        double bound=Math.Max(longest,lastLower-firstUpper)-1e-6;
        return new(instance.Meta.InstanceName,"OriginalDomain_QuotaWitnessKthOffsets_RuntimeDwell_TimingTolerance",
            eps,bound,firstUpper,lastLower,firstSource,lastSource,longest,trains,profiles,quotas);
    }
    private static string Domain(TrainStopRecord stop)
    {
        bool endpoint=stop.StopRole is TrainStopRole.Origin or TrainStopRole.Destination;
        if(endpoint && stop.StopRequirement==StopRequirement.MustPass)
            throw new InvalidDataException("Endpoint must-stop conflicts with must-pass.");
        if(endpoint || stop.StopRequirement==StopRequirement.MustStop)return "1";
        return stop.StopRequirement switch {
            StopRequirement.MustPass=>"0",
            StopRequirement.OptionalStop=>"0,1",
            _=>throw new InvalidDataException("Unknown stop mode.")
        };
    }
    private static RunTimeRule Runtime(TimetablingInstance instance,TrainStopRecord from,TrainStopRecord to)
    {
        if(to.IncomingSectionName is null || !instance.SectionByName.TryGetValue(to.IncomingSectionName,out var section))
            throw new InvalidDataException("Runtime section missing.");
        TravelDirection direction;
        if(string.Equals(section.FromStation,from.StationName,StringComparison.OrdinalIgnoreCase) &&
            string.Equals(section.ToStation,to.StationName,StringComparison.OrdinalIgnoreCase))direction=section.DirectionTag;
        else if(string.Equals(section.ToStation,from.StationName,StringComparison.OrdinalIgnoreCase) &&
            string.Equals(section.FromStation,to.StationName,StringComparison.OrdinalIgnoreCase))
            direction=section.DirectionTag switch { TravelDirection.Up=>TravelDirection.Down,TravelDirection.Down=>TravelDirection.Up,
                _=>throw new InvalidDataException("Unknown runtime direction.") };
        else throw new InvalidDataException("Runtime section does not connect consecutive stops.");
        if(direction==TravelDirection.Unknown)throw new InvalidDataException("Unknown runtime direction.");
        return instance.RunTimeRules.FirstOrDefault(r=>string.Equals(r.ScaleName,to.RunScaleName,StringComparison.OrdinalIgnoreCase) &&
            string.Equals(r.SectionName,to.IncomingSectionName,StringComparison.OrdinalIgnoreCase) && r.Direction==direction)
            ??throw new InvalidDataException("Directional runtime rule missing.");
    }
}
