// 自适应单候选池把结构冲突候选按直接层、IIS 层和扩展层分组，并以规范化身份及稳定哈希消除重复项。
// 池描述对象保存候选来源、排序和层级边界，使多轮扫描能够复现同一候选集合并准确续接检查点。
using System.Security.Cryptography;
using System.Text;

namespace GurobiTimetablingResearch.Optimization;

internal sealed record AdaptiveCandidateSelectionConfiguration(
    int InitialCap,
    int ExpandedCap,
    int PerStationCap,
    int PerPairCap,
    string SelectorVersion)
{
    internal AdaptiveCandidateSelectionConfiguration Validate()
    {
        if (InitialCap <= 0 || ExpandedCap < InitialCap ||
            PerStationCap <= 0 || PerPairCap <= 0 ||
            string.IsNullOrWhiteSpace(SelectorVersion))
        {
            throw new ArgumentException("Adaptive candidate selection configuration is invalid.");
        }
        return this;
    }
}

internal sealed record AdaptiveCandidateDescriptor(
    string CandidateId,
    string CandidateType,
    string ResourceIdentity,
    string StationIdentity,
    int FirstTrainIndex,
    int SecondTrainIndex,
    string? FirstEventSide,
    string? SecondEventSide,
    double Score,
    int SourceOrder)
{
    internal string CanonicalCandidateId => AdaptiveCanonicalCandidateIdentity.Create(this);
}

internal sealed record AdaptiveCandidatePoolDescriptor(
    IReadOnlyList<AdaptiveCandidateDescriptor> Universe,
    IReadOnlyList<AdaptiveCandidateDescriptor> Initial,
    IReadOnlyList<AdaptiveCandidateDescriptor> Expanded,
    IReadOnlyList<AdaptiveCandidateDescriptor> ExpandedDelta,
    string CandidateUniverseFingerprint,
    string SelectionConfigurationFingerprint);

internal static class AdaptiveCanonicalCandidateIdentity
{
    internal static string Create(AdaptiveCandidateDescriptor candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        CanonicalTrainPair pair = CanonicalTrainPair.Create(
            candidate.FirstTrainIndex,
            candidate.SecondTrainIndex);
        string resource = Normalize(candidate.ResourceIdentity);
        if (string.Equals(candidate.CandidateType, "section", StringComparison.OrdinalIgnoreCase))
        {
            return $"section::{resource}::{pair.LowerTrainIndex}::{pair.UpperTrainIndex}";
        }

        string firstSide = candidate.FirstEventSide?.Trim() ?? string.Empty;
        string secondSide = candidate.SecondEventSide?.Trim() ?? string.Empty;
        string directedEvents = candidate.FirstTrainIndex <= candidate.SecondTrainIndex
            ? $"{candidate.FirstTrainIndex}::{firstSide}::{candidate.SecondTrainIndex}::{secondSide}"
            : $"{candidate.SecondTrainIndex}::{secondSide}::{candidate.FirstTrainIndex}::{firstSide}";
        return $"station::{resource}::{directedEvents}";
    }

    private static string Normalize(string value) =>
        value.Trim().Replace('_', '-').ToUpperInvariant();
}

internal static class AdaptiveCandidatePoolBuilder
{
    internal static AdaptiveCandidatePoolDescriptor BuildNested(
        IReadOnlyList<AdaptiveCandidateDescriptor> candidateUniverse,
        IReadOnlyList<AdaptiveCandidateDescriptor> fixedInitialSelection,
        AdaptiveCandidateSelectionConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(candidateUniverse);
        ArgumentNullException.ThrowIfNull(fixedInitialSelection);
        configuration.Validate();

        List<AdaptiveCandidateDescriptor> universe = CanonicalDistinct(candidateUniverse);
        Dictionary<string, AdaptiveCandidateDescriptor> universeByCanonical = universe.ToDictionary(
            item => item.CanonicalCandidateId,
            StringComparer.OrdinalIgnoreCase);
        List<AdaptiveCandidateDescriptor> initial = [];
        HashSet<string> selectedIds = new(StringComparer.OrdinalIgnoreCase);
        foreach (AdaptiveCandidateDescriptor raw in fixedInitialSelection)
        {
            string canonical = raw.CanonicalCandidateId;
            if (!selectedIds.Add(canonical))
            {
                continue;
            }
            if (!universeByCanonical.TryGetValue(canonical, out AdaptiveCandidateDescriptor? selected))
            {
                throw new ArgumentException(
                    $"Initial candidate '{raw.CandidateId}' is absent from the frozen universe.");
            }
            initial.Add(selected);
            if (initial.Count == configuration.InitialCap)
            {
                break;
            }
        }

        List<AdaptiveCandidateDescriptor> expanded = initial.ToList();
        Dictionary<string, int> stationCounts = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<CanonicalTrainPair, int> pairCounts = [];
        Dictionary<string, int> typeCounts = new(StringComparer.OrdinalIgnoreCase);
        foreach (AdaptiveCandidateDescriptor selected in expanded)
        {
            IncrementCounts(selected, stationCounts, pairCounts, typeCounts);
        }

        List<AdaptiveCandidateDescriptor> remaining = universe
            .Where(item => !selectedIds.Contains(item.CanonicalCandidateId))
            .ToList();
        while (expanded.Count < configuration.ExpandedCap && remaining.Count > 0)
        {
            AdaptiveCandidateDescriptor? next = remaining
                .Where(item => stationCounts.GetValueOrDefault(item.StationIdentity) < configuration.PerStationCap &&
                    pairCounts.GetValueOrDefault(CanonicalTrainPair.Create(
                        item.FirstTrainIndex, item.SecondTrainIndex)) < configuration.PerPairCap)
                .OrderBy(item => stationCounts.GetValueOrDefault(item.StationIdentity))
                .ThenBy(item => typeCounts.GetValueOrDefault(item.CandidateType))
                .ThenBy(item => pairCounts.GetValueOrDefault(CanonicalTrainPair.Create(
                    item.FirstTrainIndex, item.SecondTrainIndex)))
                .ThenByDescending(item => item.Score)
                .ThenBy(item => item.SourceOrder)
                .ThenBy(item => item.CanonicalCandidateId, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            next ??= remaining
                .OrderBy(item => stationCounts.GetValueOrDefault(item.StationIdentity))
                .ThenBy(item => typeCounts.GetValueOrDefault(item.CandidateType))
                .ThenByDescending(item => item.Score)
                .ThenBy(item => item.SourceOrder)
                .ThenBy(item => item.CanonicalCandidateId, StringComparer.OrdinalIgnoreCase)
                .First();

            expanded.Add(next);
            selectedIds.Add(next.CanonicalCandidateId);
            IncrementCounts(next, stationCounts, pairCounts, typeCounts);
            remaining.Remove(next);
        }

        HashSet<string> initialIds = initial
            .Select(item => item.CanonicalCandidateId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<AdaptiveCandidateDescriptor> delta = expanded
            .Where(item => !initialIds.Contains(item.CanonicalCandidateId))
            .ToList();

        return new AdaptiveCandidatePoolDescriptor(
            universe,
            initial,
            expanded,
            delta,
            FingerprintUniverse(universe),
            FingerprintConfiguration(configuration));
    }

    private static List<AdaptiveCandidateDescriptor> CanonicalDistinct(
        IReadOnlyList<AdaptiveCandidateDescriptor> candidates) => candidates
        .GroupBy(item => item.CanonicalCandidateId, StringComparer.OrdinalIgnoreCase)
        .Select(group => group
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.SourceOrder)
            .ThenBy(item => item.CandidateId, StringComparer.OrdinalIgnoreCase)
            .First())
        .OrderBy(item => item.SourceOrder)
        .ThenBy(item => item.CanonicalCandidateId, StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static void IncrementCounts(
        AdaptiveCandidateDescriptor candidate,
        Dictionary<string, int> stationCounts,
        Dictionary<CanonicalTrainPair, int> pairCounts,
        Dictionary<string, int> typeCounts)
    {
        stationCounts[candidate.StationIdentity] =
            stationCounts.GetValueOrDefault(candidate.StationIdentity) + 1;
        CanonicalTrainPair pair = CanonicalTrainPair.Create(
            candidate.FirstTrainIndex, candidate.SecondTrainIndex);
        pairCounts[pair] = pairCounts.GetValueOrDefault(pair) + 1;
        typeCounts[candidate.CandidateType] =
            typeCounts.GetValueOrDefault(candidate.CandidateType) + 1;
    }

    internal static string FingerprintCandidateIds(
        IReadOnlyList<AdaptiveCandidateDescriptor> candidates) =>
        Sha256(string.Join('\n', candidates
            .Select(item => item.CanonicalCandidateId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)));

    internal static string FingerprintUniverse(IReadOnlyList<AdaptiveCandidateDescriptor> universe) =>
        Sha256(string.Join('\n', universe
            .OrderBy(item => item.CanonicalCandidateId, StringComparer.OrdinalIgnoreCase)
            .Select(item => string.Join('|',
                item.CanonicalCandidateId,
                item.CandidateType,
                item.ResourceIdentity,
                item.StationIdentity,
                item.FirstEventSide ?? string.Empty,
                item.SecondEventSide ?? string.Empty,
                item.Score.ToString("R", System.Globalization.CultureInfo.InvariantCulture)))));

    internal static string FingerprintConfiguration(
        AdaptiveCandidateSelectionConfiguration configuration) =>
        Sha256(string.Join('|',
            configuration.SelectorVersion,
            configuration.InitialCap,
            configuration.ExpandedCap,
            configuration.PerStationCap,
            configuration.PerPairCap));

    private static string Sha256(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
