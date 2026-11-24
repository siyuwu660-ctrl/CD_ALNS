using Gurobi;

namespace GurobiTimetablingResearch.Optimization;

public static class ServiceWindowModelBuilder
{
    public static void Apply(GRBModel model, IReadOnlyDictionary<string,GRBVar> arrivals,
        IReadOnlyDictionary<string,GRBVar> departures, ServiceWindowPolicy policy)
    {
        policy.Validate();
        if (policy.Windows.Count == 0) return;
        GRBLinExpr penalty = new();
        int index = 0;
        foreach (ServiceWindow w in policy.Windows)
        {
            GRBVar time = (w.Side == TimetableEventSide.Arrival ? arrivals : departures)[w.StopId];
            GRBVar early = model.AddVar(0,GRB.INFINITY,0,GRB.CONTINUOUS,$"service_early_{index}");
            GRBVar late = model.AddVar(0,GRB.INFINITY,0,GRB.CONTINUOUS,$"service_late_{index}");
            model.AddConstr(time+early,GRB.GREATER_EQUAL,w.Lower,$"service_lower_{index}");
            model.AddConstr(time-late,GRB.LESS_EQUAL,w.Upper,$"service_upper_{index}");
            penalty.AddTerm(w.EarlyWeight,early);
            penalty.AddTerm(w.LateWeight,late);
            index++;
        }
        // Existing objectives occupy slots 0/1 with priorities 2/1. Append, do not overwrite them.
        model.Update();
        int slot = model.NumObj;
        // A fresh standalone GRBModel has one implicit zero objective.
        if (slot == 1 && arrivals.Count == 0) slot = 0;
        model.SetObjectiveN(penalty,slot,3,1,0,0,"service_window_penalty");
    }
}
