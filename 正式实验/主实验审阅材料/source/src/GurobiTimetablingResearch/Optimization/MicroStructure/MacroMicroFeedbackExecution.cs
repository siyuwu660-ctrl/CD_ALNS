using System.Text;
using System.Text.Json;

namespace GurobiTimetablingResearch.Optimization;

internal enum MacroMicroExecutionMode
{
    None,
    Fresh,
    Resume
}

internal sealed record MacroMicroExecutionOptions(
    MacroMicroExecutionMode Mode,
    string? OutputDirectory,
    string? CheckpointPath,
    double TotalBudgetSeconds,
    double IterationTimeLimitSeconds,
    double SaveReserveSeconds,
    int RollingNeighborRadius,
    double FullMacroTimeLimitSeconds)
{
    internal bool IsRequested => Mode != MacroMicroExecutionMode.None;

    internal static MacroMicroExecutionOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        bool fresh = HasFlag(args, "--macro-micro-fresh");
        bool resume = HasFlag(args, "--macro-micro-resume");
        if (fresh && resume)
            throw new ArgumentException("--macro-micro-fresh and --macro-micro-resume are mutually exclusive.");

        double totalBudget = ReadPositiveDouble(args, "--macro-micro-total-budget-seconds=") ?? 7200.0;
        double iteration = ReadPositiveDouble(args, "--macro-micro-iteration-seconds=") ?? 300.0;
        double reserve = ReadPositiveDouble(args, "--macro-micro-save-reserve-seconds=") ?? 120.0;
        int rollingNeighborRadius = ReadPositiveInt(args, "--macro-micro-rolling-radius=") ?? 4;
        double fullMacroTimeLimit = ReadPositiveDouble(
            args,
            "--macro-micro-full-macro-time-limit-seconds=") ?? 1200.0;
        if (reserve >= totalBudget)
            throw new ArgumentException("Macro-Micro save reserve must be smaller than the cumulative budget.");

        if (!fresh && !resume)
            return new(
                MacroMicroExecutionMode.None,
                null,
                null,
                totalBudget,
                iteration,
                reserve,
                rollingNeighborRadius,
                fullMacroTimeLimit);

        string? output = ReadString(args, "--macro-micro-output=");
        string? checkpoint = ReadString(args, "--macro-micro-checkpoint=");
        if (fresh && string.IsNullOrWhiteSpace(output))
            throw new ArgumentException("--macro-micro-fresh requires --macro-micro-output=<directory>.");
        if (resume && string.IsNullOrWhiteSpace(checkpoint))
            throw new ArgumentException("--macro-micro-resume requires --macro-micro-checkpoint=<file>.");

        return new(
            fresh ? MacroMicroExecutionMode.Fresh : MacroMicroExecutionMode.Resume,
            string.IsNullOrWhiteSpace(output) ? null : Path.GetFullPath(output),
            string.IsNullOrWhiteSpace(checkpoint) ? null : Path.GetFullPath(checkpoint),
            totalBudget,
            iteration,
            reserve,
            rollingNeighborRadius,
            fullMacroTimeLimit);
    }

    private static bool HasFlag(IReadOnlyList<string> args, string flag) =>
        args.Any(arg => string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase));

    private static string? ReadString(IReadOnlyList<string> args, string prefix)
    {
        string? raw = args.FirstOrDefault(arg => arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (raw is null) return null;
        string value = raw[prefix.Length..].Trim().Trim('"');
        return value.Length == 0 ? null : value;
    }

    private static double? ReadPositiveDouble(IReadOnlyList<string> args, string prefix)
    {
        string? raw = ReadString(args, prefix);
        if (raw is null) return null;
        if (!double.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double value) || value <= 0.0)
            throw new ArgumentOutOfRangeException(prefix, $"{prefix} requires a positive number.");
        return value;
    }

    private static int? ReadPositiveInt(IReadOnlyList<string> args, string prefix)
    {
        string? raw = ReadString(args, prefix);
        if (raw is null) return null;
        if (!int.TryParse(raw, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out int value) || value <= 0)
            throw new ArgumentOutOfRangeException(prefix, $"{prefix} requires a positive integer.");
        return value;
    }
}

internal sealed record MacroMicroFreshBootstrapPlan(
    int RollingNeighborRadius,
    int FullMacroNeighborRadius,
    double FullMacroTimeLimitSeconds)
{
    internal bool RequiresCompleteMacroSolve => FullMacroNeighborRadius != RollingNeighborRadius;

    internal static MacroMicroFreshBootstrapPlan Create(MacroMicroExecutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Mode != MacroMicroExecutionMode.Fresh)
            throw new ArgumentException("A fresh bootstrap plan requires --macro-micro-fresh.", nameof(options));
        return new(
            options.RollingNeighborRadius,
            int.MaxValue,
            options.FullMacroTimeLimitSeconds);
    }
}

internal sealed record MacroMicroFreshSeedAcceptance(
    bool IsAccepted,
    int UnmodeledRelationViolationCount,
    int ModeledRelationViolationCount,
    int OtherBusinessViolationCount,
    string Reason);

internal static class MacroMicroFreshSeedAcceptancePolicy
{
    internal static MacroMicroFreshSeedAcceptance Evaluate(
        MacroBaselineSummary summary,
        MasterValidationReport validation)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(validation);
        IReadOnlyList<SectionRelationValidationFinding> findings =
            validation.SectionRelationFindings ?? [];
        int unmodeled = findings
            .Where(finding => !finding.WasModeled)
            .Sum(finding => finding.Violations.Count);
        int modeled = findings
            .Where(finding => finding.WasModeled)
            .Sum(finding => finding.Violations.Count);
        int other = Math.Max(0, validation.Violations.Count - unmodeled - modeled);
        bool constructionComplete =
            summary.AdmittedTrainCount == summary.TotalTrainCount &&
            summary.CompletedQuotaStageCount == summary.TotalQuotaStageCount &&
            summary.SolutionCount > 0;
        bool accepted = constructionComplete && modeled == 0 && other == 0;
        string reason = accepted
            ? "RADIUS_ROLLING_SEED_ACCEPTED_UNMODELED_COMPLETE_RELATIONS_DEFERRED"
            : !constructionComplete
                ? "RADIUS_ROLLING_SEED_INCOMPLETE"
                : modeled > 0
                    ? "RADIUS_ROLLING_SEED_MODELED_RELATION_VIOLATION"
                    : "RADIUS_ROLLING_SEED_BUSINESS_VIOLATION";
        return new(accepted, unmodeled, modeled, other, reason);
    }
}

internal enum MacroMicroFeedbackCheckpointStage
{
    MacroReady,
    FeedbackLoop,
    JointFeasible,
    Terminal
}

internal sealed record MacroMicroFeedbackCheckpoint(
    int FormatVersion,
    DateTimeOffset SavedAt,
    MacroMicroFeedbackCheckpointStage Stage,
    string InstanceName,
    string DatasetDirectory,
    string BaselineDirectory,
    string OutputDirectory,
    string CurrentCandidatePath,
    string CurrentCandidateFingerprint,
    double RunningTimeSupplementRatio,
    int EndpointOccupationSeconds,
    double InitialMacroSeconds,
    double ConsumedFeedbackSeconds,
    int NextIteration,
    DynamicHotspotEvidence? CurrentEvidence,
    MacroRepairFeedback[] PermanentFeedback,
    MacroRepairFeedback[] TemporarySearchFeedback,
    MacroRepairFeedback? PendingMacroFeedback,
    MacroMicroFeedbackAttempt[] Attempts,
    string[] AttemptedMathematicalStates,
    string Status,
    string CodeIdentity)
{
    internal const int CurrentFormatVersion = 1;

    internal static MacroMicroFeedbackCheckpoint CreateMacroReady(
        string instanceName,
        string datasetDirectory,
        string outputDirectory,
        string macroSnapshotPath,
        string macroFingerprint,
        double runningTimeSupplementRatio,
        int endpointOccupationSeconds,
        double initialMacroSeconds) => new(
        CurrentFormatVersion,
        DateTimeOffset.Now,
        MacroMicroFeedbackCheckpointStage.MacroReady,
        instanceName,
        Path.GetFullPath(datasetDirectory),
        string.Empty,
        Path.GetFullPath(outputDirectory),
        Path.GetFullPath(macroSnapshotPath),
        macroFingerprint,
        runningTimeSupplementRatio,
        endpointOccupationSeconds,
        initialMacroSeconds,
        0.0,
        1,
        null,
        [],
        [],
        null,
        [],
        [],
        "macro_ready",
        typeof(MacroMicroFeedbackCheckpoint).Assembly.ManifestModule.ModuleVersionId.ToString("D"));
}

internal static class MacroMicroFeedbackCheckpointStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    internal static void SaveAtomic(string path, MacroMicroFeedbackCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidDataException("Checkpoint path has no parent directory.");
        Directory.CreateDirectory(directory);
        string temporary = fullPath + ".tmp";
        File.WriteAllText(
            temporary,
            JsonSerializer.Serialize(checkpoint with { SavedAt = DateTimeOffset.Now }, JsonOptions),
            new UTF8Encoding(false));
        File.Move(temporary, fullPath, overwrite: true);
    }

    internal static MacroMicroFeedbackCheckpoint Load(
        string path,
        string expectedInstanceName,
        string expectedDatasetDirectory)
    {
        string fullPath = Path.GetFullPath(path);
        MacroMicroFeedbackCheckpoint checkpoint = JsonSerializer.Deserialize<MacroMicroFeedbackCheckpoint>(
            File.ReadAllText(fullPath, Encoding.UTF8), JsonOptions)
            ?? throw new InvalidDataException("Macro-Micro checkpoint is empty or invalid.");
        if (checkpoint.FormatVersion != MacroMicroFeedbackCheckpoint.CurrentFormatVersion)
            throw new InvalidDataException(
                $"Unsupported Macro-Micro checkpoint format {checkpoint.FormatVersion}.");
        if (!string.Equals(checkpoint.InstanceName, expectedInstanceName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"Checkpoint instance '{checkpoint.InstanceName}' does not match '{expectedInstanceName}'.");
        string expectedDataset = NormalizeDirectory(expectedDatasetDirectory);
        string recordedDataset = NormalizeDirectory(checkpoint.DatasetDirectory);
        if (!string.Equals(recordedDataset, expectedDataset, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"Checkpoint dataset '{recordedDataset}' does not match '{expectedDataset}'.");
        if (!File.Exists(checkpoint.CurrentCandidatePath))
            throw new FileNotFoundException(
                "Checkpoint Macro candidate is missing.", checkpoint.CurrentCandidatePath);
        return checkpoint;
    }

    internal static string DefaultPath(string outputDirectory) =>
        Path.Combine(Path.GetFullPath(outputDirectory), "state", "macro_micro_feedback.checkpoint.json");

    private static string NormalizeDirectory(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
