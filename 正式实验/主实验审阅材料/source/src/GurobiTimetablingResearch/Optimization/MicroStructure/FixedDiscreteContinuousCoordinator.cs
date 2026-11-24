using System.Diagnostics;
using Gurobi;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

internal sealed record FixedDiscreteCoordinatorAudit(
    int SourceVariableCount,
    int SourceBinaryCount,
    int SourceConstraintCount,
    int ExactFixedDiscreteCount,
    int ContinuousVariableCount,
    int RemainingIntegerCount,
    int SosCount,
    int GeneralConstraintCount,
    bool PresolveSizeAvailable,
    string PresolveStopReason,
    int PresolvedVariableCount,
    int PresolvedIntegerCount,
    int PresolvedConstraintCount,
    int ExplicitBoundRowCount,
    int PatternConstraintCount,
    IReadOnlyDictionary<string, int> FixedCountByFamily,
    bool CompactCompilerUsed = false,
    int CompactFixedVariableCount = 0,
    int CompactVariableCount = 0,
    int CompactConstraintCount = 0,
    int DifferenceGraphNodeCount = 0,
    int DifferenceGraphEdgeCount = 0,
    int ResidualLpConstraintCount = 0);

internal sealed record ContinuousProofRow(
    string ConstraintName,
    double FarkasDual,
    string EvidenceKind,
    string BusinessKind,
    string BusinessId,
    char? Sense = null,
    double? RightHandSide = null,
    IReadOnlyDictionary<string, double>? Coefficients = null,
    IReadOnlyDictionary<string, double>? FixedValues = null,
    IReadOnlyDictionary<string, int>? RequiredDiscreteLiterals = null,
    IReadOnlyDictionary<string, string>? RequiredPatternsByStation = null,
    string? ProofIdentity = null,
    string? ProofKind = null,
    double? SubstitutedRightHandSide = null,
    double? Violation = null);

internal static class ContinuousInfeasibilityEvidenceExtractor
{
    internal static IReadOnlyList<ContinuousProofRow> SelectIisRows(
        IReadOnlyList<ContinuousProofRow> constraintRows,
        IReadOnlyList<ContinuousProofRow> boundRows,
        int maxConstraintRows = 200,
        int maxBoundRows = 200)
    {
        if (maxConstraintRows < 0) throw new ArgumentOutOfRangeException(nameof(maxConstraintRows));
        if (maxBoundRows < 0) throw new ArgumentOutOfRangeException(nameof(maxBoundRows));
        return constraintRows.Take(maxConstraintRows).Concat(boundRows.Take(maxBoundRows)).ToArray();
    }

    internal static IReadOnlyList<ContinuousProofRow> Extract(GRBModel model)
    {
        List<ContinuousProofRow> farkas = [];
        foreach (GRBConstr constraint in model.GetConstrs())
        {
            double dual;
            try { dual = constraint.Get(GRB.DoubleAttr.FarkasDual); }
            catch (GRBException) { continue; }
            if (Math.Abs(dual) <= 1e-8) continue;
            string name = constraint.Get(GRB.StringAttr.ConstrName);
            (string kind, string id) = ClassifyConstraint(name);
            farkas.Add(ReadConstraintRow(model, constraint, dual, "farkas_dual", kind, id));
        }
        if (farkas.Count > 0)
            return farkas.OrderByDescending(row => Math.Abs(row.FarkasDual)).Take(200).ToArray();

        try
        {
            model.ComputeIIS();
            List<ContinuousProofRow> bounds = [];
            foreach (GRBVar variable in model.GetVars())
            {
                string name = variable.Get(GRB.StringAttr.VarName);
                if (variable.Get(GRB.IntAttr.IISLB) == 1)
                {
                    bounds.Add(new ContinuousProofRow(
                        $"iis_lb_{name}", 0.0, "iis_lower_bound", "variable_bound", name,
                        GRB.GREATER_EQUAL, variable.Get(GRB.DoubleAttr.LB),
                        new Dictionary<string, double>(StringComparer.Ordinal) { [name] = 1.0 }));
                }
                if (variable.Get(GRB.IntAttr.IISUB) == 1)
                {
                    bounds.Add(new ContinuousProofRow(
                        $"iis_ub_{name}", 0.0, "iis_upper_bound", "variable_bound", name,
                        GRB.LESS_EQUAL, variable.Get(GRB.DoubleAttr.UB),
                        new Dictionary<string, double>(StringComparer.Ordinal) { [name] = 1.0 }));
                }
            }
            ContinuousProofRow[] constraints = model.GetConstrs()
                .Where(constraint => constraint.Get(GRB.IntAttr.IISConstr) == 1)
                .Select(constraint =>
                {
                    string name = constraint.Get(GRB.StringAttr.ConstrName);
                    (string kind, string id) = ClassifyConstraint(name);
                    return ReadConstraintRow(model, constraint, 0.0, "iis_membership", kind, id);
                })
                .ToArray();
            return SelectIisRows(constraints, bounds);
        }
        catch (GRBException)
        {
            return [];
        }
    }

    private static ContinuousProofRow ReadConstraintRow(
        GRBModel model,
        GRBConstr constraint,
        double multiplier,
        string evidenceKind,
        string businessKind,
        string businessId)
    {
        GRBLinExpr expression = model.GetRow(constraint);
        Dictionary<string, double> coefficients = new(StringComparer.Ordinal);
        for (int index = 0; index < expression.Size; index++)
        {
            string variableName = expression.GetVar(index).Get(GRB.StringAttr.VarName);
            coefficients[variableName] = coefficients.GetValueOrDefault(variableName) +
                                         expression.GetCoeff(index);
        }
        return new ContinuousProofRow(
            constraint.Get(GRB.StringAttr.ConstrName),
            multiplier,
            evidenceKind,
            businessKind,
            businessId,
            constraint.Get(GRB.CharAttr.Sense),
            constraint.Get(GRB.DoubleAttr.RHS),
            coefficients);
    }

    private static (string Kind, string Id) ClassifyConstraint(string name)
    {
        if (name.StartsWith("micro_cert_", StringComparison.Ordinal)) return ("micro_pattern", name);
        if (name.StartsWith("fd_bound_", StringComparison.Ordinal)) return ("variable_bound", name);
        if (name.StartsWith("fd_fix_", StringComparison.Ordinal)) return ("macro_fixed_decision", name);
        if (name.StartsWith("travel_time_", StringComparison.Ordinal)) return ("travel_time", name);
        if (name.StartsWith("section_stop_", StringComparison.Ordinal)) return ("section_stop", name);
        if (name.StartsWith("station_quota_", StringComparison.Ordinal)) return ("station_quota", name);
        if (name.StartsWith("run_", StringComparison.Ordinal)) return ("runtime", name);
        if (name.StartsWith("dwell_", StringComparison.Ordinal)) return ("dwell", name);
        if (name.StartsWith("headway_", StringComparison.Ordinal) ||
            name.StartsWith("arrival_headway_", StringComparison.Ordinal) ||
            name.StartsWith("station_headway_", StringComparison.Ordinal)) return ("macro_headway", name);
        return ("other_business_constraint", name);
    }
}

internal sealed record ContinuousCoordinationResult(
    int StatusCode,
    string Status,
    bool HasSolution,
    double RuntimeSeconds,
    MasterSolutionSnapshot? Snapshot,
    bool MacroValidated,
    int MacroViolationCount,
    FixedDiscreteCoordinatorAudit Audit,
    ContinuousCoordinationProofDisposition ProofDisposition,
    IReadOnlyList<ContinuousProofRow> ProofRows,
    ContinuousCoordinationEvidenceBinding EvidenceBinding,
    string StopReason,
    RetimingSourceFeasibilityCheck? SourceFeasibility = null,
    IReadOnlyList<RetimingObjectiveStageResult>? RetimingStages = null,
    bool AllRetimingObjectivesOptimal = false);

internal sealed record RetimingObjectiveStageResult(
    string ObjectiveName,int? StatusCode,string Status,bool HasSolution,bool MacroValidated,
    double RuntimeSeconds,double? ObjectiveValue);

internal sealed record ContinuousExtendabilityCheckResult(
    ContinuousCoordinationResult Coordination,
    FarkasBoundaryGenerationResult Boundary,
    int FixedInterfaceValueCount);

internal sealed record CompleteMacroDiscreteState(
    IReadOnlyDictionary<string, int> StopByStopId,
    IReadOnlyDictionary<string, int> SectionPrecedenceByKey,
    IReadOnlyDictionary<string, int> StationEventPrecedenceByKey,
    IReadOnlyDictionary<string, int> MaintenanceByVariableName,
    IReadOnlyDictionary<string, int> QuotaCategoryByVariableName,
    int EndpointChangeBudget,
    int NonHotspotChangeBudget,
    string ReferenceFingerprint)
{
    internal IReadOnlyDictionary<string, int> AllAuxiliaryBinaryValues =>
        MaintenanceByVariableName.Concat(QuotaCategoryByVariableName)
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
}

internal sealed class FixedDiscreteContinuousCoordinator : IDisposable
{
    private readonly BasicMilpModel _model;
    private readonly FixedDiscreteCoordinatorAudit _audit;
    private readonly MasterSolutionSnapshot _reference;
    private readonly IReadOnlyList<MicroWitnessCertificate> _patterns;
    private readonly string _coordinationModelId;
    private readonly string _modelConfigurationIdentity;
    private readonly CompleteMacroDiscreteState _discreteState;
    private readonly CompactContinuousTimeModel _compact;

    private FixedDiscreteContinuousCoordinator(
        BasicMilpModel model,
        FixedDiscreteCoordinatorAudit audit,
        MasterSolutionSnapshot reference,
        IReadOnlyList<MicroWitnessCertificate> patterns,
        CompleteMacroDiscreteState discreteState,
        CompactContinuousTimeModel compact,
        string coordinationModelId,
        string modelConfigurationIdentity)
    {
        _model = model;
        _audit = audit;
        _reference = reference;
        _patterns = patterns;
        _discreteState = discreteState;
        _compact = compact;
        _coordinationModelId = coordinationModelId;
        _modelConfigurationIdentity = modelConfigurationIdentity;
    }

    internal BasicMilpModel Model => _model;
    internal FixedDiscreteCoordinatorAudit Audit => _audit;
    internal string DiscreteBranchKey => string.Join(";",_discreteState.AllAuxiliaryBinaryValues
        .OrderBy(p=>p.Key,StringComparer.Ordinal).Select(p=>p.Key+"="+p.Value));

    internal static FixedDiscreteContinuousCoordinator Build(
        TimetablingInstance instance,
        MasterSolutionSnapshot snapshot,
        IReadOnlyList<MicroWitnessCertificate>? patterns = null,
        IReadOnlyList<StationEventConflictCandidate>? stationEventEdges = null,
        double runningTimeSupplementRatio = 0.05,
        int endpointOccupationSeconds = StationEndpointOccupationPolicy.DefaultSeconds,
        string? coordinationModelId = null,
        bool feasibilityOnly = false,
        CompleteMacroDiscreteState? discreteState = null,
        PrecedenceTrustRegion? precedenceTrustRegion = null,
        ServiceWindowPolicy? serviceWindows = null,
        bool spanFirstRetiming = false,
        Func<double>? remaining = null)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(snapshot);
        void CheckBudget()
        {
            if(remaining is not null && remaining()<=0)
                throw new TimeoutException("Retiming construction budget expired at a safe build boundary.");
        }
        CheckBudget();
        IReadOnlyList<MicroWitnessCertificate> activePatterns = patterns ?? [];
        BasicMilpModel model = new BasicMilpModelBuilder(
            instance,
            neighborRadius: int.MaxValue,
            stationEventEdges: stationEventEdges,
            runningTimeSupplementRatio: runningTimeSupplementRatio,
            endpointOccupationSeconds: endpointOccupationSeconds,
            precedenceTrustRegion: precedenceTrustRegion,
            serviceWindows: serviceWindows,
            screenTemporalPrecedence: spanFirstRetiming).Build();
        CompactContinuousTimeModel? compact=null;
        try
        {
            CheckBudget();
            CompleteMacroDiscreteState activeState = discreteState ??
                CreateSourceClassifiedState(model, snapshot);
            ValidateExactCoverage(model.ArrivalVars.Keys, snapshot.ArrivalSecondsByStopId.Keys, "arrival");
            ValidateExactCoverage(model.DepartureVars.Keys, snapshot.DepartureSecondsByStopId.Keys, "departure");
            ValidateExactCoverage(model.StopVars.Keys, activeState.StopByStopId.Keys, "stop");
            ValidateExactCoverage(model.SectionPrecedenceVars.Keys, activeState.SectionPrecedenceByKey.Keys, "section precedence");
            ValidateExactCoverage(model.StationEventPrecedenceVars.Keys, activeState.StationEventPrecedenceByKey.Keys, "station-event precedence");

            int sourceVariables = model.Model.Get(GRB.IntAttr.NumVars);
            int sourceBinaries = model.Model.Get(GRB.IntAttr.NumBinVars);
            int sourceConstraints = model.Model.Get(GRB.IntAttr.NumConstrs);
            Dictionary<string, (GRBVar Variable, int Value, string Family)> exact =
                new(StringComparer.Ordinal);
            AddSnapshotValues(exact, model.StopVars, activeState.StopByStopId, "stop");
            AddSnapshotValues(exact, model.SectionPrecedenceVars, activeState.SectionPrecedenceByKey, "section_precedence");
            AddSnapshotValues(exact, model.StationEventPrecedenceVars, activeState.StationEventPrecedenceByKey, "station_event_precedence");
            AddExplicitAuxiliaryValues(exact, model.MaintenanceWindowBinaryBindings
                    .Select(binding => binding.BeforeWindowVariable), activeState.MaintenanceByVariableName,
                "maintenance_window");
            AddExplicitAuxiliaryValues(exact, model.StationQuotaCategoryBindings.SelectMany(binding =>
                    new[] { binding.InWindowVariable, binding.LeftOutsideVariable, binding.RightOutsideVariable }),
                activeState.QuotaCategoryByVariableName, "quota_category");

            string[] sourceBinaryNames = model.Model.GetVars()
                .Where(variable => variable.Get(GRB.CharAttr.VType) == GRB.BINARY)
                .Select(variable => variable.Get(GRB.StringAttr.VarName))
                .Order(StringComparer.Ordinal)
                .ToArray();
            string[] missing = sourceBinaryNames.Except(exact.Keys, StringComparer.Ordinal).Take(20).ToArray();
            // Original-domain screening retains forced orders as fixed continuous columns.
            // They remain part of the exact logical state, even though they are no longer binaries.
            string[] extra = exact.Where(item=>item.Value.Variable.VType!=GRB.BINARY &&
                    !(item.Value.Variable.VType==GRB.CONTINUOUS &&
                      item.Value.Variable.LB==item.Value.Variable.UB && item.Value.Variable.LB==item.Value.Value))
                .Select(item=>item.Key).Take(20).ToArray();
            if (missing.Length > 0 || extra.Length > 0)
                throw new InvalidDataException(
                    $"Exact discrete-state coverage mismatch: source={sourceBinaries}, fixed={exact.Count}, " +
                    $"missing=[{string.Join('|', missing)}], extra=[{string.Join('|', extra)}].");

            foreach ((string variableName, (GRBVar variable, int value, string family)) in exact)
            {
                model.Model.AddConstr(variable, GRB.EQUAL, value, $"fd_fix_{Sanitize(family)}_{Sanitize(variableName)}");
                variable.Set(GRB.CharAttr.VType, GRB.CONTINUOUS);
            }

            CheckBudget();

            int patternConstraints = activePatterns.Count == 0
                ? 0
                : MicroCertificateModelBinder.Apply(model, activePatterns).AddedConstraintIds.Count;
            if (feasibilityOnly)
            {
                model.Model.Set(GRB.IntAttr.NumObj, 0);
                model.Model.SetObjective(new GRBLinExpr(), GRB.MINIMIZE);
            }
            else if (!spanFirstRetiming)
            {
                AddReferenceRetimingObjective(model, snapshot);
            }
            int explicitBoundRows = MaterializeVariableBounds(model.Model);
            MasterSolutionSnapshotApplier.ApplyMipStart(model, snapshot);
            model.Model.Update();

            CheckBudget();

            Dictionary<string, string> patternStationsByConstraint = new(StringComparer.Ordinal);
            foreach (MicroWitnessCertificate pattern in activePatterns)
            for (int conditionIndex = 0; conditionIndex < pattern.Conditions.Count; conditionIndex++)
                patternStationsByConstraint[
                    MicroCertificateModelBinder.BuildConstraintName(pattern.CertificateId, conditionIndex)] =
                    pattern.StationName;
            Dictionary<string, string> sectionKeyByVariableName = model.SectionPrecedenceVars.ToDictionary(
                item => item.Value.Get(GRB.StringAttr.VarName), item => item.Key, StringComparer.Ordinal);
            Dictionary<string, string> stopIdByVariableName = model.StopVars.ToDictionary(
                item => item.Value.Get(GRB.StringAttr.VarName), item => item.Key, StringComparer.Ordinal);
            Dictionary<string, MicroWitnessCertificate> patternByStation = activePatterns.ToDictionary(
                item => item.StationName, StringComparer.OrdinalIgnoreCase);
            Dictionary<string, CompactFixedDecisionSource> fixedDecisionSources = new(StringComparer.Ordinal);
            foreach ((string variableName, (_, int value, string family)) in exact)
            {
                string? literalKey = family switch
                {
                    "section_precedence" when sectionKeyByVariableName.TryGetValue(variableName, out string? key) =>
                        $"section:{key}",
                    "maintenance_window" => $"maintenance:{variableName}",
                    "quota_category" => $"quota:{variableName}",
                    _ => null
                };
                string? patternStation = null;
                string? patternId = null;
                if (family == "stop" && stopIdByVariableName.TryGetValue(variableName, out string? stopId))
                {
                    patternStation = instance.StopsById[stopId].StationName;
                    if (patternByStation.TryGetValue(patternStation, out MicroWitnessCertificate? pattern))
                        patternId = StationPattern.Create(pattern).PatternId;
                }
                fixedDecisionSources[variableName] = new(literalKey, literalKey is null ? null : value,
                    patternStation, patternId);
            }
            CheckBudget();
            compact = CompactContinuousTimeCompiler.Compile(
                model.Model, patternStationsByConstraint, fixedDecisionSources);
            CheckBudget();

            int remainingIntegers = model.Model.GetVars().Count(variable =>
                variable.Get(GRB.CharAttr.VType) is GRB.BINARY or GRB.INTEGER or GRB.SEMIINT);
            int sos = model.Model.Get(GRB.IntAttr.NumSOS);
            int general = model.Model.Get(GRB.IntAttr.NumGenConstrs);
            if (remainingIntegers != 0 || sos != 0 || general != 0)
                throw new InvalidDataException(
                    $"Fixed-discrete coordinator is not a pure continuous model: integers={remainingIntegers}, sos={sos}, general={general}.");

            (bool presolveAvailable, string presolveStopReason, int presolvedVariables,
                int presolvedIntegers, int presolvedConstraints) =
                (false, "skipped_redundant_stats_presolve_compact_compile_is_authoritative", -1, -1, -1);
            FixedDiscreteCoordinatorAudit audit = new(
                sourceVariables,
                sourceBinaries,
                sourceConstraints,
                exact.Count,
                model.Model.Get(GRB.IntAttr.NumVars),
                remainingIntegers,
                sos,
                general,
                presolveAvailable,
                presolveStopReason,
                presolvedVariables,
                presolvedIntegers,
                presolvedConstraints,
                explicitBoundRows,
                patternConstraints,
                exact.Values.GroupBy(item => item.Family, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
                CompactCompilerUsed: true,
                compact.Audit.FixedVariableCount,
                compact.Audit.CompactVariableCount,
                compact.Audit.CompactConstraintCount,
                compact.Audit.DifferenceNodeCount,
                compact.Audit.DifferenceEdgeCount,
                compact.Audit.ResidualConstraintCount);
            string resolvedModelId = string.IsNullOrWhiteSpace(coordinationModelId)
                ? "fixed_discrete_continuous_coordinator"
                : coordinationModelId;
            string configurationIdentity = string.Join('|',
                $"source_variables={audit.SourceVariableCount}",
                $"source_binaries={audit.SourceBinaryCount}",
                $"source_constraints={audit.SourceConstraintCount}",
                $"continuous_variables={audit.ContinuousVariableCount}",
                $"compact_variables={audit.CompactVariableCount}",
                $"difference_edges={audit.DifferenceGraphEdgeCount}",
                $"residual_rows={audit.ResidualLpConstraintCount}",
                $"pattern_constraints={audit.PatternConstraintCount}",
                $"running_ratio={runningTimeSupplementRatio:R}",
                $"endpoint_seconds={endpointOccupationSeconds}",
                $"feasibility_only={feasibilityOnly}",
                "neighbor_radius=full");
            return new(model, audit, snapshot, activePatterns.ToArray(), activeState, compact,
                resolvedModelId, configurationIdentity);
        }
        catch
        {
            compact?.Dispose();
            model.Dispose();
            throw;
        }
    }

    internal ContinuousCoordinationResult Solve(
        double timeLimitSeconds,
        string? logFile = null,
        string? solveAttemptId = null)
    {
        if (timeLimitSeconds <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(timeLimitSeconds));
        if (!string.IsNullOrWhiteSpace(logFile))
        {
            string? directory = Path.GetDirectoryName(Path.GetFullPath(logFile));
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            _model.Model.Set(GRB.StringParam.LogFile, logFile);
        }
        _model.Model.Set(GRB.DoubleParam.TimeLimit, timeLimitSeconds);
        _model.Model.Set(GRB.IntParam.Seed, 0);
        _model.Model.Set(GRB.IntParam.Threads, 0);
        _model.Model.Set(GRB.IntParam.DualReductions, 0);
        _model.Model.Set(GRB.IntParam.InfUnbdInfo, 1);
        Stopwatch clock = Stopwatch.StartNew();
        CompactContinuousSolveResult compact = _compact.Solve(timeLimitSeconds, logFile);
        int status = compact.StatusCode;
        bool hasSolution = false;
        MasterSolutionSnapshot? solution = null;
        bool macroValidated = false;
        int macroViolations = 0;
        IReadOnlyList<ContinuousProofRow> proofRows = compact.ProofRows;
        if (compact.HasSolution)
        {
            foreach ((string stopId, GRBVar variable) in _model.ArrivalVars)
                _model.Model.AddConstr(variable, GRB.EQUAL, RequireValue(compact, variable),
                    $"compact_replay_arr_{Sanitize(stopId)}");
            foreach ((string stopId, GRBVar variable) in _model.DepartureVars)
                _model.Model.AddConstr(variable, GRB.EQUAL, RequireValue(compact, variable),
                    $"compact_replay_dep_{Sanitize(stopId)}");
            _model.Model.Update();
            _model.Model.Set(GRB.DoubleParam.TimeLimit, Math.Max(0.001, timeLimitSeconds - compact.RuntimeSeconds));
            if (!string.IsNullOrWhiteSpace(logFile))
                _model.Model.Set(GRB.StringParam.LogFile,
                    Path.Combine(Path.GetDirectoryName(Path.GetFullPath(logFile))!,
                        $"{Path.GetFileNameWithoutExtension(logFile)}_source_replay.log"));
            _model.Optimize();
            status = _model.Model.Get(GRB.IntAttr.Status);
            hasSolution = _model.Model.Get(GRB.IntAttr.SolCount) > 0;
            proofRows = status == GRB.Status.INFEASIBLE
                ? ContinuousInfeasibilityEvidenceExtractor.Extract(_model.Model)
                : [];
        }
        if (hasSolution)
        {
            solution = MasterSolutionSnapshot.Capture(_model) with
            {
                StopByStopId = new Dictionary<string, int>(_discreteState.StopByStopId, StringComparer.OrdinalIgnoreCase),
                SectionPrecedenceByKey = new Dictionary<string, int>(_discreteState.SectionPrecedenceByKey, StringComparer.OrdinalIgnoreCase),
                StationEventPrecedenceByKey = new Dictionary<string, int>(_discreteState.StationEventPrecedenceByKey, StringComparer.OrdinalIgnoreCase),
                VerifiedMicroAssignments = null
            };
            string solutionFingerprint = SnapshotFingerprint.Calculate(solution);
            solution = solution with
            {
                VerifiedMicroAssignments = _patterns.SelectMany(pattern => pattern.Witnesses)
                    .Select(witness => witness with
                    {
                        CandidateSnapshotFingerprint = solutionFingerprint,
                        ArrivalSeconds = solution.ArrivalSecondsByStopId[witness.RequirementId],
                        DepartureSeconds = solution.DepartureSecondsByStopId[witness.RequirementId]
                    }).ToArray()
            };
            MasterValidationReport validation = new MasterSolutionValidator().Validate(_model);
            macroValidated = validation.IsFeasible;
            macroViolations = validation.Violations.Count;
        }
        ContinuousCoordinationProofDisposition disposition = status == GRB.Status.INFEASIBLE && proofRows.Count > 0
            ? ContinuousCoordinationProofDisposition.HardProof
            : ContinuousCoordinationProofDisposition.NeighborhoodHintOnly;
        string resolvedSolveAttemptId = string.IsNullOrWhiteSpace(solveAttemptId)
            ? Path.GetFileNameWithoutExtension(logFile) ?? "solve"
            : solveAttemptId;
        ContinuousCoordinationEvidenceBinding evidence = ContinuousCoordinationEvidenceBinding.Create(
            _coordinationModelId,
            SnapshotFingerprint.Calculate(_reference),
            _patterns,
            status,
            proofRows,
            resolvedSolveAttemptId,
            _modelConfigurationIdentity);
        clock.Stop();
        string stopReason = hasSolution
            ? "compact_continuous_feasible_and_source_replay_verified"
            : status == GRB.Status.INFEASIBLE
                ? compact.StopReason
                : $"compact_continuous_no_solution_status_{status}";
        return new(status, StatusName(status), hasSolution, clock.Elapsed.TotalSeconds,
            solution, macroValidated, macroViolations, _audit, disposition, proofRows, evidence, stopReason);
    }

    internal ContinuousExtendabilityContext CreateExtendabilityContext(
        IReadOnlyList<string> activeStations,
        MasterSolutionSnapshot interfaceSnapshot)
    {
        ArgumentNullException.ThrowIfNull(activeStations);
        ArgumentNullException.ThrowIfNull(interfaceSnapshot);
        string[] stations = activeStations.Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (stations.Length is < 1 or > 3)
            throw new ArgumentException("An extendability context must activate one to three stations.", nameof(activeStations));
        HashSet<string> active = stations.ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] activeStopIds = _model.Instance.TrainStops.Where(stop => active.Contains(stop.StationName))
            .Select(stop => stop.StopId).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        Dictionary<string, int> activeStopState = activeStopIds.ToDictionary(
            stopId => stopId, stopId => interfaceSnapshot.StopByStopId[stopId],
            StringComparer.OrdinalIgnoreCase);
        string macroDiscreteIdentity = CalculateRetainedMacroDiscreteIdentity(
            _model.Instance, _discreteState, stations);
        string outsidePatternIdentity = MicroPatternCombinationIdentity.Calculate(_patterns.Where(pattern =>
            !active.Contains(pattern.StationName)).ToArray());
        string mathematicalIdentity = ContinuousExtendabilityContext.Hash(string.Join('\n',
            "compact-difference-projection-v1", _modelConfigurationIdentity, macroDiscreteIdentity,
            outsidePatternIdentity, string.Join('|', stations)));
        return ContinuousExtendabilityContext.Create(
            mathematicalIdentity, macroDiscreteIdentity, outsidePatternIdentity,
            stations, activeStopState);
    }

    internal IReadOnlyList<ContinuousInterfaceFixBinding> FixActiveInterface(
        IReadOnlyList<string> activeStations,
        MasterSolutionSnapshot interfaceSnapshot)
    {
        ArgumentNullException.ThrowIfNull(activeStations);
        ArgumentNullException.ThrowIfNull(interfaceSnapshot);
        HashSet<string> active = activeStations.ToHashSet(StringComparer.OrdinalIgnoreCase);
        TrainStopRecord[] stops = _model.Instance.TrainStops.Where(stop => active.Contains(stop.StationName))
            .OrderBy(stop => stop.TrainIndex).ThenBy(stop => stop.StationOrder).ToArray();
        if (stops.Length == 0)
            throw new InvalidDataException("The active extendability scope has no train events.");
        List<ContinuousInterfaceFixBinding> result = [];
        foreach (TrainStopRecord stop in stops)
        {
            Add(stop, StationEventSide.Incoming, _model.ArrivalVars[stop.StopId],
                interfaceSnapshot.ArrivalSecondsByStopId[stop.StopId]);
            Add(stop, StationEventSide.Outgoing, _model.DepartureVars[stop.StopId],
                interfaceSnapshot.DepartureSecondsByStopId[stop.StopId]);
        }
        _model.Model.Update();
        return result;

        void Add(TrainStopRecord stop, StationEventSide side, GRBVar variable, double value)
        {
            string token = side == StationEventSide.Incoming ? "arr" : "dep";
            GRBConstr constraint = _model.Model.AddConstr(variable, GRB.EQUAL, value,
                $"extend_fix_{token}_t{stop.TrainIndex}_s{stop.StationOrder}");
            result.Add(new(new ContinuousInterfaceKey(stop.StopId, side), variable, constraint, value));
        }
    }

    /// <summary>Full-time fixed-structure S,P,R recourse. Its optimum is conditional, never a global bound.</summary>
    internal ContinuousCoordinationResult? SolveSpanFirst(double seconds,Func<double> remaining,string? logFile=null)
    {
        if(!double.IsFinite(seconds) || seconds<=0)throw new ArgumentOutOfRangeException(nameof(seconds));
        ArgumentNullException.ThrowIfNull(remaining);
        if(_audit.RemainingIntegerCount!=0 || _audit.SosCount!=0 || _audit.GeneralConstraintCount!=0)
            throw new InvalidDataException("Retiming requires a pure continuous model.");
        var clock=Stopwatch.StartNew();
        var objectives=new Dictionary<string,GRBLinExpr>(StringComparer.Ordinal);
        for(int i=0;i<_model.Model.NumObj;i++)
        {
            _model.Model.Set(GRB.IntParam.ObjNumber,i);
            objectives[_model.Model.ObjNName]=_model.Model.GetObjective(i);
        }
        if(!objectives.ContainsKey("global_span") || !objectives.ContainsKey("running_time_supplement"))
            throw new InvalidDataException("Span-first retiming requires original business objectives.");
        double Left()=>Math.Min(seconds-clock.Elapsed.TotalSeconds,remaining());
        var sourceCheck=RetimingSourceFeasibilityChecker.Check(_model,_reference,
            Math.Min(30,seconds*.25),Left,logFile is null?null:logFile+".source.log");
        var names=new[]{"global_span","service_window_penalty","running_time_supplement"}.Where(objectives.ContainsKey).ToArray();
        List<RetimingObjectiveStageResult> stages=[];
        ContinuousCoordinationResult ResultWithoutCandidate(int status,string reason)=>new(
            status,StatusName(status),false,clock.Elapsed.TotalSeconds,null,false,0,_audit,
            ContinuousCoordinationProofDisposition.NeighborhoodHintOnly,[],
            ContinuousCoordinationEvidenceBinding.Create(_coordinationModelId,SnapshotFingerprint.Calculate(_reference),
                _patterns,status,[],"span_first_source_check",_modelConfigurationIdentity),reason,
            sourceCheck,stages.ToArray(),false);
        if(!sourceCheck.Feasible)
        {
            stages.AddRange(names.Select(name=>new RetimingObjectiveStageResult(name,null,"NotAttemptedSourceCheck",false,false,0,null)));
            return ResultWithoutCandidate(sourceCheck.NativeStatus??GRB.Status.LOADED,sourceCheck.Status);
        }
        _model.Model.Set(GRB.IntAttr.NumObj,0);
        // Flush removal of the multiobjective model before installing the first scalar
        // objective; otherwise Gurobi's pending reset can erase its span coefficients.
        _model.Model.Update();
        ContinuousCoordinationResult? incumbent=null;
        string unattemptedReason="NotAttemptedBudget";
        foreach(string name in names)
        {
            var expression=objectives[name];
            double allowed=Left();
            if(allowed<=.01)break;
            _model.Model.SetObjective(expression,GRB.MINIMIZE);
            var result=SolveSourceContinuous(allowed,logFile,"span_first_"+name,extractEvidence:false);
            stages.Add(new(name,result.StatusCode,result.Status,result.HasSolution,result.MacroValidated,
                result.RuntimeSeconds,result.HasSolution?expression.Value:null));
            if(result.HasSolution && result.MacroValidated && result.Snapshot is not null)
            {
                incumbent=result;
                // Preserve the best attained prior objective even if a later scalar pass times out.
                _model.Model.AddConstr(expression,GRB.LESS_EQUAL,expression.Value,"retiming_cap_"+name);
                _model.Model.Update();
            }
            else { unattemptedReason="NotAttemptedPriorNoValidatedCandidate";break; }
            // Without a proved scalar optimum, spend no remaining budget on lower priorities.
            if(result.StatusCode!=GRB.Status.OPTIMAL){unattemptedReason="NotAttemptedPriorNotOptimal";break;}
        }
        foreach(string name in names.Skip(stages.Count))stages.Add(new(name,null,unattemptedReason,false,false,0,null));
        bool allOptimal=stages.Count==names.Length && stages.All(stage=>stage.StatusCode==GRB.Status.OPTIMAL && stage.HasSolution && stage.MacroValidated);
        return incumbent is null?ResultWithoutCandidate(stages.FirstOrDefault(s=>s.StatusCode.HasValue)?.StatusCode??GRB.Status.LOADED,
            "UnknownRetimingNoObjectiveCandidate"):incumbent with {
                RuntimeSeconds=clock.Elapsed.TotalSeconds,SourceFeasibility=sourceCheck,RetimingStages=stages.ToArray(),
                AllRetimingObjectivesOptimal=allOptimal,
                StopReason=allOptimal?"fixed_structure_all_objectives_optimal_not_global_bound":"fixed_structure_prior_candidate_retained_without_all_objective_optima" };
    }

    internal ContinuousExtendabilityCheckResult SolveExtendability(
        double timeLimitSeconds,
        ContinuousExtendabilityContext context,
        IReadOnlyList<ContinuousInterfaceFixBinding> interfaceFixes,
        string? logFile = null,
        string? solveAttemptId = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(interfaceFixes);
        if (!context.HasValidIdentity())
            throw new InvalidDataException("Extendability context identity is invalid.");
        // Interface fix rows are added after the compact model was compiled.  This legacy
        // point-recourse path must therefore solve the source LP that actually contains those
        // rows; using Solve() here would silently ignore every extend_fix_* row.
        ContinuousCoordinationResult coordination = SolveSourceContinuous(
            timeLimitSeconds, logFile, solveAttemptId);
        FarkasBoundaryGenerationResult boundary = coordination.StatusCode == GRB.Status.INFEASIBLE
            ? FarkasBoundaryFeedbackGenerator.TryGenerate(_model.Model, context, interfaceFixes)
            : new(null, coordination.HasSolution
                    ? "outside_continuous_recourse_feasible"
                    : $"no_cut_for_status_{coordination.StatusCode}",
                0, double.NaN, double.NaN);
        return new(coordination, boundary, interfaceFixes.Count);
    }

    internal static string CalculateRetainedMacroDiscreteIdentity(
        TimetablingInstance instance,
        CompleteMacroDiscreteState state,
        IReadOnlyCollection<string> activeStations)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(activeStations);
        HashSet<string> active = activeStations.ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> activeStopIds = instance.TrainStops.Where(stop => active.Contains(stop.StationName))
            .Select(stop => stop.StopId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return ContinuousExtendabilityContext.Hash(string.Join('\n',
            string.Join('|', state.StopByStopId.Where(item => !activeStopIds.Contains(item.Key))
                .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                .Select(item => $"stop:{item.Key}={item.Value}")),
            string.Join('|', state.SectionPrecedenceByKey
                .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                .Select(item => $"section:{item.Key}={item.Value}")),
            string.Join('|', state.StationEventPrecedenceByKey
                .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                .Select(item => $"station:{item.Key}={item.Value}")),
            string.Join('|', state.MaintenanceByVariableName
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => $"maintenance:{item.Key}={item.Value}")),
            string.Join('|', state.QuotaCategoryByVariableName
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => $"quota:{item.Key}={item.Value}")),
            $"endpoint_budget={state.EndpointChangeBudget}",
            $"non_hotspot_budget={state.NonHotspotChangeBudget}",
            $"reference={state.ReferenceFingerprint}"));
    }

    internal IReadOnlyList<string> CompactNegativeCyclePatternStations => _compact.NegativeCycleEdges
        .Where(edge => !string.IsNullOrWhiteSpace(edge.PatternStation))
        .Select(edge => edge.PatternStation!)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    internal FarkasBoundaryGenerationResult TryGenerateDifferenceFeedback(
        string activeStation,
        MasterSolutionSnapshot rejectedInterface)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(activeStation);
        ArgumentNullException.ThrowIfNull(rejectedInterface);
        ContinuousExtendabilityContext context = CreateExtendabilityContext([activeStation], rejectedInterface);
        HashSet<string> activeStopIds = _model.Instance.TrainStops.Where(stop => string.Equals(
                stop.StationName, activeStation, StringComparison.OrdinalIgnoreCase))
            .Select(stop => stop.StopId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, ContinuousInterfaceKey> events = new(StringComparer.Ordinal);
        Dictionary<ContinuousInterfaceKey, double> rejected = [];
        foreach ((string stopId, GRBVar variable) in _model.ArrivalVars.Where(item => activeStopIds.Contains(item.Key)))
        {
            ContinuousInterfaceKey key = new(stopId, StationEventSide.Incoming);
            events[variable.Get(GRB.StringAttr.VarName)] = key;
            rejected[key] = rejectedInterface.ArrivalSecondsByStopId[stopId];
        }
        foreach ((string stopId, GRBVar variable) in _model.DepartureVars.Where(item => activeStopIds.Contains(item.Key)))
        {
            ContinuousInterfaceKey key = new(stopId, StationEventSide.Outgoing);
            events[variable.Get(GRB.StringAttr.VarName)] = key;
            rejected[key] = rejectedInterface.DepartureSecondsByStopId[stopId];
        }
        return DifferenceCycleFeedbackProjector.TryGenerate(
            _compact, activeStation, context, events, rejected);
    }

    public void Dispose()
    {
        _compact.Dispose();
        _model.Dispose();
    }

    private static double RequireValue(CompactContinuousSolveResult result, GRBVar variable)
    {
        string name = variable.Get(GRB.StringAttr.VarName);
        return result.SourceValues.TryGetValue(name, out double value)
            ? value
            : throw new InvalidDataException($"Compact solution omitted source time variable '{name}'.");
    }

    private ContinuousCoordinationResult SolveSourceContinuous(
        double timeLimitSeconds,
        string? logFile,
        string? solveAttemptId,
        bool extractEvidence = true)
    {
        if (!string.IsNullOrWhiteSpace(logFile))
        {
            string? directory = Path.GetDirectoryName(Path.GetFullPath(logFile));
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            _model.Model.Set(GRB.StringParam.LogFile, logFile);
        }
        _model.Model.Set(GRB.DoubleParam.TimeLimit, timeLimitSeconds);
        _model.Model.Set(GRB.IntParam.Seed, 0);
        _model.Model.Set(GRB.IntParam.Threads, 0);
        _model.Model.Set(GRB.IntParam.DualReductions, 0);
        _model.Model.Set(GRB.IntParam.InfUnbdInfo, 1);
        Stopwatch clock = Stopwatch.StartNew();
        _model.Optimize();
        clock.Stop();
        int status = _model.Model.Get(GRB.IntAttr.Status);
        bool hasSolution = _model.Model.Get(GRB.IntAttr.SolCount) > 0;
        IReadOnlyList<ContinuousProofRow> proofRows = extractEvidence && status == GRB.Status.INFEASIBLE
            ? ContinuousInfeasibilityEvidenceExtractor.Extract(_model.Model)
            : [];
        MasterSolutionSnapshot? solution = null;
        bool macroValidated = false;
        int macroViolations = 0;
        if (hasSolution)
        {
            solution = MasterSolutionSnapshot.Capture(_model) with
            {
                StopByStopId = new Dictionary<string, int>(_discreteState.StopByStopId,
                    StringComparer.OrdinalIgnoreCase),
                SectionPrecedenceByKey = new Dictionary<string, int>(_discreteState.SectionPrecedenceByKey,
                    StringComparer.OrdinalIgnoreCase),
                StationEventPrecedenceByKey = new Dictionary<string, int>(_discreteState.StationEventPrecedenceByKey,
                    StringComparer.OrdinalIgnoreCase),
                VerifiedMicroAssignments = null
            };
            MasterValidationReport validation = new MasterSolutionValidator().Validate(_model);
            macroValidated = validation.IsFeasible;
            macroViolations = validation.Violations.Count;
        }
        string attempt = string.IsNullOrWhiteSpace(solveAttemptId)
            ? Path.GetFileNameWithoutExtension(logFile) ?? "source_recourse"
            : solveAttemptId;
        ContinuousCoordinationEvidenceBinding evidence = ContinuousCoordinationEvidenceBinding.Create(
            _coordinationModelId, SnapshotFingerprint.Calculate(_reference), _patterns,
            status, proofRows, attempt, _modelConfigurationIdentity);
        ContinuousCoordinationProofDisposition disposition = status == GRB.Status.INFEASIBLE && proofRows.Count > 0
            ? ContinuousCoordinationProofDisposition.HardProof
            : ContinuousCoordinationProofDisposition.NeighborhoodHintOnly;
        return new(status, StatusName(status), hasSolution, clock.Elapsed.TotalSeconds,
            solution, macroValidated, macroViolations, _audit, disposition, proofRows, evidence,
            hasSolution ? "source_continuous_recourse_feasible" :
            status == GRB.Status.INFEASIBLE ? "source_continuous_recourse_proven_infeasible" :
            $"source_continuous_recourse_no_solution_status_{status}");
    }

    private static void AddSnapshotValues(
        IDictionary<string, (GRBVar Variable, int Value, string Family)> target,
        IReadOnlyDictionary<string, GRBVar> variables,
        IReadOnlyDictionary<string, int> values,
        string family)
    {
        foreach ((string key, GRBVar variable) in variables)
        {
            int value = values[key];
            if (value is not 0 and not 1)
                throw new InvalidDataException($"Snapshot {family} '{key}' is not binary: {value}.");
            target.Add(variable.Get(GRB.StringAttr.VarName), (variable, value, family));
        }
    }

    private static void AddMaintenanceValues(
        IDictionary<string, (GRBVar Variable, int Value, string Family)> target,
        BasicMilpModel model,
        MasterSolutionSnapshot snapshot)
    {
        foreach (MaintenanceWindowBinaryBinding binding in model.MaintenanceWindowBinaryBindings)
        {
            int value = MacroDiscreteStateResolver.ResolveMaintenanceBeforeValue(
                snapshot.ArrivalSecondsByStopId[binding.SectionEndStopId],
                snapshot.DepartureSecondsByStopId[binding.SectionStartStopId],
                binding.WindowStartSeconds,
                binding.WindowEndSeconds);
            target.Add(binding.BeforeWindowVariable.Get(GRB.StringAttr.VarName),
                (binding.BeforeWindowVariable, value, "maintenance_window"));
        }
    }

    private static CompleteMacroDiscreteState CreateSourceClassifiedState(
        BasicMilpModel model,
        MasterSolutionSnapshot snapshot)
    {
        Dictionary<string, int> maintenance = new(StringComparer.Ordinal);
        foreach (MaintenanceWindowBinaryBinding binding in model.MaintenanceWindowBinaryBindings)
            maintenance[binding.BeforeWindowVariable.Get(GRB.StringAttr.VarName)] =
                MacroDiscreteStateResolver.ResolveMaintenanceBeforeValue(
                    snapshot.ArrivalSecondsByStopId[binding.SectionEndStopId],
                    snapshot.DepartureSecondsByStopId[binding.SectionStartStopId],
                    binding.WindowStartSeconds, binding.WindowEndSeconds);
        Dictionary<string, int> quota = new(StringComparer.Ordinal);
        Dictionary<string, TrainStopRecord> stops = model.Instance.TrainStops.ToDictionary(
            stop => stop.StopId, StringComparer.OrdinalIgnoreCase);
        foreach (StationQuotaCategoryBinding binding in model.StationQuotaCategoryBindings)
        {
            TrainStopRecord stop = stops[binding.StopId];
            MacroQuotaCategory category = MacroDiscreteStateResolver.ResolveQuotaCategory(
                snapshot.StopByStopId[binding.StopId] == 1,
                stop.StopRole == TrainStopRole.Origin
                    ? snapshot.DepartureSecondsByStopId[binding.StopId]
                    : snapshot.ArrivalSecondsByStopId[binding.StopId],
                binding.WindowStartSeconds, binding.WindowEndSeconds);
            quota[binding.InWindowVariable.Get(GRB.StringAttr.VarName)] =
                category == MacroQuotaCategory.InWindow ? 1 : 0;
            quota[binding.LeftOutsideVariable.Get(GRB.StringAttr.VarName)] =
                category == MacroQuotaCategory.LeftOutside ? 1 : 0;
            quota[binding.RightOutsideVariable.Get(GRB.StringAttr.VarName)] =
                category == MacroQuotaCategory.RightOutside ? 1 : 0;
        }
        return new(
            new Dictionary<string, int>(snapshot.StopByStopId, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, int>(snapshot.SectionPrecedenceByKey, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, int>(snapshot.StationEventPrecedenceByKey, StringComparer.OrdinalIgnoreCase),
            maintenance, quota, 0, 0, SnapshotFingerprint.Calculate(snapshot));
    }

    private static void AddExplicitAuxiliaryValues(
        IDictionary<string, (GRBVar Variable, int Value, string Family)> target,
        IEnumerable<GRBVar> variables,
        IReadOnlyDictionary<string, int> values,
        string family)
    {
        GRBVar[] owned = variables.ToArray();
        string[] names = owned.Select(variable => variable.Get(GRB.StringAttr.VarName)).ToArray();
        ValidateExactCoverage(names, values.Keys, family);
        foreach (GRBVar variable in owned)
        {
            string name = variable.Get(GRB.StringAttr.VarName);
            int value = values[name];
            if (value is not 0 and not 1)
                throw new InvalidDataException($"Explicit {family} '{name}' is not binary: {value}.");
            target.Add(name, (variable, value, family));
        }
    }

    private static void AddQuotaValues(
        IDictionary<string, (GRBVar Variable, int Value, string Family)> target,
        BasicMilpModel model,
        MasterSolutionSnapshot snapshot)
    {
        Dictionary<string, TrainStopRecord> stops = model.Instance.TrainStops.ToDictionary(
            stop => stop.StopId,
            StringComparer.OrdinalIgnoreCase);
        foreach (StationQuotaCategoryBinding binding in model.StationQuotaCategoryBindings)
        {
            TrainStopRecord stop = stops[binding.StopId];
            bool isStop = snapshot.StopByStopId[binding.StopId] == 1;
            double eventTime = stop.StopRole == TrainStopRole.Origin
                ? snapshot.DepartureSecondsByStopId[binding.StopId]
                : snapshot.ArrivalSecondsByStopId[binding.StopId];
            MacroQuotaCategory category = MacroDiscreteStateResolver.ResolveQuotaCategory(
                isStop,
                eventTime,
                binding.WindowStartSeconds,
                binding.WindowEndSeconds);
            Add(binding.InWindowVariable, category == MacroQuotaCategory.InWindow ? 1 : 0, "quota_in_window");
            Add(binding.LeftOutsideVariable, category == MacroQuotaCategory.LeftOutside ? 1 : 0, "quota_left_outside");
            Add(binding.RightOutsideVariable, category == MacroQuotaCategory.RightOutside ? 1 : 0, "quota_right_outside");
        }

        void Add(GRBVar variable, int value, string family) =>
            target.Add(variable.Get(GRB.StringAttr.VarName), (variable, value, family));
    }

    private static void AddReferenceRetimingObjective(BasicMilpModel model, MasterSolutionSnapshot snapshot)
    {
        GRBLinExpr deviations = new();
        foreach ((string stopId, GRBVar variable) in model.ArrivalVars)
            AddAbsoluteDeviation(model.Model, deviations, variable, snapshot.ArrivalSecondsByStopId[stopId], $"arr_{stopId}");
        foreach ((string stopId, GRBVar variable) in model.DepartureVars)
            AddAbsoluteDeviation(model.Model, deviations, variable, snapshot.DepartureSecondsByStopId[stopId], $"dep_{stopId}");
        model.Model.Set(GRB.IntAttr.NumObj, 1);
        model.Model.SetObjective(deviations, GRB.MINIMIZE);
    }

    private static void AddAbsoluteDeviation(
        GRBModel model,
        GRBLinExpr objective,
        GRBVar variable,
        double reference,
        string id)
    {
        GRBVar deviation = model.AddVar(0.0, GRB.INFINITY, 0.0, GRB.CONTINUOUS, $"fd_dev_{Sanitize(id)}");
        model.AddConstr(deviation - variable, GRB.GREATER_EQUAL, -reference, $"fd_dev_pos_{Sanitize(id)}");
        model.AddConstr(deviation + variable, GRB.GREATER_EQUAL, reference, $"fd_dev_neg_{Sanitize(id)}");
        objective.AddTerm(1.0, deviation);
    }

    private static int MaterializeVariableBounds(GRBModel model)
    {
        model.Update();
        int count = 0;
        foreach (GRBVar variable in model.GetVars())
        {
            string name = Sanitize(variable.Get(GRB.StringAttr.VarName));
            double lower = variable.Get(GRB.DoubleAttr.LB);
            double upper = variable.Get(GRB.DoubleAttr.UB);
            if (lower > -GRB.INFINITY / 2.0)
            {
                model.AddConstr(variable, GRB.GREATER_EQUAL, lower, $"fd_bound_lb_{name}");
                variable.Set(GRB.DoubleAttr.LB, -GRB.INFINITY);
                count++;
            }
            if (upper < GRB.INFINITY / 2.0)
            {
                model.AddConstr(variable, GRB.LESS_EQUAL, upper, $"fd_bound_ub_{name}");
                variable.Set(GRB.DoubleAttr.UB, GRB.INFINITY);
                count++;
            }
        }
        return count;
    }

    private static void ValidateExactCoverage(IEnumerable<string> expectedKeys, IEnumerable<string> actualKeys, string family)
    {
        HashSet<string> expected = expectedKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> actual = actualKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (expected.SetEquals(actual)) return;
        throw new InvalidDataException(
            $"Snapshot {family} coverage mismatch: missing=[{string.Join('|', expected.Except(actual).Take(10))}], " +
            $"extra=[{string.Join('|', actual.Except(expected).Take(10))}].");
    }

    private static string StatusName(int status) => status switch
    {
        GRB.Status.OPTIMAL => "OPTIMAL",
        GRB.Status.INFEASIBLE => "INFEASIBLE",
        GRB.Status.TIME_LIMIT => "TIME_LIMIT",
        GRB.Status.INF_OR_UNBD => "INF_OR_UNBD",
        _ => status.ToString()
    };

    private static string Sanitize(string value) => new(value.Select(character =>
        char.IsLetterOrDigit(character) ? character : '_').Take(160).ToArray());
}
