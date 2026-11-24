using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

internal sealed record SavedJointSnapshotVerificationResult(
    bool Passed,
    string InstanceName,
    string SnapshotPath,
    string SnapshotSha256,
    string CandidateFingerprint,
    int ExpectedAssignmentCount,
    int SavedAssignmentCount,
    int MacroViolationCount,
    string StationMicroStatus,
    int StationMicroConflictCount,
    int StationMicroUnknownCount,
    string IndependentMicroStatus,
    int IndependentMicroConflictCount,
    bool FixedAssignmentValid,
    bool IdentityValid,
    bool SerializedFingerprintMatches,
    string ReportPath);

internal sealed record SavedStationEventIdentity(
    string StationName,
    int FirstTrainIndex,
    StationEventSide FirstSide,
    int SecondTrainIndex,
    StationEventSide SecondSide);

internal static class SavedJointSnapshotVerifier
{
    internal static SavedJointSnapshotVerificationResult Run(
        TimetablingInstance instance,
        string snapshotPath,
        string outputDirectory,
        double runningTimeSupplementRatio,
        int endpointOccupationSeconds,Func<double>? remainingSeconds=null,ServiceWindowPolicy? serviceWindows=null)
    {
        ArgumentNullException.ThrowIfNull(instance);
        string fullSnapshotPath = Path.GetFullPath(snapshotPath);
        string fullOutputDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(fullOutputDirectory);

        MasterSolutionSnapshot snapshot = MasterSolutionSnapshotStore.Load(
            fullSnapshotPath,
            instance.Meta.InstanceName,
            instance.Trains.Select(train => train.TrainIndex).ToHashSet());
        string candidateFingerprint = SnapshotFingerprint.Calculate(snapshot);
        int expectedAssignmentCount = instance.TrainStops.Count;
        IReadOnlyList<VerifiedMicroAssignmentWitness> assignments =
            snapshot.VerifiedMicroAssignments ?? [];
        IReadOnlyList<StationEventConflictCandidate> savedStationEdges =
            ResolveSavedStationEdges(instance, snapshot);

        using ValidatedSnapshotCarrier carrier = new ValidatedSnapshotCarrierBuilder().Build(
            instance,
            neighborRadius: int.MaxValue,
            snapshot,
            runningTimeSupplementRatio,
            stationEdges: savedStationEdges,
            endpointOccupationSeconds: endpointOccupationSeconds,remainingSeconds:remainingSeconds,serviceWindows:serviceWindows);
        StationLevelMicroFeasibilitySlaveResult stationAnalysis =
            CheckerMicroValidationFactory.CreateSlave(endpointOccupationSeconds).Solve(carrier.Model,new(RemainingSeconds:remainingSeconds,FixedAssignments:assignments));
        MicroFeasibilityReport independentMicro =
            CheckerMicroValidationFactory.CreateChecker(endpointOccupationSeconds).Validate(carrier.Model,new(RemainingSeconds:remainingSeconds,FixedAssignments:assignments));
        VerifiedMicroAssignmentValidationReport fixedValidation =
            VerifiedMicroAssignmentValidator.Validate(
                instance,
                snapshot,
                assignments,
                stationAnalysis,
                endpointOccupationSeconds);
        MacroMicroJointIdentityCheck identity = MacroMicroJointAcceptancePolicy.CheckIdentity(
            candidateFingerprint,
            instance.TrainStops.Select(stop => stop.StopId),
            assignments);
        int unknownCount = stationAnalysis.StationResults.Count(result =>
            result.AssignmentResult.Status == MicroSolveStatus.Unknown);
        bool fingerprintMatches = string.Equals(
            SnapshotFingerprint.Calculate(snapshot),
            candidateFingerprint,
            StringComparison.OrdinalIgnoreCase);
        bool independentMicroValid = independentMicro.IsFeasible &&
            independentMicro.Status == MicroSolveStatus.Feasible &&
            independentMicro.Conflicts.Count == 0;
        bool passed = SavedJointSnapshotAcceptancePolicy.IsAccepted(
            carrier.Validation.Violations.Count,
            stationAnalysis.Status,
            stationAnalysis.Conflicts.Count,
            unknownCount,
            independentMicroValid,
            fixedValidation.IsValid,
            identity.IsValid,
            fingerprintMatches,
            assignments.Count,
            expectedAssignmentCount);
        passed = passed && (remainingSeconds is null || remainingSeconds()>0);

        string reportPath = Path.Combine(fullOutputDirectory, "saved_joint_readback_verification.json");
        object report = new
        {
            verificationKind = "fixed_saved_joint_snapshot_complete_readback",
            executedAt = DateTimeOffset.Now,
            passed,
            instance = instance.Meta,
            snapshotPath = fullSnapshotPath,
            snapshotSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fullSnapshotPath))),
            candidateFingerprint,
            runningTimeSupplementRatio,
            endpointOccupationSeconds,
            savedStationEventEdgeCount = savedStationEdges.Count,
            expectedAssignmentCount,
            savedAssignmentCount = assignments.Count,
            macro = new
            {
                carrier.Model.StatusName,
                violationCount = carrier.Validation.Violations.Count,
                carrier.Validation.Violations
            },
            stationMicro = new
            {
                status = stationAnalysis.Status.ToString(),
                conflictCount = stationAnalysis.Conflicts.Count,
                unknownCount,
                stationCount = stationAnalysis.StationResults.Count
            },
            independentMicro = new
            {
                status = independentMicro.Status.ToString(),
                independentMicro.IsFeasible,
                conflictCount = independentMicro.Conflicts.Count,
                stationCount = independentMicro.StationSummaries.Count
            },
            fixedAssignmentValidation = fixedValidation,
            identity,
            serializedFingerprintMatches = fingerprintMatches,
            note = "The saved timetable and its saved resource assignments were checked as-is; no timetable or resource alternative was accepted as a substitute."
        };
        JsonSerializerOptions options = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, options));
        File.WriteAllText(Path.Combine(fullOutputDirectory, "saved_joint_readback_verification.md"),
            BuildMarkdown(passed, fullSnapshotPath, candidateFingerprint, expectedAssignmentCount,
                assignments.Count, carrier.Validation.Violations.Count, stationAnalysis,
                unknownCount, independentMicro, fixedValidation, identity, fingerprintMatches));

        return new SavedJointSnapshotVerificationResult(
            passed,
            instance.Meta.InstanceName,
            fullSnapshotPath,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fullSnapshotPath))),
            candidateFingerprint,
            expectedAssignmentCount,
            assignments.Count,
            carrier.Validation.Violations.Count,
            stationAnalysis.Status.ToString(),
            stationAnalysis.Conflicts.Count,
            unknownCount,
            independentMicro.Status.ToString(),
            independentMicro.Conflicts.Count,
            fixedValidation.IsValid,
            identity.IsValid,
            fingerprintMatches,
            reportPath);
    }

    internal static SavedStationEventIdentity ParseStationEventKey(string key)
    {
        string[] parts = key.Split("::", StringSplitOptions.None);
        if (parts.Length != 5 || string.IsNullOrWhiteSpace(parts[0]) ||
            !int.TryParse(parts[1], out int firstTrainIndex) ||
            !Enum.TryParse(parts[2], ignoreCase: true, out StationEventSide firstSide) ||
            !int.TryParse(parts[3], out int secondTrainIndex) ||
            !Enum.TryParse(parts[4], ignoreCase: true, out StationEventSide secondSide))
        {
            throw new InvalidDataException($"Malformed station-event precedence key '{key}'.");
        }
        return new SavedStationEventIdentity(
            parts[0], firstTrainIndex, firstSide, secondTrainIndex, secondSide);
    }

    internal static IReadOnlyList<StationEventConflictCandidate> ResolveSavedStationEdges(
        TimetablingInstance instance,
        MasterSolutionSnapshot snapshot) =>
        snapshot.StationEventPrecedenceByKey.Keys.Select(key =>
        {
            SavedStationEventIdentity identity = ParseStationEventKey(key);
            TrainStopRecord first = ResolveUniqueStationStop(
                instance, identity.FirstTrainIndex, identity.StationName);
            TrainStopRecord second = ResolveUniqueStationStop(
                instance, identity.SecondTrainIndex, identity.StationName);
            StationEventConflictCandidate edge = new(
                identity.StationName,
                identity.FirstTrainIndex,
                identity.SecondTrainIndex,
                first.StationOrder,
                second.StationOrder,
                identity.FirstSide,
                identity.SecondSide);
            string rebuiltKey = BasicMilpModelBuilder.BuildStationEventPrecedenceKey(edge);
            if (!string.Equals(rebuiltKey, key, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"Saved station-event key '{key}' rebuilt as '{rebuiltKey}'.");
            return edge;
        }).ToArray();

    private static TrainStopRecord ResolveUniqueStationStop(
        TimetablingInstance instance,
        int trainIndex,
        string stationName)
    {
        TrainStopRecord[] matches = instance.TrainStops.Where(stop =>
            stop.TrainIndex == trainIndex && string.Equals(
                stop.StationName, stationName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1)
            throw new InvalidDataException(
                $"Station-event key requires one stop for train={trainIndex}, station='{stationName}', actual={matches.Length}.");
        return matches[0];
    }

    private static string BuildMarkdown(
        bool passed,
        string snapshotPath,
        string fingerprint,
        int expectedAssignments,
        int savedAssignments,
        int macroViolations,
        StationLevelMicroFeasibilitySlaveResult stationAnalysis,
        int unknownCount,
        MicroFeasibilityReport independentMicro,
        VerifiedMicroAssignmentValidationReport fixedValidation,
        MacroMicroJointIdentityCheck identity,
        bool fingerprintMatches)
    {
        StringBuilder text = new();
        text.AppendLine("# Saved joint-feasible readback verification");
        text.AppendLine();
        text.AppendLine($"- Passed: **{passed}**");
        text.AppendLine($"- Snapshot: `{snapshotPath}`");
        text.AppendLine($"- Candidate fingerprint: `{fingerprint}`");
        text.AppendLine($"- Macro violations: {macroViolations}");
        text.AppendLine($"- Station Micro: {stationAnalysis.Status}; conflicts={stationAnalysis.Conflicts.Count}; Unknown={unknownCount}");
        text.AppendLine($"- Independent Micro: {independentMicro.Status}; conflicts={independentMicro.Conflicts.Count}");
        text.AppendLine($"- Fixed saved assignments: valid={fixedValidation.IsValid}; {savedAssignments}/{expectedAssignments}");
        text.AppendLine($"- Assignment identity: valid={identity.IsValid}");
        text.AppendLine($"- Serialized fingerprint matches: {fingerprintMatches}");
        text.AppendLine();
        text.AppendLine("The verifier fixed the saved Macro variables and checked the exact saved resource assignments. It did not accept a newly selected resource plan as a substitute.");
        return text.ToString();
    }
}
