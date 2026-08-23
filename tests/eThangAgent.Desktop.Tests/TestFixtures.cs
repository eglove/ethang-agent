using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop.Tests;

/// <summary>Shared fixtures for desktop UI tests: a stub turn runner, a stub store,
///     and the real RootSessionLifecycle so VM behavior is exercised against real
///     persistence semantics.</summary>
public static class TestFixtures
{
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

    /// <summary>Builds a MainViewModel whose turn runner streams "ack" and succeeds.</summary>
    public static MainViewModel CreateViewModel(Action? requestClose = null)
    {
        TurnRunner runner = (command, ct, onContentDelta, onReasoningDelta, onIterationEnd, onToolCall, onToolResult) =>
        {
            onContentDelta?.Invoke("ack");
            return Task.FromResult(Result<string>.Success("ack"));
        };
        return new MainViewModel(
            runner,
            new RootSessionLifecycle(new StubStore()),
            AgentId.NewId(),
            new Conversation(),
            "test/model",
            requestClose ?? (() => { }));
    }
}