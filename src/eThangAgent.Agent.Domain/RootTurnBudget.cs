namespace eThangAgent.AgentDomain;

/// <summary>How long a root turn may wait before the caller is told it cannot be answered.
/// <para>TWO bounds, because one number cannot be right: <see cref="Idle"/> resets on every
///     event the host streams, so a HEALTHY but long turn is never cut mid-stream; <see
///     cref="Ceiling"/> caps total duration so a host that streams forever still ends. A
///     single total bound would kill the first case and misreport it as a host fault.</para>
/// <para>Lives in the domain because it is a policy about how long the app waits for a
///     conversation turn — not a wire detail — and both the composition root and the
///     transport ACL must express it.</para></summary>
public sealed record RootTurnBudget
{
  /// <summary>Builds a budget. Both bounds are validated HERE and in the init accessors, so
  ///     neither construction nor a 'with' expression can produce an invalid value.</summary>
  public RootTurnBudget(TimeSpan idle, TimeSpan ceiling)
  {
    Idle = idle;
    Ceiling = ceiling;
  }

  /// <summary>The production default: two minutes of silence, one hour in total.</summary>
  public static readonly RootTurnBudget Default = new(TimeSpan.FromMinutes(2), TimeSpan.FromHours(1));

  /// <summary>Strict boundary: both bounds must be POSITIVE. A zero idle would make every
  ///     turn instantly unaddressable and a non-positive ceiling is meaningless, so neither
  ///     is silently accepted — the value is production configuration and is validated like
  ///     one.
  /// <para>The guard lives in the SETTER, not a property initializer: an initializer runs
  ///     only in the constructor, so <c>budget with { Idle = TimeSpan.Zero }</c> would slip
  ///     past it. A 'with' expression copies the record then assigns through the init
  ///     accessors, so guarding there closes every construction path.</para></summary>
  public TimeSpan Idle
  {
    get;
    init => field = value > TimeSpan.Zero
        ? value
        : throw new ArgumentOutOfRangeException(nameof(Idle), value,
            "RootTurnBudget.Idle must be positive.");
  }

  /// <summary>Strict boundary: the total-duration ceiling must be positive (same reasoning
  ///     as <see cref="Idle"/> — guarded in the setter so a 'with' cannot bypass it).</summary>
  public TimeSpan Ceiling
  {
    get;
    init => field = value > TimeSpan.Zero
        ? value
        : throw new ArgumentOutOfRangeException(nameof(Ceiling), value,
            "RootTurnBudget.Ceiling must be positive.");
  }
}
