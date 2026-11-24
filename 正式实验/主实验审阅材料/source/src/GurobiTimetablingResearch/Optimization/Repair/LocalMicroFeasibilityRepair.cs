// 局部微观修复从冲突报告中选择压力最高的车站，提取相关列车和可替代径路股道，并形成小规模修复目标。
// 决策应用器仅把经微观子问题验证的替代选择写回主模型快照，未改善结果保持原状态。
namespace GurobiTimetablingResearch.Optimization;

internal sealed record LocalMicroRepairTarget(
    string StationName,
    IReadOnlySet<int> CoreTrainIndices,
    int BaselineFocusedConflictCount,
    MicroSolveStatus BaselineStatus,
    int PersistentRounds,
    bool ProvenStationCore);

public sealed record LocalMicroRepairDiagnostic(
    int IterationIndex,
    string StationName,
    int RecourseWindowSeconds,
    string SolverStatus,
    string Outcome,
    int ReleasedTrainCount,
    int AlternativeCount,
    int ConditionalConstraintCount,
    int GlobalConflictCountBefore,
    int? GlobalConflictCountAfter,
    int FocusedConflictCountBefore,
    int? FocusedConflictCountAfter,
    double TotalRetimingSeconds,
    double MaximumRetimingSeconds,
    IReadOnlyList<LocalMicroRepairSelectedAlternative> SelectedAlternatives);

internal static class LocalMicroRepairStationSelector
{
    internal static LocalMicroRepairTarget? Select(
        StationLevelMicroFeasibilitySlaveResult result,
        IReadOnlyDictionary<string, int>? persistentRoundsByStation = null,
        int persistenceThreshold = 2)
    {
        persistentRoundsByStation ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        StationLevelMicroSubproblemResult? selected = result.StationResults
            .Where(station => station.Conflicts.Count > 0 && (
                FeedbackArbitrationPolicy.Resolve(new FeedbackEvidence(
                    FeedbackType.StationAssignmentCore,
                    HasProvenJointAssignmentConflict(station) ? EvidenceType.ProvenInfeasibleCore : EvidenceType.SearchLimitUnknown,
                    HasProvenJointAssignmentConflict(station),
                    persistentRoundsByStation.GetValueOrDefault(station.StationName, 0),
                    true)) == FeedbackAction.StationCoreOrPromotion ||
                persistentRoundsByStation.GetValueOrDefault(station.StationName, 0) >= persistenceThreshold))
            .OrderByDescending(station => HasProvenJointAssignmentConflict(station))
            .ThenByDescending(station => station.ConflictCore.ConflictCount)
            .ThenByDescending(station => station.Conflicts.Count)
            .ThenBy(station => station.StationName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(station => station.StationName, StringComparer.Ordinal)
            .FirstOrDefault();
        if (selected is null)
        {
            return null;
        }

        HashSet<int> trainIndices = selected.ConflictCore.TrainIndices.ToHashSet();
        foreach (MicroConflictExplanation conflict in selected.Conflicts)
        {
            if (conflict.FirstTrainIndex.HasValue)
            {
                trainIndices.Add(conflict.FirstTrainIndex.Value);
            }
            if (conflict.SecondTrainIndex.HasValue)
            {
                trainIndices.Add(conflict.SecondTrainIndex.Value);
            }
        }
        if (trainIndices.Count == 0)
        {
            trainIndices.UnionWith(selected.Requirements.Select(requirement => requirement.TrainIndex));
        }

        bool provenStationCore = HasProvenJointAssignmentConflict(selected);
        return new LocalMicroRepairTarget(
            selected.StationName,
            trainIndices,
            selected.Conflicts.Count,
            selected.AssignmentResult.Status,
            persistentRoundsByStation.GetValueOrDefault(selected.StationName, 0),
            provenStationCore);
    }

    private static bool HasProvenJointAssignmentConflict(StationLevelMicroSubproblemResult station) =>
        station.AssignmentResult.Status == MicroSolveStatus.ProvenInfeasible &&
        station.Conflicts.Any(conflict => string.Equals(
            conflict.ConflictKind,
            "station_route_track_assignment_infeasible",
            StringComparison.OrdinalIgnoreCase));
}

internal sealed record LocalMicroRepairEvaluation(
    bool Handled,
    bool Accepted,
    BasicMilpModel? Model,
    MasterSolutionSnapshot? Snapshot,
    MicroFeasibilityReport? Report,
    IReadOnlyList<LocalMicroRepairDiagnostic> Diagnostics,
    PromotedMicroComponent? PromotedComponent = null);

internal static class LocalMicroRepairStopDecisionApplier
{
    internal static void FixAll(BasicMilpModel model, MasterSolutionSnapshot snapshot)
    {
        foreach ((string stopId, int value) in snapshot.StopByStopId)
        {
            if (!model.StopVars.TryGetValue(stopId, out Gurobi.GRBVar? variable))
            {
                continue;
            }

            variable.Set(Gurobi.GRB.DoubleAttr.LB, value);
            variable.Set(Gurobi.GRB.DoubleAttr.UB, value);
        }
    }
}
