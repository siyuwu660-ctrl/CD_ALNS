using Gurobi;

namespace GurobiTimetablingResearch.Optimization;

public sealed record MaintenanceWindowBinaryBinding(
    string BindingId,
    string SectionStartStopId,
    string SectionEndStopId,
    double WindowStartSeconds,
    double WindowEndSeconds,
    GRBVar BeforeWindowVariable);

public sealed record StationQuotaCategoryBinding(
    string BindingId,
    string RuleId,
    string StopId,
    double WindowStartSeconds,
    double WindowEndSeconds,
    GRBVar InWindowVariable,
    GRBVar LeftOutsideVariable,
    GRBVar RightOutsideVariable);
