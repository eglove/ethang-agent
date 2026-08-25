using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.Streaming;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop.Tests;

/// <summary>Shared fixtures for desktop UI tests: a stub turn runner, a stub store,
///     and the real RootSessionLifecycle so VM behavior is exercised against real
///     persistence semantics.</summary>
public static class TestFixtures
{
    /// <summary>Runtime stub for view-model tests: records interrupt calls, never starts runs.</summary>
    public sealed class StubAgentRuntime : IAgentRuntime
    {
        public int InterruptAllCount { get; private set; }

        public Task<Result<AgentId>> Start(AgentRecord record, CancellationToken ct = default)
            => Task.FromResult(Result<AgentId>.Success(record.Id));

        public void Interrupt(AgentId? childId = null)
        {
            if (childId is null) InterruptAllCount++;
        }
    }

    public sealed class StubStore : IAgentStore
    {
        public Task<Result<string>> SaveAsync(AgentRecord record, CancellationToken ct = default)
            => Task.FromResult(Result<string>.Success("saved"));
        public Task<Result<string>> UpdateAsync(AgentRecord record, CancellationToken ct = default)
            => Task.FromResult(Result<string>.Success("updated"));
        public Task<Result<AgentRecord>> GetAsync(AgentId id, CancellationToken ct = default)
            => Task.FromResult(Result<AgentRecord>.Failure(new Error("NotFound", "not found")));
        public Task<Result<string>> AppendMessageAsync(AgentId id, Message message, CancellationToken ct = default)
            => Task.FromResult(Result<string>.Success("appended"));
        public Task<Result<IReadOnlyList<Message>>> GetTranscriptAsync(AgentId id, CancellationToken ct = default)
            => Task.FromResult(Result<IReadOnlyList<Message>>.Success([]));
        public Task<Result<IReadOnlyList<AgentRecord>>> ListChildrenAsync(AgentId parentId, CancellationToken ct = default)
            => Task.FromResult(Result<IReadOnlyList<AgentRecord>>.Success([]));
        public Task<Result<IReadOnlyList<AgentRecord>>> ListAllAsync(CancellationToken ct = default)
            => Task.FromResult(Result<IReadOnlyList<AgentRecord>>.Success([]));
    }

    /// <summary>Builds an AgentSessionViewModel whose turn runner streams "ack" and succeeds.
    ///     When <paramref name="marshalToUIThread"/> is true the stream sink marshals onto
    ///     the UI thread (production shape — for headless window tests); otherwise events
    ///     apply on the pump thread (deterministic for plain unit tests).</summary>
    public static AgentSessionViewModel CreateViewModel(bool marshalToUIThread = false)
    {
        TurnRunner runner = (command, ct, onContentDelta, onReasoningDelta, onIterationEnd, onToolCall, onToolResult) =>
        {
            onContentDelta?.Invoke("ack");
            return Task.FromResult(Result<string>.Success("ack"));
        };

        AgentSessionViewModel? vmRef = null;
        var sink = marshalToUIThread
            ? (Func<UiStreamEvent, Task>)(e => vmRef!.ApplyUiStreamEventOnUIThreadAsync(e))
            : e => vmRef!.ApplyUiStreamEventAsync(e);
        var vm = new AgentSessionViewModel(
            runner,
            new RootSessionLifecycle(new StubStore()),
            AgentId.NewId(),
            new Conversation(),
            "test/model",
            workspaceRoot: @"C:\work\demo",
            uiStreamSink: sink);
        vmRef = vm;
        return vm;
    }
}