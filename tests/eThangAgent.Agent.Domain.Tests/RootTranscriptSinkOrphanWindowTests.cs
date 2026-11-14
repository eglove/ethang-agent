using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>The drain's contract at the orphan window's edge: a message whose append FAILED stays
///     queued and is retried, and the retry must not be the thing that produces a second file
///     nothing names.
///     <para>THIS IS WHERE A REFERENCE AND ITS FILE ARE SEPARATED IN TIME. The store writes the
///     file while it is building the row, so an append that fails AFTER the file landed leaves
///     that file behind - an orphan the sweep removes, and never a reference to a file that was
///     not written. The retry then writes the row (and, because the payload is still inline in the
///     message, a SECOND file), which is why the first is sweepable rather than dangling.</para>
///     <para>The other half of the contract is that the retry is BOUNDED: a store that keeps
///     failing returns control to the caller with the message still queued instead of spinning,
///     so 'the image did not land' is recorded rather than retried forever.</para></summary>
public class RootTranscriptSinkOrphanWindowTests
{
  /// <summary>A store that fails the first N appends and then recovers, the shape of a locked
  ///     database that clears at a turn boundary. It records every attempt, so a test can prove
  ///     the retry happened rather than infer it from a count.</summary>
  private sealed class FailsThenRecovers(int failures) : IAgentStore
  {
    public List<Message> Appended { get; } = [];

    public int Attempts { get; private set; }

    public Task<Result<string>> SaveAsync(AgentRecord record, CancellationToken ct = default)
        => Task.FromResult(Result.Success(record.Id.ToString()));

    public Task<Result<string>> UpdateAsync(AgentRecord record, CancellationToken ct = default)
        => Task.FromResult(Result.Success(record.Id.ToString()));

    public Task<Result<AgentRecord>> GetAsync(AgentId id, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<AgentRecord>(new DomainError("NotFound", "no records")));

    public Task<Result<string>> AppendMessageAsync(AgentId id, Message message,
        CancellationToken ct = default)
    {
      Attempts++;
      if (Attempts <= failures)
      {
        return Task.FromResult(Result.Failure<string>(new DomainError("DbLocked", "locked")));
      }

      Appended.Add(message);
      return Task.FromResult(Result.Success(id.ToString()));
    }

    public Task<Result<string>> ReplaceTranscriptAsync(AgentId id, IReadOnlyList<Message> messages,
        CancellationToken ct = default) => Task.FromResult(Result.Success(id.ToString()));

    public Task<Result<IReadOnlyList<Message>>> GetTranscriptAsync(AgentId id,
        CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<Message>>([]));

    public Task<Result<IReadOnlyList<AgentRecord>>> ListAllAsync(CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<AgentRecord>>([]));
  }

  /// <summary>The retried message is the SAME instance, unchanged: the sink never rewrites a
  ///     message to record an attempt. The record of what happened belongs to the store's row, so
  ///     a retry cannot accumulate a line per failure in the transcript.</summary>
  [Fact]
  public async Task AFailedAppend_KeepsTheSameMessageQueued_AndTheRetryWritesItOnce()
  {
    FailsThenRecovers store = new(failures: 1);
    AgentId rootId = AgentId.NewId();
    Conversation conversation = new();
    // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
    await using RootTranscriptSink sink = new(store, conversation, () => rootId);
#pragma warning restore CA2007
    Message message = new(Role.Tool, "computer: observe", DateTimeOffset.UtcNow,
        ToolCallId: "call_1",
        Parts: [new MessagePart.ImagePart("image/png", "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==")]);

    await sink.AppendAsync(message).ConfigureAwait(true);
    Assert.True(sink.HasPending, "the failed message must stay queued");
    Assert.Empty(store.Appended);

    await sink.FlushAsync().ConfigureAwait(true);

    Assert.False(sink.HasPending);
    Message written = Assert.Single(store.Appended);
    Assert.Same(message, written);
    Assert.Equal(message.Content, written.Content);
  }

  /// <summary>A store that never recovers must not spin: the drain ends after ONE attempt and
  ///     hands control back, which is what makes the failure recordable at all.</summary>
  [Fact]
  public async Task AStoreThatNeverRecovers_EndsTheDrainInsteadOfSpinning()
  {
    FailsThenRecovers store = new(failures: int.MaxValue);
    AgentId rootId = AgentId.NewId();
    // Named decision (CA2000): NOT disposed on purpose - a spinning drain would also wedge
    // Dispose, and the point of this test is that no caller may be wedged.
#pragma warning disable CA2000 // Dispose objects before losing scope
    RootTranscriptSink sink = new(store, new Conversation(), () => rootId);
#pragma warning restore CA2000

    await sink.AppendAsync(new Message(Role.User, "stuck", DateTimeOffset.UtcNow)).ConfigureAwait(true);
    await sink.FlushAsync().ConfigureAwait(true);

    // One attempt for the append and one for the explicit flush, and never a second inside
    // either: a loop here is an unbounded retry that hangs every caller.
    Assert.Equal(2, store.Attempts);
    Assert.True(sink.HasPending);
    Assert.Empty(store.Appended);
  }
}
