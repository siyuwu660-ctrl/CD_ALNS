using Gurobi;
using GurobiTimetablingResearch.IO;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace GurobiTimetablingResearch.Optimization;

internal static class StrengthenedSpanBoundRunner
{
    internal static int Run(string[] args)
    {
        var values=new Dictionary<string,string>();
        foreach(var arg in args.Where(a=>a!="--strengthened-span-bound"))
        {
            int at=arg.IndexOf('=');
            if(at<1 || !values.TryAdd(arg[..at],arg[(at+1)..]))throw new ArgumentException("Malformed/duplicate argument");
        }
        string[] allowed=["--dataset","--bound-output","--bound-total","--bound-threads","--bound-memory-gb"];
        if(values.Keys.Except(allowed).Any())throw new ArgumentException("No local fixing, incumbent or positive-gap stopping options accepted");
        string Need(string k)=>values.GetValueOrDefault(k)??throw new ArgumentException("Missing "+k);
        double Number(string k,double d)=>values.TryGetValue(k,out var s)?double.Parse(s,CultureInfo.InvariantCulture):d;
        double budget=Number("--bound-total",43200),memory=Number("--bound-memory-gb",6);
        int threads=checked((int)Number("--bound-threads",2));
        if(!double.IsFinite(budget)||budget<=0||!double.IsFinite(memory)||memory<=0||threads<1)throw new ArgumentException("Invalid resource budget");
        string output=Path.GetFullPath(Need("--bound-output"));
        if(Directory.Exists(output)||File.Exists(output))throw new IOException("Refusing to overwrite bound evidence");
        Directory.CreateDirectory(output);
        var clock=Stopwatch.StartNew();double Left()=>Math.Max(0,budget-clock.Elapsed.TotalSeconds);
        double? baseline=null,best=null;string phase="Loading";int stages=0;
        void Write(string file,object v)=>Stages45Runner.Write(output,file,v);
        void Progress(string state,object? detail=null)=>Write("progress.json",new{phase=state,seconds=clock.Elapsed.TotalSeconds,remainingSeconds=Left(),baselineLowerBoundSeconds=baseline,lowerBoundSeconds=best,detail});
        int Finish(string status,int code=0,object? detail=null)
        {
            var value=new{status,seconds=clock.Elapsed.TotalSeconds,budgetSeconds=budget,baselineLowerBoundSeconds=baseline,lowerBoundSeconds=best,
                improvementSeconds=best-baseline,stages,scope=StrengthenedSpanBoundModel.Scope,originalOptimalityProven=false,detail};
            Write("summary.json",value);Progress("Finished",value);Console.WriteLine(JsonSerializer.Serialize(value));return code;
        }
        try
        {
            var dataset=Path.GetFullPath(Need("--dataset"));
            var hashes=StructuralSpanBoundRunner.HashDataset(dataset);
            var instance=new NormalizedDatasetLoader().Load(dataset);
            var paths=StructuralSpanBound.Build(instance,0);var capacity=StructuralCapacitySpanBound.Build(instance,paths);
            if(!hashes.SequenceEqual(StructuralSpanBoundRunner.HashDataset(dataset)))throw new IOException("Dataset changed during certificates");
            baseline=best=Math.Max(paths.LowerBoundSeconds,capacity.LowerBoundSeconds??double.NegativeInfinity);
            Write("configuration.json",new{dataset,dataFiles=hashes,budgetSeconds=budget,threads,memoryGB=memory,seed=0,
                scope=StrengthenedSpanBoundModel.Scope,fromOriginalData=true,localFixings=false,importedSnapshot=false,positiveGapStop=false,
                originalTimingTolerance=0,routeRelaxationSeconds=0,runningTimeSupplementRatio=0.05,ownRouteDomainPrecheck=true,endpointOccupationSeconds=StationEndpointOccupationPolicy.DefaultSeconds,endpointClipping=false,baselineLowerBoundSeconds=baseline,
                stationRanking="Full-domain section capacity bound descending, then station name",
                budgetIncludes="Data loading, certificates, model construction and optimization",
                notes="Relaxation incumbents are never original upper bounds. Exact macro order coupling is replaced by explicit tolerance outer rows in the final layer."});
            Write("structural_certificate.json",new{lowerBoundSeconds=baseline,paths,capacity});
            if(Left()<=0)return Finish("TimeLimitBeforeBuild");
            phase="BuildingMacro";Progress(phase);
            using var master=StrengthenedSpanBoundModel.Build(instance);
            var model=master.Model;
            StrengthenedSpanBoundModel.AddStructuralCuts(master,paths,capacity);
            model.Set(GRB.IntParam.Threads,threads);model.Set(GRB.IntParam.Seed,0);model.Set(GRB.IntParam.MIPFocus,3);
            model.Set(GRB.IntParam.NumericFocus,2);model.Set(GRB.DoubleParam.FeasibilityTol,1e-6);model.Set(GRB.DoubleParam.IntFeasTol,1e-8);
            model.Set(GRB.DoubleParam.MIPGap,0);model.Set(GRB.DoubleParam.MIPGapAbs,0);model.Set(GRB.DoubleParam.SoftMemLimit,memory);
            model.Set(GRB.IntParam.OutputFlag,1);model.Set(GRB.IntParam.LogToConsole,0);model.Set(GRB.StringParam.LogFile,Path.Combine(output,"gurobi.log"));
            var score=capacity.Sections.GroupBy(w=>w.EventSide.Equals("Departure",StringComparison.OrdinalIgnoreCase)?w.FromStation:w.ToStation)
                .ToDictionary(g=>g.Key,g=>g.Max(w=>w.LowerBoundSeconds),StringComparer.OrdinalIgnoreCase);
            var stations=instance.TrainStops.Select(s=>s.StationName).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(s=>score.GetValueOrDefault(s)).ThenBy(s=>s,StringComparer.Ordinal).ToArray();
            var targets=new[]{0,1,2,4,8,stations.Length}.Select(n=>Math.Min(n,stations.Length)).Distinct().ToList();
            targets.Add(stations.Length); // last layer also adds tolerance-safe order/dwell coupling
            int built=0;
            for(int stage=0;stage<targets.Count;stage++)
            {
                if(Left()<=0)return Finish("TimeLimit");
                bool final=stage==targets.Count-1;
                phase=final?"BuildingTolerantOrderCoupling":"BuildingStationResources";
                Progress(phase,new{stage,targetStations=targets[stage],builtStations=built});
                while(built<targets[stage])
                {
                    Progress("BuildingStation",new{stage,station=stations[built],builtStations=built});
                    StrengthenedSpanBoundModel.AddStation(master,stations[built],built,Left);built++;
                }
                if(final)StrengthenedSpanBoundModel.AddTolerantOrderCoupling(master,Left);
                model.Update();
                if(Left()<=0)return Finish("TimeLimitDuringBuild");
                phase=final?"FullToleranceOuterRelaxation":built==0?"MacroNecessaryConditions":"SelectedStationResources";
                double slice=final?Left():Math.Min(Left(),new double[]{600,1200,1800,3600,7200,7200}[Math.Min(stage,5)]);
                var live=new BoundProgress(output,phase,clock,baseline.Value,best!.Value,stage);
                model.SetCallback(live);model.Set(GRB.DoubleParam.TimeLimit,Math.Max(.001,slice));
                Progress("Optimizing",new{stage,modelPhase=phase,stations=built,variables=model.NumVars,rows=model.NumConstrs,seconds=slice});
                model.Optimize();stages++;
                if(live.Error is not null)throw new IOException(live.Error);
                if(!hashes.SequenceEqual(StructuralSpanBoundRunner.HashDataset(dataset)))throw new IOException("Dataset changed during solve");
                int status=model.Status;
                double? raw=CanUseBound(status)?Finite(model.ObjBoundC):null;
                best=MergeBound(best!.Value,raw,status);
                var row=new{stage,phase,seconds=clock.Elapsed.TotalSeconds,nativeStatus=status,nativeBound=raw,lowerBoundSeconds=best,
                    baselineLowerBoundSeconds=baseline,improvementSeconds=best-baseline,stations=built,variables=model.NumVars,rows=model.NumConstrs,
                    relaxedIncumbent= model.SolCount>0?Finite(model.ObjVal):null,relaxedIncumbentIsNotUpperBound=true};
                File.AppendAllText(Path.Combine(output,"bounds.jsonl"),JsonSerializer.Serialize(row)+Environment.NewLine);
                Progress("StageFinished",row);
                if(status==GRB.Status.MEM_LIMIT)return Finish("ResourceLimited");
                if(!CanUseBound(status))return Finish("NativeStatusNeedsReview",2,row);
                if(final && status==GRB.Status.OPTIMAL)
                    return Finish("OuterRelaxationOptimal_StrengtheningExhausted",0,new{originalOptimalityProven=false,
                        reason="All implemented necessary-condition layers are optimal; a stronger formulation or better original UB is required. No positive-gap threshold was used."});
            }
            return Finish("TimeLimit");
        }
        catch(Exception ex){Write("error.json",new{phase,error=ex.ToString()});return Finish(ex is TimeoutException?"TimeLimitDuringBuild":"Error",2);}
    }
    internal static bool CanUseBound(int status)=>status==GRB.Status.MEM_LIMIT || ResourceGlobalSpanBoundSolver.CanUseBound(status);
    internal static int WriteBuildEvidence(string output)
    {
        string binary=typeof(StrengthenedSpanBoundRunner).Assembly.Location;
        var binding=StructuralSpanBoundRunner.VerifyCompiledSources(binary);
        var value=new{binaryPath=binary,binarySha256=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(binary))),
            binding,sourceBinaryBinding="PortablePdbVerified",createdAt=DateTimeOffset.Now};
        using var stream=new FileStream(output,FileMode.CreateNew,FileAccess.Write,FileShare.Read);
        JsonSerializer.Serialize(stream,value,new JsonSerializerOptions{WriteIndented=true,PropertyNamingPolicy=JsonNamingPolicy.CamelCase});
        return 0;
    }
    internal static double MergeBound(double previous,double? raw,int status)
    {
        if(!CanUseBound(status)||raw is null||Finite(raw.Value) is null)return previous;
        return Math.Max(previous,raw.Value-1e-6);
    }
    internal static double? Finite(double value)=>double.IsFinite(value)&&Math.Abs(value)<GRB.INFINITY?value:null;
    private sealed class BoundProgress(string output,string phase,Stopwatch clock,double baseline,double prior,int stage):GRBCallback
    {
        double last=-30;
        internal string? Error{get;private set;}
        protected override void Callback()
        {
            try
            {
                if(where!=GRB.Callback.MIP || clock.Elapsed.TotalSeconds-last<30)return;
                last=clock.Elapsed.TotalSeconds;
                var raw=Finite(GetDoubleInfo(GRB.Callback.MIP_OBJBND));
                var row=new{phase,stage,seconds=last,baselineLowerBoundSeconds=baseline,
                    lowerBoundSeconds=raw is null?prior:Math.Max(prior,raw.Value-1e-6),
                    provisionalCallbackBound=raw,finalStatusValidationPending=true,nodes=GetDoubleInfo(GRB.Callback.MIP_NODCNT),
                    relaxedIncumbent=Finite(GetDoubleInfo(GRB.Callback.MIP_OBJBST)),relaxedIncumbentIsNotUpperBound=true};
                Stages45Runner.Write(output,"progress.json",row);
                File.AppendAllText(Path.Combine(output,"bounds.jsonl"),JsonSerializer.Serialize(row)+Environment.NewLine);
            }
            catch(Exception ex){Error=ex.ToString();Abort();}
        }
    }
}
