using Gurobi;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

internal sealed record ResourceSpanBoundResult(string Status,double? LowerBoundSeconds,int NativeStatus,
    double Seconds,double BuildSeconds,int SolCount,string Scope,double? RawNativeBound=null);

/// <summary>
/// An unconditioned macro relaxation of the implemented joint problem. Never accepts a
/// snapshot, local radius, fixed stop pattern, trust region or heuristic repair cut.
/// Soft windows can be omitted because their nonnegative slacks have no hard cap.
/// </summary>
internal sealed class ResourceGlobalSpanBoundSolver(TimetablingInstance instance,int seed=0) : IDisposable
{
    internal const string BoundScope="SameInstance_OriginalFullMacroRelaxation_SingleGlobalSpan_NoLocalFixings";
    private BasicMilpModel? source;
    internal ResourceSpanBoundResult Solve(double seconds,Func<double> remainingSeconds,string logPath)
    {
        if(!double.IsFinite(seconds) || seconds<=0) throw new ArgumentOutOfRangeException(nameof(seconds));
        var clock=System.Diagnostics.Stopwatch.StartNew();double buildSeconds=0;
        double Left()=>Math.Min(seconds-clock.Elapsed.TotalSeconds,remainingSeconds());
        if(Left()<=0) return Result("SkippedBoundBudget",null,0,0);
        if(source is null)
        {
            source=new BasicMilpModelBuilder(instance,neighborRadius:int.MaxValue,
                serviceWindows:ServiceWindowPolicy.Disabled,screenTemporalPrecedence:true).Build();
            var model=source.Model;ConfigureSingleSpanObjective(model);
            model.Set(GRB.IntParam.Threads,4);model.Set(GRB.IntParam.Seed,seed);
            model.Set(GRB.IntParam.MIPFocus,3);
            model.Set(GRB.IntParam.NumericFocus,2);model.Set(GRB.DoubleParam.FeasibilityTol,1e-6);
            model.Set(GRB.DoubleParam.IntFeasTol,1e-8);
            model.Set(GRB.DoubleParam.MIPGap,0);model.Set(GRB.DoubleParam.MIPGapAbs,0);
            buildSeconds=clock.Elapsed.TotalSeconds;
        }
        // Build() is synchronous. Charge its entire duration, and never start Optimize
        // after the pass/search budget expires. Keep this model for a later time slice.
        if(Left()<=0) return Result("BoundBuildBudget",null,0,0);
        var native=source.Model;
        native.Set(GRB.IntParam.OutputFlag,1);native.Set(GRB.IntParam.LogToConsole,0);
        native.Set(GRB.StringParam.LogFile,logPath);
        native.Set(GRB.DoubleParam.TimeLimit,Math.Max(.001,Left()));
        native.Optimize();
        int status=native.Status;
        if(!CanUseBound(status)) return Result("NoValidBoundNativeStatus",null,status,native.SolCount);
        // ObjVal or the incumbent of this relaxation is NOT a bound on joint optimality.
        double raw=native.ObjBoundC;
        if(!double.IsFinite(raw) || Math.Abs(raw)>=GRB.INFINITY)
            return Result("NoFiniteGlobalBound",null,status,native.SolCount);
        // Span is nonnegative by the original running-time constraints. A microsecond
        // guard is conservative; it is not a substitute for the solver's numeric contract.
        return Result("GlobalSpanBoundAvailable",Math.Max(0,raw-1e-6),status,native.SolCount,raw);

        ResourceSpanBoundResult Result(string state,double? lower,int nativeStatus,int count,double? raw=null)
            =>new(state,lower,nativeStatus,clock.Elapsed.TotalSeconds,buildSeconds,count,BoundScope,raw);
    }
    internal static bool CanUseBound(int status)=>status is GRB.Status.OPTIMAL or GRB.Status.TIME_LIMIT or
        GRB.Status.NODE_LIMIT or GRB.Status.ITERATION_LIMIT or GRB.Status.INTERRUPTED or GRB.Status.WORK_LIMIT;
    internal static void ConfigureSingleSpanObjective(GRBModel model)
    {
        model.Update();var span=model.GetObjective(0);
        // NumObj=1 alone can retain multi-objective mode and make scalar ObjBoundC
        // unavailable. Gurobi requires zeroing objectives and updating before reset.
        // https://docs.gurobi.com/projects/optimizer/en/current/reference/attributes/multiobjective.html
        model.NumObj=0;model.Update();model.SetObjective(span,GRB.MINIMIZE);model.Update();
        if(model.IsMultiObj!=0 || model.NumObj!=1 || model.ModelSense!=GRB.MINIMIZE)
            throw new InvalidOperationException("Global span bound requires one scalar minimization objective.");
    }
    public void Dispose() { source?.Dispose();source=null; }
}
