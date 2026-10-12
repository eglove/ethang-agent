using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

/// <summary>Dispatch tests for the pdf tool (task 3): per-action rendering contracts,
///     path resolution and worktree anchoring, ledger recording, vision gating on
///     render, and verbatim surfacing of seam errors - all against a fake IPdfAccess
///     (the domain never sees a PDF library).</summary>
public sealed class PdfToolTests : IDisposable
{
#pragma warning disable JSON002

  private static readonly FileVersion V1 = new(100, new DateTime(638000000000000000, DateTimeKind.Utc));
  private static readonly FileVersion V2 = new(120, new DateTime(638000000000000100, DateTimeKind.Utc));
  private static readonly string Token1 = FileVersionToken.Encode(V1);
  private static readonly string Token2 = FileVersionToken.Encode(V2);

  private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47];

  /// <summary>The verbatim description contract (BuildDescription) - one entry per
  ///     line, exactly as the brief pins it.</summary>
  private static readonly string[] DescriptionLines =
  [
    "pdf - read, render, create, and manipulate PDF documents.",
    "",
    "Actions (parameter 'action', required):",
    "  read        Paginated per-page text. Params: path, offset (page offset, default 0), maxPages (default 20, max 100).",
    "  render      Pages as PNG images (attached when the model accepts images). Params: path, pages ('1-3,5,8-' 1-based, open tail allowed), dpi (default 150, max 300). Max 10 pages per call.",
    "  create      New PDF from the pinned markdown subset. Params: path, markdown.",
    "  manipulate  Atomic batch of structural operations. Params: path, expectedVersion, operations[].",
    "",
    "read output (verbatim contract):",
    "[pdf <path> v<version> pages <first>-<last> of <total>]",
    "--- page 1 (612x792pt) ---",
    "<text lines>",
    "version v<length>:<ticks>",
    "The 'version v<...>' line is the token EVERY manipulate call must pass back as expectedVersion.",
    "",
    "manipulate operations (each object needs 'op'):",
    "  rotate {pages, degrees}          - degrees exactly 90, 180, or 270",
    "  deletePages {pages}",
    "  reorderPages {order}             - explicit full permutation of the document's pages",
    "  extractPages {pages, outputPath} - writes a new PDF; the output must not already exist",
    "  merge {sources[]}                - appends other PDFs into the target",
    "  watermarkText {text, fontSize?, opacity?, degrees?} - diagonal stamp; opacity 0.0-1.0",
    "  setMetadata {title?, author?, subject?}",
    "The batch is atomic: one invalid operation refuses the whole batch, nothing is saved.",
    "Output: [pdf manipulated <path>] operations=<n> pages=<n> version v<newtoken>.",
    "",
    "create markdown subset: '# '/## '/### ' headings; '- ' bullets; '1. '/'2. ' numbered; **bold**; *italic*; '| a | b |' consecutive lines = table ('|---|' separator rows ignored); blank line = paragraph break; anything else = literal paragraph.",
    "Output: [pdf created <path>] pages=<n> version v<token>.",
    "",
    "render output: one PNG attachment per requested page plus the line [pdf render <path>] pages=<n> dpi=<dpi>; without image input the pages are listed only.",
    "",
    "Errors: Error [NotAPdf|EncryptedPdf|CorruptPdf|OversizePdf|PageNotFound|InvalidPageRange|OutputExists|StaleDocument|MarkdownInvalid|FileNotFound|InvalidParameterValue|MissingParameter|UnknownParameter|PathOutsideWorkspace].",
  ];

  private readonly string _root;
  private readonly FakePdfAccess _fake = new();

  public PdfToolTests()
  {
    _root = Path.Combine(Path.GetTempPath(), "pdf-tool-tests-" + Guid.NewGuid().ToString("N"));
    _ = Directory.CreateDirectory(_root);
  }

  public void Dispose()
  {
    try
    {
      Directory.Delete(_root, true);
    }
    catch (IOException)
    {
      // best-effort temp cleanup
    }
    catch (UnauthorizedAccessException)
    {
      // best-effort temp cleanup
    }

    GC.SuppressFinalize(this);
  }

  private PdfTool Tool(ReadFreshnessLedger? ledger = null, IImageInputCapability? vision = null)
      => new(new WorkspacePathResolver(_root), _fake, vision, ledger);

  private static Task<ToolResult> Dispatch(PdfTool tool, string json)
      => tool.ExecuteAsync(new RawToolInput("pdf", json));

  private string Resolved(string relative)
      => new WorkspacePathResolver(_root).Resolve(relative).Value!;

  private static PdfSnapshot SnapshotAt(int offset, int max)
  {
    List<PdfPageText> pages = [];
    for (int i = 0; i < max && offset + i < 3; i++)
    {
      int pageNumber = offset + i + 1;
      pages.Add(new PdfPageText(pageNumber, 612, 792, [$"text of page {pageNumber}"]));
    }

    return new PdfSnapshot(V1, 3, offset, pages, "Title", "Author");
  }

  private const string ReadJson = "{\"action\":\"read\",\"path\":\"doc.pdf\",\"timeoutSeconds\":30}";

  // ---- definition ----

  [Fact]
  public void Definition_RequiredParametersAreTimeoutActionPath()
      => Assert.Equal(["timeoutSeconds", "action", "path"], Tool().Definition.RequiredParameters);

  [Fact]
  public void Definition_NameIsPdf() => Assert.Equal("pdf", Tool().Definition.Name);

  [Fact]
  public void Definition_DescriptionMatchesVerbatimContract()
  {
    string expected = string.Join(Environment.NewLine, DescriptionLines) + Environment.NewLine;
    Assert.Equal(expected, Tool().Definition.Description);
  }

  // ---- read ----

  [Fact]
  public async Task Read_RendersContractLines()
  {
    _fake._onRead = (_, off, max) => Result.Success(SnapshotAt(off, max));
    ToolResult r = await Dispatch(Tool(), ReadJson);
    Assert.False(r.IsError, r.Content);
    Assert.Contains($"[pdf {Resolved("doc.pdf")} v{V1.Token} pages 1-3 of 3]", r.Content, StringComparison.Ordinal);
    Assert.Contains("--- page 1 (612x792pt) ---", r.Content, StringComparison.Ordinal);
    Assert.Contains("text of page 1", r.Content, StringComparison.Ordinal);
    Assert.Contains("--- page 3 (612x792pt) ---", r.Content, StringComparison.Ordinal);
    Assert.Contains("version " + Token1, r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Read_RecordsLedgerSpan()
  {
    _fake._onRead = (_, off, max) => Result.Success(SnapshotAt(off, max));
    ReadFreshnessLedger ledger = new();
    _ = await Dispatch(Tool(ledger: ledger), ReadJson);
    Assert.False(ledger.Check(Resolved("doc.pdf"), V1, 1, 3).IsEmpty);
  }

  [Fact]
  public async Task Read_PaginatesAnnotation()
  {
    _fake._onRead = (_, off, max) => Result.Success(SnapshotAt(off, max));
    ToolResult r = await Dispatch(Tool(), "{\"action\":\"read\",\"path\":\"doc.pdf\",\"timeoutSeconds\":30,\"offset\":1,\"maxPages\":1}");
    Assert.False(r.IsError, r.Content);
    Assert.Contains("pages 2-2 of 3", r.Content, StringComparison.Ordinal);
    Assert.Contains("text of page 2", r.Content, StringComparison.Ordinal);
    Assert.DoesNotContain("--- page 1 ", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Read_SurfacesSeamErrorVerbatim()
  {
    _fake._onRead = (_, _, _) => Result.Failure<PdfSnapshot>(
        new DomainError(PdfErrorCodes.NotAPdf, "magic bytes are not a PDF header"));
    ToolResult r = await Dispatch(Tool(), ReadJson);
    Assert.True(r.IsError);
    Assert.Contains("Error [NotAPdf]: magic bytes are not a PDF header", r.Content, StringComparison.Ordinal);
  }

  // ---- render ----

  [Fact]
  public async Task Render_AttachesImagesWhenVisionAccepts()
  {
    _fake._onRender = (_, _, _) => Result.Success(new PdfRenderOutcome(
        [new PdfPageImage(1, PngBytes, 200, 300), new PdfPageImage(3, PngBytes, 200, 300)],
        [1, 3]));
    ToolResult r = await Dispatch(Tool(vision: new FakeVision(true)),
        "{\"action\":\"render\",\"path\":\"doc.pdf\",\"timeoutSeconds\":30,\"pages\":\"1,3\"}");
    Assert.False(r.IsError, r.Content);
    Assert.Contains($"[pdf render {Resolved("doc.pdf")}] pages=2 dpi=150", r.Content, StringComparison.Ordinal);
    Assert.NotNull(r.Images);
    Assert.Equal(2, r.Images.Count);
    Assert.Equal("image/png", r.Images[0].MediaType);
  }

  [Fact]
  public async Task Render_ListsPagesWhenVisionNull()
  {
    _fake._onRender = (_, _, _) => Result.Success(new PdfRenderOutcome(
        [new PdfPageImage(1, PngBytes, 200, 300), new PdfPageImage(3, PngBytes, 200, 300)],
        [1, 3]));
    ToolResult r = await Dispatch(Tool(),
        "{\"action\":\"render\",\"path\":\"doc.pdf\",\"timeoutSeconds\":30,\"pages\":\"1,3\"}");
    Assert.False(r.IsError, r.Content);
    Assert.Contains($"[pdf render {Resolved("doc.pdf")}] pages=2 dpi=150", r.Content, StringComparison.Ordinal);
    Assert.Contains("p1 listed-only (ImageNotAttachable: model has no image input)", r.Content, StringComparison.Ordinal);
    Assert.Contains("p3 listed-only (ImageNotAttachable: model has no image input)", r.Content, StringComparison.Ordinal);
    Assert.Null(r.Images);
  }

  [Fact]
  public async Task Render_ListsPagesWhenVisionDeclines()
  {
    _fake._onRender = (_, _, _) => Result.Success(new PdfRenderOutcome(
        [new PdfPageImage(2, PngBytes, 200, 300)], [2]));
    ToolResult r = await Dispatch(Tool(vision: new FakeVision(false)),
        "{\"action\":\"render\",\"path\":\"doc.pdf\",\"timeoutSeconds\":30,\"pages\":\"2\"}");
    Assert.False(r.IsError, r.Content);
    Assert.Contains("p2 listed-only (ImageNotAttachable: model has no image input)", r.Content, StringComparison.Ordinal);
    Assert.Null(r.Images);
  }

  [Fact]
  public async Task Render_PassesPagesAndDpiToSeam()
  {
    IReadOnlyList<int>? seenPages = null;
    int seenDpi = 0;
    _fake._onRender = (_, pages, dpi) =>
    {
      seenPages = pages;
      seenDpi = dpi;
      return Result.Success(new PdfRenderOutcome([], pages));
    };
    _ = await Dispatch(Tool(), "{\"action\":\"render\",\"path\":\"doc.pdf\",\"timeoutSeconds\":30,\"pages\":\"1-2,5\",\"dpi\":200}");
    Assert.Equal([1, 2, 5], seenPages);
    Assert.Equal(200, seenDpi);
  }

  [Fact]
  public async Task Render_SurfacesSeamErrorVerbatim()
  {
    _fake._onRender = (_, _, _) => Result.Failure<PdfRenderOutcome>(
        new DomainError(PdfErrorCodes.PageNotFound, "page 9 does not exist"));
    ToolResult r = await Dispatch(Tool(), "{\"action\":\"render\",\"path\":\"doc.pdf\",\"timeoutSeconds\":30,\"pages\":\"9\"}");
    Assert.True(r.IsError);
    Assert.Contains("Error [PageNotFound]: page 9 does not exist", r.Content, StringComparison.Ordinal);
  }

  // ---- create ----

  [Fact]
  public async Task Create_RendersCreatedLineAndPassesMarkdown()
  {
    string? seenMarkdown = null;
    _fake._onCreate = (_, markdown) =>
    {
      seenMarkdown = markdown;
      return Result.Success(new PdfSaveOutcome(V2, 4));
    };
    ToolResult r = await Dispatch(Tool(), "{\"action\":\"create\",\"path\":\"new.pdf\",\"timeoutSeconds\":30,\"markdown\":\"# hi\"}");
    Assert.False(r.IsError, r.Content);
    Assert.Contains($"[pdf created {Resolved("new.pdf")}] pages=4 version {Token2}", r.Content, StringComparison.Ordinal);
    Assert.Equal("# hi", seenMarkdown);
  }

  [Fact]
  public async Task Create_SurfacesOutputExists()
  {
    _fake._onCreate = (_, _) => Result.Failure<PdfSaveOutcome>(
        new DomainError(PdfErrorCodes.OutputExists, "the target already exists"));
    ToolResult r = await Dispatch(Tool(), "{\"action\":\"create\",\"path\":\"new.pdf\",\"timeoutSeconds\":30,\"markdown\":\"# hi\"}");
    Assert.True(r.IsError);
    Assert.Contains("Error [OutputExists]", r.Content, StringComparison.Ordinal);
  }

  // ---- manipulate ----

  [Fact]
  public async Task Manipulate_PassesExpectedVersionAndOperationsToSeam()
  {
    FileVersion? seenVersion = null;
    IReadOnlyList<PdfOperation>? seenOps = null;
    _fake._onManipulate = (_, version, ops) =>
    {
      seenVersion = version;
      seenOps = ops;
      return Result.Success(new PdfEditOutcome(V2, 5, 1));
    };
    string json = "{\"action\":\"manipulate\",\"path\":\"doc.pdf\",\"timeoutSeconds\":30," +
                  "\"expectedVersion\":\"" + Token1 + "\"," +
                  "\"operations\":[{\"op\":\"rotate\",\"pages\":\"1-2\",\"degrees\":90}]}";
    _ = await Dispatch(Tool(), json);
    Assert.Equal(V1, seenVersion);
    Assert.NotNull(seenOps);
    RotatePages rotate = Assert.IsType<RotatePages>(Assert.Single(seenOps));
    Assert.Equal(90, rotate.Degrees);
    Assert.Equal([1, 2], rotate.Pages);
  }

  [Fact]
  public async Task Manipulate_RendersOutcome()
  {
    _fake._onManipulate = (_, _, _) => Result.Success(new PdfEditOutcome(V2, 5, 2));
    string json = "{\"action\":\"manipulate\",\"path\":\"doc.pdf\",\"timeoutSeconds\":30," +
                  "\"expectedVersion\":\"" + Token1 + "\"," +
                  "\"operations\":[{\"op\":\"deletePages\",\"pages\":\"2\"}]}";
    ToolResult r = await Dispatch(Tool(), json);
    Assert.False(r.IsError, r.Content);
    Assert.Contains($"[pdf manipulated {Resolved("doc.pdf")}] operations=2 pages=5 version {Token2}", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Manipulate_StaleErrorSurfacesVerbatim()
  {
    _fake._onManipulate = (_, _, _) => Result.Failure<PdfEditOutcome>(
        new DomainError(PdfErrorCodes.StaleDocument, "the file changed since your read"));
    string json = "{\"action\":\"manipulate\",\"path\":\"doc.pdf\",\"timeoutSeconds\":30," +
                  "\"expectedVersion\":\"" + Token1 + "\"," +
                  "\"operations\":[{\"op\":\"deletePages\",\"pages\":\"2\"}]}";
    ToolResult r = await Dispatch(Tool(), json);
    Assert.True(r.IsError);
    Assert.Contains("Error [StaleDocument]", r.Content, StringComparison.Ordinal);
  }

  // ---- input-layer passthrough (seam untouched) ----

  [Fact]
  public async Task UnknownKey_Refused()
  {
    _fake._onRead = (_, _, _) => throw new InvalidOperationException("seam must not be called");
    ToolResult r = await Dispatch(Tool(), "{\"action\":\"read\",\"path\":\"doc.pdf\",\"timeoutSeconds\":30,\"bogus\":1}");
    Assert.True(r.IsError);
    Assert.Contains("Error [UnknownParameter]", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task MissingPath_Refused()
  {
    ToolResult r = await Dispatch(Tool(), "{\"action\":\"read\",\"timeoutSeconds\":30}");
    Assert.True(r.IsError);
    Assert.Contains("Error [MissingParameter]", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task BadPageRange_RefusedWithoutTouchingSeam()
  {
    _fake._onRender = (_, _, _) => throw new InvalidOperationException("seam must not be called");
    ToolResult r = await Dispatch(Tool(), "{\"action\":\"render\",\"path\":\"doc.pdf\",\"timeoutSeconds\":30,\"pages\":\"1-3,2-4\"}");
    Assert.True(r.IsError);
    Assert.Contains("Error [InvalidPageRange]", r.Content, StringComparison.Ordinal);
  }

  // ---- anchoring ----

  [Fact]
  public async Task RootedAt_ReplacesResolverOnly()
  {
    PdfTool original = Tool();
    ITool anchored = original.RootedAt(Path.Combine(_root, "anchor"));
    Assert.Equal("pdf", anchored.Definition.Name);
    ToolResult r = await Dispatch((PdfTool)anchored, "{\"action\":\"read\",\"path\":\"../escape.pdf\",\"timeoutSeconds\":30}");
    Assert.True(r.IsError);
    Assert.Contains("Error [PathOutsideWorkspace]", r.Content, StringComparison.Ordinal);
  }

  // ---- fakes ----

  private sealed class FakeVision(bool accepts) : IImageInputCapability
  {
    public bool AcceptsImages => accepts;
  }

  private sealed class FakePdfAccess : IPdfAccess
  {
    public Func<string, int, int, Result<PdfSnapshot>>? _onRead;
    public Func<string, IReadOnlyList<int>, int, Result<PdfRenderOutcome>>? _onRender;
    public Func<string, string, Result<PdfSaveOutcome>>? _onCreate;
    public Func<string, FileVersion, IReadOnlyList<PdfOperation>, Result<PdfEditOutcome>>? _onManipulate;

    public Task<Result<PdfSnapshot>> ReadAsync(string path, int offset, int maxPages, CancellationToken ct = default)
        => Task.FromResult(_onRead?.Invoke(path, offset, maxPages) ?? throw NotWired(nameof(_onRead)));

    public Task<Result<PdfRenderOutcome>> RenderAsync(string path, IReadOnlyList<int> pages, int dpi, CancellationToken ct = default)
        => Task.FromResult(_onRender?.Invoke(path, pages, dpi) ?? throw NotWired(nameof(_onRender)));

    public Task<Result<PdfSaveOutcome>> CreateAsync(string path, string markdown, CancellationToken ct = default)
        => Task.FromResult(_onCreate?.Invoke(path, markdown) ?? throw NotWired(nameof(_onCreate)));

    public Task<Result<PdfEditOutcome>> ManipulateAsync(string path, FileVersion expectedVersion, IReadOnlyList<PdfOperation> operations, CancellationToken ct = default)
        => Task.FromResult(_onManipulate?.Invoke(path, expectedVersion, operations) ?? throw NotWired(nameof(_onManipulate)));

    private static InvalidOperationException NotWired(string name)
        => new($"FakePdfAccess.{name} was called without a wired delegate.");
  }
}
