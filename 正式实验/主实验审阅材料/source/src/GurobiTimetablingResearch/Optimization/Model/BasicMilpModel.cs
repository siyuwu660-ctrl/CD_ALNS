// BasicMilpModel 封装 Gurobi 模型及按业务键索引的到发时刻、停站、径路和先后关系变量，并统一管理释放资源。
// 求解器、校验器和导出器通过该容器共享同一变量集合，避免各模块重新按变量名扫描底层模型。
using Gurobi;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

/// <summary>
/// 对 Gurobi environment/model 的一个轻量可释放封装。
/// 明确资源所有权有两个好处：
/// 1. 以后扩展到 master/slave/Benders 时更容易管理生命周期。
/// 2. 能避免 environment/model 隐式释放导致的隐藏错误。
/// </summary>
public sealed class BasicMilpModel : IDisposable
{
    public BasicMilpModel(
        GRBEnv environment,
        GRBModel model,
        TimetablingInstance instance,
        IReadOnlyDictionary<string, GRBVar> arrivalVars,
        IReadOnlyDictionary<string, GRBVar> departureVars,
        IReadOnlyDictionary<string, GRBVar> stopVars,
        IReadOnlyList<SectionConflictCandidate> sectionConflictCandidates,
        IReadOnlyDictionary<string, GRBVar> sectionPrecedenceVars,
        IReadOnlyList<StationEventConflictCandidate> stationEventConflictCandidates,
        IReadOnlyDictionary<string, GRBVar> stationEventPrecedenceVars,
        IReadOnlyDictionary<string, GRBVar>? runningTimeSupplementVars = null,
        double runningTimeSupplementRatio = 0.0,
        IReadOnlyDictionary<string, GRBVar>? beingOvertakenVars = null,
        IReadOnlyList<PromotedMicroComponent>? promotedMicroComponents = null,
        IReadOnlyList<StopPairProductBinding>? stopPairProductBindings = null,
        IReadOnlyList<OvertakingReversalVariableBinding>? overtakingReversalBindings = null,
        IReadOnlyList<MaintenanceWindowBinaryBinding>? maintenanceWindowBinaryBindings = null,
        IReadOnlyList<StationQuotaCategoryBinding>? stationQuotaCategoryBindings = null,
        HardEventBoundSet? hardEventBounds = null,
        IReadOnlyList<ConditionalBigMAuditRow>? conditionalBigMAuditRows = null)
    {
        Environment = environment;
        Model = model;
        Instance = instance;
        ArrivalVars = arrivalVars;
        DepartureVars = departureVars;
        StopVars = stopVars;
        SectionConflictCandidates = sectionConflictCandidates;
        SectionPrecedenceVars = sectionPrecedenceVars;
        StationEventConflictCandidates = stationEventConflictCandidates;
        StationEventPrecedenceVars = stationEventPrecedenceVars;
        RunningTimeSupplementVars = runningTimeSupplementVars ?? new Dictionary<string, GRBVar>();
        RunningTimeSupplementRatio = runningTimeSupplementRatio;
        BeingOvertakenVars = beingOvertakenVars ?? new Dictionary<string, GRBVar>();
        PromotedMicroComponents = promotedMicroComponents ?? Array.Empty<PromotedMicroComponent>();
        StopPairProductBindings = stopPairProductBindings ?? Array.Empty<StopPairProductBinding>();
        OvertakingReversalBindings = overtakingReversalBindings ?? Array.Empty<OvertakingReversalVariableBinding>();
        MaintenanceWindowBinaryBindings = maintenanceWindowBinaryBindings ?? Array.Empty<MaintenanceWindowBinaryBinding>();
        StationQuotaCategoryBindings = stationQuotaCategoryBindings ?? Array.Empty<StationQuotaCategoryBinding>();
        HardEventBounds = hardEventBounds ?? HardEventBoundSet.Empty;
        ConditionalBigMAuditRows = conditionalBigMAuditRows ?? Array.Empty<ConditionalBigMAuditRow>();
    }

    public GRBEnv Environment { get; }
    public GRBModel Model { get; }
    public TimetablingInstance Instance { get; }
    public IReadOnlyDictionary<string, GRBVar> ArrivalVars { get; }
    public IReadOnlyDictionary<string, GRBVar> DepartureVars { get; }
    public IReadOnlyDictionary<string, GRBVar> StopVars { get; }
    public IReadOnlyList<SectionConflictCandidate> SectionConflictCandidates { get; }
    public IReadOnlyDictionary<string, GRBVar> SectionPrecedenceVars { get; }
    public IReadOnlyList<StationEventConflictCandidate> StationEventConflictCandidates { get; }
    public IReadOnlyDictionary<string, GRBVar> StationEventPrecedenceVars { get; }
    public IReadOnlyDictionary<string, GRBVar> RunningTimeSupplementVars { get; }
    public double RunningTimeSupplementRatio { get; }
    public IReadOnlyDictionary<string, GRBVar> BeingOvertakenVars { get; }
    public IReadOnlyList<PromotedMicroComponent> PromotedMicroComponents { get; }
    internal IReadOnlyList<StopPairProductBinding> StopPairProductBindings { get; }
    internal IReadOnlyList<OvertakingReversalVariableBinding> OvertakingReversalBindings { get; }
    internal IReadOnlyList<MaintenanceWindowBinaryBinding> MaintenanceWindowBinaryBindings { get; }
    internal IReadOnlyList<StationQuotaCategoryBinding> StationQuotaCategoryBindings { get; }
    public HardEventBoundSet HardEventBounds { get; }
    public ServiceWindowPolicy ServiceWindows { get; internal set; } = ServiceWindowPolicy.Disabled;
    public IReadOnlyList<TemporalPrecedenceAudit> TemporalPrecedenceAudit { get; internal set; } = [];
    public ServiceObjectiveVector? CurrentServiceObjective => HasIncumbentSolution
        ? MasterSolutionSnapshot.Capture(this).ServiceObjective
        : null;
    public IReadOnlyList<ConditionalBigMAuditRow> ConditionalBigMAuditRows { get; }
    public int PromotedMicroAlternativeVariableCount { get; internal set; }
    public int PromotedMicroOrderVariableCount { get; internal set; }
    public int PromotedMicroLegacyOrderVariableCount { get; internal set; }
    public int PromotedMicroBoundPrunedRelationCount { get; internal set; }
    public int PromotedMicroConditionalConstraintCount { get; internal set; }
    internal IList<PromotedMicroChoiceVariableBinding> PromotedMicroChoiceBindings { get; } =
        new List<PromotedMicroChoiceVariableBinding>();
    internal IList<PromotedMicroOrderVariableBinding> PromotedMicroOrderBindings { get; } =
        new List<PromotedMicroOrderVariableBinding>();
    public IList<PromotedMicroBuildDiagnostic> PromotedMicroBuildDiagnostics { get; } =
        new List<PromotedMicroBuildDiagnostic>();
    public IList<string> MicroCertificateConstraintIds { get; } = new List<string>();
    public IList<string> RetainedMicroCertificateIds { get; } = new List<string>();
    public GurobiModelIdentityAuditSnapshot? IdentityAudit { get; internal set; }
    public PrecedenceTrustRegion? AppliedPrecedenceTrustRegion { get; internal set; }
    public ValidityFirstTemporaryFixationAudit? TemporaryFixationAudit { get; internal set; }
    internal IList<StructuralTrialIisDiagnostic> StructuralTrialIisDiagnostics { get; } =
        new List<StructuralTrialIisDiagnostic>();
    internal IList<StructuralCandidateProbeDiagnostic> StructuralCandidateProbeDiagnostics { get; } =
        new List<StructuralCandidateProbeDiagnostic>();
    internal string StructuralCandidateProbeStopReason { get; set; } = string.Empty;
    internal IList<IisGuidedRepairTraceDiagnostic> IisGuidedRepairTraceDiagnostics { get; } =
        new List<IisGuidedRepairTraceDiagnostic>();
    internal IList<IisDependencyPlanDiagnostic> IisDependencyPlanDiagnostics { get; } =
        new List<IisDependencyPlanDiagnostic>();
    internal IList<RejectedTrialConflictDelta> RejectedTrialConflictDeltas { get; } =
        new List<RejectedTrialConflictDelta>();
    internal IList<StructuralSingletonGlobalTrialDiagnostic> StructuralSingletonGlobalTrialDiagnostics { get; } =
        new List<StructuralSingletonGlobalTrialDiagnostic>();

    public string StatusName => Model.Status.ToString();

    public bool HasIncumbentSolution => Model.SolCount > 0;

    public void Write(string path)
    {
        Model.Write(path);
    }

    public void Optimize()
    {
        IdentityAudit = GurobiModelIdentityAuditor.ValidateAndCapture(
            Model,
            "BasicMilpModel::before_optimize");
        Model.Optimize();
    }

    public void Dispose()
    {
        Model.Dispose();
        Environment.Dispose();
    }
}
