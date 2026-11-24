using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

public enum MicroCoordinationMode { D0, D1, D2 }

public static class MicroCoordinationModeParser
{
    public static MicroCoordinationMode Parse(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        null or "" or "D0" => MicroCoordinationMode.D0,
        "D1" => MicroCoordinationMode.D1,
        "D2" => MicroCoordinationMode.D2,
        _ => throw new ArgumentException("--micro-coordination-mode must be D0, D1, or D2.")
    };
}

public enum CertificateValidityOutcome
{
    Valid,
    OutsideCertifiedDomain,
    IncompleteSnapshot
}

public enum MicroCertificateConditionKind
{
    StopMode,
    TrackOccupationOrder,
    RouteHeadway
}

public sealed record MicroCertificateCondition(
    string ConditionId,
    MicroCertificateConditionKind Kind,
    string LaterStopId,
    StationEventSide LaterEvent,
    string? EarlierStopId,
    StationEventSide? EarlierEvent,
    double MinimumDifferenceSeconds,
    int? RequiredStopValue = null,
    string SourceRuleId = "",
    double LaterOffsetSeconds = 0.0,
    double EarlierOffsetSeconds = 0.0);

public sealed record MicroWitnessCertificate(
    string CertificateId,
    string StationName,
    string CandidateSnapshotFingerprint,
    string DataVersion,
    string RuleVersion,
    int CheckerToleranceSeconds,
    IReadOnlyList<VerifiedMicroAssignmentWitness> Witnesses,
    IReadOnlyList<MicroCertificateCondition> Conditions)
{
    // Gurobi's continuous feasibility tolerance is sub-microsecond here.  This epsilon is
    // numerical hygiene only; no business-time relaxation is allowed.
    private const double NumericalResidualToleranceSeconds = 1e-6;

    public CertificateValidityOutcome Evaluate(MasterSolutionSnapshot snapshot)
    {
        if (CheckerToleranceSeconds != 0)
            return CertificateValidityOutcome.OutsideCertifiedDomain;
        foreach (MicroCertificateCondition condition in Conditions)
        {
            if (condition.Kind == MicroCertificateConditionKind.StopMode)
            {
                if (!snapshot.StopByStopId.TryGetValue(condition.LaterStopId, out int stop))
                    return CertificateValidityOutcome.IncompleteSnapshot;
                if (stop != condition.RequiredStopValue)
                    return CertificateValidityOutcome.OutsideCertifiedDomain;
                continue;
            }

            if (condition.EarlierStopId is null || condition.EarlierEvent is null ||
                !TryReadEvent(snapshot, condition.LaterStopId, condition.LaterEvent, out double later) ||
                !TryReadEvent(snapshot, condition.EarlierStopId, condition.EarlierEvent.Value, out double earlier))
                return CertificateValidityOutcome.IncompleteSnapshot;

            if ((later + condition.LaterOffsetSeconds) -
                (earlier + condition.EarlierOffsetSeconds) + NumericalResidualToleranceSeconds <
                condition.MinimumDifferenceSeconds)
                return CertificateValidityOutcome.OutsideCertifiedDomain;
        }

        return CertificateValidityOutcome.Valid;
    }

    private static bool TryReadEvent(
        MasterSolutionSnapshot snapshot,
        string stopId,
        StationEventSide side,
        out double value)
    {
        IReadOnlyDictionary<string, double> source = side == StationEventSide.Incoming
            ? snapshot.ArrivalSecondsByStopId
            : snapshot.DepartureSecondsByStopId;
        return source.TryGetValue(stopId, out value);
    }
}

public sealed record MicroWitnessCertificateBuildRequest(
    TimetablingInstance Instance,
    MasterSolutionSnapshot Snapshot,
    IReadOnlyList<VerifiedMicroAssignmentWitness> Witnesses,
    string DataVersion,
    string RuleVersion,
    int CheckerToleranceSeconds,
    int EndpointOccupationSeconds = StationEndpointOccupationPolicy.DefaultSeconds);

public sealed record MicroWitnessCertificateBuildResult(
    bool IsSupported,
    string Reason,
    MicroWitnessCertificate? Certificate);

public static class MicroWitnessCertificateBuilder
{
    public static MicroWitnessCertificateBuildResult Build(MicroWitnessCertificateBuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.CheckerToleranceSeconds != 0)
            return new(false, "Legacy business-time relaxation is not supported by the strict contract", null);
        if (request.Witnesses.Count == 0)
            return new(false, "station has no validated witnesses", null);

        string station = request.Witnesses[0].StationName;
        if (request.Witnesses.Any(w => !string.Equals(w.StationName, station, StringComparison.OrdinalIgnoreCase)))
            return new(false, "witnesses span multiple stations", null);

        TrainStopRecord[] requiredStops = request.Instance.TrainStops
            .Where(s => string.Equals(s.StationName, station, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.StopId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Dictionary<string, VerifiedMicroAssignmentWitness> witnessByStop = request.Witnesses
            .GroupBy(w => w.RequirementId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Single(), StringComparer.OrdinalIgnoreCase);
        if (requiredStops.Any(s => !witnessByStop.ContainsKey(s.StopId)) ||
            witnessByStop.Count != requiredStops.Length)
            return new(false, "whole-station witness coverage is incomplete", null);

        List<MicroCertificateCondition> conditions = [];
        foreach (TrainStopRecord stop in requiredStops)
        {
            VerifiedMicroAssignmentWitness witness = witnessByStop[stop.StopId];
            if (!request.Snapshot.StopByStopId.TryGetValue(stop.StopId, out int stopValue) ||
                stopValue != (witness.IsStop ? 1 : 0) ||
                !request.Snapshot.ArrivalSecondsByStopId.ContainsKey(stop.StopId) ||
                !request.Snapshot.DepartureSecondsByStopId.ContainsKey(stop.StopId))
                return new(false, $"snapshot/witness mismatch at {stop.StopId}", null);

            conditions.Add(new(
                $"stop:{stop.StopId}", MicroCertificateConditionKind.StopMode,
                stop.StopId, StationEventSide.Incoming, null, null, 0,
                witness.IsStop ? 1 : 0, $"stop_requirement:{stop.StopId}"));
        }

        VerifiedMicroAssignmentWitness[] ordered = request.Witnesses
            .OrderBy(w => w.RequirementId, StringComparer.OrdinalIgnoreCase).ToArray();
        for (int i = 0; i < ordered.Length; i++)
        for (int j = i + 1; j < ordered.Length; j++)
        {
            VerifiedMicroAssignmentWitness first = ordered[i];
            VerifiedMicroAssignmentWitness second = ordered[j];
            AddTrackCondition(request, first, second, conditions);
            foreach (StationEventSide firstSide in Enum.GetValues<StationEventSide>())
            foreach (StationEventSide secondSide in Enum.GetValues<StationEventSide>())
                AddRouteCondition(request, first, second, firstSide, secondSide, conditions);
        }

        string fingerprint = request.Witnesses.Select(w => w.CandidateSnapshotFingerprint)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
        string payload = string.Join("\n", conditions.OrderBy(c => c.ConditionId).Select(c => c.ToString()));
        string id = $"cert:{station}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)))[..16].ToLowerInvariant()}";
        MicroWitnessCertificate certificate = new(
            id, station, fingerprint, request.DataVersion, request.RuleVersion,
            request.CheckerToleranceSeconds, request.Witnesses.ToArray(), conditions);
        CertificateValidityOutcome validity = certificate.Evaluate(request.Snapshot);
        return validity == CertificateValidityOutcome.Valid
            ? new(true, "complete validated whole-station certificate", certificate)
            : new(false,
                $"constructed certificate does not contain its source snapshot:" +
                DescribeFirstFailure(certificate, request.Snapshot), null);
    }

    private static string DescribeFirstFailure(
        MicroWitnessCertificate certificate,
        MasterSolutionSnapshot snapshot)
    {
        const double numericalResidualToleranceSeconds = 1e-6;
        foreach (MicroCertificateCondition condition in certificate.Conditions)
        {
            if (condition.Kind == MicroCertificateConditionKind.StopMode)
            {
                if (!snapshot.StopByStopId.TryGetValue(condition.LaterStopId, out int stop))
                    return $"incomplete_stop:{condition.ConditionId}";
                if (stop != condition.RequiredStopValue)
                    return $"stop_guard:{condition.ConditionId}:actual={stop}:required={condition.RequiredStopValue}";
                continue;
            }
            if (condition.EarlierStopId is null || condition.EarlierEvent is null ||
                !TryRead(snapshot, condition.LaterStopId, condition.LaterEvent, out double later) ||
                !TryRead(snapshot, condition.EarlierStopId, condition.EarlierEvent.Value, out double earlier))
                return $"incomplete_time:{condition.ConditionId}";
            double difference = later + condition.LaterOffsetSeconds -
                                earlier - condition.EarlierOffsetSeconds;
            if (difference + numericalResidualToleranceSeconds < condition.MinimumDifferenceSeconds)
                return $"time_row:{condition.ConditionId}:actual={difference:R}:" +
                       $"required={condition.MinimumDifferenceSeconds:R}:" +
                       $"shortfall={condition.MinimumDifferenceSeconds - difference:R}";
        }
        return "unknown_evaluation_mismatch";

        static bool TryRead(
            MasterSolutionSnapshot source,
            string stopId,
            StationEventSide side,
            out double value) =>
            (side == StationEventSide.Incoming
                ? source.ArrivalSecondsByStopId
                : source.DepartureSecondsByStopId).TryGetValue(stopId, out value);
    }

    private static void AddTrackCondition(
        MicroWitnessCertificateBuildRequest request,
        VerifiedMicroAssignmentWitness left,
        VerifiedMicroAssignmentWitness right,
        ICollection<MicroCertificateCondition> conditions)
    {
        if (!string.Equals(left.TrackName, right.TrackName, StringComparison.OrdinalIgnoreCase))
            return;
        (StationEventSide leftStartSide, double leftStartOffset, StationEventSide leftEndSide, double leftEndOffset) =
            ResolveOccupation(request.Instance, left, request.EndpointOccupationSeconds);
        (StationEventSide rightStartSide, double rightStartOffset, StationEventSide rightEndSide, double rightEndOffset) =
            ResolveOccupation(request.Instance, right, request.EndpointOccupationSeconds);
        double leftStart = ReadEvent(request.Snapshot, left.RequirementId, leftStartSide) + leftStartOffset;
        double rightStart = ReadEvent(request.Snapshot, right.RequirementId, rightStartSide) + rightStartOffset;
        double leftEnd = ReadEvent(request.Snapshot, left.RequirementId, leftEndSide) + leftEndOffset;
        double rightEnd = ReadEvent(request.Snapshot, right.RequirementId, rightEndSide) + rightEndOffset;
        bool leftCanPrecede = rightStart - leftEnd + request.CheckerToleranceSeconds >= -TimetableTimePolicy.NumericToleranceSeconds;
        bool rightCanPrecede = leftStart - rightEnd + request.CheckerToleranceSeconds >= -TimetableTimePolicy.NumericToleranceSeconds;
        VerifiedMicroAssignmentWitness earlier = leftCanPrecede && !rightCanPrecede
            ? left
            : rightCanPrecede && !leftCanPrecede
                ? right
                : leftStart <= rightStart
                    ? left
                    : right;
        VerifiedMicroAssignmentWitness later = ReferenceEquals(earlier, left) ? right : left;
        StationEventSide earlierEndSide = ReferenceEquals(earlier, left) ? leftEndSide : rightEndSide;
        double earlierEndOffset = ReferenceEquals(earlier, left) ? leftEndOffset : rightEndOffset;
        StationEventSide laterStartSide = ReferenceEquals(later, left) ? leftStartSide : rightStartSide;
        double laterStartOffset = ReferenceEquals(later, left) ? leftStartOffset : rightStartOffset;
        conditions.Add(new(
            $"track:{earlier.RequirementId}>{later.RequirementId}:{earlier.TrackName}",
            MicroCertificateConditionKind.TrackOccupationOrder,
            later.RequirementId, laterStartSide,
            earlier.RequirementId, earlierEndSide, -request.CheckerToleranceSeconds, null,
            $"track_nonoverlap:{earlier.TrackName}", laterStartOffset, earlierEndOffset));
    }

    private static void AddRouteCondition(
        MicroWitnessCertificateBuildRequest request,
        VerifiedMicroAssignmentWitness left,
        VerifiedMicroAssignmentWitness right,
        StationEventSide firstSide,
        StationEventSide secondSide,
        ICollection<MicroCertificateCondition> conditions)
    {
        int? leftRoute = firstSide == StationEventSide.Incoming ? left.IncomingRouteId : left.OutgoingRouteId;
        int? rightRoute = secondSide == StationEventSide.Incoming ? right.IncomingRouteId : right.OutgoingRouteId;
        if (leftRoute is null || rightRoute is null)
            return;

        double leftTime = ReadEvent(request.Snapshot, left.RequirementId, firstSide);
        double rightTime = ReadEvent(request.Snapshot, right.RequirementId, secondSide);
        bool leftEarlier;
        if (leftTime + request.CheckerToleranceSeconds + TimetableTimePolicy.NumericToleranceSeconds < rightTime)
            leftEarlier = true;
        else if (rightTime + request.CheckerToleranceSeconds + TimetableTimePolicy.NumericToleranceSeconds < leftTime)
            leftEarlier = false;
        else
        {
            bool forwardPermitsTie = !request.Instance.TryGetRouteHeadwayRule(
                leftRoute.Value, rightRoute.Value, out RouteHeadwayRule? tieForward) ||
                                     tieForward is null || tieForward.MinHeadwaySeconds <= request.CheckerToleranceSeconds;
            bool reversePermitsTie = !request.Instance.TryGetRouteHeadwayRule(
                rightRoute.Value, leftRoute.Value, out RouteHeadwayRule? tieReverse) ||
                                     tieReverse is null || tieReverse.MinHeadwaySeconds <= request.CheckerToleranceSeconds;
            if (!forwardPermitsTie && !reversePermitsTie) return;
            leftEarlier = forwardPermitsTie;
        }
        VerifiedMicroAssignmentWitness earlier = leftEarlier ? left : right;
        VerifiedMicroAssignmentWitness later = leftEarlier ? right : left;
        int earlierRoute = leftEarlier ? leftRoute.Value : rightRoute.Value;
        int laterRoute = leftEarlier ? rightRoute.Value : leftRoute.Value;
        StationEventSide earlierSide = leftEarlier ? firstSide : secondSide;
        StationEventSide laterSide = leftEarlier ? secondSide : firstSide;
        bool hasForward = request.Instance.TryGetRouteHeadwayRule(earlierRoute, laterRoute, out RouteHeadwayRule? rule) && rule is not null;
        bool hasReverse = request.Instance.TryGetRouteHeadwayRule(laterRoute, earlierRoute, out RouteHeadwayRule? reverseRule) && reverseRule is not null;
        if (!hasForward && !hasReverse)
            return;

        conditions.Add(new(
            $"route:{earlierSide}>{laterSide}:{earlier.RequirementId}>{later.RequirementId}:{earlierRoute}>{laterRoute}",
            MicroCertificateConditionKind.RouteHeadway,
            later.RequirementId, laterSide, earlier.RequirementId, earlierSide,
            (rule?.MinHeadwaySeconds ?? 0) - request.CheckerToleranceSeconds, null,
            hasForward
                ? $"route_headway:{earlierRoute}>{laterRoute}"
                : $"missing_order_preserved_against:{laterRoute}>{earlierRoute}"));
    }

    private static double ReadEvent(MasterSolutionSnapshot snapshot, string stopId, StationEventSide side) =>
        side == StationEventSide.Incoming
            ? snapshot.ArrivalSecondsByStopId[stopId]
            : snapshot.DepartureSecondsByStopId[stopId];

    private static (StationEventSide StartSide, double StartOffset, StationEventSide EndSide, double EndOffset)
        ResolveOccupation(TimetablingInstance instance, VerifiedMicroAssignmentWitness witness, int endpointOccupationSeconds)
    {
        TrainStopRecord stop = instance.StopsById[witness.RequirementId];
        return stop.StopRole switch
        {
            TrainStopRole.Origin => (StationEventSide.Outgoing, -endpointOccupationSeconds,
                StationEventSide.Outgoing, 0.0),
            TrainStopRole.Destination => (StationEventSide.Incoming, 0.0,
                StationEventSide.Incoming, endpointOccupationSeconds),
            _ => (StationEventSide.Incoming, 0.0, StationEventSide.Outgoing, 0.0)
        };
    }
}

public readonly record struct ExactRational : IComparable<ExactRational>
{
    private ExactRational(BigInteger numerator, BigInteger denominator)
    {
        if (denominator <= 0) throw new ArgumentOutOfRangeException(nameof(denominator));
        BigInteger divisor = BigInteger.GreatestCommonDivisor(BigInteger.Abs(numerator), denominator);
        Numerator = numerator / divisor;
        Denominator = denominator / divisor;
    }

    public BigInteger Numerator { get; }
    public BigInteger Denominator { get; }
    public static ExactRational Zero => Create(0, 1);
    public static ExactRational One => Create(1, 1);
    public static ExactRational Create(long numerator, long denominator) => new(numerator, denominator);
    public static ExactRational Create(BigInteger numerator, BigInteger denominator) => new(numerator, denominator);
    public static ExactRational operator +(ExactRational a, ExactRational b) =>
        Create(a.Numerator * b.Denominator + b.Numerator * a.Denominator, a.Denominator * b.Denominator);
    public static ExactRational operator -(ExactRational a, ExactRational b) =>
        Create(a.Numerator * b.Denominator - b.Numerator * a.Denominator, a.Denominator * b.Denominator);
    public static ExactRational operator *(ExactRational a, ExactRational b) =>
        Create(a.Numerator * b.Numerator, a.Denominator * b.Denominator);
    public static bool operator <(ExactRational a, ExactRational b) => a.CompareTo(b) < 0;
    public static bool operator >(ExactRational a, ExactRational b) => a.CompareTo(b) > 0;
    public static bool operator <=(ExactRational a, ExactRational b) => a.CompareTo(b) <= 0;
    public static bool operator >=(ExactRational a, ExactRational b) => a.CompareTo(b) >= 0;
    public int CompareTo(ExactRational other) => (Numerator * other.Denominator).CompareTo(other.Numerator * Denominator);
    public override string ToString() => Denominator == BigInteger.One ? Numerator.ToString() : $"{Numerator}/{Denominator}";
}

public sealed record CertifiedUpperBoundResult(bool IsCertified, ExactRational UpperBound, string Reason);

public static class CertifiedCapacityUpperBound
{
    public static CertifiedUpperBoundResult Calculate(
        IReadOnlyList<IReadOnlyList<ExactRational>> matrix,
        IReadOnlyList<ExactRational> rightHandSide,
        IReadOnlyList<ExactRational> objective,
        IReadOnlyList<ExactRational> lowerBounds,
        IReadOnlyList<ExactRational> upperBounds,
        IReadOnlyList<ExactRational> dualMultipliers,
        ExactRational? constant = null)
    {
        int rows = matrix.Count;
        int columns = objective.Count;
        if (rightHandSide.Count != rows || dualMultipliers.Count != rows ||
            lowerBounds.Count != columns || upperBounds.Count != columns ||
            matrix.Any(row => row.Count != columns) ||
            dualMultipliers.Any(y => y < ExactRational.Zero) ||
            Enumerable.Range(0, columns).Any(j => lowerBounds[j] > upperBounds[j]))
            return new(false, ExactRational.Zero, "invalid dimensions, bounds, or dual sign");

        ExactRational upper = constant ?? ExactRational.Zero;
        for (int i = 0; i < rows; i++) upper += rightHandSide[i] * dualMultipliers[i];
        for (int j = 0; j < columns; j++)
        {
            ExactRational residual = objective[j];
            for (int i = 0; i < rows; i++) residual -= matrix[i][j] * dualMultipliers[i];
            ExactRational atLower = residual * lowerBounds[j];
            ExactRational atUpper = residual * upperBounds[j];
            upper += atLower >= atUpper ? atLower : atUpper;
        }
        return new(true, upper, "exact rational residual-envelope certificate");
    }
}

public sealed record ReopenableCertificate(string CertificateId, int ReleasedStopCount, int EstimatedBinaryCost);

public sealed record CapacityDeficitClause(string RuleId, string AlphaFingerprint, IReadOnlyList<string> CertificateIds)
{
    public bool AppliesTo(string alphaFingerprint) =>
        string.Equals(AlphaFingerprint, alphaFingerprint, StringComparison.Ordinal);
}

public sealed record MicroReopeningPlan(IReadOnlySet<string> ReleasedCertificateIds, int EstimatedBinaryCost);

public static class MicroReopeningPlanner
{
    public static MicroReopeningPlan Select(
        IReadOnlyList<ReopenableCertificate> certificates,
        IReadOnlyList<CapacityDeficitClause> clauses,
        string alphaFingerprint)
    {
        ReopenableCertificate[] ordered = certificates.OrderBy(c => c.CertificateId, StringComparer.Ordinal).ToArray();
        CapacityDeficitClause[] active = clauses.Where(c => c.AppliesTo(alphaFingerprint)).ToArray();
        HashSet<string>? best = null;
        int bestCost = int.MaxValue;
        if (ordered.Length >= 63) throw new ArgumentOutOfRangeException(nameof(certificates), "exact selector supports fewer than 63 certificates");
        long count = 1L << ordered.Length;
        for (long mask = 0; mask < count; mask++)
        {
            HashSet<string> chosen = [];
            int cost = 0;
            for (int i = 0; i < ordered.Length; i++)
                if ((mask & (1L << i)) != 0) { chosen.Add(ordered[i].CertificateId); cost += ordered[i].EstimatedBinaryCost; }
            if (cost > bestCost || active.Any(clause => !clause.CertificateIds.Any(chosen.Contains))) continue;
            if (cost < bestCost || best is null || string.Join('|', chosen.Order()) is string key &&
                string.CompareOrdinal(key, string.Join('|', best.Order())) < 0)
            { best = chosen; bestCost = cost; }
        }
        return new(best ?? new HashSet<string>(), bestCost == int.MaxValue ? 0 : bestCost);
    }
}

public sealed record ServiceCapacityCandidate(string RequirementId, bool IsCurrentStop, double EarliestEventSeconds, double LatestEventSeconds);
public sealed record ServiceCapacityBound(int UpperBound, IReadOnlyList<string> EligibleRequirementIds);

public readonly record struct ServiceTimeWindowEnvelope(double StartSeconds, double EndSeconds, double ToleranceSeconds)
{
    public static ServiceTimeWindowEnvelope Create(double startSeconds, double endSeconds, double toleranceSeconds) =>
        new(startSeconds, endSeconds, Math.Max(0, toleranceSeconds));
    public bool MayContribute(double earliest, double latest) =>
        latest >= StartSeconds - ToleranceSeconds && earliest <= EndSeconds + ToleranceSeconds;
}

public enum CapacityAnalysisStatus
{
    CertifiedSufficient,
    CertifiedDeficit,
    RestrictedDomainServiceBlocked,
    Uncertified
}

public sealed record CapacityAnalysisResult(
    string RuleId,
    int RequiredLowerBound,
    double? CertifiedUpperBound,
    CapacityAnalysisStatus Status,
    CapacityDeficitClause? Clause,
    string Evidence);

public static class ServiceCapacityRelaxationBuilder
{
    public static ServiceCapacityBound CheapUpperBound(
        IReadOnlyList<ServiceCapacityCandidate> candidates,
        double startSeconds,
        double endSeconds,
        double toleranceSeconds)
    {
        ServiceTimeWindowEnvelope window = ServiceTimeWindowEnvelope.Create(startSeconds, endSeconds, toleranceSeconds);
        string[] eligible = candidates.Where(c => window.MayContribute(c.EarliestEventSeconds, c.LatestEventSeconds))
            .Select(c => c.RequirementId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return new(eligible.Length, eligible);
    }

    public static CapacityAnalysisResult FromLpObservation(
        string ruleId, int requiredLowerBound, double primalObjective,
        double? certifiedUpperBound, IReadOnlyList<string> retainedCertificateIds,
        string alphaFingerprint) => certifiedUpperBound is double bound
            ? FromCertifiedUpperBound(ruleId, requiredLowerBound, bound, retainedCertificateIds, alphaFingerprint)
            : new(ruleId, requiredLowerBound, null, CapacityAnalysisStatus.Uncertified, null,
                $"LP primal objective {primalObjective:R} is not an upper-bound certificate");

    public static CapacityAnalysisResult FromCertifiedUpperBound(
        string ruleId, int requiredLowerBound, double certifiedUpperBound,
        IReadOnlyList<string> retainedCertificateIds, string alphaFingerprint)
    {
        if (requiredLowerBound <= 0)
            return new(ruleId, requiredLowerBound, certifiedUpperBound, CapacityAnalysisStatus.CertifiedSufficient, null,
                "lower bound is zero");
        if (certifiedUpperBound + 1e-9 >= requiredLowerBound)
            return new(ruleId, requiredLowerBound, certifiedUpperBound, CapacityAnalysisStatus.CertifiedSufficient, null,
                "certified upper bound reaches the service lower bound");
        if (retainedCertificateIds.Count == 0)
            return new(ruleId, requiredLowerBound, certifiedUpperBound, CapacityAnalysisStatus.RestrictedDomainServiceBlocked, null,
                "deficit exists but no retained certificate can form a safe reopening disjunction");
        CapacityDeficitClause clause = new(ruleId, alphaFingerprint,
            retainedCertificateIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
        return new(ruleId, requiredLowerBound, certifiedUpperBound, CapacityAnalysisStatus.CertifiedDeficit, clause,
            "certified restricted-domain deficit");
    }
}
