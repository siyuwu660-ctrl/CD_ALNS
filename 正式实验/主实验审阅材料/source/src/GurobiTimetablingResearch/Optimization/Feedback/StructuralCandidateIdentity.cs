// 结构候选身份模块把列车对、区间边和有向事件边规范化为稳定键，并支持从历史文本标识中解析业务字段。
// 规范身份消除列车顺序、大小写和格式差异，用于跨检查点去重、精确匹配及诊断表连接。
using System.Text.RegularExpressions;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

internal sealed record CanonicalTrainPair(int LowerTrainIndex, int UpperTrainIndex)
{
    internal static CanonicalTrainPair Create(int firstTrainIndex, int secondTrainIndex) =>
        firstTrainIndex <= secondTrainIndex
            ? new CanonicalTrainPair(firstTrainIndex, secondTrainIndex)
            : new CanonicalTrainPair(secondTrainIndex, firstTrainIndex);
}

internal sealed record CanonicalSectionStructuralIdentity(
    string SectionIdentity,
    CanonicalTrainPair TrainPair)
{
    internal static CanonicalSectionStructuralIdentity Create(
        string sectionName,
        int firstTrainIndex,
        int secondTrainIndex) => new(
            NormalizeSectionIdentity(sectionName),
            CanonicalTrainPair.Create(firstTrainIndex, secondTrainIndex));

    private static string NormalizeSectionIdentity(string sectionName) =>
        Regex.Replace(sectionName.Trim(), @"[-_\s]+", "_", RegexOptions.CultureInvariant)
            .ToUpperInvariant();
}

internal sealed record ParsedSectionStructuralIdentity(
    CanonicalSectionStructuralIdentity CanonicalIdentity,
    int DirectedFirstTrainIndex,
    int DirectedSecondTrainIndex);

internal sealed record DirectedStructuralElementIdentity(
    CanonicalTrainPair CanonicalPair,
    int DirectedFirstTrainIndex,
    int DirectedSecondTrainIndex,
    StationEventSide? DirectedFirstEventSide,
    StationEventSide? DirectedSecondEventSide);

internal static partial class StructuralCandidateIdentityMatcher
{
    internal static bool IsOwnedBy(string elementName, HeadwayConflictCandidate candidate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(elementName);
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.SectionEdge is not null)
        {
            ParsedSectionStructuralIdentity? parsed = ParseSectionIdentity(elementName);
            CanonicalSectionStructuralIdentity candidateIdentity =
                CanonicalSectionStructuralIdentity.Create(
                    candidate.SectionEdge.SectionName,
                    candidate.SectionEdge.FirstTrainIndex,
                    candidate.SectionEdge.SecondTrainIndex);
            return parsed?.CanonicalIdentity == candidateIdentity;
        }

        if (candidate.StationEdge is null)
        {
            return false;
        }
        StationEventConflictCandidate edge = candidate.StationEdge;
        string stationForward = BasicMilpModelBuilder.BuildStationEventConstraintBaseName(edge);
        string stationReverse = BasicMilpModelBuilder.BuildStationEventConstraintBaseName(
            edge.StationName,
            edge.SecondTrainIndex,
            edge.SecondSide,
            edge.FirstTrainIndex,
            edge.FirstSide);
        return elementName.Contains(stationForward, StringComparison.OrdinalIgnoreCase) ||
               elementName.Contains(stationReverse, StringComparison.OrdinalIgnoreCase);
    }

    private static ParsedSectionStructuralIdentity? ParseSectionIdentity(string elementName)
    {
        Match section = SectionIdentityRegex().Match(elementName);
        if (!section.Success)
        {
            return null;
        }
        int first = int.Parse(section.Groups["firstTrain"].Value);
        int second = int.Parse(section.Groups["secondTrain"].Value);
        return new ParsedSectionStructuralIdentity(
            CanonicalSectionStructuralIdentity.Create(
                section.Groups["section"].Value,
                first,
                second),
            first,
            second);
    }

    internal static DirectedStructuralElementIdentity? ParseDirectedIdentity(string elementName)
    {
        Match station = StationIdentityRegex().Match(elementName);
        if (station.Success &&
            Enum.TryParse(station.Groups[2].Value, true, out StationEventSide firstSide) &&
            Enum.TryParse(station.Groups[4].Value, true, out StationEventSide secondSide))
        {
            int first = int.Parse(station.Groups[1].Value);
            int second = int.Parse(station.Groups[3].Value);
            return new DirectedStructuralElementIdentity(
                CanonicalTrainPair.Create(first, second),
                first,
                second,
                firstSide,
                secondSide);
        }

        ParsedSectionStructuralIdentity? section = ParseSectionIdentity(elementName);
        if (section is null)
        {
            return null;
        }
        return new DirectedStructuralElementIdentity(
            section.CanonicalIdentity.TrainPair,
            section.DirectedFirstTrainIndex,
            section.DirectedSecondTrainIndex,
            null,
            null);
    }

    [GeneratedRegex(@"sta_.+?_t(-?\d+)_(Incoming|Outgoing)_t(-?\d+)_(Incoming|Outgoing)", RegexOptions.IgnoreCase)]
    private static partial Regex StationIdentityRegex();

    [GeneratedRegex(@"sec_(?<section>.+?)_t(?<firstTrain>-?\d+)_t(?<secondTrain>-?\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex SectionIdentityRegex();
}
