// 修复决策空间审计器比较快照固定值、变量上下界和计划释放集合，确认每一层实际获得了预期自由度。
// 准备器先应用分层邻域及依赖 Overlay，再输出按决策类型统计的可变变量数量，避免名义解冻但模型仍被固定。
using Gurobi;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

public sealed record RepairDecisionSpaceAuditResult(
    RepairDecisionLevel DecisionLevel,
    int ReleasedTimeVariableCount,
    int ReleasedRunningSupplementVariableCount,
    int ReleasedSectionPrecedenceVariableCount,
    int ReleasedStationEventPrecedenceVariableCount,
    int ReleasedStopVariableCount,
    int? AffectedMinimumStationOrder,
    int? AffectedMaximumStationOrder,
    IReadOnlyList<string> Violations)
{
    public bool IsValid => Violations.Count == 0;

    public void ThrowIfInvalid()
    {
        if (!IsValid)
        {
            throw new InvalidOperationException(
                "Repair decision-space audit failed: " + string.Join(" | ", Violations));
        }
    }
}

internal sealed record HierarchicalRepairAttemptPreparation(
    TemporalRecourseApplicationResult? RecourseApplication,
    RepairDecisionSpaceAuditResult? DecisionSpaceAudit);

internal static class HierarchicalRepairAttemptDecisionSpacePreparer
{
    internal static HierarchicalRepairAttemptPreparation Prepare(
        BasicMilpModel model,
        MasterSolutionSnapshot snapshot,
        RepairDecisionNeighborhood neighborhood,
        double repairRatio,
        int recourseWindowSeconds,
        bool applyTemporalRecourse)
    {
        StructuralRepairNeighborhoodApplier.ApplyDecisionNeighborhood(
            model,
            snapshot,
            neighborhood,
            repairRatio);

        TemporalRecourseApplicationResult? recourseApplication = applyTemporalRecourse
            ? TemporalRecourseNeighborhoodApplier.Apply(
                model,
                snapshot,
                neighborhood,
                recourseWindowSeconds)
            : null;
        if (recourseApplication?.HasEmptyIntersection == true)
        {
            return new HierarchicalRepairAttemptPreparation(recourseApplication, null);
        }

        // Gurobi 会延迟应用 LB/UB 修改；先 Update，再审计实际送入 Optimize 的完整变量边界。
        model.Model.Update();
        IReadOnlySet<string> scopedSupplementKeys =
            RepairDecisionSpaceAuditor.ResolveScopedSupplementKeys(model, neighborhood);
        RepairDecisionSpaceAuditResult audit = RepairDecisionSpaceAuditor.Audit(
            model,
            snapshot,
            neighborhood,
            scopedSupplementKeys);
        audit.ThrowIfInvalid();
        return new HierarchicalRepairAttemptPreparation(recourseApplication, audit);
    }
}

public static class RepairDecisionSpaceAuditor
{
    private const double BoundTolerance = 1e-6;

    public static RepairDecisionSpaceAuditResult Audit(
        BasicMilpModel model,
        MasterSolutionSnapshot snapshot,
        RepairDecisionNeighborhood neighborhood,
        IReadOnlySet<string> scopedSupplementKeys)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(neighborhood);
        ArgumentNullException.ThrowIfNull(scopedSupplementKeys);

        List<string> violations = [];
        IisDependencyOverlay overlay = RepairDecisionDependencyOverlayResolver.Resolve(neighborhood);
        int releasedTimes = 0;
        foreach (TrainStopRecord stop in model.Instance.TrainStops)
        {
            releasedTimes += AuditTimeVariable(
                model.ArrivalVars[stop.StopId], stop, "arrival", $"arrival::{stop.StopId}",
                stop.EarliestArrSeconds, stop.LatestArrSeconds, neighborhood, overlay, violations);
            releasedTimes += AuditTimeVariable(
                model.DepartureVars[stop.StopId], stop, "departure", $"departure::{stop.StopId}",
                stop.EarliestDepSeconds, stop.LatestDepSeconds, neighborhood, overlay, violations);
        }

        int releasedSupplements = 0;
        foreach ((string key, GRBVar variable) in model.RunningTimeSupplementVars)
        {
            bool isFree = IsFree(variable);
            if (isFree)
            {
                releasedSupplements++;
                if (neighborhood.DecisionLevel < RepairDecisionLevel.TimeAndRunningSupplement ||
                    (!scopedSupplementKeys.Contains(key) && !overlay.SupplementSectionKeys.Contains(key)))
                {
                    violations.Add($"running supplement '{key}' is free outside the approved level/scope");
                }
                TrainSectionRepairContext section = TrainSectionRepairContextResolver.Resolve(model.Instance, key);
                double maximumAllowedSupplementSeconds = RunningTimeSupplementPolicy.Create(model.RunningTimeSupplementRatio)
                    .ResolveMaximumSeconds(section.BaseRuntimeSeconds);
                if (variable.Get(GRB.DoubleAttr.LB) < -BoundTolerance ||
                    variable.Get(GRB.DoubleAttr.UB) > maximumAllowedSupplementSeconds + BoundTolerance)
                {
                    violations.Add($"running supplement '{key}' exceeds the configured repair-ratio bounds");
                }
            }
            else
            {
                double expected = snapshot.RunningTimeSupplementSecondsByKey?.GetValueOrDefault(key, 0.0) ?? 0.0;
                if (Math.Abs(variable.Get(GRB.DoubleAttr.LB) - expected) > BoundTolerance)
                {
                    violations.Add($"running supplement '{key}' is fixed away from snapshot value {expected}");
                }
            }
        }

        int releasedSectionPrecedence = 0;
        foreach (SectionConflictCandidate candidate in model.SectionConflictCandidates)
        {
            string key = BasicMilpModelBuilder.BuildSectionPrecedenceKey(candidate);
            if (!model.SectionPrecedenceVars.TryGetValue(key, out GRBVar? variable) || !IsFree(variable))
            {
                continue;
            }
            releasedSectionPrecedence++;
            bool inside =
                StructuralRepairNeighborhoodApplier.IsInsideSpan(neighborhood, candidate.FirstTrainIndex, candidate.FirstStopOrder) &&
                StructuralRepairNeighborhoodApplier.IsInsideSpan(neighborhood, candidate.FirstTrainIndex, candidate.FirstEndStopOrder) &&
                StructuralRepairNeighborhoodApplier.IsInsideSpan(neighborhood, candidate.SecondTrainIndex, candidate.SecondStopOrder) &&
                StructuralRepairNeighborhoodApplier.IsInsideSpan(neighborhood, candidate.SecondTrainIndex, candidate.SecondEndStopOrder);
            if (!overlay.SectionPrecedenceKeys.Contains(key) &&
                !RepairDecisionReleasePolicy.ReleasesPrecedence(neighborhood.DecisionLevel, inside))
            {
                violations.Add($"section precedence '{key}' is free outside the approved level/span");
            }
        }

        int releasedStationPrecedence = 0;
        foreach (StationEventConflictCandidate candidate in model.StationEventConflictCandidates)
        {
            string key = BasicMilpModelBuilder.BuildStationEventPrecedenceKey(candidate);
            if (!model.StationEventPrecedenceVars.TryGetValue(key, out GRBVar? variable) || !IsFree(variable))
            {
                continue;
            }
            releasedStationPrecedence++;
            bool inside =
                StructuralRepairNeighborhoodApplier.IsInsideSpan(neighborhood, candidate.FirstTrainIndex, candidate.FirstStationOrder) &&
                StructuralRepairNeighborhoodApplier.IsInsideSpan(neighborhood, candidate.SecondTrainIndex, candidate.SecondStationOrder);
            if (!overlay.StationEventPrecedenceKeys.Contains(key) &&
                !RepairDecisionReleasePolicy.ReleasesPrecedence(neighborhood.DecisionLevel, inside))
            {
                violations.Add($"station-event precedence '{key}' is free outside the approved level/span");
            }
        }

        int releasedStops = 0;
        foreach (TrainStopRecord stop in model.Instance.TrainStops)
        {
            GRBVar variable = model.StopVars[stop.StopId];
            if (!IsFree(variable))
            {
                continue;
            }
            releasedStops++;
            bool inside = StructuralRepairNeighborhoodApplier.IsInsideSpan(
                neighborhood, stop.TrainIndex, stop.StationOrder);
            bool overlayApproved = overlay.FlexibleStopKeys.Contains(stop.StopId) &&
                                   stop.StopRequirement == StopRequirement.OptionalStop &&
                                   stop.StopRole == TrainStopRole.Intermediate;
            if (!overlayApproved && !RepairDecisionReleasePolicy.ReleasesStop(
                    neighborhood.DecisionLevel, stop.StopRequirement, stop.StopRole, inside))
            {
                violations.Add(
                    $"stop '{stop.StopId}' ({stop.StopRequirement}/{stop.StopRole}) is free outside the approved level/span");
            }
        }

        AuditOvertakingClosure(model, overlay, violations);

        int? minimum = neighborhood.SpansByTrainIndex.Count == 0
            ? null
            : neighborhood.SpansByTrainIndex.Values.Min(span => span.MinimumStationOrder);
        int? maximum = neighborhood.SpansByTrainIndex.Count == 0
            ? null
            : neighborhood.SpansByTrainIndex.Values.Max(span => span.MaximumStationOrder);
        return new RepairDecisionSpaceAuditResult(
            neighborhood.DecisionLevel,
            releasedTimes,
            releasedSupplements,
            releasedSectionPrecedence,
            releasedStationPrecedence,
            releasedStops,
            minimum,
            maximum,
            violations);
    }

    internal static IReadOnlySet<string> ResolveScopedSupplementKeys(
        BasicMilpModel model,
        RepairDecisionNeighborhood neighborhood)
    {
        HashSet<string> keys = new(StringComparer.OrdinalIgnoreCase);
        if (neighborhood.DecisionLevel < RepairDecisionLevel.TimeAndRunningSupplement)
        {
            return keys;
        }
        foreach (string key in model.RunningTimeSupplementVars.Keys)
        {
            if (RepairDecisionDependencyOverlayResolver.Resolve(neighborhood)
                .SupplementSectionKeys.Contains(key))
            {
                keys.Add(key);
                continue;
            }
            TrainSectionRepairContext section = TrainSectionRepairContextResolver.Resolve(model.Instance, key);
            if (StructuralRepairNeighborhoodApplier.IsInsideSpan(
                    neighborhood, section.TrainIndex, section.FromStationOrder) &&
                StructuralRepairNeighborhoodApplier.IsInsideSpan(
                    neighborhood, section.TrainIndex, section.ToStationOrder))
            {
                keys.Add(key);
            }
        }
        return keys;
    }

    private static int AuditTimeVariable(
        GRBVar variable,
        TrainStopRecord stop,
        string family,
        string eventKey,
        double originalLowerBound,
        double originalUpperBound,
        RepairDecisionNeighborhood neighborhood,
        IisDependencyOverlay overlay,
        ICollection<string> violations)
    {
        double lower = variable.Get(GRB.DoubleAttr.LB);
        double upper = variable.Get(GRB.DoubleAttr.UB);
        if (lower < originalLowerBound - BoundTolerance || upper > originalUpperBound + BoundTolerance)
        {
            violations.Add($"{family} time '{stop.StopId}' exceeds its original hard bounds");
        }
        if (!IsFree(variable))
        {
            return 0;
        }
        if (!StructuralRepairNeighborhoodApplier.IsInsideSpan(
                neighborhood, stop.TrainIndex, stop.StationOrder) &&
            !overlay.TemporalEventKeys.Contains(eventKey))
        {
            violations.Add($"{family} time '{stop.StopId}' is free outside the geographic span");
        }
        return 1;
    }

    private static void AuditOvertakingClosure(
        BasicMilpModel model,
        IisDependencyOverlay overlay,
        ICollection<string> violations)
    {
        if (overlay.SectionPrecedenceKeys.Count == 0)
        {
            return;
        }
        IisModelDependencyCatalog catalog = IisModelDependencyCatalog.Build(model);
        foreach (OvertakingDependencyPair pair in catalog.OvertakingPairs)
        {
            bool incomingOverlay = overlay.SectionPrecedenceKeys.Contains(pair.IncomingSectionPrecedenceKey);
            bool outgoingOverlay = overlay.SectionPrecedenceKeys.Contains(pair.OutgoingSectionPrecedenceKey);
            if (!incomingOverlay && !outgoingOverlay)
            {
                continue;
            }
            bool incomingFree = model.SectionPrecedenceVars.TryGetValue(
                pair.IncomingSectionPrecedenceKey, out GRBVar? incoming) && IsFree(incoming);
            bool outgoingFree = model.SectionPrecedenceVars.TryGetValue(
                pair.OutgoingSectionPrecedenceKey, out GRBVar? outgoing) && IsFree(outgoing);
            if (!incomingFree || !outgoingFree)
            {
                violations.Add(
                    $"overtaking dependency at '{pair.StationName}' lacks paired free section precedence decisions");
            }
        }
    }

    private static bool IsFree(GRBVar variable) =>
        variable.Get(GRB.DoubleAttr.UB) - variable.Get(GRB.DoubleAttr.LB) > BoundTolerance;
}
