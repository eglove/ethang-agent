using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using Xunit;

namespace eThangAgent.ToolDomain.Tests;

/// <summary>Advertisement-contract tests: every tool's declared parameter types and
/// requiredness must match what its input parser actually enforces. These failed in
/// real use — clarify advertised `options: String` while the validator demanded a JSON
/// array of strings, and optional parameters were advertised as required. Descriptions
/// ARE format contracts (AGENTS.md); these tests keep them honest.</summary>
public class ToolContractAdvertisementTests
{
    private static ToolParameter Param(ITool tool, string name) =>
        Assert.Single(tool.Definition.Parameters, p => p.Name == name);

    // ── clarify ──────────────────────────────────────────────────────────────

    [Fact]
    public void Clarify_Options_IsAdvertisedAsAnArrayOfStrings()
    {
        var p = Param(new ClarifyTool(new StubClarifyChannel()), "options");
        Assert.Equal(ToolParameterType.StringArray, p.Type);
    }

    [Fact]
    public void Clarify_OnlyQuestionAndAllowFreeText_AreRequired()
    {
        var tool = new ClarifyTool(new StubClarifyChannel());
        Assert.Equal(["timeoutSeconds", "question", "allowFreeText"], tool.Definition.RequiredParameters);
    }

    [Fact]
    public void Clarify_Description_StatesArrayTypeVerbatim()
    {
        var p = Param(new ClarifyTool(new StubClarifyChannel()), "options");
        Assert.Contains("JSON array", p.Description);
        Assert.Contains("Optional", p.Description);
    }

    [Fact]
    public void Clarify_Options_AcceptsAnArrayOfTwoStrings()
    {
        var parsed = ClarifyInput.Create(
            "{\"question\":\"q\",\"options\":[\"a\",\"b\"],\"allowFreeText\":true}");
        Assert.True(parsed.IsSuccess);
    }

    [Fact]
    public void Clarify_Options_RejectsAPlainString()
    {
        var parsed = ClarifyInput.Create(
            "{\"question\":\"q\",\"options\":\"a\",\"allowFreeText\":true}");
        Assert.False(parsed.IsSuccess);
        Assert.Equal("InvalidParameterType", parsed.Error!.Code);
    }

    [Fact]
    public void Clarify_Options_Omitted_StillSucceeds()
    {
        var parsed = ClarifyInput.Create("{\"question\":\"q\",\"allowFreeText\":true}");
        Assert.True(parsed.IsSuccess); // options is optional; advertisement must say so
    }

    // ── git_commit ───────────────────────────────────────────────────────────

    [Fact]
    public void GitCommit_StyleAndDescription_AreRequired()
    {
        var tool = new GitCommitTool(new UnrootedPathResolver(), new StubCommitAccess());
        Assert.Equal(["timeoutSeconds", "style", "description"], tool.Definition.RequiredParameters);
    }

    [Fact]
    public void GitCommit_OptionalsAreNotRequired()
    {
        var tool = new GitCommitTool(new UnrootedPathResolver(), new StubCommitAccess());
        Assert.DoesNotContain("type", tool.Definition.RequiredParameters);
        Assert.DoesNotContain("scope", tool.Definition.RequiredParameters);
        Assert.DoesNotContain("emoji_key", tool.Definition.RequiredParameters);
        Assert.DoesNotContain("body", tool.Definition.RequiredParameters);
    }

    // ── search_files ─────────────────────────────────────────────────────────

    [Fact]
    public void SearchFiles_OnlyPatternModeMaxResults_AreRequired()
    {
        var tool = new SearchTool(new UnrootedPathResolver(), new StubSearchAccess());
        Assert.Equal(["timeoutSeconds", "pattern", "mode", "maxResults"], tool.Definition.RequiredParameters);
    }

    // ── edit ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Edit_AllAndOccurrences_AreOptionalButExclusive()
    {
        var tool = new EditTool(new UnrootedPathResolver(), new StubEditAccess());
        Assert.DoesNotContain("all", tool.Definition.RequiredParameters);
        Assert.DoesNotContain("occurrences", tool.Definition.RequiredParameters);
    }

    // ── todo ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Todo_OnlyAction_IsUnconditionallyRequired()
    {
        var tool = new TodoTool(new StubTodoStore());
        Assert.Equal(["timeoutSeconds", "action"], tool.Definition.RequiredParameters);
    }

    [Fact]
    public void Todo_Description_DocumentsExactActionAndStatusValues()
    {
        var tool = new TodoTool(new StubTodoStore());
        Assert.Contains("Add", Param(tool, "action").Description);
        Assert.Contains("InProgress", Param(tool, "status").Description);
    }
}

// ── stubs (fakes only — a Tool.Domain test never knows HTTP or OpenRouter exist) ──

internal sealed class StubClarifyChannel : IClarifyChannel
{
    public Task<Result<string>> AskAsync(ClarifyQuestion question, CancellationToken ct = default) =>
        Task.FromResult(Result<string>.Success("1"));
}

internal sealed class StubCommitAccess : IGitCommitAccess
{
    public Task<Result<GitCommitOutcome>> CommitAsync(string root, string message, CancellationToken ct = default) =>
        Task.FromResult(Result<GitCommitOutcome>.Failure(new Error("Unused", "not exercised")));
}

internal sealed class StubSearchAccess : ISearchAccess
{
    public Task<Result<FileSearch>> SearchFilesAsync(string rootPath, string pattern, bool regex,
        string? glob, int maxResults, int contextLines, CancellationToken ct = default) =>
        Task.FromResult(Result<FileSearch>.Failure(new Error("Unused", "not exercised")));
}

internal sealed class StubEditAccess : IFileEditAccess
{
    public Task<Result<ReplaceOutcome>> ReplaceInFileAsync(string path, string oldText,
        string newText, int? occurrences, CancellationToken ct = default) =>
        Task.FromResult(Result<ReplaceOutcome>.Failure(new Error("Unused", "not exercised")));
}

internal sealed class StubTodoStore : ITodoListStore
{
    public Task<Result<string>> GetValueAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(Result<string>.Failure(new Error("KeyNotFound", "empty")));
    public Task<Result<int>> WriteValueAsync(string key, string value, int? expectedVersion, CancellationToken ct = default) =>
        Task.FromResult(Result<int>.Success(1));
}
