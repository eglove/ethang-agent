using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Composition.Tests;

sealed class FakeAgentStore : IAgentStore
{
    public List<AgentRecord> Saved = [];
    public List<AgentRecord> Updated = [];
    public List<(AgentId Id, Message Message)> Appended = [];
    public AgentRecord? Current;
    public Result<string> SaveOutcome = Result<string>.Success("saved");
    public Result<AgentRecord> GetOutcome =
        Result<AgentRecord>.Failure(new Error("NotConfigured", "Get not configured"));
    public Result<string> UpdateOutcome = Result<string>.Success("updated");
    public Result<string> AppendOutcome = Result<string>.Success("appended");

    public Task<Result<string>> SaveAsync(AgentRecord record, CancellationToken ct = default)
    { Saved.Add(record); return Task.FromResult(SaveOutcome); }

    public Task<Result<AgentRecord>> GetAsync(AgentId id, CancellationToken ct = default)
        => Task.FromResult(GetOutcome.IsSuccess
            ? Result<AgentRecord>.Success(Current!)
            : Result<AgentRecord>.Failure(GetOutcome.Error!));

    public Task<Result<string>> UpdateAsync(AgentRecord record, CancellationToken ct = default)
    { Updated.Add(record); Current = record; return Task.FromResult(UpdateOutcome); }

    public Task<Result<string>> AppendMessageAsync(AgentId id, Message message, CancellationToken ct = default)
    { Appended.Add((id, message)); return Task.FromResult(AppendOutcome); }

    public Task<Result<IReadOnlyList<Message>>> GetTranscriptAsync(AgentId id, CancellationToken ct = default)
        => throw new NotSupportedException("Not exercised by RootSessionLifecycle tests.");

    public Task<Result<IReadOnlyList<AgentRecord>>> ListChildrenAsync(AgentId parentId, CancellationToken ct = default)
        => throw new NotSupportedException("Not exercised by RootSessionLifecycle tests.");

    public Task<Result<IReadOnlyList<AgentRecord>>> ListAllAsync(CancellationToken ct = default)
        => throw new NotSupportedException("Not exercised by RootSessionLifecycle tests.");
}

public class RootSessionLifecycleTests
{
    private static readonly AgentId RootId = AgentId.NewId();

    [Fact]
    public async Task Failed_Turn_Appends_Nothing()
    {
        var store = new FakeAgentStore();
        var lifecycle = new RootSessionLifecycle(store);
        var errors = new List<string>();
        await lifecycle.AppendExchangeAsync(RootId, new Conversation(), 0,
            Result<string>.Failure(new Error("E", "boom")), errors.Add);
        Assert.Empty(store.Appended);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task Successful_Turn_Appends_User_Then_Assistant_Message()
    {
        var store = new FakeAgentStore();
        var lifecycle = new RootSessionLifecycle(store);
        var conversation = new Conversation();
        conversation.AddUserMessage("hi");
        conversation.AddAssistantMessage("hello");
        await lifecycle.AppendExchangeAsync(RootId, conversation, 0,
            Result<string>.Success("hello"), _ => Assert.Fail("no errors expected"));
        Assert.Equal(2, store.Appended.Count);
        Assert.Equal(Role.User, store.Appended[0].Message.Role);
        Assert.Equal(Role.Assistant, store.Appended[^1].Message.Role);
        // The same Message instances the aggregate holds — never re-mapped copies.
        Assert.Same(conversation.Messages[0], store.Appended[0].Message);
        Assert.Same(conversation.Messages[1], store.Appended[1].Message);
    }

    [Fact]
    public async Task Append_Starts_At_MessageCountBefore_Offset()
    {
        var store = new FakeAgentStore();
        var lifecycle = new RootSessionLifecycle(store);
        var conversation = new Conversation();
        conversation.AddUserMessage("earlier user");
        conversation.AddAssistantMessage("earlier assistant");
        var user = "second user";
        conversation.AddUserMessage(user);
        conversation.AddAssistantMessage("second answer");
        await lifecycle.AppendExchangeAsync(RootId, conversation, 2,
            Result<string>.Success("second answer"), _ => Assert.Fail("no errors expected"));
        Assert.Equal(2, store.Appended.Count);
        Assert.Same(conversation.Messages[2], store.Appended[0].Message);
        Assert.Same(conversation.Messages[^1], store.Appended[1].Message);
    }

    [Fact]
    public async Task Append_Failures_Surface_Via_ReportError_And_Continue()
    {
        var store = new FakeAgentStore
        {
            AppendOutcome = Result<string>.Failure(new Error("DbDown", "nope"))
        };
        var lifecycle = new RootSessionLifecycle(store);
        var errors = new List<string>();
        var conversation = new Conversation();
        conversation.AddUserMessage("hi");
        conversation.AddAssistantMessage("hello");
        await lifecycle.AppendExchangeAsync(RootId, conversation, 0,
            Result<string>.Success("hello"), errors.Add);
        Assert.Equal(2, store.Appended.Count);      // second attempt still made
        Assert.Equal(2, errors.Count);              // both failures reported
        Assert.Contains("DbDown", errors[0]);
        Assert.Contains("DbDown", errors[1]);
    }

    [Fact]
    public async Task Complete_Marks_Row_Completed_Preserving_Other_Fields()
    {
        var createdAt = DateTimeOffset.UtcNow;
        var root = AgentRecord.Root(RootId, createdAt);
        var store = new FakeAgentStore { Current = root, GetOutcome = Result<AgentRecord>.Success(root) };
        var lifecycle = new RootSessionLifecycle(store);
        var errors = new List<string>();
        await lifecycle.CompleteAsync(RootId, errors.Add);
        Assert.Empty(errors);
        var persisted = store.Current!;
        Assert.Equal(AgentStatus.Completed, persisted.Status);
        Assert.NotNull(persisted.CompletedAt);
        // Preservation-by-reconstruction: every other field survives the transition.
        Assert.Equal(root.Id, persisted.Id);
        Assert.Equal(root.ParentId, persisted.ParentId);
        Assert.Equal(root.Depth, persisted.Depth);
        Assert.Equal(root.FailureReason, persisted.FailureReason);
        Assert.Equal(root.ModelUsed, persisted.ModelUsed);
        Assert.Equal(root.Label, persisted.Label);
        Assert.Equal(root.TaskPrompt, persisted.TaskPrompt);
        Assert.Equal(root.CreatedAt, persisted.CreatedAt);
        Assert.Null(persisted.FinalReport);
        Assert.Single(store.Updated);
        Assert.DoesNotContain(persisted, store.Saved); // update, never re-save
    }

    [Fact]
    public async Task Complete_When_Get_Fails_Reports_Error()
    {
        var store = new FakeAgentStore { GetOutcome = Result<AgentRecord>.Failure(new Error("Db", "down")) };
        var lifecycle = new RootSessionLifecycle(store);
        var errors = new List<string>();
        await lifecycle.CompleteAsync(AgentId.NewId(), errors.Add);
        Assert.Single(errors);
        Assert.Contains("Db", errors[0]);
    }

    [Fact]
    public async Task Complete_When_Update_Fails_Reports_Error()
    {
        var root = AgentRecord.Root(RootId, DateTimeOffset.UtcNow);
        var store = new FakeAgentStore
        {
            Current = root,
            GetOutcome = Result<AgentRecord>.Success(root),
            UpdateOutcome = Result<string>.Failure(new Error("DbDown", "write failed")),
        };
        var lifecycle = new RootSessionLifecycle(store);
        var errors = new List<string>();
        await lifecycle.CompleteAsync(RootId, errors.Add);
        Assert.Single(errors);
        Assert.Contains("DbDown", errors[0]);
    }
}
