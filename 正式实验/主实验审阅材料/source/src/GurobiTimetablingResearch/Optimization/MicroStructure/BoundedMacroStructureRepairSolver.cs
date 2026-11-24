using System.Diagnostics;
using Gurobi;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

internal sealed record BoundedMacroStructureScope(
    IReadOnlySet<string> ReleasedStopIds,
    IReadOnlySet<string> ReleasedSectionPrecedenceKeys,
    IReadOnlySet<string> AffectedStations,
    int IndependentMainDecisionCount,
    bool ScopeCapReached,
    string SelectionBasis,
    PrecedenceTrustRegion TrustRegion,
    string CoordinationModelId,
    string CoordinationSolveResultIdentity,
    string PatternCombinationIdentity,
    ContinuousProofAvailability ProofAvailability,
    IReadOnlyList<ContinuousProofBusinessMapping> EvidenceMappings);

internal static class BoundedMacroStructureScopeSelector
{
    internal static BoundedMacroStructureScope Select(
        TimetablingInstance instance,
        MasterSolutionSnapshot reference,
        MacroRepairCoordinationBinding binding,
        int maximumIndependentMainDecisions = 32,
        int endpointOrderChangeBudget = 4)
    {
        if (maximumIndependentMainDecisions <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumIndependentMainDecisions));
        HashSet<string> active = binding.ActiveStations.ToHashSet(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<ContinuousProofBusinessMapping> mappings = ContinuousProofDependencyMapper.Map(
            instance, binding.ActiveStations, binding.Evidence, binding.Patterns);
        PrecedenceTrustRegion legalEndpointRegion = ValidityFirstEndpointOrderTrustRegionBuilder.Build(
            instance,
            reference,
            binding.Pair is null ? new HashSet<int>() : new HashSet<int> { binding.Pair.TrainIndex },
            endpointOrderChangeBudget,
            "micro_structure_original_k_center");
        Dictionary<string, SectionConflictCandidate> candidates = new InitialConflictGraphBuilder(instance, int.MaxValue)
            .Build()
            .ToDictionary(BasicMilpModelBuilder.BuildSectionPrecedenceKey, StringComparer.OrdinalIgnoreCase);

        bool IsProofSupportedSection(string key)
        {
            return mappings.SelectMany(mapping => mapping.Decisions).Any(decision =>
                decision.IsAdjustable &&
                string.Equals(decision.DecisionFamily, "section_precedence", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(decision.DecisionKey, key, StringComparison.OrdinalIgnoreCase));
        }

        bool TouchesActiveStation(string key)
        {
            if (!candidates.TryGetValue(key, out SectionConflictCandidate? candidate) ||
                !instance.SectionByName.TryGetValue(candidate.SectionName, out SectionRecord? section)) return false;
            return active.Contains(section.FromStation) || active.Contains(section.ToStation);
        }

        List<string> precedenceCandidates = legalEndpointRegion.HotspotKeys
            .Where(reference.SectionPrecedenceByKey.ContainsKey)
            .OrderBy(key => IsProofSupportedSection(key) ? 0 : TouchesActiveStation(key) ? 1 : 2)
            .ThenBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        List<TrainStopRecord> stopCandidates = instance.TrainStops
            .Where(stop => active.Contains(stop.StationName) &&
                           stop.StopRequirement == StopRequirement.OptionalStop &&
                           stop.StopRole == TrainStopRole.Intermediate &&
                           (binding.Pair is null || stop.TrainIndex == binding.Pair.TrainIndex))
            .OrderBy(stop => stop.TrainIndex)
            .ThenBy(stop => stop.StationOrder)
            .ToList();

        List<(string Family, string Key)> confirmed = mappings
            .SelectMany(mapping => mapping.Decisions)
            .Where(decision => decision.IsAdjustable &&
                               decision.DecisionFamily is "stop" or "section_precedence")
            .Select(decision => (
                decision.DecisionFamily == "section_precedence" ? "section" : "stop",
                decision.DecisionKey))
            .GroupBy(item => $"{item.Item1}:{item.Item2}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        // Confirmed proof dependencies are selected before any pair fallback.  If a hard proof
        // itself needs more than the nominal engineering cap, its complete dependency closure is
        // retained and reported rather than silently truncated.
        List<(string Family, string Key)> ordered = confirmed
            .Concat(stopCandidates.Select(stop => ("stop", stop.StopId)))
            .Concat(precedenceCandidates.Select(key => ("section", key)))
            .GroupBy(item => $"{item.Item1}:{item.Item2}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        bool capReached = ordered.Count > maximumIndependentMainDecisions;
        int selectionLimit = Math.Max(maximumIndependentMainDecisions, confirmed.Count);
        (string Family, string Key)[] selected = ordered.Take(selectionLimit).ToArray();
        HashSet<string> releasedStops = selected.Where(item => item.Family == "stop")
            .Select(item => item.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> releasedPrecedence = selected.Where(item => item.Family == "section")
            .Select(item => item.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> affected = new(active, StringComparer.OrdinalIgnoreCase);
        foreach (string key in releasedPrecedence)
        {
            SectionConflictCandidate candidate = candidates[key];
            if (instance.SectionByName.TryGetValue(candidate.SectionName, out SectionRecord? section))
            {
                affected.Add(section.FromStation);
                affected.Add(section.ToStation);
            }
        }
        PrecedenceTrustRegion applied = new(
            legalEndpointRegion.RegionId,
            legalEndpointRegion.CenterValuesByKey,
            releasedPrecedence,
            endpointOrderChangeBudget,
            0);
        string basis = binding.Evidence.ProofAvailability switch
        {
            ContinuousProofAvailability.HardProof when confirmed.Count > 0 =>
                "corresponding_hard_proof_dependencies_then_pair_fallback",
            ContinuousProofAvailability.HardProof =>
                "corresponding_hard_proof_mapped_without_adjustable_main_decision_then_pair_fallback",
            ContinuousProofAvailability.ProofMappingFailed =>
                "proof_mapping_failed_pair_fallback_not_iis_guided",
            _ => "no_infeasibility_proof_pair_fallback"
        };
        return new(
            releasedStops,
            releasedPrecedence,
            affected,
            selected.Length,
            capReached,
            basis,
            applied,
            binding.Evidence.CoordinationModelId,
            binding.Evidence.SolveResultIdentity,
            binding.Evidence.PatternCombinationIdentity,
            binding.Evidence.ProofAvailability,
            mappings.ToArray());
    }
}

internal sealed record BoundedMacroStructureRepairResult(
    bool HasCandidate,
    int StatusCode,
    string Status,
    double RuntimeSeconds,
    double? FirstIncumbentSeconds,
    BoundedMacroStructureScope Scope,
    int SourceVariables,
    int SourceBinaries,
    int SourceConstraints,
    bool PresolveSizeAvailable,
    string PresolveStopReason,
    int PresolvedVariables,
    int PresolvedBinaries,
    int PresolvedConstraints,
    int RetainedPatternCount,
    int PatternConstraintCount,
    int FixedMainDecisionCount,
    int FreeIndependentMainDecisionCount,
    int DerivedBinaryCount,
    int EndpointOrderChanges,
    int StopChanges,
    IReadOnlyList<string> ChangedDecisionIds,
    bool MacroValidated,
    int MacroViolationCount,
    MasterSolutionSnapshot? CandidateSnapshot,
    string StopReason);

internal static class BoundedMacroStructureRepairSolver
{
    internal static BoundedMacroStructureRepairResult Solve(
        TimetablingInstance instance,
        MasterSolutionSnapshot reference,
        MacroRepairCoordinationBinding binding,
        double timeLimitSeconds,
        string? logFile,
        double runningTimeSupplementRatio = 0.05,
        int endpointOccupationSeconds = StationEndpointOccupationPolicy.DefaultSeconds,
        int maximumIndependentMainDecisions = 32,
        int endpointOrderChangeBudget = 4)
    {
        BoundedMacroStructureScope scope = BoundedMacroStructureScopeSelector.Select(
            instance, reference, binding,
            maximumIndependentMainDecisions, endpointOrderChangeBudget);
        if (scope.IndependentMainDecisionCount == 0)
            return Empty(scope, "no_legal_macro_main_decision_in_direct_scope");

        HashSet<string> affected = scope.AffectedStations.ToHashSet(StringComparer.OrdinalIgnoreCase);
        MicroWitnessCertificate[] retained = binding.Patterns
            .Where(pattern => !affected.Contains(pattern.StationName)).ToArray();
        using BasicMilpModel model = new BasicMilpModelBuilder(
            instance,
            neighborRadius: int.MaxValue,
            precedenceTrustRegion: scope.TrustRegion,
            runningTimeSupplementRatio: runningTimeSupplementRatio,
            endpointOccupationSeconds: endpointOccupationSeconds).Build();
        int fixedMain = 0;
        foreach ((string key, GRBVar variable) in model.StopVars)
        {
            if (scope.ReleasedStopIds.Contains(key)) continue;
            Fix(variable, reference.StopByStopId[key]);
            fixedMain++;
        }
        foreach ((string key, GRBVar variable) in model.SectionPrecedenceVars)
        {
            if (scope.ReleasedSectionPrecedenceKeys.Contains(key)) continue;
            Fix(variable, reference.SectionPrecedenceByKey[key]);
            fixedMain++;
        }
        foreach ((string key, GRBVar variable) in model.StationEventPrecedenceVars)
        {
            Fix(variable, reference.StationEventPrecedenceByKey[key]);
            fixedMain++;
        }
        int patternConstraints = retained.Length == 0
            ? 0
            : MicroCertificateModelBinder.Apply(model, retained).AddedConstraintIds.Count;
        MasterSolutionSnapshotApplier.ApplyMipStart(model, reference);
        model.Model.Set(GRB.IntAttr.NumObj, 1);
        model.Model.SetObjective(new GRBLinExpr(), GRB.MINIMIZE);
        model.Model.Update();
        int variables = model.Model.Get(GRB.IntAttr.NumVars);
        int binaries = model.Model.Get(GRB.IntAttr.NumBinVars);
        int constraints = model.Model.Get(GRB.IntAttr.NumConstrs);
        int derivedBinaries = Math.Max(0, binaries - fixedMain - scope.IndependentMainDecisionCount);
        (bool presolveAvailable, string presolveReason, int presolvedVariables,
            int presolvedBinaries, int presolvedConstraints) = CapturePresolveSize(model.Model);
        if (!string.IsNullOrWhiteSpace(logFile))
        {
            string? directory = Path.GetDirectoryName(Path.GetFullPath(logFile));
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            model.Model.Set(GRB.StringParam.LogFile, logFile);
        }
        model.Model.Set(GRB.IntParam.Seed, 0);
        model.Model.Set(GRB.IntParam.Threads, 0);
        model.Model.Set(GRB.DoubleParam.TimeLimit, Math.Max(0.1, timeLimitSeconds));
        FirstIncumbentTrackingCallback incumbentCallback = new(true);
        model.Model.SetCallback(incumbentCallback);
        Stopwatch clock = Stopwatch.StartNew();
        model.Optimize();
        clock.Stop();
        int status = model.Model.Get(GRB.IntAttr.Status);
        if (!model.HasIncumbentSolution)
            return new(false, status, StatusName(status), clock.Elapsed.TotalSeconds,
                incumbentCallback.FirstIncumbentTimeSeconds, scope,
                variables, binaries, constraints, presolveAvailable, presolveReason,
                presolvedVariables, presolvedBinaries, presolvedConstraints,
                retained.Length, patternConstraints, fixedMain, scope.IndependentMainDecisionCount,
                derivedBinaries, 0, 0, [], false, 0, null,
                status == GRB.Status.INFEASIBLE
                    ? "bounded_macro_scope_proven_infeasible"
                    : $"bounded_macro_scope_no_incumbent_status_{status}");

        MasterSolutionSnapshot candidate = MasterSolutionSnapshot.Capture(model) with { VerifiedMicroAssignments = null };
        List<string> changed = [];
        foreach ((string key, int value) in candidate.SectionPrecedenceByKey)
            if (reference.SectionPrecedenceByKey[key] != value) changed.Add($"section_precedence:{key}");
        foreach ((string key, int value) in candidate.StopByStopId)
            if (reference.StopByStopId[key] != value) changed.Add($"stop:{key}");
        int endpointChanges = changed.Count(item => item.StartsWith("section_precedence:", StringComparison.Ordinal));
        int stopChanges = changed.Count(item => item.StartsWith("stop:", StringComparison.Ordinal));
        if (endpointChanges > endpointOrderChangeBudget ||
            changed.Any(item => item.StartsWith("section_precedence:", StringComparison.Ordinal) &&
                                !scope.ReleasedSectionPrecedenceKeys.Contains(item["section_precedence:".Length..])) ||
            changed.Any(item => item.StartsWith("stop:", StringComparison.Ordinal) &&
                                !scope.ReleasedStopIds.Contains(item["stop:".Length..])))
            throw new InvalidDataException("Bounded Macro solution escaped the registered K/direct-dependency scope.");
        MasterValidationReport validation = new MasterSolutionValidator().Validate(model);
        return new(validation.IsFeasible, status, StatusName(status), clock.Elapsed.TotalSeconds,
            incumbentCallback.FirstIncumbentTimeSeconds, scope,
            variables, binaries, constraints, presolveAvailable, presolveReason,
            presolvedVariables, presolvedBinaries, presolvedConstraints,
            retained.Length, patternConstraints, fixedMain, scope.IndependentMainDecisionCount,
            derivedBinaries, endpointChanges, stopChanges, changed, validation.IsFeasible,
            validation.Violations.Count, validation.IsFeasible ? candidate : null,
            validation.IsFeasible ? "bounded_macro_candidate_validated" : "bounded_macro_candidate_failed_macro_validation");
    }

    private static void Fix(GRBVar variable, int value)
    {
        variable.Set(GRB.DoubleAttr.LB, value);
        variable.Set(GRB.DoubleAttr.UB, value);
    }

    private static BoundedMacroStructureRepairResult Empty(BoundedMacroStructureScope scope, string reason) =>
        new(false, 0, "NOT_RUN", 0, null, scope, 0, 0, 0, false, "not_attempted",
            0, 0, 0, 0, 0, 0, scope.IndependentMainDecisionCount, 0,
            0, 0, [], false, 0, null, reason);

    private static (bool Available, string StopReason, int Variables, int Binaries, int Constraints)
        CapturePresolveSize(GRBModel model)
    {
        try
        {
            using GRBModel presolved = model.Presolve();
            return (true, "presolved_model_available", presolved.Get(GRB.IntAttr.NumVars),
                presolved.Get(GRB.IntAttr.NumBinVars), presolved.Get(GRB.IntAttr.NumConstrs));
        }
        catch (GRBException exception)
        {
            return (false, $"presolved_model_unavailable:{exception.ErrorCode}:{exception.Message}", -1, -1, -1);
        }
    }

    private static string StatusName(int status) => status switch
    {
        GRB.Status.OPTIMAL => "OPTIMAL",
        GRB.Status.INFEASIBLE => "INFEASIBLE",
        GRB.Status.TIME_LIMIT => "TIME_LIMIT",
        GRB.Status.INTERRUPTED => "INTERRUPTED",
        _ => status.ToString()
    };
}
