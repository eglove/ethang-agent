using eThangAgent.CapabilityDomain;
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
    Harness h = new();
    h.Store._rows[KnownId] = Row(KnownId);

    CapabilityInvocationResult result = await h.Provider().InvokeAsync("search", "{}", ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    Assert.Contains("id=" + KnownId.ToString() + " ", result.Content, StringComparison.Ordinal);
    Assert.DoesNotContain("id=3f2a9f0e ", result.Content, StringComparison.Ordinal); // no truncated prefix form
  }

  [Fact]
  public async Task Add_AcknowledgesAFullGuid_NotATruncatedPrefix()
  {
    Harness h = new();

    CapabilityInvocationResult result = await h.Provider().InvokeAsync("add",
        "{" + Q("content") + ":" + Q("c") + "," + Q("category") + ":" + Q("insight") + "," + Q("scope") + ":" + Q("workspace") + "}", ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsError, "add failed: " + result.Content);
    string ack = result.Content.Split('\n')[0];
    string idPart = ack.Replace("[memories] added ", "", StringComparison.Ordinal)[..36]; // 32 hex + 4 dashes
    Assert.True(Guid.TryParseExact(idPart, "D", out _), "full guid expected: " + ack);
  }

  [Fact]
  public async Task Remove_AcknowledgesFullGuid()
  {
    Harness h = new();
    h.Store._rows[KnownId] = Row(KnownId);

    string json = "{" + Q("id") + ":" + Q(KnownId.ToString()) + "," + Q("confirm") + ":true}";
    CapabilityInvocationResult result = await h.Provider().InvokeAsync("remove", json, ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    Assert.Contains("removed " + KnownId.ToString(), result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public void Description_AdvertisesFullGuidFormat()
  {
    CuratedMemoryCapabilityProvider provider = new Harness().Provider();
    ActionDescriptor search = Assert.Single(provider.Actions, a => a.Name == "search");
    Assert.Contains("[mem] id=<guid>", search.Description, StringComparison.Ordinal);
    Assert.DoesNotContain("<first8>", search.Description, StringComparison.Ordinal);
  }

  private sealed class FakeCuratedMemoryStore : ICuratedMemoryStore
  {
    internal Dictionary<Guid, CuratedMemory> _rows = [];

    public Task<Result<CuratedMemory>> AddAsync(CuratedMemory memory, CancellationToken ct = default)
    {
      _rows[memory.Id] = memory;
      return Task.FromResult(Result.Success(memory));
    }

    public Task<Result<CuratedMemory?>> GetAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult(Result.Success(_rows.GetValueOrDefault(id)));

    public Task<Result<IReadOnlyList<CuratedMemory>>> SearchAsync(
        string? workspaceId, string? query, MemoryCategory? category,
        IReadOnlyList<string>? tags, int limit, CancellationToken ct = default)
    {
      IEnumerable<CuratedMemory> visible = _rows.Values
          .Where(m => m.Scope == MemoryScope.Global || m.WorkspaceId == workspaceId)
          .OrderByDescending(m => m.UpdatedAt);
      if (!string.IsNullOrWhiteSpace(query))
      {
        visible = visible.Where(m => m.Content.Contains(query, StringComparison.OrdinalIgnoreCase));
      }

      return Task.FromResult(Result.Success<IReadOnlyList<CuratedMemory>>([.. visible.Take(limit)]));
    }

    public Task<Result<CuratedMemory>> UpdateAsync(CuratedMemory updated, CancellationToken ct = default)
    {
      _rows[updated.Id] = updated;
      return Task.FromResult(Result.Success(updated));
    }

    public Task<Result<bool>> DeleteAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult(Result.Success(_rows.Remove(id)));
    public Task<Result<int>> DeleteManyAsync(IReadOnlyList<Guid> ids, CancellationToken ct = default)
    {
      int deleted = 0;
      foreach (Guid id in ids)
      {
        if (_rows.Remove(id))
        {
          deleted++;
        }
      }

      return Task.FromResult(Result.Success(deleted));
    }
  }
}
