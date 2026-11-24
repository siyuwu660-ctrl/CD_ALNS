using Gurobi;

namespace GurobiTimetablingResearch.Optimization;

internal sealed record ResourceInvalidSupplement(string Variable,string Value);
internal sealed record ResourceNumericalAudit(double? ConstraintViolation,double? BoundViolation,double? IntegerViolation,
    IReadOnlyList<ResourceInvalidSupplement> InvalidSupplements)
{
    internal const double IntegerTolerance=1e-8;
    public bool Passed=>Within(ConstraintViolation,SolverNumericalValueNormalizer.BoundTolerance) &&
        Within(BoundViolation,SolverNumericalValueNormalizer.BoundTolerance) && Within(IntegerViolation,IntegerTolerance) && InvalidSupplements.Count==0;

    internal static ResourceNumericalAudit Read(GRBModel model,ResourceMacroTemplate template,ResourceCompiledModel local)
        =>model.SolCount==0?new(null,null,null,[]):new(Finite(model.ConstrVio),Finite(model.BoundVio),Finite(model.IntVio),template.InvalidSupplements(local));

    private static double? Finite(double value)=>double.IsFinite(value)?value:null;
    private static bool Within(double? value,double tolerance)=>value is double v && double.IsFinite(v) && v>=0 && v<=tolerance;
}
