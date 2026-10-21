namespace eThangAgent.AgentDomain;

/// <summary>How long a root turn may wait before the caller is told it cannot be answered.
/// <para>TWO bounds, because one number cannot be right: <see cref="Idle"/> resets on every
///     event the host streams, so a HEALTHY but long turn is never cut mid-stream; <see
///     cref="Ceiling"/> caps total duration so a host that streams forever still ends. A
///     single total bound would kill the first case and misreport it as a host fault.</para>
/// <para>Lives in the domain because it is a policy about how long the app waits for a
///     conversation turn — not a wire detail — and both the composition root and the
///     transport ACL must express it.</para></summary>
public sealed record RootTurnBudget(TimeSpan Idle, TimeSpan Ceiling)
{
  /// <summary>The production default: two minutes of silence, one hour in total.</summary>
  public static readonly RootTurnBudget Default = new(TimeSpan.FromMinutes(2), TimeSpan.FromHours(1));
}
