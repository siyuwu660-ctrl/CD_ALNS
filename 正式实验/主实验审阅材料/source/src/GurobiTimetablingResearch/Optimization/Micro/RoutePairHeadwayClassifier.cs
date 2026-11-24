// 径路对分类器比较两条径路占用的端点、股道和咽喉资源，将组合划分为明确冲突、明确兼容或信息不足。
// 分类结果携带判定依据，供安全间隔解析器决定采用专用规则、保守回退还是拒绝建模。
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

internal enum RoutePairHeadwayClassificationKind
{
    NoConstraint,
    RouteChoiceRequired,
    UnavoidableConflict,
    Satisfied
}

internal sealed record RoutePairHeadwayClassification(
    RoutePairHeadwayClassificationKind Kind,
    int RecordedPairCount,
    int MissingPairCount,
    int? MinimumRecordedHeadwaySeconds);

internal static class RoutePairHeadwayClassifier
{
    private const double ToleranceSeconds = GurobiTimetablingResearch.Domain.TimetableTimePolicy.NumericToleranceSeconds;

    internal static RoutePairHeadwayClassification Classify(
        IReadOnlyList<RouteRecord> leadingRoutes,
        IReadOnlyList<RouteRecord> followingRoutes,
        double realizedGapSeconds,
        Func<int, int, int?> findRecordedHeadway)
    {
        if (Math.Abs(realizedGapSeconds) <= ToleranceSeconds)
        {
            return ClassifyEqualTime(leadingRoutes, followingRoutes, findRecordedHeadway);
        }

        int recordedCount = 0;
        int missingCount = 0;
        int violatedRecordedCount = 0;
        int? minimum = null;

        foreach (RouteRecord leadingRoute in leadingRoutes)
        {
            foreach (RouteRecord followingRoute in followingRoutes)
            {
                int? headway = findRecordedHeadway(leadingRoute.RouteId, followingRoute.RouteId);
                if (!headway.HasValue)
                {
                    missingCount++;
                    continue;
                }

                recordedCount++;
                minimum = minimum.HasValue ? Math.Min(minimum.Value, headway.Value) : headway.Value;
                if (realizedGapSeconds + ToleranceSeconds < headway.Value)
                {
                    violatedRecordedCount++;
                }
            }
        }

        RoutePairHeadwayClassificationKind kind = recordedCount == 0
            ? RoutePairHeadwayClassificationKind.NoConstraint
            : violatedRecordedCount == 0
                ? RoutePairHeadwayClassificationKind.Satisfied
                : missingCount > 0 || violatedRecordedCount < recordedCount
                ? RoutePairHeadwayClassificationKind.RouteChoiceRequired
                : RoutePairHeadwayClassificationKind.UnavoidableConflict;

        return new RoutePairHeadwayClassification(kind, recordedCount, missingCount, minimum);
    }

    private static RoutePairHeadwayClassification ClassifyEqualTime(
        IReadOnlyList<RouteRecord> firstRoutes,
        IReadOnlyList<RouteRecord> secondRoutes,
        Func<int, int, int?> findRecordedHeadway)
    {
        int recordedCount = 0;
        int missingCount = 0;
        int? minimum = null;
        int unconstrainedOutcomes = 0;
        int explicitlySatisfiedOutcomes = 0;
        int flexibleOutcomes = 0;
        int infeasibleOutcomes = 0;

        foreach (RouteRecord firstRoute in firstRoutes)
        {
            foreach (RouteRecord secondRoute in secondRoutes)
            {
                int? forward = findRecordedHeadway(firstRoute.RouteId, secondRoute.RouteId);
                int? reverse = findRecordedHeadway(secondRoute.RouteId, firstRoute.RouteId);

                foreach (int? value in new[] { forward, reverse })
                {
                    if (value.HasValue)
                    {
                        recordedCount++;
                        minimum = minimum.HasValue ? Math.Min(minimum.Value, value.Value) : value.Value;
                    }
                    else
                    {
                        missingCount++;
                    }
                }

                if (!forward.HasValue && !reverse.HasValue)
                {
                    unconstrainedOutcomes++;
                }
                else if ((forward.HasValue && forward.Value <= ToleranceSeconds) ||
                         (reverse.HasValue && reverse.Value <= ToleranceSeconds))
                {
                    explicitlySatisfiedOutcomes++;
                }
                else if (!forward.HasValue || !reverse.HasValue)
                {
                    flexibleOutcomes++;
                }
                else
                {
                    infeasibleOutcomes++;
                }
            }
        }

        int totalOutcomes = firstRoutes.Count * secondRoutes.Count;
        RoutePairHeadwayClassificationKind kind = unconstrainedOutcomes == totalOutcomes
            ? RoutePairHeadwayClassificationKind.NoConstraint
            : explicitlySatisfiedOutcomes == totalOutcomes
                ? RoutePairHeadwayClassificationKind.Satisfied
                : infeasibleOutcomes == totalOutcomes
                    ? RoutePairHeadwayClassificationKind.UnavoidableConflict
                    : RoutePairHeadwayClassificationKind.RouteChoiceRequired;

        return new RoutePairHeadwayClassification(kind, recordedCount, missingCount, minimum);
    }
}
