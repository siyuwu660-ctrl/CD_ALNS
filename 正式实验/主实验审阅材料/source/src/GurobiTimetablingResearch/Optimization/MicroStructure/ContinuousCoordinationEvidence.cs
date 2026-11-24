using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Gurobi;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

internal enum ContinuousProofAvailability
{
    HardProof,
    NoInfeasibilityProof,
    ProofMappingFailed
}

internal sealed record ContinuousCoordinationEvidenceBinding(
    string CoordinationModelId,
    string MacroCandidateFingerprint,
    string PatternCombinationIdentity,
    string MathematicalModelIdentity,
    string SolveAttemptId,
    string SolveResultIdentity,
    int StatusCode,
    ContinuousProofAvailability ProofAvailability,
    string ProofAvailabilityReason,
    IReadOnlyList<string> PatternCertificateIds,
    IReadOnlyList<ContinuousProofRow> ProofRows)
{
    internal static ContinuousCoordinationEvidenceBinding Create(
        string coordinationModelId,
        string macroCandidateFingerprint,
        IReadOnlyList<MicroWitnessCertificate> patterns,
        int statusCode,
        IEnumerable<ContinuousProofRow> proofRows,
        string solveAttemptId,
        string modelConfigurationIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(coordinationModelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(macroCandidateFingerprint);
        ArgumentNullException.ThrowIfNull(patterns);
        ArgumentNullException.ThrowIfNull(proofRows);
        ArgumentException.ThrowIfNullOrWhiteSpace(solveAttemptId);
        ContinuousProofRow[] ownedRows = proofRows.Select(row => row with { }).ToArray();
        string[] certificateIds = patterns.Select(pattern => pattern.CertificateId)
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        string patternIdentity = MicroPatternCombinationIdentity.Calculate(patterns);
        string mathematicalIdentity = Hash(string.Join('\n',
            coordinationModelId,
            macroCandidateFingerprint,
            patternIdentity,
            modelConfigurationIdentity));
        ContinuousProofAvailability availability = statusCode != GRB.Status.INFEASIBLE
            ? ContinuousProofAvailability.NoInfeasibilityProof
            : ownedRows.Length == 0
                ? ContinuousProofAvailability.ProofMappingFailed
                : ContinuousProofAvailability.HardProof;
        string reason = availability switch
        {
            ContinuousProofAvailability.HardProof => "infeasible_status_with_model_local_proof_rows",
            ContinuousProofAvailability.NoInfeasibilityProof => $"status_{statusCode}_does_not_prove_infeasibility",
            _ => "infeasible_status_but_no_proof_rows_were_mapped"
        };
        string solveIdentity = Hash(string.Join('\n',
            mathematicalIdentity,
            solveAttemptId,
            statusCode.ToString(),
            reason,
            string.Join('|', ownedRows.Select(row =>
                $"{row.ConstraintName}:{row.EvidenceKind}:{row.BusinessKind}:{row.BusinessId}:" +
                $"{row.FarkasDual:R}:{row.ProofIdentity}:{row.ProofKind}:" +
                string.Join(',', (row.RequiredDiscreteLiterals ?? new Dictionary<string, int>())
                    .OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => $"{item.Key}={item.Value}")) + ":" +
                string.Join(',', (row.RequiredPatternsByStation ?? new Dictionary<string, string>())
                    .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase).Select(item => $"{item.Key}={item.Value}"))))));
        return new(
            coordinationModelId,
            macroCandidateFingerprint,
            patternIdentity,
            mathematicalIdentity,
            solveAttemptId,
            solveIdentity,
            statusCode,
            availability,
            reason,
            certificateIds,
            ownedRows);
    }

    private static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}

internal static class MicroPatternCombinationIdentity
{
    internal static string Calculate(IReadOnlyList<MicroWitnessCertificate> patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        StringBuilder payload = new();
        foreach (MicroWitnessCertificate pattern in patterns.OrderBy(
                     pattern => pattern.StationName, StringComparer.OrdinalIgnoreCase))
        {
            payload.AppendLine(pattern.CertificateId);
            payload.AppendLine(pattern.StationName);
            payload.AppendLine(pattern.CandidateSnapshotFingerprint);
            payload.AppendLine(pattern.DataVersion);
            payload.AppendLine(pattern.RuleVersion);
            payload.AppendLine(pattern.CheckerToleranceSeconds.ToString());
            foreach (VerifiedMicroAssignmentWitness witness in pattern.Witnesses.OrderBy(
                         witness => witness.RequirementId, StringComparer.OrdinalIgnoreCase))
                payload.AppendLine(witness.ToString());
            foreach (MicroCertificateCondition condition in pattern.Conditions.OrderBy(
                         condition => condition.ConditionId, StringComparer.OrdinalIgnoreCase))
                payload.AppendLine(condition.ToString());
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload.ToString())))
            .ToLowerInvariant();
    }
}

internal sealed record MacroRepairCoordinationBinding(
    string MacroRepairCandidateId,
    IReadOnlyList<string> ActiveStations,
    MicroStationPairSelection? Pair,
    ContinuousCoordinationEvidenceBinding Evidence,
    IReadOnlyList<MicroWitnessCertificate> Patterns)
{
    internal static MacroRepairCoordinationBinding Create(
        string macroRepairCandidateId,
        IReadOnlyList<string> activeStations,
        MicroStationPairSelection? pair,
        ContinuousCoordinationEvidenceBinding evidence,
        IReadOnlyList<MicroWitnessCertificate> patterns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(macroRepairCandidateId);
        ArgumentNullException.ThrowIfNull(activeStations);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(patterns);
        string patternIdentity = MicroPatternCombinationIdentity.Calculate(patterns);
        if (!string.Equals(patternIdentity, evidence.PatternCombinationIdentity, StringComparison.Ordinal))
            throw new InvalidDataException(
                $"Macro repair candidate '{macroRepairCandidateId}' was paired with the wrong Micro pattern combination: " +
                $"evidence={evidence.PatternCombinationIdentity}, actual={patternIdentity}.");
        return new(
            macroRepairCandidateId,
            activeStations.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            pair,
            evidence with
            {
                PatternCertificateIds = evidence.PatternCertificateIds.ToArray(),
                ProofRows = evidence.ProofRows.Select(row => row with { }).ToArray()
            },
            patterns.OrderBy(pattern => pattern.StationName, StringComparer.OrdinalIgnoreCase)
                .Select(pattern => pattern with
                {
                    Witnesses = pattern.Witnesses.ToArray(),
                    Conditions = pattern.Conditions.ToArray()
                }).ToArray());
    }
}

internal enum ProofDecisionMappingStatus
{
    AdjustableMainDecision,
    PatternReplacement,
    DerivedAuxiliaryRecomputed,
    OriginalBusinessHard,
    OutsideMacroScope,
    Unmapped
}

internal sealed record ProofMappedDecision(
    string DecisionFamily,
    string DecisionKey,
    int? TrainIndex,
    int? StationOrder,
    string StationName,
    bool IsAdjustable,
    string Reason);

internal sealed record ContinuousProofBusinessMapping(
    string ConstraintName,
    string EvidenceKind,
    string BusinessKind,
    ProofDecisionMappingStatus Status,
    IReadOnlyList<ProofMappedDecision> Decisions,
    string Reason);

internal static partial class ContinuousProofDependencyMapper
{
    [GeneratedRegex(@"(?:^|_)t(?<train>\d+)_from(?<from>\d+)_to(?<to>\d+)(?:_|$)", RegexOptions.IgnoreCase)]
    private static partial Regex RuntimeRegex();

    [GeneratedRegex(@"(?:^|_)t(?<train>\d+)_s(?<order>\d+)(?:_|$)", RegexOptions.IgnoreCase)]
    private static partial Regex TrainStopRegex();

    internal static IReadOnlyList<ContinuousProofBusinessMapping> Map(
        TimetablingInstance instance,
        IReadOnlyList<string> activeStations,
        ContinuousCoordinationEvidenceBinding evidence,
        IReadOnlyList<MicroWitnessCertificate> patterns)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(activeStations);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(patterns);
        return Map(instance, activeStations, evidence.ProofRows, patterns);
    }

    internal static IReadOnlyList<ContinuousProofBusinessMapping> Map(
        TimetablingInstance instance,
        IReadOnlyList<string> activeStations,
        IReadOnlyList<ContinuousProofRow> proofRows,
        IReadOnlyList<MicroWitnessCertificate> patterns)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(activeStations);
        ArgumentNullException.ThrowIfNull(proofRows);
        ArgumentNullException.ThrowIfNull(patterns);
        HashSet<string> active = activeStations.ToHashSet(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<SectionConflictCandidate> sections = new InitialConflictGraphBuilder(
            instance, int.MaxValue).Build();
        return proofRows.Select(row => MapRow(instance, active, row, patterns, sections)).ToArray();
    }

    private static ContinuousProofBusinessMapping MapRow(
        TimetablingInstance instance,
        IReadOnlySet<string> activeStations,
        ContinuousProofRow row,
        IReadOnlyList<MicroWitnessCertificate> patterns,
        IReadOnlyList<SectionConflictCandidate> sections)
    {
        foreach (MicroWitnessCertificate pattern in patterns)
        for (int index = 0; index < pattern.Conditions.Count; index++)
        {
            if (!string.Equals(row.ConstraintName,
                    MicroCertificateModelBinder.BuildConstraintName(pattern.CertificateId, index),
                    StringComparison.OrdinalIgnoreCase)) continue;
            MicroCertificateCondition condition = pattern.Conditions[index];
            if (condition.Kind == MicroCertificateConditionKind.StopMode)
                return FromStops(instance, activeStations, row, [condition.LaterStopId],
                    "micro_pattern_stop_condition");
            ProofMappedDecision mode = new(
                "micro_pattern",
                pattern.CertificateId,
                null,
                null,
                pattern.StationName,
                activeStations.Contains(pattern.StationName),
                activeStations.Contains(pattern.StationName)
                    ? $"replace_pattern_condition:{condition.ConditionId}"
                    : $"pattern_condition_outside_active_scope:{condition.ConditionId}");
            return new(
                row.ConstraintName,
                row.EvidenceKind,
                row.BusinessKind,
                mode.IsAdjustable ? ProofDecisionMappingStatus.PatternReplacement : ProofDecisionMappingStatus.OutsideMacroScope,
                [mode],
                mode.Reason);
        }

        // Materialized bounds are the original mathematical domain of a variable.  A bound row
        // may contain a train/from/to token, but that does not turn it into a releasable stop or
        // precedence decision.
        if (row.ConstraintName.StartsWith("fd_bound_", StringComparison.OrdinalIgnoreCase))
            return new(row.ConstraintName, row.EvidenceKind, row.BusinessKind,
                ProofDecisionMappingStatus.OriginalBusinessHard, [],
                "explicit_row_materializes_an_existing_variable_bound_and_is_not_independently_released");

        if (row.ConstraintName.StartsWith("fd_fix_quota_", StringComparison.OrdinalIgnoreCase))
        {
            Match quotaStop = TrainStopRegex().Match(row.ConstraintName);
            if (quotaStop.Success)
            {
                int train = int.Parse(quotaStop.Groups["train"].Value);
                int order = int.Parse(quotaStop.Groups["order"].Value);
                TrainStopRecord? stop = instance.TrainStops.FirstOrDefault(item =>
                    item.TrainIndex == train && item.StationOrder == order);
                if (stop is not null)
                {
                    ContinuousProofBusinessMapping mapped = FromStops(
                        instance, activeStations, row, [stop.StopId],
                        "quota_category_recomputed_from_stop_and_continuous_event_time");
                    return mapped with { Status = ProofDecisionMappingStatus.DerivedAuxiliaryRecomputed };
                }
            }
            return new(row.ConstraintName, row.EvidenceKind, row.BusinessKind,
                ProofDecisionMappingStatus.DerivedAuxiliaryRecomputed, [],
                "quota_category_is_recomputed_from_stop_and_continuous_event_time_in_macro_repair");
        }

        Match runtime = RuntimeRegex().Match(row.ConstraintName);
        if (runtime.Success)
        {
            int train = int.Parse(runtime.Groups["train"].Value);
            int from = int.Parse(runtime.Groups["from"].Value);
            int to = int.Parse(runtime.Groups["to"].Value);
            string[] stopIds = instance.TrainStops.Where(stop =>
                    stop.TrainIndex == train && (stop.StationOrder == from || stop.StationOrder == to))
                .Select(stop => stop.StopId).ToArray();
            return FromStops(instance, activeStations, row, stopIds, "runtime_endpoint_stop_dependency");
        }

        Match trainStop = TrainStopRegex().Match(row.ConstraintName);
        if (trainStop.Success)
        {
            int train = int.Parse(trainStop.Groups["train"].Value);
            int order = int.Parse(trainStop.Groups["order"].Value);
            TrainStopRecord? stop = instance.TrainStops.FirstOrDefault(item =>
                item.TrainIndex == train && item.StationOrder == order);
            if (stop is not null)
                return FromStops(instance, activeStations, row, [stop.StopId], "direct_train_stop_dependency");
        }

        SectionConflictCandidate? section = sections.FirstOrDefault(candidate => row.ConstraintName.Contains(
                $"prec_{BasicMilpModelBuilder.BuildSectionConstraintBaseName(candidate)}",
                StringComparison.OrdinalIgnoreCase));
        if (section is not null)
        {
            string key = BasicMilpModelBuilder.BuildSectionPrecedenceKey(section);
            string[] stations = instance.SectionByName.TryGetValue(section.SectionName, out SectionRecord? record)
                ? [record.FromStation, record.ToStation]
                : [];
            bool inside = stations.Any(activeStations.Contains);
            ProofMappedDecision decision = new(
                "section_precedence", key, null, null, string.Join('|', stations), inside,
                inside ? "proof_supported_legal_endpoint_precedence" : "precedence_outside_active_scope");
            return new(row.ConstraintName, row.EvidenceKind, row.BusinessKind,
                inside ? ProofDecisionMappingStatus.AdjustableMainDecision : ProofDecisionMappingStatus.OutsideMacroScope,
                [decision], decision.Reason);
        }

        return new(row.ConstraintName, row.EvidenceKind, row.BusinessKind,
            ProofDecisionMappingStatus.Unmapped, [], "no_supported_specific_dependency_mapping");
    }

    private static ContinuousProofBusinessMapping FromStops(
        TimetablingInstance instance,
        IReadOnlySet<string> activeStations,
        ContinuousProofRow row,
        IReadOnlyList<string> stopIds,
        string sourceReason)
    {
        List<ProofMappedDecision> decisions = [];
        foreach (string stopId in stopIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!instance.StopsById.TryGetValue(stopId, out TrainStopRecord? stop)) continue;
            bool businessFlexible = stop.StopRequirement == StopRequirement.OptionalStop &&
                                    stop.StopRole == TrainStopRole.Intermediate;
            bool inside = activeStations.Contains(stop.StationName);
            bool adjustable = businessFlexible && inside;
            string reason = !businessFlexible
                ? $"original_business_fixed:{stop.StopRequirement}:{stop.StopRole}"
                : !inside
                    ? "optional_stop_outside_active_macro_scope"
                    : sourceReason;
            decisions.Add(new(
                "stop", stop.StopId, stop.TrainIndex, stop.StationOrder,
                stop.StationName, adjustable, reason));
        }
        ProofDecisionMappingStatus status = decisions.Any(decision => decision.IsAdjustable)
            ? ProofDecisionMappingStatus.AdjustableMainDecision
            : decisions.Any(decision => decision.Reason.StartsWith("original_business_fixed", StringComparison.Ordinal))
                ? ProofDecisionMappingStatus.OriginalBusinessHard
                : decisions.Count > 0
                    ? ProofDecisionMappingStatus.OutsideMacroScope
                    : ProofDecisionMappingStatus.Unmapped;
        return new(row.ConstraintName, row.EvidenceKind, row.BusinessKind, status,
            decisions, decisions.Count == 0 ? "referenced_train_stop_not_found" : sourceReason);
    }
}
