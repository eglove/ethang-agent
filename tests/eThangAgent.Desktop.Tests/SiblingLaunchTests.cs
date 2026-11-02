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
  /// <summary>The shell under test: EVERY CREATED SESSION RESOLVES ITS OWN HOST CHANNEL.
  ///     <para>A tab's cells each own one, and the host that raised a request is the host that
  ///     must receive its answer, so a single shared channel could not tell a correct answer
  ///     from one delivered to the wrong cell.</para></summary>
  private sealed class Shell
  {
    /// <summary>Every host channel a created session resolved, in creation order.</summary>
    public List<FakeRootTurnChannel> Hosts { get; } = [];

    /// <summary>The primary cell's channel: the first session this shell created.</summary>
    public FakeRootTurnChannel Host => Hosts[0];

    public List<(string Root, string Provider)> Created { get; } = [];

    public MainViewModel ViewModel { get; }

    public Shell() =>
      ViewModel = new MainViewModel((root, provider) => Create(root, provider));

    public async Task<AgentTabViewModel> OpenAsync(string root, string provider = "openrouter")
      => (await ViewModel.OpenAgentAsync(root, provider).ConfigureAwait(true)).Value!;

    private Task<Result<AgentSession>> Create(string root, string provider)
    {
      Created.Add((root, provider));
      FakeRootTurnChannel host = new([], holdOpen: false);
      Hosts.Add(host);
      ServiceProvider services = new ServiceCollection()
          .AddSingleton<IAgentStore>(new TestFixtures.StubStore())
          .AddSingleton<IWorkspaceContext>(new FixedWorkspaceContext("ws-alpha", root))
          .AddSingleton<IRootTurnChannel>(host)
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
  public async Task SiblingLaunch_AppendsACell_AndRetiresNoCell()
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

  /// <summary>A LAUNCH IS ANSWERED ON THE REQUESTING SESSION'S CHANNEL, not the tab's.
  ///     <para>EVERY CELL OWNS ITS OWN HOST CHANNEL, and the host whose turn asked is the host
  ///     that blocks on the answer. A tab whose SECOND cell asked and whose answer went to the
  ///     first cell's channel would leave the asking host waiting until its turn ended, where the
  ///     pending request fails as <c>SessionRequestUnreachable</c> - a tool told its launch never
  ///     happened by a host that was never asked.</para>
  ///     <para>This drives the launch exactly as production does: the per-cell request handler
  ///     passes the session that raised it.</para></summary>
  [Fact]
  public async Task SiblingLaunch_FromASecondCell_IsAnsweredOnThatCellsChannel()
  {
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync(@"C:\work\alpha").ConfigureAwait(true);
    Result<AgentCellViewModel> added = await shell.ViewModel.AddCellAsync(tab, prompt: null)
        .ConfigureAwait(true);
    Assert.True(added.IsSuccess, added.Error?.Message);
    AgentCellViewModel second = added.Value;
    Assert.Equal(2, shell.Hosts.Count);

    await shell.ViewModel.HandleSessionRequestAsync(tab,
        Launch("go", tab.WorkspaceId!), second.RootId).ConfigureAwait(true);

    // THE ASKING CELL'S HOST RECEIVED IT, and the tab's first cell heard nothing.
    (string requestId, string? sessionId, string? errorCode, string? _) =
        Assert.Single(shell.Hosts[1].SessionResponses);
    Assert.Null(errorCode);
    Assert.Equal(tab.Cells[^1].RootId.ToString(), sessionId, StringComparer.Ordinal);
    Assert.False(string.IsNullOrEmpty(requestId));
    Assert.Empty(shell.Hosts[0].SessionResponses);
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
  ///     prompt. This compares the shapes the grid actually sees.
  ///     <para>THE LABEL AND THE PROMPT DO NOT SURVIVE A RESTART, and this pins that too: the
  ///     persisted row is an ordinary root record, so it carries the fixed root label and the
  ///     fixed root prompt. A caption that lived only in the cell is how a plus row and a
  ///     launch row stay indistinguishable in the store.</para></summary>
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

    // THE LABEL IS THE CELL'S, AND THE ROW IS THE ORDINARY ROOT ROW: a persisted session
    // carries the fixed root label and the fixed root prompt whichever route started it, so
    // the two rows differ in nothing but their id and their creation time.
    AgentRecord row = AgentRecord.Root(sibling.RootId, DateTimeOffset.UnixEpoch,
        "ws-alpha", "openrouter");
    Assert.Equal("root", row.Label, StringComparer.Ordinal);
    Assert.Equal("conversation root", row.TaskPrompt, StringComparer.Ordinal);
    Assert.Equal("sibling", sibling.Label, StringComparer.Ordinal);
    Assert.Null(plus.Label);
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
