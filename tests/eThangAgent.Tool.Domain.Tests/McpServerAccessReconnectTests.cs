using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain.Mcp;

namespace eThangAgent.ToolDomain.Tests;

/// <summary>Reconnect tests (issue #105, B4): a pooled session whose server died
///     mid-session is disposed and reconnected on the next dispatch - a structured
///     reconnect, never a hang or a stale-session error loop. The dead session is a
///     scripted fake that reports itself dead after its first call.</summary>
public class McpServerAccessReconnectTests
{
  // Named decision (CA2000): every test disposes its access explicitly at the end
  // (DisposeAsync under ConfigureAwait(true)); the analyzer cannot see the transfer.
#pragma warning disable CA2000 // Use a using statement or using declaration
  private static McpServerConfig ApprovedStdio(string name = "demo", int id = 1) => new(
      id, name, McpTransport.Stdio, "npx demo", "[]", "{}", "{}", null,
      McpApprovalState.Approved, null, DateTimeOffset.UtcNow);

  /// <summary>A session that dies after the first call: ListTools succeeds (the
  ///     connect handshake), the first call succeeds, then HasExited flips true and
  ///     every later call throws - the stub server's crash shape.</summary>
  private sealed class DyingSession : IMcpClientSession
  {
    public int ListCalls { get; private set; }
    public int CallCount { get; private set; }
    public bool Dead => CallCount >= 1;
    public int DisposeCalls { get; private set; }

    public bool HasExited => Dead;

    public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct = default)
    {
      ListCalls++;
      return Task.FromResult<IReadOnlyList<McpToolInfo>>([new McpToolInfo("echo", "Echoes text")]);
    }

    public Task<McpToolCallResult> CallToolAsync(string tool, string argumentsJson, CancellationToken ct = default)
    {
      if (Dead)
      {
        throw new IOException("the server process is gone");
      }

      CallCount++;
      return Task.FromResult(new McpToolCallResult("echo: hi", false));
    }

    public ValueTask DisposeAsync()
    {
      DisposeCalls++;
      return ValueTask.CompletedTask;
    }
  }

  private sealed class DyingPool : IMcpClientSessionPool
  {
    public List<DyingSession> Created { get; } = [];

    public Task<McpConnectResult> ConnectAsync(McpServerConfig server, CancellationToken ct = default)
    {
      DyingSession session = new();
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
  public async Task Dead_Session_Is_Reconnected_On_The_Next_Dispatch()
  {
    DyingPool pool = new();
    McpServerAccess access = new(new FakeStore(ApprovedStdio()), pool, "ws-test");
    System.Text.Json.JsonElement args = ParseArgs("{}");

    // First dispatch: connects, lists, calls - the server dies after the call.
    McpOutcome first = await access.ExecuteAsync(new McpCommand.CallTool("demo", "echo", args),
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    McpOutcome.Called called = Assert.IsType<McpOutcome.Called>(first);
    Assert.False(called.IsError);

    // Second dispatch: the dead session is disposed and a FRESH session reconnects -
    // a structured reconnect, never a hang or a stale error loop.
    McpOutcome second = await access.ExecuteAsync(new McpCommand.CallTool("demo", "echo", args),
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    McpOutcome.Called again = Assert.IsType<McpOutcome.Called>(second);
    Assert.False(again.IsError);
    Assert.Equal("echo: hi", again.Content);

    Assert.Equal(2, pool.Created.Count);
    // The dead session's dispose is detached (best-effort, never blocks the
    // reconnect); give it a bounded window to land, then assert it happened.
    for (int i = 0; pool.Created[0].DisposeCalls == 0 && i < 100; i++)
    {
      await Task.Delay(10, TestContext.Current.CancellationToken).ConfigureAwait(true);
    }

    Assert.Equal(1, pool.Created[0].DisposeCalls);
    await access.DisposeAsync().ConfigureAwait(true);
  }

  [Fact]
  public async Task Dead_Session_Leaves_The_Listing_Honest()
  {
    DyingPool pool = new();
    McpServerAccess access = new(new FakeStore(ApprovedStdio()), pool, "ws-test");
    System.Text.Json.JsonElement args = ParseArgs("{}");
    _ = await access.ExecuteAsync(new McpCommand.CallTool("demo", "echo", args),
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    // After the death is observed (the next dispatch reconnects), the listing
    // reports the LIVE state, not a stale Connected.
    McpOutcome listing = await access.ExecuteAsync(new McpCommand.ListServers(),
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    McpOutcome.Status status = Assert.IsType<McpOutcome.Status>(listing);
    Assert.Equal(McpConnectionState.Connected, status.Servers[0].State);
    await access.DisposeAsync().ConfigureAwait(true);
  }
}
#pragma warning restore CA2000 // Use a using statement or using declaration
