namespace eThangAgent.AgentDomain;

/// <summary>Spawn-time capability grants (D9): the child's effective tool set is the
///     intersection of its parent's effective set with the allow list, minus the deny list.
///     Privilege cannot grow down the tree — a child grant that widens beyond the parent's
///     effective set is a spawn-validation error, never a clamp (A5). Null grants mean the
///     today's-child default surface (the registry's own child set).</summary>
/// <remarks>Namespace semantics (issue #108): an entry ending in <c>.*</c> is a namespace
///     PATTERN — <c>mcp.github.*</c> reaches every id under <c>mcp.github.</c>. Patterns
///     hang from a DISPATCH ROOT (the first segment — <c>mcp</c>, the dispatch tool's
///     registry id); a pattern entry implies its root so the dispatch tool resolves in the
///     filtered registry, and the pattern itself rides the persisted set for dispatch-time
///     matching (the MCP grant scope). A bare root entry (<c>mcp</c>) in an allow list
///     inherits the parent's namespace reach (its patterns flow down) — a child granted
///     bare <c>mcp</c> reaches exactly what the parent could, never more. A set holding a
///     root WITH patterns reaches the patterns' union; a root with none reaches everything
///     (the default surface's shape). Widening is measured over reach: a pattern is valid
///     only nested within the parent's reach. Deny entries remove what they COVER; a deny
///     that would subtract within a broader grant (unexpressible in the additive entry
///     model) is a spawn-validation error — narrow the allow instead. Exact ids keep the
///     exact semantics for every non-namespace tool.</remarks>
public sealed class ToolGrantPolicy
{
  public const string AllowKey = "tool.allow";
  public const string DenyKey = "tool.deny";

  /// <summary>The suffix marking a namespace pattern entry.</summary>
  private const string PatternSuffix = ".*";

  private readonly IReadOnlyDictionary<string, string> _grants = new Dictionary<string, string>();

  public ToolGrantPolicy(IReadOnlyDictionary<string, string>? grants)
      => _grants = grants ?? _grants;

  /// <summary>Whether any grant is present (absent grants keep the default child surface).</summary>
  public bool HasGrants => _grants.ContainsKey(AllowKey) || _grants.ContainsKey(DenyKey);

  /// <summary>Applies allow/deny to the parent's effective set. allow absent = inherit all.
  ///     deny absent = remove nothing. Entries are exact tool action ids or namespace
  ///     patterns (issue #108); the dispatch root rides a pattern grant so the dispatch
  ///     tool resolves, and a bare root allow inherits the parent's namespace reach.</summary>
  public IReadOnlySet<string> EffectiveTools(IReadOnlySet<string> parentEffective)
  {
    ArgumentNullException.ThrowIfNull(parentEffective);
    HashSet<string> effective = _grants.TryGetValue(AllowKey, out string? allow)
        ? AllowEntries(allow, parentEffective)
        : [.. parentEffective];
    if (_grants.TryGetValue(DenyKey, out string? deny))
    {
      RemoveDenied(effective, deny, ExplicitRoots(allow, parentEffective));
    }

    return effective;
  }

  /// <summary>Spawn validation (D9): every allowed entry must be within the parent's
  ///     effective reach — exact ids by containment, namespace patterns by nested-prefix
  ///     reach, deny combinations by expressibility (issue #108). Returns the offending
  ///     entries; empty = valid.</summary>
  public IReadOnlyList<string> WideningViolations(IReadOnlySet<string> parentEffective)
  {
    ArgumentNullException.ThrowIfNull(parentEffective);
    if (!_grants.TryGetValue(AllowKey, out string? allow))
    {
      return [];
    }

    List<string> violations = [];
    foreach (string entry in ParseList(allow))
    {
      if (IsPattern(entry))
      {
        if (!PatternWithinParentReach(entry, parentEffective))
        {
          violations.Add(entry);
        }
      }
      else if (PatternEntries(allow).Any(p => DispatchRoot(p) == entry))
      {
        // The bare dispatch root granted alongside its own pattern (the pattern's
        // implied root): validated with the pattern, never as a standalone candidate.
      }
      else if (parentEffective.Contains(entry))
      {
        // An exact id the parent holds: the classic narrowing case.
      }
      else if (RootReachInherited(entry, allow, parentEffective))
      {
        // A bare dispatch-root allow: reach inherited from the parent, never wider.
      }
      else
      {
        violations.Add(entry);
      }
    }

    violations.AddRange(DenyShapeViolations(allow, parentEffective));
    return violations;
  }

  // ---- allow resolution (issue #108) ----

  private static HashSet<string> AllowEntries(string allow, IReadOnlySet<string> parentEffective)
  {
    HashSet<string> effective = [];
    foreach (string entry in ParseList(allow))
    {
      if (IsPattern(entry))
      {
        // The pattern plus its dispatch root (the dispatch tool's registry id).
        _ = effective.Add(entry);
        _ = effective.Add(DispatchRoot(entry));
      }
      else if (RootReachInherited(entry, allow, parentEffective))
      {
        // A bare dispatch-root allow inherits the parent's namespace reach: the
        // parent's patterns flow down so the child reaches exactly the parent's reach.
        _ = effective.Add(entry);
        effective.UnionWith(parentEffective
            .Where(p => IsPattern(p) && DispatchRoot(p) == entry));
      }
      else if (parentEffective.Contains(entry))
      {
        _ = effective.Add(entry);
      }
    }

    return effective;
  }

  /// <summary>The dispatch roots the allow list grants EXPLICITLY: a bare root in the
  ///     allow list, or a bare root inherited from a parent whose set holds the root
  ///     with full reach (no parent patterns under it). An implied root (added only
  ///     because a pattern needed the dispatch tool) dies with its patterns.</summary>
  private static HashSet<string> ExplicitRoots(string? allow, IReadOnlySet<string> parentEffective)
  {
    HashSet<string> explicitRoots = [];
    if (allow is not null)
    {
      explicitRoots.UnionWith(ParseList(allow).Where(e => !IsPattern(e)));
    }

    explicitRoots.UnionWith(parentEffective.Where(e => !IsPattern(e))
        .Where(root => !parentEffective.Any(p => IsPattern(p) && DispatchRoot(p) == root)));

    return explicitRoots;
  }

  // ---- deny resolution (issue #108) ----

  /// <summary>Removes deny-covered entries from the allow-derived set: exact denies
  ///     remove the id (a dispatch root takes its patterns with it); deny patterns
  ///     remove every pattern they cover (equal namespace or nested within). A root
  ///     left with no surviving patterns is removed too unless it was granted
  ///     explicitly — an implied root without its patterns is meaningless.</summary>
  private static void RemoveDenied(HashSet<string> effective, string? deny, HashSet<string> explicitRoots)
  {
    if (deny is null)
    {
      return;
    }

    foreach (string exact in ExactEntries(deny))
    {
      bool removed = effective.Remove(exact);
      if (removed)
      {
        _ = effective.RemoveWhere(entry => IsPattern(entry) && IsUnder(entry, exact));
      }
    }

    _ = effective.RemoveWhere(entry => IsPattern(entry)
        && deny.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(denyEntry => denyEntry.EndsWith(".*", StringComparison.Ordinal)
                && IsUnder(entry, denyEntry[..^2])));

    // Implied-root cleanup: a surviving root with no surviving patterns and no
    // explicit grant dies with the patterns that implied it.
    _ = effective.RemoveWhere(root => !IsPattern(root)
        && !explicitRoots.Contains(root)
        && !PatternsUnder(effective, root).Any());
  }

  /// <summary>The deny list's shape violations over the allow-derived set (issue #108):
  ///     a deny whose reach survives the removal — a broader pattern still grants it, or
  ///     an unbounded root still covers it — is an unexpressible subtraction (the entry
  ///     model is additive); refuse the spawn (narrow the allow instead), never a silent
  ///     over-grant. A deny that strips the patterns bounding an inherited root leaves
  ///     the child full reach over a patterned parent — the same violation, named by the
  ///     root.</summary>
  private List<string> DenyShapeViolations(string allow, IReadOnlySet<string> parentEffective)
  {
    List<string> violations = [];
    if (!_grants.TryGetValue(DenyKey, out string? deny) || deny is null)
    {
      return violations;
    }

    HashSet<string> after = AllowEntries(allow, parentEffective);
    RemoveDenied(after, deny, ExplicitRoots(allow, parentEffective));

    violations.AddRange(PatternEntries(deny)
        .Where(denyPattern => StillGranted(after, NamespaceOf(denyPattern))));

    // A surviving root with no patterns over a patterned parent: the deny stripped
    // the patterns that bounded it, so the child would reach the whole namespace.
    violations.AddRange(after.Where(entry => !IsPattern(entry))
        .Where(root => !PatternsUnder(after, root).Any()
            && PatternsUnder(parentEffective, root).Any()));
    return violations;
  }

  /// <summary>Whether the denied namespace's reach survives in the post-deny set: a
  ///     surviving pattern covers it (equal or broader prefix under the same dispatch
  ///     root), or a surviving root covers it with full reach.</summary>
  private static bool StillGranted(HashSet<string> after, string deniedNamespace)
  {
    string root = DispatchRoot(deniedNamespace + ".");
    if (after.Contains(root) && !PatternsUnder(after, root).Any())
    {
      return true; // full reach over the namespace
    }

    return after.Any(entry => IsPattern(entry)
        && DispatchRoot(entry) == root
        && (NamespaceOf(entry) == deniedNamespace
            || deniedNamespace.StartsWith(NamespaceOf(entry) + ".", StringComparison.Ordinal)));
  }

  /// <summary>Whether the bare root entry <paramref name="entry"/> in
  ///     <paramref name="allow"/> inherits namespace reach (issue #108): the entry must
  ///     BE a dispatch root — a patterned child under it lives in the parent set or the
  ///     allow list (mcp with mcp.github.* anywhere in the chain).</summary>
  private static bool RootReachInherited(string entry, string allow, IReadOnlySet<string> parentEffective)
      => PatternsUnder(parentEffective, entry).Any()
          || PatternEntries(allow).Any(p => DispatchRoot(p) == entry);

  /// <summary>Whether a child pattern's reach is within the parent's, over the
  ///     pattern's dispatch root (issue #108): the parent's reach is full when its set
  ///     holds the exact dispatch root with no constraining patterns; otherwise the
  ///     child's prefix must start with some parent pattern's prefix (the child is
  ///     equal or narrower — never the reverse).</summary>
  private static bool PatternWithinParentReach(string pattern, IReadOnlySet<string> parentEffective)
  {
    string root = DispatchRoot(pattern);
    string childPrefix = NamespaceOf(pattern) + ".";
    List<string> parentPatterns = [.. PatternsUnder(parentEffective, root)];
    if (parentEffective.Contains(root) && parentPatterns.Count == 0)
    {
      return true; // the parent holds the dispatch tool with full reach
    }

    return parentPatterns.Any(p =>
        childPrefix.StartsWith(NamespaceOf(p) + ".", StringComparison.Ordinal));
  }

  // ---- entry shape helpers (issue #108) ----

  private static IEnumerable<string> ParseList(string semicolonSeparated)
      => semicolonSeparated
          .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
          .Where(entry => entry.Length > 0);

  private static IEnumerable<string> ExactEntries(string list)
      => ParseList(list).Where(entry => !IsPattern(entry));

  private static IEnumerable<string> PatternEntries(string list)
      => ParseList(list).Where(IsPattern);

  /// <summary>Every pattern entry in <paramref name="set"/> under
  ///     <paramref name="root"/>: its dispatch root equals the root (mcp.github.* under
  ///     mcp), or its namespace nests within the root's namespace (mcp.github.private.*
  ///     under mcp.github).</summary>
  private static IEnumerable<string> PatternsUnder(IReadOnlySet<string> set, string root)
      => set.Where(e => IsPattern(e) && IsUnder(e, root));

  /// <summary>Whether <paramref name="entry"/>'s namespace nests under
  ///     <paramref name="root"/>: equal to it, or a child of it in the dot hierarchy.</summary>
  private static bool IsUnder(string entry, string root)
  {
    string namespaceOfEntry = IsPattern(entry) ? NamespaceOf(entry) : entry;
    return namespaceOfEntry == root
        || namespaceOfEntry.StartsWith(root + ".", StringComparison.Ordinal);
  }

  private static bool IsPattern(string entry)
      => entry.EndsWith(PatternSuffix, StringComparison.Ordinal) && entry.Length > PatternSuffix.Length;

  /// <summary>The namespace of a pattern entry: the text before the trailing .*
  ///     (mcp.github.* → mcp.github) — the prefix its reach covers.</summary>
  private static string NamespaceOf(string pattern)
      => pattern[..^PatternSuffix.Length];

  /// <summary>The dispatch root a pattern or root entry hangs from: the first segment
  ///     (mcp.github.* → mcp; mcp → mcp) — the dispatch tool's registry id.</summary>
  private static string DispatchRoot(string entry)
      => entry.Split('.')[0];
}
