using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain;

/// <summary>Store-backed child transcript sink: every AppendAsync lands the message in
///     the IAgentStore immediately (CancellationToken.None — persistence never observes
///     the run's cancellation token), so an interrupt leaves the transcript persisted up
///     to the last safe point. A serialized pending queue absorbs bursts without making
///     the loop wait; FlushAsync drains it at terminal.</summary>
public sealed class ChildTranscriptStore(IAgentStore store, AgentId childId) : IChildTranscriptStore, IAsyncDisposable
{
  private readonly Queue<Message> _pending = new();
  private int _flushed;
  private readonly Lock _lock = new();

  public async Task AppendAsync(Message message)
  {
    lock (_lock)
    {
      _pending.Enqueue(message);
    }

    await DrainAsync().ConfigureAwait(false);
  }

  public Task FlushAsync() => DrainAsync();

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
  public ValueTask DisposeAsync() => new(DrainAsync());

  /// <summary>Serializes pending messages to the store in order. A failed append is
  ///     left pending (best-effort persistence): the terminal FlushAsync retries it.
  ///     CancellationToken.None everywhere: persistence must survive the run's cancel.</summary>
  private async Task DrainAsync()
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
}
