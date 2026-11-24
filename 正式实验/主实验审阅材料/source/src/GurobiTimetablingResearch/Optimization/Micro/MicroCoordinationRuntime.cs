using System.Text;
using System.Text.Json;
using Gurobi;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

internal sealed record MicroCoordinationRoundContext(
    IReadOnlyList<MicroWitnessCertificate> Certificates,
    IReadOnlyList<CompleteStationMicroComponentBuildResult> ReopeningComponents,
    string CandidateFingerprint,
    string DataVersion,
    string RuleVersion);

internal sealed record MicroCoordinationBranchDecision(
    MicroCoordinationMode Mode,
    string AlphaFingerprint,
    IReadOnlySet<string> ReopenedStations,
    IReadOnlyList<MicroWitnessCertificate> RetainedCertificates,
    IReadOnlyList<CapacityAnalysisResult> CapacityResults,
    IReadOnlyList<string> AReleasedRequirementIds,
    int EstimatedAddedAlternatives,
    int EstimatedAddedOrderVariables);

internal static class MicroCoordinationRuntime
{
    internal static MicroCoordinationRoundContext DisabledRound(MasterSolutionSnapshot snapshot) =>
        new([], [], SnapshotFingerprint.Calculate(snapshot), "disabled", "disabled");

    internal static MicroCoordinationRoundContext PrepareRound(
        BasicMilpModel sourceModel,
        MasterSolutionSnapshot snapshot,
        int endpointOccupationSeconds,
        int iterationIndex)
    {
        string candidate = SnapshotFingerprint.Calculate(snapshot);
        string dataVersion = $"{sourceModel.Instance.Meta.InstanceName}:{sourceModel.Instance.Trains.Count}:{sourceModel.Instance.TrainStops.Count}";
        string ruleVersion = $"strict-direction-time-v2;routes={sourceModel.Instance.Routes.Count};headways={sourceModel.Instance.RouteHeadwayRules.Count};endpoint={endpointOccupationSeconds}";
        List<MicroWitnessCertificate> certificates = [];
        foreach (IGrouping<string, VerifiedMicroAssignmentWitness> stationWitnesses in
                 (snapshot.VerifiedMicroAssignments ?? []).GroupBy(w => w.StationName, StringComparer.OrdinalIgnoreCase))
        {
            MicroWitnessCertificateBuildResult build = MicroWitnessCertificateBuilder.Build(
                new(sourceModel.Instance, snapshot, stationWitnesses.ToArray(), dataVersion, ruleVersion,
                    CheckerToleranceSeconds: 0, endpointOccupationSeconds));
            if (build.IsSupported && build.Certificate is not null) certificates.Add(build.Certificate);
        }

        List<CompleteStationMicroComponentBuildResult> components = [];
        foreach (MicroWitnessCertificate certificate in certificates)
        {
            components.Add(ValidityFirstHotspotComponentBuilder.BuildCompleteStationComponent(
                sourceModel, certificate.StationName, endpointOccupationSeconds, iterationIndex,
                $"certificate={certificate.CertificateId}"));
        }
        return new(certificates, components, candidate, dataVersion, ruleVersion);
    }

    internal static MicroCoordinationBranchDecision Decide(
        TimetablingInstance instance,
        MicroCoordinationRoundContext round,
        ValidityFirstRepairNeighborhoodPlan plan,
        MicroCoordinationMode mode)
    {
        string alpha = BuildAlpha(round.CandidateFingerprint, plan);
        HashSet<string> reopened = new(StringComparer.OrdinalIgnoreCase);
        if (mode != MicroCoordinationMode.D0)
        {
            foreach (MicroWitnessCertificate certificate in round.Certificates)
                if (plan.ReleasesAllVerifiedAssignments || certificate.Witnesses.Any(w =>
                        plan.ReleasedRequirementIds.Contains(w.RequirementId)))
                    reopened.Add(certificate.StationName);
        }

        List<CapacityAnalysisResult> analyses = mode == MicroCoordinationMode.D2
            ? AnalyzeCapacity(instance, round.Certificates.Where(c => !reopened.Contains(c.StationName)).ToArray(), alpha)
            : [];
        if (mode == MicroCoordinationMode.D2)
        {
            ReopenableCertificate[] candidates = round.Certificates.Where(c => !reopened.Contains(c.StationName))
                .Select(c => new ReopenableCertificate(c.CertificateId, c.Witnesses.Count,
                    round.ReopeningComponents.First(component => component.Component?.StationName == c.StationName).AlternativeCount))
                .ToArray();
            MicroReopeningPlan capacityPlan = MicroReopeningPlanner.Select(
                candidates, analyses.Where(a => a.Clause is not null).Select(a => a.Clause!).ToArray(), alpha);
            foreach (string id in capacityPlan.ReleasedCertificateIds)
            {
                MicroWitnessCertificate? certificate = round.Certificates.FirstOrDefault(c => c.CertificateId == id);
                if (certificate is not null) reopened.Add(certificate.StationName);
            }
        }

        IReadOnlyList<MicroWitnessCertificate> retained = mode == MicroCoordinationMode.D0
            ? []
            : round.Certificates.Where(c => !reopened.Contains(c.StationName)).ToArray();
        int alternatives = round.ReopeningComponents.Where(c => c.Component is not null && reopened.Contains(c.Component.StationName))
            .Sum(c => c.AlternativeCount);
        int orders = round.ReopeningComponents.Where(c => c.Component is not null && reopened.Contains(c.Component.StationName))
            .Sum(c => PromotedMicroCompactRelationEstimator.Estimate(c.Component!.Requirements,
                (leading, following) => instance.TryGetRouteHeadwayRule(leading, following, out RouteHeadwayRule? rule) && rule is not null)
                .OrderVariableCount);
        return new(mode, alpha, reopened, retained, analyses,
            plan.ReleasedRequirementIds.Order(StringComparer.OrdinalIgnoreCase).ToArray(), alternatives, orders);
    }

    private static List<CapacityAnalysisResult> AnalyzeCapacity(
        TimetablingInstance instance,
        IReadOnlyList<MicroWitnessCertificate> retained,
        string alpha)
    {
        Dictionary<string, (bool IsStop, string CertificateId)> fixedModes = retained
            .SelectMany(c => c.Witnesses.Select(w => (w.RequirementId, w.IsStop, c.CertificateId)))
            .ToDictionary(x => x.RequirementId, x => (x.IsStop, x.CertificateId), StringComparer.OrdinalIgnoreCase);
        List<CapacityAnalysisResult> results = [];
        foreach (TrainSectionStopQuotaConstruction rule in TrainSectionStopQuotaConstraintBuilder.Plan(instance))
        {
            int upper = rule.CountedStops.Count(stop => !fixedModes.TryGetValue(stop.StopId, out var fixedMode) || fixedMode.IsStop);
            string[] blockers = rule.CountedStops.Where(stop => fixedModes.TryGetValue(stop.StopId, out var mode) && !mode.IsStop)
                .Select(stop => fixedModes[stop.StopId].CertificateId).Distinct(StringComparer.Ordinal).ToArray();
            results.Add(ServiceCapacityRelaxationBuilder.FromCertifiedUpperBound(
                rule.RuleId, rule.MinStops, upper, blockers, alpha));
        }

        for (int index = 0; index < instance.StationStopQuotaRules.Count; index++)
        {
            StationStopQuotaRule rule = instance.StationStopQuotaRules[index];
            ServiceTimeWindowEnvelope envelope = ServiceTimeWindowEnvelope.Create(
                rule.StartTimeSeconds, rule.EndTimeSeconds, 0.0);
            TrainStopRecord[] candidates = instance.TrainStops.Where(stop =>
                string.Equals(stop.StationName, rule.StationName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(stop.TrainType, rule.TrainType, StringComparison.OrdinalIgnoreCase) &&
                envelope.MayContribute(
                    stop.StopRole == TrainStopRole.Origin ? stop.EarliestDepSeconds : stop.EarliestArrSeconds,
                    stop.StopRole == TrainStopRole.Origin ? stop.LatestDepSeconds : stop.LatestArrSeconds)).ToArray();
            int upper = candidates.Count(stop => !fixedModes.TryGetValue(stop.StopId, out var fixedMode) || fixedMode.IsStop);
            string[] blockers = candidates.Where(stop => fixedModes.TryGetValue(stop.StopId, out var mode) && !mode.IsStop)
                .Select(stop => fixedModes[stop.StopId].CertificateId).Distinct(StringComparer.Ordinal).ToArray();
            results.Add(ServiceCapacityRelaxationBuilder.FromCertifiedUpperBound(
                $"station-stop-{index + 1:D3}", rule.MinStops, upper, blockers, alpha));
        }
        return results;
    }

    private static string BuildAlpha(string candidate, ValidityFirstRepairNeighborhoodPlan plan)
    {
        string raw = string.Join('|', candidate, plan.NeighborhoodId, plan.EndpointOrderChangeBudget,
            string.Join(';', plan.ReleasedRequirementIds.Order(StringComparer.OrdinalIgnoreCase)));
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }
}

internal static class MicroCoordinationTraceReporter
{
    private static readonly object Sync = new();
    internal static void Append(string outputDirectory, int iterationIndex,
        ValidityFirstRepairNeighborhoodPlan plan, MicroCoordinationBranchDecision decision,
        MicroCertificateBindingResult binding, BasicMilpModel model,
        VerifiedMicroAssignmentMipStartResult start)
    {
        Directory.CreateDirectory(outputDirectory);
        string line = JsonSerializer.Serialize(new
        {
            timestamp = DateTimeOffset.Now,
            iterationIndex,
            planId = plan.NeighborhoodId,
            mode = decision.Mode.ToString(),
            decision.AlphaFingerprint,
            certificateCount = decision.RetainedCertificates.Count + decision.ReopenedStations.Count,
            retainedCertificateIds = decision.RetainedCertificates.Select(c => c.CertificateId),
            reopenedStations = decision.ReopenedStations,
            capacity = decision.CapacityResults,
            decision.AReleasedRequirementIds,
            binding.AddedConstraintIds,
            binding.ProtectedRequirementIds,
            decision.EstimatedAddedAlternatives,
            decision.EstimatedAddedOrderVariables,
            actualChoiceVariables = model.PromotedMicroAlternativeVariableCount,
            actualOrderVariables = model.PromotedMicroOrderVariableCount,
            actualCertificateConstraints = model.MicroCertificateConstraintIds.Count,
            sourceVariables = model.Model.Get(GRB.IntAttr.NumVars),
            sourceBinaryVariables = model.Model.GetVars().Count(variable => variable.Get(GRB.CharAttr.VType) == GRB.BINARY),
            sourceConstraints = model.Model.Get(GRB.IntAttr.NumConstrs),
            mipStart = start
        });
        lock (Sync) File.AppendAllText(Path.Combine(outputDirectory, "micro_coordination_trace.jsonl"), line + Environment.NewLine, new UTF8Encoding(false));
    }
}
