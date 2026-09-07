namespace eThangAgent.AgentDomain;

/// <summary>Ambient workspace anchor of the child run currently executing on this async
///     flow: the spawner sets it to the child's contract anchor just before the loop
///     starts and restores the previous value (possibly null) in its finally. Seams that
///     resolve a workspace per execution — the exec engine's workspace delegate — read
///     it FIRST and fall back to the session workspace when it is null, so a running
///     anchored child's scripts (and any grandchild chain beneath it) resolve against
///     the anchor, never the parent session's root. Backed by composition with
///     AsyncLocal storage so concurrent children never observe each other's anchors.</summary>
public interface IWorkspaceAnchorScope
{
  /// <summary>Gets the anchor of the child run on this async flow, or null when no
  ///     anchored child is running here. Set by the spawner around a child run;
  ///     never set by anything else.</summary>
  string? Current { get; set; }
}
