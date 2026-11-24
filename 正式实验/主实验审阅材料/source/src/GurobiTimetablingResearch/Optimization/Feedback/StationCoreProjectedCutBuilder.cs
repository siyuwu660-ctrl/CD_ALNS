// 车站冲突核投影器把微观不可行核心映射为主问题已有的径路选择和事件先后变量，生成可表达的逻辑割。
// 投影结果使用只读映射保存变量及系数；无法完整投影的核心会保留诊断信息而不会生成不可靠约束。
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;

namespace GurobiTimetablingResearch.Optimization;

internal sealed record StationCoreProjection(
    string ProjectionId,
    string Signature,
    string StationName,
    string CoreId,
    IReadOnlyList<string> CoreStopIds,
    IReadOnlyList<int> CoreTrainIndices,
    IReadOnlyDictionary<string, int> StopLiteralsByStopId,
    IReadOnlyDictionary<string, int> SectionLiteralsByKey,
    IReadOnlyDictionary<string, int> StationEventLiteralsByKey,
    string SourceFamily,
    string? CertificateReason)
{
    internal int StopLiteralCount => StopLiteralsByStopId.Count;

    internal int SectionLiteralCount => SectionLiteralsByKey.Count;

    internal int StationEventLiteralCount => StationEventLiteralsByKey.Count;

    internal int LiteralCount => StopLiteralCount + SectionLiteralCount + StationEventLiteralCount;

    internal bool HasCut => LiteralCount > 0 && CertificateReason is null;
}

internal static class StationCoreProjectedCutBuilder
{
    internal const string SourceFamily = "heuristic_projected_benders";
    internal const string InsufficientMasterSupportCertificate = "master_decision_space_insufficient";

    internal static StationCoreProjection Build(
        string stationName,
        string coreId,
        IReadOnlyList<string> coreStopIds,
        IReadOnlyList<int> coreTrainIndices,
        IReadOnlyDictionary<string, int> stopValues,
        IReadOnlyDictionary<string, int> sectionValues,
        IReadOnlyDictionary<string, int> stationEventValues)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stationName);
        ArgumentException.ThrowIfNullOrWhiteSpace(coreId);
        ArgumentNullException.ThrowIfNull(coreStopIds);
        ArgumentNullException.ThrowIfNull(coreTrainIndices);

        IReadOnlyList<string> canonicalStopIds = Array.AsReadOnly(coreStopIds
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray());
        IReadOnlyList<int> canonicalTrainIndices = Array.AsReadOnly(coreTrainIndices
            .Distinct()
            .OrderBy(value => value)
            .ToArray());
        IReadOnlyDictionary<string, int> canonicalStopValues = SnapshotBinaryValues(stopValues, nameof(stopValues));
        IReadOnlyDictionary<string, int> canonicalSectionValues = SnapshotBinaryValues(sectionValues, nameof(sectionValues));
        IReadOnlyDictionary<string, int> canonicalStationEventValues = SnapshotBinaryValues(
            stationEventValues,
            nameof(stationEventValues));

        string signature = string.Join(
            ";",
            $"station={Encode(stationName)}",
            $"core={Encode(coreId)}",
            $"core_stops={BuildListSignature(canonicalStopIds)}",
            $"core_trains={string.Join(",", canonicalTrainIndices)}",
            $"stop={BuildLiteralSignature(canonicalStopValues)}",
            $"section={BuildLiteralSignature(canonicalSectionValues)}",
            $"station_event={BuildLiteralSignature(canonicalStationEventValues)}");
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature)))
            .ToLowerInvariant();
        int literalCount = canonicalStopValues.Count + canonicalSectionValues.Count + canonicalStationEventValues.Count;

        return new StationCoreProjection(
            $"{stationName}::projected_station_core::{digest}",
            signature,
            stationName,
            coreId,
            canonicalStopIds,
            canonicalTrainIndices,
            canonicalStopValues,
            canonicalSectionValues,
            canonicalStationEventValues,
            SourceFamily,
            literalCount == 0 ? InsufficientMasterSupportCertificate : null);
    }

    private static IReadOnlyDictionary<string, int> SnapshotBinaryValues(
        IReadOnlyDictionary<string, int> values,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        SortedDictionary<string, int> sorted = new(StringComparer.Ordinal);
        foreach ((string key, int value) in values)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key, parameterName);
            if (value is not 0 and not 1)
            {
                throw new ArgumentOutOfRangeException(parameterName, value, "Projected incumbent values must be binary.");
            }

            sorted.Add(key, value);
        }

        return new ReadOnlyDictionary<string, int>(sorted);
    }

    private static string BuildListSignature(IEnumerable<string> values)
    {
        return string.Join(",", values.Select(Encode));
    }

    private static string BuildLiteralSignature(IReadOnlyDictionary<string, int> values)
    {
        return string.Join(",", values.Select(item => $"{Encode(item.Key)}={item.Value}"));
    }

    private static string Encode(string value)
    {
        return $"{value.Length}:{value}";
    }
}
