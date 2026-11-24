// 运行产物模块用目录布局和清单 record 统一管理快照、检查点、阶段报告及失败摘要的路径。
// 写入器集中生成各阶段文件并更新运行清单，使一次自适应运行的输入、配置与输出能够完整关联和复现。
using System.Globalization;
using System.Text;
using System.Text.Json;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

internal sealed record AdaptiveRunArtifactLayout(
    string RootDirectory,
    string ManifestDirectory,
    string CheckpointDirectory,
    string TrajectoryDirectory,
    string SweepsDirectory,
    string CandidateTrialsDirectory,
    string MicroDirectory,
    string QualityDirectory,
    string FinalDirectory)
{
    internal static AdaptiveRunArtifactLayout CreateUnique(
        string baseDirectory,
        DateTimeOffset createdAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        Directory.CreateDirectory(baseDirectory);
        string stem = $"adaptive_singleton_full_{createdAtUtc.UtcDateTime:yyyyMMdd_HHmmss}";
        for (int suffix = 0; suffix < 10_000; suffix++)
        {
            string name = suffix == 0 ? stem : $"{stem}_{suffix:D2}";
            string candidate = Path.Combine(Path.GetFullPath(baseDirectory), name);
            if (!Directory.Exists(candidate) && !File.Exists(candidate))
            {
                return CreateRequested(candidate);
            }
        }
        throw new IOException("Unable to allocate a unique Adaptive artifact directory.");
    }

    internal static AdaptiveRunArtifactLayout CreateRequested(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        string root = Path.GetFullPath(rootDirectory);
        if (Directory.Exists(root) || File.Exists(root))
        {
            throw new IOException($"Adaptive artifact directory already exists: '{root}'.");
        }
        Directory.CreateDirectory(root);
        AdaptiveRunArtifactLayout layout = new(
            root,
            Path.Combine(root, "01_manifest"),
            Path.Combine(root, "02_checkpoint"),
            Path.Combine(root, "03_trajectory"),
            Path.Combine(root, "04_sweeps"),
            Path.Combine(root, "05_candidate_trials"),
            Path.Combine(root, "06_micro"),
            Path.Combine(root, "07_quality"),
            Path.Combine(root, "08_final"));
        foreach (string directory in layout.AllDirectories())
        {
            Directory.CreateDirectory(directory);
        }
        return layout;
    }

    private IEnumerable<string> AllDirectories()
    {
        yield return ManifestDirectory;
        yield return CheckpointDirectory;
        yield return TrajectoryDirectory;
        yield return SweepsDirectory;
        yield return CandidateTrialsDirectory;
        yield return MicroDirectory;
        yield return QualityDirectory;
        yield return FinalDirectory;
    }
}

internal sealed record AdaptiveRunManifest(
    string RunId,
    DateTimeOffset CreatedAtUtc,
    string DatasetPath,
    string InputSnapshotPath,
    string InputSnapshotFingerprint,
    string InitialMicroFingerprint,
    int InitialRootConflicts,
    IReadOnlyList<string> CliArguments,
    int InitialCandidateCap,
    int ExpandedCandidateCap,
    int SafetyAcceptedTransitionCap,
    IReadOnlyList<int> RecourseWindowSeconds,
    double PrimaryTimeLimitSeconds,
    double SecondaryTimeLimitSeconds,
    double RepairSupplementRatio,
    string CheckpointPath,
    string DotNetVersion,
    string GurobiVersion,
    string OutputDirectory,
    string AdaptiveConfigurationFingerprint,
    string InstanceName = "");

internal sealed record AdaptiveRunArtifactRequest(
    AdaptiveRunArtifactLayout Layout,
    AdaptiveRunManifest Manifest,
    IReadOnlyList<AdaptiveSingletonSweepDiagnostic> Diagnostics,
    IReadOnlyList<AdaptiveSingletonCompletedSweep> SweepHistory,
    IReadOnlyList<StructuralSingletonGlobalTrialDiagnostic> CandidateTrials,
    IReadOnlyList<AdaptiveUnknownEvidence> UnknownEvidence,
    MicroFeasibilityReport InitialMicroReport,
    MicroFeasibilityReport FinalMicroReport,
    MasterSolutionSnapshot FinalSnapshot,
    MasterValidationReport FinalValidation,
    IReadOnlyList<SectionConflictEdgeRequest> SectionEdges,
    IReadOnlyList<StationEventConflictCandidate> StationEdges,
    IReadOnlyList<string> AcceptedCandidateIds,
    string CheckpointPath,
    string TerminationReason,
    string InitialReferenceFingerprint,
    double TotalRuntimeSeconds,
    int AcceptedTransitionCount = 0);

internal static class AdaptiveRunArtifactWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    internal static void Write(AdaptiveRunArtifactRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        WriteManifest(request);
        WriteTrajectory(request);
        WriteSweeps(request);
        WriteCandidateTrials(request);
        WriteUnknown(request);
        WriteQuality(request);
        WriteMicro(request);
        WriteFinal(request);
    }

    internal static void WriteFailure(
        AdaptiveRunArtifactLayout layout,
        IReadOnlyList<string> cliArguments,
        string checkpointPath,
        Exception exception)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(cliArguments);
        ArgumentNullException.ThrowIfNull(exception);
        string arguments = string.Join(' ', cliArguments.Select(PowerShellQuote));
        File.WriteAllText(
            Path.Combine(layout.ManifestDirectory, "run_command.ps1"),
            $"dotnet run --no-build -c Release --project .\\src\\GurobiTimetablingResearch\\GurobiTimetablingResearch.csproj -- {arguments}{Environment.NewLine}",
            Utf8());
        File.WriteAllText(
            Path.Combine(layout.ManifestDirectory, "console.log"),
            exception + Environment.NewLine,
            Utf8());
        object failure = new
        {
            status = "failed",
            failureType = exception.GetType().FullName,
            failureMessage = exception.Message,
            checkpointPath,
            outputDirectory = layout.RootDirectory
        };
        File.WriteAllText(
            Path.Combine(layout.FinalDirectory, "failure_summary.json"),
            JsonSerializer.Serialize(failure, JsonOptions),
            Utf8());
        File.WriteAllText(
            Path.Combine(layout.FinalDirectory, "failure_summary.md"),
            string.Join(Environment.NewLine,
                "# Adaptive Run Failure",
                string.Empty,
                $"- type: `{exception.GetType().FullName}`",
                $"- message: `{exception.Message}`",
                $"- last_valid_checkpoint: `{checkpointPath}`") + Environment.NewLine,
            Utf8());
    }

    private static void WriteManifest(AdaptiveRunArtifactRequest request)
    {
        File.WriteAllText(
            Path.Combine(request.Layout.ManifestDirectory, "run_manifest.json"),
            JsonSerializer.Serialize(request.Manifest, JsonOptions),
            Utf8());
        string arguments = string.Join(' ', request.Manifest.CliArguments.Select(PowerShellQuote));
        File.WriteAllText(
            Path.Combine(request.Layout.ManifestDirectory, "run_command.ps1"),
            $"dotnet run --no-build -c Release --project .\\src\\GurobiTimetablingResearch\\GurobiTimetablingResearch.csproj -- {arguments}{Environment.NewLine}",
            Utf8());
        File.WriteAllText(
            Path.Combine(request.Layout.ManifestDirectory, "console.log"),
            string.Join(Environment.NewLine,
                $"run_id={request.Manifest.RunId}",
                $"initial_root_conflicts={request.Manifest.InitialRootConflicts}",
                $"termination_reason={request.TerminationReason}",
                $"output_directory={request.Layout.RootDirectory}") + Environment.NewLine,
            Utf8());
    }

    private static void WriteTrajectory(AdaptiveRunArtifactRequest request)
    {
        string header = "accepted_transition_index,sweep_index,winner_layer,winner_candidate_id,root_before,root_after,gain,episode_before,episode_after,exact_before,exact_after,assignment_before,assignment_after,resolved,introduced,persistent,initial_candidate_count,expanded_candidate_count,direct_feasible_count,proven_infeasible_count,unknown_count,strict_improving_count,quality_blocked_count,step_total_retiming_seconds,step_max_retiming_seconds,initial_total_retiming_seconds,initial_max_retiming_seconds,incremental_supplement_seconds,current_total_supplement_seconds,supplement_section_count,round_runtime_seconds,runtime_per_removed_conflict,accepted_state_fingerprint,conflict_fingerprint,checkpoint_path";
        WriteCsv(
            Path.Combine(request.Layout.TrajectoryDirectory, "adaptive_trajectory.csv"),
            header,
            request.Diagnostics.Select(item => string.Join(',',
                item.AcceptedTransitionIndex,
                item.SweepIndex,
                Csv(item.AcceptedLayer),
                Csv(item.AcceptedCandidateId),
                item.RootConflictsBefore,
                item.RootConflictsAfter,
                item.Gain,
                item.EpisodeConflictsBefore,
                item.EpisodeConflictsAfter,
                item.ExactConflictsBefore,
                item.ExactConflictsAfter,
                item.AssignmentConflictsBefore,
                item.AssignmentConflictsAfter,
                item.ResolvedConflictCount,
                item.IntroducedConflictCount,
                item.PersistentConflictCount,
                item.InitialCandidateCount,
                item.ExpandedCandidateCount,
                item.DirectFeasibleCount,
                item.ProvenInfeasibleCount,
                item.UnknownCount,
                item.StrictImprovingCount,
                item.BudgetRejectedCandidateCount,
                F(item.StepTotalRetimingSeconds),
                F(item.StepMaximumRetimingSeconds),
                F(item.InitialTotalRetimingSeconds),
                F(item.InitialMaximumRetimingSeconds),
                F(item.IncrementalRunningSupplementSeconds),
                F(item.CurrentTotalRunningSupplementSeconds),
                item.CurrentSupplementSectionCount,
                F(item.SweepRuntimeSeconds),
                item.RuntimePerRemovedConflict.HasValue ? F(item.RuntimePerRemovedConflict.Value) : string.Empty,
                Csv(item.AcceptedStateFingerprintAfter),
                Csv(item.ConflictFingerprintAfter),
                Csv(item.CheckpointPath))));
        StringBuilder markdown = new("# Adaptive Accepted Trajectory\n\n");
        markdown.AppendLine("| transition | sweep | winner layer | candidate | root | gain | runtime | checkpoint |");
        markdown.AppendLine("|---:|---:|---|---|---:|---:|---:|---|");
        foreach (AdaptiveSingletonSweepDiagnostic item in request.Diagnostics)
        {
            markdown.AppendLine($"| {item.AcceptedTransitionIndex} | {item.SweepIndex} | `{item.AcceptedLayer}` | `{item.AcceptedCandidateId}` | {item.RootConflictsBefore}->{item.RootConflictsAfter} | {item.Gain} | {F(item.SweepRuntimeSeconds)} | `{item.CheckpointPath}` |");
        }
        File.WriteAllText(
            Path.Combine(request.Layout.TrajectoryDirectory, "adaptive_trajectory.md"),
            markdown.ToString(), Utf8());
    }

    private static void WriteSweeps(AdaptiveRunArtifactRequest request)
    {
        string header = "sweep_index,accepted_transition_index,accepted,initial_direct_outcome,initial_iis_outcome,expanded_direct_outcome,expanded_iis_outcome,evaluated_candidate_count,skipped_candidate_count,unknown_count,quality_blocked_count,evaluated_candidate_ids,skipped_candidate_ids,unknown_candidate_ids,quality_blocked_candidate_ids,candidate_universe_fingerprint,initial_candidate_pool_fingerprint,expanded_candidate_pool_fingerprint,search_configuration_fingerprint,termination_reason";
        WriteCsv(
            Path.Combine(request.Layout.SweepsDirectory, "adaptive_sweeps.csv"),
            header,
            request.Diagnostics.Select(item => string.Join(',',
                item.SweepIndex,
                item.AcceptedTransitionIndex,
                item.Accepted,
                Csv(item.InitialDirectOutcome),
                Csv(item.InitialIisOutcome),
                Csv(item.ExpandedDirectOutcome),
                Csv(item.ExpandedIisOutcome),
                item.EvaluatedCandidateCount,
                item.SkippedCandidateCount,
                item.UnknownCount,
                item.BudgetRejectedCandidateCount,
                Csv(item.EvaluatedCandidateIds),
                Csv(item.SkippedCandidateIds),
                Csv(item.UnknownCandidateIds),
                Csv(item.QualityBlockedCandidateIds),
                Csv(item.CandidateUniverseFingerprint),
                Csv(item.InitialCandidatePoolFingerprint),
                Csv(item.ExpandedCandidatePoolFingerprint),
                Csv(item.SearchConfigurationFingerprint),
                Csv(item.TerminationReason))));
        StringBuilder markdown = new("# Adaptive Sweep Evidence\n\n");
        markdown.AppendLine("| sweep | transition | Initial Direct | Initial IIS | Expanded Direct | Expanded IIS | termination |");
        markdown.AppendLine("|---:|---:|---|---|---|---|---|");
        foreach (AdaptiveSingletonSweepDiagnostic item in request.Diagnostics)
        {
            markdown.AppendLine($"| {item.SweepIndex} | {item.AcceptedTransitionIndex} | `{item.InitialDirectOutcome}` | `{item.InitialIisOutcome}` | `{item.ExpandedDirectOutcome}` | `{item.ExpandedIisOutcome}` | `{item.TerminationReason}` |");
        }
        File.WriteAllText(
            Path.Combine(request.Layout.SweepsDirectory, "adaptive_sweeps.md"),
            markdown.ToString(), Utf8());
    }

    private static void WriteCandidateTrials(AdaptiveRunArtifactRequest request)
    {
        string header = "iteration_index,batch_id,probe_id,candidate_index,candidate_id,candidate_type,train_i,train_j,station,section,event_i,event_j,probe_outcome,master_status,master_runtime_seconds,solution_count,master_validator_feasible,master_validator_violation_count,root_before,root_after,episode_before,episode_after,exact_before,exact_after,assignment_before,assignment_after,resolved,introduced,persistent,total_retiming_seconds,maximum_retiming_seconds,total_supplement_seconds,sections_using_supplement,strict_improvement,selection_rank,selected,outcome,rejection_reason,input_snapshot_fingerprint,trial_snapshot_fingerprint";
        WriteCsv(
            Path.Combine(request.Layout.CandidateTrialsDirectory, "adaptive_candidate_trials.csv"),
            header,
            request.CandidateTrials.Select(item => string.Join(',',
                item.IterationIndex, Csv(item.BatchId), Csv(item.ProbeId), item.CandidateIndex,
                Csv(item.CandidateId), Csv(item.CandidateType), item.TrainI, item.TrainJ,
                Csv(item.Station), Csv(item.Section), Csv(item.EventI), Csv(item.EventJ),
                item.ProbeOutcome, Csv(item.MasterStatus), F(item.MasterRuntimeSeconds),
                item.SolutionCount, item.MasterValidatorFeasible, item.MasterValidatorViolationCount,
                item.RootConflictsBefore, item.RootConflictsAfter?.ToString() ?? string.Empty,
                item.EpisodeConflictsBefore, item.EpisodeConflictsAfter?.ToString() ?? string.Empty,
                item.ExactConflictsBefore, item.ExactConflictsAfter?.ToString() ?? string.Empty,
                item.AssignmentConflictsBefore, item.AssignmentConflictsAfter?.ToString() ?? string.Empty,
                item.ResolvedCount, item.IntroducedCount, item.PersistentCount,
                F(item.TotalRetimingSeconds), F(item.MaximumRetimingSeconds),
                F(item.TotalSupplementSeconds), item.SectionsUsingSupplement,
                item.StrictImprovement, item.SelectionRank?.ToString() ?? string.Empty,
                item.Selected, Csv(item.Outcome), Csv(item.RejectionReason),
                Csv(item.InputSnapshotFingerprint), Csv(item.TrialSnapshotFingerprint))));
        StringBuilder markdown = new("# Adaptive Candidate Trials\n\n");
        markdown.AppendLine($"- trial_count: `{request.CandidateTrials.Count}`");
        markdown.AppendLine();
        markdown.AppendLine("| iteration | candidate | probe | root | strict | selected | outcome |");
        markdown.AppendLine("|---:|---|---|---|:---:|:---:|---|");
        foreach (StructuralSingletonGlobalTrialDiagnostic item in request.CandidateTrials)
        {
            markdown.AppendLine($"| {item.IterationIndex} | `{item.CandidateId}` | {item.ProbeOutcome} | {item.RootConflictsBefore}->{item.RootConflictsAfter?.ToString() ?? "-"} | {item.StrictImprovement} | {item.Selected} | `{item.Outcome}` |");
        }
        File.WriteAllText(
            Path.Combine(request.Layout.CandidateTrialsDirectory, "adaptive_candidate_trials.md"),
            markdown.ToString(), Utf8());
    }

    private static void WriteUnknown(AdaptiveRunArtifactRequest request)
    {
        WriteCsv(
            Path.Combine(request.Layout.SweepsDirectory, "adaptive_unknown_evidence.csv"),
            "candidate_id,layer,source,solver_status,expansion_round,incomplete",
            request.UnknownEvidence.Select(item => string.Join(',',
                Csv(item.CandidateId), Csv(item.Layer), Csv(item.Source), Csv(item.SolverStatus),
                item.ExpansionRound?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                item.Incomplete)));
    }

    private static void WriteQuality(AdaptiveRunArtifactRequest request)
    {
        WriteCsv(
            Path.Combine(request.Layout.QualityDirectory, "adaptive_quality_metrics.csv"),
            "accepted_transition_index,step_total_retiming_seconds,initial_total_retiming_seconds,step_maximum_retiming_seconds,initial_maximum_retiming_seconds,incremental_running_supplement_seconds,current_total_running_supplement_seconds,current_supplement_section_count",
            request.Diagnostics.Where(item => item.Accepted).Select(item => string.Join(',',
                item.AcceptedTransitionIndex,
                F(item.StepTotalRetimingSeconds), F(item.InitialTotalRetimingSeconds),
                F(item.StepMaximumRetimingSeconds), F(item.InitialMaximumRetimingSeconds),
                F(item.IncrementalRunningSupplementSeconds),
                F(item.CurrentTotalRunningSupplementSeconds), item.CurrentSupplementSectionCount)));
    }

    private static void WriteMicro(AdaptiveRunArtifactRequest request)
    {
        WriteHotspot(
            Path.Combine(request.Layout.MicroDirectory, "adaptive_conflict_hotspots_by_section.csv"),
            "section",
            request.FinalMicroReport.Conflicts.Select(item =>
                item.FirstSectionName ?? item.SecondSectionName ?? string.Empty));
        WriteHotspot(
            Path.Combine(request.Layout.MicroDirectory, "adaptive_conflict_hotspots_by_station.csv"),
            "station",
            request.FinalMicroReport.Conflicts.Select(item => item.StationName));
        WriteHotspot(
            Path.Combine(request.Layout.MicroDirectory, "adaptive_conflict_hotspots_by_train_pair.csv"),
            "train_pair",
            request.FinalMicroReport.Conflicts.Select(item =>
            {
                if (!item.FirstTrainIndex.HasValue || !item.SecondTrainIndex.HasValue)
                {
                    return string.Empty;
                }
                int lower = Math.Min(item.FirstTrainIndex.Value, item.SecondTrainIndex.Value);
                int upper = Math.Max(item.FirstTrainIndex.Value, item.SecondTrainIndex.Value);
                return $"{lower}::{upper}";
            }));
    }

    private static void WriteFinal(AdaptiveRunArtifactRequest request)
    {
        AdaptiveSingletonSweepDiagnostic? last = request.Diagnostics.LastOrDefault();
        int initialRoot = MicroConflictMetricCalculator.Calculate(
            request.InitialMicroReport.Conflicts).RootConflictCount;
        int finalRoot = MicroConflictMetricCalculator.Calculate(
            request.FinalMicroReport.Conflicts).RootConflictCount;
        if (last is not null && last.RootConflictsAfter != finalRoot)
        {
            throw new InvalidDataException(
                "Adaptive final summary does not match the last trajectory state.");
        }
        int transitions = Math.Max(request.AcceptedTransitionCount,
            request.Diagnostics.Where(item => item.Accepted)
            .Select(item => item.AcceptedTransitionIndex)
            .DefaultIfEmpty(0)
            .Max());
        Dictionary<string, int> winnerDistribution = request.Diagnostics
            .Where(item => item.Accepted)
            .GroupBy(item => item.AcceptedLayer, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        object summary = new
        {
            initialRootConflicts = initialRoot,
            finalRootConflicts = finalRoot,
            totalReduction = initialRoot - finalRoot,
            reductionPercentage = initialRoot == 0
                ? 0.0
                : 100.0 * (initialRoot - finalRoot) / initialRoot,
            acceptedTransitionCount = transitions,
            trajectory = request.Diagnostics.Select(item => new
            {
                item.SweepIndex,
                item.AcceptedTransitionIndex,
                item.Accepted,
                winnerLayer = item.AcceptedLayer,
                winnerCandidateId = item.AcceptedCandidateId,
                item.RootConflictsBefore,
                item.RootConflictsAfter,
                item.Gain
            }),
            winnerDistribution,
            finalMasterValidatorFeasible = request.FinalValidation.IsFeasible,
            finalMasterValidatorViolationCount = request.FinalValidation.Violations.Count,
            finalMicroFeasible = request.FinalMicroReport.IsFeasible,
            finalAcceptedSnapshotPath = Path.Combine(
                request.Layout.FinalDirectory, "final_master_snapshot.json"),
            finalCheckpointPath = request.CheckpointPath,
            finalAcceptedStateFingerprint = SnapshotFingerprint.Calculate(request.FinalSnapshot),
            initialReferenceFingerprint = request.InitialReferenceFingerprint,
            totalRuntimeSeconds = request.TotalRuntimeSeconds,
            finalTerminationReason = request.TerminationReason,
            unresolvedUnknownCount = request.UnknownEvidence.Count,
            qualityBlockedCount = request.Diagnostics.Sum(item => item.BudgetRejectedCandidateCount),
            safetyCapReachedWhileStillImproving = string.Equals(
                request.TerminationReason,
                AdaptiveSingletonTerminationReasons.SafetyCap,
                StringComparison.Ordinal) && finalRoot > 0
        };
        File.WriteAllText(
            Path.Combine(request.Layout.FinalDirectory, "final_summary.json"),
            JsonSerializer.Serialize(summary, JsonOptions), Utf8());
        File.WriteAllText(
            Path.Combine(request.Layout.FinalDirectory, "final_summary.md"),
            string.Join(Environment.NewLine,
                "# Adaptive Final Summary",
                string.Empty,
                $"- initial_root_conflicts: `{initialRoot}`",
                $"- final_root_conflicts: `{finalRoot}`",
                $"- total_reduction: `{initialRoot - finalRoot}`",
                $"- accepted_transition_count: `{transitions}`",
                $"- final_master_validator_violations: `{request.FinalValidation.Violations.Count}`",
                $"- final_micro_feasible: `{request.FinalMicroReport.IsFeasible}`",
                $"- termination_reason: `{request.TerminationReason}`",
                $"- checkpoint: `{request.CheckpointPath}`") + Environment.NewLine,
            Utf8());
        MasterSolutionSnapshotStore.Save(
            Path.Combine(request.Layout.FinalDirectory, "final_master_snapshot.json"),
            string.IsNullOrWhiteSpace(request.Manifest.InstanceName)
                ? request.Manifest.RunId
                : request.Manifest.InstanceName,
            request.FinalSnapshot);
        WriteMicroConflicts(request);
        WriteMicroSummary(request);
        new MasterSolutionValidator().WriteReport(
            request.FinalValidation,
            Path.Combine(request.Layout.FinalDirectory, "final_master_validator_report.md"));
        WriteStructuralState(request);
        WriteSupplements(request);
    }

    private static void WriteMicroConflicts(AdaptiveRunArtifactRequest request)
    {
        WriteCsv(
            Path.Combine(request.Layout.FinalDirectory, "final_micro_conflicts.csv"),
            "conflict_kind,station,first_train,second_train,station_order,start_seconds,end_seconds,resource,root_key,is_derived",
            request.FinalMicroReport.Conflicts.Select(item => string.Join(',',
                Csv(item.ConflictKind), Csv(item.StationName),
                item.FirstTrainIndex?.ToString() ?? string.Empty,
                item.SecondTrainIndex?.ToString() ?? string.Empty,
                item.StationOrder?.ToString() ?? string.Empty,
                F(item.StartSeconds), F(item.EndSeconds), Csv(item.ResourceKey),
                Csv(item.RootConflictKey), item.IsDerived)));
    }

    private static void WriteMicroSummary(AdaptiveRunArtifactRequest request)
    {
        MicroConflictMetrics metrics = MicroConflictMetricCalculator.Calculate(
            request.FinalMicroReport.Conflicts);
        MicroConflictCategoryCounts categories = MicroConflictCategoryCounter.Calculate(
            request.FinalMicroReport.Conflicts);
        object summary = new
        {
            request.FinalMicroReport.IsFeasible,
            status = request.FinalMicroReport.Status.ToString(),
            metrics.RawConflictCount,
            metrics.CanonicalConflictCount,
            metrics.RootConflictCount,
            metrics.EpisodeConflictCount,
            categories.ExactHeadwayConflictCount,
            categories.StationAssignmentConflictCount,
            conflictFingerprint = MicroConflictFingerprint.Calculate(request.FinalMicroReport.Conflicts)
        };
        File.WriteAllText(
            Path.Combine(request.Layout.FinalDirectory, "final_micro_summary.json"),
            JsonSerializer.Serialize(summary, JsonOptions), Utf8());
    }

    private static void WriteStructuralState(AdaptiveRunArtifactRequest request)
    {
        IEnumerable<string> sectionRows = request.SectionEdges.Select(item => string.Join(',',
            "section", Csv(item.SectionName), item.FirstTrainIndex, item.SecondTrainIndex,
            string.Empty, string.Empty, string.Empty));
        IEnumerable<string> stationRows = request.StationEdges.Select(item => string.Join(',',
            "station", Csv(item.StationName), item.FirstTrainIndex, item.SecondTrainIndex,
            item.FirstStationOrder, item.SecondStationOrder,
            Csv($"{item.FirstSide}->{item.SecondSide}")));
        WriteCsv(
            Path.Combine(request.Layout.FinalDirectory, "accepted_structural_edges.csv"),
            "edge_type,resource,first_train,second_train,first_station_order,second_station_order,event_orientation",
            sectionRows.Concat(stationRows));
        File.WriteAllLines(
            Path.Combine(request.Layout.FinalDirectory, "accepted_candidate_ids.txt"),
            request.AcceptedCandidateIds.Order(StringComparer.OrdinalIgnoreCase), Utf8());
    }

    private static void WriteSupplements(AdaptiveRunArtifactRequest request)
    {
        WriteCsv(
            Path.Combine(request.Layout.FinalDirectory, "supplement_summary.csv"),
            "supplement_key,seconds",
            (request.FinalSnapshot.RunningTimeSupplementSecondsByKey ??
             new Dictionary<string, double>())
            .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(item => $"{Csv(item.Key)},{F(item.Value)}"));
    }

    private static void WriteHotspot(string path, string identityHeader, IEnumerable<string> identities)
    {
        WriteCsv(
            path,
            $"{identityHeader},conflict_count",
            identities.Where(item => !string.IsNullOrWhiteSpace(item))
                .GroupBy(item => item, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => $"{Csv(group.Key)},{group.Count()}"));
    }

    private static void WriteCsv(string path, string header, IEnumerable<string> rows)
    {
        using StreamWriter writer = new(path, false, Utf8());
        writer.WriteLine(header);
        foreach (string row in rows)
        {
            writer.WriteLine(row);
        }
    }

    private static UTF8Encoding Utf8() => new(encoderShouldEmitUTF8Identifier: true);

    private static string F(double value) => value.ToString("F6", CultureInfo.InvariantCulture);

    private static string Csv(string? value)
    {
        string text = value ?? string.Empty;
        return text.IndexOfAny([',', '"', '\r', '\n']) < 0
            ? text
            : $"\"{text.Replace("\"", "\"\"")}\"";
    }

    private static string PowerShellQuote(string value) =>
        $"'{value.Replace("'", "''")}'";
}
