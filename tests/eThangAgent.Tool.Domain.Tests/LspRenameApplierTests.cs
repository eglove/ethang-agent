using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain.Lsp;

namespace eThangAgent.ToolDomain.Tests;

/// <summary>Contract tests for the all-or-nothing rename applier: every target is
/// validated before any write, edits apply in descending offset order, a mid-way
/// write failure restores what was already written, and a position past the end of
/// the text is a refusal rather than a clamp.</summary>
public class LspRenameApplierTests
{
  private const string Root = @"C:\ws";
  private const string FileA = @"C:\ws\A.cs";
  private const string FileB = @"C:\ws\B.cs";

  private sealed class FakeFs : IFileSystemAccess
  {
    public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<Result<FileRead>> ReadLinesAsync(string path, int startLine, int endLine, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<Result<byte[]>> ReadBytesAsync(string path, CancellationToken ct = default)
        => Task.FromResult(Files.TryGetValue(path, out string? text)
            ? Result.Success(System.Text.Encoding.UTF8.GetBytes(text))
            : Result.Failure<byte[]>(new DomainError("FileNotFound", path)));
  }

  private sealed class FakeWrite : IFileWriteAccess
  {
    public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Written { get; } = [];
    public string? FailOn { get; set; }

    public Task<Result<FileWriteOutcome>> WriteFileAsync(string path, string content, bool overwrite, CancellationToken ct = default)
    {
      if (FailOn is not null && path.EndsWith(FailOn, StringComparison.OrdinalIgnoreCase))
      {
        return Task.FromResult(Result.Failure<FileWriteOutcome>(new DomainError("WriteFailed", path)));
      }

      Written.Add(path);
      Files[path] = content;
      return Task.FromResult(Result.Success(new FileWriteOutcome(false, content.Length, LineEnding.Lf)));
    }

    public Task<Result<FileWriteOutcome>> WriteFileLinesAsync(string path, IReadOnlyList<string> lines, bool overwrite, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<Result<FileWriteOutcome>> WriteFileBytesAsync(string path, byte[] bytes, bool overwrite, CancellationToken ct = default)
        => throw new NotSupportedException();
  }

  private static LspRenameApplier Applier(FakeFs fs, FakeWrite writes) => new(fs, writes, Root);

  [Fact]
  public async Task Apply_SingleEdit_ReplacesExactlyThatSpan()
  {
    FakeFs fs = new();
    FakeWrite w = new();
    fs.Files[FileA] = "var foo = 1;";
    Result<LspRenameApplied> r = await Applier(fs, w)
        .ApplyAsync([new LspRenameEdit(FileA, 0, 4, 0, 7, "bar")], TestContext.Current.CancellationToken)
        .ConfigureAwait(true);
    Assert.True(r.IsSuccess);
    Assert.Equal("var bar = 1;", w.Files[FileA]);
    Assert.Equal(1, r.Value.EditCount);
    Assert.Equal([FileA], r.Value.FilesChanged);
  }

  [Fact]
  public async Task Apply_TwoEditsOnOneLine_AppliesBothCorrectly()
  {
    FakeFs fs = new();
    FakeWrite w = new();
    fs.Files[FileA] = "foo + foo";
    Result<LspRenameApplied> r = await Applier(fs, w).ApplyAsync(
        [new LspRenameEdit(FileA, 0, 0, 0, 3, "bar"), new LspRenameEdit(FileA, 0, 6, 0, 9, "bar")],
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(r.IsSuccess);
    Assert.Equal("bar + bar", w.Files[FileA]);
  }

  [Fact]
  public async Task Apply_NewNameLongerThanOld_ReplacesTheWholeSpan()
  {
    FakeFs fs = new();
    FakeWrite w = new();
    fs.Files[FileA] = "var foo = 1;";
    Result<LspRenameApplied> r = await Applier(fs, w)
        .ApplyAsync([new LspRenameEdit(FileA, 0, 4, 0, 7, "foobar")], TestContext.Current.CancellationToken)
        .ConfigureAwait(true);
    Assert.True(r.IsSuccess);
    Assert.Equal("var foobar = 1;", w.Files[FileA]);
  }

  [Fact]
  public async Task Apply_PathOutsideWorkspace_RefusesAndWritesNothing()
  {
    FakeFs fs = new();
    FakeWrite w = new();
    fs.Files[@"C:\other\A.cs"] = "foo";
    Result<LspRenameApplied> r = await Applier(fs, w)
        .ApplyAsync([new LspRenameEdit(@"C:\other\A.cs", 0, 0, 0, 3, "bar")], TestContext.Current.CancellationToken)
        .ConfigureAwait(true);
    Assert.False(r.IsSuccess);
    Assert.Equal("PathOutsideWorkspace", r.Error.Code);
    Assert.Empty(w.Written);
  }

  [Fact]
  public async Task Apply_PathUnderObj_RefusesAndWritesNothing()
  {
    FakeFs fs = new();
    FakeWrite w = new();
    fs.Files[@"C:\ws\obj\A.cs"] = "foo";
    Result<LspRenameApplied> r = await Applier(fs, w)
        .ApplyAsync([new LspRenameEdit(@"C:\ws\obj\A.cs", 0, 0, 0, 3, "bar")], TestContext.Current.CancellationToken)
        .ConfigureAwait(true);
    Assert.False(r.IsSuccess);
    Assert.Equal("PathExcluded", r.Error.Code);
    Assert.Empty(w.Written);
  }

  [Fact]
  public async Task Apply_MissingFile_RefusesAndWritesNothing()
  {
    FakeFs fs = new();
    FakeWrite w = new();
    fs.Files[FileA] = "foo";
    Result<LspRenameApplied> r = await Applier(fs, w).ApplyAsync(
        [new LspRenameEdit(FileA, 0, 0, 0, 3, "bar"), new LspRenameEdit(FileB, 0, 0, 0, 3, "bar")],
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.False(r.IsSuccess);
    Assert.Empty(w.Written);
  }

  [Fact]
  public async Task Apply_SecondFileWriteFails_RestoresTheFirst()
  {
    FakeFs fs = new();
    FakeWrite w = new();
    fs.Files[FileA] = "foo";
    fs.Files[FileB] = "foo";
    w.FailOn = "B.cs";
    Result<LspRenameApplied> r = await Applier(fs, w).ApplyAsync(
        [new LspRenameEdit(FileA, 0, 0, 0, 3, "bar"), new LspRenameEdit(FileB, 0, 0, 0, 3, "bar")],
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.False(r.IsSuccess);
    Assert.Equal("foo", w.Files[FileA]);
  }

  [Fact]
  public async Task Apply_OutOfRangePosition_RefusesRatherThanGuessing()
  {
    FakeFs fs = new();
    FakeWrite w = new();
    fs.Files[FileA] = "foo";
    Result<LspRenameApplied> r = await Applier(fs, w)
        .ApplyAsync([new LspRenameEdit(FileA, 9, 0, 9, 3, "bar")], TestContext.Current.CancellationToken)
        .ConfigureAwait(true);
    Assert.False(r.IsSuccess);
    Assert.Empty(w.Written);
  }

  [Fact]
  public async Task Apply_CharacterPastEndOfLine_RefusesRatherThanClamping()
  {
    FakeFs fs = new();
    FakeWrite w = new();
    fs.Files[FileA] = "foo";
    Result<LspRenameApplied> r = await Applier(fs, w)
        .ApplyAsync([new LspRenameEdit(FileA, 0, 0, 0, 99, "bar")], TestContext.Current.CancellationToken)
        .ConfigureAwait(true);
    Assert.False(r.IsSuccess);
    Assert.Empty(w.Written);
  }

  [Fact]
  public async Task Apply_FileWithBom_LosesItAfterWrite()
  {
    FakeFs fs = new();
    FakeWrite w = new();
    fs.Files[FileA] = "\uFEFFvar foo = 1;";
    Result<LspRenameApplied> r = await Applier(fs, w)
        .ApplyAsync([new LspRenameEdit(FileA, 0, 4, 0, 7, "bar")], TestContext.Current.CancellationToken)
        .ConfigureAwait(true);
    Assert.True(r.IsSuccess);
    Assert.Equal("var bar = 1;", w.Files[FileA]);
  }

  [Fact]
  public async Task Apply_NoEdits_Refuses()
  {
    FakeFs fs = new();
    FakeWrite w = new();
    Result<LspRenameApplied> r = await Applier(fs, w)
        .ApplyAsync([], TestContext.Current.CancellationToken)
        .ConfigureAwait(true);
    Assert.False(r.IsSuccess);
    Assert.Empty(w.Written);
  }
}
