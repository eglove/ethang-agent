using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.ToolDomain.Tests;

public class GitStatusToolTests
{
    private const string Root = @"C:\ws";

    private static (GitStatusTool Tool, FakeGitQueryAccess Fake) Make(GitStatus? status = null)
    {
        var fake = new FakeGitQueryAccess(
            status is null
                ? Result<GitStatus>.Failure(new Error("NotAGitRepository", $"Not a git repository: {Root}"))
                : Result<GitStatus>.Success(status));
        return (new GitStatusTool(new WorkspacePathResolver(Root), fake), fake);
    }

    // ---- Output contract ----

    [Fact]
    public async Task CleanRepo_FormatsCleanLine()
    {
        var (tool, _) = Make(new GitStatus("main", [], [], []));
        var result = await tool.ExecuteAsync(new RawToolInput("git_status", "{\"timeoutSeconds\":120}"));
        Assert.False(result.IsError);
        Assert.Equal("[git-status main: clean]", result.Content);
    }

    [Fact]
    public async Task MixedGroups_ExactFullStringOutput()
    {
        var status = new GitStatus("main",
            [new GitStatusEntry("M ", "src/a.cs")],
            [new GitStatusEntry(" M", "src/b.cs")],
            ["notes.txt"]);
        var (tool, _) = Make(status);
        var result = await tool.ExecuteAsync(new RawToolInput("git_status", "{\"timeoutSeconds\":120}"));
        Assert.False(result.IsError);
        Assert.Equal(
            """
            [git-status main: 1 staged, 1 unstaged, 1 untracked]
            staged:
            M  src/a.cs
            unstaged:
             M src/b.cs
            untracked:
            ?? notes.txt
            """,
            result.Content);
    }

    [Fact]
    public async Task EmptyGroups_AreOmitted()
    {
        var status = new GitStatus("feature",
            [new GitStatusEntry("A ", "x.cs"), new GitStatusEntry("D ", "y.cs")],
            [], []);
        var (tool, _) = Make(status);
        var result = await tool.ExecuteAsync(new RawToolInput("git_status", "{\"timeoutSeconds\":120}"));
        Assert.False(result.IsError);
        Assert.Equal(
            """
            [git-status feature: 2 staged, 0 unstaged, 0 untracked]
            staged:
            A  x.cs
            D  y.cs
            """,
            result.Content);
    }

    // ---- Backend errors surface verbatim ----

    [Fact]
    public async Task NotAGitRepository_SurfacesBackendError()
    {
        var (tool, _) = Make();
        var result = await tool.ExecuteAsync(new RawToolInput("git_status", "{\"timeoutSeconds\":120}"));
        Assert.True(result.IsError);
        Assert.Contains($"Error [NotAGitRepository]: Not a git repository: {Root}", result.Content);
    }

    [Fact]
    public async Task GitError_SurfacesBackendError()
    {
        var fake = new FakeGitQueryAccess(
            Result<GitStatus>.Failure(new Error("GitError", "fatal: bad object HEAD")));
        var tool = new GitStatusTool(new WorkspacePathResolver(Root), fake);
        var result = await tool.ExecuteAsync(new RawToolInput("git_status", "{\"timeoutSeconds\":120}"));
        Assert.True(result.IsError);
        Assert.Contains("Error [GitError]: fatal: bad object HEAD", result.Content);
    }

    // ---- Input contract ----

    [Fact]
    public async Task UnknownParameter_Rejected()
    {
        var (tool, _) = Make();
        var result = await tool.ExecuteAsync(new RawToolInput("git_status",
            """{"timeoutSeconds":120,"verbose":true}"""));
        Assert.True(result.IsError);
        Assert.Contains("Unknown parameter", result.Content);
        Assert.Contains("verbose", result.Content);
    }

    [Fact]
    public async Task NonObjectArguments_Rejected()
    {
        var (tool, _) = Make();
        var result = await tool.ExecuteAsync(new RawToolInput("git_status", "[1]"));
        Assert.True(result.IsError);
        Assert.Contains("JSON object", result.Content);
    }

    [Fact]
    public async Task EmptyObjectArguments_Accepted()
    {
        var (tool, _) = Make(new GitStatus("main", [], [], []));
        var result = await tool.ExecuteAsync(new RawToolInput("git_status", "{\"timeoutSeconds\":120}"));
        Assert.False(result.IsError);
        Assert.Equal("[git-status main: clean]", result.Content);
    }

    // The mandatory timeoutSeconds budget means arguments are never optional:
    // an empty payload is a MissingParameter error, not an implicit empty object.
    [Fact]
    public async Task MissingArguments_Rejected_MissingParameter()
    {
        var (tool, _) = Make(new GitStatus("main", [], [], []));
        var result = await tool.ExecuteAsync(new RawToolInput("git_status", ""));
        Assert.True(result.IsError);
        // An empty payload is not valid JSON, so it fails one step earlier than
        // the budget check: malformed arguments, budget never reached.
        Assert.Contains("InvalidJsonArguments", result.Content);
    }

    [Fact]
    public async Task ResolvesImplicitRoot_AndPassesToQuery()
    {
        var (tool, fake) = Make(new GitStatus("main", [], [], []));
        await tool.ExecuteAsync(new RawToolInput("git_status", "{\"timeoutSeconds\":120}"));
        Assert.Equal(Root, fake.RepoPath);
    }

    private sealed class FakeGitQueryAccess(Result<GitStatus> status) : IGitQueryAccess
    {
        public string RepoPath { get; private set; } = "";

        public Task<Result<GitStatus>> GetStatusAsync(string repoPath, CancellationToken ct = default)
        {
            RepoPath = repoPath;
            return Task.FromResult(status);
        }

        public Task<Result<GitDiff>> GetDiffAsync(string repoPath, string scope, string? path, CancellationToken ct = default)
            => throw new NotSupportedException("git_status never diffs.");
    }
}
