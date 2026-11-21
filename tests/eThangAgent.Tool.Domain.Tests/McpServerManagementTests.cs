using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain.Mcp;

namespace eThangAgent.ToolDomain.Tests;

/// <summary>Configuration policy for the MCP session pool: add stores then verifies,
///     update composes over the stored row and re-opens approval only on a launch
///     change, remove deletes and forgets the pooled session, and a launch that cannot
///     be verified leaves the store exactly as it was. The SDK session and the store are
///     scripted fakes over the seams.</summary>
public class McpServerManagementTests
{
  private static McpServerConfig ApprovedStdio(string name = "demo", int id = 1, string command = "npx demo") => new(
      id, name, McpTransport.Stdio, command, "[]", "{}", "{}", null,
      McpApprovalState.Approved, null, DateTimeOffset.UtcNow);

  private static McpServerSpec Spec(string name = "demo", string command = "npx demo") => new(
      name, McpTransport.Stdio, command, [], new Dictionary<string, string>(),
      new Dictionary<string, string>(), McpServerScope.Global, null, McpGateMode.None);

  private sealed class FakeSession : IMcpClientSession
  {
    public bool Disposed { get; private set; }
    public int ListCalls { get; private set; }
    public bool ListThrows { get; set; }

    public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct = default)
    {
      ListCalls++;
      return ListThrows
          ? throw new InvalidOperationException("handshake died")
          : Task.FromResult<IReadOnlyList<McpToolInfo>>([new McpToolInfo("echo", "Echoes text")]);
    }

    public Task<McpToolCallResult> CallToolAsync(string tool, string argumentsJson, CancellationToken ct = default) =>
        Task.FromResult(new McpToolCallResult("hi", false));

    public ValueTask DisposeAsync()
    {
      Disposed = true;
      return ValueTask.CompletedTask;
    }
  }

  private sealed class FakePool : IMcpClientSessionPool
  {
    public bool FailConnect { get; set; }
    public bool ListThrows { get; set; }
    public List<McpServerConfig> Connects { get; } = [];
    public List<FakeSession> Sessions { get; } = [];

    public Task<McpConnectResult> ConnectAsync(McpServerConfig server, CancellationToken ct = default)
    {
      Connects.Add(server);
      if (FailConnect)
      {
        return Task.FromResult<McpConnectResult>(
            new McpConnectResult.Failure("McpConnectFailed", "spawn failed"));
      }

      FakeSession session = new() { ListThrows = ListThrows };
      Sessions.Add(session);
      return Task.FromResult<McpConnectResult>(new McpConnectResult.Success(session));
    }
  }

  private sealed class FakeStore(params McpServerConfig[] servers) : IMcpServerStore
  {
    public List<McpServerConfig> Servers { get; } = [.. servers];
    public int NextId { get; set; } = 100;
    public bool FailWrites { get; set; }

    public Task<Result<IReadOnlyList<McpServerConfig>>> ListAsync(string workspaceId, CancellationToken ct = default) =>
        Task.FromResult(Result.Success<IReadOnlyList<McpServerConfig>>([.. Servers]));

    public Task<Result<McpServerConfig>> GetAsync(int id, string workspaceId, CancellationToken ct = default) =>
        Task.FromResult(Result.Success(Servers.Single(s => s.Id == id)));

    public Task<Result<McpServerConfig>> AddAsync(McpServerConfig server, CancellationToken ct = default)
    {
      if (FailWrites)
      {
        return Task.FromResult(Result.Failure<McpServerConfig>(
            new DomainError("StorageUnavailable", "disk gone")));
      }

      McpServerConfig stored = server with { Id = NextId++ };
      Servers.Add(stored);
      return Task.FromResult(Result.Success(stored));
    }

    public Task<Result<McpServerConfig>> UpdateAsync(McpServerConfig server, CancellationToken ct = default)
    {
      if (FailWrites)
      {
        return Task.FromResult(Result.Failure<McpServerConfig>(
            new DomainError("StorageUnavailable", "disk gone")));
      }

      int index = Servers.FindIndex(s => s.Id == server.Id);
      Servers[index] = server;
      return Task.FromResult(Result.Success(server));
    }

    public Task<Result<bool>> DeleteAsync(int id, string workspaceId, CancellationToken ct = default)
    {
      int removed = Servers.RemoveAll(s => s.Id == id);
      return Task.FromResult(Result.Success(removed > 0));
    }

    public Task<Result<McpOAuthTokens>> SaveTokensAsync(int serverId, McpOAuthTokens tokens, CancellationToken ct = default) =>
        Task.FromResult(Result.Success(tokens));

    public Task<Result<McpOAuthTokens?>> GetTokensAsync(int serverId, CancellationToken ct = default) =>
        Task.FromResult(Result.Success<McpOAuthTokens?>(null));

    public Task<Result<bool>> AppendDecisionAsync(int serverId, string decision, string? detail, CancellationToken ct = default) =>
        Task.FromResult(Result.Success(true));

    public Task<Result<IReadOnlyList<McpDecision>>> ListDecisionsAsync(int serverId, int take, CancellationToken ct = default) =>
        Task.FromResult(Result.Success<IReadOnlyList<McpDecision>>([]));
  }

  private static Task<McpOutcome> Run(McpServerAccess access, McpCommand command) =>
      access.ExecuteAsync(command, TestContext.Current.CancellationToken);

  // ---- add ----

  [Fact]
  public async Task Add_Stores_The_Server_Approved_And_Verifies_It()
  {
    FakeStore store = new();
    FakePool pool = new();
    using McpServerAccess access = new(store, pool, "ws-test");

    McpOutcome outcome = await Run(access, new McpCommand.AddServer(Spec())).ConfigureAwait(true);

    McpOutcome.Added added = Assert.IsType<McpOutcome.Added>(outcome);
    Assert.Equal("demo", added.Name);
    Assert.Equal(McpApprovalState.Approved, added.Approval);
    Assert.Equal(McpServerScope.Global, added.Scope);
    Assert.Equal("echo", Assert.Single(added.Tools).Name);
    McpServerConfig stored = Assert.Single(store.Servers);
    Assert.Equal("demo", stored.Name);
    Assert.Equal(McpApprovalState.Approved, stored.ApprovalState);
    Assert.Null(stored.WorkspaceId);
  }

  [Fact]
  public async Task Add_Connects_Exactly_Once_And_Disposes_The_Verification_Session()
  {
    FakeStore store = new();
    FakePool pool = new();
    using McpServerAccess access = new(store, pool, "ws-test");

    _ = await Run(access, new McpCommand.AddServer(Spec())).ConfigureAwait(true);

    _ = Assert.Single(pool.Connects);
    Assert.True(Assert.Single(pool.Sessions).Disposed);
  }

  [Fact]
  public async Task Add_With_A_Launch_That_Cannot_Connect_Stores_Nothing()
  {
    FakeStore store = new();
    FakePool pool = new() { FailConnect = true };
    using McpServerAccess access = new(store, pool, "ws-test");

    McpOutcome outcome = await Run(access, new McpCommand.AddServer(Spec())).ConfigureAwait(true);

    McpOutcome.Failure failure = Assert.IsType<McpOutcome.Failure>(outcome);
    Assert.Equal("McpConnectFailed", failure.Code);
    Assert.Contains("was not stored", failure.Message, StringComparison.Ordinal);
    Assert.Empty(store.Servers);
  }

  [Fact]
  public async Task Add_With_A_Server_That_Dies_Mid_Discovery_Stores_Nothing()
  {
    FakeStore store = new();
    FakePool pool = new() { ListThrows = true };
    using McpServerAccess access = new(store, pool, "ws-test");

    McpOutcome outcome = await Run(access, new McpCommand.AddServer(Spec())).ConfigureAwait(true);

    McpOutcome.Failure failure = Assert.IsType<McpOutcome.Failure>(outcome);
    Assert.Equal("McpConnectFailed", failure.Code);
    Assert.Empty(store.Servers);
    Assert.True(Assert.Single(pool.Sessions).Disposed);
  }

  [Fact]
  public async Task Add_Honours_The_Workspace_Scope()
  {
    FakeStore store = new();
    using McpServerAccess access = new(store, new FakePool(), "ws-test");
    McpServerSpec spec = Spec() with { Scope = McpServerScope.Workspace };

    McpOutcome outcome = await Run(access, new McpCommand.AddServer(spec)).ConfigureAwait(true);

    _ = Assert.IsType<McpOutcome.Added>(outcome);
    Assert.Equal("ws-test", Assert.Single(store.Servers).WorkspaceId);
  }

  [Fact]
  public async Task Add_Surfaces_A_Store_Failure_Without_Connecting()
  {
    FakeStore store = new() { FailWrites = true };
    FakePool pool = new();
    using McpServerAccess access = new(store, pool, "ws-test");

    McpOutcome outcome = await Run(access, new McpCommand.AddServer(Spec())).ConfigureAwait(true);

    McpOutcome.Failure failure = Assert.IsType<McpOutcome.Failure>(outcome);
    Assert.Equal("StorageUnavailable", failure.Code);
    Assert.Empty(pool.Connects);
  }

  // ---- update ----

  [Fact]
  public async Task Update_Renames_Without_Restating_The_Launch()
  {
    FakeStore store = new(ApprovedStdio());
    FakePool pool = new();
    using McpServerAccess access = new(store, pool, "ws-test");
    McpServerEdit edit = new("renamed", null, null, null, null, null, null, null, null);

    McpOutcome outcome = await Run(access, new McpCommand.UpdateServer("demo", edit)).ConfigureAwait(true);

    McpOutcome.Updated updated = Assert.IsType<McpOutcome.Updated>(outcome);
    Assert.Equal("renamed", updated.Name);
    McpServerConfig stored = Assert.Single(store.Servers);
    Assert.Equal("renamed", stored.Name);
    Assert.Equal("npx demo", stored.CommandOrUrl);
  }

  [Fact]
  public async Task Update_Of_The_Launch_Re_Opens_Approval()
  {
    FakeStore store = new(ApprovedStdio());
    using McpServerAccess access = new(store, new FakePool(), "ws-test");
    McpServerEdit edit = new(null, null, "npx other", null, null, null, null, null, null);

    McpOutcome outcome = await Run(access, new McpCommand.UpdateServer("demo", edit)).ConfigureAwait(true);

    McpOutcome.Updated updated = Assert.IsType<McpOutcome.Updated>(outcome);
    Assert.Equal(McpApprovalState.Pending, updated.Approval);
    Assert.Equal(McpApprovalState.Pending, Assert.Single(store.Servers).ApprovalState);
  }

  [Fact]
  public async Task Update_Of_The_Gate_Keeps_The_Users_Approval()
  {
    FakeStore store = new(ApprovedStdio());
    using McpServerAccess access = new(store, new FakePool(), "ws-test");
    McpServerEdit edit = new(null, null, null, null, null, null, null, null, McpGateMode.Mutating);

    McpOutcome outcome = await Run(access, new McpCommand.UpdateServer("demo", edit)).ConfigureAwait(true);

    McpOutcome.Updated updated = Assert.IsType<McpOutcome.Updated>(outcome);
    Assert.Equal(McpApprovalState.Approved, updated.Approval);
    Assert.Equal(McpGateMode.Mutating, Assert.Single(store.Servers).GateMode);
  }

  [Fact]
  public async Task Update_With_A_Launch_That_Cannot_Connect_Restores_The_Stored_Row()
  {
    FakeStore store = new(ApprovedStdio());
    FakePool pool = new() { FailConnect = true };
    using McpServerAccess access = new(store, pool, "ws-test");
    McpServerEdit edit = new(null, null, "npx broken", null, null, null, null, null, null);

    McpOutcome outcome = await Run(access, new McpCommand.UpdateServer("demo", edit)).ConfigureAwait(true);

    McpOutcome.Failure failure = Assert.IsType<McpOutcome.Failure>(outcome);
    Assert.Equal("McpConnectFailed", failure.Code);
    Assert.Contains("was not changed", failure.Message, StringComparison.Ordinal);
    McpServerConfig stored = Assert.Single(store.Servers);
    Assert.Equal("npx demo", stored.CommandOrUrl);
    Assert.Equal(McpApprovalState.Approved, stored.ApprovalState);
  }

  [Fact]
  public async Task Update_Of_An_Unknown_Server_Fails_And_Names_The_Configured_Ones()
  {
    FakeStore store = new(ApprovedStdio());
    using McpServerAccess access = new(store, new FakePool(), "ws-test");
    McpServerEdit edit = new("renamed", null, null, null, null, null, null, null, null);

    McpOutcome outcome = await Run(access, new McpCommand.UpdateServer("nope", edit)).ConfigureAwait(true);

    McpOutcome.Failure failure = Assert.IsType<McpOutcome.Failure>(outcome);
    Assert.Equal("McpServerNotFound", failure.Code);
    Assert.Contains("demo", failure.Message, StringComparison.Ordinal);
  }

  // ---- remove ----

  [Fact]
  public async Task Remove_Deletes_The_Row()
  {
    FakeStore store = new(ApprovedStdio());
    using McpServerAccess access = new(store, new FakePool(), "ws-test");

    McpOutcome outcome = await Run(access, new McpCommand.RemoveServer("demo")).ConfigureAwait(true);

    McpOutcome.Removed removed = Assert.IsType<McpOutcome.Removed>(outcome);
    Assert.Equal("demo", removed.Name);
    Assert.Empty(store.Servers);
  }

  [Fact]
  public async Task Remove_Forgets_The_Pooled_Session_So_A_Re_Add_Reconnects()
  {
    FakeStore store = new(ApprovedStdio());
    FakePool pool = new();
    using McpServerAccess access = new(store, pool, "ws-test");

    _ = await Run(access, new McpCommand.CallTool("demo", "echo", ParseArgs("{}"))).ConfigureAwait(true);
    _ = Assert.Single(pool.Connects);

    _ = await Run(access, new McpCommand.RemoveServer("demo")).ConfigureAwait(true);
    _ = await store.AddAsync(ApprovedStdio(id: 2), TestContext.Current.CancellationToken).ConfigureAwait(true);
    _ = await Run(access, new McpCommand.CallTool("demo", "echo", ParseArgs("{}"))).ConfigureAwait(true);

    Assert.Equal(2, pool.Connects.Count);
  }

  [Fact]
  public async Task Remove_Of_An_Unknown_Server_Fails()
  {
    using McpServerAccess access = new(new FakeStore(), new FakePool(), "ws-test");

    McpOutcome outcome = await Run(access, new McpCommand.RemoveServer("nope")).ConfigureAwait(true);

    McpOutcome.Failure failure = Assert.IsType<McpOutcome.Failure>(outcome);
    Assert.Equal("McpServerNotFound", failure.Code);
  }

  // ---- listing ----

  [Fact]
  public async Task List_Reports_Scope_And_Gate()
  {
    McpServerConfig workspaceScoped = ApprovedStdio("local", 2) with
    {
      WorkspaceId = "ws-test",
      GateMode = McpGateMode.Mutating,
    };
    using McpServerAccess access = new(new FakeStore(ApprovedStdio(), workspaceScoped), new FakePool(), "ws-test");

    McpOutcome outcome = await Run(access, new McpCommand.ListServers()).ConfigureAwait(true);

    McpOutcome.Status status = Assert.IsType<McpOutcome.Status>(outcome);
    McpServerStatus local = status.Servers.Single(s => s.Name == "local");
    Assert.Equal(McpServerScope.Workspace, local.Scope);
    Assert.Equal(McpGateMode.Mutating, local.GateMode);
    Assert.Equal(McpServerScope.Global, status.Servers.Single(s => s.Name == "demo").Scope);
  }

  private static System.Text.Json.JsonElement ParseArgs(string json)
  {
    using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json);
    return doc.RootElement.Clone();
  }
}
