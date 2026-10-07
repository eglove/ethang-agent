using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain.Mcp;

namespace eThangAgent.ToolDomain.Tests;

/// <summary>Status-view tests (issue #106): the pool's status entries carry the
///     captured stderr tail so the Desktop status view can show WHY a server failed -
///     the connected session's tail, nothing before a connect, and the dead session's
///     tail preserved across a FAILED reconnect (the crash's diagnosis outlives the
///     corpse).</summary>
public class McpServerStatusStderrTests
{
  private static McpServerConfig ApprovedStdio(string name = "demo", int id = 1) => new(
      id, name, McpTransport.Stdio, "npx demo", "[]", "{}", "{}", null,
      McpApprovalState.Approved, null, DateTimeOffset.UtcNow);

  /// <summary>A session with a captured stderr tail (the ContainedStdioSession shape).</summary>
  private abstract class TailedSession(string stderr) : IMcpClientSession
  {
    protected readonly string _stderrText = stderr;

    public string StderrTail => _stderrText;

    // Abstract (not the DIM): the pool's dead-session check reads the INTERFACE
    // member; a subclass property that only shadows the default never maps.
    public abstract bool HasExited { get; }
    public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<McpToolInfo>>([new McpToolInfo("echo", null)]);

    public virtual Task<McpToolCallResult> CallToolAsync(string tool, string argumentsJson, CancellationToken ct = default) =>
        Task.FromResult(new McpToolCallResult("ok", false));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
  }

  /// <summary>A live session (never dies) with a captured tail.</summary>
  private sealed class LiveSession(string stderr) : TailedSession(stderr)
  {
    public override bool HasExited => false;
  }

  /// <summary>A session that dies after its first call (the stub server's crash shape).</summary>
  private sealed class DyingSession(string stderr) : TailedSession(stderr)
  {
    private bool Called { get; set; }
    public override bool HasExited => Called;
    public string Tail => _stderrText;

    public override Task<McpToolCallResult> CallToolAsync(string tool, string argumentsJson, CancellationToken ct = default)
    {
      Called = true;
      return base.CallToolAsync(tool, argumentsJson, ct);
    }
  }

  private sealed class TailedPool : IMcpClientSessionPool
  {
    public List<TailedSession> Created { get; } = [];

    /// <summary>When true: the first connect yields a session that dies after its
    ///     first call, and every connect AFTER the first fails (the reconnect-failure
    ///     shape).</summary>
    public bool FailAfterFirst { get; set; }

    public Task<McpConnectResult> ConnectAsync(McpServerConfig server, CancellationToken ct = default)
    {
      if (FailAfterFirst)
      {
        if (Created.Count == 0)
        {
          TailedSession dying = new DyingSession($"stderr for {server.Name} #0");
          Created.Add(dying);
          return Task.FromResult<McpConnectResult>(new McpConnectResult.Success(dying));
        }

        return Task.FromResult<McpConnectResult>(
            new McpConnectResult.Failure("McpConnectFailed", "reconnect refused"));
      }

      TailedSession session = new LiveSession($"stderr for {server.Name} #{Created.Count}");
      Created.Add(session);
      return Task.FromResult<McpConnectResult>(new McpConnectResult.Success(session));
    }
  }

  private sealed class FakeStore(params McpServerConfig[] servers) : IMcpServerStore
  {
    public Task<Result<IReadOnlyList<McpServerConfig>>> ListAsync(string workspaceId, CancellationToken ct = default) =>
        Task.FromResult(Result.Success<IReadOnlyList<McpServerConfig>>(servers));

    public Task<Result<McpServerConfig>> GetAsync(int id, string workspaceId, CancellationToken ct = default) =>
        Task.FromResult(Result.Failure<McpServerConfig>(new DomainError("McpServerNotFound", "no")));

    public Task<Result<McpServerConfig>> AddAsync(McpServerConfig server, CancellationToken ct = default) =>
        Task.FromResult(Result.Success(server));

    public Task<Result<McpServerConfig>> UpdateAsync(McpServerConfig server, CancellationToken ct = default) =>
        Task.FromResult(Result.Success(server));

    public Task<Result<bool>> DeleteAsync(int id, string workspaceId, CancellationToken ct = default) =>
        Task.FromResult(Result.Success(true));

    public Task<Result<McpOAuthTokens>> SaveTokensAsync(int serverId, McpOAuthTokens tokens, CancellationToken ct = default) =>
        Task.FromResult(Result.Success(tokens));

    public Task<Result<bool>> AppendDecisionAsync(int serverId, string decision, string? detail, CancellationToken ct = default) =>
        Task.FromResult(Result.Success(true));

    public Task<Result<IReadOnlyList<McpDecision>>> ListDecisionsAsync(int serverId, int take, CancellationToken ct = default) =>
        Task.FromResult(Result.Success<IReadOnlyList<McpDecision>>([]));

    public Task<Result<McpOAuthTokens?>> GetTokensAsync(int serverId, CancellationToken ct = default) =>
        Task.FromResult(Result.Success<McpOAuthTokens?>(null));
  }

  private static System.Text.Json.JsonElement ParseArgs(string json)
  {
    using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json);
    return doc.RootElement.Clone();
  }

  [Fact]
  public async Task Connected_Server_Status_Carries_The_Session_Stderr_Tail()
  {
    TailedPool pool = new();
    McpServerAccess access = new(new FakeStore(ApprovedStdio()), pool, "ws-test");
    await using (access.ConfigureAwait(true))
    {
      _ = await access.ExecuteAsync(new McpCommand.CallTool("demo", "echo", ParseArgs("{}")),
        TestContext.Current.CancellationToken).ConfigureAwait(true);

      McpOutcome listing = await access.ExecuteAsync(new McpCommand.ListServers(),
          TestContext.Current.CancellationToken).ConfigureAwait(true);
      McpOutcome.Status status = Assert.IsType<McpOutcome.Status>(listing);
      McpServerStatus server = Assert.Single(status.Servers);
      Assert.Equal(McpConnectionState.Connected, server.State);
      Assert.Equal("stderr for demo #0", server.Stderr);
    }
  }

  [Fact]
  public async Task Not_Connected_Server_Status_Carries_No_Stderr()
  {
    TailedPool pool = new();
    McpServerAccess access = new(new FakeStore(ApprovedStdio()), pool, "ws-test");
    await using (access.ConfigureAwait(true))
    {
      McpOutcome listing = await access.ExecuteAsync(new McpCommand.ListServers(),
          TestContext.Current.CancellationToken).ConfigureAwait(true);
      McpOutcome.Status status = Assert.IsType<McpOutcome.Status>(listing);
      McpServerStatus server = Assert.Single(status.Servers);
      Assert.Equal(McpConnectionState.NotConnected, server.State);
      Assert.Null(server.Stderr);
    }
  }

  [Fact]
  public async Task Dead_Session_Stderr_Survives_A_Failed_Reconnect()
  {
    // The crash's diagnosis must outlive the corpse: the dead session is disposed,
    // the reconnect itself fails, and the status entry still shows the DEAD
    // session's stderr tail - never a blank slate over a crash.
    TailedPool pool = new() { FailAfterFirst = true };
    McpServerAccess access = new(new FakeStore(ApprovedStdio()), pool, "ws-test");
    await using (access.ConfigureAwait(true))
    {
      System.Text.Json.JsonElement args = ParseArgs("{}");

      // First dispatch: session #0 connects (tail captured), the call kills it.
      McpOutcome first = await access.ExecuteAsync(new McpCommand.CallTool("demo", "echo", args),
          TestContext.Current.CancellationToken).ConfigureAwait(true);
      McpOutcome.Called called = Assert.IsType<McpOutcome.Called>(first);
      Assert.False(called.IsError);
      DyingSession dead = Assert.IsType<DyingSession>(Assert.Single(pool.Created));
      Assert.True(dead.HasExited);
      Assert.Equal("stderr for demo #0", dead.Tail);

      // Second dispatch: the dead session is detected, disposed, and the reconnect
      // FAILS - the entry's stderr must still carry session #0's tail.
      McpOutcome second = await access.ExecuteAsync(new McpCommand.CallTool("demo", "echo", args),
          TestContext.Current.CancellationToken).ConfigureAwait(true);
      _ = Assert.IsType<McpOutcome.Failure>(second);

      McpOutcome listing = await access.ExecuteAsync(new McpCommand.ListServers(),
          TestContext.Current.CancellationToken).ConfigureAwait(true);
      McpOutcome.Status status = Assert.IsType<McpOutcome.Status>(listing);
      McpServerStatus server = Assert.Single(status.Servers);
      Assert.Equal(McpConnectionState.Failed, server.State);
      Assert.Equal("stderr for demo #0", server.Stderr);
    }
  }
}
