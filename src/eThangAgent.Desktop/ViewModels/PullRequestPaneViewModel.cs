using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Desktop.ViewModels;

/// <summary>What the GitHub pane is showing. The three degradation states are separate
///     VALUES rather than one 'error' bucket, because each has a different remedy and a pane
///     that collapsed them would tell a user with an unauthenticated CLI to install it.</summary>
internal enum GitHubPaneState
{
  /// <summary>A repository resolved and the list is meaningful (possibly empty).</summary>
  Ready,

  /// <summary>The <c>gh</c> CLI is not installed.</summary>
  NotInstalled,

  /// <summary>The CLI is installed but no host is authenticated.</summary>
  NotAuthenticated,

  /// <summary>The workspace resolves to no GitHub repository.</summary>
  NotAGitHubRepository,

  /// <summary>No GitHub seam was wired into this session at all (headless hosts).</summary>
  Unavailable,
}

/// <summary>One row of the pull-request list. Every display string is computed HERE, so the
///     view never decides what 'no review yet' means.
///     <para><see cref="ReviewDisplay"/> and <see cref="ChecksDisplay"/> exist because the
///     provider's raw vocabulary ('REVIEW_REQUIRED', three counters) is not what a person
///     reads, and a row that showed the raw vocabulary would make the reader translate it.</para></summary>
internal sealed record PullRequestRowViewModel(
    int Number,
    string Title,
    string AuthorDisplay,
    string ReviewDisplay,
    string ChecksDisplay,
    bool IsDraft,
    Uri Url)
{
  /// <summary>The list line: '#12 - title'.</summary>
  public string Display => $"#{Number} \u00B7 {Title}";

  /// <summary>Projects a seam record onto the row.
  ///     <para>A DRAFT is flagged rather than folded into the review state: a draft is not
  ///     awaiting review at all, so 'no review yet' plus a draft marker is the honest pair.</para></summary>
  public static PullRequestRowViewModel For(GitHubPullRequest pullRequest)
  {
    ArgumentNullException.ThrowIfNull(pullRequest);
    return new PullRequestRowViewModel(
        pullRequest.Number,
        pullRequest.Title,
        $"@{pullRequest.Author}",
        DescribeReview(pullRequest.ReviewDecision),
        DescribeChecks(pullRequest.Checks),
        pullRequest.IsDraft,
        pullRequest.Url);
  }

  /// <summary>Spells the provider's review decision out. An absent decision is 'no review
  ///     yet', which must never read as 'approved' - the two are opposite answers to the
  ///     question the row exists to answer.
  ///     <para>An unrecognised decision renders VERBATIM rather than being lower-cased: the
  ///     provider's own word is information, and rewriting its case would be a guess about
  ///     what it meant.</para></summary>
  internal static string DescribeReview(string? reviewDecision) => reviewDecision switch
  {
    null or "" => "no review yet",
    "APPROVED" => "approved",
    "CHANGES_REQUESTED" => "changes requested",
    "REVIEW_REQUIRED" => "review required",
    var other => other,
  };

  /// <summary>Renders the CI summary. 'no checks' and 'all green' are different answers and
  ///     must not both render as blank: an empty summary is the absence of CI, not success.</summary>
  internal static string DescribeChecks(GitHubCheckSummary checks)
  {
    ArgumentNullException.ThrowIfNull(checks);
    if (checks.Total == 0)
    {
      return "no checks";
    }

    List<string> parts = [];
    Add(parts, checks.Passing, "passing");
    Add(parts, checks.Failing, "failing");
    Add(parts, checks.Pending, "running");

    // A rollup whose counted outcomes sum to less than its total is not describable by the
    // three buckets; say how many checks there are instead of rendering an empty string that
    // would read as 'no checks'.
    return parts.Count == 0
        ? $"{checks.Total.ToString(CultureInfo.InvariantCulture)} checked"
        : string.Join(", ", parts);
  }

  private static void Add(List<string> parts, int count, string label)
  {
    if (count > 0)
    {
      parts.Add($"{count.ToString(CultureInfo.InvariantCulture)} {label}");
    }
  }
}

/// <summary>The workbench's GitHub view (issue #10): the repository's open pull requests, one
///     selected PR's body, and a form that creates a PR from the current branch.
///     <para>DEGRADATION IS CONTRACTUAL (design D6 makes <c>gh</c> a prerequisite). Exactly
///     three failure states are reachable and each renders its own actionable sentence:
///     install the CLI, run <c>gh auth login</c>, or open a workspace that is on GitHub. A
///     blank pane or a crash is a bug, not an empty state.</para>
///     <para>REALTIME IS A SLOW POLL, and only while visible: GitHub offers no push channel
///     without a public webhook endpoint, and each read spawns the CLI - orders of magnitude
///     more expensive than the database reads the other views poll. The poll also skips while
///     the pane is DEGRADED: a missing CLI or a logged-out user does not fix itself between
///     ticks, so re-probing would spend a subprocess to learn the same answer. The next
///     explicit refresh re-probes, which is how a just-installed CLI comes alive.</para>
///     <para>NO CREDENTIALS: nothing here stores or reads a GitHub token. <c>gh</c> owns
///     authentication, so the whole security surface of a token store is absent (D6).</para></summary>
internal sealed partial class PullRequestPaneViewModel(
    string workspaceRoot,
    IGitHubAccess? github,
    Action<Action> marshal,
    TimeSpan? pollInterval = null) : ObservableObject, IDisposable
{
  /// <summary>The default poll interval. Slow on purpose: the design proposes 10-30 s for
  ///     gh-backed data because every tick is a process spawn, not a query.</summary>
  internal static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(20);

  private readonly string _workspaceRoot = string.IsNullOrWhiteSpace(workspaceRoot)
      ? throw new ArgumentException("A workspace root is required.", nameof(workspaceRoot))
      : workspaceRoot;
  private readonly IGitHubAccess? _github = github;
  private readonly Action<Action> _marshal = marshal ?? throw new ArgumentNullException(nameof(marshal));
  private readonly TimeSpan _pollInterval = pollInterval ?? DefaultPollInterval;

  /// <summary>Serialises refreshes. A poll tick that arrives while a read is in flight waits
  ///     for it rather than piling a second subprocess on top of the first.</summary>
  private readonly SemaphoreSlim _refreshGate = new(1, 1);
  private CancellationTokenSource? _run;
  private bool _started;
  private bool _disposed;
  private string _selector = string.Empty;

  /// <summary>The open pull requests, in the order the provider listed them - most recently
  ///     updated first, which is the order a person scanning 'what is in flight' wants.</summary>
  public ObservableCollection<PullRequestRowViewModel> PullRequests { get; } = [];

  /// <summary>Which of the pane's states is showing. Drives both the message and the
  ///     controls: the create form is offered only when the state is Ready.</summary>
  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(IsReady))]
  public partial GitHubPaneState State { get; private set; } = GitHubPaneState.Unavailable;

  /// <summary>The header's right-hand status: 'live - N open', or the reason nothing is
  ///     showing. A view that cannot read must SAY so rather than look empty.</summary>
  [ObservableProperty]
  public partial string StatusLine { get; private set; } = "";

  /// <summary>True when a repository resolved, so the list (and the create form) is
  ///     meaningful. The view binds the create control to this, so the control cannot be
  ///     offered in a state whose submit would only fail.</summary>
  public bool IsReady => State == GitHubPaneState.Ready;

  /// <summary>Non-null whenever the pane is not showing a list: the degradation message, the
  ///     empty-list sentence, or a read failure. Never empty for a visible non-list state.</summary>
  [ObservableProperty]
  public partial string? EmptyReason { get; private set; }

  /// <summary>True when a ready repository simply has no open pull requests.</summary>
  [ObservableProperty]
  public partial bool IsEmpty { get; private set; } = true;

  /// <summary>The selected pull request's number, or null when nothing is selected.</summary>
  [ObservableProperty]
  public partial int? SelectedNumber { get; private set; }

  /// <summary>The selected pull request, as the seam reported it.</summary>
  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(SelectedDisplay))]
  public partial GitHubPullRequest? Selected { get; private set; }

  /// <summary>The selected pull request's body. Empty when nothing is selected or the PR has
  ///     no description. The BODY is a separate property because it is the one field the list
  ///     response does not need but the detail pane exists to show.</summary>
  [ObservableProperty]
  public partial string SelectedBody { get; private set; } = "";

  /// <summary>The selected PR's heading, or empty when nothing is selected.</summary>
  public string SelectedDisplay => Selected is { } selected ? selected.Display : string.Empty;

  /// <summary>Non-null when the selected PR could not be read.</summary>
  [ObservableProperty]
  public partial string? DetailError { get; private set; }

  /// <summary>True while the create form is open. A refused submit keeps it open, so the text
  ///     the user typed is never thrown away by a failure.</summary>
  [ObservableProperty]
  public partial bool IsCreating { get; private set; }

  /// <summary>The new PR's title. Required: the CLI would prompt for it, and this pane must
  ///     never prompt (a prompt on a redirected stdin fails the whole create).</summary>
  [ObservableProperty]
  public partial string NewTitle { get; set; } = "";

  [ObservableProperty]
  public partial string NewBody { get; set; } = "";

  /// <summary>The head branch the PR will come FROM - seeded from the checked-out branch, so
  ///     the ordinary case is one click plus a title.</summary>
  [ObservableProperty]
  public partial string NewHeadBranch { get; set; } = "";

  /// <summary>The base branch the PR will go INTO.</summary>
  [ObservableProperty]
  public partial string NewBaseBranch { get; set; } = "";

  [ObservableProperty]
  public partial bool NewDraft { get; set; }

  /// <summary>Non-null when the last create attempt was refused; carries the CLI's own words.
  ///     A create failure is not a pane failure: the list stays and the form stays open.</summary>
  [ObservableProperty]
  public partial string? CreateError { get; private set; }

  /// <summary>True while a create is in flight, so the button is disabled instead of firing
  ///     twice.</summary>
  [ObservableProperty]
  public partial bool IsSubmitting { get; private set; }

  /// <summary>Starts the visible-only poll and runs the first read. Idempotent, so a tab
  ///     switch back to this view cannot stack a second timer.</summary>
  public void Start()
  {
    if (_disposed || _started)
    {
      return;
    }

    if (_github is null)
    {
      // No seam: render the unavailable state once and run no timer at all.
      Apply(RenderUnavailable);
      return;
    }

    _started = true;
    _run = new CancellationTokenSource();
    _ = RefreshAsync(_run.Token);
    _ = PollAsync(_run.Token);
  }

  /// <summary>Stops the poll. Called when the pane stops being visible: a pane nobody is
  ///     looking at must cost nothing, and a subprocess per tick is far from nothing.</summary>
  public void Stop()
  {
    if (!_started)
    {
      return;
    }

    _started = false;
    _run?.Cancel();
    _run?.Dispose();
    _run = null;
  }

  /// <summary>Re-probes and re-reads the list. Every path funnels through here, so there is
  ///     exactly one renderer and no chance of two disagreeing.</summary>
  public async Task RefreshAsync(CancellationToken ct)
  {
    if (_disposed)
    {
      return;
    }

    if (_github is null)
    {
      Apply(RenderUnavailable);
      return;
    }

    await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
    try
    {
      // The probe runs on EVERY refresh: it is the only way a user who just installed the CLI
      // or logged in sees the pane come alive without restarting the app.
      Result<GitHubRepository> probe = await _github.ProbeAsync(_workspaceRoot, ct).ConfigureAwait(false);
      if (!probe.IsSuccess)
      {
        DomainError degraded = probe.Error;
        Apply(() => RenderDegraded(degraded));
        return;
      }

      _selector = probe.Value.Selector;
      Result<IReadOnlyList<GitHubPullRequest>> listed = await _github
          .ListPullRequestsAsync(_workspaceRoot, _selector, ct).ConfigureAwait(false);
      if (!listed.IsSuccess)
      {
        GitHubRepository resolved = probe.Value;
        DomainError failure = listed.Error;
        Apply(() => RenderListFailure(resolved, failure));
        return;
      }

      GitHubRepository repository = probe.Value;
      IReadOnlyList<GitHubPullRequest> pullRequests = listed.Value;
      Apply(() => RenderList(repository, pullRequests));
    }
    finally
    {
      _ = _refreshGate.Release();
    }
  }

  /// <summary>Selects a PR and loads its body. Re-selecting the same number is a no-op, so a
  ///     refresh loop cannot re-read what is already shown.</summary>
  public async Task SelectAsync(int number, CancellationToken ct)
  {
    if (_disposed || SelectedNumber == number)
    {
      return;
    }

    SelectedNumber = number;
    if (_github is null || _selector.Length == 0)
    {
      Apply(() => RenderDetailFailure("GitHub views are unavailable in this session."));
      return;
    }

    Result<GitHubPullRequest> detail = await _github
        .GetPullRequestAsync(_workspaceRoot, _selector, number, ct).ConfigureAwait(false);
    if (!detail.IsSuccess)
    {
      string message = $"pull request #{number.ToString(CultureInfo.InvariantCulture)} unavailable: {detail.Error.Message}";
      Apply(() => RenderDetailFailure(message));
      return;
    }

    GitHubPullRequest pullRequest = detail.Value;
    Apply(() =>
    {
      Selected = pullRequest;
      SelectedBody = pullRequest.Body;
      DetailError = null;
    });
  }

  /// <summary>Opens the create form, seeded with the branches the caller already knows (the
  ///     checked-out branch and its base). Nothing is sent until <see cref="SubmitCreateAsync"/>.
  ///     <para>Refused while the pane is not Ready: creating a PR needs a repository to create
  ///     it in, and a form whose submit could only fail is worse than no form.</para></summary>
  public void BeginCreate(string headBranch, string baseBranch)
  {
    if (_disposed || State != GitHubPaneState.Ready)
    {
      return;
    }

    NewHeadBranch = headBranch ?? string.Empty;
    NewBaseBranch = baseBranch ?? string.Empty;
    NewTitle = string.Empty;
    NewBody = string.Empty;
    NewDraft = false;
    CreateError = null;
    IsCreating = true;
  }

  /// <summary>Closes the create form without sending anything.</summary>
  public void CancelCreate()
  {
    IsCreating = false;
    CreateError = null;
  }

  /// <summary>Sends the create request.
  ///     <para>Validation is local and complete BEFORE the subprocess runs: title, head, and
  ///     base are all required by the CLI's non-interactive form, so a missing one is reported
  ///     here rather than as the CLI's prompt failing on a closed stdin.</para>
  ///     <para>A refusal leaves the form OPEN with the CLI's own message, because the usual
  ///     refusal ('a pull request already exists') is answered by editing, not by retyping.</para></summary>
  public async Task SubmitCreateAsync(CancellationToken ct)
  {
    if (_disposed || _github is null || State != GitHubPaneState.Ready)
    {
      return;
    }

    if (string.IsNullOrWhiteSpace(NewTitle))
    {
      CreateError = "A title is required to create a pull request.";
      return;
    }

    if (string.IsNullOrWhiteSpace(NewHeadBranch) || string.IsNullOrWhiteSpace(NewBaseBranch))
    {
      CreateError = "Both the head branch and the base branch are required.";
      return;
    }

    IsSubmitting = true;
    try
    {
      GitHubCreatePullRequestRequest request = new(
          NewTitle.Trim(),
          NewBody,
          NewHeadBranch.Trim(),
          NewBaseBranch.Trim(),
          NewDraft);
      Result<GitHubPullRequest> created = await _github
          .CreatePullRequestAsync(_workspaceRoot, _selector, request, ct).ConfigureAwait(false);
      if (!created.IsSuccess)
      {
        CreateError = created.Error.Message;
        return;
      }

      Apply(() =>
      {
        IsCreating = false;
        CreateError = null;
      });
    }
    finally
    {
      IsSubmitting = false;
    }

    // The new PR must appear without the user asking: the list is the confirmation that the
    // create worked, and a stale list reads as a failure.
    await RefreshAsync(ct).ConfigureAwait(false);
  }

  private void RenderList(GitHubRepository repository, IReadOnlyList<GitHubPullRequest> pullRequests)
  {
    List<PullRequestRowViewModel> rows = [.. pullRequests.Select(PullRequestRowViewModel.For)];
    PullRequests.Clear();
    foreach (PullRequestRowViewModel row in rows)
    {
      PullRequests.Add(row);
    }

    State = GitHubPaneState.Ready;
    IsEmpty = rows.Count == 0;
    EmptyReason = rows.Count == 0
        ? $"No open pull requests in {repository.NameWithOwner}."
        : null;
    StatusLine = $"live \u00B7 {rows.Count.ToString(CultureInfo.InvariantCulture)} open";

    // A PR that left the list (merged, closed) must not stay selected: the detail would
    // describe something the list no longer contains.
    if (SelectedNumber is { } selected && rows.TrueForAll(row => row.Number != selected))
    {
      SelectedNumber = null;
      Selected = null;
      SelectedBody = "";
      DetailError = null;
    }
  }

  /// <summary>Renders one of the three degradation states, each with the sentence naming what
  ///     the user can actually do about it.</summary>
  private void RenderDegraded(DomainError error)
  {
    (GitHubPaneState state, string message, string status) = error.Code switch
    {
      "GhNotInstalled" => (GitHubPaneState.NotInstalled,
          "GitHub views need the gh CLI - install it from cli.github.com, then reopen this view.",
          "gh not installed"),
      "GhNotAuthenticated" => (GitHubPaneState.NotAuthenticated,
          "Run gh auth login to enable GitHub views.",
          "not signed in"),
      "NotAGitHubRepository" => (GitHubPaneState.NotAGitHubRepository,
          "This workspace is not a GitHub repository.",
          "no GitHub repository"),
      _ => (GitHubPaneState.Unavailable,
          $"GitHub views are unavailable: {error.Message}",
          "unavailable"),
    };

    ClearContent();
    IsEmpty = false;
    State = state;
    EmptyReason = message;
    StatusLine = status;
  }

  /// <summary>A repository resolved but its list could not be read. Kept distinct from the
  ///     degradation states: the remedy is 'try again', not 'install something'.</summary>
  private void RenderListFailure(GitHubRepository repository, DomainError error)
  {
    ClearContent();
    IsEmpty = false;
    State = GitHubPaneState.Ready;
    EmptyReason = $"Could not read pull requests for {repository.NameWithOwner}: {error.Message}";
    StatusLine = "read failed";
  }

  private void RenderDetailFailure(string message)
  {
    Selected = null;
    SelectedBody = "";
    DetailError = message;
  }

  private void RenderUnavailable()
  {
    ClearContent();
    IsEmpty = false;
    State = GitHubPaneState.Unavailable;
    EmptyReason = "GitHub views are unavailable in this session.";
    StatusLine = "unavailable";
  }

  /// <summary>Drops everything the pane was showing. Shared by every non-list render so no
  ///     stale row or detail can survive a state change.</summary>
  private void ClearContent()
  {
    PullRequests.Clear();
    SelectedNumber = null;
    Selected = null;
    SelectedBody = "";
    DetailError = null;
    IsCreating = false;
  }

  /// <summary>One tick per interval, refreshing ONLY while the pane is ready. Each tick is a
  ///     subprocess, so a degraded state (missing CLI, logged-out user, non-GitHub workspace)
  ///     skips it entirely: nothing changes between ticks without the user acting, and the
  ///     next explicit refresh re-probes.</summary>
  private async Task PollAsync(CancellationToken ct)
  {
    using PeriodicTimer timer = new(_pollInterval);
    try
    {
      while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
      {
        if (State != GitHubPaneState.Ready)
        {
          continue;
        }

        await RefreshAsync(ct).ConfigureAwait(false);
      }
    }
    catch (OperationCanceledException)
    {
      // Stop() and disposal are the only cancellation sources.
    }
  }

  /// <summary>Applies a render on the UI thread. Every mutation of the observable state goes
  ///     through here: refreshes complete on pool threads.</summary>
  private void Apply(Action render)
  {
    if (_disposed)
    {
      return;
    }

    _marshal(render);
  }

  public void Dispose()
  {
    if (_disposed)
    {
      return;
    }

    _disposed = true;
    _started = false;
    _run?.Cancel();
    _run?.Dispose();
    _run = null;
    _refreshGate.Dispose();
  }
}
