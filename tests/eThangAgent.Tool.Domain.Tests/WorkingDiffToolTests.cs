using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.ToolDomain.Tests;

public class WorkingDiffToolTests
{
    private const string Root = @"C:\ws";
    private const string SubFile = @"C:\ws\sub\a.cs";

    private static WorkingDiffTool Make(Result<GitDiff> diff, out FakeGitQueryAccess fake)
    {
        fake = new FakeGitQueryAccess(diff);
        return new WorkingDiffTool(new WorkspacePathResolver(Root), fake);
    }

    private static Result<GitDiff> Ok(
        int files = 2, int additions = 3, int deletions = 1,
        string? patch = null, bool truncated = false, int totalChars = 0) =>
        Result<GitDiff>.Success(new GitDiff(
            new GitDiffStats(files, additions, deletions),
            patch ?? "diff --git a/x.cs b/x.cs\nindex 111..222 100644\n--- a/x.cs\n+++ b/x.cs\n",
            truncated,
            totalChars == 0 ? (patch ?? "diff").Length : totalChars));

    // ---- Input contract ----

    [Fact]
    public async Task MissingScope_ReturnsError()
    {
        var tool = Make(Ok(), out _);
        var result = await tool.ExecuteAsync(new RawToolInput("working_diff", "{\"timeoutSeconds\":120}"));
        Assert.True(result.IsError);
        Assert.Contains("MissingParameter", result.Content);
        Assert.Contains("'scope'", result.Content);
    }

    [Fact]
    public async Task WrongTypeScope_Rejected()
    {
        var tool = Make(Ok(), out _);
        var result = await tool.ExecuteAsync(new RawToolInput("working_diff",
            """{"timeoutSeconds":120,"scope":42}"""));
        Assert.True(result.IsError);
        Assert.Contains("InvalidParameterType", result.Content);
        Assert.Contains("string", result.Content);
    }

    [Fact]
    public async Task InvalidScopeValue_Rejected()
    {
        var tool = Make(Ok(), out _);
        var result = await tool.ExecuteAsync(new RawToolInput("working_diff",
            """{"timeoutSeconds":120,"scope":"Both"}"""));
        Assert.True(result.IsError);
        Assert.Contains("InvalidParameterValue", result.Content);
        Assert.Contains("Staged", result.Content);
        Assert.Contains("Unstaged", result.Content);
        Assert.Contains("All", result.Content);
    }

    [Fact]
    public async Task ScopeIsCaseSensitive_LowercaseRejected()
    {
        var tool = Make(Ok(), out _);
        var result = await tool.ExecuteAsync(new RawToolInput("working_diff",
            """{"timeoutSeconds":120,"scope":"staged"}"""));
        Assert.True(result.IsError);
        Assert.Contains("InvalidParameterValue", result.Content);
    }

    [Fact]
    public async Task EmptyStringPath_Rejected()
    {
        var tool = Make(Ok(), out _);
        var result = await tool.ExecuteAsync(new RawToolInput("working_diff",
            """{"timeoutSeconds":120,"scope":"All","path":""}"""));
        Assert.True(result.IsError);
        Assert.Contains("InvalidParameterValue", result.Content);
        Assert.Contains("'path'", result.Content);
    }

    [Fact]
    public async Task PathOutsideWorkspace_SurfacesResolverError()
    {
        var tool = Make(Ok(), out _);
        var result = await tool.ExecuteAsync(new RawToolInput("working_diff",
            """{"timeoutSeconds":120,"scope":"All","path":"..\\evil.txt"}"""));
        Assert.True(result.IsError);
        Assert.Contains("PathOutsideWorkspace", result.Content);
    }

    [Fact]
    public async Task UnknownParameter_Rejected()
    {
        var tool = Make(Ok(), out _);
        var result = await tool.ExecuteAsync(new RawToolInput("working_diff",
            """{"timeoutSeconds":120,"scope":"All","stat":true}"""));
        Assert.True(result.IsError);
        Assert.Contains("Unknown parameter", result.Content);
        Assert.Contains("stat", result.Content);
    }

    // ---- Output contract ----

    [Fact]
    public async Task Success_HeaderMathComesFromFakeStats()
    {
        var tool = Make(Ok(files: 2, additions: 3, deletions: 1), out _);
        var result = await tool.ExecuteAsync(new RawToolInput("working_diff",
            """{"timeoutSeconds":120,"scope":"All"}"""));
        Assert.False(result.IsError);
        Assert.StartsWith("[working-diff scope=All path=none: 2 file(s), +3/-1 lines]\n", result.Content);
    }

    [Fact]
    public async Task Patch_PassedThroughVerbatim()
    {
        var patch = "+hello\tworld\n-old line\n@@ -1 +1 @@\n+new line with <xml> & \"quotes\"\n";
        var tool = Make(Ok(patch: patch), out _);
        var result = await tool.ExecuteAsync(new RawToolInput("working_diff",
            """{"timeoutSeconds":120,"scope":"Unstaged"}"""));
        Assert.False(result.IsError);
        Assert.Equal(
            "[working-diff scope=Unstaged path=none: 2 file(s), +3/-1 lines]\n" + patch,
            result.Content);
    }

    [Fact]
    public async Task Truncation_AppendsExactWarningLine()
    {
        var patch = "diff --git a/x.cs b/x.cs\n+truncated tail\n";
        var tool = Make(Ok(patch: patch, truncated: true, totalChars: 45123), out _);
        var result = await tool.ExecuteAsync(new RawToolInput("working_diff",
            """{"timeoutSeconds":120,"scope":"All"}"""));
        Assert.False(result.IsError);
        Assert.EndsWith(
            "\n[warning] truncated at 20000 chars; total 45123 — narrow with path/scope",
            result.Content);
    }

    [Fact]
    public async Task NoDifferences_UsesContractLine()
    {
        var tool = Make(Ok(files: 0, additions: 0, deletions: 0, patch: ""), out _);
        var result = await tool.ExecuteAsync(new RawToolInput("working_diff",
            """{"timeoutSeconds":120,"scope":"Staged"}"""));
        Assert.False(result.IsError);
        Assert.Equal("[working-diff scope=Staged path=none: no differences]", result.Content);
    }

    // ---- Path resolution and forwarding ----

    [Fact]
    public async Task ResolvedAbsolutePath_ScopeAndRoot_CapturedByFake()
    {
        var tool = Make(Ok(), out var fake);
        var result = await tool.ExecuteAsync(new RawToolInput("working_diff",
            """{"timeoutSeconds":120,"scope":"Unstaged","path":"sub/a.cs"}"""));
        Assert.False(result.IsError);
        Assert.Equal(Root, fake.RepoPath);
        Assert.Equal(SubFile, fake.Path);
        Assert.Equal("Unstaged", fake.Scope);
        // The header shows the resolved absolute path actually queried.
        Assert.StartsWith($"[working-diff scope=Unstaged path={SubFile}: ", result.Content);
    }

    // ---- Backend errors surface verbatim ----

    [Fact]
    public async Task NotAGitRepository_SurfacesBackendError()
    {
        var tool = Make(Result<GitDiff>.Failure(
                new Error("NotAGitRepository", $"Not a git repository: {Root}")),
            out _);
        var result = await tool.ExecuteAsync(new RawToolInput("working_diff",
            """{"timeoutSeconds":120,"scope":"All"}"""));
        Assert.True(result.IsError);
        Assert.Contains($"Error [NotAGitRepository]: Not a git repository: {Root}", result.Content);
    }

    [Fact]
    public async Task GitError_SurfacesBackendError()
    {
        var tool = Make(Result<GitDiff>.Failure(
                new Error("GitError", "fatal: unable to read tree")),
            out _);
        var result = await tool.ExecuteAsync(new RawToolInput("working_diff",
            """{"timeoutSeconds":120,"scope":"Staged"}"""));
        Assert.True(result.IsError);
        Assert.Contains("Error [GitError]: fatal: unable to read tree", result.Content);
    }

    private sealed class FakeGitQueryAccess(Result<GitDiff> diff) : IGitQueryAccess
    {
        public string RepoPath { get; private set; } = "";
        public string Scope { get; private set; } = "";
        public string? Path { get; private set; }

        public Task<Result<GitStatus>> GetStatusAsync(string repoPath, CancellationToken ct = default)
            => throw new NotSupportedException("working_diff never queries status.");

        public Task<Result<GitDiff>> GetDiffAsync(string repoPath, string scope, string? path, CancellationToken ct = default)
        {
            RepoPath = repoPath;
            Scope = scope;
            Path = path;
            return Task.FromResult(diff);
        }
    }
}
