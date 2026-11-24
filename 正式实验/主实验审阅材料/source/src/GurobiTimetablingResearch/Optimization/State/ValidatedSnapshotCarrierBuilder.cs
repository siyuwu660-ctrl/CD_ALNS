// 已验证快照载体构建器创建与原实例变量结构一致的模型，将快照固定或作为起点后执行可行性确认。
// 返回的载体同时包含模型、规范快照和验证报告，供微观检查、续跑预检及受控候选修复安全复用。
using Gurobi;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

public sealed record ValidatedSnapshotCarrier(
    BasicMilpModel Model,
    MasterSolutionSnapshot Snapshot,
    MasterValidationReport Validation) : IDisposable
{
    public void Dispose() => Model.Dispose();
}

public sealed class ValidatedSnapshotCarrierBuilder
{
    public ValidatedSnapshotCarrier Build(
        TimetablingInstance instance,
        int? neighborRadius,
        MasterSolutionSnapshot snapshot,
        double repairSupplementRatio,
        IReadOnlyList<SectionConflictEdgeRequest>? sectionEdges = null,
        IReadOnlyList<StationEventConflictCandidate>? stationEdges = null,
        IReadOnlyList<PromotedMicroComponent>? promotedComponents = null,
        int endpointOccupationSeconds = StationEndpointOccupationPolicy.DefaultSeconds,Func<double>? remainingSeconds=null,ServiceWindowPolicy? serviceWindows=null)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(snapshot);
        double ratio = CheckerRepairSupplementOptions.Create(repairSupplementRatio).Ratio;
        HashSet<int> expectedTrains = instance.Trains.Select(train => train.TrainIndex).ToHashSet();
        if (!snapshot.AdmittedTrainIndices.SetEquals(expectedTrains))
        {
            throw new InvalidDataException("Master snapshot train set does not match the fixed carrier instance.");
        }
        if (!double.IsFinite(snapshot.ObjectiveValue))
        {
            throw new InvalidDataException("Master snapshot objective must be finite.");
        }

        BasicMilpModel model = new BasicMilpModelBuilder(
            instance,
            neighborRadius,
            additionalSectionEdges: sectionEdges,
            stationEventEdges: stationEdges,
            runningTimeSupplementRatio: ratio,
            promotedMicroComponents: promotedComponents,
            endpointOccupationSeconds: endpointOccupationSeconds,serviceWindows:serviceWindows).Build();
        try
        {
            if(remainingSeconds is not null && remainingSeconds()<=0) throw new TimeoutException("Budget expired during fixed carrier build.");
            ValidateExactCoverage(model.ArrivalVars.Keys, snapshot.ArrivalSecondsByStopId.Keys, "arrival");
            ValidateExactCoverage(model.DepartureVars.Keys, snapshot.DepartureSecondsByStopId.Keys, "departure");
            ValidateExactCoverage(model.StopVars.Keys, snapshot.StopByStopId.Keys, "stop");
            ValidateExactCoverage(
                model.SectionPrecedenceVars.Keys,
                snapshot.SectionPrecedenceByKey.Keys,
                "section precedence");
            ValidateExactCoverage(
                model.StationEventPrecedenceVars.Keys,
                snapshot.StationEventPrecedenceByKey.Keys,
                "station-event precedence");

            IReadOnlyDictionary<string, double> supplementValues =
                snapshot.RunningTimeSupplementSecondsByKey is null or { Count: 0 }
                    ? model.RunningTimeSupplementVars.Keys.ToDictionary(
                        key => key, _ => 0.0, StringComparer.OrdinalIgnoreCase)
                    : snapshot.RunningTimeSupplementSecondsByKey;
            ValidateExactCoverage(
                model.RunningTimeSupplementVars.Keys,
                supplementValues.Keys,
                "running-time supplement");

            FixContinuous(model.ArrivalVars, snapshot.ArrivalSecondsByStopId, "arrival");
            FixContinuous(model.DepartureVars, snapshot.DepartureSecondsByStopId, "departure");
            FixBinary(model.StopVars, snapshot.StopByStopId, "stop");
            FixBinary(model.SectionPrecedenceVars, snapshot.SectionPrecedenceByKey, "section precedence");
            FixBinary(
                model.StationEventPrecedenceVars,
                snapshot.StationEventPrecedenceByKey,
                "station-event precedence");
            FixNonNegative(model.RunningTimeSupplementVars, supplementValues, "running-time supplement");
            StationQuotaWindowPolicy.FixSnapshotCategories(model, snapshot);

            model.Model.Set(GRB.IntParam.OutputFlag, 0);
            model.Model.Set(GRB.IntParam.SolutionLimit, 1);
            model.Model.Set(GRB.DoubleParam.TimeLimit, Math.Min(5.0,Math.Max(0.001,remainingSeconds?.Invoke()??5.0)));
            model.Optimize();
            if (!model.HasIncumbentSolution)
            {
                SnapshotCarrierSolveOutcomePolicy.ThrowNoIncumbent(model.Model.Status,remainingSeconds is not null);
            }

            MasterValidationReport validation = new MasterSolutionValidator().Validate(model);
            if (!validation.IsFeasible || validation.Violations.Count != 0)
            {
                throw new InvalidDataException(
                    "Fixed Master snapshot carrier failed validation: " +
                    string.Join(" | ", validation.Violations));
            }
            return new ValidatedSnapshotCarrier(model, snapshot, validation);
        }
        catch
        {
            model.Dispose();
            throw;
        }
    }

    private static void ValidateExactCoverage(
        IEnumerable<string> expectedKeys,
        IEnumerable<string> actualKeys,
        string family)
    {
        HashSet<string> expected = expectedKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> actual = actualKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (expected.SetEquals(actual))
        {
            return;
        }

        string missing = string.Join('|', expected.Except(actual, StringComparer.OrdinalIgnoreCase).Take(10));
        string extra = string.Join('|', actual.Except(expected, StringComparer.OrdinalIgnoreCase).Take(10));
        throw new InvalidDataException(
            $"Master snapshot {family} key coverage mismatch; missing=[{missing}], extra=[{extra}].");
    }

    private static void FixContinuous(
        IReadOnlyDictionary<string, GRBVar> variables,
        IReadOnlyDictionary<string, double> values,
        string family)
    {
        foreach ((string key, GRBVar variable) in variables)
        {
            double value = values[key];
            if (!double.IsFinite(value))
            {
                throw new InvalidDataException($"Master snapshot {family} '{key}' is not finite.");
            }
            Fix(variable, value);
        }
    }

    private static void FixNonNegative(
        IReadOnlyDictionary<string, GRBVar> variables,
        IReadOnlyDictionary<string, double> values,
        string family)
    {
        foreach ((string key, GRBVar variable) in variables)
        {
            double value = values[key];
            if (!double.IsFinite(value) || value < 0.0)
            {
                throw new InvalidDataException(
                    $"Master snapshot {family} '{key}' must be finite and non-negative.");
            }
            Fix(variable, value);
        }
    }

    private static void FixBinary(
        IReadOnlyDictionary<string, GRBVar> variables,
        IReadOnlyDictionary<string, int> values,
        string family)
    {
        foreach ((string key, GRBVar variable) in variables)
        {
            int value = values[key];
            if (value is not 0 and not 1)
            {
                throw new InvalidDataException($"Master snapshot {family} '{key}' must be binary.");
            }
            Fix(variable, value);
        }
    }

    private static void Fix(GRBVar variable, double value)
    {
        variable.Set(GRB.DoubleAttr.LB, value);
        variable.Set(GRB.DoubleAttr.UB, value);
    }
}

internal static class SnapshotCarrierSolveOutcomePolicy
{
    internal static void ThrowNoIncumbent(int status,bool bounded)
    {
        if(bounded && status!=GRB.Status.INFEASIBLE)
            throw new BoundedSnapshotCarrierUnknownException($"Bounded fixed carrier has no incumbent; status={status}.");
        if(bounded) throw new BoundedSnapshotCandidateRejectedException($"Fixed saved candidate is infeasible; status={status}.");
        throw new InvalidDataException($"Fixed Master snapshot carrier has no incumbent; solver status={status}.");
    }
}

internal sealed class BoundedSnapshotCarrierUnknownException(string message):TimeoutException(message);
internal sealed class BoundedSnapshotCandidateRejectedException(string message):IOException(message);
