using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;
using eThangAgent.TestSupport;

namespace eThangAgent.Storage.ACL.Tests;

/// <summary>The incremental transcript read a follower polls with (issue #385).
///     <para>THE COST IS WHAT IS ASSERTED, not the shape alone. The desktop cell follows a host
///     agent every 500 ms and used to re-read the whole transcript on every tick: on the largest
///     measured session - 1,735 messages, 4.69 MiB - that is 9.38 MiB/s of continuous read from
///     a file the same process is writing, spent to discover that nothing had changed.</para>
///     <para>Allocation is measured rather than bytes-on-the-wire because it is the same
///     quantity the poll actually costs: every message the read materializes becomes managed
///     garbage whether or not the caller renders it.</para></summary>
public sealed class TranscriptTailReadTests : IDisposable
{
  /// <summary>The size of the transcript that made the defect visible: 1,735 messages over a
  ///     4.69 MiB session. 400 messages is smaller and still large enough that a whole-transcript
  ///     read differs from a tail read by two orders of magnitude.</summary>
  private const int Messages = 400;

  /// <summary>Each message carries text of a realistic length for an assistant turn, so the
  ///     transcript is measured in hundreds of kilobytes rather than in message metadata.</summary>
  private static readonly string Body = new('x', 512);

  private readonly string _dbPath = TemplateDatabase.Copy();
  private readonly SqliteAgentStore _store;

  public TranscriptTailReadTests()
      => _store = new SqliteAgentStore(new AppDatabase(_dbPath));

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

  /// <summary>Seeds one agent holding <paramref name="count"/> messages and returns its id.
  ///     <para>WRITTEN THROUGH THE STORE, one append at a time, which is exactly how a running
  ///     agent persists at its safe points - so the fixture has the sequence numbers and row
  ///     order production writes.</para></summary>
  private async Task<AgentId> SeedAsync(int count)
  {
    AgentId id = AgentId.NewId();
    _ = await _store.SaveAsync(
        AgentRecord.Started(id, "provider/model", null, "task",
            new DateTimeOffset(2026, 8, 21, 10, 0, 0, TimeSpan.Zero)),
        ct: TestContext.Current.CancellationToken).ConfigureAwait(true);
    for (int i = 0; i < count; i++)
    {
      Message message = new(Role.Assistant, $"{i}: {Body}",
          new DateTimeOffset(2026, 8, 21, 10, 0, 0, TimeSpan.Zero).AddSeconds(i));
      Result<string> appended = await _store.AppendMessageAsync(id, message,
          TestContext.Current.CancellationToken).ConfigureAwait(true);
      Assert.True(appended.IsSuccess, appended.Error?.Message);
    }

    return id;
  }

  /// <summary>A poll that finds nothing new reads a count, not a transcript: the allocation of a
  ///     tail read at the cursor is a small fraction of a whole-transcript read of the same
  ///     session.</summary>
  [Fact]
  public async Task ATailReadAtTheCursor_CostsWhatTheCountCosts_NotWhatTheTranscriptCosts()
  {
    AgentId id = await SeedAsync(Messages).ConfigureAwait(true);

    long whole = await MedianAllocationAsync(async () =>
    {
      Result<IReadOnlyList<Message>> read = await _store.GetTranscriptAsync(id,
          TestContext.Current.CancellationToken).ConfigureAwait(true);
      Assert.True(read.IsSuccess, read.Error?.Message);
      Assert.Equal(Messages, read.Value.Count);
    }).ConfigureAwait(true);

    long tail = await MedianAllocationAsync(async () =>
    {
      Result<TranscriptTail> read = await _store.GetTranscriptTailAsync(id, Messages,
          TestContext.Current.CancellationToken).ConfigureAwait(true);
      Assert.True(read.IsSuccess, read.Error?.Message);
      Assert.Empty(read.Value.Messages);
      Assert.Equal(Messages, read.Value.TotalCount);
    }).ConfigureAwait(true);

    Assert.True(tail * 20 < whole,
        $"a poll that finds nothing new must not read the transcript: {tail} bytes allocated for the " +
        $"tail against {whole} for the whole {Messages}-message transcript");
  }

  /// <summary>A tail read returns exactly the messages at or after the requested sequence, in
  ///     order, and the count of the whole transcript.</summary>
  [Fact]
  public async Task ATailRead_ReturnsTheMessagesAtOrAfterTheStart_AndTheTotalCount()
  {
    AgentId id = await SeedAsync(5).ConfigureAwait(true);

    Result<TranscriptTail> read = await _store.GetTranscriptTailAsync(id, 3,
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(read.IsSuccess, read.Error?.Message);
    Assert.Equal(5, read.Value.TotalCount);
    Assert.Equal(["3: " + Body, "4: " + Body], read.Value.Messages.Select(m => m.Content));
  }

  /// <summary>A start beyond the end is empty and still reports the count, which is what lets a
  ///     follower distinguish 'nothing arrived' from 'the transcript was rewritten'.</summary>
  [Fact]
  public async Task ATailRead_PastTheEnd_IsEmpty_AndStillCounts()
  {
    AgentId id = await SeedAsync(4).ConfigureAwait(true);

    Result<TranscriptTail> read = await _store.GetTranscriptTailAsync(id, 9,
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(read.IsSuccess, read.Error?.Message);
    Assert.Empty(read.Value.Messages);
    Assert.Equal(4, read.Value.TotalCount);
  }

  /// <summary>An unknown agent is the same NotFound the whole-transcript read reports, so a
  ///     follower's error handling does not change with the call.</summary>
  [Fact]
  public async Task ATailRead_OfAnUnknownAgent_ReportsNotFound()
  {
    Result<TranscriptTail> read = await _store.GetTranscriptTailAsync(AgentId.NewId(), 0,
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(read.IsSuccess);
    Assert.Equal("NotFound", read.Error.Code);
  }

  /// <summary>The bytes one operation allocates, over several passes with the median taken: a
  ///     single reading is at the mercy of a pooled connection opening or a JIT tier, and the
  ///     claim is about the steady state a poll lives in.</summary>
  private static async Task<long> MedianAllocationAsync(Func<Task> operation)
  {
    await operation().ConfigureAwait(true); // warm: pool, serializer metadata, reader buffers
    List<long> readings = [];
    for (int i = 0; i < 5; i++)
    {
      long before = GC.GetTotalAllocatedBytes(precise: true);
      await operation().ConfigureAwait(true);
      readings.Add(GC.GetTotalAllocatedBytes(precise: true) - before);
    }

    readings.Sort();
    return readings[readings.Count / 2];
  }
}
