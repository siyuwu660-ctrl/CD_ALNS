namespace GurobiTimetablingResearch.Domain;

/// <summary>No business-time relaxation. Epsilon only absorbs floating-point solver residuals.</summary>
public static class TimetableTimePolicy
{
    public const double NumericToleranceSeconds = 1e-6;
    public const int BusinessRelaxationSeconds = 0;
}

/// <summary>Passing is a macro event class; its incoming/outgoing route identities remain distinct.</summary>
public static class RouteEventDirectionPolicy
{
    public static bool Matches(RouteRecord route, bool incoming) => route.MovementEvent switch
    {
        MovementEvent.Arrival => incoming,
        MovementEvent.Departure => !incoming,
        MovementEvent.Passing => route.OperationType.Trim() switch
        {
            "通过接车" or "pass-arrival" => incoming,
            "通过发车" or "pass-departure" => !incoming,
            _ => false // Unknown passing direction must not silently enter both domains.
        },
        _ => false
    };
}
