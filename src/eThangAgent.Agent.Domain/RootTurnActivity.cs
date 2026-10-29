namespace eThangAgent.AgentDomain;

/// <summary>The phases a ROOT session's own turn loop reports (issue #301 D5). These are
///     facts the root loop already produces, which is why the root's idle clock can be
///     sourced from them:
///     <list type="bullet">
///     <item>a turn STARTED</item>
///     <item>a tool dispatch BEGAN</item>
///     <item>a provider stream produced a fragment</item>
///     <item>a turn SETTLED</item>
///     </list>
///     <para>DELIBERATELY NOT <see cref="ChildProgressEvent"/>: that event exists to describe a
///     CHILD run, and #305 retired the child runtime while #316 retires the sibling machinery.
///     A clock fed by it would stop ticking the moment those land, and the failure would be
///     silent - a root that never went idle and therefore was never nudged.</para>
///     <para>And deliberately not a wall-clock timer: that is the mechanism <c>ChildTimeout</c>
///     was deleted for. The clock measures the age of a real fact, never elapsed time on its
///     own.</para></summary>
public enum RootTurnPhase
{
  /// <summary>A turn began.</summary>
  TurnStarted,

  /// <summary>A tool dispatch began inside the turn.</summary>
  ToolStarted,

  /// <summary>The provider stream produced a fragment (content or reasoning).</summary>
  Streamed,

  /// <summary>The turn settled: the session is idle by definition.</summary>
  TurnSettled,
}

/// <summary>One root-turn activity fact. Metadata only, like the child event family: a phase
///     and a short label, never transcript content.</summary>
public sealed record RootTurnActivity(AgentId SessionId, DateTimeOffset At, RootTurnPhase Phase, string Label)
{
  /// <summary>True when this fact means the session is WORKING, so its idle clock resets.
  ///     A settled turn is the opposite: it is the moment idleness begins.</summary>
  public bool IsWork => Phase != RootTurnPhase.TurnSettled;

  public override string ToString() => $"{Phase} {Label} at {At:O}";
}
