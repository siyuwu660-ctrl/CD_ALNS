using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

internal sealed record DirectedConflict(string Kind,string SectionName,int FirstTrainIndex,int SecondTrainIndex,
    string FirstStopId,string SecondStopId,double Magnitude,string RelationKey);
internal sealed record DirectedDiagnosis(IReadOnlyList<DirectedConflict> Conflicts,
    IReadOnlySet<string> SupportedLongDwellStopIds,IReadOnlySet<string> UnresolvedLongDwellStopIds,
    double HeadwayDeficitSeconds,IReadOnlyDictionary<string,double> StopPressure,
    IReadOnlyDictionary<string,int> SuggestedOrders,IReadOnlyList<OvertakingStopProjection> Witnesses,
    string SourceFingerprint,IReadOnlyList<QuotaDependencyRow>? QuotaRows=null)
{
    public int QuotaViolationCount=>QuotaRows?.Count(r=>r.Violation>0)??0;
    public int QuotaViolationMagnitude=>QuotaRows?.Sum(r=>r.Violation)??0;
    public int OvertakerCapViolationCount=>Conflicts.Count(c=>c.Kind=="overtaker_cap");
    public bool ReadyForStrictCheck=>Conflicts.Count==0 && UnresolvedLongDwellStopIds.Count==0 && QuotaViolationCount==0;
}
internal static class DirectedCandidateDiagnostics
{
    private const double Tolerance=TimetableTimePolicy.NumericToleranceSeconds;
    /// <summary>Diagnose conflicts on the complete relation graph using the candidate's actual events.</summary>
    /// <param name="instance">Original complete instance and hard resource domains.</param>
    /// <param name="snapshot">Complete train pattern and physical times in seconds.</param>
    /// <returns>Conflict, quota, order suggestion and waiting-support evidence for repair.</returns>
    /// <exception cref="InvalidDataException">The snapshot omits trains, stops or finite event times.</exception>
    internal static DirectedDiagnosis Analyze(TimetablingInstance instance,MasterSolutionSnapshot snapshot)
    {
        BottleneckExperimentControls.ValidatePattern(instance,snapshot.StopByStopId);
        var ids=instance.TrainStops.Select(s=>s.StopId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach(var values in new[]{snapshot.ArrivalSecondsByStopId,snapshot.DepartureSecondsByStopId})
            if(values.Count!=ids.Count || values.Any(p=>!ids.Contains(p.Key) || !double.IsFinite(p.Value)))
                throw new InvalidDataException("Diagnosis requires complete finite physical event times.");
        if(!snapshot.AdmittedTrainIndices.SetEquals(instance.Trains.Select(t=>t.TrainIndex)))
            throw new InvalidDataException("Diagnosis requires all original trains.");
        var stops=instance.TrainStops.ToDictionary(s=>(s.TrainIndex,s.StationOrder));
        var full=new InitialConflictGraphBuilder(instance,int.MaxValue).Build();
        List<DirectedConflict> conflicts=[];
        Dictionary<string,int> suggested=new(StringComparer.OrdinalIgnoreCase),certain=new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string,double> pressure=ids.ToDictionary(id=>id,_=>0.0,StringComparer.OrdinalIgnoreCase);
        foreach(var c in full)
        {
            string key=BasicMilpModelBuilder.BuildSectionPrecedenceKey(c);
            var a=stops[(c.FirstTrainIndex,c.FirstStopOrder)];var b=stops[(c.SecondTrainIndex,c.SecondStopOrder)];
            var ae=stops[(c.FirstTrainIndex,c.FirstEndStopOrder)];var be=stops[(c.SecondTrainIndex,c.SecondEndStopOrder)];
            double dep=snapshot.DepartureSecondsByStopId[b.StopId]-snapshot.DepartureSecondsByStopId[a.StopId];
            double arr=snapshot.ArrivalSecondsByStopId[be.StopId]-snapshot.ArrivalSecondsByStopId[ae.StopId];
            double df=Required(a,b,true),dr=Required(b,a,true),af=Required(ae,be,false),ar=Required(be,ae,false);
            bool forward=dep>=df-Tolerance && arr>=af-Tolerance,reverse=-dep>=dr-Tolerance && -arr>=ar-Tolerance;
            // Suggestions are search choices; only a unique feasible direction is a physical witness.
            int suggestion=forward&&!reverse?0:reverse&&!forward?1:
                snapshot.SectionPrecedenceByKey.TryGetValue(key,out int prior)?prior:
                Math.Max(0,df-dep)+Math.Max(0,af-arr)<=Math.Max(0,dr+dep)+Math.Max(0,ar+arr)?0:1;
            suggested[key]=suggestion;
            if(forward!=reverse) certain[key]=forward?0:1;
            if(dep>Tolerance && arr< -Tolerance || dep< -Tolerance && arr>Tolerance) Add("section_order",a,be,Math.Min(Math.Abs(dep),Math.Abs(arr)));
            double deficit=(dep>=0?df:dr)-Math.Abs(dep);
            if(deficit>Tolerance) Add("headway",a,b,deficit);
            deficit=(arr>=0?af:ar)-Math.Abs(arr);
            if(deficit>Tolerance) Add("headway",ae,be,deficit);
            double Required(TrainStopRecord x,TrainStopRecord y,bool origin)=>MasterRouteDomainHeadwayProvider.ResolveLowerBound(
                instance,x,snapshot.StopByStopId[x.StopId]==1,y,snapshot.StopByStopId[y.StopId]==1,c.SectionName,origin);
            void Add(string kind,TrainStopRecord x,TrainStopRecord y,double amount)
            {
                conflicts.Add(new(kind,c.SectionName,c.FirstTrainIndex,c.SecondTrainIndex,x.StopId,y.StopId,amount,key));
                pressure[x.StopId]+=amount;pressure[y.StopId]+=amount;
            }
        }
        var projections=OvertakingStopProjectionPlanner.Plan(instance,full);
        List<OvertakingStopProjection> witnesses=[];HashSet<string> supported=new(StringComparer.OrdinalIgnoreCase);
        var visitsMap = instance.TrainStops.ToDictionary(stop => stop.StopId, StringComparer.OrdinalIgnoreCase);
        Dictionary<(int FirstTrain, int SecondTrain), int> pairFlipCountsMap = new();
        foreach(var p in projections)
        {
            double arrivalGap = snapshot.ArrivalSecondsByStopId[p.SecondStopId] - snapshot.ArrivalSecondsByStopId[p.FirstStopId];
            double departureGap = snapshot.DepartureSecondsByStopId[p.SecondStopId] - snapshot.DepartureSecondsByStopId[p.FirstStopId];
            if (Math.Abs(arrivalGap) <= Tolerance || Math.Abs(departureGap) <= Tolerance ||
                Math.Sign(arrivalGap) == Math.Sign(departureGap)) continue;
            string overtaken = arrivalGap > 0 ? p.FirstStopId : p.SecondStopId;
            string overtaker = arrivalGap > 0 ? p.SecondStopId : p.FirstStopId;
            var pairKey = (p.FirstTrainIndex, p.SecondTrainIndex);
            int flipCount = pairFlipCountsMap[pairKey] = pairFlipCountsMap.GetValueOrDefault(pairKey) + 1;
            if (flipCount > 1)
                conflicts.Add(new("repeated_pair_overtaking", p.StationName, p.FirstTrainIndex, p.SecondTrainIndex,
                    p.FirstStopId, p.SecondStopId, 1, p.IncomingPrecedenceKey));
            if (!StationOvertakingResourcePolicy.CanOvertake(instance, visitsMap[overtaken], visitsMap[overtaker]))
            {
                conflicts.Add(new("station_overtaking_resource", p.StationName, p.FirstTrainIndex, p.SecondTrainIndex,
                    p.FirstStopId, p.SecondStopId, 1, p.IncomingPrecedenceKey));
                pressure[overtaken] += 600;
                continue;
            }
            if (!certain.TryGetValue(p.IncomingPrecedenceKey, out int incoming) ||
                !certain.TryGetValue(p.OutgoingPrecedenceKey, out int outgoing) || incoming == outgoing) continue;
            if(snapshot.StopByStopId[overtaken]==1) { supported.Add(overtaken);witnesses.Add(p); }
            else
            {
                conflicts.Add(new("overtaken_without_stop",p.StationName,p.FirstTrainIndex,p.SecondTrainIndex,p.FirstStopId,p.SecondStopId,1,p.IncomingPrecedenceKey));
                pressure[overtaken]+=600;
            }
        }
        // Count distinct overtakers per physical stop, rather than duplicate section witnesses.
        foreach(var group in witnesses.GroupBy(p=>certain[p.OutgoingPrecedenceKey]==1?p.FirstStopId:p.SecondStopId,StringComparer.OrdinalIgnoreCase))
        {
            var overtakers=group.Select(p=>p.FirstStopId.Equals(group.Key,StringComparison.OrdinalIgnoreCase)?p.SecondTrainIndex:p.FirstTrainIndex).Distinct().ToArray();
            if(overtakers.Length<=1) continue;
            var first=group.First();
            conflicts.Add(new("overtaker_cap",first.StationName,first.FirstTrainIndex,first.SecondTrainIndex,
                first.FirstStopId,first.SecondStopId,overtakers.Length-1,first.ProjectionId));
            pressure[group.Key]+=600*(overtakers.Length-1);supported.Remove(group.Key);
        }
        var quotaRows=QuotaDependencySupport.Analyze(instance,snapshot);
        foreach(var row in quotaRows.Where(r=>r.Violation>0))
            foreach(string id in row.StopIds) pressure[id]+=600*row.Violation;
        HashSet<string> supportedLong=new(StringComparer.OrdinalIgnoreCase),unresolvedLong=new(StringComparer.OrdinalIgnoreCase);
        foreach(var stop in instance.TrainStops)
        {
            var dwell=NormalDwellTimeResolver.Resolve(instance.NormalDwellTimeRules,stop);
            if(!dwell.Applies) continue;
            double realized=snapshot.DepartureSecondsByStopId[stop.StopId]-snapshot.ArrivalSecondsByStopId[stop.StopId];
            if(realized<=dwell.NormalMaxDwellSeconds*snapshot.StopByStopId[stop.StopId]+Tolerance) continue;
            if(supported.Contains(stop.StopId) && realized<=dwell.WaitingMaxDwellSeconds+Tolerance) supportedLong.Add(stop.StopId);
            else { unresolvedLong.Add(stop.StopId);pressure[stop.StopId]+=Math.Max(1,realized-dwell.NormalMaxDwellSeconds); }
        }
        return new(conflicts,supportedLong,unresolvedLong,conflicts.Where(c=>c.Kind=="headway").Sum(c=>c.Magnitude),
            pressure,suggested,witnesses,SnapshotFingerprint.Calculate(snapshot),quotaRows);
    }
}
