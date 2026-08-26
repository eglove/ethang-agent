using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Storage.ACL.Tests;

public sealed class SqliteAgentStoreConcurrencyTests : IDisposable
{
  private readonly string _dbPath = Path.Combine(
      Path.GetTempPath(), $"ethang-agents-{Guid.NewGuid():N}.db");
  private readonly SqliteAgentStore _store;

  public SqliteAgentStoreConcurrencyTests()
      => _store = new SqliteAgentStore(new AppDatabase(_dbPath));

  public void Dispose()
  {
    GC.SuppressFinalize(this);
    // Named decision (CA1031): temp-db cleanup is best effort.
#pragma warning disable CA1031 // Do not catch general exception types
    try
    {
      File.Delete(_dbPath);
    }
    catch { }
#pragma warning restore CA1031
  }

  /// <summary>Each call runs on its own thread-pool thread via Task.Run because
  ///     Microsoft.Data.Sqlite async methods complete synchronously — without the
  ///     dispatch, Task.WhenAll would serialize trivially and prove nothing.</summary>
  [Fact]
  public async Task ConcurrentSavesAndAppends_AllSucceedAndPersist()
  {
    AgentRecord[] records = [.. Enumerable.Range(0, 20)
        .Select(i => AgentRecord.Spawned(
            AgentId.NewId(), AgentId.NewId(), 1, "provider/model", $"agent-{i}",
            $"task {i}", new DateTimeOffset(2026, 8, 21, 10, 0, i, TimeSpan.Zero)))];

    Result<string>[] saves = await Task.WhenAll(records.Select(
        r => Task.Run(() => _store.SaveAsync(r))));

    AgentRecord shared = AgentRecord.Spawned(
        AgentId.NewId(), null, 1, "provider/model", "shared", "shared task",
        new DateTimeOffset(2026, 8, 21, 9, 0, 0, TimeSpan.Zero));
    Assert.True((await _store.SaveAsync(shared)).IsSuccess);

    Message[] messages = [.. Enumerable.Range(0, 10)
        .Select(i => new Message(Role.User, $"message {i}",
            new DateTimeOffset(2026, 8, 21, 10, 1, i, TimeSpan.Zero)))];
    Result<string>[] appends = await Task.WhenAll(messages.Select(
        m => Task.Run(() => _store.AppendMessageAsync(shared.Id, m))));

    Assert.All(saves, s => Assert.True(s.IsSuccess));
    Assert.All(appends, a => Assert.True(a.IsSuccess));

    foreach (AgentRecord? record in records)
    {
      Assert.Equal(record, (await _store.GetAsync(record.Id)).Value);
    }

    Result<IReadOnlyList<Message>> transcript = await _store.GetTranscriptAsync(shared.Id);
    Assert.True(transcript.IsSuccess);
    Assert.Equal(10, transcript.Value!.Count);
    Assert.Equal(
        messages.Select(m => m.Content).Order().ToArray(),
        transcript.Value.Select(m => m.Content).Order().ToArray());
  }
}
