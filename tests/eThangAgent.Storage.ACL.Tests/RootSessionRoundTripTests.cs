using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Storage.ACL.Tests;

/// <summary>Round-trip of the persisted root session against the real SQLite store: the
///     Root factory's depth-0 sentinels, transcript appends in order, and the Completed
///     transition — the exact lifecycle the CLI REPL drives.</summary>
public sealed class RootSessionRoundTripTests : IDisposable
{
  private readonly string _dbPath = Path.Combine(
      Path.GetTempPath(), $"ethang-agents-{Guid.NewGuid():N}.db");
  private readonly SqliteAgentStore _store;

  public RootSessionRoundTripTests()
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

  [Fact]
  public async Task RootRecord_PersistsDepthZeroSentinels()
  {
    AgentId rootId = AgentId.NewId();
    DateTimeOffset createdAt = new(2026, 8, 21, 9, 0, 0, TimeSpan.Zero);

    Result<string> saved = await _store.SaveAsync(
        AgentRecord.Root(rootId, createdAt, @"C:\workspaces\demo", "openrouter"), ct: TestContext.Current.CancellationToken);

    Assert.True(saved.IsSuccess);
    AgentRecord record = (await _store.GetAsync(rootId, ct: TestContext.Current.CancellationToken)).Value!;

    Assert.Equal(rootId, record.Id);
    Assert.Null(record.ParentId);
    Assert.Equal(0, record.Depth);
    Assert.Equal(AgentStatus.Running, record.Status);
    Assert.Equal("unassigned", record.ModelUsed);
    Assert.Equal("root", record.Label);
    Assert.Equal("conversation root", record.TaskPrompt);
    Assert.Equal(createdAt, record.CreatedAt);
    Assert.Null(record.CompletedAt);
    Assert.Null(record.FinalReport);
    Assert.Equal(@"C:\workspaces\demo", record.WorkspaceId);
    Assert.Equal("openrouter", record.Provider);
  }

  [Fact]
  public async Task AppendedExchange_ReturnsInOrderFromTranscript()
  {
    AgentId rootId = AgentId.NewId();
    _ = await _store.SaveAsync(AgentRecord.Root(rootId,
        new DateTimeOffset(2026, 8, 21, 10, 0, 0, TimeSpan.Zero), @"C:\workspaces\demo", "openrouter"), ct: TestContext.Current.CancellationToken);

    Message user = new(Role.User, "list the test files",
        new DateTimeOffset(2026, 8, 21, 10, 0, 1, TimeSpan.Zero));
    Message assistant = new(Role.Assistant, "found three files",
        new DateTimeOffset(2026, 8, 21, 10, 0, 2, TimeSpan.Zero));

    Assert.True((await _store.AppendMessageAsync(rootId, user, ct: TestContext.Current.CancellationToken)).IsSuccess);
    Assert.True((await _store.AppendMessageAsync(rootId, assistant, ct: TestContext.Current.CancellationToken)).IsSuccess);

    Result<IReadOnlyList<Message>> transcript = await _store.GetTranscriptAsync(rootId, ct: TestContext.Current.CancellationToken);

    Assert.True(transcript.IsSuccess);
    Assert.Equal(2, transcript.Value.Count);

    Assert.Equal(user.Role, transcript.Value[0].Role);
    Assert.Equal(user.Content, transcript.Value[0].Content);
    Assert.Equal(user.Timestamp, transcript.Value[0].Timestamp);
    Assert.Null(transcript.Value[0].ToolCalls);

    Assert.Equal(assistant.Role, transcript.Value[1].Role);
    Assert.Equal(assistant.Content, transcript.Value[1].Content);
    Assert.Equal(assistant.Timestamp, transcript.Value[1].Timestamp);
    Assert.Null(transcript.Value[1].ToolCalls);
  }

  [Fact]
  public async Task CompletedTransition_PersistsStatusAndTimestamp()
  {
    AgentId rootId = AgentId.NewId();
    _ = await _store.SaveAsync(AgentRecord.Root(rootId,
        new DateTimeOffset(2026, 8, 21, 10, 0, 0, TimeSpan.Zero), @"C:\workspaces\demo", "openrouter"), ct: TestContext.Current.CancellationToken);

    DateTimeOffset completedAt = new(2026, 8, 21, 10, 5, 0, TimeSpan.Zero);
    AgentRecord running = (await _store.GetAsync(rootId, ct: TestContext.Current.CancellationToken)).Value!;
    Result<string> updated = await _store.UpdateAsync(running with
    {
      Status = AgentStatus.Completed,
      CompletedAt = completedAt,
    }, ct: TestContext.Current.CancellationToken);

    Assert.True(updated.IsSuccess);
    AgentRecord record = (await _store.GetAsync(rootId, ct: TestContext.Current.CancellationToken)).Value!;

    Assert.Equal(AgentStatus.Completed, record.Status);
    Assert.Equal(completedAt, record.CompletedAt);

    Assert.Equal("unassigned", record.ModelUsed);
    Assert.Equal(0, record.Depth);
    Assert.Equal("root", record.Label);
  }
}
