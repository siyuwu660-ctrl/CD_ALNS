// 微观反馈生成器结合冲突解释与当前二元变量快照，构造主问题可接受的 precedence、nogood 或站点压力反馈。
// 反馈对象保存涉及变量、当前取值和证据类别，使 Checker 能在不依赖微观检查器内部结构的情况下试探加割。
using Gurobi;

namespace GurobiTimetablingResearch.Optimization;

/// <summary>
/// 将车站级从问题的冲突解释转换为主问题可消费的反馈。
/// 当前输出可作为 no-good 割使用的修复模式，同时通过反馈类型保留扩展为压力或分离反馈的接口。
/// </summary>
internal sealed class MicroFeedbackGenerator
{
    internal IReadOnlyList<MicroFeedback> Generate(
        BasicMilpModel model,
        StationLevelMicroFeasibilitySlaveResult slaveResult,
        ISet<string> screenedFeedbackIds)
    {
        List<MicroFeedback> feedbackItems = new();

        foreach (StationLevelMicroSubproblemResult stationResult in slaveResult.StationResults
            .Where(result => !result.IsFeasible)
            .OrderByDescending(result => result.ConflictCore.ConflictCount)
            .ThenBy(result => result.StationName, StringComparer.OrdinalIgnoreCase))
        {
            if (IsProvenJointAssignmentInfeasibility(stationResult))
            {
                Dictionary<string, Domain.TrainStopRecord> stopById = model.Instance.TrainStops
                    .ToDictionary(stop => stop.StopId, StringComparer.OrdinalIgnoreCase);
                Dictionary<string, MasterBinaryVariableSnapshot> stopVariableSnapshots = model.StopVars
                    .Where(item => stopById.TryGetValue(item.Key, out Domain.TrainStopRecord? stop) &&
                        stop.StopRequirement == Domain.StopRequirement.OptionalStop &&
                        stop.StopRole == Domain.TrainStopRole.Intermediate)
                    .ToDictionary(
                        item => item.Key,
                        item => Snapshot(item.Value),
                        StringComparer.OrdinalIgnoreCase);
                Dictionary<string, MasterBinaryVariableSnapshot> sectionVariableSnapshots =
                    model.SectionPrecedenceVars.ToDictionary(
                    item => item.Key,
                    item => Snapshot(item.Value),
                    StringComparer.OrdinalIgnoreCase);
                Dictionary<string, MasterBinaryVariableSnapshot> stationEventVariableSnapshots =
                    model.StationEventPrecedenceVars.ToDictionary(
                    item => item.Key,
                    item => Snapshot(item.Value),
                    StringComparer.OrdinalIgnoreCase);

                feedbackItems.AddRange(GenerateProjectedStationCoreFromVariableSnapshots(
                    stationResult,
                    stopVariableSnapshots,
                    sectionVariableSnapshots,
                    stationEventVariableSnapshots,
                    screenedFeedbackIds));
                continue;
            }

            feedbackItems.AddRange(BuildStationPressureFeedbacks(model, stationResult, screenedFeedbackIds));

            Dictionary<string, int> stopIncumbentValues = stationResult.ConflictCore.SupportStopIds
                .Where(model.StopVars.ContainsKey)
                .ToDictionary(
                    stopId => stopId,
                    stopId => model.StopVars[stopId].Get(GRB.DoubleAttr.X) >= 0.5 ? 1 : 0,
                    StringComparer.OrdinalIgnoreCase);

            foreach ((int FirstTrainIndex, int SecondTrainIndex, Dictionary<string, int> IncumbentValues) mappedPair in
                ResolveStationConflictCutDataCandidates(model, stationResult))
            {
                foreach ((Dictionary<string, int> PrecedenceSupport, Dictionary<string, int> StopSupport, string VariantName) in
                    BuildRepairSetVariants(mappedPair.IncumbentValues, stopIncumbentValues))
                {
                    string feedbackId = BuildRepairFeedbackId(
                        stationResult.StationName,
                        mappedPair.FirstTrainIndex,
                        mappedPair.SecondTrainIndex,
                        PrecedenceSupport,
                        StopSupport);

                    if (!screenedFeedbackIds.Add(feedbackId))
                    {
                        continue;
                    }

                    feedbackItems.Add(new MicroFeedback(
                        feedbackId,
                        MicroFeedbackKind.RepairPattern,
                        stationResult.StationName,
                        mappedPair.FirstTrainIndex,
                        mappedPair.SecondTrainIndex,
                        stationResult.ConflictCore.PrimaryConflictKind,
                        stationResult.ConflictCore.ConflictCount,
                        PrecedenceSupport,
                        StopSupport,
                        $"variant={VariantName}; primary_conflict={stationResult.ConflictCore.PrimaryConflictKind}; core_conflicts={stationResult.ConflictCore.ConflictCount}; precedence_support={PrecedenceSupport.Count}; stop_support={StopSupport.Count}",
                        "station-level slave found a local micro infeasibility; at least one supported macro decision should change"));
                }
            }
        }

        return feedbackItems;
    }

    internal static LocalPrecedenceNogoodCut ToMasterCut(MicroFeedback feedback)
    {
        if (feedback.Kind == MicroFeedbackKind.ProjectedStationCore)
        {
            StationCoreProjection projection = feedback.Projection ?? throw new InvalidOperationException(
                $"Projected feedback '{feedback.FeedbackId}' has no station-core projection.");
            if (!projection.HasCut)
            {
                throw new InvalidOperationException(
                    $"Projected feedback '{feedback.FeedbackId}' is a certificate and cannot be translated into a master cut.");
            }

            return new LocalPrecedenceNogoodCut(
                projection.ProjectionId,
                projection.StationName,
                projection.CoreTrainIndices[0],
                projection.CoreTrainIndices[1],
                projection.SectionLiteralsByKey,
                projection.StopLiteralsByStopId,
                projection.SourceFamily,
                feedback.Rationale,
                projection.StationEventLiteralsByKey);
        }

        if (feedback.Kind is not MicroFeedbackKind.RepairPattern and not MicroFeedbackKind.StationPressure)
        {
            throw new InvalidOperationException($"Feedback '{feedback.FeedbackId}' cannot be translated into a master cut.");
        }

        return new LocalPrecedenceNogoodCut(
            feedback.FeedbackId,
            feedback.StationName,
            feedback.FirstTrainIndex ?? -1,
            feedback.SecondTrainIndex ?? -1,
            feedback.PrecedenceIncumbentValuesByKey,
            feedback.StopIncumbentValuesByStopId,
            feedback.Kind == MicroFeedbackKind.StationPressure
                ? "micro_feedback_station_pressure_relief"
                : "micro_feedback_repair_pattern",
            feedback.Rationale);
    }

    internal static IReadOnlyList<MicroFeedback> GenerateProjectedStationCore(
        StationLevelMicroSubproblemResult stationResult,
        IReadOnlyDictionary<string, int> mutableStopValues,
        IReadOnlyDictionary<string, int> sectionValues,
        IReadOnlyDictionary<string, int> stationEventValues,
        ISet<string> screenedFeedbackIds)
    {
        ArgumentNullException.ThrowIfNull(stationResult);
        ArgumentNullException.ThrowIfNull(mutableStopValues);
        ArgumentNullException.ThrowIfNull(sectionValues);
        ArgumentNullException.ThrowIfNull(stationEventValues);
        ArgumentNullException.ThrowIfNull(screenedFeedbackIds);

        if (!IsProvenJointAssignmentInfeasibility(stationResult))
        {
            return Array.Empty<MicroFeedback>();
        }

        StationInfeasibleCore core = stationResult.AssignmentResult.InfeasibleCore!;
        if (core.RequirementIds.Count < 2 || core.TrainIndices.Count < 2)
        {
            return Array.Empty<MicroFeedback>();
        }

        HashSet<string> coreRequirementIds = core.RequirementIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<int> coreTrainIndices = core.TrainIndices.ToHashSet();
        IReadOnlyList<StationMicroRequirement> coreRequirements = stationResult.Requirements
            .Where(requirement => coreRequirementIds.Contains(requirement.StopId) &&
                coreTrainIndices.Contains(requirement.TrainIndex))
            .OrderBy(requirement => requirement.StopId, StringComparer.Ordinal)
            .ToList();
        HashSet<string> stationSectionNames = coreRequirements
            .SelectMany(requirement => new[] { requirement.IncomingSectionName, requirement.OutgoingSectionName })
            .Where(sectionName => !string.IsNullOrWhiteSpace(sectionName))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Dictionary<string, int> projectedStopValues = core.RequirementIds
            .Where(mutableStopValues.ContainsKey)
            .ToDictionary(
                stopId => stopId,
                stopId => mutableStopValues[stopId],
                StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> projectedSectionValues = sectionValues
            .Where(item => IsCoreSectionPrecedenceKey(item.Key, stationSectionNames, coreTrainIndices))
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> projectedStationEventValues = stationEventValues
            .Where(item => IsCoreStationEventPrecedenceKey(
                item.Key,
                stationResult.StationName,
                coreTrainIndices))
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
        string coreId = $"{stationResult.StationName}::core::{string.Join("|", core.RequirementIds.OrderBy(value => value, StringComparer.Ordinal))}";
        StationCoreProjection projection = StationCoreProjectedCutBuilder.Build(
            stationResult.StationName,
            coreId,
            core.RequirementIds,
            core.TrainIndices,
            projectedStopValues,
            projectedSectionValues,
            projectedStationEventValues);

        if (!screenedFeedbackIds.Add(projection.ProjectionId))
        {
            return Array.Empty<MicroFeedback>();
        }

        string evidence = $"core_id={projection.CoreId}; core_requirements={string.Join("|", projection.CoreStopIds)}; " +
            $"core_trains={string.Join("|", projection.CoreTrainIndices)}; stop_literals={projection.StopLiteralCount}; " +
            $"section_literals={projection.SectionLiteralCount}; station_event_literals={projection.StationEventLiteralCount}; " +
            $"certificate_reason={projection.CertificateReason ?? string.Empty}";
        MicroFeedback feedback = new(
            projection.ProjectionId,
            MicroFeedbackKind.ProjectedStationCore,
            projection.StationName,
            projection.CoreTrainIndices[0],
            projection.CoreTrainIndices[1],
            stationResult.ConflictCore.PrimaryConflictKind,
            stationResult.ConflictCore.ConflictCount,
            projection.SectionLiteralsByKey,
            projection.StopLiteralsByStopId,
            evidence,
            projection.HasCut
                ? "proven station route-track assignment core projected onto its complete supported incumbent Master signature"
                : "proven station route-track assignment core has no mutable support in the current Master decision space",
            projection,
            projection.StationEventLiteralsByKey);

        return new[] { feedback };
    }

    internal static IReadOnlyList<MicroFeedback> GenerateProjectedStationCoreFromVariableSnapshots(
        StationLevelMicroSubproblemResult stationResult,
        IReadOnlyDictionary<string, MasterBinaryVariableSnapshot> stopVariableSnapshots,
        IReadOnlyDictionary<string, MasterBinaryVariableSnapshot> sectionVariableSnapshots,
        IReadOnlyDictionary<string, MasterBinaryVariableSnapshot> stationEventVariableSnapshots,
        ISet<string> screenedFeedbackIds)
    {
        ArgumentNullException.ThrowIfNull(stopVariableSnapshots);
        ArgumentNullException.ThrowIfNull(sectionVariableSnapshots);
        ArgumentNullException.ThrowIfNull(stationEventVariableSnapshots);

        return GenerateProjectedStationCore(
            stationResult,
            ExtractMutableIncumbentValues(stopVariableSnapshots),
            ExtractMutableIncumbentValues(sectionVariableSnapshots),
            ExtractMutableIncumbentValues(stationEventVariableSnapshots),
            screenedFeedbackIds);
    }

    private static Dictionary<string, int> ExtractMutableIncumbentValues(
        IReadOnlyDictionary<string, MasterBinaryVariableSnapshot> variableSnapshots)
    {
        return variableSnapshots
            .Where(item => item.Value.IsMutable)
            .ToDictionary(
                item => item.Key,
                item => item.Value.BinaryIncumbentValue,
                StringComparer.OrdinalIgnoreCase);
    }

    private static MasterBinaryVariableSnapshot Snapshot(GRBVar variable)
    {
        return new MasterBinaryVariableSnapshot(
            variable.Get(GRB.DoubleAttr.LB),
            variable.Get(GRB.DoubleAttr.UB),
            variable.Get(GRB.DoubleAttr.X));
    }

    private static bool IsProvenJointAssignmentInfeasibility(StationLevelMicroSubproblemResult stationResult)
    {
        return !stationResult.AssignmentResult.IsFeasible &&
            stationResult.AssignmentResult.Status == MicroSolveStatus.ProvenInfeasible &&
            stationResult.AssignmentResult.InfeasibleCore is not null &&
            stationResult.Conflicts.Any(conflict => string.Equals(
                conflict.ConflictKind,
                "station_route_track_assignment_infeasible",
                StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsCoreSectionPrecedenceKey(
        string key,
        IReadOnlySet<string> stationSectionNames,
        IReadOnlySet<int> coreTrainIndices)
    {
        int secondSeparator = key.LastIndexOf("::", StringComparison.Ordinal);
        if (secondSeparator <= 0 ||
            !int.TryParse(key[(secondSeparator + 2)..], out int secondTrainIndex))
        {
            return false;
        }

        int firstSeparator = key.LastIndexOf("::", secondSeparator - 1, StringComparison.Ordinal);
        if (firstSeparator <= 0 ||
            !int.TryParse(key[(firstSeparator + 2)..secondSeparator], out int firstTrainIndex))
        {
            return false;
        }

        string sectionName = key[..firstSeparator];
        return firstTrainIndex != secondTrainIndex &&
            coreTrainIndices.Contains(firstTrainIndex) &&
            coreTrainIndices.Contains(secondTrainIndex) &&
            stationSectionNames.Contains(sectionName);
    }

    private static bool IsCoreStationEventPrecedenceKey(
        string key,
        string stationName,
        IReadOnlySet<int> coreTrainIndices)
    {
        string stationPrefix = $"{stationName}::";
        if (!key.StartsWith(stationPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string[] parts = key[stationPrefix.Length..].Split("::", StringSplitOptions.None);
        return parts.Length == 4 &&
            int.TryParse(parts[0], out int firstTrainIndex) &&
            int.TryParse(parts[2], out int secondTrainIndex) &&
            firstTrainIndex != secondTrainIndex &&
            coreTrainIndices.Contains(firstTrainIndex) &&
            coreTrainIndices.Contains(secondTrainIndex);
    }

    private static IEnumerable<MicroFeedback> BuildStationPressureFeedbacks(
        BasicMilpModel model,
        StationLevelMicroSubproblemResult stationResult,
        ISet<string> screenedFeedbackIds)
    {
        double? startSeconds = stationResult.Conflicts.Count == 0
            ? null
            : stationResult.Conflicts.Min(conflict => Math.Min(conflict.StartSeconds, conflict.EndSeconds));
        double? endSeconds = stationResult.Conflicts.Count == 0
            ? null
            : stationResult.Conflicts.Max(conflict => Math.Max(conflict.StartSeconds, conflict.EndSeconds));

        Dictionary<string, Domain.TrainStopRecord> stopById = model.Instance.TrainStops
            .ToDictionary(stop => stop.StopId, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> optionalStoppedCore = stationResult.ConflictCore.SupportStopIds
            .Where(stopId => model.StopVars.ContainsKey(stopId))
            .Where(stopId => stopById.TryGetValue(stopId, out Domain.TrainStopRecord? stop) &&
                stop.StopRequirement == Domain.StopRequirement.OptionalStop &&
                stop.StopRole == Domain.TrainStopRole.Intermediate)
            .Where(stopId => model.StopVars[stopId].Get(GRB.DoubleAttr.X) >= 0.5)
            .ToDictionary(stopId => stopId, _ => 1, StringComparer.OrdinalIgnoreCase);

        if (optionalStoppedCore.Count == 0 && startSeconds.HasValue && endSeconds.HasValue)
        {
            optionalStoppedCore = stationResult.Requirements
                .Where(requirement => requirement.IsStop)
                .Where(requirement => requirement.ArrivalSeconds <= endSeconds.Value &&
                    requirement.DepartureSeconds >= startSeconds.Value)
                .Select(requirement => requirement.StopId)
                .Where(stopId => model.StopVars.ContainsKey(stopId))
                .Where(stopId => stopById.TryGetValue(stopId, out Domain.TrainStopRecord? stop) &&
                    stop.StopRequirement == Domain.StopRequirement.OptionalStop &&
                    stop.StopRole == Domain.TrainStopRole.Intermediate)
                .Where(stopId => model.StopVars[stopId].Get(GRB.DoubleAttr.X) >= 0.5)
                .OrderBy(stopId => stopId, StringComparer.OrdinalIgnoreCase)
                .Take(4)
                .ToDictionary(stopId => stopId, _ => 1, StringComparer.OrdinalIgnoreCase);
        }

        if (optionalStoppedCore.Count == 0)
        {
            yield break;
        }

        foreach (KeyValuePair<string, int> stopTerm in RankPressureStopTerms(stationResult, optionalStoppedCore, startSeconds, endSeconds))
        {
            Dictionary<string, int> singleStopSupport = new(StringComparer.OrdinalIgnoreCase)
            {
                [stopTerm.Key] = stopTerm.Value
            };

            MicroFeedback? singleFeedback = BuildStationPressureFeedback(
                stationResult,
                singleStopSupport,
                screenedFeedbackIds,
                "single_optional_stop_relief",
                startSeconds,
                endSeconds);

            if (singleFeedback is not null)
            {
                yield return singleFeedback;
            }
        }

        if (optionalStoppedCore.Count <= 1)
        {
            yield break;
        }

        MicroFeedback? windowFeedback = BuildStationPressureFeedback(
            stationResult,
            optionalStoppedCore,
            screenedFeedbackIds,
            "window_pressure_relief",
            startSeconds,
            endSeconds);

        if (windowFeedback is not null)
        {
            yield return windowFeedback;
        }
    }

    private static MicroFeedback? BuildStationPressureFeedback(
        StationLevelMicroSubproblemResult stationResult,
        IReadOnlyDictionary<string, int> stopSupport,
        ISet<string> screenedFeedbackIds,
        string variantName,
        double? startSeconds,
        double? endSeconds)
    {
        string stopSignature = string.Join(
            "|",
            stopSupport.Keys.OrderBy(stopId => stopId, StringComparer.OrdinalIgnoreCase));
        string feedbackId = $"{stationResult.StationName}::pressure_relief::{variantName}::{stationResult.ConflictCore.PrimaryConflictKind}::{stopSignature}";
        if (!screenedFeedbackIds.Add(feedbackId))
        {
            return null;
        }

        return new MicroFeedback(
            feedbackId,
            MicroFeedbackKind.StationPressure,
            stationResult.StationName,
            null,
            null,
            stationResult.ConflictCore.PrimaryConflictKind,
            stationResult.ConflictCore.ConflictCount,
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            stopSupport,
            startSeconds.HasValue && endSeconds.HasValue
                ? $"variant={variantName}; window=[{startSeconds.Value:F3},{endSeconds.Value:F3}]; optional_stopped_core={stopSupport.Count}"
                : $"variant={variantName}; window=unknown; optional_stopped_core={stopSupport.Count}",
            $"station pressure relief feedback; variant={variantName}; at least one optional stopped train in the local pressure core should pass");
    }

    private static IReadOnlyList<KeyValuePair<string, int>> RankPressureStopTerms(
        StationLevelMicroSubproblemResult stationResult,
        IReadOnlyDictionary<string, int> stopSupport,
        double? startSeconds,
        double? endSeconds)
    {
        Dictionary<string, StationMicroRequirement> requirementByStopId = stationResult.Requirements
            .ToDictionary(requirement => requirement.StopId, StringComparer.OrdinalIgnoreCase);

        return stopSupport
            .OrderByDescending(item => ComputePressureScore(stationResult, requirementByStopId, item.Key, startSeconds, endSeconds))
            .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static double ComputePressureScore(
        StationLevelMicroSubproblemResult stationResult,
        IReadOnlyDictionary<string, StationMicroRequirement> requirementByStopId,
        string stopId,
        double? startSeconds,
        double? endSeconds)
    {
        if (!requirementByStopId.TryGetValue(stopId, out StationMicroRequirement? requirement))
        {
            return 0.0;
        }

        int conflictParticipation = stationResult.Conflicts.Count(conflict =>
            conflict.FirstTrainIndex == requirement.TrainIndex ||
            conflict.SecondTrainIndex == requirement.TrainIndex);
        int overlappingStops = stationResult.Requirements.Count(other =>
            other.IsStop &&
            !string.Equals(other.StopId, requirement.StopId, StringComparison.OrdinalIgnoreCase) &&
            other.ArrivalSeconds <= requirement.DepartureSeconds &&
            other.DepartureSeconds >= requirement.ArrivalSeconds);
        int narrowTrackBonus = requirement.FeasibleTracks.Count == 0
            ? 5
            : Math.Max(0, 5 - requirement.FeasibleTracks.Count);
        double windowOverlapBonus = startSeconds.HasValue && endSeconds.HasValue &&
            requirement.ArrivalSeconds <= endSeconds.Value &&
            requirement.DepartureSeconds >= startSeconds.Value
                ? 3.0
                : 0.0;

        return conflictParticipation * 10.0 + overlappingStops * 2.0 + narrowTrackBonus + windowOverlapBonus;
    }

    private static IEnumerable<(Dictionary<string, int> PrecedenceSupport, Dictionary<string, int> StopSupport, string VariantName)> BuildRepairSetVariants(
        IReadOnlyDictionary<string, int> precedenceIncumbentValues,
        IReadOnlyDictionary<string, int> stopIncumbentValues)
    {
        List<KeyValuePair<string, int>> precedenceTerms = precedenceIncumbentValues
            .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        List<KeyValuePair<string, int>> stopTerms = stopIncumbentValues
            .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (KeyValuePair<string, int> precedenceTerm in precedenceTerms)
        {
            yield return (
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    [precedenceTerm.Key] = precedenceTerm.Value
                },
                stopTerms.ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase),
                "single_precedence_plus_full_stop");
        }

        foreach (KeyValuePair<string, int> stopTerm in stopTerms)
        {
            yield return (
                precedenceTerms.ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    [stopTerm.Key] = stopTerm.Value
                },
                "full_precedence_plus_single_stop");
        }

        foreach (KeyValuePair<string, int> precedenceTerm in precedenceTerms)
        {
            foreach (KeyValuePair<string, int> stopTerm in stopTerms)
            {
                yield return (
                    new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                    {
                        [precedenceTerm.Key] = precedenceTerm.Value
                    },
                    new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                    {
                        [stopTerm.Key] = stopTerm.Value
                    },
                    "single_precedence_plus_single_stop");
            }
        }

        if (precedenceTerms.Count > 0 || stopTerms.Count > 0)
        {
            yield return (
                precedenceTerms.ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase),
                stopTerms.ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase),
                "full_core_support");
        }
    }

    private static IEnumerable<(int FirstTrainIndex, int SecondTrainIndex, Dictionary<string, int> IncumbentValues)> ResolveStationConflictCutDataCandidates(
        BasicMilpModel model,
        StationLevelMicroSubproblemResult stationResult)
    {
        List<(int FirstTrainIndex, int SecondTrainIndex)> rankedPairs = stationResult.Conflicts
            .Where(conflict => conflict.FirstTrainIndex.HasValue && conflict.SecondTrainIndex.HasValue)
            .GroupBy(
                conflict => (
                    FirstTrainIndex: Math.Min(conflict.FirstTrainIndex!.Value, conflict.SecondTrainIndex!.Value),
                    SecondTrainIndex: Math.Max(conflict.FirstTrainIndex!.Value, conflict.SecondTrainIndex!.Value)))
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key.FirstTrainIndex)
            .ThenBy(group => group.Key.SecondTrainIndex)
            .Select(group => group.Key)
            .ToList();

        if (rankedPairs.Count == 0)
        {
            List<int> fallbackTrainIndices = stationResult.Requirements
                .Select(requirement => requirement.TrainIndex)
                .Distinct()
                .OrderBy(item => item)
                .ToList();

            for (int firstIndex = 0; firstIndex < fallbackTrainIndices.Count; firstIndex++)
            {
                for (int secondIndex = firstIndex + 1; secondIndex < fallbackTrainIndices.Count; secondIndex++)
                {
                    rankedPairs.Add((fallbackTrainIndices[firstIndex], fallbackTrainIndices[secondIndex]));
                }
            }
        }

        IReadOnlyList<string> stationSectionNames = stationResult.Requirements
            .SelectMany(requirement => new[] { requirement.IncomingSectionName, requirement.OutgoingSectionName })
            .Where(sectionName => !string.IsNullOrWhiteSpace(sectionName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToList();

        foreach ((int trainA, int trainB) in rankedPairs.Distinct())
        {
            foreach ((int firstTrainIndex, int secondTrainIndex) in new[] { (trainA, trainB), (trainB, trainA) })
            {
                Dictionary<string, int> incumbentValues = ResolveStationPrecedenceIncumbentValues(
                    model,
                    stationResult.StationName,
                    stationSectionNames,
                    firstTrainIndex,
                    secondTrainIndex,
                    stationResult.ConflictCore.SupportPrecedenceKeys);

                if (incumbentValues.Count > 0)
                {
                    yield return (firstTrainIndex, secondTrainIndex, incumbentValues);
                }
            }
        }
    }

    private static Dictionary<string, int> ResolveStationPrecedenceIncumbentValues(
        BasicMilpModel model,
        string stationName,
        IReadOnlyList<string> stationSectionNames,
        int firstTrainIndex,
        int secondTrainIndex,
        IReadOnlyList<string> preferredSupportKeys)
    {
        Dictionary<string, int> incumbentValues = preferredSupportKeys
            .Where(model.SectionPrecedenceVars.ContainsKey)
            .Where(key => key.EndsWith($"::{firstTrainIndex}::{secondTrainIndex}", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(
                key => key,
                key => model.SectionPrecedenceVars[key].Get(GRB.DoubleAttr.X) >= 0.5 ? 1 : 0,
                StringComparer.OrdinalIgnoreCase);

        if (incumbentValues.Count > 0)
        {
            return incumbentValues;
        }

        foreach (string sectionName in stationSectionNames)
        {
            string key = BasicMilpModelBuilder.BuildSectionPrecedenceKey(sectionName, firstTrainIndex, secondTrainIndex);
            if (model.SectionPrecedenceVars.TryGetValue(key, out GRBVar? variable))
            {
                incumbentValues[key] = variable.Get(GRB.DoubleAttr.X) >= 0.5 ? 1 : 0;
            }
        }

        if (incumbentValues.Count > 0)
        {
            return incumbentValues;
        }

        return ResolveLocalPrecedenceIncumbentValues(model, stationName, firstTrainIndex, secondTrainIndex);
    }

    private static Dictionary<string, int> ResolveLocalPrecedenceIncumbentValues(
        BasicMilpModel model,
        string stationName,
        int firstTrainIndex,
        int secondTrainIndex)
    {
        Dictionary<string, int> result = new(StringComparer.OrdinalIgnoreCase);

        foreach ((string incomingSectionName, string outgoingSectionName) in BuildAdjacentSectionsForStation(model.Instance, stationName))
        {
            string incomingKey = BasicMilpModelBuilder.BuildSectionPrecedenceKey(incomingSectionName, firstTrainIndex, secondTrainIndex);
            if (model.SectionPrecedenceVars.TryGetValue(incomingKey, out GRBVar? incomingVar))
            {
                result[incomingKey] = incomingVar.Get(GRB.DoubleAttr.X) >= 0.5 ? 1 : 0;
            }

            string outgoingKey = BasicMilpModelBuilder.BuildSectionPrecedenceKey(outgoingSectionName, firstTrainIndex, secondTrainIndex);
            if (model.SectionPrecedenceVars.TryGetValue(outgoingKey, out GRBVar? outgoingVar))
            {
                result[outgoingKey] = outgoingVar.Get(GRB.DoubleAttr.X) >= 0.5 ? 1 : 0;
            }
        }

        return result;
    }

    private static List<(string IncomingSectionName, string OutgoingSectionName)> BuildAdjacentSectionsForStation(
        Domain.TimetablingInstance instance,
        string stationName)
    {
        List<(string IncomingSectionName, string OutgoingSectionName)> pairs = new();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (Domain.TrainRecord train in instance.Trains)
        {
            for (int index = 1; index < train.OrderedStops.Count - 1; index++)
            {
                Domain.TrainStopRecord currentStop = train.OrderedStops[index];
                Domain.TrainStopRecord nextStop = train.OrderedStops[index + 1];
                if (!string.Equals(currentStop.StationName, stationName, StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(currentStop.IncomingSectionName) ||
                    string.IsNullOrWhiteSpace(nextStop.IncomingSectionName))
                {
                    continue;
                }

                string key = $"{currentStop.IncomingSectionName}::{nextStop.IncomingSectionName}";
                if (seen.Add(key))
                {
                    pairs.Add((currentStop.IncomingSectionName, nextStop.IncomingSectionName));
                }
            }
        }

        return pairs;
    }

    private static string BuildRepairFeedbackId(
        string stationName,
        int firstTrainIndex,
        int secondTrainIndex,
        IReadOnlyDictionary<string, int> precedenceIncumbentValues,
        IReadOnlyDictionary<string, int> stopIncumbentValues)
    {
        string precedenceSignature = string.Join(
            "|",
            precedenceIncumbentValues
                .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                .Select(item => $"{item.Key}={item.Value}"));

        string stopSignature = string.Join(
            "|",
            stopIncumbentValues
                .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                .Select(item => $"{item.Key}={item.Value}"));

        return $"{stationName}::{firstTrainIndex}::{secondTrainIndex}::prec[{precedenceSignature}]::stop[{stopSignature}]";
    }
}

internal enum MicroFeedbackKind
{
    RepairPattern,
    StationPressure,
    ProjectedStationCore
}

internal sealed record MasterBinaryVariableSnapshot(
    double LowerBound,
    double UpperBound,
    double IncumbentValue)
{
    internal bool IsMutable =>
        double.IsFinite(LowerBound) &&
        double.IsFinite(UpperBound) &&
        UpperBound - LowerBound > 0.5;

    internal int BinaryIncumbentValue => IncumbentValue >= 0.5 ? 1 : 0;
}

internal sealed record MicroFeedback(
    string FeedbackId,
    MicroFeedbackKind Kind,
    string StationName,
    int? FirstTrainIndex,
    int? SecondTrainIndex,
    string PrimaryConflictKind,
    int PriorityScore,
    IReadOnlyDictionary<string, int> PrecedenceIncumbentValuesByKey,
    IReadOnlyDictionary<string, int> StopIncumbentValuesByStopId,
    string Evidence,
    string Rationale,
    StationCoreProjection? Projection = null,
    IReadOnlyDictionary<string, int>? StationEventPrecedenceIncumbentValuesByKey = null);
