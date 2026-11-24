using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

/// <summary>Reject statically impossible stop quotas before creating a native model.</summary>
internal static class StationQuotaPreflight
{
    /// <summary>Check necessary stop-count bounds from legal modes and propagated hard event windows.</summary>
    /// <param name="instance">Original instance with station and section quota rules.</param>
    /// <param name="preprocessing">Permanent stop-mode domains and fixed values for this instance.</param>
    /// <param name="eventBounds">Permanent arrival and departure bounds in seconds.</param>
    /// <exception cref="InvalidDataException">A rule has invalid data or cannot meet its minimum or maximum.</exception>
    internal static void Validate(TimetablingInstance instance, StopModePreprocessingResult preprocessing,
        HardEventBoundSet eventBounds)
    {
        var decisionsMap = preprocessing.Decisions.ToDictionary(decision => decision.StopId, StringComparer.OrdinalIgnoreCase);
        var stationNamesSet = instance.Stations.ToHashSet(StringComparer.OrdinalIgnoreCase);
        double tolerance = TimetableTimePolicy.NumericToleranceSeconds;
        for (int ruleIndex = 0; ruleIndex < instance.StationStopQuotaRules.Count; ruleIndex++)
        {
            StationStopQuotaRule rule = instance.StationStopQuotaRules[ruleIndex];
            string identity = $"station-stop-{ruleIndex + 1:D3}";
            string description = $"Quota '{identity}' station='{rule.StationName}', type='{rule.TrainType}', " +
                $"window=[{rule.StartTimeSeconds},{rule.EndTimeSeconds}], minimum={rule.MinStops}, maximum={rule.MaxStops}";
            if (string.IsNullOrWhiteSpace(rule.StationName) || string.IsNullOrWhiteSpace(rule.TrainType) ||
                rule.MinStops < 0 || rule.MaxStops < rule.MinStops || rule.StartTimeSeconds < 0 ||
                rule.StartTimeSeconds > rule.EndTimeSeconds)
                throw new InvalidDataException($"{description}: invalid fields, bounds or window.");
            if (!stationNamesSet.Contains(rule.StationName))
                throw new InvalidDataException($"{description}: unknown station reference.");

            TrainStopRecord[] matching = instance.TrainStops.Where(stop =>
                stop.StationName.Equals(rule.StationName, StringComparison.OrdinalIgnoreCase) &&
                stop.TrainType.Equals(rule.TrainType, StringComparison.OrdinalIgnoreCase)).ToArray();
            int maximumPossible = 0;
            List<string> requiredJobsList = [];
            List<string> excludedJobsList = [];
            foreach (TrainStopRecord stop in matching)
            {
                StopModePreprocessingDecision decision = decisionsMap[stop.StopId];
                if (stop.StopRequirement == StopRequirement.MustPass || !decision.Analysis.Stopping.IsFeasible)
                {
                    string reason = stop.StopRequirement == StopRequirement.MustPass
                        ? "hard must-pass requirement" : decision.Analysis.Stopping.Evidence;
                    excludedJobsList.Add($"{stop.StopId}: no legal stop route-track ({reason})");
                    continue;
                }

                HardEventBound countTime = stop.StopRole == TrainStopRole.Origin
                    ? eventBounds.Departure(stop.StopId) : eventBounds.Arrival(stop.StopId);
                if (countTime.Lower > rule.EndTimeSeconds + tolerance || countTime.Upper < rule.StartTimeSeconds - tolerance)
                {
                    excludedJobsList.Add($"{stop.StopId}: hard window [{countTime.Lower},{countTime.Upper}] does not intersect quota window");
                    continue;
                }

                // A fixed stop counts in every solution only when its entire hard window lies inside the quota.
                maximumPossible++;
                if (decision.FixedStopValue == 1 && countTime.Lower >= rule.StartTimeSeconds &&
                    countTime.Upper <= rule.EndTimeSeconds)
                    requiredJobsList.Add(stop.StopId);
            }

            int minimumRequired = requiredJobsList.Count;
            if (maximumPossible < rule.MinStops || minimumRequired > rule.MaxStops)
                throw new InvalidDataException($"{description}, matching_jobs={matching.Length}, " +
                    $"maximum_possible={maximumPossible}, minimum_required={minimumRequired}; " +
                    (matching.Length == 0 ? "no matching work." : maximumPossible < rule.MinStops
                        ? string.Join(" | ", excludedJobsList)
                        : $"required stops within hard window: {string.Join(", ", requiredJobsList)}."));
        }

        foreach (var row in TrainSectionStopQuotaConstraintBuilder.Plan(instance))
        {
            int maximumPossible = row.CountedStops.Count(stop => stop.StopRequirement != StopRequirement.MustPass &&
                decisionsMap[stop.StopId].Analysis.Stopping.IsFeasible);
            int minimumRequired = row.CountedStops.Count(stop =>
                preprocessing.FixedStopValuesByStopId.GetValueOrDefault(stop.StopId) == 1);
            if (maximumPossible < row.MinStops || minimumRequired > row.MaxStops)
                throw new InvalidDataException($"Quota '{row.RuleId}' train={row.TrainIndex}, " +
                    $"section='{row.SectionStartStation}/{row.SectionEndStation}', minimum={row.MinStops}, maximum={row.MaxStops}, " +
                    $"maximum_possible={maximumPossible}, minimum_required={minimumRequired}, counted_jobs={row.CountedStops.Count}; " +
                    "counted endpoints and legal stop route-track domains contradict the quota.");
        }
    }
}
