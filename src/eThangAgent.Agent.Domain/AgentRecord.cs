namespace eThangAgent.AgentDomain;

public sealed record AgentRecord(
    AgentId Id,
    AgentId? ParentId,
    int Depth,
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
    ChildPhase? Phase = null,
    string? Contract = null)
{
  /// <summary>Creates a freshly spawned child row. When a spawn contract is supplied it is
  ///     serialized into the Contract column so resume and audit see the agreement the run
  ///     started with. Attempts/Phase stay at their defaults: the RUNTIME writes them when it
  ///     actually starts (or restarts) the run — records are born un-attempted (FR-L2).</summary>
  public static AgentRecord Spawned(AgentId id, AgentId? parentId, int depth, string modelUsed, string? label, string taskPrompt, DateTimeOffset createdAt, SpawnContract? contract = null)
      => new(id, parentId, depth, AgentStatus.Running, null, modelUsed, label, taskPrompt, createdAt, null, null, Contract: SpawnContract.Encode(contract));

  /// <summary>Creates the persisted root session row: the host REPL conversation itself as an
  ///     ordinary depth-0 agent with no parent, Running from creation, bound to its workspace
  ///     and provider so the Sessions catalog can list it and resume can rehydrate it.
  ///     <para><see cref="ModelUsed"/> carries the sentinel <c>"unassigned"</c>: no model has
  ///     served the root yet. Unlike spawned children, whose model is chosen per spawn, the
  ///     root's exchanges run through the host's own configured model, so no assignment is
  ///     recorded at row creation.</para></summary>
  public static AgentRecord Root(AgentId id, DateTimeOffset createdAt, string workspaceId, string provider)
      => new(id, null, 0, AgentStatus.Running, null, "unassigned", "root", "conversation root", createdAt, null, null, workspaceId, provider);
}
