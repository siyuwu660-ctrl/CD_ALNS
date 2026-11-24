using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace GurobiTimetablingResearch.Optimization;

/// <summary>
/// 用站点、规范列车集合、进路股道域和证明上下文标识一个联合分配不可行核心。
/// 时间区间和停站相关域保留在证明上下文中，避免把固定时刻下的失败误判为同一结构核心。
/// </summary>
public sealed record AssignmentCoreIdentity(
    string IdentityKey,
    string StationName,
    string CanonicalTrainSet,
    string ResourceDomainSignature,
    string ProofContext);

public sealed record AssignmentCoreOccurrence(
    AssignmentCoreIdentity Identity,
    int ConsecutiveRounds);

/// <summary>
/// 从 Global Micro 的联合分配失败证据生成稳定身份；证据缺失的部分使用 unavailable，
/// 不通过冲突数量或站名补猜核心成员。
/// </summary>
public static class AssignmentCoreIdentityAnalyzer
{
    public static AssignmentCoreIdentity Analyze(MicroConflictExplanation conflict)
    {
        ArgumentNullException.ThrowIfNull(conflict);
        if (!string.Equals(
                conflict.ConflictKind,
                "station_route_track_assignment_infeasible",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The conflict is not a joint route-track assignment core.", nameof(conflict));
        }

        string trainSet = ReadField(conflict.Evidence, "core_trains");
        string requirementIds = ReadField(conflict.Evidence, "core_requirement_ids");
        string trackDomains = ReadField(conflict.Evidence, "core_track_domains");
        string incomingRoutes = ReadField(conflict.Evidence, "core_incoming_route_ids");
        string outgoingRoutes = ReadField(conflict.Evidence, "core_outgoing_route_ids");
        string minimality = ReadField(conflict.Evidence, "core_minimality");
        string resourceSignature = string.Join(';',
            $"requirements={requirementIds}",
            $"tracks={trackDomains}",
            $"incoming={incomingRoutes}",
            $"outgoing={outgoingRoutes}");
        string proofContext = string.Join(';',
            $"resource={conflict.ResourceKey}",
            $"start={conflict.StartSeconds.ToString("R", CultureInfo.InvariantCulture)}",
            $"end={conflict.EndSeconds.ToString("R", CultureInfo.InvariantCulture)}",
            $"minimality={minimality}");
        string payload = string.Join('|', conflict.StationName, trainSet, resourceSignature, proofContext);
        string identityKey = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        return new AssignmentCoreIdentity(
            identityKey,
            conflict.StationName,
            trainSet,
            resourceSignature,
            proofContext);
    }

    public static IReadOnlyList<AssignmentCoreIdentity> Analyze(MicroFeasibilityReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return report.Conflicts
            .Where(conflict => string.Equals(
                conflict.ConflictKind,
                "station_route_track_assignment_infeasible",
                StringComparison.OrdinalIgnoreCase))
            .Select(Analyze)
            .GroupBy(identity => identity.IdentityKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(identity => identity.StationName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(identity => identity.IdentityKey, StringComparer.Ordinal)
            .ToList();
    }

    private static string ReadField(string evidence, string key)
    {
        string prefix = key + "=";
        string? value = (evidence ?? string.Empty)
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(part => part.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return value is null ? "unavailable" : value[prefix.Length..].Trim();
    }
}

/// <summary>
/// 仅在完整身份连续出现时累计轮数；同站、同数量但列车集合或证明上下文变化会重新计数。
/// </summary>
public sealed class AssignmentCorePersistenceTracker
{
    private Dictionary<string, int> _active = new(StringComparer.Ordinal);

    public IReadOnlyList<AssignmentCoreOccurrence> Observe(
        IReadOnlyList<AssignmentCoreIdentity> identities)
    {
        ArgumentNullException.ThrowIfNull(identities);
        Dictionary<string, int> next = new(StringComparer.Ordinal);
        List<AssignmentCoreOccurrence> occurrences = [];
        foreach (AssignmentCoreIdentity identity in identities
                     .GroupBy(item => item.IdentityKey, StringComparer.Ordinal)
                     .Select(group => group.First()))
        {
            int count = _active.GetValueOrDefault(identity.IdentityKey) + 1;
            next[identity.IdentityKey] = count;
            occurrences.Add(new AssignmentCoreOccurrence(identity, count));
        }

        _active = next;
        return occurrences
            .OrderByDescending(item => item.ConsecutiveRounds)
            .ThenBy(item => item.Identity.StationName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Identity.IdentityKey, StringComparer.Ordinal)
            .ToList();
    }
}
