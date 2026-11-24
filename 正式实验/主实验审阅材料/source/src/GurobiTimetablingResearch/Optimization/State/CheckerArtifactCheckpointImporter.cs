// 产物检查点导入器从既有运行目录读取快照、已接受结构边和诊断表，重建可续跑的 CheckerCheckpoint。
// 导入时按规范键去重并校验文件间一致性，避免把未完成轮次或相互矛盾的产物带入新一轮求解。
using System.Globalization;
using GurobiTimetablingResearch.Domain;
using GurobiTimetablingResearch.IO;

namespace GurobiTimetablingResearch.Optimization;

public sealed record CheckerArtifactImportOptions(
    bool ZeroConflictMode,
    int EndpointOccupationSeconds,
    int ExpectedSectionEdgeCount,
    int ExpectedStationEventEdgeCount);

public static class CheckerArtifactCheckpointImporter
{
    public static CheckerCheckpoint Import(
        string checkerArtifactsDirectory,
        TimetablingInstance instance,
        CheckerArtifactImportOptions options)
    {
        SimpleCsvReader reader = new();
        IReadOnlyList<IReadOnlyDictionary<string, string>> auditRows = reader.ReadRows(
            Path.Combine(checkerArtifactsDirectory, "checker_loop_cut_audit.csv"));
        HashSet<int> retainedIterations = auditRows
            .Where(row => string.Equals(Required(row, "outcome"), "Retained", StringComparison.OrdinalIgnoreCase))
            .Select(row => ParseInt(Required(row, "iteration_index"), "iteration_index"))
            .ToHashSet();
        if (retainedIterations.Count == 0)
        {
            throw new InvalidDataException("Checker artifact audit contains no retained structural iteration.");
        }

        int lastAcceptedIteration = retainedIterations.Max();
        IReadOnlyDictionary<string, string> lastAudit = auditRows.Single(row =>
            ParseInt(Required(row, "iteration_index"), "iteration_index") == lastAcceptedIteration &&
            string.Equals(Required(row, "outcome"), "Retained", StringComparison.OrdinalIgnoreCase));
        double objective = ParseFiniteDouble(Required(lastAudit, "objective_after"), "objective_after");

        Dictionary<(int TrainIndex, int StationOrder), TrainStopRecord> stopByTrainAndOrder = instance.TrainStops
            .ToDictionary(stop => (stop.TrainIndex, stop.StationOrder));
        Dictionary<string, double> arrivals = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, double> departures = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> stops = new(StringComparer.OrdinalIgnoreCase);
        foreach (IReadOnlyDictionary<string, string> row in reader.ReadRows(
                     Path.Combine(checkerArtifactsDirectory, "schedule.csv")))
        {
            int trainIndex = ParseInt(Required(row, "train_index"), "train_index");
            int stationOrder = ParseInt(Required(row, "station_order"), "station_order");
            if (!stopByTrainAndOrder.TryGetValue((trainIndex, stationOrder), out TrainStopRecord? stop))
            {
                throw new InvalidDataException(
                    $"Schedule row references unknown train/station order {trainIndex}/{stationOrder}.");
            }
            if (!arrivals.TryAdd(stop.StopId, ParseFiniteDouble(Required(row, "arrival_seconds"), "arrival_seconds")) ||
                !departures.TryAdd(stop.StopId, ParseFiniteDouble(Required(row, "departure_seconds"), "departure_seconds")) ||
                !stops.TryAdd(stop.StopId, ParseBinary(Required(row, "is_stop"), "is_stop")))
            {
                throw new InvalidDataException($"Schedule contains a duplicate row for stop '{stop.StopId}'.");
            }
        }
        if (arrivals.Count != instance.TrainStops.Count)
        {
            throw new InvalidDataException(
                $"Schedule contains {arrivals.Count} stops but the instance contains {instance.TrainStops.Count}.");
        }

        Dictionary<string, int> sectionPrecedence = new(StringComparer.OrdinalIgnoreCase);
        foreach (IReadOnlyDictionary<string, string> row in reader.ReadRows(
                     Path.Combine(checkerArtifactsDirectory, "section_precedence.csv")))
        {
            string sectionName = Required(row, "section_name");
            int firstTrain = ParseInt(Required(row, "first_train_index"), "first_train_index");
            int secondTrain = ParseInt(Required(row, "second_train_index"), "second_train_index");
            string key = BasicMilpModelBuilder.BuildSectionPrecedenceKey(sectionName, firstTrain, secondTrain);
            if (!sectionPrecedence.TryAdd(key, ParseBinary(Required(row, "precedence_value"), "precedence_value")))
            {
                throw new InvalidDataException($"Section precedence contains duplicate key '{key}'.");
            }
        }

        List<SectionConflictEdgeRequest> sectionEdges = new();
        List<StationEventConflictCandidate> stationEdges = new();
        HashSet<string> sectionKeys = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> stationKeys = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> acceptedCandidateIds = new(StringComparer.OrdinalIgnoreCase);
        foreach (IReadOnlyDictionary<string, string> row in reader.ReadRows(
                     Path.Combine(checkerArtifactsDirectory, "checker_loop_cut_selection.csv")))
        {
            int iteration = ParseInt(Required(row, "iteration_index"), "iteration_index");
            if (!retainedIterations.Contains(iteration))
            {
                continue;
            }

            string candidateKey = Required(row, "candidate_key");
            acceptedCandidateIds.Add(candidateKey);
            string[] parts = candidateKey.Split("::", StringSplitOptions.None);
            if (parts.Length == 4 && string.Equals(parts[0], "section", StringComparison.OrdinalIgnoreCase))
            {
                SectionConflictEdgeRequest edge = new(
                    parts[1],
                    ParseInt(parts[2], "section candidate first train"),
                    ParseInt(parts[3], "section candidate second train"));
                string canonicalKey = InitialConflictGraphBuilder.BuildCanonicalPairKey(
                    edge.SectionName, edge.FirstTrainIndex, edge.SecondTrainIndex);
                if (sectionKeys.Add(canonicalKey))
                {
                    sectionEdges.Add(edge);
                }
                continue;
            }

            if (parts.Length == 6 && string.Equals(parts[0], "station", StringComparison.OrdinalIgnoreCase))
            {
                int firstTrain = ParseInt(parts[2], "station candidate first train");
                int secondTrain = ParseInt(parts[4], "station candidate second train");
                StationEventSide firstSide = ParseSide(parts[3], "station candidate first side");
                StationEventSide secondSide = ParseSide(parts[5], "station candidate second side");
                TrainStopRecord firstStop = ResolveUniqueStop(instance, firstTrain, parts[1]);
                TrainStopRecord secondStop = ResolveUniqueStop(instance, secondTrain, parts[1]);
                StationEventConflictCandidate edge = new(
                    parts[1], firstTrain, secondTrain,
                    firstStop.StationOrder, secondStop.StationOrder,
                    firstSide, secondSide);
                string canonicalKey = BasicMilpModelBuilder.BuildStationEventPrecedenceKey(edge);
                if (stationKeys.Add(canonicalKey))
                {
                    stationEdges.Add(edge);
                }
                continue;
            }

            throw new InvalidDataException($"Malformed Checker structural candidate key '{candidateKey}'.");
        }

        if (sectionEdges.Count != options.ExpectedSectionEdgeCount ||
            stationEdges.Count != options.ExpectedStationEventEdgeCount)
        {
            throw new InvalidDataException(
                $"Imported active-edge counts {sectionEdges.Count}/{stationEdges.Count} do not match " +
                $"expected {options.ExpectedSectionEdgeCount}/{options.ExpectedStationEventEdgeCount}.");
        }

        Dictionary<string, int> stationPrecedence = new(StringComparer.OrdinalIgnoreCase);
        foreach (StationEventConflictCandidate edge in stationEdges)
        {
            TrainStopRecord firstStop = stopByTrainAndOrder[(edge.FirstTrainIndex, edge.FirstStationOrder)];
            TrainStopRecord secondStop = stopByTrainAndOrder[(edge.SecondTrainIndex, edge.SecondStationOrder)];
            double firstTime = edge.FirstSide == StationEventSide.Incoming
                ? arrivals[firstStop.StopId]
                : departures[firstStop.StopId];
            double secondTime = edge.SecondSide == StationEventSide.Incoming
                ? arrivals[secondStop.StopId]
                : departures[secondStop.StopId];
            if (Math.Abs(firstTime - secondTime) <= 1e-6)
            {
                continue;
            }
            stationPrecedence[BasicMilpModelBuilder.BuildStationEventPrecedenceKey(edge)] =
                firstTime < secondTime ? 0 : 1;
        }

        MasterSolutionSnapshot snapshot = new(
            objective,
            arrivals,
            departures,
            stops,
            sectionPrecedence,
            stationPrecedence,
            instance.Trains.Select(train => train.TrainIndex).ToHashSet());
        return new CheckerCheckpoint(
            lastAcceptedIteration,
            snapshot,
            sectionEdges,
            stationEdges,
            acceptedCandidateIds,
            options.ZeroConflictMode,
            options.EndpointOccupationSeconds,
            Path.GetFullPath(checkerArtifactsDirectory),
            DateTimeOffset.UtcNow);
    }

    private static TrainStopRecord ResolveUniqueStop(TimetablingInstance instance, int trainIndex, string stationName)
    {
        List<TrainStopRecord> matches = instance.TrainStops.Where(stop =>
            stop.TrainIndex == trainIndex &&
            string.Equals(stop.StationName, stationName, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count == 1
            ? matches[0]
            : throw new InvalidDataException(
                $"Station candidate requires one stop for train {trainIndex} at '{stationName}', found {matches.Count}.");
    }

    private static string Required(IReadOnlyDictionary<string, string> row, string column) =>
        row.TryGetValue(column, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidDataException($"Checker artifact row is missing required column '{column}'.");

    private static int ParseInt(string raw, string field) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : throw new InvalidDataException($"Checker artifact field '{field}' has invalid integer '{raw}'.");

    private static double ParseFiniteDouble(string raw, string field)
    {
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ||
            !double.IsFinite(value))
        {
            throw new InvalidDataException($"Checker artifact field '{field}' has invalid number '{raw}'.");
        }
        return value;
    }

    private static int ParseBinary(string raw, string field)
    {
        double value = ParseFiniteDouble(raw, field);
        if (Math.Abs(value) <= 1e-6)
        {
            return 0;
        }
        if (Math.Abs(value - 1.0) <= 1e-6)
        {
            return 1;
        }
        throw new InvalidDataException($"Checker artifact field '{field}' must be binary, found '{raw}'.");
    }

    private static StationEventSide ParseSide(string raw, string field) =>
        Enum.TryParse(raw, true, out StationEventSide side)
            ? side
            : throw new InvalidDataException($"Checker artifact field '{field}' has invalid event side '{raw}'.");
}
