using Gurobi;
namespace GurobiTimetablingResearch.Optimization;

internal sealed record ResourceGuidanceCaps(double MicroViolation,double ServicePenalty,double Span);
internal static class ResourceLocalSearchControls
{
    internal static void ApplyGuidance(GRBModel model,GRBLinExpr violation,ResourceGuidanceCaps caps)
    {
        if(new[]{caps.MicroViolation,caps.ServicePenalty,caps.Span}.Any(v=>!double.IsFinite(v)||v<0))
            throw new ArgumentException("Invalid local guidance caps.");
        model.AddConstr(violation,GRB.LESS_EQUAL,caps.MicroViolation,"guide_conflict_cap");
        bool spanFound=false,serviceFound=false;
        for(int i=0;i<model.NumObj;i++)
        {
            model.Set(GRB.IntParam.ObjNumber,i);string name=model.ObjNName;
            if(name=="global_span")
            {
                model.AddConstr(model.GetObjective(i),GRB.LESS_EQUAL,caps.Span,"guide_span_cap");
                model.ObjNPriority=7;spanFound=true;
            }
            if(name=="service_window_penalty")
            {
                model.AddConstr(model.GetObjective(i),GRB.LESS_EQUAL,caps.ServicePenalty,"guide_service_cap");serviceFound=true;
            }
        }
        if(!spanFound || !serviceFound)throw new InvalidOperationException("Guidance requires original span and service objectives.");
        model.Update();
    }
    internal static string RequireDiversity(GRBModel model,GRBLinExpr stopChanges,IReadOnlyList<StationMicroRepairAugmentation> augmentations)
    {
        // A local exclusion only: discarded with this repair, never a global cut.
        GRBLinExpr different=new();string kind="None";
        if(stopChanges.Size>=4) { different.Add(stopChanges);kind="StopPattern"; }
        else
        {
            foreach(var binding in augmentations.SelectMany(a=>a.ChoiceBindings))
                if(binding.Variable.Start>.5 && binding.Variable.Start<GRB.INFINITY && binding.Variable.LB!=binding.Variable.UB)
                { different.AddConstant(1);different.AddTerm(-1,binding.Variable);kind="MicroResources"; }
        }
        if(kind!="None")model.AddConstr(different,GRB.GREATER_EQUAL,1,"quality_local_structure_exclusion");
        return kind;
    }
}
internal sealed class ResourceRepairEffort(double anchorSpan,double maximumSeconds,double? patienceSeconds=null)
{
    private double best=anchorSpan,lastImprovement;
    internal double ProbeSeconds { get; }=Math.Min(maximumSeconds,patienceSeconds??Math.Min(60,Math.Max(5,maximumSeconds*.25)));
    internal void Observe(double seconds,double span)
    { if(double.IsFinite(span) && span<best-1e-6) { best=span;lastImprovement=seconds; } }
    internal bool ShouldStop(double seconds)=>seconds>=maximumSeconds || seconds>=ProbeSeconds && seconds-lastImprovement>=ProbeSeconds;
}
internal sealed class ResourceQualityProgress(GRBLinExpr span,ResourceRepairEffort effort,string path):GRBCallback
{
    private double last=-30;
    internal string? Error { get; private set; }
    protected override void Callback()
    {
        try
        {
            if(where is not (GRB.Callback.MIP or GRB.Callback.MIPSOL))return;
            double seconds=GetDoubleInfo(GRB.Callback.RUNTIME);
            if(where==GRB.Callback.MIPSOL)
            {
                double value=span.Constant;
                for(int i=0;i<span.Size;i++)value+=span.GetCoeff(i)*GetSolution(span.GetVar(i));
                effort.Observe(seconds,value);
            }
            if(where==GRB.Callback.MIP && seconds-last>=30)
            {
                last=seconds;File.WriteAllText(path,System.Text.Json.JsonSerializer.Serialize(new {seconds,probeSeconds=effort.ProbeSeconds,
                    scope="LocalQualitySearch_NotGlobalBound"}));
            }
            if(effort.ShouldStop(seconds))Abort();
        }
        catch(Exception ex) { Error=ex.ToString();Abort(); }
    }
}
