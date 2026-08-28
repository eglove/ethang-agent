using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>A directed construction dependency between two named units. The names are
///     opaque to this domain — they may be C# types, Spring beans, npm modules, or any
///     other wiring identity. <see cref="Deferred"/> marks a deferral boundary (factory,
///     lazy/provider wrapper, lookup-by-name): the target is not constructed when the
///     source is, only later, on demand.</summary>
public sealed record DependencyEdge(string From, string To, bool Deferred)
{
  public override string ToString() => $"{From} -> {To}" + (Deferred ? " [deferred]" : "");
}

/// <summary>The verdict on one detected dependency cycle. A cycle is <see cref="DeadlockRisk"/>
///     when at least one edge inside its strongly-connected component is eager: constructing
///     any member re-enters the in-progress member through that edge. When every internal
///     edge is deferred the cycle cannot hang construction, but removing a deferral would
///     arm it — hence <see cref="Latent"/>.</summary>
public enum CycleVerdict { DeadlockRisk, Latent }

/// <summary>One detected cycle: the strongly-connected component's members plus its verdict.</summary>
public sealed record DetectedCycle(IReadOnlyList<string> Members, CycleVerdict Verdict);

/// <summary>Result of analyzing a dependency graph: cycles reachable from any entry point
///     (the ones that can actually fire), and how many cycles exist outside that reach.
///     Members are listed in discovery order starting from the smallest-named member so
///     output is deterministic across runs.</summary>
public sealed record CycleReport(IReadOnlyList<DetectedCycle> ReachableCycles, int UnreachableCycles)
{
  public bool IsClean => ReachableCycles.Count == 0;
}

/// <summary>Pure cycle detection over a named-node directed graph with eager/deferred edge
///     classification. No language knowledge, no I/O: callers supply an already-resolved
///     edge list. Detection uses Tarjan strongly-connected components; a component counts
///     as a cycle when it has more than one member or contains a self-edge. The verdict is
///     deliberately conservative: any eager edge inside a live component raises
///     <see cref="CycleVerdict.DeadlockRisk"/>, even if some simple cycle through the
///     component might avoid it — under-flagging a deadlock is worse than over-flagging.</summary>
public static class CycleDetector
{
  public static Result<CycleReport> Detect(IReadOnlyList<string> entries, IReadOnlyList<DependencyEdge> edges)
  {
    ArgumentNullException.ThrowIfNull(entries);
    ArgumentNullException.ThrowIfNull(edges);
    if (edges.Count == 0)
    {
      return Result.Success(new CycleReport([], 0));
    }

    // Adjacency (successor names only) + full node set.
    Dictionary<string, List<string>> graph = BuildSuccessorMap(edges, deferredOnly: false);
    List<string> nodes = new SortedSet<string>(edges.SelectMany(e => new[] { e.From, e.To })).ToList();
    List<List<string>> components = new TarjanPass(graph).Run(nodes);

    // Reachability from entry points decides which cycles can actually fire.
    HashSet<string> reachable = CollectReachable(entries, graph, nodes);

    // A construction-time deadlock needs a cycle that can be walked entirely
    // during construction — i.e. an all-EAGER cycle. Deferral boundaries cannot
    // fire while a singleton is mid-construction, so any cycle containing one is
    // only latent. Eager-only components therefore decide the verdict.
    HashSet<string> deadlockMembers = CyclicMembers(BuildSuccessorMap(edges, deferredOnly: true), nodes);

    (List<DetectedCycle> cycles, int unreachable) = ClassifyComponents(components, graph, reachable, deadlockMembers);
    cycles.Sort((a, b) => string.CompareOrdinal(a.Members[0], b.Members[0]));
    CycleReport report = new(cycles, unreachable);
    return Result.Success(report);
  }

  /// <summary>Successor-name adjacency; <paramref name="deferredOnly"/> keeps only the
  ///     eager edges (the all-eager subgraph that decides deadlock verdicts).</summary>
  private static Dictionary<string, List<string>> BuildSuccessorMap(IReadOnlyList<DependencyEdge> edges, bool deferredOnly)
  {
    Dictionary<string, List<string>> successors = [];
    foreach (DependencyEdge e in edges)
    {
      if (deferredOnly && e.Deferred)
      {
        continue;
      }

      if (!successors.TryGetValue(e.From, out List<string>? list))
      {
        successors[e.From] = list = [];
      }

      list.Add(e.To);
    }

    return successors;
  }

  /// <summary>Breadth-first reachability over the graph, seeded with the entry points.
  ///     No entry points = check the whole graph, i.e. everything is reachable.</summary>
  private static HashSet<string> CollectReachable(IReadOnlyList<string> entries,
      Dictionary<string, List<string>> graph, IReadOnlyList<string> nodes)
  {
    HashSet<string> reachable = entries.Count == 0 ? [.. nodes] : [.. entries];
    Queue<string> queue = new(entries);
    while (queue.Count > 0)
    {
      if (graph.TryGetValue(queue.Dequeue(), out List<string>? succ))
      {
        // HashSet.Add returns true exactly for the newly-reachable nodes.
        foreach (string w in succ.Where(reachable.Add))
        {
          queue.Enqueue(w);
        }
      }
    }

    return reachable;
  }

  /// <summary>Turns strongly-connected components into the report: live cycles become
  ///     <see cref="DetectedCycle"/>s in deterministic order, dead ones are counted.</summary>
  private static (List<DetectedCycle> Cycles, int Unreachable) ClassifyComponents(
      List<List<string>> components, Dictionary<string, List<string>> graph,
      HashSet<string> reachable, HashSet<string> deadlockMembers)
  {
    List<DetectedCycle> cycles = [];
    int unreachable = 0;
    foreach (List<string> comp in components)
    {
      bool isSelfLoop = comp.Count == 1 && graph.TryGetValue(comp[0], out List<string>? sl) && sl.Contains(comp[0]);
      if (comp.Count < 2 && !isSelfLoop)
      {
        continue;
      }

      if (!comp.Any(reachable.Contains))
      {
        unreachable++;
        continue;
      }

      List<string> members = [.. comp.OrderBy(x => x, StringComparer.Ordinal)];
      DetectedCycle cycle = new(members,
          members.Any(deadlockMembers.Contains) ? CycleVerdict.DeadlockRisk : CycleVerdict.Latent);
      cycles.Add(cycle);
    }

    return (cycles, unreachable);
  }

  /// <summary>Nodes lying on a cycle (SCC of size &gt;1 or self-edge) within the given
  ///     subgraph. Used with the eager-only edge set to find constructible cycles.</summary>
  private static HashSet<string> CyclicMembers(Dictionary<string, List<string>> eagerGraph, IReadOnlyList<string> nodes)
  {
    List<List<string>> components = new TarjanPass(eagerGraph).Run(nodes);
    HashSet<string> cyclic = [];
    foreach (List<string> component in components)
    {
      if (!IsCyclicComponent(component, eagerGraph))
      {
        continue;
      }

      foreach (string m in component)
      {
        _ = cyclic.Add(m);
      }
    }

    return cyclic;
  }

  /// <summary>A component counts as a cycle when it has more than one member or
  ///     contains a self-edge.</summary>
  private static bool IsCyclicComponent(List<string> component, Dictionary<string, List<string>> graph)
  {
    if (component.Count > 1)
    {
      return true;
    }

    bool hasSelfEdge = graph.TryGetValue(component[0], out List<string>? successors) && successors.Contains(component[0]);
    return hasSelfEdge;
  }

  /// <summary>Faithful Tarjan strongly-connected-components pass over a successor map.
  ///     The recursive walk and its mutable index/low/stack state live together so
  ///     both call sites (component detection and eager-cycle membership) share one
  ///     implementation without touching each other's state.</summary>
  private sealed class TarjanPass(Dictionary<string, List<string>> successors)
  {
    private readonly Dictionary<string, int> _index = [];
    private readonly Dictionary<string, int> _low = [];
    private readonly HashSet<string> _onStack = [];
    private readonly Stack<string> _stack = new();
    private readonly List<List<string>> _components = [];
    private int _counter;

    internal List<List<string>> Run(IReadOnlyList<string> nodes)
    {
      foreach (string n in nodes.Where(n => !_index.ContainsKey(n)))
      {
        StrongConnect(n);
      }

      return _components;
    }

    private void StrongConnect(string v)
    {
      _index[v] = _low[v] = _counter++;
      _stack.Push(v);
      _ = _onStack.Add(v);
      if (successors.TryGetValue(v, out List<string>? succ))
      {
        foreach (string w in succ)
        {
          Relax(v, w);
        }
      }

      if (_low[v] != _index[v])
      {
        return;
      }

      PopComponent(v);
    }

    private void Relax(string v, string w)
    {
      if (!_index.TryGetValue(w, out int wIndex))
      {
        StrongConnect(w);
        _low[v] = Math.Min(_low[v], _low[w]);
      }
      else if (_onStack.Contains(w))
      {
        _low[v] = Math.Min(_low[v], wIndex);
      }
    }

    private void PopComponent(string v)
    {
      List<string> component = [];
      string popped;
      do
      {
        popped = _stack.Pop();
        _ = _onStack.Remove(popped);
        component.Add(popped);
      } while (popped != v);
      _components.Add(component);
    }
  }
}
