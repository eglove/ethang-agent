using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.StateDomain;
using eThangAgent.ToolDomain.Mcp;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop.Tests;

/// <summary>The MCP servers dialog's shell wiring (issue #106): a tab's container
///     reaches the dialog with its OWN store, its OWN shared pooled access, and its
///     workspace id - the same class of wiring the Links dialog tests pin. The
///     dialog's save and status flows run against the session's real seams, so a
///     config saved here is immediately visible to the mcp tool's dispatches.
///     Registry-free: the store and access are real domain objects over fakes.</summary>
public class McpServersDialogWiringTests
{
  [Fact]
  public async Task Shell_Exposes_Selected_Tab_Mcp_Seams()
  {
    FakeStore store = new();
    FakeAccess access = new(store);
    AgentId rootId = AgentId.NewId();
    MainViewModel shell = await MainViewModel.ForPrebuiltSessionAsync(
        Session(rootId, store, access)).ConfigureAwait(true);

    Assert.NotNull(shell.SelectedMcpServersLoader);
    (IMcpServerStore Store, IMcpServerAccess Access, string WorkspaceId) = shell.SelectedMcpServersLoader();
    Assert.Same(store, Store);
    Assert.Same(access, Access);
    Assert.Equal("ws-demo", WorkspaceId);
  }

  [Fact]
  public async Task Dialog_Built_From_Shell_Seams_Saves_Through_The_Session_Store()
  {
    FakeStore store = new();
    FakeAccess access = new(store);
    AgentId rootId = AgentId.NewId();
    MainViewModel shell = await MainViewModel.ForPrebuiltSessionAsync(
        Session(rootId, store, access)).ConfigureAwait(true);

    // The McpServersWindow's exact construction: the shell's seams.
    Assert.NotNull(shell.SelectedMcpServersLoader);
    (IMcpServerStore dialogStore, IMcpServerAccess dialogAccess, string WorkspaceId) = shell.SelectedMcpServersLoader();
    McpServersViewModel dialog = new(dialogStore, dialogAccess, WorkspaceId);
    dialog.BeginAdd();
    dialog.FormName = "saved";
    dialog.FormCommandOrUrl = "npx saved";
    dialog.SaveCommand.Execute(null);

    // The row is in the SAME store the session's mcp tool reads.
    _ = Assert.Single(store.Rows);
    Assert.Equal("saved", store.Rows[0].Name);
  }

  [Fact]
  public void Shell_Without_A_Tab_Exposes_Nothing()
  {
    MainViewModel vm = new((_, _) => Task.FromResult(Result.Failure<AgentSession>(
        new DomainError("NoFactory", "unused"))));
    Assert.Null(vm.SelectedMcpServersLoader);
  }

  private static AgentSession Session(AgentId rootId, FakeStore store, FakeAccess access)
  {
    ServiceProvider services = new ServiceCollection()
        .AddSingleton<IMcpServerStore>(store)
        .AddSingleton<IMcpServerAccess>(access)
        .AddSingleton<IWorkspaceContext>(new FixedWorkspaceContext("ws-demo"))
        .BuildServiceProvider();
    return new AgentSession(
        services,
        rootId,
        new Conversation(),
        Handler: null!,
        Lifecycle: new RootSessionLifecycle(new TestFixtures.StubStore()),
        Model: ModelConfig.Create("test/model", null, 128, 0.1f, 8192).Value!,
        WorkspaceRoot: @"C:\ws\demo",
        ProviderName: "openrouter",
        Inbox: new BoundedAgentMailbox(),
        ChildRuntime: new TestFixtures.StubAgentRuntime());
  }

  private sealed class FakeStore : IMcpServerStore
  {
    public List<McpServerConfig> Rows { get; } = [];

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

  private sealed class FakeAccess(IMcpServerStore store) : IMcpServerAccess
  {
    public Task<McpOutcome> ExecuteAsync(McpCommand command, CancellationToken ct = default)
    {
      if (command is McpCommand.ListServers)
      {
        Result<IReadOnlyList<McpServerConfig>> configs =
            store.ListAsync("ws-demo", CancellationToken.None).GetAwaiter().GetResult();
        McpServerStatus[] statuses = [.. (configs.Value ?? []).Select(c => new McpServerStatus(
            c.Name, c.ApprovalState, c.Transport, c.CommandOrUrl,
            McpConnectionState.NotConnected, [], null, null))];
        return Task.FromResult<McpOutcome>(new McpOutcome.Status(statuses));
      }

      return Task.FromResult<McpOutcome>(new McpOutcome.Failure("InvalidAction", "unused"));
    }
  }
};
