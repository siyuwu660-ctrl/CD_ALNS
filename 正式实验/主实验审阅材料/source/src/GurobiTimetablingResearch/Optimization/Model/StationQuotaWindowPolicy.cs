using Gurobi;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

/// <summary>
/// Closed business windows: endpoints count inside. Variable-time MILP outer
/// branches remain closures; exact membership is canonicalized when times are
/// known and independently recounted before acceptance. No business epsilon/grid.
/// </summary>
internal static class StationQuotaWindowPolicy
{
    internal static MacroQuotaCategory Classify(bool isStop, double time, double start, double end,
        double tolerance = TimetableTimePolicy.NumericToleranceSeconds)
    {
        if (!double.IsFinite(time) || !double.IsFinite(start) || !double.IsFinite(end) || start > end ||
            !double.IsFinite(tolerance) || tolerance < 0)
            throw new ArgumentException("Invalid closed station quota window or event time.");
        if (!isStop) return MacroQuotaCategory.None;
        if (time < start - tolerance) return MacroQuotaCategory.LeftOutside;
        if (time > end + tolerance) return MacroQuotaCategory.RightOutside;
        return MacroQuotaCategory.InWindow;
    }

    internal static bool Contains(double time, double start, double end) =>
        Classify(true, time, start, end) == MacroQuotaCategory.InWindow;

    // Apply only using permanent valid event bounds during construction. Never
    // tighten a cached repair template from temporary neighborhood fixings.
    internal static void RestrictToPermanentBounds(GRBVar inside, GRBVar left, GRBVar right,
        double lower, double upper, double start, double end)
    {
        if (lower >= start) left.UB = 0;
        if (upper <= end) right.UB = 0;
        if (upper < start || lower > end) inside.UB = 0;
    }

    internal static void FixSnapshotCategories(BasicMilpModel model, MasterSolutionSnapshot snapshot)
    {
        var stops = model.Instance.TrainStops.ToDictionary(s => s.StopId, StringComparer.OrdinalIgnoreCase);
        foreach (var binding in model.StationQuotaCategoryBindings)
        {
            var stop = stops[binding.StopId];
            double time = stop.StopRole == TrainStopRole.Origin
                ? snapshot.DepartureSecondsByStopId[binding.StopId] : snapshot.ArrivalSecondsByStopId[binding.StopId];
            var category = Classify(snapshot.StopByStopId[binding.StopId] == 1, time,
                binding.WindowStartSeconds, binding.WindowEndSeconds);
            Fix(binding.InWindowVariable, category == MacroQuotaCategory.InWindow);
            Fix(binding.LeftOutsideVariable, category == MacroQuotaCategory.LeftOutside);
            Fix(binding.RightOutsideVariable, category == MacroQuotaCategory.RightOutside);
        }
        static void Fix(GRBVar variable, bool value) { variable.LB = value ? 1 : 0; variable.UB = value ? 1 : 0; }
    }
}
