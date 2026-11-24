using Gurobi;
using GurobiTimetablingResearch.Domain;
using System.Security.Cryptography;
using System.Text;

namespace GurobiTimetablingResearch.Optimization;

/// <summary>
/// 指明热点作业可进入联合模型的停站模式，以及这些模式是否完整覆盖原始业务自由度。
/// </summary>
public sealed record HotspotStopModeSelection(
    IReadOnlyList<bool> IncludedModes,
    bool PreserveAllLegalStopModes);

/// <summary>
/// 依据硬停站要求和已构造的进路—股道域筛选模式；空域模式不会进入联合模型。
/// </summary>
public static class HotspotStopModePolicy
{
    public static HotspotStopModeSelection Resolve(
        StopRequirement stopRequirement,
        TrainStopRole stopRole,
        int stoppingAlternativeCount,
        int passingAlternativeCount,
        bool originalDomainProvesSingleMode = false)
    {
        if (stoppingAlternativeCount < 0 || passingAlternativeCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stoppingAlternativeCount));
        }

        bool stoppingAvailable = stoppingAlternativeCount > 0;
        bool passingAvailable = passingAlternativeCount > 0;
        if (stopRole is TrainStopRole.Origin or TrainStopRole.Destination)
        {
            return new HotspotStopModeSelection(stoppingAvailable ? [true] : [], false);
        }

        return stopRequirement switch
        {
            StopRequirement.MustStop => new HotspotStopModeSelection(stoppingAvailable ? [true] : [], false),
            StopRequirement.MustPass => new HotspotStopModeSelection(passingAvailable ? [false] : [], false),
            _ when stoppingAvailable && passingAvailable => new HotspotStopModeSelection([true, false], true),
            // 只有原始数据域已经证明单侧可行时，才把 OptionalStop 作为单侧硬域加入热点模型。
            // 临时筛选造成的单侧缺失仍然拒绝，避免把一次修复限制错误地永久化。
            StopRequirement.OptionalStop when originalDomainProvesSingleMode && stoppingAvailable =>
                new HotspotStopModeSelection([true], false),
            StopRequirement.OptionalStop when originalDomainProvesSingleMode && passingAvailable =>
                new HotspotStopModeSelection([false], false),
            StopRequirement.OptionalStop => new HotspotStopModeSelection([], false),
            _ => new HotspotStopModeSelection([], false)
        };
    }
}

internal sealed record ValidityFirstHotspotBuildResult(
    bool IsComplete,
    PromotedMicroComponent? Component,
    string Status,
    int CandidateRequirementCount,
    int ClosureRequirementCount,
    ValidityFirstRepairScopeEstimate? ScopeEstimate = null,
    IReadOnlyList<string>? CoreRequirementIds = null,
    IReadOnlyList<string>? ProtectionRequirementIds = null,
    string ScopeFingerprint = "",
    int TrainCount = 0,
    IReadOnlyList<HotspotRequirementDomainAudit>? DomainAudits = null);

internal sealed record CompleteStationMicroComponentBuildResult(
    bool IsComplete,
    PromotedMicroComponent? Component,
    string Reason,
    int RequirementCount,
    int AlternativeCount);

public sealed record HotspotRequirementDomainAudit(
    string StationName,
    string RequirementId,
    int TrainIndex,
    StopRequirement StopRequirement,
    string OriginalResolution,
    int StoppingAlternativeCount,
    int PassingAlternativeCount,
    string StoppingFailureReason,
    string PassingFailureReason,
    string IncludedModes,
    string Classification,
    string Evidence);

/// <summary>
/// 从真实 Global Micro 子问题提取热点，并使用相同域解析器构造可持久化的局部精确组件。
/// </summary>
internal static class ValidityFirstHotspotComponentBuilder
{
    internal static CompleteStationMicroComponentBuildResult BuildCompleteStationComponent(
        BasicMilpModel model,
        string stationName,
        int endpointOccupationSeconds,
        int promotionIteration,
        string source)
    {
        List<PromotedStationRequirement> requirements = [];
        foreach (TrainRecord train in model.Instance.Trains)
        foreach (TrainStopRecord stop in train.OrderedStops.Where(stop => string.Equals(
                     stop.StationName, stationName, StringComparison.OrdinalIgnoreCase)))
        {
            IReadOnlyList<PromotedRouteTrackAlternative> stopping = BuildAlternatives(
                model, train, stop, true, endpointOccupationSeconds);
            IReadOnlyList<PromotedRouteTrackAlternative> passing = BuildAlternatives(
                model, train, stop, false, endpointOccupationSeconds);
            StopModeDomainAnalysis domain = StopModeDomainResolver.Analyze(model.Instance, train, stop);
            bool originalSingleMode =
                (domain.Resolution == StopModeResolution.ForceStop && stopping.Count > 0 && passing.Count == 0) ||
                (domain.Resolution == StopModeResolution.ForcePass && stopping.Count == 0 && passing.Count > 0);
            HotspotStopModeSelection selected = HotspotStopModePolicy.Resolve(
                stop.StopRequirement, stop.StopRole, stopping.Count, passing.Count, originalSingleMode);
            if (selected.IncludedModes.Count == 0)
                return new(false, null, $"empty_complete_domain::{stop.StopId}", requirements.Count, requirements.Sum(r => r.AllowedAlternatives.Count));

            List<PromotedRouteTrackAlternative> alternatives = [];
            if (selected.IncludedModes.Contains(true)) alternatives.AddRange(stopping);
            if (selected.IncludedModes.Contains(false)) alternatives.AddRange(passing);
            requirements.Add(new(
                stop.StopId,
                stop.TrainIndex,
                stop.StationOrder,
                selected.PreserveAllLegalStopModes
                    ? stop.IdealStop
                    : selected.IncludedModes.Single(),
                alternatives.Distinct().ToArray(),
                selected.PreserveAllLegalStopModes));
        }

        if (requirements.Count == 0)
            return new(false, null, "station_has_no_requirements", 0, 0);
        PromotedMicroComponent component = new(
            $"micro_reopen::{stationName}",
            stationName,
            requirements.Select(r => r.TrainIndex).Distinct().Order().ToArray(),
            requirements,
            "whole_station_certificate_reopened",
            source,
            promotionIteration,
            1,
            true);
        return new(true, component, "complete_original_resource_domain", requirements.Count,
            requirements.Sum(r => r.AllowedAlternatives.Count));
    }

    internal static ValidityFirstHotspotBuildResult Build(
        BasicMilpModel model,
        StationLevelMicroSubproblemResult stationResult,
        int maximumRequirementCount,
        int endpointOccupationSeconds,
        int promotionIteration,
        int persistentRounds)
    {
        return Build(
            model,
            stationResult,
            new ValidityFirstRepairScopeBudget(
                maximumRequirementCount,
                int.MaxValue,
                int.MaxValue,
                int.MaxValue,
                int.MaxValue),
            endpointOccupationSeconds,
            promotionIteration,
            persistentRounds);
    }

    internal static ValidityFirstHotspotBuildResult Build(
        BasicMilpModel model,
        StationLevelMicroSubproblemResult stationResult,
        ValidityFirstRepairScopeBudget scopeBudget,
        int endpointOccupationSeconds,
        int promotionIteration,
        int persistentRounds)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(stationResult);
        ArgumentNullException.ThrowIfNull(scopeBudget);
        Dictionary<int, TrainRecord> trainByIndex = model.Instance.Trains.ToDictionary(train => train.TrainIndex);
        List<RequirementCandidate> candidates = [];
        List<HotspotRequirementDomainAudit> domainAudits = [];

        foreach (StationMicroRequirement current in stationResult.Requirements)
        {
            TrainStopRecord stop = model.Instance.TrainStops.Single(item =>
                string.Equals(item.StopId, current.StopId, StringComparison.OrdinalIgnoreCase));
            TrainRecord train = trainByIndex[stop.TrainIndex];
            IReadOnlyList<PromotedRouteTrackAlternative> stoppingAlternatives = BuildAlternatives(
                model,
                train,
                stop,
                true,
                endpointOccupationSeconds);
            IReadOnlyList<PromotedRouteTrackAlternative> passingAlternatives = BuildAlternatives(
                model,
                train,
                stop,
                false,
                endpointOccupationSeconds);
            StopModeDomainAnalysis originalDomain = current.StopModeAnalysis ??
                StopModeDomainResolver.Analyze(model.Instance, train, stop);
            bool originalDomainProvesSingleMode =
                (originalDomain.Resolution == StopModeResolution.ForceStop &&
                 stoppingAlternatives.Count > 0 && passingAlternatives.Count == 0) ||
                (originalDomain.Resolution == StopModeResolution.ForcePass &&
                 stoppingAlternatives.Count == 0 && passingAlternatives.Count > 0);
            HotspotStopModeSelection modeSelection = HotspotStopModePolicy.Resolve(
                stop.StopRequirement,
                stop.StopRole,
                stoppingAlternatives.Count,
                passingAlternatives.Count,
                originalDomainProvesSingleMode);
            string domainClassification = ResolveDomainClassification(
                originalDomain,
                stoppingAlternatives.Count,
                passingAlternatives.Count,
                modeSelection);
            domainAudits.Add(new HotspotRequirementDomainAudit(
                stop.StationName,
                stop.StopId,
                stop.TrainIndex,
                stop.StopRequirement,
                originalDomain.Resolution.ToString(),
                stoppingAlternatives.Count,
                passingAlternatives.Count,
                originalDomain.Stopping.FailureReason.ToString(),
                originalDomain.Passing.FailureReason.ToString(),
                string.Join('|', modeSelection.IncludedModes.Select(mode => mode ? "stop" : "pass")),
                domainClassification,
                originalDomain.Evidence));
            if (modeSelection.IncludedModes.Count == 0)
            {
                return new ValidityFirstHotspotBuildResult(
                    false,
                    null,
                    $"hotspot_empty_legal_domain::{stop.StopId}",
                    candidates.Count,
                    0,
                    DomainAudits: domainAudits);
            }

            List<PromotedRouteTrackAlternative> allowed = [];
            if (modeSelection.IncludedModes.Contains(true))
            {
                allowed.AddRange(stoppingAlternatives);
            }
            if (modeSelection.IncludedModes.Contains(false))
            {
                allowed.AddRange(passingAlternatives);
            }

            double arrivalLower = model.ArrivalVars[stop.StopId].Get(GRB.DoubleAttr.LB);
            double arrivalUpper = model.ArrivalVars[stop.StopId].Get(GRB.DoubleAttr.UB);
            double departureLower = model.DepartureVars[stop.StopId].Get(GRB.DoubleAttr.LB);
            double departureUpper = model.DepartureVars[stop.StopId].Get(GRB.DoubleAttr.UB);
            (double earliestOccupation, double latestOccupation) = ResolveBroadOccupationDomain(
                stop.StopRole,
                arrivalLower,
                arrivalUpper,
                departureLower,
                departureUpper,
                endpointOccupationSeconds);
            candidates.Add(new RequirementCandidate(
                current,
                new PromotedStationRequirement(
                    stop.StopId,
                    stop.TrainIndex,
                    stop.StationOrder,
                    modeSelection.PreserveAllLegalStopModes
                        ? current.IsStop
                        : modeSelection.IncludedModes.Single(),
                    allowed.Distinct().ToList(),
                    modeSelection.PreserveAllLegalStopModes),
                new HotspotRequirementDomain(
                    stop.StopId,
                    stop.StationName,
                    stop.TrainIndex,
                    earliestOccupation,
                    latestOccupation,
                    allowed.Select(item => item.TrackName)
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    allowed.SelectMany(item => new[] { item.IncomingRouteId, item.OutgoingRouteId })
                        .Where(routeId => routeId.HasValue)
                        .Select(routeId => routeId!.Value)
                        .Distinct().ToList())));
        }

        IReadOnlyList<string> seedIds = stationResult.ConflictCore.SupportStopIds.Count > 0
            ? stationResult.ConflictCore.SupportStopIds
            : candidates.Where(candidate => stationResult.ConflictCore.TrainIndices.Contains(
                    candidate.CurrentRequirement.TrainIndex))
                .Select(candidate => candidate.CurrentRequirement.StopId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        if (seedIds.Count == 0)
        {
            return new ValidityFirstHotspotBuildResult(
                false,
                null,
                "hotspot_core_has_no_requirement_support",
                candidates.Count,
                0,
                DomainAudits: domainAudits);
        }

        HotspotInteractionClosureResult closure = HotspotInteractionClosureResolver.Resolve(
            candidates.Select(candidate => candidate.Domain).ToList(),
            seedIds,
            scopeBudget.MaximumRequirementCount,
            (leading, following) => model.Instance.TryGetRouteHeadwayRule(
                leading,
                following,
                out RouteHeadwayRule? rule) && rule is not null
                ? rule.MinHeadwaySeconds
                : null);
        if (!closure.IsComplete)
        {
            return new ValidityFirstHotspotBuildResult(
                false,
                null,
                closure.Status,
                candidates.Count,
                0,
                DomainAudits: domainAudits);
        }

        HashSet<string> closureIds = closure.Requirements
            .Select(requirement => requirement.RequirementId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<PromotedStationRequirement> promotedRequirements = candidates
            .Where(candidate => closureIds.Contains(candidate.CurrentRequirement.StopId))
            .Select(candidate => candidate.PromotedRequirement)
            .OrderBy(requirement => requirement.TrainIndex)
            .ThenBy(requirement => requirement.StationOrder)
            .ToList();
        HashSet<string> canonicalSeedIds = seedIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<string> coreRequirementIds = promotedRequirements
            .Select(requirement => requirement.RequirementId)
            .Where(canonicalSeedIds.Contains)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
        IReadOnlyList<string> protectionRequirementIds = promotedRequirements
            .Select(requirement => requirement.RequirementId)
            .Where(id => !canonicalSeedIds.Contains(id))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
        ValidityFirstRepairScopeEstimate scopeEstimate = EstimateScope(model, promotedRequirements);
        ValidityFirstRepairScopeBudgetDecision budgetDecision = scopeBudget.Evaluate(scopeEstimate);
        if (!budgetDecision.Allowed)
        {
            return new ValidityFirstHotspotBuildResult(
                false,
                null,
                budgetDecision.Status,
                candidates.Count,
                promotedRequirements.Count,
                scopeEstimate,
                coreRequirementIds,
                protectionRequirementIds,
                TrainCount: promotedRequirements.Select(requirement => requirement.TrainIndex).Distinct().Count(),
                DomainAudits: domainAudits);
        }

        string componentKey = string.Join('-', promotedRequirements.Select(requirement => requirement.RequirementId));
        PromotedMicroComponent component = new(
            $"validity_hotspot::{stationResult.StationName}::{componentKey}",
            stationResult.StationName,
            promotedRequirements.Select(requirement => requirement.TrainIndex).Distinct().Order().ToList(),
            promotedRequirements,
            "validity_first_complete_interaction_closure",
            $"{stationResult.ConflictCore.PrimaryConflictKind}::{string.Join('|', seedIds)}",
            promotionIteration,
            persistentRounds,
            true);
        string scopeFingerprint = BuildScopeFingerprint(
            stationResult.StationName,
            coreRequirementIds,
            protectionRequirementIds,
            promotedRequirements);
        return new ValidityFirstHotspotBuildResult(
            true,
            component,
            budgetDecision.Status,
            candidates.Count,
            promotedRequirements.Count,
            scopeEstimate,
            coreRequirementIds,
            protectionRequirementIds,
            scopeFingerprint,
            promotedRequirements.Select(requirement => requirement.TrainIndex).Distinct().Count(),
            domainAudits);
    }

    private static string ResolveDomainClassification(
        StopModeDomainAnalysis originalDomain,
        int stoppingAlternativeCount,
        int passingAlternativeCount,
        HotspotStopModeSelection selection)
    {
        if (originalDomain.Resolution == StopModeResolution.Infeasible)
        {
            return "original_business_or_data_incompatible";
        }
        if (originalDomain.Resolution is StopModeResolution.ForceStop or StopModeResolution.ForcePass &&
            selection.IncludedModes.Count == 1)
        {
            return "original_domain_proven_single_mode";
        }
        if (originalDomain.Resolution == StopModeResolution.Flexible &&
            stoppingAlternativeCount > 0 && passingAlternativeCount > 0)
        {
            return "complete_flexible_domain";
        }
        return "route_track_or_temporary_filter_mismatch";
    }

    private static ValidityFirstRepairScopeEstimate EstimateScope(
        BasicMilpModel model,
        IReadOnlyList<PromotedStationRequirement> requirements)
    {
        int alternativeCount = requirements.Sum(requirement => requirement.AllowedAlternatives.Count);
        PromotedMicroCompactRelationEstimate compact =
            PromotedMicroCompactRelationEstimator.Estimate(
                requirements,
                (leading, following) => model.Instance.TryGetRouteHeadwayRule(
                    leading,
                    following,
                    out RouteHeadwayRule? rule) && rule is not null);
        int relationCount = compact.ConditionalConstraintCount / 2;
        long estimatedVariables = (long)alternativeCount + compact.OrderVariableCount;
        long legacyEstimatedVariables = (long)alternativeCount + compact.LegacyOrderVariableCount;
        long fixedStopConstraints = requirements.Count(requirement =>
            !PromotedStopModeLinkPlanBuilder.Build(requirement).IsFlexible);
        long estimatedConstraints = 2L * requirements.Count +
                                    compact.ConditionalConstraintCount +
                                    fixedStopConstraints;
        return new ValidityFirstRepairScopeEstimate(
            requirements.Count,
            alternativeCount,
            checked((int)Math.Min(estimatedVariables, int.MaxValue)),
            checked((int)Math.Min(estimatedConstraints, int.MaxValue)),
            relationCount,
            compact.OrderVariableCount,
            checked((int)Math.Min(legacyEstimatedVariables, int.MaxValue)),
            compact.LegacyOrderVariableCount);
    }

    private static string BuildScopeFingerprint(
        string stationName,
        IReadOnlyList<string> coreRequirementIds,
        IReadOnlyList<string> protectionRequirementIds,
        IReadOnlyList<PromotedStationRequirement> requirements)
    {
        string canonical = string.Join('|',
            stationName,
            string.Join(';', coreRequirementIds),
            string.Join(';', protectionRequirementIds),
            string.Join(';', requirements.Select(requirement =>
                $"{requirement.RequirementId}:{string.Join(',', requirement.AllowedAlternatives
                    .OrderBy(alternative => alternative.TrackName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(alternative => alternative.IncomingRouteId)
                    .ThenBy(alternative => alternative.OutgoingRouteId)
                    .Select(alternative => $"{alternative.TrackName}/{alternative.IncomingRouteId}/{alternative.OutgoingRouteId}/{alternative.IsStop}"))}")));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static IReadOnlyList<PromotedRouteTrackAlternative> BuildAlternatives(
        BasicMilpModel model,
        TrainRecord train,
        TrainStopRecord stop,
        bool isStop,
        int endpointOccupationSeconds)
    {
        StationMicroRequirement requirement = StationLevelMicroFeasibilitySlave.BuildRequirementForMode(
            model,
            train,
            stop,
            isStop);
        StationOccupationInterval occupation = StationEndpointOccupationPolicy.Resolve(
            requirement.StopRole,
            requirement.IsStop,
            requirement.ArrivalSeconds,
            requirement.DepartureSeconds,
            endpointOccupationSeconds,
            0.0,
            86400.0);
        StationAssignmentRequirement assignmentRequirement = new(
            requirement.StopId,
            requirement.TrainIndex,
            requirement.IsStop,
            requirement.ArrivalSeconds,
            requirement.DepartureSeconds,
            occupation.StartSeconds,
            occupation.EndSeconds,
            requirement.IncomingRoutes,
            requirement.OutgoingRoutes,
            requirement.FeasibleTracks);
        return StationRouteTrackAssignmentSolver.BuildAlternatives(assignmentRequirement)
            .Select(alternative => new PromotedRouteTrackAlternative(
                alternative.TrackName,
                alternative.IncomingRoute?.RouteId,
                alternative.OutgoingRoute?.RouteId,
                isStop))
            .Distinct()
            .ToList();
    }

    private static (double Earliest, double Latest) ResolveBroadOccupationDomain(
        TrainStopRole role,
        double arrivalLower,
        double arrivalUpper,
        double departureLower,
        double departureUpper,
        int endpointOccupationSeconds) => role switch
        {
            TrainStopRole.Origin => (
                departureLower - endpointOccupationSeconds,
                departureUpper),
            TrainStopRole.Destination => (
                arrivalLower,
                arrivalUpper + endpointOccupationSeconds),
            _ => (arrivalLower, departureUpper)
        };

    private sealed record RequirementCandidate(
        StationMicroRequirement CurrentRequirement,
        PromotedStationRequirement PromotedRequirement,
        HotspotRequirementDomain Domain);
}
