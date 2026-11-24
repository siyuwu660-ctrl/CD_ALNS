namespace GurobiTimetablingResearch.Optimization;

internal enum RetimingAttemptOutcome { Unfinished, BuildBudgetExpired, AllObjectivesOptimal, SourceInconsistent }

/// <summary>Run-local accounting; entries never cross data, configuration or objective contexts.</summary>
internal sealed class RetimingAttemptPolicy
{
    private sealed record Attempt(int Round,double Allowance,int Count,RetimingAttemptOutcome Outcome);
    private readonly Dictionary<string,Attempt> sources = new(StringComparer.Ordinal);
    private readonly Dictionary<string,Attempt> structures = new(StringComparer.Ordinal);
    private readonly Dictionary<string,string> sourceStructures = new(StringComparer.Ordinal);
    internal static double AvailableAllowance(double totalSeconds,double searchRemaining,double usedSeconds)
        => Math.Max(0,Math.Min(searchRemaining*.2,totalSeconds*.05-usedSeconds));
    private double observedBuildSeconds,observedVerificationSeconds;
    internal void ObserveBuild(double seconds) => observedBuildSeconds=Math.Max(observedBuildSeconds,seconds);
    internal void ObserveVerification(double seconds) => observedVerificationSeconds=Math.Max(observedVerificationSeconds,seconds);
    internal double VerificationReserve(double allowance) => Math.Max(allowance*.25,observedVerificationSeconds*1.25);
    internal bool CanFitAttempt(double allowance)
        => allowance>observedBuildSeconds*1.25+VerificationReserve(allowance)+allowance*.1;
    internal bool CanAttemptSource(string key,int round,double allowance,out string reason)
    {
        if(!CanAttempt(sources,key,round,allowance,out reason))return false;
        return !sourceStructures.TryGetValue(key,out string? structure) ||
            CanAttempt(structures,structure,round,allowance,out reason);
    }
    internal bool CanAttemptStructure(string key,int round,double allowance,out string reason)
        => CanAttempt(structures,key,round,allowance,out reason);
    internal void Record(string source,string? structure,int round,double allowance,RetimingAttemptOutcome outcome,bool scalarAttempted)
    {
        // Only the fully constructed model supplies this identity, including certificate
        // conditions and auxiliary branches. Never infer it from a coarse timetable key.
        if(structure is not null)sourceStructures[source]=structure;
        Save(sources,source,round,allowance,outcome);
        // A source reconstruction failure says nothing about other timetables of this structure.
        if(scalarAttempted && structure is not null && outcome!=RetimingAttemptOutcome.SourceInconsistent)
            Save(structures,structure,round,allowance,outcome);
    }
    private static bool CanAttempt(Dictionary<string,Attempt> history,string key,int round,double allowance,out string reason)
    {
        if(!double.IsFinite(allowance) || allowance<=0)throw new ArgumentOutOfRangeException(nameof(allowance));
        if(!history.TryGetValue(key,out var previous)){reason="New";return true;}
        if(previous.Outcome==RetimingAttemptOutcome.AllObjectivesOptimal){reason="AllObjectivesOptimal";return false;}
        if(previous.Outcome==RetimingAttemptOutcome.SourceInconsistent){reason="SourceConsistencyFailure";return false;}
        if(previous.Count>=3){reason="BoundedRetryLimit";return false;}
        int elapsed=round-previous.Round;
        if(elapsed<3){reason="Cooldown";return false;}
        if(allowance>previous.Allowance*1.25){reason="LargerBudgetRetry";return true;}
        // Cooling down cannot make an allowance that did not even build the model usable.
        if(previous.Outcome==RetimingAttemptOutcome.BuildBudgetExpired){reason="ConstructionNeedsLargerBudget";return false;}
        // The runner's available budget decreases as work is charged. After cooling down,
        // a smaller usable allowance must not permanently blacklist unfinished work.
        // The caller still enforces its minimum allowance and total budget; Count caps retries.
        if(elapsed>=6){reason=allowance<previous.Allowance?"CooledReducedBudgetRetry":"CooledRetry";return true;}
        reason="AwaitingBudgetOrCooldown";return false;
    }
    private static void Save(Dictionary<string,Attempt> history,string key,int round,double allowance,RetimingAttemptOutcome outcome)
        => history[key]=new(round,allowance,history.TryGetValue(key,out var previous)?previous.Count+1:1,outcome);
}
