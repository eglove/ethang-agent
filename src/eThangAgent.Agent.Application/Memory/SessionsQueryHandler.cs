using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.MemoryDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application.Memory;

/// <summary>Read side of the memory CQRS split: lists persisted sessions newest-first with
///     lineage, size, lifecycle, and the constant hot tier. Scope and branches are validated
///     with the same strictness as recall so both actions share one wire contract; the listing
///     itself spans every persisted row, ordered CreatedAt descending.</summary>
public sealed class SessionsQueryHandler(IAgentStore store) : IMemorySessionsQuery
{
  /// <summary>pi-fabric's session ceiling, ported verbatim as the wire limit cap.</summary>
  public const int MaxLimit = 500;

  private readonly IAgentStore _store = store ?? throw new ArgumentNullException(nameof(store));

  /// <param name="scope">Null/"global" or "session:&lt;agentId&gt;" — validated, not applied:
  ///     the listing always spans all persisted rows per the approved task contract.</param>
  /// <param name="branches">Exactly "active" or "all".</param>
  /// <param name="limit">1..500.</param>
  public async Task<Result<IReadOnlyList<SessionSummary>>> Execute(
      string? scope, string branches, int limit, CancellationToken ct = default)
  {
    if (limit is < 1 or > MaxLimit)
    {
      return InvalidArgument($"limit must be between 1 and {MaxLimit}.");
    }

    Result<SessionScope> parsedScope = SessionScope.Parse(scope);
    if (!parsedScope.IsSuccess)
    {
      return Result.Failure<IReadOnlyList<SessionSummary>>(parsedScope.Error!);
    }

    bool branchKnown = string.Equals(branches, "active", StringComparison.Ordinal) ||
                      string.Equals(branches, "all", StringComparison.Ordinal);
    if (!branchKnown)
    {
      return InvalidArgument("branches must be 'active' or 'all'.");
    }

    Result<IReadOnlyList<AgentRecord>> listed = await _store.ListAllAsync(ct);
    if (!listed.IsSuccess)
    {
      return Result.Failure<IReadOnlyList<SessionSummary>>(listed.Error!);
    }

    List<SessionSummary> summaries = [];
    foreach (AgentRecord? record in listed.Value!.OrderByDescending(r => r.CreatedAt).Take(limit))
    {
      Result<IReadOnlyList<Message>> transcript = await _store.GetTranscriptAsync(record.Id, ct);
      if (!transcript.IsSuccess)
      {
        return Result.Failure<IReadOnlyList<SessionSummary>>(transcript.Error!);
      }

      summaries.Add(new SessionSummary(
          record.Id,
          // AgentRecord.Label is nullable; the summary projection renders an empty
          // label rather than inventing a display name.
          record.Label ?? string.Empty,
          record.Depth,
          transcript.Value!.Count,
          record.Status.ToString(),
          Tier: "hot"));
    }

    return Result.Success<IReadOnlyList<SessionSummary>>(summaries);
  }

  private static Result<IReadOnlyList<SessionSummary>> InvalidArgument(string message)
      => Result.Failure<IReadOnlyList<SessionSummary>>(new DomainError("InvalidArgument", message));
}
