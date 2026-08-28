using eThangAgent.Agent.Application.Sessions;
using eThangAgent.AgentDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop.Tests;

/// <summary>Behavior of the Sessions dialog view-model: catalog loading, open-session
///     grey-out, pre-selection, and the confirm guard. The catalog is faked at the
///     loader-delegate seam; the real query handler is covered in Agent.Application tests.</summary>
public class SessionsViewModelTests
{
  private static readonly DateTimeOffset Started =
      new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

  private static SessionCatalogEntry Entry(AgentId id, string workspace = "C:/ws/demo",
      string provider = "openrouter", DateTimeOffset? createdAt = null)
      => new(id, workspace, provider, AgentStatus.Completed,
          createdAt ?? Started, Started.AddMinutes(30));

  private static SessionsViewModel CreateVm(
      IReadOnlyList<SessionCatalogEntry> entries,
      IReadOnlySet<AgentId>? open = null)
      => new(
          _ => Task.FromResult(Result.Success(entries)),
          () => open ?? new HashSet<AgentId>());

  private static async Task<SessionsViewModel> LoadVmAsync(
      IReadOnlyList<SessionCatalogEntry> entries, IReadOnlySet<AgentId>? open = null)
  {
    SessionsViewModel vm = CreateVm(entries, open);
    await vm.LoadAsync().ConfigureAwait(true);
    return vm;
  }

  [Fact]
  public async Task Load_Populates_Rows_Newest_First_With_Detail()
  {
    // The handler returns entries newest first; the view-model preserves that order.
    SessionsViewModel vm = await LoadVmAsync(
    [
        Entry(AgentId.NewId(), workspace: "C:/ws/newer", createdAt: Started.AddMinutes(5)),
            Entry(AgentId.NewId(), workspace: "C:/ws/older", createdAt: Started),
    ]);

    Assert.Equal(2, vm.Rows.Count);
    Assert.Equal("C:/ws/newer", vm.Rows[0].WorkspaceRoot);
    Assert.Equal("newer", vm.Rows[0].WorkspaceName);
    Assert.Equal("C:/ws/older", vm.Rows[1].WorkspaceRoot);
    Assert.Equal("OpenRouter", vm.Rows[0].ProviderDisplay); // display name, not the raw id
    Assert.Contains("started", vm.Rows[0].Detail, StringComparison.Ordinal);
    Assert.False(vm.Rows[0].IsOpen);
  }

  [Fact]
  public async Task Load_Marks_Open_Sessions_Greyed_And_Preselects_First_Resumable()
  {
    AgentId openId = AgentId.NewId();
    AgentId closedId = AgentId.NewId();

    SessionsViewModel vm = await LoadVmAsync(
        [Entry(openId, createdAt: Started.AddMinutes(5)), Entry(closedId, createdAt: Started)],
        open: new HashSet<AgentId> { openId });

    Assert.True(vm.Rows.Single(r => r.Id == openId).IsOpen);
    Assert.True(vm.Rows.Single(r => r.Id == openId).Dimming < 1.0);
    Assert.False(vm.Rows.Single(r => r.Id == closedId).IsOpen);
    // The already-open row is skipped for pre-selection.
    Assert.Equal(closedId, vm.SelectedRow!.Id);
  }

  [Fact]
  public async Task Confirm_On_Resumable_Row_Raises_The_Session_Id()
  {
    AgentId id = AgentId.NewId();
    SessionsViewModel vm = await LoadVmAsync([Entry(id)]);
    AgentId? confirmed = null;
    vm.ConfirmRequested += (_, sessionId) => confirmed = sessionId;

    vm.SelectedRow = vm.Rows.Single(r => r.Id == id);
    vm.ConfirmCommand.Execute(null);

    Assert.Equal(id, confirmed);
  }

  [Fact]
  public async Task Confirm_On_Open_Row_Never_Fires()
  {
    AgentId openId = AgentId.NewId();
    SessionsViewModel vm = await LoadVmAsync(
        [Entry(openId)],
        open: new HashSet<AgentId> { openId });
    bool fired = false;
    vm.ConfirmRequested += (_, _) => fired = true;

    vm.SelectedRow = vm.Rows.Single();
    vm.ConfirmCommand.Execute(null); // guard: ICommand.Execute skips CanExecute

    Assert.False(fired);
  }

  [Fact]
  public async Task Load_Failure_Lands_In_LoadError()
  {
    SessionsViewModel vm = new(
        _ => Task.FromResult(Result.Failure<IReadOnlyList<SessionCatalogEntry>>(
            new DomainError("DbDown", "nope"))),
        () => new HashSet<AgentId>());
    await vm.LoadAsync().ConfigureAwait(true);

    Assert.False(string.IsNullOrEmpty(vm.LoadError));
    Assert.Empty(vm.Rows);
  }

  [Fact]
  public async Task Load_Runs_Only_Once()
  {
    int calls = 0;
    SessionsViewModel vm = new(
        _ =>
        {
          calls++;
          return Task.FromResult(Result.Success<IReadOnlyList<SessionCatalogEntry>>([]));
        },
        () => new HashSet<AgentId>());

    await vm.LoadAsync().ConfigureAwait(true);
    await vm.LoadAsync().ConfigureAwait(true);

    Assert.Equal(1, calls);
  }
}
