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
    if (request.Page < 1)
    {
      return InvalidArgument("page must be at least 1.");
    }

    if (request.PageSize is < 1 or > 200)
    {
      return InvalidArgument("pageSize must be between 1 and 200.");
    }

    Result<SessionScope> parsedScope = SessionScope.Parse(request.Scope);
    if (!parsedScope.IsSuccess)
    {
      return Result.Failure<RecallPage>(parsedScope.Error);
    }

    if (!string.Equals(request.QueryMode, "literal", StringComparison.Ordinal) &&
        !string.Equals(request.QueryMode, "regex", StringComparison.Ordinal))
    {
      return InvalidArgument("queryMode must be 'literal' or 'regex'.");
    }

    if (request.Role is not null &&
        !string.Equals(request.Role, "user", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(request.Role, "assistant", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(request.Role, "tool", StringComparison.OrdinalIgnoreCase))
    {
      return InvalidArgument("role must be 'user', 'assistant', or 'tool'.");
    }

    BranchMode? branchMode = request.Branches switch
    {
      "active" => BranchMode.ActivePath,
      "all" => BranchMode.AllBranches,
      _ => null,
    };
    if (branchMode is not { } mode)
    {
      return InvalidArgument("branches must be 'active' or 'all'.");
    }

    Result<List<SessionCorpus>> corpora = await BuildCorporaAsync(parsedScope.Value, ct).ConfigureAwait(false);
    if (!corpora.IsSuccess)
    {
      return Result.Failure<RecallPage>(corpora.Error);
    }

    MemoryQueryPlan plan = MemoryQueryPlan.Plan(request.Query, request.QueryMode);
    SearchOutcome outcome = SearchService.Search(
        corpora.Value, plan, parsedScope.Value, mode, request.Role, request.Page, request.PageSize);

    return outcome switch
    {
      SearchOk ok => Result.Success(Project(ok.Result)),
      SearchFail fail => Result.Failure<RecallPage>(FromRenderedLine(fail.DomainError)),
      _ => throw new NotSupportedException($"Unhandled search outcome {outcome.GetType().Name}."),
    };
  }

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
