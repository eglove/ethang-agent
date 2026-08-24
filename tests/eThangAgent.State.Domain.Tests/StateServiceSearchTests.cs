using eThangAgent.SharedKernel;
using eThangAgent.StateDomain;

namespace eThangAgent.State.Domain.Tests;

public class StateServiceSearchTests
{
    private readonly FakeSearchStore _store = new();
    private StateService Service() => new(
        _store, new FakeEvidenceRunner(), new FixedWs("ws1"));

    [Fact]
    public async Task Search_PassesQueryAndLimit_ToStore()
    {
        _store.Hits = [new StateSearchHit("plans", "p1", "snippet one")];
        var r = await Service().SearchAsync("ledger", 5);
        Assert.True(r.IsSuccess);
        Assert.Equal("ledger", _store.LastQuery);
        Assert.Equal(5, _store.LastLimit);
        Assert.Single(r.Value!);
        Assert.Equal("plans/p1", $"{r.Value![0].Ns}/{r.Value![0].Name}");
    }

    [Fact]
    public async Task Search_EmptyOrWhitespaceQuery_Fails()
    {
        Assert.Equal("InvalidQuery", (await Service().SearchAsync("", 20)).Error!.Code);
        Assert.Equal("InvalidQuery", (await Service().SearchAsync("   ", 20)).Error!.Code);
        Assert.Null(_store.LastQuery); // never reached the store
    }

    [Fact]
    public async Task Search_LimitOutOfRange_Fails()
    {
        Assert.Equal("InvalidLimit", (await Service().SearchAsync("x", 0)).Error!.Code);
        Assert.Equal("InvalidLimit", (await Service().SearchAsync("x", 101)).Error!.Code);
        Assert.Null(_store.LastQuery);
    }

    [Fact]
    public async Task Search_LimitBoundaryValues_Accepted()
    {
        await Service().SearchAsync("x", 1);
        await Service().SearchAsync("x", 100);
        Assert.Equal(100, _store.LastLimit);
    }

    [Fact]
    public async Task Search_StoreFailure_SurfacesAsGutter()
    {
        _store.Failure = new Error("InvalidQuery", "fts5: syntax error near \"(\"");
        var r = await Service().SearchAsync("AND (", 20);
        Assert.False(r.IsSuccess);
        Assert.Equal("InvalidQuery", r.Error!.Code);
    }

    private sealed class FakeSearchStore : IStateStore
    {
        public IReadOnlyList<StateSearchHit> Hits { get; set; } = [];
        public string? LastQuery { get; private set; }
        public int? LastLimit { get; private set; }
        public Error? Failure { get; set; }

        public Task<StateKeyValue?> GetKeyAsync(string workspaceId, string ns, string name, CancellationToken ct = default)
            => Task.FromResult<StateKeyValue?>(null);

        public Task<IReadOnlyList<StateKeyValue>> ListKeysAsync(string workspaceId, string? ns, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<StateKeyValue>>([]);

        public Task<StateKeyValue?> SetKeyCasAsync(string workspaceId, string ns, string name, string value, int? expectedVersion, CancellationToken ct = default)
            => Task.FromResult<StateKeyValue?>(null);

        public Task<bool> DeleteKeyCasAsync(string workspaceId, string ns, string name, int? expectedVersion, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task<Result<IReadOnlyList<StateSearchHit>>> SearchKeysAsync(string workspaceId, string query, int limit, CancellationToken ct = default)
        {
            if (Failure is not null)
                return Task.FromResult(Result<IReadOnlyList<StateSearchHit>>.Failure(Failure));
            LastQuery = query;
            LastLimit = limit;
            return Task.FromResult(Result<IReadOnlyList<StateSearchHit>>.Success(Hits));
        }

        public Task<TransitionRecord> InsertTransitionAsync(string workspaceId, TransitionRecord transition, CancellationToken ct = default)
            => Task.FromResult(transition);

        public Task<IReadOnlyList<TransitionRecord>> GetTransitionsAsync(string workspaceId, IReadOnlyList<string> ids, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TransitionRecord>>([]);

        public Task SetTransitionStatusAsync(string workspaceId, string transitionId, string status, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task AppendEventAsync(string workspaceId, string kind, string payloadJson, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<StateEvent>> GetEventsAsync(string workspaceId, int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<StateEvent>>([]);
    }

    private sealed class FakeEvidenceRunner : IEvidenceRunner
    {
        public Task<EvidenceResult> RunAsync(string command, CancellationToken ct = default)
            => Task.FromResult(new EvidenceResult(command, true, ""));
    }

    private sealed class FixedWs(string id) : IWorkspaceContext
    {
        public string WorkspaceId { get; } = id;
    }
}