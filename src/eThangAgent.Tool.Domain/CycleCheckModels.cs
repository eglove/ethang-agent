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

    // Adjacency + full node set.
    Dictionary<string, List<(string To, bool Deferred)>> next = [];
    foreach (DependencyEdge e in edges)
    {
      if (!next.TryGetValue(e.From, out List<(string To, bool Deferred)>? list))
      {
        next[e.From] = list = [];
      }

      list.Add((e.To, e.Deferred));
    }
    List<string> nodes = new SortedSet<string>(edges.SelectMany(e => new[] { e.From, e.To })).ToList();

    Dictionary<string, int> index = [];
    Dictionary<string, int> low = [];
    HashSet<string> onStack = [];
    Stack<string> stack = new();
    int counter = 0;
    List<List<string>> components = [];

    void StrongConnect(string v)
    {
      index[v] = low[v] = counter++;
      stack.Push(v);
      _ = onStack.Add(v);
      if (next.TryGetValue(v, out List<(string To, bool Deferred)>? succ))
      {
        foreach ((string? w, _) in succ)
        {
          if (!index.TryGetValue(w, out int wIndex))
          {
            StrongConnect(w);
            low[v] = Math.Min(low[v], low[w]);
          }
          else if (onStack.Contains(w))
          {
            low[v] = Math.Min(low[v], wIndex);
          }
        }
      }

      if (low[v] != index[v])
      {
        return;
      }

      List<string> comp = [];
      string w2;
      do
      {
        w2 = stack.Pop();
        _ = onStack.Remove(w2);
        comp.Add(w2);
      } while (w2 != v);
      components.Add(comp);
    }

    foreach (string? n in nodes.Where(n => !index.ContainsKey(n)))
    {
      StrongConnect(n);
    }

    // Reachability from entry points decides which cycles can actually fire.
    // No entry points = check the whole graph, i.e. everything is reachable.
    HashSet<string> reachable = entries.Count == 0 ? [.. nodes] : [.. entries];
    Queue<string> queue = new(entries);
    while (queue.Count > 0)
    {
      if (next.TryGetValue(queue.Dequeue(), out List<(string To, bool Deferred)>? succ))
      {
        foreach ((string? w, _) in succ)
        {
          if (reachable.Add(w))
          {
            queue.Enqueue(w);
          }
        }
      }
    }

    List<DetectedCycle> cycles = [];
    int unreachable = 0;
    // A construction-time deadlock needs a cycle that can be walked entirely
    // during construction — i.e. an all-EAGER cycle. Deferral boundaries cannot
    // fire while a singleton is mid-construction, so any cycle containing one is
    // only latent. Eager-only components therefore decide the verdict.
    Dictionary<string, List<string>> eagerNext = [];
    foreach (DependencyEdge? e in edges.Where(e => !e.Deferred))
    {
      if (!eagerNext.TryGetValue(e.From, out List<string>? list))
      {
        eagerNext[e.From] = list = [];
      }

      list.Add(e.To);
    }
    HashSet<string> deadlockMembers = CyclicMembers(eagerNext, nodes);

    foreach (List<string> comp in components)
    {
      bool isSelfLoop = comp.Count == 1 &&
          next.TryGetValue(comp[0], out List<(string To, bool Deferred)>? sl) && sl.Any(s => s.To == comp[0]);
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
      cycles.Add(new DetectedCycle(members,
          members.Any(deadlockMembers.Contains) ? CycleVerdict.DeadlockRisk : CycleVerdict.Latent));
    }

    cycles.Sort((a, b) => string.CompareOrdinal(a.Members[0], b.Members[0]));
    return Result.Success(new CycleReport(cycles, unreachable));
  }

  /// <summary>Nodes lying on a cycle (SCC of size &gt;1 or self-edge) within the given
  ///     subgraph. Used with the eager-only edge set to find constructible cycles.</summary>
  private static HashSet<string> CyclicMembers(Dictionary<string, List<string>> adjacency, IReadOnlyList<string> nodes)
  {
    Dictionary<string, int> index = [];
    Dictionary<string, int> low = [];
    HashSet<string> onStack = [];
    Stack<string> stack = new();
    int counter = 0;
    HashSet<string> cyclic = [];

    void StrongConnect(string v)
    {
      index[v] = low[v] = counter++;
      stack.Push(v);
      _ = onStack.Add(v);
      if (adjacency.TryGetValue(v, out List<string>? succ))
      {
        foreach (string w in succ)
        {
          if (!index.TryGetValue(w, out int wIndex))
          {
            StrongConnect(w);
            low[v] = Math.Min(low[v], low[w]);
          }
          else if (onStack.Contains(w))
          {
            low[v] = Math.Min(low[v], wIndex);
          }
        }
      }

      if (low[v] != index[v])
      {
        return;
      }

      List<string> comp = [];
      string w2;
      do
      {
        w2 = stack.Pop();
        _ = onStack.Remove(w2);
        comp.Add(w2);
      } while (w2 != v);
      bool hasSelfEdge = comp.Count == 1 &&
          adjacency.TryGetValue(comp[0], out List<string>? s) && s.Contains(comp[0]);
      if (comp.Count > 1 || hasSelfEdge)
      {
        foreach (string m in comp)
        {
          _ = cyclic.Add(m);
        }
      }
    }

    foreach (string n in nodes)
    {
      if (!index.ContainsKey(n))
      {
        StrongConnect(n);
      }
    }

    return cyclic;
  }
}
