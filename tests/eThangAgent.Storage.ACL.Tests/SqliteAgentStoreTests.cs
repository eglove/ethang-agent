using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Storage.ACL.Tests;

public class SqliteAgentStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"ethang-agents-{Guid.NewGuid():N}.db");
    private readonly SqliteAgentStore _store;

    public SqliteAgentStoreTests()
        => _store = new SqliteAgentStore(new AppDatabase(_dbPath));

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { }
    }

    private static AgentRecord FullyPopulatedRecord(AgentId? id = null, AgentId? parentId = null) => new(
        id ?? AgentId.NewId(),
        parentId ?? AgentId.NewId(),
        Depth: 2,
        Status: AgentStatus.Failed,
        FailureReason: AgentFailureReason.Timeout,
        ModelUsed: "provider/model-x",
        Label: "research",
        TaskPrompt: "summarize the docs",
        CreatedAt: new DateTimeOffset(2026, 8, 21, 10, 0, 0, TimeSpan.Zero),
        CompletedAt: new DateTimeOffset(2026, 8, 21, 10, 5, 0, TimeSpan.Zero),
        FinalReport: "all done");

    [Fact]
    public async Task Save_Get_RoundTripsEveryField()
    {
        var populated = FullyPopulatedRecord();
        var root = new AgentRecord(
            AgentId.NewId(), null, 0, AgentStatus.Running, null, "provider/model-root",
            null, "root task", new DateTimeOffset(2026, 8, 21, 9, 0, 0, TimeSpan.Zero), null, null);

        Assert.True((await _store.SaveAsync(populated)).IsSuccess);
        Assert.True((await _store.SaveAsync(root)).IsSuccess);

        Assert.Equal(populated, (await _store.GetAsync(populated.Id)).Value);
        Assert.Equal(root, (await _store.GetAsync(root.Id)).Value);
    }

    [Fact]
    public async Task Update_RunningToCompleted_SetsFinalReportAndCompletedAt()
    {
        var running = AgentRecord.Spawned(
            AgentId.NewId(), AgentId.NewId(), 1, "provider/model", "child", "do work",
            new DateTimeOffset(2026, 8, 21, 10, 0, 0, TimeSpan.Zero));
        await _store.SaveAsync(running);

        var completed = running with
        {
            Status = AgentStatus.Completed,
            CompletedAt = new DateTimeOffset(2026, 8, 21, 10, 3, 30, TimeSpan.Zero),
            FinalReport = "the final report",
        };
        var updated = await _store.UpdateAsync(completed);

        Assert.True(updated.IsSuccess);
        Assert.Equal(completed, (await _store.GetAsync(running.Id)).Value);
    }

    [Fact]
    public async Task AppendAndGetTranscript_PreservesOrderAndContent()
    {
        var id = AgentId.NewId();
        await _store.SaveAsync(AgentRecord.Spawned(
            id, null, 1, "provider/model", null, "task",
            new DateTimeOffset(2026, 8, 21, 10, 0, 0, TimeSpan.Zero)));

        var first = new Message(Role.User, "first prompt",
            new DateTimeOffset(2026, 8, 21, 10, 0, 1, TimeSpan.Zero));
        var second = new Message(Role.Assistant, "calling tool",
            new DateTimeOffset(2026, 8, 21, 10, 0, 2, TimeSpan.Zero),
            [new ToolCall("call_1", "exec", "{ script: 'ls' }")], "call_1");
        var third = new Message(Role.Tool, "tool output",
            new DateTimeOffset(2026, 8, 21, 10, 0, 3, TimeSpan.Zero), ToolCallId: "call_1");

        await _store.AppendMessageAsync(id, first);
        await _store.AppendMessageAsync(id, second);
        await _store.AppendMessageAsync(id, third);

        var transcript = await _store.GetTranscriptAsync(id);

        Assert.True(transcript.IsSuccess);
        Assert.Equal(3, transcript.Value!.Count);

        Assert.Equal(first.Role, transcript.Value[0].Role);
        Assert.Equal(first.Content, transcript.Value[0].Content);
        Assert.Equal(first.Timestamp, transcript.Value[0].Timestamp);

        Assert.Equal(second.Role, transcript.Value[1].Role);
        Assert.Equal(second.Content, transcript.Value[1].Content);
        Assert.Equal(second.Timestamp, transcript.Value[1].Timestamp);
        Assert.Equal(second.ToolCallId, transcript.Value[1].ToolCallId);
        Assert.NotNull(transcript.Value[1].ToolCalls);
        Assert.Equal(second.ToolCalls!, transcript.Value[1].ToolCalls!);

        Assert.Equal(third.Role, transcript.Value[2].Role);
        Assert.Equal(third.Content, transcript.Value[2].Content);
        Assert.Equal(third.Timestamp, transcript.Value[2].Timestamp);
        Assert.Equal(third.ToolCallId, transcript.Value[2].ToolCallId);
        Assert.Null(transcript.Value[2].ToolCalls);
    }

    [Fact]
    public async Task UnknownAgentId_ReturnsTypedNotFoundFailure()
    {
        var missing = AgentId.NewId();
        var record = FullyPopulatedRecord(missing);

        Assert.Equal("NotFound", (await _store.GetAsync(missing)).Error!.Code);
        Assert.Equal("NotFound", (await _store.UpdateAsync(record)).Error!.Code);
        Assert.Equal("NotFound", (await _store.AppendMessageAsync(missing,
            new Message(Role.User, "x", DateTimeOffset.UtcNow))).Error!.Code);
        Assert.Equal("NotFound", (await _store.GetTranscriptAsync(missing)).Error!.Code);
    }

    [Fact]
    public async Task ListChildren_FiltersByParent_OrdersByCreatedAt()
    {
        var parentA = AgentId.NewId();
        var parentB = AgentId.NewId();

        var oldest = AgentRecord.Spawned(AgentId.NewId(), parentA, 1, "provider/model", "oldest",
            "task", new DateTimeOffset(2026, 8, 21, 10, 0, 0, TimeSpan.Zero));
        var newest = AgentRecord.Spawned(AgentId.NewId(), parentA, 1, "provider/model", "newest",
            "task", new DateTimeOffset(2026, 8, 21, 11, 0, 0, TimeSpan.Zero));
        var middle = AgentRecord.Spawned(AgentId.NewId(), parentA, 1, "provider/model", "middle",
            "task", new DateTimeOffset(2026, 8, 21, 10, 30, 0, TimeSpan.Zero));
        var otherParent = AgentRecord.Spawned(AgentId.NewId(), parentB, 1, "provider/model", "other",
            "task", new DateTimeOffset(2026, 8, 21, 10, 15, 0, TimeSpan.Zero));

        foreach (var child in new[] { newest, otherParent, oldest, middle })
            await _store.SaveAsync(child);

        var children = await _store.ListChildrenAsync(parentA);

        Assert.True(children.IsSuccess);
        Assert.Equal(new[] { oldest.Id, middle.Id, newest.Id },
            children.Value!.Select(c => c.Id).ToArray());
    }

    [Fact]
    public async Task Events_PersistAndReload()
    {
        var id = AgentId.NewId();
        var otherId = AgentId.NewId();
        var spawned = new AgentSpawned(id,
            new DateTimeOffset(2026, 8, 21, 10, 0, 0, TimeSpan.Zero), 1, "provider/model", "child");
        var completed = new AgentCompleted(id,
            new DateTimeOffset(2026, 8, 21, 10, 2, 0, TimeSpan.Zero), AgentStatus.Completed, null);
        var failed = new AgentCompleted(id,
            new DateTimeOffset(2026, 8, 21, 10, 4, 0, TimeSpan.Zero), AgentStatus.Failed,
            AgentFailureReason.ProviderError);

        await _store.AppendEventAsync(spawned);
        await _store.AppendEventAsync(new AgentSpawned(otherId,
            new DateTimeOffset(2026, 8, 21, 10, 1, 0, TimeSpan.Zero), 1, "provider/model", null));
        await _store.AppendEventAsync(completed);
        await _store.AppendEventAsync(failed);

        var events = await _store.GetEventsAsync(id);

        Assert.True(events.IsSuccess);
        Assert.Equal(3, events.Value!.Count);
        Assert.Equal(spawned, events.Value[0]);
        Assert.Equal(completed, events.Value[1]);
        Assert.Equal(failed, events.Value[2]);
    }

    [Fact]
    public async Task DataPersistsAcrossStoreRecreation()
    {
        var id = AgentId.NewId();
        var record = AgentRecord.Spawned(id, null, 1, "provider/model", null, "task",
            new DateTimeOffset(2026, 8, 21, 10, 0, 0, TimeSpan.Zero));
        await _store.SaveAsync(record);
        await _store.AppendMessageAsync(id,
            new Message(Role.User, "hello", new DateTimeOffset(2026, 8, 21, 10, 0, 1, TimeSpan.Zero)));

        var reopened = new SqliteAgentStore(new AppDatabase(_dbPath));

        Assert.Equal(record, (await reopened.GetAsync(id)).Value);
        var transcript = await reopened.GetTranscriptAsync(id);
        Assert.Single(transcript.Value!);
        Assert.Equal("hello", transcript.Value![0].Content);
    }
}
