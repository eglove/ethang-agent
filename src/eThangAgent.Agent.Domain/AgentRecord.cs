namespace eThangAgent.AgentDomain;

public sealed record AgentRecord(
    AgentId Id,
    AgentStatus Status,
    AgentFailureReason? FailureReason,
    string ModelUsed,
    string? Label,
    string TaskPrompt,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    string? FinalReport,
    string? WorkspaceId = null,
    string? Provider = null,
    int Attempts = 0,
    AgentPhase? Phase = null,
    string? Contract = null)
{

  /// <summary>The ModelUsed sentinel for a root that has served no model yet: no
  /// selection has pinned the session. Resolvers treat it as unpinned.</summary>
  public const string UnassignedModel = "unassigned";

  /// <summary>The Provider sentinel a row carries when the wiring supplies no provider
  ///     name (legacy hosts, tests): the column is never SQL NULL, so diagnostics can tell
  ///     "started unbound" from an absent column, and the run's provider invariant
  ///     recognizes the marker by value.</summary>
  public const string UnboundProvider = "unbound";

  /// <summary>Creates a freshly started agent row. When a contract is supplied it is
  ///     serialized into the Contract column so resume and audit see the agreement the run
  ///     started with. Attempts/Phase stay at their defaults: the RUNTIME writes them when it
  ///     actually starts (or restarts) the run — records are born un-attempted (FR-L2).
  ///     <para>THE WORKSPACE STAMP IS LOAD-BEARING (issue #305 D5). A row that carries no
  ///     workspace is INVISIBLE to orphan repair: <c>OrphanRepairHandler</c> filters
  ///     <c>r.WorkspaceId == workspaceId</c>, so a NULL-workspace row is skipped on every
  ///     session open, forever. Measured before this fix: 0 of 2,078 rows carried a
  ///     workspace, and 131 rows sat marked Running with <c>attempts = 0</c> and no run behind
  ///     them. The stamp is therefore what makes repair structurally able to look, not a nicety
  ///     for the Sessions catalog.</para></summary>
  public static AgentRecord Started(AgentId id, string modelUsed, string? label, string taskPrompt,
      DateTimeOffset createdAt, SpawnContract? contract = null, string? provider = null,
      string? workspaceId = null)
      => new(id, AgentStatus.Running, null, modelUsed, label, taskPrompt, createdAt, null, null,
          workspaceId, Contract: SpawnContract.Encode(contract), Provider: provider ?? UnboundProvider);

  /// <summary>Creates the persisted root session row: the host REPL conversation itself as
  ///     an ordinary agent, Running from creation, bound to its workspace and provider so the
  ///     Sessions catalog can list it and resume can rehydrate it.
  ///     <para><see cref="ModelUsed"/> stamps the model that will serve the first turn when
  ///     the caller resolves one before persisting (the factory's bootstrap resolution —
  ///     eval runs and the Sessions catalog then record the model as fact, not annotation).
  ///     When no model is known at creation the sentinel <see cref="UnassignedModel"/> carries the
  ///     original meaning: no model has served the root yet.</para></summary>
  public static AgentRecord Root(AgentId id, DateTimeOffset createdAt, string workspaceId, string provider,
      string? modelUsed = null)
      => new(id, AgentStatus.Running, null, modelUsed ?? UnassignedModel, "root", "conversation root",
          createdAt, null, null, workspaceId, provider);
}
