using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>The three ways a GitHub-backed surface can be unavailable, plus the ready state.
///     <para>Design D6 makes the <c>gh</c> CLI a prerequisite, so the failure path IS part of
///     the feature: each state names what the user can do about it, and a blank pane or a
///     crash is a bug. The states are deliberately a closed set - a surface either renders
///     the list or renders exactly one of these.</para></summary>
public enum GitHubAvailability
{
  /// <summary>The CLI resolved, the user is authenticated, and the workspace is a GitHub
  ///     repository: the list is meaningful.</summary>
  Ready,

  /// <summary>The <c>gh</c> executable could not be resolved or started.</summary>
  NotInstalled,

  /// <summary><c>gh</c> is installed but no host holds a credential.</summary>
  NotAuthenticated,

  /// <summary><c>gh</c> is usable, but this workspace resolves to no GitHub repository.</summary>
  NotAGitHubRepository,
}

/// <summary>One CI check as <c>gh</c> reports it in <c>statusCheckRollup</c>: the name a person
///     reads, the coarse status, and the conclusion once it has one.
///     <para><see cref="Conclusion"/> is null while a check is still running - 'not finished'
///     and 'finished green' are different facts and must not render alike.</para></summary>
public sealed record GitHubCheck(string Name, string Status, string? Conclusion);

/// <summary>The CI summary a PR row shows: how many checks reported each outcome.
///     <para>Counting is the sum the pane renders ('3 passing, 1 failing'), so the rollup is
///     reduced ONCE at the seam rather than re-counted by every surface.</para></summary>
public sealed record GitHubCheckSummary(int Total, int Passing, int Failing, int Pending)
{
  /// <summary>An empty summary: a PR with no checks configured. A PR with no CI and a PR
  ///     whose CI is all green are different facts, and <see cref="Total"/> tells them apart.</summary>
  public static GitHubCheckSummary None { get; } = new(0, 0, 0, 0);

  /// <summary>True when at least one check ran and none failed and none is still running.</summary>
  public bool IsGreen => Total > 0 && Failing == 0 && Pending == 0;
}

/// <summary>A pull request as the workbench renders it. Provider-neutral: nothing here is
///     a <c>gh</c> type, so the seam holds if the CLI is ever replaced.
///     <para><see cref="ReviewDecision"/> is the provider's own vocabulary (approved /
///     review required / changes requested) and is null when nobody has decided yet;
///     <see cref="IsDraft"/> is separate because a draft is not under review at all.</para></summary>
public sealed record GitHubPullRequest(
    int Number,
    string Title,
    string Author,
    string HeadBranch,
    string BaseBranch,
    Uri Url,
    bool IsDraft,
    string? ReviewDecision,
    GitHubCheckSummary Checks,
    DateTimeOffset UpdatedAt,
    string Body = "")
{
  /// <summary>The one-line identity a list row shows: '#123 - title'.</summary>
  public string Display => $"#{Number} \u00B7 {Title}";
}

/// <summary>The repository a workspace resolves to, as <c>gh repo view</c> reports it.
///     <para><see cref="Selector"/> is what every later <c>gh</c> call is handed as
///     <c>--repo</c>: the enterprise-host form when the remote names one, and the plain
///     <c>owner/name</c> form on github.com.</para></summary>
public sealed record GitHubRepository(string NameWithOwner, Uri Url, string Selector)
{
  /// <summary>The remote a workspace's <c>origin</c> points at: the host and the
  ///     <c>owner/name</c> pair, exactly as the remote URL spelled them.</summary>
  public static GitHubRepository FromRemote(string nameWithOwner, Uri url, string host) =>
      new(nameWithOwner, url, host == "github.com" ? nameWithOwner : $"{host}/{nameWithOwner}");
}

/// <summary>What a workspace's git remote says about its GitHub repository: the host (so
///     an enterprise host stays addressable) and the <c>owner/name</c> pair.</summary>
public sealed record GitHubRemote(string Host, string OwnerRepo)
{
  /// <summary>The <c>--repo</c> argument: bare <c>owner/name</c> on github.com, and
  ///     <c>host/owner/name</c> everywhere else, which is the form <c>gh</c> documents.</summary>
  public string Selector => Host == "github.com" ? OwnerRepo : $"{Host}/{OwnerRepo}";
}

/// <summary>A create-PR request. Every field here is required by the CLI's non-interactive
///     form: <c>gh pr create</c> prompts for anything omitted, and this seam must never prompt.
///     <para><see cref="Draft"/> is the only optional knob, because a draft PR is a
///     deliberate choice rather than a default.</para></summary>
public sealed record GitHubCreatePullRequestRequest(
    string Title,
    string Body,
    string HeadBranch,
    string BaseBranch,
    bool Draft = false);

/// <summary>GitHub pull-request access for the desktop's GitHub view (issue #10).
///     <para>Design D6 settled the implementation: drive the <c>gh</c> CLI as a subprocess.
///     <c>gh</c> already owns authentication (keyring-backed), org policy, SSO, and
///     enterprise hosts, so the harness stores NO GitHub credentials of its own - there is
///     no API client, no token, and no OAuth flow behind this seam.</para>
///     <para>Every expected failure is a typed <see cref="Result{T}"/> error, never an
///     exception: <c>GhNotInstalled</c>, <c>GhNotAuthenticated</c>, and
///     <c>NotAGitHubRepository</c> are the three degradation codes the view turns into its
///     actionable empty states.</para></summary>
public interface IGitHubAccess
{
  /// <summary>Probes whether GitHub views can show live data for this workspace, and
  ///     names the repository when they can.
  ///     <para>The probe NEVER throws: not-installed, not-authenticated, and
  ///     not-a-repository are ordinary outcomes of asking, not faults. It is also the
  ///     only call a surface has to make when it wants to render its empty state.</para></summary>
  Task<Result<GitHubRepository>> ProbeAsync(string workspacePath, CancellationToken ct = default);

  /// <summary>Lists open pull requests for <paramref name="selector"/> (the repository a
  ///     workspace resolved to), so a person can see what is in flight. Ordered by what
  ///     <c>gh</c> returns - most recently updated first - and not re-sorted behind the seam.</summary>
  Task<Result<IReadOnlyList<GitHubPullRequest>>> ListPullRequestsAsync(
      string workspacePath, string selector, CancellationToken ct = default);

  /// <summary>Reads one pull request by number, including its body, for the detail pane.</summary>
  Task<Result<GitHubPullRequest>> GetPullRequestAsync(
      string workspacePath, string selector, int number, CancellationToken ct = default);

  /// <summary>Creates a pull request from <paramref name="request"/>'s head branch into its
  ///     base branch and reports the created PR.
  ///     <para>The ONE write this feature performs, and it is deliberate: creating a PR is
  ///     the highest-value action in the whole view. Nothing here merges, comments, or
  ///     reviews - those are a later depth decision, not a silent extra.</para></summary>
  Task<Result<GitHubPullRequest>> CreatePullRequestAsync(
      string workspacePath, string selector, GitHubCreatePullRequestRequest request, CancellationToken ct = default);
}
