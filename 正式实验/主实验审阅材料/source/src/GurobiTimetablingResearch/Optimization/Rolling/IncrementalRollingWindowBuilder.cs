// 增量滚动窗口构建器按列车难度排序切分索引，并通过重叠区把上一窗口的边界列车带入下一窗口。
// 返回的窗口序列用于逐批扩大模型，同时保持相邻子问题之间有足够公共变量传递既有解。
namespace GurobiTimetablingResearch.Optimization;

public static class IncrementalRollingWindowBuilder
{
    public static IReadOnlyList<IReadOnlySet<int>> BuildAdmissionSets(
        IReadOnlyList<int> orderedTrainIndices,
        int initialBatchSize,
        int admissionBatchSize)
    {
        if (initialBatchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(initialBatchSize));
        }

        if (admissionBatchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(admissionBatchSize));
        }

        List<IReadOnlySet<int>> result = new();
        int admittedCount = Math.Min(initialBatchSize, orderedTrainIndices.Count);
        while (admittedCount > 0)
        {
            result.Add(orderedTrainIndices.Take(admittedCount).ToHashSet());
            if (admittedCount == orderedTrainIndices.Count)
            {
                break;
            }

            int remaining = orderedTrainIndices.Count - admittedCount;
            admittedCount += remaining < admissionBatchSize ? 1 : admissionBatchSize;
        }

        return result;
    }
}
