using eThangAgent.Agent.Application.Sessions;
using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop.Tests;

/// <summary>The Sessions catalog as one window sees it: the shell states its workspace once,
///     and the store query is scoped to it, so a session belonging to another workspace is
///     never loaded and cannot reach the dialog. The same catalog handler serves every window
///     in the process.</summary>
public class SessionCatalogScopeTests
{
  private const string Alpha = @"C:\work\alpha";
  private const string Beta = @"C:\work\beta";

  private static readonly DateTimeOffset Started =
      new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

  /// <summary>A shell bound to one workspace, holding a catalog over a store that serves
  ///     both workspaces' rows.</summary>
  private static MainViewModel CreateShell(string? workspaceRoot, params AgentRecord[] records)
  {
    static Task<Result<AgentSession>> create(string root, string provider)
        => Task.FromResult(Result.Success(FakeSession(root, provider)));
    return new MainViewModel(create, new MainViewModelOptions
    {
      WorkspaceRoot = workspaceRoot,
      SessionCatalog = new SessionCatalogQueryHandler(new TestFixtures.ListAgentStore(records)),
    });
  }

  private static AgentSession FakeSession(string root, string provider)
  {
    ServiceProvider services = new ServiceCollection()
        .AddSingleton<IAgentStore>(new TestFixtures.StubStore())
        .BuildServiceProvider();
    return new AgentSession(
        services,
        AgentId.NewId(),
        new Conversation(),
        Handler: null!,
        Lifecycle: new RootSessionLifecycle(new TestFixtures.StubStore()),
        Model: ModelConfig.Create("test/model", 128, 0.1f, 8192).Value!,
        WorkspaceRoot: root,
        ProviderName: provider,
        Inbox: new BoundedAgentMailbox(),
        ChildRuntime: new TestFixtures.StubAgentRuntime(),
        Preferences: null);
  }

  /// <summary>Three alpha sessions and two beta ones; beta's is the newest row in the store.</summary>
  private static AgentRecord[] TwoWorkspaces()
  {
    AgentRecord[] records =
    [
        AgentRecord.Root(AgentId.NewId(), Started, Alpha, "openrouter"),
        AgentRecord.Root(AgentId.NewId(), Started.AddMinutes(1), Alpha, "openrouter"),
        AgentRecord.Root(AgentId.NewId(), Started.AddMinutes(2), Alpha, "openrouter"),
        AgentRecord.Root(AgentId.NewId(), Started.AddMinutes(3), Beta, "openrouter"),
        AgentRecord.Root(AgentId.NewId(), Started.AddMinutes(9), Beta, "openrouter"),
    ];
    return records;
  }

  [Fact]
  public async Task Shell_Bound_To_Alpha_Lists_Only_Alpha_Sessions()
  {
    AgentRecord[] records = TwoWorkspaces();
    MainViewModel shell = CreateShell(Alpha, records);
    HashSet<Guid> betaIds = [.. records
        .Where(record => record.WorkspaceId == Beta)
        .Select(record => record.Id.Value)];

    Result<IReadOnlyList<SessionCatalogEntry>> listed =
        await shell.SessionCatalogLoader!(CancellationToken.None).ConfigureAwait(true);

    Assert.True(listed.IsSuccess);
    Assert.Equal(3, listed.Value.Count);
    Assert.All(listed.Value, entry => Assert.Equal(Alpha, entry.WorkspaceId));
    Assert.DoesNotContain(listed.Value, entry => betaIds.Contains(entry.Id.Value));
  }

  /// <summary>Two windows in one process list two different workspaces from one handler.</summary>
  [Fact]
  public async Task Two_Shells_Over_One_Catalog_List_Their_Own_Workspace()
  {
    AgentRecord[] records = TwoWorkspaces();

    Result<IReadOnlyList<SessionCatalogEntry>> alpha =
        await CreateShell(Alpha, records).SessionCatalogLoader!(CancellationToken.None)
            .ConfigureAwait(true);
    Result<IReadOnlyList<SessionCatalogEntry>> beta =
        await CreateShell(Beta, records).SessionCatalogLoader!(CancellationToken.None)
            .ConfigureAwait(true);

    Assert.True(alpha.IsSuccess);
    Assert.True(beta.IsSuccess);
    Assert.Equal(3, alpha.Value.Count);
    Assert.Equal(2, beta.Value.Count);
    Assert.All(beta.Value, entry => Assert.Equal(Beta, entry.WorkspaceId));
  }

  /// <summary>A shell with no workspace still offers the loader, and the dialog reports the
  ///     scope refusal rather than the shell claiming the catalog is unavailable.</summary>
  [Fact]
  public async Task Shell_With_No_Workspace_Refuses_Rather_Than_Listing_Everything()
  {
    MainViewModel shell = CreateShell(null, TwoWorkspaces());

    Result<IReadOnlyList<SessionCatalogEntry>> listed =
        await shell.SessionCatalogLoader!(CancellationToken.None).ConfigureAwait(true);

    Assert.False(listed.IsSuccess);
    Assert.Equal("InvalidWorkspace", listed.Error.Code);
  }

  [Fact]
  public async Task Shell_Without_A_Catalog_Reports_It_Unavailable()
  {
    static Task<Result<AgentSession>> create(string root, string provider)
        => Task.FromResult(Result.Success(FakeSession(root, provider)));
    MainViewModel shell = new(create, new MainViewModelOptions { WorkspaceRoot = Alpha });

    Assert.Null(shell.SessionCatalogLoader);
    await Task.CompletedTask.ConfigureAwait(true);
  }
}
