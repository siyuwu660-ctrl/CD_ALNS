using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Gurobi;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

internal sealed record SeedSkeletonResult(bool IsQuotaFeasible,string Status,string Method,
    IReadOnlyDictionary<string,int> Stops,string Fingerprint,double Seconds,IReadOnlyList<string> Audit,
    string Role="QuotaSkeletonNotTimetable");

internal static class QuotaStopSkeletonSolver
{
    internal static SeedSkeletonResult Solve(TimetablingInstance instance,int seed,double seconds,
        IReadOnlyList<StopLinearCut>? strengthening=null)
    {
        if(!double.IsFinite(seconds) || seconds<=0) throw new ArgumentOutOfRangeException(nameof(seconds));
        Stopwatch clock=Stopwatch.StartNew();List<string> audit=[];
        var domains=StructuralStopDomains.Build(instance);
        if(domains.Any(p=>p.Value.Contradiction)) return Result(false,"ProvenEmptyStopDomain","None",new Dictionary<string,int>());
        var trainRows=TrainSectionStopQuotaConstraintBuilder.Plan(instance);
        var stationRows=instance.StationStopQuotaRules.Select((r,k)=>new { Rule=r,Index=k,
            Stops=instance.TrainStops.Where(s=>s.StationName.Equals(r.StationName,StringComparison.OrdinalIgnoreCase) && s.TrainType.Equals(r.TrainType,StringComparison.OrdinalIgnoreCase)).ToArray() }).ToArray();
        bool fullDay=stationRows.All(r=>r.Stops.All(s=>r.Rule.StartTimeSeconds<=EventBounds(s).lo && r.Rule.EndTimeSeconds>=EventBounds(s).hi));
        bool oneTrain=instance.TrainStops.All(s=>trainRows.Count(r=>r.CountedStops.Any(t=>t.StopId==s.StopId))<=1);
        bool oneStation=instance.TrainStops.All(s=>stationRows.Count(r=>r.Stops.Any(t=>t.StopId==s.StopId))<=1);
        bool network=fullDay && oneTrain && oneStation && (strengthening is null || strengthening.Count==0);
        audit.Add($"NetworkCertificate: full-event-domain={fullDay}; train-incidence<=1={oneTrain}; station-incidence<=1={oneStation}; extraRows={strengthening?.Count??0}.");
        // Negating station rows gives a directed bipartite node-arc incidence matrix with integral bounds.
        // A simplex vertex is integral; integrality is still checked before accepting any LP result.
        using GRBEnv env=new(true);env.Set(GRB.IntParam.OutputFlag,0);env.Start();using GRBModel model=new(env);
        char type=network?GRB.CONTINUOUS:GRB.BINARY;Random random=new(seed);
        var vars=instance.TrainStops.ToDictionary(s=>s.StopId,s=>model.AddVar(domains[s.StopId].Lower,domains[s.StopId].Upper,
            (s.IdealStop?-0.05:0.05)+random.NextDouble(),type,s.StopId));
        foreach(var row in trainRows) AddRange(row.CountedStops.Select(s=>vars[s.StopId]),row.MinStops,row.MaxStops,row.RuleId);
        Dictionary<string,GRBVar> eventVars=[];List<(StationStopQuotaRule rule,TrainStopRecord[] stops,Dictionary<string,GRBVar> counted)> checks=[];
        foreach(var row in stationRows)
        {
            Dictionary<string,GRBVar> counted=[];
            foreach(var s in row.Stops)
            {
                var (lo,hi)=EventBounds(s);
                if(row.Rule.StartTimeSeconds<=lo && row.Rule.EndTimeSeconds>=hi) { counted[s.StopId]=vars[s.StopId];continue; }
                if(!eventVars.TryGetValue(s.StopId,out var t)) { t=model.AddVar(lo,hi,0,GRB.CONTINUOUS,$"event::{s.StopId}");eventVars.Add(s.StopId,t); }
                string key=$"r{row.Index}::{s.StopId}";
                var inside=model.AddVar(0,1,0,GRB.BINARY,$"in::{key}");var left=model.AddVar(0,1,0,GRB.BINARY,$"left::{key}");var right=model.AddVar(0,1,0,GRB.BINARY,$"right::{key}");
                StationQuotaWindowPolicy.RestrictToPermanentBounds(inside,left,right,lo,hi,row.Rule.StartTimeSeconds,row.Rule.EndTimeSeconds);
                model.AddConstr(inside+left+right,GRB.EQUAL,vars[s.StopId],$"class::{key}");
                double lm=Math.Max(0,hi-(row.Rule.StartTimeSeconds)),rm=Math.Max(0,row.Rule.EndTimeSeconds-lo);
                model.AddConstr(t+lm*left,GRB.LESS_EQUAL,row.Rule.StartTimeSeconds+lm,$"leftub::{key}");
                model.AddConstr(t-rm*right,GRB.GREATER_EQUAL,row.Rule.EndTimeSeconds-rm,$"rightlb::{key}");
                double il=Math.Max(0,row.Rule.StartTimeSeconds-lo),iu=Math.Max(0,hi-row.Rule.EndTimeSeconds);
                model.AddConstr(t-il*inside,GRB.GREATER_EQUAL,row.Rule.StartTimeSeconds-il,$"inlb::{key}");
                model.AddConstr(t+iu*inside,GRB.LESS_EQUAL,row.Rule.EndTimeSeconds+iu,$"inub::{key}");counted.Add(s.StopId,inside);
            }
            AddRange(counted.Values,row.Rule.MinStops,row.Rule.MaxStops,$"station_{row.Index}");checks.Add((row.Rule,row.Stops,counted));
        }
        foreach(var cut in strengthening??[]) cut.Apply(model,vars);
        model.Set(GRB.IntParam.Method,1);model.Set(GRB.IntParam.Threads,4);model.Set(GRB.IntParam.Seed,seed);
        model.Set(GRB.DoubleParam.TimeLimit,Math.Max(0.001,seconds-clock.Elapsed.TotalSeconds));model.Optimize();
        string method=network?"CertifiedNetworkLP":"BinaryQuotaMILP";
        if(model.SolCount==0) return Result(false,model.Status==GRB.Status.INFEASIBLE?"ProvenQuotaInfeasible":"UnknownQuotaBudget",method,new Dictionary<string,int>());
        if(vars.Values.Any(v=>Math.Abs(v.X-Math.Round(v.X))>1e-6))
            return Result(false,"UnknownFractionalVertexRejected",method,new Dictionary<string,int>());
        var stops=vars.ToDictionary(p=>p.Key,p=>(int)Math.Round(p.Value.X));
        foreach(var p in stops) if(p.Value<domains[p.Key].Lower || p.Value>domains[p.Key].Upper) throw new InvalidDataException("Illegal stop seed.");
        foreach(var row in trainRows)
        {
            int count=row.CountedStops.Sum(s=>stops[s.StopId]);if(count<row.MinStops || count>row.MaxStops) throw new InvalidDataException("Seed train quota violation.");
        }
        foreach(var row in checks)
        {
            int count=row.stops.Count(s=>stops[s.StopId]==1 && InWindow(s,row.rule));
            if(count<row.rule.MinStops || count>row.rule.MaxStops)
            {
                // Outside-branch closures overlap the closed business window at its endpoints.
                // A rejected witness is unknown, not a proof that every seed is infeasible.
                audit.Add($"Closed-window recount rejected native quota witness at {row.rule.StationName}: count={count}; required=[{row.rule.MinStops},{row.rule.MaxStops}].");
                return Result(false,"UnknownQuotaBoundaryCandidate",method,new Dictionary<string,int>());
            }
        }
        if((strengthening??[]).Any(c=>!c.Satisfied(stops))) throw new InvalidDataException("Seed strengthening violation.");
        audit.Add("Independent integer/domain/train-quota/station-quota/strengthening checks passed; event values are quota witnesses only, not train timetables.");
        return Result(true,"QuotaFeasible",method,stops);

        bool InWindow(TrainStopRecord s,StationStopQuotaRule r)
        {
            if(!eventVars.TryGetValue(s.StopId,out var t)) return true;
            return StationQuotaWindowPolicy.Contains(t.X,r.StartTimeSeconds,r.EndTimeSeconds);
        }
        void AddRange(IEnumerable<GRBVar> variables,int min,int max,string name)
        {
            GRBLinExpr expr=new();foreach(var v in variables) expr.AddTerm(1,v);
            model.AddConstr(expr,GRB.GREATER_EQUAL,min,$"min::{name}");model.AddConstr(expr,GRB.LESS_EQUAL,max,$"max::{name}");
        }
        SeedSkeletonResult Result(bool feasible,string status,string method,IReadOnlyDictionary<string,int> stops)
        {
            string fingerprint=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n",stops.OrderBy(p=>p.Key,StringComparer.Ordinal).Select(p=>$"{p.Key}={p.Value}")))));
            return new(feasible,status,method,stops,fingerprint,clock.Elapsed.TotalSeconds,audit);
        }
        static (int lo,int hi) EventBounds(TrainStopRecord s)=>s.StopRole==TrainStopRole.Origin?(s.EarliestDepSeconds,s.LatestDepSeconds):(s.EarliestArrSeconds,s.LatestArrSeconds);
    }
}
