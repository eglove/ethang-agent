using eThangAgent.Desktop.ViewModels;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain.Mcp;

namespace eThangAgent.Desktop.Tests;

/// <summary>The MCP servers dialog's view-model (issue #106): the config form
///     (name, transport, command or URL, args/env/headers JSON, scope, pinned
///     version) writing through the V15 store, the approval controls (approve /
///     revoke - the trust event), removal (tokens deleted with the row), and the
///     per-server status view fed by the session's SHARED pooled access. Guards
///     fail with structured error text, never exceptions - the dialog stays open.
///     The store and access are real domain objects over fakes; nothing here knows
///     Avalonia exists.</summary>
public class McpServersViewModelTests
{
  // JSON002 fires only in the format/IDE host; the pragma pair is the repo's
  // named decision for hand-written JSON test shapes.
#pragma warning disable JSON002 // Probable JSON string detected
  private const string ArgsJson = "[\"--flag\"]";
  private const string EnvJson = "{\"K\":\"V\"}";
#pragma warning restore JSON002 // Probable JSON string detected
  private sealed class FakeStore : IMcpServerStore
  {
    public List<McpServerConfig> Rows { get; } = [];
    public List<McpServerConfig> Updates { get; } = [];
    public List<int> Deletes { get; } = [];
    public bool FailDeletes { get; set; }

    public Task<Result<IReadOnlyList<McpServerConfig>>> ListAsync(string workspaceId, CancellationToken ct = default) =>
        Task.FromResult(Result.Success<IReadOnlyList<McpServerConfig>>([.. Rows]));

    public Task<Result<McpServerConfig>> GetAsync(int id, string workspaceId, CancellationToken ct = default) =>
        Task.FromResult(Rows.FirstOrDefault(r => r.Id == id) is { } row
            ? Result.Success(row)
            : Result.Failure<McpServerConfig>(new DomainError("McpServerNotFound", "no")));

    public Task<Result<McpServerConfig>> AddAsync(McpServerConfig server, CancellationToken ct = default)
    {
      McpServerConfig stored = server with { Id = Rows.Count + 1 };
      Rows.Add(stored);
      return Task.FromResult(Result.Success(stored));
    }

    public Task<Result<McpServerConfig>> UpdateAsync(McpServerConfig server, CancellationToken ct = default)
    {
      int index = Rows.FindIndex(r => r.Id == server.Id);
      if (index < 0)
      {
        return Task.FromResult(Result.Failure<McpServerConfig>(new DomainError("McpServerNotFound", "no")));
      }

      Rows[index] = server;
      Updates.Add(server);
      return Task.FromResult(Result.Success(server));
    }

    public Task<Result<bool>> DeleteAsync(int id, string workspaceId, CancellationToken ct = default)
    {
      if (FailDeletes)
      {
        return Task.FromResult(Result.Failure<bool>(new DomainError("StorageUnavailable", "db closed")));
      }

      Deletes.Add(id);
      _ = Rows.RemoveAll(r => r.Id == id);
      return Task.FromResult(Result.Success(true));
    }

    public Task<Result<McpOAuthTokens>> SaveTokensAsync(int serverId, McpOAuthTokens tokens, CancellationToken ct = default) =>
        Task.FromResult(Result.Success(tokens));

    public Task<Result<McpOAuthTokens?>> GetTokensAsync(int serverId, CancellationToken ct = default) =>
        Task.FromResult(Result.Success<McpOAuthTokens?>(null));
  }

  /// <summary>A pooled access over the SAME store - the status view's live source.</summary>
  private sealed class FakeAccess(IMcpServerStore store) : IMcpServerAccess
  {
    public Dictionary<string, (McpConnectionState State, string? Error, string? Stderr)> Live { get; } = [];

    public Task<McpOutcome> ExecuteAsync(McpCommand command, CancellationToken ct = default)
    {
      if (command is McpCommand.ListServers)
      {
        Result<IReadOnlyList<McpServerConfig>> configs =
            store.ListAsync("ws", CancellationToken.None).GetAwaiter().GetResult();
        McpServerStatus[] statuses = [.. (configs.Value ?? []).Select(c =>
        {
          (McpConnectionState State, string? Error, string? Stderr) = Live.GetValueOrDefault(c.Name,
              (McpConnectionState.NotConnected, null, null));
          return new McpServerStatus(c.Name, c.ApprovalState, c.Transport, c.CommandOrUrl,
              State, [], Error, Stderr);
        })];
        return Task.FromResult<McpOutcome>(new McpOutcome.Status(statuses));
      }

      return Task.FromResult<McpOutcome>(new McpOutcome.Failure("InvalidAction", "unused"));
    }
  }

  private static McpServerConfig Row(
      string name = "demo",
      McpTransport transport = McpTransport.Stdio,
      string commandOrUrl = "npx demo",
      McpApprovalState approval = McpApprovalState.Approved,
      string? workspace = null,
      int id = 1) => new(
      id, name, transport, commandOrUrl, "[]", "{}", "{}", workspace,
      approval, null, DateTimeOffset.UtcNow);

  private static (McpServersViewModel Vm, FakeStore Store, FakeAccess Access) Make(
      params McpServerConfig[] rows)
  {
    FakeStore store = new();
    store.Rows.AddRange(rows);
    FakeAccess access = new(store);
    McpServersViewModel vm = new(store, access, "ws");
    return (vm, store, access);
  }

  // ---- load: rows and live status ----

  [Fact]
  public async Task Load_Lists_Configured_Servers_With_Live_Status()
  {
    (McpServersViewModel vm, _, FakeAccess access) = Make(Row(), Row("other", id: 2));
    access.Live["demo"] = (McpConnectionState.Connected, null, "tail");

    await vm.LoadAsync().ConfigureAwait(true);

    Assert.Equal(2, vm.Servers.Count);
    McpServerRow demo = vm.Servers.Single(s => s.Name == "demo");
    Assert.Equal(McpConnectionState.Connected, demo.State);
    Assert.Equal("tail", demo.Stderr);
    Assert.False(vm.IsLoading);
    Assert.Null(vm.LoadError);
  }

  [Fact]
  public async Task Load_Failure_Lands_In_LoadError()
  {
    (McpServersViewModel vm, FakeStore store, _) = Make();
    store.FailDeletes = true; // unused here; use a throwing store instead below

    // Swap in a failing store through the constructor seam: make a VM whose store throws.
    McpServersViewModel failing = new(new ThrowingStore(), new FakeAccess(new ThrowingStore()), "ws");
    await failing.LoadAsync().ConfigureAwait(true);

    Assert.NotNull(failing.LoadError);
    _ = vm;
    _ = store;
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
  }

  // ---- form validation ----

  [Fact]
  public void Empty_Name_Fails_Validation()
  {
    (McpServersViewModel vm, _, _) = Make();
    vm.BeginAdd();
    vm.FormName = "  ";
    vm.FormCommandOrUrl = "npx x";
    Assert.NotNull(vm.FormErrorText);
    Assert.False(vm.SaveCommand.CanExecute(null));
  }

  [Fact]
  public void Stdio_Requires_A_Command()
  {
    (McpServersViewModel vm, _, _) = Make();
    vm.BeginAdd();
    vm.FormName = "x";
    vm.FormTransportIndex = 0; // stdio
    vm.FormCommandOrUrl = "";
    Assert.NotNull(vm.FormErrorText);
  }

  [Fact]
  public void Http_Requires_A_Valid_Absolute_Url()
  {
    (McpServersViewModel vm, _, _) = Make();
    vm.BeginAdd();
    vm.FormName = "x";
    vm.FormTransportIndex = 1; // http
    vm.FormCommandOrUrl = "not a url";
    Assert.NotNull(vm.FormErrorText);
  }

  [Fact]
  public void Malformed_Args_Json_Fails_Validation()
  {
    (McpServersViewModel vm, _, _) = Make();
    vm.BeginAdd();
    vm.FormName = "x";
    vm.FormCommandOrUrl = "npx x";
    vm.FormArgsJson = "[not json";
    Assert.NotNull(vm.FormErrorText);
  }

  [Fact]
  public void Malformed_Env_Json_Fails_Validation()
  {
    (McpServersViewModel vm, _, _) = Make();
    vm.BeginAdd();
    vm.FormName = "x";
    vm.FormCommandOrUrl = "npx x";
    vm.FormEnvJson = "{broken";
    Assert.NotNull(vm.FormErrorText);
  }

  [Fact]
  public void Malformed_Headers_Json_Fails_Validation()
  {
    (McpServersViewModel vm, _, _) = Make();
    vm.BeginAdd();
    vm.FormName = "x";
    vm.FormCommandOrUrl = "npx x";
    vm.FormHeadersJson = "{broken";
    Assert.NotNull(vm.FormErrorText);
  }

  [Fact]
  public void Args_Must_Be_A_Json_Array()
  {
    (McpServersViewModel vm, _, _) = Make();
    vm.BeginAdd();
    vm.FormName = "x";
    vm.FormCommandOrUrl = "npx x";
    vm.FormArgsJson = "{}";
    Assert.NotNull(vm.FormErrorText);
  }

  [Fact]
  public void Env_And_Headers_Must_Be_Json_Objects()
  {
    (McpServersViewModel vm, _, _) = Make();
    vm.BeginAdd();
    vm.FormName = "x";
    vm.FormCommandOrUrl = "npx x";
    vm.FormEnvJson = "[]";
    Assert.NotNull(vm.FormErrorText);

    vm.FormEnvJson = "{}";
    vm.FormHeadersJson = "[]";
    Assert.NotNull(vm.FormErrorText);
  }

  [Fact]
  public void Valid_Form_Passes_Validation()
  {
    (McpServersViewModel vm, _, _) = Make();
    vm.BeginAdd();
    vm.FormName = "x";
    vm.FormCommandOrUrl = "npx x";
    vm.FormArgsJson = ArgsJson;
    vm.FormEnvJson = EnvJson;
    vm.FormHeadersJson = "{}";
    Assert.Null(vm.FormErrorText);
    Assert.True(vm.SaveCommand.CanExecute(null));
  }

  // ---- save: add and edit ----

  [Fact]
  public async Task Save_Adds_A_Pending_Server_And_Reloads()
  {
    (McpServersViewModel vm, FakeStore store, _) = Make();
    await vm.LoadAsync().ConfigureAwait(true);
    vm.BeginAdd();
    vm.FormName = "fresh";
    vm.FormCommandOrUrl = "npx fresh";
    vm.FormArgsJson = ArgsJson;

    vm.SaveCommand.Execute(null);

    McpServerConfig added = Assert.Single(store.Rows);
    Assert.Equal("fresh", added.Name);
    Assert.Equal(McpApprovalState.Pending, added.ApprovalState); // the trust event is a separate click
    Assert.Equal(McpTransport.Stdio, added.Transport);
    Assert.Equal(ArgsJson, added.ArgsJson);
    Assert.Equal("ws", added.WorkspaceId); // default scope: this workspace
    Assert.False(vm.IsFormOpen);
    await Task.Delay(50, TestContext.Current.CancellationToken).ConfigureAwait(true); // reload lands
    _ = Assert.Single(vm.Servers);
  }

  [Fact]
  public async Task Save_Editing_Identity_Reopens_Approval()
  {
    (McpServersViewModel vm, FakeStore store, _) = Make(Row(approval: McpApprovalState.Approved));
    await vm.LoadAsync().ConfigureAwait(true);
    vm.BeginEdit(vm.Servers[0]);
    vm.FormCommandOrUrl = "npx DIFFERENT"; // the command changed - trust must not carry over

    vm.SaveCommand.Execute(null);

    McpServerConfig updated = Assert.Single(store.Updates);
    Assert.Equal(McpApprovalState.Pending, updated.ApprovalState);
    Assert.Equal("npx DIFFERENT", updated.CommandOrUrl);
  }

  [Fact]
  public async Task Save_Editing_Non_Identity_Fields_Keeps_Approval()
  {
    (McpServersViewModel vm, FakeStore store, _) = Make(Row(approval: McpApprovalState.Approved));
    await vm.LoadAsync().ConfigureAwait(true);
    vm.BeginEdit(vm.Servers[0]);
    vm.FormEnvJson = EnvJson; // env is not identity

    vm.SaveCommand.Execute(null);

    McpServerConfig updated = Assert.Single(store.Updates);
    Assert.Equal(McpApprovalState.Approved, updated.ApprovalState);
    Assert.Equal(EnvJson, updated.EnvJson);
  }

  [Fact]
  public void Save_With_Invalid_Form_Does_Not_Touch_The_Store()
  {
    (McpServersViewModel vm, FakeStore store, _) = Make();
    vm.BeginAdd();
    vm.FormName = ""; // invalid

    vm.SaveCommand.Execute(null); // the guard is load-bearing: Execute does not consult CanExecute

    Assert.Empty(store.Rows);
    Assert.NotNull(vm.FormError);
  }

  // ---- approval controls (the trust event) ----

  [Fact]
  public async Task Approve_Marks_The_Selected_Server_Approved()
  {
    (McpServersViewModel vm, FakeStore store, _) = Make(Row(approval: McpApprovalState.Pending));
    await vm.LoadAsync().ConfigureAwait(true);
    vm.Selected = vm.Servers[0];

    vm.ApproveCommand.Execute(null);

    McpServerConfig updated = Assert.Single(store.Updates);
    Assert.Equal(McpApprovalState.Approved, updated.ApprovalState);
    Assert.Null(vm.ActionError);
  }

  [Fact]
  public async Task Revoke_Marks_The_Selected_Server_Revoked()
  {
    (McpServersViewModel vm, FakeStore store, _) = Make(Row(approval: McpApprovalState.Approved));
    await vm.LoadAsync().ConfigureAwait(true);
    vm.Selected = vm.Servers[0];

    vm.RevokeCommand.Execute(null);

    McpServerConfig updated = Assert.Single(store.Updates);
    Assert.Equal(McpApprovalState.Revoked, updated.ApprovalState);
  }

  [Fact]
  public async Task Approve_Without_Selection_Is_A_No_Op()
  {
    (McpServersViewModel vm, FakeStore store, _) = Make(Row());
    await vm.LoadAsync().ConfigureAwait(true);

    vm.ApproveCommand.Execute(null);

    Assert.Empty(store.Updates);
  }

  // ---- removal (tokens deleted with the row - the store's contract) ----

  [Fact]
  public async Task Remove_Deletes_The_Selected_Row()
  {
    (McpServersViewModel vm, FakeStore store, _) = Make(Row(), Row("other", id: 2));
    await vm.LoadAsync().ConfigureAwait(true);
    vm.Selected = vm.Servers.Single(s => s.Name == "demo");

    vm.RemoveCommand.Execute(null);

    Assert.Equal([1], store.Deletes);
    Assert.Null(vm.ActionError);
  }

  [Fact]
  public async Task Remove_Failure_Lands_In_ActionError()
  {
    (McpServersViewModel vm, FakeStore store, _) = Make(Row());
    store.FailDeletes = true;
    await vm.LoadAsync().ConfigureAwait(true);
    vm.Selected = vm.Servers[0];

    vm.RemoveCommand.Execute(null);

    Assert.NotNull(vm.ActionError);
    Assert.Equal("db closed", vm.ActionError);
  }

  [Fact]
  public void Commands_Start_Disabled_Without_A_Selection()
  {
    (McpServersViewModel vm, _, _) = Make();
    Assert.False(vm.RemoveCommand.CanExecute(null));
    Assert.False(vm.ApproveCommand.CanExecute(null));
    Assert.False(vm.RevokeCommand.CanExecute(null));
    Assert.False(vm.SaveCommand.CanExecute(null)); // no form open
  }
};
