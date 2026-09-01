using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop.Tests;

/// <summary>The Links dialog's resolve"invoke chain in the REAL shell: a tab's
///     container must reach the dialog with its OWN registry (the consent object
///     agent.route resolves through) and a store-backed candidate loader — the same
///     wiring class of defect the handoff's ledger lessons describe (a library
///     without a door). Registry and store are real domain objects; only the rows
///     are faked.</summary>
public class LinksDialogWiringTests
{
  [Fact]
  public async Task Shell_Exposes_Selected_Tab_Registry_And_Store_Back_Loader()
  {
    AgentLinkRegistry registry = new();
    AgentId rootId = AgentId.NewId();
    MainViewModel shell = await MainViewModel.ForPrebuiltSessionAsync(
        Session(rootId, registry, new TestFixtures.ListAgentStore([Record(rootId)]))).ConfigureAwait(true);

    Assert.Same(registry, shell.SelectedLinksRegistry!);
    Assert.NotNull(shell.LinksCatalogLoader);
    Result<IReadOnlyList<AgentRecord>> listed = await shell.LinksCatalogLoader(CancellationToken.None).ConfigureAwait(true);
    Assert.True(listed.IsSuccess);
    AgentId listedId = Assert.Single(listed.Value).Id;
    Assert.Equal(rootId, listedId);
  }

  [Fact]
  public async Task Dialog_Built_From_Shell_Surface_Creates_A_Real_Routable_Link()
  {
    AgentLinkRegistry registry = new();
    AgentId rootId = AgentId.NewId();
    MainViewModel shell = await MainViewModel.ForPrebuiltSessionAsync(
        Session(rootId, registry, new TestFixtures.ListAgentStore([Record(rootId)]))).ConfigureAwait(true);

    // The LinksWindow's exact construction: shell loader + shell registry.
    LinksViewModel dialog = new(shell.LinksCatalogLoader!, shell.SelectedLinksRegistry!);
    await dialog.LoadAsync().ConfigureAwait(true);
    dialog.SelectedCandidate = dialog.Candidates[0];
    dialog.LinkName = "peer";

    dialog.LinkCommand.Execute(null);

    // The link is live on the SAME registry the session's capability provider
    // resolves agent.route names through — door and road are one object.
    Result<LinkAddress> resolved = registry.Resolve("peer");
    Assert.True(resolved.IsSuccess, resolved.Error?.Message ?? "not linked");
    Assert.Equal(rootId.Value.ToString("D"), resolved.Value.AgentAddress);
  }

  [Fact]
  public void Shell_Without_A_Tab_Exposes_Nothing()
  {
    MainViewModel vm = new((_, _) => Task.FromResult(Result.Failure<AgentSession>(
        new DomainError("NoFactory", "unused"))));
    Assert.Null(vm.SelectedLinksRegistry);
    Assert.Null(vm.LinksCatalogLoader);
  }

  private static AgentRecord Record(AgentId id) => AgentRecord.Spawned(
      id, null, 0, "test/model", null, "prompt",
      new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero));

  private static AgentSession Session(AgentId rootId, AgentLinkRegistry registry, IAgentStore store)
  {
    ServiceProvider services = new ServiceCollection()
        .AddSingleton(registry)
        .AddSingleton(store)
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
        ClarifyChannel: null!,
        Inbox: new BoundedAgentMailbox(),
        ChildRuntime: new TestFixtures.StubAgentRuntime());
  }
}
