// 检查点模型保存主问题快照、活动冲突边、轮次预算、自适应指标及已提升微观组件，是中断续跑的完整状态载体。
// 存储层使用带版本的 JSON 信封区分格式演进，恢复工厂将旧版本字段归一为 CheckerResumeState 后再交给求解循环。
using System.Text.Json;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

public sealed record CheckerCheckpoint(
    int LastAcceptedIteration,
    MasterSolutionSnapshot Snapshot,
    IReadOnlyList<SectionConflictEdgeRequest> ActiveSectionEdges,
    IReadOnlyList<StationEventConflictCandidate> ActiveStationEventEdges,
    IReadOnlySet<string> AcceptedStructuralCandidateIds,
    bool ZeroConflictMode,
    int EndpointOccupationSeconds,
    string SourceIdentity,
    DateTimeOffset CreatedAtUtc,
    double RepairRunningTimeSupplementRatio = 0.05,
    IReadOnlyList<PromotedMicroComponent>? PromotedMicroComponents = null,
    RepairDecisionLevel? RepairDecisionLevel = null,
    string NormalDwellModelVersion = "normal-dwell-v1",
    IReadOnlyList<string>? CompatibilityWarnings = null,
    bool AdaptiveSingletonMode = false,
    MasterSolutionSnapshot? InitialReferenceSnapshot = null,
    string InitialReferenceSnapshotFingerprint = "",
    string AcceptedStateFingerprint = "",
    string MicroConflictFingerprint = "",
    string SearchConfigurationFingerprint = "",
    string CandidateUniverseFingerprint = "",
    IReadOnlyList<string>? InitialCandidateIds = null,
    IReadOnlyList<string>? ExpandedCandidateIds = null,
    IReadOnlyList<AdaptiveUnknownEvidence>? UnknownEvidence = null,
    string TerminationReason = "",
    int FormatVersion = 3,
    AdaptiveCheckpointMicroMetrics? MicroMetrics = null,
    string InitialCandidatePoolFingerprint = "",
    string ExpandedCandidatePoolFingerprint = "",
    int InitialCandidateCap = 0,
    int ExpandedCandidateCap = 0,
    int SafetyAcceptedTransitionCap = 0,
    string LastWinnerLayer = "",
    string LastWinnerCandidateId = "",
    AdaptiveCheckpointQualityMetrics? LastWinnerQuality = null,
    string StagnationScope = "",
    CheckerCoordinationStrategy CoordinationStrategy = CheckerCoordinationStrategy.Legacy,
    MasterSolutionSnapshot? BestDiagnosticSolution = null,
    MasterSolutionSnapshot? BestJointFeasibleSolution = null,
    IReadOnlyList<ValidatedMicroFeedback>? PermanentMicroFeedback = null);

public sealed record AdaptiveCheckpointMicroMetrics(
    int RawConflictCount,
    int CanonicalConflictCount,
    int RootConflictCount,
    int EpisodeConflictCount,
    int ExactConflictCount,
    int AssignmentConflictCount,
    int ConflictedStationCount,
    int AffectedTrainCount,
    int ConnectedComponentCount);

public sealed record AdaptiveCheckpointQualityMetrics(
    double StepTotalRetimingSeconds,
    double InitialTotalRetimingSeconds,
    double StepMaximumRetimingSeconds,
    double InitialMaximumRetimingSeconds,
    double IncrementalRunningSupplementSeconds,
    double CurrentTotalRunningSupplementSeconds,
    int CurrentSupplementSectionCount);

internal sealed record CheckerResumeState(
    int IterationOffset,
    MasterSolutionSnapshot? InitialSnapshot,
    IReadOnlyList<SectionConflictEdgeRequest> SectionEdges,
    IReadOnlyList<StationEventConflictCandidate> StationEdges,
    IReadOnlySet<string> AcceptedCandidateIds);

internal static class CheckerResumeStateFactory
{
    internal static CheckerResumeState Create(
        MasterSolutionSnapshot? legacySeed,
        CheckerCheckpoint? checkpoint)
    {
        if (legacySeed is not null && checkpoint is not null)
        {
            throw new ArgumentException("A Checker legacy seed and checkpoint cannot be supplied together.");
        }

        return checkpoint is null
            ? new CheckerResumeState(
                0,
                legacySeed,
                Array.Empty<SectionConflictEdgeRequest>(),
                Array.Empty<StationEventConflictCandidate>(),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase))
            : new CheckerResumeState(
                checkpoint.LastAcceptedIteration,
                checkpoint.Snapshot,
                checkpoint.ActiveSectionEdges.ToList(),
                checkpoint.ActiveStationEventEdges.ToList(),
                checkpoint.AcceptedStructuralCandidateIds.ToHashSet(StringComparer.OrdinalIgnoreCase));
    }
}

internal enum CheckerInitialCarrierMode
{
    OptimizeSeed,
    FixedCheckpointSnapshot
}

internal static class CheckerInitialCarrierModeResolver
{
    internal static CheckerInitialCarrierMode Resolve(CheckerResumeState resumeState) =>
        resumeState.InitialSnapshot is not null
            ? CheckerInitialCarrierMode.FixedCheckpointSnapshot
            : CheckerInitialCarrierMode.OptimizeSeed;
}

public sealed class CheckerIterationBudget
{
    private readonly int _maxCompletedIterations;

    public CheckerIterationBudget(int maxCompletedIterations)
    {
        _maxCompletedIterations = maxCompletedIterations > 0
            ? maxCompletedIterations
            : throw new ArgumentOutOfRangeException(nameof(maxCompletedIterations));
    }

    public void EnsureCanStart(int completedIterations)
    {
        if (completedIterations < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(completedIterations));
        }
        if (completedIterations >= _maxCompletedIterations)
        {
            throw new InvalidOperationException(
                $"Checker iteration budget {_maxCompletedIterations} is exhausted.");
        }
    }
}

public enum CheckerResumeInputMode
{
    None,
    ImportArtifacts,
    LoadCheckpoint
}

public sealed record CheckerResumeInput(
    CheckerResumeInputMode Mode,
    string? ImportArtifactsDirectory,
    string? CheckpointInputPath,
    string? CheckpointOutputPath);

public static class CheckerResumeInputResolver
{
    public static CheckerResumeInput Resolve(
        string? importArtifactsDirectory,
        string? checkpointInputPath,
        string? checkpointOutputPath)
    {
        if (!string.IsNullOrWhiteSpace(importArtifactsDirectory) &&
            !string.IsNullOrWhiteSpace(checkpointInputPath))
        {
            throw new ArgumentException(
                "Checker artifact import and checkpoint input are mutually exclusive.");
        }
        if (!string.IsNullOrWhiteSpace(importArtifactsDirectory) &&
            string.IsNullOrWhiteSpace(checkpointOutputPath))
        {
            throw new ArgumentException("Checker artifact import requires a checkpoint output path.");
        }

        CheckerResumeInputMode mode = !string.IsNullOrWhiteSpace(importArtifactsDirectory)
            ? CheckerResumeInputMode.ImportArtifacts
            : !string.IsNullOrWhiteSpace(checkpointInputPath)
                ? CheckerResumeInputMode.LoadCheckpoint
                : CheckerResumeInputMode.None;
        return new CheckerResumeInput(
            mode,
            importArtifactsDirectory,
            checkpointInputPath,
            checkpointOutputPath);
    }
}

public static class CheckerCheckpointStore
{
    private const int CurrentLegacyFormatVersion = 2;
    private const int CurrentSchemaVersion = 3;
    private const int CurrentAdaptiveFormatVersion = 3;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static void SaveAtomic(string path, string instanceName, CheckerCheckpoint checkpoint)
    {
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string acceptedFingerprint = string.IsNullOrWhiteSpace(checkpoint.AcceptedStateFingerprint)
            ? SnapshotFingerprint.Calculate(checkpoint.Snapshot)
            : checkpoint.AcceptedStateFingerprint;
        string initialReferenceFingerprint = checkpoint.InitialReferenceSnapshot is null
            ? string.Empty
            : string.IsNullOrWhiteSpace(checkpoint.InitialReferenceSnapshotFingerprint)
                ? SnapshotFingerprint.Calculate(checkpoint.InitialReferenceSnapshot)
                : checkpoint.InitialReferenceSnapshotFingerprint;
        CheckpointV3Envelope envelope = new(
            CurrentSchemaVersion,
            instanceName,
            checkpoint.LastAcceptedIteration,
            SnapshotEnvelope.From(checkpoint.Snapshot),
            acceptedFingerprint,
            checkpoint.MicroConflictFingerprint,
            checkpoint.InitialReferenceSnapshot is null
                ? null
                : SnapshotEnvelope.From(checkpoint.InitialReferenceSnapshot),
            initialReferenceFingerprint,
            checkpoint.SearchConfigurationFingerprint,
            checkpoint.CandidateUniverseFingerprint,
            checkpoint.InitialCandidateIds?.Order(StringComparer.OrdinalIgnoreCase).ToList() ?? [],
            checkpoint.ExpandedCandidateIds?.Order(StringComparer.OrdinalIgnoreCase).ToList() ?? [],
            checkpoint.ActiveSectionEdges.ToList(),
            checkpoint.ActiveStationEventEdges.ToList(),
            checkpoint.AcceptedStructuralCandidateIds.Order(StringComparer.OrdinalIgnoreCase).ToList(),
            checkpoint.ZeroConflictMode,
            checkpoint.EndpointOccupationSeconds,
            checkpoint.SourceIdentity,
            checkpoint.CreatedAtUtc,
            checkpoint.RepairRunningTimeSupplementRatio,
            checkpoint.PromotedMicroComponents?.ToList() ?? [],
            checkpoint.RepairDecisionLevel,
            checkpoint.NormalDwellModelVersion,
            checkpoint.CompatibilityWarnings?.ToList() ?? [],
            checkpoint.AdaptiveSingletonMode,
            checkpoint.UnknownEvidence?.ToList() ?? [],
            checkpoint.TerminationReason,
            "current_invocation_wall_clock",
            checkpoint.FormatVersion,
            checkpoint.MicroMetrics,
            checkpoint.InitialCandidatePoolFingerprint,
            checkpoint.ExpandedCandidatePoolFingerprint,
            checkpoint.InitialCandidateCap,
            checkpoint.ExpandedCandidateCap,
            checkpoint.SafetyAcceptedTransitionCap,
            checkpoint.LastWinnerLayer,
            checkpoint.LastWinnerCandidateId,
            checkpoint.LastWinnerQuality,
            checkpoint.StagnationScope,
            checkpoint.CoordinationStrategy,
            checkpoint.BestDiagnosticSolution is null
                ? null
                : SnapshotEnvelope.From(checkpoint.BestDiagnosticSolution),
            checkpoint.BestJointFeasibleSolution is null
                ? null
                : SnapshotEnvelope.From(checkpoint.BestJointFeasibleSolution),
            checkpoint.PermanentMicroFeedback?.ToList() ?? []);
        ValidateV3Integrity(envelope);
        string temporaryPath = fullPath + $".tmp-{Guid.NewGuid():N}";
        string previousPath = BuildPreviousPath(fullPath);
        string previousTemporaryPath = previousPath + $".tmp-{Guid.NewGuid():N}";
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
            using (FileStream stream = new(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            CheckpointV3Envelope verified = JsonSerializer.Deserialize<CheckpointV3Envelope>(
                File.ReadAllText(temporaryPath), JsonOptions)
                ?? throw new InvalidDataException("Temporary Checker checkpoint is empty or invalid.");
            ValidateV3Integrity(verified);
            if (File.Exists(fullPath))
            {
                File.Copy(fullPath, previousTemporaryPath, overwrite: false);
                CheckpointV3Envelope previous = JsonSerializer.Deserialize<CheckpointV3Envelope>(
                    File.ReadAllText(previousTemporaryPath), JsonOptions)
                    ?? throw new InvalidDataException("Previous Checker checkpoint is empty or invalid.");
                ValidateV3Integrity(previous);
                File.Move(previousTemporaryPath, previousPath, overwrite: true);
            }
            File.Move(temporaryPath, fullPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
            if (File.Exists(previousTemporaryPath))
            {
                File.Delete(previousTemporaryPath);
            }
        }
    }

    private static string BuildPreviousPath(string fullPath)
    {
        string directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
        return Path.Combine(
            directory,
            Path.GetFileNameWithoutExtension(fullPath) + ".previous" + Path.GetExtension(fullPath));
    }

    public static CheckerCheckpoint Load(
        string path,
        TimetablingInstance instance,
        bool expectedZeroConflictMode,
        int expectedEndpointOccupationSeconds,
        double? expectedRepairRunningTimeSupplementRatio = null,
        bool adaptiveMode = false,
        MasterSolutionSnapshot? explicitLegacyInitialReference = null,
        CheckerCoordinationStrategy expectedCoordinationStrategy = CheckerCoordinationStrategy.Legacy,
        string? expectedSearchConfigurationFingerprint = null)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = document.RootElement;
        if (root.TryGetProperty("schemaVersion", out JsonElement schemaVersionElement))
        {
            int schemaVersion = schemaVersionElement.GetInt32();
            if (schemaVersion != CurrentSchemaVersion)
            {
                throw new InvalidDataException($"Unsupported Checker checkpoint schema {schemaVersion}.");
            }
            CheckpointV3Envelope v3 = JsonSerializer.Deserialize<CheckpointV3Envelope>(
                root.GetRawText(), JsonOptions)
                ?? throw new InvalidDataException($"Checker checkpoint '{path}' is empty or invalid.");
            ValidateV3Integrity(v3);
            return LoadV3(
                v3,
                instance,
                expectedZeroConflictMode,
                expectedEndpointOccupationSeconds,
                expectedRepairRunningTimeSupplementRatio,
                adaptiveMode,
                expectedCoordinationStrategy,
                expectedSearchConfigurationFingerprint);
        }
        if (!root.TryGetProperty("formatVersion", out _))
        {
            throw new InvalidDataException("Checker checkpoint has no recognized schema version field.");
        }
        CheckpointEnvelope envelope = JsonSerializer.Deserialize<CheckpointEnvelope>(
            root.GetRawText(), JsonOptions)
            ?? throw new InvalidDataException($"Checker checkpoint '{path}' is empty or invalid.");
        if (envelope.FormatVersion is < 1 or > CurrentLegacyFormatVersion)
        {
            throw new InvalidDataException($"Unsupported Checker checkpoint format {envelope.FormatVersion}.");
        }
        if (expectedCoordinationStrategy != CheckerCoordinationStrategy.Legacy)
        {
            throw new InvalidDataException(
                "A legacy Checker checkpoint has no compatible coordination-strategy identity.");
        }
        if (!string.IsNullOrWhiteSpace(expectedSearchConfigurationFingerprint))
        {
            throw new InvalidDataException(
                "A legacy Checker checkpoint has no compatible search-configuration fingerprint.");
        }
        if (!string.Equals(envelope.InstanceName, instance.Meta.InstanceName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Checker checkpoint instance does not match the loaded dataset.");
        }
        if (envelope.ZeroConflictMode != expectedZeroConflictMode)
        {
            throw new InvalidDataException("Checker checkpoint zero-conflict mode does not match.");
        }
        if (envelope.EndpointOccupationSeconds != expectedEndpointOccupationSeconds)
        {
            throw new InvalidDataException("Checker checkpoint endpoint occupation setting does not match.");
        }
        if (envelope.FormatVersion >= 2 && expectedRepairRunningTimeSupplementRatio.HasValue &&
            Math.Abs(envelope.RunningTimeSupplementRatio - expectedRepairRunningTimeSupplementRatio.Value) > 1e-12)
        {
            throw new InvalidDataException("Checker checkpoint running-time supplement ratio does not match.");
        }

        ValidateSnapshot(instance, envelope.Snapshot);

        ValidateEdges(instance, envelope.ActiveSectionEdges, envelope.ActiveStationEventEdges);
        List<string> compatibilityWarnings = envelope.CompatibilityWarnings?.ToList() ?? [];
        if (envelope.FormatVersion == 1)
        {
            compatibilityWarnings.Add(
                "legacy_checkpoint_assumes_empty_promotion_default_ratio_and_normal_dwell_v1; regenerate from Macro/Rolling if dwell semantics matter");
        }
        CheckerCheckpoint legacy = new(
            envelope.LastAcceptedIteration,
            envelope.Snapshot.ToSnapshot(),
            envelope.ActiveSectionEdges,
            envelope.ActiveStationEventEdges,
            envelope.AcceptedStructuralCandidateIds.ToHashSet(StringComparer.OrdinalIgnoreCase),
            envelope.ZeroConflictMode,
            envelope.EndpointOccupationSeconds,
            envelope.SourceIdentity,
            envelope.CreatedAtUtc,
            envelope.RunningTimeSupplementRatio,
            envelope.PromotedMicroComponents ?? [],
            envelope.RepairDecisionLevel,
            envelope.NormalDwellModelVersion ?? "normal-dwell-v1",
            compatibilityWarnings);
        if (!adaptiveMode)
        {
            return legacy;
        }
        if (explicitLegacyInitialReference is null)
        {
            throw new InvalidDataException(
                "Adaptive resume from a legacy checkpoint requires an explicit initial reference snapshot.");
        }
        ValidateSnapshot(instance, SnapshotEnvelope.From(explicitLegacyInitialReference));
        return legacy with
        {
            AdaptiveSingletonMode = true,
            InitialReferenceSnapshot = explicitLegacyInitialReference,
            InitialReferenceSnapshotFingerprint = SnapshotFingerprint.Calculate(explicitLegacyInitialReference),
            AcceptedStateFingerprint = SnapshotFingerprint.Calculate(legacy.Snapshot),
            CompatibilityWarnings = compatibilityWarnings
                .Append("adaptive_legacy_resume_uses_explicit_initial_reference")
                .ToList()
        };
    }

    private static CheckerCheckpoint LoadV3(
        CheckpointV3Envelope envelope,
        TimetablingInstance instance,
        bool expectedZeroConflictMode,
        int expectedEndpointOccupationSeconds,
        double? expectedRepairRunningTimeSupplementRatio,
        bool adaptiveMode,
        CheckerCoordinationStrategy expectedCoordinationStrategy,
        string? expectedSearchConfigurationFingerprint)
    {
        if (!string.Equals(envelope.InstanceName, instance.Meta.InstanceName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Checker checkpoint instance does not match the loaded dataset.");
        }
        if (envelope.ZeroConflictMode != expectedZeroConflictMode)
        {
            throw new InvalidDataException("Checker checkpoint zero-conflict mode does not match.");
        }
        if (envelope.EndpointOccupationSeconds != expectedEndpointOccupationSeconds)
        {
            throw new InvalidDataException("Checker checkpoint endpoint occupation setting does not match.");
        }
        if (envelope.CoordinationStrategy != expectedCoordinationStrategy)
        {
            throw new InvalidDataException(
                $"Checker checkpoint coordination strategy '{envelope.CoordinationStrategy}' does not match " +
                $"requested strategy '{expectedCoordinationStrategy}'.");
        }
        if (!string.IsNullOrWhiteSpace(expectedSearchConfigurationFingerprint) &&
            !string.Equals(
                envelope.SearchConfigurationFingerprint,
                expectedSearchConfigurationFingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Checker checkpoint search configuration fingerprint does not match the current invocation.");
        }
        if (expectedRepairRunningTimeSupplementRatio.HasValue &&
            Math.Abs(envelope.RunningTimeSupplementRatio -
                     expectedRepairRunningTimeSupplementRatio.Value) > 1e-12)
        {
            throw new InvalidDataException("Checker checkpoint running-time supplement ratio does not match.");
        }
        ValidateSnapshot(instance, envelope.AcceptedSnapshot);
        if (envelope.InitialReferenceSnapshot is not null)
        {
            ValidateSnapshot(instance, envelope.InitialReferenceSnapshot);
        }
        if (envelope.BestDiagnosticSolution is not null)
        {
            ValidateSnapshot(instance, envelope.BestDiagnosticSolution);
            if (envelope.BestDiagnosticSolution.SolutionRole != TimetableSolutionRole.BestDiagnosticSolution)
            {
                throw new InvalidDataException("Checker checkpoint diagnostic solution has an invalid role.");
            }
        }
        if (envelope.BestJointFeasibleSolution is not null)
        {
            ValidateSnapshot(instance, envelope.BestJointFeasibleSolution);
            if (envelope.BestJointFeasibleSolution.SolutionRole != TimetableSolutionRole.BestJointFeasibleSolution)
            {
                throw new InvalidDataException("Checker checkpoint joint-feasible solution has an invalid role.");
            }
        }
        if (adaptiveMode &&
            (!envelope.AdaptiveSingletonMode || envelope.InitialReferenceSnapshot is null))
        {
            throw new InvalidDataException(
                "Adaptive checkpoint resume requires a persisted immutable initial reference snapshot.");
        }
        ValidateEdges(instance, envelope.ActiveSectionEdges, envelope.ActiveStationEventEdges);
        return new CheckerCheckpoint(
            envelope.LastAcceptedTransitionIndex,
            envelope.AcceptedSnapshot.ToSnapshot(),
            envelope.ActiveSectionEdges,
            envelope.ActiveStationEventEdges,
            envelope.AcceptedStructuralCandidateIds.ToHashSet(StringComparer.OrdinalIgnoreCase),
            envelope.ZeroConflictMode,
            envelope.EndpointOccupationSeconds,
            envelope.SourceIdentity,
            envelope.CreatedAtUtc,
            envelope.RunningTimeSupplementRatio,
            envelope.PromotedMicroComponents ?? [],
            envelope.RepairDecisionLevel,
            envelope.NormalDwellModelVersion ?? "normal-dwell-v1",
            envelope.CompatibilityWarnings ?? [],
            envelope.AdaptiveSingletonMode,
            envelope.InitialReferenceSnapshot?.ToSnapshot(),
            envelope.InitialReferenceSnapshotFingerprint,
            envelope.AcceptedStateFingerprint,
            envelope.MicroConflictFingerprint,
            envelope.SearchConfigurationFingerprint,
            envelope.CandidateUniverseFingerprint,
            envelope.InitialCandidateIds ?? [],
            envelope.ExpandedCandidateIds ?? [],
            envelope.UnknownEvidence ?? [],
            envelope.TerminationReason,
            envelope.FormatVersion,
            envelope.MicroMetrics,
            envelope.InitialCandidatePoolFingerprint,
            envelope.ExpandedCandidatePoolFingerprint,
            envelope.InitialCandidateCap,
            envelope.ExpandedCandidateCap,
            envelope.SafetyAcceptedTransitionCap,
            envelope.LastWinnerLayer,
            envelope.LastWinnerCandidateId,
            envelope.LastWinnerQuality,
            envelope.StagnationScope,
            envelope.CoordinationStrategy,
            envelope.BestDiagnosticSolution?.ToSnapshot(),
            envelope.BestJointFeasibleSolution?.ToSnapshot(),
            envelope.PermanentMicroFeedback ?? []);
    }

    private static void ValidateV3Integrity(CheckpointV3Envelope envelope)
    {
        if (envelope.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException($"Unsupported Checker checkpoint schema {envelope.SchemaVersion}.");
        }
        if (envelope.FormatVersion != CurrentAdaptiveFormatVersion)
        {
            throw new InvalidDataException($"Unsupported Checker checkpoint v3 format {envelope.FormatVersion}.");
        }
        string actualAcceptedFingerprint = SnapshotFingerprint.Calculate(
            envelope.AcceptedSnapshot.ToSnapshot());
        if (!string.Equals(actualAcceptedFingerprint, envelope.AcceptedStateFingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Checker checkpoint accepted-state fingerprint mismatch.");
        }
        if (envelope.InitialReferenceSnapshot is not null)
        {
            string actualInitialFingerprint = SnapshotFingerprint.Calculate(
                envelope.InitialReferenceSnapshot.ToSnapshot());
            if (!string.Equals(actualInitialFingerprint,
                    envelope.InitialReferenceSnapshotFingerprint, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Checker checkpoint initial-reference fingerprint mismatch.");
            }
        }
        else if (envelope.AdaptiveSingletonMode)
        {
            throw new InvalidDataException(
                "Adaptive Checker checkpoint has no immutable initial reference snapshot.");
        }
        ValidateOptionalFingerprint(envelope.MicroConflictFingerprint, "Micro conflict");
        ValidateOptionalFingerprint(envelope.SearchConfigurationFingerprint, "search configuration");
        ValidateOptionalFingerprint(envelope.CandidateUniverseFingerprint, "candidate universe");
        ValidateOptionalFingerprint(envelope.InitialCandidatePoolFingerprint, "Initial candidate pool");
        ValidateOptionalFingerprint(envelope.ExpandedCandidatePoolFingerprint, "Expanded candidate pool");
        if (envelope.AdaptiveSingletonMode)
        {
            if (envelope.MicroMetrics is null)
            {
                throw new InvalidDataException("Adaptive Checker checkpoint has no accepted Micro metrics.");
            }
            if (envelope.InitialCandidateCap <= 0 ||
                envelope.ExpandedCandidateCap < envelope.InitialCandidateCap ||
                envelope.SafetyAcceptedTransitionCap <= 0)
            {
                throw new InvalidDataException("Adaptive Checker checkpoint candidate or safety caps are invalid.");
            }
        }
    }

    private static void ValidateOptionalFingerprint(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException($"Checker checkpoint {name} fingerprint is invalid.");
        }
    }

    private static void ValidateSnapshot(TimetablingInstance instance, SnapshotEnvelope snapshot)
    {
        HashSet<int> expectedTrains = instance.Trains.Select(train => train.TrainIndex).ToHashSet();
        if (!snapshot.AdmittedTrainIndices.ToHashSet().SetEquals(expectedTrains))
        {
            throw new InvalidDataException("Checker checkpoint train set does not match the loaded dataset.");
        }
        HashSet<string> expectedStops = instance.TrainStops
            .Select(stop => stop.StopId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!snapshot.ArrivalSecondsByStopId.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(expectedStops) ||
            !snapshot.DepartureSecondsByStopId.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(expectedStops) ||
            !snapshot.StopByStopId.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(expectedStops))
        {
            throw new InvalidDataException("Checker checkpoint stop set does not match the loaded dataset.");
        }

        Dictionary<string, TrainStopRecord> stopById = instance.TrainStops.ToDictionary(
            stop => stop.StopId,
            StringComparer.OrdinalIgnoreCase);
        Dictionary<int, RouteRecord> routeById = instance.Routes.ToDictionary(route => route.RouteId);
        string snapshotFingerprint = SnapshotFingerprint.Calculate(snapshot.ToSnapshot());
        foreach (VerifiedMicroAssignmentWitness witness in snapshot.VerifiedMicroAssignments ?? [])
        {
            if (!stopById.TryGetValue(witness.RequirementId, out TrainStopRecord? stop) ||
                stop.TrainIndex != witness.TrainIndex ||
                !string.Equals(stop.StationName, witness.StationName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Checker checkpoint Micro witness '{witness.RequirementId}' does not match the loaded dataset.");
            }
            if (!string.Equals(
                    witness.VerificationScope,
                    "station_micro_validated",
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Checker checkpoint Micro witness '{witness.RequirementId}' is not a validated witness.");
            }
            if (!string.IsNullOrWhiteSpace(witness.CandidateSnapshotFingerprint) &&
                !string.Equals(
                    witness.CandidateSnapshotFingerprint,
                    snapshotFingerprint,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Checker checkpoint Micro witness '{witness.RequirementId}' belongs to a different candidate snapshot.");
            }
            if (witness.ArrivalSeconds.HasValue &&
                Math.Abs(witness.ArrivalSeconds.Value - snapshot.ArrivalSecondsByStopId[witness.RequirementId]) > 1e-6 ||
                witness.DepartureSeconds.HasValue &&
                Math.Abs(witness.DepartureSeconds.Value - snapshot.DepartureSecondsByStopId[witness.RequirementId]) > 1e-6)
            {
                throw new InvalidDataException(
                    $"Checker checkpoint Micro witness '{witness.RequirementId}' does not match the saved timetable times.");
            }
            ValidateWitnessRoute(routeById, witness.IncomingRouteId, witness, "incoming");
            ValidateWitnessRoute(routeById, witness.OutgoingRouteId, witness, "outgoing");
        }
    }

    private static void ValidateWitnessRoute(
        IReadOnlyDictionary<int, RouteRecord> routeById,
        int? routeId,
        VerifiedMicroAssignmentWitness witness,
        string side)
    {
        if (!routeId.HasValue)
        {
            return;
        }
        if (!routeById.TryGetValue(routeId.Value, out RouteRecord? route) ||
            !string.Equals(route.StationName, witness.StationName, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(route.TrackName, witness.TrackName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Checker checkpoint Micro witness '{witness.RequirementId}' has an invalid {side} route {routeId.Value}.");
        }
    }

    private static void ValidateEdges(
        TimetablingInstance instance,
        IReadOnlyList<SectionConflictEdgeRequest> sectionEdges,
        IReadOnlyList<StationEventConflictCandidate> stationEdges)
    {
        HashSet<int> trainIndices = instance.Trains.Select(train => train.TrainIndex).ToHashSet();
        HashSet<string> sectionNames = instance.Sections.Select(section => section.SectionName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (SectionConflictEdgeRequest edge in sectionEdges)
        {
            if (!sectionNames.Contains(edge.SectionName) ||
                !trainIndices.Contains(edge.FirstTrainIndex) ||
                !trainIndices.Contains(edge.SecondTrainIndex))
            {
                throw new InvalidDataException($"Checker checkpoint contains an invalid section edge '{edge}'.");
            }
        }

        foreach (StationEventConflictCandidate edge in stationEdges)
        {
            bool firstExists = instance.TrainStops.Any(stop =>
                stop.TrainIndex == edge.FirstTrainIndex &&
                stop.StationOrder == edge.FirstStationOrder &&
                string.Equals(stop.StationName, edge.StationName, StringComparison.OrdinalIgnoreCase));
            bool secondExists = instance.TrainStops.Any(stop =>
                stop.TrainIndex == edge.SecondTrainIndex &&
                stop.StationOrder == edge.SecondStationOrder &&
                string.Equals(stop.StationName, edge.StationName, StringComparison.OrdinalIgnoreCase));
            if (!firstExists || !secondExists)
            {
                throw new InvalidDataException($"Checker checkpoint contains an invalid station-event edge '{edge}'.");
            }
        }
    }

    private sealed record CheckpointEnvelope(
        int FormatVersion,
        string InstanceName,
        int LastAcceptedIteration,
        SnapshotEnvelope Snapshot,
        List<SectionConflictEdgeRequest> ActiveSectionEdges,
        List<StationEventConflictCandidate> ActiveStationEventEdges,
        List<string> AcceptedStructuralCandidateIds,
        bool ZeroConflictMode,
        int EndpointOccupationSeconds,
        string SourceIdentity,
        DateTimeOffset CreatedAtUtc,
        double RunningTimeSupplementRatio = 0.05,
        List<PromotedMicroComponent>? PromotedMicroComponents = null,
        RepairDecisionLevel? RepairDecisionLevel = null,
        string? NormalDwellModelVersion = null,
        List<string>? CompatibilityWarnings = null);

    private sealed record CheckpointV3Envelope(
        int SchemaVersion,
        string InstanceName,
        int LastAcceptedTransitionIndex,
        SnapshotEnvelope AcceptedSnapshot,
        string AcceptedStateFingerprint,
        string MicroConflictFingerprint,
        SnapshotEnvelope? InitialReferenceSnapshot,
        string InitialReferenceSnapshotFingerprint,
        string SearchConfigurationFingerprint,
        string CandidateUniverseFingerprint,
        List<string> InitialCandidateIds,
        List<string> ExpandedCandidateIds,
        List<SectionConflictEdgeRequest> ActiveSectionEdges,
        List<StationEventConflictCandidate> ActiveStationEventEdges,
        List<string> AcceptedStructuralCandidateIds,
        bool ZeroConflictMode,
        int EndpointOccupationSeconds,
        string SourceIdentity,
        DateTimeOffset CreatedAtUtc,
        double RunningTimeSupplementRatio = 0.05,
        List<PromotedMicroComponent>? PromotedMicroComponents = null,
        RepairDecisionLevel? RepairDecisionLevel = null,
        string? NormalDwellModelVersion = null,
        List<string>? CompatibilityWarnings = null,
        bool AdaptiveSingletonMode = false,
        List<AdaptiveUnknownEvidence>? UnknownEvidence = null,
        string TerminationReason = "",
        string RuntimeBudgetSemantics = "current_invocation_wall_clock",
        int FormatVersion = CurrentAdaptiveFormatVersion,
        AdaptiveCheckpointMicroMetrics? MicroMetrics = null,
        string InitialCandidatePoolFingerprint = "",
        string ExpandedCandidatePoolFingerprint = "",
        int InitialCandidateCap = 0,
        int ExpandedCandidateCap = 0,
        int SafetyAcceptedTransitionCap = 0,
        string LastWinnerLayer = "",
        string LastWinnerCandidateId = "",
        AdaptiveCheckpointQualityMetrics? LastWinnerQuality = null,
        string StagnationScope = "",
        CheckerCoordinationStrategy CoordinationStrategy = CheckerCoordinationStrategy.Legacy,
        SnapshotEnvelope? BestDiagnosticSolution = null,
        SnapshotEnvelope? BestJointFeasibleSolution = null,
        List<ValidatedMicroFeedback>? PermanentMicroFeedback = null);

    private sealed record SnapshotEnvelope(
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
        List<VerifiedMicroAssignmentWitness>? VerifiedMicroAssignments = null)
    {
        internal static SnapshotEnvelope From(MasterSolutionSnapshot snapshot) => new(
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
            snapshot.VerifiedMicroAssignments?.ToList() ?? []);

        internal MasterSolutionSnapshot ToSnapshot() => new(
            ObjectiveValue,
            new Dictionary<string, double>(ArrivalSecondsByStopId, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, double>(DepartureSecondsByStopId, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, int>(StopByStopId, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, int>(SectionPrecedenceByKey, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, int>(StationEventPrecedenceByKey, StringComparer.OrdinalIgnoreCase),
            AdmittedTrainIndices.ToHashSet(),
            new Dictionary<string, double>(
                RunningTimeSupplementSecondsByKey ?? new Dictionary<string, double>(),
                StringComparer.OrdinalIgnoreCase),
            BusinessObjective,
            SolutionRole,
            BoundReport,
            VerifiedMicroAssignments ?? []);
    }
}
