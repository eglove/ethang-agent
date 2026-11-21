using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain.Mcp;

namespace eThangAgent.Desktop.Tests;

/// <summary>The MCP servers dialog's form is opened by the two buttons that exist for it.
///     The view-model's form state is not the claim: the buttons and the form are wired
///     through the view, and a break there leaves a dialog whose Add and Edit do nothing
///     while every view-model test still passes.</summary>
[Trait("Category", "Process")]
public class McpServersDialogFormTests
{
  private static McpServersViewModel ViewModel() => new(new EmptyStore(), new EmptyAccess(), "ws-demo");

  [AvaloniaFact]
  public void AddServer_OpensTheForm()
  {
    McpServersViewModel vm = ViewModel();
    Views.McpServersWindow window = new(vm);
    window.Show();

    window.GetControl<Button>("AddButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    Assert.True(vm.IsFormOpen, "Add server must open the form");
    Assert.True(window.GetControl<Button>("SaveButton").IsVisible, "the form's Save button must be visible");
    Assert.False(window.GetControl<Button>("AddButton").IsVisible, "the list controls hide while the form is open");
    window.Close();
  }

  [AvaloniaFact]
  public void Cancel_ClosesTheFormAgain()
  {
    McpServersViewModel vm = ViewModel();
    Views.McpServersWindow window = new(vm);
    window.Show();

    window.GetControl<Button>("AddButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    window.GetControl<Button>("CancelFormButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    Assert.False(vm.IsFormOpen, "Cancel must close the form");
    Assert.True(window.GetControl<Button>("AddButton").IsVisible, "the list controls return");
    window.Close();
  }

  private sealed class EmptyStore : IMcpServerStore
  {
    public Task<Result<IReadOnlyList<McpServerConfig>>> ListAsync(string workspaceId, CancellationToken ct = default) =>
        Task.FromResult(Result.Success<IReadOnlyList<McpServerConfig>>([]));

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

  private sealed class EmptyAccess : IMcpServerAccess
  {
    public Task<McpOutcome> ExecuteAsync(McpCommand command, CancellationToken ct = default) =>
        Task.FromResult<McpOutcome>(new McpOutcome.Status([]));
  }
}
