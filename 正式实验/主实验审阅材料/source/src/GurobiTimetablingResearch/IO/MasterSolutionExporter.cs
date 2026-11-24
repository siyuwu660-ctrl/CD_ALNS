// 主问题结果导出器从 Gurobi 变量和模型统计量中提取到发时刻、停站选择、径路股道与目标界，并写成可复核的 CSV/JSON 产物。
// 导出时统一处理无 incumbent、界值方向和数值格式，保证快照校验、实验比较与人工检查读取到稳定字段。
using System.Globalization;
using System.Text;
using System.Text.Json;
using Gurobi;
using GurobiTimetablingResearch.Domain;
using GurobiTimetablingResearch.Optimization;

namespace GurobiTimetablingResearch.IO;

public sealed record OptionalSolverAttributeValue(double? Value, string UnavailableReason);

public static class ModelStatsBoundPolicy
{
    public static bool ShouldExportSingleObjectiveBounds(int objectiveCount) => objectiveCount <= 1;

    /// <summary>
    /// 仅读取当前求解状态允许访问的可选属性。Gurobi 明确返回“属性不可用”时记录空值，
    /// 其他求解器异常继续向上传递，避免导出过程掩盖真实模型错误。
    /// </summary>
    public static OptionalSolverAttributeValue ReadOptionalSolverAttribute(
        Func<double> reader,
        string attributeName)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentException.ThrowIfNullOrWhiteSpace(attributeName);
        try
        {
            return new OptionalSolverAttributeValue(reader(), string.Empty);
        }
        catch (GRBException exception) when (exception.ErrorCode == GRB.Error.DATA_NOT_AVAILABLE)
        {
            return new OptionalSolverAttributeValue(
                null,
                $"{attributeName}_unavailable_for_current_model_state");
        }
    }

    public static OptionalSolverAttributeValue Unavailable(
        string attributeName,
        string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attributeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new OptionalSolverAttributeValue(null, $"{attributeName}_{reason}");
    }
}

/// <summary>
/// 把主问题求解结果导出为 Checker 和实验分析可读取的业务表。
/// 输出以“列车—车站—时间—停站”等领域字段组织，并补充 Gurobi 模型统计量。
/// </summary>
public sealed class MasterSolutionExporter
{
    public void Export(BasicMilpModel model, string outputDirectory)
    {
        if (!model.HasIncumbentSolution)
        {
            throw new InvalidOperationException("Cannot export master solution because Gurobi has no incumbent solution.");
        }

        Directory.CreateDirectory(outputDirectory);

        WriteSchedule(model, Path.Combine(outputDirectory, "schedule.csv"));
        WriteSelectedStops(model, Path.Combine(outputDirectory, "selected_stops.csv"));
        WriteSectionPrecedence(model, Path.Combine(outputDirectory, "section_precedence.csv"));
        WriteDwellDiagnostics(model, Path.Combine(outputDirectory, "dwell_diagnostics.csv"));
        WriteRunningTimeSupplementDiagnostics(model, Path.Combine(outputDirectory, "running_time_supplement_diagnostics.csv"));
        WritePromotionDiagnostics(model, Path.Combine(outputDirectory, "promotion_diagnostics.csv"));
        WriteModelStats(model, Path.Combine(outputDirectory, "model_stats.json"));
        WriteConstraintNameInventory(model, Path.Combine(outputDirectory, "constraint_names.csv"));
    }

    private static void WriteDwellDiagnostics(BasicMilpModel model, string path)
    {
        IReadOnlySet<string> overtaken = OvertakingStateEvaluator.ResolveActuallyOvertakenStopIds(model);
        using StreamWriter writer = CreateUtf8Writer(path);
        writer.WriteLine("train,station,is_stop,is_being_overtaken,dwell_seconds,normal_min,normal_max,waiting_max,applied_dwell_mode");
        foreach (TrainStopRecord stop in model.Instance.TrainStops
            .Where(stop => stop.StopRole == TrainStopRole.Intermediate)
            .OrderBy(stop => stop.TrainIndex).ThenBy(stop => stop.StationOrder))
        {
            int isStop = RoundBinary(model.StopVars[stop.StopId]);
            bool isOvertaken = overtaken.Contains(stop.StopId);
            NormalDwellResolution dwell = NormalDwellTimeResolver.Resolve(model.Instance.NormalDwellTimeRules, stop);
            string mode = isStop == 0 ? "pass" : isOvertaken ? "waiting_overtaken" : "normal";
            writer.WriteLine(string.Join(',',
                stop.TrainIndex.ToString(CultureInfo.InvariantCulture),
                Escape(stop.StationName),
                isStop.ToString(CultureInfo.InvariantCulture),
                isOvertaken ? "1" : "0",
                (GetValue(model.DepartureVars[stop.StopId]) - GetValue(model.ArrivalVars[stop.StopId])).ToString("F3", CultureInfo.InvariantCulture),
                dwell.NormalMinDwellSeconds.ToString(CultureInfo.InvariantCulture),
                dwell.NormalMaxDwellSeconds.ToString(CultureInfo.InvariantCulture),
                dwell.WaitingMaxDwellSeconds.ToString(CultureInfo.InvariantCulture),
                mode));
        }
    }

    private static void WriteRunningTimeSupplementDiagnostics(BasicMilpModel model, string path)
    {
        using StreamWriter writer = CreateUtf8Writer(path);
        writer.WriteLine("train,section,base_runtime,start_add,stop_add,supplement_seconds,supplement_ratio_used,realized_runtime");
        foreach (TrainRecord train in model.Instance.Trains.OrderBy(train => train.TrainIndex))
        {
            for (int index = 0; index < train.OrderedStops.Count - 1; index++)
            {
                TrainStopRecord current = train.OrderedStops[index];
                TrainStopRecord next = train.OrderedStops[index + 1];
                RunTimeRule rule = ResolveRunTimeRule(model.Instance, current, next);
                string sectionName = next.IncomingSectionName!;
                string key = RunningTimeSupplementPolicy.BuildKey(train.TrainIndex, sectionName);
                double supplement = model.RunningTimeSupplementVars.TryGetValue(key, out GRBVar? variable)
                    ? GetValue(variable)
                    : 0.0;
                int startAdd = RoundBinary(model.StopVars[current.StopId]) == 1 ? rule.StartAddSeconds : 0;
                int stopAdd = RoundBinary(model.StopVars[next.StopId]) == 1 ? rule.StopAddSeconds : 0;
                double realized = GetValue(model.ArrivalVars[next.StopId]) - GetValue(model.DepartureVars[current.StopId]);
                writer.WriteLine(string.Join(',',
                    train.TrainIndex.ToString(CultureInfo.InvariantCulture),
                    Escape(sectionName),
                    rule.RunTimeSeconds.ToString(CultureInfo.InvariantCulture),
                    startAdd.ToString(CultureInfo.InvariantCulture),
                    stopAdd.ToString(CultureInfo.InvariantCulture),
                    supplement.ToString("F3", CultureInfo.InvariantCulture),
                    model.RunningTimeSupplementRatio.ToString("0.####", CultureInfo.InvariantCulture),
                    realized.ToString("F3", CultureInfo.InvariantCulture)));
            }
        }
    }

    private static void WritePromotionDiagnostics(BasicMilpModel model, string path)
    {
        using StreamWriter writer = CreateUtf8Writer(path);
        writer.WriteLine("station,component_id,train_count,alternative_variable_count,order_variable_count,legacy_order_variable_count,constraint_count,source_core,promotion_iteration,persistent_rounds");
        foreach (PromotedMicroBuildDiagnostic item in model.PromotedMicroBuildDiagnostics)
        {
            writer.WriteLine(string.Join(',',
                Escape(item.StationName), Escape(item.ComponentId), item.TrainCount,
                item.AlternativeVariableCount, item.OrderVariableCount,
                item.LegacyOrderVariableCount, item.ConstraintCount, Escape(item.SourceCore),
                item.PromotionIteration, item.PersistentRounds));
        }
    }

    private static void WriteSchedule(BasicMilpModel model, string path)
    {
        using StreamWriter writer = CreateUtf8Writer(path);
        writer.WriteLine("train_index,train_no,train_type,station_order,station_name,arrival_seconds,departure_seconds,is_stop,stop_requirement,role");

        foreach (TrainRecord train in model.Instance.Trains.OrderBy(train => train.TrainIndex))
        {
            foreach (TrainStopRecord stop in train.OrderedStops.OrderBy(stop => stop.StationOrder))
            {
                writer.WriteLine(string.Join(
                    ',',
                    stop.TrainIndex.ToString(CultureInfo.InvariantCulture),
                    Escape(stop.TrainNo),
                    Escape(stop.TrainType),
                    stop.StationOrder.ToString(CultureInfo.InvariantCulture),
                    Escape(stop.StationName),
                    GetValue(model.ArrivalVars[stop.StopId]).ToString("F3", CultureInfo.InvariantCulture),
                    GetValue(model.DepartureVars[stop.StopId]).ToString("F3", CultureInfo.InvariantCulture),
                    RoundBinary(model.StopVars[stop.StopId]).ToString(CultureInfo.InvariantCulture),
                    stop.StopRequirement,
                    stop.StopRole));
            }
        }
    }

    private static void WriteSelectedStops(BasicMilpModel model, string path)
    {
        using StreamWriter writer = CreateUtf8Writer(path);
        writer.WriteLine("train_index,train_no,station_order,station_name,is_stop,dwell_seconds");

        foreach (TrainStopRecord stop in model.Instance.TrainStops.OrderBy(stop => stop.TrainIndex).ThenBy(stop => stop.StationOrder))
        {
            int isStop = RoundBinary(model.StopVars[stop.StopId]);
            double dwellSeconds = GetValue(model.DepartureVars[stop.StopId]) - GetValue(model.ArrivalVars[stop.StopId]);

            writer.WriteLine(string.Join(
                ',',
                stop.TrainIndex.ToString(CultureInfo.InvariantCulture),
                Escape(stop.TrainNo),
                stop.StationOrder.ToString(CultureInfo.InvariantCulture),
                Escape(stop.StationName),
                isStop.ToString(CultureInfo.InvariantCulture),
                dwellSeconds.ToString("F3", CultureInfo.InvariantCulture)));
        }
    }

    private static void WriteSectionPrecedence(BasicMilpModel model, string path)
    {
        using StreamWriter writer = CreateUtf8Writer(path);
        writer.WriteLine("section_name,first_train_index,second_train_index,start_station,end_station,rank_gap,precedence_value,actual_order");

        foreach (SectionConflictCandidate candidate in model.SectionConflictCandidates)
        {
            string key = BasicMilpModelBuilder.BuildSectionPrecedenceKey(candidate);
            if (!model.SectionPrecedenceVars.TryGetValue(key, out GRBVar? precedenceVar))
            {
                continue;
            }

            double value = GetValue(precedenceVar);
            string actualOrder = value < 0.5 ? "first_before_second" : "second_before_first";

            writer.WriteLine(string.Join(
                ',',
                Escape(candidate.SectionName),
                candidate.FirstTrainIndex.ToString(CultureInfo.InvariantCulture),
                candidate.SecondTrainIndex.ToString(CultureInfo.InvariantCulture),
                Escape(candidate.StartStationName),
                Escape(candidate.EndStationName),
                candidate.RankGap.ToString(CultureInfo.InvariantCulture),
                value.ToString("F3", CultureInfo.InvariantCulture),
                actualOrder));
        }
    }

    private static void WriteModelStats(BasicMilpModel model, string path)
    {
        int objectiveCount = model.Model.Get(GRB.IntAttr.NumObj);
        int statusCode = model.Model.Get(GRB.IntAttr.Status);
        bool exportSingleObjectiveBounds =
            ModelStatsBoundPolicy.ShouldExportSingleObjectiveBounds(objectiveCount);
        OptionalSolverAttributeValue objectiveValue =
            ModelStatsBoundPolicy.ReadOptionalSolverAttribute(
                () => model.Model.Get(GRB.DoubleAttr.ObjVal),
                "ObjVal");
        OptionalSolverAttributeValue bestBound;
        OptionalSolverAttributeValue mipGap;
        if (!exportSingleObjectiveBounds)
        {
            bestBound = ModelStatsBoundPolicy.Unavailable("ObjBound", "multi_objective_model");
            mipGap = ModelStatsBoundPolicy.Unavailable("MIPGap", "multi_objective_model");
        }
        else if (statusCode == GRB.Status.LOADED)
        {
            // 修改已求解模型后，Gurobi 可能保留 X/ObjVal，但旧搜索界已经失效，不能再导出为当前界。
            bestBound = ModelStatsBoundPolicy.Unavailable("ObjBound", "model_loaded_or_modified");
            mipGap = ModelStatsBoundPolicy.Unavailable("MIPGap", "model_loaded_or_modified");
        }
        else
        {
            bestBound = ModelStatsBoundPolicy.ReadOptionalSolverAttribute(
                () => model.Model.Get(GRB.DoubleAttr.ObjBound),
                "ObjBound");
            mipGap = ModelStatsBoundPolicy.ReadOptionalSolverAttribute(
                () => model.Model.Get(GRB.DoubleAttr.MIPGap),
                "MIPGap");
        }
        List<double> supplements = model.RunningTimeSupplementVars.Values.Select(GetValue).ToList();
        var stats = new
        {
            instance = model.Instance.Meta.InstanceName,
            status = model.StatusName,
            objective_value = objectiveValue.Value,
            objective_value_unavailable_reason = objectiveValue.UnavailableReason,
            best_bound = bestBound.Value,
            best_bound_unavailable_reason = bestBound.UnavailableReason,
            best_bound_scope = bestBound.Value.HasValue
                ? "current_exported_model_only"
                : string.Empty,
            is_global_problem_lower_bound = false,
            mip_gap = mipGap.Value,
            mip_gap_unavailable_reason = mipGap.UnavailableReason,
            runtime_seconds = model.Model.Get(GRB.DoubleAttr.Runtime),
            solution_count = model.Model.Get(GRB.IntAttr.SolCount),
            train_count = model.Instance.Trains.Count,
            stop_count = model.Instance.TrainStops.Count,
            section_count = model.Instance.Sections.Count,
            movement_headway_rule_count = model.Instance.MovementHeadwayRules.Count,
            section_conflict_candidate_count = model.SectionConflictCandidates.Count,
            variable_count = model.Model.Get(GRB.IntAttr.NumVars),
            constraint_count = model.Model.Get(GRB.IntAttr.NumConstrs),
            total_running_supplement_seconds = supplements.Sum(),
            max_running_supplement_seconds = supplements.Count == 0 ? 0.0 : supplements.Max(),
            sections_using_supplement = supplements.Count(value => value > 1e-6),
            average_supplement_seconds = supplements.Count == 0 ? 0.0 : supplements.Average(),
            promoted_micro_component_count = model.PromotedMicroComponents.Count,
            promoted_micro_alternative_variable_count = model.PromotedMicroAlternativeVariableCount,
            promoted_micro_order_variable_count = model.PromotedMicroOrderVariableCount,
            promoted_micro_legacy_order_variable_count = model.PromotedMicroLegacyOrderVariableCount,
            promoted_micro_bound_pruned_relation_count = model.PromotedMicroBoundPrunedRelationCount,
            promoted_micro_conditional_constraint_count = model.PromotedMicroConditionalConstraintCount
        };

        string json = JsonSerializer.Serialize(stats, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static void WriteConstraintNameInventory(BasicMilpModel model, string path)
    {
        using StreamWriter writer = CreateUtf8Writer(path);
        writer.WriteLine("constraint_name");
        foreach (GRBConstr constraint in model.Model.GetConstrs())
        {
            writer.WriteLine(Escape(constraint.Get(GRB.StringAttr.ConstrName)));
        }
    }

    private static StreamWriter CreateUtf8Writer(string path)
    {
        return new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static double GetValue(GRBVar variable)
    {
        return variable.Get(GRB.DoubleAttr.X);
    }

    private static int RoundBinary(GRBVar variable)
    {
        return GetValue(variable) >= 0.5 ? 1 : 0;
    }

    private static RunTimeRule ResolveRunTimeRule(
        TimetablingInstance instance,
        TrainStopRecord current,
        TrainStopRecord next)
    {
        string sectionName = next.IncomingSectionName ??
            throw new InvalidDataException($"Stop '{next.StopId}' has no incoming section.");
        SectionRecord section = instance.SectionByName[sectionName];
        TravelDirection direction =
            string.Equals(section.FromStation, current.StationName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(section.ToStation, next.StationName, StringComparison.OrdinalIgnoreCase)
                ? TravelDirection.Down
                : TravelDirection.Up;
        return instance.RunTimeRules.Single(rule =>
            string.Equals(rule.ScaleName, next.RunScaleName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(rule.SectionName, sectionName, StringComparison.OrdinalIgnoreCase) &&
            rule.Direction == direction);
    }

    private static string Escape(string value)
    {
        if (!value.Contains(',') && !value.Contains('"') && !value.Contains('\n') && !value.Contains('\r'))
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }
}
