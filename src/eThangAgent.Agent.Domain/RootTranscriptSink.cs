using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain;

/// <summary>The ROOT conversation's continuous transcript sink (spec D9): the root persists
///     every message at the safe point it is added, exactly as a child run does, so a killed
///     conversation process loses at most the in-flight tool call - never the turn.
///     <para>The root id is resolved LAZILY and cached on first success: the composition's
///     RootSessionIdentity is filled after the container builds, so a value captured at
///     construction would always be absent. A sink with no root id is INERT - it persists
///     nothing and reports the conversation length it started from - because headless
///     containers that never persisted a root row still run turns, and the end-of-turn
///     append covers them.</para>
///     <para>The count this sink reports is an ABSOLUTE conversation index, never a count of
///     what this instance happened to write: it starts at the conversation's length when the
///     sink was built (the hydrated prefix of a resumed session, already persisted by the
///     session that wrote it) and advances once per message that lands. A relative count
///     would coincide with the conversation index on a fresh session and diverge on resume
///     and while inert - the two shapes that re-append a whole turn.</para>
///     <para>Two further properties make the continuous write safe to combine with the
///     end-of-turn append. Message REFERENCE deduplication: the agent subscribes the
///     conversation on every build and the root agent is rebuilt whenever its model changes,
///     so one conversation can carry several live subscriptions; each Add* creates a fresh
///     Message instance, so the only way the same reference arrives twice is a second
///     subscription - exactly what must not double-persist. And a single-flight ordered
///     drain: concurrent appends can never persist one message twice nor reorder the
///     exchange, and the drain gives up after ONE failed store write, leaving the message
///     queued for the next attempt, so a locked or full database degrades instead of
///     wedging every caller.</para>
///     <para>Persistence always runs with <see cref="CancellationToken.None"/>: it must
///     survive the cancellation of the turn it is recording.</para></summary>
public sealed class RootTranscriptSink : IChildTranscriptStore, IDisposable, IAsyncDisposable
{
  private readonly IAgentStore _store;
  private readonly Func<AgentId?> _rootIdSource;
  private readonly Lock _lock = new();
  private readonly Queue<Message> _pending = new();

  // The absolute conversation index this sink has reached. SEEDED with the conversation's
  // length at construction: on resume that hydrated prefix was persisted by the session
  // that wrote it, so it counts. The sink is resolved with the root holder BEFORE a turn
  // begins (the holder is what builds the agent that subscribes it), so the seed is the
  // pre-turn length - never a partially-grown turn.
  private int _persisted;
  private IDisposable? _subscriptionLease;
  private int _retirements;
  private Task _drainTask = Task.CompletedTask;
  private Message? _lastAccepted;
  private AgentId? _rootId;
  private bool _draining;

  /// <summary>Builds a sink over <paramref name="conversation"/>, seeding its absolute
  ///     persisted index with the conversation's CURRENT length (the hydrated prefix of a
  ///     resumed session is already persisted).</summary>
  public RootTranscriptSink(IAgentStore store, Conversation conversation, Func<AgentId?> rootIdSource)
  {
    ArgumentNullException.ThrowIfNull(conversation);
    _store = store ?? throw new ArgumentNullException(nameof(store));
    _rootIdSource = rootIdSource ?? throw new ArgumentNullException(nameof(rootIdSource));
    _persisted = conversation.Messages.Count;
  }

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

  /// <summary>Registers the live conversation subscription this sink is driven by, retiring
  ///     the previous one. The root agent is rebuilt on every model change and each build
  ///     subscribes the SHARED conversation again, so without this a conversation would
  ///     accumulate one live subscription per build and push every message N times - the
  ///     doubled turn that corrupts resume. The sink owns the retirement because it is the
  ///     object that knows it is the root's single sink, and it lives in the same assembly
  ///     as the agent's subscription handle (no public surface is widened for it).</summary>
  public void TrackSubscription(IDisposable lease)
  {
    ArgumentNullException.ThrowIfNull(lease);
    IDisposable? previous;
    lock (_lock)
    {
      previous = _subscriptionLease;
      _subscriptionLease = lease;
    }

    if (previous is null)
    {
      return;
    }

    previous.Dispose();
    lock (_lock)
    {
      _retirements++;
    }
  }

  /// <summary>How many previous subscriptions this sink retired. Internal, for tests: the
  ///     reference dedupe below absorbs a duplicated notification, so a missed retirement is
  ///     invisible in the STORE — this counter is the only direct observation of it.</summary>
  internal int Retirements
  {
    get
    {
      lock (_lock)
      {
        return _retirements;
      }
    }
  }

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

  /// <summary>The ABSOLUTE conversation index this sink has persisted up to: the hydrated
  ///     prefix it was built over plus every message it wrote. An inert sink reports the
  ///     prefix, which is exactly what the end-of-turn append must skip.</summary>
  public int PersistedCount
  {
    get
    {
      lock (_lock)
      {
        return _persisted;
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
  ///     flush; the queue is never reordered. BOUNDED: one drain attempt, never a retry
  ///     loop - a store that keeps failing must return control to the caller.</summary>
  public Task FlushAsync() => DrainAsync();

  /// <summary>The absolute persisted index, after ONE bounded flush attempt. The flush is
  ///     bounded, so a failing store cannot hang the read - which means the count can sit
  ///     BELOW a message still queued. That is safe only because the caller tells the sink
  ///     when its own backstop writes one of those messages
  ///     (<see cref="NotePersistedByBackstop"/>): without that, the sink would re-write it on
  ///     its next flush and the turn would be doubled.</summary>
  public async Task<int> FlushedCount()
  {
    await FlushAsync().ConfigureAwait(false);
    return PersistedCount;
  }

  /// <summary>Drops a message the END-OF-TURN BACKSTOP persisted, so the sink never writes it
  ///     a second time. The backstop exists precisely because a bounded flush can leave a
  ///     message queued while the reported count sits below it; when the backstop then writes
  ///     that message, the sink must forget it. Removing a queued message also advances the
  ///     absolute index, because the row now exists. Returns true when the message was
  ///     actually queued here (the case that would otherwise duplicate).</summary>
  public bool NotePersistedByBackstop(Message message)
  {
    ArgumentNullException.ThrowIfNull(message);
    lock (_lock)
    {
      if (!_pending.Contains(message))
      {
        return false;
      }

      Message[] kept = [.. _pending.Where(queued => !ReferenceEquals(queued, message))];
      _pending.Clear();
      foreach (Message queued in kept)
      {
        _pending.Enqueue(queued);
      }

      _persisted++;
      return true;
    }
  }

  /// <summary>Interface conformance for <see cref="ReplaceAsync(IReadOnlyList{Message})"/>:
  ///     the richer overload reports whether the replacement actually landed, which the
  ///     end-of-turn path needs so an inert or failing sink cannot silently swallow a
  ///     compacted turn. The interface shape discards that answer.</summary>
  Task IChildTranscriptStore.ReplaceAsync(IReadOnlyList<Message> messages) => ReplaceAsync(messages);

  /// <summary>Replaces the whole persisted transcript (compaction/shrink survival) and, ON
  ///     SUCCESS, resets the absolute baseline to the replacement's length. An inert sink
  ///     writes nothing and reports false, so the caller can persist the replacement itself.
  ///     <para>The baseline moves only after the store accepted the replacement: the
  ///     lifecycle falls back to writing it itself when this returns false, and a baseline
  ///     advanced by a replacement that never landed would claim rows that do not exist —
  ///     the next turn's append would then skip real messages. The pending queue is cleared
  ///     unconditionally, because a replacement supersedes whatever was queued.</para></summary>
  public async Task<bool> ReplaceAsync(IReadOnlyList<Message> messages)
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
      return false;
    }

    Result<string> replaced = await _store
        .ReplaceTranscriptAsync(rootId.Value, messages, CancellationToken.None)
        .ConfigureAwait(false);
    if (!replaced.IsSuccess)
    {
      return false;
    }

    lock (_lock)
    {
      _persisted = messages.Count;
    }

    return true;
  }

  /// <summary>Terminal flush: disposes by persisting whatever is still queued.</summary>
  public ValueTask DisposeAsync() => new(FlushAsync());

  /// <summary>Sync dispose for the DI container's shutdown path, which disposes a
  ///     singleton synchronously. Named decision: the flush hops to the threadpool so a
  ///     UI-thread container close cannot deadlock on a store continuation, and the result
  ///     is awaited inline — the pending queue is what this call exists to land.</summary>
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
  ///     queued (the next flush retries it) — the loop never retries on its own, so a
  ///     persistently failing store cannot spin.</summary>
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
          _persisted++;
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
