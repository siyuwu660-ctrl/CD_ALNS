using System.Security.Cryptography;
using System.Text;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

internal sealed record MacroRepairContext(string Fingerprint,string[] FeedbackPairs,string[] FeedbackStops,
    bool OriginalTimeDomain=false,bool PendingFeedback=false,int OrderAttempts=0,int StopAttempts=0,int BoundaryAttempts=0,
    string[]? ExhaustedSearches=null,bool CoverageRequired=false,int OrderUnavailableAttempts=0,int StopUnavailableAttempts=0,
    int BoundaryUnavailableAttempts=0,int FeedbackFollowups=0,string FeedbackReason="MacroIisFeedback",int StopCoverageVersion=0)
{
    public bool CoverageComplete=>OrderAttempts+OrderUnavailableAttempts>=2 && StopAttempts+StopUnavailableAttempts>=2 && BoundaryAttempts+BoundaryUnavailableAttempts>=2;
    public bool NeedsProtectedAttempt=>CoverageRequired && !CoverageComplete || PendingFeedback && FeedbackFollowups<3;
    internal static string Identity(MasterSolutionSnapshot candidate)
    {
        // Snapshot order metadata changes when an active model is sparse; it is not a new physical timetable.
        string N(double x)=>x.ToString("R",System.Globalization.CultureInfo.InvariantCulture);
        string text=string.Join("|",candidate.StopByStopId.OrderBy(p=>p.Key,StringComparer.Ordinal).Select(p=>
            p.Key+":"+p.Value+":"+N(candidate.ArrivalSecondsByStopId[p.Key])+":"+N(candidate.DepartureSecondsByStopId[p.Key])));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
    internal static MacroRepairContext For(MasterSolutionSnapshot candidate,MacroRepairContext? prior=null)
    {
        string key=Identity(candidate);
        var context=prior?.Fingerprint==key?prior:new(key,[],[]);
        // Pre-scope counters only audited the global free-stop total, not core dependencies.
        return context.StopCoverageVersion==1?context:context with {StopAttempts=0,StopUnavailableAttempts=0,StopCoverageVersion=1};
    }
    internal static bool NearFeasible(DirectedDiagnosis diagnosis)=>diagnosis.Conflicts.Count<=8 &&
        diagnosis.UnresolvedLongDwellStopIds.Count==0 && diagnosis.QuotaViolationCount==0;
    internal MacroAlnsAction? RequiredAction(bool nearFeasible)
    {
        if(PendingFeedback && FeedbackFollowups<3) return new(FeedbackStops.Length>0?MacroAlnsDirection.StopQuotaTimeOrder:MacroAlnsDirection.OrderTime,2);
        if(!nearFeasible) return null;
        if(OrderAttempts+OrderUnavailableAttempts<2) return new(MacroAlnsDirection.OrderTime,OrderAttempts+OrderUnavailableAttempts);
        if(StopAttempts+StopUnavailableAttempts<2) return new(MacroAlnsDirection.StopQuotaTimeOrder,Math.Min(2,1+StopAttempts+StopUnavailableAttempts));
        if(BoundaryAttempts+BoundaryUnavailableAttempts<2) return new(MacroAlnsDirection.OrderTime,2);
        return null;
    }
    internal MacroRepairContext Observe(MacroAlnsAction action,int nativeStatus,int? polishStatus,bool effectiveOrders,bool effectiveStops)
    {
        var next=this with {PendingFeedback=false,FeedbackFollowups=FeedbackFollowups+(PendingFeedback?1:0),StopCoverageVersion=1};
        if(nativeStatus==0) return next; // Build failures consume a feedback attempt, never effective coverage.
        int credit=DirectedRepairRunner.IterationTimedOut(nativeStatus,polishStatus)?1:2;
        bool order=action.Direction==MacroAlnsDirection.OrderTime;
        bool stop=action.Direction==MacroAlnsDirection.StopQuotaTimeOrder;
        bool boundary=action.Scale==2 && action.Direction!=MacroAlnsDirection.TimeOnly;
        return next with {
            OrderAttempts=Math.Min(2,OrderAttempts+(order && effectiveOrders?credit:0)),
            StopAttempts=Math.Min(2,StopAttempts+(stop && effectiveStops && effectiveOrders?credit:0)),
            BoundaryAttempts=Math.Min(2,BoundaryAttempts+(boundary && effectiveOrders?credit:0)),
            OrderUnavailableAttempts=Math.Min(2,OrderUnavailableAttempts+(order && !effectiveOrders?1:0)),
            StopUnavailableAttempts=Math.Min(2,StopUnavailableAttempts+(stop && !(effectiveStops && effectiveOrders)?1:0)),
            BoundaryUnavailableAttempts=Math.Min(2,BoundaryUnavailableAttempts+(boundary && !effectiveOrders?1:0)) };
    }
    internal MacroRepairContext Feedback(DirectedIisGuidance? guidance,string reason="MacroIisFeedback")
    {
        if(guidance is null || guidance.ReleasedPairs.Count+guidance.StopIds.Count==0 && !guidance.RemoveTemporaryRadius) return this;
        bool novel=guidance.ReleasedPairs.Except(FeedbackPairs).Any() || guidance.StopIds.Except(FeedbackStops,StringComparer.OrdinalIgnoreCase).Any() ||
            guidance.RemoveTemporaryRadius && !OriginalTimeDomain;
        return this with { FeedbackPairs=FeedbackPairs.Concat(guidance.ReleasedPairs).Distinct().Order(StringComparer.Ordinal).ToArray(),
            FeedbackStops=FeedbackStops.Concat(guidance.StopIds).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            OriginalTimeDomain=OriginalTimeDomain || guidance.RemoveTemporaryRadius,
            PendingFeedback=(PendingFeedback || novel) && FeedbackFollowups<3,FeedbackReason=novel?reason:FeedbackReason };
    }
    internal MacroRepairContext Exhaust(string key,int status,int? polish,double? deficit,double? unsupported)
    {
        if(status!=Gurobi.GRB.Status.OPTIMAL || polish!=Gurobi.GRB.Status.OPTIMAL ||
            !(deficit>TimetableTimePolicy.NumericToleranceSeconds || unsupported>TimetableTimePolicy.NumericToleranceSeconds)) return this;
        return this with { ExhaustedSearches=(ExhaustedSearches??[]).Append(key).Distinct().TakeLast(256).ToArray() };
    }
}

internal static class MacroRepairDependencies
{
    internal static bool CoreStopsAdjustable(IReadOnlySet<string> related,IReadOnlySet<string> required,IReadOnlySet<string> actual,bool bounded)
        =>!bounded && related.Any(actual.Contains) && required.All(actual.Contains);
    internal static bool CoreOrdersAdjustable(IReadOnlySet<string> required,IReadOnlyDictionary<string,int> actual)=>required.Count==0?actual.Values.Any(n=>n>0):required.All(pair=>actual.GetValueOrDefault(pair)>0);
    internal static DirectedIisGuidance SeparateWaiting(DirectedDiagnosis diagnosis,IReadOnlyList<OvertakingStopProjection> projections,Func<OvertakingStopProjection,double> distance)
    {
        var pairs=new HashSet<string>(StringComparer.Ordinal);
        foreach(string stop in diagnosis.UnresolvedLongDwellStopIds)
        {
            var witness=projections.Where(p=>p.FirstStopId==stop || p.SecondStopId==stop).OrderBy(distance).ThenBy(p=>p.ProjectionId,StringComparer.Ordinal).FirstOrDefault();
            if(witness is not null) pairs.Add(DirectedFeasibilityRepair.Pair(witness.FirstTrainIndex,witness.SecondTrainIndex));
        }
        return new(pairs,diagnosis.UnresolvedLongDwellStopIds.ToHashSet(StringComparer.OrdinalIgnoreCase),false);
    }
    internal static QuotaDependencyExpansion ExpandStops(TimetablingInstance instance,MasterSolutionSnapshot candidate,IReadOnlyCollection<string> required,
        IEnumerable<string> optional,int maxStops,int maxTrains,Func<double>? remaining=null)
        =>QuotaDependencySupport.Expand(instance,candidate,required.Distinct(StringComparer.OrdinalIgnoreCase).Concat(optional),
            Math.Max(maxStops,required.Distinct(StringComparer.OrdinalIgnoreCase).Count()),
            Math.Max(maxTrains,required.Select(id=>instance.StopsById[id].TrainIndex).Distinct().Count()),remaining:remaining);
    internal static HashSet<string> Required(DirectedDiagnosis diagnosis,IEnumerable<string>? feedback=null)
    {
        var result=diagnosis.Conflicts.Select(c=>DirectedFeasibilityRepair.Pair(c.FirstTrainIndex,c.SecondTrainIndex)).ToHashSet(StringComparer.Ordinal);
        // A cap conflict records only its first witness. The entire physical aggregate must remain adjustable.
        var capStops=diagnosis.Conflicts.Where(c=>c.Kind=="overtaker_cap").SelectMany(c=>new[]{c.FirstStopId,c.SecondStopId}).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach(var p in diagnosis.Witnesses.Where(p=>capStops.Contains(p.FirstStopId)||capStops.Contains(p.SecondStopId)))
            result.Add(DirectedFeasibilityRepair.Pair(p.FirstTrainIndex,p.SecondTrainIndex));
        result.UnionWith(RelevantWaiting(diagnosis));
        if(feedback is not null) result.UnionWith(feedback);
        return result;
    }
    internal static string[] RelevantWaiting(DirectedDiagnosis diagnosis)
    {
        var core=diagnosis.Conflicts.SelectMany(c=>new[]{c.FirstTrainIndex,c.SecondTrainIndex}).ToHashSet();
        return diagnosis.Witnesses.Where(p=>core.Contains(p.FirstTrainIndex)||core.Contains(p.SecondTrainIndex)||
            diagnosis.UnresolvedLongDwellStopIds.Contains(p.FirstStopId)||diagnosis.UnresolvedLongDwellStopIds.Contains(p.SecondStopId))
            .OrderByDescending(p=>diagnosis.StopPressure.GetValueOrDefault(p.FirstStopId)+diagnosis.StopPressure.GetValueOrDefault(p.SecondStopId))
            .ThenBy(p=>p.FirstTrainIndex).ThenBy(p=>p.SecondTrainIndex)
            .Select(p=>DirectedFeasibilityRepair.Pair(p.FirstTrainIndex,p.SecondTrainIndex)).Distinct().ToArray();
    }
    internal static HashSet<string> FillWaiting(IReadOnlySet<string> required,IEnumerable<string> waiting,IEnumerable<string> selected,int nominalLimit)
        =>required.Order(StringComparer.Ordinal).Concat(waiting).Concat(selected).Distinct(StringComparer.Ordinal)
            .Take(Math.Max(required.Count,nominalLimit)).ToHashSet(StringComparer.Ordinal);
}
