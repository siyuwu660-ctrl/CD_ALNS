using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

/// <summary>Measures quota support and builds a quota-blind, size-matched control expansion.</summary>
internal static class QuotaAblation
{
    /// <summary>Expand with quota guidance, or fill the same slots by ordinary timetable proximity.</summary>
    /// <param name="instance">Original timetable and structural stop domains.</param>
    /// <param name="anchor">Current event times in seconds and stop pattern.</param>
    /// <param name="seedStopIds">Base repair cells, shared by both modes.</param>
    /// <param name="maxStops">Original expansion cap on released cells.</param>
    /// <param name="maxTrains">Original expansion cap on distinct trains.</param>
    /// <param name="remaining">Optional remaining wall-clock seconds.</param>
    /// <param name="control">Whether to select additional cells without using quota membership to rank them.</param>
    /// <returns>Selected expansion and the guided/control additions for auditing.</returns>
    internal static QuotaDependencyExpansion Expand(TimetablingInstance instance, MasterSolutionSnapshot anchor,
        IEnumerable<string> seedStopIds, int maxStops, int maxTrains, Func<double>? remaining, bool control)
    {
        string[] seeds = seedStopIds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var guided = QuotaDependencySupport.Expand(instance, anchor, seeds, maxStops, maxTrains, remaining: remaining);
        var additions = guided.FreeTimeStopIds.Except(seeds, StringComparer.OrdinalIgnoreCase).ToArray();
        if (additions.Length == 0) return guided;
        var guidedEvidence = guided with { GuidedAdditions = additions, SelectedAdditions = additions };
        if (!control) return guidedEvidence;
        return SelectControl(instance, anchor, guidedEvidence, additions, remaining);
    }

    /// <summary>Replace the added cells while preserving their count, optional count and distinct train count.</summary>
    /// <param name="instance">Original visits and structural stop domains.</param>
    /// <param name="anchor">Anchor clocks used only for proximity ranking.</param>
    /// <param name="guided">Guided sizing result with seed cells retained.</param>
    /// <param name="additions">Guided added IDs used only to compute slot counts.</param>
    /// <param name="remaining">Optional remaining wall-clock seconds.</param>
    /// <returns>Control choices with size matched before downstream time support.</returns>
    /// <exception cref="TimeoutException">The neighborhood construction budget expires.</exception>
    private static QuotaDependencyExpansion SelectControl(TimetablingInstance instance, MasterSolutionSnapshot anchor,
        QuotaDependencyExpansion guided, string[] additions, Func<double>? remaining)
    {
        var seedIds = guided.FreeTimeStopIds.Except(additions, StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seeds = seedIds.Select(id => instance.StopsById[id]).ToArray();
        var seedTrains = seeds.Select(stop => stop.TrainIndex).ToHashSet();
        var domains = StructuralStopDomains.Build(instance);
        bool Optional(string id) => domains[id].Lower == 0 && domains[id].Upper == 1;
        double Clock(TrainStopRecord stop) => stop.StopRole == TrainStopRole.Origin
            ? anchor.DepartureSecondsByStopId[stop.StopId] : anchor.ArrivalSecondsByStopId[stop.StopId];

        // Slot shapes contain cardinalities only. Ordinary candidates are ranked by route position and event-time proximity.
        var ranks = instance.TrainStops.Where(stop => !seedIds.Contains(stop.StopId))
            .Select(stop => new { Stop = stop, PositionDistance = seeds.Min(seed => Math.Abs(seed.StationOrder - stop.StationOrder)),
                TimeDistance = seeds.Min(seed => Math.Abs(Clock(seed) - Clock(stop))) })
            .OrderBy(item => item.PositionDistance).ThenBy(item => item.TimeDistance)
            .ThenBy(item => item.Stop.StopId, StringComparer.Ordinal)
            .Select(item => item.Stop).ToArray();
        var additionStops = additions.Select(id => instance.StopsById[id]).ToArray();
        var addedOnSeeds = additionStops.Where(stop => seedTrains.Contains(stop.TrainIndex)).ToArray();
        var selected = new List<string>();
        AddSlots(ranks.Where(stop => seedTrains.Contains(stop.TrainIndex)), addedOnSeeds.Count(stop => Optional(stop.StopId)),
            addedOnSeeds.Count(stop => !Optional(stop.StopId)));

        var slots = additionStops.Where(stop => !seedTrains.Contains(stop.TrainIndex)).GroupBy(stop => stop.TrainIndex)
            .Select(group => (Optional: group.Count(stop => Optional(stop.StopId)), Fixed: group.Count(stop => !Optional(stop.StopId))))
            .OrderByDescending(slot => slot.Optional + slot.Fixed).ThenByDescending(slot => slot.Optional).ToArray();
        var pools = ranks.Where(stop => !seedTrains.Contains(stop.TrainIndex)).GroupBy(stop => stop.TrainIndex)
            .Select(group => (Stops: group.ToArray(), Optional: group.Count(stop => Optional(stop.StopId)),
                Fixed: group.Count(stop => !Optional(stop.StopId)))).ToArray();
        int[] poolToSlot = Enumerable.Repeat(-1, pools.Length).ToArray();
        for (int slotIndex = slots.Length - 1; slotIndex >= 0; slotIndex--)
        {
            if (!Assign(slotIndex, new bool[pools.Length]))
                throw new InvalidOperationException("Cannot construct a size-matched control from the original structural domains.");
        }
        for (int poolIndex = 0; poolIndex < pools.Length; poolIndex++)
        {
            if (poolToSlot[poolIndex] < 0) continue;
            var slot = slots[poolToSlot[poolIndex]];
            AddSlots(pools[poolIndex].Stops, slot.Optional, slot.Fixed);
        }
        return guided with
        {
            FreeTimeStopIds = seedIds.Concat(selected).ToHashSet(StringComparer.OrdinalIgnoreCase),
            FreeStopIds = seedIds.Concat(selected).Where(Optional).ToHashSet(StringComparer.OrdinalIgnoreCase),
            SelectedAdditions = selected.ToArray()
        };

        // A matching permits an earlier train to be reassigned when reserving a compatible train for another slot.
        bool Assign(int slotIndex, bool[] visited)
        {
            if (remaining?.Invoke() <= 0) throw new TimeoutException("Quota control matching exceeded the construction budget.");
            var slot = slots[slotIndex];
            for (int poolIndex = 0; poolIndex < pools.Length; poolIndex++)
            {
                var pool = pools[poolIndex];
                if (visited[poolIndex] || pool.Optional < slot.Optional || pool.Fixed < slot.Fixed) continue;
                visited[poolIndex] = true;
                if (poolToSlot[poolIndex] >= 0 && !Assign(poolToSlot[poolIndex], visited)) continue;
                poolToSlot[poolIndex] = slotIndex;
                return true;
            }
            return false;
        }

        void AddSlots(IEnumerable<TrainStopRecord> candidates, int optionalCount, int fixedCount)
        {
            var ids = candidates.Select(stop => stop.StopId).ToArray();
            selected.AddRange(ids.Where(Optional).Take(optionalCount));
            selected.AddRange(ids.Where(id => !Optional(id)).Take(fixedCount));
        }
    }
}
