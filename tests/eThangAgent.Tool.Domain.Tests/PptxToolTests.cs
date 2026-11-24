using eThangAgent.SharedKernel;
using eThangAgent.TestSupport;

namespace eThangAgent.ToolDomain.Tests;

/// <summary>Dispatch tests for the pptx tool: per-action rendering contracts, path
///     resolution and worktree anchoring, ledger recording, vision gating on extract, the
///     verbatim format contract in the description, and verbatim surfacing of seam errors -
///     all against a fake IPptxAccess (the domain never sees PresentationML).</summary>
public sealed class PptxToolTests : IDisposable
{
#pragma warning disable JSON002

  private static readonly FileVersion V1 = new(100, new DateTime(638000000000000000, DateTimeKind.Utc));
  private static readonly FileVersion V2 = new(120, new DateTime(638000000000000100, DateTimeKind.Utc));
  private static readonly string Token1 = FileVersionToken.Encode(V1);
  private static readonly string Token2 = FileVersionToken.Encode(V2);

  private readonly string _root;
  private readonly FakePptxAccess _fake = new();

  public PptxToolTests()
  {
    _root = Path.Combine(TestTempRoot.Root, "pptx-tool-tests-" + Guid.NewGuid().ToString("N"));
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

  private PptxTool Tool(ReadFreshnessLedger? ledger = null, IImageInputCapability? vision = null)
      => new(new WorkspacePathResolver(_root), _fake, vision, ledger);

  private static Task<ToolResult> Dispatch(PptxTool tool, string json)
      => tool.ExecuteAsync(new RawToolInput("pptx", json));

  private string Resolved(string relative)
      => new WorkspacePathResolver(_root).Resolve(relative).Value!;

  private static PptxOutline OutlineOf(params PptxSlideSummary[] slides) =>
      new(V1, slides.Length, slides, [new PptxMediaInfo("/ppt/media/image1.png", "image/png", 10)], ["Title Slide", "Title and Content"]);

  private static PptxSlideSummary Summary(int number, string title, string firstLine, bool hidden, string layout) =>
      new(number, title, firstLine, hidden, layout);

  private static PptxSlideSnapshot SlideOf(int number, IReadOnlyList<PptxShapeInfo> shapes,
      string notes = "", IReadOnlyList<PptxCommentInfo>? comments = null, IReadOnlyList<PptxAnimationInfo>? animations = null) =>
      new(V1, new PptxSlideContent(number, "Title and Content", false, shapes), notes,
          [new PptxMediaInfo("/ppt/media/image1.png", "image/png", 10)], comments ?? [], animations ?? []);

  private const string ReadJson = "{\"action\":\"read\",\"path\":\"deck.pptx\",\"timeoutSeconds\":30}";

  // ---- definition ----

  [Fact]
  public void Definition_IsNamedPptxAndDemandsActionAndPath()
  {
    ToolDefinition definition = Tool().Definition;
    Assert.Equal("pptx", definition.Name);
    Assert.Equal(["timeoutSeconds", "action", "path"], definition.RequiredParameters);
  }

  [Fact]
  public void Definition_DescriptionStatesTheAddressGrammarVerbatim()
  {
    string description = Tool().Definition.Description;
    Assert.Contains("s<N>.sh<M>", description, StringComparison.Ordinal);
    Assert.Contains("s<N>.sh<M>.sh<K>", description, StringComparison.Ordinal);
    Assert.Contains("s<N>.sh<M>.t<R>.c<C>", description, StringComparison.Ordinal);
    Assert.Contains("s<N>.notes", description, StringComparison.Ordinal);
    Assert.Contains("s<N>.anim<K>", description, StringComparison.Ordinal);
  }

  [Fact]
  public void Definition_DescriptionNamesEveryOperationWithAnExample()
  {
    string description = Tool().Definition.Description;
    foreach (string op in new[]
    {
        "replaceText", "setNotes", "insertSlide", "deleteSlide", "moveSlide", "setSlideLayout",
        "replacePicture", "insertPicture", "insertTable", "insertTextBox", "setChartData",
        "setAnimation", "removeAnimation",
    })
    {
      Assert.Contains(op + " {", description, StringComparison.Ordinal);
    }
  }

  [Fact]
  public void Definition_DescriptionStatesTheVersionTokenAndRefusalCodes()
  {
    string description = Tool().Definition.Description;
    Assert.Contains("v<length>:<ticks>", description, StringComparison.Ordinal);
    Assert.Contains("StalePresentation", description, StringComparison.Ordinal);
    Assert.Contains("OperationUnsupported", description, StringComparison.Ordinal);
    Assert.Contains("ChartDataMissing", description, StringComparison.Ordinal);
  }

  // ---- read: outline ----

  [Fact]
  public async Task Read_OutlineRendersOneLinePerSlide()
  {
    _fake._onReadOutline = _ => Result.Success(OutlineOf(
        Summary(1, "CompTIA A+ Core 1", "Mobile devices", false, "Title Slide"),
        Summary(2, "Chapter 2", "Networking", true, "Title and Content")));
    ToolResult r = await Dispatch(Tool(), ReadJson);
    Assert.False(r.IsError, r.Content);
    Assert.Contains($"[pptx {Resolved("deck.pptx")} v{V1.Token} slides 2 layouts [Title Slide; Title and Content]]", r.Content, StringComparison.Ordinal);
    Assert.Contains("s1 | Title Slide | CompTIA A+ Core 1 | Mobile devices", r.Content, StringComparison.Ordinal);
    Assert.Contains("s2 | hidden | Title and Content | Chapter 2 | Networking", r.Content, StringComparison.Ordinal);
    Assert.Contains("version " + Token1, r.Content, StringComparison.Ordinal);
    Assert.Contains("flags: slides=2 hidden=1 media=[/ppt/media/image1.png image/png 10]", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Read_OutlineRecordsTheLedger()
  {
    _fake._onReadOutline = _ => Result.Success(OutlineOf(Summary(1, "A", "B", false, "Title Slide")));
    ReadFreshnessLedger ledger = new();
    _ = await Dispatch(Tool(ledger: ledger), ReadJson);
    Assert.False(ledger.Check(Resolved("deck.pptx"), V1, 1, 1).IsEmpty);
  }

  [Fact]
  public async Task Read_OutlineSurfacesTheSeamErrorVerbatim()
  {
    _fake._onReadOutline = _ => Result.Failure<PptxOutline>(
        new DomainError(PptxErrorCodes.NotAPptx, "the package carries no ppt/presentation.xml"));
    ToolResult r = await Dispatch(Tool(), ReadJson);
    Assert.True(r.IsError);
    Assert.Contains("Error [NotAPptx]: the package carries no ppt/presentation.xml", r.Content, StringComparison.Ordinal);
  }

  // ---- read: one slide ----

  [Fact]
  public async Task Read_SlideRendersShapesNotesCommentsAndAnimations()
  {
    PptxShapeInfo[] shapes =
    [
        new PptxTextBoxShape(1, "s3.sh1", "Chapter 1", true, true),
        new PptxPictureShape(2, "s3.sh2", "/ppt/media/image1.png"),
        new PptxTableShape(3, "s3.sh3", 2, 2, [new PptxCellText(1, 1, "a"), new PptxCellText(1, 2, "b"),
            new PptxCellText(2, 1, "c"), new PptxCellText(2, 2, "d")]),
        new PptxChartShape(4, "s3.sh4", "bar", ["Revenue"], ["Q1", "Q2"]),
        new PptxGroupShape(5, "s3.sh5", [new PptxTextBoxShape(1, "s3.sh5.sh1", "grouped", false, false)]),
    ];
    _fake._onReadSlide = (_, n) => Result.Success(SlideOf(n, shapes, "speaker words",
        [new PptxCommentInfo(1, "Reviewer", "Check this figure", "s3.sh1", true)],
        [new PptxAnimationInfo(1, "s3.sh1", "entr:10", "onClick", "2000")]));
    ToolResult r = await Dispatch(Tool(), "{\"action\":\"read\",\"path\":\"deck.pptx\",\"timeoutSeconds\":30,\"slide\":3}");
    Assert.False(r.IsError, r.Content);
    Assert.Contains($"[pptx {Resolved("deck.pptx")} v{V1.Token} slide 3]", r.Content, StringComparison.Ordinal);
    Assert.Contains("slide 3 | Title and Content | hidden=false", r.Content, StringComparison.Ordinal);
    Assert.Contains("s3.sh1 | text | title | Chapter 1", r.Content, StringComparison.Ordinal);
    Assert.Contains("s3.sh2 | picture | /ppt/media/image1.png", r.Content, StringComparison.Ordinal);
    Assert.Contains("s3.sh3 | table | 2x2", r.Content, StringComparison.Ordinal);
    Assert.Contains("    s3.sh3.t1.c1 | a", r.Content, StringComparison.Ordinal);
    Assert.Contains("    s3.sh3.t2.c2 | d", r.Content, StringComparison.Ordinal);
    Assert.Contains("s3.sh4 | chart | bar | series [Revenue] | categories [Q1; Q2]", r.Content, StringComparison.Ordinal);
    Assert.Contains("s3.sh5 | group | 1 child(ren)", r.Content, StringComparison.Ordinal);
    Assert.Contains("    s3.sh5.sh1 | text | box | grouped", r.Content, StringComparison.Ordinal);
    Assert.Contains("notes: speaker words", r.Content, StringComparison.Ordinal);
    Assert.Contains("1 | Reviewer | s3.sh1 | legacy=true | Check this figure", r.Content, StringComparison.Ordinal);
    Assert.Contains("1 | s3.sh1 | entr:10 | onClick | 2000ms", r.Content, StringComparison.Ordinal);
    Assert.Contains("version " + Token1, r.Content, StringComparison.Ordinal);
    Assert.Contains("flags: shapes=5 media=[/ppt/media/image1.png image/png 10]", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Read_SlideRendersEmptySectionsAsNone()
  {
    _fake._onReadSlide = (_, n) => Result.Success(SlideOf(n, [new PptxTextBoxShape(1, "s1.sh1", "", false, false)]));
    ToolResult r = await Dispatch(Tool(), "{\"action\":\"read\",\"path\":\"deck.pptx\",\"timeoutSeconds\":30,\"slide\":1}");
    Assert.False(r.IsError, r.Content);
    Assert.Contains("notes: none", r.Content, StringComparison.Ordinal);
    Assert.Contains("comments: none", r.Content, StringComparison.Ordinal);
    Assert.Contains("animations: none", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Read_SlideRecordsTheLedger()
  {
    _fake._onReadSlide = (_, n) => Result.Success(SlideOf(n, []));
    ReadFreshnessLedger ledger = new();
    _ = await Dispatch(Tool(ledger: ledger), "{\"action\":\"read\",\"path\":\"deck.pptx\",\"timeoutSeconds\":30,\"slide\":3}");
    Assert.False(ledger.Check(Resolved("deck.pptx"), V1, 3, 3).IsEmpty);
  }

  [Fact]
  public async Task Read_SlideSurfacesSlideNotFound()
  {
    _fake._onReadSlide = (_, n) => Result.Failure<PptxSlideSnapshot>(
        new DomainError(PptxErrorCodes.SlideNotFound, $"slide {n} is outside 1..2"));
    ToolResult r = await Dispatch(Tool(), "{\"action\":\"read\",\"path\":\"deck.pptx\",\"timeoutSeconds\":30,\"slide\":42}");
    Assert.True(r.IsError);
    Assert.Contains("Error [SlideNotFound]", r.Content, StringComparison.Ordinal);
  }

  // ---- edit ----

  [Fact]
  public async Task Edit_PassesExpectedVersionAndOperationsToTheSeam()
  {
    FileVersion? capturedVersion = null;
    IReadOnlyList<PptxOperation>? capturedOps = null;
    _fake._onEdit = (_, version, operations) =>
    {
      capturedVersion = version;
      capturedOps = operations;
      return Result.Success(new PptxEditOutcome(V2, 1));
    };
    string json = "{\"action\":\"edit\",\"path\":\"deck.pptx\",\"timeoutSeconds\":30,\"expectedVersion\":\"" + Token1 + "\"," +
                  "\"operations\":[{\"op\":\"replaceText\",\"address\":\"s2.sh1\",\"text\":\"New\"}]}";
    ToolResult r = await Dispatch(Tool(), json);
    Assert.False(r.IsError, r.Content);
    Assert.Equal(V1, capturedVersion);
    Assert.Equal("New", Assert.IsType<ReplaceText>(Assert.Single(capturedOps!)).Text);
    Assert.Contains($"[pptx edited {Resolved("deck.pptx")}] changed=1 version {Token2}", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Edit_StaleErrorSurfacesVerbatim()
  {
    _fake._onEdit = (_, _, _) => Result.Failure<PptxEditOutcome>(
        new DomainError(PptxErrorCodes.StalePresentation, "the deck changed since your read"));
    string json = "{\"action\":\"edit\",\"path\":\"deck.pptx\",\"timeoutSeconds\":30,\"expectedVersion\":\"" + Token1 + "\"," +
                  "\"operations\":[{\"op\":\"deleteSlide\",\"slide\":1}]}";
    ToolResult r = await Dispatch(Tool(), json);
    Assert.True(r.IsError);
    Assert.Contains("Error [StalePresentation]", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Edit_WithoutExpectedVersionNeverReachesTheSeam()
  {
    _fake._onEdit = (_, _, _) => throw new InvalidOperationException("the seam must not be called");
    ToolResult r = await Dispatch(Tool(),
        "{\"action\":\"edit\",\"path\":\"deck.pptx\",\"timeoutSeconds\":30,\"operations\":[{\"op\":\"deleteSlide\",\"slide\":1}]}");
    Assert.True(r.IsError);
    Assert.Contains("Error [MissingParameter]", r.Content, StringComparison.Ordinal);
    Assert.Contains("expectedVersion", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Edit_OperationsThatAreNotAnArrayNeverReachTheSeam()
  {
    _fake._onEdit = (_, _, _) => throw new InvalidOperationException("the seam must not be called");
    ToolResult r = await Dispatch(Tool(),
        "{\"action\":\"edit\",\"path\":\"deck.pptx\",\"timeoutSeconds\":30,\"expectedVersion\":\"" + Token1 + "\",\"operations\":\"nope\"}");
    Assert.True(r.IsError);
    Assert.Contains("array of operation objects", r.Content, StringComparison.Ordinal);
  }

  // ---- create ----

  [Fact]
  public async Task Create_RefusesAnExistingFile()
  {
    await File.WriteAllTextAsync(Path.Combine(_root, "exists.pptx"), "x", TestContext.Current.CancellationToken);
    _fake._onCreate = (_, _) => throw new InvalidOperationException("the seam must not be called");
    ToolResult r = await Dispatch(Tool(), "{\"action\":\"create\",\"path\":\"exists.pptx\",\"timeoutSeconds\":30,\"markdown\":\"# hi\"}");
    Assert.True(r.IsError);
    Assert.Contains("Error [InvalidParameterValue]", r.Content, StringComparison.Ordinal);
    Assert.Contains("already exists", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Create_RendersTheCreatedLine()
  {
    _fake._onCreate = (_, _) => Result.Success(new PptxSaveOutcome(V2));
    _fake._onReadOutline = _ => Result.Success(OutlineOf(Summary(1, "T", "B", false, "Title and Content")));
    ToolResult r = await Dispatch(Tool(), "{\"action\":\"create\",\"path\":\"new.pptx\",\"timeoutSeconds\":30,\"markdown\":\"# T\"}");
    Assert.False(r.IsError, r.Content);
    Assert.Contains($"[pptx created {Resolved("new.pptx")}] slides=1 version {Token2}", r.Content, StringComparison.Ordinal);
  }

  // ---- comments ----

  [Fact]
  public async Task Comments_ListRendersLegacyFlag()
  {
    _fake._onListComments = _ => Result.Success<IReadOnlyList<PptxCommentInfo>>(
        [new PptxCommentInfo(1, "Ethan", "check this", "s1.sh2", true)]);
    ToolResult r = await Dispatch(Tool(), "{\"action\":\"comments\",\"path\":\"deck.pptx\",\"timeoutSeconds\":30,\"commentAction\":\"list\"}");
    Assert.False(r.IsError, r.Content);
    Assert.Contains("1 | Ethan | s1.sh2 | legacy=true | check this", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Comments_ListWithNoneIsStatedPlainly()
  {
    _fake._onListComments = _ => Result.Success<IReadOnlyList<PptxCommentInfo>>([]);
    ToolResult r = await Dispatch(Tool(), "{\"action\":\"comments\",\"path\":\"deck.pptx\",\"timeoutSeconds\":30,\"commentAction\":\"list\"}");
    Assert.False(r.IsError, r.Content);
    Assert.Equal("comments: none", r.Content);
  }

  [Fact]
  public async Task Comments_AddRendersTheNewIdAndVersion()
  {
    _fake._onAddComment = (_, _, _, _, _) => Result.Success(new PptxSaveOutcome(V2));
    _fake._onListComments = _ => Result.Success<IReadOnlyList<PptxCommentInfo>>(
        [new PptxCommentInfo(1, "Reviewer", "Check this figure", "s2.sh1", false)]);
    ToolResult r = await Dispatch(Tool(),
        "{\"action\":\"comments\",\"path\":\"deck.pptx\",\"timeoutSeconds\":30,\"commentAction\":\"add\"," +
        "\"address\":\"s2.sh1\",\"author\":\"Reviewer\",\"text\":\"Check this figure\",\"expectedVersion\":\"" + Token1 + "\"}");
    Assert.False(r.IsError, r.Content);
    Assert.Contains($"[pptx comment added id=1] version {Token2}", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Comments_DeleteRendersTheIdAndVersion()
  {
    _fake._onDeleteComment = (_, _, id) => id == 2
        ? Result.Success(new PptxSaveOutcome(V2))
        : Result.Failure<PptxSaveOutcome>(new DomainError(PptxErrorCodes.CommentNotFound, "no such comment"));
    ToolResult r = await Dispatch(Tool(),
        "{\"action\":\"comments\",\"path\":\"deck.pptx\",\"timeoutSeconds\":30,\"commentAction\":\"delete\"," +
        "\"commentId\":2,\"expectedVersion\":\"" + Token1 + "\"}");
    Assert.False(r.IsError, r.Content);
    Assert.Contains($"[pptx comment deleted id=2] version {Token2}", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Comments_DeleteOfAnUnknownIdSurfacesCommentNotFound()
  {
    _fake._onDeleteComment = (_, _, _) => Result.Failure<PptxSaveOutcome>(
        new DomainError(PptxErrorCodes.CommentNotFound, "the deck carries 0 comments"));
    ToolResult r = await Dispatch(Tool(),
        "{\"action\":\"comments\",\"path\":\"deck.pptx\",\"timeoutSeconds\":30,\"commentAction\":\"delete\"," +
        "\"commentId\":99,\"expectedVersion\":\"" + Token1 + "\"}");
    Assert.True(r.IsError);
    Assert.Contains("Error [CommentNotFound]", r.Content, StringComparison.Ordinal);
  }

  // ---- extract ----

  [Fact]
  public async Task Extract_AttachesOnlyWhenVisionAccepts()
  {
    _fake._onReadOutline = _ => Result.Success(OutlineOf(Summary(1, "A", "B", false, "Title Slide")));
    _fake._onExtractImages = (_, _) => Result.Success<IReadOnlyList<ToolResultImage>>(
        [new ToolResultImage("image/png", Convert.ToBase64String((byte[])[1, 2, 3]))]);
    ToolResult without = await Dispatch(Tool(), "{\"action\":\"extract\",\"path\":\"deck.pptx\",\"timeoutSeconds\":30}");
    Assert.False(without.IsError, without.Content);
    Assert.Contains("listed-only (ImageNotAttachable: model has no image input)", without.Content, StringComparison.Ordinal);
    Assert.Null(without.Images);

    ToolResult with = await Dispatch(Tool(vision: new FakeVision(true)), "{\"action\":\"extract\",\"path\":\"deck.pptx\",\"timeoutSeconds\":30}");
    Assert.False(with.IsError, with.Content);
    Assert.Contains("/ppt/media/image1.png image/png 10 attached", with.Content, StringComparison.Ordinal);
    _ = Assert.Single(with.Images!);
  }

  [Fact]
  public async Task Extract_ListsNonAttachableMedia()
  {
    PptxOutline outline = new(V1, 1, [Summary(1, "A", "B", false, "Title Slide")],
        [new PptxMediaInfo("/ppt/media/a.wmf", "image/x-wmf", 20)], ["Title Slide"]);
    _fake._onReadOutline = _ => Result.Success(outline);
    _fake._onExtractImages = (_, _) => Result.Success<IReadOnlyList<ToolResultImage>>([]);
    ToolResult r = await Dispatch(Tool(vision: new FakeVision(true)), "{\"action\":\"extract\",\"path\":\"deck.pptx\",\"timeoutSeconds\":30}");
    Assert.False(r.IsError, r.Content);
    Assert.Contains("/ppt/media/a.wmf image/x-wmf 20 listed-only (ImageNotAttachable: PNG/JPEG only)", r.Content, StringComparison.Ordinal);
  }

  // ---- input-layer passthrough ----

  [Fact]
  public async Task UnknownAction_NamesAllFive()
  {
    ToolResult r = await Dispatch(Tool(), "{\"action\":\"slides\",\"path\":\"deck.pptx\",\"timeoutSeconds\":30}");
    Assert.True(r.IsError);
    Assert.Contains("Error [InvalidParameterValue]", r.Content, StringComparison.Ordinal);
    Assert.Contains("read, edit, create, comments, extract", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task UnknownKey_IsRefused()
  {
    ToolResult r = await Dispatch(Tool(), "{\"action\":\"read\",\"path\":\"deck.pptx\",\"timeoutSeconds\":30,\"bogus\":1}");
    Assert.True(r.IsError);
    Assert.Contains("Error [UnknownParameter]", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task MissingPath_IsRefused()
  {
    ToolResult r = await Dispatch(Tool(), "{\"action\":\"read\",\"timeoutSeconds\":30}");
    Assert.True(r.IsError);
    Assert.Contains("Error [MissingParameter]", r.Content, StringComparison.Ordinal);
  }

  // ---- anchoring and ledger wiring ----

  [Fact]
  public async Task RootedAt_ReplacesTheResolver()
  {
    ITool anchored = Tool().RootedAt(Path.Combine(_root, "anchor"));
    Assert.Equal("pptx", anchored.Definition.Name);
    ToolResult r = await Dispatch((PptxTool)anchored, "{\"action\":\"read\",\"path\":\"../escape.pptx\",\"timeoutSeconds\":30}");
    Assert.True(r.IsError);
    Assert.Contains("Error [PathOutsideWorkspace]", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task RootedAt_KeepsTheStrictResolverForMutatingActions()
  {
    ITool anchored = Tool().RootedAt(Path.Combine(_root, "anchor"));
    ToolResult r = await Dispatch((PptxTool)anchored,
        "{\"action\":\"edit\",\"path\":\"../escape.pptx\",\"timeoutSeconds\":30,\"expectedVersion\":\"" + Token1 + "\"," +
        "\"operations\":[{\"op\":\"deleteSlide\",\"slide\":1}]}");
    Assert.True(r.IsError);
    Assert.Contains("Error [PathOutsideWorkspace]", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public void Ledger_WiringReportsAndReplacesTheLedger()
  {
    PptxTool bare = Tool();
    Assert.False(bare.HasLedger);
    ITool bound = bare.WithLedger(new ReadFreshnessLedger());
    Assert.True(((ILedgerBoundTool)bound).HasLedger);
    Assert.Same(bound, ((ILedgerBoundTool)bound).WithLedger(new ReadFreshnessLedger()));
  }

  // ---- fakes ----

  private sealed class FakeVision(bool accepts) : IImageInputCapability
  {
    public bool AcceptsImages => accepts;
  }

  private sealed class FakePptxAccess : IPptxAccess
  {
    public Func<string, Result<PptxOutline>>? _onReadOutline;
    public Func<string, int, Result<PptxSlideSnapshot>>? _onReadSlide;
    public Func<string, FileVersion, IReadOnlyList<PptxOperation>, Result<PptxEditOutcome>>? _onEdit;
    public Func<string, string, Result<PptxSaveOutcome>>? _onCreate;
    public Func<string, Result<IReadOnlyList<PptxCommentInfo>>>? _onListComments;
    public Func<string, FileVersion, string, string, string, Result<PptxSaveOutcome>>? _onAddComment;
    public Func<string, FileVersion, int, Result<PptxSaveOutcome>>? _onDeleteComment;
    public Func<string, IReadOnlyList<string>?, Result<IReadOnlyList<ToolResultImage>>>? _onExtractImages;

    public Task<Result<PptxOutline>> ReadOutlineAsync(string path, CancellationToken ct = default)
        => Task.FromResult(_onReadOutline?.Invoke(path) ?? throw NotWired(nameof(_onReadOutline)));

    public Task<Result<PptxSlideSnapshot>> ReadSlideAsync(string path, int slide, CancellationToken ct = default)
        => Task.FromResult(_onReadSlide?.Invoke(path, slide) ?? throw NotWired(nameof(_onReadSlide)));

    public Task<Result<PptxEditOutcome>> EditAsync(string path, FileVersion expectedVersion,
        IReadOnlyList<PptxOperation> operations, CancellationToken ct = default)
        => Task.FromResult(_onEdit?.Invoke(path, expectedVersion, operations) ?? throw NotWired(nameof(_onEdit)));

    public Task<Result<PptxSaveOutcome>> CreateAsync(string path, string markdown, CancellationToken ct = default)
        => Task.FromResult(_onCreate?.Invoke(path, markdown) ?? throw NotWired(nameof(_onCreate)));

    public Task<Result<IReadOnlyList<PptxCommentInfo>>> ListCommentsAsync(string path, CancellationToken ct = default)
        => Task.FromResult(_onListComments?.Invoke(path) ?? throw NotWired(nameof(_onListComments)));

    public Task<Result<PptxSaveOutcome>> AddCommentAsync(string path, FileVersion expectedVersion,
        string address, string author, string text, CancellationToken ct = default)
        => Task.FromResult(_onAddComment?.Invoke(path, expectedVersion, address, author, text) ?? throw NotWired(nameof(_onAddComment)));

    public Task<Result<PptxSaveOutcome>> DeleteCommentAsync(string path, FileVersion expectedVersion,
        int commentId, CancellationToken ct = default)
        => Task.FromResult(_onDeleteComment?.Invoke(path, expectedVersion, commentId) ?? throw NotWired(nameof(_onDeleteComment)));

    public Task<Result<IReadOnlyList<ToolResultImage>>> ExtractImagesAsync(string path,
        IReadOnlyList<string>? names, CancellationToken ct = default)
        => Task.FromResult(_onExtractImages?.Invoke(path, names) ?? throw NotWired(nameof(_onExtractImages)));

    private static InvalidOperationException NotWired(string name)
        => new($"FakePptxAccess.{name} was called without a wired delegate.");
  }
}
