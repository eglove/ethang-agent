using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Storage.ACL.Tests;

/// <summary>ListAllAsync over the real SQLite store: every row returned in
///     CreatedAt order regardless of insertion sequence — the corpus source the
///     memory recall and session queries read.</summary>
public sealed class ListAllRoundTripTests : IDisposable
{
  private readonly string _dbPath = Path.Combine(
      Path.GetTempPath(), $"ethang-agents-{Guid.NewGuid():N}.db");
  private readonly SqliteAgentStore _store;

  public ListAllRoundTripTests()
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

  [Fact]
  public async Task ListAll_ReturnsEveryRow_OrderedByCreatedAt_NotInsertionOrder()
  {
    AgentRecord late = AgentRecord.Root(AgentId.NewId(),
        new DateTimeOffset(2026, 8, 21, 18, 0, 0, TimeSpan.Zero), @"C:\workspaces\demo", "openrouter");
    AgentRecord early = AgentRecord.Spawned(AgentId.NewId(), parentId: null, depth: 1,
        modelUsed: "mock/model", label: "early child", taskPrompt: "run first",
        createdAt: new DateTimeOffset(2026, 8, 21, 8, 0, 0, TimeSpan.Zero));

    // Insert newest first; the listing must still come back oldest first.
    Assert.True((await _store.SaveAsync(late)).IsSuccess);
    Assert.True((await _store.SaveAsync(early)).IsSuccess);
    Assert.True((await _store.AppendMessageAsync(late.Id,
        new Message(Role.User, "a turn", DateTimeOffset.UtcNow))).IsSuccess);

    Result<IReadOnlyList<AgentRecord>> listed = await _store.ListAllAsync();

    Assert.True(listed.IsSuccess);
    Assert.Equal([early.Id, late.Id], listed.Value!.Select(r => r.Id).ToList());
    Assert.Equal("early child", listed.Value![0].Label);
    Assert.Equal("root", listed.Value![1].Label);
    Assert.Equal(2, listed.Value!.Count);
  }

  [Fact]
  public async Task ListAll_EmptyDatabase_YieldsEmptySuccess()
  {
    Result<IReadOnlyList<AgentRecord>> listed = await _store.ListAllAsync();

    Assert.True(listed.IsSuccess);
    Assert.Empty(listed.Value!);
  }
}
