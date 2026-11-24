using System.Globalization;
using System.Text;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

/// <summary>
/// 逐轮记录阶段 2 相对上一步、固定初始图和修复参考图的时刻偏移，并同时保留业务目标与 supplement 用量。
/// </summary>
public static class ValidityFirstRecoveryRoundReporter
{
    public static void Append(
        int iteration,
        string phase,
        BasicMilpModel outputModel,
        MasterSolutionSnapshot immutableInitialReference,
        MasterSolutionSnapshot repairReference,
        MasterSolutionSnapshot inputSnapshot,
        MasterSolutionSnapshot outputSnapshot,
        int rootConflictsBefore,
        int rootConflictsAfter,
        bool repairReferenceUpdated,
        int validFeedbackCount,
        string hotspotComponentId,
        string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);
        ArgumentNullException.ThrowIfNull(outputModel);
        ArgumentNullException.ThrowIfNull(immutableInitialReference);
        ArgumentNullException.ThrowIfNull(repairReference);
        ArgumentNullException.ThrowIfNull(inputSnapshot);
        ArgumentNullException.ThrowIfNull(outputSnapshot);
        Directory.CreateDirectory(outputDirectory);

        (double stepTotal, double stepMaximum) = CalculateRetiming(
            outputModel.Instance.TrainStops,
            outputSnapshot,
            inputSnapshot);
        (double initialTotal, double initialMaximum) = CalculateRetiming(
            outputModel.Instance.TrainStops,
            outputSnapshot,
            immutableInitialReference);
        (double referenceTotal, double referenceMaximum) = CalculateRetiming(
            outputModel.Instance.TrainStops,
            outputSnapshot,
            repairReference);
        IReadOnlyCollection<double> supplements =
            outputSnapshot.RunningTimeSupplementSecondsByKey?.Values.ToList() ?? [];
        string path = Path.Combine(outputDirectory, "validity_first_recovery_rounds.csv");
        bool writeHeader = !File.Exists(path);
        using StreamWriter writer = new(path, true, new UTF8Encoding(false));
        if (writeHeader)
        {
            writer.WriteLine(
                "iteration,phase,input_snapshot_fingerprint,output_snapshot_fingerprint," +
                "immutable_initial_reference_fingerprint,repair_reference_fingerprint," +
                "root_conflicts_before,root_conflicts_after,repair_reference_updated," +
                "step_total_retiming_seconds,step_maximum_retiming_seconds," +
                "initial_total_retiming_seconds,initial_maximum_retiming_seconds," +
                "repair_reference_total_retiming_seconds,repair_reference_maximum_retiming_seconds," +
                "business_global_span_seconds,business_running_supplement_seconds," +
                "actual_running_supplement_seconds,maximum_running_supplement_seconds," +
                "supplement_section_count,valid_feedback_count,hotspot_component_id");
        }

        writer.WriteLine(string.Join(',',
            iteration.ToString(CultureInfo.InvariantCulture),
            Escape(phase),
            SnapshotFingerprint.Calculate(inputSnapshot),
            SnapshotFingerprint.Calculate(outputSnapshot),
            SnapshotFingerprint.Calculate(immutableInitialReference),
            SnapshotFingerprint.Calculate(repairReference),
            rootConflictsBefore.ToString(CultureInfo.InvariantCulture),
            rootConflictsAfter.ToString(CultureInfo.InvariantCulture),
            repairReferenceUpdated,
            Format(stepTotal),
            Format(stepMaximum),
            Format(initialTotal),
            Format(initialMaximum),
            Format(referenceTotal),
            Format(referenceMaximum),
            Format(outputSnapshot.BusinessObjective?.PrimaryGlobalSpanSeconds),
            Format(outputSnapshot.BusinessObjective?.SecondaryRunningTimeSupplementSeconds),
            Format(supplements.Sum()),
            Format(supplements.Count == 0 ? 0.0 : supplements.Max()),
            supplements.Count(value => value > 1e-6).ToString(CultureInfo.InvariantCulture),
            validFeedbackCount.ToString(CultureInfo.InvariantCulture),
            Escape(hotspotComponentId)));
    }

    private static (double Total, double Maximum) CalculateRetiming(
        IReadOnlyList<TrainStopRecord> stops,
        MasterSolutionSnapshot candidate,
        MasterSolutionSnapshot reference)
    {
        double total = 0.0;
        double maximum = 0.0;
        foreach (TrainStopRecord stop in stops)
        {
            if (stop.StopRole != TrainStopRole.Origin)
            {
                Add(Math.Abs(
                    Read(candidate.ArrivalSecondsByStopId, stop.StopId) -
                    Read(reference.ArrivalSecondsByStopId, stop.StopId)));
            }
            if (stop.StopRole != TrainStopRole.Destination)
            {
                Add(Math.Abs(
                    Read(candidate.DepartureSecondsByStopId, stop.StopId) -
                    Read(reference.DepartureSecondsByStopId, stop.StopId)));
            }
        }
        return (total, maximum);

        void Add(double value)
        {
            total += value;
            maximum = Math.Max(maximum, value);
        }
    }

    private static double Read(IReadOnlyDictionary<string, double> values, string stopId) =>
        values.TryGetValue(stopId, out double value)
            ? value
            : throw new InvalidDataException($"Retiming audit is missing stop '{stopId}'.");

    private static string Format(double? value) =>
        value?.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Escape(string? value)
    {
        value ??= string.Empty;
        return value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
    }
}
