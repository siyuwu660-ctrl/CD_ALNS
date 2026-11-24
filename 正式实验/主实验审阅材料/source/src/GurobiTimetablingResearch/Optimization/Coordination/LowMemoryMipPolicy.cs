using Gurobi;

namespace GurobiTimetablingResearch.Optimization;

internal sealed record LowMemoryMipParameters(int Method,int Threads,string MethodName);

internal static class LowMemoryMipPolicy
{
    internal const int DualSimplexMethod=1;

    internal static LowMemoryMipParameters Apply(GRBModel model,int threads)
    {
        // Avoid concurrent/barrier root LP allocations for the large complete
        // models. This does not cap model memory or guarantee a feasible solve.
        // https://docs.gurobi.com/projects/optimizer/en/current/concepts/parameters/guidelines.html
        model.Set(GRB.IntParam.Method,DualSimplexMethod);
        model.Set(GRB.IntParam.Threads,threads);
        return new(model.Get(GRB.IntParam.Method),model.Get(GRB.IntParam.Threads),"DualSimplex");
    }
}
