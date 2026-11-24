// 依赖扩张规划器把 IIS 分类结果投影到具体列车、停站、区间和先后关系，并计算下一步最小释放集合。
// 规划时利用实例的停站顺序和冲突邻接关系补齐直接依赖，输出可幂等合并的 DependencyExpansionPlan。
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

internal sealed record IisGuidedDependencyExpansionRequest(
    HeadwayConflictCandidate Candidate,
    IReadOnlyList<StructuralIisElement> IisElements,
    IisDependencyOverlay CurrentOverlay,
    IisModelDependencyCatalog Catalog,
    MasterSolutionSnapshot Snapshot,
    RepairDecisionLevel RepairLevel,
    int BaseWindowSeconds,
    int ExpansionRound,
    IisGuidedRepairOptions Options);

internal static class IisGuidedDependencyExpansionPlanner
{
    internal static DependencyExpansionPlan Build(IisGuidedDependencyExpansionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RepairLevel != RepairDecisionLevel.FlexibleStop)
        {
            throw new InvalidOperationException("IIS-guided closure currently supports singleton FlexibleStop only.");
        }
        if (!request.Options.Enabled)
        {
            return new DependencyExpansionPlan(
                Array.Empty<IisDependencyRelease>(), false, Array.Empty<string>(), "iis_guided_repair_disabled");
        }
        if (request.ExpansionRound <= 0 || request.ExpansionRound > request.Options.MaxExpansionRounds)
        {
            throw new ArgumentOutOfRangeException(nameof(request.ExpansionRound));
        }

        List<IisDependencyRelease> releases = [];
        List<IisDependencyRelease> blockedDependencies = [];
        HashSet<string> unsupported = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> derivedAuxiliaries = new(StringComparer.OrdinalIgnoreCase);
        bool blockedByHardConstraint = false;
        foreach (StructuralIisElement element in request.IisElements)
        {
            switch (element.BlockerKind)
            {
                case IisBlockerKind.FixedSectionPrecedence:
                    AddSectionDependency(request, element, releases, derivedAuxiliaries);
                    break;
                case IisBlockerKind.FixedStationEventPrecedence:
                    AddStationEventDependency(request, element, releases);
                    break;
                case IisBlockerKind.RepairTemporalLowerBound:
                case IisBlockerKind.RepairTemporalUpperBound:
                    AddTemporalDependency(request, element, releases);
                    break;
                case IisBlockerKind.FixedStopDecision:
                    blockedByHardConstraint |= !AddStopDependency(
                        request, element, releases, blockedDependencies);
                    break;
                case IisBlockerKind.RunningSupplementUpperBound:
                case IisBlockerKind.RunningSupplementLowerBound:
                    AddSupplementDependency(request, element, releases);
                    break;
                case IisBlockerKind.OriginalEarliestLatestBound:
                    blockedByHardConstraint = true;
                    blockedDependencies.Add(CreateBlockedRelease(
                        request, element, IisDependencyType.TemporalEvent,
                        "original_earliest_latest_bound"));
                    break;
                case IisBlockerKind.StructuralCandidateConstraint:
                case IisBlockerKind.AcceptedStructuralConstraint:
                case IisBlockerKind.RunningTimeBaseConstraint:
                case IisBlockerKind.NormalDwellConstraint:
                case IisBlockerKind.WaitingDwellConstraint:
                case IisBlockerKind.BeingOvertakenConstraint:
                case IisBlockerKind.StationQuotaConstraint:
                case IisBlockerKind.TravelTimeConstraint:
                case IisBlockerKind.MaintenanceConstraint:
                case IisBlockerKind.HeadwayConstraint:
                case IisBlockerKind.OtherHardConstraint:
                case IisBlockerKind.VariableOriginalLB:
                case IisBlockerKind.VariableOriginalUB:
                case IisBlockerKind.Unknown:
                    unsupported.Add(element.ElementName);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(element.BlockerKind));
            }
        }

        IReadOnlyList<IisDependencyRelease> distinct = releases
            .GroupBy(item => item.Identity, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.ExtraTemporalSeconds).First())
            .OrderBy(item => item.DependencyType)
            .ThenBy(item => item.VariableKey, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new DependencyExpansionPlan(
            distinct,
            blockedByHardConstraint,
            unsupported.Order(StringComparer.OrdinalIgnoreCase).ToList(),
            distinct.Count > 0 ? "iis_primary_dependency_expansion" : "iis_no_expandable_primary_dependency",
            derivedAuxiliaries,
            blockedDependencies);
    }

    private static void AddSectionDependency(
        IisGuidedDependencyExpansionRequest request,
        StructuralIisElement element,
        ICollection<IisDependencyRelease> releases,
        ISet<string> derivedAuxiliaries)
    {
        SectionDependencyDecision? direct = request.Catalog.SectionDecisions.FirstOrDefault(item =>
            string.Equals(item.Key, element.VariableKey, StringComparison.OrdinalIgnoreCase));
        if (direct is null || !IsLocallyConnected(request.Candidate, direct))
        {
            return;
        }
        AddRelease(request, element, releases, IisDependencyType.SectionPrecedence, direct.Key,
            "direct_fixed_section_precedence", direct.FirstTrainIndex, direct.SecondTrainIndex,
            direct.StationNames.FirstOrDefault(name => string.Equals(
                name, ResolveCandidateStation(request.Candidate), StringComparison.OrdinalIgnoreCase)) ?? string.Empty,
            direct.SectionName);

        Queue<string> pendingPrecedenceKeys = new();
        HashSet<string> discoveredPrecedenceKeys = new(StringComparer.OrdinalIgnoreCase) { direct.Key };
        pendingPrecedenceKeys.Enqueue(direct.Key);
        while (pendingPrecedenceKeys.Count > 0)
        {
            string currentPrecedenceKey = pendingPrecedenceKeys.Dequeue();
            foreach (OvertakingDependencyPair pair in request.Catalog.OvertakingPairs.Where(item =>
                         string.Equals(item.IncomingSectionPrecedenceKey, currentPrecedenceKey, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(item.OutgoingSectionPrecedenceKey, currentPrecedenceKey, StringComparison.OrdinalIgnoreCase)))
            {
                SectionDependencyDecision? incoming = request.Catalog.SectionDecisions.FirstOrDefault(item =>
                    string.Equals(item.Key, pair.IncomingSectionPrecedenceKey, StringComparison.OrdinalIgnoreCase));
                SectionDependencyDecision? outgoing = request.Catalog.SectionDecisions.FirstOrDefault(item =>
                    string.Equals(item.Key, pair.OutgoingSectionPrecedenceKey, StringComparison.OrdinalIgnoreCase));
                if (incoming is null || outgoing is null)
                {
                    continue;
                }
                AddPairedSectionDependency(incoming, "paired_overtaking_incoming_precedence");
                AddPairedSectionDependency(outgoing, "paired_overtaking_outgoing_precedence");
                foreach (string auxiliary in pair.DerivedAuxiliaryKeys)
                {
                    derivedAuxiliaries.Add(auxiliary);
                }

                void AddPairedSectionDependency(SectionDependencyDecision decision, string reason)
                {
                    if (!discoveredPrecedenceKeys.Add(decision.Key))
                    {
                        return;
                    }
                    AddRelease(request, element, releases, IisDependencyType.SectionPrecedence, decision.Key,
                        reason, decision.FirstTrainIndex, decision.SecondTrainIndex,
                        pair.StationName, decision.SectionName);
                    pendingPrecedenceKeys.Enqueue(decision.Key);
                }
            }
        }
    }

    private static void AddStationEventDependency(
        IisGuidedDependencyExpansionRequest request,
        StructuralIisElement element,
        ICollection<IisDependencyRelease> releases)
    {
        StationEventDependencyDecision? decision = request.Catalog.StationEventDecisions.FirstOrDefault(item =>
            string.Equals(item.Key, element.VariableKey, StringComparison.OrdinalIgnoreCase));
        if (decision is null ||
            !SharesCandidateTrain(request.Candidate, decision.FirstTrainIndex, decision.SecondTrainIndex) ||
            !string.Equals(decision.StationName, ResolveCandidateStation(request.Candidate), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        AddRelease(request, element, releases, IisDependencyType.StationEventPrecedence, decision.Key,
            "direct_fixed_station_event_precedence", decision.FirstTrainIndex, decision.SecondTrainIndex,
            decision.StationName, eventName: $"{decision.FirstSide}->{decision.SecondSide}",
            stationOrder: decision.FirstStationOrder);
    }

    private static void AddTemporalDependency(
        IisGuidedDependencyExpansionRequest request,
        StructuralIisElement element,
        ICollection<IisDependencyRelease> releases)
    {
        TemporalDependencyEvent? temporal = request.Catalog.TemporalEvents.FirstOrDefault(item =>
            string.Equals(item.Key, element.VariableKey, StringComparison.OrdinalIgnoreCase));
        if (temporal is null)
        {
            return;
        }
        int currentExtra = request.CurrentOverlay.ExtraTemporalSecondsByEventKey
            .GetValueOrDefault(temporal.Key, 0);
        int nextExtra = Math.Min(
            request.Options.MaxExtraTemporalSeconds,
            currentExtra + request.Options.TemporalExpansionStepSeconds);
        if (nextExtra <= currentExtra)
        {
            return;
        }
        AddRelease(request, element, releases, IisDependencyType.TemporalEvent, temporal.Key,
            "targeted_repair_temporal_bound", temporal.TrainIndex, null,
            temporal.StationName, eventName: temporal.EventType,
            stationOrder: temporal.StationOrder, extraTemporalSeconds: nextExtra);
    }

    private static bool AddStopDependency(
        IisGuidedDependencyExpansionRequest request,
        StructuralIisElement element,
        ICollection<IisDependencyRelease> releases,
        ICollection<IisDependencyRelease> blockedDependencies)
    {
        StopDependencyDecision? stop = request.Catalog.StopDecisions.FirstOrDefault(item =>
            string.Equals(item.StopId, element.VariableKey, StringComparison.OrdinalIgnoreCase));
        if (stop is null)
        {
            return true;
        }
        if (stop.Requirement != StopRequirement.OptionalStop ||
            stop.Role != TrainStopRole.Intermediate)
        {
            blockedDependencies.Add(CreateBlockedRelease(
                request,
                element,
                IisDependencyType.FlexibleStop,
                "hard_stop_requirement",
                stop.TrainIndex,
                stop.StationName,
                stop.StationOrder));
            return false;
        }
        AddRelease(request, element, releases, IisDependencyType.FlexibleStop, stop.StopId,
            "direct_flexible_stop", stop.TrainIndex, null, stop.StationName,
            eventName: "stop", stationOrder: stop.StationOrder);
        return true;
    }

    private static IisDependencyRelease CreateBlockedRelease(
        IisGuidedDependencyExpansionRequest request,
        StructuralIisElement element,
        IisDependencyType type,
        string reason,
        int? trainIndex = null,
        string stationName = "",
        int? stationOrder = null) => new(
            type,
            element.VariableKey,
            "IIS",
            element.ElementName,
            request.ExpansionRound,
            reason,
            TrainI: trainIndex,
            Station: stationName,
            Event: element.EventType,
            StationOrder: stationOrder,
            WasAlreadyReleased: false,
            WillRelease: false,
            BlockedByHardRule: true);

    private static void AddSupplementDependency(
        IisGuidedDependencyExpansionRequest request,
        StructuralIisElement element,
        ICollection<IisDependencyRelease> releases)
    {
        SupplementDependencyDecision? supplement = request.Catalog.SupplementDecisions.FirstOrDefault(item =>
            string.Equals(item.Key, element.VariableKey, StringComparison.OrdinalIgnoreCase));
        if (supplement is null ||
            (supplement.TrainIndex != request.Candidate.FirstTrainIndex &&
             supplement.TrainIndex != request.Candidate.SecondTrainIndex &&
             !element.TrainIndices.Contains(supplement.TrainIndex)))
        {
            return;
        }
        AddRelease(request, element, releases, IisDependencyType.RunningSupplement, supplement.Key,
            "direct_running_supplement_bound", supplement.TrainIndex, null,
            sectionName: supplement.SectionName, stationOrder: supplement.FromStationOrder);
    }

    private static void AddRelease(
        IisGuidedDependencyExpansionRequest request,
        StructuralIisElement element,
        ICollection<IisDependencyRelease> releases,
        IisDependencyType type,
        string key,
        string reason,
        int? trainI,
        int? trainJ,
        string stationName = "",
        string sectionName = "",
        string eventName = "",
        int? stationOrder = null,
        int extraTemporalSeconds = 0)
    {
        bool alreadyReleased = request.CurrentOverlay.Contains(type, key);
        releases.Add(new IisDependencyRelease(
            type,
            key,
            "IIS",
            element.ElementName,
            request.ExpansionRound,
            reason,
            extraTemporalSeconds,
            trainI,
            trainJ,
            stationName,
            sectionName,
            eventName,
            stationOrder,
            alreadyReleased,
            WillRelease: !alreadyReleased ||
                         (type == IisDependencyType.TemporalEvent &&
                          request.CurrentOverlay.ExtraTemporalSecondsByEventKey.GetValueOrDefault(key) < extraTemporalSeconds),
            BlockedByHardRule: false));
    }

    private static bool IsLocallyConnected(
        HeadwayConflictCandidate candidate,
        SectionDependencyDecision decision) =>
        SharesCandidateTrain(candidate, decision.FirstTrainIndex, decision.SecondTrainIndex) &&
        (string.IsNullOrWhiteSpace(ResolveCandidateStation(candidate)) ||
         decision.StationNames.Contains(ResolveCandidateStation(candidate), StringComparer.OrdinalIgnoreCase));

    private static bool SharesCandidateTrain(
        HeadwayConflictCandidate candidate,
        int firstTrainIndex,
        int secondTrainIndex) =>
        firstTrainIndex == candidate.FirstTrainIndex || firstTrainIndex == candidate.SecondTrainIndex ||
        secondTrainIndex == candidate.FirstTrainIndex || secondTrainIndex == candidate.SecondTrainIndex;

    private static string ResolveCandidateStation(HeadwayConflictCandidate candidate) =>
        candidate.StationEdge?.StationName ?? candidate.Evidence?.StationName ?? string.Empty;
}
