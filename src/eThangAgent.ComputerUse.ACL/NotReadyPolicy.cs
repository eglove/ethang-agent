namespace eThangAgent.ComputerUse.ACL;

/// <summary>The cold-start not-ready schedule (spec 1.3): when the pipe is not yet
///     accepting (broker warm-up), the SAME request is retried with backoff
///     250/500/750/1000/1500 ms for at most 6 total attempts, then fails TIMEOUT.
///     The clock is injectable so tests run instantly.</summary>
public sealed class NotReadyPolicy(INotReadyDelayer delayer)
{
  /// <summary>The backoff schedule, indexed by attempt (0-based, before the first retry).
  ///     Attempt 1 is immediate - the schedule applies from the second attempt.</summary>
  public static readonly TimeSpan[] BackoffSchedule =
  [
    TimeSpan.Zero,
    TimeSpan.FromMilliseconds(250),
    TimeSpan.FromMilliseconds(500),
    TimeSpan.FromMilliseconds(750),
    TimeSpan.FromMilliseconds(1000),
    TimeSpan.FromMilliseconds(1500),
  ];

  /// <summary>Total attempts allowed before the policy gives up: the first try plus five retries.</summary>
  public const int MaxAttempts = 6;

  /// <summary>The failure the policy surfaces after exhausting the schedule: TIMEOUT,
  ///     which the surface hint table reports retryable.</summary>
  public const string GiveUpCode = "TIMEOUT";

  private readonly INotReadyDelayer _delayer = delayer ?? throw new ArgumentNullException(nameof(delayer));

  /// <summary>Waits the backoff for the given 0-based attempt index. True when the caller
  ///     may retry (attempt index inside the schedule), false when attempts are exhausted.</summary>
  public async Task<bool> WaitBeforeRetryAsync(int attempt, CancellationToken ct = default)
  {
    if (attempt < 0 || attempt >= BackoffSchedule.Length)
    {
      return false;
    }

    await _delayer.DelayAsync(BackoffSchedule[attempt], ct).ConfigureAwait(false);
    return true;
  }
}

/// <summary>The delay seam: production uses Task.Delay, tests use a fake clock.</summary>
public interface INotReadyDelayer
{
  Task DelayAsync(TimeSpan delay, CancellationToken ct = default);
}

/// <summary>Production delayer: real Task.Delay.</summary>
public sealed class TaskDelayer : INotReadyDelayer
{
  public static readonly TaskDelayer Instance = new();

  public async Task DelayAsync(TimeSpan delay, CancellationToken ct = default) =>
    await Task.Delay(delay, ct).ConfigureAwait(false);
}
