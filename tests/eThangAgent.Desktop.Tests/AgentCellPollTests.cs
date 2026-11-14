using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using eThangAgent.TestSupport;

namespace eThangAgent.Desktop.Tests;

/// <summary>The cell's poll against a real SQLite transcript (issue #385).
///     <para>THE READ IS THE SUBJECT, not the rendering. Every other cell test answers the store
///     with a list, so a poll that read the whole transcript and rendered only the tail would be
///     green through all of them. Here the store is the production <see cref="SqliteAgentStore"/>
///     over a real file, and the assertion is how many messages each tick ASKS FOR.</para>
///     <para>IT COUNTS RATHER THAN TIMES: the counting store wraps the real one and sums every
///     read's message count, so the claim holds on any machine at any speed.</para></summary>
public sealed class AgentCellPollTests : IDisposable
{
  /// <summary>The message count of the largest session measured for the issue: 1,735 messages
  ///     over 4.69 MiB, the case that read 9.38 MiB/s twice a second to learn nothing changed.
  ///     </summary>
  private const int TranscriptMessages = 1735;

  /// <summary>Text sized so the seeded transcript is a realistic 4.7 MB rather than a list of
  ///     empty strings: a whole-transcript read has to be materially more expensive than a tail
  ///     read for the measurement to mean anything.</summary>
  private static readonly string Body = new('x', 2700);

  private readonly string _dbPath = TemplateDatabase.Copy();

  public void Dispose()
  {
    GC.SuppressFinalize(this);
    // Named decision (CA1031): temp-db cleanup is best effort.
#pragma warning disable CA1031, S108 // Do not catch general exception types
    try
    {
      File.Delete(_dbPath);
    }
    catch { }
#pragma warning restore CA1031, S108
  }

  /// <summary>A poll that finds nothing new costs a count and no messages, on every tick after
  ///     the first. Before the change it cost the whole transcript on every tick.</summary>
  [Fact]
  public async Task APollingCell_ReadsTheTail_NotTheWholeTranscript()
  {
    (CountingStore store, AgentId agent) = await SeedAsync().ConfigureAwait(true);

    AgentCellViewModel cell = new(agent, "following", store,
        TestFixtures.CreateSynchronouslyMarshalledViewModel());

    Task loop = cell.RunFollowAsync(_ => Task.FromResult(false), generation: 1);
    await loop.ConfigureAwait(true);

    // The immediate first read paints the card: every message, once.
    Assert.Equal(TranscriptMessages, store.MessagesRead);

    // Three more ticks with nothing new. Each asks for the tail past the cursor and gets none,
    // so the counter does not move - and the COUNT still comes back, which is how the poll learns
    // that nothing changed without reading anything.
    for (int i = 0; i < 3; i++)
    {
      await cell.RunFollowAsync(OneTick, generation: 1).ConfigureAwait(true);
    }

    Assert.Equal(TranscriptMessages, store.MessagesRead);
    Assert.Equal(4, store.TailReads);
  }

  /// <summary>A tick that finds new messages reads exactly those, so the cost follows what
  ///     arrived rather than what is on screen.</summary>
  [Fact]
  public async Task APollThatFindsNewMessages_ReadsOnlyThose()
  {
    (CountingStore store, AgentId agent) = await SeedAsync().ConfigureAwait(true);
    AgentCellViewModel cell = new(agent, "following", store,
        TestFixtures.CreateSynchronouslyMarshalledViewModel());
    await cell.RunFollowAsync(_ => Task.FromResult(false), generation: 1).ConfigureAwait(true);

    _ = await store.AppendMessageAsync(agent, new Message(Role.Assistant, "one more",
        DateTimeOffset.UtcNow), TestContext.Current.CancellationToken).ConfigureAwait(true);
    await cell.RunFollowAsync(OneTick, generation: 1).ConfigureAwait(true);

    Assert.Equal(TranscriptMessages + 1, store.MessagesRead);
  }

  /// <summary>One tick, then the loop ends.</summary>
  private static Task<bool> OneTick(TimeSpan _) => Task.FromResult(true);

  /// <summary>Wraps the production store so a test can count what the cell actually ASKED FOR:
  ///     the messages every read returned, and how many tail reads happened at all.</summary>
  private sealed class CountingStore(IAgentStore inner) : IAgentStore
  {
    /// <summary>Messages returned across every read so far - the quantity a poll pays for.</summary>
    public int MessagesRead { get; private set; }

    /// <summary>How many reads went through the incremental member.</summary>
    public int TailReads { get; private set; }

    public Task<Result<string>> SaveAsync(AgentRecord record, CancellationToken ct = default)
        => inner.SaveAsync(record, ct);

    public Task<Result<string>> UpdateAsync(AgentRecord record, CancellationToken ct = default)
        => inner.UpdateAsync(record, ct);

    public Task<Result<AgentRecord>> GetAsync(AgentId id, CancellationToken ct = default)
        => inner.GetAsync(id, ct);

    public Task<Result<string>> AppendMessageAsync(AgentId id, Message message,
        CancellationToken ct = default) => inner.AppendMessageAsync(id, message, ct);

    public Task<Result<string>> ReplaceTranscriptAsync(AgentId id, IReadOnlyList<Message> messages,
        CancellationToken ct = default) => inner.ReplaceTranscriptAsync(id, messages, ct);

    public async Task<Result<IReadOnlyList<Message>>> GetTranscriptAsync(AgentId id,
        CancellationToken ct = default)
    {
      Result<IReadOnlyList<Message>> read = await inner.GetTranscriptAsync(id, ct).ConfigureAwait(false);
      if (read.IsSuccess)
      {
        MessagesRead += read.Value.Count;
      }

      return read;
    }

    public async Task<Result<TranscriptTail>> GetTranscriptTailAsync(AgentId id, int fromSeq,
        CancellationToken ct = default)
    {
      Result<TranscriptTail> read = await inner.GetTranscriptTailAsync(id, fromSeq, ct)
          .ConfigureAwait(false);
      if (read.IsSuccess)
      {
        TailReads++;
        MessagesRead += read.Value.Messages.Count;
      }

      return read;
    }

    public Task<Result<IReadOnlyList<AgentRecord>>> ListAllAsync(CancellationToken ct = default)
        => inner.ListAllAsync(ct);
  }

  private async Task<(CountingStore Store, AgentId Agent)> SeedAsync()
  {
    CountingStore store = new(new SqliteAgentStore(new AppDatabase(_dbPath)));
    AgentId id = AgentId.NewId();
    _ = await store.SaveAsync(AgentRecord.Started(id, "provider/model", null, "task",
        new DateTimeOffset(2026, 8, 21, 10, 0, 0, TimeSpan.Zero)),
        ct: TestContext.Current.CancellationToken).ConfigureAwait(true);
    for (int i = 0; i < TranscriptMessages; i++)
    {
      _ = await store.AppendMessageAsync(id, new Message(Role.Assistant, $"{i}: {Body}",
          new DateTimeOffset(2026, 8, 21, 10, 0, 0, TimeSpan.Zero).AddSeconds(i)),
          TestContext.Current.CancellationToken).ConfigureAwait(true);
    }

    return (store, id);
  }
}
