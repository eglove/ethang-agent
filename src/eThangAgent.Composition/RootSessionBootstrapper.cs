using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Composition;

/// <summary>The ONE place a host persists the root session. The desktop host and the
/// E2E fixture both call this: when whoever persists AgentRecord.Root and whoever
/// appends transcript messages disagree on the id, memory recall silently loses the
/// session — so the id must come from a single source.</summary>
public static class RootSessionBootstrapper
{
  /// <summary>Persists a fresh root record (depth 0, no parent, Running) and returns
  ///     its id. The SAME id must be handed to the view-model/handler that appends
  ///     transcript messages; a mismatch silently breaks memory recall.</summary>
  public static Task<Result<AgentId>> PersistRootAsync(IAgentStore store,
      CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(store);
    AgentId rootId = AgentId.NewId();
    return PersistAsync(store, rootId, ct);
  }

  private static async Task<Result<AgentId>> PersistAsync(
      IAgentStore store, AgentId rootId, CancellationToken ct)
  {
    Result<string> saved = await store.SaveAsync(AgentRecord.Root(rootId, DateTimeOffset.UtcNow), ct).ConfigureAwait(false);
    return saved.IsSuccess
        ? Result.Success(rootId)
        : Result.Failure<AgentId>(new DomainError("PersistFailed",
            $"failed to persist root session: [{saved.Error.Code}] {saved.Error.Message}"));
  }
}
