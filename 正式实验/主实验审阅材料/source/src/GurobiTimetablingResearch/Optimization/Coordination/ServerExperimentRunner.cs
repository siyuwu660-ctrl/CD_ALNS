using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Gurobi;
using GurobiTimetablingResearch.Domain;
using GurobiTimetablingResearch.IO;

namespace GurobiTimetablingResearch.Optimization;

/// <summary>Runs package preflight or one explicitly configured from-zero experiment.</summary>
internal static class ServerExperimentRunner
{
    [DllImport("gurobi100", EntryPoint = "GRBversion", CallingConvention = CallingConvention.Cdecl)]
    private static extern void GetNativeVersion(out int major, out int minor, out int patch);

    /// <summary>Validate the package invocation and dispatch to the selected method.</summary>
    /// <param name="args">Package command and its unique key-value arguments.</param>
    /// <returns>Zero for a passed check or a strict result, two for a completed run without a strict result, one for a failure.</returns>
    internal static int Run(string[] args)
    {
        try
        {
            if (args.SequenceEqual(new[] { "--server-package", "--license-check" })) return ProbeLicense();
            if (args.SequenceEqual(new[] { "--server-package", "--quota-selftest" })) return CheckQuotaControl();
            bool checkOnly = args.Contains("--check-only", StringComparer.Ordinal);
            string[] keys = ["--method", "--dataset", "--output", "--total", "--reserve", "--macro", "--bound", "--seed", "--quota-mode"];
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string arg in args.Skip(1))
            {
                if (arg == "--check-only") continue;
                int separator = arg.IndexOf('=');
                if (separator < 1 || !keys.Contains(arg[..separator]) || !values.TryAdd(arg[..separator], arg[(separator + 1)..]))
                    throw new ArgumentException("Unexpected or duplicate package argument: " + arg);
            }
            string Required(string key) => values.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value)
                ? value : throw new ArgumentException("Missing " + key);
            int Number(string key) => int.Parse(Required(key), NumberStyles.None, CultureInfo.InvariantCulture);

            string method = Required("--method");
            string mode = Required("--quota-mode");
            if (method is not ("alns" or "mip" or "lbbd") || mode is not ("guided" or "control") || (mode == "control" && method != "alns"))
                throw new ArgumentException("Invalid method or quota comparison mode.");
            string input = Path.GetFullPath(Required("--dataset"));
            string output = Path.GetFullPath(Required("--output"));
            int total = Number("--total");
            int reserve = Number("--reserve");
            int macro = Number("--macro");
            int bound = Number("--bound");
            int seed = Number("--seed");
            if (total <= 0 || reserve <= 0 || macro <= 0 || macro + reserve >= total || bound > total || seed > 4)
                throw new ArgumentException("Invalid package experiment budgets or seed.");

            var clock = Stopwatch.StartNew();
            TimetablingInstance instance = new NormalizedDatasetLoader().Load(input);
            if (checkOnly)
            {
                var modes = StopModePreprocessor.Analyze(instance);
                var bounds = HardEventBoundPropagator.Build(instance);
                StationQuotaPreflight.Validate(instance, modes, bounds);
                if (modes.Contradictions.Count > 0 || bounds.Contradictions.Count > 0)
                    throw new InvalidDataException("Static visit or time contradictions in " + instance.Meta.InstanceName);
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    method, mode, total, reserve, macro, bound, seed, threads = 4,
                    caseName = instance.Meta.InstanceName, trains = instance.Trains.Count,
                    stations = instance.Stations.Count, stationQuotas = instance.StationStopQuotaRules.Count,
                    licenseChecked = false
                }));
                return 0;
            }
            if (Directory.Exists(output) || File.Exists(output)) throw new IOException("Experiment output already exists.");
            double remaining = total - clock.Elapsed.TotalSeconds;
            if (remaining <= macro + reserve) throw new TimeoutException("Insufficient budget after input loading.");

            // Propagate the same remaining wall budget through all methods, including verification.
            string status;
            bool joint;
            switch (method)
            {
                case "alns":
                    var result = JhFromZeroRunner.Solve(instance, new(output, remaining, macro, reserve, seed,
                        BoundTotalSeconds: bound, UseAlns: true, ImproveQuality: true, GuideFeasibilitySpan: true,
                        QuotaControl: mode == "control"));
                    status = result.Status;
                    joint = result.Joint?.JointFeasible == true;
                    break;
                case "mip":
                    var mip = DirectJointMipRunner.Solve(instance, new(output, remaining, reserve, seed, Threads: 4));
                    status = mip.Status;
                    joint = mip.JointFeasible;
                    break;
                default:
                    var lbbd = CormanLbbdRunner.Solve(instance, new(output, remaining, remaining - reserve,
                        remaining, Seed: seed, Threads: 4, SpanFirst: true));
                    status = lbbd.Status;
                    joint = lbbd.JointFeasible;
                    break;
            }
            Console.WriteLine(JsonSerializer.Serialize(new { method, mode, status, joint, seconds = clock.Elapsed.TotalSeconds }));
            return status.StartsWith("Error", StringComparison.OrdinalIgnoreCase) ? 1 : joint ? 0 : 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.ToString());
            return 1;
        }
    }

    /// <summary>Test the loaded Windows x64 library, configured thread count and unrestricted model-size licensing.</summary>
    /// <returns>Zero after a bounded license probe succeeds.</returns>
    /// <exception cref="InvalidOperationException">The native version, thread count or probe outcome differs from the package contract.</exception>
    private static int ProbeLicense()
    {
        GetNativeVersion(out int major, out int minor, out int patch);
        if ((major, minor, patch) != (10, 0, 1) || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new InvalidOperationException("Expected Windows x64 Gurobi 10.0.1.");
        using var environment = new GRBEnv(true);
        environment.Set(GRB.IntParam.OutputFlag, 0);
        environment.Start();
        using var model = new GRBModel(environment);
        if (model.Get(GRB.IntParam.Threads) != 4)
            throw new InvalidOperationException("Expected Threads=4 in the job's gurobi.env.");
        model.Set(GRB.DoubleParam.TimeLimit, 15);
        var variables = model.AddVars(2101, GRB.BINARY);
        GRBLinExpr count = new();
        foreach (GRBVar variable in variables) count.AddTerm(1, variable);
        model.AddConstr(count, GRB.GREATER_EQUAL, 1, "license_ge_one");
        model.SetObjective(count, GRB.MINIMIZE);
        model.Optimize();
        if (model.Status != GRB.Status.OPTIMAL || Math.Abs(model.ObjVal - 1) > 1e-8)
            throw new InvalidOperationException("License-size probe did not solve its synthetic model.");
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            passed = true, native = $"{major}.{minor}.{patch}", architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            runtime = RuntimeInformation.FrameworkDescription, threads = model.Get(GRB.IntParam.Threads),
            variables = model.NumVars
        }));
        return 0;
    }

    /// <summary>Exercise the package's ordinary and quota-guided expansion against a synthetic stop count.</summary>
    /// <returns>Zero when the guided and control expansions choose different eligible cells of the same size.</returns>
    private static int CheckQuotaControl()
    {
        var trains = Enumerable.Range(1, 3).Select(index =>
        {
            string number = "T" + index;
            TrainStopRecord Visit(int order, string station, TrainStopRole role, StopRequirement requirement, int time) =>
                new($"{number}-{station}", index, number, index == 2 ? "other" : "quota", order, station,
                    order == 1 ? null : order == 2 ? "A-B" : "B-C", null, null,
                    time, time, time, time, true, 1, requirement, 0, 100, order == 1 ? null : "scale", null, 0, 0, 1, 1, role);
            int middle = index == 3 ? 1600 : index == 2 ? 1150 : 1100;
            return new TrainRecord(index, number, index == 2 ? "other" : "quota",
                [Visit(1, "A", TrainStopRole.Origin, StopRequirement.MustStop, middle - 100),
                 Visit(2, "B", TrainStopRole.Intermediate, StopRequirement.OptionalStop, middle),
                 Visit(3, "C", TrainStopRole.Destination, StopRequirement.MustStop, middle + 100)]);
        }).ToArray();
        var stops = trains.SelectMany(train => train.OrderedStops).ToArray();
        var routes = new RouteRecord[]
        {
            new(1, "A", "A-B", "1", "departure", MovementEvent.Departure),
            new(2, "B", "A-B", "1", "arrival", MovementEvent.Arrival),
            new(3, "B", "B-C", "1", "departure", MovementEvent.Departure),
            new(4, "B", "A-B", "1", "pass-arrival", MovementEvent.Passing),
            new(5, "B", "B-C", "1", "pass-departure", MovementEvent.Passing),
            new(6, "C", "B-C", "1", "arrival", MovementEvent.Arrival)
        };
        TimetablingInstance instance = new(new("quota-fixture", "", "second"),
            new(["A-B", "B-C"], new Dictionary<string, int> { ["A-B"] = 0, ["B-C"] = 1 }),
            ["A", "B", "C"], [], trains, stops, [], [], [new("B", "quota", 0, 86399, 1, 3)], [], [],
            routes, [], [], [], [], []);
        var times = stops.ToDictionary(stop => stop.StopId, stop => (double)stop.EarliestArrSeconds);
        MasterSolutionSnapshot anchor = new(0, times, times,
            stops.ToDictionary(stop => stop.StopId, stop => stop.StopId == "T3-B" ? 0 : 1),
            new Dictionary<string, int>(), new Dictionary<string, int>(), new HashSet<int> { 1, 2, 3 });
        var guided = QuotaAblation.Expand(instance, anchor, ["T1-B"], 2, 2, null, false);
        var control = QuotaAblation.Expand(instance, anchor, ["T1-B"], 2, 2, null, true);
        if (!guided.FreeStopIds.SetEquals(["T1-B", "T3-B"]) || !control.FreeStopIds.SetEquals(["T1-B", "T2-B"]) ||
            guided.FreeTimeStopIds.Count != control.FreeTimeStopIds.Count || guided.Rows.Count != control.Rows.Count)
            throw new InvalidOperationException("Quota-control fixture did not preserve size with ordinary selection.");
        Console.WriteLine("PASS matched quota expansion: guided T3-B, ordinary T2-B; same hard quota rows");
        return 0;
    }
}
