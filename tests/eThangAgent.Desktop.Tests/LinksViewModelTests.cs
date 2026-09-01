using eThangAgent.AgentDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop.Tests;

/// <summary>Behavior of the Links dialog view-model: candidate listing, link
///     creation through the consented registry door, live-link listing, revocation,
///     and the confirm guards. The registry is the REAL domain object — the door
///     must exercise the consent contract it enforces (D10), never a fake.</summary>
public class LinksViewModelTests
{
  private static AgentRecord Record(AgentId id, string label = "root", int depth = 0, DateTimeOffset? createdAt = null)
    => AgentRecord.Spawned(id, null, depth, "test/model", label, "prompt",
        createdAt ?? new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero));

  private static LinksViewModel CreateVm(
      IReadOnlyList<AgentRecord> candidates, AgentLinkRegistry? registry = null)
    => new(_ => Task.FromResult(Result.Success(candidates)), registry ?? new AgentLinkRegistry());

  private static async Task<LinksViewModel> LoadVmAsync(
      IReadOnlyList<AgentRecord> candidates, AgentLinkRegistry? registry = null)
  {
    LinksViewModel vm = CreateVm(candidates, registry);
    await vm.LoadAsync().ConfigureAwait(true);
    return vm;
  }

  [Fact]
  public async Task Load_Lists_Candidates_And_Live_Links_Newest_First()
  {
    AgentLinkRegistry registry = new();
    AgentId older = AgentId.NewId();
    AgentId newer = AgentId.NewId();
    _ = registry.Link("beta", "c", newer.Value.ToString("D"), consented: true);
    // Ensure an ordering gap so 'beta' is unambiguously newer.
    await Task.Delay(5, TestContext.Current.CancellationToken).ConfigureAwait(true);
    _ = registry.Link("alpha", "c", older.Value.ToString("D"), consented: true);

    LinksViewModel vm = await LoadVmAsync(
        [Record(older, createdAt: Started), Record(newer, createdAt: Started.AddMinutes(5))],
        registry);

    // Candidate rows: the store returns them oldest-first; the view lists newest first.
    Assert.Equal(2, vm.Candidates.Count);
    Assert.Equal(newer, vm.Candidates[0].Id);
    Assert.Equal(older, vm.Candidates[1].Id);
    // Live links: the registry's own newest-first snapshot order.
    Assert.Equal(2, vm.Links.Count);
    Assert.Equal("alpha", vm.Links[0].Name);
    Assert.Equal("beta", vm.Links[1].Name);
  }

  [Fact]
  public async Task Link_Creates_A_Consented_Link_And_Refreshes_The_List()
  {
    AgentLinkRegistry registry = new();
    AgentId target = AgentId.NewId();
    LinksViewModel vm = await LoadVmAsync([Record(target)], registry);
    vm.SelectedCandidate = vm.Candidates[0];
    vm.LinkName = "peer";

    vm.LinkCommand.Execute(null);

    Assert.True(vm.LinkError is null, $"unexpected link error: {vm.LinkError}");
    // The link is REAL: route through the same registry resolves it.
    Result<LinkAddress> resolved = registry.Resolve("peer");
    Assert.True(resolved.IsSuccess);
    Assert.Equal(target.Value.ToString("D"), resolved.Value.AgentAddress);
    // The live-links list refreshed to include the new link.
    LinkRow row = Assert.Single(vm.Links);
    Assert.Equal("peer", row.Name);
    // The name field resets after a successful link.
    Assert.Equal(string.Empty, vm.LinkName);
  }

  [Fact]
  public async Task Link_Without_Name_Or_Selection_Fails_Structured_Not_Throws()
  {
    AgentLinkRegistry registry = new();
    LinksViewModel vm = await LoadVmAsync([Record(AgentId.NewId())], registry);

    // No name typed: the command is guarded off; direct execution still fails structured.
    Assert.False(vm.LinkCommand.CanExecute(null));
    vm.LinkName = "   ";
    vm.LinkCommand.Execute(null);
    Assert.Contains("name", vm.LinkError, StringComparison.OrdinalIgnoreCase);

    vm.LinkName = "peer";
    vm.LinkCommand.Execute(null);
    Assert.Null(vm.SelectedCandidate);
    Assert.NotNull(vm.LinkError);
    Assert.Contains("agent", vm.LinkError, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task Revoke_Removes_The_Link_From_The_Registry_And_The_List()
  {
    AgentLinkRegistry registry = new();
    _ = registry.Link("peer", "c", AgentId.NewId().Value.ToString("D"), consented: true);
    LinksViewModel vm = await LoadVmAsync([Record(AgentId.NewId())], registry);
    vm.SelectedLink = vm.Links[0];

    vm.RevokeCommand.Execute(null);

    Result<bool> secondRevoke = registry.Revoke("peer");
    Assert.False(secondRevoke.IsSuccess); // already gone
    Assert.Equal("NotFound", secondRevoke.Error.Code);
    Assert.Empty(vm.Links);
  }

  private static DateTimeOffset Started => new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);
}
