namespace GurobiTimetablingResearch.Optimization;

// Search ranking only: this is neither distance to feasibility nor a bound.
internal sealed record DirectedJointProgress(int FailedStations,int UnknownStations,int ExactConflicts,int WitnessViolations,bool CompleteScan)
{
    internal bool IsBetterThan(DirectedJointProgress? other)
    {
        if(!CompleteScan || UnknownStations!=0) return false;
        if(other is null || !other.CompleteScan || other.UnknownStations!=0) return true;
        return (FailedStations,ExactConflicts,WitnessViolations).CompareTo((other.FailedStations,other.ExactConflicts,other.WitnessViolations))<0;
    }
}

internal static class DirectedFeedbackPriority
{
    internal static int LastPromotion(string station,IReadOnlyList<PromotedMicroComponent>? retained)=>
        (retained ?? []).Where(c=>c.StationName.Equals(station,StringComparison.OrdinalIgnoreCase))
            .Select(c=>c.PromotionIteration).DefaultIfEmpty(-1).Max();
}
