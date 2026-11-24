using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

internal sealed record StationPattern(
    string PatternId,
    string StationName,
    string DataVersion,
    string RuleVersion,
    string DependencyIdentity,
    DateTimeOffset ImportedAt,
    MicroWitnessCertificate Certificate)
{
    internal static StationPattern Create(MicroWitnessCertificate certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        string canonical = string.Join('\n', new[]
        {
            $"station={certificate.StationName}",
            $"data={certificate.DataVersion}",
            $"rule={certificate.RuleVersion}",
            $"tolerance={certificate.CheckerToleranceSeconds}",
            string.Join('|', certificate.Witnesses
                .OrderBy(item => item.RequirementId, StringComparer.OrdinalIgnoreCase)
                .Select(item => string.Join(':', item.RequirementId, item.TrainIndex,
                    item.IsStop ? 1 : 0, item.TrackName,
                    item.IncomingRouteId?.ToString(CultureInfo.InvariantCulture) ?? "-",
                    item.OutgoingRouteId?.ToString(CultureInfo.InvariantCulture) ?? "-"))),
            string.Join('|', certificate.Conditions
                .OrderBy(item => ConditionCanonical(item), StringComparer.Ordinal)
                .Select(ConditionCanonical))
        });
        string id = Hash(canonical);
        string dependency = Hash(string.Join('|', certificate.DataVersion, certificate.RuleVersion,
            certificate.CheckerToleranceSeconds.ToString(CultureInfo.InvariantCulture)));
        return new(id, certificate.StationName, certificate.DataVersion, certificate.RuleVersion,
            dependency, DateTimeOffset.Now, certificate with
            {
                Witnesses = certificate.Witnesses.ToArray(),
                Conditions = certificate.Conditions.ToArray()
            });
    }

    private static string ConditionCanonical(MicroCertificateCondition item) => string.Join(':',
        item.Kind, item.LaterStopId, item.LaterEvent,
        item.EarlierStopId ?? "-", item.EarlierEvent?.ToString() ?? "-",
        item.MinimumDifferenceSeconds.ToString("R", CultureInfo.InvariantCulture),
        item.RequiredStopValue?.ToString(CultureInfo.InvariantCulture) ?? "-",
        item.SourceRuleId,
        item.LaterOffsetSeconds.ToString("R", CultureInfo.InvariantCulture),
        item.EarlierOffsetSeconds.ToString("R", CultureInfo.InvariantCulture));

    internal static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

/// <summary>
/// Incremental station-pattern store.  A pattern is keyed only by its discrete business
/// structure and rule/data dependencies; source times and source snapshot identity remain
/// provenance and never split an otherwise identical mode.
/// </summary>
internal sealed class StationPatternPool
{
    private const int FormatVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private readonly string _directory;
    private readonly Dictionary<string, StationPattern> _patterns = new(StringComparer.Ordinal);

    internal StationPatternPool(string directory, string instanceName, string macroFingerprint)
    {
        _directory = Path.GetFullPath(directory);
        InstanceName = instanceName;
        MacroFingerprint = macroFingerprint;
    }

    internal string InstanceName { get; }
    internal string MacroFingerprint { get; }
    internal int Count => _patterns.Count;
    internal IReadOnlyList<StationPattern> Patterns => _patterns.Values
        .OrderBy(item => item.StationName, StringComparer.OrdinalIgnoreCase)
        .ThenBy(item => item.PatternId, StringComparer.Ordinal).ToArray();

    internal bool Add(StationPattern pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        StationPattern canonical = StationPattern.Create(pattern.Certificate);
        if (!string.Equals(canonical.PatternId, pattern.PatternId, StringComparison.Ordinal))
            throw new InvalidDataException($"Pattern identity mismatch for '{pattern.PatternId}'.");
        return _patterns.TryAdd(pattern.PatternId, pattern);
    }

    internal IReadOnlyList<StationPattern> GetPatterns(string station) => _patterns.Values
        .Where(item => string.Equals(item.StationName, station, StringComparison.OrdinalIgnoreCase))
        .OrderBy(item => item.PatternId, StringComparer.Ordinal).ToArray();

    internal void Save()
    {
        Directory.CreateDirectory(_directory);
        string patternsDirectory = Path.Combine(_directory, "patterns");
        Directory.CreateDirectory(patternsDirectory);
        foreach (StationPattern pattern in Patterns)
        {
            string path = Path.Combine(patternsDirectory, $"{pattern.PatternId}.json");
            if (!File.Exists(path))
                File.WriteAllText(path, JsonSerializer.Serialize(pattern, JsonOptions), new UTF8Encoding(false));
        }
        PatternPoolIndex index = new(FormatVersion, InstanceName, MacroFingerprint,
            Patterns.Select(item => new PatternPoolIndexItem(item.PatternId, item.StationName,
                $"patterns/{item.PatternId}.json")).ToArray());
        File.WriteAllText(Path.Combine(_directory, "index.json"),
            JsonSerializer.Serialize(index, JsonOptions), new UTF8Encoding(false));
    }

    internal static StationPatternPool Load(string directory, string instanceName, string macroFingerprint)
    {
        directory = Path.GetFullPath(directory);
        PatternPoolIndex index = JsonSerializer.Deserialize<PatternPoolIndex>(
            File.ReadAllText(Path.Combine(directory, "index.json"), Encoding.UTF8), JsonOptions)
            ?? throw new InvalidDataException("Pattern-pool index is empty.");
        if (index.FormatVersion != FormatVersion ||
            !string.Equals(index.InstanceName, instanceName, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(index.MacroFingerprint, macroFingerprint, StringComparison.Ordinal))
            throw new InvalidDataException("Pattern-pool identity does not match the requested baseline.");
        StationPatternPool pool = new(directory, instanceName, macroFingerprint);
        foreach (PatternPoolIndexItem item in index.Patterns)
        {
            StationPattern pattern = JsonSerializer.Deserialize<StationPattern>(
                File.ReadAllText(Path.Combine(directory, item.RelativePath.Replace('/', Path.DirectorySeparatorChar)),
                    Encoding.UTF8), JsonOptions)
                ?? throw new InvalidDataException($"Pattern '{item.PatternId}' is empty.");
            if (!pool.Add(pattern)) throw new InvalidDataException($"Duplicate pattern '{item.PatternId}'.");
        }
        pool.VerifyReadOnlyContent();
        return pool;
    }

    internal void VerifyReadOnlyContent()
    {
        foreach (StationPattern pattern in _patterns.Values)
        {
            string actual = StationPattern.Create(pattern.Certificate).PatternId;
            if (!string.Equals(actual, pattern.PatternId, StringComparison.Ordinal))
                throw new InvalidDataException($"Pattern '{pattern.PatternId}' failed read-only identity verification.");
            if (pattern.Certificate.Witnesses.Count == 0)
                throw new InvalidDataException($"Pattern '{pattern.PatternId}' has no resource assignments.");
        }
    }

    private sealed record PatternPoolIndex(
        int FormatVersion,
        string InstanceName,
        string MacroFingerprint,
        PatternPoolIndexItem[] Patterns);
    private sealed record PatternPoolIndexItem(string PatternId, string StationName, string RelativePath);
}

internal readonly record struct PatternTimeEventKey(string StopId, StationEventSide EventSide)
{
    public override string ToString() => $"{StopId}:{EventSide}";
}

internal readonly record struct PatternTimeBounds(double LowerBound, double UpperBound);

internal sealed record PatternDifferenceConstraint(
    PatternTimeEventKey Left,
    PatternTimeEventKey Right,
    double UpperBound,
    string SupportId,
    string BusinessRuleId);

internal sealed record PatternTimeDomain(
    bool IsFeasible,
    IReadOnlyList<PatternDifferenceConstraint> Constraints,
    IReadOnlyDictionary<PatternTimeEventKey, PatternTimeBounds> Bounds,
    IReadOnlyList<string> InfeasibleSupportIds,
    bool RequiresResidualLp)
{
    internal bool Contains(IReadOnlyDictionary<PatternTimeEventKey, double> point, double tolerance = 1e-7)
    {
        foreach ((PatternTimeEventKey key, PatternTimeBounds bounds) in Bounds)
        {
            if (!point.TryGetValue(key, out double value) ||
                value < bounds.LowerBound - tolerance || value > bounds.UpperBound + tolerance) return false;
        }
        return Constraints.All(row => point.TryGetValue(row.Left, out double left) &&
                                      point.TryGetValue(row.Right, out double right) &&
                                      left - right <= row.UpperBound + tolerance);
    }
}

internal static class PatternTimeDomainCompiler
{
    private static readonly PatternTimeEventKey Origin = new("@origin", StationEventSide.Incoming);

    internal static PatternDifferenceConstraint CompileCondition(MicroPatternTemporalCondition condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        // (second + secondOffset) - (first + firstOffset) >= gap
        // is exactly first - second <= secondOffset - firstOffset - gap.
        return new(
            new(condition.FirstStopId, condition.FirstEventSide),
            new(condition.SecondStopId, condition.SecondEventSide),
            condition.SecondOffsetSeconds - condition.FirstOffsetSeconds - condition.MinimumGapSeconds,
            condition.ConditionId,
            condition.BusinessRuleId);
    }

    internal static PatternTimeDomain Compile(
        IReadOnlyList<MicroPatternTemporalCondition> conditions,
        IReadOnlyDictionary<PatternTimeEventKey, PatternTimeBounds> bounds,
        bool requiresResidualLp = false)
    {
        PatternDifferenceConstraint[] rows = conditions.Select(CompileCondition).ToArray();
        List<Edge> edges = rows.Select(row => new Edge(row.Right, row.Left, row.UpperBound, row.SupportId)).ToList();
        foreach ((PatternTimeEventKey key, PatternTimeBounds range) in bounds)
        {
            if (range.LowerBound > range.UpperBound)
                return new(false, rows, bounds, [$"bound:{key}"], requiresResidualLp);
            edges.Add(new(Origin, key, range.UpperBound, $"ub:{key}"));
            edges.Add(new(key, Origin, -range.LowerBound, $"lb:{key}"));
        }
        PatternTimeEventKey[] vertices = edges.SelectMany(edge => new[] { edge.From, edge.To })
            .Append(Origin).Distinct().ToArray();
        Dictionary<PatternTimeEventKey, double> distance = vertices.ToDictionary(item => item, _ => 0.0);
        Dictionary<PatternTimeEventKey, Edge> predecessor = [];
        PatternTimeEventKey? changed = null;
        for (int iteration = 0; iteration < vertices.Length; iteration++)
        {
            changed = null;
            foreach (Edge edge in edges)
            {
                if (distance[edge.To] <= distance[edge.From] + edge.Weight + 1e-10) continue;
                distance[edge.To] = distance[edge.From] + edge.Weight;
                predecessor[edge.To] = edge;
                changed = edge.To;
            }
            if (changed is null) break;
        }
        if (changed is null) return new(true, rows, bounds, [], requiresResidualLp);

        PatternTimeEventKey cursor = changed.Value;
        for (int i = 0; i < vertices.Length; i++)
            if (predecessor.TryGetValue(cursor, out Edge? edge)) cursor = edge.From;
        HashSet<string> support = new(StringComparer.Ordinal);
        PatternTimeEventKey start = cursor;
        do
        {
            if (!predecessor.TryGetValue(cursor, out Edge? edge)) break;
            support.Add(edge.SupportId);
            cursor = edge.From;
        } while (!cursor.Equals(start) && support.Count <= vertices.Length + 1);
        return new(false, rows, bounds, support.Order(StringComparer.Ordinal).ToArray(), requiresResidualLp);
    }

    private sealed record Edge(
        PatternTimeEventKey From,
        PatternTimeEventKey To,
        double Weight,
        string SupportId);
}

internal sealed record PatternCombinationConflict(
    string ConflictId,
    string ProofIdentity,
    IReadOnlyDictionary<string, string> RequiredPatternsByStation,
    IReadOnlyDictionary<string, int> RequiredDiscreteLiterals,
    IReadOnlyDictionary<string, string> RequiredDependencies,
    DateTimeOffset CreatedAt)
{
    internal static PatternCombinationConflict Create(
        string proofIdentity,
        IReadOnlyDictionary<string, string> patterns,
        IReadOnlyDictionary<string, int> literals,
        IReadOnlyDictionary<string, string> dependencies)
    {
        string canonical = string.Join('\n',
            patterns.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                .Select(item => $"pattern:{item.Key}={item.Value}")
                .Concat(literals.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(item => $"literal:{item.Key}={item.Value}"))
                .Concat(dependencies.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(item => $"dependency:{item.Key}={item.Value}")));
        return new(StationPattern.Hash(canonical), proofIdentity,
            new Dictionary<string, string>(patterns, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, int>(literals, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(dependencies, StringComparer.OrdinalIgnoreCase),
            DateTimeOffset.Now);
    }

    internal bool Applies(
        IReadOnlyDictionary<string, string> patterns,
        IReadOnlyDictionary<string, int> literals,
        IReadOnlyDictionary<string, string> dependencies) =>
        RequiredPatternsByStation.All(item => patterns.TryGetValue(item.Key, out string? value) &&
                                                     string.Equals(value, item.Value, StringComparison.Ordinal)) &&
        RequiredDiscreteLiterals.All(item => literals.TryGetValue(item.Key, out int value) && value == item.Value) &&
        RequiredDependencies.All(item => dependencies.TryGetValue(item.Key, out string? value) &&
                                               string.Equals(value, item.Value, StringComparison.Ordinal));
}
