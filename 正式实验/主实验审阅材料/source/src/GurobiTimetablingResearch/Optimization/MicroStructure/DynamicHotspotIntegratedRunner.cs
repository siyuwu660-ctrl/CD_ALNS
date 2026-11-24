using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

/// <summary>
/// Executes one proof-selected integrated hotspot experiment from an already saved
/// full-Macro candidate.  It deliberately has no path that rebuilds or optimizes the
/// initial Macro model.
/// </summary>
internal static class DynamicHotspotIntegratedRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    internal static int Run(TimetablingInstance instance, DynamicHotspotRunRequest request)
    {
        Stopwatch totalClock = Stopwatch.StartNew();
        DynamicHotspotResolvedRun resolved = request.Resolve();
        Directory.CreateDirectory(resolved.OutputDirectory);
        Directory.CreateDirectory(Path.Combine(resolved.OutputDirectory, "logs"));
        Directory.CreateDirectory(Path.Combine(resolved.OutputDirectory, "state"));

        string resultPath = Path.Combine(resolved.OutputDirectory, "result.md");
        string summaryPath = Path.Combine(resolved.OutputDirectory, "run_summary.json");
        string attemptsPath = Path.Combine(resolved.OutputDirectory, "attempts.csv");
        string modelStatsPath = Path.Combine(resolved.OutputDirectory, "model_stats.json");
        string evidencePath = Path.Combine(resolved.OutputDirectory, "scope_evidence.json");
        string logPath = Path.Combine(resolved.OutputDirectory, "logs", "integrated_hotspot_probe.log");
        string candidatePath = Path.Combine(resolved.OutputDirectory, "state", "joint_candidate.snapshot.json");
        string proofPath = Path.Combine(resolved.OutputDirectory, "state", "integrated_model_proof.json");
        string diagnosticPath = Path.Combine(resolved.OutputDirectory, "state", "diagnostic_state.json");

        try
        {
            MasterSolutionSnapshot reference = MasterSolutionSnapshotStore.Load(
                resolved.MacroSnapshotPath,
                instance.Meta.InstanceName,
                instance.Trains.Select(train => train.TrainIndex).ToHashSet());
            string actualFingerprint = SnapshotFingerprint.Calculate(reference);
            if (!string.Equals(actualFingerprint, resolved.MacroFingerprint, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"Recorded Macro fingerprint differs from the saved snapshot: " +
                    $"recorded={resolved.MacroFingerprint}, actual={actualFingerprint}.");

            MicroPatternReconstructionBundle bundle = MicroPatternReconstructionBundleStore.Load(
                resolved.PatternBundlePath, instance, reference);
            int baselineWitnessCount = bundle.Patterns.Sum(pattern => pattern.Witnesses.Count);
            if (baselineWitnessCount != instance.TrainStops.Count)
                throw new InvalidDataException(
                    $"Saved pattern coverage is incomplete: witnesses={baselineWitnessCount}, " +
                    $"requirements={instance.TrainStops.Count}.");

            DynamicHotspotEvidence evidence = DynamicHotspotEvidenceLoader.Load(
                resolved.ProofPath, instance, bundle.Patterns);
            WriteJson(evidencePath, new
            {
                sourceProofPath = resolved.ProofPath,
                evidence,
                note = "Scope is derived from this attempt-local proof; station names are not constants."
            });

            string[] corridorStationOrder = ResolveCorridorStationOrder(instance);
            DynamicHotspotComponentBuildResult allComponents = DynamicHotspotComponentFactory.Build(
                instance,
                reference,
                corridorStationOrder,
                bundle.EndpointOccupationSeconds,
                evidence.ProofIdentity);
            if (!allComponents.IsComplete)
                throw new InvalidDataException($"Hotspot component domain is incomplete: {allComponents.Status}");

            Dictionary<string, DynamicHotspotStationCost> costs = allComponents.StationAudits.ToDictionary(
                audit => audit.StationName,
                audit => new DynamicHotspotStationCost(
                    audit.StationName,
                    checked(audit.AlternativeCount + audit.EstimatedOrderVariables),
                    checked(audit.AlternativeCount + audit.EstimatedOrderVariables),
                    audit.EstimatedConstraints),
                StringComparer.OrdinalIgnoreCase);
            DynamicHotspotScopeSelection scope = DynamicHotspotScopeSelector.SelectContiguous(
                evidence,
                corridorStationOrder,
                costs,
                new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
                new DynamicHotspotScopeBudget(
                    resolved.Gate.MaximumVariables,
                    resolved.Gate.MaximumActiveBinaries,
                    resolved.Gate.MaximumConstraints));
            IReadOnlySet<string> reportedStations = (scope.IsAccepted
                    ? scope.StationNames
                    : scope.RequiredStationNames)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            DynamicHotspotComponentBuildResult components = new(
                allComponents.IsComplete,
                allComponents.Status,
                allComponents.Components.Where(component => reportedStations.Contains(component.StationName)).ToArray(),
                allComponents.StationAudits.Where(audit => reportedStations.Contains(audit.StationName)).ToArray());
            if (!scope.IsAccepted)
            {
                totalClock.Stop();
                WriteJson(diagnosticPath, new { scope, components.StationAudits, reason = scope.Status });
                WriteAttempts(attemptsPath, scope, null, null, totalClock.Elapsed.TotalSeconds);
                WriteSummary(summaryPath, resolved, reference, bundle, evidence, scope, components, null, null,
                    false, false, null, diagnosticPath, totalClock.Elapsed.TotalSeconds, "scope_rejected_before_optimize");
                WriteResult(resultPath, resolved, scope, components, null, null, false, false,
                    totalClock.Elapsed.TotalSeconds, diagnosticPath);
                WriteReproduce(resolved);
                WriteSourceManifest(resolved.OutputDirectory);
                return 2;
            }

            HashSet<string> activeStations = scope.StationNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
            MicroWitnessCertificate[] retainedPatterns = bundle.Patterns
                .Where(pattern => !activeStations.Contains(pattern.StationName))
                .OrderBy(pattern => pattern.StationName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            VerifiedMicroAssignmentWitness[] mipStart = bundle.Patterns
                .SelectMany(pattern => pattern.Witnesses)
                .ToArray();

            using DynamicHotspotIntegratedCoordinator coordinator = DynamicHotspotIntegratedCoordinator.Build(
                new DynamicHotspotIntegratedBuildRequest(
                    instance,
                    reference,
                    scope,
                    components.Components,
                    retainedPatterns,
                    resolved.Gate,
                    mipStart,
                    bundle.EndpointOccupationSeconds,
                    bundle.RunningTimeSupplementRatio,
                    EndpointOrderChangeBudget: 4));
            WriteJson(modelStatsPath, new
            {
                coordinator.Audit,
                scope,
                components.StationAudits,
                fullLineTimeVariables = new
                {
                    arrivals = coordinator.Model.ArrivalVars.Count,
                    departures = coordinator.Model.DepartureVars.Count,
                    allContinuous = coordinator.Model.ArrivalVars.Values
                        .Concat(coordinator.Model.DepartureVars.Values)
                        .All(variable => variable.Get(Gurobi.GRB.CharAttr.VType) == Gurobi.GRB.CONTINUOUS)
                },
                businessBoundaries = new
                {
                    completeMacroRelations = true,
                    k = 4,
                    kReference = "saved Macro candidate",
                    nonHotspotEndpointChanges = 0,
                    b = "off",
                    continuousTimeAndClosedWindowSemanticsUnchanged = true
                }
            });

            DynamicHotspotIntegratedSolveResult solve = coordinator.Solve(resolved.ProbeSeconds, logPath);
            if (solve.ProofRows.Count > 0)
            {
                string currentProofIdentity = CalculateIntegratedProofIdentity(scope, coordinator.Audit, solve.ProofRows);
                WriteJson(proofPath, new
                {
                    proofIdentity = currentProofIdentity,
                    sourceProofIdentity = scope.ProofIdentity,
                    solve.Status,
                    solve.StatusCode,
                    rows = solve.ProofRows,
                    note = "Proof belongs only to this integrated model and this selected scope."
                });
            }

            bool strictJointFeasible = false;
            bool readbackPassed = false;
            object? validationSummary = null;
            string finalStatePath = solve.ProofRows.Count > 0 ? proofPath : diagnosticPath;
            if (solve.Candidate is not null && solve.MacroValidated)
            {
                MasterSolutionSnapshot candidate = solve.Candidate;
                string candidateFingerprint = SnapshotFingerprint.Calculate(candidate);
                VerifiedMicroAssignmentWitness[] combined = retainedPatterns
                    .SelectMany(pattern => pattern.Witnesses)
                    .Concat(solve.ActiveAssignments)
                    .Select(witness => RebaseWitness(witness, candidate, candidateFingerprint))
                    .OrderBy(witness => witness.StationName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(witness => witness.TrainIndex)
                    .ThenBy(witness => witness.RequirementId, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                int distinctRequirements = combined.Select(witness => witness.RequirementId)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count();
                candidate = candidate with { VerifiedMicroAssignments = combined };

                Stopwatch validationClock = Stopwatch.StartNew();
                StationLevelMicroFeasibilitySlaveResult stationAnalysis =
                    CheckerMicroValidationFactory.CreateSlave(bundle.EndpointOccupationSeconds).Solve(coordinator.Model);
                MicroFeasibilityReport globalMicro =
                    CheckerMicroValidationFactory.CreateChecker(bundle.EndpointOccupationSeconds).Validate(coordinator.Model);
                VerifiedMicroAssignmentValidationReport fixedAssignments = VerifiedMicroAssignmentValidator.Validate(
                    instance, candidate, combined, stationAnalysis, bundle.EndpointOccupationSeconds);
                validationClock.Stop();
                strictJointFeasible =
                    solve.MacroViolationCount == 0 &&
                    globalMicro.IsFeasible && globalMicro.Status == MicroSolveStatus.Feasible &&
                    globalMicro.Conflicts.Count == 0 &&
                    fixedAssignments.IsValid &&
                    combined.Length == instance.TrainStops.Count &&
                    distinctRequirements == instance.TrainStops.Count;
                validationSummary = new
                {
                    seconds = validationClock.Elapsed.TotalSeconds,
                    macroViolationCount = solve.MacroViolationCount,
                    globalMicroStatus = globalMicro.Status.ToString(),
                    globalMicroConflictCount = globalMicro.Conflicts.Count,
                    globalMicroUnknown = globalMicro.Status == MicroSolveStatus.Unknown ? 1 : 0,
                    assignmentCount = combined.Length,
                    distinctRequirementCount = distinctRequirements,
                    fixedAssignmentValidation = fixedAssignments
                };
                if (strictJointFeasible)
                {
                    MasterSolutionSnapshotStore.Save(candidatePath, instance.Meta.InstanceName, candidate);
                    MasterSolutionSnapshot readback = MasterSolutionSnapshotStore.Load(
                        candidatePath,
                        instance.Meta.InstanceName,
                        instance.Trains.Select(train => train.TrainIndex).ToHashSet());
                    VerifiedMicroAssignmentValidationReport readbackValidation = VerifiedMicroAssignmentValidator.Validate(
                        instance,
                        readback,
                        readback.VerifiedMicroAssignments ?? [],
                        stationAnalysis,
                        bundle.EndpointOccupationSeconds);
                    readbackPassed =
                        string.Equals(SnapshotFingerprint.Calculate(readback), candidateFingerprint,
                            StringComparison.OrdinalIgnoreCase) && readbackValidation.IsValid;
                    strictJointFeasible &= readbackPassed;
                    finalStatePath = candidatePath;
                }
            }

            if (!File.Exists(diagnosticPath) && !strictJointFeasible)
            {
                WriteJson(diagnosticPath, new
                {
                    scope,
                    coordinator.Audit,
                    solve,
                    validationSummary,
                    mathematicalMeaning = solve.Status == "INFEASIBLE"
                        ? "Only this proof-selected K=4 integrated hotspot scope is infeasible."
                        : "No global infeasibility conclusion; the controlled solve ended without a strict joint solution."
                });
                finalStatePath = diagnosticPath;
            }

            totalClock.Stop();
            WriteAttempts(attemptsPath, scope, coordinator.Audit, solve, totalClock.Elapsed.TotalSeconds);
            WriteSummary(summaryPath, resolved, reference, bundle, evidence, scope, components, coordinator.Audit, solve,
                strictJointFeasible, readbackPassed, validationSummary, finalStatePath,
                totalClock.Elapsed.TotalSeconds, strictJointFeasible ? "strict_joint_feasible" : solve.StopReason);
            WriteResult(resultPath, resolved, scope, components, coordinator.Audit, solve,
                strictJointFeasible, readbackPassed, totalClock.Elapsed.TotalSeconds, finalStatePath);
            WriteReproduce(resolved);
            WriteSourceManifest(resolved.OutputDirectory);
            return strictJointFeasible ? 0 : 1;
        }
        catch (Exception exception)
        {
            totalClock.Stop();
            WriteJson(diagnosticPath, new
            {
                status = "engineering_failure",
                exception = exception.ToString(),
                totalSeconds = totalClock.Elapsed.TotalSeconds
            });
            File.WriteAllText(resultPath,
                $"# Dynamic hotspot integrated Micro experiment\n\n" +
                $"The run stopped on an engineering failure before a mathematical conclusion.\n\n" +
                $"- Error: `{exception.Message}`\n" +
                $"- Diagnostic: `{diagnosticPath}`\n",
                new UTF8Encoding(false));
            WriteReproduce(resolved);
            WriteSourceManifest(resolved.OutputDirectory);
            return 3;
        }
    }

    private static VerifiedMicroAssignmentWitness RebaseWitness(
        VerifiedMicroAssignmentWitness witness,
        MasterSolutionSnapshot candidate,
        string candidateFingerprint) => witness with
    {
        CandidateSnapshotFingerprint = candidateFingerprint,
        ArrivalSeconds = candidate.ArrivalSecondsByStopId[witness.RequirementId],
        DepartureSeconds = candidate.DepartureSecondsByStopId[witness.RequirementId],
        VerificationScope = "station_micro_validated"
    };

    private static string[] ResolveCorridorStationOrder(TimetablingInstance instance)
    {
        List<string> ordered = [];
        foreach (IGrouping<int, TrainStopRecord> group in instance.TrainStops
                     .GroupBy(stop => stop.StationOrder)
                     .OrderBy(group => group.Key))
        {
            string[] stations = group.Select(stop => stop.StationName)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (stations.Length != 1)
                throw new InvalidDataException(
                    $"Station order {group.Key} is not a single fixed corridor station: {string.Join('|', stations)}.");
            ordered.Add(stations[0]);
        }
        if (ordered.Count == 0)
            throw new InvalidDataException("The instance has no ordered corridor stations.");
        return ordered.ToArray();
    }

    private static string CalculateIntegratedProofIdentity(
        DynamicHotspotScopeSelection scope,
        DynamicHotspotIntegratedModelAudit audit,
        IReadOnlyList<ContinuousProofRow> rows)
    {
        StringBuilder canonical = new();
        canonical.Append(scope.ProofIdentity).Append('|')
            .Append(string.Join(',', scope.StationNames)).Append('|')
            .Append(audit.ActiveVariableCount).Append('|')
            .Append(audit.ActiveBinaryCount).Append('|')
            .Append(audit.ActiveConstraintCount).AppendLine();
        foreach (ContinuousProofRow row in rows.OrderBy(row => row.ConstraintName, StringComparer.Ordinal))
            canonical.Append(row.ConstraintName).Append('|').Append(row.EvidenceKind).Append('|')
                .Append(row.BusinessKind).Append('|').Append(row.BusinessId).AppendLine();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
    }

    private static void WriteAttempts(
        string path,
        DynamicHotspotScopeSelection scope,
        DynamicHotspotIntegratedModelAudit? audit,
        DynamicHotspotIntegratedSolveResult? solve,
        double totalSeconds)
    {
        string[] values =
        [
            "1",
            Csv(string.Join('|', scope.StationNames)),
            Csv(scope.ProofIdentity),
            Csv(scope.Status),
            audit?.ActiveVariableCount.ToString(CultureInfo.InvariantCulture) ?? "",
            audit?.ActiveBinaryCount.ToString(CultureInfo.InvariantCulture) ?? "",
            audit?.ActiveConstraintCount.ToString(CultureInfo.InvariantCulture) ?? "",
            audit?.PresolvedVariableCount.ToString(CultureInfo.InvariantCulture) ?? "",
            audit?.PresolvedActiveBinaryCount.ToString(CultureInfo.InvariantCulture) ?? "",
            audit?.PresolvedConstraintCount.ToString(CultureInfo.InvariantCulture) ?? "",
            solve?.Status ?? "NOT_RUN",
            solve?.FirstIncumbentSeconds?.ToString("R", CultureInfo.InvariantCulture) ?? "",
            solve?.RuntimeSeconds.ToString("R", CultureInfo.InvariantCulture) ?? "",
            totalSeconds.ToString("R", CultureInfo.InvariantCulture)
        ];
        File.WriteAllText(path,
            "attempt,stations,proof_identity,scope_status,variables,binaries,constraints,presolved_variables,presolved_binaries,presolved_constraints,solve_status,first_incumbent_seconds,solve_seconds,total_seconds\n" +
            string.Join(',', values) + "\n", new UTF8Encoding(false));
    }

    private static void WriteSummary(
        string path,
        DynamicHotspotResolvedRun resolved,
        MasterSolutionSnapshot reference,
        MicroPatternReconstructionBundle bundle,
        DynamicHotspotEvidence evidence,
        DynamicHotspotScopeSelection scope,
        DynamicHotspotComponentBuildResult components,
        DynamicHotspotIntegratedModelAudit? audit,
        DynamicHotspotIntegratedSolveResult? solve,
        bool strictJointFeasible,
        bool readbackPassed,
        object? validation,
        string finalStatePath,
        double totalSeconds,
        string finalStatus)
    {
        WriteJson(path, new
        {
            formatVersion = 1,
            runKind = "fixed saved Macro start; proof-selected integrated hotspot discrete decisions plus full-line free continuous times",
            createdAt = DateTimeOffset.Now,
            codeIdentity = new
            {
                assemblyMvid = typeof(DynamicHotspotIntegratedRunner).Assembly.ManifestModule.ModuleVersionId,
                gitIdentity = (string?)null,
                gitIdentityReason = "preserved experiment workspace is not a Git repository"
            },
            input = new
            {
                resolved.BaselineDirectory,
                resolved.DatasetDirectory,
                resolved.MacroSnapshotPath,
                resolved.PatternBundlePath,
                resolved.ProofPath,
                macroFingerprint = SnapshotFingerprint.Calculate(reference),
                bundle.PatternCombinationIdentity,
                baselinePatternCount = bundle.Patterns.Length,
                baselineWitnessCount = bundle.Patterns.Sum(pattern => pattern.Witnesses.Count),
                historicalPatternGenerationTimeIncluded = false
            },
            configuration = new
            {
                seed = 0,
                threads = 0,
                k = 4,
                nonHotspotEndpointChanges = 0,
                a = "existing business boundary retained by full Macro builder",
                b = "off",
                initialMacroOptimizeCalled = false,
                probeSeconds = resolved.ProbeSeconds,
                gate = resolved.Gate,
                timeSemantics = "continuous; closed-window recount; zero business-time relaxation; numerical tolerance 1e-6 seconds"
            },
            evidence,
            scope,
            stationAudits = components.StationAudits,
            model = audit,
            solve,
            validation,
            result = new
            {
                strictJointFeasible,
                readbackPassed,
                finalStatus,
                finalStatePath,
                totalSeconds
            }
        });
    }

    private static void WriteResult(
        string path,
        DynamicHotspotResolvedRun resolved,
        DynamicHotspotScopeSelection scope,
        DynamicHotspotComponentBuildResult components,
        DynamicHotspotIntegratedModelAudit? audit,
        DynamicHotspotIntegratedSolveResult? solve,
        bool strictJointFeasible,
        bool readbackPassed,
        double totalSeconds,
        string finalStatePath)
    {
        StringBuilder text = new();
        text.AppendLine("# Dynamic hotspot integrated Micro experiment");
        text.AppendLine();
        text.AppendLine(strictJointFeasible
            ? "Result: a single candidate passed complete Macro, Global Micro, fixed-resource, and readback validation."
            : "Result: no strict joint-feasible solution was established by this controlled experiment.");
        text.AppendLine();
        text.AppendLine($"- Saved Macro reused: `{resolved.MacroSnapshotPath}`");
        text.AppendLine("- Initial Macro optimization: not called");
        text.AppendLine($"- Proof-selected stations: `{string.Join(" | ", scope.StationNames)}`");
        text.AppendLine($"- Scope status: `{scope.Status}`");
        text.AppendLine($"- Station count was data-driven: {scope.StationNames.Count}; no two-station constant or station-ID constant was used");
        text.AppendLine($"- K / non-hotspot endpoint changes: 4 / 0");
        text.AppendLine($"- Local Micro alternatives: {components.StationAudits.Sum(item => item.AlternativeCount)}");
        if (audit is not null)
        {
            text.AppendLine($"- Actual model: {audit.ActiveVariableCount} variables, {audit.ActiveBinaryCount} binaries, {audit.ActiveConstraintCount} constraints");
            text.AppendLine($"- Presolved model: {audit.PresolvedVariableCount} variables, {audit.PresolvedActiveBinaryCount} binaries, {audit.PresolvedConstraintCount} constraints");
            text.AppendLine($"- Gate: `{audit.GateStatus}`");
        }
        if (solve is not null)
        {
            text.AppendLine($"- Solve status: `{solve.Status}`; first incumbent: {solve.FirstIncumbentSeconds?.ToString("F3", CultureInfo.InvariantCulture) ?? "not recorded"} s");
            text.AppendLine($"- Solve time: {solve.RuntimeSeconds:F3} s; Macro violations: {solve.MacroViolationCount}");
            text.AppendLine($"- Stop reason: `{solve.StopReason}`");
        }
        text.AppendLine($"- Strict joint feasible: {strictJointFeasible}");
        text.AppendLine($"- Serialized readback passed: {readbackPassed}");
        text.AppendLine($"- Total elapsed: {totalSeconds:F3} s");
        text.AppendLine($"- Final candidate or diagnostic: `{finalStatePath}`");
        text.AppendLine();
        text.AppendLine("A reduced binary count is a structural observation, not a measured speedup claim; no paired performance experiment was run.");
        File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
    }

    /// <summary>Write an optional hotspot reproduction command using a caller-selected project directory.</summary>
    /// <param name="resolved">Previously resolved files and output directory for this run.</param>
    private static void WriteReproduce(DynamicHotspotResolvedRun resolved)
    {
        string project = Path.Combine(
            Directory.GetParent(typeof(DynamicHotspotIntegratedRunner).Assembly.Location)!.FullName,
            "placeholder");
        _ = project;
        string path = Path.Combine(resolved.OutputDirectory, "reproduce.ps1");
        string command =
            "param([string]$work = (Get-Location).Path)\n" +
            "$ErrorActionPreference = 'Stop'\n" +
            "Set-Location -LiteralPath $work\n" +
            "dotnet run --project 'src\\GurobiTimetablingResearch\\GurobiTimetablingResearch.csproj' -c Release --no-build -- " +
            $"--dataset='{resolved.DatasetDirectory}' --micro-hotspot-integrated " +
            $"--micro-hotspot-baseline='{resolved.BaselineDirectory}' " +
            $"--micro-hotspot-output='{resolved.OutputDirectory}' " +
            $"--micro-hotspot-proof='{resolved.ProofPath}' " +
            $"--micro-hotspot-probe-seconds={resolved.ProbeSeconds.ToString("R", CultureInfo.InvariantCulture)}\n";
        File.WriteAllText(path, command, new UTF8Encoding(false));
    }

    private static void WriteSourceManifest(string outputDirectory)
    {
        string root = Directory.GetCurrentDirectory();
        string[] relativePaths =
        [
            "src\\GurobiTimetablingResearch\\Application\\Program.cs",
            "src\\GurobiTimetablingResearch\\Optimization\\MicroStructure\\DynamicHotspotScope.cs",
            "src\\GurobiTimetablingResearch\\Optimization\\MicroStructure\\DynamicHotspotRunRequest.cs",
            "src\\GurobiTimetablingResearch\\Optimization\\MicroStructure\\DynamicHotspotEvidenceLoader.cs",
            "src\\GurobiTimetablingResearch\\Optimization\\MicroStructure\\DynamicHotspotComponentFactory.cs",
            "src\\GurobiTimetablingResearch\\Optimization\\MicroStructure\\DynamicHotspotOutsideStopPolicy.cs",
            "src\\GurobiTimetablingResearch\\Optimization\\MicroStructure\\DynamicHotspotIntegratedCoordinator.cs",
            "src\\GurobiTimetablingResearch\\Optimization\\MicroStructure\\DynamicHotspotIntegratedRunner.cs",
            "tests\\GurobiTimetablingResearch.MicroCoordination.Tests\\Program.cs"
        ];
        StringBuilder csv = new("path,sha256\n");
        foreach (string relative in relativePaths)
        {
            string full = Path.Combine(root, relative);
            if (!File.Exists(full)) continue;
            string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(full)));
            csv.Append(Csv(Path.GetFullPath(full))).Append(',').Append(hash).AppendLine();
        }
        File.WriteAllText(Path.Combine(outputDirectory, "source_manifest.csv"), csv.ToString(),
            new UTF8Encoding(false));
    }

    private static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(false));
    }

    private static string Csv(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) < 0 ? value : $"\"{value.Replace("\"", "\"\"")}\"";
}
