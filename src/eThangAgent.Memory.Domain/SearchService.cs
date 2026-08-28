using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.MemoryDomain;

/// <summary>Success outcome: carries the paged result.</summary>
public sealed record SearchOk(SearchResult Result) : SearchOutcome;

/// <summary>Failure outcome: carries the rendered typed error line.</summary>
public sealed record SearchFail(string DomainError) : SearchOutcome;

/// <summary>Success carries the paged result; failure carries the rendered typed error line.</summary>
public abstract record SearchOutcome;

/// <summary>One ordered page of matches over the whole (unpaged) match set.</summary>
public sealed record SearchResult(IReadOnlyList<Hit> Hits, int TotalMatched, int Page, int Pages);

/// <summary>A single matched entry.</summary>
public sealed record Hit(MemoryEntry Entry);

/// <summary>
/// Filters the session corpus by scope, branch lineage, and role, orders what survives
/// newest-first (Timestamp desc, Seq desc, session id ordinal ascending), applies the
/// query plan, and pages the flat hit list. Ordering is identical across all plan modes.
/// </summary>
public static class SearchService
{
  /// <summary>
  /// <paramref name="page"/> and <paramref name="pageSize"/> must already be valid —
  /// the capability layer validates wire input before the domain is reached; a violation
  /// here is programmer error.
  /// </summary>
  public static SearchOutcome Search(
      IReadOnlyList<SessionCorpus> sessions,
      MemoryQueryPlan plan,
      SessionScope scope,
      BranchMode branches,
      string? role,
      int page,
      int pageSize)
  {
    ArgumentNullException.ThrowIfNull(sessions);
    ArgumentNullException.ThrowIfNull(plan);
    ArgumentNullException.ThrowIfNull(scope);
    if (page < 1)
    {
      throw new ArgumentException("page must be at least 1.", nameof(page));
    }

    if (pageSize < 1)
    {
      throw new ArgumentException("pageSize must be at least 1.", nameof(pageSize));
    }

    List<SessionCorpus> scoped = InScope(sessions, scope);
    List<SessionCorpus> candidates = branches == BranchMode.AllBranches
        ? scoped
        : KeepActivePaths(scoped);

    List<MemoryEntry> ordered = [.. candidates
        .SelectMany(c => c.Entries)
        .Where(e => string.IsNullOrWhiteSpace(role) ||
                    string.Equals(e.Role, role, StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(e => e.Timestamp)
        .ThenByDescending(e => e.Seq)
        .ThenBy(e => e.Session.Value.ToString(), StringComparer.Ordinal)];

    return plan switch
    {
      RegexPatternPlan regex => MatchByRegex(ordered, regex.Pattern, page, pageSize),
      TermsPlan terms => Page(FilterByTerms(ordered, terms.Tokens), page, pageSize),
      _ => Page(ordered, page, pageSize),
    };
  }

  private static List<SessionCorpus> InScope(IReadOnlyList<SessionCorpus> sessions, SessionScope scope)
      => scope switch
      {
        AllSessionsScope => [.. sessions],
        SingleSessionScope s => [.. sessions.Where(c => c.Id == s.Id)],
        _ => throw new NotSupportedException($"Unhandled scope type {scope.GetType().Name}."),
      };

  /// <summary>
  /// Active path: a session qualifies only when its ParentId chain walk terminates at a
  /// root (ParentId null) within the given set. Chains whose ancestor row is absent are
  /// orphans and excluded — that is the observable branch difference. A lineage cycle
  /// never reaches a root, so it is excluded rather than walked forever.
  /// </summary>
  private static List<SessionCorpus> KeepActivePaths(List<SessionCorpus> scoped)
  {
    Dictionary<AgentId, SessionCorpus> byId = new(scoped.Count);
    foreach (SessionCorpus corpus in scoped)
    {
      byId[corpus.Id] = corpus;
    }

    List<SessionCorpus> active = [];
    foreach (SessionCorpus corpus in scoped)
    {
      SessionCorpus current = corpus;
      HashSet<AgentId> visited = [corpus.Id];
      bool reachesRoot = true;
      while (current.ParentId is { } parentId)
      {
        if (!byId.TryGetValue(parentId, out SessionCorpus? parent))
        {
          reachesRoot = false; // ancestor row absent — orphan chain
          break;
        }
        if (!visited.Add(parent.Id))
        {
          reachesRoot = false; // cycle — no root reachable
          break;
        }
        current = parent;
      }

      if (reachesRoot)
      {
        active.Add(corpus);
      }
    }

    return active;
  }

  /// <summary>AND semantics: an entry matches only when every planned token is in its canonical token set.</summary>
  private static List<MemoryEntry> FilterByTerms(List<MemoryEntry> ordered, IReadOnlyList<string> tokens)
  {
    if (tokens.Count == 0)
    {
      return ordered; // vacuous AND; unreachable via Plan (whitespace yields Browse)
    }

    List<MemoryEntry> matched = [];
    foreach (MemoryEntry entry in ordered)
    {
      HashSet<string> entryTokens = [.. LexicalTokenizer.Tokenize(entry.Content)];
      bool allPresent = true;
      foreach (string token in tokens)
      {
        if (entryTokens.Contains(token))
        {
          continue;
        }

        allPresent = false;
        break;
      }

      if (allPresent)
      {
        matched.Add(entry);
      }
    }

    return matched;
  }

  /// <summary>
  /// Delegates to <see cref="BoundedRegex"/> over the candidate contents in canonical
  /// order; the first typed failure propagates verbatim as the outcome's error line.
  /// </summary>
  private static SearchOutcome MatchByRegex(List<MemoryEntry> ordered, string pattern, int page, int pageSize)
  {
    Result<IReadOnlyList<int>> result = BoundedRegex.Execute(pattern, [.. ordered.Select(e => e.Content)]);
    if (!result.IsSuccess)
    {
      return new SearchFail($"Error [{result.Error.Code}]: {result.Error.Message}");
    }

    List<MemoryEntry> matched = [.. result.Value.Select(i => ordered[i])];
    return Page(matched, page, pageSize);
  }

  private static SearchOk Page(List<MemoryEntry> matched, int page, int pageSize)
  {
    int total = matched.Count;
    int pages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
    List<Hit> hits = [.. matched
        .Skip((page - 1) * pageSize)
        .Take(pageSize)
        .Select(e => new Hit(e))];

    return new SearchOk(new SearchResult(hits, total, page, pages));
  }
}
