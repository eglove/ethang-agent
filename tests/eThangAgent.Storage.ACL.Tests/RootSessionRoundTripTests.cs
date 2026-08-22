using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Storage.ACL.Tests;

/// <summary>Round-trip of the persisted root session against the real SQLite store: the
///     Root factory's depth-0 sentinels, transcript appends in order, and the Completed
///     transition — the exact lifecycle the CLI REPL drives.</summary>
public class RootSessionRoundTripTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"ethang-agents-{Guid.NewGuid():N}.db");
    private readonly SqliteAgentStore _store;

    public RootSessionRoundTripTests()
        => _store = new SqliteAgentStore(new AppDatabase(_dbPath));

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task RootRecord_PersistsDepthZeroSentinels()
    {
        var rootId = AgentId.NewId();
        var createdAt = new DateTimeOffset(2026, 8, 21, 9, 0, 0, TimeSpan.Zero);

        var saved = await _store.SaveAsync(AgentRecord.Root(rootId, createdAt));

        Assert.True(saved.IsSuccess);
        var record = (await _store.GetAsync(rootId)).Value!;

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
    }

    [Fact]
    public async Task AppendedExchange_ReturnsInOrderFromTranscript()
    {
        var rootId = AgentId.NewId();
        await _store.SaveAsync(AgentRecord.Root(rootId,
            new DateTimeOffset(2026, 8, 21, 10, 0, 0, TimeSpan.Zero)));

        var user = new Message(Role.User, "list the test files",
            new DateTimeOffset(2026, 8, 21, 10, 0, 1, TimeSpan.Zero));
        var assistant = new Message(Role.Assistant, "found three files",
            new DateTimeOffset(2026, 8, 21, 10, 0, 2, TimeSpan.Zero));

        Assert.True((await _store.AppendMessageAsync(rootId, user)).IsSuccess);
        Assert.True((await _store.AppendMessageAsync(rootId, assistant)).IsSuccess);

        var transcript = await _store.GetTranscriptAsync(rootId);

        Assert.True(transcript.IsSuccess);
        Assert.Equal(2, transcript.Value!.Count);

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
        var rootId = AgentId.NewId();
        await _store.SaveAsync(AgentRecord.Root(rootId,
            new DateTimeOffset(2026, 8, 21, 10, 0, 0, TimeSpan.Zero)));

        var completedAt = new DateTimeOffset(2026, 8, 21, 10, 5, 0, TimeSpan.Zero);
        var running = (await _store.GetAsync(rootId)).Value!;
        var updated = await _store.UpdateAsync(running with
        {
            Status = AgentStatus.Completed,
            CompletedAt = completedAt,
        });

        Assert.True(updated.IsSuccess);
        var record = (await _store.GetAsync(rootId)).Value!;

        Assert.Equal(AgentStatus.Completed, record.Status);
        Assert.Equal(completedAt, record.CompletedAt);

        Assert.Equal("unassigned", record.ModelUsed);
        Assert.Equal(0, record.Depth);
        Assert.Equal("root", record.Label);
    }
}
