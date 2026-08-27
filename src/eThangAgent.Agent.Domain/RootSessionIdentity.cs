namespace eThangAgent.AgentDomain;

/// <summary>Holds the persisted root session id for a composed container. The session factory
///     sets it immediately after the session factory persists the root row, so services
///     that need the root id (e.g. root model resolution persisting
///     <see cref="AgentRecord.ModelUsed"/>) can resolve it lazily without the container being
///     rebuilt. Before the factory sets it, the id is null and consumers must tolerate that
///     (resolution still serves a model; persistence is skipped).</summary>
public sealed class RootSessionIdentity
{
  /// <summary>The root session id, or null before the factory persists the root row.</summary>
  public AgentId? Id { get; set; }
}
