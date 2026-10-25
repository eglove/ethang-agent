using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.FileSystem.ACL.Tests;

/// <summary>Issue #10 decical D6: the GitHub seam is driven by shelling out to the
///     <c>gh</c> CLI. These tests drive the adapter over a RECORDED runner, so they pin
///     the argv the CLI is handed, the JSON it answers with, and the mapping of every
///     failure fingerprint onto the three named degradation codes - without needing gh
///     installed, authenticated, or online.</summary>
// JSON002 fires only in the format/IDE host; the fixtures below are deliberately JSON -
// they are exactly what the gh CLI hands the adapter.
#pragma warning disable JSON002 // Probable JSON string detected
public sealed class GhCliAccessTests
{
  private const string Workspace = @"C:\work\demo";
  private const string GithubRemote = "git@github.com:eglove/ethang-agent.git";
  private const string AuthOk =
      "{\"hosts\":{\"github.com\":[{\"state\":\"success\",\"active\":true,\"host\":\"github.com\"," +
      "\"login\":\"eglove\",\"tokenSource\":\"keyring\",\"scopes\":\"repo\",\"gitProtocol\":\"ssh\"}]}}";

  /// <summary>Records every invocation and answers by matching the argv's first tokens,
  ///     so one test says exactly which command produced which reply.</summary>
  private sealed class FakeRunner : IGhCliRunner
  {
    public List<(string Exe, string[] Args)> Calls { get; } = [];

    /// <summary>The reply for a call, chosen by the code under test's own argv.</summary>
    public Func<string, string[], Result<GhCliRun>> Reply { get; set; } =
        (_, _) => Result.Success(new GhCliRun(0, ""));

    public Task<Result<GhCliRun>> RunAsync(string exe, string[] args, CancellationToken ct)
    {
      Calls.Add((exe, args));
      return Task.FromResult(Reply(exe, args));
    }
  }

  private sealed class Harness
  {
    public FakeRunner Runner { get; } = new();

    public string? GhExe { get; set; } = @"C:\Program Files\GitHub CLI\gh.exe";

    public string? GitExe { get; set; } = @"C:\Program Files\Git\cmd\git.exe";

    public GhCliAccess Build() => new(() => GhExe, () => GitExe, Runner);
  }

  private static Harness Ready()
  {
    Harness h = new();
    h.Runner.Reply = (exe, args) => args switch
    {
      ["-C", _, "remote", "get-url", "origin"] => Result.Success(new GhCliRun(0, GithubRemote + "\n", "")),
      ["auth", ..] => Result.Success(new GhCliRun(0, AuthOk, "")),
      ["repo", "view", ..] => Result.Success(new GhCliRun(0,
          "{\"nameWithOwner\":\"eglove/ethang-agent\",\"url\":\"https://github.com/eglove/ethang-agent\"}", "")),
      ["pr", "list", ..] => Result.Success(new GhCliRun(0, "[]", "")),
      _ => Result.Success(new GhCliRun(0, "")),
    };
    return h;
  }

  [Fact]
  public async Task Probe_WhenGhCannotBeResolved_ReportsNotInstalled()
  {
    Harness h = Ready();
    h.GhExe = null;

    Result<GitHubRepository> probe = await h.Build().ProbeAsync(Workspace, TestContext.Current.CancellationToken);

    Assert.False(probe.IsSuccess);
    Assert.Equal("GhNotInstalled", probe.Error.Code);
    Assert.Empty(h.Runner.Calls);
  }

  [Fact]
  public async Task Probe_WhenNoHostIsLoggedIn_ReportsNotAuthenticated()
  {
    Harness h = Ready();
    h.Runner.Reply = (exe, args) => args switch
    {
      ["-C", ..] => Result.Success(new GhCliRun(0, GithubRemote + "\n", "")),
      ["auth", ..] => Result.Success(new GhCliRun(0, "{\"hosts\":{}}",
          "You are not logged into any GitHub hosts. To log in, run: gh auth login")),
      _ => Result.Success(new GhCliRun(0, "")),
    };

    Result<GitHubRepository> probe = await h.Build().ProbeAsync(Workspace, TestContext.Current.CancellationToken);

    Assert.False(probe.IsSuccess);
    Assert.Equal("GhNotAuthenticated", probe.Error.Code);
  }

  [Fact]
  public async Task Probe_WhenAuthStatusExitsNeedingLogin_ReportsNotAuthenticated()
  {
    Harness h = Ready();
    h.Runner.Reply = (exe, args) => args switch
    {
      ["-C", ..] => Result.Success(new GhCliRun(0, GithubRemote + "\n", "")),
      ["auth", ..] => Result.Success(new GhCliRun(4, "",
          "To get started with GitHub CLI, please run:  gh auth login")),
      _ => Result.Success(new GhCliRun(0, "")),
    };

    Result<GitHubRepository> probe = await h.Build().ProbeAsync(Workspace, TestContext.Current.CancellationToken);

    Assert.False(probe.IsSuccess);
    Assert.Equal("GhNotAuthenticated", probe.Error.Code);
  }

  [Fact]
  public async Task Probe_WhenTheWorkspaceHasNoGitHubRemote_ReportsNotAGitHubRepository()
  {
    Harness h = Ready();
    h.Runner.Reply = (exe, args) => args switch
    {
      ["-C", ..] => Result.Success(new GhCliRun(1, "",
          "failed to run git: fatal: not a git repository (or any of the parent directories): .git")),
      ["auth", ..] => Result.Success(new GhCliRun(0, AuthOk, "")),
      _ => Result.Success(new GhCliRun(0, "")),
    };

    Result<GitHubRepository> probe = await h.Build().ProbeAsync(Workspace, TestContext.Current.CancellationToken);

    Assert.False(probe.IsSuccess);
    Assert.Equal("NotAGitHubRepository", probe.Error.Code);
  }

  [Fact]
  public async Task Probe_WhenGhCannotResolveTheRepository_ReportsNotAGitHubRepository()
  {
    Harness h = Ready();
    h.Runner.Reply = (exe, args) => args switch
    {
      ["-C", ..] => Result.Success(new GhCliRun(0, GithubRemote + "\n", "")),
      ["auth", ..] => Result.Success(new GhCliRun(0, AuthOk, "")),
      ["repo", "view", ..] => Result.Success(new GhCliRun(1, "",
          "GraphQL: Could not resolve to a Repository with the name 'eglove/nope'.")),
      _ => Result.Success(new GhCliRun(0, "")),
    };

    Result<GitHubRepository> probe = await h.Build().ProbeAsync(Workspace, TestContext.Current.CancellationToken);

    Assert.False(probe.IsSuccess);
    Assert.Equal("NotAGitHubRepository", probe.Error.Code);
  }

  [Fact]
  public async Task Probe_WhenReady_NamesTheRepository()
  {
    Harness h = Ready();

    Result<GitHubRepository> probe = await h.Build().ProbeAsync(Workspace, TestContext.Current.CancellationToken);

    Assert.True(probe.IsSuccess);
    Assert.Equal("eglove/ethang-agent", probe.Value.NameWithOwner);
    Assert.Equal("https://github.com/eglove/ethang-agent", probe.Value.Url.ToString());
  }

  [Fact]
  public async Task List_WhenNotAuthenticated_ReportsTheSameDegradationCode()
  {
    Harness h = Ready();
    h.Runner.Reply = (exe, args) => args switch
    {
      ["-C", ..] => Result.Success(new GhCliRun(0, GithubRemote + "\n", "")),
      _ => Result.Success(new GhCliRun(4, "", "gh auth login")),
    };

    Result<IReadOnlyList<GitHubPullRequest>> listed = await h.Build()
        .ListPullRequestsAsync(Workspace, "eglove/ethang-agent", TestContext.Current.CancellationToken);

    Assert.False(listed.IsSuccess);
    Assert.Equal("GhNotAuthenticated", listed.Error.Code);
  }

  [Fact]
  public async Task List_WhenGhIsMissing_ReportsNotInstalled()
  {
    Harness h = Ready();
    h.GhExe = null;

    Result<IReadOnlyList<GitHubPullRequest>> listed = await h.Build()
        .ListPullRequestsAsync(Workspace, "eglove/ethang-agent", TestContext.Current.CancellationToken);

    Assert.False(listed.IsSuccess);
    Assert.Equal("GhNotInstalled", listed.Error.Code);
  }

  [Fact]
  public async Task List_ParsesNumberTitleAuthorReviewStateAndChecks()
  {
    Harness h = Ready();
    h.Runner.Reply = (exe, args) => args switch
    {
      ["-C", ..] => Result.Success(new GhCliRun(0, GithubRemote + "\n", "")),
      ["pr", "list", ..] => Result.Success(new GhCliRun(0, PrListJson, "")),
      _ => Result.Success(new GhCliRun(0, "")),
    };

    Result<IReadOnlyList<GitHubPullRequest>> listed = await h.Build()
        .ListPullRequestsAsync(Workspace, "eglove/ethang-agent", TestContext.Current.CancellationToken);

    Assert.True(listed.IsSuccess, listed.IsSuccess ? "" : listed.Error.Message);
    GitHubPullRequest pr = Assert.Single(listed.Value);
    Assert.Equal(12, pr.Number);
    Assert.Equal("feat(desktop): add the GitHub view", pr.Title);
    Assert.Equal("eglove", pr.Author);
    Assert.Equal("feature/github", pr.HeadBranch);
    Assert.Equal("master", pr.BaseBranch);
    Assert.Equal("APPROVED", pr.ReviewDecision);
    Assert.False(pr.IsDraft);
    Assert.Equal(3, pr.Checks.Total);
    Assert.Equal(2, pr.Checks.Passing);
    Assert.Equal(1, pr.Checks.Failing);
    Assert.Equal(0, pr.Checks.Pending);
    Assert.Equal("#12 \u00B7 feat(desktop): add the GitHub view", pr.Display);
  }

  [Fact]
  public async Task List_WithNoChecksConfigured_ReportsAnEmptySummary_NotGreen()
  {
    Harness h = Ready();
    h.Runner.Reply = (exe, args) => args switch
    {
      ["-C", ..] => Result.Success(new GhCliRun(0, GithubRemote + "\n", "")),
      ["pr", "list", ..] => Result.Success(new GhCliRun(0, NoChecksJson, "")),
      _ => Result.Success(new GhCliRun(0, "")),
    };

    Result<IReadOnlyList<GitHubPullRequest>> listed = await h.Build()
        .ListPullRequestsAsync(Workspace, "eglove/ethang-agent", TestContext.Current.CancellationToken);

    Assert.True(listed.IsSuccess, listed.IsSuccess ? "" : listed.Error.Message);
    GitHubPullRequest pr = Assert.Single(listed.Value);
    Assert.Equal(0, pr.Checks.Total);
    Assert.False(pr.Checks.IsGreen);
  }

  [Fact]
  public async Task List_CountsAStillRunningCheckAsPending()
  {
    Harness h = Ready();
    h.Runner.Reply = (exe, args) => args switch
    {
      ["-C", ..] => Result.Success(new GhCliRun(0, GithubRemote + "\n", "")),
      ["pr", "list", ..] => Result.Success(new GhCliRun(0, PendingCheckJson, "")),
      _ => Result.Success(new GhCliRun(0, "")),
    };

    Result<IReadOnlyList<GitHubPullRequest>> listed = await h.Build()
        .ListPullRequestsAsync(Workspace, "eglove/ethang-agent", TestContext.Current.CancellationToken);

    Assert.True(listed.IsSuccess, listed.IsSuccess ? "" : listed.Error.Message);
    GitHubPullRequest pr = Assert.Single(listed.Value);
    Assert.Equal(1, pr.Checks.Pending);
    Assert.Equal(0, pr.Checks.Passing);
    Assert.False(pr.Checks.IsGreen);
  }

  [Fact]
  public async Task Get_ReadsTheBodyForTheDetailPane()
  {
    Harness h = Ready();
    h.Runner.Reply = (exe, args) => args switch
    {
      ["-C", ..] => Result.Success(new GhCliRun(0, GithubRemote + "\n", "")),
      ["pr", "view", ..] => Result.Success(new GhCliRun(0, PrViewJson, "")),
      _ => Result.Success(new GhCliRun(0, "")),
    };

    Result<GitHubPullRequest> pr = await h.Build()
        .GetPullRequestAsync(Workspace, "eglove/ethang-agent", 12, TestContext.Current.CancellationToken);

    Assert.True(pr.IsSuccess, pr.IsSuccess ? "" : pr.Error.Message);
    Assert.Equal(12, pr.Value.Number);
    Assert.Contains("Closes #10", pr.Value.Body, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Create_HandsTheCliEveryFlagItNeeds_AndNeverPrompts()
  {
    Harness h = Ready();
    string[]? createArgs = null;
    h.Runner.Reply = (exe, args) => args switch
    {
      ["pr", "create", ..] => RecordCreate(args),
      ["pr", "view", ..] => Result.Success(new GhCliRun(0, PrViewJson)),
      ["-C", ..] => Result.Success(new GhCliRun(0, GithubRemote + "\n")),
      _ => Result.Success(new GhCliRun(0, "")),
    };

    // The created PR is READ BACK by number, so this reply serves both calls.
    Result<GhCliRun> RecordCreate(string[] args)
    {
      createArgs = args;
      return Result.Success(new GhCliRun(0, "https://github.com/eglove/ethang-agent/pull/12"));
    }

    Result<GitHubPullRequest> created = await h.Build().CreatePullRequestAsync(
        Workspace,
        "eglove/ethang-agent",
        new GitHubCreatePullRequestRequest("Title here", "Body here", "feature/github", "master"),
        TestContext.Current.CancellationToken);

    Assert.True(created.IsSuccess, created.IsSuccess ? "" : created.Error.Message);
    Assert.NotNull(createArgs);
    Assert.Contains("--repo", createArgs, StringComparer.Ordinal);
    Assert.Contains("eglove/ethang-agent", createArgs, StringComparer.Ordinal);
    Assert.Contains("--head", createArgs, StringComparer.Ordinal);
    Assert.Contains("feature/github", createArgs, StringComparer.Ordinal);
    Assert.Contains("--base", createArgs, StringComparer.Ordinal);
    Assert.Contains("master", createArgs, StringComparer.Ordinal);
    Assert.Contains("--title", createArgs, StringComparer.Ordinal);
    Assert.Contains("Title here", createArgs, StringComparer.Ordinal);
    Assert.Contains("--body", createArgs, StringComparer.Ordinal);
    Assert.Contains("Body here", createArgs, StringComparer.Ordinal);
    Assert.DoesNotContain("--draft", createArgs, StringComparer.Ordinal);
    Assert.Equal(12, created.Value.Number);
  }

  [Fact]
  public async Task Create_WhenTheCliRefuses_CarriesItsOwnStderrBack()
  {
    Harness h = Ready();
    h.Runner.Reply = (exe, args) => args switch
    {
      ["-C", ..] => Result.Success(new GhCliRun(0, GithubRemote + "\n", "")),
      ["pr", "create", ..] => Result.Success(new GhCliRun(1, "",
          "pull request create failed: GraphQL: A pull request already exists")),
      _ => Result.Success(new GhCliRun(0, "")),
    };

    Result<GitHubPullRequest> created = await h.Build().CreatePullRequestAsync(
        Workspace,
        "eglove/ethang-agent",
        new GitHubCreatePullRequestRequest("t", "b", "h", "master"),
        TestContext.Current.CancellationToken);

    Assert.False(created.IsSuccess);
    Assert.Contains("A pull request already exists", created.Error.Message, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData("git@github.com:eglove/ethang-agent.git", "github.com", "eglove/ethang-agent")]
  [InlineData("https://github.com/eglove/ethang-agent.git", "github.com", "eglove/ethang-agent")]
  [InlineData("https://github.com/eglove/ethang-agent", "github.com", "eglove/ethang-agent")]
  [InlineData("ssh://git@github.com/eglove/ethang-agent.git", "github.com", "eglove/ethang-agent")]
  [InlineData("git@github.example.com:org/repo.git", "github.example.com", "org/repo")]
  public void RemoteUrl_ParsesIntoHostAndOwnerRepo(string remote, string host, string ownerRepo)
  {
    bool parsed = GhCliAccess.TryParseRemote(remote, out GitHubRemote parsed2);

    Assert.True(parsed, $"'{remote}' must parse as a GitHub remote");
    Assert.Equal(host, parsed2.Host);
    Assert.Equal(ownerRepo, parsed2.OwnerRepo);
    Assert.Equal(host == "github.com" ? ownerRepo : $"{host}/{ownerRepo}", parsed2.Selector);
  }

  [Theory]
  [InlineData("https://gitlab.com")] // a host with no owner/name path
  [InlineData("C:\\repos\\local")]
  [InlineData("git@github.com:onlyowner")]
  [InlineData("")]
  public void RemoteUrl_RefusesWhatCannotNameAHostAndRepository(string remote) =>
      Assert.False(GhCliAccess.TryParseRemote(remote, out _));

  /// <summary>A remote on a host <c>gh</c> is not authenticated for is NOT a GitHub
  ///     repository - and gitlab.com parses exactly like github.com, so parsing alone could
  ///     never tell them apart. The host set from <c>gh auth status</c> is the authority.</summary>
  [Fact]
  public async Task Probe_WhenTheRemoteHostIsNotAGitHubHost_ReportsNotAGitHubRepository()
  {
    Harness h = Ready();
    h.Runner.Reply = (exe, args) => args switch
    {
      ["-C", ..] => Result.Success(new GhCliRun(0, "https://gitlab.com/org/repo.git\n")),
      ["auth", ..] => Result.Success(new GhCliRun(0, AuthOk)),
      _ => Result.Success(new GhCliRun(0, "")),
    };

    Result<GitHubRepository> probe = await h.Build().ProbeAsync(Workspace, TestContext.Current.CancellationToken);

    Assert.False(probe.IsSuccess);
    Assert.Equal("NotAGitHubRepository", probe.Error.Code);
  }

  /// <summary>An enterprise host gh HAS authenticated is a GitHub repository, with no
  ///     hardcoded hostname allowlist standing in the way.</summary>
  [Fact]
  public async Task Probe_WhenAnEnterpriseHostIsAuthenticated_UsesThatHostAsTheSelector()
  {
    Harness h = Ready();
    const string enterpriseAuth =
        "{\"hosts\":{\"github.example.com\":[{\"state\":\"success\",\"active\":true}]}}";
    h.Runner.Reply = (exe, args) => args switch
    {
      ["-C", ..] => Result.Success(new GhCliRun(0, "git@github.example.com:org/repo.git\n")),
      ["auth", ..] => Result.Success(new GhCliRun(0, enterpriseAuth)),
      ["repo", "view", ..] => Result.Success(new GhCliRun(0,
          "{\"nameWithOwner\":\"org/repo\",\"url\":\"https://github.example.com/org/repo\"}")),
      _ => Result.Success(new GhCliRun(0, "")),
    };

    Result<GitHubRepository> probe = await h.Build().ProbeAsync(Workspace, TestContext.Current.CancellationToken);

    Assert.True(probe.IsSuccess, probe.IsSuccess ? "" : probe.Error.Message);
    Assert.Equal("github.example.com/org/repo", probe.Value.Selector);
  }

  private const string PrListJson =
      "[{\"number\":12,\"title\":\"feat(desktop): add the GitHub view\"," +
      "\"author\":{\"id\":\"MDQ6VXNlcjE\",\"is_bot\":false,\"login\":\"eglove\",\"name\":\"E Glove\"}," +
      "\"state\":\"OPEN\",\"isDraft\":false," +
      "\"url\":\"https://github.com/eglove/ethang-agent/pull/12\"," +
      "\"headRefName\":\"feature/github\",\"baseRefName\":\"master\"," +
      "\"reviewDecision\":\"APPROVED\",\"updatedAt\":\"2026-10-25T12:00:00Z\"," +
      "\"statusCheckRollup\":[" +
      "{\"__typename\":\"CheckRun\",\"name\":\"build\",\"status\":\"COMPLETED\",\"conclusion\":\"SUCCESS\"}," +
      "{\"__typename\":\"CheckRun\",\"name\":\"lint\",\"status\":\"COMPLETED\",\"conclusion\":\"SKIPPED\"}," +
      "{\"__typename\":\"CheckRun\",\"name\":\"test\",\"status\":\"COMPLETED\",\"conclusion\":\"FAILURE\"}" +
      "]}]";

  private const string NoChecksJson =
      "[{\"number\":13,\"title\":\"docs: readme\",\"author\":{\"login\":\"eglove\"}," +
      "\"state\":\"OPEN\",\"isDraft\":false,\"url\":\"https://github.com/eglove/ethang-agent/pull/13\"," +
      "\"headRefName\":\"docs\",\"baseRefName\":\"master\",\"reviewDecision\":null," +
      "\"updatedAt\":\"2026-10-25T12:00:00Z\",\"statusCheckRollup\":[]}]";

  private const string PendingCheckJson =
      "[{\"number\":14,\"title\":\"wip\",\"author\":{\"login\":\"eglove\"}," +
      "\"state\":\"OPEN\",\"isDraft\":true,\"url\":\"https://github.com/eglove/ethang-agent/pull/14\"," +
      "\"headRefName\":\"wip\",\"baseRefName\":\"master\",\"reviewDecision\":null," +
      "\"updatedAt\":\"2026-10-25T12:00:00Z\",\"statusCheckRollup\":[" +
      "{\"__typename\":\"CheckRun\",\"name\":\"build\",\"status\":\"IN_PROGRESS\",\"conclusion\":\"\"}]}]";

  private const string PrViewJson =
      "{\"number\":12,\"title\":\"feat(desktop): add the GitHub view\"," +
      "\"author\":{\"login\":\"eglove\"},\"state\":\"OPEN\",\"isDraft\":false," +
      "\"url\":\"https://github.com/eglove/ethang-agent/pull/12\"," +
      "\"headRefName\":\"feature/github\",\"baseRefName\":\"master\"," +
      "\"reviewDecision\":\"APPROVED\",\"updatedAt\":\"2026-10-25T12:00:00Z\"," +
      "\"body\":\"Closes #10\\n\\nThe GitHub view.\",\"statusCheckRollup\":[]}";
}
#pragma warning restore JSON002 // Probable JSON string detected
