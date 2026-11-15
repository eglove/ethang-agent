using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;
using eThangAgent.TestSupport;
using eThangAgent.Transport.ACL;

namespace eThangAgent.Composition.Tests;

/// <summary>Reaching a conversation's host is IDEMPOTENT: a second attach against a host that
///     is already serving must leave exactly the one process and the one connection the first
///     attach established.
///     <para>WHY IT MATTERS, and what it costs if it is wrong: a reconnect is an attach, so a
///     reconnect that opened a second host would leave the first one running with nobody
///     reading it - a process per reconnect, each holding the session's SQLite file. The
///     count observed here is the PROCESS count, because that is the claim.</para>
///     <para>It also pins the other half: the connection the first attach opened is NOT
///     replaced. A re-attach that swapped the transport would tear down the pipe a live turn
///     is writing through, which is the failure mode this behaviour exists to forbid.</para>
///     </summary>
[Trait("Category", "Process")]
public class ReattachIdempotenceTests
{
  /// <summary>The count: two attaches, one host process.</summary>
  [Fact]
  public async Task TwoAttachesAgainstALiveHost_LeaveExactlyOneHostProcess()
  {
    CancellationToken ct = TestContext.Current.CancellationToken;
    string root = TestTempRoot.CreateDirectory("ethang-reattach").FullName;
    try
    {
      string workspace = Directory.CreateDirectory(Path.Combine(root, "ws")).FullName;
      AgentSettings settings = new AgentSettings(
          new OpenRouterSettings("sk-test", new Uri("https://openrouter.test")),
          new AgentRunOptions(null, 2)).WithWorkspaceRoot(workspace);
      string identity = Guid.NewGuid().ToString();
      RemoteHostSupervisor supervisor = new(identity,
          Path.Combine(root, RemoteHostSupervisor.ScratchFolderForConversation(identity)),
          settings, Path.Combine(root, "app.db"), _ => { });
      RemoteAgentRuntime runtime = new();
      try
      {
        _ = await supervisor.AttachAsync(runtime, ct).ConfigureAwait(true);
        int? first = supervisor.HostProcessId;
        NamedPipeChildTransport? connection = runtime.CurrentTransport;
        Assert.True(first is not null, "the first attach must have started a host.");
        Assert.True(connection is not null, "the first attach must have installed a connection.");

        // The second attach is the reconnect path's call: it must reuse both.
        _ = await supervisor.AttachAsync(runtime, ct).ConfigureAwait(true);

        Assert.Equal(first, supervisor.HostProcessId);
        Assert.Same(connection, runtime.CurrentTransport);
        Assert.True(runtime.IsConnected, "the connection the first attach opened must still be live.");
      }
      finally
      {
        await supervisor.DisposeAsync().ConfigureAwait(true);
      }
    }
    finally
    {
      TryDelete(root);
    }
  }

  /// <summary>A DEAD host is reconnected, and the reconnect is what makes the connection usable
  ///     again: a turn sent after the host was killed fails <c>HostUnavailable</c>, one reconnect
  ///     starts a replacement, and the next reconnect leaves that replacement alone.</summary>
  [Fact]
  public async Task KilledHost_IsReplacedByOneReconnect_AndTheReplacementIsThenReused()
  {
    CancellationToken ct = TestContext.Current.CancellationToken;
    string root = TestTempRoot.CreateDirectory("ethang-reattach-kill").FullName;
    try
    {
      string workspace = Directory.CreateDirectory(Path.Combine(root, "ws")).FullName;
      AgentSettings settings = new AgentSettings(
          new OpenRouterSettings("sk-test", new Uri("https://openrouter.test")),
          new AgentRunOptions(null, 2)).WithWorkspaceRoot(workspace);
      string identity = Guid.NewGuid().ToString();
      RemoteHostSupervisor supervisor = new(identity,
          Path.Combine(root, RemoteHostSupervisor.ScratchFolderForConversation(identity)),
          settings, Path.Combine(root, "app.db"), _ => { });
      RemoteAgentRuntime runtime = new();
      RemoteRootTurnChannel channel = new(runtime, () => supervisor);
      try
      {
        _ = await supervisor.AttachAsync(runtime, ct).ConfigureAwait(true);
        int? killed = supervisor.HostProcessId;
        Assert.True(killed is not null, "the first attach must have started a host.");

        // The host dies under the session: the app's next turn cannot reach it.
        supervisor.StopHost();
        await WaitUntilAsync(() => !runtime.IsConnected, ct).ConfigureAwait(true);

        Result<bool> first = await channel.ReconnectAsync(ct).ConfigureAwait(true);
        Assert.True(first.IsSuccess, first.Error?.Message);
        Assert.True(first.Value);
        int? replacement = supervisor.HostProcessId;
        Assert.True(replacement is not null, "the reconnect must have started a host.");
        Assert.NotEqual(killed, replacement);

        // The host is alive again, so a second reconnect reuses it: one process, not two.
        Result<bool> second = await channel.ReconnectAsync(ct).ConfigureAwait(true);
        Assert.True(second.IsSuccess, second.Error?.Message);
        Assert.Equal(replacement, supervisor.HostProcessId);
      }
      finally
      {
        await supervisor.DisposeAsync().ConfigureAwait(true);
      }
    }
    finally
    {
      TryDelete(root);
    }
  }

  /// <summary>Waits for a fact the host's death produces asynchronously. Bounded by the ceiling, so
  ///     a host that never reports its loss fails this test rather than hanging the run.</summary>
  private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
  {
    for (int attempt = 0; attempt < 200 && !condition(); attempt++)
    {
      await Task.Delay(25, ct).ConfigureAwait(false);
    }
  }

  /// <summary>The port's reconnect answers WITHOUT opening a second connection, which is what
  ///     makes it safe for the turn loop to ask on every host loss.</summary>
  [Fact]
  public async Task ChannelReconnectAgainstALiveHost_ReportsTrueAndKeepsTheConnection()
  {
    CancellationToken ct = TestContext.Current.CancellationToken;
    string root = TestTempRoot.CreateDirectory("ethang-reattach-port").FullName;
    try
    {
      string workspace = Directory.CreateDirectory(Path.Combine(root, "ws")).FullName;
      AgentSettings settings = new AgentSettings(
          new OpenRouterSettings("sk-test", new Uri("https://openrouter.test")),
          new AgentRunOptions(null, 2)).WithWorkspaceRoot(workspace);
      string identity = Guid.NewGuid().ToString();
      RemoteHostSupervisor supervisor = new(identity,
          Path.Combine(root, RemoteHostSupervisor.ScratchFolderForConversation(identity)),
          settings, Path.Combine(root, "app.db"), _ => { });
      RemoteAgentRuntime runtime = new();
      RemoteRootTurnChannel channel = new(runtime, () => supervisor);
      try
      {
        _ = await supervisor.AttachAsync(runtime, ct).ConfigureAwait(true);
        int? before = supervisor.HostProcessId;

        Result<bool> reconnected = await channel.ReconnectAsync(ct).ConfigureAwait(true);

        Assert.True(reconnected.IsSuccess, reconnected.Error?.Message);
        Assert.True(reconnected.Value);
        Assert.Equal(before, supervisor.HostProcessId);
      }
      finally
      {
        await supervisor.DisposeAsync().ConfigureAwait(true);
      }
    }
    finally
    {
      TryDelete(root);
    }
  }

  /// <summary>A channel with no supervisor has no reconnect path and says so, rather than
  ///     launching a host from nowhere.</summary>
  [Fact]
  public async Task ChannelWithoutASupervisor_FailsTheReconnectByName()
  {
    RemoteRootTurnChannel channel = new(new RemoteAgentRuntime(transport: null));

    Result<bool> reconnected = await channel.ReconnectAsync(TestContext.Current.CancellationToken)
        .ConfigureAwait(true);

    Assert.False(reconnected.IsSuccess);
    Assert.Equal("HostUnavailable", reconnected.Error.Code);
  }

  /// <summary>A cancelled reconnect asks for no work: a typed failure, and no host started.</summary>
  [Fact]
  public async Task CancelledReconnect_IsATypedFailureAndStartsNothing()
  {
    RemoteAgentRuntime runtime = new(transport: null);
    using CancellationTokenSource cancelled = new();
    await cancelled.CancelAsync().ConfigureAwait(true);
    RemoteRootTurnChannel channel = new(runtime);

    Result<bool> reconnected = await channel.ReconnectAsync(cancelled.Token).ConfigureAwait(true);

    Assert.False(reconnected.IsSuccess);
    Assert.False(runtime.IsConnected);
  }

  private static void TryDelete(string path)
  {
    // Named decision (CA1031): a temp-directory cleanup must never fail the test it follows -
    // a host still releasing its SQLite handle is the expected transient.
#pragma warning disable CA1031 // Do not catch general exception types
    try
    {
      Directory.Delete(path, recursive: true);
    }
    catch (Exception)
    {
      // Left behind deliberately: a passing test must not fail on cleanup.
    }
#pragma warning restore CA1031
  }
}
