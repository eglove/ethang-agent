using System.Collections.Frozen;
using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain;

/// <summary>Which wake reasons a wait accepts. UserMessage is ALWAYS active
///     (spec decision 1: the unconditional user wake) — the filter cannot exclude it.
///     'settled' is accepted for expressiveness but is a no-op: settlement always ends
///     the wait with the outcome regardless.</summary>
public sealed class WakeFilter
{
  private readonly HashSet<WakeReason> _reasons;

  private WakeFilter(HashSet<WakeReason> reasons) => _reasons = reasons;

  /// <summary>Parses the wakeOn argument: "all" or a comma list of
  ///     message|idle|settled|state|stream|budget|mailbox. Invalid tokens
  ///     fail with InvalidActionInput naming the token. 'progress' is RETIRED: a child's
  ///     progress beats fire on every tool call, so a wait that woke on them never rests.</summary>
  public static Result<WakeFilter> Parse(string? raw)
  {
    HashSet<WakeReason> reasons = [];
    if (string.IsNullOrWhiteSpace(raw) || raw.Trim() == "message")
    {
      return Result.Success(new WakeFilter(reasons)); // default: only the always-on user wake
    }

    if (string.Equals(raw.Trim(), "all", StringComparison.OrdinalIgnoreCase))
    {
      foreach (WakeReason reason in (WakeReason[])[WakeReason.Idle, WakeReason.StreamStall,
        WakeReason.StateWrite, WakeReason.Budget, WakeReason.Mailbox, WakeReason.Plan])
      {
        _ = reasons.Add(reason);
      }
      return Result.Success(new WakeFilter(reasons));
    }

    foreach (string token in raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
    {
      // TryGetValue distinguishes absent (invalid) from recognized: the recognized
      // no-op tokens map to null ('message' — the user wake is always on — and
      // 'settled' — settlement ends the wait on its own).
      if (!TokenReasons.TryGetValue(token.ToUpperInvariant(), out WakeReason? reason))
      {
        return Result.Failure<WakeFilter>(new DomainError("InvalidActionInput",
            $"'wakeOn' token '{token}' is not one of: message, idle, settled, state, stream, budget, mailbox, plan, all."));
      }

      if (reason is { } known)
      {
        _ = reasons.Add(known);
      }
    }

    return Result.Success(new WakeFilter(reasons));
  }

  /// <summary>Token -> reason lookup: null marks a recognized no-op token.</summary>
  private static readonly FrozenDictionary<string, WakeReason?> TokenReasons =
      new Dictionary<string, WakeReason?>(StringComparer.Ordinal)
      {
        ["IDLE"] = WakeReason.Idle,
        ["STATE"] = WakeReason.StateWrite,
        ["STREAM"] = WakeReason.StreamStall,
        ["BUDGET"] = WakeReason.Budget,
        ["MAILBOX"] = WakeReason.Mailbox,
        ["PLAN"] = WakeReason.Plan,
        ["MESSAGE"] = null,
        ["SETTLED"] = null,
      }.ToFrozenDictionary();

  public bool Accepts(WakeReason reason)
      => reason == WakeReason.UserMessage || _reasons.Contains(reason);
}

/// <summary>One wait invocation's inputs: the child id, the parsed filter, and the
///     optional soft timeout (expiry is an annotation, never an error).</summary>
public sealed record WaitSpec(AgentId Id, WakeFilter Filter, TimeSpan? Timeout);

/// <summary>The wait's outcome: settled (outcome present) or woken/expired
///     (reason plus the accumulated digest lines, oldest first).</summary>
public sealed record WaitResult(
    bool Settled,
    AgentRunOutcome? Outcome,
    WakeReason? Reason,
    IReadOnlyList<string> DigestLines,
    DomainError? Error = null);

/// <summary>The wake-aware wait core: races the child's settle task against wake
///     signals and the optional soft timeout. Subscribes FIRST (no lost-wake window),
///     then checks settlement. Signals arriving while the wait is returning are
///     dropped (the lease is disposed before the return is built). The settle awaiter
///     is a delegate over the runtime's WhenSettledAsync so the type stays testable.
///     Timeout expiry renders as a wake with no digest lines — an annotation, never
///     an error (spec: WakeTimeout is not an error code).</summary>
public sealed class WakeAwareWait(IWaitWakeSource source, Func<CancellationToken, Task<Result<AgentRunOutcome>>> settleAwaiter, TimeProvider clock)
{
  public async Task<WaitResult> RunAsync(WaitSpec spec, CancellationToken ct)
  {
    ArgumentNullException.ThrowIfNull(spec);
    List<string> digests = [];
    WakeReason? firstReason = null;
    TaskCompletionSource<bool> woken = new(TaskCreationOptions.RunContinuationsAsynchronously);

    void OnWake(WakeSignal signal)
    {
      if (!spec.Filter.Accepts(signal.Reason))
      {
        return;
      }

      lock (digests)
      {
        firstReason ??= signal.Reason;
        digests.Add(signal.DigestLine);
      }

      _ = woken.TrySetResult(true);
    }

    using IDisposable lease = source.Subscribe(OnWake);

    // Settlement check AFTER subscribing: a child that settled between the caller's
    // status check and here is caught by the await below, not lost.
    Task<Result<AgentRunOutcome>> settleTask = settleAwaiter(ct);
    Task firstSettled = await Task.WhenAny(settleTask, woken.Task, TimeoutTask(spec.Timeout, ct)).ConfigureAwait(false);

    if (firstSettled == settleTask)
    {
      Result<AgentRunOutcome> outcome = await settleTask.ConfigureAwait(false);
      return outcome.IsSuccess
          ? new WaitResult(true, outcome.Value, null, [])
          : new WaitResult(false, null, null, [], outcome.Error); // runtime errors (NotFound, Cancelled) flow to the caller verbatim
    }

    // Cancellation propagates: the timeout task completes Canceled when ct fires —
    // observe its exception so the OCE flows to the caller (the provider maps it to
    // Error [Cancelled] exactly as the legacy wait did).
    await firstSettled.ConfigureAwait(false); // rethrows OCE when the timeout task was cancelled

    WakeReason reason = firstReason ?? WakeReason.UserMessage; // timeout: no signals, renders as the timeout reason
    string[] snapshot;
    lock (digests)
    {
      snapshot = [.. digests];
    }

    return new WaitResult(false, null, firstSettled == woken.Task ? reason : null, snapshot);
  }

  private async Task TimeoutTask(TimeSpan? timeout, CancellationToken ct)
  {
    if (timeout is not { } span)
    {
      await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
      return;
    }

    await Task.Delay(span, clock, ct).ConfigureAwait(false);
  }
}
