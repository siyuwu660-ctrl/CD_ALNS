using System.Text;
using System.Text.Json;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

internal sealed record MicroPatternReconstructionBundle(
    int FormatVersion,
    string ReconstructionLabel,
    bool IsDirectedReconstruction,
    DateTimeOffset CreatedAt,
    string InstanceName,
    string MacroCandidateFingerprint,
    string MacroSnapshotPath,
    string DataVersion,
    string RuleVersion,
    string CodeIdentity,
    double RunningTimeSupplementRatio,
    int EndpointOccupationSeconds,
    string PatternCombinationIdentity,
    string[] RebuiltStations,
    string[] AuxiliaryDefinitions,
    MicroWitnessCertificate[] Patterns)
{
    internal static MicroPatternReconstructionBundle Create(
        string reconstructionLabel,
        TimetablingInstance instance,
        MasterSolutionSnapshot macroCandidate,
        string macroSnapshotPath,
        string dataVersion,
        string ruleVersion,
        string codeIdentity,
        double runningTimeSupplementRatio,
        int endpointOccupationSeconds,
        IReadOnlyList<string> rebuiltStations,
        IReadOnlyList<MicroWitnessCertificate> patterns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reconstructionLabel);
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(macroCandidate);
        ArgumentNullException.ThrowIfNull(rebuiltStations);
        ArgumentNullException.ThrowIfNull(patterns);
        MicroWitnessCertificate[] ownedPatterns = patterns
            .OrderBy(pattern => pattern.StationName, StringComparer.OrdinalIgnoreCase)
            .Select(pattern => pattern with
            {
                Witnesses = pattern.Witnesses.ToArray(),
                Conditions = pattern.Conditions.ToArray()
            }).ToArray();
        string[] duplicateStations = ownedPatterns.GroupBy(
                pattern => pattern.StationName, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() != 1).Select(group => group.Key).ToArray();
        if (duplicateStations.Length > 0)
            throw new InvalidDataException(
                $"Pattern bundle contains duplicate station certificates: {string.Join('|', duplicateStations)}.");
        MicroPatternReconstructionBundleStore.ValidatePatternCandidateGroups(ownedPatterns, macroCandidate);
        return new(
            1,
            reconstructionLabel,
            true,
            DateTimeOffset.Now,
            instance.Meta.InstanceName,
            SnapshotFingerprint.Calculate(macroCandidate),
            Path.GetFullPath(macroSnapshotPath),
            dataVersion,
            ruleVersion,
            codeIdentity,
            runningTimeSupplementRatio,
            endpointOccupationSeconds,
            MicroPatternCombinationIdentity.Calculate(ownedPatterns),
            rebuiltStations.Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            [
                "all original binary stop/precedence/maintenance/quota states are fixed by equality rows and then represented as continuous variables",
                "shared stop-pair product variables retain the exact three-row binary-product envelope from the compact Macro builder",
                "reversal and beingOvertaken remain continuous auxiliaries with their complete defining equalities/inequalities and dwell implications",
                "quota category auxiliaries are fixed to the saved candidate category in the fixed-discrete continuous model; closed windows use independent recount; outside branches meet at the boundary without a one-second exclusion band",
                "absolute-retiming deviation auxiliaries have lower bound zero and no finite upper bound"
            ],
            ownedPatterns);
    }
}

internal static class MicroPatternReconstructionBundleStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    internal static void Save(string path, MicroPatternReconstructionBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        BundlePayload payload = ToPayload(bundle);
        File.WriteAllText(fullPath, JsonSerializer.Serialize(payload, JsonOptions), new UTF8Encoding(false));
    }

    internal static MicroPatternReconstructionBundle Load(
        string path,
        TimetablingInstance instance,
        MasterSolutionSnapshot macroCandidate)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(macroCandidate);
        BundlePayload payload = JsonSerializer.Deserialize<BundlePayload>(
            File.ReadAllText(Path.GetFullPath(path), Encoding.UTF8), JsonOptions)
            ?? throw new InvalidDataException("Micro pattern reconstruction bundle is empty or invalid.");
        if (payload.FormatVersion != 1)
            throw new InvalidDataException($"Unsupported Micro pattern bundle format {payload.FormatVersion}.");
        if (!string.Equals(payload.InstanceName, instance.Meta.InstanceName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"Pattern bundle instance '{payload.InstanceName}' does not match '{instance.Meta.InstanceName}'.");
        if (!string.Equals(payload.MacroCandidateFingerprint, SnapshotFingerprint.Calculate(macroCandidate),
                StringComparison.Ordinal))
            throw new InvalidDataException("Pattern bundle Macro candidate fingerprint does not match the loaded snapshot.");
        MicroPatternReconstructionBundle bundle = FromPayload(payload);
        string actualIdentity = MicroPatternCombinationIdentity.Calculate(bundle.Patterns);
        if (!string.Equals(actualIdentity, bundle.PatternCombinationIdentity, StringComparison.Ordinal))
            throw new InvalidDataException(
                $"Pattern combination identity mismatch: saved={bundle.PatternCombinationIdentity}, actual={actualIdentity}.");
        ValidatePatternCandidateGroups(bundle.Patterns, macroCandidate);
        return bundle;
    }

    /// <summary>
    /// A locally generated pattern is certified against the station solver's retimed candidate,
    /// not necessarily against the source Macro timetable.  Certificates created by the same
    /// joint station solve share one candidate fingerprint, so replay every such group together
    /// before checking its fingerprint and conditions.  This is essential for two-station modes:
    /// neither station certificate alone contains all retimed events of the joint candidate.
    /// </summary>
    internal static void ValidatePatternCandidateGroups(
        IReadOnlyList<MicroWitnessCertificate> patterns,
        MasterSolutionSnapshot macroCandidate)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        ArgumentNullException.ThrowIfNull(macroCandidate);
        foreach (IGrouping<string, MicroWitnessCertificate> group in patterns.GroupBy(
                     pattern => pattern.CandidateSnapshotFingerprint,
                     StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(group.Key))
                throw new InvalidDataException("Pattern candidate fingerprint is empty.");

            Dictionary<string, double> arrivals = new(
                macroCandidate.ArrivalSecondsByStopId, StringComparer.OrdinalIgnoreCase);
            Dictionary<string, double> departures = new(
                macroCandidate.DepartureSecondsByStopId, StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> stops = new(
                macroCandidate.StopByStopId, StringComparer.OrdinalIgnoreCase);
            foreach (VerifiedMicroAssignmentWitness witness in group.SelectMany(pattern => pattern.Witnesses))
            {
                if (!witness.ArrivalSeconds.HasValue || !witness.DepartureSeconds.HasValue)
                    throw new InvalidDataException(
                        $"Pattern witness '{witness.RequirementId}' has no reconstructible event times.");
                arrivals[witness.RequirementId] = witness.ArrivalSeconds.Value;
                departures[witness.RequirementId] = witness.DepartureSeconds.Value;
                // A reconstructed local mode includes its stop/pass decision.  Omitting it
                // made a perfectly saved flexible-stop pattern fail its own snapshot hash,
                // and, more importantly, would not be enough to rebuild service conditions.
                stops[witness.RequirementId] = witness.IsStop ? 1 : 0;
            }

            MasterSolutionSnapshot replayed = macroCandidate with
            {
                ArrivalSecondsByStopId = arrivals,
                DepartureSecondsByStopId = departures,
                StopByStopId = stops,
                VerifiedMicroAssignments = group.SelectMany(pattern => pattern.Witnesses).ToArray()
            };
            string replayedFingerprint = SnapshotFingerprint.Calculate(replayed);
            if (!string.Equals(group.Key, replayedFingerprint, StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"Pattern candidate replay mismatch: saved={group.Key}, replayed={replayedFingerprint}.");

            MicroWitnessCertificate? invalid = group.FirstOrDefault(pattern =>
                pattern.Evaluate(replayed) != CertificateValidityOutcome.Valid);
            if (invalid is not null)
                throw new InvalidDataException(
                    $"Pattern '{invalid.CertificateId}' is invalid on its reconstructed local candidate.");
        }
    }

    /// <summary>
    /// Rebinds a retained certificate group to the smallest snapshot reconstructible from
    /// the supplied Macro base plus that group's own witnesses.  This is needed when one
    /// member of a historically joint-generated group is replaced while its sibling mode
    /// remains unchanged.  Resources, event-order conditions, stop conditions and times
    /// are not changed; only the full-snapshot provenance fingerprint is made standalone.
    /// </summary>
    internal static IReadOnlyList<MicroWitnessCertificate> RebaseToReconstructibleGroups(
        MasterSolutionSnapshot macroCandidate,
        IReadOnlyList<MicroWitnessCertificate> patterns)
    {
        ArgumentNullException.ThrowIfNull(macroCandidate);
        ArgumentNullException.ThrowIfNull(patterns);
        List<MicroWitnessCertificate> result = [];
        foreach (IGrouping<string, MicroWitnessCertificate> group in patterns.GroupBy(
                     pattern => pattern.CandidateSnapshotFingerprint, StringComparer.Ordinal))
        {
            Dictionary<string, double> arrivals = new(
                macroCandidate.ArrivalSecondsByStopId, StringComparer.OrdinalIgnoreCase);
            Dictionary<string, double> departures = new(
                macroCandidate.DepartureSecondsByStopId, StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> stops = new(
                macroCandidate.StopByStopId, StringComparer.OrdinalIgnoreCase);
            VerifiedMicroAssignmentWitness[] witnesses = group.SelectMany(pattern => pattern.Witnesses).ToArray();
            foreach (VerifiedMicroAssignmentWitness witness in witnesses)
            {
                if (!witness.ArrivalSeconds.HasValue || !witness.DepartureSeconds.HasValue)
                    throw new InvalidDataException(
                        $"Pattern witness '{witness.RequirementId}' cannot be rebound without saved event times.");
                arrivals[witness.RequirementId] = witness.ArrivalSeconds.Value;
                departures[witness.RequirementId] = witness.DepartureSeconds.Value;
                stops[witness.RequirementId] = witness.IsStop ? 1 : 0;
            }
            MasterSolutionSnapshot replayed = macroCandidate with
            {
                ArrivalSecondsByStopId = arrivals,
                DepartureSecondsByStopId = departures,
                StopByStopId = stops,
                VerifiedMicroAssignments = witnesses
            };
            foreach (MicroWitnessCertificate pattern in group)
                if (pattern.Evaluate(replayed) != CertificateValidityOutcome.Valid)
                    throw new InvalidDataException(
                        $"Pattern '{pattern.CertificateId}' cannot be independently rebound without changing its conditions.");
            string fingerprint = SnapshotFingerprint.Calculate(replayed);
            foreach (MicroWitnessCertificate pattern in group)
                result.Add(pattern with
                {
                    CandidateSnapshotFingerprint = fingerprint,
                    Witnesses = pattern.Witnesses.Select(witness => witness with
                    {
                        CandidateSnapshotFingerprint = fingerprint
                    }).ToArray(),
                    Conditions = pattern.Conditions.ToArray()
                });
        }
        return result.OrderBy(pattern => pattern.StationName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static BundlePayload ToPayload(MicroPatternReconstructionBundle bundle) => new(
        bundle.FormatVersion,
        bundle.ReconstructionLabel,
        bundle.IsDirectedReconstruction,
        bundle.CreatedAt,
        bundle.InstanceName,
        bundle.MacroCandidateFingerprint,
        bundle.MacroSnapshotPath,
        bundle.DataVersion,
        bundle.RuleVersion,
        bundle.CodeIdentity,
        bundle.RunningTimeSupplementRatio,
        bundle.EndpointOccupationSeconds,
        bundle.PatternCombinationIdentity,
        bundle.RebuiltStations,
        bundle.AuxiliaryDefinitions,
        bundle.Patterns.Select(pattern => new PatternPayload(
            pattern.CertificateId,
            pattern.StationName,
            pattern.CandidateSnapshotFingerprint,
            pattern.DataVersion,
            pattern.RuleVersion,
            pattern.CheckerToleranceSeconds,
            pattern.Witnesses.ToArray(),
            pattern.Conditions.ToArray())).ToArray());

    private static MicroPatternReconstructionBundle FromPayload(BundlePayload payload) => new(
        payload.FormatVersion,
        payload.ReconstructionLabel,
        payload.IsDirectedReconstruction,
        payload.CreatedAt,
        payload.InstanceName,
        payload.MacroCandidateFingerprint,
        payload.MacroSnapshotPath,
        payload.DataVersion,
        payload.RuleVersion,
        payload.CodeIdentity,
        payload.RunningTimeSupplementRatio,
        payload.EndpointOccupationSeconds,
        payload.PatternCombinationIdentity,
        payload.RebuiltStations,
        payload.AuxiliaryDefinitions,
        payload.Patterns.Select(pattern => new MicroWitnessCertificate(
            pattern.CertificateId,
            pattern.StationName,
            pattern.CandidateSnapshotFingerprint,
            pattern.DataVersion,
            pattern.RuleVersion,
            pattern.CheckerToleranceSeconds,
            pattern.Witnesses,
            pattern.Conditions)).ToArray());

    private sealed record BundlePayload(
        int FormatVersion,
        string ReconstructionLabel,
        bool IsDirectedReconstruction,
        DateTimeOffset CreatedAt,
        string InstanceName,
        string MacroCandidateFingerprint,
        string MacroSnapshotPath,
        string DataVersion,
        string RuleVersion,
        string CodeIdentity,
        double RunningTimeSupplementRatio,
        int EndpointOccupationSeconds,
        string PatternCombinationIdentity,
        string[] RebuiltStations,
        string[] AuxiliaryDefinitions,
        PatternPayload[] Patterns);

    private sealed record PatternPayload(
        string CertificateId,
        string StationName,
        string CandidateSnapshotFingerprint,
        string DataVersion,
        string RuleVersion,
        int CheckerToleranceSeconds,
        VerifiedMicroAssignmentWitness[] Witnesses,
        MicroCertificateCondition[] Conditions);
}
