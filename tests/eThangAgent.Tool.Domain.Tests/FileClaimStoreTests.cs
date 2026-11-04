using eThangAgent.ToolDomain.FileClaims;

namespace eThangAgent.ToolDomain.Tests;

/// <summary>Contract tests for the file-claim store (issue #363): session-keyed
/// claims, 15-minute TTL expiry-on-read with no sweeper, renewal on mutating calls
/// only, typed refusal payload. The store is in-memory; the daemon and the
/// app-process code-less store share this contract.</summary>
public class FileClaimStoreTests
{
  private static readonly DateTimeOffset T0 = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

  [Fact]
  public void Ttl_IsFifteenMinutes() => Assert.Equal(TimeSpan.FromMinutes(15), FileClaimPolicy.ClaimTtl);

  [Fact]
  public void Acquire_FirstClaim_GrantedWithExpiry()
  {
    SharedFileClaimStore store = new();
    FileClaimResult r = store.Acquire("ws-1", "C:\\ws\\a.cs", "s-1", T0);
    Assert.True(r.Granted);
    Assert.Equal("s-1", r.HolderSessionId);
    Assert.Equal(T0 + FileClaimPolicy.ClaimTtl, r.Expiry);
  }

  [Fact]
  public void Acquire_ConcurrentSameFile_RefusedNamingHolderAndExpiry_FirstClaimIntact()
  {
    SharedFileClaimStore store = new();
    FileClaimResult first = store.Acquire("ws-1", "C:\\ws\\a.cs", "s-1", T0);
    Assert.True(first.Granted);
    FileClaimResult second = store.Acquire("ws-1", "C:\\ws\\a.cs", "s-2", T0 + TimeSpan.FromMinutes(1));
    Assert.False(second.Granted);
    Assert.Equal("s-1", second.HolderSessionId);
    Assert.Equal(first.Expiry, second.Expiry);
    Assert.Equal("FileClaimHeld", FileClaimErrorCodes.FileClaimHeld);
    FileClaim? peek = store.Peek("ws-1", "C:\\ws\\a.cs", T0 + TimeSpan.FromMinutes(1));
    Assert.NotNull(peek);
    Assert.Equal("s-1", peek.SessionId);
    Assert.Equal(first.Expiry, peek.Expiry);
  }

  [Fact]
  public void Acquire_DifferentFiles_BothGranted()
  {
    SharedFileClaimStore store = new();
    Assert.True(store.Acquire("ws-1", "C:\\ws\\a.cs", "s-1", T0).Granted);
    Assert.True(store.Acquire("ws-1", "C:\\ws\\b.cs", "s-2", T0).Granted);
  }

  [Fact]
  public void Acquire_DifferentWorkspaces_BothGranted()
  {
    SharedFileClaimStore store = new();
    Assert.True(store.Acquire("ws-1", "C:\\ws\\a.cs", "s-1", T0).Granted);
    Assert.True(store.Acquire("ws-2", "C:\\ws\\a.cs", "s-2", T0).Granted);
  }

  [Fact]
  public void Acquire_DeadHolder_ExpiresOnRead_LaterAcquisitionSucceeds()
  {
    SharedFileClaimStore store = new();
    Assert.True(store.Acquire("ws-1", "C:\\ws\\a.cs", "s-1", T0).Granted);
    DateTimeOffset after = T0 + FileClaimPolicy.ClaimTtl + TimeSpan.FromSeconds(1);
    Assert.Null(store.Peek("ws-1", "C:\\ws\\a.cs", after));
    FileClaimResult later = store.Acquire("ws-1", "C:\\ws\\a.cs", "s-2", after);
    Assert.True(later.Granted);
    Assert.Equal("s-2", later.HolderSessionId);
    Assert.Equal(after + FileClaimPolicy.ClaimTtl, later.Expiry);
  }

  [Fact]
  public void Renew_ByHolder_RefreshesExpiry()
  {
    SharedFileClaimStore store = new();
    FileClaimResult first = store.Acquire("ws-1", "C:\\ws\\a.cs", "s-1", T0);
    DateTimeOffset at = T0 + TimeSpan.FromMinutes(10);
    FileClaimResult renewed = store.Renew("ws-1", "C:\\ws\\a.cs", "s-1", at);
    Assert.True(renewed.Granted);
    Assert.Equal(at + FileClaimPolicy.ClaimTtl, renewed.Expiry);
    Assert.NotEqual(first.Expiry, renewed.Expiry);
    FileClaim? peek = store.Peek("ws-1", "C:\\ws\\a.cs", at);
    Assert.NotNull(peek);
    Assert.Equal(renewed.Expiry, peek.Expiry);
  }

  [Fact]
  public void Renew_ByNonHolder_RefusedNamingHolderAndExpiry()
  {
    SharedFileClaimStore store = new();
    FileClaimResult first = store.Acquire("ws-1", "C:\\ws\\a.cs", "s-1", T0);
    FileClaimResult r = store.Renew("ws-1", "C:\\ws\\a.cs", "s-2", T0 + TimeSpan.FromMinutes(5));
    Assert.False(r.Granted);
    Assert.Equal("s-1", r.HolderSessionId);
    Assert.Equal(first.Expiry, r.Expiry);
  }

  [Fact]
  public void Renew_AfterExpiry_Refused()
  {
    SharedFileClaimStore store = new();
    Assert.True(store.Acquire("ws-1", "C:\\ws\\a.cs", "s-1", T0).Granted);
    DateTimeOffset after = T0 + FileClaimPolicy.ClaimTtl + TimeSpan.FromSeconds(1);
    FileClaimResult r = store.Renew("ws-1", "C:\\ws\\a.cs", "s-1", after);
    Assert.False(r.Granted);
    Assert.Null(r.HolderSessionId);
  }

  [Fact]
  public void Release_ByHolder_FreesForLaterAcquisition()
  {
    SharedFileClaimStore store = new();
    Assert.True(store.Acquire("ws-1", "C:\\ws\\a.cs", "s-1", T0).Granted);
    Assert.True(store.Release("ws-1", "C:\\ws\\a.cs", "s-1", T0 + TimeSpan.FromMinutes(1)));
    Assert.Null(store.Peek("ws-1", "C:\\ws\\a.cs", T0 + TimeSpan.FromMinutes(1)));
    Assert.True(store.Acquire("ws-1", "C:\\ws\\a.cs", "s-2", T0 + TimeSpan.FromMinutes(1)).Granted);
  }

  [Fact]
  public void Release_ByNonHolder_DoesNotFree()
  {
    SharedFileClaimStore store = new();
    Assert.True(store.Acquire("ws-1", "C:\\ws\\a.cs", "s-1", T0).Granted);
    Assert.False(store.Release("ws-1", "C:\\ws\\a.cs", "s-2", T0));
    FileClaim? peek = store.Peek("ws-1", "C:\\ws\\a.cs", T0);
    Assert.NotNull(peek);
    Assert.Equal("s-1", peek.SessionId);
  }
}
