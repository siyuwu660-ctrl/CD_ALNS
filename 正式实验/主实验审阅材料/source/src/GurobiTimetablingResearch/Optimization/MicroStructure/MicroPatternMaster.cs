using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Gurobi;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

internal sealed record MicroPatternMasterAudit(
    int PatternVariableCount,
    int EndpointVariableCount,
    int MaintenanceVariableCount,
    int QuotaCategoryVariableCount,
    int ConstraintCount,
    int PresolvedVariableCount,
    int PresolvedBinaryCount,
    int PresolvedConstraintCount,
    int OvertakingStopProjectionCount,
    int EmptyTimeCategoryCount,
    int IncompatibleTimeCategoryPairCount);

internal sealed record MicroPatternMasterResult(
    bool HasSelection,
    int StatusCode,
    string Status,
    double RuntimeSeconds,
    IReadOnlyDictionary<string, StationPattern> SelectedPatterns,
    CompleteMacroDiscreteState? DiscreteState,
    MicroPatternMasterAudit Audit,
    string StopReason,
    MicroPatternMasterInfeasibilityProof? InfeasibilityProof = null);

internal sealed record MicroPatternMasterProofRow(
    string ConstraintName,
    string BusinessSource,
    string? StationName,
    string? StopId);

internal sealed record MicroPatternMasterProofBound(
    string VariableName,
    bool LowerBound,
    bool UpperBound,
    double LowerValue,
    double UpperValue,
    string? BusinessSource,
    string? StationName,
    string? StopId);

internal sealed record MicroPatternMasterInfeasibilityProof(
    string ProofIdentity,
    IReadOnlyList<MicroPatternMasterProofRow> Rows,
    IReadOnlyList<MicroPatternMasterProofBound> VariableBounds,
    IReadOnlyList<string> SuggestedStations,
    IReadOnlyList<string> SuggestedStopIds,
    string Status);

internal sealed class PatternConflictStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private readonly List<PatternCombinationConflict> _conflicts = [];
    internal IReadOnlyList<PatternCombinationConflict> Conflicts => _conflicts.ToArray();
    internal int Count => _conflicts.Count;

    internal bool Add(PatternCombinationConflict conflict)
    {
        ArgumentNullException.ThrowIfNull(conflict);
        if (_conflicts.Any(item => string.Equals(item.ConflictId, conflict.ConflictId, StringComparison.Ordinal)))
            return false;
        _conflicts.Add(conflict);
        return true;
    }

    internal void Save(string path)
    {
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, JsonSerializer.Serialize(_conflicts, JsonOptions), new UTF8Encoding(false));
    }

    internal static PatternConflictStore Load(string path)
    {
        PatternConflictStore store = new();
        PatternCombinationConflict[] items = JsonSerializer.Deserialize<PatternCombinationConflict[]>(
            File.ReadAllText(Path.GetFullPath(path), Encoding.UTF8), JsonOptions) ?? [];
        foreach (PatternCombinationConflict item in items) store.Add(item);
        return store;
    }
}

/// <summary>
/// Compact discrete master: one station pattern per station plus the time-category,
/// maintenance and legally releasable endpoint decisions required to make the downstream
/// full-line checker purely continuous.
/// </summary>
internal sealed class MicroPatternMaster
{
    private static readonly ConditionalWeakTable<TimetablingInstance, Dictionary<string, AuxiliaryMetadata>> MetadataCache = new();
    private readonly TimetablingInstance _instance;
    private readonly MasterSolutionSnapshot _reference;
    private readonly StationPatternPool _pool;
    private readonly PatternConflictStore _conflicts;
    private readonly PrecedenceTrustRegion _trustRegion;
    private readonly AuxiliaryMetadata _metadata;
    private readonly IReadOnlyDictionary<string, string> _dependencies;
    private readonly HashSet<string> _initialPatternIds;

    internal MicroPatternMaster(
        TimetablingInstance instance,
        MasterSolutionSnapshot reference,
        StationPatternPool pool,
        PatternConflictStore conflicts,
        PrecedenceTrustRegion trustRegion,
        IReadOnlyDictionary<string, string> dependencies,
        IReadOnlySet<string> initialPatternIds,
        int endpointOccupationSeconds,
        double runningTimeSupplementRatio)
    {
        _instance = instance;
        _reference = reference;
        _pool = pool;
        _conflicts = conflicts;
        _trustRegion = trustRegion;
        _dependencies = new Dictionary<string, string>(dependencies, StringComparer.OrdinalIgnoreCase);
        _initialPatternIds = initialPatternIds.ToHashSet(StringComparer.Ordinal);
        _metadata = GetOrDiscoverMetadata(instance, endpointOccupationSeconds, runningTimeSupplementRatio);
    }

    internal MicroPatternMasterResult Solve(double timeLimitSeconds, string? logFile = null)
    {
        if (timeLimitSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(timeLimitSeconds));
        using GRBEnv environment = new();
        using GRBModel model = new(environment) { ModelName = "micro_pattern_master" };
        Dictionary<string, (string Source, string? Station, string? Stop)> constraintProvenance =
            new(StringComparer.Ordinal);
        Dictionary<string, (string Source, string? Station, string? Stop)> variableProvenance =
            new(StringComparer.Ordinal);
        Dictionary<string, string> stationByStop = _instance.TrainStops.ToDictionary(
            stop => stop.StopId, stop => stop.StationName, StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(logFile))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(logFile))!);
            model.Set(GRB.StringParam.LogFile, logFile);
        }
        Dictionary<string, GRBVar> patternVars = new(StringComparer.Ordinal);
        Dictionary<string, GRBLinExpr> stopExpressions = new(StringComparer.OrdinalIgnoreCase);
        foreach (IGrouping<string, StationPattern> group in _pool.Patterns.GroupBy(
                     item => item.StationName, StringComparer.OrdinalIgnoreCase))
        {
            GRBLinExpr exactlyOne = new();
            string[] requiredStopIds = _instance.TrainStops.Where(stop => string.Equals(
                    stop.StationName, group.Key, StringComparison.OrdinalIgnoreCase))
                .Select(stop => stop.StopId).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (StationPattern pattern in group)
            {
                string[] actualStopIds = pattern.Certificate.Witnesses.Select(item => item.RequirementId)
                    .Order(StringComparer.OrdinalIgnoreCase).ToArray();
                if (!requiredStopIds.SequenceEqual(actualStopIds, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        $"Pattern '{pattern.PatternId}' does not cover station '{group.Key}' exactly.");
                string patternVariableName = $"pattern_{Sanitize(group.Key)}_{pattern.PatternId[..12]}";
                GRBVar variable = model.AddVar(0, 1, _initialPatternIds.Contains(pattern.PatternId) ? 0 : 1,
                    GRB.BINARY, patternVariableName);
                patternVars.Add(pattern.PatternId, variable);
                variableProvenance[patternVariableName] =
                    ($"station_pattern:{pattern.PatternId}", group.Key, null);
                exactlyOne.AddTerm(1, variable);
                foreach (VerifiedMicroAssignmentWitness witness in pattern.Certificate.Witnesses)
                {
                    if (!stopExpressions.TryGetValue(witness.RequirementId, out GRBLinExpr? expression))
                        stopExpressions.Add(witness.RequirementId, expression = new GRBLinExpr());
                    expression.AddTerm(witness.IsStop ? 1 : 0, variable);
                }
            }
            string selectName = $"select_one_{Sanitize(group.Key)}";
            model.AddConstr(exactlyOne, GRB.EQUAL, 1, selectName);
            constraintProvenance[selectName] = ("select_exactly_one_station_pattern", group.Key, null);
        }
        string[] missingStops = _instance.TrainStops.Select(stop => stop.StopId)
            .Where(stopId => !stopExpressions.ContainsKey(stopId)).Take(20).ToArray();
        if (missingStops.Length > 0)
            throw new InvalidDataException($"Pattern pool has no coverage for stops [{string.Join('|', missingStops)}].");

        foreach (TrainSectionStopQuotaConstruction construction in TrainSectionStopQuotaConstraintBuilder.Plan(_instance))
        {
            GRBLinExpr count = new();
            foreach (TrainStopRecord stop in construction.CountedStops) count.Add(stopExpressions[stop.StopId]);
            model.AddConstr(count, GRB.GREATER_EQUAL, construction.MinStops,
                $"master_{construction.MinimumConstraintName}");
            model.AddConstr(count, GRB.LESS_EQUAL, construction.MaxStops,
                $"master_{construction.MaximumConstraintName}");
        }

        Dictionary<string, GRBVar> maintenance = new(StringComparer.Ordinal);
        foreach (MaintenanceMetadata binding in _metadata.Maintenance)
            maintenance[binding.VariableName] = model.AddVar(0, 1, 0.001, GRB.BINARY,
                $"master_{Sanitize(binding.VariableName)}");

        Dictionary<string, GRBVar> quota = new(StringComparer.Ordinal);
        int emptyTimeCategories = 0;
        foreach (QuotaMetadata binding in _metadata.Quota)
        {
            GRBVar inWindow = model.AddVar(0, 1, 0.001, GRB.BINARY, $"master_{Sanitize(binding.InWindowName)}");
            GRBVar left = model.AddVar(0, 1, 0.001, GRB.BINARY, $"master_{Sanitize(binding.LeftName)}");
            GRBVar right = model.AddVar(0, 1, 0.001, GRB.BINARY, $"master_{Sanitize(binding.RightName)}");
            quota[binding.InWindowName] = inWindow;
            quota[binding.LeftName] = left;
            quota[binding.RightName] = right;
            model.AddConstr(inWindow + left + right, GRB.EQUAL, stopExpressions[binding.StopId],
                $"master_quota_class_{Sanitize(binding.BindingId)}");
            foreach ((GRBVar variable, MacroQuotaCategory category, string name) in new[]
                     {
                         (inWindow, MacroQuotaCategory.InWindow, binding.InWindowName),
                         (left, MacroQuotaCategory.LeftOutside, binding.LeftName),
                         (right, MacroQuotaCategory.RightOutside, binding.RightName)
                     })
            {
                PatternTimeCategoryInterval interval = PatternTimeCategoryCompatibility.Resolve(
                    binding.EventLowerBound, binding.EventUpperBound, category, binding.Start, binding.End);
                if (!interval.IsEmpty) continue;
                string emptyName = $"master_quota_empty_{Sanitize(name)}";
                model.AddConstr(variable, GRB.EQUAL, 0, emptyName);
                constraintProvenance[emptyName] =
                    ($"original_event_bounds_exclude_category:{binding.BindingId}:{category}",
                        stationByStop[binding.StopId], binding.StopId);
                emptyTimeCategories++;
            }
        }
        foreach (IGrouping<string, QuotaMetadata> group in _metadata.Quota.GroupBy(item => item.RuleId))
        {
            int index = int.Parse(group.Key["station-stop-".Length..]) - 1;
            StationStopQuotaRule rule = _instance.StationStopQuotaRules[index];
            GRBLinExpr count = new();
            foreach (QuotaMetadata item in group) count.AddTerm(1, quota[item.InWindowName]);
            model.AddConstr(count, GRB.GREATER_EQUAL, rule.MinStops, $"master_station_quota_min_{index + 1}");
            model.AddConstr(count, GRB.LESS_EQUAL, rule.MaxStops, $"master_station_quota_max_{index + 1}");
        }

        int incompatibleTimeCategoryPairs = 0;
        foreach (IGrouping<string, QuotaMetadata> stopGroup in _metadata.Quota.GroupBy(
                     item => item.StopId, StringComparer.OrdinalIgnoreCase))
        {
            (string BindingId, string Name, PatternTimeCategoryInterval Interval)[] categories = stopGroup
                .SelectMany(binding => new[]
                {
                    (binding.BindingId, binding.InWindowName, PatternTimeCategoryCompatibility.Resolve(
                        binding.EventLowerBound, binding.EventUpperBound, MacroQuotaCategory.InWindow,
                        binding.Start, binding.End)),
                    (binding.BindingId, binding.LeftName, PatternTimeCategoryCompatibility.Resolve(
                        binding.EventLowerBound, binding.EventUpperBound, MacroQuotaCategory.LeftOutside,
                        binding.Start, binding.End)),
                    (binding.BindingId, binding.RightName, PatternTimeCategoryCompatibility.Resolve(
                        binding.EventLowerBound, binding.EventUpperBound, MacroQuotaCategory.RightOutside,
                        binding.Start, binding.End))
                }).ToArray();
            for (int first = 0; first < categories.Length; first++)
            for (int second = first + 1; second < categories.Length; second++)
            {
                if (string.Equals(categories[first].BindingId, categories[second].BindingId,
                        StringComparison.OrdinalIgnoreCase) ||
                    categories[first].Interval.IsEmpty || categories[second].Interval.IsEmpty ||
                    categories[first].Interval.Intersects(categories[second].Interval))
                    continue;
                string incompatibleName =
                    $"master_quota_incompatible_{StationPattern.Hash(categories[first].Name + "|" + categories[second].Name)[..16]}";
                model.AddConstr(quota[categories[first].Name] + quota[categories[second].Name],
                    GRB.LESS_EQUAL, 1, incompatibleName);
                constraintProvenance[incompatibleName] =
                    ($"same_event_disjoint_categories:{categories[first].Name}|{categories[second].Name}",
                        stationByStop[stopGroup.Key], stopGroup.Key);
                incompatibleTimeCategoryPairs++;
            }
        }

        Dictionary<string, GRBVar> endpoints = new(StringComparer.OrdinalIgnoreCase);
        GRBLinExpr mismatch = new();
        foreach (string key in _trustRegion.HotspotKeys.Where(_reference.SectionPrecedenceByKey.ContainsKey))
        {
            int center = _reference.SectionPrecedenceByKey[key];
            GRBVar variable = model.AddVar(0, 1, 0.001, GRB.BINARY,
                $"master_endpoint_{StationPattern.Hash(key)[..12]}");
            endpoints[key] = variable;
            if (center == 0) mismatch.AddTerm(1, variable);
            else { mismatch.AddConstant(1); mismatch.AddTerm(-1, variable); }
        }
        model.AddConstr(mismatch, GRB.LESS_EQUAL, _trustRegion.MaxTotalChanges, "master_endpoint_k");

        foreach (OvertakingStopProjection projection in _metadata.OvertakingStopProjections)
        {
            GRBLinExpr first = new();
            first.Add(stopExpressions[projection.FirstStopId]);
            AddPrecedenceTerm(first, projection.OutgoingPrecedenceKey, -1, endpoints);
            AddPrecedenceTerm(first, projection.IncomingPrecedenceKey, 1, endpoints);
            string firstName = $"master_overtake_stop_first_{StationPattern.Hash(projection.ProjectionId)[..16]}";
            model.AddConstr(first, GRB.GREATER_EQUAL, 0, firstName);
            constraintProvenance[firstName] =
                ($"{projection.ProjectionId}:s_first>=o_out-o_in", projection.StationName, projection.FirstStopId);

            GRBLinExpr second = new();
            second.Add(stopExpressions[projection.SecondStopId]);
            AddPrecedenceTerm(second, projection.IncomingPrecedenceKey, -1, endpoints);
            AddPrecedenceTerm(second, projection.OutgoingPrecedenceKey, 1, endpoints);
            string secondName = $"master_overtake_stop_second_{StationPattern.Hash(projection.ProjectionId)[..16]}";
            model.AddConstr(second, GRB.GREATER_EQUAL, 0, secondName);
            constraintProvenance[secondName] =
                ($"{projection.ProjectionId}:s_second>=o_in-o_out", projection.StationName, projection.SecondStopId);
        }

        IReadOnlyDictionary<string, int> baselineAuxiliary = BaselineAuxiliaryState();
        foreach ((string name, GRBVar variable) in maintenance.Concat(quota))
            variable.Set(GRB.DoubleAttr.Obj, baselineAuxiliary.GetValueOrDefault(name) == 1 ? -0.001 : 0.001);
        foreach ((string key, GRBVar variable) in endpoints)
            variable.Set(GRB.DoubleAttr.Obj, _reference.SectionPrecedenceByKey[key] == 1 ? -0.001 : 0.001);

        int appliedConflicts = 0;
        foreach (PatternCombinationConflict conflict in _conflicts.Conflicts)
        {
            if (!conflict.RequiredDependencies.All(item => _dependencies.TryGetValue(item.Key, out string? value) &&
                    string.Equals(value, item.Value, StringComparison.Ordinal))) continue;
            List<(GRBVar Variable, int Value)> variableLiterals = [];
            int fixedMatched = 0;
            bool impossible = false;
            foreach ((string station, string patternId) in conflict.RequiredPatternsByStation)
            {
                if (!patternVars.TryGetValue(patternId, out GRBVar? variable)) { impossible = true; break; }
                variableLiterals.Add((variable, 1));
            }
            if (impossible) continue;
            foreach ((string key, int value) in conflict.RequiredDiscreteLiterals)
            {
                if (TryResolveLiteral(key, endpoints, maintenance, quota, out GRBVar? variable, out int? fixedValue))
                    variableLiterals.Add((variable!, value));
                else if (fixedValue == value) fixedMatched++;
                else { impossible = true; break; }
            }
            if (impossible) continue;
            GRBLinExpr matches = new();
            foreach ((GRBVar variable, int value) in variableLiterals)
            {
                if (value == 1) matches.AddTerm(1, variable);
                else { matches.AddConstant(1); matches.AddTerm(-1, variable); }
            }
            string conflictName = $"pattern_conflict_{conflict.ConflictId[..12]}";
            model.AddConstr(matches, GRB.LESS_EQUAL, variableLiterals.Count - 1, conflictName);
            string? conflictStation = conflict.RequiredPatternsByStation.Keys
                .Order(StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            constraintProvenance[conflictName] =
                ($"proven_full_line_conflict:{conflict.ProofIdentity}", conflictStation, null);
            appliedConflicts++;
        }

        model.ModelSense = GRB.MINIMIZE;
        model.Set(GRB.IntParam.Seed, 0);
        model.Set(GRB.IntParam.Threads, 0);
        model.Set(GRB.DoubleParam.TimeLimit, timeLimitSeconds);
        model.Update();
        // Do not invoke a second, statistics-only Presolve before Optimize.  Gurobi performs
        // the real presolve in Optimize; repeating it here rebuilt a large presolved model on
        // every pattern-master iteration without changing any mathematical decision.
        (int pv, int pb, int pc) = (-1, -1, -1);
        MicroPatternMasterAudit audit = new(patternVars.Count, endpoints.Count, maintenance.Count, quota.Count,
            model.Get(GRB.IntAttr.NumConstrs), pv, pb, pc,
            _metadata.OvertakingStopProjections.Length, emptyTimeCategories, incompatibleTimeCategoryPairs);
        Stopwatch clock = Stopwatch.StartNew();
        model.Optimize();
        clock.Stop();
        int status = model.Get(GRB.IntAttr.Status);
        if (model.Get(GRB.IntAttr.SolCount) == 0)
        {
            MicroPatternMasterInfeasibilityProof? proof = status == GRB.Status.INFEASIBLE
                ? ExtractMasterProof(model, constraintProvenance, variableProvenance)
                : null;
            return new(false, status, StatusName(status), clock.Elapsed.TotalSeconds,
                new Dictionary<string, StationPattern>(), null, audit,
                status == GRB.Status.INFEASIBLE ? "pattern_master_infeasible_generate_alternative" :
                $"pattern_master_no_selection_status_{status}", proof);
        }

        Dictionary<string, StationPattern> selected = _pool.Patterns
            .Where(pattern => patternVars[pattern.PatternId].Get(GRB.DoubleAttr.X) >= 0.5)
            .ToDictionary(pattern => pattern.StationName, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> stops = new(StringComparer.OrdinalIgnoreCase);
        foreach (StationPattern pattern in selected.Values)
        foreach (VerifiedMicroAssignmentWitness witness in pattern.Certificate.Witnesses)
            stops[witness.RequirementId] = witness.IsStop ? 1 : 0;
        Dictionary<string, int> section = new(_reference.SectionPrecedenceByKey, StringComparer.OrdinalIgnoreCase);
        foreach ((string key, GRBVar variable) in endpoints) section[key] = variable.Get(GRB.DoubleAttr.X) >= 0.5 ? 1 : 0;
        CompleteMacroDiscreteState discrete = new(
            stops, section,
            new Dictionary<string, int>(_reference.StationEventPrecedenceByKey, StringComparer.OrdinalIgnoreCase),
            maintenance.ToDictionary(item => item.Key, item => item.Value.Get(GRB.DoubleAttr.X) >= 0.5 ? 1 : 0,
                StringComparer.Ordinal),
            quota.ToDictionary(item => item.Key, item => item.Value.Get(GRB.DoubleAttr.X) >= 0.5 ? 1 : 0,
                StringComparer.Ordinal),
            _trustRegion.MaxTotalChanges, _trustRegion.MaxNonHotspotChanges,
            SnapshotFingerprint.Calculate(_reference));
        return new(true, status, StatusName(status), clock.Elapsed.TotalSeconds,
            selected, discrete, audit, $"pattern_master_selection;applied_conflicts={appliedConflicts}");
    }

    private IReadOnlyDictionary<string, int> BaselineAuxiliaryState()
    {
        Dictionary<string, int> result = new(StringComparer.Ordinal);
        foreach (MaintenanceMetadata item in _metadata.Maintenance)
            result[item.VariableName] = MacroDiscreteStateResolver.ResolveMaintenanceBeforeValue(
                _reference.ArrivalSecondsByStopId[item.SectionEndStopId],
                _reference.DepartureSecondsByStopId[item.SectionStartStopId], item.Start, item.End);
        Dictionary<string, TrainStopRecord> stops = _instance.TrainStops.ToDictionary(
            stop => stop.StopId, StringComparer.OrdinalIgnoreCase);
        foreach (QuotaMetadata item in _metadata.Quota)
        {
            TrainStopRecord stop = stops[item.StopId];
            MacroQuotaCategory category = MacroDiscreteStateResolver.ResolveQuotaCategory(
                _reference.StopByStopId[item.StopId] == 1,
                stop.StopRole == TrainStopRole.Origin ? _reference.DepartureSecondsByStopId[item.StopId] :
                    _reference.ArrivalSecondsByStopId[item.StopId], item.Start, item.End);
            result[item.InWindowName] = category == MacroQuotaCategory.InWindow ? 1 : 0;
            result[item.LeftName] = category == MacroQuotaCategory.LeftOutside ? 1 : 0;
            result[item.RightName] = category == MacroQuotaCategory.RightOutside ? 1 : 0;
        }
        return result;
    }

    private static MicroPatternMasterInfeasibilityProof ExtractMasterProof(
        GRBModel model,
        IReadOnlyDictionary<string, (string Source, string? Station, string? Stop)> constraintProvenance,
        IReadOnlyDictionary<string, (string Source, string? Station, string? Stop)> variableProvenance)
    {
        try
        {
            model.ComputeIIS();
            MicroPatternMasterProofRow[] rows = model.GetConstrs()
                .Where(constraint => constraint.Get(GRB.IntAttr.IISConstr) == 1)
                .Select(constraint =>
                {
                    string name = constraint.Get(GRB.StringAttr.ConstrName);
                    (string source, string? station, string? stop) = constraintProvenance
                        .GetValueOrDefault(name, (name, null, null));
                    return new MicroPatternMasterProofRow(name, source, station, stop);
                }).ToArray();
            MicroPatternMasterProofBound[] bounds = model.GetVars().Select(variable =>
                {
                    bool lower = variable.Get(GRB.IntAttr.IISLB) == 1;
                    bool upper = variable.Get(GRB.IntAttr.IISUB) == 1;
                    if (!lower && !upper) return null;
                    string name = variable.Get(GRB.StringAttr.VarName);
                    (string source, string? station, string? stop) = variableProvenance
                        .GetValueOrDefault(name, (name, null, null));
                    return new MicroPatternMasterProofBound(name, lower, upper,
                        variable.Get(GRB.DoubleAttr.LB), variable.Get(GRB.DoubleAttr.UB),
                        source, station, stop);
                }).Where(item => item is not null).Select(item => item!).ToArray();
            string[] stations = rows.Select(row => row.StationName)
                .Concat(bounds.Select(bound => bound.StationName))
                .Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!)
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            string[] stops = rows.Select(row => row.StopId)
                .Concat(bounds.Select(bound => bound.StopId))
                .Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!)
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            string identity = StationPattern.Hash(string.Join('\n',
                rows.Select(row => $"row:{row.ConstraintName}:{row.BusinessSource}")
                    .Concat(bounds.Select(bound => $"bound:{bound.VariableName}:{bound.LowerBound}:{bound.UpperBound}"))));
            return new(identity, rows, bounds, stations, stops, "iis_available");
        }
        catch (GRBException exception)
        {
            return new(StationPattern.Hash($"iis_unavailable:{exception.ErrorCode}:{exception.Message}"),
                [], [], [], [], $"iis_unavailable:{exception.ErrorCode}:{exception.Message}");
        }
    }

    private void AddPrecedenceTerm(
        GRBLinExpr expression,
        string key,
        double coefficient,
        IReadOnlyDictionary<string, GRBVar> endpoints)
    {
        if (endpoints.TryGetValue(key, out GRBVar? variable))
        {
            expression.AddTerm(coefficient, variable);
            return;
        }
        if (!_reference.SectionPrecedenceByKey.TryGetValue(key, out int value))
            throw new InvalidDataException($"Overtaking projection '{key}' is absent from the frozen full-relation Macro state.");
        expression.AddConstant(coefficient * value);
    }

    private bool TryResolveLiteral(
        string key,
        IReadOnlyDictionary<string, GRBVar> endpoints,
        IReadOnlyDictionary<string, GRBVar> maintenance,
        IReadOnlyDictionary<string, GRBVar> quota,
        out GRBVar? variable,
        out int? fixedValue)
    {
        variable = null;
        fixedValue = null;
        int split = key.IndexOf(':');
        if (split < 0) return false;
        string family = key[..split];
        string id = key[(split + 1)..];
        IReadOnlyDictionary<string, GRBVar>? vars = family switch
        {
            "section" => endpoints,
            "maintenance" => maintenance,
            "quota" => quota,
            _ => null
        };
        if (vars is not null && vars.TryGetValue(id, out variable)) return true;
        if (family == "section" && _reference.SectionPrecedenceByKey.TryGetValue(id, out int sectionValue))
            fixedValue = sectionValue;
        return false;
    }

    private static AuxiliaryMetadata GetOrDiscoverMetadata(
        TimetablingInstance instance,
        int endpointOccupationSeconds,
        double runningTimeSupplementRatio)
    {
        Dictionary<string, AuxiliaryMetadata> cache = MetadataCache.GetValue(
            instance, _ => new Dictionary<string, AuxiliaryMetadata>(StringComparer.Ordinal));
        string key = $"endpoint={endpointOccupationSeconds};ratio={runningTimeSupplementRatio.ToString("R", CultureInfo.InvariantCulture)}";
        lock (cache)
        {
            if (!cache.TryGetValue(key, out AuxiliaryMetadata? metadata))
            {
                metadata = DiscoverMetadata(instance, endpointOccupationSeconds, runningTimeSupplementRatio);
                cache.Add(key, metadata);
            }
            return metadata;
        }
    }

    private static AuxiliaryMetadata DiscoverMetadata(
        TimetablingInstance instance,
        int endpointOccupationSeconds,
        double runningTimeSupplementRatio)
    {
        using BasicMilpModel model = new BasicMilpModelBuilder(instance, neighborRadius: int.MaxValue,
            runningTimeSupplementRatio: runningTimeSupplementRatio,
            endpointOccupationSeconds: endpointOccupationSeconds).Build();
        return new(
            model.MaintenanceWindowBinaryBindings.Select(item => new MaintenanceMetadata(
                item.BindingId, item.SectionStartStopId, item.SectionEndStopId,
                item.WindowStartSeconds, item.WindowEndSeconds,
                item.BeforeWindowVariable.Get(GRB.StringAttr.VarName))).ToArray(),
            model.StationQuotaCategoryBindings.Select(item => new QuotaMetadata(
                item.BindingId, item.RuleId, item.StopId, item.WindowStartSeconds, item.WindowEndSeconds,
                item.InWindowVariable.Get(GRB.StringAttr.VarName),
                item.LeftOutsideVariable.Get(GRB.StringAttr.VarName),
                item.RightOutsideVariable.Get(GRB.StringAttr.VarName),
                instance.StopsById[item.StopId].StopRole == TrainStopRole.Origin
                    ? instance.StopsById[item.StopId].EarliestDepSeconds
                    : instance.StopsById[item.StopId].EarliestArrSeconds,
                instance.StopsById[item.StopId].StopRole == TrainStopRole.Origin
                    ? instance.StopsById[item.StopId].LatestDepSeconds
                    : instance.StopsById[item.StopId].LatestArrSeconds)).ToArray(),
            OvertakingStopProjectionPlanner.Plan(instance, model.SectionConflictCandidates).ToArray());
    }

    private static string StatusName(int status) => status switch
    {
        GRB.Status.OPTIMAL => "OPTIMAL",
        GRB.Status.INFEASIBLE => "INFEASIBLE",
        GRB.Status.TIME_LIMIT => "TIME_LIMIT",
        GRB.Status.INTERRUPTED => "INTERRUPTED",
        _ => $"STATUS_{status}"
    };
    private static string Sanitize(string value) => new(value.Select(character =>
        char.IsLetterOrDigit(character) ? character : '_').Take(90).ToArray());

    private sealed record AuxiliaryMetadata(
        MaintenanceMetadata[] Maintenance,
        QuotaMetadata[] Quota,
        OvertakingStopProjection[] OvertakingStopProjections);
    private sealed record MaintenanceMetadata(
        string BindingId, string SectionStartStopId, string SectionEndStopId,
        double Start, double End, string VariableName);
    private sealed record QuotaMetadata(
        string BindingId, string RuleId, string StopId, double Start, double End,
        string InWindowName, string LeftName, string RightName,
        double EventLowerBound, double EventUpperBound);
}
