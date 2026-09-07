using System.Diagnostics;
using System.Text;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.FileSystem.ACL;

/// <summary>
/// Git worktree operations (list, create, remove) implemented by shelling out to the
/// <c>git</c> CLI directly via <see cref="ProcessStartInfo"/> (no shell intermediary).
/// Same discipline as <see cref="DirectGitAccess"/>: the git executable is resolved
/// ONCE through an absolute path (S4036), every invocation is anchored with <c>-C</c>,
/// stdout/stderr are captured separately (exact exit codes, no stream merging), and
/// every git failure maps to a named <see cref="DomainError"/> carrying git's stderr
/// tail. Worktrees are created under <c>repoRoot/.worktrees/NAME</c> on branches
/// named <c>worktree/NAME</c>; the <c>.worktrees/</c> directory is registered in the
/// repository's exclude file — its path resolved BY GIT across repo layouts (normal,
/// linked-worktree, bare) — so created worktrees never pollute the repository's status.
/// </summary>
public sealed class GitWorktreeAccess : IGitWorktreeAccess
{
  private const string RevParse = "rev-parse";
  private const string WorktreesDir = ".worktrees";
  private const string BranchPrefix = "worktree/";
  private const string ExcludeEntry = ".worktrees/";
  private const string DetachedMarker = "(detached)";
  private const string HeadsPrefix = "refs/heads/";

  private static readonly string[] NewLines = ["\r\n", "\n"];

  // Resolved once: spawning git through an absolute path keeps the executable
  // from being shadowed by a manipulated PATH (S4036).
  private static readonly Lazy<string> GitExePath = new(ResolveGitExecutable);

  public async Task<Result<IReadOnlyList<WorktreeInfo>>> ListAsync(string repoRoot, CancellationToken ct = default)
  {
    Result<GitRun> run = await RunGitVerifiedAsync(repoRoot, ["worktree", "list", "--porcelain"], ct).ConfigureAwait(false);
    if (!run.IsSuccess)
    {
      return Result.Failure<IReadOnlyList<WorktreeInfo>>(run.Error);
    }

    List<WorktreeInfo> worktrees = [];
    foreach (WorktreeEntry entry in ParseWorktreeList(run.Value.StdOut))
    {
      bool isMain = IsSamePath(entry.Path, repoRoot);
      bool isDirty = false;
      // 'git status' fails inside a bare repo, so bare entries alone skip the
      // dirty probe; detached worktrees still get the real check.
      if (!entry.IsBare)
      {
        Result<bool> dirty = await IsDirtyAsync(entry.Path, ct).ConfigureAwait(false);
        if (!dirty.IsSuccess)
        {
          return Result.Failure<IReadOnlyList<WorktreeInfo>>(dirty.Error);
        }

        isDirty = dirty.Value;
      }

      worktrees.Add(new WorktreeInfo(
          Name: Path.GetFileName(entry.Path),
          Path: entry.Path,
          Branch: entry.Branch ?? DetachedMarker,
          HeadShortSha: entry.Head.Length >= 7 ? entry.Head[..7] : entry.Head,
          IsMain: isMain,
          IsDirty: isDirty));
    }

    return Result.Success<IReadOnlyList<WorktreeInfo>>(worktrees);
  }

  public async Task<Result<WorktreeInfo>> CreateAsync(string repoRoot, string name, CancellationToken ct = default)
  {
    if (name is null)
    {
      // WorktreeName.Create throws on null; expected failures stay Result-typed.
      return Result.Failure<WorktreeInfo>(InvalidNameError("null"));
    }

    Result<WorktreeName> validated = WorktreeName.Create(name);
    if (!validated.IsSuccess)
    {
      return Result.Failure<WorktreeInfo>(validated.Error);
    }

    string targetPath = Path.Combine(repoRoot, WorktreesDir, validated.Value.Value);

    // A worktree already registered at the target path is a hard duplicate.
    Result<WorktreeEntry?> registered = await FindRegisteredWorktreeAsync(repoRoot, targetPath, ct).ConfigureAwait(false);
    if (!registered.IsSuccess)
    {
      return Result.Failure<WorktreeInfo>(registered.Error);
    }

    if (registered.Value is not null)
    {
      return Result.Failure<WorktreeInfo>(new DomainError("WorktreeExists",
          $"A worktree already exists at {targetPath}."));
    }

    string branch = BranchPrefix + validated.Value.Value;
    GitRun branchProbe = await RunGitAsync(repoRoot, [RevParse, "--verify", "--quiet", $"refs/heads/{branch}"], ct).ConfigureAwait(false);
    if (!branchProbe.Ok)
    {
      return Result.Failure<WorktreeInfo>(branchProbe.Err);
    }

    // The nonzero exit IS the signal that the branch name is free to claim.
    if (branchProbe.ExitCode == 0)
    {
      return Result.Failure<WorktreeInfo>(new DomainError("BranchExists",
          $"Branch '{branch}' already exists; pick a different worktree name."));
    }

    if (Directory.Exists(targetPath))
    {
      // The path is not a registered worktree (checked above), so anything
      // here is a stale remnant: prune git's dead admin data, then clear the
      // directory so 'worktree add' can claim the path. Blast radius is
      // bounded — validated names cannot traverse, and the directory sits in
      // the reserved .worktrees/ area this class owns.
      Result<GitRun> prune = await RunGitVerifiedAsync(repoRoot, ["worktree", "prune"], ct).ConfigureAwait(false);
      if (!prune.IsSuccess)
      {
        return Result.Failure<WorktreeInfo>(prune.Error);
      }

      try
      {
        Directory.Delete(targetPath, true);
      }
      catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
      {
        // Same crash family as the old exclude write: ToolExecution catches only
        // cancellation, so a stale-dir removal failure must land as a named error.
        return Result.Failure<WorktreeInfo>(StaleDirectoryRemovalFailed(targetPath, ex.Message));
      }
    }

    _ = Directory.CreateDirectory(Path.Combine(repoRoot, WorktreesDir));
    Result<bool> excluded = await EnsureWorktreesExcludedAsync(repoRoot, ct).ConfigureAwait(false);
    if (!excluded.IsSuccess)
    {
      return Result.Failure<WorktreeInfo>(excluded.Error);
    }

    Result<GitRun> add = await RunGitVerifiedAsync(repoRoot, ["worktree", "add", "-b", branch, targetPath, "HEAD"], ct).ConfigureAwait(false);
    if (!add.IsSuccess)
    {
      return Result.Failure<WorktreeInfo>(add.Error);
    }

    GitRun head = await RunGitAsync(repoRoot, [RevParse, "--short=7", "HEAD"], ct).ConfigureAwait(false);
    if (!head.Ok)
    {
      return Result.Failure<WorktreeInfo>(head.Err);
    }

    if (head.ExitCode != 0)
    {
      return Result.Failure<WorktreeInfo>(ToGitFailure(repoRoot, head.ExitCode, head.StdErr));
    }

    WorktreeInfo created = new(
        Name: validated.Value.Value,
        Path: targetPath,
        Branch: branch,
        HeadShortSha: head.StdOut.Trim(),
        IsMain: false,
        IsDirty: false);
    return Result.Success(created);
  }

  public async Task<Result<bool>> RemoveAsync(string repoRoot, string name, bool force, CancellationToken ct = default)
  {
    if (name is null)
    {
      // WorktreeName.Create throws on null; expected failures stay Result-typed.
      return Result.Failure<bool>(InvalidNameError("null"));
    }

    Result<WorktreeName> validated = WorktreeName.Create(name);
    if (!validated.IsSuccess)
    {
      return Result.Failure<bool>(validated.Error);
    }

    string targetPath = Path.Combine(repoRoot, WorktreesDir, validated.Value.Value);
    Result<WorktreeEntry?> registered = await FindRegisteredWorktreeAsync(repoRoot, targetPath, ct).ConfigureAwait(false);
    if (!registered.IsSuccess)
    {
      return Result.Failure<bool>(registered.Error);
    }

    if (registered.Value is null)
    {
      return Result.Failure<bool>(new DomainError("WorktreeNotFound",
          $"No worktree named '{validated.Value.Value}' exists under {Path.Combine(repoRoot, WorktreesDir)}."));
    }

    // Defense in depth: names under .worktrees can never denote the main
    // worktree, but the refusal is the documented contract.
    if (IsSamePath(registered.Value.Path, repoRoot))
    {
      return Result.Failure<bool>(new DomainError("MainWorktree",
          "The main worktree cannot be removed."));
    }

    Result<bool> dirty = await IsDirtyAsync(registered.Value.Path, ct).ConfigureAwait(false);
    if (!dirty.IsSuccess)
    {
      return Result.Failure<bool>(dirty.Error);
    }

    if (dirty.Value && !force)
    {
      return Result.Failure<bool>(new DomainError("WorktreeDirty",
          $"Worktree '{validated.Value.Value}' has uncommitted changes; pass force to discard them."));
    }

    string[] args = force ? ["worktree", "remove", "--force", targetPath] : ["worktree", "remove", targetPath];
    Result<GitRun> removed = await RunGitVerifiedAsync(repoRoot, args, ct).ConfigureAwait(false);
    return removed.IsSuccess ? Result.Success(true) : Result.Failure<bool>(removed.Error);
  }

  /// <summary>Runs 'git -C WORKTREE status --porcelain' and reports whether
  ///     the output is non-empty (any tracked modification or untracked file).</summary>
  private static async Task<Result<bool>> IsDirtyAsync(string worktreePath, CancellationToken ct)
  {
    Result<GitRun> run = await RunGitVerifiedAsync(worktreePath, ["status", "--porcelain"], ct).ConfigureAwait(false);
    return run.IsSuccess
        ? Result.Success(run.Value.StdOut.Trim().Length > 0)
        : Result.Failure<bool>(run.Error);
  }

  private static async Task<Result<WorktreeEntry?>> FindRegisteredWorktreeAsync(string repoRoot, string targetPath, CancellationToken ct)
  {
    Result<GitRun> run = await RunGitVerifiedAsync(repoRoot, ["worktree", "list", "--porcelain"], ct).ConfigureAwait(false);
    if (!run.IsSuccess)
    {
      return Result.Failure<WorktreeEntry?>(run.Error);
    }

    WorktreeEntry? match = ParseWorktreeList(run.Value.StdOut)
        .FirstOrDefault(entry => IsSamePath(entry.Path, targetPath));
    return Result.Success(match);
  }

  /// <summary>Splits 'git worktree list --porcelain' output into block records.
  ///     Each block starts at a 'worktree PATH' line; 'HEAD SHA', 'branch REF',
  ///     and 'bare' lines refine it, any other line is ignored. Detachedness is
  ///     conveyed by the absent branch line — never conflated with bare.</summary>
  private static List<WorktreeEntry> ParseWorktreeList(string stdout)
  {
    List<WorktreeEntry> entries = [];
    string? path = null;
    string? head = null;
    string? branch = null;
    bool isBare = false;

    void Flush()
    {
      if (path is not null)
      {
        entries.Add(new WorktreeEntry(path, head ?? "", branch, isBare));
      }
    }

    string[] lines = stdout.Split(NewLines, StringSplitOptions.RemoveEmptyEntries);
    foreach (string line in lines)
    {
      if (line.StartsWith("worktree ", StringComparison.Ordinal))
      {
        Flush();
        path = line["worktree ".Length..];
        head = null;
        branch = null;
        isBare = false;
      }
      else if (line.StartsWith("HEAD ", StringComparison.Ordinal))
      {
        head = line["HEAD ".Length..].Trim();
      }
      else if (line.StartsWith("branch ", StringComparison.Ordinal))
      {
        string reference = line["branch ".Length..].Trim();
        branch = reference.StartsWith(HeadsPrefix, StringComparison.Ordinal)
            ? reference[HeadsPrefix.Length..]
            : reference;
      }
      else if (line == "bare")
      {
        isBare = true;
      }
    }

    Flush();
    return entries;
  }

  /// <summary>Appends '.worktrees/' to the repository's exclude file when absent so
  ///     created worktrees never pollute the repository's status. The exclude path is
  ///     resolved BY GIT ('rev-parse --path-format=absolute --git-path') so every repo
  ///     layout resolves correctly: a normal repository, a linked worktree (whose .git
  ///     is a file with a gitdir pointer — hand-joining <c>root/.git/info</c> would throw
  ///     IOException), and a bare repository alike. Writing is skipped when the file's
  ///     directory does not exist (a bare repository ships none); any remaining failure
  ///     is a named <c>ExcludeRegistrationFailed</c> error, never an exception —
  ///     ToolExecution catches only cancellation, so an unhandled throw here would
  ///     crash the turn instead of reaching the model as an error result.</summary>
  private static async Task<Result<bool>> EnsureWorktreesExcludedAsync(string repoRoot, CancellationToken ct)
  {
    GitRun resolved = await RunGitAsync(repoRoot,
        [RevParse, "--path-format=absolute", "--git-path", "info/exclude"], ct).ConfigureAwait(false);
    if (!resolved.Ok)
    {
      return Result.Failure<bool>(resolved.Err);
    }

    if (resolved.ExitCode != 0)
    {
      return Result.Failure<bool>(ToGitFailure(repoRoot, resolved.ExitCode, resolved.StdErr));
    }

    string excludePath = resolved.StdOut.Trim();
    if (excludePath.Length == 0)
    {
      return Result.Failure<bool>(ExcludeRegistrationFailed(repoRoot,
          "git resolved an empty exclude path"));
    }

    // A directory that does not exist and cannot be created safely (a bare repo's
    // admin area ships no info/) means there is no exclude file to register into —
    // skip, not fail: exclusion is a courtesy, and writing a bogus TREE into a bare
    // repo was the old bug this resolution exists to prevent.
    string? excludeDir = Path.GetDirectoryName(excludePath);
    if (excludeDir is not null && !Directory.Exists(excludeDir))
    {
      try
      {
        _ = Directory.CreateDirectory(excludeDir);
      }
      catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
      {
        return Result.Success(true);
      }
    }

    string existing = File.Exists(excludePath)
        ? await File.ReadAllTextAsync(excludePath, ct).ConfigureAwait(false)
        : string.Empty;
    if (existing.Contains(ExcludeEntry, StringComparison.Ordinal))
    {
      return Result.Success(true);
    }

    try
    {
      string prefix = existing.Length > 0 && !existing.EndsWith('\n') ? "\n" : string.Empty;
      await File.AppendAllTextAsync(excludePath, prefix + ExcludeEntry + "\n", ct).ConfigureAwait(false);
      return Result.Success(true);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
      return Result.Failure<bool>(ExcludeRegistrationFailed(excludePath, ex.Message));
    }
  }

  private static DomainError ExcludeRegistrationFailed(string target, string detail)
      => new("ExcludeRegistrationFailed",
          $"Could not register '{WorktreesDir}/' in the repository's exclude file ({target}): " +
          detail + ". Worktree creation is unaffected; the entry may pollute 'git status'.");

  private static DomainError StaleDirectoryRemovalFailed(string target, string detail)
      => new("StaleDirectoryRemovalFailed",
          $"A stale directory occupies '{target}' and could not be removed: " +
          detail + ". Remove it manually, then retry.");

  private static DomainError InvalidNameError(string shown)
      => new("InvalidName",
          $"'{shown}' is not a valid worktree name; use 1..{WorktreeName.MaxLength} characters of " +
          "lowercase letters, digits, and hyphens (^[a-z0-9-]+$), with no trimming.");

  /// <summary>Ordinal, case-insensitive comparison of two absolute paths with
  ///     normalized separators and no trailing slash — the main-worktree test.</summary>
  private static bool IsSamePath(string left, string right)
  {
    static string Normalize(string p) => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    return string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
  }

  /// <summary>One block of 'git worktree list --porcelain' output. <see cref="Branch"/>
  ///     is the short branch name when a branch line was present, else null.</summary>
  private sealed record WorktreeEntry(string Path, string Head, string? Branch, bool IsBare);

  /// <summary>Result of a single git CLI invocation. <see cref="Ok"/> is false only
  ///     when the process could not be started at all (e.g. git not on PATH).</summary>
  private sealed record GitRun(bool Ok, int ExitCode, string StdOut, string StdErr, DomainError Err)
  {
    public static GitRun OkRun(int exitCode, string stdout, string stderr)
        => new(true, exitCode, stdout, stderr, null!);
    public static GitRun Fail(DomainError error) => new(false, 0, "", "", error);
  }

  /// <summary>Runs git and fails the result when the process could not start or
  ///     exited nonzero — the guard every caller repeats after its invocation.</summary>
  private static async Task<Result<GitRun>> RunGitVerifiedAsync(string anchorPath, string[] args, CancellationToken ct)
  {
    GitRun run = await RunGitAsync(anchorPath, args, ct).ConfigureAwait(false);
    if (!run.Ok)
    {
      return Result.Failure<GitRun>(run.Err);
    }

    Result<GitRun> verified = run.ExitCode == 0
        ? Result.Success(run)
        : Result.Failure<GitRun>(ToGitFailure(anchorPath, run.ExitCode, run.StdErr));
    return verified;
  }

  private static async Task<GitRun> RunGitAsync(string anchorPath, string[] args, CancellationToken ct)
  {
    ProcessStartInfo psi = new(GitExePath.Value)
    {
      WorkingDirectory = anchorPath,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true,
      StandardOutputEncoding = Encoding.UTF8,
      StandardErrorEncoding = Encoding.UTF8
    };
    // -C anchors every invocation so no working-directory juggling is needed.
    psi.ArgumentList.Add("-C");
    psi.ArgumentList.Add(anchorPath);
    foreach (string a in args)
    {
      psi.ArgumentList.Add(a);
    }

    try
    {
      using Process p = Process.Start(psi)!;
      // Read stderr concurrently with stdout to avoid filling a pipe buffer
      // and deadlocking before either side finishes.
      Task<string> errTask = p.StandardError.ReadToEndAsync(ct);
      string stdout = await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
      string stderr = await errTask.ConfigureAwait(false);
      await p.WaitForExitAsync(ct).ConfigureAwait(false);
      return GitRun.OkRun(p.ExitCode, stdout, stderr);
    }
    // Named decision (CA1031): git transport failures (not on PATH, etc.) surface as
    // GitRun.Fail so callers see a typed error instead of a crash.
#pragma warning disable CA1031 // Do not catch general exception types
    catch (Exception ex)
    {
      return GitRun.Fail(new DomainError("FileSystemError", ex.Message));
    }
#pragma warning restore CA1031 // Do not catch general exception types
  }

  private static string ResolveGitExecutable()
  {
    // Probe the standard per-machine and per-user Git install layouts before
    // falling back to PATH, without hard-coding a single install location.
    string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    string[] candidates =
    [
      Path.Combine(programFiles, "Git", "cmd", "git.exe"),
      Path.Combine(localAppData, "Programs", "Git", "cmd", "git.exe"),
    ];

    string? resolved = candidates.FirstOrDefault(File.Exists);
    if (resolved is not null)
    {
      return resolved;
    }

    // System32\where.exe is always present, so the PATH probe itself goes
    // through an absolute path too. On any failure the bare name keeps the
    // spawn failure surfacing through the existing typed GitRun.Fail path.
    try
    {
      using Process where = Process.Start(new ProcessStartInfo(
          Path.Combine(Environment.SystemDirectory, "where.exe"), "git")
      {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        CreateNoWindow = true
      })!;
      string? line = where.StandardOutput.ReadLine();
      where.WaitForExit();
      string candidate = line?.Trim() ?? "";
      return candidate.Length > 0 && File.Exists(candidate) ? candidate : "git";
    }
    // Named decision (CA1031): path probing is best effort.
#pragma warning disable CA1031 // Do not catch general exception types
    catch (Exception)
    {
      return "git";
    }
#pragma warning restore CA1031 // Do not catch general exception types
  }

  private static DomainError ToGitFailure(string anchorPath, int exitCode, string stderr)
  {
    if (stderr.Contains("not a git repository", StringComparison.Ordinal))
    {
      return new DomainError("NotAGitRepository", $"Not a git repository: {anchorPath}");
    }

    // A silent git failure must still carry information: fall back to the exit
    // code so the error never reaches the model empty-handed.
    string msg = stderr.Trim();
    if (msg.Length == 0)
    {
      msg = $"git exited {exitCode} with no error output.";
    }

    return new DomainError("GitError", msg);
  }
}
