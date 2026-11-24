namespace GurobiTimetablingResearch.Optimization;

public sealed record ConditionalBigMAuditRow(
    string ConstraintName,
    double Value,
    string Formula,
    IReadOnlyList<string> BoundSources,
    bool UsedLegacyFallback = false);

public static class ConditionalBigMCalculator
{
    public static double ForLowerBound(
        HardEventBound earlier,
        HardEventBound later,
        double maximumRequiredSeparation)
    {
        Validate(earlier);
        Validate(later);
        if (!double.IsFinite(maximumRequiredSeparation))
            throw new ArgumentOutOfRangeException(nameof(maximumRequiredSeparation));
        return Math.Max(0.0, maximumRequiredSeparation + earlier.Upper - later.Lower);
    }

    public static double RelaxUpperBound(HardEventBound value, double activeUpper)
    {
        Validate(value);
        if (!double.IsFinite(activeUpper)) throw new ArgumentOutOfRangeException(nameof(activeUpper));
        return Math.Max(0.0, value.Upper - activeUpper);
    }

    public static double RelaxLowerBound(HardEventBound value, double activeLower)
    {
        Validate(value);
        if (!double.IsFinite(activeLower)) throw new ArgumentOutOfRangeException(nameof(activeLower));
        return Math.Max(0.0, activeLower - value.Lower);
    }

    private static void Validate(HardEventBound value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!double.IsFinite(value.Lower) || !double.IsFinite(value.Upper) || value.Lower > value.Upper)
            throw new ArgumentOutOfRangeException(nameof(value),
                $"A finite consistent event bound is required, got [{value.Lower}, {value.Upper}].");
    }
}
