using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain;

/// <summary>Store-backed child transcript sink: every AppendAsync lands the message in
///     the IAgentStore immediately (CancellationToken.None — persistence never observes
///     the run's cancellation token), so an interrupt leaves the transcript persisted up
///     to the last safe point. A serialized pending queue absorbs bursts without making
///     the loop wait; FlushAsync drains it at terminal.
///     <para>The drain is SINGLE-FLIGHT: AppendAsync is fire-and-forget from the loop's
///     mutation callback, so several appends can be in flight at once. Without the guard
///     two drainers peek the same head message and the second Dequeue throws
///     InvalidOperationException("Queue empty") — an intermittent child failure
///     (2026-10-19: ~1 in 10 nested-spawn E2E runs failed this way). FlushAsync waits the
///     active drain out instead of returning early, so a terminal flush never reports a
///     count the store has not reached.</para></summary>
public sealed class ChildTranscriptStore(IAgentStore store, AgentId childId) : IChildTranscriptStore, IAsyncDisposable
{
  private readonly Queue<Message> _pending = new();
  private int _flushed;
  private readonly Lock _lock = new();
  private Task _drainTask = Task.CompletedTask;
  private bool _draining;

  public async Task AppendAsync(Message message)
  {
    ArgumentNullException.ThrowIfNull(message);
    lock (_lock)
    {
      _pending.Enqueue(message);
    }

    await DrainAsync().ConfigureAwait(false);
  }

  /// <summary>Persists every queued message, waiting out any drain already in flight. A
  ///     failed append stays queued (best-effort persistence) and is retried by the next
  ///     flush; the queue is never reordered.</summary>
  public async Task FlushAsync()
  {
    while (true)
    {
      await DrainAsync().ConfigureAwait(false);
      lock (_lock)
      {
        if (_pending.Count == 0)
        {
          return;
        }
      }
    }
  }

  public Task<int> FlushedCount()
  {
    lock (_lock)
    {
      return Task.FromResult(_flushed);
    }
  }

  public async Task ReplaceAsync(IReadOnlyList<Message> messages)
  {
    ArgumentNullException.ThrowIfNull(messages);
    lock (_lock)
    {
      _pending.Clear();
      _flushed = messages.Count;
    }

    _ = await store.ReplaceTranscriptAsync(childId, messages, CancellationToken.None).ConfigureAwait(false);
  }

  /// <summary>Terminal flush: disposes by draining any pending messages.</summary>
  public ValueTask DisposeAsync() => new(FlushAsync());

  /// <summary>Starts the one active drain, or hands back the one already running. The
  ///     drain holds the single-flight flag from start to completion, so no second drainer
  ///     can peek the same head message.</summary>
  private Task DrainAsync()
  {
    TaskCompletionSource completion;
    lock (_lock)
    {
      if (_draining)
      {
        return _drainTask;
      }

      if (_pending.Count == 0)
      {
        return Task.CompletedTask;
      }

      _draining = true;
      completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      _drainTask = completion.Task;
    }

    return DrainCoreAsync(completion);
  }

  /// <summary>Serializes pending messages to the store in order. A failed append is
  ///     left pending (best-effort persistence): the terminal FlushAsync retries it.
  ///     CancellationToken.None everywhere: persistence must survive the run's cancel.</summary>
  private async Task DrainCoreAsync(TaskCompletionSource completion)
  {
    try
    {
      while (true)
      {
        Message? next;
        lock (_lock)
        {
          next = _pending.Count > 0 ? _pending.Peek() : null;
        }

        if (next is null)
        {
          return;
        }

        Result<string> appended = await store.AppendMessageAsync(childId, next, CancellationToken.None).ConfigureAwait(false);
        if (!appended.IsSuccess)
        {
          return; // stays pending; the terminal FlushAsync retries it
        }

        lock (_lock)
        {
          _ = _pending.Dequeue();
          _flushed++;
        }
      }
    }
    finally
    {
      lock (_lock)
      {
        _draining = false;
      }

      _ = completion.TrySetResult();
    }
  }
}
