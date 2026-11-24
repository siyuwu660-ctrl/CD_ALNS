namespace GurobiTimetablingResearch.Optimization;

/// <summary>Search preferences only; no feasibility certificate or global bound.</summary>
internal static class ResourceSearchPolicy
{
    internal static double QualityReward(IReadOnlyList<double> before,IReadOnlyList<double> after,bool strict,bool bestSpan,bool diverse)
    {
        if(!strict) return 0;
        if(before.Count!=3 || after.Count!=3 || before.Concat(after).Any(v=>!double.IsFinite(v)||v<0))
            throw new ArgumentException("Invalid quality vector.");
        if(after[0]<before[0]-1e-6) return (bestSpan?4:1)+Math.Min(1,(before[0]-after[0])/Math.Max(1,before[0]));
        if(Math.Abs(after[0]-before[0])<=1e-6 && ResourceAlnsPolicy.Gain(before,after)>0) return .1;
        return diverse?.05:0;
    }
    internal static bool AcceptCurrent(ServiceObjectiveVector best,ServiceObjectiveVector current,ServiceObjectiveVector candidate,
        bool strict,bool newStructure,int stagnation,double remainingFraction)
    {
        if(!strict || best.PolicyIdentity!=candidate.PolicyIdentity || best.PolicyIdentity!=current.PolicyIdentity ||
            new[]{candidate.GlobalSpanSeconds,candidate.ServicePenalty,candidate.RunningTimeSupplementSeconds}.Any(x=>!double.IsFinite(x)||x<0))return false;
        if(ResourceSpanObjectivePolicy.Better(candidate,best))return true;
        if(candidate.ServicePenalty>best.ServicePenalty+1e-6)return false;
        double threshold=stagnation<6?0:Math.Min(30,best.GlobalSpanSeconds*.0025)*Math.Clamp(remainingFraction,0,1);
        if(candidate.GlobalSpanSeconds>best.GlobalSpanSeconds+threshold+1e-6)return false;
        if(ResourceSpanObjectivePolicy.Better(candidate,current))return true;
        bool equal=Math.Abs(candidate.GlobalSpanSeconds-current.GlobalSpanSeconds)<=1e-6 &&
            Math.Abs(candidate.ServicePenalty-current.ServicePenalty)<=1e-6 &&
            Math.Abs(candidate.RunningTimeSupplementSeconds-current.RunningTimeSupplementSeconds)<=1e-6;
        return newStructure && (equal || stagnation>=6 && threshold>0);
    }
    internal static bool GuidanceAccepts(double oldViolation,ServiceObjectiveVector baseline,double violation,ServiceObjectiveVector candidate,bool complete,bool couldBeJoint=false)
        =>complete && baseline.PolicyIdentity==candidate.PolicyIdentity && double.IsFinite(violation) && violation>=0 &&
            candidate.ServicePenalty<=baseline.ServicePenalty+1e-6 && candidate.GlobalSpanSeconds<=baseline.GlobalSpanSeconds+1e-6 &&
            (couldBeJoint || violation<=oldViolation+1e-6 && candidate.GlobalSpanSeconds<baseline.GlobalSpanSeconds-1e-6);
    internal static double GuidanceBudget(double iterationBudget,double remaining,bool jointCandidate)
        =>jointCandidate?0:Math.Max(0,Math.Min(15,Math.Min(iterationBudget*.1,remaining-2)));

    internal static string TimetableKey(MasterSolutionSnapshot snapshot)
    {
        // Millisecond rounding suppresses solver noise; this key is only a reward statistic.
        string Times(IReadOnlyDictionary<string,double> values)=>string.Join(";",values.OrderBy(p=>p.Key,StringComparer.Ordinal)
            .Select(p=>p.Key+":"+Math.Round(p.Value,3,MidpointRounding.AwayFromZero).ToString("F3",System.Globalization.CultureInfo.InvariantCulture)));
        string stops=string.Join(";",snapshot.StopByStopId.OrderBy(p=>p.Key,StringComparer.Ordinal).Select(p=>$"{p.Key}:{p.Value}"));
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            stops+"|"+Times(snapshot.ArrivalSecondsByStopId)+"|"+Times(snapshot.DepartureSecondsByStopId))));
    }

    internal static string StructureKey(MasterSolutionSnapshot snapshot)
    {
        string Bits(IReadOnlyDictionary<string,int> values)=>string.Join(";",values.OrderBy(p=>p.Key,StringComparer.Ordinal).Select(p=>$"{p.Key}:{p.Value}"));
        var resources=snapshot.VerifiedMicroAssignments?.OrderBy(w=>w.RequirementId,StringComparer.Ordinal)
            .Select(w=>$"{w.RequirementId}:{w.TrackName}:{w.IncomingRouteId}:{w.OutgoingRouteId}")??[];
        return Bits(snapshot.StopByStopId)+"|"+Bits(snapshot.SectionPrecedenceByKey)+"|"+Bits(snapshot.StationEventPrecedenceByKey)+"|"+string.Join(";",resources);
    }
}
