// IIS 引导诊断分别输出每轮修复轨迹和依赖计划，记录阻塞类型、释放对象、模型状态及动作选择。
// 两张表通过候选键和尝试序号关联，用于还原从首次不可行到闭包扩张后可行或终止的全过程。
using System.Globalization;
using System.Text;

namespace GurobiTimetablingResearch.Optimization;

internal sealed record IisGuidedRepairTraceDiagnostic(
    string CandidateId,
    int ExpansionRound,
    RepairDecisionLevel RepairLevel,
    int TemporalWindowSeconds,
    int ReleasedTrainCount,
    int ReleasedTimeCount,
    int ReleasedSectionPrecedenceCount,
    int ReleasedStationEventPrecedenceCount,
    int ReleasedStopCount,
    int ReleasedSupplementCount,
    int IisConstraintCount,
    int IisBoundCount,
    int NewSectionPrecedenceDependencies,
    int NewStationEventDependencies,
    int NewTemporalEvents,
    int NewStopDependencies,
    int NewSupplementDependencies,
    int OriginalHardBlockerCount,
    string SolverStatus,
    double RuntimeSeconds,
    int SolutionCount,
    bool? Accepted,
    string TerminationReason);

internal sealed record IisDependencyPlanDiagnostic(
    string CandidateId,
    int ExpansionRound,
    IisDependencyType DependencyType,
    int? TrainI,
    int? TrainJ,
    string Station,
    string Section,
    string Event,
    string VariableKey,
    string SourceIisElement,
    string Reason,
    bool WasAlreadyReleased,
    bool WillRelease,
    bool BlockedByHardRule,
    string ReleaseSource);

internal static class IisGuidedRepairDiagnosticsReporter
{
    internal static void Write(
        IReadOnlyList<IisGuidedRepairTraceDiagnostic> trace,
        IReadOnlyList<IisDependencyPlanDiagnostic> plans,
        string outputDirectory)
    {
        ArgumentNullException.ThrowIfNull(trace);
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        Directory.CreateDirectory(outputDirectory);
        WriteTraceCsv(trace, Path.Combine(outputDirectory, "iis_guided_repair_trace.csv"));
        WriteTraceMarkdown(trace, Path.Combine(outputDirectory, "iis_guided_repair_trace.md"));
        WritePlanCsv(plans, Path.Combine(outputDirectory, "iis_dependency_expansion_plan.csv"));
    }

    internal static IReadOnlyList<IisDependencyPlanDiagnostic> FromPlan(
        string candidateId,
        int expansionRound,
        DependencyExpansionPlan plan) => plan.ProposedReleases
        .Concat(plan.EffectiveBlockedDependencies)
        .Select(item =>
        new IisDependencyPlanDiagnostic(
            candidateId,
            expansionRound,
            item.DependencyType,
            item.TrainI,
            item.TrainJ,
            item.Station,
            item.Section,
            item.Event,
            item.VariableKey,
            item.SourceIisElement,
            item.Reason,
            item.WasAlreadyReleased,
            item.WillRelease,
            item.BlockedByHardRule,
            item.ReleaseSource)).ToList();

    private static void WriteTraceCsv(
        IReadOnlyList<IisGuidedRepairTraceDiagnostic> diagnostics,
        string path)
    {
        using StreamWriter writer = new(path, false, new UTF8Encoding(true));
        writer.WriteLine("candidate_id,expansion_round,repair_level,temporal_window,released_train_count,released_time_count,released_section_precedence_count,released_station_event_precedence_count,released_stop_count,released_supplement_count,iis_constraint_count,iis_bound_count,new_section_precedence_dependencies,new_station_event_dependencies,new_temporal_events,new_stop_dependencies,new_supplement_dependencies,original_hard_blocker_count,solver_status,runtime,solution_count,accepted,termination_reason");
        foreach (IisGuidedRepairTraceDiagnostic item in diagnostics)
        {
            writer.WriteLine(string.Join(',',
                Escape(item.CandidateId), item.ExpansionRound, item.RepairLevel,
                item.TemporalWindowSeconds, item.ReleasedTrainCount, item.ReleasedTimeCount,
                item.ReleasedSectionPrecedenceCount, item.ReleasedStationEventPrecedenceCount,
                item.ReleasedStopCount, item.ReleasedSupplementCount,
                item.IisConstraintCount, item.IisBoundCount,
                item.NewSectionPrecedenceDependencies, item.NewStationEventDependencies,
                item.NewTemporalEvents, item.NewStopDependencies, item.NewSupplementDependencies,
                item.OriginalHardBlockerCount, Escape(item.SolverStatus),
                item.RuntimeSeconds.ToString("F6", CultureInfo.InvariantCulture), item.SolutionCount,
                item.Accepted?.ToString() ?? string.Empty, Escape(item.TerminationReason)));
        }
    }

    private static void WriteTraceMarkdown(
        IReadOnlyList<IisGuidedRepairTraceDiagnostic> diagnostics,
        string path)
    {
        StringBuilder builder = new();
        builder.AppendLine("# IIS-Guided Repair Trace");
        builder.AppendLine();
        builder.AppendLine("| candidate | round | level/window | released P/E/T/S/R | IIS C/B | status | sol | accepted | termination |");
        builder.AppendLine("|---|---:|---|---|---|---|---:|---|---|");
        foreach (IisGuidedRepairTraceDiagnostic item in diagnostics)
        {
            builder.AppendLine($"| `{item.CandidateId}` | {item.ExpansionRound} | {item.RepairLevel}/{item.TemporalWindowSeconds} | {item.ReleasedSectionPrecedenceCount}/{item.ReleasedStationEventPrecedenceCount}/{item.ReleasedTimeCount}/{item.ReleasedStopCount}/{item.ReleasedSupplementCount} | {item.IisConstraintCount}/{item.IisBoundCount} | `{item.SolverStatus}` | {item.SolutionCount} | {item.Accepted?.ToString() ?? "-"} | `{item.TerminationReason}` |");
        }
        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(true));
    }

    private static void WritePlanCsv(
        IReadOnlyList<IisDependencyPlanDiagnostic> diagnostics,
        string path)
    {
        using StreamWriter writer = new(path, false, new UTF8Encoding(true));
        writer.WriteLine("candidate_id,expansion_round,dependency_type,train_i,train_j,station,section,event,variable_key,source_iis_element,reason,was_already_released,will_release,blocked_by_hard_rule,release_source");
        foreach (IisDependencyPlanDiagnostic item in diagnostics)
        {
            writer.WriteLine(string.Join(',',
                Escape(item.CandidateId), item.ExpansionRound, item.DependencyType,
                item.TrainI?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                item.TrainJ?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                Escape(item.Station), Escape(item.Section), Escape(item.Event),
                Escape(item.VariableKey), Escape(item.SourceIisElement), Escape(item.Reason),
                item.WasAlreadyReleased, item.WillRelease, item.BlockedByHardRule,
                Escape(item.ReleaseSource)));
        }
    }

    private static string Escape(string value) =>
        !value.Contains(',') && !value.Contains('"') && !value.Contains('\n') && !value.Contains('\r')
            ? value
            : $"\"{value.Replace("\"", "\"\"")}\"";
}
