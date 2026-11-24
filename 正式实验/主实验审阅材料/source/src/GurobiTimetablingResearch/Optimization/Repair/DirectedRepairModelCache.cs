using Gurobi;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

// A same-run immutable macro template. Each attempt owns a native copy; no
// temporary bound, slack, objective or incumbent is ever written into the template.
internal sealed class DirectedRepairModelCache : IDisposable
{
    private BasicMilpModel? template;
    private string[] guardKeys=[],stationKeys=[];
    private bool leased;
    internal int BuildCount { get; private set; }
    internal int CopyCount { get; private set; }

    internal DirectedRepairModelLease Acquire(TimetablingInstance instance,IReadOnlyList<SectionConflictEdgeRequest> edges,
        ServiceWindowPolicy policy,IReadOnlyList<StationEventConflictCandidate> stationEdges)
    {
        if(leased)throw new InvalidOperationException("A macro model copy is still in use.");
        var guards=edges.Select(e=>BasicMilpModelBuilder.BuildSectionPrecedenceKey(e.SectionName,e.FirstTrainIndex,e.SecondTrainIndex)).Distinct().Order(StringComparer.Ordinal).ToArray();
        var stations=stationEdges.Select(BasicMilpModelBuilder.BuildStationEventPrecedenceKey).Distinct().Order(StringComparer.Ordinal).ToArray();
        bool reused=template is not null && ReferenceEquals(template.Instance,instance) && template.ServiceWindows.Identity==policy.Identity &&
            guards.SequenceEqual(guardKeys) && stations.SequenceEqual(stationKeys);
        var timer=System.Diagnostics.Stopwatch.StartNew();
        if(!reused)
        {
            Dispose();
            template=new BasicMilpModelBuilder(instance,neighborRadius:0,additionalSectionEdges:edges,serviceWindows:policy,
                screenTemporalPrecedence:true,preserveOmittedOvertakingSupport:true,stationEventEdges:stationEdges).Build();
            template.Model.Set(GRB.IntParam.OutputFlag,0);template.Model.Update();
            guardKeys=guards;stationKeys=stations;BuildCount++;
        }
        double templateSeconds=timer.Elapsed.TotalSeconds;timer.Restart();
        var original=template!;
        var copy=new GRBModel(original.Model);
        try
        {
            var byName=copy.GetVars().ToDictionary(v=>v.VarName,StringComparer.Ordinal);
            GRBVar V(GRBVar v)=>byName[v.VarName];
            Dictionary<string,GRBVar> Map(IReadOnlyDictionary<string,GRBVar> map)=>map.ToDictionary(p=>p.Key,p=>V(p.Value),StringComparer.OrdinalIgnoreCase);
            var view=new BasicMilpModel(original.Environment,copy,instance,Map(original.ArrivalVars),Map(original.DepartureVars),Map(original.StopVars),
                original.SectionConflictCandidates,Map(original.SectionPrecedenceVars),original.StationEventConflictCandidates,Map(original.StationEventPrecedenceVars),
                Map(original.RunningTimeSupplementVars),original.RunningTimeSupplementRatio,Map(original.BeingOvertakenVars),
                stopPairProductBindings:original.StopPairProductBindings.Select(b=>b with {ProductVariable=V(b.ProductVariable)}).ToArray(),
                overtakingReversalBindings:original.OvertakingReversalBindings.Select(b=>b with {IncomingPrecedence=V(b.IncomingPrecedence),OutgoingPrecedence=V(b.OutgoingPrecedence),ReversalVariable=V(b.ReversalVariable)}).ToArray(),
                maintenanceWindowBinaryBindings:original.MaintenanceWindowBinaryBindings.Select(b=>b with {BeforeWindowVariable=V(b.BeforeWindowVariable)}).ToArray(),
                stationQuotaCategoryBindings:original.StationQuotaCategoryBindings.Select(b=>b with {InWindowVariable=V(b.InWindowVariable),LeftOutsideVariable=V(b.LeftOutsideVariable),RightOutsideVariable=V(b.RightOutsideVariable)}).ToArray(),
                hardEventBounds:original.HardEventBounds,conditionalBigMAuditRows:original.ConditionalBigMAuditRows)
                {ServiceWindows=policy,TemporalPrecedenceAudit=original.TemporalPrecedenceAudit};
            leased=true;CopyCount++;
            return new(view,reused,()=>{copy.Dispose();leased=false;},templateSeconds,timer.Elapsed.TotalSeconds);
        }
        catch{copy.Dispose();throw;}
    }
    public void Dispose()
    {
        if(leased)throw new InvalidOperationException("Dispose the active macro copy before its template.");
        template?.Dispose();template=null;
    }
}

internal sealed class DirectedRepairModelLease(BasicMilpModel source,bool reused,Action release,double buildSeconds=0,double copySeconds=0) : IDisposable
{
    private bool disposed;
    internal BasicMilpModel Source=>source;
    internal bool TemplateReused=>reused;
    internal double TemplateBuildSeconds=>buildSeconds;
    internal double TemplateCopySeconds=>copySeconds;
    // The environment belongs to the cache. Do not call Source.Dispose().
    public void Dispose(){if(!disposed){disposed=true;release();}}
}
