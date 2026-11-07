using eThangAgent.TestSupport;
using eThangAgent.ToolDomain.FileClaims;

namespace eThangAgent.Storage.ACL.Tests;

// Issue #363/#365 defect 5: every conversation host in a workspace is its own process, so
// a claim table held in one process's memory protects nothing. The app database is the one
// store every host already shares, and this is the table in it.
public sealed class SqliteFileClaimStoreTests : IDisposable
{
  private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

  private readonly string _databasePath = Path.Combine(
      TestTempRoot.Root, "claims-" + Guid.NewGuid().ToString("N") + ".db");

  private SqliteFileClaimStore NewStore() => new(new AppDatabase(_databasePath));

  public void Dispose()
  {
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    File.Delete(_databasePath);
  }

  [Fact]
  public void Acquire_SecondSession_IsRefusedWithHolderAndExpiry()
  {
    SqliteFileClaimStore store = NewStore();
    FileClaimResult first = store.Acquire("ws", "C:\\ws\\a.cs", "session-1", Now);
    Assert.True(first.Granted);

    FileClaimResult rival = store.Acquire("ws", "C:\\ws\\a.cs", "session-2", Now);
    Assert.False(rival.Granted);
    Assert.Equal("session-1", rival.HolderSessionId);
    Assert.Equal(Now + FileClaimPolicy.ClaimTtl, rival.Expiry);
  }

  [Fact]
  public void Acquire_SameSessionAgain_IsGrantedAndRenews()
  {
    SqliteFileClaimStore store = NewStore();
    _ = store.Acquire("ws", "C:\\ws\\a.cs", "session-1", Now);
    FileClaimResult again = store.Acquire("ws", "C:\\ws\\a.cs", "session-1", Now.AddMinutes(5));
    Assert.True(again.Granted);
    Assert.Equal(Now.AddMinutes(5) + FileClaimPolicy.ClaimTtl, again.Expiry);
  }

  [Fact]
  public void Acquire_AfterExpiry_IsGrantedToTheNewSession()
  {
    SqliteFileClaimStore store = NewStore();
    _ = store.Acquire("ws", "C:\\ws\\a.cs", "session-1", Now);
    FileClaimResult later = store.Acquire("ws", "C:\\ws\\a.cs", "session-2", Now + FileClaimPolicy.ClaimTtl + TimeSpan.FromSeconds(1));
    Assert.True(later.Granted);
    Assert.Equal("session-2", later.HolderSessionId);
  }

  [Fact]
  public void Renew_AndRelease_FollowTheHoldersIdentity()
  {
    SqliteFileClaimStore store = NewStore();
    _ = store.Acquire("ws", "C:\\ws\\a.cs", "session-1", Now);
    Assert.False(store.Renew("ws", "C:\\ws\\a.cs", "session-2", Now).Granted);
    Assert.True(store.Renew("ws", "C:\\ws\\a.cs", "session-1", Now.AddMinutes(1)).Granted);
    Assert.False(store.Release("ws", "C:\\ws\\a.cs", "session-2", Now));
    Assert.True(store.Release("ws", "C:\\ws\\a.cs", "session-1", Now));
    Assert.Null(store.Peek("ws", "C:\\ws\\a.cs", Now));
  }

  [Fact]
  public void TwoStoreInstances_ShareOneTable()
  {
    _ = NewStore().Acquire("ws", "C:\\ws\\a.cs", "session-1", Now);
    FileClaimResult rival = NewStore().Acquire("ws", "C:\\ws\\a.cs", "session-2", Now);
    Assert.False(rival.Granted);
    Assert.Equal("session-1", rival.HolderSessionId);
  }

  [Fact]
  public void DifferentWorkspaces_DoNotCollide()
  {
    SqliteFileClaimStore store = NewStore();
    _ = store.Acquire("ws-a", "C:\\ws\\a.cs", "session-1", Now);
    Assert.True(store.Acquire("ws-b", "C:\\ws\\a.cs", "session-2", Now).Granted);
  }
}
