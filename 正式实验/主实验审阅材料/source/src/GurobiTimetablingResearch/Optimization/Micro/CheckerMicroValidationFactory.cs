// 微观校验工厂用统一的端点占用策略创建可行性检查器及其依赖组件。
// 工厂返回组合 record，确保普通 Checker、只检查模式和候选试验共享完全一致的微观冲突判定口径。
namespace GurobiTimetablingResearch.Optimization;

internal sealed record CheckerMicroValidationComponents(
    MasterMicroFeasibilityChecker Checker,
    StationLevelMicroFeasibilitySlave Slave);

internal static class CheckerMicroValidationFactory
{
    internal static CheckerMicroValidationComponents Create(int endpointOccupationSeconds) => new(
        CreateChecker(endpointOccupationSeconds),
        CreateSlave(endpointOccupationSeconds));

    internal static MasterMicroFeasibilityChecker CreateChecker(int endpointOccupationSeconds) =>
        new(endpointOccupationSeconds);

    internal static StationLevelMicroFeasibilitySlave CreateSlave(int endpointOccupationSeconds) =>
        new(endpointOccupationSeconds);
}
