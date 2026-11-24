// 冲突连通分量构建器把列车视为顶点、成对冲突视为无向边，并用邻接表搜索得到相互耦合的列车集合。
// 连通分量用于把微观冲突拆成局部修复单元，限制候选扩张和子模型规模。
namespace GurobiTimetablingResearch.Optimization;

internal static class ConflictConnectedComponentBuilder
{
    internal static IReadOnlyList<IReadOnlyList<T>> Build<T>(
        IReadOnlyList<T> items,
        Func<T, T, bool> hasConflictEdge)
    {
        bool[] visited = new bool[items.Count];
        List<IReadOnlyList<T>> components = [];

        for (int root = 0; root < items.Count; root++)
        {
            if (visited[root])
            {
                continue;
            }

            List<T> component = [];
            Queue<int> queue = new();
            visited[root] = true;
            queue.Enqueue(root);
            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                component.Add(items[current]);
                for (int candidate = 0; candidate < items.Count; candidate++)
                {
                    if (visited[candidate] || !hasConflictEdge(items[current], items[candidate]))
                    {
                        continue;
                    }

                    visited[candidate] = true;
                    queue.Enqueue(candidate);
                }
            }

            components.Add(component);
        }

        return components;
    }
}
