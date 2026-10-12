using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

/// <summary>The ILedgerBoundTool contract: WithLedger binds the agent's ledger on a
///     ledger-less tool (the loop's binding path in Agent.cs) and is idempotent on a
///     tool that already carries one. Regression tests for the param-vs-field bug:
///     WithLedger checked the PARAMETER for null instead of the bound field, so a
///     non-null argument returned `this` — the ledger was never attached.</summary>
public class LedgerBindingTests
{
  private sealed class FakeFileSystemAccess : IFileSystemAccess
  {
    public Task<Result<FileRead>> ReadLinesAsync(string path, int startLine, int endLine, CancellationToken ct = default) => Task.FromResult(Result.Failure<FileRead>(new DomainError("FileNotFound", "x")));
    public Task<Result<byte[]>> ReadBytesAsync(string path, CancellationToken ct = default) => Task.FromResult(Result.Failure<byte[]>(new DomainError("FileNotFound", "x")));
  }

  private sealed class FakeDocxAccess : IDocxAccess
  {
    public Task<Result<DocxSnapshot>> ReadAsync(string path, int offset, int maxBlocks, CancellationToken ct = default) => Task.FromResult(Result.Failure<DocxSnapshot>(new DomainError(DocxErrorCodes.FileNotFound, "x")));
    public Task<Result<DocxEditOutcome>> EditAsync(string path, FileVersion expectedVersion, IReadOnlyList<DocxOperation> operations, CancellationToken ct = default) => Task.FromResult(Result.Failure<DocxEditOutcome>(new DomainError(DocxErrorCodes.FileNotFound, "x")));
    public Task<Result<DocxSaveOutcome>> CreateAsync(string path, string markdown, CancellationToken ct = default) => Task.FromResult(Result.Failure<DocxSaveOutcome>(new DomainError(DocxErrorCodes.FileNotFound, "x")));
    public Task<Result<IReadOnlyList<DocxCommentInfo>>> ListCommentsAsync(string path, CancellationToken ct = default) => Task.FromResult(Result.Failure<IReadOnlyList<DocxCommentInfo>>(new DomainError(DocxErrorCodes.FileNotFound, "x")));
    public Task<Result<DocxSaveOutcome>> AddCommentAsync(string path, FileVersion expectedVersion, string address, string author, string text, CancellationToken ct = default) => Task.FromResult(Result.Failure<DocxSaveOutcome>(new DomainError(DocxErrorCodes.FileNotFound, "x")));
    public Task<Result<DocxSaveOutcome>> DeleteCommentAsync(string path, FileVersion expectedVersion, int commentId, CancellationToken ct = default) => Task.FromResult(Result.Failure<DocxSaveOutcome>(new DomainError(DocxErrorCodes.FileNotFound, "x")));
    public Task<Result<IReadOnlyList<DocxRevisionInfo>>> ListRevisionsAsync(string path, CancellationToken ct = default) => Task.FromResult(Result.Failure<IReadOnlyList<DocxRevisionInfo>>(new DomainError(DocxErrorCodes.FileNotFound, "x")));
    public Task<Result<DocxSaveOutcome>> ResolveRevisionsAsync(string path, FileVersion expectedVersion, bool accept, DocxRevisionScope scope, CancellationToken ct = default) => Task.FromResult(Result.Failure<DocxSaveOutcome>(new DomainError(DocxErrorCodes.FileNotFound, "x")));
    public Task<Result<IReadOnlyList<ToolResultImage>>> ExtractImagesAsync(string path, IReadOnlyList<string>? names, CancellationToken ct = default) => Task.FromResult(Result.Failure<IReadOnlyList<ToolResultImage>>(new DomainError(DocxErrorCodes.FileNotFound, "x")));
  }

  private sealed class FakePdfAccess : IPdfAccess
  {
    public Task<Result<PdfSnapshot>> ReadAsync(string path, int offset, int maxPages, CancellationToken ct = default) => Task.FromResult(Result.Failure<PdfSnapshot>(new DomainError(PdfErrorCodes.FileNotFound, "x")));
    public Task<Result<PdfRenderOutcome>> RenderAsync(string path, IReadOnlyList<int> pages, int dpi, CancellationToken ct = default) => Task.FromResult(Result.Failure<PdfRenderOutcome>(new DomainError(PdfErrorCodes.FileNotFound, "x")));
    public Task<Result<PdfSaveOutcome>> CreateAsync(string path, string markdown, CancellationToken ct = default) => Task.FromResult(Result.Failure<PdfSaveOutcome>(new DomainError(PdfErrorCodes.FileNotFound, "x")));
    public Task<Result<PdfEditOutcome>> ManipulateAsync(string path, FileVersion expectedVersion, IReadOnlyList<PdfOperation> operations, CancellationToken ct = default) => Task.FromResult(Result.Failure<PdfEditOutcome>(new DomainError(PdfErrorCodes.FileNotFound, "x")));
  }

  [Fact]
  public void ReadTool_WithLedger_Binds_The_Ledger()
  {
    ReadTool tool = new(new UnrootedPathResolver(), new FakeFileSystemAccess());
    Assert.False(tool.HasLedger);
    ITool bound = tool.WithLedger(new ReadFreshnessLedger());
    ReadTool read = Assert.IsType<ReadTool>(bound);
    Assert.True(read.HasLedger);
  }

  [Fact]
  public void DocxTool_WithLedger_Binds_The_Ledger()
  {
    DocxTool tool = new(new UnrootedPathResolver(), new FakeDocxAccess(), null);
    Assert.False(tool.HasLedger);
    ITool bound = tool.WithLedger(new ReadFreshnessLedger());
    DocxTool docx = Assert.IsType<DocxTool>(bound);
    Assert.True(docx.HasLedger);
  }

  [Fact]
  public void PdfTool_WithLedger_Binds_The_Ledger()
  {
    PdfTool tool = new(new UnrootedPathResolver(), new FakePdfAccess(), null);
    Assert.False(tool.HasLedger);
    ITool bound = tool.WithLedger(new ReadFreshnessLedger());
    PdfTool pdf = Assert.IsType<PdfTool>(bound);
    Assert.True(pdf.HasLedger);
  }

  [Fact]
  public void WithLedger_Is_Idempotent_When_Already_Bound()
  {
    ReadFreshnessLedger ledger = new();
    ReadTool tool = new(new UnrootedPathResolver(), new FakeFileSystemAccess(), ledger);
    Assert.True(tool.HasLedger);
    Assert.Same(tool, tool.WithLedger(new ReadFreshnessLedger()));
  }

  [Fact]
  public void RootedAt_Carries_The_Ledger()
  {
    ReadFreshnessLedger ledger = new();
    DocxTool tool = new(new UnrootedPathResolver(), new FakeDocxAccess(), null, ledger);
    Assert.True(tool.HasLedger);
    ITool rooted = tool.RootedAt(@"C:\some\anchor");
    DocxTool docx = Assert.IsType<DocxTool>(rooted);
    Assert.True(docx.HasLedger);
  }
}
