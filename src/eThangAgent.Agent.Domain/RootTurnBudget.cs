namespace eThangAgent.AgentDomain;

/// <summary>How long a root turn may wait before the caller is told it cannot be answered.
/// <para>TWO bounds, because one number cannot be right, and a THIRD was removed: there is
///     deliberately no total-duration ceiling (issue #248). A wall-clock cap on a whole turn
///     has no practical use — long turns are legitimate (big investigations, long test runs,
///     waiting on subagents) — and it only ever killed work in progress that could not be
///     resumed. What remains is <see cref="Idle"/>, which resets on every event the host
///     streams so a HEALTHY but long turn is never cut mid-stream, and <see cref="Send"/>,
///     which bounds putting ONE frame on the wire — a different question, because a
///     slow-but-healthy host may legitimately take a long time to absorb a large frame.</para>
/// <para>The idle bound measures LIVENESS, not busyness (2026-10-22). A long tool call — an
///     exec script, a sub-agent wait — emits NOTHING, so silence used to mean "busy" and a
///     healthy host was reported as unaddressable; the app then abandoned the turn while the
///     host kept running it, and that abandoned turn held the host's turn gate, so every
///     later turn queued behind it and failed the same way. The host now HEARTBEATS while a
///     turn is quiet, and the window it must beat rides the turn request — so the bound
///     fires only when the host genuinely stopped answering.</para>
/// <para>The send bound is deliberately SEPARATE and generous. A bounded write is cancelled
///     when it lapses, and a cancellation that lands mid-frame desynchronizes the peer's frame
///     stream and costs the whole connection; sharing <see cref="Idle"/> (two minutes) would
///     therefore drop a slow-but-healthy host's connection for a frame it was still digesting.
///     It is not derived from anything else — a frame is bounded work (at most the 4MB seam
///     limit), so minutes are generous for it while an hour would let a dead peer block a turn
///     that has no other bound left.</para>
/// <para>Lives in the domain because it is a policy about how long the app waits for a
///     conversation turn — not a wire detail — and both the composition root and the
///     transport ACL must express it.</para></summary>
public sealed record RootTurnBudget
{
  /// <summary>The production default: two minutes of silence, and five minutes to put ONE
  ///     frame on the wire.</summary>
  public static readonly RootTurnBudget Default = new(
      TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5));

  /// <summary>Builds a budget. Every bound is validated HERE and in the init accessors, so
  ///     neither construction nor a 'with' expression can produce an invalid value.</summary>
  public RootTurnBudget(TimeSpan idle, TimeSpan send)
  {
    Idle = idle;
    Send = send;
  }

  /// <summary>Strict boundary: the idle bound must be POSITIVE. A zero idle would make every
  ///     turn instantly unaddressable, so it is never silently accepted — the value is
  ///     production configuration and is validated like one.
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

  /// <summary>Strict boundary: the send bound must be positive (same reasoning as
  ///     <see cref="Idle"/> — guarded in the setter so a 'with' cannot bypass it). A zero
  ///     send bound would cancel every frame before its header could be written.</summary>
  public TimeSpan Send
  {
    get;
    init => field = value > TimeSpan.Zero
        ? value
        : throw new ArgumentOutOfRangeException(nameof(Send), value,
            "RootTurnBudget.Send must be positive.");
  }
}
