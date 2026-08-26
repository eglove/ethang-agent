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
    string? FinalReport)
{
  public static AgentRecord Spawned(AgentId id, AgentId? parentId, int depth, string modelUsed, string? label, string taskPrompt, DateTimeOffset createdAt)
      => new(id, parentId, depth, AgentStatus.Running, null, modelUsed, label, taskPrompt, createdAt, null, null);

  /// <summary>Creates the persisted root session row: the host REPL conversation itself as an
  ///     ordinary depth-0 agent with no parent, Running from creation.
  ///     <para><see cref="ModelUsed"/> carries the sentinel <c>"unassigned"</c>: no model has
  ///     served the root yet. Unlike spawned children, whose model is chosen per spawn, the
  ///     root's exchanges run through the host's own configured model, so no assignment is
  ///     recorded at row creation.</para></summary>
  public static AgentRecord Root(AgentId id, DateTimeOffset createdAt)
      => new(id, null, 0, AgentStatus.Running, null, "unassigned", "root", "conversation root", createdAt, null, null);
}
