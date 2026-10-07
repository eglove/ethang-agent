using eThangAgent.Desktop.ViewModels;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain.Mcp;

namespace eThangAgent.Desktop.Tests;

/// <summary>The dialog's gate and log surface (issues #107/#109): the gate toggle
///     rides the form (a trust-relevant field), approve/revoke append decision rows,
///     and the selected server's decision log is loadable for display.</summary>
public class McpServersDialogGateTests
{
  private sealed class LoggingStore : IMcpServerStore
  {
    public List<McpServerConfig> Rows { get; } = [];
    public List<(int ServerId, string Decision, string? Detail)> Appends { get; } = [];

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
      return Task.FromResult(Result.Success(server));
    }

    public Task<Result<bool>> DeleteAsync(int id, string workspaceId, CancellationToken ct = default)
    {
      _ = Rows.RemoveAll(r => r.Id == id);
      return Task.FromResult(Result.Success(true));
    }

    public Task<Result<McpOAuthTokens>> SaveTokensAsync(int serverId, McpOAuthTokens tokens, CancellationToken ct = default) =>
        Task.FromResult(Result.Success(tokens));

    public Task<Result<McpOAuthTokens?>> GetTokensAsync(int serverId, CancellationToken ct = default) =>
        Task.FromResult(Result.Success<McpOAuthTokens?>(null));

    public Task<Result<bool>> AppendDecisionAsync(int serverId, string decision, string? detail, CancellationToken ct = default)
    {
      Appends.Add((serverId, decision, detail));
      return Task.FromResult(Result.Success(true));
    }

    public Task<Result<IReadOnlyList<McpDecision>>> ListDecisionsAsync(int serverId, int take, CancellationToken ct = default) =>
        Task.FromResult(Result.Success<IReadOnlyList<McpDecision>>([]));
  }

  private sealed class FakeAccess(IMcpServerStore store) : IMcpServerAccess
  {
    public Task<McpOutcome> ExecuteAsync(McpCommand command, CancellationToken ct = default)
    {
      Result<IReadOnlyList<McpServerConfig>> configs =
          store.ListAsync("ws", CancellationToken.None).GetAwaiter().GetResult();
      return Task.FromResult<McpOutcome>(new McpOutcome.Status([.. (configs.Value ?? []).Select(c =>
          new McpServerStatus(c.Name, c.ApprovalState, c.Transport, c.CommandOrUrl,
              McpConnectionState.NotConnected, [], null, null))]));
    }
  }

  private static (McpServersViewModel Vm, LoggingStore Store) MakeWithApproved()
  {
    LoggingStore store = new();
    _ = store.AddAsync(new McpServerConfig(0, "demo", McpTransport.Stdio, "npx demo", "[]", "{}", "{}",
        null, McpApprovalState.Approved, null, DateTimeOffset.UtcNow), CancellationToken.None).GetAwaiter().GetResult();
    McpServersViewModel vm = new(store, new FakeAccess(store), "ws");
    vm.LoadAsync().GetAwaiter().GetResult();
    return (vm, store);
  }

  [Fact]
  public void Approve_Appends_Approved_Decision()
  {
    (McpServersViewModel vm, LoggingStore store) = MakeWithApproved();
    vm.Selected = vm.Servers[0];

    vm.ApproveCommand.Execute(null);

    (int appendServerId, string appendDecision, _) = Assert.Single(store.Appends);
    Assert.Equal(1, appendServerId);
    Assert.Equal("approved", appendDecision);
  }

  [Fact]
  public void Revoke_Appends_Revoked_Decision()
  {
    (McpServersViewModel vm, LoggingStore store) = MakeWithApproved();
    vm.Selected = vm.Servers[0];

    vm.RevokeCommand.Execute(null);

    (int _, string appendDecision, string? _) = Assert.Single(store.Appends);
    Assert.Equal("revoked", appendDecision);
  }

  [Fact]
  public void Form_Carries_Gate_Mode_And_Save_Persists_It()
  {
    (McpServersViewModel vm, LoggingStore store) = MakeWithApproved();
    vm.BeginEdit(vm.Servers[0]);

    vm.FormGateMutating = true;
    vm.SaveCommand.Execute(null);

    McpServerConfig updated = store.Rows[0];
    Assert.Equal(McpGateMode.Mutating, updated.GateMode);
  }
}
