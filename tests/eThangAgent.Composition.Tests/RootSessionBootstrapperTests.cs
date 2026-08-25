using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.SharedKernel;

namespace eThangAgent.Composition.Tests;

/// <summary>The root-session bootstrap is duplicated logic with a silent-failure mode:
/// when the host that persists AgentRecord.Root and the host that appends transcript
/// messages disagree on the id, memory recall quietly loses the session. Both hosts
/// must obtain the persisted root id from this ONE helper.</summary>
public class RootSessionBootstrapperTests
{
    [Fact]
    public async Task PersistRoot_PersistsARunningDepthZeroRoot_AndReturnsTheSameId()
    {
        var store = new FakeAgentStore();
        var result = await RootSessionBootstrapper.PersistRootAsync(store);

        Assert.True(result.IsSuccess);
        var record = Assert.Single(store.Saved);
        Assert.Equal(result.Value!, record.Id);
        Assert.Null(record.ParentId);
        Assert.Equal(0, record.Depth);
        Assert.Equal(AgentStatus.Running, record.Status);
    }

    [Fact]
    public async Task PersistRoot_StoreFailure_SurfacesTheStoreError()
    {
        var store = new FakeAgentStore { FailOnSave = true };
        var result = await RootSessionBootstrapper.PersistRootAsync(store);

        Assert.False(result.IsSuccess);
        Assert.Equal("PersistFailed", result.Error!.Code);
        Assert.Empty(store.Saved);
    }

    private sealed class FakeAgentStore : IAgentStore
    {
        public bool FailOnSave { get; init; }
        public System.Collections.Generic.List<AgentRecord> Saved { get; } = [];

        public Task<Result<string>> SaveAsync(AgentRecord record, CancellationToken ct = default)
        {
            if (FailOnSave)
                return Task.FromResult(Result<string>.Failure(new Error("PersistFailed", "boom")));
            Saved.Add(record);
            return Task.FromResult(Result<string>.Success(record.Id.ToString()));
        }

        public Task<Result<string>> UpdateAsync(AgentRecord record, CancellationToken ct = default) =>
            Task.FromResult(Result<string>.Success(record.Id.ToString()));

        public Task<Result<AgentRecord>> GetAsync(AgentId id, CancellationToken ct = default) =>
            Task.FromResult(Result<AgentRecord>.Failure(new Error("NotFound", "no")));

        public Task<Result<string>> AppendMessageAsync(AgentId id, ConversationDomain.Message message, CancellationToken ct = default) =>
            Task.FromResult(Result<string>.Success("ok"));

        public Task<Result<IReadOnlyList<ConversationDomain.Message>>> GetTranscriptAsync(AgentId id, CancellationToken ct = default) =>
            Task.FromResult(Result<IReadOnlyList<ConversationDomain.Message>>.Success([]));

        public Task<Result<IReadOnlyList<AgentRecord>>> ListChildrenAsync(AgentId parentId, CancellationToken ct = default) =>
            Task.FromResult(Result<IReadOnlyList<AgentRecord>>.Success([]));

        public Task<Result<IReadOnlyList<AgentRecord>>> ListAllAsync(CancellationToken ct = default) =>
            Task.FromResult(Result<IReadOnlyList<AgentRecord>>.Success([]));
    }
}
