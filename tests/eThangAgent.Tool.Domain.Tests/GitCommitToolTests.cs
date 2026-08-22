using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.ToolDomain.Tests;

public class GitCommitToolTests
{
    private const string Root = @"C:\ws";

    private static GitCommitTool Make(Result<GitCommitOutcome> outcome, out FakeGitCommitAccess fake)
    {
        fake = new FakeGitCommitAccess(outcome);
        return new GitCommitTool(new WorkspacePathResolver(Root), fake);
    }

    private static Result<GitCommitOutcome> OkFor(string message) =>
        Result<GitCommitOutcome>.Success(new GitCommitOutcome("abc1234", "main", message));

    // ---- Rendering flows through to the commit seam ----

    [Fact]
    public async Task HappyConventionalWithScope_CommitsExactRenderedMessage()
    {
        var tool = Make(OkFor("feat(tools): add git tools\n"), out var fake);
        var result = await tool.ExecuteAsync(new RawToolInput("git_commit",
            """{"style":"Conventional","type":"feat","scope":"tools","description":"add git tools"}"""));
        Assert.False(result.IsError);
        Assert.Equal("feat(tools): add git tools\n", fake.Message);
        Assert.Equal(Root, fake.RepoPath);
    }

    [Fact]
    public async Task HappyConventionalWithScope_FormatsAnnotationPlusMessageBlock()
    {
        var tool = Make(OkFor("feat(tools): add git tools\n"), out _);
        var result = await tool.ExecuteAsync(new RawToolInput("git_commit",
            """{"style":"Conventional","type":"feat","scope":"tools","description":"add git tools"}"""));
        Assert.False(result.IsError);
        Assert.Equal("[git-commit abc1234] committed on main\nfeat(tools): add git tools\n", result.Content);
    }

    [Fact]
    public async Task Gitmoji_RendersEmojiIntoCommittedMessage()
    {
        var tool = Make(OkFor("\u2728 add git tools\n"), out var fake);
        var result = await tool.ExecuteAsync(new RawToolInput("git_commit",
            """{"style":"Gitmoji","emoji_key":":sparkles:","description":"add git tools"}"""));
        Assert.False(result.IsError);
        Assert.Equal("\u2728 add git tools\n", fake.Message);
    }

    [Fact]
    public async Task StyleNone_DescriptionStandsAlone()
    {
        var tool = Make(OkFor("wip notes\n"), out var fake);
        var result = await tool.ExecuteAsync(new RawToolInput("git_commit",
            """{"style":"None","description":"wip notes"}"""));
        Assert.False(result.IsError);
        Assert.Equal("wip notes\n", fake.Message);
    }

    [Fact]
    public async Task Body_FlowsThroughToCommittedMessage()
    {
        var tool = Make(OkFor("fix(tools): guard input\n\ndetail line\n"), out var fake);
        var result = await tool.ExecuteAsync(new RawToolInput("git_commit",
            """{"style":"Conventional","type":"fix","scope":"tools","description":"guard input","body":"detail line"}"""));
        Assert.False(result.IsError);
        Assert.Equal("fix(tools): guard input\n\ndetail line\n", fake.Message);
    }

    // ---- CommitMessage validation codes surface verbatim ----

    [Fact]
    public async Task InvalidStyle_SurfacesVerbatim()
    {
        var tool = Make(null!, out _);
        var result = await tool.ExecuteAsync(new RawToolInput("git_commit",
            """{"style":"Bogus","description":"x"}"""));
        Assert.True(result.IsError);
        Assert.Contains("Error [InvalidStyle]:", result.Content);
    }

    [Fact]
    public async Task UnknownType_SurfacesVerbatim()
    {
        var tool = Make(null!, out _);
        var result = await tool.ExecuteAsync(new RawToolInput("git_commit",
            """{"style":"Conventional","type":"banana","description":"x"}"""));
        Assert.True(result.IsError);
        Assert.Contains("Error [UnknownType]:", result.Content);
    }

    [Fact]
    public async Task TypeRequired_SurfacesVerbatim()
    {
        var tool = Make(null!, out _);
        var result = await tool.ExecuteAsync(new RawToolInput("git_commit",
            """{"style":"Conventional","description":"x"}"""));
        Assert.True(result.IsError);
        Assert.Contains("Error [TypeRequired]:", result.Content);
    }

    [Fact]
    public async Task ParameterNotAllowed_SurfacesVerbatim()
    {
        var tool = Make(null!, out _);
        var result = await tool.ExecuteAsync(new RawToolInput("git_commit",
            """{"style":"Conventional","type":"feat","emoji_key":":sparkles:","description":"x"}"""));
        Assert.True(result.IsError);
        Assert.Contains("Error [ParameterNotAllowed]:", result.Content);
    }

    [Fact]
    public async Task DescriptionTooLong_SurfacesVerbatim()
    {
        var tool = Make(null!, out _);
        var longDescription = new string('a', 73);
        var result = await tool.ExecuteAsync(new RawToolInput("git_commit",
            $$"""{"style":"None","description":"{{longDescription}}"}"""));
        Assert.True(result.IsError);
        Assert.Contains("Error [DescriptionTooLong]:", result.Content);
    }

    // ---- Input contract ----

    [Fact]
    public async Task MissingDescription_ReturnsMissingParameter()
    {
        var tool = Make(null!, out _);
        var result = await tool.ExecuteAsync(new RawToolInput("git_commit",
            """{"style":"None"}"""));
        Assert.True(result.IsError);
        Assert.Contains("MissingParameter", result.Content);
        Assert.Contains("'description'", result.Content);
    }

    [Fact]
    public async Task MissingStyle_ReturnsMissingParameter()
    {
        var tool = Make(null!, out _);
        var result = await tool.ExecuteAsync(new RawToolInput("git_commit",
            """{"description":"x"}"""));
        Assert.True(result.IsError);
        Assert.Contains("MissingParameter", result.Content);
        Assert.Contains("'style'", result.Content);
    }

    [Fact]
    public async Task UnknownParameter_Rejected()
    {
        var tool = Make(null!, out _);
        var result = await tool.ExecuteAsync(new RawToolInput("git_commit",
            """{"style":"None","description":"x","author":"me"}"""));
        Assert.True(result.IsError);
        Assert.Contains("Unknown parameter", result.Content);
        Assert.Contains("author", result.Content);
    }

    // ---- Backend errors surface verbatim with hints ----

    [Fact]
    public async Task NothingStaged_SurfacesBackendHint()
    {
        var tool = Make(Result<GitCommitOutcome>.Failure(new Error("NothingStaged",
                $"The index is empty; there is nothing to commit in {Root}. Stage changes first (e.g. exec: git add <file>).")),
            out _);
        var result = await tool.ExecuteAsync(new RawToolInput("git_commit",
            """{"style":"None","description":"x"}"""));
        Assert.True(result.IsError);
        Assert.Contains("Error [NothingStaged]:", result.Content);
        Assert.Contains("Stage changes first", result.Content);
    }

    private sealed class FakeGitCommitAccess(Result<GitCommitOutcome> outcome) : IGitCommitAccess
    {
        public string RepoPath { get; private set; } = "";
        public string Message { get; private set; } = "";

        public Task<Result<GitCommitOutcome>> CommitAsync(string repoPath, string message, CancellationToken ct = default)
        {
            RepoPath = repoPath;
            Message = message;
            return Task.FromResult(outcome);
        }
    }
}
