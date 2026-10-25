using eThangAgent.Desktop.ViewModels;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Desktop.Tests;

/// <summary>Issue #10: the workbench's GitHub view. Three things are contractual and each
///     gets its own group of tests: the three degradation states render their own actionable
///     message (never a blank pane, never a crash), the list/detail render what the seam
///     reports, and the slow poll runs ONLY while the pane is visible.</summary>
public sealed class PullRequestPaneViewModelTests
{
  private const string Workspace = @"C:\work\demo";

  private static readonly GitHubRepository Repo =
      new("eglove/ethang-agent", new Uri("https://github.com/eglove/ethang-agent"), "eglove/ethang-agent");

  private sealed class FakeGitHub : IGitHubAccess
  {
    public Result<GitHubRepository> Probe { get; set; } = Result.Success(Repo);

    public Result<IReadOnlyList<GitHubPullRequest>> List { get; set; } = Result.Success<IReadOnlyList<GitHubPullRequest>>([]);

    public Result<GitHubPullRequest> Detail { get; set; } =
        Result.Failure<GitHubPullRequest>(new DomainError("NotFound", "no detail configured"));

    public Result<GitHubPullRequest> Create { get; set; } =
        Result.Failure<GitHubPullRequest>(new DomainError("NotFound", "no create configured"));

    public int ProbeCalls { get; private set; }

    public int ListCalls { get; private set; }

    public List<int> DetailCalls { get; } = [];

    public List<GitHubCreatePullRequestRequest> CreateCalls { get; } = [];

    public Task<Result<GitHubRepository>> ProbeAsync(string workspacePath, CancellationToken ct = default)
    {
      ProbeCalls++;
      return Task.FromResult(Probe);
    }

    public Task<Result<IReadOnlyList<GitHubPullRequest>>> ListPullRequestsAsync(
        string workspacePath, string selector, CancellationToken ct = default)
    {
      ListCalls++;
      return Task.FromResult(List);
    }

    public Task<Result<GitHubPullRequest>> GetPullRequestAsync(
        string workspacePath, string selector, int number, CancellationToken ct = default)
    {
      DetailCalls.Add(number);
      return Task.FromResult(Detail);
    }

    public Task<Result<GitHubPullRequest>> CreatePullRequestAsync(
        string workspacePath, string selector, GitHubCreatePullRequestRequest request, CancellationToken ct = default)
    {
      CreateCalls.Add(request);
      return Task.FromResult(Create);
    }
  }

  private static GitHubPullRequest Pr(int number = 12, string title = "feat(desktop): add the GitHub view",
      string? review = "APPROVED", bool draft = false, int passing = 2, int failing = 0, int pending = 0) =>
      new(number, title, "eglove", "feature/github", "master",
          new Uri($"https://github.com/eglove/ethang-agent/pull/{number}"), draft, review,
          new GitHubCheckSummary(passing + failing + pending, passing, failing, pending),
          DateTimeOffset.UnixEpoch, "Closes #10");

  private static PullRequestPaneViewModel Build(FakeGitHub? github, TimeSpan? pollInterval = null) =>
      new(Workspace, github, render => render(), pollInterval: pollInterval ?? TimeSpan.FromHours(1));

  // ---- the three degradation states -------------------------------------------------

  [Fact]
  public async Task WhenGhIsNotInstalled_NamesTheInstallPath()
  {
    FakeGitHub github = new()
    {
      Probe = Result.Failure<GitHubRepository>(new DomainError("GhNotInstalled", "no gh")),
    };
    using PullRequestPaneViewModel vm = Build(github);

    await vm.RefreshAsync(TestContext.Current.CancellationToken);

    Assert.Equal(GitHubPaneState.NotInstalled, vm.State);
    Assert.Contains("gh", vm.EmptyReason!, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("cli.github.com", vm.EmptyReason!, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task WhenNoHostIsAuthenticated_NamesTheLoginCommand()
  {
    FakeGitHub github = new()
    {
      Probe = Result.Failure<GitHubRepository>(new DomainError("GhNotAuthenticated", "not logged in")),
    };
    using PullRequestPaneViewModel vm = Build(github);

    await vm.RefreshAsync(TestContext.Current.CancellationToken);

    Assert.Equal(GitHubPaneState.NotAuthenticated, vm.State);
    Assert.Contains("gh auth login", vm.EmptyReason!, StringComparison.Ordinal);
  }

  [Fact]
  public async Task WhenTheWorkspaceIsNotAGitHubRepository_Says_So()
  {
    FakeGitHub github = new()
    {
      Probe = Result.Failure<GitHubRepository>(new DomainError("NotAGitHubRepository", "no remote")),
    };
    using PullRequestPaneViewModel vm = Build(github);

    await vm.RefreshAsync(TestContext.Current.CancellationToken);

    Assert.Equal(GitHubPaneState.NotAGitHubRepository, vm.State);
    Assert.Contains("not a GitHub repository", vm.EmptyReason!, StringComparison.OrdinalIgnoreCase);
  }

  /// <summary>The three states are DISTINCT: a pane that collapsed them would tell a user
  ///     with an unauthenticated gh to install it.</summary>
  [Fact]
  public async Task TheThreeDegradationMessages_AreAllDifferent()
  {
    List<string?> messages = [];
    foreach (string code in new[] { "GhNotInstalled", "GhNotAuthenticated", "NotAGitHubRepository" })
    {
      FakeGitHub github = new() { Probe = Result.Failure<GitHubRepository>(new DomainError(code, "x")) };
      using PullRequestPaneViewModel vm = Build(github);
      await vm.RefreshAsync(TestContext.Current.CancellationToken);
      messages.Add(vm.EmptyReason);
    }

    Assert.Equal(3, messages.Distinct(StringComparer.Ordinal).Count());
  }

  // ---- the list and the detail -------------------------------------------------------

  [Fact]
  public async Task WhenReady_RendersEachPrWithNumberTitleAuthorReviewAndChecks()
  {
    FakeGitHub github = new()
    {
      List = Result.Success<IReadOnlyList<GitHubPullRequest>>([
          Pr(12, "feat(desktop): add the GitHub view", "APPROVED", passing: 3, failing: 1),
          Pr(13, "docs: readme", review: null, passing: 0),
      ]),
    };
    using PullRequestPaneViewModel vm = Build(github);

    await vm.RefreshAsync(TestContext.Current.CancellationToken);

    Assert.Equal(GitHubPaneState.Ready, vm.State);
    Assert.Equal(2, vm.PullRequests.Count);
    PullRequestRowViewModel first = vm.PullRequests[0];
    Assert.Equal(12, first.Number);
    Assert.Equal("#12 \u00B7 feat(desktop): add the GitHub view", first.Display);
    Assert.Equal("@eglove", first.AuthorDisplay);
    Assert.Equal("approved", first.ReviewDisplay);
    Assert.Equal("3 passing, 1 failing", first.ChecksDisplay);
    // A PR nobody has reviewed says so, rather than looking approved.
    Assert.Equal("no review yet", vm.PullRequests[1].ReviewDisplay);
  }

  [Fact]
  public async Task WhenAReadyRepositoryHasNoOpenPrs_TheEmptyStateSaysSo()
  {
    FakeGitHub github = new();
    using PullRequestPaneViewModel vm = Build(github);

    await vm.RefreshAsync(TestContext.Current.CancellationToken);

    Assert.Equal(GitHubPaneState.Ready, vm.State);
    Assert.True(vm.IsEmpty);
    Assert.Contains("no open pull requests", vm.EmptyReason!, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task SelectingAPr_LoadsItsBodyIntoTheDetail()
  {
    FakeGitHub github = new()
    {
      List = Result.Success<IReadOnlyList<GitHubPullRequest>>([Pr(12)]),
      Detail = Result.Success(Pr(12)),
    };
    using PullRequestPaneViewModel vm = Build(github);
    await vm.RefreshAsync(TestContext.Current.CancellationToken);

    await vm.SelectAsync(12, TestContext.Current.CancellationToken);

    Assert.Equal([12], github.DetailCalls);
    Assert.NotNull(vm.Selected);
    Assert.Equal(12, vm.Selected.Number);
    Assert.Contains("Closes #10", vm.SelectedBody, StringComparison.Ordinal);
  }

  [Fact]
  public async Task SelectingTheSamePrAgain_DoesNotReReadIt()
  {
    FakeGitHub github = new()
    {
      List = Result.Success<IReadOnlyList<GitHubPullRequest>>([Pr(12)]),
      Detail = Result.Success(Pr(12)),
    };
    using PullRequestPaneViewModel vm = Build(github);
    await vm.RefreshAsync(TestContext.Current.CancellationToken);
    await vm.SelectAsync(12, TestContext.Current.CancellationToken);

    await vm.SelectAsync(12, TestContext.Current.CancellationToken);

    _ = Assert.Single(github.DetailCalls);
  }

  // ---- creating a PR ----------------------------------------------------------------

  [Fact]
  public async Task CreatingAPr_SeedsTheBranchFieldsFromTheCurrentBranch_AndSendsThemVerbatim()
  {
    FakeGitHub github = new()
    {
      List = Result.Success<IReadOnlyList<GitHubPullRequest>>([]),
      Create = Result.Success(Pr(12, "my change")),
    };
    using PullRequestPaneViewModel vm = Build(github);
    await vm.RefreshAsync(TestContext.Current.CancellationToken);

    vm.BeginCreate("feature/github", "master");
    Assert.Equal("feature/github", vm.NewHeadBranch);
    Assert.Equal("master", vm.NewBaseBranch);
    Assert.True(vm.IsCreating);

    vm.NewTitle = "my change";
    vm.NewBody = "body";
    await vm.SubmitCreateAsync(TestContext.Current.CancellationToken);

    GitHubCreatePullRequestRequest sent = Assert.Single(github.CreateCalls);
    Assert.Equal("my change", sent.Title);
    Assert.Equal("body", sent.Body);
    Assert.Equal("feature/github", sent.HeadBranch);
    Assert.Equal("master", sent.BaseBranch);
    Assert.False(sent.Draft);
  }

  [Fact]
  public async Task CreatingAPr_WithATitleIsRequired()
  {
    FakeGitHub github = new();
    using PullRequestPaneViewModel vm = Build(github);
    await vm.RefreshAsync(TestContext.Current.CancellationToken);

    vm.BeginCreate("feature/github", "master");
    vm.NewTitle = "   ";
    await vm.SubmitCreateAsync(TestContext.Current.CancellationToken);

    Assert.Empty(github.CreateCalls);
    Assert.Contains("title", vm.CreateError!, StringComparison.OrdinalIgnoreCase);
    Assert.True(vm.IsCreating, "a refused submit must keep the form open so the text is not lost");
  }

  [Fact]
  public async Task CreatingAPr_WhenTheCliRefuses_KeepsTheFormOpenAndShowsTheCliMessage()
  {
    FakeGitHub github = new()
    {
      Create = Result.Failure<GitHubPullRequest>(new DomainError("GitHubError",
          "a pull request for branch \"feature/github\" into branch \"master\" already exists")),
    };
    using PullRequestPaneViewModel vm = Build(github);
    await vm.RefreshAsync(TestContext.Current.CancellationToken);

    vm.BeginCreate("feature/github", "master");
    vm.NewTitle = "t";
    await vm.SubmitCreateAsync(TestContext.Current.CancellationToken);

    Assert.True(vm.IsCreating);
    Assert.Contains("already exists", vm.CreateError!, StringComparison.Ordinal);
  }

  [Fact]
  public async Task CreatingAPr_OnSuccess_ClosesTheFormAndRefreshesTheList()
  {
    FakeGitHub github = new()
    {
      List = Result.Success<IReadOnlyList<GitHubPullRequest>>([]),
      Create = Result.Success(Pr(12, "created")),
    };
    using PullRequestPaneViewModel vm = Build(github);
    await vm.RefreshAsync(TestContext.Current.CancellationToken);
    int listCallsBefore = github.ListCalls;

    github.List = Result.Success<IReadOnlyList<GitHubPullRequest>>([Pr(12, "created")]);
    vm.BeginCreate("feature/github", "master");
    vm.NewTitle = "created";
    await vm.SubmitCreateAsync(TestContext.Current.CancellationToken);

    Assert.False(vm.IsCreating);
    Assert.Null(vm.CreateError);
    Assert.True(github.ListCalls > listCallsBefore, "the new PR must appear without a manual refresh");
  }

  // ---- polling ----------------------------------------------------------------------

  /// <summary>READ-ONLY while hidden: a pane nobody is looking at runs no subprocess. The
  ///     refresh path is what starts the timer, and Stop must end it.</summary>
  [Fact]
  public async Task Polling_RunsOnlyWhileVisible_AndStopsWhenHidden()
  {
    FakeGitHub github = new();
    using PullRequestPaneViewModel vm = Build(github, TimeSpan.FromMilliseconds(20));

    vm.Start();
    await vm.RefreshAsync(TestContext.Current.CancellationToken);
    await WaitUntilAsync(() => github.ListCalls >= 2, TestContext.Current.CancellationToken);
    int whileVisible = github.ListCalls;

    vm.Stop();

    // A read started before Stop may still land (the fake does not observe the token), so the
    // assertion samples AFTER things settle and then proves the count stops MOVING: two
    // consecutive windows with no new call is what 'polling stopped' means.
    await Task.Delay(150, TestContext.Current.CancellationToken);
    int afterStop = github.ListCalls;
    await Task.Delay(200, TestContext.Current.CancellationToken);

    Assert.True(github.ListCalls == afterStop,
        $"polling must stop when the pane is hidden (saw {afterStop} then {github.ListCalls})");
    Assert.True(whileVisible >= 2, "the poll must actually have ticked while the pane was visible");
  }

  [Fact]
  public async Task Start_IsIdempotent_SoATabSwitchDoesNotStackPollers()
  {
    FakeGitHub github = new();
    using PullRequestPaneViewModel vm = Build(github, TimeSpan.FromMilliseconds(20));

    vm.Start();
    vm.Start();
    await vm.RefreshAsync(TestContext.Current.CancellationToken);
    await Task.Delay(120, TestContext.Current.CancellationToken);
    vm.Stop();
    await Task.Delay(120, TestContext.Current.CancellationToken);
    int afterStop = github.ListCalls;
    await Task.Delay(200, TestContext.Current.CancellationToken);

    Assert.True(github.ListCalls == afterStop,
        $"a doubled Start() must not leave a second poller running (saw {afterStop} then {github.ListCalls})");
  }

  /// <summary>A probe failure must NOT be retried every tick as if it were data: the
  ///     degradation state is stable until the user acts, and each tick costs a subprocess.
  ///     The pane does re-probe on the next explicit refresh.</summary>
  [Fact]
  public async Task WhenDegraded_PollingDoesNotRerunTheProbeEveryTick()
  {
    FakeGitHub github = new()
    {
      Probe = Result.Failure<GitHubRepository>(new DomainError("GhNotInstalled", "no gh")),
    };
    using PullRequestPaneViewModel vm = Build(github, TimeSpan.FromMilliseconds(20));

    vm.Start();
    await vm.RefreshAsync(TestContext.Current.CancellationToken);
    int afterFirst = github.ProbeCalls;
    await Task.Delay(120, TestContext.Current.CancellationToken);
    vm.Stop();

    Assert.Equal(afterFirst, github.ProbeCalls);
  }

  [Fact]
  public async Task ARepositoryThatTurnsReady_ClearsTheDegradationOnTheNextRefresh()
  {
    FakeGitHub github = new()
    {
      Probe = Result.Failure<GitHubRepository>(new DomainError("GhNotAuthenticated", "nope")),
    };
    using PullRequestPaneViewModel vm = Build(github);
    await vm.RefreshAsync(TestContext.Current.CancellationToken);
    Assert.Equal(GitHubPaneState.NotAuthenticated, vm.State);

    github.Probe = Result.Success(Repo);
    await vm.RefreshAsync(TestContext.Current.CancellationToken);

    Assert.Equal(GitHubPaneState.Ready, vm.State);
    // Ready with no open PRs is the EMPTY state, not a degradation: the message changes from
    // 'run gh auth login' to a plain statement about the repository.
    Assert.True(vm.IsEmpty);
    Assert.Contains("No open pull requests", vm.EmptyReason!, StringComparison.Ordinal);
    Assert.DoesNotContain("auth login", vm.EmptyReason!, StringComparison.Ordinal);
  }

  [Fact]
  public async Task WithNoSeamWired_ThePaneReportsUnavailable_RatherThanStayingBlank()
  {
    using PullRequestPaneViewModel vm = Build(null);

    await vm.RefreshAsync(TestContext.Current.CancellationToken);
    vm.Start();
    vm.Stop();

    Assert.Equal(GitHubPaneState.Unavailable, vm.State);
    Assert.False(string.IsNullOrWhiteSpace(vm.EmptyReason));
  }

  private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
  {
    for (int i = 0; i < 500 && !condition(); i++)
    {
      await Task.Delay(10, ct).ConfigureAwait(false);
    }
  }
}
