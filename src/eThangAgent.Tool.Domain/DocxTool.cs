using System.Globalization;
using System.Text;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>The docx tool (task 3): one action-switch over the IDocxAccess seam -
///     read, edit, create, comments, revisions, extract. Strict input parsing via
///     DocxToolInput; workspace path resolution with worktree anchoring via
///     IWorkspaceScopedTool; turn-local read-freshness ledger participation on read;
///     the version token (v&lt;length&gt;:&lt;ticks&gt;) is the model's handle for every
///     version-guarded mutation. The description below is the verbatim format
///     contract the model reads; the renderers implement it exactly.</summary>
public sealed class DocxTool(
    IPathResolver resolver,
    IDocxAccess docx,
    IImageInputCapability? vision,
    ReadFreshnessLedger? ledger = null) : ITool, IWorkspaceScopedTool, ILedgerBoundTool
{
  private const string ToolName = "docx";

  private const string PngMediaType = "image/png";

  private const string JpegMediaType = "image/jpeg";

  private readonly IPathResolver _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
  private readonly IDocxAccess _docx = docx ?? throw new ArgumentNullException(nameof(docx));
  private readonly IImageInputCapability? _vision = vision;
  private readonly ReadFreshnessLedger? _ledger = ledger;

  /// <inheritdoc />
  public ITool RootedAt(string workspaceRoot)
      => new DocxTool(new WorkspacePathResolver(workspaceRoot), _docx, _vision, _ledger);

  /// <inheritdoc />
  public bool HasLedger => _ledger is not null;

  /// <inheritdoc />
  public ITool WithLedger(ReadFreshnessLedger ledger)
      => _ledger is null ? new DocxTool(_resolver, _docx, _vision, ledger) : this;

  public ToolDefinition Definition { get; } = new(
      ToolName,
      BuildDescription(),
      [
          new ToolParameter(ToolTimeout.ParameterName, ToolParameterType.WholeNumber, ToolTimeout.ParameterDescription, Minimum: 1),
            new ToolParameter("action", ToolParameterType.Text,
                "Exactly one of read, edit, create, comments, revisions, extract (case-sensitive)."),
            new ToolParameter("path", ToolParameterType.Text,
                "Document path, workspace-relative or absolute-inside-workspace. Required for every action."),
            new ToolParameter("offset", ToolParameterType.WholeNumber,
                "Optional, read only: block offset into the body list (default 0)."),
            new ToolParameter("maxBlocks", ToolParameterType.WholeNumber,
                "Optional, read only: maximum blocks per page, 1..400 (default 200)."),
            new ToolParameter("expectedVersion", ToolParameterType.Text,
                "The exact 'version v<length>:<ticks>' token a previous read returned. Required for edit, comments add/delete, revisions accept/reject."),
            new ToolParameter("operations", ToolParameterType.Text,
                "Optional, edit only: array of operation objects (see the contract in the description)."),
            new ToolParameter("markdown", ToolParameterType.Text,
                "Optional, create only: the pinned-subset markdown source."),
            new ToolParameter("commentAction", ToolParameterType.Text,
                "Optional, comments only: list | add | delete."),
            new ToolParameter("commentId", ToolParameterType.WholeNumber,
                "Optional, comments delete only: the c<id> from comments list."),
            new ToolParameter("address", ToolParameterType.Text,
                "Optional, comments add only: block address the comment anchors to."),
            new ToolParameter("author", ToolParameterType.Text,
                "Optional, comments add only: comment author name."),
            new ToolParameter("text", ToolParameterType.Text,
                "Optional, comments add only: comment text."),
            new ToolParameter("revisionAction", ToolParameterType.Text,
                "Optional, revisions only: list | accept | reject."),
            new ToolParameter("scope", ToolParameterType.Text,
                "Optional, revisions accept/reject only: {all: true} | {author: <name>} | {address: <address>}."),
            new ToolParameter("names", ToolParameterType.TextArray,
                "Optional, extract only: media names to extract (default all)."),
      ],
      [ToolTimeout.ParameterName, "action", "path"]);

  private static string BuildDescription()
  {
    StringBuilder sb = new();
    _ = sb.AppendLine("docx - read, edit, create, and inspect .docx documents.");
    _ = sb.AppendLine();
    _ = sb.AppendLine("Actions (parameter 'action', required):");
    _ = sb.AppendLine("  read        Numbered block view. Params: path, offset (block offset, default 0), maxBlocks (default 200, max 400).");
    _ = sb.AppendLine("  edit        Atomic batch of operations. Params: path, expectedVersion, operations[].");
    _ = sb.AppendLine("  create      New document from the pinned markdown subset. Params: path, markdown.");
    _ = sb.AppendLine("  comments    list | add | delete. Params: path; add: address, author, text, expectedVersion; delete: commentId, expectedVersion.");
    _ = sb.AppendLine("  revisions   list | accept | reject. Params: path; accept/reject: expectedVersion, optional scope {all|author|address}.");
    _ = sb.AppendLine("  extract     Embedded images as attachments (PNG/JPEG only). Params: path, optional names[].");
    _ = sb.AppendLine();
    _ = sb.AppendLine("Addresses: p<N> body paragraph; t<N> body table; t<N>.r<R>.c<C> table cell;");
    _ = sb.AppendLine("sec<S>.header.p<N> / sec<S>.footer.p<N> header/footer paragraph. All 1-based, case-sensitive.");
    _ = sb.AppendLine();
    _ = sb.AppendLine("read output (verbatim contract):");
    _ = sb.AppendLine("[docx <path> v<version> blocks <first>-<last> of <total>]");
    _ = sb.AppendLine("p1 | Heading 1 | text");
    _ = sb.AppendLine("p2 | Normal | text");
    _ = sb.AppendLine("t3 | table 4x2");
    _ = sb.AppendLine("    r1: c1 text | c2 text");
    _ = sb.AppendLine("version v<length>:<ticks>");
    _ = sb.AppendLine("flags: tracked-changes=<n> comments=<n> sections=<n> media=[name type bytes; ...]");
    _ = sb.AppendLine("The 'version v<...>' line is the token EVERY edit/comments/revisions call must pass back as expectedVersion.");
    _ = sb.AppendLine();
    _ = sb.AppendLine("edit operations (each object needs 'op'):");
    _ = sb.AppendLine("  replaceParagraphText {address, text}      - keeps paragraph style; first run's formatting applies; run variance lost");
    _ = sb.AppendLine("  setParagraphStyle {address, style}");
    _ = sb.AppendLine("  insertParagraph {afterAddress, text, style?}  - afterAddress 'p0' inserts before the first block");
    _ = sb.AppendLine("  deleteParagraph {address}");
    _ = sb.AppendLine("  setCellText {address, text}");
    _ = sb.AppendLine("  insertTableRow {tableAddress, afterRow}   - afterRow 0 = before first row");
    _ = sb.AppendLine("  deleteTableRow {tableAddress, row}");
    _ = sb.AppendLine("  applyRunFormatting {address, format{bold?,italic?,underline?,fontSizeHalfPoints?,colorHex?}} - whole paragraph's runs take it; null fields unchanged");
    _ = sb.AppendLine("  findReplace {find, replace}               - literal, body paragraphs, case-sensitive");
    _ = sb.AppendLine("The batch is atomic: one invalid operation refuses the whole batch, nothing is saved.");
    _ = sb.AppendLine("Output: [docx edited <path>] changed-blocks=<n> version v<newtoken> - the NEW token supersedes the old.");
    _ = sb.AppendLine();
    _ = sb.AppendLine("create markdown subset: '# '/## '/### ' headings; '- ' bullets; '1. '/'2. ' numbered; **bold**; *italic*; '| a | b |' consecutive lines = table ('|---|' separator rows ignored); blank line = paragraph break; anything else = literal paragraph.");
    _ = sb.AppendLine("Output: [docx created <path>] blocks=<n> version v<token>.");
    _ = sb.AppendLine();
    _ = sb.AppendLine("comments list: c<id> | <author> | <anchor> | <text>. add/delete output: [docx comment added id=<n>] / [docx comment deleted id=<n>] version v<token>.");
    _ = sb.AppendLine("revisions list: r<index> | <type> | <author> | <anchor>. accept/reject output: [docx revisions <verb> count=<n>] version v<token>.");
    _ = sb.AppendLine("extract: text lists every media part; PNG/JPEG attach as images when vision accepts; others listed only.");
    _ = sb.AppendLine();
    _ = sb.AppendLine("Errors: Error [NotADocx|MacroDocument|CorruptDocument|OversizeDocument|StaleDocument|AddressNotFound|InvalidAddress|StyleNotFound|CommentNotFound|RevisionNotFound|MarkdownInvalid|InvalidParameterValue|MissingParameter|UnknownParameter|PathOutsideWorkspace].");
    return sb.ToString();
  }

  public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(input);
    Result<DocxToolInput> parsed = DocxToolInput.FromArguments(input.JsonArguments);
    if (!parsed.IsSuccess)
    {
      return Task.FromResult(Err(parsed.Error));
    }

    DocxToolInput args = parsed.Value;
    Result<string> resolved = _resolver.Resolve(args.Path!);
    if (!resolved.IsSuccess)
    {
      return Task.FromResult(Err(resolved.Error));
    }

    Result<ToolCallEnvelope> budget = ToolCallEnvelopeParser.Parse(input.Name, input.JsonArguments);
    return !budget.IsSuccess
        ? Task.FromResult(Err(budget.Error))
        : ToolExecution.RunAsync(input.Name, budget.Value.Timeout, token => DispatchAsync(args, resolved.Value, token), ct);
  }

  private async Task<ToolResult> DispatchAsync(DocxToolInput args, string path, CancellationToken ct)
  {
    return args.Action switch
    {
      "read" => await ReadAsync(args, path, ct).ConfigureAwait(false),
      "edit" => await EditAsync(args, path, ct).ConfigureAwait(false),
      "create" => await CreateAsync(args, path, ct).ConfigureAwait(false),
      "comments" => await CommentsAsync(args, path, ct).ConfigureAwait(false),
      "revisions" => await RevisionsAsync(args, path, ct).ConfigureAwait(false),
      "extract" => await ExtractAsync(args, path, ct).ConfigureAwait(false),
      _ => new ToolResult($"Error [InvalidParameterValue]: unknown action '{args.Action}'.", true),
    };
  }

  private async Task<ToolResult> ReadAsync(DocxToolInput args, string path, CancellationToken ct)
  {
    Result<DocxSnapshot> read = await _docx.ReadAsync(path, args.Offset, args.MaxBlocks, ct).ConfigureAwait(false);
    if (!read.IsSuccess)
    {
      return Err(read.Error);
    }

    DocxSnapshot snap = read.Value;
    _ledger?.Record(path, snap.Version, args.Offset + 1, args.Offset + snap.Blocks.Count);

    StringBuilder sb = new();
    _ = sb.AppendLine(CultureInfo.InvariantCulture,
        $"[docx {path} v{snap.Version.Token} blocks {args.Offset + 1}-{args.Offset + snap.Blocks.Count} of {snap.TotalBlocks}]");
    foreach (DocxBlock block in snap.Blocks)
    {
      AppendBlock(sb, block);
    }

    _ = sb.AppendLine(CultureInfo.InvariantCulture, $"version {FileVersionToken.Encode(snap.Version)}");
    _ = sb.Append(CultureInfo.InvariantCulture,
        $"flags: tracked-changes={snap.TrackedChangeCount} comments={snap.CommentCount} sections={snap.SectionCount} media=[{RenderMedia(snap.Media)}]");
    return new ToolResult(sb.ToString(), false);
  }

  private static void AppendBlock(StringBuilder sb, DocxBlock block)
  {
    switch (block)
    {
      case DocxParagraphBlock p:
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"p{p.Index} | {p.Style} | {p.Text}");
        break;

      case DocxTableBlock t:
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"t{t.Index} | table {t.Rows}x{t.Cols}");
        for (int row = 1; row <= t.Rows; row++)
        {
          IEnumerable<string> cells = t.Cells
              .Where(c => c.Row == row)
              .OrderBy(c => c.Col)
              .Select(c => c.Text);
          _ = sb.AppendLine(CultureInfo.InvariantCulture, $"    r{row}: {string.Join(" | ", cells)}");
        }

        break;

      default:
        throw new InvalidOperationException($"Unhandled DocxBlock kind: {block.GetType().Name}");
    }
  }

  private static string RenderMedia(IReadOnlyList<DocxMediaInfo> media)
      => string.Join("; ", media.Select(m => $"{m.Name} {m.MediaType} {m.ByteCount}"));

  private async Task<ToolResult> EditAsync(DocxToolInput args, string path, CancellationToken ct)
  {
    Result<DocxEditOutcome> edited = await _docx
        .EditAsync(path, args.ExpectedVersion!, args.Operations, ct).ConfigureAwait(false);
    return !edited.IsSuccess
        ? Err(edited.Error)
        : Ok($"[docx edited {path}] changed-blocks={edited.Value.ChangedBlocks} version {FileVersionToken.Encode(edited.Value.NewVersion)}");
  }

  private async Task<ToolResult> CreateAsync(DocxToolInput args, string path, CancellationToken ct)
  {
    if (File.Exists(path))
    {
      return Err(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"'{path}' already exists - create is for new paths; delete the file first if replacement is intended."));
    }

    Result<DocxSaveOutcome> created = await _docx.CreateAsync(path, args.Markdown!, ct).ConfigureAwait(false);
    if (!created.IsSuccess)
    {
      return Err(created.Error);
    }

    Result<DocxSnapshot> read = await _docx.ReadAsync(path, 0, 1, ct).ConfigureAwait(false);
    int blocks = read.IsSuccess ? read.Value.TotalBlocks : 0;
    return Ok($"[docx created {path}] blocks={blocks} version {FileVersionToken.Encode(created.Value.NewVersion)}");
  }

  private async Task<ToolResult> CommentsAsync(DocxToolInput args, string path, CancellationToken ct)
  {
    switch (args.CommentAction)
    {
      case "list":
        {
          Result<IReadOnlyList<DocxCommentInfo>> listed = await _docx.ListCommentsAsync(path, ct).ConfigureAwait(false);
          if (!listed.IsSuccess)
          {
            return Err(listed.Error);
          }

          if (listed.Value.Count == 0)
          {
            return Ok("comments: none");
          }

          StringBuilder sb = new();
          foreach (DocxCommentInfo c in listed.Value)
          {
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"c{c.Id} | {c.Author} | {c.AnchorAddress} | {c.Text}");
          }

          sb.Length -= Environment.NewLine.Length;
          return new ToolResult(sb.ToString(), false);
        }

      case "add":
        {
          Result<DocxSaveOutcome> added = await _docx
              .AddCommentAsync(path, args.ExpectedVersion!, args.Address!, args.Author!, args.Text!, ct)
              .ConfigureAwait(false);
          if (!added.IsSuccess)
          {
            return Err(added.Error);
          }

          Result<IReadOnlyList<DocxCommentInfo>> listed = await _docx.ListCommentsAsync(path, ct).ConfigureAwait(false);
          int id = listed.IsSuccess && listed.Value.Count > 0 ? listed.Value.Max(c => c.Id) : 0;
          return Ok($"[docx comment added id={id}] version {FileVersionToken.Encode(added.Value.NewVersion)}");
        }

      default:
        {
          Result<DocxSaveOutcome> deleted = await _docx
              .DeleteCommentAsync(path, args.ExpectedVersion!, args.CommentId, ct).ConfigureAwait(false);
          return !deleted.IsSuccess
              ? Err(deleted.Error)
              : Ok($"[docx comment deleted id={args.CommentId}] version {FileVersionToken.Encode(deleted.Value.NewVersion)}");
        }
    }
  }

  private async Task<ToolResult> RevisionsAsync(DocxToolInput args, string path, CancellationToken ct)
  {
    switch (args.RevisionAction)
    {
      case "list":
        {
          Result<IReadOnlyList<DocxRevisionInfo>> listed = await _docx.ListRevisionsAsync(path, ct).ConfigureAwait(false);
          if (!listed.IsSuccess)
          {
            return Err(listed.Error);
          }

          if (listed.Value.Count == 0)
          {
            return Ok("revisions: none");
          }

          StringBuilder sb = new();
          foreach (DocxRevisionInfo r in listed.Value)
          {
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"r{r.Index} | {r.Type} | {r.Author} | {r.AnchorAddress}");
          }

          sb.Length -= Environment.NewLine.Length;
          return new ToolResult(sb.ToString(), false);
        }

      default:
        {
          DocxRevisionScope scope = args.Scope!;
          Result<IReadOnlyList<DocxRevisionInfo>> listed = await _docx.ListRevisionsAsync(path, ct).ConfigureAwait(false);
          int count = listed.IsSuccess ? listed.Value.Count(r => ScopeMatches(scope, r)) : 0;

          Result<DocxSaveOutcome> resolved = await _docx
              .ResolveRevisionsAsync(path, args.ExpectedVersion!, args.RevisionAction == "accept", scope, ct)
              .ConfigureAwait(false);
          return !resolved.IsSuccess
              ? Err(resolved.Error)
              : Ok($"[docx revisions {args.RevisionAction} count={count}] version {FileVersionToken.Encode(resolved.Value.NewVersion)}");
        }
    }
  }

  private static bool ScopeMatches(DocxRevisionScope scope, DocxRevisionInfo revision)
      => scope.All
          || (scope.Author is not null && scope.Author == revision.Author)
          || (scope.Address is not null && scope.Address == revision.AnchorAddress);

  private async Task<ToolResult> ExtractAsync(DocxToolInput args, string path, CancellationToken ct)
  {
    Result<DocxSnapshot> read = await _docx.ReadAsync(path, 0, 1, ct).ConfigureAwait(false);
    if (!read.IsSuccess)
    {
      return Err(read.Error);
    }

    List<DocxMediaInfo> media = [.. read.Value.Media];
    List<ToolResultImage> extracted = [];
    bool visionAccepts = _vision?.AcceptsImages == true;
    if (visionAccepts)
    {
      Result<IReadOnlyList<ToolResultImage>> pull = await _docx
          .ExtractImagesAsync(path, args.ImageNames, ct).ConfigureAwait(false);
      if (!pull.IsSuccess)
      {
        return Err(pull.Error);
      }

      extracted = [.. pull.Value];
    }

    StringBuilder sb = new();
    int attachIndex = 0;
    foreach (DocxMediaInfo m in media)
    {
      bool isAttachableType = m.MediaType is PngMediaType or JpegMediaType;
      bool inFilter = args.ImageNames is null || args.ImageNames.Count == 0 || args.ImageNames.Contains(m.Name);
      if (!isAttachableType)
      {
        _ = sb.AppendLine(CultureInfo.InvariantCulture,
            $"{m.Name} {m.MediaType} {m.ByteCount} listed-only (ImageNotAttachable: PNG/JPEG only)");
      }
      else if (!visionAccepts)
      {
        _ = sb.AppendLine(CultureInfo.InvariantCulture,
            $"{m.Name} {m.MediaType} {m.ByteCount} listed-only (ImageNotAttachable: model has no image input)");
      }
      else if (inFilter && attachIndex < extracted.Count)
      {
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"{m.Name} {m.MediaType} {m.ByteCount} attached");
        attachIndex++;
      }
      else
      {
        _ = sb.AppendLine(CultureInfo.InvariantCulture,
            $"{m.Name} {m.MediaType} {m.ByteCount} listed-only (ImageNotAttachable: not in the requested names)");
      }
    }

    sb.Length -= Environment.NewLine.Length;
    return new ToolResult(sb.ToString(), false, Images: extracted.Count > 0 ? extracted : null);
  }

  private static ToolResult Ok(string content) => new(content, false);

  private static ToolResult Err(DomainError error) => new(
      $"Error [{error.Code}]: {error.Message}", true);
}
