using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain.Mcp;

namespace eThangAgent.ToolDomain.Tests;

/// <summary>Policy tests for the MCP session pool (issue #104): lazy connect (B2),
///     cached discovery, the approval gate (B5: pending never connects), failed
///     connects recorded as status entries (B3), and the unknown-server/tool error
///     contract (B4). The SDK session is a scripted fake over the seam.</summary>
public class McpServerAccessTests
{
  // Named decision (CA2000): every session here is an in-memory fake whose dispose
  // is a no-op; the pool's sessions are the tests' observation points and outlive
  // single commands. No test holds a real transport.
#pragma warning disable CA2000 // Use a using statement or using declaration
  private const string ToolsJson = "[{" + "\"name\":\"echo\"," + "\"description\":\"Echoes text\"}," +
      "{\"name\":\"ping\",\"description\":null}]";

  private static McpServerConfig ApprovedStdio(string name = "demo", int id = 1) => new(
      id, name, McpTransport.Stdio, "npx demo", "[]", "{}", "{}", null,
      McpApprovalState.Approved, null, DateTimeOffset.UtcNow);

  private static McpServerConfig Pending(string name = "pend", int id = 2) => new(
      id, name, McpTransport.Http, "https://x", "[]", "{}", "{}", null,
      McpApprovalState.Pending, null, DateTimeOffset.UtcNow);

  private sealed class FakeSession : IMcpClientSession
  {
    public int ListCalls { get; private set; }
    public List<(string Tool, string Json)> Calls { get; } = [];
    public string ToolsPayload { get; set; } = ToolsJson;
    public string CallReply { get; set; } = "hi";
    public bool CallIsError { get; set; }

    public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct = default)
    {
      ListCalls++;
      return Task.FromResult<IReadOnlyList<McpToolInfo>>(ParseTools(ToolsPayload));
    }

    public Task<McpToolCallResult> CallToolAsync(string tool, string argumentsJson, CancellationToken ct = default)
    {
      Calls.Add((tool, argumentsJson));
      return Task.FromResult(new McpToolCallResult(CallReply, CallIsError));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static List<McpToolInfo> ParseTools(string json)
    {
      List<McpToolInfo> tools = [];
      using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json);
      foreach (System.Text.Json.JsonElement el in doc.RootElement.EnumerateArray())
      {
        tools.Add(new McpToolInfo(el.GetProperty("name").GetString()!,
            el.TryGetProperty("description", out System.Text.Json.JsonElement descEl) && descEl.ValueKind == System.Text.Json.JsonValueKind.String
                ? descEl.GetString() : null));
      }

      return tools;
    }
  }

  private sealed class FakeSessionPool : IMcpClientSessionPool
  {
    public Func<McpServerConfig, IMcpClientSession> Factory { get; set; } = _ => new FakeSession();
    public bool FailAll { get; set; }
    public List<McpServerConfig> Connects { get; } = [];
    public Dictionary<string, FakeSession> Sessions { get; } = [];
    public Dictionary<string, int> ConnectCounts { get; } = [];

    public Task<McpConnectResult> ConnectAsync(McpServerConfig server, CancellationToken ct = default)
    {
      Connects.Add(server);
      ConnectCounts[server.Name] = ConnectCounts.GetValueOrDefault(server.Name) + 1;
      if (Factory is null)
      {
        return Task.FromResult<McpConnectResult>(new McpConnectResult.Failure("McpConnectFailed", "spawn failed"));
      }

      if (FailAll)
      {
        return Task.FromResult<McpConnectResult>(new McpConnectResult.Failure("McpConnectFailed", "spawn failed"));
      }

      IMcpClientSession configured = Factory(server);
      if (configured is FakeSession known)
      {
        known.ToolsPayload = ToolsJson;
        Sessions[server.Name] = known;
      }

      return Task.FromResult<McpConnectResult>(new McpConnectResult.Success(configured));
    }
  }

  private static McpServerAccess Make(FakeSessionPool pool, params McpServerConfig[] servers)
  {
    FakeStore store = new(servers);
    return new McpServerAccess(store, pool, "ws-test");
  }

  private static Task<McpOutcome> Run(McpServerAccess access, McpCommand command) =>
      access.ExecuteAsync(command, TestContext.Current.CancellationToken);

  private class FakeStore(params McpServerConfig[] servers) : IMcpServerStore
  {
    public List<McpServerConfig> Servers { get; } = [.. servers];

    public Task<Result<IReadOnlyList<McpServerConfig>>> ListAsync(string workspaceId, CancellationToken ct = default) =>
        Task.FromResult(Result.Success<IReadOnlyList<McpServerConfig>>(Servers));

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

    public Task<Result<McpOAuthTokens?>> GetTokensAsync(int serverId, CancellationToken ct = default) =>
        Task.FromResult(Result.Success<McpOAuthTokens?>(null));

    public virtual Task<Result<bool>> AppendDecisionAsync(int serverId, string decision, string? detail, CancellationToken ct = default) =>
        Task.FromResult(Result.Success(true));

    public Task<Result<IReadOnlyList<McpDecision>>> ListDecisionsAsync(int serverId, int take, CancellationToken ct = default) =>
        Task.FromResult(Result.Success<IReadOnlyList<McpDecision>>([]));
  }

  // ---- B2: lazy connect, cached discovery ----

  [Fact]
  public async Task List_Never_Connects()
  {
    FakeSessionPool pool = new() { Factory = _ => new FakeSession() };
    McpServerAccess access = Make(pool, ApprovedStdio());
    McpOutcome result = await Run(access, new McpCommand.ListServers()).ConfigureAwait(true);
    McpOutcome.Status status = Assert.IsType<McpOutcome.Status>(result);
    _ = Assert.Single(status.Servers);
    Assert.Equal(McpConnectionState.NotConnected, status.Servers[0].State);
    Assert.Empty(pool.Connects);
  }

  [Fact]
  public async Task First_Call_Connects_Once_Second_Call_Reuses()
  {
    FakeSessionPool pool = new() { Factory = _ => new FakeSession() };
    McpServerAccess access = Make(pool, ApprovedStdio());
    _ = await Run(access, new McpCommand.CallTool("demo", "echo", ParseArgs("{}"))).ConfigureAwait(true);
    _ = await Run(access, new McpCommand.CallTool("demo", "echo", ParseArgs("{}"))).ConfigureAwait(true);
    Assert.Equal(1, pool.ConnectCounts.GetValueOrDefault("demo"));
    FakeSession session = pool.Sessions["demo"];
    Assert.Equal(2, session.Calls.Count);
  }

  [Fact]
  public async Task Call_Caches_Tool_List_Once()
  {
    FakeSessionPool pool = new() { Factory = _ => new FakeSession() };
    McpServerAccess access = Make(pool, ApprovedStdio());
    _ = await Run(access, new McpCommand.CallTool("demo", "echo", ParseArgs("{}"))).ConfigureAwait(true);
    _ = await Run(access, new McpCommand.ListServers()).ConfigureAwait(true);
    FakeSession session = pool.Sessions["demo"];
    Assert.Equal(1, session.ListCalls);
    McpOutcome result = await Run(access, new McpCommand.ListServers()).ConfigureAwait(true);
    McpOutcome.Status status = Assert.IsType<McpOutcome.Status>(result);
    Assert.Equal(McpConnectionState.Connected, status.Servers[0].State);
    Assert.Equal(2, status.Servers[0].Tools.Count);
    Assert.Equal("echo", status.Servers[0].Tools[0].Name);
  }

  [Fact]
  public async Task Call_Passes_Arguments_Json()
  {
    FakeSessionPool pool = new() { Factory = _ => new FakeSession() };
    McpServerAccess access = Make(pool, ApprovedStdio());
    _ = await Run(access, new McpCommand.CallTool("demo", "echo", ParseArgs("{" + "\"text\":\"hi\"}"))).ConfigureAwait(true);
    FakeSession session = pool.Sessions["demo"];
    (string Tool, string Json) call = Assert.Single(session.Calls);
    Assert.Equal("echo", call.Tool);
    Assert.Contains("\"text\"", call.Json, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Call_Result_Passes_Through()
  {
    FakeSessionPool pool = new() { Factory = _ => new FakeSession() { CallReply = "answer", CallIsError = true } };
    McpServerAccess access = Make(pool, ApprovedStdio());
    McpOutcome result = await Run(access, new McpCommand.CallTool("demo", "echo", ParseArgs("{}"))).ConfigureAwait(true);
    McpOutcome.Called called = Assert.IsType<McpOutcome.Called>(result);
    Assert.Equal("answer", called.Content);
    Assert.True(called.IsError);
  }

  // ---- B5: approval gate ----

  [Fact]
  public async Task Pending_Server_Never_Connects()
  {
    FakeSessionPool pool = new() { Factory = _ => new FakeSession() };
    McpServerAccess access = Make(pool, ApprovedStdio(), Pending());
    McpOutcome result = await Run(access, new McpCommand.CallTool("pend", "echo", ParseArgs("{}"))).ConfigureAwait(true);
    McpOutcome.Failure failure = Assert.IsType<McpOutcome.Failure>(result);
    Assert.Equal("McpServerNotApproved", failure.Code);
    Assert.Empty(pool.Connects);
  }

  [Fact]
  public async Task Revoked_Server_Never_Connects()
  {
    FakeSessionPool pool = new() { Factory = _ => new FakeSession() };
    McpServerAccess access = Make(pool, new McpServerConfig(3, "rev", McpTransport.Stdio, "x", "[]", "{}", "{}",
        null, McpApprovalState.Revoked, null, DateTimeOffset.UtcNow));
    McpOutcome result = await Run(access, new McpCommand.CallTool("rev", "echo", ParseArgs("{}"))).ConfigureAwait(true);
    McpOutcome.Failure failure = Assert.IsType<McpOutcome.Failure>(result);
    Assert.Equal("McpServerNotApproved", failure.Code);
    Assert.Empty(pool.Connects);
  }

  // ---- B3: failed connect ----

  [Fact]
  public async Task Failed_Connect_Returns_Structured_Error_And_Status()
  {
    FakeSessionPool pool = new() { FailAll = true };
    McpServerAccess access = Make(pool, ApprovedStdio());
    McpOutcome result = await Run(access, new McpCommand.CallTool("demo", "echo", ParseArgs("{}"))).ConfigureAwait(true);
    McpOutcome.Failure failure = Assert.IsType<McpOutcome.Failure>(result);
    Assert.Equal("McpConnectFailed", failure.Code);
    McpOutcome listing = await Run(access, new McpCommand.ListServers()).ConfigureAwait(true);
    McpOutcome.Status status = Assert.IsType<McpOutcome.Status>(listing);
    Assert.Equal(McpConnectionState.Failed, status.Servers[0].State);
    Assert.NotNull(status.Servers[0].Error);
  }

  [Fact]
  public async Task Failed_Connect_Retries_On_Next_Call()
  {
    FakeSessionPool pool = new() { FailAll = true };
    McpServerAccess access = Make(pool, ApprovedStdio());
    _ = await Run(access, new McpCommand.CallTool("demo", "echo", ParseArgs("{}"))).ConfigureAwait(true);
    pool.FailAll = false; // now it would succeed
    McpOutcome result = await Run(access, new McpCommand.CallTool("demo", "echo", ParseArgs("{}"))).ConfigureAwait(true);
    _ = Assert.IsType<McpOutcome.Called>(result);
    Assert.Equal(2, pool.ConnectCounts.GetValueOrDefault("demo"));
  }

  // ---- B4: unknown server/tool ----

  [Fact]
  public async Task Unknown_Server_Fails_McpServerNotFound()
  {
    FakeSessionPool pool = new() { Factory = _ => new FakeSession() };
    McpServerAccess access = Make(pool, ApprovedStdio());
    McpOutcome result = await Run(access, new McpCommand.CallTool("nope", "echo", ParseArgs("{}"))).ConfigureAwait(true);
    McpOutcome.Failure failure = Assert.IsType<McpOutcome.Failure>(result);
    Assert.Equal("McpServerNotFound", failure.Code);
    Assert.Contains("demo", failure.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Unknown_Tool_Fails_McpToolNotFound()
  {
    FakeSessionPool pool = new() { Factory = _ => new FakeSession() };
    McpServerAccess access = Make(pool, ApprovedStdio());
    _ = await Run(access, new McpCommand.CallTool("demo", "echo", ParseArgs("{}"))).ConfigureAwait(true);
    McpOutcome result = await Run(access, new McpCommand.CallTool("demo", "nope", ParseArgs("{}"))).ConfigureAwait(true);
    McpOutcome.Failure failure = Assert.IsType<McpOutcome.Failure>(result);
    Assert.Equal("McpToolNotFound", failure.Code);
    Assert.Contains("echo", failure.Message, StringComparison.Ordinal);
  }
#pragma warning restore CA2000 // Use a using statement or using declaration

  // ---- storage fault ----

  [Fact]
  public async Task Store_Failure_Fails_StorageUnavailable()
  {
    McpServerAccess access = new(new ThrowingStore(), new FakeSessionPool(), "ws-test");
    try
    {
      McpOutcome result = await Run(access, new McpCommand.ListServers()).ConfigureAwait(true);
      McpOutcome.Failure failure = Assert.IsType<McpOutcome.Failure>(result);
      Assert.Equal("StorageUnavailable", failure.Code);
    }
    finally
    {
      await access.DisposeAsync().ConfigureAwait(true);
    }
  }

  private sealed class ThrowingStore : IMcpServerStore
  {
    public Task<Result<IReadOnlyList<McpServerConfig>>> ListAsync(string workspaceId, CancellationToken ct = default) =>
        throw new InvalidOperationException("db gone");

    public Task<Result<McpServerConfig>> GetAsync(int id, string workspaceId, CancellationToken ct = default) =>
        throw new InvalidOperationException("db gone");

    public Task<Result<McpServerConfig>> AddAsync(McpServerConfig server, CancellationToken ct = default) =>
        throw new InvalidOperationException("db gone");

    public Task<Result<McpServerConfig>> UpdateAsync(McpServerConfig server, CancellationToken ct = default) =>
        throw new InvalidOperationException("db gone");

    public Task<Result<bool>> DeleteAsync(int id, string workspaceId, CancellationToken ct = default) =>
        throw new InvalidOperationException("db gone");

    public Task<Result<McpOAuthTokens>> SaveTokensAsync(int serverId, McpOAuthTokens tokens, CancellationToken ct = default) =>
        throw new InvalidOperationException("db gone");

    public Task<Result<McpOAuthTokens?>> GetTokensAsync(int serverId, CancellationToken ct = default) =>
        throw new InvalidOperationException("db gone");

    public Task<Result<bool>> AppendDecisionAsync(int serverId, string decision, string? detail, CancellationToken ct = default) =>
        throw new InvalidOperationException("db gone");

    public Task<Result<IReadOnlyList<McpDecision>>> ListDecisionsAsync(int serverId, int take, CancellationToken ct = default) =>
        throw new InvalidOperationException("db gone");
  }

  private static System.Text.Json.JsonElement ParseArgs(string json)
  {
    using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json);
    return doc.RootElement.Clone();
  }

  // ---- issue #109: the per-call gate ----

  private static McpServerConfig Gated(string name = "gated", int id = 3) => new(
      id, name, McpTransport.Stdio, "npx gated", "[]", "{}", "{}", null,
      McpApprovalState.Approved, null, DateTimeOffset.UtcNow, McpGateMode.Mutating);

  // Named decision (CA2000): the access is an in-memory policy object over fakes;
  // its dispose is a no-op and the tests observe through the fakes.
#pragma warning disable CA2000 // Use a using statement or using declaration
  private static async Task<(McpOutcome Outcome, LoggingStore Store)> RunGatedAsync(
      McpServerConfig server, string tool)
  {
    FakeSessionPool pool = new();
    LoggingStore store = new(server);
    McpServerAccess access = new(store, pool, "ws-test");
    McpOutcome outcome = await access.ExecuteAsync(new McpCommand.CallTool(server.Name, tool,
        System.Text.Json.JsonDocument.Parse("{}").RootElement.Clone()),
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    return (outcome, store);
  }
#pragma warning restore CA2000

  private sealed class LoggingStore(params McpServerConfig[] servers) : FakeStore(servers)
  {
    public List<(int ServerId, string Decision)> Appends { get; } = [];

    public override Task<Result<bool>> AppendDecisionAsync(int serverId, string decision, string? detail, CancellationToken ct = default)
    {
      Appends.Add((serverId, detail is null ? decision : decision + ":" + detail));
      return Task.FromResult(Result.Success(true));
    }
  }

  [Fact]
  public async Task Gated_Mutating_Call_Is_Refused_And_Logged()
  {
    (McpOutcome outcome, LoggingStore store) = await RunGatedAsync(Gated(), "echo").ConfigureAwait(true);

    McpOutcome.Failure failure = Assert.IsType<McpOutcome.Failure>(outcome);
    Assert.Equal("McpCallGated", failure.Code);
    Assert.Contains("mutating", failure.Message, StringComparison.Ordinal);
    (int appendServerId, string appendDecision) = Assert.Single(store.Appends);
    Assert.Equal(3, appendServerId);
    Assert.StartsWith("gate-denied", appendDecision, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Gated_Server_Undeclared_Tool_Is_Mutating_By_Default()
  {
    // The MCP spec has no mutation flag: an undeclared tool gates (deny-by-default).
    (McpOutcome outcome, LoggingStore _) = await RunGatedAsync(Gated(), "ping").ConfigureAwait(true);

    _ = Assert.IsType<McpOutcome.Failure>(outcome);
  }

  [Fact]
  public async Task Ungated_Server_Behaves_As_Before()
  {
    (McpOutcome outcome, LoggingStore store) = await RunGatedAsync(ApprovedStdio(), "echo").ConfigureAwait(true);

    _ = Assert.IsType<McpOutcome.Called>(outcome);
    Assert.Empty(store.Appends);
  }

  // ---- issue #107 hardening: the pool never keeps a session connected under a
  // launch the user no longer trusts ----

#pragma warning disable CA2000 // Use a using statement or using declaration
  [Fact]
  public async Task TrustRelevant_Config_Change_Reconnects_The_Pooled_Session()
  {
    // Approved + connected under env A; the user edits the env (pending),
    // re-approves: the next dispatch must run against a FRESH session spawned
    // under env B - never the process the old trust decision produced.
    FakeSessionPool pool = new();
    FakeStore store = new(ApprovedStdio());
    McpServerAccess access = new(store, pool, "ws-test");
    _ = await Run(access, new McpCommand.CallTool("demo", "echo",
        System.Text.Json.JsonDocument.Parse("{}").RootElement.Clone())).ConfigureAwait(true);
    Assert.Equal(1, pool.ConnectCounts["demo"]);

    // The env edit + re-approval: same id, new env JSON, approved again.
#pragma warning disable JSON002 // Probable JSON string detected
    store.Servers[0] = store.Servers[0] with { EnvJson = "{\"K\":\"V2\"}" };
#pragma warning restore JSON002 // Probable JSON string detected

    _ = await Run(access, new McpCommand.CallTool("demo", "echo",
        System.Text.Json.JsonDocument.Parse("{}").RootElement.Clone())).ConfigureAwait(true);

    Assert.Equal(2, pool.ConnectCounts["demo"]);
  }

  [Fact]
  public async Task Unchanged_Config_Reuses_The_Pooled_Session()
  {
    FakeSessionPool pool = new();
    FakeStore store = new(ApprovedStdio());
    McpServerAccess access = new(store, pool, "ws-test");
    _ = await Run(access, new McpCommand.CallTool("demo", "echo",
        System.Text.Json.JsonDocument.Parse("{}").RootElement.Clone())).ConfigureAwait(true);
    _ = await Run(access, new McpCommand.CallTool("demo", "echo",
        System.Text.Json.JsonDocument.Parse("{}").RootElement.Clone())).ConfigureAwait(true);

    Assert.Equal(1, pool.ConnectCounts["demo"]);
  }
#pragma warning restore CA2000
}
