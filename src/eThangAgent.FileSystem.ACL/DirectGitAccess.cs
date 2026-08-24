using System.Diagnostics;
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
    private static readonly string[] NewLines = { "\r\n", "\n" };

    public async Task<Result<GitStatus>> GetStatusAsync(string repoPath, CancellationToken ct = default)
    {
        // Probe repo-ness explicitly so a plain directory reports NotAGitRepository
        // rather than being misread as a detached HEAD by the branch step below.
        var probe = await RunGitAsync(repoPath, ["rev-parse", "--is-inside-work-tree"], ct);
        if (!probe.Ok) return Result<GitStatus>.Failure(probe.Error);
        if (probe.ExitCode != 0)
            return Result<GitStatus>.Failure(ToGitFailure(repoPath, probe.ExitCode, probe.StdErr));

        // Resolve the branch via symbolic-ref: unlike 'rev-parse --abbrev-ref HEAD'
        // this also works on an UNBORN HEAD (fresh 'git init', no commits yet).
        // Only a detached HEAD lacks a symbolic ref — surface a visible marker.
        var branchRes = await RunGitAsync(repoPath, ["symbolic-ref", "--short", "HEAD"], ct);
        if (!branchRes.Ok) return Result<GitStatus>.Failure(branchRes.Error);
        var branch = branchRes.ExitCode == 0 ? branchRes.StdOut.Trim() : "(detached)";

        var statusRes = await RunGitAsync(repoPath, ["status", "--porcelain"], ct);
        if (!statusRes.Ok) return Result<GitStatus>.Failure(statusRes.Error);
        if (statusRes.ExitCode != 0)
            return Result<GitStatus>.Failure(ToGitFailure(repoPath, statusRes.ExitCode, statusRes.StdErr));

        var staged = new List<GitStatusEntry>();
        var unstaged = new List<GitStatusEntry>();
        var untracked = new List<string>();

        var lines = statusRes.StdOut.Split(NewLines, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line) || line.Length < 4) continue;
            var code = line[..2];
            var path = line[3..];
            if (code == "??") { untracked.Add(path); continue; }
            // Renames keep their FULL 'old -> new' porcelain string as the Path.
            var x = code[0];
            var y = code[1];
            if (x != ' ') staged.Add(new GitStatusEntry(code, path));
            if (y != ' ') unstaged.Add(new GitStatusEntry(code, path));
        }

        return Result<GitStatus>.Success(new GitStatus(branch, staged, unstaged, untracked));
    }

    public async Task<Result<GitDiff>> GetDiffAsync(string repoPath, string scope, string? path, CancellationToken ct = default)
    {
        if (scope is not ("Staged" or "Unstaged" or "All"))
            return Result<GitDiff>.Failure(new Error("InvalidScope",
                $"Unknown diff scope '{scope}'. Expected 'Staged', 'Unstaged', or 'All'."));

        var probe = await RunGitAsync(repoPath, ["rev-parse", "--git-dir"], ct);
        if (!probe.Ok) return Result<GitDiff>.Failure(probe.Error);
        if (probe.ExitCode != 0)
            return Result<GitDiff>.Failure(ToGitFailure(repoPath, probe.ExitCode, probe.StdErr));

        // Optional pathspec filter: everything after '--'.
        var pathArgs = path is null ? [] : new[] { "--", path };

        var wantStaged = scope is "Staged" or "All";
        var wantUnstaged = scope is "Unstaged" or "All";

        var files = 0;
        var additions = 0;
        var deletions = 0;
        var numstatLines = new List<string>();
        if (wantStaged)
        {
            var r = await RunGitAsync(repoPath, WithPath(["diff", "--cached", "--numstat"], pathArgs), ct);
            if (!r.Ok) return Result<GitDiff>.Failure(r.Error);
            if (r.ExitCode != 0) return Result<GitDiff>.Failure(ToGitFailure(repoPath, r.ExitCode, r.StdErr));
            numstatLines.AddRange(r.StdOut.Split(NewLines, StringSplitOptions.RemoveEmptyEntries));
        }
        if (wantUnstaged)
        {
            var r = await RunGitAsync(repoPath, WithPath(["diff", "--numstat"], pathArgs), ct);
            if (!r.Ok) return Result<GitDiff>.Failure(r.Error);
            if (r.ExitCode != 0) return Result<GitDiff>.Failure(ToGitFailure(repoPath, r.ExitCode, r.StdErr));
            numstatLines.AddRange(r.StdOut.Split(NewLines, StringSplitOptions.RemoveEmptyEntries));
        }
        foreach (var row in numstatLines)
        {
            if (string.IsNullOrWhiteSpace(row)) continue;
            var parts = row.Split('\t');
            if (parts.Length < 3) continue;
            files++;
            if (parts[0] != "-") additions += int.Parse(parts[0]); // binary '-' counts as 0
            if (parts[1] != "-") deletions += int.Parse(parts[1]); // binary '-' counts as 0
        }

        var sb = new StringBuilder();
        if (wantStaged)
        {
            var r = await RunGitAsync(repoPath, WithPath(["diff", "--cached"], pathArgs), ct);
            if (!r.Ok) return Result<GitDiff>.Failure(r.Error);
            if (r.ExitCode != 0) return Result<GitDiff>.Failure(ToGitFailure(repoPath, r.ExitCode, r.StdErr));
            if (r.StdOut.Length > 0)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append("### staged ###\n").Append(r.StdOut);
            }
        }
        if (wantUnstaged)
        {
            var r = await RunGitAsync(repoPath, WithPath(["diff"], pathArgs), ct);
            if (!r.Ok) return Result<GitDiff>.Failure(r.Error);
            if (r.ExitCode != 0) return Result<GitDiff>.Failure(ToGitFailure(repoPath, r.ExitCode, r.StdErr));
            if (r.StdOut.Length > 0)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append("### unstaged ###\n").Append(r.StdOut);
            }
        }
        var patch = sb.ToString();

        // Bound the patch at the cap, cutting at the last complete line before the
        // cap. TotalChars always reports the FULL untruncated length.
        var totalChars = patch.Length;
        var truncated = false;
        var cap = WorkingDiffTool.PatchCharCap;
        if (totalChars > cap)
        {
            var cut = patch.LastIndexOf('\n', cap - 1);
            if (cut < 0) cut = cap - 1;
            patch = patch[..(cut + 1)];
            truncated = true;
        }

        return Result<GitDiff>.Success(new GitDiff(
            new GitDiffStats(files, additions, deletions), patch, truncated, totalChars));
    }

    public async Task<Result<GitCommitOutcome>> CommitAsync(string repoPath, string message, CancellationToken ct = default)
    {
        // Outside a repository 'git diff --cached' exits 129 with usage text, so
        // probe repo-ness explicitly first.
        var probe = await RunGitAsync(repoPath, ["rev-parse", "--git-dir"], ct);
        if (!probe.Ok) return Result<GitCommitOutcome>.Failure(probe.Error);
        if (probe.ExitCode != 0)
            return Result<GitCommitOutcome>.Failure(ToGitFailure(repoPath, probe.ExitCode, probe.StdErr));

        var indexRes = await RunGitAsync(repoPath, ["diff", "--cached", "--name-only"], ct);
        if (!indexRes.Ok) return Result<GitCommitOutcome>.Failure(indexRes.Error);
        if (indexRes.ExitCode != 0)
            return Result<GitCommitOutcome>.Failure(ToGitFailure(repoPath, indexRes.ExitCode, indexRes.StdErr));
        if (string.IsNullOrWhiteSpace(indexRes.StdOut))
            return Result<GitCommitOutcome>.Failure(new Error("NothingStaged",
                $"The index is empty; there is nothing to commit in {repoPath}. " +
                "Stage changes first (e.g. exec: git add <file>)."));

        // Commit the CURRENT INDEX via a temp message file so multi-line messages
        // survive verbatim. Never stages anything itself.
        // --cleanup=verbatim keeps the message byte-for-byte; git's default
        // 'whitespace' cleanup silently collapses blank lines and would make the
        // committed message differ from what we report.
        var tmp = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tmp, message, new UTF8Encoding(false), ct);
            var commitRes = await RunGitAsync(repoPath, ["commit", "--cleanup=verbatim", "-F", tmp], ct);
            if (!commitRes.Ok) return Result<GitCommitOutcome>.Failure(commitRes.Error);
            if (commitRes.ExitCode != 0)
                return Result<GitCommitOutcome>.Failure(ToGitFailure(repoPath, commitRes.ExitCode, commitRes.StdErr));
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }

        var hashRes = await RunGitAsync(repoPath, ["rev-parse", "--short", "HEAD"], ct);
        if (!hashRes.Ok) return Result<GitCommitOutcome>.Failure(hashRes.Error);
        if (hashRes.ExitCode != 0)
            return Result<GitCommitOutcome>.Failure(ToGitFailure(repoPath, hashRes.ExitCode, hashRes.StdErr));
        var branchRes = await RunGitAsync(repoPath, ["rev-parse", "--abbrev-ref", "HEAD"], ct);
        if (!branchRes.Ok) return Result<GitCommitOutcome>.Failure(branchRes.Error);
        if (branchRes.ExitCode != 0)
            return Result<GitCommitOutcome>.Failure(ToGitFailure(repoPath, branchRes.ExitCode, branchRes.StdErr));

        return Result<GitCommitOutcome>.Success(new GitCommitOutcome(
            hashRes.StdOut.Trim(), branchRes.StdOut.Trim(), message));
    }

    /// <summary>Result of a single git CLI invocation. <see cref="Ok"/> is false only
    /// when the process could not be started at all (e.g. git not on PATH).</summary>
    private sealed record GitRun(bool Ok, int ExitCode, string StdOut, string StdErr, Error Error)
    {
        public static GitRun OkRun(int exitCode, string stdout, string stderr)
            => new(true, exitCode, stdout, stderr, null!);
        public static GitRun Fail(Error error) => new(false, 0, "", "", error);
    }

    private static async Task<GitRun> RunGitAsync(string repoPath, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git")
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
        foreach (var a in args) psi.ArgumentList.Add(a);

        try
        {
            using var p = Process.Start(psi)!;
            // Read stderr concurrently with stdout to avoid filling a pipe buffer
            // and deadlocking before either side finishes.
            var errTask = p.StandardError.ReadToEndAsync(ct);
            var stdout = await p.StandardOutput.ReadToEndAsync(ct);
            var stderr = await errTask;
            await p.WaitForExitAsync(ct);
            return GitRun.OkRun(p.ExitCode, stdout, stderr);
        }
        catch (Exception ex)
        {
            return GitRun.Fail(new Error("FileSystemError", ex.Message));
        }
    }

    private static Error ToGitFailure(string repoPath, int exitCode, string stderr)
    {
        if (stderr.Contains("not a git repository"))
            return new Error("NotAGitRepository", $"Not a git repository: {repoPath}");
        // A silent git failure must still carry information: fall back to the exit
        // code so the error never reaches the model empty-handed.
        var msg = stderr.Trim();
        if (msg.Length == 0) msg = $"git exited {exitCode} with no error output.";
        return new Error("GitError", msg);
    }

    private static string[] WithPath(string[] baseArgs, string[] pathArgs)
    {
        if (pathArgs.Length == 0) return baseArgs;
        var result = new string[baseArgs.Length + pathArgs.Length];
        Array.Copy(baseArgs, result, baseArgs.Length);
        Array.Copy(pathArgs, 0, result, baseArgs.Length, pathArgs.Length);
        return result;
    }

    public void Dispose() { }
}
