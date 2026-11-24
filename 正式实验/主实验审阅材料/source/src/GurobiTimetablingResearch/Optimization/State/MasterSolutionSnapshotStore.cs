// 快照存储器用带版本信封的 JSON 读写 MasterSolutionSnapshot，并对键集合及数值进行基本校验。
// 独立存储格式使解状态可以跨进程作为种子复用，而不依赖 Gurobi 模型对象或变量创建顺序。
using System.Text.Json;

namespace GurobiTimetablingResearch.Optimization;

public static class MasterSolutionSnapshotStore
{
    private const int CurrentFormatVersion = 5;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static void Save(string path, string instanceName, MasterSolutionSnapshot snapshot)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        SnapshotEnvelope envelope = new(
            CurrentFormatVersion,
            instanceName,
            snapshot.ObjectiveValue,
            new Dictionary<string, double>(snapshot.ArrivalSecondsByStopId, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, double>(snapshot.DepartureSecondsByStopId, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, int>(snapshot.StopByStopId, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, int>(snapshot.SectionPrecedenceByKey, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, int>(snapshot.StationEventPrecedenceByKey, StringComparer.OrdinalIgnoreCase),
            snapshot.AdmittedTrainIndices.Order().ToList(),
            new Dictionary<string, double>(
                snapshot.RunningTimeSupplementSecondsByKey ?? new Dictionary<string, double>(),
                StringComparer.OrdinalIgnoreCase),
            snapshot.BusinessObjective,
            snapshot.SolutionRole,
            snapshot.BoundReport,
            snapshot.VerifiedMicroAssignments?.ToList(),
            snapshot.ServiceObjective);
        File.WriteAllText(path, JsonSerializer.Serialize(envelope, JsonOptions));
    }

    public static MasterSolutionSnapshot Load(
        string path,
        string expectedInstanceName,
        IReadOnlySet<int> expectedTrainIndices)
    {
        SnapshotEnvelope envelope = JsonSerializer.Deserialize<SnapshotEnvelope>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException($"Master snapshot '{path}' is empty or invalid.");
        if (envelope.FormatVersion is < 1 or > CurrentFormatVersion)
        {
            throw new InvalidDataException($"Unsupported Master snapshot format {envelope.FormatVersion}.");
        }
        if (!string.Equals(envelope.InstanceName, expectedInstanceName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Master snapshot instance '{envelope.InstanceName}' does not match '{expectedInstanceName}'.");
        }

        HashSet<int> admitted = envelope.AdmittedTrainIndices.ToHashSet();
        if (!admitted.SetEquals(expectedTrainIndices))
        {
            throw new InvalidDataException("Master snapshot train set does not match the loaded instance.");
        }

        return CreateSnapshot(
            envelope.ObjectiveValue,
            envelope.ArrivalSecondsByStopId,
            envelope.DepartureSecondsByStopId,
            envelope.StopByStopId,
            envelope.SectionPrecedenceByKey,
            envelope.StationEventPrecedenceByKey,
            admitted,
            envelope.RunningTimeSupplementSecondsByKey,
            envelope.BusinessObjective,
            envelope.SolutionRole,
            envelope.BoundReport,
            envelope.VerifiedMicroAssignments,
            envelope.ServiceObjective);
    }

    /// <summary>
    /// Loads the bare snapshot payload embedded in a diagnostic artifact.  The public
    /// domain record deliberately exposes read-only interfaces, so deserialize through
    /// concrete collection types just like the versioned file envelope does.
    /// </summary>
    public static MasterSolutionSnapshot DeserializePayload(
        string json,
        IReadOnlySet<int> expectedTrainIndices)
    {
        SnapshotPayload payload = JsonSerializer.Deserialize<SnapshotPayload>(json, JsonOptions)
                                  ?? throw new InvalidDataException("Embedded Master snapshot is empty or invalid.");
        HashSet<int> admitted = payload.AdmittedTrainIndices.ToHashSet();
        if (!admitted.SetEquals(expectedTrainIndices))
            throw new InvalidDataException("Embedded Master snapshot train set does not match the loaded instance.");

        return CreateSnapshot(
            payload.ObjectiveValue,
            payload.ArrivalSecondsByStopId,
            payload.DepartureSecondsByStopId,
            payload.StopByStopId,
            payload.SectionPrecedenceByKey,
            payload.StationEventPrecedenceByKey,
            admitted,
            payload.RunningTimeSupplementSecondsByKey,
            payload.BusinessObjective,
            payload.SolutionRole,
            payload.BoundReport,
            payload.VerifiedMicroAssignments,
            payload.ServiceObjective);
    }

    private static MasterSolutionSnapshot CreateSnapshot(
        double objectiveValue,
        Dictionary<string, double> arrivals,
        Dictionary<string, double> departures,
        Dictionary<string, int> stops,
        Dictionary<string, int> sectionPrecedence,
        Dictionary<string, int> stationEventPrecedence,
        IReadOnlySet<int> admitted,
        Dictionary<string, double>? supplements,
        BusinessObjectiveVector? businessObjective,
        TimetableSolutionRole solutionRole,
        OptimizationBoundReport? boundReport,
        IReadOnlyList<VerifiedMicroAssignmentWitness>? witnesses,
        ServiceObjectiveVector? serviceObjective) =>
        new(
            objectiveValue,
            new Dictionary<string, double>(arrivals, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, double>(departures, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, int>(stops, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, int>(sectionPrecedence, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, int>(stationEventPrecedence, StringComparer.OrdinalIgnoreCase),
            admitted,
            new Dictionary<string, double>(supplements ?? new Dictionary<string, double>(), StringComparer.OrdinalIgnoreCase),
            businessObjective,
            solutionRole,
            boundReport,
            witnesses?.ToArray(),
            serviceObjective);

    private sealed record SnapshotEnvelope(
        int FormatVersion,
        string InstanceName,
        double ObjectiveValue,
        Dictionary<string, double> ArrivalSecondsByStopId,
        Dictionary<string, double> DepartureSecondsByStopId,
        Dictionary<string, int> StopByStopId,
        Dictionary<string, int> SectionPrecedenceByKey,
        Dictionary<string, int> StationEventPrecedenceByKey,
        List<int> AdmittedTrainIndices,
        Dictionary<string, double>? RunningTimeSupplementSecondsByKey = null,
        BusinessObjectiveVector? BusinessObjective = null,
        TimetableSolutionRole SolutionRole = TimetableSolutionRole.WorkingSolution,
        OptimizationBoundReport? BoundReport = null,
        List<VerifiedMicroAssignmentWitness>? VerifiedMicroAssignments = null,
        ServiceObjectiveVector? ServiceObjective = null);

    private sealed record SnapshotPayload(
        double ObjectiveValue,
        Dictionary<string, double> ArrivalSecondsByStopId,
        Dictionary<string, double> DepartureSecondsByStopId,
        Dictionary<string, int> StopByStopId,
        Dictionary<string, int> SectionPrecedenceByKey,
        Dictionary<string, int> StationEventPrecedenceByKey,
        List<int> AdmittedTrainIndices,
        Dictionary<string, double>? RunningTimeSupplementSecondsByKey = null,
        BusinessObjectiveVector? BusinessObjective = null,
        TimetableSolutionRole SolutionRole = TimetableSolutionRole.WorkingSolution,
        OptimizationBoundReport? BoundReport = null,
        List<VerifiedMicroAssignmentWitness>? VerifiedMicroAssignments = null,
        ServiceObjectiveVector? ServiceObjective = null);
}
