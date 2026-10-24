using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

/// <summary>Issue #251's model-facing half: the annotation line STATES the convention the
///     write or edit landed in, so the model never hand-rolls EOL handling in a script
///     (the behavior that produced the two CRLF-in-an-LF-repo incidents).</summary>
public class LineEndingAnnotationTests
{
  private const string Root = @"C:\ws";
  private const string Resolved = @"C:\ws\a.txt";

  [Fact]
  public async Task Write_StatesTheNormalizedConvention()
  {
    CapturingWriteAccess fake = new(Result.Success(new FileWriteOutcome(true, 42, LineEnding.Lf)));
    ToolResult result = await new WriteTool(new WorkspacePathResolver(Root), fake)
      .ExecuteAsync(new RawToolInput("write",
        /*lang=json,strict*/
        """{"timeoutSeconds":120,"path":"a.txt","content":"x\ny","overwrite":false}"""), ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    Assert.Equal($"[write {Resolved}] created, 42 bytes, LF line endings", result.Content);
  }

  [Fact]
  public async Task Write_LinesMode_SendsLinesToTheBackend()
  {
    CapturingWriteAccess fake = new(Result.Success(new FileWriteOutcome(true, 5, LineEnding.CrLf)));
    ToolResult result = await new WriteTool(new WorkspacePathResolver(Root), fake)
      .ExecuteAsync(new RawToolInput("write",
        /*lang=json,strict*/
        """{"timeoutSeconds":120,"path":"a.txt","lines":["first","second"],"overwrite":false}"""), ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    Assert.Equal(["first", "second"], fake.LastLines);
    Assert.Contains("CRLF line endings", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Edit_AnchorMode_StatesTheNormalizedConvention()
  {
    CapturingEditAccess fake = new(Result.Success(new ReplaceOutcome(1, 3, LineEnding.CrLf)));
    ToolResult result = await new EditTool(new WorkspacePathResolver(Root), fake)
      .ExecuteAsync(new RawToolInput("edit",
        /*lang=json,strict*/
        """{"timeoutSeconds":120,"path":"a.txt","old":"x","replacement":"y","all":true}"""), ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    Assert.Equal($"[edit {Resolved}] replaced 1 occurrence, file now 3 lines, CRLF line endings", result.Content);
  }

  [Fact]
  public async Task Edit_RangeMode_StatesTheNormalizedConvention()
  {
    CapturingEditAccess fake = new(Result.Success(new ReplaceOutcome(2, 3, LineEnding.Lf)));
    ToolResult result = await new EditTool(new WorkspacePathResolver(Root), fake)
      .ExecuteAsync(new RawToolInput("edit",
        /*lang=json,strict*/
        """{"timeoutSeconds":120,"path":"a.txt","startLine":1,"endLine":2,"replacement":"y"}"""), ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    Assert.Equal($"[edit {Resolved}] replaced lines 1-2, file now 3 lines, LF line endings", result.Content);
  }

  private sealed class CapturingWriteAccess(Result<FileWriteOutcome> outcome) : IFileWriteAccess
  {
    public IReadOnlyList<string>? LastLines { get; private set; }

    public Task<Result<FileWriteOutcome>> WriteFileAsync(
        string path, string content, bool overwrite, CancellationToken ct = default)
        => Task.FromResult(outcome);

    public Task<Result<FileWriteOutcome>> WriteFileLinesAsync(
        string path, IReadOnlyList<string> lines, bool overwrite, CancellationToken ct = default)
    {
      LastLines = lines;
      return Task.FromResult(outcome);
    }

    public Task<Result<FileWriteOutcome>> WriteFileBytesAsync(
        string path, byte[] bytes, bool overwrite, CancellationToken ct = default)
        => throw new NotSupportedException();
  }

  private sealed class CapturingEditAccess(Result<ReplaceOutcome> outcome) : IFileEditAccess
  {
    public Task<Result<ReplaceOutcome>> ReplaceInFileAsync(
        string path, string oldText, string newText, int? occurrences, CancellationToken ct = default)
        => Task.FromResult(outcome);

    public Task<Result<ReplaceOutcome>> ReplaceLineRangeAsync(
        string path, int startLine, int endLine, string newText, CancellationToken ct = default)
        => Task.FromResult(outcome);

    public Task<Result<HunkReplaceOutcome>> ReplaceHunksAsync(
        string path, IReadOnlyList<EditHunk> hunks, CancellationToken ct = default)
        => throw new NotImplementedException("hunks mode is not exercised here");
  }
}
