using eThangAgent.SharedKernel;

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
    ToolParameter p = Param(new ClarifyTool(new StubClarifyChannel()), "options");
    Assert.Equal(ToolParameterType.TextArray, p.Type);
  }

  [Fact]
  public void Clarify_OnlyQuestionAndAllowFreeText_AreRequired()
  {
    ClarifyTool tool = new(new StubClarifyChannel());
    Assert.Equal(["timeoutSeconds", "question", "allowFreeText"], tool.Definition.RequiredParameters);
  }

  [Fact]
  public void Clarify_Description_StatesArrayTypeVerbatim()
  {
    ToolParameter p = Param(new ClarifyTool(new StubClarifyChannel()), "options");
    Assert.Contains("JSON array", p.Description, StringComparison.Ordinal);
    Assert.Contains("Optional", p.Description, StringComparison.Ordinal);
  }

  [Fact]
  public void Clarify_Options_AcceptsAnArrayOfTwoStrings()
  {
    Result<ClarifyInput> parsed = ClarifyInput.Create(
                                 /*lang=json,strict*/
                                 "{\"question\":\"q\",\"options\":[\"a\",\"b\"],\"allowFreeText\":true}");
    Assert.True(parsed.IsSuccess);
  }

  [Fact]
  public void Clarify_Options_RejectsAPlainString()
  {
    Result<ClarifyInput> parsed = ClarifyInput.Create(
                                 /*lang=json,strict*/
                                 "{\"question\":\"q\",\"options\":\"a\",\"allowFreeText\":true}");
    Assert.False(parsed.IsSuccess);
    Assert.Equal("InvalidParameterType", parsed.Error.Code);
  }


  [Fact]
  public void Clarify_Description_StatesTheHumanWaitHasNoTimeLimit()
  {
    // Format contract: the model must know the clarify wait cannot time out.
    ClarifyTool tool = new(new StubClarifyChannel());
    Assert.Contains("NO time limit", tool.Definition.Description, StringComparison.Ordinal);
    Assert.Contains("Error [ToolTimeout]", tool.Definition.Description, StringComparison.Ordinal);
  }
  [Fact]
  public void Clarify_Options_Omitted_StillSucceeds()
  {
    Result<ClarifyInput> parsed = ClarifyInput.Create(/*lang=json,strict*/ "{\"question\":\"q\",\"allowFreeText\":true}");
    Assert.True(parsed.IsSuccess); // options is optional; advertisement must say so
  }

  // ── git_commit ───────────────────────────────────────────────────────────

  private static GitCommitTool NewTool() =>
      new(new UnrootedPathResolver(), new StubCommitAccess(), new FixedStyleProvider(CommitStyle.None));

  [Fact]
  public void GitCommit_OnlyTimeoutAndDescription_AreRequired() =>
      Assert.Equal(["timeoutSeconds", "description"], NewTool().Definition.RequiredParameters);

  [Fact]
  public void GitCommit_Style_IsNotAModelFacingParameter() =>
      // The style is a host setting resolved at execution time — it must not be
      // advertised to the model; stale callers naming it are rejected as unknown
      // input by the parser (pinned in GitCommitToolStyleResolutionTests).
      Assert.DoesNotContain(NewTool().Definition.Parameters, p => p.Name == "style");

  [Fact]
  public void GitCommit_Description_StatesStyleComesFromHostSetting()
  {
    string d = NewTool().Definition.Description;
    Assert.Contains("style", d, StringComparison.Ordinal);
    Assert.Contains("host", d, StringComparison.Ordinal);
  }

  [Fact]
  public void GitCommit_OptionalsAreNotRequired()
  {
    GitCommitTool tool = NewTool();
    Assert.DoesNotContain("type", tool.Definition.RequiredParameters);
    Assert.DoesNotContain("scope", tool.Definition.RequiredParameters);
    Assert.DoesNotContain("emoji_key", tool.Definition.RequiredParameters);
    Assert.DoesNotContain("body", tool.Definition.RequiredParameters);
  }

  [Fact]
  public void GitCommit_Files_IsAdvertisedAsAnOptionalArrayOfStrings()
  {
    GitCommitTool tool = NewTool();
    ToolParameter p = Param(tool, "files");
    Assert.Equal(ToolParameterType.TextArray, p.Type);
    Assert.DoesNotContain("files", tool.Definition.RequiredParameters);
  }

  [Fact]
  public void GitCommit_Files_Description_StatesTheStagingContract()
  {
    ToolParameter p = Param(NewTool(), "files");
    Assert.Contains("JSON array of workspace-relative paths", p.Description, StringComparison.Ordinal);
    Assert.Contains("non-empty relative paths", p.Description, StringComparison.Ordinal);
    Assert.Contains("'..'", p.Description, StringComparison.Ordinal);
    Assert.Contains("Omit to commit the index as-is", p.Description, StringComparison.Ordinal);
  }

  [Fact]
  public void GitCommit_Description_StatesBothModes()
  {
    string d = NewTool().Definition.Description;
    Assert.Contains("never stages", d, StringComparison.Ordinal);
    Assert.Contains("stages exactly those", d, StringComparison.Ordinal);
  }
  // ── search_files ─────────────────────────────────────────────────────────

  [Fact]
  public void SearchFiles_OnlyPatternModeMaxResults_AreRequired()
  {
    SearchTool tool = new(new UnrootedPathResolver(), new StubSearchAccess());
    Assert.Equal(["timeoutSeconds", "pattern", "mode", "maxResults"], tool.Definition.RequiredParameters);
  }

  // ── edit ─────────────────────────────────────────────────────────────────

  [Fact]
  public void Edit_AllAndOccurrences_AreOptionalButExclusive()
  {
    EditTool tool = new(new UnrootedPathResolver(), new StubEditAccess());
    Assert.DoesNotContain("all", tool.Definition.RequiredParameters);
    Assert.DoesNotContain("occurrences", tool.Definition.RequiredParameters);
    Assert.Contains("replacement", tool.Definition.RequiredParameters);
    Assert.DoesNotContain("new", tool.Definition.RequiredParameters);
  }

  // ── write ─────────────────────────────────────────────────────────────────

  [Fact]
  public void Write_ContentAndLines_AreOptionalButExclusive_WithContentRequiredByDefault()
  {
    WriteTool tool = new(new UnrootedPathResolver(), new StubWriteAccess());
    Assert.DoesNotContain("content", tool.Definition.RequiredParameters);
    Assert.DoesNotContain("lines", tool.Definition.RequiredParameters);
    Assert.DoesNotContain("overwrite", tool.Definition.RequiredParameters);
  }

  [Fact]
  public void Write_Lines_IsAdvertisedAsATextArrayOfStrings()
  {
    WriteTool tool = new(new UnrootedPathResolver(), new StubWriteAccess());
    ToolParameter p = Param(tool, "lines");
    Assert.Equal(ToolParameterType.TextArray, p.Type);
  }

  // ── todo ─────────────────────────────────────────────────────────────────

  [Fact]
  public void Todo_OnlyAction_IsUnconditionallyRequired()
  {
    TodoTool tool = new(new StubTodoStore());
    Assert.Equal(["timeoutSeconds", "action"], tool.Definition.RequiredParameters);
  }

  [Fact]
  public void Todo_Description_DocumentsExactActionAndStatusValues()
  {
    TodoTool tool = new(new StubTodoStore());
    Assert.Contains("Add", Param(tool, "action").Description, StringComparison.Ordinal);
    Assert.Contains("InProgress", Param(tool, "status").Description, StringComparison.Ordinal);
  }

  // ── db_query ─────────────────────────────────────────────────────────────

  [Fact]
  public void DbQuery_OnlySql_IsRequired()
  {
    DbQueryTool tool = new(new FakeSelfDatabaseAccess());
    Assert.Equal(["timeoutSeconds", "sql"], tool.Definition.RequiredParameters);
  }

  [Fact]
  public void DbQuery_MaxRows_IsAdvertisedAsOptionalBoundedWholeNumber()
  {
    ToolParameter p = Param(new DbQueryTool(new FakeSelfDatabaseAccess()), "maxRows");
    Assert.Equal(ToolParameterType.WholeNumber, p.Type);
    Assert.Contains("default 100", p.Description, StringComparison.Ordinal);
    Assert.Contains("1000", p.Description, StringComparison.Ordinal);
  }

  [Fact]
  public void DbQuery_Description_DocumentsTheOutputContract()
  {
    string description = new DbQueryTool(new FakeSelfDatabaseAccess()).Definition.Description;
    Assert.Contains("[db_query]", description, StringComparison.Ordinal);
    Assert.Contains("Error [InvalidSql]", description, StringComparison.Ordinal);
    Assert.Contains("Error [QueryFailed]", description, StringComparison.Ordinal);
    Assert.Contains("<null>", description, StringComparison.Ordinal);
    Assert.Contains("<blob N bytes>", description, StringComparison.Ordinal);
    Assert.Contains("\\|", description, StringComparison.Ordinal);
  }

  // ── db_schema ────────────────────────────────────────────────────────────

  [Fact]
  public void DbSchema_IncludeCounts_IsOptional()
  {
    DbSchemaTool tool = new(new FakeSelfDatabaseAccess());
    Assert.Equal(["timeoutSeconds"], tool.Definition.RequiredParameters);
    Assert.DoesNotContain("includeCounts", tool.Definition.RequiredParameters);
  }

  [Fact]
  public void DbSchema_Description_DocumentsAnnotationAndHiddenTables()
  {
    string description = new DbSchemaTool(new FakeSelfDatabaseAccess()).Definition.Description;
    Assert.Contains("[db_schema]", description, StringComparison.Ordinal);
    Assert.Contains("sqlite_*", description, StringComparison.Ordinal);
    Assert.Contains("db_query", description, StringComparison.Ordinal);
  }
}

// ── stubs (fakes only — a Tool.Domain test never knows HTTP or OpenRouter exist) ──

internal sealed class StubClarifyChannel : IClarifyChannel
{
  public Task<Result<string>> AskAsync(ClarifyQuestion question, CancellationToken ct = default) =>
      Task.FromResult(Result.Success("1"));
}

internal sealed class StubCommitAccess : IGitCommitAccess
{
  public Task<Result<bool>> StageAsync(string repoPath, IReadOnlyList<string> paths, CancellationToken ct = default) =>
      Task.FromResult(Result.Failure<bool>(new DomainError("Unused", "not exercised")));

  public Task<Result<GitCommitOutcome>> CommitAsync(string repoPath, string message, CancellationToken ct = default) =>
      Task.FromResult(Result.Failure<GitCommitOutcome>(new DomainError("Unused", "not exercised")));
}

internal sealed class StubSearchAccess : ISearchAccess
{
  public Task<Result<FileSearch>> SearchFilesAsync(string rootPath, string pattern, bool regex,
      string? glob, int maxResults, int contextLines, CancellationToken ct = default) =>
      Task.FromResult(Result.Failure<FileSearch>(new DomainError("Unused", "not exercised")));
}

internal sealed class StubEditAccess : IFileEditAccess
{
  public Task<Result<ReplaceOutcome>> ReplaceInFileAsync(string path, string oldText,
      string newText, int? occurrences, CancellationToken ct = default) =>
      Task.FromResult(Result.Failure<ReplaceOutcome>(new DomainError("Unused", "not exercised")));
}


internal sealed class StubWriteAccess : IFileWriteAccess
{
  public Task<Result<FileWriteOutcome>> WriteFileAsync(string path, string content,
      bool overwrite, CancellationToken ct = default) =>
      Task.FromResult(Result.Failure<FileWriteOutcome>(new DomainError("Unused", "not exercised")));
  public Task<Result<FileWriteOutcome>> WriteFileBytesAsync(string path, byte[] bytes,
      bool overwrite, CancellationToken ct = default) =>
      Task.FromResult(Result.Failure<FileWriteOutcome>(new DomainError("Unused", "not exercised")));
}

internal sealed class StubTodoStore : ITodoListStore
{
  public Task<Result<string>> GetValueAsync(string key, CancellationToken ct = default) =>
      Task.FromResult(Result.Failure<string>(new DomainError("KeyNotFound", "empty")));
  public Task<Result<int>> WriteValueAsync(string key, string value, int? expectedVersion, CancellationToken ct = default) =>
      Task.FromResult(Result.Success(1));
}
