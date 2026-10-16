namespace GuitarPractice.Domain.Planning;

/// <summary>
/// Min-cost flow with convex per-arc costs, solved by successive shortest paths (one unit per augmentation).
/// Each arc carries a marginal cost function m(k) = cost of the k-th unit; m must be non-decreasing in k,
/// which keeps the residual graph free of negative cycles, so Bellman-Ford/SPFA is exact.
/// Graphs here are tiny (tasks + 7 days + 2 nodes), so O(units * V * E) is sub-millisecond in practice.
/// Fully deterministic: iteration order is insertion order.
/// </summary>
internal sealed class ConvexFlowNetwork
{
    private readonly List<int>[] _out;            // residual arc ids leaving each node
    private readonly List<int> _from = [];
    private readonly List<int> _to = [];
    private readonly List<int> _cap = [];
    private readonly List<int> _flow = [];
    private readonly List<Func<int, double>> _marginal = [];

    public ConvexFlowNetwork(int nodeCount)
    {
        _out = new List<int>[nodeCount];
        for (var i = 0; i < nodeCount; i++) _out[i] = [];
    }

    public int NodeCount => _out.Length;

    /// <returns>Edge id, usable with <see cref="Flow"/>.</returns>
    public int AddEdge(int from, int to, int capacity, Func<int, double>? marginalCost = null)
    {
        var id = _from.Count;
        _from.Add(from);
        _to.Add(to);
        _cap.Add(capacity);
        _flow.Add(0);
        _marginal.Add(marginalCost ?? (_ => 0d));
        _out[from].Add(2 * id);      // forward residual arc
        _out[to].Add(2 * id + 1);    // backward residual arc
        return id;
    }

    public int Flow(int edgeId) => _flow[edgeId];

    /// <summary>Pushes up to <paramref name="amount"/> units from source to sink at minimum total cost.</summary>
    /// <returns>Units actually pushed.</returns>
    public int Run(int source, int sink, int amount)
    {
        var pushed = 0;
        while (pushed < amount && TryAugmentOne(source, sink)) pushed++;
        return pushed;
    }

    private bool TryAugmentOne(int source, int sink)
    {
        var n = NodeCount;
        var dist = new double[n];
        var prevArc = new int[n];
        var inQueue = new bool[n];
        var relaxCount = new int[n];
        Array.Fill(dist, double.PositiveInfinity);
        Array.Fill(prevArc, -1);

        const double eps = 1e-9;
        var queue = new Queue<int>();
        dist[source] = 0;
        queue.Enqueue(source);
        inQueue[source] = true;

        while (queue.Count > 0)
        {
            var u = queue.Dequeue();
            inQueue[u] = false;
            foreach (var arc in _out[u])
            {
                if (ResidualCapacity(arc) <= 0) continue;
                var v = Head(arc);
                var nd = dist[u] + ArcCost(arc);
                if (nd >= dist[v] - eps) continue;

                dist[v] = nd;
                prevArc[v] = arc;
                if (inQueue[v]) continue;
                if (++relaxCount[v] > n)
                    throw new InvalidOperationException("Negative cycle in residual graph: marginal costs must be non-decreasing.");
                queue.Enqueue(v);
                inQueue[v] = true;
            }
        }

        if (double.IsPositiveInfinity(dist[sink])) return false;

        for (var v = sink; v != source;)
        {
            var arc = prevArc[v];
            var e = arc >> 1;
            if ((arc & 1) == 0) _flow[e]++; else _flow[e]--;
            v = Tail(arc);
        }
        return true;
    }

    private int ResidualCapacity(int arc)
    {
        var e = arc >> 1;
        return (arc & 1) == 0 ? _cap[e] - _flow[e] : _flow[e];
    }

    private double ArcCost(int arc)
    {
        var e = arc >> 1;
        return (arc & 1) == 0 ? _marginal[e](_flow[e] + 1) : -_marginal[e](_flow[e]);
    }

    private int Head(int arc) => (arc & 1) == 0 ? _to[arc >> 1] : _from[arc >> 1];
    private int Tail(int arc) => (arc & 1) == 0 ? _from[arc >> 1] : _to[arc >> 1];
}
