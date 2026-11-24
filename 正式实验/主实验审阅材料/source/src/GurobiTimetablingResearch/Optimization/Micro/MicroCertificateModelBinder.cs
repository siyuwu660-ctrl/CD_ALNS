using Gurobi;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

public sealed record MicroCertificateBindingResult(
    IReadOnlyList<string> AddedConstraintIds,
    IReadOnlyList<string> ProtectedRequirementIds,
    IReadOnlyList<string> CertificateIds);

public static class MicroCertificateModelBinder
{
    public static MicroCertificateBindingResult Apply(
        BasicMilpModel model,
        IReadOnlyList<MicroWitnessCertificate> retainedCertificates)
    {
        ArgumentNullException.ThrowIfNull(model);
        List<string> added = [];
        foreach (MicroWitnessCertificate certificate in retainedCertificates)
        foreach ((MicroCertificateCondition condition, int index) in certificate.Conditions.Select((value, i) => (value, i)))
        {
            string name = BuildConstraintName(certificate.CertificateId, index);
            if (condition.Kind == MicroCertificateConditionKind.StopMode)
            {
                model.Model.AddConstr(
                    model.StopVars[condition.LaterStopId], GRB.EQUAL,
                    condition.RequiredStopValue!.Value, name);
            }
            else
            {
                GRBLinExpr expression = ResolveEvent(model, condition.LaterStopId, condition.LaterEvent) +
                                            condition.LaterOffsetSeconds -
                                        ResolveEvent(model, condition.EarlierStopId!, condition.EarlierEvent!.Value) -
                                            condition.EarlierOffsetSeconds;
                model.Model.AddConstr(expression, GRB.GREATER_EQUAL, condition.MinimumDifferenceSeconds, name);
            }
            added.Add(name);
        }
        model.Model.Update();
        foreach (string id in added) model.MicroCertificateConstraintIds.Add(id);
        foreach (string id in retainedCertificates.Select(c => c.CertificateId))
            model.RetainedMicroCertificateIds.Add(id);
        return new(
            added,
            retainedCertificates.SelectMany(c => c.Witnesses).Select(w => w.RequirementId)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            retainedCertificates.Select(c => c.CertificateId).ToArray());
    }

    private static GRBVar ResolveEvent(BasicMilpModel model, string stopId, StationEventSide side) =>
        side == StationEventSide.Incoming ? model.ArrivalVars[stopId] : model.DepartureVars[stopId];

    internal static string BuildConstraintName(string certificateId, int conditionIndex) =>
        $"micro_cert_{Sanitize(certificateId)}_{conditionIndex}";

    private static string Sanitize(string value) => new(value.Select(character =>
        char.IsLetterOrDigit(character) ? character : '_').Take(80).ToArray());
}
