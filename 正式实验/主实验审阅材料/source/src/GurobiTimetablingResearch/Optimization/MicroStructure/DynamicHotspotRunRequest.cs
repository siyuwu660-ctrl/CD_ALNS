using System.Text.Json;

namespace GurobiTimetablingResearch.Optimization;

public sealed record DynamicHotspotModelGate(
    int MaximumVariables = 110_000,
    int MaximumActiveBinaries = 35_000,
    int MaximumConstraints = 525_000,
    int MaximumPresolvedActiveBinaries = 32_000,
    double MaximumBuildAndPresolveSeconds = 120.0);

public sealed record DynamicHotspotResolvedRun(
    string BaselineDirectory,
    string OutputDirectory,
    string DatasetDirectory,
    string MacroSnapshotPath,
    string PatternBundlePath,
    string ProofPath,
    string MacroFingerprint,
    double ProbeSeconds,
    DynamicHotspotModelGate Gate);

public sealed record DynamicHotspotRunRequest(
    string BaselineDirectory,
    string OutputDirectory,
    double ProbeSeconds = 300.0,
    DynamicHotspotModelGate? Gate = null,
    string? ProofPath = null)
{
    public DynamicHotspotResolvedRun Resolve()
    {
        if (ProbeSeconds <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(ProbeSeconds));
        string baseline = Path.GetFullPath(BaselineDirectory);
        string output = Path.GetFullPath(OutputDirectory);
        string summaryPath = Path.Combine(baseline, "run_summary.json");
        if (!File.Exists(summaryPath))
            throw new FileNotFoundException("The selected baseline has no run_summary.json.", summaryPath);

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(summaryPath));
        JsonElement input = RequireProperty(document.RootElement, "input", summaryPath);
        string dataset = ReadPath(input, "dataset", summaryPath);
        string snapshot = ReadPath(input, "sourcePath", summaryPath);
        string bundle = ReadPath(input, "bundlePath", summaryPath);
        string fingerprint = ReadString(input, "macroFingerprint", summaryPath);
        string proof = string.IsNullOrWhiteSpace(ProofPath)
            ? Path.Combine(baseline, "state", "last_master_infeasibility_proof.json")
            : Path.GetFullPath(ProofPath);

        RequireDirectory(dataset, "dataset");
        RequireFile(snapshot, "saved Macro snapshot");
        RequireFile(bundle, "saved pattern bundle");
        RequireFile(proof, "attempt-local proof");
        if (string.IsNullOrWhiteSpace(fingerprint))
            throw new InvalidDataException($"{summaryPath}: macroFingerprint is empty.");

        DynamicHotspotModelGate gate = Gate ?? new DynamicHotspotModelGate();
        if (gate.MaximumVariables <= 0 || gate.MaximumActiveBinaries <= 0 ||
            gate.MaximumConstraints <= 0 || gate.MaximumPresolvedActiveBinaries <= 0 ||
            gate.MaximumBuildAndPresolveSeconds <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(Gate), "Every model gate must be positive.");

        return new DynamicHotspotResolvedRun(
            baseline,
            output,
            dataset,
            snapshot,
            bundle,
            Path.GetFullPath(proof),
            fingerprint,
            ProbeSeconds,
            gate);
    }

    private static JsonElement RequireProperty(JsonElement parent, string name, string source)
    {
        if (!parent.TryGetProperty(name, out JsonElement value))
            throw new InvalidDataException($"{source}: missing property '{name}'.");
        return value;
    }

    private static string ReadPath(JsonElement parent, string name, string source) =>
        Path.GetFullPath(ReadString(parent, name, source));

    private static string ReadString(JsonElement parent, string name, string source)
    {
        JsonElement value = RequireProperty(parent, name, source);
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"{source}: property '{name}' is not a string.");
        return value.GetString() ?? string.Empty;
    }

    private static void RequireDirectory(string path, string identity)
    {
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException($"Recorded {identity} does not exist: {path}");
    }

    private static void RequireFile(string path, string identity)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Recorded {identity} does not exist.", path);
    }
}
