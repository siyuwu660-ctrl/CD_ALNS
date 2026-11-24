// 滚动时域求解器按列车难度和冲突热点构造重叠窗口，逐窗求解局部主模型并把 incumbent 快照传给下一窗口。
// 固定区、活动区和重叠区控制变量自由度；失败时可扩窗或恢复配额，最终再以全局模型收口并输出每窗进度。
using System.Globalization;
using System.Text;
using System.Text.Json;
using Gurobi;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

/// <summary>
/// 滚动时域主问题求解器。
/// 固定窗口模式对应 v1；自适应窗口模式对应 v2。
/// v2 的核心区别是：窗口大小、重叠强度和每窗时间不再写死，
/// 而是由候选冲突、选停复杂度、弱站约束和种子解越行热点共同决定。
/// </summary>
public sealed class RollingHorizonMasterSolver
{
    private readonly TimetablingInstance _instance;
    private readonly int? _neighborRadius;
    private readonly RollingHorizonOptions _options;
    private readonly string? _artifactsDirectory;

    public RollingHorizonMasterSolver(
        TimetablingInstance instance,
        int? neighborRadius,
        RollingHorizonOptions options,
        string? artifactsDirectory = null)
    {
        _instance = instance;
        _neighborRadius = neighborRadius;
        _options = options;
        _artifactsDirectory = artifactsDirectory;
    }

    public RollingHorizonResult Solve()
    {
        List<int> orderedTrainIndices = BuildReferenceTrainOrder();
        List<RollingWindowProgress> progress = new();
        List<QuotaRestorationStageDiagnostic> quotaDiagnostics = new();
        MasterSolutionSnapshot? currentSnapshot = null;
        List<RollingWindow> windows;
        IReadOnlyList<RollingTrainScore> trainScores;

        IReadOnlyList<IReadOnlySet<int>> admissionSets = IncrementalRollingWindowBuilder.BuildAdmissionSets(
            orderedTrainIndices,
            Math.Min(_options.InitialAdmissionBatchSize, orderedTrainIndices.Count),
            _options.AdmissionBatchSize);

        for (int admissionIndex = 0; admissionIndex < admissionSets.Count; admissionIndex++)
        {
            IReadOnlySet<int> admittedTrainIndices = admissionSets[admissionIndex];
            TimetablingInstance stageInstance = TimetablingInstanceProjector.Project(_instance, admittedTrainIndices);
            using BasicMilpModel stageModel = BuildModel(stageInstance);
            if (currentSnapshot is not null)
            {
                MasterSolutionSnapshotApplier.ApplyMipStart(stageModel, currentSnapshot);
            }

            double stageTimeLimit = admissionIndex == 0
                ? _options.SeedTimeLimitSeconds
                : _options.WindowTimeLimitSeconds;
            stageModel.Model.Set(GRB.DoubleParam.TimeLimit, stageTimeLimit);
            stageModel.Optimize();

            for (int retry = 0; !stageModel.HasIncumbentSolution && retry < _options.MaxAdmissionRetries; retry++)
            {
                stageTimeLimit = Math.Min(_options.MaxWindowTimeLimitSeconds, stageTimeLimit * 2.0);
                stageModel.Model.Set(GRB.DoubleParam.TimeLimit, stageTimeLimit);
                stageModel.Optimize();
            }

            if (!stageModel.HasIncumbentSolution)
            {
                int admittedBeforeFailure = currentSnapshot?.AdmittedTrainIndices.Count ?? 0;
                throw new InvalidOperationException(
                    $"Incremental rolling stopped with a partial result after admitting {admittedBeforeFailure} of {orderedTrainIndices.Count} trains; " +
                    $"stage {admissionIndex + 1} with {admittedTrainIndices.Count} trains found no incumbent.");
            }

            double objectiveBefore = currentSnapshot?.ObjectiveValue ?? stageModel.Model.Get(GRB.DoubleAttr.ObjVal);
            currentSnapshot = MasterSolutionSnapshot.Capture(stageModel);

            progress.Add(new RollingWindowProgress(
                admissionIndex,
                $"admission_{admissionIndex + 1}",
                admittedTrainIndices.Count,
                0.0,
                stageTimeLimit,
                objectiveBefore,
                currentSnapshot.ObjectiveValue,
                true,
                stageModel.StatusName,
                stageModel.Model.Get(GRB.DoubleAttr.Runtime)));

            if (admittedTrainIndices.Count == orderedTrainIndices.Count)
            {
                trainScores = RollingDifficultyScorer.Score(stageModel);
                windows = _options.UseAdaptiveWindows
                    ? AdaptiveRollingWindowBuilder.Build(orderedTrainIndices, trainScores, stageModel, _options)
                    : RollingWindowBuilder.BuildFixed(orderedTrainIndices, _options);
                goto BootstrapComplete;
            }
        }

        throw new InvalidOperationException("Incremental rolling did not create a complete admission stage.");

    BootstrapComplete:
        MasterSolutionSnapshot quotaFreeSnapshot = currentSnapshot!;
        IReadOnlyList<StationQuotaRestorationStage> quotaStages = StationQuotaRestorationStageBuilder.Build(
            _instance.StationStopQuotaRules,
            maxStationsPerStage: 1);
        IReadOnlySet<int> allTrainIndices = orderedTrainIndices.ToHashSet();
        foreach (StationQuotaRestorationStage quotaStage in quotaStages)
        {
            StationQuotaDeficitReport quotaDeficit = StationQuotaDeficitCalculator.Calculate(
                _instance,
                quotaFreeSnapshot,
                quotaStage.AddedRules);
            Console.WriteLine(
                $"Station-quota restoration stage {quotaStage.StageIndex}/{quotaStages.Count}: " +
                $"station={quotaDeficit.StationName}, previous_stage={quotaStage.StageIndex - 1}, " +
                $"previous_incumbent_objective={quotaFreeSnapshot.ObjectiveValue:F6}, " +
                $"quota_deficit_total={quotaDeficit.TotalDeficit}.");
            foreach (StationQuotaRuleDeficit ruleDeficit in quotaDeficit.Rules)
            {
                Console.WriteLine(
                    $"Station-quota deficit detail: station={quotaDeficit.StationName}, " +
                    $"train_type={ruleDeficit.TrainType}, window=[{ruleDeficit.StartTimeSeconds},{ruleDeficit.EndTimeSeconds}], " +
                    $"required_min={ruleDeficit.MinimumStops}, actual={ruleDeficit.ActualStops}, " +
                    $"deficit={ruleDeficit.Deficit}, allowed_max={ruleDeficit.MaximumStops}.");
            }
            TimetablingInstance restorationInstance = TimetablingInstanceProjector.Project(
                _instance,
                allTrainIndices,
                quotaStage.CumulativeRules);
            using BasicMilpModel quotaRestorationModel = BuildModel(restorationInstance);
            MasterSolutionSnapshotApplier.ApplyMipStart(quotaRestorationModel, quotaFreeSnapshot);
            int mipStartAssignedVariables =
                quotaFreeSnapshot.ArrivalSecondsByStopId.Keys.Count(quotaRestorationModel.ArrivalVars.ContainsKey) +
                quotaFreeSnapshot.DepartureSecondsByStopId.Keys.Count(quotaRestorationModel.DepartureVars.ContainsKey) +
                quotaFreeSnapshot.StopByStopId.Keys.Count(quotaRestorationModel.StopVars.ContainsKey) +
                quotaFreeSnapshot.SectionPrecedenceByKey.Keys.Count(quotaRestorationModel.SectionPrecedenceVars.ContainsKey) +
                quotaFreeSnapshot.StationEventPrecedenceByKey.Keys.Count(quotaRestorationModel.StationEventPrecedenceVars.ContainsKey) +
                (quotaFreeSnapshot.RunningTimeSupplementSecondsByKey?.Keys.Count(
                    quotaRestorationModel.RunningTimeSupplementVars.ContainsKey) ?? 0);
            Console.WriteLine(
                $"Station-quota MIP start applied: target_stage={quotaStage.StageIndex}, " +
                $"source_stage={quotaStage.StageIndex - 1}, assigned_variables={mipStartAssignedVariables}, " +
                $"model_variables={quotaRestorationModel.Model.Get(GRB.IntAttr.NumVars)}, " +
                $"source_objective={quotaFreeSnapshot.ObjectiveValue:F6}.");
            IReadOnlySet<int> releasedTrainIndices = StationQuotaRestorationNeighborhoodResolver.ResolveTrainIndices(
                _instance,
                quotaStage.AddedRules);
            StructuralRepairNeighborhoodApplier.FixOutside(
                quotaRestorationModel,
                quotaFreeSnapshot,
                releasedTrainIndices);
            quotaRestorationModel.Model.Update();
            HashSet<string> addedStationTypes = quotaStage.AddedRules
                .Select(rule => $"{rule.StationName}\u001f{rule.TrainType}")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            List<GRBVar> stageStopVars = quotaRestorationModel.Instance.TrainStops
                .Where(stop => addedStationTypes.Contains($"{stop.StationName}\u001f{stop.TrainType}"))
                .Select(stop => quotaRestorationModel.StopVars[stop.StopId])
                .ToList();
            int flexibleStopCandidateCount = stageStopVars.Count(variable =>
                variable.Get(GRB.DoubleAttr.LB) < 0.5 && variable.Get(GRB.DoubleAttr.UB) > 0.5);
            int stopDomainFeasibleCandidateCount = stageStopVars.Count(variable =>
                variable.Get(GRB.DoubleAttr.UB) > 0.5);
            StationQuotaRestorationBudget restorationBudget = StationQuotaRestorationBudgetPolicy.Resolve(
                quotaStage.StageIndex,
                _options.StationQuotaRestorationMaxTimeLimitSeconds,
                _options.StationQuotaRestorationOverrideStageIndex,
                _options.StationQuotaRestorationOverrideTimeLimitSeconds);
            double restorationTimeLimit = restorationBudget.TotalTimeLimitSeconds;
            Console.WriteLine(
                $"Station-quota restoration budget: stage={quotaStage.StageIndex}, " +
                $"time_limit_seconds={restorationTimeLimit:F0}, targeted_override={restorationBudget.IsOverride}, " +
                "solve_mode=FEASIBILITY_FIRST, solution_limit=1.");
            string? stageLogPath = ResolveSolverLogPath($"quota_restoration_stage_{quotaStage.StageIndex}.log");
            if (stageLogPath is not null)
            {
                File.WriteAllText(stageLogPath, string.Empty, new UTF8Encoding(false));
                quotaRestorationModel.Model.Set(GRB.StringParam.LogFile, stageLogPath);
            }
            FirstIncumbentTrackingCallback incumbentCallback = new();
            quotaRestorationModel.Model.SetCallback(incumbentCallback);
            quotaRestorationModel.Model.Set(GRB.DoubleParam.TimeLimit, restorationTimeLimit);
            quotaRestorationModel.Model.Set(GRB.IntParam.SolutionLimit, 1);
            quotaRestorationModel.Optimize();
            if (stageLogPath is not null)
            {
                quotaRestorationModel.Model.Set(GRB.StringParam.LogFile, string.Empty);
            }
            string solverLog = ReadSolverLog(stageLogPath);
            MacroSolveTermination termination = MacroSolveTerminationClassifier.Resolve(
                quotaRestorationModel.Model.Get(GRB.IntAttr.Status),
                quotaRestorationModel.HasIncumbentSolution,
                feasibilityFirst: true);
            GurobiLogSolveSummary logSummary = GurobiLogSolveSummaryParser.Parse(solverLog);
            QuotaRestorationStageDiagnostic stageDiagnostic = new(
                quotaStage.StageIndex,
                quotaDeficit.StationName,
                quotaStage.AddedRules.Count,
                quotaFreeSnapshot.ObjectiveValue,
                quotaDeficit.TotalDeficit,
                quotaDeficit.Rules.Count(item => item.Deficit > 0),
                quotaDeficit.Rules.Max(item => item.Deficit),
                flexibleStopCandidateCount,
                stopDomainFeasibleCandidateCount,
                quotaRestorationModel.Model.Get(GRB.IntAttr.NumVars),
                quotaRestorationModel.Model.Get(GRB.IntAttr.NumBinVars),
                quotaRestorationModel.Model.Get(GRB.IntAttr.NumVars) -
                    quotaRestorationModel.Model.Get(GRB.IntAttr.NumIntVars),
                quotaRestorationModel.Model.Get(GRB.IntAttr.NumConstrs),
                mipStartAssignedVariables,
                quotaRestorationModel.Model.Get(GRB.IntAttr.NumVars),
                MipStartFeedbackClassifier.Resolve(solverLog),
                incumbentCallback.FirstIncumbentTimeSeconds,
                quotaRestorationModel.Model.Get(GRB.IntAttr.SolCount),
                quotaRestorationModel.Model.Get(GRB.DoubleAttr.Runtime),
                quotaRestorationModel.StatusName,
                quotaRestorationModel.HasIncumbentSolution
                    ? quotaRestorationModel.Model.Get(GRB.DoubleAttr.ObjVal)
                    : null,
                logSummary.BestBound,
                logSummary.MipGap,
                termination.ToString());
            quotaDiagnostics.Add(stageDiagnostic);
            WriteQuotaDiagnosticsIncrementally(quotaDiagnostics);
            Console.WriteLine(
                $"Station-quota restoration result: stage={quotaStage.StageIndex}, " +
                $"termination={termination}, first_incumbent_seconds=" +
                $"{FormatOptional(stageDiagnostic.FirstIncumbentTimeSeconds)}, " +
                $"runtime_seconds={stageDiagnostic.RuntimeSeconds:F3}, solutions={stageDiagnostic.SolutionCount}.");

            if (!quotaRestorationModel.HasIncumbentSolution)
            {
                throw new InvalidOperationException(
                    $"Incremental rolling admitted all {orderedTrainIndices.Count} trains, but station-quota restoration stage " +
                    $"{quotaStage.StageIndex}/{quotaStages.Count} found no incumbent after adding " +
                    $"{string.Join(",", quotaStage.AddedRules.Select(rule => rule.StationName).Distinct(StringComparer.OrdinalIgnoreCase))}.");
            }

            double objectiveBeforeRestoration = quotaFreeSnapshot.ObjectiveValue;
            currentSnapshot = MasterSolutionSnapshot.Capture(quotaRestorationModel);
            quotaFreeSnapshot = currentSnapshot;
            progress.Add(new RollingWindowProgress(
                admissionSets.Count + quotaStage.StageIndex - 1,
                $"restore_station_quotas_{quotaStage.StageIndex}",
                orderedTrainIndices.Count,
                0.0,
                restorationTimeLimit,
                objectiveBeforeRestoration,
                currentSnapshot.ObjectiveValue,
                true,
                termination.ToString(),
                quotaRestorationModel.Model.Get(GRB.DoubleAttr.Runtime)));
        }

        MasterSolutionSnapshot completeSnapshot = currentSnapshot!;

        int windowIndex = 1;
        foreach (RollingWindow window in windows)
        {
            using BasicMilpModel windowModel = BuildModel();
            ApplyMipStart(windowModel, completeSnapshot);
            FixOutsideWindowDiscreteDecisions(windowModel, completeSnapshot, window);
            windowModel.Model.Set(GRB.DoubleParam.TimeLimit, window.TimeLimitSeconds);
            windowModel.Optimize();

            bool accepted = false;
            double incumbentBefore = completeSnapshot.ObjectiveValue;
            double incumbentAfter = incumbentBefore;

            if (windowModel.HasIncumbentSolution)
            {
                MasterSolutionSnapshot candidateSnapshot = MasterSolutionSnapshot.Capture(windowModel);
                if (candidateSnapshot.ObjectiveValue <= completeSnapshot.ObjectiveValue + 1e-6)
                {
                    completeSnapshot = candidateSnapshot;
                    accepted = true;
                }

                incumbentAfter = completeSnapshot.ObjectiveValue;
            }

            progress.Add(new RollingWindowProgress(
                windowIndex,
                $"window_{windowIndex}_{window.SourceName}",
                window.ActiveTrainIndices.Count,
                window.DifficultyScore,
                window.TimeLimitSeconds,
                incumbentBefore,
                incumbentAfter,
                accepted,
                windowModel.StatusName,
                windowModel.Model.Get(GRB.DoubleAttr.Runtime)));

            windowIndex++;
        }

        BasicMilpModel finalModel = BuildModel();
        ApplyMipStart(finalModel, completeSnapshot);
        string? finalLogPath = ResolveSolverLogPath("final_macro_solve.log");
        if (finalLogPath is not null)
        {
            File.WriteAllText(finalLogPath, string.Empty, new UTF8Encoding(false));
            finalModel.Model.Set(GRB.StringParam.LogFile, finalLogPath);
        }
        FirstIncumbentTrackingCallback finalIncumbentCallback = new();
        finalModel.Model.SetCallback(finalIncumbentCallback);
        finalModel.Model.Set(GRB.DoubleParam.TimeLimit, _options.FinalMacroTimeLimitSeconds);
        Console.WriteLine(
            $"Final full Macro refinement: time_limit_seconds={_options.FinalMacroTimeLimitSeconds:F0}, " +
            "decisions_fixed=False, acceptance=INCUMBENT_AND_ZERO_VALIDATOR_VIOLATIONS.");
        finalModel.Optimize();
        if (finalLogPath is not null)
        {
            finalModel.Model.Set(GRB.StringParam.LogFile, string.Empty);
        }
        string finalSolverLog = ReadSolverLog(finalLogPath);
        GurobiLogSolveSummary finalLogSummary = GurobiLogSolveSummaryParser.Parse(finalSolverLog);
        MacroSolveTermination finalTermination = MacroSolveTerminationClassifier.Resolve(
            finalModel.Model.Get(GRB.IntAttr.Status),
            finalModel.HasIncumbentSolution,
            feasibilityFirst: false);
        MasterValidationReport finalValidation = new MasterSolutionValidator().Validate(finalModel);
        bool baselineAccepted = MacroBaselineAcceptancePolicy.IsAccepted(
            completeSnapshot.AdmittedTrainIndices.Count,
            orderedTrainIndices.Count,
            quotaDiagnostics.Count,
            quotaStages.Count,
            finalModel.HasIncumbentSolution,
            finalValidation.Violations.Count);
        List<double> finalSupplements = finalModel.HasIncumbentSolution
            ? finalModel.RunningTimeSupplementVars.Values
                .Select(variable => variable.Get(GRB.DoubleAttr.X))
                .ToList()
            : [];
        MacroBaselineSummary baselineSummary = new(
            baselineAccepted,
            completeSnapshot.AdmittedTrainIndices.Count,
            orderedTrainIndices.Count,
            quotaDiagnostics.Count,
            quotaStages.Count,
            finalTermination.ToString(),
            finalModel.HasIncumbentSolution
                ? finalModel.Model.Get(GRB.DoubleAttr.ObjVal)
                : null,
            finalLogSummary.BestBound,
            finalLogSummary.MipGap,
            finalModel.Model.Get(GRB.DoubleAttr.Runtime),
            finalModel.Model.Get(GRB.DoubleAttr.NodeCount),
            finalModel.Model.Get(GRB.IntAttr.SolCount),
            finalIncumbentCallback.FirstIncumbentTimeSeconds,
            finalValidation.Violations.Count,
            finalSupplements.Sum(),
            baselineAccepted ? "MACRO_BASELINE_SUCCESS" : "MACRO_BASELINE_FAILED");
        WriteFinalSummaryIncrementally(baselineSummary);
        Console.WriteLine(
            $"Final Macro result: termination={finalTermination}, runtime_seconds={baselineSummary.RuntimeSeconds:F3}, " +
            $"solutions={baselineSummary.SolutionCount}, validator_violations={baselineSummary.ValidatorViolationCount}, " +
            $"running_supplement_total={baselineSummary.RunningSupplementTotalSeconds:F6}, " +
            $"baseline={baselineSummary.TerminationReason}.");

        if (!finalModel.HasIncumbentSolution)
        {
            finalModel.Dispose();
            throw new InvalidOperationException(
                $"Final full Macro solve terminated as {finalTermination} without an incumbent after " +
                $"{_options.FinalMacroTimeLimitSeconds:F0} seconds.");
        }

        return new RollingHorizonResult(
            finalModel,
            progress,
            trainScores,
            windows,
            quotaDiagnostics,
            baselineSummary,
            finalValidation);
    }

    public static void WriteProgressReport(RollingHorizonResult result, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        WriteProgressCsv(result.Progress, Path.Combine(outputDirectory, "rolling_progress.csv"));
        WriteProgressMarkdown(result.Progress, Path.Combine(outputDirectory, "rolling_progress.md"));
        WriteTrainScores(result.TrainScores, Path.Combine(outputDirectory, "rolling_train_scores.csv"));
        WriteWindowComposition(result.Windows, result.TrainScores, Path.Combine(outputDirectory, "rolling_window_composition.csv"));
        QuotaRestorationDiagnosticsReporter.Write(
            result.QuotaRestorationDiagnostics ?? Array.Empty<QuotaRestorationStageDiagnostic>(),
            outputDirectory);
        if (result.BaselineSummary is not null)
        {
            MacroBaselineSummaryReporter.Write(result.BaselineSummary, outputDirectory);
        }
    }

    private string? ResolveSolverLogPath(string fileName)
    {
        if (string.IsNullOrWhiteSpace(_artifactsDirectory))
        {
            return null;
        }

        Directory.CreateDirectory(_artifactsDirectory);
        return Path.Combine(_artifactsDirectory, fileName);
    }

    private static string ReadSolverLog(string? path) =>
        path is not null && File.Exists(path) ? File.ReadAllText(path) : string.Empty;

    private void WriteQuotaDiagnosticsIncrementally(IReadOnlyList<QuotaRestorationStageDiagnostic> diagnostics)
    {
        if (!string.IsNullOrWhiteSpace(_artifactsDirectory))
        {
            QuotaRestorationDiagnosticsReporter.Write(diagnostics, _artifactsDirectory);
        }
    }

    private void WriteFinalSummaryIncrementally(MacroBaselineSummary summary)
    {
        if (!string.IsNullOrWhiteSpace(_artifactsDirectory))
        {
            MacroBaselineSummaryReporter.Write(summary, _artifactsDirectory);
        }
    }

    private static string FormatOptional(double? value) =>
        value.HasValue ? value.Value.ToString("F3", CultureInfo.InvariantCulture) : "NA";

    private BasicMilpModel BuildModel()
    {
        BasicMilpModelBuilder builder = new(
            _instance,
            _neighborRadius,
            runningTimeSupplementRatio: _options.RunningTimeSupplementRatio);
        return builder.Build();
    }

    private BasicMilpModel BuildModel(TimetablingInstance instance)
    {
        BasicMilpModelBuilder builder = new(
            instance,
            _neighborRadius,
            runningTimeSupplementRatio: _options.RunningTimeSupplementRatio);
        return builder.Build();
    }

    private static void ApplyMipStart(BasicMilpModel model, MasterSolutionSnapshot snapshot)
    {
        MasterSolutionSnapshotApplier.ApplyMipStart(model, snapshot);
    }

    private static void FixOutsideWindowDiscreteDecisions(BasicMilpModel model, MasterSolutionSnapshot snapshot, RollingWindow window)
    {
        foreach (TrainStopRecord stop in model.Instance.TrainStops)
        {
            if (!window.ActiveTrainIndices.Contains(stop.TrainIndex))
            {
                FixStopDecision(model, snapshot, stop);
            }
        }

        foreach (SectionConflictCandidate candidate in model.SectionConflictCandidates)
        {
            string key = BasicMilpModelBuilder.BuildSectionPrecedenceKey(candidate);
            if (window.ReleasedPrecedenceKeys.Contains(key))
            {
                continue;
            }

            if (!window.ActiveTrainIndices.Contains(candidate.FirstTrainIndex) &&
                !window.ActiveTrainIndices.Contains(candidate.SecondTrainIndex))
            {
                FixPrecedenceVariable(model, snapshot, candidate);
            }
        }
        foreach ((string key, GRBVar variable) in model.RunningTimeSupplementVars)
        {
            int separator = key.IndexOf("::", StringComparison.Ordinal);
            if (separator > 0 && int.TryParse(key[..separator], out int trainIndex) &&
                !window.ActiveTrainIndices.Contains(trainIndex))
            {
                double value = snapshot.RunningTimeSupplementSecondsByKey?.GetValueOrDefault(key, 0.0) ?? 0.0;
                FixVariable(variable, value);
            }
        }
    }

    private static void FixAllVariables(BasicMilpModel model, MasterSolutionSnapshot snapshot)
    {
        foreach (TrainStopRecord stop in model.Instance.TrainStops)
        {
            FixStopVariables(model, snapshot, stop);
        }

        foreach (SectionConflictCandidate candidate in model.SectionConflictCandidates)
        {
            FixPrecedenceVariable(model, snapshot, candidate);
        }
        foreach ((string key, int value) in snapshot.StationEventPrecedenceByKey)
        {
            if (model.StationEventPrecedenceVars.TryGetValue(key, out GRBVar? variable))
            {
                FixVariable(variable, value);
            }
        }
        foreach ((string key, GRBVar variable) in model.RunningTimeSupplementVars)
        {
            double value = snapshot.RunningTimeSupplementSecondsByKey?.GetValueOrDefault(key, 0.0) ?? 0.0;
            FixVariable(variable, value);
        }
    }

    private static void FixStopVariables(BasicMilpModel model, MasterSolutionSnapshot snapshot, TrainStopRecord stop)
    {
        if (snapshot.ArrivalSecondsByStopId.TryGetValue(stop.StopId, out double arrival) &&
            model.ArrivalVars.TryGetValue(stop.StopId, out GRBVar? arrivalVar))
        {
            FixVariable(arrivalVar, arrival);
        }

        if (snapshot.DepartureSecondsByStopId.TryGetValue(stop.StopId, out double departure) &&
            model.DepartureVars.TryGetValue(stop.StopId, out GRBVar? departureVar))
        {
            FixVariable(departureVar, departure);
        }

        FixStopDecision(model, snapshot, stop);
    }

    private static void FixStopDecision(BasicMilpModel model, MasterSolutionSnapshot snapshot, TrainStopRecord stop)
    {
        if (snapshot.StopByStopId.TryGetValue(stop.StopId, out int isStop) &&
            model.StopVars.TryGetValue(stop.StopId, out GRBVar? stopVar))
        {
            FixVariable(stopVar, isStop);
        }
    }

    private static void FixPrecedenceVariable(BasicMilpModel model, MasterSolutionSnapshot snapshot, SectionConflictCandidate candidate)
    {
        string key = BasicMilpModelBuilder.BuildSectionPrecedenceKey(candidate);
        if (snapshot.SectionPrecedenceByKey.TryGetValue(key, out int precedence) &&
            model.SectionPrecedenceVars.TryGetValue(key, out GRBVar? variable))
        {
            FixVariable(variable, precedence);
        }
    }

    private static void FixVariable(GRBVar variable, double value)
    {
        variable.Set(GRB.DoubleAttr.LB, value);
        variable.Set(GRB.DoubleAttr.UB, value);
    }

    private static void FixPrecedenceDecisions(BasicMilpModel model, MasterSolutionSnapshot snapshot)
    {
        foreach ((string key, int value) in snapshot.SectionPrecedenceByKey)
        {
            if (model.SectionPrecedenceVars.TryGetValue(key, out GRBVar? variable))
            {
                FixVariable(variable, value);
            }
        }

        foreach ((string key, int value) in snapshot.StationEventPrecedenceByKey)
        {
            if (model.StationEventPrecedenceVars.TryGetValue(key, out GRBVar? variable))
            {
                FixVariable(variable, value);
            }
        }
    }

    private List<int> BuildReferenceTrainOrder()
    {
        return _instance.Trains
            .OrderBy(train => train.OrderedStops[0].RawDepTimeSeconds ?? train.OrderedStops[0].EarliestDepSeconds)
            .ThenBy(train => train.TrainIndex)
            .Select(train => train.TrainIndex)
            .ToList();
    }

    private static void WriteProgressCsv(IReadOnlyList<RollingWindowProgress> progress, string path)
    {
        using StreamWriter writer = new(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        writer.WriteLine("window_index,window_name,active_train_count,difficulty_score,time_limit_seconds,objective_before,objective_after,accepted,status,runtime_seconds");

        foreach (RollingWindowProgress item in progress)
        {
            writer.WriteLine(string.Join(
                ',',
                item.WindowIndex.ToString(CultureInfo.InvariantCulture),
                item.WindowName,
                item.ActiveTrainCount.ToString(CultureInfo.InvariantCulture),
                item.DifficultyScore.ToString("F6", CultureInfo.InvariantCulture),
                item.TimeLimitSeconds.ToString("F6", CultureInfo.InvariantCulture),
                item.ObjectiveBefore.ToString("F6", CultureInfo.InvariantCulture),
                item.ObjectiveAfter.ToString("F6", CultureInfo.InvariantCulture),
                item.Accepted ? "True" : "False",
                item.Status,
                item.RuntimeSeconds.ToString("F6", CultureInfo.InvariantCulture)));
        }
    }

    private static void WriteProgressMarkdown(IReadOnlyList<RollingWindowProgress> progress, string path)
    {
        StringBuilder builder = new();
        builder.AppendLine("# Rolling Horizon Master Progress");
        builder.AppendLine();
        builder.AppendLine("## Overall");
        builder.AppendLine();

        RollingWindowProgress? first = progress.FirstOrDefault();
        RollingWindowProgress? last = progress.LastOrDefault();
        if (first is not null && last is not null)
        {
            builder.AppendLine($"- initial_objective: `{first.ObjectiveAfter:F3}`");
            builder.AppendLine($"- final_objective: `{last.ObjectiveAfter:F3}`");
            builder.AppendLine($"- accepted_windows: `{progress.Count(item => item.Accepted)}`");
            builder.AppendLine($"- total_windows_including_seed: `{progress.Count}`");
            builder.AppendLine($"- total_solver_runtime_seconds: `{progress.Sum(item => item.RuntimeSeconds):F3}`");
        }

        builder.AppendLine();
        builder.AppendLine("## Window Details");
        builder.AppendLine();
        builder.AppendLine("| index | name | active_trains | difficulty | time_limit | before | after | accepted | status | runtime |");
        builder.AppendLine("|---:|---|---:|---:|---:|---:|---:|---|---|---:|");

        foreach (RollingWindowProgress item in progress)
        {
            builder.AppendLine($"| {item.WindowIndex} | {item.WindowName} | {item.ActiveTrainCount} | {item.DifficultyScore:F3} | {item.TimeLimitSeconds:F3} | {item.ObjectiveBefore:F3} | {item.ObjectiveAfter:F3} | {item.Accepted} | {item.Status} | {item.RuntimeSeconds:F3} |");
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static void WriteTrainScores(IReadOnlyList<RollingTrainScore> trainScores, string path)
    {
        using StreamWriter writer = new(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        writer.WriteLine("train_index,total_score,conflict_degree,optional_stop_count,weak_station_count,overtaking_participation_count");

        foreach (RollingTrainScore score in trainScores.OrderByDescending(score => score.TotalScore).ThenBy(score => score.TrainIndex))
        {
            writer.WriteLine(string.Join(
                ',',
                score.TrainIndex.ToString(CultureInfo.InvariantCulture),
                score.TotalScore.ToString("F6", CultureInfo.InvariantCulture),
                score.ConflictDegree.ToString(CultureInfo.InvariantCulture),
                score.OptionalStopCount.ToString(CultureInfo.InvariantCulture),
                score.WeakStationCount.ToString(CultureInfo.InvariantCulture),
                score.OvertakingParticipationCount.ToString(CultureInfo.InvariantCulture)));
        }
    }

    private static void WriteWindowComposition(
        IReadOnlyList<RollingWindow> windows,
        IReadOnlyList<RollingTrainScore> trainScores,
        string path)
    {
        Dictionary<int, RollingTrainScore> scoreByTrain = trainScores.ToDictionary(score => score.TrainIndex);
        using StreamWriter writer = new(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        writer.WriteLine("window_index,source_name,active_train_count,difficulty_score,time_limit_seconds,released_precedence_count,train_indices,avg_train_score,max_train_score,total_conflict_degree,total_optional_stop_count,total_weak_station_count,total_overtaking_participation_count");

        for (int index = 0; index < windows.Count; index++)
        {
            RollingWindow window = windows[index];
            List<RollingTrainScore> scores = window.ActiveTrainIndices
                .Where(scoreByTrain.ContainsKey)
                .Select(trainIndex => scoreByTrain[trainIndex])
                .ToList();

            writer.WriteLine(string.Join(
                ',',
                (index + 1).ToString(CultureInfo.InvariantCulture),
                Escape(window.SourceName),
                window.ActiveTrainIndices.Count.ToString(CultureInfo.InvariantCulture),
                window.DifficultyScore.ToString("F6", CultureInfo.InvariantCulture),
                window.TimeLimitSeconds.ToString("F6", CultureInfo.InvariantCulture),
                window.ReleasedPrecedenceKeys.Count.ToString(CultureInfo.InvariantCulture),
                Escape(string.Join('|', window.ActiveTrainIndices.OrderBy(trainIndex => trainIndex))),
                scores.Select(score => score.TotalScore).DefaultIfEmpty(0.0).Average().ToString("F6", CultureInfo.InvariantCulture),
                scores.Select(score => score.TotalScore).DefaultIfEmpty(0.0).Max().ToString("F6", CultureInfo.InvariantCulture),
                scores.Sum(score => score.ConflictDegree).ToString(CultureInfo.InvariantCulture),
                scores.Sum(score => score.OptionalStopCount).ToString(CultureInfo.InvariantCulture),
                scores.Sum(score => score.WeakStationCount).ToString(CultureInfo.InvariantCulture),
                scores.Sum(score => score.OvertakingParticipationCount).ToString(CultureInfo.InvariantCulture)));
        }
    }

    private static string Escape(string value)
    {
        if (!value.Contains(',') && !value.Contains('"') && !value.Contains('\n') && !value.Contains('\r'))
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }
}

public static class RollingDifficultyScorer
{
    public static IReadOnlyList<RollingTrainScore> Score(BasicMilpModel model)
    {
        Dictionary<int, int> conflictDegree = new();
        Dictionary<int, int> optionalStopCount = model.Instance.TrainStops
            .Where(stop => stop.StopRequirement == StopRequirement.OptionalStop)
            .GroupBy(stop => stop.TrainIndex)
            .ToDictionary(group => group.Key, group => group.Count());
        Dictionary<int, int> weakStationCount = CountWeakStationTraversals(model.Instance);
        Dictionary<int, int> overtakingCount = CountOvertakingParticipation(model);

        foreach (SectionConflictCandidate candidate in model.SectionConflictCandidates)
        {
            Increment(conflictDegree, candidate.FirstTrainIndex);
            Increment(conflictDegree, candidate.SecondTrainIndex);
        }

        return model.Instance.Trains
            .Select(train =>
            {
                int trainIndex = train.TrainIndex;
                int conflict = Get(conflictDegree, trainIndex);
                int optional = Get(optionalStopCount, trainIndex);
                int weak = Get(weakStationCount, trainIndex);
                int overtaking = Get(overtakingCount, trainIndex);
                double total = 1.0 + 0.08 * conflict + 0.8 * optional + 1.2 * weak + 2.0 * overtaking;

                return new RollingTrainScore(trainIndex, conflict, optional, weak, overtaking, total);
            })
            .ToList();
    }

    /// <summary>Count intermediate visits that lack legal stopping routes and tracks for waiting.</summary>
    /// <param name="instance">Original stop requirements and route-track restrictions.</param>
    /// <returns>Counts by train used by the rolling search order.</returns>
    private static Dictionary<int, int> CountWeakStationTraversals(TimetablingInstance instance)
    {
        Dictionary<int, int> result = new();
        var decisions = StopModePreprocessor.Analyze(instance).Decisions;
        foreach (var decision in decisions)
        {
            if (instance.StopsById[decision.StopId].StopRole == TrainStopRole.Intermediate &&
                (decision.StopRequirement == StopRequirement.MustPass || !decision.Analysis.Stopping.IsFeasible))
                Increment(result, decision.TrainIndex);
        }
        return result;
    }

    /// <summary>Count modeled order reversals by train for rolling search guidance.</summary>
    /// <param name="model">Solved model with borrowed section order variables.</param>
    /// <returns>Number of overtaking participations keyed by train index.</returns>
    private static Dictionary<int, int> CountOvertakingParticipation(BasicMilpModel model)
    {
        Dictionary<string, SectionConflictCandidate> candidateByKey = model.SectionConflictCandidates.ToDictionary(
            BasicMilpModelBuilder.BuildSectionPrecedenceKey,
            StringComparer.OrdinalIgnoreCase);
        Dictionary<int, int> result = new();

        foreach ((string stationName, string incomingSectionName, string outgoingSectionName) in BuildStationAdjacentSectionPairs(model.Instance))
        {
            foreach (SectionConflictCandidate incomingCandidate in model.SectionConflictCandidates.Where(candidate => string.Equals(candidate.SectionName, incomingSectionName, StringComparison.OrdinalIgnoreCase)))
            {
                string outgoingKey = $"{outgoingSectionName}::{incomingCandidate.FirstTrainIndex}::{incomingCandidate.SecondTrainIndex}";
                if (!candidateByKey.TryGetValue(outgoingKey, out SectionConflictCandidate? outgoingCandidate))
                {
                    continue;
                }

                int incomingValue = ReadPrecedence(model, incomingCandidate);
                int outgoingValue = ReadPrecedence(model, outgoingCandidate);
                if (incomingValue != outgoingValue)
                {
                    Increment(result, incomingCandidate.FirstTrainIndex);
                    Increment(result, incomingCandidate.SecondTrainIndex);
                }
            }
        }

        return result;
    }

    private static List<(string StationName, string IncomingSectionName, string OutgoingSectionName)> BuildStationAdjacentSectionPairs(TimetablingInstance instance)
    {
        Dictionary<string, (string IncomingSectionName, string OutgoingSectionName)> byStation = new(StringComparer.OrdinalIgnoreCase);

        foreach (TrainRecord train in instance.Trains)
        {
            for (int index = 1; index < train.OrderedStops.Count - 1; index++)
            {
                TrainStopRecord currentStop = train.OrderedStops[index];
                TrainStopRecord nextStop = train.OrderedStops[index + 1];
                if (!string.IsNullOrWhiteSpace(currentStop.IncomingSectionName) &&
                    !string.IsNullOrWhiteSpace(nextStop.IncomingSectionName))
                {
                    byStation.TryAdd(currentStop.StationName, (currentStop.IncomingSectionName, nextStop.IncomingSectionName));
                }
            }
        }

        return byStation.Select(item => (item.Key, item.Value.IncomingSectionName, item.Value.OutgoingSectionName)).ToList();
    }

    private static int ReadPrecedence(BasicMilpModel model, SectionConflictCandidate candidate)
    {
        string key = BasicMilpModelBuilder.BuildSectionPrecedenceKey(candidate);
        return model.SectionPrecedenceVars[key].Get(GRB.DoubleAttr.X) >= 0.5 ? 1 : 0;
    }

    private static int Get(IReadOnlyDictionary<int, int> values, int key)
    {
        return values.TryGetValue(key, out int value) ? value : 0;
    }

    private static void Increment(IDictionary<int, int> values, int key)
    {
        values[key] = values.TryGetValue(key, out int current) ? current + 1 : 1;
    }
}

public static class RollingWindowBuilder
{
    public static List<RollingWindow> BuildFixed(IReadOnlyList<int> orderedTrainIndices, RollingHorizonOptions options)
    {
        int safeWindowSize = Math.Max(1, options.WindowSize);
        int safeOverlap = Math.Max(0, Math.Min(options.WindowOverlap, safeWindowSize - 1));
        int step = safeWindowSize - safeOverlap;
        List<RollingWindow> windows = new();

        for (int start = 0; start < orderedTrainIndices.Count; start += step)
        {
            HashSet<int> activeTrainIndices = orderedTrainIndices.Skip(start).Take(safeWindowSize).ToHashSet();
            if (activeTrainIndices.Count == 0)
            {
                continue;
            }

            windows.Add(new RollingWindow(
                activeTrainIndices,
                activeTrainIndices.Count,
                options.WindowTimeLimitSeconds,
                safeWindowSize,
                safeOverlap,
                "sequence",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
            if (start + safeWindowSize >= orderedTrainIndices.Count)
            {
                break;
            }
        }

        return windows;
    }
}

public static class AdaptiveRollingWindowBuilder
{
    public static List<RollingWindow> Build(
        IReadOnlyList<int> orderedTrainIndices,
        IReadOnlyList<RollingTrainScore> trainScores,
        BasicMilpModel seedModel,
        RollingHorizonOptions options)
    {
        Dictionary<int, RollingTrainScore> scoreByTrain = trainScores.ToDictionary(score => score.TrainIndex);
        double averageScore = trainScores.Select(score => score.TotalScore).DefaultIfEmpty(1.0).Average();
        double scoreBudget = options.AdaptiveWindowScoreBudget > 0
            ? options.AdaptiveWindowScoreBudget
            : Math.Max(averageScore * Math.Max(options.MinWindowSize, 1), averageScore * options.WindowSize);

        List<HashSet<int>> rawWindows = new();
        int start = 0;
        while (start < orderedTrainIndices.Count)
        {
            double accumulatedScore = 0.0;
            int end = start;
            while (end < orderedTrainIndices.Count)
            {
                int trainIndex = orderedTrainIndices[end];
                accumulatedScore += scoreByTrain[trainIndex].TotalScore;
                end++;

                int count = end - start;
                if (count >= options.MinWindowSize &&
                    (accumulatedScore >= scoreBudget || count >= options.MaxWindowSize))
                {
                    break;
                }
            }

            if (end <= start)
            {
                end = Math.Min(start + options.MinWindowSize, orderedTrainIndices.Count);
            }

            int windowCount = end - start;
            double boundaryRisk = ComputeBoundaryRisk(orderedTrainIndices, scoreByTrain, end, options.MinWindowOverlap);
            int overlap = Math.Clamp(
                options.MinWindowOverlap + (int)Math.Ceiling(boundaryRisk / Math.Max(averageScore, 1.0)),
                options.MinWindowOverlap,
                Math.Min(options.MaxWindowOverlap, Math.Max(0, windowCount - 1)));

            HashSet<int> activeTrainIndices = orderedTrainIndices.Skip(start).Take(windowCount).ToHashSet();
            rawWindows.Add(activeTrainIndices);

            if (end >= orderedTrainIndices.Count)
            {
                break;
            }

            start = Math.Max(start + 1, end - overlap);
        }

        double minDifficulty = rawWindows.Select(window => ComputeWindowDifficulty(window, scoreByTrain)).DefaultIfEmpty(1.0).Min();
        double maxDifficulty = rawWindows.Select(window => ComputeWindowDifficulty(window, scoreByTrain)).DefaultIfEmpty(1.0).Max();

        List<RollingWindow> windows = rawWindows.Select(window =>
            {
                double difficulty = ComputeWindowDifficulty(window, scoreByTrain);
                double timeLimit = AllocateTimeLimit(difficulty, minDifficulty, maxDifficulty, options);
                return new RollingWindow(
                    window,
                    difficulty,
                    timeLimit,
                    window.Count,
                    0,
                    "sequence",
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            })
            .ToList();

        if (options.UseHotspotWindows)
        {
            windows.AddRange(BuildHotspotWindows(orderedTrainIndices, scoreByTrain, seedModel, options));
        }

        return windows;
    }

    private static List<RollingWindow> BuildHotspotWindows(
        IReadOnlyList<int> orderedTrainIndices,
        IReadOnlyDictionary<int, RollingTrainScore> scoreByTrain,
        BasicMilpModel seedModel,
        RollingHorizonOptions options)
    {
        Dictionary<int, int> rankByTrain = orderedTrainIndices
            .Select((trainIndex, rank) => (trainIndex, rank))
            .ToDictionary(item => item.trainIndex, item => item.rank);

        return BuildHotspotCandidates(seedModel)
            .OrderByDescending(candidate => candidate.OvertakingCount)
            .ThenBy(candidate => candidate.StationName, StringComparer.OrdinalIgnoreCase)
            .Take(options.MaxHotspotWindowCount)
            .Select(candidate =>
            {
                HashSet<int> activeTrains = ExpandHotspotTrainSet(candidate.TrainIndices, orderedTrainIndices, rankByTrain, scoreByTrain, options);
                double difficulty = activeTrains.Sum(trainIndex => scoreByTrain.TryGetValue(trainIndex, out RollingTrainScore? score) ? score.TotalScore : 1.0);

                return new RollingWindow(
                    activeTrains,
                    difficulty,
                    options.MaxWindowTimeLimitSeconds,
                    activeTrains.Count,
                    options.HotspotNeighborPadding,
                    $"hotspot_{candidate.StationName}",
                    candidate.ReleasedPrecedenceKeys);
            })
            .Where(window => window.ActiveTrainIndices.Count > 0)
            .ToList();
    }

    /// <summary>Find rolling search hotspots from actual modeled reversals in the seed.</summary>
    /// <param name="seedModel">Solved seed model with borrowed section order variables.</param>
    /// <returns>Stations and train pairs eligible for local order release.</returns>
    private static IReadOnlyList<RollingHotspotCandidate> BuildHotspotCandidates(BasicMilpModel seedModel)
    {
        Dictionary<string, SectionConflictCandidate> candidateByKey = seedModel.SectionConflictCandidates.ToDictionary(
            BasicMilpModelBuilder.BuildSectionPrecedenceKey,
            StringComparer.OrdinalIgnoreCase);
        List<RollingHotspotCandidateBuilder> builders = new();

        foreach ((string stationName, string incomingSectionName, string outgoingSectionName) in BuildStationAdjacentSectionPairs(seedModel.Instance))
        {
            RollingHotspotCandidateBuilder builder = new(stationName);
            foreach (SectionConflictCandidate incomingCandidate in seedModel.SectionConflictCandidates.Where(candidate => string.Equals(candidate.SectionName, incomingSectionName, StringComparison.OrdinalIgnoreCase)))
            {
                string outgoingKey = $"{outgoingSectionName}::{incomingCandidate.FirstTrainIndex}::{incomingCandidate.SecondTrainIndex}";
                if (!candidateByKey.TryGetValue(outgoingKey, out SectionConflictCandidate? outgoingCandidate))
                {
                    continue;
                }

                int incomingValue = ReadPrecedence(seedModel, incomingCandidate);
                int outgoingValue = ReadPrecedence(seedModel, outgoingCandidate);
                if (incomingValue == outgoingValue)
                {
                    continue;
                }

                builder.AddOvertakingPair(incomingCandidate, outgoingCandidate);
            }

            if (builder.OvertakingCount > 0)
            {
                builders.Add(builder);
            }
        }

        return builders.Select(builder => builder.Build()).ToList();
    }

    private static HashSet<int> ExpandHotspotTrainSet(
        IReadOnlySet<int> coreTrainIndices,
        IReadOnlyList<int> orderedTrainIndices,
        IReadOnlyDictionary<int, int> rankByTrain,
        IReadOnlyDictionary<int, RollingTrainScore> scoreByTrain,
        RollingHorizonOptions options)
    {
        HashSet<int> expanded = new(coreTrainIndices);
        foreach (int trainIndex in coreTrainIndices)
        {
            if (!rankByTrain.TryGetValue(trainIndex, out int rank))
            {
                continue;
            }

            int start = Math.Max(0, rank - options.HotspotNeighborPadding);
            int end = Math.Min(orderedTrainIndices.Count - 1, rank + options.HotspotNeighborPadding);
            for (int index = start; index <= end; index++)
            {
                expanded.Add(orderedTrainIndices[index]);
            }
        }

        if (expanded.Count <= options.MaxWindowSize)
        {
            return expanded;
        }

        return expanded
            .OrderByDescending(trainIndex => coreTrainIndices.Contains(trainIndex))
            .ThenByDescending(trainIndex => scoreByTrain.TryGetValue(trainIndex, out RollingTrainScore? score) ? score.TotalScore : 0.0)
            .ThenBy(trainIndex => trainIndex)
            .Take(options.MaxWindowSize)
            .ToHashSet();
    }

    private static List<(string StationName, string IncomingSectionName, string OutgoingSectionName)> BuildStationAdjacentSectionPairs(TimetablingInstance instance)
    {
        Dictionary<string, (string IncomingSectionName, string OutgoingSectionName)> byStation = new(StringComparer.OrdinalIgnoreCase);

        foreach (TrainRecord train in instance.Trains)
        {
            for (int index = 1; index < train.OrderedStops.Count - 1; index++)
            {
                TrainStopRecord currentStop = train.OrderedStops[index];
                TrainStopRecord nextStop = train.OrderedStops[index + 1];
                if (!string.IsNullOrWhiteSpace(currentStop.IncomingSectionName) &&
                    !string.IsNullOrWhiteSpace(nextStop.IncomingSectionName))
                {
                    byStation.TryAdd(currentStop.StationName, (currentStop.IncomingSectionName, nextStop.IncomingSectionName));
                }
            }
        }

        return byStation.Select(item => (item.Key, item.Value.IncomingSectionName, item.Value.OutgoingSectionName)).ToList();
    }

    private static int ReadPrecedence(BasicMilpModel model, SectionConflictCandidate candidate)
    {
        return model.SectionPrecedenceVars[BasicMilpModelBuilder.BuildSectionPrecedenceKey(candidate)].Get(GRB.DoubleAttr.X) >= 0.5 ? 1 : 0;
    }

    private static double ComputeBoundaryRisk(
        IReadOnlyList<int> orderedTrainIndices,
        IReadOnlyDictionary<int, RollingTrainScore> scoreByTrain,
        int boundaryIndex,
        int radius)
    {
        int start = Math.Max(0, boundaryIndex - radius);
        int end = Math.Min(orderedTrainIndices.Count, boundaryIndex + radius);
        double risk = 0.0;
        for (int index = start; index < end; index++)
        {
            risk += scoreByTrain[orderedTrainIndices[index]].TotalScore;
        }

        return risk;
    }

    private static double ComputeWindowDifficulty(HashSet<int> window, IReadOnlyDictionary<int, RollingTrainScore> scoreByTrain)
    {
        return window.Sum(trainIndex => scoreByTrain[trainIndex].TotalScore);
    }

    private static double AllocateTimeLimit(double difficulty, double minDifficulty, double maxDifficulty, RollingHorizonOptions options)
    {
        if (Math.Abs(maxDifficulty - minDifficulty) < 1e-9)
        {
            return options.WindowTimeLimitSeconds;
        }

        double ratio = (difficulty - minDifficulty) / (maxDifficulty - minDifficulty);
        return options.MinWindowTimeLimitSeconds + ratio * (options.MaxWindowTimeLimitSeconds - options.MinWindowTimeLimitSeconds);
    }
}

public sealed class RollingHotspotCandidateBuilder
{
    private readonly HashSet<int> _trainIndices = new();
    private readonly HashSet<string> _releasedPrecedenceKeys = new(StringComparer.OrdinalIgnoreCase);

    public RollingHotspotCandidateBuilder(string stationName)
    {
        StationName = stationName;
    }

    public string StationName { get; }
    public int OvertakingCount { get; private set; }

    public void AddOvertakingPair(SectionConflictCandidate incomingCandidate, SectionConflictCandidate outgoingCandidate)
    {
        OvertakingCount++;
        _trainIndices.Add(incomingCandidate.FirstTrainIndex);
        _trainIndices.Add(incomingCandidate.SecondTrainIndex);
        _releasedPrecedenceKeys.Add(BasicMilpModelBuilder.BuildSectionPrecedenceKey(incomingCandidate));
        _releasedPrecedenceKeys.Add(BasicMilpModelBuilder.BuildSectionPrecedenceKey(outgoingCandidate));
    }

    public RollingHotspotCandidate Build()
    {
        return new RollingHotspotCandidate(
            StationName,
            OvertakingCount,
            new HashSet<int>(_trainIndices),
            new HashSet<string>(_releasedPrecedenceKeys, StringComparer.OrdinalIgnoreCase));
    }
}

public sealed record RollingHotspotCandidate(
    string StationName,
    int OvertakingCount,
    IReadOnlySet<int> TrainIndices,
    IReadOnlySet<string> ReleasedPrecedenceKeys);

public sealed class FirstIncumbentTrackingCallback : GRBCallback
{
    private readonly bool _stopAfterFirstIncumbent;

    public FirstIncumbentTrackingCallback(bool stopAfterFirstIncumbent = false)
    {
        _stopAfterFirstIncumbent = stopAfterFirstIncumbent;
    }

    public double? FirstIncumbentTimeSeconds { get; private set; }
    public bool EarlyStopTriggered { get; private set; }

    protected override void Callback()
    {
        if (where == GRB.Callback.MIPSOL && !FirstIncumbentTimeSeconds.HasValue)
        {
            FirstIncumbentTimeSeconds = GetDoubleInfo(GRB.Callback.RUNTIME);
            if (_stopAfterFirstIncumbent)
            {
                EarlyStopTriggered = true;
                Abort();
            }
        }
    }
}

public sealed record QuotaRestorationStageDiagnostic(
    int StageIndex,
    string StationName,
    int QuotaRuleCount,
    double PreviousIncumbentObjective,
    int TotalQuotaDeficit,
    int DeficitWindowCount,
    int MaximumSingleWindowDeficit,
    int FlexibleStopCandidateCount,
    int StopDomainFeasibleCandidateCount,
    int NumVars,
    int NumBinaryVars,
    int NumContinuousVars,
    int NumConstrs,
    int MipStartVariableCount,
    int TotalModelVariableCount,
    MipStartFeedback MipStartResult,
    double? FirstIncumbentTimeSeconds,
    int SolutionCount,
    double RuntimeSeconds,
    string Status,
    double? Objective,
    double? BestBound,
    double? MipGap,
    string TerminationReason);

public static class QuotaRestorationDiagnosticsReporter
{
    public static void Write(IReadOnlyList<QuotaRestorationStageDiagnostic> diagnostics, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        WriteCsv(diagnostics, Path.Combine(outputDirectory, "quota_restoration_diagnostics.csv"));
        WriteMarkdown(diagnostics, Path.Combine(outputDirectory, "quota_restoration_diagnostics.md"));
    }

    private static void WriteCsv(IReadOnlyList<QuotaRestorationStageDiagnostic> diagnostics, string path)
    {
        using StreamWriter writer = new(path, append: false, new UTF8Encoding(true));
        writer.WriteLine(
            "stage_index,station_name,quota_rule_count,previous_incumbent_objective,total_quota_deficit," +
            "deficit_window_count,maximum_single_window_deficit,flexible_stop_candidate_count," +
            "stop_domain_feasible_candidate_count,num_vars,num_binary_vars,num_continuous_vars,num_constrs," +
            "mip_start_variable_count,total_model_variable_count,mip_start_result,first_incumbent_time_seconds," +
            "solution_count,runtime_seconds,status,objective,best_bound,mip_gap,termination_reason");
        foreach (QuotaRestorationStageDiagnostic item in diagnostics)
        {
            writer.WriteLine(string.Join(',',
                item.StageIndex.ToString(CultureInfo.InvariantCulture),
                EscapeCsv(item.StationName),
                item.QuotaRuleCount.ToString(CultureInfo.InvariantCulture),
                item.PreviousIncumbentObjective.ToString("F6", CultureInfo.InvariantCulture),
                item.TotalQuotaDeficit.ToString(CultureInfo.InvariantCulture),
                item.DeficitWindowCount.ToString(CultureInfo.InvariantCulture),
                item.MaximumSingleWindowDeficit.ToString(CultureInfo.InvariantCulture),
                item.FlexibleStopCandidateCount.ToString(CultureInfo.InvariantCulture),
                item.StopDomainFeasibleCandidateCount.ToString(CultureInfo.InvariantCulture),
                item.NumVars.ToString(CultureInfo.InvariantCulture),
                item.NumBinaryVars.ToString(CultureInfo.InvariantCulture),
                item.NumContinuousVars.ToString(CultureInfo.InvariantCulture),
                item.NumConstrs.ToString(CultureInfo.InvariantCulture),
                item.MipStartVariableCount.ToString(CultureInfo.InvariantCulture),
                item.TotalModelVariableCount.ToString(CultureInfo.InvariantCulture),
                item.MipStartResult.ToString(),
                FormatOptional(item.FirstIncumbentTimeSeconds),
                item.SolutionCount.ToString(CultureInfo.InvariantCulture),
                item.RuntimeSeconds.ToString("F6", CultureInfo.InvariantCulture),
                EscapeCsv(item.Status),
                FormatOptional(item.Objective),
                FormatOptional(item.BestBound),
                FormatOptional(item.MipGap),
                EscapeCsv(item.TerminationReason)));
        }
    }

    private static void WriteMarkdown(IReadOnlyList<QuotaRestorationStageDiagnostic> diagnostics, string path)
    {
        StringBuilder builder = new();
        builder.AppendLine("# Station-Quota Restoration Diagnostics");
        builder.AppendLine();
        builder.AppendLine("| stage | station | deficit | windows | max deficit | flexible stops | domain-feasible stops | MIP start | first incumbent (s) | runtime (s) | solutions | termination |");
        builder.AppendLine("|---:|---|---:|---:|---:|---:|---:|---|---:|---:|---:|---|");
        foreach (QuotaRestorationStageDiagnostic item in diagnostics)
        {
            builder.AppendLine(
                $"| {item.StageIndex} | {item.StationName} | {item.TotalQuotaDeficit} | {item.DeficitWindowCount} | " +
                $"{item.MaximumSingleWindowDeficit} | {item.FlexibleStopCandidateCount} | " +
                $"{item.StopDomainFeasibleCandidateCount} | {item.MipStartResult} | " +
                $"{FormatOptional(item.FirstIncumbentTimeSeconds)} | {item.RuntimeSeconds:F3} | " +
                $"{item.SolutionCount} | {item.TerminationReason} |");
        }
        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(true));
    }

    private static string FormatOptional(double? value) =>
        value.HasValue ? value.Value.ToString("F6", CultureInfo.InvariantCulture) : string.Empty;

    private static string EscapeCsv(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
}

public sealed record MacroBaselineSummary(
    bool IsAccepted,
    int AdmittedTrainCount,
    int TotalTrainCount,
    int CompletedQuotaStageCount,
    int TotalQuotaStageCount,
    string Status,
    double? Objective,
    double? BestBound,
    double? MipGap,
    double RuntimeSeconds,
    double NodeCount,
    int SolutionCount,
    double? FirstIncumbentTimeSeconds,
    int ValidatorViolationCount,
    double RunningSupplementTotalSeconds,
    string TerminationReason);

public static class MacroBaselineSummaryReporter
{
    public static void Write(MacroBaselineSummary summary, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        string json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(
            Path.Combine(outputDirectory, "final_macro_summary.json"),
            json,
            new UTF8Encoding(true));

        StringBuilder markdown = new();
        markdown.AppendLine("# Final Macro Bootstrap Summary");
        markdown.AppendLine();
        markdown.AppendLine($"- accepted: `{summary.IsAccepted}`");
        markdown.AppendLine($"- admitted_trains: `{summary.AdmittedTrainCount}/{summary.TotalTrainCount}`");
        markdown.AppendLine($"- completed_quota_stages: `{summary.CompletedQuotaStageCount}/{summary.TotalQuotaStageCount}`");
        markdown.AppendLine($"- status: `{summary.Status}`");
        markdown.AppendLine($"- objective: `{FormatOptional(summary.Objective)}`");
        markdown.AppendLine($"- best_bound: `{FormatOptional(summary.BestBound)}`");
        markdown.AppendLine($"- mip_gap: `{FormatOptional(summary.MipGap)}`");
        markdown.AppendLine($"- runtime_seconds: `{summary.RuntimeSeconds:F6}`");
        markdown.AppendLine($"- node_count: `{summary.NodeCount:F0}`");
        markdown.AppendLine($"- solution_count: `{summary.SolutionCount}`");
        markdown.AppendLine($"- first_incumbent_time_seconds: `{FormatOptional(summary.FirstIncumbentTimeSeconds)}`");
        markdown.AppendLine($"- validator_violation_count: `{summary.ValidatorViolationCount}`");
        markdown.AppendLine($"- running_supplement_total_seconds: `{summary.RunningSupplementTotalSeconds:F6}`");
        markdown.AppendLine($"- termination_reason: `{summary.TerminationReason}`");
        File.WriteAllText(
            Path.Combine(outputDirectory, "final_macro_summary.md"),
            markdown.ToString(),
            new UTF8Encoding(true));
    }

    private static string FormatOptional(double? value) =>
        value.HasValue ? value.Value.ToString("F6", CultureInfo.InvariantCulture) : "NA";
}

public sealed record RollingHorizonOptions(
    int WindowSize,
    int WindowOverlap,
    double SeedTimeLimitSeconds,
    double WindowTimeLimitSeconds,
    bool UseAdaptiveWindows = false,
    double AdaptiveWindowScoreBudget = 0.0,
    int MinWindowSize = 8,
    int MaxWindowSize = 45,
    int MinWindowOverlap = 2,
    int MaxWindowOverlap = 12,
    double MinWindowTimeLimitSeconds = 5.0,
    double MaxWindowTimeLimitSeconds = 30.0,
    bool UseHotspotWindows = true,
    int MaxHotspotWindowCount = 3,
    int HotspotNeighborPadding = 2,
    int InitialAdmissionBatchSize = 8,
    int AdmissionBatchSize = 8,
    int MaxAdmissionRetries = 2,
    double RunningTimeSupplementRatio = 0.05,
    int? StationQuotaRestorationOverrideStageIndex = null,
    double? StationQuotaRestorationOverrideTimeLimitSeconds = null,
    double StationQuotaRestorationMaxTimeLimitSeconds = 600.0,
    double FinalMacroTimeLimitSeconds = 600.0);

public sealed record RollingTrainScore(
    int TrainIndex,
    int ConflictDegree,
    int OptionalStopCount,
    int WeakStationCount,
    int OvertakingParticipationCount,
    double TotalScore);

public sealed record RollingWindow(
    IReadOnlySet<int> ActiveTrainIndices,
    double DifficultyScore,
    double TimeLimitSeconds,
    int TargetWindowSize,
    int WindowOverlap,
    string SourceName,
    IReadOnlySet<string> ReleasedPrecedenceKeys);

public sealed record RollingWindowProgress(
    int WindowIndex,
    string WindowName,
    int ActiveTrainCount,
    double DifficultyScore,
    double TimeLimitSeconds,
    double ObjectiveBefore,
    double ObjectiveAfter,
    bool Accepted,
    string Status,
    double RuntimeSeconds);

public sealed record RollingHorizonResult(
    BasicMilpModel FinalModel,
    IReadOnlyList<RollingWindowProgress> Progress,
    IReadOnlyList<RollingTrainScore> TrainScores,
    IReadOnlyList<RollingWindow> Windows,
    IReadOnlyList<QuotaRestorationStageDiagnostic>? QuotaRestorationDiagnostics = null,
    MacroBaselineSummary? BaselineSummary = null,
    MasterValidationReport? FinalValidation = null) : IDisposable
{
    public void Dispose()
    {
        FinalModel.Dispose();
    }
}
