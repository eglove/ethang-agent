using System.Diagnostics;
using eThangAgent.FileSystem.ACL;
using eThangAgent.ToolDomain;

namespace eThangAgent.FileSystem.ACL.Tests;

public class DirectGitAccessTests : IDisposable
{
    private readonly string _repoDir;

    public DirectGitAccessTests()
    {
        _repoDir = Path.Combine(Path.GetTempPath(), "ethang-git-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_repoDir);
        RunGit("init");
        RunGit("config", "user.email", "test@test.com");
        RunGit("config", "user.name", "Test");
    }

    public void Dispose() { try { Directory.Delete(_repoDir, true); } catch { } }

    private void RunGit(params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = _repoDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit(30000);
    }

    [Fact]
    public async Task GetStatusAsync_CleanRepo_ReturnsNoEntries()
    {
        var access = new DirectGitAccess();

        var r = await access.GetStatusAsync(_repoDir);

        Assert.True(r.IsSuccess);
        Assert.Empty(r.Value!.Staged);
        Assert.Empty(r.Value!.Unstaged);
        Assert.Empty(r.Value!.Untracked);
    }

    [Fact]
    public async Task GetStatusAsync_WithUntrackedFile_ReturnsEntry()
    {
        File.WriteAllText(Path.Combine(_repoDir, "test.txt"), "hello");
        var access = new DirectGitAccess();

        var r = await access.GetStatusAsync(_repoDir);

        Assert.True(r.IsSuccess);
        Assert.NotEmpty(r.Value!.Untracked);
    }

    [Fact]
    public async Task CommitAsync_WithStagedChange_ReturnsHash()
    {
        File.WriteAllText(Path.Combine(_repoDir, "staged.txt"), "content");
        RunGit("add", "staged.txt");
        var access = new DirectGitAccess();

        var r = await access.CommitAsync(_repoDir, "feat: test commit");

        Assert.True(r.IsSuccess);
        Assert.NotEmpty(r.Value!.Hash);
        Assert.Equal("feat: test commit", r.Value.Message);
    }

    [Fact]
    public async Task GetDiffAsync_CleanRepo_ReturnsEmptyDiff()
    {
        var access = new DirectGitAccess();

        var r = await access.GetDiffAsync(_repoDir, "Unstaged", path: null);

        Assert.True(r.IsSuccess);
        Assert.Equal(0, r.Value!.Stats.Files);
    }
}
