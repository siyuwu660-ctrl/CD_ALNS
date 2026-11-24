// 初始冲突图构建器依据列车运行区间的时间窗重叠生成潜在冲突边，并用规范列车对键消除方向造成的重复。
// 图仅保留可能竞争同一资源的组合，作为主问题先后变量和后续微观反馈候选的稀疏索引。
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

/// <summary>
/// 基于参考顺序生成初始稀疏区间冲突图。
/// 这里的候选边覆盖所有列车共享区间，而不是只在可越行站起点建边；
/// 是否允许相邻区间顺序变化，交给后续 station-capability-aware no-overtake 约束决定。
/// </summary>
public sealed class InitialConflictGraphBuilder
{
    private readonly TimetablingInstance _instance;
    private readonly int _neighborRadius;
    private readonly IReadOnlyList<SectionConflictEdgeRequest> _additionalEdges;

    public InitialConflictGraphBuilder(
        TimetablingInstance instance,
        int? neighborRadius = null,
        IReadOnlyList<SectionConflictEdgeRequest>? additionalEdges = null)
    {
        _instance = instance;
        _neighborRadius = neighborRadius ?? (instance.Trains.Count >= 100 ? 5 : 4);
        _additionalEdges = additionalEdges ?? Array.Empty<SectionConflictEdgeRequest>();
    }

    public IReadOnlyList<SectionConflictCandidate> Build()
    {
        List<TrainRecord> orderedTrains = BuildInitialTrainOrder();
        Dictionary<int, int> trainRankMap = orderedTrains
            .Select((train, index) => (train.TrainIndex, Rank: index))
            .ToDictionary(item => item.TrainIndex, item => item.Rank);

        List<SectionConflictCandidate> candidates = new();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (SectionRecord section in _instance.Sections)
        {
            List<(TrainRecord Train, TrainStopRecord StartStop, TrainStopRecord EndStop)> traversals = ResolveSectionTraversals(section);
            if (traversals.Count <= 1)
            {
                continue;
            }

            List<(TrainRecord Train, TrainStopRecord StartStop, TrainStopRecord EndStop)> orderedTraversals = traversals
                .OrderBy(item => trainRankMap[item.Train.TrainIndex])
                .ToList();

            for (int index = 0; index < orderedTraversals.Count; index++)
            {
                for (int offset = 1; offset <= _neighborRadius && index + offset < orderedTraversals.Count; offset++)
                {
                    var first = orderedTraversals[index];
                    var second = orderedTraversals[index + offset];

                    string key = BuildCanonicalPairKey(section.SectionName, first.Train.TrainIndex, second.Train.TrainIndex);
                    if (!seen.Add(key))
                    {
                        continue;
                    }

                    candidates.Add(new SectionConflictCandidate(
                        section.SectionName,
                        first.Train.TrainIndex,
                        second.Train.TrainIndex,
                        first.StartStop.StationOrder,
                        second.StartStop.StationOrder,
                        first.EndStop.StationOrder,
                        second.EndStop.StationOrder,
                        offset,
                        first.StartStop.StationName,
                        first.EndStop.StationName));
                }
            }
        }

        foreach (SectionConflictEdgeRequest request in _additionalEdges)
        {
            SectionRecord? section = _instance.Sections.FirstOrDefault(item =>
                string.Equals(item.SectionName, request.SectionName, StringComparison.OrdinalIgnoreCase));
            if (section is null || request.FirstTrainIndex == request.SecondTrainIndex)
            {
                continue;
            }

            List<(TrainRecord Train, TrainStopRecord StartStop, TrainStopRecord EndStop)> traversals = ResolveSectionTraversals(section);
            var first = traversals.FirstOrDefault(item => item.Train.TrainIndex == request.FirstTrainIndex);
            var second = traversals.FirstOrDefault(item => item.Train.TrainIndex == request.SecondTrainIndex);
            if (first.Train is null || second.Train is null)
            {
                continue;
            }

            string key = BuildCanonicalPairKey(section.SectionName, first.Train.TrainIndex, second.Train.TrainIndex);
            if (!seen.Add(key))
            {
                continue;
            }

            bool firstRanksFirst = trainRankMap[first.Train.TrainIndex] <= trainRankMap[second.Train.TrainIndex];
            var orderedFirst = firstRanksFirst ? first : second;
            var orderedSecond = firstRanksFirst ? second : first;
            candidates.Add(new SectionConflictCandidate(
                section.SectionName,
                orderedFirst.Train.TrainIndex,
                orderedSecond.Train.TrainIndex,
                orderedFirst.StartStop.StationOrder,
                orderedSecond.StartStop.StationOrder,
                orderedFirst.EndStop.StationOrder,
                orderedSecond.EndStop.StationOrder,
                Math.Abs(trainRankMap[orderedFirst.Train.TrainIndex] - trainRankMap[orderedSecond.Train.TrainIndex]),
                orderedFirst.StartStop.StationName,
                orderedFirst.EndStop.StationName));
        }

        return candidates;
    }

    internal static string BuildCanonicalPairKey(string sectionName, int firstTrainIndex, int secondTrainIndex)
    {
        int lower = Math.Min(firstTrainIndex, secondTrainIndex);
        int upper = Math.Max(firstTrainIndex, secondTrainIndex);
        return $"{sectionName}::{lower}::{upper}";
    }

    private List<TrainRecord> BuildInitialTrainOrder()
    {
        return _instance.Trains
            .OrderBy(train =>
            {
                TrainStopRecord origin = train.OrderedStops[0];
                return origin.RawDepTimeSeconds ?? origin.EarliestDepSeconds;
            })
            .ThenBy(train => train.TrainIndex)
            .ToList();
    }

    private List<(TrainRecord Train, TrainStopRecord StartStop, TrainStopRecord EndStop)> ResolveSectionTraversals(SectionRecord section)
    {
        List<(TrainRecord Train, TrainStopRecord StartStop, TrainStopRecord EndStop)> traversals = new();

        foreach (TrainRecord train in _instance.Trains)
        {
            for (int index = 0; index < train.OrderedStops.Count - 1; index++)
            {
                TrainStopRecord currentStop = train.OrderedStops[index];
                TrainStopRecord nextStop = train.OrderedStops[index + 1];

                if (!string.Equals(nextStop.IncomingSectionName, section.SectionName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                traversals.Add((train, currentStop, nextStop));
                break;
            }
        }

        return traversals;
    }
}
