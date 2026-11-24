namespace GurobiTimetablingResearch.Optimization;

/// <summary>
/// Closed feasible interval of one quota-category literal after intersecting the
/// category semantics with the original event bounds.
/// </summary>
internal readonly record struct PatternTimeCategoryInterval(double LowerBound, double UpperBound)
{
    public bool IsEmpty => LowerBound > UpperBound;

    public bool Intersects(PatternTimeCategoryInterval other) =>
        !IsEmpty && !other.IsEmpty && Math.Max(LowerBound, other.LowerBound) <=
        Math.Min(UpperBound, other.UpperBound);
}

internal static class PatternTimeCategoryCompatibility
{
    public static PatternTimeCategoryInterval Resolve(
        double originalLowerBound,
        double originalUpperBound,
        MacroQuotaCategory category,
        double windowStart,
        double windowEnd)
    {
        if (originalLowerBound > originalUpperBound)
            throw new ArgumentOutOfRangeException(nameof(originalLowerBound));
        if (windowStart > windowEnd)
            throw new ArgumentOutOfRangeException(nameof(windowStart));

        (double categoryLower, double categoryUpper) = category switch
        {
            MacroQuotaCategory.InWindow => (windowStart, windowEnd),
            MacroQuotaCategory.LeftOutside => (double.NegativeInfinity, windowStart),
            MacroQuotaCategory.RightOutside => (windowEnd, double.PositiveInfinity),
            _ => throw new ArgumentOutOfRangeException(nameof(category), category, null)
        };
        return new(
            Math.Max(originalLowerBound, categoryLower),
            Math.Min(originalUpperBound, categoryUpper));
    }
}
