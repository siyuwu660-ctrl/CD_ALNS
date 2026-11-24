using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Gurobi;

namespace GurobiTimetablingResearch.Optimization;

/// <summary>
/// 表示在预留最终验证时间后，一次增强 Master 实际可以使用的求解预算。
/// </summary>
public sealed record ValidityFirstRuntimeBudget(
    bool CanStart,
    double RequestedSolveSeconds,
    double EffectiveSolveSeconds,
    double RemainingGlobalSeconds,
    double ReservedValidationSeconds);

/// <summary>
/// 用总运行时钟裁剪每次 Master 预算，避免一次求解占用最终 Validator 和 Global Micro 的预留时间。
/// </summary>
public static class ValidityFirstRuntimeBudgetPolicy
{
    public static ValidityFirstRuntimeBudget Resolve(
        double requestedSolveSeconds,
        double totalBudgetSeconds,
        double elapsedSeconds,
        double finalValidationReserveSeconds)
    {
        ValidateFinitePositive(requestedSolveSeconds, nameof(requestedSolveSeconds));
        ValidateFinitePositive(totalBudgetSeconds, nameof(totalBudgetSeconds));
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        }
        if (!double.IsFinite(finalValidationReserveSeconds) || finalValidationReserveSeconds < 0.0 ||
            finalValidationReserveSeconds >= totalBudgetSeconds)
        {
            throw new ArgumentOutOfRangeException(nameof(finalValidationReserveSeconds));
        }

        double remaining = Math.Max(0.0, totalBudgetSeconds - elapsedSeconds);
        double availableForSolve = Math.Max(0.0, remaining - finalValidationReserveSeconds);
        double effective = Math.Min(requestedSolveSeconds, availableForSolve);
        return new ValidityFirstRuntimeBudget(
            effective > 1e-9,
            requestedSolveSeconds,
            effective,
            remaining,
            Math.Min(finalValidationReserveSeconds, remaining));
    }

    private static void ValidateFinitePositive(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value <= 0.0)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}

/// <summary>
/// 将 IIS 限定在求解器已经证明增强 Master 不可行的情形；无 incumbent 的超时仍保持 Unknown。
/// </summary>
public static class ValidityFirstIisPolicy
{
    public static bool ShouldCompute(int statusCode, int solutionCount) =>
        statusCode == GRB.Status.INFEASIBLE && solutionCount == 0;
}

public sealed record ValidityFirstIisRuntimeBudget(
    bool CanStart,
    double RequestedSeconds,
    double EffectiveSeconds,
    double CumulativeUsedSeconds,
    double CumulativeBudgetSeconds,
    double RemainingGlobalSeconds);

/// <summary>
/// 将 IIS 限制在独立的单次和累计预算内，并继续占用同一份全局运行预算。
/// </summary>
public static class ValidityFirstIisRuntimeBudgetPolicy
{
    public static ValidityFirstIisRuntimeBudget Resolve(
        double requestedSeconds,
        double cumulativeBudgetSeconds,
        double cumulativeUsedSeconds,
        double totalBudgetSeconds,
        double elapsedSeconds,
        double finalValidationReserveSeconds)
    {
        if (!double.IsFinite(requestedSeconds) || requestedSeconds <= 0.0 ||
            !double.IsFinite(cumulativeBudgetSeconds) || cumulativeBudgetSeconds < 0.0 ||
            !double.IsFinite(cumulativeUsedSeconds) || cumulativeUsedSeconds < 0.0 ||
            !double.IsFinite(totalBudgetSeconds) || totalBudgetSeconds <= 0.0 ||
            !double.IsFinite(elapsedSeconds) || elapsedSeconds < 0.0 ||
            !double.IsFinite(finalValidationReserveSeconds) || finalValidationReserveSeconds < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(requestedSeconds));
        }

        double remainingGlobal = Math.Max(0.0, totalBudgetSeconds - elapsedSeconds);
        double availableGlobal = Math.Max(0.0, remainingGlobal - finalValidationReserveSeconds);
        double availableCumulative = Math.Max(0.0, cumulativeBudgetSeconds - cumulativeUsedSeconds);
        double effective = Math.Min(requestedSeconds, Math.Min(availableGlobal, availableCumulative));
        return new ValidityFirstIisRuntimeBudget(
            effective > 1e-9,
            requestedSeconds,
            effective,
            cumulativeUsedSeconds,
            cumulativeBudgetSeconds,
            remainingGlobal);
    }
}

public sealed class ValidityFirstIisBudgetTracker
{
    public double UsedSeconds { get; private set; }

    public void Record(double runtimeSeconds)
    {
        if (!double.IsFinite(runtimeSeconds) || runtimeSeconds < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(runtimeSeconds));
        }
        UsedSeconds += runtimeSeconds;
    }
}

public sealed record ValidityFirstIisExecutionContext(
    double PerCallBudgetSeconds,
    double CumulativeBudgetSeconds,
    double TotalRuntimeBudgetSeconds,
    double FinalValidationReserveSeconds,
    Func<double> ElapsedSecondsProvider,
    ValidityFirstIisBudgetTracker BudgetTracker);

public sealed record ValidityFirstIisDiagnostic(
    bool Attempted,
    bool Completed,
    bool TimedOut,
    bool? IsMinimal,
    bool HasReliableDiagnosticMembers,
    double EffectiveTimeLimitSeconds,
    double RuntimeSeconds,
    double CumulativeRuntimeSeconds,
    int ConstraintMemberCount,
    int BoundMemberCount,
    string Status,
    string? LogPath);

/// <summary>
/// IIS 不完整时仍允许后续有界邻域修复；它只降低释放依据的证明强度，不阻塞流程。
/// </summary>
public static class ValidityFirstIisContinuationPolicy
{
    public static bool CanContinue(ValidityFirstIisDiagnostic? diagnostic) =>
        diagnostic is null || !diagnostic.Attempted || diagnostic.Completed || diagnostic.TimedOut;
}

/// <summary>
/// 保存原生 Gurobi 日志中不能从最终模型属性直接恢复的 presolve、root 和数值告警信息。
/// </summary>
public sealed record GurobiStructuralLogSummary(
    int? OriginalRows,
    int? OriginalColumns,
    long? OriginalNonzeros,
    int? ContinuousVariables,
    int? IntegerVariables,
    int? BinaryVariables,
    int? PresolvedRows,
    int? PresolvedColumns,
    long? PresolvedNonzeros,
    double? PresolveSeconds,
    double? RootRelaxationSeconds,
    double? PrimaryObjectiveSeconds,
    double? SecondaryObjectiveSeconds,
    bool PrimaryObjectiveStarted,
    bool SecondaryObjectiveStarted,
    string ModelFingerprint,
    IReadOnlyList<string> NumericalWarnings);

/// <summary>
/// 从完整 Gurobi 文本日志提取结构化字段，同时保留原日志作为最终审计依据。
/// </summary>
public static class GurobiStructuralLogSummaryParser
{
    private static readonly Regex OriginalModelRegex = new(
        @"Optimize a model with\s+(\d+)\s+rows,\s+(\d+)\s+columns and\s+(\d+)\s+nonzeros",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex VariableTypesRegex = new(
        @"Variable types:\s+(\d+)\s+continuous,\s+(\d+)\s+integer\s+\((\d+)\s+binary\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex PresolvedRegex = new(
        @"Presolved:\s+(\d+)\s+rows,\s+(\d+)\s+columns,\s+(\d+)\s+nonzeros",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex PresolveTimeRegex = new(
        @"Presolve time:\s+([0-9.eE+\-]+)s",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex RootRelaxationRegex = new(
        @"Root relaxation:.*?,\s*([0-9.eE+\-]+)\s+seconds",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex FingerprintRegex = new(
        @"Model fingerprint:\s*(\S+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ObjectiveOneSectionRegex = new(
        @"Multi-objectives:\s*optimize objective 1.*?(?=Multi-objectives:\s*(?:optimize objective 2|stopped|solved))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
    private static readonly Regex ObjectiveTwoSectionRegex = new(
        @"Multi-objectives:\s*optimize objective 2.*?(?=Multi-objectives:\s*(?:stopped|solved))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
    private static readonly Regex ExploredRuntimeRegex = new(
        @"Explored .*? in\s+([0-9.eE+\-]+)\s+seconds",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static GurobiStructuralLogSummary Parse(string? solverLog)
    {
        string text = solverLog ?? string.Empty;
        Match original = OriginalModelRegex.Match(text);
        Match variableTypes = VariableTypesRegex.Match(text);
        Match presolved = PresolvedRegex.Match(text);
        Match presolveTime = PresolveTimeRegex.Match(text);
        Match rootRelaxation = RootRelaxationRegex.Match(text);
        Match fingerprint = FingerprintRegex.Match(text);
        double? primaryObjectiveSeconds = ParseLastExploredSeconds(
            ObjectiveOneSectionRegex.Match(text));
        double? secondaryCumulativeSeconds = ParseLastExploredSeconds(
            ObjectiveTwoSectionRegex.Match(text));
        double? secondaryObjectiveSeconds = primaryObjectiveSeconds.HasValue &&
                                            secondaryCumulativeSeconds.HasValue
            ? Math.Max(0.0, secondaryCumulativeSeconds.Value - primaryObjectiveSeconds.Value)
            : null;
        IReadOnlyList<string> warnings = text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.Contains("warning", StringComparison.OrdinalIgnoreCase) ||
                           line.Contains("numerical trouble", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return new GurobiStructuralLogSummary(
            ParseInt(original, 1),
            ParseInt(original, 2),
            ParseLong(original, 3),
            ParseInt(variableTypes, 1),
            ParseInt(variableTypes, 2),
            ParseInt(variableTypes, 3),
            ParseInt(presolved, 1),
            ParseInt(presolved, 2),
            ParseLong(presolved, 3),
            ParseDouble(presolveTime, 1),
            ParseDouble(rootRelaxation, 1),
            primaryObjectiveSeconds,
            secondaryObjectiveSeconds,
            text.Contains("optimize objective 1", StringComparison.OrdinalIgnoreCase),
            text.Contains("optimize objective 2", StringComparison.OrdinalIgnoreCase),
            fingerprint.Success ? fingerprint.Groups[1].Value : string.Empty,
            warnings);
    }

    private static double? ParseLastExploredSeconds(Match section)
    {
        if (!section.Success)
        {
            return null;
        }

        MatchCollection matches = ExploredRuntimeRegex.Matches(section.Value);
        return matches.Count == 0 ? null : ParseDouble(matches[^1], 1);
    }

    private static int? ParseInt(Match match, int group) =>
        match.Success && int.TryParse(match.Groups[group].Value, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int value) ? value : null;

    private static long? ParseLong(Match match, int group) =>
        match.Success && long.TryParse(match.Groups[group].Value, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out long value) ? value : null;

    private static double? ParseDouble(Match match, int group) =>
        match.Success && double.TryParse(match.Groups[group].Value, NumberStyles.Float,
            CultureInfo.InvariantCulture, out double value) ? value : null;
}

public sealed record ValidityFirstEnhancedMasterSolveDiagnostic(
    int Sequence,
    int IterationIndex,
    string Phase,
    double RequestedTimeLimitSeconds,
    double EffectiveTimeLimitSeconds,
    double RemainingGlobalBudgetSecondsBeforeSolve,
    double FinalValidationReserveSeconds,
    double ActualTimeLimitSeconds,
    int ActualMipFocus,
    double ActualMipGap,
    int ActualSolutionLimit,
    int ActualThreads,
    int ActualSeed,
    int OriginalRows,
    int OriginalVariables,
    int OriginalContinuousVariables,
    int OriginalIntegerVariables,
    int OriginalBinaryVariables,
    int? PresolvedRows,
    int? PresolvedColumns,
    long? PresolvedNonzeros,
    double? PresolveSeconds,
    double? RootRelaxationSeconds,
    double? PrimaryObjectiveSeconds,
    double? SecondaryObjectiveSeconds,
    double NodeCount,
    int SolutionCount,
    string Status,
    double RuntimeSeconds,
    double? FirstIncumbentTimeSeconds,
    double? FirstEligibleCandidateTimeSeconds,
    MacroCandidateCheckStrategy CandidateStrategy,
    bool EarlyStopRequested,
    bool EarlyStopTriggered,
    string ApplicationStopReason,
    bool CandidateCanProceedToFullCheck,
    bool PrimaryObjectiveStarted,
    bool SecondaryObjectiveStarted,
    MacroCandidateEligibilityAudit CandidateEligibility,
    string? CandidateSnapshotPath,
    MipStartFeedback MipStartResult,
    IReadOnlyList<string> NumericalWarnings,
    string GurobiModelFingerprint,
    string ConfigurationFingerprint,
    string LogPath,
    string? ModelPath,
    string? IisPath,
    ValidityFirstIisDiagnostic? IisDiagnostic = null);

/// <summary>
/// 统一执行 validity-first 的增强 Master，确保每次求解使用相同的预算裁剪、参数审计和产物保存规则。
/// </summary>
public static class ValidityFirstEnhancedMasterSolveRunner
{
    public static ValidityFirstEnhancedMasterSolveDiagnostic Optimize(
        BasicMilpModel model,
        string outputDirectory,
        int sequence,
        int iterationIndex,
        string phase,
        ValidityFirstRuntimeBudget budget,
        string configurationFingerprint,
        ValidityFirstSolveParameterPlan solveParameters,
        ValidityFirstIisExecutionContext? iisContext = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);
        if (!budget.CanStart)
        {
            throw new InvalidOperationException("The enhanced Master has no non-reserved global runtime budget.");
        }

        string diagnosticsDirectory = Path.Combine(outputDirectory, "enhanced_master");
        Directory.CreateDirectory(diagnosticsDirectory);
        string safePhase = string.Concat(phase.Select(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '_'));
        string prefix = $"{sequence:D3}_iteration_{iterationIndex:D3}_{safePhase}";
        string logPath = Path.Combine(diagnosticsDirectory, prefix + ".gurobi.log");
        string diagnosticPath = Path.Combine(diagnosticsDirectory, prefix + ".diagnostic.json");
        string parameterPath = Path.Combine(diagnosticsDirectory, prefix + ".parameters.json");
        string candidateSnapshotPath = Path.Combine(diagnosticsDirectory, prefix + ".candidate_snapshot.json");
        string exceptionPath = Path.Combine(diagnosticsDirectory, prefix + ".exception.json");
        string identityFailurePath = Path.Combine(diagnosticsDirectory, prefix + ".identity_failure.txt");
        string preOptimizeModelPath = Path.Combine(diagnosticsDirectory, prefix + ".preopt.lp");
        string variableMappingPath = Path.Combine(diagnosticsDirectory, prefix + ".variables.csv");
        string constraintMappingPath = Path.Combine(diagnosticsDirectory, prefix + ".constraints.csv");
        string restrictionsPath = Path.Combine(diagnosticsDirectory, prefix + ".temporary_restrictions.json");
        bool compactDiagnostics = string.Equals(
            Environment.GetEnvironmentVariable("TT_COMPACT_VALIDITY_DIAGNOSTICS"),
            "1",
            StringComparison.Ordinal);
        File.WriteAllText(logPath, string.Empty, new UTF8Encoding(false));

        model.IdentityAudit = GurobiModelIdentityAuditor.ValidateAndCapture(
            model.Model,
            $"{phase}::before_optimize",
            identityFailurePath);
        if (!compactDiagnostics)
        {
            GurobiModelIdentityAuditor.WriteMapping(
                model.IdentityAudit,
                variableMappingPath,
                constraintMappingPath);
            model.Model.Write(preOptimizeModelPath);
        }

        model.Model.Set(GRB.StringParam.LogFile, logPath);
        model.Model.Set(GRB.DoubleParam.TimeLimit, budget.EffectiveSolveSeconds);
        model.Model.Set(GRB.IntParam.MIPFocus, solveParameters.MipFocus);
        model.Model.Set(GRB.DoubleParam.MIPGap, solveParameters.MipGap);
        model.Model.Set(
            GRB.IntParam.SolutionLimit,
            solveParameters.SolutionLimit ?? 2_000_000_000);
        FirstIncumbentTrackingCallback incumbentCallback = new(
            solveParameters.StopAfterFirstModeledFeasibleCandidate);
        model.Model.SetCallback(incumbentCallback);

        double actualTimeLimit = model.Model.Get(GRB.DoubleParam.TimeLimit);
        int actualMipFocus = model.Model.Get(GRB.IntParam.MIPFocus);
        double actualMipGap = model.Model.Get(GRB.DoubleParam.MIPGap);
        int actualSolutionLimit = model.Model.Get(GRB.IntParam.SolutionLimit);
        int actualThreads = model.Model.Get(GRB.IntParam.Threads);
        int actualSeed = model.Model.Get(GRB.IntParam.Seed);
        Console.WriteLine(
            $"Validity-first enhanced Master parameters: phase={phase}, iteration={iterationIndex}, " +
            $"TimeLimit={actualTimeLimit:R}, MIPFocus={actualMipFocus}, MIPGap={actualMipGap:R}, " +
            $"SolutionLimit={actualSolutionLimit}, Threads={actualThreads}, Seed={actualSeed}, " +
            $"remaining_global={budget.RemainingGlobalSeconds:R}, reserve={budget.ReservedValidationSeconds:R}.");

        JsonSerializerOptions jsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        File.WriteAllText(restrictionsPath, JsonSerializer.Serialize(new
        {
            temporaryMicroFixations = model.TemporaryFixationAudit,
            endpointTrustRegion = model.AppliedPrecedenceTrustRegion,
            retainedMicroCertificateIds = model.RetainedMicroCertificateIds,
            microCertificateConstraintIds = model.MicroCertificateConstraintIds,
            promotedComponents = model.PromotedMicroBuildDiagnostics,
            promotedChoiceBusinessMappings = model.PromotedMicroChoiceBindings.Select(binding => new
            {
                binding.StationName,
                binding.RequirementId,
                binding.TrainIndex,
                binding.IsStop,
                binding.TrackName,
                binding.IncomingRouteId,
                binding.OutgoingRouteId,
                VariableName = binding.Variable.Get(GRB.StringAttr.VarName)
            }).ToList(),
            promotedOrderBusinessMappings = model.PromotedMicroOrderBindings.Select(binding => new
            {
                binding.StationName,
                binding.FirstRequirementId,
                binding.SecondRequirementId,
                kind = binding.Kind.ToString(),
                firstEvent = binding.FirstEventSide.ToString(),
                secondEvent = binding.SecondEventSide.ToString(),
                binding.FirstTimeOffsetSeconds,
                binding.SecondTimeOffsetSeconds,
                VariableName = binding.Variable.Get(GRB.StringAttr.VarName)
            }).ToList()
        }, jsonOptions), new UTF8Encoding(false));
        File.WriteAllText(parameterPath, JsonSerializer.Serialize(new
        {
            timeLimit = actualTimeLimit,
            mipFocus = actualMipFocus,
            mipGap = actualMipGap,
            solutionLimit = actualSolutionLimit,
            threads = actualThreads,
            seed = actualSeed,
            objectiveKind = solveParameters.ObjectiveKind.ToString(),
            candidateStrategy = solveParameters.CandidateStrategy.ToString(),
            stopAfterFirstModeledFeasibleCandidate =
                solveParameters.StopAfterFirstModeledFeasibleCandidate,
            macroObjectives = MacroObjectiveCatalog.Definitions,
            iisPerCallTimeLimit = iisContext?.PerCallBudgetSeconds,
            iisCumulativeBudget = iisContext?.CumulativeBudgetSeconds,
            compactDiagnostics,
            configurationFingerprint
        }, jsonOptions), new UTF8Encoding(false));

        try
        {
            model.Optimize();
        }
        catch (Exception exception)
        {
            model.Model.Set(GRB.StringParam.LogFile, string.Empty);
            File.WriteAllText(exceptionPath, JsonSerializer.Serialize(new
            {
                applicationStopReason = "solver_or_callback_exception",
                exceptionType = exception.GetType().FullName,
                exception.Message,
                innerExceptionType = exception.InnerException?.GetType().FullName,
                innerExceptionMessage = exception.InnerException?.Message,
                phase,
                iterationIndex,
                sequence
            }, jsonOptions), new UTF8Encoding(false));
            throw;
        }
        double solveRuntimeSeconds = model.Model.Get(GRB.DoubleAttr.Runtime);
        model.Model.Set(GRB.StringParam.LogFile, string.Empty);

        string solverLog = File.Exists(logPath) ? File.ReadAllText(logPath) : string.Empty;
        GurobiStructuralLogSummary structural = GurobiStructuralLogSummaryParser.Parse(solverLog);
        int statusCode = model.Model.Get(GRB.IntAttr.Status);
        int solutionCount = model.Model.Get(GRB.IntAttr.SolCount);
        MacroCandidateEligibilityAudit candidateEligibility = solutionCount > 0
            ? MacroCandidateSnapshotAuditor.CaptureAndSave(model, candidateSnapshotPath)
            : new MacroCandidateEligibilityAudit(
                false,
                false,
                false,
                null,
                null,
                null,
                model.Model.Get(GRB.DoubleParam.FeasibilityTol),
                model.Model.Get(GRB.DoubleParam.IntFeasTol),
                MacroObjectiveCatalog.NecessaryViolationDefinitions.Count,
                null,
                0, 0, 0, 0, 0, 0,
                null,
                "no_integer_incumbent");
        MacroCandidateSolveDisposition disposition = MacroCandidateSolveOutcomeClassifier.Resolve(
            statusCode,
            solutionCount,
            candidateEligibility.Passed,
            solveParameters.StopAfterFirstModeledFeasibleCandidate,
            incumbentCallback.EarlyStopTriggered);
        string? modelPath = null;
        string? iisPath = null;
        ValidityFirstIisDiagnostic? iisDiagnostic = null;
        if (statusCode == GRB.Status.TIME_LIMIT && solutionCount == 0)
        {
            modelPath = Path.Combine(diagnosticsDirectory, prefix + ".unknown.lp");
            model.Model.Write(modelPath);
        }
        else if (ValidityFirstIisPolicy.ShouldCompute(statusCode, solutionCount))
        {
            model.IdentityAudit = GurobiModelIdentityAuditor.ValidateAndCapture(
                model.Model,
                $"{phase}::before_iis",
                identityFailurePath);
            if (iisContext is not null)
            {
                ValidityFirstIisRuntimeBudget iisBudget = ValidityFirstIisRuntimeBudgetPolicy.Resolve(
                    iisContext.PerCallBudgetSeconds,
                    iisContext.CumulativeBudgetSeconds,
                    iisContext.BudgetTracker.UsedSeconds,
                    iisContext.TotalRuntimeBudgetSeconds,
                    iisContext.ElapsedSecondsProvider(),
                    iisContext.FinalValidationReserveSeconds);
                if (iisBudget.CanStart)
                {
                    string iisLogPath = Path.Combine(diagnosticsDirectory, prefix + ".iis.gurobi.log");
                    File.WriteAllText(iisLogPath, string.Empty, new UTF8Encoding(false));
                    Stopwatch iisClock = Stopwatch.StartNew();
                    bool completed = false;
                    bool timedOut = false;
                    try
                    {
                        model.Model.Set(GRB.StringParam.LogFile, iisLogPath);
                        model.Model.Set(GRB.DoubleParam.TimeLimit, iisBudget.EffectiveSeconds);
                        model.Model.ComputeIIS();
                        completed = true;
                    }
                    catch (GRBException exception) when (
                        exception.Message.Contains("time limit", StringComparison.OrdinalIgnoreCase))
                    {
                        timedOut = true;
                    }
                    finally
                    {
                        iisClock.Stop();
                        iisContext.BudgetTracker.Record(iisClock.Elapsed.TotalSeconds);
                        model.Model.Set(GRB.DoubleParam.TimeLimit, actualTimeLimit);
                        model.Model.Set(GRB.StringParam.LogFile, string.Empty);
                    }

                    bool? isMinimal = TryReadIisMinimal(model.Model);
                    int constraintMembers = CountIisConstraints(model.Model);
                    int boundMembers = CountIisBounds(model.Model);
                    bool hasMembers = constraintMembers + boundMembers > 0;
                    string iisLog = File.Exists(iisLogPath) ? File.ReadAllText(iisLogPath) : string.Empty;
                    timedOut = timedOut ||
                               iisLog.Contains("time limit", StringComparison.OrdinalIgnoreCase) ||
                               isMinimal == false;
                    iisPath = Path.Combine(diagnosticsDirectory, prefix + ".infeasible.ilp");
                    if (hasMembers)
                    {
                        model.Model.Write(iisPath);
                    }
                    else
                    {
                        iisPath = null;
                    }
                    iisDiagnostic = new ValidityFirstIisDiagnostic(
                        true,
                        completed && !timedOut,
                        timedOut || !completed,
                        isMinimal,
                        hasMembers,
                        iisBudget.EffectiveSeconds,
                        iisClock.Elapsed.TotalSeconds,
                        iisContext.BudgetTracker.UsedSeconds,
                        constraintMembers,
                        boundMembers,
                        !timedOut && completed && isMinimal == true
                            ? "complete_minimal"
                            : hasMembers ? "time_limited_nonminimal" : "time_limited_diagnostic_insufficient",
                        iisLogPath);
                }
                else
                {
                    iisDiagnostic = new ValidityFirstIisDiagnostic(
                        false, false, false, null, false, 0.0, 0.0,
                        iisContext.BudgetTracker.UsedSeconds, 0, 0,
                        "budget_unavailable", null);
                }
            }
        }

        int variableCount = model.Model.Get(GRB.IntAttr.NumVars);
        int integerCount = model.Model.Get(GRB.IntAttr.NumIntVars);
        ValidityFirstEnhancedMasterSolveDiagnostic diagnostic = new(
            sequence,
            iterationIndex,
            phase,
            budget.RequestedSolveSeconds,
            budget.EffectiveSolveSeconds,
            budget.RemainingGlobalSeconds,
            budget.ReservedValidationSeconds,
            actualTimeLimit,
            actualMipFocus,
            actualMipGap,
            actualSolutionLimit,
            actualThreads,
            actualSeed,
            model.Model.Get(GRB.IntAttr.NumConstrs),
            variableCount,
            variableCount - integerCount,
            integerCount,
            model.Model.Get(GRB.IntAttr.NumBinVars),
            structural.PresolvedRows,
            structural.PresolvedColumns,
            structural.PresolvedNonzeros,
            structural.PresolveSeconds,
            structural.RootRelaxationSeconds,
            structural.PrimaryObjectiveSeconds,
            structural.SecondaryObjectiveSeconds,
            model.Model.Get(GRB.DoubleAttr.NodeCount),
            solutionCount,
            model.StatusName,
            solveRuntimeSeconds,
            incumbentCallback.FirstIncumbentTimeSeconds,
            candidateEligibility.Passed ? incumbentCallback.FirstIncumbentTimeSeconds : null,
            solveParameters.CandidateStrategy,
            solveParameters.StopAfterFirstModeledFeasibleCandidate,
            incumbentCallback.EarlyStopTriggered,
            disposition.ApplicationStopReason,
            disposition.CanProceedToFullCheck,
            structural.PrimaryObjectiveStarted,
            structural.SecondaryObjectiveStarted,
            candidateEligibility,
            candidateEligibility.SnapshotPath,
            MipStartFeedbackClassifier.Resolve(solverLog),
            structural.NumericalWarnings,
            structural.ModelFingerprint,
            configurationFingerprint,
            logPath,
            modelPath,
            iisPath,
            iisDiagnostic);

        File.WriteAllText(diagnosticPath, JsonSerializer.Serialize(diagnostic, jsonOptions), new UTF8Encoding(false));
        File.WriteAllText(parameterPath, JsonSerializer.Serialize(new
        {
            timeLimit = actualTimeLimit,
            mipFocus = actualMipFocus,
            mipGap = actualMipGap,
            solutionLimit = actualSolutionLimit,
            threads = actualThreads,
            seed = actualSeed,
            objectiveKind = solveParameters.ObjectiveKind.ToString(),
            candidateStrategy = solveParameters.CandidateStrategy.ToString(),
            stopAfterFirstModeledFeasibleCandidate =
                solveParameters.StopAfterFirstModeledFeasibleCandidate,
            macroObjectives = MacroObjectiveCatalog.Definitions,
            iisPerCallTimeLimit = iisContext?.PerCallBudgetSeconds,
            iisCumulativeBudget = iisContext?.CumulativeBudgetSeconds,
            compactDiagnostics,
            configurationFingerprint
        }, jsonOptions), new UTF8Encoding(false));
        return diagnostic;
    }

    private static bool? TryReadIisMinimal(GRBModel model)
    {
        try
        {
            return model.Get(GRB.IntAttr.IISMinimal) == 1;
        }
        catch (GRBException)
        {
            return null;
        }
    }

    private static int CountIisConstraints(GRBModel model)
    {
        try
        {
            return model.GetConstrs().Count(constraint => constraint.Get(GRB.IntAttr.IISConstr) == 1);
        }
        catch (GRBException)
        {
            return 0;
        }
    }

    private static int CountIisBounds(GRBModel model)
    {
        try
        {
            return model.GetVars().Count(variable =>
                variable.Get(GRB.IntAttr.IISLB) == 1 || variable.Get(GRB.IntAttr.IISUB) == 1);
        }
        catch (GRBException)
        {
            return 0;
        }
    }
}

/// <summary>
/// 表示一次 validity-first Master 求解实际采用的可行性搜索参数。
/// </summary>
public sealed record ValidityFirstSolveParameterPlan(
    int MipFocus,
    double MipGap,
    int? SolutionLimit = null,
    ValidityFirstSolveObjectiveKind ObjectiveKind = ValidityFirstSolveObjectiveKind.BusinessMultiObjective,
    MacroCandidateCheckStrategy CandidateStrategy = MacroCandidateCheckStrategy.CompleteObjectives,
    bool StopAfterFirstModeledFeasibleCandidate = false);

/// <summary>
/// 在可行性恢复阶段应用宽松的内层停止精度；完整可行解出现后的业务目标改善阶段
/// 保留模型已有参数，避免把内层5%误报为完整问题的 global gap。
/// </summary>
public static class ValidityFirstSolveParameterPolicy
{
    public static ValidityFirstSolveParameterPlan Resolve(string phase, CheckerLoopOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);
        ArgumentNullException.ThrowIfNull(options);
        bool businessImprovementPhase = string.Equals(
            phase,
            "business_objective_improvement",
            StringComparison.OrdinalIgnoreCase);
        bool phaseTwoRecovery = phase is "feedback_batch" or "hotspot_joint_master" or
            "hotspot_exactification";
        if (options.EnablePhasedFeasibilityRecovery && phaseTwoRecovery)
        {
            return new ValidityFirstSolveParameterPlan(
                options.ValidityFirstRecoveryMipFocus,
                options.ValidityFirstRecoveryMipGap,
                SolutionLimit: 1,
                ValidityFirstSolveObjectiveKind.FeasibilityRecovery,
                options.MacroCandidateCheckStrategy,
                StopAfterFirstModeledFeasibleCandidate: false);
        }

        bool earlyCheck =
            options.MacroCandidateCheckStrategy == MacroCandidateCheckStrategy.EarlyCheck &&
            MacroCandidatePhasePolicy.IsEarlyCheckPhase(phase);
        if (earlyCheck)
        {
            return new ValidityFirstSolveParameterPlan(
                options.ValidityFirstRecoveryMipFocus,
                options.ValidityFirstRecoveryMipGap,
                SolutionLimit: null,
                ValidityFirstSolveObjectiveKind.BusinessMultiObjective,
                options.MacroCandidateCheckStrategy,
                StopAfterFirstModeledFeasibleCandidate: true);
        }

        return businessImprovementPhase
            ? new ValidityFirstSolveParameterPlan(
                0,
                0.0001,
                CandidateStrategy: options.MacroCandidateCheckStrategy)
            : new ValidityFirstSolveParameterPlan(
                options.ValidityFirstRecoveryMipFocus,
                options.ValidityFirstRecoveryMipGap,
                CandidateStrategy: options.MacroCandidateCheckStrategy);
    }
}
