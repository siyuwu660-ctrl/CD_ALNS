using Gurobi;

namespace GurobiTimetablingResearch.Optimization;

internal readonly record struct OrderedStopPairState(
    GRBVar FirstStop,
    GRBVar SecondStop,
    GRBVar SharedProduct)
{
    internal GRBLinExpr Weighted(double ss, double sp, double ps, double pp)
    {
        GRBLinExpr expression = new();
        expression.AddConstant(pp);
        expression.AddTerm(sp - pp, FirstStop);
        expression.AddTerm(ps - pp, SecondStop);
        expression.AddTerm(ss - sp - ps + pp, SharedProduct);
        return expression;
    }
}

public sealed record StopPairProductBinding(
    string FirstStopId, string SecondStopId, GRBVar ProductVariable);

internal sealed class StopPairProductRegistry
{
    private readonly GRBModel _model;
    private readonly IReadOnlyDictionary<string, GRBVar> _stopVars;
    private readonly Dictionary<(string First, string Second), GRBVar> _products = new(new PairComparer());

    internal StopPairProductRegistry(GRBModel model, IReadOnlyDictionary<string, GRBVar> stopVars)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _stopVars = stopVars ?? throw new ArgumentNullException(nameof(stopVars));
    }

    internal int Count => _products.Count;
    internal IReadOnlyList<StopPairProductBinding> Bindings => _products.Select(item =>
        new StopPairProductBinding(item.Key.First, item.Key.Second, item.Value)).ToArray();

    internal OrderedStopPairState GetOrdered(string firstStopId, string secondStopId)
    {
        if (!_stopVars.TryGetValue(firstStopId, out GRBVar? firstStop))
            throw new KeyNotFoundException($"Unknown first StopId '{firstStopId}'.");
        if (!_stopVars.TryGetValue(secondStopId, out GRBVar? secondStop))
            throw new KeyNotFoundException($"Unknown second StopId '{secondStopId}'.");
        if (string.Equals(firstStopId, secondStopId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"A stop-product pair requires distinct StopIds: '{firstStopId}'.");

        (string First, string Second) key = StringComparer.OrdinalIgnoreCase.Compare(firstStopId, secondStopId) <= 0
            ? (firstStopId, secondStopId)
            : (secondStopId, firstStopId);
        if (!_products.TryGetValue(key, out GRBVar? product))
        {
            string suffix = $"{Sanitize(key.First)}__{Sanitize(key.Second)}";
            product = _model.AddVar(0.0, 1.0, 0.0, GRB.CONTINUOUS, $"stop_product_{suffix}");
            _model.AddConstr(product - _stopVars[key.First], GRB.LESS_EQUAL, 0.0, $"stop_product_first_ub_{suffix}");
            _model.AddConstr(product - _stopVars[key.Second], GRB.LESS_EQUAL, 0.0, $"stop_product_second_ub_{suffix}");
            _model.AddConstr(product - _stopVars[key.First] - _stopVars[key.Second], GRB.GREATER_EQUAL, -1.0, $"stop_product_lb_{suffix}");
            _products.Add(key, product);
        }
        return new OrderedStopPairState(firstStop, secondStop, product);
    }

    private static string Sanitize(string value) =>
        new(value.Select(character => char.IsLetterOrDigit(character) ? character : '_').ToArray());

    private sealed class PairComparer : IEqualityComparer<(string First, string Second)>
    {
        public bool Equals((string First, string Second) x, (string First, string Second) y) =>
            string.Equals(x.First, y.First, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Second, y.Second, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string First, string Second) obj) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.First),
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Second));
    }
}
