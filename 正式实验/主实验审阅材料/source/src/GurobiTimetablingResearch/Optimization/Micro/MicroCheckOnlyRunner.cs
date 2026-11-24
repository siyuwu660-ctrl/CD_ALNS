// 只检查运行器加载并固定主解快照，构造载体模型后执行主规则校验和微观可行性检查，但不改变任何决策。
// 快照指纹与组合结果对象保证诊断明确对应输入解，适合独立回归检查和续跑前预检。
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace GurobiTimetablingResearch.Optimization;

public sealed record MicroCheckOnlyResult(
    MasterValidationReport Validation,
    MicroFeasibilityReport Report,
    MicroCheckOnlyDiagnosticsSummary C0,
    string SnapshotFingerprint,
    int StructuralEdgeDelta,
    int CutDelta,
    int PromotionDelta);

public sealed class MicroCheckOnlyRunner
{
    private readonly Func<ValidatedSnapshotCarrier> _carrierFactory;
    private readonly Func<BasicMilpModel, MicroFeasibilityReport> _microCheck;
    private readonly Action<MicroFeasibilityReport, string>? _detailedReportWriter;

    public MicroCheckOnlyRunner(
        Func<ValidatedSnapshotCarrier> carrierFactory,
        Func<BasicMilpModel, MicroFeasibilityReport> microCheck,
        Action<MicroFeasibilityReport, string>? detailedReportWriter = null)
    {
        _carrierFactory = carrierFactory ?? throw new ArgumentNullException(nameof(carrierFactory));
        _microCheck = microCheck ?? throw new ArgumentNullException(nameof(microCheck));
        _detailedReportWriter = detailedReportWriter;
    }

    public MicroCheckOnlyResult Run(string outputDirectory)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new ArgumentException("Micro-check-only output directory is required.", nameof(outputDirectory));
        }

        using ValidatedSnapshotCarrier carrier = _carrierFactory();
        if (!carrier.Validation.IsFeasible || carrier.Validation.Violations.Count != 0)
        {
            throw new InvalidDataException("Micro-check-only requires a validated fixed Master carrier.");
        }

        string fingerprintBefore = SnapshotFingerprint.Calculate(carrier.Snapshot);
        MicroFeasibilityReport report = _microCheck(carrier.Model)
            ?? throw new InvalidDataException("Micro-check-only delegate returned no report.");
        _detailedReportWriter?.Invoke(report, outputDirectory);
        string fingerprintAfter = SnapshotFingerprint.Calculate(carrier.Snapshot);
        if (!string.Equals(fingerprintBefore, fingerprintAfter, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Micro-check-only mutated the input Master snapshot.");
        }

        MicroCheckOnlyDiagnosticsSummary summary = MicroCheckOnlyDiagnosticsAggregator.Summarize(report);
        MicroCheckOnlyResult result = new(
            carrier.Validation,
            report,
            summary,
            fingerprintBefore,
            StructuralEdgeDelta: 0,
            CutDelta: 0,
            PromotionDelta: 0);
        MicroCheckOnlyDiagnosticsReporter.Write(summary, result, outputDirectory);
        if (result.StructuralEdgeDelta != 0 || result.CutDelta != 0 || result.PromotionDelta != 0)
        {
            throw new InvalidOperationException("Micro-check-only produced a structural mutation.");
        }
        return result;
    }
}

public static class SnapshotFingerprint
{
    public static string Calculate(MasterSolutionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        StringBuilder canonical = new();
        canonical.Append("objective=").AppendLine(snapshot.ObjectiveValue.ToString("R", CultureInfo.InvariantCulture));
        Append(canonical, "arrival", snapshot.ArrivalSecondsByStopId, value => value.ToString("R", CultureInfo.InvariantCulture));
        Append(canonical, "departure", snapshot.DepartureSecondsByStopId, value => value.ToString("R", CultureInfo.InvariantCulture));
        Append(canonical, "stop", snapshot.StopByStopId, value => value.ToString(CultureInfo.InvariantCulture));
        Append(canonical, "section_precedence", snapshot.SectionPrecedenceByKey, value => value.ToString(CultureInfo.InvariantCulture));
        Append(canonical, "station_precedence", snapshot.StationEventPrecedenceByKey, value => value.ToString(CultureInfo.InvariantCulture));
        Append(
            canonical,
            "supplement",
            snapshot.RunningTimeSupplementSecondsByKey ?? new Dictionary<string, double>(),
            value => value.ToString("R", CultureInfo.InvariantCulture));
        canonical.Append("trains=").AppendLine(string.Join(',', snapshot.AdmittedTrainIndices.Order()));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
    }

    private static void Append<T>(
        StringBuilder builder,
        string family,
        IReadOnlyDictionary<string, T> values,
        Func<T, string> format)
    {
        foreach ((string key, T value) in values.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(family).Append('|').Append(key).Append('=').AppendLine(format(value));
        }
    }
}
