using System.Globalization;
using System.Text;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>The pptx tool: one action-switch over the IPptxAccess seam - read, edit, create,
///     comments, extract. Strict input parsing via <see cref="PptxToolInput"/>; workspace
///     path resolution with worktree anchoring via IWorkspaceScopedTool; turn-local
///     read-freshness ledger participation on read; the version token
///     (v&lt;length&gt;:&lt;ticks&gt;) is the model's handle for every version-guarded
///     mutation. The description below is the verbatim format contract the model reads;
///     the renderers implement it exactly. A slide is the pagination unit, so there is no
///     offset or maxBlocks here.
///     </summary>
public sealed class PptxTool(
    IPathResolver resolver,
    IPptxAccess pptx,
    IImageInputCapability? vision,
    ReadFreshnessLedger? ledger = null) : ITool, IWorkspaceScopedTool, ILedgerBoundTool
{
  private const string ToolName = "pptx";

  private const string PngMediaType = "image/png";

  private const string JpegMediaType = "image/jpeg";

  private readonly IPathResolver _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

  // Actions that only READ the deck. A trusted root approved for reading is usable for
  // these; edit, create and comments all write, so they take the strict resolver. Deny by
  // default: an action not named here is treated as mutating.
  private static readonly IReadOnlySet<string> ReadOnlyActions =
      new HashSet<string>(StringComparer.Ordinal) { "read", "extract" };
  private readonly IPptxAccess _pptx = pptx ?? throw new ArgumentNullException(nameof(pptx));
  private readonly IImageInputCapability? _vision = vision;
  private readonly ReadFreshnessLedger? _ledger = ledger;

  /// <inheritdoc />
  public ITool RootedAt(string workspaceRoot)
      => new PptxTool(new WorkspacePathResolver(workspaceRoot), _pptx, _vision, _ledger);

  /// <inheritdoc />
  public bool HasLedger => _ledger is not null;

  /// <inheritdoc />
  public ITool WithLedger(ReadFreshnessLedger ledger)
      => _ledger is null ? new PptxTool(_resolver, _pptx, _vision, ledger) : this;

  public ToolDefinition Definition { get; } = new(
      ToolName,
      BuildDescription(),
      [
          new ToolParameter(ToolTimeout.ParameterName, ToolParameterType.WholeNumber, ToolTimeout.ParameterDescription, Minimum: 1),
            new ToolParameter("action", ToolParameterType.Text,
                "Exactly one of read, edit, create, comments, extract (case-sensitive)."),
            new ToolParameter("path", ToolParameterType.Text,
                "Presentation path, workspace-relative or absolute-inside-workspace. Required for every action."),
            new ToolParameter("slide", ToolParameterType.WholeNumber,
                "Optional, read only: the 1-based slide number to read in full. Omit it for the outline."),
            new ToolParameter("expectedVersion", ToolParameterType.Text,
                "The exact 'version v<length>:<ticks>' token a previous read returned. Required for edit and for comments add/delete."),
            new ToolParameter("operations", ToolParameterType.Text,
                "Optional, edit only: array of operation objects (see the contract in the description)."),
            new ToolParameter("markdown", ToolParameterType.Text,
                "Optional, create only: the pinned-subset markdown source; a line of exactly three hyphens separates slides."),
            new ToolParameter("commentAction", ToolParameterType.Text,
                "Optional, comments only: list | add | delete."),
            new ToolParameter("commentId", ToolParameterType.WholeNumber,
                "Optional, comments delete only: the id a comments list returned."),
            new ToolParameter("address", ToolParameterType.Text,
                "Optional, comments add only: the shape address the comment anchors to."),
            new ToolParameter("author", ToolParameterType.Text,
                "Optional, comments add only: comment author name."),
            new ToolParameter("text", ToolParameterType.Text,
                "Optional, comments add only: comment text."),
            new ToolParameter("names", ToolParameterType.TextArray,
                "Optional, extract only: media names to extract (default all)."),
      ],
      [ToolTimeout.ParameterName, "action", "path"]);

  private static string BuildDescription()
  {
    StringBuilder sb = new();
    _ = sb.AppendLine("pptx - read, edit, create, and inspect .pptx presentations.");
    _ = sb.AppendLine();
    _ = sb.AppendLine("Actions (parameter 'action', required):");
    _ = sb.AppendLine("  read        Without 'slide': the outline, one line per slide. With 'slide': that one slide in full.");
    _ = sb.AppendLine("              Params: path, optional slide (1-based). A slide is the pagination unit; there is no offset or maxBlocks.");
    _ = sb.AppendLine("  edit        Atomic batch of operations. Params: path, expectedVersion, operations[].");
    _ = sb.AppendLine("  create      New deck from the pinned markdown subset. Params: path, markdown.");
    _ = sb.AppendLine("  comments    list | add | delete. Params: path; add: address, author, text, expectedVersion; delete: commentId, expectedVersion.");
    _ = sb.AppendLine("  extract     Embedded media as attachments (PNG/JPEG only). Params: path, optional names[].");
    _ = sb.AppendLine();
    _ = sb.AppendLine("Addresses (1-based, case-sensitive):");
    _ = sb.AppendLine("  s<N>                          slide N");
    _ = sb.AppendLine("  s<N>.sh<M>                    the Mth shape in slide N's shape tree, in document order");
    _ = sb.AppendLine("  s<N>.sh<M>.sh<K>              the Kth child of the group at shape M");
    _ = sb.AppendLine("  s<N>.sh<M>.t<R>.c<C>          row R, column C of the table at shape M");
    _ = sb.AppendLine("  s<N>.notes                    slide N's speaker notes");
    _ = sb.AppendLine("  s<N>.anim<K>                  slide N's animation effect K, in playback order");
    _ = sb.AppendLine("A chart is a shape (kind 'chart'), never a separate address form.");
    _ = sb.AppendLine();
    _ = sb.AppendLine("read outline output (verbatim contract):");
    _ = sb.AppendLine("[pptx <path> v<version> slides <count> layouts [<name>; ...]]");
    _ = sb.AppendLine("s1 | <layout name> | <title> | <first line>");
    _ = sb.AppendLine("s2 | hidden | <layout name> | <title> | <first line>");
    _ = sb.AppendLine("version v<length>:<ticks>");
    _ = sb.AppendLine("flags: slides=<n> hidden=<n> media=[<name> <type> <bytes>; ...]");
    _ = sb.AppendLine("Hidden slides are listed and flagged, never skipped. The 'version v<...>' line is the token EVERY edit and comments call must pass back as expectedVersion.");
    _ = sb.AppendLine();
    _ = sb.AppendLine("read slide output (verbatim contract):");
    _ = sb.AppendLine("[pptx <path> v<version> slide <n>]");
    _ = sb.AppendLine("slide <n> | <layout name> | hidden=<true|false>");
    _ = sb.AppendLine("s<n>.sh1 | text | title | <text>");
    _ = sb.AppendLine("s<n>.sh2 | picture | <media name>");
    _ = sb.AppendLine("s<n>.sh3 | table | 2x3");
    _ = sb.AppendLine("    s<n>.sh3.t1.c1 | <cell text>");
    _ = sb.AppendLine("s<n>.sh4 | chart | bar | series [<name>; ...] | categories [<name>; ...]");
    _ = sb.AppendLine("s<n>.sh5 | group | <children, one indented line each>");
    _ = sb.AppendLine("notes: <speaker notes, or 'none'>");
    _ = sb.AppendLine("comments: <count>, then one indented line per comment: <id> | <author> | <anchor> | legacy=<true|false> | <text>   (or 'comments: none')");
    _ = sb.AppendLine("animations: <count>, then one indented line per effect: <index> | <target address> | <effect> | <trigger> | <duration>ms   (or 'animations: none')");
    _ = sb.AppendLine("version v<length>:<ticks>");
    _ = sb.AppendLine("flags: shapes=<n> media=[<name> <type> <bytes>; ...]");
    _ = sb.AppendLine();
    _ = sb.AppendLine("edit operations (each object needs 'op'; geometry is English Metric Units, 914400 per inch):");
    _ = sb.AppendLine("  replaceText {address, text}                      - a text box (s<N>.sh<M>) or one table cell (s<N>.sh<M>.t<R>.c<C>); keeps position, size and the first run's formatting");
    _ = sb.AppendLine("  setNotes {slide, text}                           - replaces the slide's speaker notes; a slide with none gains them");
    _ = sb.AppendLine("  insertSlide {afterSlide, layout}                 - afterSlide 0 inserts at the front; layout names come from the outline's layouts list");
    _ = sb.AppendLine("  deleteSlide {slide}                              - the last remaining slide cannot be deleted");
    _ = sb.AppendLine("  moveSlide {from, to}");
    _ = sb.AppendLine("  setSlideLayout {slide, layout}                   - content that does not map to the new layout stays in place; a change that would strand content is refused");
    _ = sb.AppendLine("  replacePicture {address, image}                  - image is a workspace path to a PNG or JPEG; position, size and crop are kept");
    _ = sb.AppendLine("  insertPicture {slide, image, x, y, width, height}");
    _ = sb.AppendLine("  insertTable {slide, rows, cols, cells, x, y, width, height}   - cells is rows arrays of cols strings");
    _ = sb.AppendLine("  insertTextBox {slide, text, x, y, width, height}");
    _ = sb.AppendLine("  setChartData {address, series?, categories?, values?}         - values is one array per series; the chart's type never changes");
    _ = sb.AppendLine("  setAnimation {slide, effectIndex, effect?, trigger?, durationMilliseconds?}   - effect is appear, fade or wipe; a NEW effect (index one past the last) must name its shape: 'fade s1.sh2'");
    _ = sb.AppendLine("  removeAnimation {slide, effectIndex}");
    _ = sb.AppendLine("Triggers: onClick, withPrevious, afterPrevious. Null members are left unchanged. The batch is atomic: one invalid operation refuses the whole batch and nothing is saved.");
    _ = sb.AppendLine("Output: [pptx edited <path>] changed=<n> version v<newtoken> - the NEW token supersedes the old.");
    _ = sb.AppendLine();
    _ = sb.AppendLine("create markdown subset: a line of exactly three hyphens separates slides; the first non-empty line of a slide is its title ('# ' and '- ' markers stripped); every other non-empty line is a bullet; a line indented by a tab or two spaces is a second-level bullet; **bold** and *italic* are applied.");
    _ = sb.AppendLine("Output: [pptx created <path>] slides=<n> version v<token>.");
    _ = sb.AppendLine();
    _ = sb.AppendLine("comments list: <id> | <author> | <anchor> | legacy=<true|false> | <text>. add/delete output: [pptx comment added id=<n>] / [pptx comment deleted id=<n>] version v<token>.");
    _ = sb.AppendLine("extract: text lists every media part; PNG/JPEG attach as images when vision accepts; others listed only.");
    _ = sb.AppendLine();
    _ = sb.AppendLine("Errors: Error [NotAPptx|MacroPresentation|CorruptPresentation|OversizePresentation|StalePresentation|InvalidAddress|AddressNotFound|SlideNotFound|LayoutNotFound|StrandedContent|ChartDataMissing|ChartTypeUnsupported|AnimationNotFound|CommentNotFound|MarkdownInvalid|ImageNotAttachable|OperationUnsupported|FileNotFound|InvalidParameterValue|MissingParameter|UnknownParameter|PathOutsideWorkspace].");
    return sb.ToString();
  }

  public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(input);
    Result<PptxToolInput> parsed = PptxToolInput.FromArguments(input.JsonArguments);
    if (!parsed.IsSuccess)
    {
      return Task.FromResult(Err(parsed.Error));
    }

    PptxToolInput args = parsed.Value;
    // Action-scoped resolution: reading and extraction may reach a read-only trusted root;
    // every writing action keeps the strict resolver.
    Result<string> resolved = ActionScopedPathResolver.Resolve(
        _resolver, args.Path!, input.JsonArguments, ReadOnlyActions);
    if (!resolved.IsSuccess)
    {
      return Task.FromResult(Err(resolved.Error));
    }

    Result<ToolCallEnvelope> budget = ToolCallEnvelopeParser.Parse(input.Name, input.JsonArguments);
    return !budget.IsSuccess
        ? Task.FromResult(Err(budget.Error))
        : ToolExecution.RunAsync(input.Name, budget.Value.Timeout, token => DispatchAsync(args, resolved.Value, token), ct);
  }

  private async Task<ToolResult> DispatchAsync(PptxToolInput args, string path, CancellationToken ct)
  {
    return args.Action switch
    {
      "read" => await ReadAsync(args, path, ct).ConfigureAwait(false),
      "edit" => await EditAsync(args, path, ct).ConfigureAwait(false),
      "create" => await CreateAsync(args, path, ct).ConfigureAwait(false),
      "comments" => await CommentsAsync(args, path, ct).ConfigureAwait(false),
      "extract" => await ExtractAsync(args, path, ct).ConfigureAwait(false),
      _ => new ToolResult($"Error [InvalidParameterValue]: unknown action '{args.Action}'.", true),
    };
  }

  /// <summary>read: the outline without a slide number, one slide in full with one. The
  ///     outline records the whole slide range in the ledger; a single slide records its
  ///     own line.</summary>
  private async Task<ToolResult> ReadAsync(PptxToolInput args, string path, CancellationToken ct)
  {
    return args.Slide == 0
        ? await ReadOutlineAsync(path, ct).ConfigureAwait(false)
        : await ReadSlideAsync(args.Slide, path, ct).ConfigureAwait(false);
  }

  private async Task<ToolResult> ReadOutlineAsync(string path, CancellationToken ct)
  {
    Result<PptxOutline> read = await _pptx.ReadOutlineAsync(path, ct).ConfigureAwait(false);
    if (!read.IsSuccess)
    {
      return Err(read.Error);
    }

    PptxOutline outline = read.Value;
    _ledger?.Record(path, outline.Version, 1, Math.Max(outline.SlideCount, 1));
    return new ToolResult(RenderOutline(path, outline), false);
  }

  private async Task<ToolResult> ReadSlideAsync(int slide, string path, CancellationToken ct)
  {
    Result<PptxSlideSnapshot> read = await _pptx.ReadSlideAsync(path, slide, ct).ConfigureAwait(false);
    if (!read.IsSuccess)
    {
      return Err(read.Error);
    }

    PptxSlideSnapshot snapshot = read.Value;
    _ledger?.Record(path, snapshot.Version, slide, slide);
    return new ToolResult(RenderSlide(path, snapshot), false);
  }

  /// <summary>The outline contract: a header naming the deck, its version, its slide count
  ///     and its layout names, one line per slide, the version token, and the flags line.
  ///     </summary>
  private static string RenderOutline(string path, PptxOutline outline)
  {
    StringBuilder sb = new();
    _ = sb.AppendLine(CultureInfo.InvariantCulture,
        $"[pptx {path} v{outline.Version.Token} slides {outline.SlideCount} layouts [{string.Join("; ", outline.LayoutNames)}]]");
    foreach (PptxSlideSummary summary in outline.Slides)
    {
      AppendSummary(sb, summary);
    }

    _ = sb.AppendLine(CultureInfo.InvariantCulture, $"version {FileVersionToken.Encode(outline.Version)}");
    _ = sb.Append(CultureInfo.InvariantCulture,
        $"flags: slides={outline.SlideCount} hidden={outline.Slides.Count(s => s.Hidden)} media=[{RenderMedia(outline.Media)}]");
    return sb.ToString();
  }

  /// <summary>One outline line: number, the hidden marker when the slide is hidden, layout
  ///     name, title and first line.</summary>
  private static void AppendSummary(StringBuilder sb, PptxSlideSummary summary)
  {
    string hidden = summary.Hidden ? "hidden | " : string.Empty;
    _ = sb.AppendLine(CultureInfo.InvariantCulture,
        $"s{summary.Number} | {hidden}{summary.LayoutName} | {summary.Title} | {summary.FirstLine}");
  }

  /// <summary>The slide contract: a header, one line per shape in document order, the
  ///     notes, the comments, the animations, the version token and the flags line.
  ///     </summary>
  private static string RenderSlide(string path, PptxSlideSnapshot snapshot)
  {
    StringBuilder sb = new();
    _ = sb.AppendLine(CultureInfo.InvariantCulture, $"[pptx {path} v{snapshot.Version.Token} slide {snapshot.Number}]");
    _ = sb.AppendLine(CultureInfo.InvariantCulture,
        $"slide {snapshot.Number} | {snapshot.LayoutName} | hidden={Flag(snapshot.Hidden)}");
    foreach (PptxShapeInfo shape in snapshot.Shapes)
    {
      AppendShape(sb, shape, string.Empty);
    }

    _ = sb.AppendLine(CultureInfo.InvariantCulture, $"notes: {Notes(snapshot.Notes)}");
    AppendComments(sb, snapshot.Comments);
    AppendAnimations(sb, snapshot.Animations);
    _ = sb.AppendLine(CultureInfo.InvariantCulture, $"version {FileVersionToken.Encode(snapshot.Version)}");
    _ = sb.Append(CultureInfo.InvariantCulture,
        $"flags: shapes={snapshot.Shapes.Count} media=[{RenderMedia(snapshot.Media)}]");
    return sb.ToString();
  }

  /// <summary>One shape line, then whatever the shape carries beneath it: a table's cells,
  ///     a group's children, one indent level per nesting level.</summary>
  private static void AppendShape(StringBuilder sb, PptxShapeInfo shape, string indent)
  {
    switch (shape)
    {
      case PptxTextBoxShape text:
        _ = sb.AppendLine(CultureInfo.InvariantCulture,
            $"{indent}{text.Address} | text | {Role(text)} | {text.Text}");
        break;

      case PptxPictureShape picture:
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{picture.Address} | picture | {picture.MediaName}");
        break;

      case PptxTableShape table:
        AppendTable(sb, table, indent);
        break;

      case PptxChartShape chart:
        _ = sb.AppendLine(CultureInfo.InvariantCulture,
            $"{indent}{chart.Address} | chart | {chart.ChartType} | series [{string.Join("; ", chart.SeriesNames)}] | categories [{string.Join("; ", chart.CategoryNames)}]");
        break;

      default:
        AppendGroup(sb, shape, indent);
        break;
    }
  }

  /// <summary>A text box's role: a title placeholder, any other placeholder, or a plain
  ///     text box.</summary>
  private static string Role(PptxTextBoxShape text) => text switch
  {
    { IsTitle: true } => "title",
    { IsPlaceholder: true } => "placeholder",
    _ => "box",
  };

  /// <summary>A boolean as the contract spells it: true or false.</summary>
  private static string Flag(bool value) => value ? "true" : "false";

  /// <summary>A table's line and one indented line per cell, addressed by row and column.
  ///     </summary>
  private static void AppendTable(StringBuilder sb, PptxTableShape table, string indent)
  {
    _ = sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{table.Address} | table | {table.Rows}x{table.Cols}");
    foreach (PptxCellText cell in table.Cells.OrderBy(c => c.Row).ThenBy(c => c.Col))
    {
      _ = sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {table.Address}.t{cell.Row}.c{cell.Col} | {cell.Text}");
    }
  }

  /// <summary>A group's line and its children, one indent level deeper.</summary>
  private static void AppendGroup(StringBuilder sb, PptxShapeInfo shape, string indent)
  {
    PptxGroupShape group = (PptxGroupShape)shape;
    _ = sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{group.Address} | group | {group.Children.Count} child(ren)");
    foreach (PptxShapeInfo child in group.Children)
    {
      AppendShape(sb, child, indent + "    ");
    }
  }

  /// <summary>The notes line's value: the text, or 'none' when the slide has none.</summary>
  private static string Notes(string notes) => notes.Length == 0 ? "none" : notes;

  /// <summary>The comments block: one line per comment with its id, author, anchor, whether
  ///     it came from the legacy shape, and its text.</summary>
  private static void AppendComments(StringBuilder sb, IReadOnlyList<PptxCommentInfo> comments)
  {
    if (comments.Count == 0)
    {
      _ = sb.AppendLine("comments: none");
      return;
    }

    _ = sb.AppendLine(CultureInfo.InvariantCulture, $"comments: {comments.Count}");
    foreach (PptxCommentInfo comment in comments)
    {
      _ = sb.AppendLine(CultureInfo.InvariantCulture,
          $"    {comment.Id} | {comment.Author} | {comment.AnchorAddress} | legacy={Flag(comment.Legacy)} | {comment.Text}");
    }
  }

  /// <summary>The animations block: one line per effect in playback order.</summary>
  private static void AppendAnimations(StringBuilder sb, IReadOnlyList<PptxAnimationInfo> animations)
  {
    if (animations.Count == 0)
    {
      _ = sb.AppendLine("animations: none");
      return;
    }

    _ = sb.AppendLine(CultureInfo.InvariantCulture, $"animations: {animations.Count}");
    foreach (PptxAnimationInfo animation in animations)
    {
      _ = sb.AppendLine(CultureInfo.InvariantCulture,
          $"    {animation.Index} | {animation.Address} | {animation.Effect} | {animation.Trigger} | {animation.Duration}ms");
    }
  }

  private static string RenderMedia(IReadOnlyList<PptxMediaInfo> media)
      => string.Join("; ", media.Select(m => $"{m.Name} {m.MediaType} {m.ByteCount}"));

  private async Task<ToolResult> EditAsync(PptxToolInput args, string path, CancellationToken ct)
  {
    Result<PptxEditOutcome> edited = await _pptx
        .EditAsync(path, args.ExpectedVersion!, args.Operations, ct).ConfigureAwait(false);
    return !edited.IsSuccess
        ? Err(edited.Error)
        : Ok($"[pptx edited {path}] changed={edited.Value.ChangedCount} version {FileVersionToken.Encode(edited.Value.NewVersion)}");
  }

  private async Task<ToolResult> CreateAsync(PptxToolInput args, string path, CancellationToken ct)
  {
    if (File.Exists(path))
    {
      return Err(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"'{path}' already exists - create is for new paths; delete the file first if replacement is intended."));
    }

    Result<PptxSaveOutcome> created = await _pptx.CreateAsync(path, args.Markdown!, ct).ConfigureAwait(false);
    if (!created.IsSuccess)
    {
      return Err(created.Error);
    }

    Result<PptxOutline> read = await _pptx.ReadOutlineAsync(path, ct).ConfigureAwait(false);
    int slides = read.IsSuccess ? read.Value.SlideCount : 0;
    return Ok($"[pptx created {path}] slides={slides} version {FileVersionToken.Encode(created.Value.NewVersion)}");
  }

  private async Task<ToolResult> CommentsAsync(PptxToolInput args, string path, CancellationToken ct)
  {
    return args.CommentAction switch
    {
      "list" => await ListCommentsAsync(path, ct).ConfigureAwait(false),
      "add" => await AddCommentAsync(args, path, ct).ConfigureAwait(false),
      _ => await DeleteCommentAsync(args, path, ct).ConfigureAwait(false),
    };
  }

  private async Task<ToolResult> ListCommentsAsync(string path, CancellationToken ct)
  {
    Result<IReadOnlyList<PptxCommentInfo>> listed = await _pptx.ListCommentsAsync(path, ct).ConfigureAwait(false);
    if (!listed.IsSuccess)
    {
      return Err(listed.Error);
    }

    if (listed.Value.Count == 0)
    {
      return Ok("comments: none");
    }

    StringBuilder sb = new();
    foreach (PptxCommentInfo c in listed.Value)
    {
      _ = sb.AppendLine(CultureInfo.InvariantCulture,
          $"{c.Id} | {c.Author} | {c.AnchorAddress} | legacy={Flag(c.Legacy)} | {c.Text}");
    }

    sb.Length -= Environment.NewLine.Length;
    return new ToolResult(sb.ToString(), false);
  }

  private async Task<ToolResult> AddCommentAsync(PptxToolInput args, string path, CancellationToken ct)
  {
    Result<PptxSaveOutcome> added = await _pptx
        .AddCommentAsync(path, args.ExpectedVersion!, args.Address!, args.Author!, args.Text!, ct)
        .ConfigureAwait(false);
    if (!added.IsSuccess)
    {
      return Err(added.Error);
    }

    Result<IReadOnlyList<PptxCommentInfo>> listed = await _pptx.ListCommentsAsync(path, ct).ConfigureAwait(false);
    int id = listed.IsSuccess && listed.Value.Count > 0 ? listed.Value.Max(c => c.Id) : 0;
    return Ok($"[pptx comment added id={id}] version {FileVersionToken.Encode(added.Value.NewVersion)}");
  }

  private async Task<ToolResult> DeleteCommentAsync(PptxToolInput args, string path, CancellationToken ct)
  {
    Result<PptxSaveOutcome> deleted = await _pptx
        .DeleteCommentAsync(path, args.ExpectedVersion!, args.CommentId, ct).ConfigureAwait(false);
    return !deleted.IsSuccess
        ? Err(deleted.Error)
        : Ok($"[pptx comment deleted id={args.CommentId}] version {FileVersionToken.Encode(deleted.Value.NewVersion)}");
  }

  private async Task<ToolResult> ExtractAsync(PptxToolInput args, string path, CancellationToken ct)
  {
    Result<PptxOutline> read = await _pptx.ReadOutlineAsync(path, ct).ConfigureAwait(false);
    if (!read.IsSuccess)
    {
      return Err(read.Error);
    }

    List<PptxMediaInfo> media = [.. read.Value.Media];
    List<ToolResultImage> extracted = [];
    bool visionAccepts = _vision?.AcceptsImages == true;
    if (visionAccepts)
    {
      Result<IReadOnlyList<ToolResultImage>> pull = await _pptx
          .ExtractImagesAsync(path, args.ImageNames, ct).ConfigureAwait(false);
      if (!pull.IsSuccess)
      {
        return Err(pull.Error);
      }

      extracted = [.. pull.Value];
    }

    StringBuilder sb = RenderMediaLines(media, args.ImageNames, extracted, visionAccepts);
    return new ToolResult(sb.ToString(), false, Images: extracted.Count > 0 ? extracted : null);
  }

  /// <summary>Renders the extract listing: one line per media part, attaching the pulled
  ///     images in order to the parts the filter admits.</summary>
  private static StringBuilder RenderMediaLines(List<PptxMediaInfo> media, IReadOnlyList<string>? filter,
      List<ToolResultImage> extracted, bool visionAccepts)
  {
    StringBuilder sb = new();
    int attachIndex = 0;
    foreach (PptxMediaInfo m in media)
    {
      attachIndex += AppendMediaLine(sb, m, filter, extracted, attachIndex, visionAccepts);
    }

    sb.Length -= Environment.NewLine.Length;
    return sb;
  }

  /// <summary>Appends one media part's listing line; returns 1 when the part consumed an
  ///     attached image, 0 otherwise. The four outcomes mirror the docx tool's: non-PNG/JPEG,
  ///     no image input, attached, filtered out.</summary>
  private static int AppendMediaLine(StringBuilder sb, PptxMediaInfo m, IReadOnlyList<string>? filter,
      List<ToolResultImage> extracted, int attachIndex, bool visionAccepts)
  {
    bool isAttachableType = m.MediaType is PngMediaType or JpegMediaType;
    bool inFilter = filter is null || filter.Count == 0 || filter.Contains(m.Name);
    if (!isAttachableType)
    {
      _ = sb.AppendLine(CultureInfo.InvariantCulture,
          $"{m.Name} {m.MediaType} {m.ByteCount} listed-only (ImageNotAttachable: PNG/JPEG only)");
      return 0;
    }

    if (!visionAccepts)
    {
      _ = sb.AppendLine(CultureInfo.InvariantCulture,
          $"{m.Name} {m.MediaType} {m.ByteCount} listed-only (ImageNotAttachable: model has no image input)");
      return 0;
    }

    if (inFilter && attachIndex < extracted.Count)
    {
      _ = sb.AppendLine(CultureInfo.InvariantCulture, $"{m.Name} {m.MediaType} {m.ByteCount} attached");
      return 1;
    }

    _ = sb.AppendLine(CultureInfo.InvariantCulture,
        $"{m.Name} {m.MediaType} {m.ByteCount} listed-only (ImageNotAttachable: not in the requested names)");
    return 0;
  }

  private static ToolResult Ok(string content) => new(content, false);

  private static ToolResult Err(DomainError error) => new(
      $"Error [{error.Code}]: {error.Message}", true);
}
