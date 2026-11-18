using eThangAgent.TestSupport;
using eThangAgent.ToolDomain;
using eThangAgent.Windows.ACL;

namespace eThangAgent.Desktop.Tests;

/// <summary>What a launch does before any window exists: a launch that names a workspace
///     another process already owns signals that process and exits with the handoff code,
///     and every other launch starts the app.
///     <para>THE DECISION IS SEPARATE FROM THE WINDOW so it can be observed without opening
///     one: <see cref="LaunchHandoff.Resolve"/> answers what the launch should do, and
///     <c>Program.Main</c> acts on the answer.</para></summary>
public sealed class LaunchHandoffTests
{
  /// <summary>An owned workspace is handed off: the owning process is signalled with the root
  ///     it must raise, and the launch reports the handoff code so no window is created.
  ///     </summary>
  [Fact]
  public void Resolve_ForAnOwnedWorkspace_HandsOff_AndSignalsTheOwner()
  {
    CancellationToken ct = TestContext.Current.CancellationToken;
    string root = ExistingRoot();
    using ManualResetEventSlim received = new();
    string? signalled = null;
    using SingleInstanceGate? owner = WorkspaceInstanceHandoff.Acquire(root, payload =>
    {
      signalled = payload;
      received.Set();
    });
    Assert.NotNull(owner);

    LaunchHandoffResult result = LaunchHandoff.Resolve(root);

    Assert.True(result.IsHandedOff);
    Assert.Equal(LaunchHandoff.HandedOffExitCode, result.ExitCode);
    Assert.Equal(root, result.WorkspaceRoot);
    Assert.NotNull(result.Message);
    Assert.Contains(root, result.Message, StringComparison.Ordinal);
    Assert.True(received.Wait(TestCeiling.Value, ct),
        "the process that owns the workspace never received the signal");
    Assert.Equal(root, signalled);
  }

  /// <summary>A workspace no process owns is not a handoff: the launch starts the app, which
  ///     stops at the launcher.</summary>
  [Fact]
  public void Resolve_ForAnUnownedWorkspace_StartsTheApp()
  {
    string root = ExistingRoot();
    Assert.False(WorkspaceInstanceHandoff.TrySignalExisting(root),
        "nothing owns a freshly created directory, so this launch cannot be a handoff");

    LaunchHandoffResult result = LaunchHandoff.Resolve(root);

    Assert.False(result.IsHandedOff);
    Assert.Equal(0, result.ExitCode);
    Assert.Null(result.Message);
  }

  /// <summary>A launch with no workspace argument is the ordinary start: the launcher, and no
  ///     handoff attempt at all.</summary>
  [Fact]
  public void Resolve_WithNoArgument_StartsTheApp()
  {
    LaunchHandoffResult result = LaunchHandoff.Resolve(null);

    Assert.False(result.IsHandedOff);
    Assert.Equal(0, result.ExitCode);
    Assert.Null(result.WorkspaceRoot);
  }

  /// <summary>An argument that is not an existing directory is not a workspace: the launch
  ///     starts the app rather than signalling a name minted from a path that is not there.
  ///     </summary>
  [Fact]
  public void Resolve_ForADirectoryThatIsNotThere_StartsTheApp()
  {
    string missing = Path.Combine(TestTempRoot.Root, "ethang-launch-absent-" + Guid.NewGuid().ToString("N"));
    Assert.False(Directory.Exists(missing));

    LaunchHandoffResult result = LaunchHandoff.Resolve(missing);

    Assert.False(result.IsHandedOff);
    Assert.Equal(0, result.ExitCode);
  }

  /// <summary>A second spelling of one directory is the same workspace, so it contends with
  ///     the process that opened it rather than starting a second window on the same folder.
  ///     </summary>
  [Fact]
  public void Resolve_TreatsAnotherSpellingOfOneDirectoryAsTheSameWorkspace()
  {
    string root = ExistingRoot();
    using SingleInstanceGate? owner = WorkspaceInstanceHandoff.Acquire(root, _ => { });
    Assert.NotNull(owner);

    LaunchHandoffResult result = LaunchHandoff.Resolve(root + Path.DirectorySeparatorChar);

    Assert.True(result.IsHandedOff);
    Assert.Equal(root, result.WorkspaceRoot);
  }

  /// <summary>The exit code the launch returns when it handed the workspace to the process that
  ///     already had it open. Distinct from 0 (a normal run) and 1 (a failed startup).</summary>
  [Fact]
  public void HandedOffExitCode_IsDistinctFromANormalRun()
      => Assert.NotEqual(0, LaunchHandoff.HandedOffExitCode);

  /// <summary>A directory that exists, so the launch's existence check passes.</summary>
  private static string ExistingRoot()
      => WorkspaceIdentity.Normalize(TestTempRoot.CreateDirectory("ethang-launch").FullName);
}
