namespace GurobiTimetablingResearch.Optimization;

/// <summary>
/// Resolves the stop-state context represented by retained outside Micro patterns.
/// A retained pattern is conditional on its own stop/pass choices, so those choices
/// replace the older Macro start only outside the actively rebuilt stations.
/// </summary>
public static class DynamicHotspotOutsideStopPolicy
{
    public static IReadOnlyDictionary<string, int> Resolve(
        IReadOnlyDictionary<string, int> macroReferenceStops,
        IReadOnlyCollection<string> activeStationNames,
        IReadOnlyList<MicroWitnessCertificate> retainedPatterns)
    {
        ArgumentNullException.ThrowIfNull(macroReferenceStops);
        ArgumentNullException.ThrowIfNull(activeStationNames);
        ArgumentNullException.ThrowIfNull(retainedPatterns);
        HashSet<string> active = activeStationNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> result = new(macroReferenceStops, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> ownerByStop = new(StringComparer.OrdinalIgnoreCase);

        foreach (MicroWitnessCertificate pattern in retainedPatterns)
        {
            if (active.Contains(pattern.StationName))
                throw new InvalidDataException(
                    $"Retained pattern '{pattern.CertificateId}' overlaps active station '{pattern.StationName}'.");
            Dictionary<string, MicroCertificateCondition> stopConditions = pattern.Conditions
                .Where(condition => condition.Kind == MicroCertificateConditionKind.StopMode)
                .GroupBy(condition => condition.LaterStopId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.Count() == 1
                        ? group.Single()
                        : throw new InvalidDataException(
                            $"Pattern '{pattern.CertificateId}' has duplicate stop conditions for '{group.Key}'."),
                    StringComparer.OrdinalIgnoreCase);
            foreach (VerifiedMicroAssignmentWitness witness in pattern.Witnesses)
            {
                if (!result.ContainsKey(witness.RequirementId))
                    throw new InvalidDataException(
                        $"Pattern '{pattern.CertificateId}' references unknown stop '{witness.RequirementId}'.");
                if (!stopConditions.TryGetValue(witness.RequirementId, out MicroCertificateCondition? condition))
                    throw new InvalidDataException(
                        $"Pattern '{pattern.CertificateId}' has no stop condition for '{witness.RequirementId}'.");
                int required = witness.IsStop ? 1 : 0;
                if (condition.RequiredStopValue != required)
                    throw new InvalidDataException(
                        $"Pattern '{pattern.CertificateId}' witness/condition disagree at '{witness.RequirementId}'.");
                if (ownerByStop.TryGetValue(witness.RequirementId, out string? owner) &&
                    result[witness.RequirementId] != required)
                    throw new InvalidDataException(
                        $"Retained patterns '{owner}' and '{pattern.CertificateId}' disagree at '{witness.RequirementId}'.");
                ownerByStop[witness.RequirementId] = pattern.CertificateId;
                result[witness.RequirementId] = required;
            }
        }
        return result;
    }
}
