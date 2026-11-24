using Gurobi;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

internal sealed class ResourceMacroTemplate : IDisposable
{
    private readonly BasicMilpModel source;
    private readonly ResourceLinearTemplate linear;
    private readonly IReadOnlyDictionary<(int Train,int Order),TrainStopRecord> positions;
    internal TimetablingInstance Instance=>source.Instance;
    internal ServiceWindowPolicy Policy=>source.ServiceWindows;
    internal int SourceRows=>linear.SourceRows;
    internal int SourceVariables=>linear.SourceVariables;

    internal ResourceMacroTemplate(TimetablingInstance instance,ServiceWindowPolicy policy,MasterSolutionSnapshot anchor,Func<double>? remaining=null)
    {
        if(remaining?.Invoke()<=0) throw new TimeoutException("Macro template deadline expired.");
        source=new BasicMilpModelBuilder(instance,neighborRadius:int.MaxValue,serviceWindows:policy,screenTemporalPrecedence:true,
            stationEventEdges:SavedJointSnapshotVerifier.ResolveSavedStationEdges(instance,anchor)).Build();
        source.Model.Set(GRB.IntParam.OutputFlag,0);
        positions=instance.TrainStops.ToDictionary(s=>(s.TrainIndex,s.StationOrder));
        try { linear=new(source.Model,remaining); } catch { source.Dispose();throw; }
    }

    internal ResourceCompiledModel Build(MasterSolutionSnapshot anchor,ResourceNeighborhood neighborhood,Func<double>? remaining=null)
    {
        Dictionary<string,(double Lower,double Upper)> restrictions=new(StringComparer.Ordinal);
        foreach(var p in source.ArrivalVars) Time(p.Key,p.Value,anchor.ArrivalSecondsByStopId[p.Key]);
        foreach(var p in source.DepartureVars) Time(p.Key,p.Value,anchor.DepartureSecondsByStopId[p.Key]);
        foreach(var p in source.StopVars) if(!neighborhood.FreeStopIds.Contains(p.Key)) Fix(p.Value,anchor.StopByStopId[p.Key]);
        foreach(var c in source.SectionConflictCandidates)
        {
            string key=BasicMilpModelBuilder.BuildSectionPrecedenceKey(c);
            string[] ids=[positions[(c.FirstTrainIndex,c.FirstStopOrder)].StopId,positions[(c.FirstTrainIndex,c.FirstEndStopOrder)].StopId,
                positions[(c.SecondTrainIndex,c.SecondStopOrder)].StopId,positions[(c.SecondTrainIndex,c.SecondEndStopOrder)].StopId];
            bool release=neighborhood.FreeSectionOrderKeys?.Contains(key) ??
                ids.Any(id=>neighborhood.FreeTimeStopIds.Contains(id) || neighborhood.FreeStopIds.Contains(id));
            if(!release)
            {
                if(anchor.SectionPrecedenceByKey.TryGetValue(key,out int order)) Fix(source.SectionPrecedenceVars[key],order);
                else if(neighborhood.FreeSectionOrderKeys is not null)
                    Fix(source.SectionPrecedenceVars[key],anchor.DepartureSecondsByStopId[ids[0]]<=anchor.DepartureSecondsByStopId[ids[2]]?0:1);
            }
        }
        foreach(var c in source.StationEventConflictCandidates)
        {
            string a=positions[(c.FirstTrainIndex,c.FirstStationOrder)].StopId,b=positions[(c.SecondTrainIndex,c.SecondStationOrder)].StopId;
            string key=BasicMilpModelBuilder.BuildStationEventPrecedenceKey(c);
            bool release=neighborhood.FreeStationOrderKeys?.Contains(key) ??
                (neighborhood.FreeTimeStopIds.Contains(a) || neighborhood.FreeTimeStopIds.Contains(b));
            if(!release)
            {
                if(anchor.StationEventPrecedenceByKey.TryGetValue(key,out int value)) Fix(source.StationEventPrecedenceVars[key],value);
                else if(neighborhood.FreeStationOrderKeys is not null)
                {
                    double first=c.FirstSide==StationEventSide.Incoming?anchor.ArrivalSecondsByStopId[a]:anchor.DepartureSecondsByStopId[a];
                    double second=c.SecondSide==StationEventSide.Incoming?anchor.ArrivalSecondsByStopId[b]:anchor.DepartureSecondsByStopId[b];
                    Fix(source.StationEventPrecedenceVars[key],first<=second?0:1);
                }
            }
        }
        foreach(var train in Instance.Trains) for(int i=1;i<train.OrderedStops.Count;i++)
        {
            var a=train.OrderedStops[i-1];var b=train.OrderedStops[i];
            if(neighborhood.FreeTimeStopIds.Contains(a.StopId) || neighborhood.FreeTimeStopIds.Contains(b.StopId)) continue;
            string key=RunningTimeSupplementPolicy.BuildKey(train.TrainIndex,b.IncomingSectionName??$"{a.StationOrder}-{b.StationOrder}");
            if(anchor.RunningTimeSupplementSecondsByKey?.TryGetValue(key,out double value)==true) Fix(source.RunningTimeSupplementVars[key],value);
        }
        MasterSolutionSnapshotApplier.ApplyExpandableMacroMipStart(source,anchor with { VerifiedMicroAssignments=null });
        var starts=source.Model.GetVars().Select(v=>(v.VarName,v.Start)).Where(p=>p.Start!=GRB.UNDEFINED)
            .ToDictionary(p=>p.VarName,p=>p.Start,StringComparer.Ordinal);
        var retained=source.ArrivalVars.Values.Concat(source.DepartureVars.Values).Concat(source.StopVars.Values).Select(v=>v.VarName).ToHashSet(StringComparer.Ordinal);
        return linear.Build(restrictions,starts,retained,remaining);

        void Fix(GRBVar variable,double value)=>restrictions[variable.VarName]=(value,value);
        void Time(string id,GRBVar variable,double value)
        {
            if(!neighborhood.FreeTimeStopIds.Contains(id)) Fix(variable,value);
            else if(neighborhood.RadiusSeconds is double radius)
                restrictions[variable.VarName]=(Math.Max(variable.LB,value-radius),Math.Min(variable.UB,value+radius));
        }
    }

    internal IReadOnlyList<ResourceTightLink> TightLinks(MasterSolutionSnapshot anchor,Func<double>? remaining=null)
    {
        MasterSolutionSnapshotApplier.ApplyExpandableMacroMipStart(source,anchor with { VerifiedMicroAssignments=null });
        var values=source.Model.GetVars().Where(v=>v.Start!=GRB.UNDEFINED && double.IsFinite(v.Start))
            .ToDictionary(v=>v.VarName,v=>v.Start,StringComparer.Ordinal);
        var events=source.ArrivalVars.Concat(source.DepartureVars).ToDictionary(p=>p.Value.VarName,p=>p.Key,StringComparer.Ordinal);
        return linear.TightLinks(values,events,remaining);
    }

    // View has no separate ownership; ResourceCompiledModel disposes the native model/environment.
    internal BasicMilpModel View(ResourceCompiledModel local)
    {
        Dictionary<string,GRBVar> Map(IReadOnlyDictionary<string,GRBVar> original)=>original.ToDictionary(p=>p.Key,p=>local.Variables[p.Value.VarName],StringComparer.OrdinalIgnoreCase);
        return new(local.Environment,local.Model,Instance,Map(source.ArrivalVars),Map(source.DepartureVars),Map(source.StopVars),
            [],new Dictionary<string,GRBVar>(),[],new Dictionary<string,GRBVar>()) { ServiceWindows=Policy };
    }

    internal IReadOnlyList<ResourceInvalidSupplement> InvalidSupplements(ResourceCompiledModel local)
        =>source.RunningTimeSupplementVars.Values.Select(v=>(Name:v.VarName,Value:local.Value(v.VarName)))
            .Where(p=>!double.IsFinite(p.Value) || p.Value < -SolverNumericalValueNormalizer.BoundTolerance)
            .Take(16).Select(p=>new ResourceInvalidSupplement(p.Name,p.Value.ToString("R",System.Globalization.CultureInfo.InvariantCulture))).ToArray();

    internal MasterSolutionSnapshot Capture(ResourceCompiledModel local)
    {
        Dictionary<string,double> Values(IReadOnlyDictionary<string,GRBVar> map)=>map.ToDictionary(p=>p.Key,p=>local.Value(p.Value.VarName),StringComparer.OrdinalIgnoreCase);
        Dictionary<string,int> Bits(IReadOnlyDictionary<string,GRBVar> map)=>map.ToDictionary(p=>p.Key,p=>(int)Math.Round(local.Value(p.Value.VarName)),StringComparer.OrdinalIgnoreCase);
        var arrivals=Values(source.ArrivalVars);var departures=Values(source.DepartureVars);
        var supplements=source.RunningTimeSupplementVars.ToDictionary(p=>p.Key,p=>SolverNumericalValueNormalizer.NormalizeNonNegative(local.Value(p.Value.VarName)),StringComparer.OrdinalIgnoreCase);
        var business=BusinessObjectiveCalculator.Calculate(Instance.Trains.Select(t=>departures[t.OrderedStops[0].StopId]),
            Instance.Trains.Select(t=>arrivals[t.OrderedStops[^1].StopId]),supplements.Values);
        var result=new MasterSolutionSnapshot(local.Model.ObjVal,arrivals,departures,Bits(source.StopVars),Bits(source.SectionPrecedenceVars),
            Bits(source.StationEventPrecedenceVars),Instance.Trains.Select(t=>t.TrainIndex).ToHashSet(),supplements,business);
        return result with { ServiceObjective=ServiceBusinessObjectiveEvaluator.Calculate(Instance,result,Policy) };
    }
    public void Dispose()=>source.Dispose();
}
