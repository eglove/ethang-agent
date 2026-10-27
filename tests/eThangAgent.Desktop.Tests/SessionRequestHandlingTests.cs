using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Transport.ACL;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop.Tests;

/// <summary>The shell's session-request handling (issue #234 task 7): the host's
///     new_session tool and the compaction handoff both emit a <c>session-request</c> and
///     then BLOCK awaiting the app's answer, so the shell must answer exactly once — the new
///     session's id on success, a typed refusal on every failure — and swap the tab only
///     once the requesting turn has gone idle.
/// <para>The answer travels back over the SAME channel the turn arrived on
///     (<see cref="IRootTurnChannel.SendSessionResponse"/>), which is why the request id
///     rides the handler seam. A request left unanswered is a tool waiting out its whole
///     budget for an answer that was never coming.</para></summary>
public class SessionRequestHandlingTests
{

  /// <summary>The shell under test: every created session resolves <see cref="Host"/>, so
  ///     both the original tab and its replacement can answer requests.</summary>
  private sealed class Shell
  {
    public FakeRootTurnChannel Host { get; } = new([], holdOpen: false);

    public List<(string Root, string Provider, IReadOnlyList<Message>? Seed)> Created { get; } = [];

    /// <summary>Creation calls from this index onward (0-based) fail.</summary>
    public int? FailFromCall { get; set; }

    public MainViewModel ViewModel { get; }

    public Shell()
    {
      ViewModel = new MainViewModel(
          (root, provider) => Create(root, provider, seed: null),
          new MainViewModelOptions
          {
            SeededSessionCreator = (root, provider, seed) => Create(root, provider, seed),
          });
    }

    public async Task<AgentTabViewModel> OpenAsync(string root, string provider = "openrouter")
      => (await ViewModel.OpenAgentAsync(root, provider).ConfigureAwait(true)).Value!;

    private Task<Result<AgentSession>> Create(string root, string provider, IReadOnlyList<Message>? seed)
    {
      Created.Add((root, provider, seed));
      return Task.FromResult(FailFromCall is { } from && Created.Count > from
          ? Result.Failure<AgentSession>(new DomainError("HostUnavailable", "no host"))
          : Result.Success(Session(root, provider)));
    }

    /// <summary>A session whose container resolves this shell's host channel — the shell
    ///     reads it off the container exactly as production does, so the answer path under
    ///     test is the real one.</summary>
    private AgentSession Session(string root, string provider)
    {
      ServiceProvider services = new ServiceCollection()
          .AddSingleton<IAgentStore>(new TestFixtures.StubStore())
          .AddSingleton<IRootTurnChannel>(Host)
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
  }

  private static SessionRequestPayload Request(string prompt, string? handoffSummary = null)
      => new("req-" + Guid.NewGuid().ToString("N"), prompt, handoffSummary);

  [Fact]
  public async Task SessionRequest_AnswersWithTheNewSessionId_AndSwapsTheTab()
  {
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync(@"C:\work\alpha").ConfigureAwait(true);
    SessionRequestPayload request = Request("explore the auth module");

    await shell.ViewModel.HandleSessionRequestAsync(tab, request).ConfigureAwait(true);

    // The new session exists, the tab was replaced in place, and the request was answered
    // with the id the host is blocked on.
    Assert.Equal(2, shell.Created.Count);
    AgentTabViewModel fresh = Assert.Single(shell.ViewModel.Tabs);
    Assert.NotSame(tab, fresh);
    Assert.Equal(@"C:\work\alpha", fresh.Container.WorkspaceRoot, StringComparer.OrdinalIgnoreCase);

    (string requestId, string? sessionId, string? errorCode, string? errorMessage) =
        Assert.Single(shell.Host.SessionResponses);
    Assert.Equal(request.RequestId, requestId, StringComparer.Ordinal);
    Assert.Equal(fresh.Container.RootId.ToString(), sessionId, StringComparer.Ordinal);
    Assert.Null(errorCode);
    Assert.Null(errorMessage);
  }

  [Fact]
  public async Task SessionRequest_WithAHandoffSummary_SeedsTheNewSession()
  {
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync(@"C:\work\alpha").ConfigureAwait(true);

    await shell.ViewModel.HandleSessionRequestAsync(tab, Request("", "the dense summary")).ConfigureAwait(true);

    (string _, string _, IReadOnlyList<Message>? seed) = shell.Created[^1];
    Message seeded = Assert.Single(seed!);
    Assert.True(seeded.IsSummary);
    Assert.Contains("the dense summary", seeded.Content, StringComparison.Ordinal);
    // Seed-only launch: an empty prompt means NO first turn is fired — a handoff continues
    // from the summary rather than re-asking.
    Assert.False(Assert.Single(shell.ViewModel.Tabs).ViewModel.IsBusy);
  }

  [Fact]
  public async Task SessionRequest_LaunchFailure_IsAnsweredWithItsTypedCode()
  {
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync(@"C:\work\alpha").ConfigureAwait(true);
    shell.FailFromCall = 1;

    await shell.ViewModel.HandleSessionRequestAsync(tab, Request("go")).ConfigureAwait(true);

    // The refusal MUST reach the host: an unanswered request is a tool waiting out its
    // budget for an answer that was never coming.
    (string _, string? sessionId, string? errorCode, string? errorMessage) =
        Assert.Single(shell.Host.SessionResponses);
    Assert.Null(sessionId);
    Assert.Equal("HostUnavailable", errorCode);
    Assert.False(string.IsNullOrWhiteSpace(errorMessage));
    // The tab the user was reading survives the failed launch.
    Assert.Same(tab, Assert.Single(shell.ViewModel.Tabs));
  }

  [Fact]
  public async Task SessionRequest_ForAClosedTab_IsAnsweredTabClosed()
  {
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync(@"C:\work\alpha").ConfigureAwait(true);
    await shell.ViewModel.CloseTabAsync(tab).ConfigureAwait(true);

    await shell.ViewModel.HandleSessionRequestAsync(tab, Request("go")).ConfigureAwait(true);

    (string _, string? sessionId, string? errorCode, string? _) =
        Assert.Single(shell.Host.SessionResponses);
    Assert.Null(sessionId);
    Assert.Equal("TabClosed", errorCode);
    Assert.Empty(shell.ViewModel.Tabs);
    _ = Assert.Single(shell.Created); // nothing was created for a tab that is gone
  }

  [Fact]
  public async Task SessionRequest_WhileAnotherIsPending_IsRefused_NotQueued()
  {
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync(@"C:\work\alpha").ConfigureAwait(true);

    // A REAL turn in flight is what holds the first request's swap open: the host emits a
    // session-request from INSIDE its turn, so the replacement must wait for that turn to go
    // idle. That pending window is what a second request must not race into — two launches
    // for one tab slot would leave one session alive in no tab at all.
    shell.Host.HoldOpen = true;
    Task turn = tab.ViewModel.SubmitAsync("hi");
    await shell.Host.TurnStarted.ConfigureAwait(true);

    Task first = shell.ViewModel.HandleSessionRequestAsync(tab, Request("first"));
    await shell.Host.FirstSessionResponse.ConfigureAwait(true);
    await shell.ViewModel.HandleSessionRequestAsync(tab, Request("second")).ConfigureAwait(true);

    Assert.Equal(2, shell.Host.SessionResponses.Count);
    (string _, string? firstSessionId, string? firstError, string? _) = shell.Host.SessionResponses[0];
    Assert.NotNull(firstSessionId);
    Assert.Null(firstError);
    (string _, string? secondSessionId, string? secondError, string? _) = shell.Host.SessionResponses[1];
    Assert.Null(secondSessionId);
    Assert.Equal("SessionRequestPending", secondError);

    // Release the turn so the first request's swap completes instead of hanging the test.
    tab.ViewModel.RequestStop();
    await turn.ConfigureAwait(true);
    await first.ConfigureAwait(true);
  }

  [Fact]
  public async Task SessionRequest_OnASessionWithoutAHostChannel_ReportsLocally_AndCreatesNothing()
  {
    // A headless/in-process session has no channel, so no host can be waiting: the request is
    // a wiring fault, reported on the transcript rather than answered into the void.
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

    await vm.HandleSessionRequestAsync(tab, Request("go")).ConfigureAwait(true);

    Assert.Contains(tab.ViewModel.Transcript.Notices,
        n => n.Contains("Error [SessionRequestUnroutable]", StringComparison.Ordinal));
    Assert.Same(tab, Assert.Single(vm.Tabs));
  }
}
