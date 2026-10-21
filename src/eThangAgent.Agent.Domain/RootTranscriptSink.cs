using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain;

/// <summary>The ROOT conversation's continuous transcript sink (spec D9): the root persists
///     every message at the safe point it is added, exactly as a child run does, so a killed
///     conversation process loses at most the in-flight tool call - never the turn.
///     <para>The root id is resolved LAZILY and cached on first success: the composition's
///     RootSessionIdentity is filled after the container builds, so a value captured at
///     construction would always be absent. A sink with no root id is INERT - it persists
///     nothing and reports zero rather than throwing - because headless containers that
///     never persisted a root row still run turns, and the end-of-turn append covers them.</para>
///     <para>Two properties make the continuous write safe to combine with that end-of-turn
///     append. First, message REFERENCE deduplication: the agent subscribes to the
///     conversation on every build and the root agent is rebuilt whenever its model changes,
///     so one conversation can carry several live subscriptions; each Add* creates a fresh
///     Message instance, so the only way the same reference arrives twice is a second
///     subscription - exactly what must not double-persist. Second, a single-flight drain
///     over an ordered queue: concurrent appends can never persist one message twice nor
///     reorder the exchange, and FlushAsync waits for the in-flight drain instead of
///     reporting a stale count (a stale count would make the end-of-turn append write a
///     message the sink is about to write - the doubled turn that corrupts resume).</para>
///     <para>Persistence always runs with <see cref="CancellationToken.None"/>: it must
///     survive the cancellation of the turn it is recording.</para></summary>
public sealed class RootTranscriptSink(IAgentStore store, Func<AgentId?> rootIdSource)
    : IChildTranscriptStore, IDisposable, IAsyncDisposable
{
  private readonly IAgentStore _store = store ?? throw new ArgumentNullException(nameof(store));
  private readonly Func<AgentId?> _rootIdSource = rootIdSource ?? throw new ArgumentNullException(nameof(rootIdSource));
  private readonly Lock _lock = new();
  private readonly Queue<Message> _pending = new();
  private Task _drainTask = Task.CompletedTask;
  private Message? _lastAccepted;
  private AgentId? _rootId;
  private int _flushed;
  private bool _draining;

  /// <summary>The root id this sink persists under, or null while the session has none
  ///     (an inert sink). Resolved once and cached: a session's root id never changes.</summary>
  public AgentId? RootId
  {
    get
    {
      lock (_lock)
      {
        return _rootId ??= _rootIdSource();
      }
    }
  }

  /// <summary>True while the sink can persist: a root id is known.</summary>
  public bool IsActive => RootId is not null;

  /// <summary>True when an earlier append failed and is still waiting to be retried.</summary>
  public bool HasPending => PendingCount > 0;

  /// <summary>Messages queued but not yet persisted (diagnostics and tests).</summary>
  public int PendingCount
  {
    get
    {
      lock (_lock)
      {
        return _pending.Count;
      }
    }
  }

  /// <summary>Persists one message immediately, in conversation order. A repeat of the
  ///     message instance last accepted (a second subscription) is ignored. Fire-and-forget
  ///     safe: the loop never waits on it.</summary>
  public Task AppendAsync(Message message)
  {
    ArgumentNullException.ThrowIfNull(message);
    if (RootId is null)
    {
      return Task.CompletedTask;
    }

    lock (_lock)
    {
      if (ReferenceEquals(_lastAccepted, message))
      {
        return Task.CompletedTask;
      }

      _lastAccepted = message;
      _pending.Enqueue(message);
    }

    return FlushAsync();
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

  /// <summary>Number of conversation messages already persisted. Flushes first: a count
  ///     read while a write is in flight would under-report and make a caller re-append a
  ///     message that is about to land.</summary>
  public async Task<int> FlushedCount()
  {
    await FlushAsync().ConfigureAwait(false);
    lock (_lock)
    {
      return _flushed;
    }
  }

  /// <summary>Replaces the whole persisted transcript (compaction/shrink survival) and
  ///     resets the flushed baseline to the replacement's length.</summary>
  public async Task ReplaceAsync(IReadOnlyList<Message> messages)
  {
    ArgumentNullException.ThrowIfNull(messages);
    AgentId? rootId = RootId;
    lock (_lock)
    {
      _pending.Clear();
      _lastAccepted = messages.Count > 0 ? messages[^1] : null;
    }

    if (rootId is null)
    {
      return;
    }

    _ = await _store.ReplaceTranscriptAsync(rootId.Value, messages, CancellationToken.None).ConfigureAwait(false);
    lock (_lock)
    {
      _flushed = messages.Count;
    }
  }

  /// <summary>Terminal flush: disposes by persisting whatever is still queued.</summary>
  public ValueTask DisposeAsync() => new(FlushAsync());

  /// <summary>Sync dispose for the DI container's shutdown path, which disposes a
  ///     singleton synchronously. Named decision: the flush hops to the threadpool so a
  ///     UI-thread container close cannot deadlock on a store continuation, and the
  ///     result is awaited inline — the pending queue is what this call exists to land.</summary>
  public void Dispose()
  {
    Task flush = Task.Run(FlushAsync);
    flush.GetAwaiter().GetResult();
  }

  /// <summary>Starts the one active drain, or hands back the one already running. The
  ///     drain holds the single-flight flag from start to completion so no second drainer
  ///     can peek the same head message.</summary>
  private Task DrainAsync()
  {
    TaskCompletionSource completion;
    AgentId rootId;
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

      rootId = _rootId ?? throw new InvalidOperationException(
          "a queued message always carries a resolved root id.");
      _draining = true;
      completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      _drainTask = completion.Task;
    }

    return DrainCoreAsync(rootId, completion);
  }

  /// <summary>The drain loop: one message at a time, in queue order, with
  ///     CancellationToken.None. A failed append ends the drain with the message still
  ///     queued (the next flush retries it); a fault ends it the same way and propagates.</summary>
  private async Task DrainCoreAsync(AgentId rootId, TaskCompletionSource completion)
  {
    try
    {
      while (true)
      {
        Message next;
        lock (_lock)
        {
          if (_pending.Count == 0)
          {
            return;
          }

          next = _pending.Peek();
        }

        Result<string> appended = await _store
            .AppendMessageAsync(rootId, next, CancellationToken.None)
            .ConfigureAwait(false);
        if (!appended.IsSuccess)
        {
          return;
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
