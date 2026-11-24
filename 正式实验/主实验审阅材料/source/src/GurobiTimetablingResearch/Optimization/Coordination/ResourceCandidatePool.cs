namespace GurobiTimetablingResearch.Optimization;

internal sealed record ResourcePoolEntry(MasterSolutionSnapshot Snapshot,ResourceConflictProfile Profile);
internal sealed class ResourceCandidatePool(int capacity,bool phaseLexicographic=false)
{
    private List<ResourcePoolEntry> entries=[];
    internal int Count=>entries.Count;
    internal bool ContainsPattern(MasterSolutionSnapshot snapshot)
    {
        string pattern=ConflictGuidedQuotaExchange.Key(snapshot.StopByStopId);
        return entries.Any(e=>ConflictGuidedQuotaExchange.Key(e.Snapshot.StopByStopId)==pattern);
    }
    internal ResourcePoolEntry Best=>entries[0];
    internal ResourcePoolEntry Select(int round,int stagnation)=>entries[stagnation>=3 && round%4==0?(round/4)%entries.Count:0];
    /// <summary>Retain a complete candidate using resource severity and the common objective tie-break.</summary>
    /// <param name="snapshot">Candidate timetable with its raw service objective, if available.</param>
    /// <param name="profile">Completed resource diagnosis with violation duration in seconds.</param>
    /// <returns>Whether the candidate remains in the bounded search pool.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The pool capacity is not positive.</exception>
    internal bool Add(MasterSolutionSnapshot snapshot,ResourceConflictProfile profile)
    {
        if(capacity<1) throw new ArgumentOutOfRangeException(nameof(capacity));
        if(!profile.Complete || profile.UnknownStations!=0 || !double.IsFinite(profile.ViolationSeconds) || profile.ViolationSeconds<0) return false;
        string identity=SnapshotFingerprint.Calculate(snapshot);
        var existing=entries.FirstOrDefault(e=>SnapshotFingerprint.Calculate(e.Snapshot)==identity);
        if(existing is not null)
        {
            if(profile.ViolationSeconds>=existing.Profile.ViolationSeconds-1e-6) return false;
            entries.Remove(existing);
        }
        entries.Add(new(snapshot,profile));
        var sorted=entries.OrderBy(e=>e.Profile.ViolationSeconds)
            .ThenBy(e=>phaseLexicographic?0:e.Profile.Stations.Values.Count(s=>s.Status!=MicroSolveStatus.Feasible))
            .ThenBy(e => e.Snapshot.ServiceObjective is { } objective ? ResourceSpanObjectivePolicy.Key(objective) : default).ToList();
        entries=sorted.Take(Math.Min(3,capacity)).ToList();
        foreach(var entry in sorted.Skip(entries.Count).Where(e=>e.Profile.ViolationSeconds<=sorted[0].Profile.ViolationSeconds*1.15+60))
        {
            if(entries.Count>=capacity) break;
            string pattern=ConflictGuidedQuotaExchange.Key(entry.Snapshot.StopByStopId);
            if(entries.Any(e=>ConflictGuidedQuotaExchange.Key(e.Snapshot.StopByStopId)==pattern)) continue;
            entries.Add(entry);
        }
        return entries.Any(e=>SnapshotFingerprint.Calculate(e.Snapshot)==identity);
    }
}
