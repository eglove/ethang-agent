using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Composition.Tests;

internal sealed class FakeAgentStore : IAgentStore
{
  public List<AgentRecord> _saved = [];
  public List<AgentRecord> _updated = [];
  public List<(AgentId Id, Message Message)> _appended = [];
  public AgentRecord? _current;
  public Result<string> _saveOutcome = Result.Success("saved");
  public Result<AgentRecord> _getOutcome =
      Result.Failure<AgentRecord>(new DomainError("NotConfigured", "Get not configured"));
  public Result<string> _updateOutcome = Result.Success("updated");
  public Result<string> _appendOutcome = Result.Success("appended");

  public Task<Result<string>> SaveAsync(AgentRecord record, CancellationToken ct = default)
  {
    _saved.Add(record);
    return Task.FromResult(_saveOutcome);
  }

  public Task<Result<AgentRecord>> GetAsync(AgentId id, CancellationToken ct = default)
      => Task.FromResult(_getOutcome.IsSuccess
          ? Result.Success(_current!)
          : Result.Failure<AgentRecord>(_getOutcome.Error!));

  public Task<Result<string>> UpdateAsync(AgentRecord record, CancellationToken ct = default)
  {
    _updated.Add(record);
    _current = record;
    return Task.FromResult(_updateOutcome);
  }

  public Task<Result<string>> AppendMessageAsync(AgentId id, Message message, CancellationToken ct = default)
  {
    _appended.Add((id, message));
    return Task.FromResult(_appendOutcome);
  }

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
  public async Task Failed_Turn_Still_Appends_Its_Slice()
  {
    // A provider failure leaves the user message in the conversation; a cancelled turn
    // is protocol-repaired. Either way the slice is valid transcript — resume fidelity
    // requires it to persist even though the turn result is a failure.
    FakeAgentStore store = new();
    RootSessionLifecycle lifecycle = new(store);
    Conversation conversation = new();
    conversation.AddUserMessage("hi");
    List<string> errors = [];
    await lifecycle.AppendExchangeAsync(RootId, conversation, 0,
        Result.Failure<string>(new DomainError("E", "boom")), errors.Add);
    _ = Assert.Single(store._appended);
    Assert.Same(conversation.Messages[0], store._appended[0].Message);
    Assert.Empty(errors);
  }

  [Fact]
  public async Task Turn_Slice_Appends_Every_Message_In_Order_Including_Tool_Traffic_And_Nudges()
  {
    FakeAgentStore store = new();
    RootSessionLifecycle lifecycle = new(store);
    Conversation conversation = new();
    conversation.AddUserMessage("list files");
    IReadOnlyList<ToolCall> calls = [new ToolCall("call-1", "search", /*lang=json,strict*/ "{\"pattern\":\"*.cs\"}")];
    conversation.AddAssistantMessage("", calls);
    conversation.AddToolResult("call-1", "found 3 files");
    conversation.AddAssistantMessage("found three files");
    conversation.AddSystemMessage("nudge: write a memory");
    await lifecycle.AppendExchangeAsync(RootId, conversation, 0,
        Result.Success("found three files"), _ => Assert.Fail("no errors expected"));
    Assert.Equal(5, store._appended.Count);
    Assert.Equal(Role.User, store._appended[0].Message.Role);
    Assert.Equal(Role.Assistant, store._appended[1].Message.Role);
    Assert.Equal(calls, store._appended[1].Message.ToolCalls);
    Assert.Equal(Role.Tool, store._appended[2].Message.Role);
    Assert.Equal("call-1", store._appended[2].Message.ToolCallId);
    Assert.Equal(Role.Assistant, store._appended[3].Message.Role);
    // The nudge lands AFTER the assistant message — the persisted order is the true
    // conversation order, never the old "final message only" snapshot.
    Assert.Equal(Role.System, store._appended[^1].Message.Role);
    // The same Message instances the aggregate holds — never re-mapped copies.
    Assert.Same(conversation.Messages[0], store._appended[0].Message);
    Assert.Same(conversation.Messages[^1], store._appended[^1].Message);
  }

  [Fact]
  public async Task OutOfRange_MessageCountBefore_Reports_And_Appends_Nothing()
  {
    FakeAgentStore store = new();
    RootSessionLifecycle lifecycle = new(store);
    Conversation conversation = new();
    conversation.AddUserMessage("hi");
    List<string> errors = [];
    await lifecycle.AppendExchangeAsync(RootId, conversation, 5,
        Result.Success("hi"), errors.Add);
    Assert.Empty(store._appended);
    _ = Assert.Single(errors);
    Assert.Contains("InvalidSlice", errors[0], StringComparison.Ordinal);
  }

  [Fact]
  public async Task Append_Starts_At_MessageCountBefore_Offset()
  {
    FakeAgentStore store = new();
    RootSessionLifecycle lifecycle = new(store);
    Conversation conversation = new();
    conversation.AddUserMessage("earlier user");
    conversation.AddAssistantMessage("earlier assistant");
    string user = "second user";
    conversation.AddUserMessage(user);
    conversation.AddAssistantMessage("second answer");
    await lifecycle.AppendExchangeAsync(RootId, conversation, 2,
        Result.Success("second answer"), _ => Assert.Fail("no errors expected"));
    Assert.Equal(2, store._appended.Count);
    Assert.Same(conversation.Messages[2], store._appended[0].Message);
    Assert.Same(conversation.Messages[^1], store._appended[1].Message);
  }

  [Fact]
  public async Task Append_Failures_Surface_Via_ReportError_And_Continue()
  {
    FakeAgentStore store = new()
    {
      _appendOutcome = Result.Failure<string>(new DomainError("DbDown", "nope"))
    };
    RootSessionLifecycle lifecycle = new(store);
    List<string> errors = [];
    Conversation conversation = new();
    conversation.AddUserMessage("hi");
    conversation.AddAssistantMessage("hello");
    await lifecycle.AppendExchangeAsync(RootId, conversation, 0,
        Result.Success("hello"), errors.Add);
    Assert.Equal(2, store._appended.Count);      // second attempt still made
    Assert.Equal(2, errors.Count);              // both failures reported
    Assert.Contains("DbDown", errors[0], StringComparison.Ordinal);
    Assert.Contains("DbDown", errors[1], StringComparison.Ordinal);
  }

  [Fact]
  public async Task Complete_Marks_Row_Completed_Preserving_Other_Fields()
  {
    DateTimeOffset createdAt = DateTimeOffset.UtcNow;
    AgentRecord root = AgentRecord.Root(RootId, createdAt, @"C:\workspaces\demo", "openrouter");
    FakeAgentStore store = new() { _current = root, _getOutcome = Result.Success(root) };
    RootSessionLifecycle lifecycle = new(store);
    List<string> errors = [];
    await lifecycle.CompleteAsync(RootId, errors.Add);
    Assert.Empty(errors);
    AgentRecord persisted = store._current;
    Assert.Equal(AgentStatus.Completed, persisted.Status);
    _ = Assert.NotNull(persisted.CompletedAt);
    // Preservation-by-reconstruction: every other field survives the transition.
    Assert.Equal(root.Id, persisted.Id);
    Assert.Equal(root.ParentId, persisted.ParentId);
    Assert.Equal(root.Depth, persisted.Depth);
    Assert.Equal(root.FailureReason, persisted.FailureReason);
    Assert.Equal(root.ModelUsed, persisted.ModelUsed);
    Assert.Equal(root.Label, persisted.Label);
    Assert.Equal(root.TaskPrompt, persisted.TaskPrompt);
    Assert.Equal(root.CreatedAt, persisted.CreatedAt);
    Assert.Equal(root.WorkspaceId, persisted.WorkspaceId);
    Assert.Equal(root.Provider, persisted.Provider);
    Assert.Null(persisted.FinalReport);
    _ = Assert.Single(store._updated);
    Assert.DoesNotContain(persisted, store._saved); // update, never re-save
  }

  [Fact]
  public async Task Complete_When_Get_Fails_Reports_Error()
  {
    FakeAgentStore store = new() { _getOutcome = Result.Failure<AgentRecord>(new DomainError("Db", "down")) };
    RootSessionLifecycle lifecycle = new(store);
    List<string> errors = [];
    await lifecycle.CompleteAsync(AgentId.NewId(), errors.Add);
    _ = Assert.Single(errors);
    Assert.Contains("Db", errors[0], StringComparison.Ordinal);
  }

  [Fact]
  public async Task Complete_When_Update_Fails_Reports_Error()
  {
    AgentRecord root = AgentRecord.Root(RootId, DateTimeOffset.UtcNow, @"C:\workspaces\demo", "openrouter");
    FakeAgentStore store = new()
    {
      _current = root,
      _getOutcome = Result.Success(root),
      _updateOutcome = Result.Failure<string>(new DomainError("DbDown", "write failed")),
    };
    RootSessionLifecycle lifecycle = new(store);
    List<string> errors = [];
    await lifecycle.CompleteAsync(RootId, errors.Add);
    _ = Assert.Single(errors);
    Assert.Contains("DbDown", errors[0], StringComparison.Ordinal);
  }
}
