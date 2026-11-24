// 依赖扩张计划用类型化记录表示 IIS 指向的区间先后、车站事件、时间、停站和附加时分决策。
// Overlay 把这些释放项叠加到基础修复邻域，使模型仅放开导致不可行的依赖闭包，而不是整体解除固定变量。
namespace GurobiTimetablingResearch.Optimization;

internal enum IisDependencyType
{
    SectionPrecedence,
    StationEventPrecedence,
    TemporalEvent,
    FlexibleStop,
    RunningSupplement
}

internal sealed record IisDependencyRelease(
    IisDependencyType DependencyType,
    string VariableKey,
    string ReleaseSource,
    string SourceIisElement,
    int ExpansionRound,
    string Reason,
    int ExtraTemporalSeconds = 0,
    int? TrainI = null,
    int? TrainJ = null,
    string Station = "",
    string Section = "",
    string Event = "",
    int? StationOrder = null,
    bool WasAlreadyReleased = false,
    bool WillRelease = true,
    bool BlockedByHardRule = false)
{
    internal string Identity => $"{DependencyType}::{VariableKey}";
}

internal sealed record DependencyExpansionPlan(
    IReadOnlyList<IisDependencyRelease> ProposedReleases,
    bool BlockedByOriginalHardConstraint,
    IReadOnlyList<string> UnsupportedBlockers,
    string ExpansionReason,
    IReadOnlySet<string>? DerivedOvertakingAuxiliaryKeys = null,
    IReadOnlyList<IisDependencyRelease>? BlockedDependencies = null)
{
    internal IReadOnlySet<string> EffectiveDerivedOvertakingAuxiliaryKeys { get; } =
        DerivedOvertakingAuxiliaryKeys ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    internal IReadOnlyList<IisDependencyRelease> EffectiveBlockedDependencies { get; } =
        BlockedDependencies ?? Array.Empty<IisDependencyRelease>();

    internal IReadOnlySet<int> AdditionalTrainIndices => ProposedReleases
        .SelectMany(item => new[] { item.TrainI, item.TrainJ })
        .Where(item => item.HasValue)
        .Select(item => item!.Value)
        .ToHashSet();

    internal IReadOnlySet<int> AdditionalStationOrders => ProposedReleases
        .Where(item => item.StationOrder.HasValue)
        .Select(item => item.StationOrder!.Value)
        .ToHashSet();
}

public sealed class IisDependencyOverlay
{
    private readonly IReadOnlyDictionary<string, IisDependencyRelease> _releases;

    private IisDependencyOverlay(IReadOnlyDictionary<string, IisDependencyRelease> releases)
    {
        _releases = releases;
    }

    internal static IisDependencyOverlay Empty { get; } = new(
        new Dictionary<string, IisDependencyRelease>(StringComparer.OrdinalIgnoreCase));

    internal IReadOnlyDictionary<string, IisDependencyRelease> ReleasesByIdentity => _releases;

    internal IReadOnlySet<string> SectionPrecedenceKeys => ResolveKeys(IisDependencyType.SectionPrecedence);
    internal IReadOnlySet<string> StationEventPrecedenceKeys => ResolveKeys(IisDependencyType.StationEventPrecedence);
    internal IReadOnlySet<string> TemporalEventKeys => ResolveKeys(IisDependencyType.TemporalEvent);
    internal IReadOnlySet<string> FlexibleStopKeys => ResolveKeys(IisDependencyType.FlexibleStop);
    internal IReadOnlySet<string> SupplementSectionKeys => ResolveKeys(IisDependencyType.RunningSupplement);

    internal IReadOnlyDictionary<string, int> ExtraTemporalSecondsByEventKey => _releases.Values
        .Where(item => item.DependencyType == IisDependencyType.TemporalEvent)
        .ToDictionary(
            item => item.VariableKey,
            item => item.ExtraTemporalSeconds,
            StringComparer.OrdinalIgnoreCase);

    internal IisDependencyOverlay Union(DependencyExpansionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Dictionary<string, IisDependencyRelease> result = _releases.ToDictionary(
            item => item.Key,
            item => item.Value,
            StringComparer.OrdinalIgnoreCase);
        foreach (IisDependencyRelease release in plan.ProposedReleases.Where(item => item.WillRelease))
        {
            if (!string.Equals(release.ReleaseSource, "IIS", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(release.SourceIisElement) ||
                release.ExpansionRound <= 0)
            {
                throw new InvalidOperationException(
                    $"Overlay dependency '{release.Identity}' lacks valid IIS provenance.");
            }
            if (release.BlockedByHardRule)
            {
                throw new InvalidOperationException(
                    $"Hard-blocked dependency '{release.Identity}' cannot enter the approved overlay.");
            }

            if (result.TryGetValue(release.Identity, out IisDependencyRelease? existing))
            {
                if (release.DependencyType != existing.DependencyType)
                {
                    throw new InvalidOperationException(
                        $"Dependency '{release.VariableKey}' changed type across expansion rounds.");
                }
                result[release.Identity] = release.DependencyType == IisDependencyType.TemporalEvent &&
                                           release.ExtraTemporalSeconds > existing.ExtraTemporalSeconds
                    ? release
                    : existing;
            }
            else
            {
                result.Add(release.Identity, release);
            }
        }
        return new IisDependencyOverlay(result);
    }

    internal bool Contains(IisDependencyType type, string key) =>
        _releases.ContainsKey($"{type}::{key}");

    private IReadOnlySet<string> ResolveKeys(IisDependencyType type) => _releases.Values
        .Where(item => item.DependencyType == type)
        .Select(item => item.VariableKey)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
}

internal static class RepairDecisionDependencyOverlayResolver
{
    internal static IisDependencyOverlay Resolve(RepairDecisionNeighborhood neighborhood) =>
        neighborhood.DependencyOverlay ?? IisDependencyOverlay.Empty;
}
