using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Storage.ACL.Tests;

/// <summary>W2.5 store round-trip: upsert/list/delete against real SQLite, replace-by-name
///     semantics, and workspace scoping — the persistence contract the link registry rides.</summary>
public sealed class SqliteLinkStoreTests : IDisposable
{
  private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ethang-links-{Guid.NewGuid():N}.db");
  private readonly AppDatabase _db;
  private readonly SqliteLinkStore _store;

  public SqliteLinkStoreTests()
  {
    _db = new AppDatabase(_dbPath);
    _store = new SqliteLinkStore(_db);
  }

  public void Dispose()
  {
    GC.SuppressFinalize(this);
    // Named decision (CA1031): temp-db cleanup is best effort.
#pragma warning disable CA1031, S108 // Do not catch general exception types
    try
    {
      File.Delete(_dbPath);
    }
    catch
    {
    }
#pragma warning restore CA1031, S108
  }

  private static StoredLink Link(string name, string address) =>
      new(name, "container-a", address, new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero));

  private IReadOnlyList<StoredLink> Listed(string workspaceId)
  {
    Result<IReadOnlyList<StoredLink>> listed = _store.List(workspaceId);
    Assert.True(listed.IsSuccess);
    return listed.Value;
  }

  [Fact]
  public void Upsert_Then_List_RoundTrips_All_Fields()
  {
    Result<string> upserted = _store.Upsert("ws-a", Link("researcher", "00000000-0000-0000-0000-000000000001"));
    Assert.True(upserted.IsSuccess);

    StoredLink row = Assert.Single(Listed("ws-a"));
    Assert.Equal("researcher", row.Name);
    Assert.Equal("container-a", row.Container);
    Assert.Equal("00000000-0000-0000-0000-000000000001", row.AgentAddress);
    Assert.Equal(new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero), row.LinkedAt);
  }

  [Fact]
  public void Upsert_Same_Name_Replaces_Address_And_Timestamp()
  {
    _ = _store.Upsert("ws-a", Link("researcher", "00000000-0000-0000-0000-000000000001"));
    StoredLink replacement = new("researcher", "container-a", "00000000-0000-0000-0000-000000000002",
        new DateTimeOffset(2026, 9, 2, 8, 0, 0, TimeSpan.Zero));
    _ = _store.Upsert("ws-a", replacement);

    StoredLink row = Assert.Single(Listed("ws-a"));
    Assert.Equal("00000000-0000-0000-0000-000000000002", row.AgentAddress);
    Assert.Equal(replacement.LinkedAt, row.LinkedAt);
  }

  [Fact]
  public void List_Scopes_By_Workspace()
  {
    _ = _store.Upsert("ws-a", Link("researcher", "00000000-0000-0000-0000-000000000001"));
    _ = _store.Upsert("ws-b", Link("researcher", "00000000-0000-0000-0000-000000000002"));

    StoredLink a = Assert.Single(Listed("ws-a"));
    Assert.Equal("00000000-0000-0000-0000-000000000001", a.AgentAddress);
    _ = Assert.Single(Listed("ws-b"));
  }

  [Fact]
  public void List_Orders_Newest_First()
  {
    _ = _store.Upsert("ws-a", new StoredLink("old", "c", "a1", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));
    _ = _store.Upsert("ws-a", new StoredLink("new", "c", "a2", new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero)));

    IReadOnlyList<StoredLink> rows = Listed("ws-a");
    Assert.Equal("new", rows[0].Name);
    Assert.Equal("old", rows[1].Name);
  }

  [Fact]
  public void Delete_Removes_The_Row_And_Reports_True_Absent_Reports_False()
  {
    _ = _store.Upsert("ws-a", Link("researcher", "00000000-0000-0000-0000-000000000001"));

    Result<bool> deleted = _store.Delete("ws-a", "researcher");
    Assert.True(deleted.IsSuccess);
    Assert.True(deleted.Value);
    Assert.Empty(Listed("ws-a"));

    Result<bool> again = _store.Delete("ws-a", "researcher");
    Assert.True(again.IsSuccess);
    Assert.False(again.Value);
  }

  [Fact]
  public void Delete_Scopes_By_Workspace()
  {
    _ = _store.Upsert("ws-a", Link("researcher", "00000000-0000-0000-0000-000000000001"));
    _ = _store.Upsert("ws-b", Link("researcher", "00000000-0000-0000-0000-000000000002"));
    _ = _store.Delete("ws-a", "researcher");

    StoredLink b = Assert.Single(Listed("ws-b"));
    Assert.Equal("00000000-0000-0000-0000-000000000002", b.AgentAddress);
  }

  [Fact]
  public void Blank_Workspace_Or_Name_Is_Rejected()
  {
    _ = Assert.Throws<ArgumentException>(() => _store.List(" "));
    _ = Assert.Throws<ArgumentException>(() => _store.Upsert(" ", Link("n", "a")));
    _ = Assert.Throws<ArgumentException>(() => _store.Upsert("ws", Link(" ", "a")));
    _ = Assert.Throws<ArgumentException>(() => _store.Delete("ws", " "));
  }
}
