using System.Text.RegularExpressions;
using Gurobi;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

internal sealed record TimeCategoryContradiction(
    string StopId,
    string FirstLiteral,
    string SecondLiteral,
    PatternTimeCategoryInterval FirstInterval,
    PatternTimeCategoryInterval SecondInterval,
    string Reason);

internal sealed record MicroPatternSelectionConsistencyDetail(
    int Iteration,
    string ConflictId,
    IReadOnlyList<OvertakingStopProjectionViolation> OvertakingStopViolations,
    IReadOnlyList<TimeCategoryContradiction> TimeCategoryContradictions);

internal sealed record MicroPatternConsistencyAuditResult(
    int SelectionCount,
    int OvertakingStopSelectionCount,
    int TimeCategorySelectionCount,
    IReadOnlyList<MicroPatternSelectionConsistencyDetail> Details);

/// <summary>
/// Arithmetic audit over persisted complete pattern-master selections. It shares the same
/// overtaking projection planner and category intervals as the formal master, so historical
/// counts exercise the production construction logic without re-optimizing JZ.
/// </summary>
internal static class MicroPatternConsistencyAuditor
{
    internal static MicroPatternConsistencyAuditResult Audit(
        TimetablingInstance instance,
        MasterSolutionSnapshot reference,
        StationPatternPool pool,
        IReadOnlyList<PatternCombinationConflict> completeSelections)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(completeSelections);

        using BasicMilpModel source = new BasicMilpModelBuilder(
            instance, neighborRadius: int.MaxValue).Build();
        OvertakingStopProjection[] overtaking = OvertakingStopProjectionPlanner.Plan(
            instance, source.SectionConflictCandidates).ToArray();
        Dictionary<string, QuotaLiteral> quotaByName = BuildQuotaLiterals(instance, source);
        Dictionary<string, StationPattern> patternById = pool.Patterns.ToDictionary(
            item => item.PatternId, StringComparer.Ordinal);
        List<MicroPatternSelectionConsistencyDetail> details = [];

        foreach (PatternCombinationConflict selection in completeSelections)
        {
            Dictionary<string, int> stops = new(StringComparer.OrdinalIgnoreCase);
            foreach (string patternId in selection.RequiredPatternsByStation.Values)
            {
                if (!patternById.TryGetValue(patternId, out StationPattern? pattern))
                    throw new InvalidDataException($"Selection '{selection.ConflictId}' references missing pattern '{patternId}'.");
                foreach (VerifiedMicroAssignmentWitness witness in pattern.Certificate.Witnesses)
                    stops[witness.RequirementId] = witness.IsStop ? 1 : 0;
            }
            Dictionary<string, int> precedence = new(reference.SectionPrecedenceByKey,
                StringComparer.OrdinalIgnoreCase);
            foreach ((string key, int value) in selection.RequiredDiscreteLiterals)
                if (key.StartsWith("section:", StringComparison.Ordinal)) precedence[key["section:".Length..]] = value;
            IReadOnlyList<OvertakingStopProjectionViolation> overtakingViolations =
                OvertakingStopProjectionPlanner.FindViolations(overtaking, stops, precedence);
            IReadOnlyList<TimeCategoryContradiction> categoryContradictions =
                FindCategoryContradictions(selection.RequiredDiscreteLiterals, quotaByName);
            details.Add(new(ParseIteration(selection.ProofIdentity), selection.ConflictId,
                overtakingViolations, categoryContradictions));
        }

        return new(completeSelections.Count,
            details.Count(item => item.OvertakingStopViolations.Count > 0),
            details.Count(item => item.TimeCategoryContradictions.Count > 0),
            details);
    }

    private static Dictionary<string, QuotaLiteral> BuildQuotaLiterals(
        TimetablingInstance instance,
        BasicMilpModel source)
    {
        Dictionary<string, QuotaLiteral> result = new(StringComparer.Ordinal);
        foreach (StationQuotaCategoryBinding binding in source.StationQuotaCategoryBindings)
        {
            TrainStopRecord stop = instance.StopsById[binding.StopId];
            double lower = stop.StopRole == TrainStopRole.Origin
                ? stop.EarliestDepSeconds : stop.EarliestArrSeconds;
            double upper = stop.StopRole == TrainStopRole.Origin
                ? stop.LatestDepSeconds : stop.LatestArrSeconds;
            Add(binding.InWindowVariable, MacroQuotaCategory.InWindow);
            Add(binding.LeftOutsideVariable, MacroQuotaCategory.LeftOutside);
            Add(binding.RightOutsideVariable, MacroQuotaCategory.RightOutside);

            void Add(GRBVar variable, MacroQuotaCategory category)
            {
                string name = variable.Get(GRB.StringAttr.VarName);
                result.Add(name, new(binding.BindingId, binding.StopId, name, category,
                    PatternTimeCategoryCompatibility.Resolve(lower, upper, category,
                        binding.WindowStartSeconds, binding.WindowEndSeconds)));
            }
        }
        return result;
    }

    private static IReadOnlyList<TimeCategoryContradiction> FindCategoryContradictions(
        IReadOnlyDictionary<string, int> literals,
        IReadOnlyDictionary<string, QuotaLiteral> quotaByName)
    {
        QuotaLiteral[] selected = literals.Where(item => item.Value == 1 &&
                item.Key.StartsWith("quota:", StringComparison.Ordinal) &&
                quotaByName.ContainsKey(item.Key["quota:".Length..]))
            .Select(item => quotaByName[item.Key["quota:".Length..]])
            .ToArray();
        List<TimeCategoryContradiction> result = [];
        foreach (QuotaLiteral literal in selected.Where(item => item.Interval.IsEmpty))
            result.Add(new(literal.StopId, literal.VariableName, literal.VariableName,
                literal.Interval, literal.Interval, "selected_category_outside_original_event_bounds"));
        foreach (IGrouping<string, QuotaLiteral> group in selected.GroupBy(
                     item => item.StopId, StringComparer.OrdinalIgnoreCase))
        {
            QuotaLiteral[] items = group.ToArray();
            for (int first = 0; first < items.Length; first++)
            for (int second = first + 1; second < items.Length; second++)
            {
                if (string.Equals(items[first].BindingId, items[second].BindingId,
                        StringComparison.OrdinalIgnoreCase) ||
                    items[first].Interval.IsEmpty || items[second].Interval.IsEmpty ||
                    items[first].Interval.Intersects(items[second].Interval)) continue;
                result.Add(new(group.Key, items[first].VariableName, items[second].VariableName,
                    items[first].Interval, items[second].Interval,
                    "same_event_selected_into_disjoint_time_categories"));
            }
        }
        return result;
    }

    private static int ParseIteration(string identity)
    {
        Match match = Regex.Match(identity ?? string.Empty, @"(?:^|:)iteration=(\d+)(?:$|:)",
            RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups[1].Value, out int value) ? value : -1;
    }

    private sealed record QuotaLiteral(
        string BindingId,
        string StopId,
        string VariableName,
        MacroQuotaCategory Category,
        PatternTimeCategoryInterval Interval);
}
