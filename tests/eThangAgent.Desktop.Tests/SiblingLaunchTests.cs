using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.StateDomain;
using eThangAgent.Transport.ACL;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop.Tests;

/// <summary>A model's own launch (issue #338): the app starts a SIBLING agent and appends its
///     cell, retiring nothing.
///     <para>THE ACCEPTANCE, AS BEHAVIOUR: the caller keeps its cell, its transcript, and its
///     liveness; the new agent arrives beside it in the same tab, with the label the caller
///     named; and the request is answered with the new session's id so the blocked tool
///     returns. A launch is emitted from INSIDE the caller's turn, so the test drives it
///     exactly there - a mid-turn launch that waited would deadlock and never answer.</para>
///     <para>THE PLUS ROUTE IS THE CONTROL: a sibling must produce the same shape as a cell the
///     user added by hand, which is what 'indistinguishable apart from timestamp, label, and
///     prompt' means operationally.</para></summary>
public class SiblingLaunchTests
{
  /// <summary>The shell under test: every created session resolves <see cref="Host"/>, so the
  ///     tab and its siblings can all answer requests.</summary>
  private sealed class Shell
  {
    public FakeRootTurnChannel Host { get; } = new([], holdOpen: false);

    public List<(string Root, string Provider)> Created { get; } = [];

    public MainViewModel ViewModel { get; }

    public Shell() =>
      ViewModel = new MainViewModel((root, provider) => Create(root, provider));

    public async Task<AgentTabViewModel> OpenAsync(string root, string provider = "openrouter")
      => (await ViewModel.OpenAgentAsync(root, provider).ConfigureAwait(true)).Value!;

    private Task<Result<AgentSession>> Create(string root, string provider)
    {
      Created.Add((root, provider));
      ServiceProvider services = new ServiceCollection()
          .AddSingleton<IAgentStore>(new TestFixtures.StubStore())
          .AddSingleton<IWorkspaceContext>(new FixedWorkspaceContext("ws-alpha", root))
          .AddSingleton<IRootTurnChannel>(Host)
          .BuildServiceProvider();
      return Task.FromResult(Result.Success(new AgentSession(
          services, AgentId.NewId(), new Conversation(), Handler: null!,
          Lifecycle: new RootSessionLifecycle(new TestFixtures.StubStore()),
          Model: ModelConfig.Create("test/model", 128, 0.1f, 8192).Value!,
          WorkspaceRoot: root, ProviderName: provider,
          Inbox: new BoundedAgentMailbox(),
          ChildRuntime: new TestFixtures.StubAgentRuntime())));
    }
  }

  private static SessionRequestPayload Launch(string prompt, string workspace, string? label = null)
      => new("req-" + Guid.NewGuid().ToString("N"), prompt, HandoffSummary: null)
      {
        SiblingOfWorkspaceId = workspace,
        Label = label,
      };

  [Fact]
  public async Task SiblingLaunch_AppendsACell_AndRetiresTheCallers()
  {
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync(@"C:\work\alpha").ConfigureAwait(true);
    AgentCellViewModel caller = Assert.Single(tab.Cells);
    caller.ViewModel.Transcript.AddUser("the caller's own work");

    await shell.ViewModel.HandleSessionRequestAsync(tab,
        Launch("audit the retry path", tab.WorkspaceId!, "auditor"), caller.RootId).ConfigureAwait(true);

    // TWO CELLS, and the CALLER'S is still the live one: a sibling supersedes nothing.
    Assert.Equal(2, tab.Cells.Count);
    Assert.Same(caller, tab.Cells[0]);
    Assert.False(caller.IsFinished);
    Assert.NotNull(caller.Container);
    // The caller's transcript survives, which is what 'retires nothing' means for the user.
    Assert.Equal("the caller's own work",
        Assert.Single(caller.ViewModel.Transcript.Entries.OfType<UserMessageEntry>()).Text);

    // The new agent arrived beside it, captioned with the label the caller named.
    AgentCellViewModel sibling = tab.Cells[1];
    Assert.NotEqual(caller.RootId, sibling.RootId);
    Assert.Equal("auditor", sibling.Title);
    Assert.Null(sibling.PlacementLine);
    Assert.NotSame(caller, sibling);
  }

  [Fact]
  public async Task SiblingLaunch_IsAnsweredWithTheNewAgentsId()
  {
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync(@"C:\work\alpha").ConfigureAwait(true);
    SessionRequestPayload request = Launch("go", tab.WorkspaceId!);

    await shell.ViewModel.HandleSessionRequestAsync(tab, request).ConfigureAwait(true);

    (string requestId, string? sessionId, string? errorCode, string? _) =
        Assert.Single(shell.Host.SessionResponses);
    Assert.Equal(request.RequestId, requestId, StringComparer.Ordinal);
    Assert.Null(errorCode);
    Assert.Equal(tab.Cells[1].RootId.ToString(), sessionId, StringComparer.Ordinal);
  }

  /// <summary>A SIBLING IS ANSWERED MID-TURN. The caller's turn is running when the launch is
  ///     emitted, so a path that waited for it to go idle would never answer and the calling
  ///     tool would time out. This pins that the answer lands with the turn still open.</summary>
  [Fact]
  public async Task SiblingLaunch_IssuedFromInsideARunningTurn_StillAnswers()
  {
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync(@"C:\work\alpha").ConfigureAwait(true);
    shell.Host.HoldOpen = true;
    Task turn = tab.ViewModel.SubmitAsync("hi");
    await shell.Host.TurnStarted.ConfigureAwait(true);

    await shell.ViewModel.HandleSessionRequestAsync(tab,
        Launch("go", tab.WorkspaceId!), tab.Container.RootId).ConfigureAwait(true);

    (string requester, string? sessionId, string? errorCode, string? error) =
        Assert.Single(shell.Host.SessionResponses);
    Assert.Null(errorCode);
    Assert.NotNull(sessionId);
    Assert.False(string.IsNullOrEmpty(requester));
    Assert.Null(error);

    // Release the turn so the test does not hang on the still-open host channel.
    tab.ViewModel.RequestStop();
    await turn.ConfigureAwait(true);
  }

  [Fact]
  public async Task SiblingLaunch_NamingAnotherWorkspace_IsRefused_AndStartsNothing()
  {
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync(@"C:\work\alpha").ConfigureAwait(true);

    await shell.ViewModel.HandleSessionRequestAsync(tab,
        Launch("go", "some-other-workspace")).ConfigureAwait(true);

    (string requestId, string? sessionId, string? errorCode, string? message) =
        Assert.Single(shell.Host.SessionResponses);
    Assert.Null(sessionId);
    Assert.Equal("SessionRequestWrongWorkspace", errorCode);
    Assert.False(string.IsNullOrEmpty(requestId));
    Assert.False(string.IsNullOrEmpty(message));
    _ = Assert.Single(tab.Cells); // nothing was appended
  }

  /// <summary>THE CONTROL: a sibling and a "+" cell differ only in their label, timestamp, and
  ///     prompt. This compares the shapes the grid actually sees.</summary>
  [Fact]
  public async Task SiblingCell_HasTheSameShapeAsAPlusCell()
  {
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync(@"C:\work\alpha").ConfigureAwait(true);
    Result<AgentCellViewModel> added = await shell.ViewModel.AddCellAsync(tab, prompt: null)
        .ConfigureAwait(true);
    Assert.True(added.IsSuccess, added.Error?.Message);
    AgentCellViewModel plus = added.Value;

    await shell.ViewModel.HandleSessionRequestAsync(tab,
        Launch("go", tab.WorkspaceId!, "sibling")).ConfigureAwait(true);

    AgentCellViewModel sibling = tab.Cells[^1];
    // Same kind of cell, same grid semantics, no placement, and a caption from its own name.
    Assert.False(sibling.RunsInOwnHost);
    Assert.Null(sibling.Placement);
    Assert.Null(plus.Placement);
    Assert.Equal("sibling", sibling.Title);
    Assert.Equal(plus.Container!.WorkspaceRoot, sibling.Container!.WorkspaceRoot,
        StringComparer.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task SiblingLaunch_OnASessionWithoutAHostChannel_ReportsLocally_AndCreatesNothing()
  {
    // A headless session has no channel, so no host can be waiting: the launch is a wiring
    // fault reported on the transcript rather than answered into the void.
    MainViewModel vm = new((root, provider) =>
    {
      ServiceProvider services = new ServiceCollection()
          .AddSingleton<IAgentStore>(new TestFixtures.StubStore())
          .BuildServiceProvider();
      return Task.FromResult(Result.Success(new AgentSession(
          services, AgentId.NewId(), new Conversation(), Handler: null!,
          Lifecycle: new RootSessionLifecycle(new TestFixtures.StubStore()),
          Model: ModelConfig.Create("test/model", 128, 0.1f, 8192).Value!,
          WorkspaceRoot: root, ProviderName: provider,
          Inbox: new BoundedAgentMailbox(),
          ChildRuntime: new TestFixtures.StubAgentRuntime())));
    });
    AgentTabViewModel tab = (await vm.OpenAgentAsync(@"C:\work\alpha", "openrouter").ConfigureAwait(true)).Value!;

    await vm.HandleSessionRequestAsync(tab, Launch("go", tab.WorkspaceId!)).ConfigureAwait(true);

    Assert.Contains(tab.ViewModel.Transcript.Notices,
        n => n.Contains("Error [SessionRequestUnroutable]", StringComparison.Ordinal));
    _ = Assert.Single(tab.Cells);
  }
}
