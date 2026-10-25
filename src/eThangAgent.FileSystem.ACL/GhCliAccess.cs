using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.FileSystem.ACL;

/// <summary>One <c>gh</c> invocation's outcome: the exact exit code and the two streams,
///     never merged. A process that could not be STARTED is not a run at all - the runner
///     reports it as a failed <see cref="Result{T}"/> instead, so a missing executable can
///     never be mistaken for a CLI that answered nonzero.</summary>
public sealed record GhCliRun(int ExitCode, string StdOut, string StdErr = "");

/// <summary>The process seam <see cref="GhCliAccess"/> runs behind, so the adapter's
///     argv, JSON parsing, and degradation mapping are testable without <c>gh</c>,
///     authentication, or a network. Production resolves the CLI exactly once and hands
///     the absolute path in; a null result means the executable is genuinely absent.</summary>
public interface IGhCliRunner
{
  Task<Result<GhCliRun>> RunAsync(string exe, string[] args, CancellationToken ct);
}

/// <summary>The production runner: spawns the resolved executable directly through
///     <see cref="ProcessStartInfo"/>, with NO shell intermediary, stdout and stderr
///     captured separately, and the exact exit code propagated.
///     <para>No <c>WorkingDirectory</c> is set: a child whose working directory sits inside
///     the workspace PINS that directory (the <c>DirectGitAccess</c> lesson), and every
///     call names what it needs explicitly - <c>--repo</c> for the repository and
///     <c>git -C &lt;workspace&gt;</c> for the remote lookup.</para>
///     <para>stdin is closed immediately: the CLI prompts on a terminal, and a closed
///     stdin makes an unexpected prompt fail fast instead of hanging a turn.</para></summary>
public sealed class GhProcessRunner : IGhCliRunner
{
  public async Task<Result<GhCliRun>> RunAsync(string exe, string[] args, CancellationToken ct)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(exe);
    ArgumentNullException.ThrowIfNull(args);
    ProcessStartInfo psi = new(exe)
    {
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      RedirectStandardInput = true,
      UseShellExecute = false,
      CreateNoWindow = true,
      StandardOutputEncoding = Encoding.UTF8,
      StandardErrorEncoding = Encoding.UTF8,
    };
    foreach (string argument in args)
    {
      psi.ArgumentList.Add(argument);
    }

    try
    {
      using Process p = Process.Start(psi)!;
      p.StandardInput.Close();
      // Read stderr concurrently with stdout: a full pipe buffer deadlocks the child.
      Task<string> errTask = p.StandardError.ReadToEndAsync(ct);
      string stdout = await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
      string stderr = await errTask.ConfigureAwait(false);
      await p.WaitForExitAsync(ct).ConfigureAwait(false);
      return Result.Success(new GhCliRun(p.ExitCode, stdout, stderr));
    }
    // Named decision (CA1031): a transport failure (the exe vanished between resolution and
    // spawn, or a permission error) surfaces as a typed Result error, never a crash.
#pragma warning disable CA1031 // Do not catch general exception types
    catch (Exception ex)
    {
      return Result.Failure<GhCliRun>(new DomainError("FileSystemError", ex.Message));
    }
#pragma warning restore CA1031 // Do not catch general exception types
  }
}

/// <summary>GitHub pull-request access over the <c>gh</c> CLI (issue #10, design D6).
///     <para>The CLI is a prerequisite by decision: it owns authentication, org policy,
///     SSO, and enterprise hosts, so this adapter stores no credentials and speaks only
///     the CLI's JSON surface (<c>--json</c>), never its screen output.</para>
///     <para>DEGRADATION IS PART OF THE FEATURE. Each of the three failure states maps to
///     its own code - <c>GhNotInstalled</c>, <c>GhNotAuthenticated</c>,
///     <c>NotAGitHubRepository</c> - so the view can name what the user should do. The
///     fingerprints are the CLI's own: an unresolvable executable, no authenticated host
///     (an empty host map, or an exit that asks for a login), and a workspace whose
///     <c>origin</c> is not a GitHub remote (or a host that refuses to resolve it).</para></summary>
public sealed class GhCliAccess(Func<string?> resolveGh, Func<string?> resolveGit, IGhCliRunner runner)
    : IGitHubAccess
{
  /// <summary>The JSON fields every PR read asks for. One list serves both the list and
  ///     the detail call, so the two can never disagree about what a PR contains.</summary>
  internal const string PrFields =
      "number,title,author,state,isDraft,url,headRefName,baseRefName,reviewDecision,statusCheckRollup,updatedAt,body";

  /// <summary>The failure code a 'this workspace has no GitHub repository' answer maps to.</summary>
  internal const string NotAGitHubRepository = "NotAGitHubRepository";

  /// <summary>The failure code for a call that is NOT part of the availability probe: the
  ///     workspace has already resolved to a repository, so 'not a repo' would be a lie and
  ///     the CLI's own message is what the user needs.</summary>
  internal const string GitHubFailure = "GitHubError";

  /// <summary>The page a PR row falls back to when the CLI reports no usable URL: the
  ///     repository's own page, so the pane still has something honest to point at.</summary>
#pragma warning disable S1075 // Anchored fallback: a provider page, not a filesystem path.
  private static readonly Uri FallbackRepositoryUri = new("https://github.com");
#pragma warning restore S1075

  private static readonly Lazy<string?> GhExe = new(() => ResolveExecutable("gh",
  [
      Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GitHub CLI", "gh.exe"),
      Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "GitHub CLI", "gh.exe"),
  ]));

  private static readonly Lazy<string?> GitExe = new(() => ResolveExecutable("git",
  [
      Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "cmd", "git.exe"),
      Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Git", "cmd", "git.exe"),
  ]));

  private readonly Func<string?> _resolveGh = resolveGh ?? throw new ArgumentNullException(nameof(resolveGh));
  private readonly Func<string?> _resolveGit = resolveGit ?? throw new ArgumentNullException(nameof(resolveGit));
  private readonly IGhCliRunner _runner = runner ?? throw new ArgumentNullException(nameof(runner));

  /// <summary>Production wiring: the CLI and git are resolved once per process (a
  ///     <see cref="Lazy{T}"/> over the standard install layouts, then PATH - the
  ///     <c>DirectGitAccess</c> resolution shape), so a session never re-probes the
  ///     filesystem on every poll.</summary>
  public static GhCliAccess CreateForProcess() =>
      new(() => GhExe.Value, () => GitExe.Value, new GhProcessRunner());

  /// <summary>Probes the standard install layouts, then PATH. A null result is the honest
  ///     'not installed' answer: the caller turns it into <c>GhNotInstalled</c> and the view
  ///     says so, rather than spawning a bare name and failing somewhere less legible.</summary>
  internal static string? ResolveExecutable(string name, string[] candidates)
  {
    string? installed = candidates.FirstOrDefault(File.Exists);
    if (installed is not null)
    {
      return installed;
    }

    // System32\where.exe is always present, so the PATH probe goes through an absolute path
    // instead of trusting whatever PATH the app inherited.
    try
    {
      ProcessStartInfo psi = new(Path.Combine(Environment.SystemDirectory, "where.exe"), name)
      {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        CreateNoWindow = true,
      };
      using Process where = Process.Start(psi)!;
      string? line = where.StandardOutput.ReadLine();
      where.WaitForExit();
      string candidate = line?.Trim() ?? "";
      return candidate.Length > 0 && File.Exists(candidate) ? candidate : null;
    }
    // Named decision (CA1031): path probing is best effort; a probe failure means 'not found'.
#pragma warning disable CA1031 // Do not catch general exception types
    catch (Exception)
    {
      return null;
    }
#pragma warning restore CA1031 // Do not catch general exception types
  }

  public async Task<Result<GitHubRepository>> ProbeAsync(string workspacePath, CancellationToken ct = default)
  {
    // The host set decides BOTH questions at once: whether anyone is logged in, and whether
    // this workspace's remote host is a GitHub host at all. A remote on a host gh does not
    // know is not a GitHub repository (gitlab.com parses exactly like github.com), while an
    // enterprise host gh HAS authenticated is one - so the host list is the authority rather
    // than any hardcoded allowlist.
    Result<IReadOnlyList<string>> auth = await EnsureAuthenticatedAsync(ct).ConfigureAwait(false);
    if (!auth.IsSuccess)
    {
      return Result.Failure<GitHubRepository>(auth.Error);
    }

    Result<GitHubRemote> remote = await ResolveRemoteAsync(workspacePath, ct).ConfigureAwait(false);
    if (!remote.IsSuccess)
    {
      return Result.Failure<GitHubRepository>(remote.Error);
    }

    if (!auth.Value.Contains(remote.Value.Host, StringComparer.OrdinalIgnoreCase))
    {
      return Result.Failure<GitHubRepository>(new DomainError(NotAGitHubRepository,
          $"The workspace's origin is on '{remote.Value.Host}', which is not a GitHub host gh is authenticated for."));
    }

    // Name the repository by asking the CLI, not by re-parsing the remote: the remote says
    // where a push goes and the provider says what the repository is called (a rename, a
    // redirect), and the pane shows the latter.
    return await RunJsonAsync(
        ["repo", "view", remote.Value.Selector, "--json", "nameWithOwner,url"],
        NotAGitHubRepository,
        root =>
        {
          string name = ReadString(root, "nameWithOwner") ?? remote.Value.OwnerRepo;
          string urlText = ReadString(root, "url") ?? $"https://{remote.Value.Host}/{name}";
          return Url(urlText) is { } url
              ? Result.Success(GitHubRepository.FromRemote(name, url, remote.Value.Host))
              : Result.Failure<GitHubRepository>(new DomainError("GitHubParseError",
                  $"gh reported an unusable repository URL '{urlText}'."));
        },
        ct).ConfigureAwait(false);
  }

  public async Task<Result<IReadOnlyList<GitHubPullRequest>>> ListPullRequestsAsync(
      string workspacePath, string selector, CancellationToken ct = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(selector);
    ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
    return await RunJsonAsync(
        ["pr", "list", "--repo", selector, "--state", "open", "--limit", "50", "--json", PrFields],
        GitHubFailure,
        ParsePullRequestList,
        ct).ConfigureAwait(false);
  }

  public async Task<Result<GitHubPullRequest>> GetPullRequestAsync(
      string workspacePath, string selector, int number, CancellationToken ct = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(selector);
    ArgumentOutOfRangeException.ThrowIfLessThan(number, 1);
    return await RunJsonAsync(
        ["pr", "view", number.ToString(CultureInfo.InvariantCulture), "--repo", selector, "--json", PrFields],
        GitHubFailure,
        root => ParsePullRequest(root) is { } pr
            ? Result.Success(pr)
            : Result.Failure<GitHubPullRequest>(new DomainError("GitHubParseError",
                $"gh pr view returned no readable pull request #{number.ToString(CultureInfo.InvariantCulture)}.")),
        ct).ConfigureAwait(false);
  }

  public async Task<Result<GitHubPullRequest>> CreatePullRequestAsync(
      string workspacePath, string selector, GitHubCreatePullRequestRequest request, CancellationToken ct = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(selector);
    ArgumentNullException.ThrowIfNull(request);
    List<string> args =
    [
      "pr", "create",
      "--repo", selector,
      "--head", request.HeadBranch,
      "--base", request.BaseBranch,
      "--title", request.Title,
      "--body", request.Body,
    ];
    if (request.Draft)
    {
      args.Add("--draft");
    }

    Result<GhCliRun> run = await RunCliAsync([.. args], ct).ConfigureAwait(false);
    if (!run.IsSuccess)
    {
      return Result.Failure<GitHubPullRequest>(run.Error);
    }

    if (run.Value.ExitCode != 0)
    {
      return Result.Failure<GitHubPullRequest>(FailureFor(run.Value, GitHubFailure));
    }

    // The CLI prints the new PR's URL on success. Reading the PR back is what gives the
    // caller its number and its CI state, so a create is never reported from a bare URL.
    string url = FirstNonEmptyLine(run.Value.StdOut);
    int number = ParsePullNumber(url);
    return number > 0
        ? await GetPullRequestAsync(workspacePath, selector, number, ct).ConfigureAwait(false)
        : Result.Failure<GitHubPullRequest>(new DomainError("GitHubParseError",
            $"gh pr create reported no pull request number: '{url}'."));
  }

  /// <summary>Reads the pull request number out of the URL <c>gh pr create</c> prints.
  ///     A URL with no trailing number is not a PR, so it yields 0 and the caller says so.</summary>
  internal static int ParsePullNumber(string url)
  {
    if (string.IsNullOrWhiteSpace(url))
    {
      return 0;
    }

    string trimmed = url.Trim().TrimEnd('/');
    int slash = trimmed.LastIndexOf('/');
    return slash >= 0
        && int.TryParse(trimmed[(slash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)
        ? number
        : 0;
  }

  private static string FirstNonEmptyLine(string text) => text
      .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
      .Select(line => line.Trim())
      .FirstOrDefault(line => line.Length > 0) ?? "";

  /// <summary>Parses the list response. A response that is not an array is a contract
  ///     violation the caller must see; a single unreadable ROW is skipped rather than
  ///     blanking the list.</summary>
  private static Result<IReadOnlyList<GitHubPullRequest>> ParsePullRequestList(JsonElement root)
  {
    if (root.ValueKind != JsonValueKind.Array)
    {
      return Result.Failure<IReadOnlyList<GitHubPullRequest>>(new DomainError("GitHubParseError",
          "gh pr list did not return an array of pull requests."));
    }

    List<GitHubPullRequest> pullRequests = [];
    foreach (JsonElement element in root.EnumerateArray())
    {
      if (ParsePullRequest(element) is { } pullRequest)
      {
        pullRequests.Add(pullRequest);
      }
    }

    return Result.Success<IReadOnlyList<GitHubPullRequest>>(pullRequests);
  }

  /// <summary>Parses one PR object from the CLI's JSON. Returns null only when the object
  ///     carries no number at all - a row nobody could identify is better skipped than
  ///     rendered as '#0'.</summary>
  internal static GitHubPullRequest? ParsePullRequest(JsonElement element) =>
      element.ValueKind == JsonValueKind.Object
      && element.TryGetProperty("number", out JsonElement numberElement)
      && numberElement.TryGetInt32(out int number)
          ? BuildPullRequest(element, number)
          : null;

  /// <summary>Builds the record once the object is known to be a PR. Split out of
  ///     <see cref="ParsePullRequest"/> so the guard reads as a guard.</summary>
  private static GitHubPullRequest BuildPullRequest(JsonElement element, int number) =>
      new(
        number,
        ReadString(element, "title") ?? "(untitled)",
        ReadAuthor(element),
        ReadString(element, "headRefName") ?? "",
        ReadString(element, "baseRefName") ?? "",
        PullRequestUrl(element),
        element.TryGetProperty("isDraft", out JsonElement draft) && draft.ValueKind == JsonValueKind.True,
        ReadReviewDecision(element),
        SummarizeChecks(element),
        ReadUpdatedAt(element),
        ReadString(element, "body") ?? "");

  /// <summary>The PR's page URL. A row with no usable URL still needs a value (the record
  ///     is provider-neutral and the pane shows the URL as a hint), so the repository's own
  ///     page is the honest fallback rather than an invented pull URL.</summary>
  private static Uri PullRequestUrl(JsonElement element) =>
      Url(ReadString(element, "url") ?? "") ?? FallbackRepositoryUri;

  /// <summary>When the PR was last touched. An unparseable timestamp keeps the row (it is
  ///     ordered by the provider's list, not by this value) and reports the absence as
  ///     <see cref="DateTimeOffset.MinValue"/> - never 'now', which would rank a stale PR
  ///     as the freshest thing on screen.</summary>
  private static DateTimeOffset ReadUpdatedAt(JsonElement element) =>
      DateTimeOffset.TryParse(
          ReadString(element, "updatedAt"),
          CultureInfo.InvariantCulture,
          DateTimeStyles.AdjustToUniversal,
          out DateTimeOffset parsed)
          ? parsed
          : DateTimeOffset.MinValue;

  private static string ReadAuthor(JsonElement element) =>
      element.TryGetProperty("author", out JsonElement author) && author.ValueKind == JsonValueKind.Object
          ? ReadString(author, "login") ?? ""
          : "";

  /// <summary>The review state, normalised to null when nobody has decided. The CLI reports
  ///     <c>""</c> for 'no decision' in some versions and omits the field in others; both
  ///     mean the same thing and neither should render as a decision.</summary>
  internal static string? ReadReviewDecision(JsonElement element)
  {
    string? decision = ReadString(element, "reviewDecision");
    return string.IsNullOrWhiteSpace(decision) ? null : decision;
  }

  /// <summary>Reduces <c>statusCheckRollup</c> to the counts a row renders.
  ///     <para>Each check is classified by <see cref="ClassifyCheck"/>: a deliberately
  ///     skipped job is not a failure (treating it as one would paint a correctly triaged PR
  ///     red), an unfinished one is pending, and anything nobody can classify counts as
  ///     failing - a check whose outcome is unknown is not evidence of health.</para></summary>
  internal static GitHubCheckSummary SummarizeChecks(JsonElement element)
  {
    if (!element.TryGetProperty("statusCheckRollup", out JsonElement rollup)
        || rollup.ValueKind != JsonValueKind.Array)
    {
      return GitHubCheckSummary.None;
    }

    int total = 0;
    int passing = 0;
    int failing = 0;
    int pending = 0;
    foreach (JsonElement check in rollup.EnumerateArray())
    {
      if (check.ValueKind != JsonValueKind.Object)
      {
        continue;
      }

      total++;
      GitHubCheckState state = ClassifyCheck(ReadString(check, "conclusion"), ReadString(check, "status") ?? "");
      passing += state == GitHubCheckState.Passing ? 1 : 0;
      pending += state == GitHubCheckState.Pending ? 1 : 0;
      failing += state == GitHubCheckState.Failing ? 1 : 0;
    }

    return new GitHubCheckSummary(total, passing, failing, pending);
  }

  /// <summary>Classifies one check. A COMPLETED check with no conclusion FAILS rather than
  ///     pending: it will never resolve, and a spinner that never stops is worse than a red
  ///     mark that names itself.</summary>
  private static GitHubCheckState ClassifyCheck(string? conclusion, string status)
  {
    if (conclusion is "SUCCESS" or "NEUTRAL" or "SKIPPED")
    {
      return GitHubCheckState.Passing;
    }

    if (conclusion is null or "")
    {
      // A COMPLETED check with no conclusion will never resolve; calling it pending would
      // render a spinner that never stops.
      return status.Equals("COMPLETED", StringComparison.OrdinalIgnoreCase)
          ? GitHubCheckState.Failing
          : GitHubCheckState.Pending;
    }

    return GitHubCheckState.Failing;
  }

  private enum GitHubCheckState
  {
    Passing,
    Pending,
    Failing,
  }

  private static string? ReadString(JsonElement element, string name) =>
      element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
          ? value.GetString()
          : null;

  private static Uri? Url(string text) =>
      !string.IsNullOrWhiteSpace(text) && Uri.TryCreate(text, UriKind.Absolute, out Uri? parsed) ? parsed : null;

  /// <summary>Maps one failed invocation onto a typed error. Unauthenticated wins over
  ///     everything else because it is the state whose remedy (<c>gh auth login</c>) no
  ///     other message would tell the user.</summary>
  internal static DomainError FailureFor(GhCliRun run, string fallbackCode)
  {
    string stderr = run.StdErr.Trim();
    string code = run.ExitCode == 4 || stderr.Contains("gh auth login", StringComparison.OrdinalIgnoreCase)
        ? "GhNotAuthenticated"
        : fallbackCode;
    string message = stderr.Length > 0
        ? stderr
        : $"gh exited {run.ExitCode.ToString(CultureInfo.InvariantCulture)} with no error output.";
    return new DomainError(code, message);
  }

  /// <summary>Runs a JSON-producing command behind the shared guards: the CLI must exist and
  ///     a nonzero exit surfaces as a typed failure carrying the CLI's own words.</summary>
  private async Task<Result<T>> RunJsonAsync<T>(
      string[] args,
      string failureCode,
      Func<JsonElement, Result<T>> parse,
      CancellationToken ct)
  {
    Result<GhCliRun> run = await RunCliAsync(args, ct).ConfigureAwait(false);
    if (!run.IsSuccess)
    {
      return Result.Failure<T>(run.Error);
    }

    if (run.Value.ExitCode != 0)
    {
      return Result.Failure<T>(FailureFor(run.Value, failureCode));
    }

    try
    {
      using JsonDocument doc = JsonDocument.Parse(run.Value.StdOut);
      return parse(doc.RootElement);
    }
    // Named decision (CA1031): malformed JSON from an external tool is an expected failure,
    // reported as a typed error rather than an exception.
#pragma warning disable CA1031 // Do not catch general exception types
    catch (Exception ex)
    {
      return Result.Failure<T>(new DomainError("GitHubParseError",
          $"gh returned unreadable JSON: {ex.Message}"));
    }
#pragma warning restore CA1031 // Do not catch general exception types
  }

  private async Task<Result<GhCliRun>> RunCliAsync(string[] args, CancellationToken ct)
  {
    string? exe = _resolveGh();
    return exe is null
        ? Result.Failure<GhCliRun>(NotInstalled)
        : await _runner.RunAsync(exe, args, ct).ConfigureAwait(false);
  }

  internal static DomainError NotInstalled => new("GhNotInstalled",
      "The gh CLI is not installed, so GitHub views have no data source.");

  internal static DomainError NotAuthenticated(string message) => new("GhNotAuthenticated", message);

  /// <summary>Confirms a host holds a credential.
  ///     <para>The status BODY is the reliable signal, not the exit code: an empty host map
  ///     exits 0 and still means nobody is logged in (observed), while a missing login exits
  ///     4. Both are the same answer to the user.</para></summary>
  private async Task<Result<IReadOnlyList<string>>> EnsureAuthenticatedAsync(CancellationToken ct)
  {
    string? exe = _resolveGh();
    if (exe is null)
    {
      return Result.Failure<IReadOnlyList<string>>(NotInstalled);
    }

    Result<GhCliRun> run = await _runner
        .RunAsync(exe, ["auth", "status", "--json", "hosts"], ct).ConfigureAwait(false);
    if (!run.IsSuccess)
    {
      return Result.Failure<IReadOnlyList<string>>(run.Error);
    }

    if (run.Value.ExitCode == 0 && AuthenticatedHosts(run.Value.StdOut) is { Count: > 0 } hosts)
    {
      return Result.Success(hosts);
    }

    string said = run.Value.StdErr.Trim();
    return Result.Failure<IReadOnlyList<string>>(NotAuthenticated(said.Length > 0
        ? said
        : "No GitHub host is authenticated. Run 'gh auth login'."));
  }

  /// <summary>The hosts <c>gh auth status --json hosts</c> reports an authenticated profile
  ///     for. A host counts only when one of its profiles reports <c>state = success</c> - a
  ///     profile that exists but failed to authenticate is not a credential.
  ///     <para>This set is the authority on WHICH hosts exist, not just whether one is
  ///     logged in: a remote on a host gh does not know (a GitLab clone) is not a GitHub
  ///     repository at all, while an enterprise host gh has authenticated is one. Nothing
  ///     here hardcodes hostnames, so a GitHub Enterprise install works without a code
  ///     change.</para></summary>
  internal static IReadOnlyList<string> AuthenticatedHosts(string json)
  {
    try
    {
      using JsonDocument doc = JsonDocument.Parse(json);
      if (!doc.RootElement.TryGetProperty("hosts", out JsonElement hosts)
          || hosts.ValueKind != JsonValueKind.Object)
      {
        return [];
      }

      List<string> authenticated = [];
      foreach (JsonProperty host in hosts.EnumerateObject())
      {
        if (host.Value.ValueKind != JsonValueKind.Array)
        {
          continue;
        }

        bool usable = host.Value.EnumerateArray().Any(profile =>
            profile.ValueKind == JsonValueKind.Object
            && string.Equals(ReadString(profile, "state"), "success", StringComparison.OrdinalIgnoreCase));
        if (usable)
        {
          authenticated.Add(host.Name);
        }
      }

      return authenticated;
    }
    // Named decision (CA1031): an unparseable status body is 'no authenticated host'.
#pragma warning disable CA1031 // Do not catch general exception types
    catch (Exception)
    {
      return [];
    }
#pragma warning restore CA1031 // Do not catch general exception types
  }

  /// <summary>Reads the workspace's <c>origin</c> URL through git and parses it. Every
  ///     'not a repo', 'no remotes', and 'not GitHub' outcome lands on
  ///     <c>NotAGitHubRepository</c>, because to a person all three mean the same thing:
  ///     this workspace has no GitHub repository behind it.</summary>
  private async Task<Result<GitHubRemote>> ResolveRemoteAsync(string workspacePath, CancellationToken ct)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
    string? git = _resolveGit();
    if (git is null)
    {
      return Result.Failure<GitHubRemote>(new DomainError(NotAGitHubRepository,
          "git is not installed, so the workspace's GitHub remote cannot be resolved."));
    }

    Result<GhCliRun> run = await _runner
        .RunAsync(git, ["-C", workspacePath, "remote", "get-url", "origin"], ct).ConfigureAwait(false);
    if (!run.IsSuccess)
    {
      return Result.Failure<GitHubRemote>(run.Error);
    }

    if (run.Value.ExitCode != 0)
    {
      string said = run.Value.StdErr.Trim();
      return Result.Failure<GitHubRemote>(new DomainError(NotAGitHubRepository,
          said.Length > 0 ? said : "The workspace has no 'origin' remote."));
    }

    string remote = run.Value.StdOut.Trim();
    return TryParseRemote(remote, out GitHubRemote parsed)
        ? Result.Success(parsed)
        : Result.Failure<GitHubRemote>(new DomainError(NotAGitHubRepository,
            $"The workspace's origin '{remote}' is not a GitHub remote."));
  }

  /// <summary>Parses a git remote URL into its host and <c>owner/name</c> pair.
  ///     <para>All the remote spellings a person actually has are accepted - scp-style
  ///     <c>git@host:owner/name.git</c>, <c>https://host/owner/name(.git)</c>, and
  ///     <c>ssh://git@host/owner/name.git</c> - because the same repository is cloned each
  ///     of those ways and a view that understood only one would look unconfigured. A local
  ///     path is refused: it is not a remote at all.</para></summary>
  public static bool TryParseRemote(string remote, out GitHubRemote parsed)
  {
    parsed = null!;
    if (string.IsNullOrWhiteSpace(remote))
    {
      return false;
    }

    string text = remote.Trim();
    string host;
    string path;
    int schemeSeparator = text.IndexOf("://", StringComparison.Ordinal);
    if (schemeSeparator > 0)
    {
      if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? uri) || uri.Host.Length == 0)
      {
        return false;
      }

      host = uri.Host;
      path = uri.AbsolutePath;
    }
    else
    {
      // scp-style: [user@]host:owner/name
      int at = text.IndexOf('@', StringComparison.Ordinal);
      int colon = text.IndexOf(':', at + 1);
      if (colon <= at + 1)
      {
        return false;
      }

      host = text[(at + 1)..colon];
      path = text[(colon + 1)..];
    }

    string trimmedPath = path.Trim('/');
    if (trimmedPath.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
    {
      trimmedPath = trimmedPath[..^4];
    }

    string[] segments = trimmedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
    if (host.Length == 0 || segments.Length != 2)
    {
      return false;
    }

    parsed = new GitHubRemote(host, $"{segments[0]}/{segments[1]}");
    return true;
  }
}
