using System.Diagnostics;
using System.Text;
using eThangAgent.FileSystem.ACL;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.FileSystem.ACL.Tests;

/// <summary>
/// Integration tests for <see cref="PowerShellGitAccess"/> against real git repositories
/// created in temp directories. Repository fixtures are driven directly through
/// <c>git</c> via <see cref="Process"/> (test-only arrangement, never under test).
/// </summary>
public sealed class PowerShellGitAccessIntegrationTests : IDisposable
{
    private readonly PowerShellGitAccess _access = new();

    public void Dispose()
    {
        _access.Dispose();
    }

    /// <summary>Deletes a temp tree; git child processes can briefly hold handles.</summary>
    private static void SafeDelete(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { }
    }

    // ---- fixtures ----------------------------------------------------------

    private static string InitRepo()
    {
        var dir = Directory.CreateTempSubdirectory("ethang-git").FullName;
        Git(dir, "init");
        Git(dir, "config", "user.email", "test@local");
        Git(dir, "config", "user.name", "test");
        return dir;
    }

    /// <summary>Seeds an initial commit so HEAD exists (fresh repos have unborn HEAD).</summary>
    private static void SeedCommit(string repo)
    {
        File.WriteAllText(Path.Combine(repo, "seed.txt"), "seed\n");
        Git(repo, "add", "seed.txt");
        Git(repo, "commit", "-m", "seed");
    }

    private static void Write(string repo, string relativePath, string content) =>
        File.WriteAllText(Path.Combine(repo, relativePath), content);

    private static void Append(string repo, string relativePath, string content) =>
        File.AppendAllText(Path.Combine(repo, relativePath), content);

    private static (int ExitCode, string StdOut, string StdErr) RunGit(string repo, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(repo);
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var errTask = p.StandardError.ReadToEndAsync();
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = errTask.GetAwaiter().GetResult();
        p.WaitForExit();
        return (p.ExitCode, stdout, stderr);
    }

    private static void Git(string repo, params string[] args)
    {
        var (code, _, err) = RunGit(repo, args);
        if (code != 0)
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} exited {code}: {err}");
    }

    private static string GitOut(string repo, params string[] args) => RunGit(repo, args).StdOut;

    private static string[] Project(IEnumerable<GitStatusEntry> entries) =>
        entries.Select(e => $"{e.Code}|{e.Path}").OrderBy(s => s, StringComparer.Ordinal).ToArray();

    // ---- status ------------------------------------------------------------

    [Fact]
    public async Task Status_FreshRepoWithInitialCommit_ReturnsBranchAndCleanLists()
    {
        var repo = InitRepo();
        try
        {
            SeedCommit(repo); // unborn HEAD has no branch ref yet; seed before asserting
            var r = await _access.GetStatusAsync(repo);
            Assert.True(r.IsSuccess, r.Error?.Message);
            var expectedBranch = GitOut(repo, "rev-parse", "--abbrev-ref", "HEAD").Trim();
            Assert.Equal(expectedBranch, r.Value!.Branch);
            Assert.Empty(r.Value.Staged);
            Assert.Empty(r.Value.Unstaged);
            Assert.Empty(r.Value.Untracked);
        }
        finally { SafeDelete(repo); }
    }

    [Fact]
    public async Task Status_MixedChanges_GroupsExactly_DualListingAndRenameStaging()
    {
        var repo = InitRepo();
        try
        {
            Write(repo, "a.txt", "v1\n");
            Write(repo, "old.txt", "rename me\n");
            Write(repo, "c.txt", "c1\n");
            Git(repo, "add", ".");
            Git(repo, "commit", "-m", "base");

            Write(repo, "a.txt", "v2\n");          // staged modification
            Git(repo, "add", "a.txt");
            Write(repo, "a.txt", "v3\n");          // further unstaged edit -> MM (both lists)
            Write(repo, "b.txt", "brand new\n");   // untracked
            Append(repo, "c.txt", "c2\n");         // unstaged-only modification
            Git(repo, "mv", "old.txt", "renamed.txt"); // staged rename keeps full 'old -> new'

            var r = await _access.GetStatusAsync(repo);
            Assert.True(r.IsSuccess, r.Error?.Message);
            Assert.Equal(new[]
            {
                "MM|a.txt",
                "R |old.txt -> renamed.txt",
            }, Project(r.Value!.Staged));
            Assert.Equal(new[]
            {
                " M|c.txt",
                "MM|a.txt",
            }, Project(r.Value!.Unstaged));
            Assert.Equal(new[] { "b.txt" }, r.Value!.Untracked);
        }
        finally { SafeDelete(repo); }
    }

    [Fact]
    public async Task Status_FreshlyInitializedRepo_NoCommits_SucceedsWithDefaultBranchAndCleanLists()
    {
        var repo = InitRepo(); // init + config only — NO commit, HEAD is unborn
        try
        {
            // Expected branch comes from git itself (init.defaultBranch varies by
            // environment); the contract under test is that status SUCCEEDS here.
            var expectedBranch = GitOut(repo, "symbolic-ref", "--short", "HEAD").Trim();

            var r = await _access.GetStatusAsync(repo);
            Assert.True(r.IsSuccess, r.Error?.Message);
            Assert.Equal(expectedBranch, r.Value!.Branch);
            Assert.Empty(r.Value.Staged);
            Assert.Empty(r.Value.Unstaged);
            Assert.Empty(r.Value.Untracked);
        }
        finally { SafeDelete(repo); }
    }

    [Fact]
    public async Task Status_DetachedHead_ReportsDetachedMarker()
    {
        var repo = InitRepo();
        try
        {
            SeedCommit(repo);
            Git(repo, "checkout", "--detach"); // detach at current HEAD, tree stays clean

            var r = await _access.GetStatusAsync(repo);
            Assert.True(r.IsSuccess, r.Error?.Message);
            Assert.Equal("(detached)", r.Value!.Branch);
            Assert.Empty(r.Value.Staged);
            Assert.Empty(r.Value.Unstaged);
            Assert.Empty(r.Value.Untracked);
        }
        finally { SafeDelete(repo); }
    }

    [Fact]
    public async Task Status_PlainDirectory_Fails_NotAGitRepository()
    {
        var dir = Directory.CreateTempSubdirectory("ethang-nogit").FullName;
        try
        {
            var r = await _access.GetStatusAsync(dir);
            Assert.False(r.IsSuccess);
            Assert.Equal("NotAGitRepository", r.Error!.Code);
        }
        finally { SafeDelete(dir); }
    }

    // ---- diff --------------------------------------------------------------

    [Fact]
    public async Task Diff_CleanRepo_StagedScope_EmptyStats()
    {
        var repo = InitRepo();
        try
        {
            SeedCommit(repo);
            var r = await _access.GetDiffAsync(repo, "Staged", null);
            Assert.True(r.IsSuccess, r.Error?.Message);
            Assert.Equal(0, r.Value!.Stats.Files);
            Assert.Equal(0, r.Value.Stats.Additions);
            Assert.Equal(0, r.Value.Stats.Deletions);
            Assert.Equal(string.Empty, r.Value.Patch);
            Assert.False(r.Value.Truncated);
            Assert.Equal(0, r.Value.TotalChars);
        }
        finally { SafeDelete(repo); }
    }

    [Fact]
    public async Task Diff_CleanRepo_UnstagedScope_EmptyStats()
    {
        var repo = InitRepo();
        try
        {
            SeedCommit(repo);
            var r = await _access.GetDiffAsync(repo, "Unstaged", null);
            Assert.True(r.IsSuccess, r.Error?.Message);
            Assert.Equal(0, r.Value!.Stats.Files);
            Assert.Equal(0, r.Value.Stats.Additions);
            Assert.Equal(0, r.Value.Stats.Deletions);
            Assert.Equal(string.Empty, r.Value.Patch);
            Assert.False(r.Value.Truncated);
            Assert.Equal(0, r.Value.TotalChars);
        }
        finally { SafeDelete(repo); }
    }

    [Fact]
    public async Task Diff_CleanRepo_AllScope_EmptyStats()
    {
        var repo = InitRepo();
        try
        {
            SeedCommit(repo);
            var r = await _access.GetDiffAsync(repo, "All", null);
            Assert.True(r.IsSuccess, r.Error?.Message);
            Assert.Equal(0, r.Value!.Stats.Files);
            Assert.Equal(0, r.Value.Stats.Additions);
            Assert.Equal(0, r.Value.Stats.Deletions);
            Assert.Equal(string.Empty, r.Value.Patch);
            Assert.False(r.Value.Truncated);
            Assert.Equal(0, r.Value.TotalChars);
        }
        finally { SafeDelete(repo); }
    }

    private (string Repo, IDisposable Cleanup) RepoWithSplitChanges()
    {
        var repo = InitRepo();
        Write(repo, "f1.txt", "base1\n");
        Write(repo, "f2.txt", "base2\n");
        Git(repo, "add", ".");
        Git(repo, "commit", "-m", "base");
        Write(repo, "f1.txt", "line-A1\n");   // staged
        Git(repo, "add", "f1.txt");
        Write(repo, "f2.txt", "line-U1\n");   // unstaged
        return (repo, new TempDir(repo));
    }

    [Fact]
    public async Task Diff_StagedVersusUnstaged_ContentSeparation()
    {
        var (repo, cleanup) = RepoWithSplitChanges();
        try
        {
            var staged = await _access.GetDiffAsync(repo, "Staged", null);
            Assert.True(staged.IsSuccess, staged.Error?.Message);
            Assert.Equal(1, staged.Value!.Stats.Files);
            Assert.Equal(1, staged.Value.Stats.Additions);
            Assert.Equal(1, staged.Value.Stats.Deletions);
            Assert.Contains("+line-A1", staged.Value.Patch);
            Assert.DoesNotContain("line-U1", staged.Value.Patch);

            var unstaged = await _access.GetDiffAsync(repo, "Unstaged", null);
            Assert.True(unstaged.IsSuccess, unstaged.Error?.Message);
            Assert.Equal(1, unstaged.Value!.Stats.Files);
            Assert.Equal(1, unstaged.Value.Stats.Additions);
            Assert.Equal(1, unstaged.Value.Stats.Deletions);
            Assert.Contains("+line-U1", unstaged.Value.Patch);
            Assert.DoesNotContain("line-A1", unstaged.Value.Patch);
        }
        finally { cleanup.Dispose(); }
    }

    [Fact]
    public async Task Diff_AllScope_SeparatorsPresent_StatsAggregated()
    {
        var (repo, cleanup) = RepoWithSplitChanges();
        try
        {
            var r = await _access.GetDiffAsync(repo, "All", null);
            Assert.True(r.IsSuccess, r.Error?.Message);
            Assert.Contains("### staged ###", r.Value!.Patch);
            Assert.Contains("### unstaged ###", r.Value!.Patch);
            Assert.Contains("+line-A1", r.Value!.Patch);
            Assert.Contains("+line-U1", r.Value!.Patch);
            Assert.Equal(2, r.Value!.Stats.Files);
            Assert.Equal(2, r.Value!.Stats.Additions);
            Assert.Equal(2, r.Value!.Stats.Deletions);
            Assert.False(r.Value!.Truncated);
        }
        finally { cleanup.Dispose(); }
    }

    [Fact]
    public async Task Diff_PatchBeyondCap_Truncated_WithAccurateTotalChars()
    {
        var repo = InitRepo();
        try
        {
            SeedCommit(repo);
            var sb = new StringBuilder();
            for (var i = 0; i < 250; i++)
                sb.AppendLine(new string('x', 300)); // ~75KB, well over the 20K cap
            Write(repo, "big.txt", sb.ToString());
            Git(repo, "add", "big.txt");

            var r = await _access.GetDiffAsync(repo, "Staged", null);
            Assert.True(r.IsSuccess, r.Error?.Message);

            // The delivered patch includes the section header, so the full length
            // is header + raw git output.
            const string stagedHeader = "### staged ###\n";
            var fullPatch = GitOut(repo, "diff", "--cached");
            var expectedFull = stagedHeader + fullPatch;
            Assert.True(expectedFull.Length > 20000, $"fixture produced only {expectedFull.Length} chars");

            Assert.True(r.Value!.Truncated);
            Assert.Equal(expectedFull.Length, r.Value!.TotalChars); // full untruncated length
            Assert.True(r.Value!.Patch.Length <= 20000,
                $"patch was {r.Value!.Patch.Length} chars, cap is 20000");
            Assert.EndsWith("\n", r.Value!.Patch); // cut at a complete line
            Assert.StartsWith(r.Value!.Patch, expectedFull, StringComparison.Ordinal);
        }
        finally { SafeDelete(repo); }
    }

    // ---- commit ------------------------------------------------------------

    [Fact]
    public async Task Commit_HappyPath_Hash_Branch_AndExactMessageRoundTrip()
    {
        var repo = InitRepo();
        try
        {
            SeedCommit(repo);
            Write(repo, "g.txt", "content\n");
            Git(repo, "add", "g.txt");
            const string message = "feat: subject line\n\nBody paragraph with detail.\n";

            var r = await _access.CommitAsync(repo, message);
            Assert.True(r.IsSuccess, r.Error?.Message);
            Assert.False(string.IsNullOrWhiteSpace(r.Value!.Hash));
            var expectedBranch = GitOut(repo, "rev-parse", "--abbrev-ref", "HEAD").Trim();
            Assert.Equal(expectedBranch, r.Value!.Branch);
            Assert.Equal(message, r.Value!.Message);

            var committed = GitOut(repo, "cat-file", "commit", "HEAD");
            var bodyStart = committed.IndexOf("\n\n", StringComparison.Ordinal);
            Assert.True(bodyStart >= 0, "commit object missing message body");
            Assert.Equal(message, committed[(bodyStart + 2)..]); // exact round-trip incl. body and trailing newline
        }
        finally { SafeDelete(repo); }
    }

    [Fact]
    public async Task Commit_EmptyIndex_OnFreshRepo_Fails_NothingStaged()
    {
        var repo = InitRepo();
        try
        {
            var r = await _access.CommitAsync(repo, "nothing\n");
            Assert.False(r.IsSuccess);
            Assert.Equal("NothingStaged", r.Error!.Code);
        }
        finally { SafeDelete(repo); }
    }

    [Fact]
    public async Task Commit_EmptyIndex_AfterEverythingCommitted_Fails_NothingStaged()
    {
        var repo = InitRepo();
        try
        {
            SeedCommit(repo); // working tree and index clean afterwards
            var r = await _access.CommitAsync(repo, "nothing\n");
            Assert.False(r.IsSuccess);
            Assert.Equal("NothingStaged", r.Error!.Code);
        }
        finally { SafeDelete(repo); }
    }

    [Fact]
    public async Task Commit_NeverIncludesUnstagedEdits()
    {
        var repo = InitRepo();
        try
        {
            SeedCommit(repo);
            Write(repo, "h.txt", "v2\n");
            Git(repo, "add", "h.txt");
            Write(repo, "h.txt", "v3\n"); // unstaged edit made AFTER staging

            var r = await _access.CommitAsync(repo, "partial\n");
            Assert.True(r.IsSuccess, r.Error?.Message);

            var committed = GitOut(repo, "show", "HEAD:h.txt");
            Assert.Equal("v2\n", committed);
            Assert.DoesNotContain("v3", committed);
        }
        finally { SafeDelete(repo); }
    }

    [Fact]
    public async Task Commit_PlainDirectory_Fails_NotAGitRepository()
    {
        var dir = Directory.CreateTempSubdirectory("ethang-nogit-c").FullName;
        try
        {
            var r = await _access.CommitAsync(dir, "msg\n");
            Assert.False(r.IsSuccess);
            Assert.Equal("NotAGitRepository", r.Error!.Code);
        }
        finally { SafeDelete(dir); }
    }

    private sealed class TempDir : IDisposable
    {
        private readonly string _path;
        public TempDir(string path) => _path = path;
        public void Dispose()
        {
            try { Directory.Delete(_path, recursive: true); } catch { }
        }
    }
}
