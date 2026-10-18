using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.MemoryDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application.Memory;

/// <summary>Read side of the memory CQRS split: resolves scope and branch lineage against
///     the agent store, builds the session corpora, plans the query once, searches, and
///     projects one paged result. Wire input is validated strictly here — unknown values are
///     typed errors naming valid spellings, never silent fallbacks.</summary>
public sealed class RecallQueryHandler(IAgentStore store) : IMemoryRecallQuery
{
  private readonly IAgentStore _store = store ?? throw new ArgumentNullException(nameof(store));

  /// <summary>Runs one recall query. Every member of <paramref name="request"/> is
  ///     validated strictly here, in a fixed order — unknown values are typed errors
  ///     naming valid spellings, never silent fallbacks.</summary>
  public async Task<Result<RecallPage>> Execute(RecallRequest request, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    if (ValidatePaging(request) is { } pagingError)
    {
      return InvalidArgument(pagingError);
    }

    Result<SessionScope> parsedScope = SessionScope.Parse(request.Scope);
    if (!parsedScope.IsSuccess)
    {
      return Result.Failure<RecallPage>(parsedScope.Error);
    }

    if (ValidateQueryShape(request) is { } shapeError)
    {
      return InvalidArgument(shapeError);
    }

    Result<BranchMode> branchMode = ParseBranches(request.Branches);
    if (!branchMode.IsSuccess)
    {
      return Result.Failure<RecallPage>(branchMode.Error);
    }

    Result<List<SessionCorpus>> corpora = await BuildCorporaAsync(parsedScope.Value, ct).ConfigureAwait(false);
    if (!corpora.IsSuccess)
    {
      return Result.Failure<RecallPage>(corpora.Error);
    }

    MemoryQueryPlan plan = MemoryQueryPlan.Plan(request.Query, request.QueryMode);
    SearchOutcome outcome = SearchService.Search(
        corpora.Value, plan, parsedScope.Value, branchMode.Value, request.Role, request.Page, request.PageSize);

    return outcome switch
    {
      SearchOk ok => Result.Success(Project(ok.Result)),
      SearchFail fail => Result.Failure<RecallPage>(FromRenderedLine(fail.DomainError)),
      _ => throw new NotSupportedException($"Unhandled search outcome {outcome.GetType().Name}."),
    };
  }

  /// <summary>Paging bounds, validated first: page starts at 1, pageSize spans 1..200.</summary>
  private static string? ValidatePaging(RecallRequest request)
      => request.Page < 1 ? "page must be at least 1." : ValidatePageSize(request.PageSize);

  private static string? ValidatePageSize(int pageSize)
      => pageSize is < 1 or > 200 ? "pageSize must be between 1 and 200." : null;

  /// <summary>Query shape after the scope: queryMode must name a known planner, role when
  ///     present must name a known transcript role.</summary>
  private static string? ValidateQueryShape(RecallRequest request)
      => !string.Equals(request.QueryMode, "literal", StringComparison.Ordinal) &&
          !string.Equals(request.QueryMode, "regex", StringComparison.Ordinal)
            ? "queryMode must be 'literal' or 'regex'."
            : ValidateRole(request.Role);

  private static string? ValidateRole(string? role)
      => role is not null &&
        !string.Equals(role, "user", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(role, "tool", StringComparison.OrdinalIgnoreCase)
          ? "role must be 'user', 'assistant', or 'tool'."
          : null;

  /// <summary>Branch lineage: 'active' walks the active path, 'all' walks every branch;
  ///     anything else (including null) is a typed invalid-argument failure.</summary>
  private static Result<BranchMode> ParseBranches(string? branches) => branches switch
  {
    "active" => Result.Success(BranchMode.ActivePath),
    "all" => Result.Success(BranchMode.AllBranches),
    _ => Result.Failure<BranchMode>(new DomainError("InvalidArgument", "branches must be 'active' or 'all'.")),
  };

  private static RecallPage Project(SearchResult result)
  {
    List<RecallHit> hits = [.. result.Hits
        .Select(hit =>
        {
          MemoryEntry entry = hit.Entry;
          return new RecallHit(
                  entry.Session, entry.Seq, entry.Role, entry.Content, entry.Timestamp);
        })];
    return new RecallPage(hits, result.TotalMatched, result.Page, result.Pages);
  }

  private static Result<RecallPage> InvalidArgument(string message)
      => Result.Failure<RecallPage>(new DomainError("InvalidArgument", message));

  private async Task<Result<List<SessionCorpus>>> BuildCorporaAsync(SessionScope scope, CancellationToken ct)
  {
    Result<IReadOnlyList<AgentRecord>> records = scope switch
    {
      AllSessionsScope => await _store.ListAllAsync(ct).ConfigureAwait(false),
      SingleSessionScope session => (await _store.GetAsync(session.Id, ct).ConfigureAwait(false))
          .Map(single => (IReadOnlyList<AgentRecord>)[single]),
      _ => throw new NotSupportedException($"Unhandled scope type {scope.GetType().Name}."),
    };
    return await FromRecordsAsync(records, ct).ConfigureAwait(false);
  }

  /// <summary>Turns store rows into corpora; every store failure surfaces untouched.</summary>
  private async Task<Result<List<SessionCorpus>>> FromRecordsAsync(
      Result<IReadOnlyList<AgentRecord>> records, CancellationToken ct)
  {
    if (!records.IsSuccess)
    {
      return Result.Failure<List<SessionCorpus>>(records.Error);
    }

    List<SessionCorpus> corpora = [];
    foreach (AgentRecord record in records.Value)
    {
      Result<IReadOnlyList<Message>> transcript = await _store.GetTranscriptAsync(record.Id, ct).ConfigureAwait(false);
      if (!transcript.IsSuccess)
      {
        return Result.Failure<List<SessionCorpus>>(transcript.Error);
      }

      corpora.Add(new SessionCorpus(
          record.Id,
          record.ParentId,
          record.Depth,
          [.. transcript.Value.Select((message, index) => new MemoryEntry(
                    record.Id, index, message.Role.ToString(), message.Content, message.Timestamp))]));
    }

    return Result.Success(corpora);
  }

  /// <summary>Unwraps <see cref="SearchFail"/>: it carries the already-rendered typed
  ///     error line ("Error [code]: message"), while this handler's failures are structured
  ///     <see cref="DomainError"/> pairs rendered to the same shape at the capability edge — so the
  ///     line is split back into its pair, preserving it verbatim through one more hop.</summary>
  private static DomainError FromRenderedLine(string renderedLine)
  {
    const string head = "Error [";
    if (!renderedLine.StartsWith(head, StringComparison.Ordinal))
    {
      throw new FormatException(
          $"Search failure line does not start with '{head}': {renderedLine}");
    }

    int codeEnd = renderedLine.IndexOf(']', head.Length);
    if (codeEnd < 0)
    {
      throw new FormatException(
          $"Search failure line has no closing bracket: {renderedLine}");
    }

    string code = renderedLine[head.Length..codeEnd];
    string remainder = renderedLine[(codeEnd + 1)..];
    string message = remainder.StartsWith(": ", StringComparison.Ordinal)
        ? remainder[2..]
        : remainder;
    return new DomainError(code, message);
  }
}
