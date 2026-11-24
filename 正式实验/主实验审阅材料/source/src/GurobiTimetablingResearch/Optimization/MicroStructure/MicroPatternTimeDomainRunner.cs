using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Gurobi;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

internal sealed record MicroPatternTimeDomainRequest(
    string BaselineDirectory,
    string OutputDirectory,
    string DatasetPath,
    double TotalBudgetSeconds = 7200,
    double MilestoneSeconds = 3600,
    double SaveReserveSeconds = 120,
    double MasterSliceSeconds = 10,
    double CoordinationSliceSeconds = 120,
    double GenerationSliceSeconds = 60,
    double GenerationTaskMaximumSeconds = 300,
    double PreviouslyConsumedSeconds = 0,
    int EndpointOccupationSeconds = StationEndpointOccupationPolicy.DefaultSeconds,
    double RunningTimeSupplementRatio = 0.05);

internal sealed record MicroPatternTimeDomainTraceRow(
    int Iteration,
    string Phase,
    string Status,
    int PoolSize,
    string SelectedPatterns,
    int MasterBinaries,
    int MasterConstraints,
    int ContinuousVariables,
    int ContinuousBinaries,
    int ContinuousConstraints,
    int PatternConstraintCount,
    int ProofRowCount,
    int ConflictCount,
    string GeneratedStation,
    bool PatternAdded,
    double MasterSeconds,
    double BuildAndProofSeconds,
    double CoordinationSeconds,
    double GenerationSeconds,
    double ValidationSeconds,
    double ElapsedSeconds,
    string Reason);

internal static class MicroPatternTimeDomainRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    internal static int Run(TimetablingInstance instance, MicroPatternTimeDomainRequest request)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(request);
        if (request.TotalBudgetSeconds <= request.SaveReserveSeconds || request.MilestoneSeconds <= 0 ||
            request.MilestoneSeconds >= request.TotalBudgetSeconds)
            throw new ArgumentOutOfRangeException(nameof(request), "Invalid shared budget or milestone.");
        if (request.GenerationSliceSeconds <= 0 || request.GenerationTaskMaximumSeconds < request.GenerationSliceSeconds)
            throw new ArgumentOutOfRangeException(nameof(request), "Invalid local pattern-generation slice or task cap.");
        if (request.EndpointOccupationSeconds is not (0 or 600) ||
            Math.Abs(request.RunningTimeSupplementRatio - 0.05) > 1e-12)
            throw new InvalidDataException("Pattern-time-domain JZ run requires endpoint=0 (or explicit legacy 600) and running ratio=0.05.");

        Stopwatch wall = Stopwatch.StartNew();
        string baseline = Path.GetFullPath(request.BaselineDirectory);
        string output = Path.GetFullPath(request.OutputDirectory);
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(Path.Combine(output, "logs"));
        Directory.CreateDirectory(Path.Combine(output, "state"));
        string summaryPath = Path.Combine(baseline, "run_summary.json");
        if (!File.Exists(summaryPath)) throw new FileNotFoundException("Baseline run summary is missing.", summaryPath);
        (string sourcePath, string bundlePath, string? historicalFeedbackPath) = ResolveInputs(summaryPath, baseline);
        MasterSolutionSnapshot reference = MasterSolutionSnapshotStore.Load(
            sourcePath, instance.Meta.InstanceName,
            instance.Trains.Select(train => train.TrainIndex).ToHashSet());
        MicroPatternReconstructionBundle bundle = MicroPatternReconstructionBundleStore.Load(
            bundlePath, instance, reference);
        if (bundle.Patterns.Length != instance.Stations.Count ||
            bundle.Patterns.Sum(pattern => pattern.Witnesses.Count) != instance.TrainStops.Count)
            throw new InvalidDataException(
                $"Baseline pattern coverage is not complete: patterns={bundle.Patterns.Length}, " +
                $"witnesses={bundle.Patterns.Sum(pattern => pattern.Witnesses.Count)}, " +
                $"expected={instance.Stations.Count}/{instance.TrainStops.Count}.");

        string macroFingerprint = SnapshotFingerprint.Calculate(reference);
        string poolDirectory = Path.Combine(output, "state", "pattern_pool");
        string baselinePoolDirectory = Path.Combine(baseline, "state", "pattern_pool");
        int importedPatternCount = 0;
        int importedConflictCount = 0;
        int discardedImpliedConflictCount = 0;
        StationPatternPool pool;
        if (File.Exists(Path.Combine(poolDirectory, "index.json")))
            pool = StationPatternPool.Load(poolDirectory, instance.Meta.InstanceName, macroFingerprint);
        else
        {
            pool = new StationPatternPool(poolDirectory, instance.Meta.InstanceName, macroFingerprint);
            if (File.Exists(Path.Combine(baselinePoolDirectory, "index.json")))
            {
                StationPatternPool baselinePool = StationPatternPool.Load(
                    baselinePoolDirectory, instance.Meta.InstanceName, macroFingerprint);
                foreach (StationPattern pattern in baselinePool.Patterns) pool.Add(pattern);
                importedPatternCount = baselinePool.Count;
            }
            else
            {
                foreach (MicroWitnessCertificate certificate in bundle.Patterns)
                    pool.Add(StationPattern.Create(certificate));
                importedPatternCount = bundle.Patterns.Length;
            }
            pool.Save();
        }
        StationPatternPool replayedPool = StationPatternPool.Load(poolDirectory, instance.Meta.InstanceName, macroFingerprint);
        if (replayedPool.Count != pool.Count) throw new InvalidDataException("Pattern-pool readback changed its count.");
        pool = replayedPool;
        HashSet<string> initialPatternIds = bundle.Patterns.Select(certificate => StationPattern.Create(certificate).PatternId)
            .ToHashSet(StringComparer.Ordinal);
        string conflictPath = Path.Combine(output, "state", "pattern_conflicts.json");
        PatternConflictStore conflicts;
        if (File.Exists(conflictPath))
        {
            conflicts = PatternConflictStore.Load(conflictPath);
        }
        else
        {
            conflicts = new PatternConflictStore();
            string baselineConflictPath = Path.Combine(baseline, "state", "pattern_conflicts.json");
            if (File.Exists(baselineConflictPath))
            {
                PatternConflictStore baselineConflicts = PatternConflictStore.Load(baselineConflictPath);
                PatternCombinationConflict[] completeSelections = baselineConflicts.Conflicts
                    .Where(item => item.RequiredPatternsByStation.Count == instance.Stations.Count)
                    .ToArray();
                MicroPatternConsistencyAuditResult consistency = MicroPatternConsistencyAuditor.Audit(
                    instance, reference, pool, completeSelections);
                HashSet<string> nowImplied = consistency.Details
                    .Where(item => item.OvertakingStopViolations.Count > 0 ||
                                   item.TimeCategoryContradictions.Count > 0)
                    .Select(item => item.ConflictId).ToHashSet(StringComparer.Ordinal);
                foreach (PatternCombinationConflict item in baselineConflicts.Conflicts)
                    if (!nowImplied.Contains(item.ConflictId)) conflicts.Add(item);
                importedConflictCount = conflicts.Count;
                discardedImpliedConflictCount = nowImplied.Count;
                File.WriteAllText(Path.Combine(output, "state", "imported_consistency_audit.json"),
                    JsonSerializer.Serialize(new
                    {
                        source = baselineConflictPath,
                        sourceConflictCount = baselineConflicts.Count,
                        completeSelectionCount = completeSelections.Length,
                        consistency.OvertakingStopSelectionCount,
                        consistency.TimeCategorySelectionCount,
                        discardedAsImpliedByFormalMaster = nowImplied.Count,
                        retainedConflictCount = conflicts.Count,
                        consistency.Details
                    }, JsonOptions), new UTF8Encoding(false));
                conflicts.Save(conflictPath);
            }
        }
        string conditionalFeedbackPath = Path.Combine(output, "state", "conditional_time_feedback.json");
        List<ContinuousExtendabilityFeedback> conditionalFeedback = File.Exists(conditionalFeedbackPath)
            ? ContinuousExtendabilityFeedbackStore.Load(conditionalFeedbackPath).ToList()
            : [];
        int historicalFeedbackCount = CountHistoricalFeedback(historicalFeedbackPath);
        PrecedenceTrustRegion trustRegion = ValidityFirstEndpointOrderTrustRegionBuilder.Build(
            instance, reference, new HashSet<int> { 16, 19, 60 }, 4,
            "micro_pattern_time_domain_original_k_reference");
        IReadOnlyDictionary<string, string> dependencies = BuildDependencies(bundle, reference, trustRegion);
        MicroPatternMaster master = new(instance, reference, pool, conflicts, trustRegion,
            dependencies, initialPatternIds, request.EndpointOccupationSeconds,
            request.RunningTimeSupplementRatio);

        string[] generationPriority = BuildGenerationPriority(instance);
        List<MicroPatternTimeDomainTraceRow> trace = [];
        string tracePath = Path.Combine(output, "coordination_trace.csv");
        int priorIteration = ReadLastTraceIteration(tracePath);
        string generationLedgerPath = Path.Combine(output, "state", "generation_tasks.json");
        PatternGenerationTaskLedger generationLedger = File.Exists(generationLedgerPath)
            ? PatternGenerationTaskLedger.Load(generationLedgerPath, generationPriority,
                request.GenerationTaskMaximumSeconds)
            : RecoverGenerationLedger(output, generationPriority, request.GenerationTaskMaximumSeconds);
        generationLedger.Save(generationLedgerPath);
        Dictionary<string, IndependentStationPatternSession> generationSessions =
            new(StringComparer.Ordinal);
        bool milestoneSaved = HasSavedMilestone(output);
        bool jointFeasible = false;
        string finalStatus = "not_started";
        string stopReason = "not_started";
        MasterSolutionSnapshot? finalSnapshot = null;
        FixedPatternJointValidation? finalValidation = null;
        IReadOnlyDictionary<string, StationPattern>? finalSelection = null;
        MicroPatternMasterResult? lastUsableSelection = null;
        double firstJointFeasibleSeconds = double.NaN;
        double masterSeconds = 0, buildProofSeconds = 0, coordinationSeconds = 0,
            generationSeconds = 0, validationSeconds = 0;
        double lastBuildAndProofSeconds = 0;
        int iteration = priorIteration, coordinationChecks = conflicts.Count, provenConflicts = conflicts.Count,
            unknownChecks = 0,
            generatedPatternsThisProcess = 0, generatedTimeFeedback = 0,
            appliedTimeFeedbackRows = 0;
        MicroPatternMasterAudit? lastMasterAudit = null;
        FixedDiscreteCoordinatorAudit? lastContinuousAudit = null;

        double Elapsed() => request.PreviouslyConsumedSeconds + wall.Elapsed.TotalSeconds;

        while (Elapsed() < request.TotalBudgetSeconds - request.SaveReserveSeconds)
        {
            FlushPendingTrace();
            iteration++;
            SaveMilestoneIfNeeded();
            double remaining = request.TotalBudgetSeconds - request.SaveReserveSeconds - Elapsed();
            if (remaining <= 0) break;
            string masterLog = Path.Combine(output, "logs", $"master_{iteration:000}.log");
            MicroPatternMasterResult selected = master.Solve(
                Math.Min(request.MasterSliceSeconds, remaining), masterLog);
            masterSeconds += selected.RuntimeSeconds;
            lastMasterAudit = selected.Audit;
            if (!selected.HasSelection || selected.DiscreteState is null)
            {
                if (IsRecoverableMasterUnknownStatus(selected.StatusCode))
                {
                    bool changedMaster = false;
                    do
                    {
                        remaining = request.TotalBudgetSeconds - request.SaveReserveSeconds - Elapsed();
                        if (remaining <= 0) break;
                        (string recoveryStation, bool recoveryAdded, double recoverySeconds, string recoveryReason) = GenerateAlternative(
                            lastUsableSelection, null, null, generationPriority, generationLedger,
                            Math.Min(request.GenerationSliceSeconds, remaining));
                        generationSeconds += recoverySeconds;
                        if (recoveryAdded)
                        {
                            generatedPatternsThisProcess++;
                            pool.Save();
                            master = new(instance, reference, pool, conflicts, trustRegion,
                                dependencies, initialPatternIds, request.EndpointOccupationSeconds,
                                request.RunningTimeSupplementRatio);
                            changedMaster = true;
                        }
                        trace.Add(Row("master_unknown_generation", recoveryReason, selected,
                            recoveryStation, recoveryAdded, generation: recoverySeconds));
                        FlushPendingTrace();
                        SaveMilestoneIfNeeded();
                        if (!recoveryAdded && !generationLedger.HasEligibleTask) break;
                    } while (!changedMaster);

                    if (changedMaster) continue;
                    finalStatus = "pattern_master_unknown";
                    stopReason = generationLedger.HasEligibleTask
                        ? $"{selected.StopReason};shared_budget_reserve_reached_before_master_changed"
                        : $"{selected.StopReason};no eligible generation task remains";
                    break;
                }
                string? masterProofStop = selected.InfeasibilityProof?.SuggestedStopIds.FirstOrDefault();
                string? masterProofStation = masterProofStop is not null && instance.StopsById.TryGetValue(
                        masterProofStop, out TrainStopRecord? proofStopRecord)
                    ? proofStopRecord.StationName
                    : selected.InfeasibilityProof?.SuggestedStations.FirstOrDefault();
                if (selected.InfeasibilityProof is not null)
                    File.WriteAllText(Path.Combine(output, "state", "last_master_infeasibility_proof.json"),
                        JsonSerializer.Serialize(new { iteration, selected.InfeasibilityProof }, JsonOptions),
                        new UTF8Encoding(false));
                (string station, bool added, double seconds, string reason) = GenerateAlternative(
                    null, masterProofStation, masterProofStop, generationPriority, generationLedger,
                    Math.Min(request.GenerationSliceSeconds,
                        request.TotalBudgetSeconds - request.SaveReserveSeconds - Elapsed()));
                generationSeconds += seconds;
                if (added)
                {
                    generatedPatternsThisProcess++;
                    pool.Save();
                    master = new(instance, reference, pool, conflicts, trustRegion,
                        dependencies, initialPatternIds, request.EndpointOccupationSeconds,
                        request.RunningTimeSupplementRatio);
                }
                trace.Add(Row("master_infeasible_generation", reason, selected, station, added, generation: seconds));
                if (!added && !generationLedger.HasEligibleTask)
                {
                    finalStatus = "pattern_generation_tasks_exhausted_or_capped";
                    stopReason = $"no eligible station task remains under the 300-second/proven-domain limits:{reason}";
                    break;
                }
                continue;
            }

            lastUsableSelection = selected;

            Stopwatch buildClock = Stopwatch.StartNew();
            IReadOnlyList<MicroWitnessCertificate> selectedCertificates = selected.SelectedPatterns.Values
                .Select(pattern => pattern.Certificate).ToArray();
            string coordinationLog = Path.Combine(output, "logs", $"coordination_{iteration:000}.log");
            using FixedDiscreteContinuousCoordinator coordinator = FixedDiscreteContinuousCoordinator.Build(
                instance, reference, selectedCertificates,
                runningTimeSupplementRatio: request.RunningTimeSupplementRatio,
                endpointOccupationSeconds: request.EndpointOccupationSeconds,
                coordinationModelId: $"pattern-time-domain-{iteration}",
                feasibilityOnly: true,
                discreteState: selected.DiscreteState,
                precedenceTrustRegion: trustRegion);
            buildClock.Stop();
            buildProofSeconds += buildClock.Elapsed.TotalSeconds;
            lastBuildAndProofSeconds = buildClock.Elapsed.TotalSeconds;
            lastContinuousAudit = coordinator.Audit;
            File.WriteAllText(Path.Combine(output, "state", "latest_compact_compile_audit.json"),
                JsonSerializer.Serialize(new
                {
                    iteration,
                    elapsedSeconds = Elapsed(),
                    audit = coordinator.Audit
                }, JsonOptions), new UTF8Encoding(false));
            if (coordinator.Audit.RemainingIntegerCount != 0 ||
                coordinator.Model.Model.GetConstrs().Any(constraint => constraint.Get(GRB.StringAttr.ConstrName)
                    .StartsWith("extend_fix_", StringComparison.Ordinal)))
                throw new InvalidDataException("Full-line checker is not free-time/pure-continuous.");

            remaining = request.TotalBudgetSeconds - request.SaveReserveSeconds - Elapsed();
            if (remaining <= 0) break;
            ContinuousCoordinationResult coordination = coordinator.Solve(
                Math.Min(request.CoordinationSliceSeconds, remaining), coordinationLog,
                $"pattern-time-domain-{iteration}");
            coordinationChecks++;
            coordinationSeconds += coordination.RuntimeSeconds;

            if (coordination.HasSolution && coordination.Snapshot is not null)
            {
                Stopwatch validationClock = Stopwatch.StartNew();
                FixedPatternJointValidation validation =
                    MicroStructureProofBindingRecoveryRunner.ValidateWithFixedPatterns(
                        coordinator.Model, coordination.Snapshot, selectedCertificates,
                        request.EndpointOccupationSeconds);
                validationClock.Stop();
                validationSeconds += validationClock.Elapsed.TotalSeconds;
                finalValidation = validation;
                if (validation.JointFeasible && validation.Snapshot is not null)
                {
                    jointFeasible = true;
                    finalSnapshot = validation.Snapshot;
                    finalSelection = selected.SelectedPatterns;
                    firstJointFeasibleSeconds = Elapsed();
                    finalStatus = "joint_feasible";
                    stopReason = "independent Macro, Global Micro, fixed-resource and residual validation passed";
                    trace.Add(Row("joint_feasible", stopReason, selected,
                        validation: validationClock.Elapsed.TotalSeconds,
                        continuous: coordination.RuntimeSeconds));
                    break;
                }
                finalStatus = "continuous_solution_failed_joint_validation";
                stopReason = $"macro={validation.MacroViolationCount};micro={validation.GlobalMicroConflictCount};" +
                             $"unknown={validation.GlobalMicroUnknownCount};fixed={validation.FixedResourceValid}";
                trace.Add(Row("validation_failed", stopReason, selected,
                    validation: validationClock.Elapsed.TotalSeconds,
                    continuous: coordination.RuntimeSeconds));
                break;
            }

            if (coordination.StatusCode != GRB.Status.INFEASIBLE)
            {
                unknownChecks++;
                trace.Add(Row("coordination_unknown", coordination.StopReason, selected,
                    proofRows: coordination.ProofRows.Count, continuous: coordination.RuntimeSeconds));
                // Unknown is neither a conflict nor a permanent mode exclusion. Rotate to a
                // different generated alternative only if one is available; otherwise stop with
                // the true unknown stage rather than retrying an unchanged model.
                (string station, bool added, double seconds, string reason) = GenerateAlternative(
                    selected, null, null, generationPriority, generationLedger,
                    Math.Min(request.GenerationSliceSeconds,
                        request.TotalBudgetSeconds - request.SaveReserveSeconds - Elapsed()));
                generationSeconds += seconds;
                if (added)
                {
                    generatedPatternsThisProcess++;
                    pool.Save();
                    master = new(instance, reference, pool, conflicts, trustRegion,
                        dependencies, initialPatternIds, request.EndpointOccupationSeconds,
                        request.RunningTimeSupplementRatio);
                    trace.Add(Row("unknown_generation", reason, selected, station, true, generation: seconds));
                    continue;
                }
                finalStatus = "full_line_coordination_unknown";
                stopReason = coordination.StopReason;
                break;
            }

            Stopwatch proofClock = Stopwatch.StartNew();
            string? feedbackStation = null;
            string feedbackReason = "no_difference_cycle_feedback";
            foreach (string station in coordinator.CompactNegativeCyclePatternStations)
            {
                MasterSolutionSnapshot rejected = BuildPatternSourceInterface(reference, selected, station);
                FarkasBoundaryGenerationResult projected = coordinator.TryGenerateDifferenceFeedback(station, rejected);
                feedbackReason = projected.Reason;
                if (projected.Feedback is null || conditionalFeedback.Any(item => string.Equals(
                        item.FeedbackId, projected.Feedback.FeedbackId, StringComparison.Ordinal))) continue;
                conditionalFeedback.Add(projected.Feedback);
                ContinuousExtendabilityFeedbackStore.Save(conditionalFeedbackPath, conditionalFeedback);
                generatedTimeFeedback++;
                feedbackStation = station;
                break;
            }
            PatternCombinationConflict learned = BuildConflictFromProof(
                iteration, selected, coordination, coordinator.Model, dependencies);
            File.WriteAllText(Path.Combine(output, "state", "last_compile_proof.json"),
                JsonSerializer.Serialize(new
                {
                    iteration,
                    coordination.EvidenceBinding.CoordinationModelId,
                    coordination.EvidenceBinding.SolveResultIdentity,
                    proofRows = coordination.ProofRows,
                    learnedConflict = learned,
                    proofKinds = coordination.ProofRows.Select(row => row.ProofKind)
                        .Where(kind => !string.IsNullOrWhiteSpace(kind)).Distinct(StringComparer.Ordinal).ToArray(),
                    proofIdentities = coordination.ProofRows.Select(row => row.ProofIdentity)
                        .Where(identity => !string.IsNullOrWhiteSpace(identity)).Distinct(StringComparer.Ordinal).ToArray()
                }, JsonOptions), new UTF8Encoding(false));
            bool conflictAdded = conflicts.Add(learned);
            conflicts.Save(conflictPath);
            proofClock.Stop();
            buildProofSeconds += proofClock.Elapsed.TotalSeconds;
            lastBuildAndProofSeconds += proofClock.Elapsed.TotalSeconds;
            provenConflicts += conflictAdded ? 1 : 0;
            string? proofStation = feedbackStation ??
                SelectProofStation(selected.SelectedPatterns, coordination.ProofRows);
            (string generatedStation, bool patternAdded, double generatedSeconds, string generationReason) =
                GenerateAlternative(selected, proofStation, null, generationPriority, generationLedger,
                    Math.Min(request.GenerationSliceSeconds,
                        request.TotalBudgetSeconds - request.SaveReserveSeconds - Elapsed()));
            generationSeconds += generatedSeconds;
            if (patternAdded)
            {
                generatedPatternsThisProcess++;
                pool.Save();
            }
            if (conflictAdded || patternAdded)
                master = new(instance, reference, pool, conflicts, trustRegion,
                    dependencies, initialPatternIds, request.EndpointOccupationSeconds,
                    request.RunningTimeSupplementRatio);
            trace.Add(Row("proven_conflict_generation", generationReason, selected,
                generatedStation, patternAdded, coordination.ProofRows.Count,
                generation: generatedSeconds, continuous: coordination.RuntimeSeconds) with
            {
                Reason = $"{generationReason};difference_feedback={feedbackReason};" +
                         $"feedback_station={feedbackStation ?? "none"};" +
                         $"feedback_pool={conditionalFeedback.Count};" +
                         $"small_proof_patterns={learned.RequiredPatternsByStation.Count};" +
                         $"small_proof_literals={learned.RequiredDiscreteLiterals.Count};" +
                         $"proof_kinds={string.Join('|', coordination.ProofRows.Select(row => row.ProofKind).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal))}"
            });
            if (!conflictAdded && !patternAdded && !generationLedger.HasEligibleTask)
            {
                finalStatus = "no_new_mathematical_state";
                stopReason = "proven conflict already stored and no new locally valid pattern was generated";
                break;
            }
        }

        foreach (IndependentStationPatternSession session in generationSessions.Values) session.Dispose();
        generationSessions.Clear();
        SaveMilestoneIfNeeded(forceIfPast: true);
        if (!jointFeasible && finalStatus == "not_started")
        {
            finalStatus = Elapsed() >= request.TotalBudgetSeconds - request.SaveReserveSeconds
                ? "shared_budget_reserved_for_validation_and_save"
                : "terminated_without_joint_solution";
            stopReason = finalStatus;
        }
        pool.Save();
        conflicts.Save(conflictPath);
        generationLedger.Save(generationLedgerPath);
        string? solutionPath = null;
        string? assignmentsPath = null;
        bool readbackPassed = false;
        if (jointFeasible && finalSnapshot is not null && finalSelection is not null)
        {
            string solutionDirectory = Path.Combine(output, "joint_solution");
            Directory.CreateDirectory(solutionDirectory);
            solutionPath = Path.Combine(solutionDirectory, "joint_solution.snapshot.json");
            MasterSolutionSnapshotStore.Save(solutionPath, instance.Meta.InstanceName, finalSnapshot);
            MasterSolutionSnapshot readback = MasterSolutionSnapshotStore.Load(
                solutionPath, instance.Meta.InstanceName,
                instance.Trains.Select(train => train.TrainIndex).ToHashSet());
            readbackPassed = string.Equals(SnapshotFingerprint.Calculate(finalSnapshot),
                SnapshotFingerprint.Calculate(readback), StringComparison.Ordinal) &&
                readback.VerifiedMicroAssignments?.Count == instance.TrainStops.Count;
            if (!readbackPassed) throw new InvalidDataException("Saved joint solution failed read-only snapshot replay.");
            assignmentsPath = Path.Combine(solutionDirectory, "resource_assignments.csv");
            WriteAssignments(finalSnapshot.VerifiedMicroAssignments ?? [], assignmentsPath);
            File.WriteAllText(Path.Combine(solutionDirectory, "validation.json"),
                JsonSerializer.Serialize(finalValidation, JsonOptions), new UTF8Encoding(false));
        }
        else
        {
            File.WriteAllText(Path.Combine(output, "state", "diagnostic_state.json"),
                JsonSerializer.Serialize(new
                {
                    finalStatus, stopReason, iteration, poolCount = pool.Count,
                    conflictCount = conflicts.Count, coordinationChecks, provenConflicts, unknownChecks,
                    generatedTimeFeedback, appliedTimeFeedbackRows,
                    conditionalFeedbackCount = conditionalFeedback.Count,
                    generationTasks = generationLedger.States, lastMasterAudit, lastContinuousAudit
                }, JsonOptions), new UTF8Encoding(false));
        }
        wall.Stop();
        FlushPendingTrace();
        int generatedPatterns = pool.Patterns.Count(pattern => !initialPatternIds.Contains(pattern.PatternId));
        object summary = new
        {
            formatVersion = 1,
            runKind = "development-corrected continuation from fixed saved compact-full-Macro start; not a fresh Macro run",
            createdAt = DateTimeOffset.Now,
            codeIdentity = Assembly.GetExecutingAssembly().ManifestModule.ModuleVersionId.ToString(),
            input = new
            {
                dataset = Path.GetFullPath(request.DatasetPath), baseline, sourcePath, bundlePath,
                historicalFeedbackPath, macroFingerprint,
                baselinePatternIdentity = bundle.PatternCombinationIdentity,
                baselinePatternCount = bundle.Patterns.Length,
                baselineWitnessCount = bundle.Patterns.Sum(pattern => pattern.Witnesses.Count),
                historicalPatternGenerationTimeIncluded = false,
                importedPatternCount, importedConflictCount, discardedImpliedConflictCount
            },
            configuration = new
            {
                seed = 0, threads = 0, k = 4, kReference = "saved Macro candidate",
                nonHotspotChanges = 0, a = "existing boundary retained", b = "off",
                endpointOccupationSeconds = request.EndpointOccupationSeconds,
                runningTimeSupplementRatio = request.RunningTimeSupplementRatio,
                timeSemantics = "continuous; closed-window recount; zero business-time relaxation; numerical tolerance 1e-6 seconds",
                totalBudgetSeconds = request.TotalBudgetSeconds,
                milestoneSeconds = request.MilestoneSeconds,
                saveReserveSeconds = request.SaveReserveSeconds,
                previouslyConsumedSeconds = request.PreviouslyConsumedSeconds,
                masterSliceSeconds = request.MasterSliceSeconds,
                coordinationSliceSeconds = request.CoordinationSliceSeconds,
                generationSliceSeconds = request.GenerationSliceSeconds,
                generationTaskMaximumSeconds = request.GenerationTaskMaximumSeconds
            },
            mechanisms = new
            {
                stationPatternPool = true, multiStationPatternMaster = true,
                fullLineFreeTimeContinuousChecker = true,
                compactTimeCompilerUsed = lastContinuousAudit?.CompactCompilerUsed ?? false,
                compactContinuousVariables = lastContinuousAudit?.CompactVariableCount,
                compactContinuousConstraints = lastContinuousAudit?.CompactConstraintCount,
                differenceGraphNodes = lastContinuousAudit?.DifferenceGraphNodeCount,
                differenceGraphEdges = lastContinuousAudit?.DifferenceGraphEdgeCount,
                residualLpConstraints = lastContinuousAudit?.ResidualLpConstraintCount,
                conditionalTimeFeedbackGenerated = generatedTimeFeedback,
                conditionalTimeFeedbackPool = conditionalFeedback.Count,
                conditionalTimeFeedbackAppliedRows = appliedTimeFeedbackRows,
                evidenceGuidedAlternativeGeneration = generatedPatterns > 0,
                historicalFeedbackLoadedAsDiagnosticOnly = historicalFeedbackCount,
                historicalFeedbackHardReplayed = 0,
                sourceInterfaceTimeFixRows = 0,
                maintenanceAndQuotaCategoriesSelectedByMaster = true,
                selectedPatternsMayChangeAtMultipleStations = true
            },
            result = new
            {
                jointFeasible, finalStatus, stopReason, iteration, coordinationChecks,
                generatedPatterns, finalPoolSize = pool.Count, provenConflicts, unknownChecks,
                firstJointFeasibleSeconds = double.IsNaN(firstJointFeasibleSeconds) ? (double?)null : firstJointFeasibleSeconds,
                milestone3600Recorded = milestoneSaved, totalWallSeconds = Elapsed(),
                readbackPassed, assignmentCount = finalSnapshot?.VerifiedMicroAssignments?.Count ?? 0,
                lastMasterAudit, lastContinuousAudit, finalValidation
            },
            timings = new
            {
                masterSeconds, fullLineBuildAndProofSeconds = buildProofSeconds,
                fullLineCoordinationSeconds = coordinationSeconds,
                localPatternGenerationSeconds = generationSeconds,
                independentValidationSeconds = validationSeconds,
                totalWallSeconds = Elapsed(),
                thisProcessGeneratedPatterns = generatedPatternsThisProcess
            },
            paths = new
            {
                workDirectory = Directory.GetCurrentDirectory(), outputDirectory = output,
                result = Path.Combine(output, "result.md"),
                runSummary = Path.Combine(output, "run_summary.json"),
                trace = Path.Combine(output, "coordination_trace.csv"),
                reproduce = Path.Combine(output, "reproduce.ps1"),
                patternPool = poolDirectory, conflicts = conflictPath,
                solution = solutionPath, assignments = assignmentsPath,
                diagnostic = jointFeasible ? null : Path.Combine(output, "state", "diagnostic_state.json")
            }
        };
        File.WriteAllText(Path.Combine(output, "run_summary.json"),
            JsonSerializer.Serialize(summary, JsonOptions), new UTF8Encoding(false));
        WriteResult(output, request, jointFeasible, finalStatus, stopReason, Elapsed(),
            firstJointFeasibleSeconds, milestoneSaved, pool.Count, generatedPatterns, conflicts.Count,
            coordinationChecks, finalValidation, solutionPath, assignmentsPath);
        WriteReproduce(request, output);
        return jointFeasible ? 0 : 2;

        void SaveMilestoneIfNeeded(bool forceIfPast = false)
        {
            if (milestoneSaved || Elapsed() < request.MilestoneSeconds) return;
            milestoneSaved = true;
            File.WriteAllText(Path.Combine(output, "state", "milestone_3600.json"),
                JsonSerializer.Serialize(new
                {
                    recordedAtElapsedSeconds = Elapsed(),
                    iteration, poolCount = pool.Count, conflictCount = conflicts.Count,
                    coordinationChecks, provenConflicts, unknownChecks,
                    generatedTimeFeedback, appliedTimeFeedbackRows,
                    conditionalFeedbackCount = conditionalFeedback.Count,
                    generatedPatterns = TotalGeneratedPatterns(),
                    generationTasks = generationLedger.States,
                    status = finalStatus, reason = stopReason,
                    note = "in-process snapshot; cumulative clock and state were not reset"
                }, JsonOptions), new UTF8Encoding(false));
            pool.Save();
            conflicts.Save(conflictPath);
            ContinuousExtendabilityFeedbackStore.Save(conditionalFeedbackPath, conditionalFeedback);
            generationLedger.Save(generationLedgerPath);
        }

        int TotalGeneratedPatterns() => pool.Patterns.Count(pattern => !initialPatternIds.Contains(pattern.PatternId));

        void FlushPendingTrace()
        {
            if (trace.Count == 0) return;
            AppendTrace(trace, tracePath);
            trace.Clear();
        }

        MicroPatternTimeDomainTraceRow Row(
            string phase, string reason, MicroPatternMasterResult masterResult,
            string station = "", bool patternAdded = false, int proofRows = 0,
            double generation = 0, double continuous = 0, double validation = 0) => new(
                iteration, phase, masterResult.Status, pool.Count,
                string.Join('|', masterResult.SelectedPatterns.OrderBy(item => item.Key,
                    StringComparer.OrdinalIgnoreCase).Select(item => $"{item.Key}={item.Value.PatternId}")),
                masterResult.Audit.PatternVariableCount + masterResult.Audit.EndpointVariableCount +
                masterResult.Audit.MaintenanceVariableCount + masterResult.Audit.QuotaCategoryVariableCount,
                masterResult.Audit.ConstraintCount,
                lastContinuousAudit?.ContinuousVariableCount ?? 0,
                lastContinuousAudit?.RemainingIntegerCount ?? 0,
                lastContinuousAudit?.SourceConstraintCount ?? 0,
                lastContinuousAudit?.PatternConstraintCount ?? 0,
                proofRows, conflicts.Count, station, patternAdded,
                masterResult.RuntimeSeconds, buildClockValue(), continuous, generation, validation,
                Elapsed(), reason);

        double buildClockValue() => lastBuildAndProofSeconds;

        (string Station, bool Added, double Seconds, string Reason) GenerateAlternative(
            MicroPatternMasterResult? selection,
            string? suggestedStation,
            string? suggestedStopId,
            IReadOnlyList<string> priority,
            PatternGenerationTaskLedger ledger,
            double timeLimit)
        {
            if (timeLimit <= 0) return (suggestedStation ?? "", false, 0, "no shared time remains for generation");
            string? station = ledger.SelectNext(suggestedStation, priority,
                preferEvidenceRegardlessOfFairness: !string.IsNullOrWhiteSpace(suggestedStopId));
            if (station is null)
                return ("", false, 0, "every station task is proven exhausted or reached its cumulative 300-second cap");
            double slice = Math.Min(timeLimit,
                Math.Min(request.GenerationSliceSeconds, ledger.RemainingSeconds(station)));
            if (slice <= 0) return (station, false, 0, "selected station task has no remaining generation allowance");
            Stopwatch generationClock = Stopwatch.StartNew();
            string log = Path.Combine(output, "logs", $"generation_{iteration:000}_{Sanitize(station)}.log");
            try
            {
                if (!string.IsNullOrWhiteSpace(suggestedStopId) &&
                    (!instance.StopsById.TryGetValue(suggestedStopId, out TrainStopRecord? targetRecord) ||
                     !string.Equals(targetRecord.StationName, station, StringComparison.OrdinalIgnoreCase)))
                    suggestedStopId = null;
                ContinuousExtendabilityContext? feedbackContext = null;
                ContinuousExtendabilityFeedback[] applicableFeedback = [];
                if (selection?.DiscreteState is not null)
                    applicableFeedback = SelectApplicableFeedback(
                        instance, selection, station, conditionalFeedback, out feedbackContext);
                string sessionKey = feedbackContext is null
                    ? $"{station}|unconditioned|proof-stop={suggestedStopId ?? "none"}"
                    : $"{station}|{feedbackContext.MathematicalModelIdentity}|proof-stop={suggestedStopId ?? "none"}";
                if (!generationSessions.TryGetValue(sessionKey, out IndependentStationPatternSession? session))
                {
                    session = IndependentStationPatternSession.Open(
                        instance, reference, [station], request.EndpointOccupationSeconds, log,
                        request.RunningTimeSupplementRatio,
                        allowFlexibleStopDecisions: true);
                    session.ExcludeExistingResourcePatterns(pool.GetPatterns(station));
                    if (!string.IsNullOrWhiteSpace(suggestedStopId))
                        session.RequireProofSupportedStop(suggestedStopId, 1);
                    generationSessions.Add(sessionKey, session);
                }
                LocalExtendabilityFeedbackApplication? feedbackApplication = feedbackContext is null
                    ? null
                    : session.ApplyExtendabilityFeedback(feedbackContext, applicableFeedback);
                appliedTimeFeedbackRows += feedbackApplication?.AddedConstraintIds.Count ?? 0;
                int diversityRows = pool.GetPatterns(station).Count;
                IndependentStationPatternResult generated = session.Continue(slice);
                generationClock.Stop();
                if (!generated.HasPattern || generated.Patterns.Count != 1)
                {
                    PatternGenerationTaskOutcome outcome = generated.StatusCode == GRB.Status.INFEASIBLE
                        ? PatternGenerationTaskOutcome.ProvenExhausted
                        : generated.CandidateSnapshot is not null
                            ? PatternGenerationTaskOutcome.EngineeringUnavailable
                            : PatternGenerationTaskOutcome.Unknown;
                    string? retainedCandidate = null;
                    if (generated.CandidateSnapshot is not null)
                    {
                        string rejectedDirectory = Path.Combine(output, "state", "rejected_exports");
                        Directory.CreateDirectory(rejectedDirectory);
                        retainedCandidate = Path.Combine(rejectedDirectory,
                            $"iteration_{iteration:000}_{Sanitize(station)}.snapshot.json");
                        MasterSolutionSnapshotStore.Save(retainedCandidate, instance.Meta.InstanceName,
                            generated.CandidateSnapshot);
                        File.WriteAllText(Path.Combine(rejectedDirectory,
                                $"iteration_{iteration:000}_{Sanitize(station)}.witnesses.json"),
                            JsonSerializer.Serialize(generated.Witnesses, JsonOptions), new UTF8Encoding(false));
                    }
                    string noPatternReason = $"{generated.StopReason};diversity_rows={diversityRows};" +
                                             $"proof_target_stop={suggestedStopId ?? "none"};" +
                                             $"time_feedback_eligible={feedbackApplication?.EligibleFeedbackIds.Count ?? 0};" +
                                             $"time_feedback_added={feedbackApplication?.AddedConstraintIds.Count ?? 0};" +
                                             $"failed_assignment_retained={retainedCandidate ?? "none"};" +
                                             $"task_consumed_before={ledger.States.Single(item => string.Equals(item.StationName, station, StringComparison.OrdinalIgnoreCase)).ConsumedSeconds:R}";
                    ledger.Record(station, generationClock.Elapsed.TotalSeconds, outcome, noPatternReason);
                    ledger.Save(generationLedgerPath);
                    if (outcome is PatternGenerationTaskOutcome.ProvenExhausted or
                        PatternGenerationTaskOutcome.EngineeringUnavailable)
                    {
                        session.Dispose();
                        generationSessions.Remove(sessionKey);
                    }
                    return (station, false, generationClock.Elapsed.TotalSeconds, noPatternReason);
                }
                StationPattern pattern = StationPattern.Create(generated.Patterns[0]);
                bool added = pool.Add(pattern);
                PatternGenerationTaskOutcome addedOutcome = added
                    ? PatternGenerationTaskOutcome.PatternAdded
                    : PatternGenerationTaskOutcome.DuplicatePattern;
                string patternReason = added
                    ? $"new pattern {pattern.PatternId};diversity_rows={diversityRows};" +
                      $"proof_target_stop={suggestedStopId ?? "none"};" +
                      $"time_feedback_eligible={feedbackApplication?.EligibleFeedbackIds.Count ?? 0};" +
                      $"time_feedback_added={feedbackApplication?.AddedConstraintIds.Count ?? 0}"
                    : $"duplicate canonical pattern {pattern.PatternId};diversity_rows={diversityRows};" +
                      $"proof_target_stop={suggestedStopId ?? "none"};" +
                      $"time_feedback_eligible={feedbackApplication?.EligibleFeedbackIds.Count ?? 0};" +
                      $"time_feedback_added={feedbackApplication?.AddedConstraintIds.Count ?? 0}";
                ledger.Record(station, generationClock.Elapsed.TotalSeconds, addedOutcome, patternReason);
                ledger.Save(generationLedgerPath);
                if (added)
                {
                    // Make the retained Gurobi session search a genuinely different resource/stop
                    // structure on its next slice while preserving its accumulated solver state.
                    session.ExcludeExistingResourcePatterns([pattern]);
                }
                return (station, added, generationClock.Elapsed.TotalSeconds, patternReason);
            }
            catch (InvalidOperationException exception) when (
                exception.Message.Contains("empty alternative domain", StringComparison.OrdinalIgnoreCase))
            {
                generationClock.Stop();
                File.WriteAllText(Path.Combine(output, "logs",
                        $"generation_{iteration:000}_{Sanitize(station)}_empty_domain.txt"),
                    exception.ToString(), new UTF8Encoding(false));
                string reason = $"generation_domain_proven_empty:{exception.Message}";
                ledger.Record(station, generationClock.Elapsed.TotalSeconds,
                    PatternGenerationTaskOutcome.ProvenExhausted, reason);
                ledger.Save(generationLedgerPath);
                string prefix = $"{station}|";
                string[] matchingKeys = generationSessions.Keys.Where(key => key.StartsWith(
                    prefix, StringComparison.OrdinalIgnoreCase)).ToArray();
                foreach (string key in matchingKeys)
                if (generationSessions.Remove(key, out IndependentStationPatternSession? session))
                    session.Dispose();
                return (station, false, generationClock.Elapsed.TotalSeconds,
                    reason);
            }
        }
    }

    internal static bool IsRecoverableMasterUnknownStatus(int statusCode) =>
        statusCode is GRB.Status.TIME_LIMIT or GRB.Status.INTERRUPTED;

    internal static bool HasSavedMilestone(string outputDirectory) =>
        File.Exists(Path.Combine(outputDirectory, "state", "milestone_3600.json"));

    private static MasterSolutionSnapshot BuildPatternSourceInterface(
        MasterSolutionSnapshot reference,
        MicroPatternMasterResult selection,
        string activeStation)
    {
        if (selection.DiscreteState is null)
            throw new InvalidDataException("A selected pattern combination lacks its complete discrete state.");
        if (!selection.SelectedPatterns.TryGetValue(activeStation, out StationPattern? pattern))
            throw new InvalidDataException($"Selected combination has no pattern for station '{activeStation}'.");
        Dictionary<string, double> arrivals = new(reference.ArrivalSecondsByStopId,
            StringComparer.OrdinalIgnoreCase);
        Dictionary<string, double> departures = new(reference.DepartureSecondsByStopId,
            StringComparer.OrdinalIgnoreCase);
        foreach (VerifiedMicroAssignmentWitness witness in pattern.Certificate.Witnesses)
        {
            if (witness.ArrivalSeconds.HasValue)
                arrivals[witness.RequirementId] = witness.ArrivalSeconds.Value;
            if (witness.DepartureSeconds.HasValue)
                departures[witness.RequirementId] = witness.DepartureSeconds.Value;
        }
        return reference with
        {
            ArrivalSecondsByStopId = arrivals,
            DepartureSecondsByStopId = departures,
            StopByStopId = new Dictionary<string, int>(selection.DiscreteState.StopByStopId,
                StringComparer.OrdinalIgnoreCase),
            SectionPrecedenceByKey = new Dictionary<string, int>(
                selection.DiscreteState.SectionPrecedenceByKey, StringComparer.OrdinalIgnoreCase),
            StationEventPrecedenceByKey = new Dictionary<string, int>(
                selection.DiscreteState.StationEventPrecedenceByKey, StringComparer.OrdinalIgnoreCase),
            VerifiedMicroAssignments = null
        };
    }

    private static ContinuousExtendabilityFeedback[] SelectApplicableFeedback(
        TimetablingInstance instance,
        MicroPatternMasterResult selection,
        string activeStation,
        IReadOnlyList<ContinuousExtendabilityFeedback> feedback,
        out ContinuousExtendabilityContext? context)
    {
        context = null;
        if (selection.DiscreteState is null || feedback.Count == 0) return [];
        string[] activeStations = [activeStation];
        HashSet<string> activeStopIds = instance.TrainStops.Where(stop => string.Equals(
                stop.StationName, activeStation, StringComparison.OrdinalIgnoreCase))
            .Select(stop => stop.StopId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> activeStops = selection.DiscreteState.StopByStopId
            .Where(item => activeStopIds.Contains(item.Key))
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
        string macroIdentity = FixedDiscreteContinuousCoordinator.CalculateRetainedMacroDiscreteIdentity(
            instance, selection.DiscreteState, activeStations);
        string outsidePatternIdentity = MicroPatternCombinationIdentity.Calculate(
            selection.SelectedPatterns.Where(item => !string.Equals(item.Key, activeStation,
                    StringComparison.OrdinalIgnoreCase))
                .Select(item => item.Value.Certificate).ToArray());

        var eligibleGroups = feedback.Where(item => item.HasValidIdentity() &&
                item.Context.ActiveStations.Count == 1 && string.Equals(
                    item.Context.ActiveStations[0], activeStation, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Context.MacroDiscreteIdentity, macroIdentity, StringComparison.Ordinal) &&
                string.Equals(item.Context.OutsidePatternIdentity, outsidePatternIdentity, StringComparison.Ordinal))
            .GroupBy(item => item.Context.MathematicalModelIdentity, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.Ordinal)
            .FirstOrDefault();
        if (eligibleGroups is null) return [];
        ContinuousExtendabilityContext currentContext = ContinuousExtendabilityContext.Create(
            eligibleGroups.Key, macroIdentity, outsidePatternIdentity, activeStations, activeStops);
        context = currentContext;
        return eligibleGroups.Where(item => item.AssessApplicability(currentContext).ModelEligible).ToArray();
    }

    private static PatternTimeDomain CompileSelectedPatternTimeDomain(
        TimetablingInstance instance,
        IEnumerable<StationPattern> patterns)
    {
        List<MicroPatternTemporalCondition> conditions = [];
        foreach (StationPattern pattern in patterns)
        foreach (MicroCertificateCondition condition in pattern.Certificate.Conditions
                     .Where(condition => condition.Kind != MicroCertificateConditionKind.StopMode))
        {
            if (condition.EarlierStopId is null || condition.EarlierEvent is null)
                throw new InvalidDataException($"Temporal condition '{condition.ConditionId}' lacks its earlier event.");
            conditions.Add(new(condition.ConditionId, pattern.StationName,
                condition.EarlierStopId, condition.EarlierEvent.Value, condition.EarlierOffsetSeconds,
                condition.LaterStopId, condition.LaterEvent, condition.LaterOffsetSeconds,
                condition.MinimumDifferenceSeconds, condition.SourceRuleId));
        }
        Dictionary<PatternTimeEventKey, PatternTimeBounds> bounds = new();
        foreach (TrainStopRecord stop in instance.TrainStops)
        {
            bounds[new(stop.StopId, StationEventSide.Incoming)] =
                new(stop.EarliestArrSeconds, stop.LatestArrSeconds);
            bounds[new(stop.StopId, StationEventSide.Outgoing)] =
                new(stop.EarliestDepSeconds, stop.LatestDepSeconds);
        }
        // The graph is a certified precheck over the saved difference rows.  Full Macro runtime,
        // quota, maintenance, stop-product and any remaining rows are always checked by the LP.
        return PatternTimeDomainCompiler.Compile(conditions, bounds, requiresResidualLp: true);
    }

    private static PatternCombinationConflict BuildConflictFromProof(
        int iteration,
        MicroPatternMasterResult selection,
        ContinuousCoordinationResult coordination,
        BasicMilpModel model,
        IReadOnlyDictionary<string, string> dependencies)
    {
        HashSet<string> proofNames = coordination.ProofRows.Select(row => row.ConstraintName)
            .ToHashSet(StringComparer.Ordinal);
        Dictionary<string, string> patterns = new(StringComparer.OrdinalIgnoreCase);
        foreach (ContinuousProofRow row in coordination.ProofRows)
        foreach ((string station, string patternId) in row.RequiredPatternsByStation ??
                 new Dictionary<string, string>())
            patterns[station] = patternId;
        foreach ((string station, StationPattern pattern) in selection.SelectedPatterns)
        {
            bool used = pattern.Certificate.Conditions.Select((_, index) =>
                    MicroCertificateModelBinder.BuildConstraintName(pattern.Certificate.CertificateId, index))
                .Any(proofNames.Contains);
            if (used) patterns[station] = pattern.PatternId;
        }
        Dictionary<string, int> literals = new(StringComparer.OrdinalIgnoreCase);
        foreach (ContinuousProofRow row in coordination.ProofRows)
        foreach ((string key, int value) in row.RequiredDiscreteLiterals ??
                 new Dictionary<string, int>())
            literals[key] = value;
        CompleteMacroDiscreteState state = selection.DiscreteState!;
        foreach (MaintenanceWindowBinaryBinding binding in model.MaintenanceWindowBinaryBindings)
            if (HasFixedProof(binding.BeforeWindowVariable))
                literals[$"maintenance:{binding.BeforeWindowVariable.Get(GRB.StringAttr.VarName)}"] =
                    state.MaintenanceByVariableName[binding.BeforeWindowVariable.Get(GRB.StringAttr.VarName)];
        foreach (StationQuotaCategoryBinding binding in model.StationQuotaCategoryBindings)
        foreach (GRBVar variable in new[] { binding.InWindowVariable, binding.LeftOutsideVariable, binding.RightOutsideVariable })
            if (HasFixedProof(variable))
                literals[$"quota:{variable.Get(GRB.StringAttr.VarName)}"] =
                    state.QuotaCategoryByVariableName[variable.Get(GRB.StringAttr.VarName)];
        foreach ((string key, GRBVar variable) in model.SectionPrecedenceVars)
            if (HasFixedProof(variable) && state.SectionPrecedenceByKey.TryGetValue(key, out int value))
                literals[$"section:{key}"] = value;
        foreach ((string stopId, GRBVar variable) in model.StopVars)
            if (HasFixedProof(variable))
            {
                string station = model.Instance.TrainStops.First(stop => string.Equals(
                    stop.StopId, stopId, StringComparison.OrdinalIgnoreCase)).StationName;
                patterns[station] = selection.SelectedPatterns[station].PatternId;
            }
        // A mapped proof subset is preferred.  If the Farkas rows were numerically truncated
        // before exposing a fixed row, exclude only this complete discrete assignment.  This is
        // still valid because the free-time LP proved that exact assignment infeasible.
        if (patterns.Count == 0 && literals.Count == 0)
        {
            foreach ((string station, StationPattern pattern) in selection.SelectedPatterns)
                patterns[station] = pattern.PatternId;
            foreach ((string name, int value) in state.MaintenanceByVariableName)
                literals[$"maintenance:{name}"] = value;
            foreach ((string name, int value) in state.QuotaCategoryByVariableName)
                literals[$"quota:{name}"] = value;
            foreach (string key in model.AppliedPrecedenceTrustRegion?.HotspotKeys.AsEnumerable() ?? Enumerable.Empty<string>())
                if (state.SectionPrecedenceByKey.TryGetValue(key, out int value)) literals[$"section:{key}"] = value;
        }
        return PatternCombinationConflict.Create(
            $"{coordination.EvidenceBinding.SolveResultIdentity}:iteration={iteration}",
            patterns, literals, dependencies);

        bool HasFixedProof(GRBVar variable)
        {
            string token = Sanitize(variable.Get(GRB.StringAttr.VarName));
            return proofNames.Any(name => name.StartsWith("fd_fix_", StringComparison.Ordinal) &&
                                          name.EndsWith(token, StringComparison.Ordinal));
        }
    }

    private static string? SelectProofStation(
        IReadOnlyDictionary<string, StationPattern> selected,
        IReadOnlyList<ContinuousProofRow> proofRows)
    {
        string? guarded = proofRows.SelectMany(row =>
                (row.RequiredPatternsByStation ?? new Dictionary<string, string>()).Keys)
            .GroupBy(station => station, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Key).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(guarded)) return guarded;
        HashSet<string> names = proofRows.Select(row => row.ConstraintName).ToHashSet(StringComparer.Ordinal);
        return selected.Values
            .Select(pattern => new
            {
                pattern.StationName,
                Hits = pattern.Certificate.Conditions.Select((_, index) =>
                        MicroCertificateModelBinder.BuildConstraintName(pattern.Certificate.CertificateId, index))
                    .Count(names.Contains)
            })
            .Where(item => item.Hits > 0)
            .OrderByDescending(item => item.Hits)
            .ThenBy(item => item.StationName, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.StationName).FirstOrDefault();
    }

    private static string[] BuildGenerationPriority(TimetablingInstance instance)
    {
        string[] evidence = ["站4945", "站4943", "站4949"];
        return evidence.Where(station => instance.Stations.Contains(station, StringComparer.OrdinalIgnoreCase))
            .Concat(instance.Stations.Order(StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IReadOnlyDictionary<string, string> BuildDependencies(
        MicroPatternReconstructionBundle bundle,
        MasterSolutionSnapshot reference,
        PrecedenceTrustRegion trustRegion) => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["instance"] = bundle.InstanceName,
        ["data"] = bundle.DataVersion,
        ["rules"] = bundle.RuleVersion,
        ["endpoint"] = bundle.EndpointOccupationSeconds.ToString(CultureInfo.InvariantCulture),
        ["running-ratio"] = bundle.RunningTimeSupplementRatio.ToString("R", CultureInfo.InvariantCulture),
        ["fixed-station-precedence"] = StationPattern.Hash(string.Join('|', reference.StationEventPrecedenceByKey
            .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase).Select(item => $"{item.Key}={item.Value}"))),
        ["fixed-nonhotspot-section-precedence"] = StationPattern.Hash(string.Join('|',
            reference.SectionPrecedenceByKey.Where(item => !trustRegion.HotspotKeys.Contains(item.Key))
                .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase).Select(item => $"{item.Key}={item.Value}")))
    };

    private static (string Source, string Bundle, string? Feedback) ResolveInputs(string summaryPath, string baseline)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(summaryPath, Encoding.UTF8));
        JsonElement input = document.RootElement.GetProperty("input");
        string source = input.GetProperty("sourcePath").GetString()
                        ?? throw new InvalidDataException("Baseline sourcePath is empty.");
        string bundle = Path.Combine(baseline, "state", "micro_patterns.json");
        if (!File.Exists(bundle))
            bundle = input.GetProperty("bundlePath").GetString()
                     ?? throw new InvalidDataException("Baseline bundlePath is empty.");
        string? feedback = input.TryGetProperty("historicalFeedbackPath", out JsonElement element)
            ? element.GetString()
            : null;
        return (Path.GetFullPath(source), Path.GetFullPath(bundle),
            string.IsNullOrWhiteSpace(feedback) ? null : Path.GetFullPath(feedback));
    }

    private static int CountHistoricalFeedback(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return 0;
        try { return ContinuousExtendabilityFeedbackStore.Load(path).Count; }
        catch { return 0; }
    }

    private static int ReadLastTraceIteration(string path)
    {
        int maximum = 0;
        if (File.Exists(path))
        {
            foreach (string line in File.ReadLines(path, Encoding.UTF8).Skip(1))
            {
                int separator = line.IndexOf(',');
                string token = separator < 0 ? line : line[..separator];
                if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                    maximum = Math.Max(maximum, value);
            }
        }
        string? output = Path.GetDirectoryName(Path.GetFullPath(path));
        string logs = Path.Combine(output ?? string.Empty, "logs");
        if (Directory.Exists(logs))
            foreach (string masterLog in Directory.GetFiles(logs, "master_*.log"))
            {
                Match match = Regex.Match(Path.GetFileName(masterLog), @"^master_(\d+)\.log$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out int value))
                    maximum = Math.Max(maximum, value);
            }
        return maximum;
    }

    private static PatternGenerationTaskLedger RecoverGenerationLedger(
        string output,
        IReadOnlyList<string> stations,
        double maximumSecondsPerTask)
    {
        PatternGenerationTaskLedger ledger = PatternGenerationTaskLedger.Create(stations, maximumSecondsPerTask);
        string logs = Path.Combine(output, "logs");
        if (!Directory.Exists(logs)) return ledger;
        foreach (string station in stations)
        {
            string suffix = $"_{Sanitize(station)}.log";
            string[] matching = Directory.GetFiles(logs, "generation_*.log")
                .Where(path => Path.GetFileName(path).EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.OrdinalIgnoreCase).ToArray();
            if (matching.Length == 0) continue;
            double consumed = 0;
            bool provenExhausted = false;
            bool foundCandidate = false;
            foreach (string path in matching)
            {
                string text = File.ReadAllText(path, Encoding.UTF8);
                double fileSeconds = Regex.Matches(text, @"in\s+([0-9]+(?:\.[0-9]+)?)\s+seconds",
                        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)
                    .Select(match => double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture))
                    .DefaultIfEmpty(0).Max();
                consumed += fileSeconds;
                provenExhausted |= text.Contains("Model is infeasible", StringComparison.OrdinalIgnoreCase) &&
                                   text.Contains("Solution count 0", StringComparison.OrdinalIgnoreCase);
                foundCandidate |= Regex.IsMatch(text, @"Solution count\s+[1-9]", RegexOptions.IgnoreCase);
            }
            PatternGenerationTaskOutcome outcome = provenExhausted
                ? PatternGenerationTaskOutcome.ProvenExhausted
                : foundCandidate
                    ? PatternGenerationTaskOutcome.PatternAdded
                    : PatternGenerationTaskOutcome.Unknown;
            ledger.Record(station, consumed, outcome,
                $"recovered_from_{matching.Length}_existing_generation_log(s)");
        }
        return ledger;
    }

    private static void WriteAssignments(IReadOnlyList<VerifiedMicroAssignmentWitness> witnesses, string path)
    {
        using StreamWriter writer = new(path, false, new UTF8Encoding(true));
        writer.WriteLine("station,requirement_id,train_index,is_stop,track,incoming_route,outgoing_route,arrival_seconds,departure_seconds,candidate_fingerprint");
        foreach (VerifiedMicroAssignmentWitness item in witnesses.OrderBy(item => item.StationName,
                     StringComparer.OrdinalIgnoreCase).ThenBy(item => item.RequirementId, StringComparer.OrdinalIgnoreCase))
            writer.WriteLine(string.Join(',', Csv(item.StationName), Csv(item.RequirementId), item.TrainIndex,
                item.IsStop, Csv(item.TrackName), item.IncomingRouteId, item.OutgoingRouteId,
                item.ArrivalSeconds?.ToString("R", CultureInfo.InvariantCulture),
                item.DepartureSeconds?.ToString("R", CultureInfo.InvariantCulture),
                item.CandidateSnapshotFingerprint));
    }

    private static void AppendTrace(IReadOnlyList<MicroPatternTimeDomainTraceRow> rows, string path)
    {
        bool writeHeader = !File.Exists(path) || new FileInfo(path).Length == 0;
        using StreamWriter writer = new(path, append: true, new UTF8Encoding(writeHeader));
        if (writeHeader)
            writer.WriteLine("iteration,phase,status,pool_size,selected_patterns,master_binaries,master_constraints,continuous_variables,continuous_binaries,continuous_constraints,pattern_constraints,proof_rows,conflicts,generated_station,pattern_added,master_seconds,build_proof_seconds,coordination_seconds,generation_seconds,validation_seconds,elapsed_seconds,reason");
        foreach (MicroPatternTimeDomainTraceRow row in rows)
            writer.WriteLine(string.Join(',', row.Iteration, row.Phase, row.Status, row.PoolSize,
                Csv(row.SelectedPatterns), row.MasterBinaries, row.MasterConstraints,
                row.ContinuousVariables, row.ContinuousBinaries, row.ContinuousConstraints,
                row.PatternConstraintCount, row.ProofRowCount, row.ConflictCount,
                Csv(row.GeneratedStation), row.PatternAdded,
                F(row.MasterSeconds), F(row.BuildAndProofSeconds), F(row.CoordinationSeconds),
                F(row.GenerationSeconds), F(row.ValidationSeconds), F(row.ElapsedSeconds), Csv(row.Reason)));
    }

    private static void WriteResult(
        string output, MicroPatternTimeDomainRequest request, bool joint, string status, string reason,
        double total, double firstJoint, bool milestone, int poolCount, int generated, int conflicts,
        int checks, FixedPatternJointValidation? validation, string? solution, string? assignments)
    {
        StringBuilder text = new();
        text.AppendLine("# JZ Micro 多模式—自由时刻协调实验");
        text.AppendLine();
        text.AppendLine($"- 完整联合可行：`{joint}`");
        text.AppendLine($"- 最终状态：`{status}`");
        text.AppendLine($"- 终止原因：`{reason}`");
        text.AppendLine($"- 首次联合可行时间（秒）：`{(double.IsNaN(firstJoint) ? "未记录" : F(firstJoint))}`");
        text.AppendLine($"- 3600 秒中间状态已记录：`{milestone}`（不中断、不重启、不重置累计时间）");
        text.AppendLine($"- 实际总墙钟（秒）：`{F(total)}`；共享上限：`{F(request.TotalBudgetSeconds)}`；保存预留：`{F(request.SaveReserveSeconds)}`");
        text.AppendLine($"- 模式池：`{poolCount}`（本轮新增 `{generated}`）；条件冲突：`{conflicts}`；全线检查：`{checks}`");
        text.AppendLine();
        text.AppendLine("## 实际机制");
        text.AppendLine();
        text.AppendLine("- 每站模式池保留不同资源/停站/进路离散模式；模式 ID 不含来源时刻或完整模型 GUID。");
        text.AppendLine("- 主问题同时选择 12 站模式、维护窗分支、闭区间站级配额时间类别及原 K=4 合法端点变化。");
        text.AppendLine("- 全线 checker 固定完整离散状态后仅含连续变量，所有到发时刻在原窗口内自由；未使用 `extend_fix_*`。");
        text.AppendLine("- 只有自由时刻模型 `INFEASIBLE` 才保存条件冲突；TIME_LIMIT/INTERRUPTED 保持 Unknown。");
        text.AppendLine("- 历史固定点反馈只作诊断；未删除其保护条件，也未作为无条件 hard cut 重放。");
        text.AppendLine("- 替代模式使用现有单站业务 MIP 与资源差异行生成，多个站的替代项在同一个主问题中可同时选择；未重建庞大两站联合 MIP。");
        text.AppendLine();
        text.AppendLine("## 独立验证");
        text.AppendLine();
        if (validation is null) text.AppendLine("- 未产生可供联合验证的全线连续候选。");
        else
        {
            text.AppendLine($"- Macro 违规：`{validation.MacroViolationCount}`");
            text.AppendLine($"- Global Micro 冲突/Unknown：`{validation.GlobalMicroConflictCount}/{validation.GlobalMicroUnknownCount}`");
            text.AppendLine($"- 固定资源验证：`{validation.FixedResourceValid}`；资源分配：`{validation.FixedResourceWitnessCount}`");
            text.AppendLine($"- 联合接纳：`{validation.JointFeasible}`");
        }
        text.AppendLine();
        text.AppendLine("## 路径");
        text.AppendLine();
        text.AppendLine($"- 输出目录：`{output}`");
        text.AppendLine($"- 解：`{solution ?? "未生成"}`");
        text.AppendLine($"- 资源分配：`{assignments ?? "未生成"}`");
        File.WriteAllText(Path.Combine(output, "result.md"), text.ToString(), new UTF8Encoding(true));
    }

    private static void WriteReproduce(MicroPatternTimeDomainRequest request, string output)
    {
        string project = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(),
            "src", "GurobiTimetablingResearch", "GurobiTimetablingResearch.csproj"));
        string command = "$ErrorActionPreference = 'Stop'\r\n" +
                         $"dotnet run --no-build -c Release --project '{project.Replace("'", "''")}' -- " +
                         $"'{Path.GetFullPath(request.DatasetPath).Replace("'", "''")}' --micro-pattern-time-domain " +
                         $"--micro-pattern-baseline='{Path.GetFullPath(request.BaselineDirectory).Replace("'", "''")}' " +
                         $"--micro-pattern-output='{output.Replace("'", "''")}' " +
                         $"--micro-pattern-budget-seconds={F(request.TotalBudgetSeconds)} " +
                         $"--micro-pattern-milestone-seconds={F(request.MilestoneSeconds)} " +
                         $"--micro-pattern-previously-consumed-seconds={F(request.PreviouslyConsumedSeconds)}\r\n";
        File.WriteAllText(Path.Combine(output, "reproduce.ps1"), command, new UTF8Encoding(true));
    }

    private static string F(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static string Csv(object? value)
    {
        string text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        return text.IndexOfAny([',', '"', '\r', '\n']) < 0 ? text : $"\"{text.Replace("\"", "\"\"")}\"";
    }
    private static string Sanitize(string value) => new(value.Select(character =>
        char.IsLetterOrDigit(character) ? character : '_').Take(90).ToArray());
}
