using System.Text;
using System.Text.Json;
using System.Diagnostics;
using Gurobi;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

internal sealed record FreshCompleteMacroSolveResult(
    string SnapshotPath,
    string CandidateFingerprint,
    int RelationCount,
    int VariableCount,
    int BinaryVariableCount,
    int ConstraintCount,
    int DerivedPrecedenceStartCount,
    double RuntimeSeconds,
    string SolverStatus,
    double? FirstIncumbentSeconds,
    bool FeasibilityFirst,
    MacroMipStartCoverageAudit? MipStartCoverage,
    int ValidationViolationCount,
    bool ReadbackIdentityMatches,
    double BuildSeconds);

/// <summary>
/// Rebuilds the complete Macro after the reduced rolling construction.  The
/// rolling snapshot is used only as a MIP start; every complete relation and
/// every original business constraint remains active and free to change.
/// </summary>
internal static class FreshCompleteMacroSolver
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    internal static FreshCompleteMacroSolveResult Solve(
        TimetablingInstance instance,
        MasterSolutionSnapshot rollingSeed,
        string outputDirectory,
        double runningTimeSupplementRatio,
        int endpointOccupationSeconds,
        double timeLimitSeconds,
        int? expectedRelationCount,
        IReadOnlyList<string>? seedValidationViolations = null)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(rollingSeed);
        if (timeLimitSeconds <= 0.0 || !double.IsFinite(timeLimitSeconds))
            throw new ArgumentOutOfRangeException(nameof(timeLimitSeconds));

        string output = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(output);
        string logPath = Path.Combine(output, "complete_macro.log");
        string summaryPath = Path.Combine(output, "complete_macro_summary.json");
        string snapshotPath = Path.Combine(output, "complete_macro.snapshot.json");

        Stopwatch buildClock = Stopwatch.StartNew();
        using BasicMilpModel model = new BasicMilpModelBuilder(
            instance,
            neighborRadius: int.MaxValue,
            runningTimeSupplementRatio: runningTimeSupplementRatio,
            endpointOccupationSeconds: endpointOccupationSeconds).Build();
        buildClock.Stop();
        int relationCount = model.SectionConflictCandidates.Count;
        if (expectedRelationCount.HasValue && relationCount != expectedRelationCount.Value)
            throw new InvalidDataException(
                $"Complete Macro relation count mismatch: expected={expectedRelationCount.Value}, actual={relationCount}.");

        VerifiedMicroAssignmentMipStartResult mipStart =
            MasterSolutionSnapshotApplier.ApplyExpandableMacroMipStart(
                model,
                rollingSeed,
                seedValidationViolations);
        File.WriteAllText(logPath, string.Empty, new UTF8Encoding(false));
        model.Model.Set(GRB.StringParam.LogFile, logPath);
        model.Model.Set(GRB.DoubleParam.TimeLimit, timeLimitSeconds);
        ConfigureFeasibilityFirst(model);
        FirstIncumbentTrackingCallback incumbentCallback = new(stopAfterFirstIncumbent: true);
        model.Model.SetCallback(incumbentCallback);
        Console.WriteLine(
            $"Complete Macro build finished: seconds={buildClock.Elapsed.TotalSeconds:F3}, " +
            $"variables={model.Model.Get(GRB.IntAttr.NumVars)}, constraints={model.Model.Get(GRB.IntAttr.NumConstrs)}.");
        Console.WriteLine(
            $"Complete Macro feasibility-first solve from radius seed: relations={relationCount}, " +
            $"derived_precedence_starts={mipStart.StartedDerivedSectionPrecedenceVariableCount}, " +
            $"mip_start_coverage={mipStart.MacroCoverage?.CoverageRatio:P2}, " +
            $"time_limit_seconds={timeLimitSeconds:F0}, decisions_fixed=False.");
        model.Optimize();
        model.Model.Set(GRB.StringParam.LogFile, string.Empty);

        int variableCount = model.Model.Get(GRB.IntAttr.NumVars);
        int binaryVariableCount = model.Model.GetVars().Count(variable =>
            variable.Get(GRB.CharAttr.VType) == GRB.BINARY);
        int constraintCount = model.Model.Get(GRB.IntAttr.NumConstrs);
        MasterValidationReport? validation = model.HasIncumbentSolution
            ? new MasterSolutionValidator().Validate(model)
            : null;
        WriteJson(summaryPath, new
        {
            stage = "radius4_seed_to_complete_macro",
            seedIsFixed = false,
            seedObjective = rollingSeed.ObjectiveValue,
            relationCount,
            variableCount,
            binaryVariableCount,
            constraintCount,
            mipStart = new
            {
                mipStart.StartedDerivedSectionPrecedenceVariableCount,
                radiusSeedPrecedenceCount = rollingSeed.SectionPrecedenceByKey.Count,
                completePrecedenceCount = model.SectionPrecedenceVars.Count,
                coverage = mipStart.MacroCoverage
            },
            solverStatus = model.StatusName,
            solutionCount = model.Model.Get(GRB.IntAttr.SolCount),
            runtimeSeconds = model.Model.Get(GRB.DoubleAttr.Runtime),
            firstIncumbentSeconds = incumbentCallback.FirstIncumbentTimeSeconds,
            feasibilityFirst = true,
            buildSeconds = buildClock.Elapsed.TotalSeconds,
            bigM = SummarizeBigM(model.ConditionalBigMAuditRows),
            validatorViolationCount = validation?.Violations.Count,
            validatorFeasible = validation?.IsFeasible,
            readbackIdentityMatches = (bool?)null
        });

        if (!model.HasIncumbentSolution)
            throw new InvalidOperationException(
                $"Complete Macro solve ended as {model.StatusName} without an incumbent after " +
                $"{model.Model.Get(GRB.DoubleAttr.Runtime):F3} seconds. The radius-four seed remains saved, " +
                "but Micro was not started.");
        if (validation is null || !validation.IsFeasible || validation.Violations.Count != 0)
            throw new InvalidDataException(
                "Complete Macro incumbent failed independent validation: " +
                string.Join(" | ", validation?.Violations ?? []));

        MasterSolutionSnapshot completeSnapshot = MasterSolutionSnapshot.Capture(model);
        MasterSolutionSnapshotStore.Save(snapshotPath, instance.Meta.InstanceName, completeSnapshot);
        string fingerprint = SnapshotFingerprint.Calculate(completeSnapshot);
        MasterSolutionSnapshot readback = MasterSolutionSnapshotStore.Load(
            snapshotPath,
            instance.Meta.InstanceName,
            instance.Trains.Select(train => train.TrainIndex).ToHashSet());
        bool readbackIdentityMatches = string.Equals(
            fingerprint,
            SnapshotFingerprint.Calculate(readback),
            StringComparison.OrdinalIgnoreCase);
        if (!IsAcceptedForMicro(model.HasIncumbentSolution, validation, readbackIdentityMatches))
            throw new InvalidDataException("Complete Macro snapshot failed identity-preserving read-back validation.");
        WriteJson(summaryPath, new
        {
            stage = "radius4_seed_to_complete_macro",
            seedIsFixed = false,
            seedObjective = rollingSeed.ObjectiveValue,
            relationCount,
            variableCount,
            binaryVariableCount,
            constraintCount,
            mipStart = new
            {
                mipStart.StartedDerivedSectionPrecedenceVariableCount,
                radiusSeedPrecedenceCount = rollingSeed.SectionPrecedenceByKey.Count,
                completePrecedenceCount = model.SectionPrecedenceVars.Count,
                coverage = mipStart.MacroCoverage
            },
            solverStatus = model.StatusName,
            solutionCount = model.Model.Get(GRB.IntAttr.SolCount),
            runtimeSeconds = model.Model.Get(GRB.DoubleAttr.Runtime),
            firstIncumbentSeconds = incumbentCallback.FirstIncumbentTimeSeconds,
            feasibilityFirst = true,
            buildSeconds = buildClock.Elapsed.TotalSeconds,
            bigM = SummarizeBigM(model.ConditionalBigMAuditRows),
            validatorViolationCount = validation.Violations.Count,
            validatorFeasible = validation.IsFeasible,
            readbackIdentityMatches
        });
        Console.WriteLine(
            $"Complete Macro accepted: fingerprint={fingerprint}, " +
            $"first_incumbent_seconds={incumbentCallback.FirstIncumbentTimeSeconds?.ToString("F3") ?? "NA"}, " +
            $"runtime_seconds={model.Model.Get(GRB.DoubleAttr.Runtime):F3}, violations=0, readback=True.");
        return new FreshCompleteMacroSolveResult(
            snapshotPath,
            fingerprint,
            relationCount,
            variableCount,
            binaryVariableCount,
            constraintCount,
            mipStart.StartedDerivedSectionPrecedenceVariableCount,
            model.Model.Get(GRB.DoubleAttr.Runtime),
            model.StatusName,
            incumbentCallback.FirstIncumbentTimeSeconds,
            true,
            mipStart.MacroCoverage,
            validation.Violations.Count,
            readbackIdentityMatches,
            buildClock.Elapsed.TotalSeconds);
    }

    internal static void ConfigureFeasibilityFirst(BasicMilpModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        model.Model.Set(GRB.IntAttr.NumObj, 0);
        model.Model.SetObjective(new GRBLinExpr(), GRB.MINIMIZE);
        model.Model.Set(GRB.IntParam.MIPFocus, 1);
        model.Model.Set(GRB.IntParam.SolutionLimit, 1);
        model.Model.Update();
    }

    internal static bool IsAcceptedForMicro(
        bool hasIncumbent,
        MasterValidationReport? validation,
        bool readbackIdentityMatches) =>
        hasIncumbent &&
        validation is { IsFeasible: true } &&
        validation.Violations.Count == 0 &&
        readbackIdentityMatches;

    private static object SummarizeBigM(IReadOnlyList<ConditionalBigMAuditRow> rows)
    {
        double[] values = rows.Select(row => row.Value).Order().ToArray();
        double? median = values.Length == 0
            ? null
            : values.Length % 2 == 1
                ? values[values.Length / 2]
                : (values[values.Length / 2 - 1] + values[values.Length / 2]) / 2.0;
        return new
        {
            count = values.Length,
            minimum = values.Length == 0 ? (double?)null : values[0],
            maximum = values.Length == 0 ? (double?)null : values[^1],
            median,
            legacyFallbackCount = rows.Count(row => row.UsedLegacyFallback)
        };
    }

    private static void WriteJson<T>(string path, T value) => File.WriteAllText(
        path,
        JsonSerializer.Serialize(value, JsonOptions),
        new UTF8Encoding(false));
}
