using eThangAgent.SharedKernel;

namespace eThangAgent.MemoryDomain.Tests;

/// <summary>Search/update/remove round-trip on ids: every [mem] line and action
/// acknowledgement renders the FULL GUID so the model can copy an id from a search
/// result straight into update/remove without guessing at truncation.</summary>
public class CuratedMemoryFullIdTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid KnownId = Guid.Parse("3f2a9f0e-1111-2222-3333-444455556666");

    private sealed class Harness
    {
        public FakeCuratedMemoryStore Store { get; } = new();

        public CuratedMemoryCapabilityProvider Provider() => new(
            Store,
            () => "ws-abc-123",
            () => "session-xyz",
            () => 1,
            () => FixedNow);
    }

    private static CuratedMemory Row(Guid id) => new(
        id, "ws-abc-123", MemoryCategory.Insight, ["t"], "content",
        null, MemoryScope.Workspace, "session-xyz", 1, FixedNow, FixedNow);

    private static string Q(string s) => '"' + s + '"';

    [Fact]
    public async Task Search_RendersFullGuid()
    {
        var h = new Harness();
        h.Store.Rows[KnownId] = Row(KnownId);

        var result = await h.Provider().InvokeAsync("search", "{}");

        Assert.False(result.IsError);
        Assert.Contains("id=" + KnownId.ToString() + " ", result.Content);
        Assert.DoesNotContain("id=3f2a9f0e ", result.Content); // no truncated prefix form
    }

    [Fact]
    public async Task Add_AcknowledgesAFullGuid_NotATruncatedPrefix()
    {
        var h = new Harness();

        var result = await h.Provider().InvokeAsync("add",
            "{" + Q("content") + ":" + Q("c") + "," + Q("category") + ":" + Q("insight") + "," + Q("scope") + ":" + Q("workspace") + "}");

        Assert.False(result.IsError, "add failed: " + result.Content);
        var ack = result.Content.Split('\n')[0];
        var idPart = ack.Replace("[memories] added ", "")[..36]; // 32 hex + 4 dashes
        Assert.True(Guid.TryParseExact(idPart, "D", out _), "full guid expected: " + ack);
    }

    [Fact]
    public async Task Remove_AcknowledgesFullGuid()
    {
        var h = new Harness();
        h.Store.Rows[KnownId] = Row(KnownId);

        var json = "{" + Q("id") + ":" + Q(KnownId.ToString()) + "," + Q("confirm") + ":true}";
        var result = await h.Provider().InvokeAsync("remove", json);

        Assert.False(result.IsError);
        Assert.Contains("removed " + KnownId.ToString(), result.Content);
    }

    [Fact]
    public void Description_AdvertisesFullGuidFormat()
    {
        var provider = new Harness().Provider();
        var search = Assert.Single(provider.Actions, a => a.Name == "search");
        Assert.Contains("[mem] id=<guid>", search.Description);
        Assert.DoesNotContain("<first8>", search.Description);
    }

    private sealed class FakeCuratedMemoryStore : ICuratedMemoryStore
    {
        public Dictionary<Guid, CuratedMemory> Rows = [];

        public Task<Result<CuratedMemory>> AddAsync(CuratedMemory memory, CancellationToken ct = default)
        {
            Rows[memory.Id] = memory;
            return Task.FromResult(Result<CuratedMemory>.Success(memory));
        }

        public Task<Result<CuratedMemory?>> GetAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(Result<CuratedMemory?>.Success(Rows.GetValueOrDefault(id)));

        public Task<Result<IReadOnlyList<CuratedMemory>>> SearchAsync(
            string? workspaceId, string? query, MemoryCategory? category,
            IReadOnlyList<string>? tags, int limit, CancellationToken ct = default)
        {
            IEnumerable<CuratedMemory> visible = Rows.Values
                .Where(m => m.Scope == MemoryScope.Global || m.WorkspaceId == workspaceId)
                .OrderByDescending(m => m.UpdatedAt);
            if (!string.IsNullOrWhiteSpace(query))
                visible = visible.Where(m => m.Content.Contains(query, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(Result<IReadOnlyList<CuratedMemory>>.Success(visible.Take(limit).ToList()));
        }

        public Task<Result<CuratedMemory>> UpdateAsync(CuratedMemory updated, CancellationToken ct = default)
        {
            Rows[updated.Id] = updated;
            return Task.FromResult(Result<CuratedMemory>.Success(updated));
        }

        public Task<Result<bool>> DeleteAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(Result<bool>.Success(Rows.Remove(id)));
    }
}
