using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

/// <summary>Dispatch tests for the docx tool (task 3): per-action rendering contracts,
///     path resolution and worktree anchoring, ledger recording, vision gating on
///     extract, and verbatim surfacing of seam errors - all against a fake
///     IDocxAccess (the domain never sees OOXML).</summary>
public sealed class DocxToolTests : IDisposable
{
#pragma warning disable JSON002

  private static readonly FileVersion V1 = new(100, new DateTime(638000000000000000, DateTimeKind.Utc));
  private static readonly FileVersion V2 = new(120, new DateTime(638000000000000100, DateTimeKind.Utc));
  private static readonly string Token1 = FileVersionToken.Encode(V1);
  private static readonly string Token2 = FileVersionToken.Encode(V2);

  private static readonly DocxBlock[] Body =
  [
      new DocxParagraphBlock(1, "Heading 1", "Title"),
        new DocxParagraphBlock(2, "Normal", "Body"),
        new DocxTableBlock(3, 1, 2, [new DocxCellText(1, 1, "a"), new DocxCellText(1, 2, "b")]),
    ];

  private readonly string _root;
  private readonly FakeDocxAccess _fake = new();

  public DocxToolTests()
  {
    _root = Path.Combine(Path.GetTempPath(), "docx-tool-tests-" + Guid.NewGuid().ToString("N"));
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

  private DocxTool Tool(ReadFreshnessLedger? ledger = null, IImageInputCapability? vision = null)
      => new(new WorkspacePathResolver(_root), _fake, vision, ledger);

  private static Task<ToolResult> Dispatch(DocxTool tool, string json)
      => tool.ExecuteAsync(new RawToolInput("docx", json));

  private string Resolved(string relative)
      => new WorkspacePathResolver(_root).Resolve(relative).Value!;

  private static DocxSnapshot SnapshotAt(int offset, int max, IReadOnlyList<DocxMediaInfo>? media = null)
  {
    List<DocxBlock> page = [.. Body.Skip(offset).Take(max)];
    return new DocxSnapshot(V1, Body.Length, offset, page, 1, 0, 0, media ?? [], []);
  }

  private static string ReadJson => "{\"action\":\"read\",\"path\":\"doc.docx\",\"timeoutSeconds\":30}";

  // ---- read ----

  [Fact]
  public async Task Read_RendersContractLines()
  {
    _fake._onRead = (_, off, max) => Result.Success(SnapshotAt(off, max));
    ToolResult r = await Dispatch(Tool(), ReadJson);
    Assert.False(r.IsError, r.Content);
    Assert.Contains($"[docx {Resolved("doc.docx")} v{V1.Token} blocks 1-3 of 3]", r.Content, StringComparison.Ordinal);
    Assert.Contains("p1 | Heading 1 | Title", r.Content, StringComparison.Ordinal);
    Assert.Contains("p2 | Normal | Body", r.Content, StringComparison.Ordinal);
    Assert.Contains("t3 | table 1x2", r.Content, StringComparison.Ordinal);
    Assert.Contains("    r1: a | b", r.Content, StringComparison.Ordinal);
    Assert.Contains("version " + Token1, r.Content, StringComparison.Ordinal);
    Assert.Contains("flags: tracked-changes=0 comments=0 sections=1 media=[]", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Read_RecordsLedgerSpan()
  {
    _fake._onRead = (_, off, max) => Result.Success(SnapshotAt(off, max));
    ReadFreshnessLedger ledger = new();
    _ = await Dispatch(Tool(ledger: ledger), ReadJson);
    Assert.False(ledger.Check(Resolved("doc.docx"), V1, 1, 3).IsEmpty);
  }

  [Fact]
  public async Task Read_PaginatesAnnotation()
  {
    _fake._onRead = (_, off, max) => Result.Success(SnapshotAt(off, max));
    ToolResult r = await Dispatch(Tool(), "{\"action\":\"read\",\"path\":\"doc.docx\",\"timeoutSeconds\":30,\"offset\":1,\"maxBlocks\":1}");
    Assert.False(r.IsError, r.Content);
    Assert.Contains("blocks 2-2 of 3", r.Content, StringComparison.Ordinal);
    Assert.Contains("p2 | Normal | Body", r.Content, StringComparison.Ordinal);
    Assert.DoesNotContain("p1 |", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Read_SurfacesSeamErrorVerbatim()
  {
    _fake._onRead = (_, _, _) => Result.Failure<DocxSnapshot>(
        new DomainError(DocxErrorCodes.NotADocx, "magic bytes are not a zip package"));
    ToolResult r = await Dispatch(Tool(), ReadJson);
    Assert.True(r.IsError);
    Assert.Contains("Error [NotADocx]: magic bytes are not a zip package", r.Content, StringComparison.Ordinal);
  }

  // ---- edit ----

  [Fact]
  public async Task Edit_PassesExpectedVersionToSeam()
  {
    FileVersion? captured = null;
    _fake._onEdit = (_, version, _) =>
    {
      captured = version;
      return Result.Success(new DocxEditOutcome(V2, 1));
    };
    string json = "{\"action\":\"edit\",\"path\":\"doc.docx\",\"timeoutSeconds\":30," +
                  "\"expectedVersion\":\"" + Token1 + "\"," +
                  "\"operations\":[{\"op\":\"findReplace\",\"find\":\"a\",\"replace\":\"b\"}]}";
    _ = await Dispatch(Tool(), json);
    Assert.Equal(V1, captured);
  }

  [Fact]
  public async Task Edit_RendersOutcome()
  {
    _fake._onEdit = (_, _, _) => Result.Success(new DocxEditOutcome(V2, 1));
    string json = "{\"action\":\"edit\",\"path\":\"doc.docx\",\"timeoutSeconds\":30," +
                  "\"expectedVersion\":\"" + Token1 + "\"," +
                  "\"operations\":[{\"op\":\"findReplace\",\"find\":\"a\",\"replace\":\"b\"}]}";
    ToolResult r = await Dispatch(Tool(), json);
    Assert.False(r.IsError, r.Content);
    Assert.Contains($"[docx edited {Resolved("doc.docx")}] changed-blocks=1 version {Token2}", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Edit_StaleErrorSurfacesVerbatim()
  {
    _fake._onEdit = (_, _, _) => Result.Failure<DocxEditOutcome>(
        new DomainError(DocxErrorCodes.StaleDocument, "the file changed since your read"));
    string json = "{\"action\":\"edit\",\"path\":\"doc.docx\",\"timeoutSeconds\":30," +
                  "\"expectedVersion\":\"" + Token1 + "\"," +
                  "\"operations\":[{\"op\":\"findReplace\",\"find\":\"a\",\"replace\":\"b\"}]}";
    ToolResult r = await Dispatch(Tool(), json);
    Assert.True(r.IsError);
    Assert.Contains("Error [StaleDocument]", r.Content, StringComparison.Ordinal);
  }

  // ---- create ----

  [Fact]
  public async Task Create_RefusesExistingFile()
  {
    await File.WriteAllTextAsync(Path.Combine(_root, "exists.docx"), "x", TestContext.Current.CancellationToken);
    _fake._onCreate = (_, _) => throw new InvalidOperationException("seam must not be called");
    ToolResult r = await Dispatch(Tool(), "{\"action\":\"create\",\"path\":\"exists.docx\",\"timeoutSeconds\":30,\"markdown\":\"# hi\"}");
    Assert.True(r.IsError);
    Assert.Contains("Error [InvalidParameterValue]", r.Content, StringComparison.Ordinal);
    Assert.Contains("already exists", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Create_RendersCreatedLine()
  {
    _fake._onCreate = (_, _) => Result.Success(new DocxSaveOutcome(V2));
    _fake._onRead = (_, off, max) => Result.Success(SnapshotAt(off, max));
    ToolResult r = await Dispatch(Tool(), "{\"action\":\"create\",\"path\":\"new.docx\",\"timeoutSeconds\":30,\"markdown\":\"# hi\"}");
    Assert.False(r.IsError, r.Content);
    Assert.Contains($"[docx created {Resolved("new.docx")}] blocks=3 version {Token2}", r.Content, StringComparison.Ordinal);
  }

  // ---- comments ----

  [Fact]
  public async Task Comments_List_Renders()
  {
    _fake._onListComments = _ => Result.Success<IReadOnlyList<DocxCommentInfo>>(
        [new DocxCommentInfo(1, "Ethan", "check this", "p1")]);
    ToolResult r = await Dispatch(Tool(), "{\"action\":\"comments\",\"path\":\"doc.docx\",\"timeoutSeconds\":30,\"commentAction\":\"list\"}");
    Assert.False(r.IsError, r.Content);
    Assert.Contains("c1 | Ethan | p1 | check this", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Comments_Add_RendersIdAndVersion()
  {
    _fake._onAddComment = (_, _, _, _, _) => Result.Success(new DocxSaveOutcome(V2));
    _fake._onListComments = _ => Result.Success<IReadOnlyList<DocxCommentInfo>>(
        [new DocxCommentInfo(1, "Ethan", "check this", "p1")]);
    ToolResult r = await Dispatch(Tool(),
        "{\"action\":\"comments\",\"path\":\"doc.docx\",\"timeoutSeconds\":30,\"commentAction\":\"add\"," +
        "\"address\":\"p1\",\"author\":\"Ethan\",\"text\":\"check this\",\"expectedVersion\":\"" + Token1 + "\"}");
    Assert.False(r.IsError, r.Content);
    Assert.Contains($"[docx comment added id=1] version {Token2}", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Comments_Delete_Renders()
  {
    _fake._onDeleteComment = (_, _, _) => Result.Success(new DocxSaveOutcome(V2));
    ToolResult r = await Dispatch(Tool(),
        "{\"action\":\"comments\",\"path\":\"doc.docx\",\"timeoutSeconds\":30,\"commentAction\":\"delete\"," +
        "\"commentId\":2,\"expectedVersion\":\"" + Token1 + "\"}");
    Assert.False(r.IsError, r.Content);
    Assert.Contains($"[docx comment deleted id=2] version {Token2}", r.Content, StringComparison.Ordinal);
  }

  // ---- revisions ----

  [Fact]
  public async Task Revisions_List_Renders()
  {
    _fake._onListRevisions = _ => Result.Success<IReadOnlyList<DocxRevisionInfo>>(
        [new DocxRevisionInfo(1, "Ethan", "Insertion", "p1")]);
    ToolResult r = await Dispatch(Tool(), "{\"action\":\"revisions\",\"path\":\"doc.docx\",\"timeoutSeconds\":30,\"revisionAction\":\"list\"}");
    Assert.False(r.IsError, r.Content);
    Assert.Contains("r1 | Insertion | Ethan | p1", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Revisions_List_IgnoresScope()
  {
    _fake._onListRevisions = _ => Result.Success<IReadOnlyList<DocxRevisionInfo>>(
        [new DocxRevisionInfo(1, "Ethan", "Insertion", "p1")]);
    ToolResult r = await Dispatch(Tool(),
        "{\"action\":\"revisions\",\"path\":\"doc.docx\",\"timeoutSeconds\":30,\"revisionAction\":\"list\"," +
        "\"scope\":{\"author\":\"Ethan\"}}");
    Assert.False(r.IsError, r.Content);
    Assert.Contains("r1 | Insertion | Ethan | p1", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Revisions_Accept_RendersCountAndVersion()
  {
    _fake._onListRevisions = _ => Result.Success<IReadOnlyList<DocxRevisionInfo>>(
    [
        new DocxRevisionInfo(1, "Ethan", "Insertion", "p1"),
            new DocxRevisionInfo(2, "Ethan", "Deletion", "p2"),
        ]);
    DocxRevisionScope? scopeSeen = null;
    _fake._onResolveRevisions = (_, _, accept, scope) =>
    {
      scopeSeen = scope;
      return Result.Success(new DocxSaveOutcome(V2));
    };
    ToolResult r = await Dispatch(Tool(),
        "{\"action\":\"revisions\",\"path\":\"doc.docx\",\"timeoutSeconds\":30,\"revisionAction\":\"accept\"," +
        "\"expectedVersion\":\"" + Token1 + "\"}");
    Assert.False(r.IsError, r.Content);
    Assert.Contains($"[docx revisions accept count=2] version {Token2}", r.Content, StringComparison.Ordinal);
    Assert.NotNull(scopeSeen);
    Assert.True(scopeSeen.All);
  }

  [Fact]
  public async Task Revisions_Reject_ByAuthor_CountsOnlyMatching()
  {
    _fake._onListRevisions = _ => Result.Success<IReadOnlyList<DocxRevisionInfo>>(
    [
        new DocxRevisionInfo(1, "A", "Insertion", "p1"),
            new DocxRevisionInfo(2, "B", "Deletion", "p2"),
        ]);
    DocxRevisionScope? scopeSeen = null;
    _fake._onResolveRevisions = (_, _, _, scope) =>
    {
      scopeSeen = scope;
      return Result.Success(new DocxSaveOutcome(V2));
    };
    ToolResult r = await Dispatch(Tool(),
        "{\"action\":\"revisions\",\"path\":\"doc.docx\",\"timeoutSeconds\":30,\"revisionAction\":\"reject\"," +
        "\"expectedVersion\":\"" + Token1 + "\",\"scope\":{\"author\":\"A\"}}");
    Assert.False(r.IsError, r.Content);
    Assert.Contains($"[docx revisions reject count=1] version {Token2}", r.Content, StringComparison.Ordinal);
    Assert.Equal("A", scopeSeen!.Author);
  }

  // ---- extract ----

  [Fact]
  public async Task Extract_AttachesOnlyWhenVisionAccepts()
  {
    DocxMediaInfo[] media = [new DocxMediaInfo("img1.png", "image/png", 10)];
    _fake._onRead = (_, _, _) => Result.Success(SnapshotAt(0, 1, media));
    _fake._onExtractImages = (_, _) => Result.Success<IReadOnlyList<ToolResultImage>>(
        [new ToolResultImage("image/png", Convert.ToBase64String((byte[])[1, 2, 3]))]);
    ToolResult without = await Dispatch(Tool(), "{\"action\":\"extract\",\"path\":\"doc.docx\",\"timeoutSeconds\":30}");
    Assert.False(without.IsError, without.Content);
    Assert.Contains("img1.png image/png 10 listed-only (ImageNotAttachable: model has no image input)", without.Content, StringComparison.Ordinal);
    Assert.Null(without.Images);

    ToolResult with = await Dispatch(Tool(vision: new FakeVision(true)), "{\"action\":\"extract\",\"path\":\"doc.docx\",\"timeoutSeconds\":30}");
    Assert.False(with.IsError, with.Content);
    Assert.Contains("img1.png image/png 10 attached", with.Content, StringComparison.Ordinal);
    Assert.NotNull(with.Images);
    _ = Assert.Single(with.Images);
  }

  [Fact]
  public async Task Extract_ListsNonAttachableMedia()
  {
    DocxMediaInfo[] media =
    [
      new DocxMediaInfo("img1.png", "image/png", 10),
      new DocxMediaInfo("pic.bmp", "image/bmp", 20),
    ];
    _fake._onRead = (_, _, _) => Result.Success(SnapshotAt(0, 1, media));
    _fake._onExtractImages = (_, _) => Result.Success<IReadOnlyList<ToolResultImage>>(
        [new ToolResultImage("image/png", Convert.ToBase64String((byte[])[1, 2, 3]))]);
    ToolResult r = await Dispatch(Tool(vision: new FakeVision(true)), "{\"action\":\"extract\",\"path\":\"doc.docx\",\"timeoutSeconds\":30}");
    Assert.False(r.IsError, r.Content);
    Assert.Contains("img1.png image/png 10 attached", r.Content, StringComparison.Ordinal);
    Assert.Contains("pic.bmp image/bmp 20 listed-only (ImageNotAttachable: PNG/JPEG only)", r.Content, StringComparison.Ordinal);
    _ = Assert.Single(r.Images!);
  }

  // ---- input-layer passthrough ----

  [Fact]
  public async Task UnknownKey_Refused()
  {
    ToolResult r = await Dispatch(Tool(), "{\"action\":\"read\",\"path\":\"doc.docx\",\"timeoutSeconds\":30,\"bogus\":1}");
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

  // ---- anchoring ----

  [Fact]
  public async Task RootedAt_ReplacesResolverOnly()
  {
    DocxTool original = Tool();
    ITool anchored = original.RootedAt(Path.Combine(_root, "anchor"));
    Assert.Equal("docx", anchored.Definition.Name);
    ToolResult r = await Dispatch((DocxTool)anchored, "{\"action\":\"read\",\"path\":\"../escape.docx\",\"timeoutSeconds\":30}");
    Assert.True(r.IsError);
    Assert.Contains("Error [PathOutsideWorkspace]", r.Content, StringComparison.Ordinal);
  }

  // ---- fakes ----

  private sealed class FakeVision(bool accepts) : IImageInputCapability
  {
    public bool AcceptsImages => accepts;
  }

  private sealed class FakeDocxAccess : IDocxAccess
  {
    public Func<string, int, int, Result<DocxSnapshot>>? _onRead;
    public Func<string, FileVersion, IReadOnlyList<DocxOperation>, Result<DocxEditOutcome>>? _onEdit;
    public Func<string, string, Result<DocxSaveOutcome>>? _onCreate;
    public Func<string, Result<IReadOnlyList<DocxCommentInfo>>>? _onListComments;
    public Func<string, FileVersion, string, string, string, Result<DocxSaveOutcome>>? _onAddComment;
    public Func<string, FileVersion, int, Result<DocxSaveOutcome>>? _onDeleteComment;
    public Func<string, Result<IReadOnlyList<DocxRevisionInfo>>>? _onListRevisions;
    public Func<string, FileVersion, bool, DocxRevisionScope, Result<DocxSaveOutcome>>? _onResolveRevisions;
    public Func<string, IReadOnlyList<string>?, Result<IReadOnlyList<ToolResultImage>>>? _onExtractImages;

    public Task<Result<DocxSnapshot>> ReadAsync(string path, int offset, int maxBlocks, CancellationToken ct = default)
        => Task.FromResult(_onRead?.Invoke(path, offset, maxBlocks) ?? throw NotWired(nameof(_onRead)));

    public Task<Result<DocxEditOutcome>> EditAsync(string path, FileVersion expectedVersion, IReadOnlyList<DocxOperation> operations, CancellationToken ct = default)
        => Task.FromResult(_onEdit?.Invoke(path, expectedVersion, operations) ?? throw NotWired(nameof(_onEdit)));

    public Task<Result<DocxSaveOutcome>> CreateAsync(string path, string markdown, CancellationToken ct = default)
        => Task.FromResult(_onCreate?.Invoke(path, markdown) ?? throw NotWired(nameof(_onCreate)));

    public Task<Result<IReadOnlyList<DocxCommentInfo>>> ListCommentsAsync(string path, CancellationToken ct = default)
        => Task.FromResult(_onListComments?.Invoke(path) ?? throw NotWired(nameof(_onListComments)));

    public Task<Result<DocxSaveOutcome>> AddCommentAsync(string path, FileVersion expectedVersion, string address, string author, string text, CancellationToken ct = default)
        => Task.FromResult(_onAddComment?.Invoke(path, expectedVersion, address, author, text) ?? throw NotWired(nameof(_onAddComment)));

    public Task<Result<DocxSaveOutcome>> DeleteCommentAsync(string path, FileVersion expectedVersion, int commentId, CancellationToken ct = default)
        => Task.FromResult(_onDeleteComment?.Invoke(path, expectedVersion, commentId) ?? throw NotWired(nameof(_onDeleteComment)));

    public Task<Result<IReadOnlyList<DocxRevisionInfo>>> ListRevisionsAsync(string path, CancellationToken ct = default)
        => Task.FromResult(_onListRevisions?.Invoke(path) ?? throw NotWired(nameof(_onListRevisions)));

    public Task<Result<DocxSaveOutcome>> ResolveRevisionsAsync(string path, FileVersion expectedVersion, bool accept, DocxRevisionScope scope, CancellationToken ct = default)
        => Task.FromResult(_onResolveRevisions?.Invoke(path, expectedVersion, accept, scope) ?? throw NotWired(nameof(_onResolveRevisions)));

    public Task<Result<IReadOnlyList<ToolResultImage>>> ExtractImagesAsync(string path, IReadOnlyList<string>? names, CancellationToken ct = default)
        => Task.FromResult(_onExtractImages?.Invoke(path, names) ?? throw NotWired(nameof(_onExtractImages)));

    private static InvalidOperationException NotWired(string name)
        => new($"FakeDocxAccess.{name} was called without a wired delegate.");
  }
}
