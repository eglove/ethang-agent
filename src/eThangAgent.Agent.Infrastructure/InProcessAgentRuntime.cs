using System.Collections.Concurrent;
using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.AgentInfrastructure;

/// <summary>In-process actor runtime: every accepted child runs to completion on one background task
/// while the caller continues. A strict concurrency cap is enforced with a zero-timeout slot wait —
/// at-capacity starts fail with <see cref="RuntimeErrors.CapReached"/> and produce no side effects.
/// Each active run owns a CancellationTokenSource registered here, so <see cref="Interrupt"/> can
/// cancel one or all runs; runners observe the token and persist well-formed terminal outcomes. a CancellationTokenSource registered here, so <see cref="Interrupt"/> can
/// cancel one or all runs; runners observe the token and persist well-formed terminal outcomes.</summary>
#pragma warning disable CA1001 // Types that own disposable fields should be disposable
// Named decision: the runtime is a process-lifetime singleton owned by the composition
// root; disposing the semaphore on teardown adds no value.
public sealed class InProcessAgentRuntime : IAgentRuntime
{
  private readonly IAgentRunner _runner;
  private readonly IAgentStore _store;
  private readonly SemaphoreSlim _slots;
  private readonly ConcurrentDictionary<AgentId, CancellationTokenSource> _active = [];

  public InProcessAgentRuntime(IAgentRunner runner, IAgentStore store, int maxConcurrentAgents)
  {
    ArgumentNullException.ThrowIfNull(runner);
    ArgumentNullException.ThrowIfNull(store);
    if (maxConcurrentAgents < 1)
    {
      throw new ArgumentOutOfRangeException(nameof(maxConcurrentAgents), maxConcurrentAgents,
          "MaxConcurrentAgents must be at least 1.");
    }

    _runner = runner;
    _store = store;
    _slots = new SemaphoreSlim(maxConcurrentAgents, maxConcurrentAgents);
  }

  public Task<Result<AgentId>> Start(AgentRecord record, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(record);
    if (!_slots.Wait(0, CancellationToken.None))
    {
      return Task.FromResult(Result.Failure<AgentId>(CapError()));
    }

    // Named decision (CA2000): ownership of the CTS transfers to _active; it is disposed
    // in RunToCompletionAsync's finally when the run settles.
#pragma warning disable CA2000 // Call IDisposable.Dispose on object created by
    CancellationTokenSource cts = new();
#pragma warning restore CA2000 // Call IDisposable.Dispose on object created by
    _active[record.Id] = cts;
    _ = Task.Run(() => RunToCompletionAsync(record, cts), CancellationToken.None);
    return Task.FromResult(Result.Success<AgentId>(record.Id));
  }

  public void Interrupt(AgentId? childId = null)
  {
    if (childId is { } id)
    {
      // Named decision (CA2000): the CTS is cancelled here; disposal belongs to the
      // run's finally, which owns its lifetime.
#pragma warning disable CA2000 // Call IDisposable.Dispose on object created by
      if (_active.TryRemove(id, out CancellationTokenSource? cts))
#pragma warning restore CA2000 // Call IDisposable.Dispose on object created by
      {
        cts.Cancel();
      }

      return;
    }
    foreach (AgentId runningId in _active.Keys)
    {
      Interrupt(runningId);
    }
  }

  private async Task RunToCompletionAsync(AgentRecord record, CancellationTokenSource cts)
  {
    try
    {
      AgentRunOutcome outcome = await _runner.RunAsync(record, cts.Token).ConfigureAwait(false);
      _ = await _store.UpdateAsync(record with
      {
        Status = outcome.Status,
        FailureReason = outcome.Reason,
        CompletedAt = DateTimeOffset.UtcNow,
        FinalReport = outcome.Report,
      }).ConfigureAwait(false);
    }
    // Named decision (CA1031): the runtime is an actor boundary - ANY runner fault must
    // become a well-formed Failed outcome for agent.result retrieval, never a crash.
#pragma warning disable CA1031 // Do not catch general exception types
    catch (Exception ex)
    {
      // Runner faults are terminal child outcomes, not crashes: persist them so the parent
      // can retrieve a well-formed failure via agent.result.
      _ = await _store.UpdateAsync(record with
      {
        Status = AgentStatus.Failed,
        FailureReason = AgentFailureReason.ProviderError,
        CompletedAt = DateTimeOffset.UtcNow,
        FinalReport = "Error [ProviderError]: " + ex.Message,
      }).ConfigureAwait(false);
    }
    finally
    {
      if (_active.TryRemove(record.Id, out CancellationTokenSource? removed))
      {
        removed.Dispose();
      }

      _ = _slots.Release();
    }
  }

  /// <summary>Translates the canonical CapReached string into an <see cref="DomainError"/> without duplicating
  /// its text: "Error [Code]: message" becomes Error(Code, message), so downstream pass-through rendering
  /// reproduces <see cref="RuntimeErrors.CapReached"/> byte-for-byte.</summary>
  private static DomainError CapError()
  {
    string canonical = RuntimeErrors.CapReached;
    int codeStart = canonical.IndexOf('[', StringComparison.Ordinal) + 1;
    int codeEnd = canonical.IndexOf(']', codeStart);
    return new DomainError(canonical[codeStart..codeEnd], canonical[(codeEnd + 3)..]);
  }
}

#pragma warning restore CA1001 // Types that own disposable fields should be disposable
