using eThangAgent.Composition;
using eThangAgent.FileSystem.ACL;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using eThangAgent.ToolDomain;

namespace eThangAgent.Desktop.Tests;

/// <summary>Issue #285: the gate declaration end to end through the DESKTOP's own store - the
///     path the Open Workspace dialog uses, over the app-owned database, for a workspace no
///     session is open on yet.
///     <para>THE THREE STATES ARE THE POINT (D1): declare, decline, and never-asked must stay
///     distinguishable, because 'the user said no' and 'nobody asked' are different facts and a
///     recorded decision that looked like an omission would be unauditable.</para>
///     <para>AND THE WRITE DIRECTION IS EXCLUSIVE: declaring clears a stale decline and declining
///     clears a stale declaration, so the gate's answer never depends on read order.</para>
///     </summary>
public sealed class WorkspaceGateStoreTests : IDisposable
{
  private static readonly DateTimeOffset T0 = new(2026, 10, 31, 9, 0, 0, TimeSpan.Zero);
  private readonly string _root = Path.Combine(Path.GetTempPath(), $"ethang-gate-{Guid.NewGuid():N}");
  private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ethang-gate-db-{Guid.NewGuid():N}.db");

  public WorkspaceGateStoreTests() => Directory.CreateDirectory(_root);

  public void Dispose()
  {
    GC.SuppressFinalize(this);
    // Named decision (CA1031): temp cleanup is best effort.
#pragma warning disable CA1031, S108 // Do not catch general exception types
    try
    {
      Directory.Delete(_root, recursive: true);
      File.Delete(_dbPath);
    }
    catch { }
#pragma warning restore CA1031, S108
  }

  /// <summary>The store over a real database and the real identity resolver, which is exactly
  ///     how <c>DesktopHost</c> builds it.</summary>
  private WorkspaceGateStore Store()
  {
    AppDatabase database = new(_dbPath);
    return new WorkspaceGateStore(database,
        new WorkspaceIdentityResolver(new SqliteWorkspaceIdentityStore(database)));
  }

  private static T Must<T>(Result<T> result)
  {
    Assert.True(result.IsSuccess, result.Error?.Message ?? "expected success");
    return result.Value;
  }

  /// <summary>Reads a key the way the GATE reads it: through the state service over the
  ///     workspace's resolved identity, so the test proves the write landed where the session
  ///     that opens next will look.</summary>
  private async Task<string?> ReadKeyAsync(string key)
  {
    AppDatabase database = new(_dbPath);
    WorkspaceIdentityResolver identities =
        new(new SqliteWorkspaceIdentityStore(database));
    WorkspaceIdentityResolution id = Must(await identities.ResolveAsync(_root,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
    SqliteStateStore store = new(database);
    return (await store.GetKeyAsync(id.Id, "goal", key.Replace("goal/", "", StringComparison.Ordinal),
        TestContext.Current.CancellationToken).ConfigureAwait(true))?.Value;
  }

  [Fact]
  public async Task ReadAsync_OfAWorkspaceNobodyAsked_IsNeverAsked()
  {
    WorkspaceGateState state = await Store().ReadAsync(_root,
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.Equal(GateStateKind.NeverAsked, state.Kind);
    Assert.True(state.NeedsAnswer);
    Assert.Empty(state.Commands);
  }

  [Fact]
  public async Task Declare_StoresTheCommandsVerbatim_AndIsReadBack()
  {
    WorkspaceGateStore store = Store();
    _ = Must(await store.ApplyAsync(_root,
        GateDeclaration.Declare(["dotnet build", "  dotnet test  "]), T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));

    WorkspaceGateState state = await store.ReadAsync(_root,
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.Equal(GateStateKind.Declared, state.Kind);
    // Surrounding whitespace is trimmed; the order the user wrote is preserved.
    Assert.Equal(["dotnet build", "dotnet test"], state.Commands);
    Assert.False(state.NeedsAnswer);
  }

  [Fact]
  public async Task Decline_IsRecorded_AndIsNotReadAsNeverAsked()
  {
    // D1's whole point: a recorded decision must not look like an omission.
    WorkspaceGateStore store = Store();
    _ = Must(await store.ApplyAsync(_root, GateDeclaration.Decline("this repo has no fast check"),
        T0, TestContext.Current.CancellationToken).ConfigureAwait(true));

    WorkspaceGateState state = await store.ReadAsync(_root,
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.Equal(GateStateKind.Declined, state.Kind);
    Assert.False(state.NeedsAnswer);
  }

  [Fact]
  public async Task Unchanged_WritesNothing_SoTheWorkspaceStaysAsItWas()
  {
    // The default must be inert: a host that has not been taught about gates cannot decline
    // one on the user's behalf.
    WorkspaceGateStore store = Store();
    _ = Must(await store.ApplyAsync(_root, GateDeclaration.Unchanged, T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));

    Assert.Null(await ReadKeyAsync(CompletionGate.GoalCheckKey).ConfigureAwait(true));
    Assert.Null(await ReadKeyAsync(CompletionGate.DeclinedKey).ConfigureAwait(true));
  }

  [Fact]
  public async Task Declaring_ClearsAnEarlierDecline()
  {
    // A user who declined last month and now writes commands must not have the stale decline
    // suppressing the gate they just configured.
    WorkspaceGateStore store = Store();
    _ = Must(await store.ApplyAsync(_root, GateDeclaration.Decline(), T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
    _ = Must(await store.ApplyAsync(_root, GateDeclaration.Declare(["go build ./..."]), T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));

    Assert.Null(await ReadKeyAsync(CompletionGate.DeclinedKey).ConfigureAwait(true));
    Assert.NotNull(await ReadKeyAsync(CompletionGate.GoalCheckKey).ConfigureAwait(true));
    WorkspaceGateState state = await store.ReadAsync(_root,
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.Equal(GateStateKind.Declared, state.Kind);
  }

  [Fact]
  public async Task Declining_ClearsAnEarlierDeclaration()
  {
    // The other direction: leaving both keys present would make the gate's answer depend on
    // read order, which is a bug nobody could see.
    WorkspaceGateStore store = Store();
    _ = Must(await store.ApplyAsync(_root, GateDeclaration.Declare(["npm test"]), T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
    _ = Must(await store.ApplyAsync(_root, GateDeclaration.Decline(), T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));

    Assert.Null(await ReadKeyAsync(CompletionGate.GoalCheckKey).ConfigureAwait(true));
    Assert.NotNull(await ReadKeyAsync(CompletionGate.DeclinedKey).ConfigureAwait(true));
  }

  [Fact]
  public async Task ApplyAsync_WithoutAWorkspace_IsANamedFailure_NotAnException()
  {
    Result<bool> refused = await Store().ApplyAsync("  ", GateDeclaration.Decline(), T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(refused.IsSuccess);
    Assert.Equal("InvalidWorkspace", refused.Error.Code);
  }

  [Fact]
  public async Task ReadAsync_OfAMissingDirectory_AsksRatherThanAssumingADecision()
  {
    // An unreadable workspace must still open the dialog; offering the question again is
    // recoverable, silently assuming a decision the user never made is not.
    WorkspaceGateState state = await Store().ReadAsync(
        Path.Combine(_root, "not-there"), TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.Equal(GateStateKind.NeverAsked, state.Kind);
  }

  [Fact]
  public async Task Declare_RefusesAnEmptyCommandList()
  {
    // A declaration with no commands is not a gate; it is a decline wearing a gate's name.
    _ = Assert.Throws<ArgumentException>(() => GateDeclaration.Declare(["   ", ""]));
    await Task.CompletedTask.ConfigureAwait(true);
  }

  [Fact]
  public async Task TheStoredGateIsWhatTheCompletionGateReads_SoTheNextClaimIsGated()
  {
    // The acceptance criterion 'a declared gate is immediately live': the key this store writes
    // is the key CompletionGate names, so nothing else has to be taught about it.
    WorkspaceGateStore store = Store();
    _ = Must(await store.ApplyAsync(_root, GateDeclaration.Declare(["dotnet test"]), T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));

    string? stored = await ReadKeyAsync(CompletionGate.GoalCheckKey).ConfigureAwait(true);
    Assert.NotNull(stored);
    Assert.Contains("dotnet test", stored, StringComparison.Ordinal);
  }
}
