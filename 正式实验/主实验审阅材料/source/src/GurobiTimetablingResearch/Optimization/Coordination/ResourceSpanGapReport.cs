namespace GurobiTimetablingResearch.Optimization;

internal sealed record ResourceSpanGapReport(double? UpperBoundSeconds,double? LowerBoundSeconds,
    double? AbsoluteGapSeconds,double? RelativeGap,double? GapPercent,double TargetRelativeGap,bool TargetMet,string Status)
{
    internal static ResourceSpanGapReport Calculate(double? upper,double? lower,double target)
    {
        if(!double.IsFinite(target) || target<0 || target>=1) throw new ArgumentOutOfRangeException(nameof(target));
        if(upper is double u && (!double.IsFinite(u) || u<0) ||
            lower is double l && (!double.IsFinite(l) || l<0)) throw new ArgumentException("Bounds must be finite and nonnegative.");
        if(upper is null || lower is null) return new(upper,lower,null,null,null,target,false,
            upper is null?"NoVerifiedJointUpperBound":"NoGlobalLowerBound");
        if(lower>upper) return new(upper,lower,null,null,null,target,false,"InconsistentBounds");
        double absolute=upper.Value-lower.Value,relative=upper==0?0:absolute/Math.Abs(upper.Value);
        return new(upper,lower,absolute,relative,relative*100,target,relative<=target,"ValidGlobalSpanBounds");
    }
}
