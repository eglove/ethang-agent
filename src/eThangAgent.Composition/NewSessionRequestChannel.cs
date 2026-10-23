using System.Text.Json;
using eThangAgent.SharedKernel;
using eThangAgent.Transport.ACL;

namespace eThangAgent.Composition;

/// <summary>Per-container carrier for the LIVE root turn's session-request emitter
///     (issue #234): the host's turn runner fills it at turn start and
///     clears it in the turn's finally, exactly like the conversation-wide per-turn state
///     it rides. The <see cref="WireNewSessionPort"/> emits through it; the
///     ChildHostServer completes pending requests when the app's <c>session-response</c>
///     envelope arrives. A request emitted with no live turn (or a disconnected channel)
///     fails immediately — never a hang.
///     <para>ERROR CODES (the channel's own contract, named here because the tool's
///     description enumerates them): <c>SessionRequestUnreachable</c> when no turn is
///     connected or the turn ends while a request is pending — the request cannot be
///     carried to the app; <c>SessionRequestCancelled</c> when the caller's token
///     cancels a pending request. The app's own typed error (e.g.
///     <c>SessionLaunchFailed</c>) is propagated verbatim on a refused launch.</para>
///     <para>Thread-safe: the emitter is set and cleared from the turn's thread while
///     requests arrive from the tool's execution context; the pending map is guarded by
///     one lock. No DI container references.</para></summary>
public sealed class NewSessionRequestChannel
{
  // Set by the host's turn code per turn; null between turns. A volatile read is enough:
  // the emitter is replaced whole (never mutated in place), and the pending map carries
  // the fine-grained synchronization.
  private Action<string>? _emitter;
  private readonly Lock _gate = new();
  private readonly Dictionary<string, TaskCompletionSource<Result<string>>> _pending = new(StringComparer.Ordinal);

  /// <summary>Connects the channel to one turn's emitter. Called at turn start; a
  ///     previous turn's emitter is replaced (turns are serialized by the turn gate).</summary>
  public void Connect(Action<string> emitSessionRequest)
  {
    ArgumentNullException.ThrowIfNull(emitSessionRequest);
    lock (_gate)
    {
      _emitter = emitSessionRequest;
    }
  }

  /// <summary>Disconnects at turn end (the finally). Pending requests complete as
  ///     <c>SessionRequestUnreachable</c> failures — the turn that could carry them to
  ///     the app is gone, so no answer can ever arrive.</summary>
  public void Disconnect()
  {
    lock (_gate)
    {
      _emitter = null;
      FailAll("SessionRequestUnreachable",
          "the turn that carried this session-request has ended before the app answered.");
    }
  }

  /// <summary>True when a turn's emitter is connected.</summary>
  public bool IsConnected
  {
    get
    {
      lock (_gate)
      {
        return _emitter is not null;
      }
    }
  }

  /// <summary>Emits one session-request and returns the Task that completes with the
  ///     app's answer. The answer is the serialized session id on success, or a failure
  ///     carrying the app's typed error code. Fails immediately
  ///     (<c>SessionRequestUnreachable</c>) when no turn is connected, and as
  ///     <c>SessionRequestCancelled</c> when <paramref name="ct"/> cancels the wait.
  ///     <paramref name="requestId"/> must be unique per call.</summary>
  public Task<Result<string>> RequestAsync(string requestId, string prompt, string? handoffSummary,
      CancellationToken ct = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
    Action<string>? emit;
    lock (_gate)
    {
      emit = _emitter;
      if (emit is null)
      {
        return Task.FromResult(Result.Failure<string>(new DomainError("SessionRequestUnreachable",
            "no live turn is connected to carry the session-request to the app.")));
      }

      if (ct.IsCancellationRequested)
      {
        return Task.FromResult(Result.Failure<string>(new DomainError("SessionRequestCancelled",
            "the session-request was cancelled before it was emitted.")));
      }

      TaskCompletionSource<Result<string>> waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
      _pending.Add(requestId, waiter);
      // The emission happens under the lock so a Disconnect racing this request cannot
      // strand it: either the emitter is captured and the request is pending BEFORE the
      // turn's finally runs FailAll, or the emitter is already null and the request
      // fails above.
      emit(JsonSerializer.Serialize(new SessionRequestPayload(requestId, prompt, handoffSummary)));
      Task<Result<string>> pending = AwaitAnswered(waiter.Task, ct);
      return pending;
    }
  }

  /// <summary>Awaits the pending request's answer with the caller's token: a cancel
  ///     while the request is pending completes it as <c>SessionRequestCancelled</c> —
  ///     a typed failure through the Result seam, never an exception across it. The
  ///     underlying waiter is left for Complete/Disconnect to settle (first-wins
  ///     TrySetResult), so a late answer cannot resurrect a cancelled request.</summary>
  private static async Task<Result<string>> AwaitAnswered(Task<Result<string>> answered, CancellationToken ct)
  {
    try
    {
      return await answered.WaitAsync(ct).ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
      return Result.Failure<string>(new DomainError("SessionRequestCancelled",
          "the session-request was cancelled while waiting for the app's answer."));
    }
  }

  /// <summary>Completes one pending request with the app's answer (the ChildHostServer's
  ///     session-response handler). Unknown requestIds are ignored (a late or duplicate
  ///     response for an already-completed request).</summary>
  public void Complete(string requestId, Result<string> outcome)
  {
    ArgumentNullException.ThrowIfNull(outcome);
    TaskCompletionSource<Result<string>>? waiter;
    lock (_gate)
    {
      if (!_pending.Remove(requestId, out waiter))
      {
        return; // late or duplicate response: nothing to complete
      }
    }

    _ = waiter.TrySetResult(outcome);
  }

  /// <summary>Fails every pending request (turn teardown). Called with the lock held by
  ///     Disconnect; the completion itself happens outside the lock (TrySet* never
  ///     runs user continuations inline under RunContinuationsAsynchronously).</summary>
  private void FailAll(string code, string message)
  {
    KeyValuePair<string, TaskCompletionSource<Result<string>>>[] stranded = [.. _pending];
    _pending.Clear();
    foreach ((_, TaskCompletionSource<Result<string>> waiter) in stranded)
    {
      _ = waiter.TrySetResult(Result.Failure<string>(new DomainError(code, message)));
    }
  }
}
