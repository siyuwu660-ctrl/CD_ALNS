using System.Globalization;
using System.Text;
using Gurobi;

namespace GurobiTimetablingResearch.Optimization;

public sealed record GurobiModelIdentityEntry(
    string BusinessId,
    string SymbolName,
    int ModelIndex,
    string SymbolKind,
    string ConstructionSource);

/// <summary>
/// 保存模型存活期间稳定业务标识到实际 Gurobi 对象的映射，避免按名称再次查询时命中错误的同名对象。
/// </summary>
public sealed record GurobiModelIdentityAuditSnapshot(
    IReadOnlyDictionary<string, GRBVar> VariablesByBusinessId,
    IReadOnlyDictionary<string, GRBConstr> ConstraintsByBusinessId,
    IReadOnlyList<GurobiModelIdentityEntry> VariableEntries,
    IReadOnlyList<GurobiModelIdentityEntry> ConstraintEntries);

/// <summary>
/// 在模型更新后检查最终名称长度和唯一性，并以最终名称作为跨模型可重复生成的稳定业务标识。
/// </summary>
public static class GurobiModelIdentityAuditor
{
    public const int MaximumSymbolNameLength = 255;

    public static GurobiModelIdentityAuditSnapshot ValidateAndCapture(
        GRBModel model,
        string constructionPhase,
        string? failureReportPath = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(constructionPhase);
        model.Update();

        GRBVar[] variables = model.GetVars();
        GRBConstr[] constraints = model.GetConstrs();
        IReadOnlyList<GurobiModelIdentityEntry> variableEntries = variables
            .Select((variable, index) => BuildEntry(
                variable.Get(GRB.StringAttr.VarName), index, "variable", constructionPhase))
            .ToList();
        IReadOnlyList<GurobiModelIdentityEntry> constraintEntries = constraints
            .Select((constraint, index) => BuildEntry(
                constraint.Get(GRB.StringAttr.ConstrName), index, "constraint", constructionPhase))
            .ToList();

        List<string> violations = [];
        CollectViolations(variableEntries, violations);
        CollectViolations(constraintEntries, violations);
        if (violations.Count > 0)
        {
            string detail = string.Join(Environment.NewLine, violations);
            if (!string.IsNullOrWhiteSpace(failureReportPath))
            {
                string fullPath = Path.GetFullPath(failureReportPath);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                File.WriteAllText(fullPath, detail, new UTF8Encoding(false));
            }
            throw new InvalidDataException(
                $"Gurobi model identity audit failed before Optimize/IIS in phase '{constructionPhase}'." +
                Environment.NewLine + detail);
        }

        return new GurobiModelIdentityAuditSnapshot(
            variables.ToDictionary(
                variable => variable.Get(GRB.StringAttr.VarName),
                StringComparer.OrdinalIgnoreCase),
            constraints.ToDictionary(
                constraint => constraint.Get(GRB.StringAttr.ConstrName),
                StringComparer.OrdinalIgnoreCase),
            variableEntries,
            constraintEntries);
    }

    public static void WriteMapping(
        GurobiModelIdentityAuditSnapshot snapshot,
        string variablePath,
        string constraintPath)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        WriteEntries(snapshot.VariableEntries, variablePath);
        WriteEntries(snapshot.ConstraintEntries, constraintPath);
    }

    private static GurobiModelIdentityEntry BuildEntry(
        string name,
        int index,
        string kind,
        string constructionPhase) => new(
        name,
        name,
        index,
        kind,
        ResolveConstructionSource(name, constructionPhase));

    private static void CollectViolations(
        IReadOnlyList<GurobiModelIdentityEntry> entries,
        ICollection<string> violations)
    {
        foreach (GurobiModelIdentityEntry entry in entries.Where(entry =>
                     string.IsNullOrWhiteSpace(entry.SymbolName) ||
                     entry.SymbolName.Length > MaximumSymbolNameLength))
        {
            violations.Add(
                $"invalid_name kind={entry.SymbolKind};index={entry.ModelIndex};length={entry.SymbolName.Length};" +
                $"name={entry.SymbolName};source={entry.ConstructionSource}");
        }

        foreach (IGrouping<string, GurobiModelIdentityEntry> duplicate in entries
                     .Where(entry => !string.IsNullOrWhiteSpace(entry.SymbolName))
                     .GroupBy(entry => entry.SymbolName, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
        {
            violations.Add(
                $"duplicate_name kind={duplicate.First().SymbolKind};name={duplicate.Key};objects=[" +
                string.Join('|', duplicate.Select(entry =>
                    $"index={entry.ModelIndex};business_id={entry.BusinessId};source={entry.ConstructionSource}")) +
                "]");
        }
    }

    private static string ResolveConstructionSource(string name, string phase)
    {
        if (name.StartsWith("pair_overtake_", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("being_overtaken_", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("max_dwell_", StringComparison.OrdinalIgnoreCase))
        {
            return "OvertakingDwellConstraintBuilder";
        }
        if (name.StartsWith("promoted_", StringComparison.OrdinalIgnoreCase))
        {
            return "StationMicroRepairModelAugmenter";
        }
        if (name.StartsWith("tr_", StringComparison.OrdinalIgnoreCase))
        {
            return "BasicMilpModelBuilder.PrecedenceTrustRegion";
        }
        return $"BasicMilpModelBuilder::{phase}";
    }

    private static void WriteEntries(IEnumerable<GurobiModelIdentityEntry> entries, string path)
    {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        using StreamWriter writer = new(fullPath, false, new UTF8Encoding(false));
        writer.WriteLine("business_id,symbol_name,model_index,symbol_kind,construction_source");
        foreach (GurobiModelIdentityEntry entry in entries)
        {
            writer.WriteLine(string.Join(',',
                Csv(entry.BusinessId),
                Csv(entry.SymbolName),
                entry.ModelIndex.ToString(CultureInfo.InvariantCulture),
                entry.SymbolKind,
                entry.ConstructionSource));
        }
    }

    private static string Csv(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) < 0
            ? value
            : $"\"{value.Replace("\"", "\"\"")}\"";
}
