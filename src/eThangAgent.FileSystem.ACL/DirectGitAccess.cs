using System.Diagnostics;
using System.Globalization;
using System.Text;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.FileSystem.ACL;

/// <summary>
/// Read-only git queries and index-only commits implemented by shelling out to the
/// <c>git</c> CLI directly via <see cref="ProcessStartInfo"/> (no shell intermediary).
/// Same scope vocabulary (<c>Staged</c>/<c>Unstaged</c>/<c>All</c>), same branch resolution, same
/// <c>NothingStaged</c> guard, same verbatim commit message handling, same patch cap.
/// Every git invocation is anchored at the repository root with <c>-C</c>, so no
/// working-directory juggling takes place. stdout/stderr are captured separately
/// (exact exit codes, no stream merging, no CRLF rewriting).
/// </summary>
public sealed class DirectGitAccess : IGitQueryAccess, IGitCommitAccess, IDisposable
{
  private const string RevParse = "rev-parse";

  private static readonly string[] NewLines = ["\r\n", "\n"];

  // Resolved once: spawning git through an absolute path keeps the executable
  // from being shadowed by a manipulated PATH (S4036).
  private static readonly Lazy<string> GitExePath = new(ResolveGitExecutable);

  public async Task<Result<GitStatus>> GetStatusAsync(string repoPath, CancellationToken ct = default)
  {
    // Probe repo-ness explicitly so a plain directory reports NotAGitRepository
    // rather than being misread as a detached HEAD by the branch step below.
    Result<GitRun> probe = await RunGitVerifiedAsync(repoPath, [RevParse, "--is-inside-work-tree"], ct).ConfigureAwait(false);
    if (!probe.IsSuccess)
    {
      return Result.Failure<GitStatus>(probe.Error);
    }

    // Resolve the branch via symbolic-ref: unlike 'rev-parse --abbrev-ref HEAD'
    // this also works on an UNBORN HEAD (fresh 'git init', no commits yet).
    // Only a detached HEAD lacks a symbolic ref — surface a visible marker.
    // Raw run (no nonzero-exit guard): a detached HEAD's exit code IS the signal.
    GitRun branchRes = await RunGitAsync(repoPath, ["symbolic-ref", "--short", "HEAD"], ct).ConfigureAwait(false);
    if (!branchRes.Ok)
    {
      return Result.Failure<GitStatus>(branchRes.Err);
    }

    string branch = branchRes.ExitCode == 0 ? branchRes.StdOut.Trim() : "(detached)";

    Result<GitRun> statusRes = await RunGitVerifiedAsync(repoPath, ["status", "--porcelain"], ct).ConfigureAwait(false);
    if (!statusRes.IsSuccess)
    {
      return Result.Failure<GitStatus>(statusRes.Error);
    }

    (List<GitStatusEntry> staged, List<GitStatusEntry> unstaged, List<string> untracked) =
        ParsePorcelain(statusRes.Value.StdOut);
    GitStatus status = new(branch, staged, unstaged, untracked);
    return Result.Success(status);
  }

  /// <summary>Splits 'git status --porcelain' output into staged, unstaged, and
  ///     untracked entries.</summary>
  private static (List<GitStatusEntry> Staged, List<GitStatusEntry> Unstaged, List<string> Untracked) ParsePorcelain(
      string stdout)
  {
    List<GitStatusEntry> staged = [];
    List<GitStatusEntry> unstaged = [];
    List<string> untracked = [];

    string[] lines = stdout.Split(NewLines, StringSplitOptions.RemoveEmptyEntries);
    foreach (string line in lines)
    {
      if (string.IsNullOrWhiteSpace(line) || line.Length < 4)
      {
        continue;
      }

      string code = line[..2];
      string path = line[3..];
      if (code == "??")
      {
        untracked.Add(path);
        continue;
      }

      // Renames keep their FULL 'old -> new' porcelain string as the Path.
      char x = code[0];
      char y = code[1];
      if (x != ' ')
      {
        staged.Add(new GitStatusEntry(code, path));
      }

      if (y != ' ')
      {
        unstaged.Add(new GitStatusEntry(code, path));
      }
    }

    return (staged, unstaged, untracked);
  }

  public async Task<Result<GitDiff>> GetDiffAsync(string repoPath, string scope, string? path, CancellationToken ct = default)
  {
    if (scope is not ("Staged" or "Unstaged" or "All"))
    {
      return Result.Failure<GitDiff>(new DomainError("InvalidScope",
          $"Unknown diff scope '{scope}'. Expected 'Staged', 'Unstaged', or 'All'."));
    }

    Result<GitRun> probe = await RunGitVerifiedAsync(repoPath, [RevParse, "--git-dir"], ct).ConfigureAwait(false);
    if (!probe.IsSuccess)
    {
      return Result.Failure<GitDiff>(probe.Error);
    }

    // Optional pathspec filter: everything after '--'.
    string[] pathArgs = path is null ? [] : ["--", path];

    bool wantStaged = scope is "Staged" or "All";
    bool wantUnstaged = scope is "Unstaged" or "All";

    Result<GitDiffStats> stats = await CollectStatsAsync(repoPath, pathArgs, wantStaged, wantUnstaged, ct).ConfigureAwait(false);
    if (!stats.IsSuccess)
    {
      return Result.Failure<GitDiff>(stats.Error);
    }

    Result<string> patch = await RenderPatchAsync(repoPath, pathArgs, wantStaged, wantUnstaged, ct).ConfigureAwait(false);
    if (!patch.IsSuccess)
    {
      return Result.Failure<GitDiff>(patch.Error);
    }

    GitDiff diff = TruncatePatch(stats.Value, patch.Value);
    return Result.Success(diff);
  }

  /// <summary>Runs both numstat passes in scope order, then folds them into the stats.</summary>
  private static async Task<Result<GitDiffStats>> CollectStatsAsync(string repoPath, string[] pathArgs,
      bool wantStaged, bool wantUnstaged, CancellationToken ct)
  {
    List<string> numstatLines = [];
    if (wantStaged)
    {
      Result<List<string>> staged = await CollectNumstatAsync(repoPath, ["diff", "--cached", "--numstat"], pathArgs, ct).ConfigureAwait(false);
      if (!staged.IsSuccess)
      {
        return Result.Failure<GitDiffStats>(staged.Error);
      }

      numstatLines.AddRange(staged.Value);
    }

    if (wantUnstaged)
    {
      Result<List<string>> unstaged = await CollectNumstatAsync(repoPath, ["diff", "--numstat"], pathArgs, ct).ConfigureAwait(false);
      if (!unstaged.IsSuccess)
      {
        return Result.Failure<GitDiffStats>(unstaged.Error);
      }

      numstatLines.AddRange(unstaged.Value);
    }

    return Result.Success(FoldNumstat(numstatLines));
  }

  private static async Task<Result<List<string>>> CollectNumstatAsync(string repoPath, string[] baseArgs,
      string[] pathArgs, CancellationToken ct)
  {
    Result<GitRun> run = await RunGitVerifiedAsync(repoPath, WithPath(baseArgs, pathArgs), ct).ConfigureAwait(false);
    if (!run.IsSuccess)
    {
      return Result.Failure<List<string>>(run.Error);
    }

    List<string> lines = [.. run.Value.StdOut.Split(NewLines, StringSplitOptions.RemoveEmptyEntries)];
    return Result.Success(lines);
  }

  /// <summary>Folds numstat rows into file/addition/deletion totals.</summary>
  private static GitDiffStats FoldNumstat(List<string> numstatLines)
  {
    int files = 0;
    int additions = 0;
    int deletions = 0;
    foreach (string row in numstatLines)
    {
      if (string.IsNullOrWhiteSpace(row))
      {
        continue;
      }

      string[] parts = row.Split('\t');
      if (parts.Length < 3)
      {
        continue;
      }

      files++;
      if (parts[0] != "-")
      {
        additions += int.Parse(parts[0], CultureInfo.InvariantCulture); // binary '-' counts as 0
      }

      if (parts[1] != "-")
      {
        deletions += int.Parse(parts[1], CultureInfo.InvariantCulture); // binary '-' counts as 0
      }
    }

    return new GitDiffStats(files, additions, deletions);
  }

  /// <summary>Runs the section diffs in scope order and joins them under their
  ///     '### staged ###' / '### unstaged ###' gutters.</summary>
  private static async Task<Result<string>> RenderPatchAsync(string repoPath, string[] pathArgs,
      bool wantStaged, bool wantUnstaged, CancellationToken ct)
  {
    StringBuilder sb = new();
    if (wantStaged)
    {
      Result<bool> staged = await AppendDiffSectionAsync(sb, repoPath, ["diff", "--cached"], pathArgs, "staged", ct).ConfigureAwait(false);
      if (!staged.IsSuccess)
      {
        return Result.Failure<string>(staged.Error);
      }
    }

    if (wantUnstaged)
    {
      Result<bool> unstaged = await AppendDiffSectionAsync(sb, repoPath, ["diff"], pathArgs, "unstaged", ct).ConfigureAwait(false);
      if (!unstaged.IsSuccess)
      {
        return Result.Failure<string>(unstaged.Error);
      }
    }

    string patch = sb.ToString();
    return Result.Success(patch);
  }

  /// <summary>Appends one diff section under its gutter when the section is non-empty.</summary>
  private static async Task<Result<bool>> AppendDiffSectionAsync(StringBuilder sb, string repoPath,
      string[] baseArgs, string[] pathArgs, string label, CancellationToken ct)
  {
    Result<GitRun> r = await RunGitVerifiedAsync(repoPath, WithPath(baseArgs, pathArgs), ct).ConfigureAwait(false);
    if (!r.IsSuccess)
    {
      return Result.Failure<bool>(r.Error);
    }

    if (r.Value.StdOut.Length > 0)
    {
      if (sb.Length > 0)
      {
        _ = sb.Append('\n');
      }

      _ = sb.Append("### ").Append(label).Append(" ###\n").Append(r.Value.StdOut);
    }

    return Result.Success(true);
  }

  /// <summary>Bounds the patch at the cap, cutting at the last complete line before the
  ///     cap. TotalChars always reports the FULL untruncated length.</summary>
  private static GitDiff TruncatePatch(GitDiffStats stats, string fullPatch)
  {
    int totalChars = fullPatch.Length;
    bool truncated = false;
    string patch = fullPatch;
    int cap = WorkingDiffTool.PatchCharCap;
    if (totalChars > cap)
    {
      int cut = patch.LastIndexOf('\n', cap - 1);
      if (cut < 0)
      {
        cut = cap - 1;
      }

      patch = patch[..(cut + 1)];
      truncated = true;
    }

    return new GitDiff(stats, patch, truncated, totalChars);
  }

  public async Task<Result<GitCommitOutcome>> CommitAsync(string repoPath, string message, CancellationToken ct = default)
  {
    // Outside a repository 'git diff --cached' exits 129 with usage text, so
    // probe repo-ness explicitly first.
    GitRun probe = await RunGitAsync(repoPath, [RevParse, "--git-dir"], ct).ConfigureAwait(false);
    if (!probe.Ok)
    {
      return Result.Failure<GitCommitOutcome>(probe.Err);
    }

    if (probe.ExitCode != 0)
    {
      return Result.Failure<GitCommitOutcome>(ToGitFailure(repoPath, probe.ExitCode, probe.StdErr));
    }

    GitRun indexRes = await RunGitAsync(repoPath, ["diff", "--cached", "--name-only"], ct).ConfigureAwait(false);
    if (!indexRes.Ok)
    {
      return Result.Failure<GitCommitOutcome>(indexRes.Err);
    }

    if (indexRes.ExitCode != 0)
    {
      return Result.Failure<GitCommitOutcome>(ToGitFailure(repoPath, indexRes.ExitCode, indexRes.StdErr));
    }

    if (string.IsNullOrWhiteSpace(indexRes.StdOut))
    {
      return Result.Failure<GitCommitOutcome>(new DomainError("NothingStaged",
          $"The index is empty; there is nothing to commit in {repoPath}. " +
          "Stage changes first (e.g. exec: git add <file>)."));
    }

    // Commit the CURRENT INDEX via a temp message file so multi-line messages
    // survive verbatim. Never stages anything itself.
    // --cleanup=verbatim keeps the message byte-for-byte; git's default
    // 'whitespace' cleanup silently collapses blank lines and would make the
    // committed message differ from what we report.
    string tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    try
    {
      // CreateNew claims the unpredictable name before anything is written,
      // closing the pre-creation window GetTempFileName left open (S5445).
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using (StreamWriter writer = new(
          new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.Read),
          new UTF8Encoding(false)))
      {
        await writer.WriteAsync(message.AsMemory(), ct).ConfigureAwait(false);
      }
#pragma warning restore CA2007

      GitRun commitRes = await RunGitAsync(repoPath, ["commit", "--cleanup=verbatim", "-F", tmp], ct).ConfigureAwait(false);
      if (!commitRes.Ok)
      {
        return Result.Failure<GitCommitOutcome>(commitRes.Err);
      }

      if (commitRes.ExitCode != 0)
      {
        return Result.Failure<GitCommitOutcome>(ToGitFailure(repoPath, commitRes.ExitCode, commitRes.StdErr));
      }
    }
    finally
    {
      try
      {
        File.Delete(tmp);
      }
      // Named decision (CA1031): temp-file cleanup is best effort.
#pragma warning disable CA1031 // Do not catch general exception types
      catch (Exception) { /* best effort: the commit itself already succeeded */ }
#pragma warning restore CA1031 // Do not catch general exception types
    }

    GitRun hashRes = await RunGitAsync(repoPath, [RevParse, "--short", "HEAD"], ct).ConfigureAwait(false);
    if (!hashRes.Ok)
    {
      return Result.Failure<GitCommitOutcome>(hashRes.Err);
    }

    if (hashRes.ExitCode != 0)
    {
      return Result.Failure<GitCommitOutcome>(ToGitFailure(repoPath, hashRes.ExitCode, hashRes.StdErr));
    }

    GitRun branchRes = await RunGitAsync(repoPath, [RevParse, "--abbrev-ref", "HEAD"], ct).ConfigureAwait(false);
    if (!branchRes.Ok)
    {
      return Result.Failure<GitCommitOutcome>(branchRes.Err);
    }

    if (branchRes.ExitCode != 0)
    {
      return Result.Failure<GitCommitOutcome>(ToGitFailure(repoPath, branchRes.ExitCode, branchRes.StdErr));
    }

    GitCommitOutcome committed = new(hashRes.StdOut.Trim(), branchRes.StdOut.Trim(), message);
    return Result.Success(committed);
  }

  /// <summary>Result of a single git CLI invocation. <see cref="Ok"/> is false only
  /// when the process could not be started at all (e.g. git not on PATH).</summary>
  private sealed record GitRun(bool Ok, int ExitCode, string StdOut, string StdErr, DomainError Err)
  {
    public static GitRun OkRun(int exitCode, string stdout, string stderr)
        => new(true, exitCode, stdout, stderr, null!);
    public static GitRun Fail(DomainError error) => new(false, 0, "", "", error);
  }

  /// <summary>Runs git and fails the result when the process could not start or exited
  ///     nonzero — the guard every query repeats after its invocation.</summary>
  private static async Task<Result<GitRun>> RunGitVerifiedAsync(string repoPath, string[] args, CancellationToken ct)
  {
    GitRun run = await RunGitAsync(repoPath, args, ct).ConfigureAwait(false);
    if (!run.Ok)
    {
      return Result.Failure<GitRun>(run.Err);
    }

    Result<GitRun> verified = run.ExitCode == 0
        ? Result.Success(run)
        : Result.Failure<GitRun>(ToGitFailure(repoPath, run.ExitCode, run.StdErr));
    return verified;
  }

  private static async Task<GitRun> RunGitAsync(string repoPath, string[] args, CancellationToken ct)
  {
    ProcessStartInfo psi = new(GitExePath.Value)
    {
      WorkingDirectory = repoPath,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true,
      StandardOutputEncoding = Encoding.UTF8,
      StandardErrorEncoding = Encoding.UTF8
    };
    // -C anchors at the repo root so no working-directory juggling is needed.
    psi.ArgumentList.Add("-C");
    psi.ArgumentList.Add(repoPath);
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

  private static DomainError ToGitFailure(string repoPath, int exitCode, string stderr)
  {
    if (stderr.Contains("not a git repository", StringComparison.Ordinal))
    {
      return new DomainError("NotAGitRepository", $"Not a git repository: {repoPath}");
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

  private static string[] WithPath(string[] baseArgs, string[] pathArgs)
  {
    if (pathArgs.Length == 0)
    {
      return baseArgs;
    }

    string[] result = new string[baseArgs.Length + pathArgs.Length];
    Array.Copy(baseArgs, result, baseArgs.Length);
    Array.Copy(pathArgs, 0, result, baseArgs.Length, pathArgs.Length);
    return result;
  }

  public void Dispose() { }
}
