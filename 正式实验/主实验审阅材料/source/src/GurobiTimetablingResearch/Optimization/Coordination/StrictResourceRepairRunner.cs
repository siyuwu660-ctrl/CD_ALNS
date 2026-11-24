using System.Diagnostics;
using System.Security.Cryptography;
using GurobiTimetablingResearch.Domain;
using GurobiTimetablingResearch.IO;

namespace GurobiTimetablingResearch.Optimization;

/// <summary>Migration repair of a saved timetable; never a from-zero performance benchmark.</summary>
internal static class StrictResourceRepairRunner
{
    internal static int Run(string[] args)
    {
        string Required(string key)=>args.Single(a=>a.StartsWith(key,StringComparison.Ordinal))[key.Length..];
        string dataset=Path.GetFullPath(Required("--dataset="));
        string input=Path.GetFullPath(Required("--repair-snapshot="));
        string output=Path.GetFullPath(Required("--repair-output="));
        bool wide=args.Contains("--repair-wide",StringComparer.Ordinal);
        if(Directory.Exists(output))throw new IOException("Refusing to overwrite repair evidence.");
        Directory.CreateDirectory(output);
        double budget=1800;
        if(args.FirstOrDefault(a=>a.StartsWith("--repair-total=")) is string raw)
            budget=double.Parse(raw["--repair-total=".Length..],System.Globalization.CultureInfo.InvariantCulture);
        if(!double.IsFinite(budget)||budget<60)throw new ArgumentOutOfRangeException(nameof(budget));
        var clock=Stopwatch.StartNew();double nextMemoryCheck=0;
        double Left()
        {
            if(clock.Elapsed.TotalSeconds>=nextMemoryCheck)
            {
                nextMemoryCheck=clock.Elapsed.TotalSeconds+.5;
                using var process=Process.GetCurrentProcess();
                if(process.PrivateMemorySize64>12L*1024*1024*1024)
                    throw new InsufficientMemoryException("Restricted repair private-memory limit: 12 GiB.");
            }
            return Math.Max(0,budget-clock.Elapsed.TotalSeconds);
        }
        void Write(string name,object value)=>Stages45Runner.Write(output,name,value);
        void Progress(string phase,object? details=null)=>Write("progress.json",new{phase,seconds=clock.Elapsed.TotalSeconds,details});
        var instance=new NormalizedDatasetLoader().Load(dataset);
        var anchor=MasterSolutionSnapshotStore.Load(input,instance.Meta.InstanceName,instance.Trains.Select(t=>t.TrainIndex).ToHashSet()) with
            {VerifiedMicroAssignments=null,BoundReport=null,SolutionRole=TimetableSolutionRole.WorkingSolution};
        var policy=ServiceWindowPolicy.Create(instance,new(true));
        var original=ServiceBusinessObjectiveEvaluator.Calculate(instance,anchor,policy);
        Write("configuration.json",new{kind="StrictContractMigrationRepair_NotFromZeroBenchmark",dataset,input,budget,
            inputSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(input))),
            binarySha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(StrictResourceRepairRunner).Assembly.Location))),
            dataFiles=Directory.GetFiles(dataset,"*.csv").ToDictionary(p=>Path.GetFileName(p)!,p=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))),
            strictBusinessTimingRelaxationSeconds=0,numericToleranceSeconds=TimetableTimePolicy.NumericToleranceSeconds,
            processPrivateMemoryLimitGiB=12,passingDurationSeconds=0,clearanceInRecordedHeadways=true,endpointOccupationSeconds=StationEndpointOccupationPolicy.DefaultSeconds,
            stoppingPatternFixed=true,originDeparturesAndDestinationArrivalsFixed=!wide,original});
        try
        {
            Progress("FixedTimeResourceReassignment");
            var profile=ResourceConflictProfiler.Evaluate(instance,anchor,Left);
            var diagnosis=DirectedCandidateDiagnostics.Analyze(instance,anchor);
            Write("initial_diagnosis.json",new{profile.Complete,profile.UnknownStations,profile.ViolationSeconds,profile.Conflicts,
                stations=profile.Stations.Values.Select(s=>new{s.StationName,s.Status,s.CoreStopIds}),
                macroConflicts=diagnosis.Conflicts,diagnosis.UnresolvedLongDwellStopIds});
            if(ResourceLnsRunner.ShouldStrictlyVerify(profile) && Verify(anchor,0))return 0;
            var seeds=profile.Conflicts.SelectMany(c=>new[]{c.FirstStopId,c.SecondStopId})
                .Concat(profile.Stations.Values.SelectMany(s=>s.CoreStopIds))
                .Concat(diagnosis.Conflicts.SelectMany(c=>new[]{c.FirstStopId,c.SecondStopId}))
                .Concat(diagnosis.UnresolvedLongDwellStopIds).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if(seeds.Count==0)throw new InvalidDataException("No diagnosed repair scope; refusing an unconstrained rerun.");
            var focus=seeds.Select(id=>instance.StopsById[id].StationOrder).ToArray();
            Progress("BuildMacroTemplate",new{seeds=seeds.Count});
            using var template=new ResourceMacroTemplate(instance,policy,anchor,Left);
            int round=0;
            foreach(int radius in wide?new[]{100,101}:new[]{2,4,7})
            {
                if(Left()<120)break;round++;
                int low=focus.Min()-radius,high=focus.Max()+radius;
                var free=instance.TrainStops.Where(s=>(wide || s.StopRole==TrainStopRole.Intermediate) && s.StationOrder>=low && s.StationOrder<=high)
                    .Select(s=>s.StopId).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var neighborhood=new ResourceNeighborhood(seeds,free,new HashSet<string>(StringComparer.OrdinalIgnoreCase),0,radius is 2 or 100?600:1800);
                double seconds=Math.Min(wide?600:round==1?240:round==2?360:600,Left()-90);
                Progress("LocalStrictRepair",new{round,radius,freeOperations=free.Count,seconds,originDeparturesAndDestinationArrivalsFixed=!wide});
                var result=ResourceLocalSolver.Solve(template,anchor,neighborhood,profile,
                    new(seconds,Path.Combine(output,$"repair_{round}.log"),RequireZeroMicro:true,BoundedAlnsConstruction:true,StopAtZeroMicro:true),Left);
                Write($"repair_{round}.json",result with{Candidate=null,SelectedResources=null,TightLinks=null});
                if(result.Candidate is null)continue;
                var candidate=result.Candidate;
                MasterSolutionSnapshotStore.Save(Path.Combine(output,$"candidate_{round}.snapshot.json"),instance.Meta.InstanceName,candidate);
                Progress("GlobalResourceCheck",new{round});
                var checkedProfile=ResourceConflictProfiler.Evaluate(instance,candidate,Left,preferred:result.SelectedResources);
                Write($"profile_{round}.json",new{checkedProfile.Complete,checkedProfile.UnknownStations,checkedProfile.ViolationSeconds,checkedProfile.Conflicts});
                if(ResourceLnsRunner.ShouldStrictlyVerify(checkedProfile) && Verify(candidate,round))return 0;
            }
            Progress("FinishedWithoutVerifiedRepair");
            Write("summary.json",new{status="NoVerifiedRepairWithinRestrictedAttempts",jointFeasible=false,seconds=clock.Elapsed.TotalSeconds,original});
            return 2;
        }
        catch(Exception e)
        {
            Write("error.json",new{error=e.ToString(),seconds=clock.Elapsed.TotalSeconds});
            Progress("Error");return 2;
        }

        bool Verify(MasterSolutionSnapshot candidate,int round)
        {
            Progress("CompleteMacroMicroAndSavedReadback",new{round});
            var proof=Stages45JointVerifier.Verify(instance,candidate,policy,Left,Math.Min(120,Left()),output,round,promotionSlots:0);
            if(!proof.JointFeasible||proof.Canonical is null||proof.Readback?.Passed!=true)return false;
            var best=proof.Canonical;var objective=ServiceBusinessObjectiveEvaluator.Calculate(instance,best,policy);
            MasterSolutionSnapshotStore.Save(Path.Combine(output,"repaired_joint.snapshot.json"),instance.Meta.InstanceName,best);
            Write("summary.json",new{status="JointFeasible",jointFeasible=true,seconds=clock.Elapsed.TotalSeconds,round,original,objective,
                stopChanges=best.StopByStopId.Count(p=>p.Value!=anchor.StopByStopId[p.Key]),
                changedArrivals=best.ArrivalSecondsByStopId.Count(p=>Math.Abs(p.Value-anchor.ArrivalSecondsByStopId[p.Key])>1e-6),
                changedDepartures=best.DepartureSecondsByStopId.Count(p=>Math.Abs(p.Value-anchor.DepartureSecondsByStopId[p.Key])>1e-6),
                maxEventChangeSeconds=best.ArrivalSecondsByStopId.Select(p=>Math.Abs(p.Value-anchor.ArrivalSecondsByStopId[p.Key]))
                    .Concat(best.DepartureSecondsByStopId.Select(p=>Math.Abs(p.Value-anchor.DepartureSecondsByStopId[p.Key]))).Max(),
                proof.Readback,role="RepairedHistoricalSolution_NotFromZeroBenchmark"});
            Progress("JointFeasible",new{objective,round});return true;
        }
    }
}
