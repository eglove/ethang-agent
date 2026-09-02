using eThangAgent.SharedKernel;
namespace eThangAgent.AgentDomain.Tests;

/// <summary>Link semantics: consent required, resolve only through links, revocation exact.
///     The NotLinked contract of the source spec's error table is pinned here.</summary>
public class AgentLinkRegistryTests
{
  [Fact]
  public void Link_WithoutConsent_IsRefused()
  {
    AgentLinkRegistry registry = new();
    Result<LinkAddress> linked = registry.Link("peer", "container-a", "agent-1", consented: false);
    Assert.False(linked.IsSuccess);
    Assert.False(linked.IsSuccess);
    Assert.Equal("ConsentRequired", linked.Error.Code);
  }

  [Fact]
  public void Resolve_UnknownName_FailsNotLinked()
  {
    AgentLinkRegistry registry = new();
    Result<LinkAddress> resolved = registry.Resolve("nope");
    Assert.False(resolved.IsSuccess);
    Assert.Equal("NotLinked", resolved.Error.Code);
  }

  [Fact]
  public void Link_Resolve_Revoke_RoundTrip()
  {
    AgentLinkRegistry registry = new();
    _ = registry.Link("peer", "container-a", "agent-1", consented: true);

    Result<LinkAddress> resolved = registry.Resolve("peer");
    string address = resolved.Match(success => success.AgentAddress, error => throw new InvalidOperationException(error.Message));
    Assert.Equal("agent-1", address);

    Assert.True(registry.Revoke("peer").IsSuccess);
    Result<LinkAddress> after = registry.Resolve("peer");
    Assert.False(after.IsSuccess);
    Assert.Equal("NotLinked", after.Error.Code);
  }

  [Fact]
  public void Resolve_RevealsNothingBeyondTheAddress_TrustModel_R2_4()
  {
    AgentLinkRegistry registry = new();
    _ = registry.Link("peer", "container-a", "agent-1", consented: true);

    Result<LinkAddress> resolved = registry.Resolve("peer");
    Assert.True(resolved.IsSuccess);
    LinkAddress address = resolved.Value;
    // The linker's records carry consent state and a linked-at timestamp; Resolve
    // exposes exactly Name, Container, AgentAddress — nothing else (open question 6).
    Assert.Equal("peer", address.Name);
    Assert.Equal("container-a", address.Container);
    Assert.Equal("agent-1", address.AgentAddress);
    System.Reflection.PropertyInfo[] exposed = typeof(LinkAddress).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
    string[] names = [.. exposed.Select(pr => pr.Name).OrderBy(n => n, StringComparer.Ordinal)];
    Assert.Equal(["AgentAddress", "Container", "Name"], names);
  }

  [Fact]
  public void Revoke_UnknownName_FailsNotFound()
  {
    AgentLinkRegistry registry = new();
    Result<bool> revoked = registry.Revoke("ghost");
    Assert.False(revoked.IsSuccess);
    Assert.Equal("NotFound", revoked.Error.Code);
  }

  [Fact]
  public void Snapshot_ListsLiveLinks_AndReflectsRevocation()
  {
    AgentLinkRegistry registry = new();
    _ = registry.Link("alpha", "container-a", "00000000-0000-0000-0000-000000000001", consented: true);
    _ = registry.Link("beta", "container-b", "00000000-0000-0000-0000-000000000002", consented: true);

    IReadOnlyList<LinkAddress> snapshot = registry.Snapshot;
    Assert.Equal(2, snapshot.Count);
    // Newest first: the most recently created link leads the dialog's list.
    Assert.Equal("beta", snapshot[0].Name);
    Assert.Equal("alpha", snapshot[1].Name);

    _ = registry.Revoke("alpha");
    LinkAddress remaining = Assert.Single(registry.Snapshot);
    Assert.Equal("beta", remaining.Name);
  }

  /// <summary>Fake store for domain tests: seeded rows + recorded operations, no SQL.</summary>
  private sealed class FakeLinkStore : ILinkStore
  {
    public Dictionary<string, StoredLink> Rows { get; } = [];
    public List<string> Operations { get; } = [];
    public List<string> Workspaces { get; } = [];
    public Result<IReadOnlyList<StoredLink>>? ListFailure { get; set; }
    public bool FailUpserts { get; set; }

    public Result<IReadOnlyList<StoredLink>> List(string workspaceId)
    {
      Workspaces.Add(workspaceId);
      Operations.Add("list");
      return ListFailure is { } failure
          ? failure
          : Result.Success<IReadOnlyList<StoredLink>>([.. Rows.Values]);
    }

    public Result<string> Upsert(string workspaceId, StoredLink link)
    {
      Workspaces.Add(workspaceId);
      return FailUpserts
          ? Result.Failure<string>(new DomainError("StorageUnavailable", "upsert refused by the fake."))
          : UpsertRow(link);
    }

    public Result<bool> Delete(string workspaceId, string name)
    {
      Workspaces.Add(workspaceId);
      Operations.Add("delete:" + name);
      return Result.Success(Rows.Remove(name));
    }

    private Result<string> UpsertRow(StoredLink link)
    {
      Rows[link.Name] = link;
      return Result.Success(link.Name);
    }

    public static StoredLink Row(string name, string address) =>
        new(name, "container-a", address, new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero));
  }

  #region W2 store-backed registry (hydrate + write-through)

  [Fact]
  public void Construct_WithStore_Hydrates_Persisted_Links()
  {
    FakeLinkStore store = new()
    {
      Rows =
      {
        ["researcher"] = FakeLinkStore.Row("researcher", "00000000-0000-0000-0000-000000000001"),
        ["writer"] = FakeLinkStore.Row("writer", "00000000-0000-0000-0000-000000000002"),
      },
    };
    AgentLinkRegistry registry = new(store, () => "ws-42");

    LinkAddress[] snapshot = [.. registry.Snapshot];
    Assert.Equal(2, snapshot.Length);
    Result<LinkAddress> resolved = registry.Resolve("researcher");
    Assert.True(resolved.IsSuccess);
    // R2.4 holds for hydrated rows too: exactly the address tuple.
    Assert.Equal("00000000-0000-0000-0000-000000000001", resolved.Value.AgentAddress);
  }


  [Fact]
  public void Link_Writes_Through_To_The_Store()
  {
    FakeLinkStore store = new();
    AgentLinkRegistry registry = new(store, () => "ws-42");

    Result<LinkAddress> linked = registry.Link("researcher", "container-a", "00000000-0000-0000-0000-000000000001", consented: true);
    Assert.True(linked.IsSuccess);
    StoredLink row = Assert.Single(store.Rows.Values);
    Assert.Equal("researcher", row.Name);
    Assert.Equal("container-a", row.Container);
    Assert.Equal("00000000-0000-0000-0000-000000000001", row.AgentAddress);
  }

  [Fact]
  public void Link_Same_Name_RePoints_In_Memory_And_Store()
  {
    FakeLinkStore store = new();
    AgentLinkRegistry registry = new(store, () => "ws-42");
    _ = registry.Link("researcher", "container-a", "00000000-0000-0000-0000-000000000001", consented: true);
    _ = registry.Link("researcher", "container-a", "00000000-0000-0000-0000-000000000002", consented: true);

    LinkAddress remaining = Assert.Single(registry.Snapshot);
    Assert.Equal("00000000-0000-0000-0000-000000000002", remaining.AgentAddress);
    StoredLink row = Assert.Single(store.Rows.Values);
    Assert.Equal("00000000-0000-0000-0000-000000000002", row.AgentAddress);
  }

  [Fact]
  public void Revoke_Deletes_The_Persisted_Row_And_Stays_Gone()
  {
    FakeLinkStore store = new()
    {
      Rows = { ["researcher"] = FakeLinkStore.Row("researcher", "00000000-0000-0000-0000-000000000001") },
    };
    AgentLinkRegistry registry = new(store, () => "ws-42");

    Assert.True(registry.Revoke("researcher").IsSuccess);
    Assert.Empty(store.Rows);
    Assert.Empty(registry.Snapshot);

    // A fresh registry over the same store does not see it: revocation is permanent.
    AgentLinkRegistry fresh = new(store, () => "ws-42");
    Result<LinkAddress> gone = fresh.Resolve("researcher");
    Assert.False(gone.IsSuccess);
    Assert.Equal("NotLinked", gone.Error.Code);
  }

  [Fact]
  public void Revoke_Unknown_Name_Fails_NotFound_Without_A_Store_Write()
  {
    FakeLinkStore store = new();
    AgentLinkRegistry registry = new(store, () => "ws-42");

    Result<bool> revoked = registry.Revoke("ghost");
    Assert.False(revoked.IsSuccess);
    Assert.Equal("NotFound", revoked.Error.Code);
    Assert.DoesNotContain(store.Operations, op => op == "delete:ghost");
  }

  [Fact]
  public void Consent_Failure_Writes_Nothing()
  {
    FakeLinkStore store = new();
    AgentLinkRegistry registry = new(store, () => "ws-42");

    Result<LinkAddress> linked = registry.Link("peer", "c", "agent-1", consented: false);
    Assert.False(linked.IsSuccess);
    Assert.Equal("ConsentRequired", linked.Error.Code);
    // Store untouched: the Operations list starts with exactly the hydration read.
    Assert.Equal(["list"], store.Operations);
  }

  [Fact]
  public void Store_Failure_On_Link_Rolls_Back_And_Surfaces()
  {
    FakeLinkStore store = new();
    AgentLinkRegistry registry = new(store, () => "ws-42");
    _ = registry.Link("stable", "container-a", "00000000-0000-0000-0000-000000000001", consented: true);
    store.FailUpserts = true;

    Result<LinkAddress> linked = registry.Link("researcher", "container-a", "00000000-0000-0000-0000-000000000002", consented: true);
    Assert.False(linked.IsSuccess);
    Assert.Equal("StorageUnavailable", linked.Error.Code);
    // Memory rolled back: the failed link never resolves; the earlier one still does.
    Result<LinkAddress> failed = registry.Resolve("researcher");
    Assert.False(failed.IsSuccess);
    Assert.Equal("NotLinked", failed.Error.Code);
    Result<LinkAddress> stable = registry.Resolve("stable");
    Assert.True(stable.IsSuccess);
    Assert.Equal("00000000-0000-0000-0000-000000000001", stable.Value.AgentAddress);
  }

  [Fact]
  public void Hydration_Failure_Throws_Named_Infrastructure_Error()
  {
    FakeLinkStore store = new()
    {
      ListFailure = Result.Failure<IReadOnlyList<StoredLink>>(new DomainError("StorageUnavailable", "db locked")),
    };
    InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() => new AgentLinkRegistry(store, () => "ws-42"));
    Assert.Contains("StorageUnavailable", failure.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void Workspace_Scoping_Flows_Into_Every_Store_Call()
  {
    FakeLinkStore store = new();
    AgentLinkRegistry registry = new(store, () => "ws-42");
    _ = registry.Link("researcher", "container-a", "00000000-0000-0000-0000-000000000001", consented: true);
    _ = registry.Revoke("researcher");

    Assert.NotEmpty(store.Workspaces);
    Assert.All(store.Workspaces, ws => Assert.Equal("ws-42", ws));
  }

  #endregion
}
