using System.Text.Json;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

public static class DynamicHotspotEvidenceLoader
{
    public static DynamicHotspotEvidence Load(string path, TimetablingInstance instance) =>
        Load(path, instance, []);

    public static DynamicHotspotEvidence Load(
        string path,
        TimetablingInstance instance,
        IReadOnlyList<MicroWitnessCertificate> retainedPatterns)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(retainedPatterns);
        string fullPath = Path.GetFullPath(path);
        string json = File.ReadAllText(fullPath);
        JsonSerializerOptions options = new() { PropertyNameCaseInsensitive = true };
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty("scheduledEvidence", out JsonElement scheduledElement))
        {
            DynamicHotspotEvidence? scheduled = JsonSerializer.Deserialize<DynamicHotspotEvidence>(
                scheduledElement.GetRawText(), options);
            return ValidateScheduledEvidence(
                scheduled,
                instance,
                $"scheduled::{Path.GetFileName(fullPath)}");
        }
        bool isEnvelope = document.RootElement.TryGetProperty("infeasibilityProof", out _);
        ProofPayload? proof = isEnvelope
            ? JsonSerializer.Deserialize<ProofEnvelope>(json, options)?.InfeasibilityProof
            : JsonSerializer.Deserialize<ProofPayload>(json, options);
        if (proof is null)
            return new DynamicHotspotEvidence(
                $"missing::{Path.GetFileName(fullPath)}",
                DynamicHotspotProofStatus.EmptyProof,
                [],
                [],
                []);

        ProofRowPayload[] sourceRows = proof.Rows ?? [];
        if (sourceRows.Length == 0)
            return new DynamicHotspotEvidence(
                proof.ProofIdentity ?? $"empty::{Path.GetFileName(fullPath)}",
                DynamicHotspotProofStatus.EmptyProof,
                [],
                proof.SuggestedStations ?? [],
                proof.SuggestedStopIds ?? []);

        HashSet<string> stationNames = instance.Stations
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, (MicroWitnessCertificate Certificate, MicroCertificateCondition Condition)> patternRows =
            retainedPatterns.SelectMany(certificate => certificate.Conditions.Select((condition, index) => new
                {
                    Name = MicroCertificateModelBinder.BuildConstraintName(certificate.CertificateId, index),
                    Certificate = certificate,
                    Condition = condition
                }))
                .GroupBy(item => item.Name, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => (group.Single().Certificate, group.Single().Condition),
                    StringComparer.Ordinal);
        bool mappingFailed = false;
        List<DynamicHotspotEvidenceRow> rows = [];
        foreach (ProofRowPayload row in sourceRows)
        {
            string constraintName = row.ConstraintName ?? string.Empty;
            if (patternRows.TryGetValue(constraintName, out var mappedPattern))
            {
                string source = $"micro-pattern::{mappedPattern.Certificate.CertificateId}::" +
                                mappedPattern.Condition.SourceRuleId;
                string[] conditionStops = new[]
                    {
                        mappedPattern.Condition.LaterStopId,
                        mappedPattern.Condition.EarlierStopId
                    }
                    .Where(stopId => !string.IsNullOrWhiteSpace(stopId))
                    .Select(stopId => stopId!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (conditionStops.Length == 0 ||
                    !stationNames.Contains(mappedPattern.Certificate.StationName) ||
                    conditionStops.Any(stopId => !instance.StopsById.TryGetValue(stopId, out TrainStopRecord? stop) ||
                                                 !string.Equals(stop.StationName,
                                                     mappedPattern.Certificate.StationName,
                                                     StringComparison.OrdinalIgnoreCase)))
                {
                    mappingFailed = true;
                    rows.Add(new DynamicHotspotEvidenceRow(
                        constraintName, source, mappedPattern.Certificate.StationName, null,
                        DynamicHotspotEvidenceDisposition.Unmapped));
                    continue;
                }

                rows.AddRange(conditionStops.Select(stopId => new DynamicHotspotEvidenceRow(
                    constraintName,
                    source,
                    mappedPattern.Certificate.StationName,
                    stopId,
                    DynamicHotspotEvidenceDisposition.Adjustable)));
                continue;
            }

            string businessSource = row.BusinessSource ?? row.BusinessKind ?? row.BusinessId ?? string.Empty;
            if (string.Equals(row.BusinessKind, "micro_pattern", StringComparison.OrdinalIgnoreCase))
            {
                mappingFailed = true;
                rows.Add(new DynamicHotspotEvidenceRow(
                    constraintName, businessSource, row.StationName, row.StopId,
                    DynamicHotspotEvidenceDisposition.Unmapped));
                continue;
            }
            bool stationKnown = string.IsNullOrWhiteSpace(row.StationName) || stationNames.Contains(row.StationName);
            bool stopKnown = string.IsNullOrWhiteSpace(row.StopId) || instance.StopsById.ContainsKey(row.StopId);
            bool stopMatchesStation = string.IsNullOrWhiteSpace(row.StopId) ||
                                      string.IsNullOrWhiteSpace(row.StationName) ||
                                      (stopKnown && string.Equals(
                                          instance.StopsById[row.StopId!].StationName,
                                          row.StationName,
                                          StringComparison.OrdinalIgnoreCase));
            if (!stationKnown || !stopKnown || !stopMatchesStation) mappingFailed = true;
            DynamicHotspotEvidenceDisposition disposition = !stationKnown || !stopKnown || !stopMatchesStation
                ? DynamicHotspotEvidenceDisposition.Unmapped
                : IsAdjustableSource(businessSource)
                    ? DynamicHotspotEvidenceDisposition.Adjustable
                    : DynamicHotspotEvidenceDisposition.ImmutableBusinessRule;
            rows.Add(new DynamicHotspotEvidenceRow(
                constraintName,
                businessSource,
                row.StationName,
                row.StopId,
                disposition));
        }

        foreach (string station in proof.SuggestedStations ?? [])
            if (!stationNames.Contains(station)) mappingFailed = true;
        foreach (string stopId in proof.SuggestedStopIds ?? [])
            if (!instance.StopsById.ContainsKey(stopId)) mappingFailed = true;

        DynamicHotspotProofStatus status = mappingFailed
            ? DynamicHotspotProofStatus.MappingFailed
            : IsHardProofStatus(proof.Status)
                ? DynamicHotspotProofStatus.HardProof
                : DynamicHotspotProofStatus.Unknown;
        return new DynamicHotspotEvidence(
            proof.ProofIdentity ?? $"unidentified::{Path.GetFileName(fullPath)}",
            status,
            rows.ToArray(),
            (proof.SuggestedStations ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            (proof.SuggestedStopIds ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static DynamicHotspotEvidence ValidateScheduledEvidence(
        DynamicHotspotEvidence? scheduled,
        TimetablingInstance instance,
        string fallbackIdentity)
    {
        if (scheduled is null)
            return new DynamicHotspotEvidence(
                fallbackIdentity,
                DynamicHotspotProofStatus.EmptyProof,
                [],
                [],
                []);

        DynamicHotspotEvidenceRow[] rows = (scheduled.Rows ?? [])
            .Where(row => row is not null)
            .ToArray();
        string[] stations = (scheduled.SuggestedStations ?? [])
            .Where(station => !string.IsNullOrWhiteSpace(station))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string[] stopIds = (scheduled.SuggestedStopIds ?? [])
            .Where(stopId => !string.IsNullOrWhiteSpace(stopId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (rows.Length == 0)
            return new DynamicHotspotEvidence(
                string.IsNullOrWhiteSpace(scheduled.ProofIdentity) ? fallbackIdentity : scheduled.ProofIdentity,
                DynamicHotspotProofStatus.EmptyProof,
                [],
                stations,
                stopIds);

        HashSet<string> knownStations = instance.Stations.ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool mappingFailed = stations.Any(station => !knownStations.Contains(station)) ||
                             stopIds.Any(stopId => !instance.StopsById.ContainsKey(stopId)) ||
                             rows.Any(row =>
                                 string.IsNullOrWhiteSpace(row.ConstraintName) ||
                                 (!string.IsNullOrWhiteSpace(row.StationName) &&
                                  !knownStations.Contains(row.StationName)) ||
                                 (!string.IsNullOrWhiteSpace(row.StopId) &&
                                  !instance.StopsById.TryGetValue(row.StopId, out TrainStopRecord? stop)) ||
                                 (!string.IsNullOrWhiteSpace(row.StopId) &&
                                  !string.IsNullOrWhiteSpace(row.StationName) &&
                                  instance.StopsById.TryGetValue(row.StopId, out TrainStopRecord? mappedStop) &&
                                  !string.Equals(mappedStop.StationName, row.StationName,
                                      StringComparison.OrdinalIgnoreCase)));

        return new DynamicHotspotEvidence(
            string.IsNullOrWhiteSpace(scheduled.ProofIdentity) ? fallbackIdentity : scheduled.ProofIdentity,
            mappingFailed ? DynamicHotspotProofStatus.MappingFailed : scheduled.Status,
            rows,
            stations,
            stopIds);
    }

    private static bool IsHardProofStatus(string? status) =>
        string.Equals(status, "iis_available", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "farkas_available", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "infeasible", StringComparison.OrdinalIgnoreCase);

    private static bool IsAdjustableSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return false;
        return source.StartsWith("select_exactly_one_station_pattern", StringComparison.OrdinalIgnoreCase) ||
               source.StartsWith("overtake-stop::", StringComparison.OrdinalIgnoreCase) ||
               source.StartsWith("proven_full_line_conflict:", StringComparison.OrdinalIgnoreCase) ||
               source.Contains("pattern", StringComparison.OrdinalIgnoreCase) ||
               source.Contains("event_order", StringComparison.OrdinalIgnoreCase) ||
               source.Contains("time_category", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ProofEnvelope(int Iteration, ProofPayload? InfeasibilityProof);

    private sealed record ProofPayload(
        string? ProofIdentity,
        ProofRowPayload[]? Rows,
        string[]? SuggestedStations,
        string[]? SuggestedStopIds,
        string? Status);

    private sealed record ProofRowPayload(
        string? ConstraintName,
        string? BusinessSource,
        string? StationName,
        string? StopId,
        string? BusinessKind,
        string? BusinessId);
}
