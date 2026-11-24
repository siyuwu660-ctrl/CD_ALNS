using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Gurobi;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

internal sealed record JointFeasibleBaselineVerificationResult(
    bool Passed,
    string OutputDirectory,
    string CandidateFingerprint,
    int AcceptedIteration,
    int MacroViolationCount,
    int GlobalMicroConflictCount,
    int GlobalMicroUnknownCount,
    int SavedAssignmentCount,
    int ValidatedSavedAssignmentCount,
    int ValidatedStationCount,
    int FreshAssignmentDifferenceCount,
    int PersistentComponentCount,
    int PersistentRequirementCount,
    int PermanentFeedbackCount,
    double GlobalSpanSeconds,
    double? GlobalUpperBound,
    double? GlobalLowerBound,
    double? GlobalGap,
    double RuntimeSeconds,
    string ConfirmedCheckpointPath,
    IReadOnlyList<string> Failures);

/// <summary>
/// 对已保存的联合可行 checkpoint 做只读回读复验，并把原见证、重新搜索结果和阶段 3 构造检查分开记录。
/// 该流程不修改时刻和资源选择，不执行跨度优化，也不会把待改善状态写回已验证基准。
/// </summary>
internal static class JointFeasibleBaselineVerifier
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    internal static JointFeasibleBaselineVerificationResult Run(
        TimetablingInstance instance,
        string repositoryRoot,
        string cleanCheckpointPath,
        string outputDirectory)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        cleanCheckpointPath = Path.GetFullPath(cleanCheckpointPath);
        outputDirectory = Path.GetFullPath(outputDirectory);
        if (!File.Exists(cleanCheckpointPath))
        {
            throw new FileNotFoundException("The clean joint-feasible checkpoint does not exist.", cleanCheckpointPath);
        }
        if (Directory.Exists(outputDirectory) && Directory.EnumerateFileSystemEntries(outputDirectory).Any())
        {
            throw new IOException($"Baseline verification output directory is not empty: '{outputDirectory}'.");
        }
        Directory.CreateDirectory(outputDirectory);
        List<string> failures = [];
        string originalCheckpointHash = HashFile(cleanCheckpointPath);
        CheckerCheckpoint checkpoint = CheckerCheckpointStore.Load(
            cleanCheckpointPath,
            instance,
            expectedZeroConflictMode: true,
            expectedEndpointOccupationSeconds: 600,
            expectedRepairRunningTimeSupplementRatio: 0.05,
            expectedCoordinationStrategy: CheckerCoordinationStrategy.ValidityFirstBenders);
        MasterSolutionSnapshot snapshot = checkpoint.BestJointFeasibleSolution
            ?? throw new InvalidDataException("The clean checkpoint has no best joint-feasible solution.");
        string fingerprint = SnapshotFingerprint.Calculate(snapshot);
        if (checkpoint.LastAcceptedIteration != 12)
        {
            failures.Add($"accepted_iteration={checkpoint.LastAcceptedIteration}, expected=12");
        }
        if (!string.Equals(checkpoint.AcceptedStateFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            failures.Add("accepted-state fingerprint does not match the joint-feasible candidate");
        }

        IReadOnlyList<PromotedMicroComponent> persistentComponents = checkpoint.PromotedMicroComponents ?? [];
        int persistentRequirementCount = persistentComponents.Sum(component => component.Requirements.Count);
        if (persistentComponents.Count != 12 || persistentRequirementCount != 685)
        {
            failures.Add($"persistent hotspot coverage={persistentComponents.Count}/{persistentRequirementCount}, expected=12/685");
        }
        IReadOnlyList<VerifiedMicroAssignmentWitness> savedWitnesses = snapshot.VerifiedMicroAssignments ?? [];
        if (savedWitnesses.Count != 685)
        {
            failures.Add($"saved assignment count={savedWitnesses.Count}, expected=685");
        }
        if (savedWitnesses.Any(witness =>
                !string.Equals(witness.CandidateSnapshotFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase)))
        {
            failures.Add("one or more saved assignments belong to a different candidate fingerprint");
        }

        IReadOnlyList<ValidatedMicroFeedback> feedback = checkpoint.PermanentMicroFeedback ?? [];
        if (feedback.Count != 204)
        {
            failures.Add($"permanent feedback count={feedback.Count}, expected=204");
        }
        foreach (ValidatedMicroFeedback item in feedback)
        {
            if (ValidatedMicroFeedbackPolicy.ResolvePersistence(item) != MicroFeedbackPersistence.Permanent ||
                !item.AppliedToModel || !item.ValidatedBySnapshot || item.ReturnedSnapshotSatisfiesFeedback != true ||
                !string.Equals(item.ValidatedSnapshotFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"feedback '{item.FeedbackId}' is not fully validated by the round-12 candidate");
            }
        }

        MasterValidationReport macroValidation;
        MicroFeasibilityReport globalMicro;
        StationLevelMicroFeasibilitySlaveResult freshStationAnalysis;
        VerifiedMicroAssignmentValidationReport savedValidation;
        IReadOnlyList<VerifiedMicroAssignmentWitness> freshWitnesses;
        int freshDifferenceCount;
        int fixedRouteOrderCount;
        using (ValidatedSnapshotCarrier carrier = new ValidatedSnapshotCarrierBuilder().Build(
                   instance,
                   neighborRadius: null,
                   snapshot,
                   checkpoint.RepairRunningTimeSupplementRatio,
                   checkpoint.ActiveSectionEdges,
                   checkpoint.ActiveStationEventEdges,
                   persistentComponents,
                   checkpoint.EndpointOccupationSeconds))
        {
            macroValidation = carrier.Validation;
            StationLevelMicroFeasibilitySlave slave =
                CheckerMicroValidationFactory.CreateSlave(checkpoint.EndpointOccupationSeconds);
            freshStationAnalysis = slave.Solve(carrier.Model);
            savedValidation = VerifiedMicroAssignmentValidator.Validate(
                instance,
                snapshot,
                savedWitnesses,
                freshStationAnalysis,
                checkpoint.EndpointOccupationSeconds);
            freshWitnesses = VerifiedMicroAssignmentWitnessBuilder.Capture(freshStationAnalysis, fingerprint);
            freshDifferenceCount = CountChoiceDifferences(savedWitnesses, freshWitnesses);

            MasterMicroFeasibilityChecker microChecker =
                CheckerMicroValidationFactory.CreateChecker(checkpoint.EndpointOccupationSeconds);
            globalMicro = microChecker.Validate(carrier.Model);
            microChecker.WriteReports(globalMicro, Path.Combine(outputDirectory, "global_micro"));
            new MasterSolutionValidator().WriteReport(
                macroValidation,
                Path.Combine(outputDirectory, "macro_validation.md"));
        }

        if (!macroValidation.IsFeasible || macroValidation.Violations.Count != 0)
        {
            failures.Add($"Macro Validator violations={macroValidation.Violations.Count}");
        }
        if (!savedValidation.IsValid || savedValidation.ValidatedRequirementCount != 685 ||
            savedValidation.ValidatedStationCount != 12)
        {
            failures.Add(
                $"saved assignment validation failed: validated={savedValidation.ValidatedRequirementCount}, stations={savedValidation.ValidatedStationCount}, findings={savedValidation.Findings.Count}");
        }
        if (!globalMicro.IsFeasible || globalMicro.Conflicts.Count != 0 || globalMicro.Status == MicroSolveStatus.Unknown)
        {
            failures.Add($"Global Micro status={globalMicro.Status}, conflicts={globalMicro.Conflicts.Count}");
        }

        IReadOnlyList<PromotedMicroComponent> transientAssignments =
            ValidityFirstFeasibleAssignmentPromotionBuilder.BuildFromVerifiedWitnesses(
                freshStationAnalysis,
                savedWitnesses,
                13);
        IReadOnlyList<PromotedMicroComponent> stageThreeComponents =
            ValidityFirstStageThreeComponentSet.Compose(persistentComponents, transientAssignments);
        using (BasicMilpModel stageThreeModel = new BasicMilpModelBuilder(
                   instance,
                   additionalSectionEdges: checkpoint.ActiveSectionEdges,
                   stationEventEdges: checkpoint.ActiveStationEventEdges,
                   runningTimeSupplementRatio: checkpoint.RepairRunningTimeSupplementRatio,
                   promotedMicroComponents: stageThreeComponents,
                   endpointOccupationSeconds: checkpoint.EndpointOccupationSeconds).Build())
        {
            string before = SnapshotFingerprint.Calculate(snapshot);
            ValidityFirstVerifiedStructureFixationResult fixation =
                ValidityFirstVerifiedStructureFixer.Apply(
                    stageThreeModel,
                    snapshot,
                    transientAssignments,
                    checkpoint.EndpointOccupationSeconds);
            fixedRouteOrderCount = fixation.FixedRouteOrderCount;
            if (!string.Equals(before, SnapshotFingerprint.Calculate(snapshot), StringComparison.Ordinal))
            {
                failures.Add("stage-3 preparation mutated the accepted round-12 candidate");
            }
        }

        BusinessObjectiveVector objective = BusinessObjectiveCalculator.Calculate(
            instance.Trains.Select(train => snapshot.DepartureSecondsByStopId[train.OrderedStops[0].StopId]),
            instance.Trains.Select(train => snapshot.ArrivalSecondsByStopId[train.OrderedStops[^1].StopId]),
            snapshot.RunningTimeSupplementSecondsByKey?.Values ?? []);
        double? upperBound = failures.Count == 0 ? objective.PrimaryGlobalSpanSeconds : null;

        WriteTimetable(instance, snapshot, Path.Combine(outputDirectory, "round12_timetable.csv"));
        WriteWitnesses(savedWitnesses, Path.Combine(outputDirectory, "saved_micro_assignments.csv"));
        WriteWitnesses(freshWitnesses, Path.Combine(outputDirectory, "freshly_searched_micro_assignments.csv"));
        WriteWitnessComparison(savedWitnesses, freshWitnesses,
            Path.Combine(outputDirectory, "saved_vs_fresh_assignment_comparison.csv"));
        WriteComponents(persistentComponents, Path.Combine(outputDirectory, "persistent_hotspots.csv"));
        WriteFeedback(feedback, Path.Combine(outputDirectory, "validated_feedback.csv"));
        File.WriteAllText(
            Path.Combine(outputDirectory, "saved_assignment_validation.json"),
            JsonSerializer.Serialize(savedValidation, JsonOptions),
            new UTF8Encoding(true));
        WriteRoute9488Audit(instance, savedWitnesses, Path.Combine(outputDirectory, "route_94_88_audit.json"));

        string currentCheckpointPath = Path.Combine(Path.GetDirectoryName(cleanCheckpointPath)!, "validity_first_checkpoint.json");
        object stageStateAudit = BuildStageStateAudit(instance, currentCheckpointPath, fingerprint);
        File.WriteAllText(
            Path.Combine(outputDirectory, "round12_round13_state_separation.json"),
            JsonSerializer.Serialize(stageStateAudit, JsonOptions),
            new UTF8Encoding(true));

        string confirmedCheckpointPath = string.Empty;
        if (failures.Count == 0)
        {
            confirmedCheckpointPath = Path.Combine(outputDirectory, "confirmed_round12_checkpoint.json");
            File.Copy(cleanCheckpointPath, confirmedCheckpointPath, overwrite: false);
            if (!string.Equals(HashFile(confirmedCheckpointPath), originalCheckpointHash, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(confirmedCheckpointPath);
                failures.Add("confirmed checkpoint copy hash does not match its original");
                confirmedCheckpointPath = string.Empty;
                upperBound = null;
            }
            else
            {
                CopySourceEvidence(cleanCheckpointPath, outputDirectory);
            }
        }

        stopwatch.Stop();
        JointFeasibleBaselineVerificationResult result = new(
            failures.Count == 0,
            outputDirectory,
            fingerprint,
            checkpoint.LastAcceptedIteration,
            macroValidation.Violations.Count,
            globalMicro.Conflicts.Count,
            globalMicro.Status == MicroSolveStatus.Unknown ? 1 : 0,
            savedWitnesses.Count,
            savedValidation.ValidatedRequirementCount,
            savedValidation.ValidatedStationCount,
            freshDifferenceCount,
            persistentComponents.Count,
            persistentRequirementCount,
            feedback.Count,
            objective.PrimaryGlobalSpanSeconds,
            upperBound,
            null,
            null,
            stopwatch.Elapsed.TotalSeconds,
            confirmedCheckpointPath,
            failures);
        File.WriteAllText(
            Path.Combine(outputDirectory, "baseline_verification.json"),
            JsonSerializer.Serialize(result, JsonOptions),
            new UTF8Encoding(true));
        WriteSummary(result, originalCheckpointHash, fixedRouteOrderCount,
            savedValidation.MissingOrderedRoutePairCount,
            Path.Combine(outputDirectory, "baseline_verification.md"));
        WriteProvenance(
            instance,
            checkpoint,
            cleanCheckpointPath,
            currentCheckpointPath,
            outputDirectory,
            originalCheckpointHash);
        WriteHashManifest(repositoryRoot, cleanCheckpointPath, currentCheckpointPath, outputDirectory);
        return result;
    }

    private static void WriteProvenance(
        TimetablingInstance instance,
        CheckerCheckpoint checkpoint,
        string cleanCheckpointPath,
        string currentCheckpointPath,
        string outputDirectory,
        string originalCheckpointHash)
    {
        object provenance = new
        {
            verificationKind = "fixed_round12_joint_feasible_checkpoint_readback",
            verificationCommand = $"dotnet GurobiTimetablingResearch.dll Data/JZ --verify-joint-feasible-checkpoint=\"{cleanCheckpointPath}\" --joint-feasible-baseline-output=\"{outputDirectory}\"",
            solverAssemblyVersion = typeof(GRB).Assembly.GetName().Version?.ToString() ?? "unavailable",
            instance = instance.Meta,
            sourceCheckpointPath = cleanCheckpointPath,
            sourceCheckpointSha256 = originalCheckpointHash,
            currentRound13CheckpointPath = File.Exists(currentCheckpointPath) ? currentCheckpointPath : null,
            checkpoint.SourceIdentity,
            checkpoint.SearchConfigurationFingerprint,
            checkpoint.CandidateUniverseFingerprint,
            checkpoint.ZeroConflictMode,
            checkpoint.EndpointOccupationSeconds,
            checkpoint.RepairRunningTimeSupplementRatio,
            checkpoint.CoordinationStrategy,
            checkpoint.LastAcceptedIteration,
            checkpoint.AcceptedStateFingerprint,
            persistentFeedbackCount = checkpoint.PermanentMicroFeedback?.Count ?? 0,
            feedbackAssessment = "204条反馈均由原Global Micro精确间隔不可行证据产生；本次只修正阶段3固定顺序的查表方向，未改变反馈分类器和Master中反馈约束，因此无需删除或降级。",
            stageThreeModelWasOptimized = false,
            timetableWasChanged = false
        };
        File.WriteAllText(
            Path.Combine(outputDirectory, "baseline_provenance.json"),
            JsonSerializer.Serialize(provenance, JsonOptions),
            new UTF8Encoding(true));
    }

    private static void CopySourceEvidence(string cleanCheckpointPath, string outputDirectory)
    {
        string sourceRunDirectory = Path.GetDirectoryName(cleanCheckpointPath)!;
        string evidenceDirectory = Path.Combine(outputDirectory, "original_run_evidence");
        Directory.CreateDirectory(evidenceDirectory);
        string manifest = Path.Combine(sourceRunDirectory, "01_manifest", "run_manifest.json");
        if (File.Exists(manifest))
        {
            File.Copy(manifest, Path.Combine(evidenceDirectory, "run_manifest.json"), overwrite: false);
        }
        string originalValidation = Path.Combine(
            sourceRunDirectory, "micro_validation", "012_hotspot_exactification");
        if (Directory.Exists(originalValidation))
        {
            string validationCopy = Path.Combine(evidenceDirectory, "round12_original_micro_validation");
            Directory.CreateDirectory(validationCopy);
            foreach (string source in Directory.EnumerateFiles(originalValidation))
            {
                File.Copy(source, Path.Combine(validationCopy, Path.GetFileName(source)), overwrite: false);
            }
        }
    }

    private static int CountChoiceDifferences(
        IReadOnlyList<VerifiedMicroAssignmentWitness> saved,
        IReadOnlyList<VerifiedMicroAssignmentWitness> fresh)
    {
        Dictionary<string, VerifiedMicroAssignmentWitness> freshById = fresh
            .ToDictionary(item => item.RequirementId, StringComparer.OrdinalIgnoreCase);
        return saved.Count(item => !freshById.TryGetValue(item.RequirementId, out VerifiedMicroAssignmentWitness? other) ||
            item.IsStop != other.IsStop ||
            !string.Equals(item.TrackName, other.TrackName, StringComparison.OrdinalIgnoreCase) ||
            item.IncomingRouteId != other.IncomingRouteId || item.OutgoingRouteId != other.OutgoingRouteId) +
            fresh.Count(item => saved.All(old => !string.Equals(old.RequirementId, item.RequirementId,
                StringComparison.OrdinalIgnoreCase)));
    }

    private static object BuildStageStateAudit(
        TimetablingInstance instance,
        string currentCheckpointPath,
        string acceptedFingerprint)
    {
        if (!File.Exists(currentCheckpointPath))
        {
            return new { currentCheckpointAvailable = false };
        }
        CheckerCheckpoint current = CheckerCheckpointStore.Load(
            currentCheckpointPath,
            instance,
            true,
            600,
            0.05,
            expectedCoordinationStrategy: CheckerCoordinationStrategy.ValidityFirstBenders);
        IReadOnlyList<PromotedMicroComponent> components = current.PromotedMicroComponents ?? [];
        IReadOnlyList<PromotedMicroComponent> persistent = components
            .Where(component => !component.ComponentId.StartsWith("validity_stage3_assignment::",
                StringComparison.OrdinalIgnoreCase)).ToList();
        IReadOnlyList<PromotedMicroComponent> transient = components
            .Where(component => component.ComponentId.StartsWith("validity_stage3_assignment::",
                StringComparison.OrdinalIgnoreCase)).ToList();
        return new
        {
            currentCheckpointAvailable = true,
            current.LastAcceptedIteration,
            acceptedFingerprintMatchesRound12 = string.Equals(
                current.AcceptedStateFingerprint, acceptedFingerprint, StringComparison.OrdinalIgnoreCase),
            allComponentCount = components.Count,
            allRequirementCount = components.Sum(component => component.Requirements.Count),
            persistentComponentCount = persistent.Count,
            persistentRequirementCount = persistent.Sum(component => component.Requirements.Count),
            transientStage3ComponentCount = transient.Count,
            transientStage3RequirementCount = transient.Sum(component => component.Requirements.Count),
            cause = "阶段3准备时把12个当前候选单选组件追加到12个持久热点，并在Optimize前提前保存；它们是不同角色的同一批685项需求，不是新的已验证覆盖。"
        };
    }

    private static void WriteRoute9488Audit(
        TimetablingInstance instance,
        IReadOnlyList<VerifiedMicroAssignmentWitness> witnesses,
        string path)
    {
        Dictionary<string, TrainStopRecord> stops = instance.TrainStops.ToDictionary(
            stop => stop.StopId, StringComparer.OrdinalIgnoreCase);
        var events = witnesses
            .Where(witness => string.Equals(witness.StationName, "站3967", StringComparison.OrdinalIgnoreCase))
            .SelectMany(witness => new[]
            {
                witness.IncomingRouteId.HasValue
                    ? new { witness, routeId = witness.IncomingRouteId.Value, side = "arrival", time = witness.ArrivalSeconds!.Value }
                    : null,
                witness.OutgoingRouteId.HasValue
                    ? new { witness, routeId = witness.OutgoingRouteId.Value, side = "departure", time = witness.DepartureSeconds!.Value }
                    : null
            }.Where(item => item is not null)!)
            .ToList();
        var firstCase = (from first in events
                         from second in events
                         where first!.routeId == 94 && second!.routeId == 88 &&
                               first.time < second.time &&
                               !string.Equals(first.witness.RequirementId, second.witness.RequirementId,
                                   StringComparison.OrdinalIgnoreCase)
                         select new { first, second }).FirstOrDefault();
        RouteRecord route94 = instance.Routes.Single(route => route.RouteId == 94);
        RouteRecord route88 = instance.Routes.Single(route => route.RouteId == 88);
        int? forward = FindHeadway(instance, 94, 88);
        int? reverse = FindHeadway(instance, 88, 94);
        object audit = new
        {
            route94 = new { route94.RouteId, route94.StationName, route94.SectionName, route94.TrackName, route94.OperationType, route94.MovementEvent },
            route88 = new { route88.RouteId, route88.StationName, route88.SectionName, route88.TrackName, route88.OperationType, route88.MovementEvent },
            headway94To88 = forward,
            headway88To94 = reverse,
            businessMeaning = "间隔表按有序的前车进路→后车进路查询；缺少94→88表示该实际方向没有额外专用间隔，不表示禁止，也不复制反向420秒。股道占用等其他资源约束仍独立生效。",
            trigger = firstCase is null ? null : new
            {
                firstRequirementId = firstCase.first!.witness.RequirementId,
                firstTrainIndex = firstCase.first.witness.TrainIndex,
                firstTrainNo = stops[firstCase.first.witness.RequirementId].TrainNo,
                firstCase.first.side,
                firstCase.first.time,
                secondRequirementId = firstCase.second!.witness.RequirementId,
                secondTrainIndex = firstCase.second.witness.TrainIndex,
                secondTrainNo = stops[firstCase.second.witness.RequirementId].TrainNo,
                secondCaseSide = firstCase.second.side,
                secondCaseTime = firstCase.second.time,
                realizedGapSeconds = firstCase.second.time - firstCase.first.time,
                sameTrain = firstCase.first.witness.TrainIndex == firstCase.second.witness.TrainIndex,
                sharedTrack = string.Equals(firstCase.first.witness.TrackName, firstCase.second.witness.TrackName,
                    StringComparison.OrdinalIgnoreCase)
            }
        };
        File.WriteAllText(path, JsonSerializer.Serialize(audit, JsonOptions), new UTF8Encoding(true));
    }

    private static void WriteTimetable(TimetablingInstance instance, MasterSolutionSnapshot snapshot, string path)
    {
        using StreamWriter writer = new(path, false, new UTF8Encoding(true));
        writer.WriteLine("stop_id,train_index,train_no,station_order,station_name,arrival_seconds,departure_seconds,is_stop");
        foreach (TrainStopRecord stop in instance.TrainStops.OrderBy(stop => stop.TrainIndex).ThenBy(stop => stop.StationOrder))
        {
            writer.WriteLine(string.Join(',', Csv(stop.StopId), stop.TrainIndex, Csv(stop.TrainNo), stop.StationOrder,
                Csv(stop.StationName), F(snapshot.ArrivalSecondsByStopId[stop.StopId]),
                F(snapshot.DepartureSecondsByStopId[stop.StopId]), snapshot.StopByStopId[stop.StopId]));
        }
    }

    private static void WriteWitnesses(IReadOnlyList<VerifiedMicroAssignmentWitness> witnesses, string path)
    {
        using StreamWriter writer = new(path, false, new UTF8Encoding(true));
        writer.WriteLine("station,requirement_id,train_index,is_stop,track,incoming_route,outgoing_route,arrival,departure,candidate_fingerprint,scope");
        foreach (VerifiedMicroAssignmentWitness item in witnesses.OrderBy(item => item.StationName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.TrainIndex).ThenBy(item => item.RequirementId, StringComparer.OrdinalIgnoreCase))
        {
            writer.WriteLine(string.Join(',', Csv(item.StationName), Csv(item.RequirementId), item.TrainIndex, item.IsStop,
                Csv(item.TrackName), item.IncomingRouteId, item.OutgoingRouteId,
                item.ArrivalSeconds.HasValue ? F(item.ArrivalSeconds.Value) : string.Empty,
                item.DepartureSeconds.HasValue ? F(item.DepartureSeconds.Value) : string.Empty,
                item.CandidateSnapshotFingerprint, item.VerificationScope));
        }
    }

    private static void WriteWitnessComparison(
        IReadOnlyList<VerifiedMicroAssignmentWitness> saved,
        IReadOnlyList<VerifiedMicroAssignmentWitness> fresh,
        string path)
    {
        Dictionary<string, VerifiedMicroAssignmentWitness> freshById = fresh.ToDictionary(
            item => item.RequirementId, StringComparer.OrdinalIgnoreCase);
        using StreamWriter writer = new(path, false, new UTF8Encoding(true));
        writer.WriteLine("requirement_id,saved_choice,fresh_choice,same_choice");
        foreach (VerifiedMicroAssignmentWitness item in saved.OrderBy(item => item.RequirementId, StringComparer.OrdinalIgnoreCase))
        {
            freshById.TryGetValue(item.RequirementId, out VerifiedMicroAssignmentWitness? other);
            string savedChoice = Choice(item);
            string freshChoice = other is null ? "missing" : Choice(other);
            writer.WriteLine($"{Csv(item.RequirementId)},{Csv(savedChoice)},{Csv(freshChoice)},{string.Equals(savedChoice, freshChoice, StringComparison.OrdinalIgnoreCase)}");
        }
    }

    private static void WriteComponents(IReadOnlyList<PromotedMicroComponent> components, string path)
    {
        using StreamWriter writer = new(path, false, new UTF8Encoding(true));
        writer.WriteLine("component_id,station,promotion_reason,promotion_iteration,requirement_count,requirement_ids");
        foreach (PromotedMicroComponent component in components.OrderBy(item => item.ComponentId, StringComparer.OrdinalIgnoreCase))
        {
            writer.WriteLine(string.Join(',', Csv(component.ComponentId), Csv(component.StationName),
                Csv(component.PromotionReason), component.PromotionIteration, component.Requirements.Count,
                Csv(string.Join('|', component.Requirements.Select(item => item.RequirementId).Order(StringComparer.OrdinalIgnoreCase)))));
        }
    }

    private static void WriteFeedback(IReadOnlyList<ValidatedMicroFeedback> feedback, string path)
    {
        using StreamWriter writer = new(path, false, new UTF8Encoding(true));
        writer.WriteLine("feedback_id,station,validity,applied,validated,returned_snapshot_satisfies,validated_snapshot_fingerprint,proof_source,mathematical_constraint,applicability_conditions");
        foreach (ValidatedMicroFeedback item in feedback.OrderBy(item => item.FeedbackId, StringComparer.OrdinalIgnoreCase))
        {
            writer.WriteLine(string.Join(',', Csv(item.FeedbackId), Csv(item.StationName), item.Validity,
                item.AppliedToModel, item.ValidatedBySnapshot, item.ReturnedSnapshotSatisfiesFeedback,
                item.ValidatedSnapshotFingerprint, Csv(item.ProofSource), Csv(item.MathematicalConstraint),
                Csv(item.ApplicabilityConditions)));
        }
    }

    private static void WriteSummary(
        JointFeasibleBaselineVerificationResult result,
        string originalCheckpointHash,
        int fixedRouteOrderCount,
        int missingOrderedRoutePairCount,
        string path)
    {
        StringBuilder text = new();
        text.AppendLine("# JZ 第 12 轮零冲突基准复验");
        text.AppendLine();
        text.AppendLine($"- passed: `{result.Passed}`");
        text.AppendLine($"- candidate_fingerprint: `{result.CandidateFingerprint}`");
        text.AppendLine($"- original_checkpoint_sha256: `{originalCheckpointHash}`");
        text.AppendLine($"- accepted_iteration: `{result.AcceptedIteration}`");
        text.AppendLine($"- macro_violations: `{result.MacroViolationCount}`");
        text.AppendLine($"- global_micro_conflicts: `{result.GlobalMicroConflictCount}`");
        text.AppendLine($"- global_micro_unknown: `{result.GlobalMicroUnknownCount}`");
        text.AppendLine($"- saved_assignments_validated: `{result.ValidatedSavedAssignmentCount}/{result.SavedAssignmentCount}`");
        text.AppendLine($"- validated_stations: `{result.ValidatedStationCount}`");
        text.AppendLine($"- freshly_searched_choice_differences: `{result.FreshAssignmentDifferenceCount}`");
        text.AppendLine($"- missing_ordered_route_pairs_exercised: `{missingOrderedRoutePairCount}`");
        text.AppendLine($"- stage3_fixed_route_orders: `{fixedRouteOrderCount}`");
        text.AppendLine($"- global_span_seconds: `{result.GlobalSpanSeconds.ToString("F3", CultureInfo.InvariantCulture)}`");
        text.AppendLine($"- valid_UB: `{result.GlobalUpperBound?.ToString("F3", CultureInfo.InvariantCulture) ?? ""}`");
        text.AppendLine("- LB: ``");
        text.AppendLine("- global_gap: ``");
        text.AppendLine($"- runtime_seconds: `{result.RuntimeSeconds.ToString("F3", CultureInfo.InvariantCulture)}`");
        if (result.Failures.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("## 失败项");
            foreach (string failure in result.Failures)
            {
                text.AppendLine($"- {failure}");
            }
        }
        File.WriteAllText(path, text.ToString(), new UTF8Encoding(true));
    }

    private static void WriteHashManifest(
        string repositoryRoot,
        string cleanCheckpointPath,
        string currentCheckpointPath,
        string outputDirectory)
    {
        List<string> files = [cleanCheckpointPath];
        if (File.Exists(currentCheckpointPath))
        {
            files.Add(currentCheckpointPath);
        }
        files.AddRange(Directory.EnumerateFiles(Path.Combine(repositoryRoot, "Data", "JZ"), "*", SearchOption.AllDirectories));
        files.AddRange(Directory.EnumerateFiles(Path.Combine(repositoryRoot, "src"), "*.cs", SearchOption.AllDirectories));
        using StreamWriter writer = new(Path.Combine(outputDirectory, "sha256_manifest.csv"), false, new UTF8Encoding(true));
        writer.WriteLine("sha256,length,path");
        foreach (string file in files.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            FileInfo info = new(file);
            writer.WriteLine($"{HashFile(file)},{info.Length},{Csv(Path.GetFullPath(file))}");
        }
    }

    private static string Choice(VerifiedMicroAssignmentWitness item) =>
        $"{item.IsStop}|{item.TrackName}|{item.IncomingRouteId}|{item.OutgoingRouteId}";

    private static int? FindHeadway(TimetablingInstance instance, int leadingRouteId, int followingRouteId) =>
        instance.TryGetRouteHeadwayRule(leadingRouteId, followingRouteId, out RouteHeadwayRule? rule) && rule is not null
            ? rule.MinHeadwaySeconds
            : null;

    private static string HashFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string F(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static string Csv(string? value)
    {
        value ??= string.Empty;
        return value.IndexOfAny([',', '"', '\r', '\n']) < 0
            ? value
            : $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }
}
