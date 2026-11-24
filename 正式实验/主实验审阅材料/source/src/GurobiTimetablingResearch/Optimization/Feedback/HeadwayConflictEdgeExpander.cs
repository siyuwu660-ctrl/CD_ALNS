// 冲突边扩张器把一个候选展开为建模所需的区间先后边和车站事件先后边，并用规范键过滤已存在约束。
// 批选择器控制一次试探加入的边数，使结构反馈能够以小批量进入主问题并支持失败回滚。
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

internal sealed record HeadwayConflictEdgeExpansion(
    IReadOnlyList<SectionConflictEdgeRequest> SectionEdges,
    IReadOnlyList<StationEventConflictCandidate> StationEdges);

internal static class HeadwayConflictEdgeExpander
{
    internal static HeadwayConflictEdgeExpansion Expand(IReadOnlyList<MicroConflictExplanation> conflicts)
    {
        List<SectionConflictEdgeRequest> sectionEdges = new();
        List<StationEventConflictCandidate> stationEdges = new();
        HashSet<string> sectionKeys = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> stationKeys = new(StringComparer.OrdinalIgnoreCase);

        foreach (MicroConflictExplanation conflict in conflicts.Where(item =>
                     string.Equals(item.ConflictKind, "exact_headway_infeasible", StringComparison.OrdinalIgnoreCase) &&
                     item.FirstTrainIndex.HasValue && item.SecondTrainIndex.HasValue &&
                     item.StationOrder.HasValue && item.SecondStationOrder.HasValue &&
                     item.FirstEventSide.HasValue && item.SecondEventSide.HasValue))
        {
            if (conflict.FirstEventSide == conflict.SecondEventSide &&
                !string.IsNullOrWhiteSpace(conflict.FirstSectionName) &&
                string.Equals(conflict.FirstSectionName, conflict.SecondSectionName, StringComparison.OrdinalIgnoreCase))
            {
                SectionConflictEdgeRequest edge = new(
                    conflict.FirstSectionName,
                    conflict.FirstTrainIndex!.Value,
                    conflict.SecondTrainIndex!.Value);
                string key = InitialConflictGraphBuilder.BuildCanonicalPairKey(
                    edge.SectionName, edge.FirstTrainIndex, edge.SecondTrainIndex);
                if (sectionKeys.Add(key))
                {
                    sectionEdges.Add(edge);
                }

                continue;
            }

            StationEventConflictCandidate stationEdge = new(
                conflict.StationName,
                conflict.FirstTrainIndex!.Value,
                conflict.SecondTrainIndex!.Value,
                conflict.StationOrder!.Value,
                conflict.SecondStationOrder!.Value,
                conflict.FirstEventSide!.Value,
                conflict.SecondEventSide!.Value);
            string stationKey = BasicMilpModelBuilder.BuildStationEventPrecedenceKey(stationEdge);
            if (stationKeys.Add(stationKey))
            {
                stationEdges.Add(stationEdge);
            }
        }

        return new HeadwayConflictEdgeExpansion(sectionEdges, stationEdges);
    }
}

internal static class HeadwayConflictEdgeBatchSelector
{
    internal static HeadwayConflictEdgeExpansion Select(HeadwayConflictEdgeExpansion expansion, int maxEdges)
    {
        if (maxEdges <= 0)
        {
            return new HeadwayConflictEdgeExpansion(Array.Empty<SectionConflictEdgeRequest>(), Array.Empty<StationEventConflictCandidate>());
        }

        List<SectionConflictEdgeRequest> sections = new();
        List<StationEventConflictCandidate> stations = new();
        int sectionIndex = 0;
        int stationIndex = 0;
        while (sections.Count + stations.Count < maxEdges &&
               (sectionIndex < expansion.SectionEdges.Count || stationIndex < expansion.StationEdges.Count))
        {
            if (stationIndex < expansion.StationEdges.Count)
            {
                stations.Add(expansion.StationEdges[stationIndex++]);
            }

            if (sections.Count + stations.Count < maxEdges && sectionIndex < expansion.SectionEdges.Count)
            {
                sections.Add(expansion.SectionEdges[sectionIndex++]);
            }
        }

        return new HeadwayConflictEdgeExpansion(sections, stations);
    }
}
