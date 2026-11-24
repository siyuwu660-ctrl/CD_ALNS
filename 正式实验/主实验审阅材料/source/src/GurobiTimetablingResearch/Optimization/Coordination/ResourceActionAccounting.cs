namespace GurobiTimetablingResearch.Optimization;

internal sealed record ResourceActionObservation(double Reward,double Seconds);

internal static class ResourceActionAccounting
{
    internal static ResourceActionObservation LocalObservation(double reward,double totalSeconds,
        double secondsBeforeRecovery,double externalSeconds,bool candidateFromRecovery)
        => new(candidateFromRecovery?0:reward,Math.Max(.001,candidateFromRecovery
            ?secondsBeforeRecovery:totalSeconds-externalSeconds));
}

internal sealed record ResourceBoundaryRecoveryAttempt(bool Attempted,bool Applied,double Seconds,string Status,
    string? SourceFingerprint=null,string? ResultFingerprint=null);
