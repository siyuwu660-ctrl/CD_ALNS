using System.Text.Json;
using System.Globalization;
using System.Text;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

internal static class MicroStructureSnapshotLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    internal static MasterSolutionSnapshot Load(
        string snapshotPath,
        string? checkpointPath,
        TimetablingInstance instance)
    {
        MasterSolutionSnapshot snapshot = MasterSolutionSnapshotStore.Load(
            snapshotPath,
            instance.Meta.InstanceName,
            instance.Trains.Select(train => train.TrainIndex).ToHashSet());
        if (string.IsNullOrWhiteSpace(checkpointPath)) return snapshot;
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(checkpointPath));
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("instanceName", out JsonElement instanceName) ||
            !string.Equals(instanceName.GetString(), instance.Meta.InstanceName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Micro-structure checkpoint instance does not match the loaded dataset.");
        if (!root.TryGetProperty("acceptedSnapshot", out JsonElement accepted) ||
            !accepted.TryGetProperty("verifiedMicroAssignments", out JsonElement witnessElement) ||
            witnessElement.ValueKind != JsonValueKind.Array)
            return snapshot;
        VerifySameAcceptedState(accepted, snapshot);
        List<VerifiedMicroAssignmentWitness> witnesses =
            JsonSerializer.Deserialize<List<VerifiedMicroAssignmentWitness>>(witnessElement.GetRawText(), JsonOptions) ?? [];
        string fingerprint = SnapshotFingerprint.Calculate(snapshot);
        VerifiedMicroAssignmentWitness[] refreshed = witnesses.Select(witness => witness with
        {
            CandidateSnapshotFingerprint = fingerprint,
            ArrivalSeconds = snapshot.ArrivalSecondsByStopId[witness.RequirementId],
            DepartureSeconds = snapshot.DepartureSecondsByStopId[witness.RequirementId]
        }).ToArray();
        return snapshot with { VerifiedMicroAssignments = refreshed };
    }

    private static void VerifySameAcceptedState(JsonElement accepted, MasterSolutionSnapshot snapshot)
    {
        CompareDoubleMap("arrivalSecondsByStopId", snapshot.ArrivalSecondsByStopId);
        CompareDoubleMap("departureSecondsByStopId", snapshot.DepartureSecondsByStopId);
        CompareIntMap("stopByStopId", snapshot.StopByStopId);
        CompareIntMap("sectionPrecedenceByKey", snapshot.SectionPrecedenceByKey);
        CompareIntMap("stationEventPrecedenceByKey", snapshot.StationEventPrecedenceByKey);

        void CompareDoubleMap(string property, IReadOnlyDictionary<string, double> expected)
        {
            if (!accepted.TryGetProperty(property, out JsonElement value)) return;
            Dictionary<string, double> actual = JsonSerializer.Deserialize<Dictionary<string, double>>(
                value.GetRawText(), JsonOptions) ?? [];
            if (actual.Count != expected.Count || expected.Any(item =>
                    !actual.TryGetValue(item.Key, out double candidate) || Math.Abs(candidate - item.Value) > 1e-6))
                throw new InvalidDataException(
                    $"Checkpoint acceptedSnapshot '{property}' is not the same state as the supplied Macro snapshot.");
        }

        void CompareIntMap(string property, IReadOnlyDictionary<string, int> expected)
        {
            if (!accepted.TryGetProperty(property, out JsonElement value)) return;
            Dictionary<string, int> actual = JsonSerializer.Deserialize<Dictionary<string, int>>(
                value.GetRawText(), JsonOptions) ?? [];
            if (actual.Count != expected.Count || expected.Any(item =>
                    !actual.TryGetValue(item.Key, out int candidate) || candidate != item.Value))
                throw new InvalidDataException(
                    $"Checkpoint acceptedSnapshot '{property}' is not the same state as the supplied Macro snapshot.");
        }
    }
}

internal static class FailedMicroStationLoader
{
    internal static IReadOnlyList<string> LoadFromStationSummary(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("The preserved Micro station summary is required for a no-rerun diagnostic.", path);
        List<string> result = [];
        foreach (string line in File.ReadLines(path).Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            string[] fields = line.Split(',', 6);
            if (fields.Length < 5 || !int.TryParse(fields[4], out int conflicts) || conflicts <= 0) continue;
            result.Add(fields[0].Trim().Trim('"'));
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}

internal sealed record APropagationStationRanking(
    bool Configured,
    bool ActualSavedAnalysisUsed,
    string SourcePath,
    IReadOnlyDictionary<string, int> RankByStation,
    IReadOnlyDictionary<string, int> SelectedDemandCountByStation);

internal static class APropagationStationRankingLoader
{
    internal static APropagationStationRanking Load(string stationSummaryPath, TimetablingInstance instance)
    {
        string stationDirectory = Path.GetDirectoryName(Path.GetFullPath(stationSummaryPath))!;
        string? runRoot = Directory.GetParent(stationDirectory)?.Parent?.FullName;
        string source = runRoot is null ? string.Empty : Path.Combine(runRoot, "repair_neighborhood_selector_audit.csv");
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
            return new(true, false, source, new Dictionary<string, int>(), new Dictionary<string, int>());
        Dictionary<string, TrainStopRecord> stopById = instance.TrainStops.ToDictionary(
            stop => stop.StopId, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, (int Count, double MaximumShift)> scores = new(StringComparer.OrdinalIgnoreCase);
        foreach (string line in File.ReadLines(source).Skip(1))
        {
            IReadOnlyList<string> fields = ParseCsv(line);
            if (fields.Count < 11 || !bool.TryParse(fields[10], out bool selected) || !selected) continue;
            if (!stopById.TryGetValue(fields[5], out TrainStopRecord? stop)) continue;
            double shift = fields.Count > 8 && double.TryParse(fields[8], NumberStyles.Float,
                CultureInfo.InvariantCulture, out double parsed) ? Math.Abs(parsed) : 0;
            (int count, double maximum) = scores.GetValueOrDefault(stop.StationName);
            scores[stop.StationName] = (count + 1, Math.Max(maximum, shift));
        }
        string[] ordered = scores.OrderByDescending(item => item.Value.Count)
            .ThenByDescending(item => item.Value.MaximumShift)
            .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Key).ToArray();
        return new(true, true, source,
            ordered.Select((station, index) => (station, index)).ToDictionary(
                item => item.station, item => item.index, StringComparer.OrdinalIgnoreCase),
            scores.ToDictionary(item => item.Key, item => item.Value.Count, StringComparer.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> ParseCsv(string line)
    {
        List<string> fields = [];
        StringBuilder value = new();
        bool quoted = false;
        for (int index = 0; index < line.Length; index++)
        {
            char character = line[index];
            if (character == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"')
                {
                    value.Append('"');
                    index++;
                }
                else quoted = !quoted;
            }
            else if (character == ',' && !quoted)
            {
                fields.Add(value.ToString());
                value.Clear();
            }
            else value.Append(character);
        }
        fields.Add(value.ToString());
        return fields;
    }
}

internal static class MicroBusinessCouplingBuilder
{
    internal static IReadOnlyList<MicroBusinessCoupling> Build(TimetablingInstance instance)
    {
        List<MicroBusinessCoupling> result = [];
        foreach (TrainRecord train in instance.Trains)
        for (int index = 0; index < train.OrderedStops.Count - 1; index++)
        {
            TrainStopRecord first = train.OrderedStops[index];
            TrainStopRecord second = train.OrderedStops[index + 1];
            result.Add(Normalize(new(
                first.StationName,
                second.StationName,
                $"runtime:t{train.TrainIndex}:{first.StopId}->{second.StopId}",
                train.TrainIndex,
                "runtime")));
        }

        foreach (TrainSectionStopQuotaConstruction rule in TrainSectionStopQuotaConstraintBuilder.Plan(instance))
        {
            string[] stations = rule.CountedStops.Select(stop => stop.StationName)
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            for (int first = 0; first < stations.Length; first++)
            for (int second = first + 1; second < stations.Length; second++)
                result.Add(Normalize(new(stations[first], stations[second], rule.RuleId, rule.TrainIndex, "section_stop")));
        }

        foreach ((TrainSectionTravelTimeLimitRule rule, int index) in
                 instance.TrainSectionTravelTimeLimitRules.Select((value, index) => (value, index)))
            result.Add(Normalize(new(
                rule.SectionStartStation,
                rule.SectionEndStation,
                $"travel-time-{index + 1:D3}:t{rule.TrainIndex}",
                rule.TrainIndex,
                "travel_time")));

        return result.Distinct().OrderBy(item => item.RuleId, StringComparer.Ordinal).ToArray();
    }

    private static MicroBusinessCoupling Normalize(MicroBusinessCoupling coupling) =>
        StringComparer.OrdinalIgnoreCase.Compare(coupling.FirstStation, coupling.SecondStation) <= 0
            ? coupling
            : coupling with { FirstStation = coupling.SecondStation, SecondStation = coupling.FirstStation };
}
