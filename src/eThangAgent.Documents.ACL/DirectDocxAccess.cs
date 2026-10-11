using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;


// S3220/S3878 flag every OOXML element-constructor call below: the element trees
// are built exactly this way by design (a child element argument to the params
// ctor is the intended shape; collection expressions bind the same overloads).
// Named deviation, scoped to this file - mirrors TestDocxFactory.
#pragma warning disable S3220, S3878
namespace eThangAgent.Documents.ACL;

/// <summary>One resolved edit operation: the OpenXml element(s) it targets plus the
///     apply action, resolved against the document BEFORE any mutation (the atomic
///     contract). BlockKey deduplicates ChangedBlocks across the batch.</summary>
internal sealed record ResolvedEdit(string BlockKey, Action Apply);

/// <summary>The IDocxAccess implementation over DocumentFormat.OpenXml (task 5 the
///     read path, task 6 the edit path; later tasks replace their own
///     NotSupportedException placeholder - plan-mandated staging). Every method
///     stats the file FIRST and returns the content-true FileVersion; the sniffer
///     gates every open.</summary>
public sealed class DirectDocxAccess : IDocxAccess
{
  /// <summary>Reads one page of the flat body block list plus document metadata.</summary>
  public Task<Result<DocxSnapshot>> ReadAsync(string path, int offset, int maxBlocks, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(path);
    return Task.FromResult((offset < 0 || maxBlocks is < 1 or > DocxLimits.MaxBlocksPerPage)
      ? InvalidSnapshot(
        $"offset must be >= 0 and maxBlocks must be 1..{DocxLimits.MaxBlocksPerPage}; got offset {offset}, maxBlocks {maxBlocks}.")
      : ReadCore(path, offset, maxBlocks));
  }

  /// <summary>Applies an atomic edit batch (task 6): stat-check the version FIRST
  ///     (StaleDocument), sniff, open editable, resolve+validate ALL operations
  ///     against the open document BEFORE any mutation, mutate, save, re-stat.
  ///     ChangedBlocks counts DISTINCT addressed blocks (one block touched by two
  ///     operations counts once).</summary>
  public Task<Result<DocxEditOutcome>> EditAsync(string path, FileVersion expectedVersion, IReadOnlyList<DocxOperation> operations, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(path);
    ArgumentNullException.ThrowIfNull(expectedVersion);
    ArgumentNullException.ThrowIfNull(operations);
    return Task.FromResult(EditCore(path, expectedVersion, operations));
  }

  /// <summary>Creates a new .docx from the pinned markdown subset (task 8): the
  ///     file must not exist; the parser's violations translate to MarkdownInvalid
  ///     with the 1-based line number.</summary>
  public Task<Result<DocxSaveOutcome>> CreateAsync(string path, string markdown, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(path);
    ArgumentNullException.ThrowIfNull(markdown);
    if (File.Exists(path))
    {
      return Task.FromResult(FailSave(ToolErrorCodes.InvalidParameterValue,
        $"'{path}' already exists; create is for new paths (delete first is the model's explicit move)."));
    }

    string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
    if (dir is not null && !Directory.Exists(dir))
    {
      return Task.FromResult(FailSave(DocxErrorCodes.FileNotFound, $"Directory not found: '{dir}'."));
    }

    try
    {
      IReadOnlyList<DocxMarkdownBlock> blocks = DocxMarkdownParser.Parse(markdown);
      DocxDocumentBuilder.Build(path, blocks);
      return Task.FromResult(Result.Success(new DocxSaveOutcome(StatAfter(path))));
    }
    catch (MarkdownInvalidException ex)
    {
      return Task.FromResult(FailSave(DocxErrorCodes.MarkdownInvalid,
        ex.Message == "line 0" ? "The markdown violates the pinned subset (see the tool description)." : ex.Message));
    }
  }

  /// <summary>Lists the document's comments (task 9): id, author, text, and the
  ///     anchor address of the block carrying commentRangeStart.</summary>
  public Task<Result<IReadOnlyList<DocxCommentInfo>>> ListCommentsAsync(string path, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(path);
    return Task.FromResult(ListCommentsCore(path));
  }

  /// <summary>Adds a comment anchored at the addressed block (task 9, version-guarded):
  ///     appends the comments part when missing; the id is max-existing + 1; the
  ///     range wraps the target paragraph's content.</summary>
  public Task<Result<DocxSaveOutcome>> AddCommentAsync(string path, FileVersion expectedVersion, string address, string author, string text, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(path);
    ArgumentNullException.ThrowIfNull(expectedVersion);
    ArgumentNullException.ThrowIfNull(address);
    ArgumentNullException.ThrowIfNull(author);
    ArgumentNullException.ThrowIfNull(text);
    return Task.FromResult(MutateSave(path, expectedVersion, doc => AddComment(doc, address, author, text)));
  }

  /// <summary>Deletes one comment by id (task 9, version-guarded): the Comment
  ///     element, its range markers, and any reference run with that id.</summary>
  public Task<Result<DocxSaveOutcome>> DeleteCommentAsync(string path, FileVersion expectedVersion, int commentId, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(path);
    ArgumentNullException.ThrowIfNull(expectedVersion);
    return Task.FromResult(MutateSave(path, expectedVersion, doc => DeleteComment(doc, commentId)));
  }

  public Task<Result<IReadOnlyList<ToolResultImage>>> ExtractImagesAsync(string path, IReadOnlyList<string>? names, CancellationToken ct = default)
    => throw new NotSupportedException("ExtractImagesAsync is implemented by task 11 (extract images).");

  private static Result<DocxSnapshot> ReadCore(string path, int offset, int maxBlocks)
  {
    if (!File.Exists(path))
    {
      return FailSnapshot(DocxErrorCodes.FileNotFound, $"File not found: '{path}'.");
    }

    FileInfo info = new(path);
    FileVersion version = new(info.Length, info.LastWriteTimeUtc);
    Result<string> sniff = DocxFormatSniffer.Inspect(path);
    if (!sniff.IsSuccess)
    {
      return Result.Failure<DocxSnapshot>(sniff.Error);
    }

    try
    {
      using WordprocessingDocument doc = WordprocessingDocument.Open(path, false, new OpenSettings
      {
        MaxCharactersInPart = DocxLimits.MaxCharactersInPart,
      });
      return doc.MainDocumentPart?.Document?.Body is null
        ? FailSnapshot(DocxErrorCodes.CorruptDocument, $"'{path}' has no readable document body.")
        : Result.Success(DocxBlockWalker.Walk(doc, version, offset, maxBlocks));
    }
    catch (OpenXmlPackageException ex)
    {
      return FailSnapshot(DocxErrorCodes.CorruptDocument, $"'{path}' could not be opened as a Word package: {Clamp(ex.Message)}");
    }
    catch (IOException ex)
    {
      return FailSnapshot(DocxErrorCodes.CorruptDocument, $"'{path}' could not be opened as a Word package: {Clamp(ex.Message)}");
    }
  }

  private static Result<DocxEditOutcome> EditCore(string path, FileVersion expectedVersion, IReadOnlyList<DocxOperation> operations)
  {
    if (operations.Count == 0)
    {
      return FailEdit(ToolErrorCodes.InvalidParameterValue, "The operations list must not be empty.");
    }

    if (operations.Count > DocxLimits.MaxOperationsPerEdit)
    {
      return FailEdit(ToolErrorCodes.InvalidParameterValue, $"The operations list exceeds the {DocxLimits.MaxOperationsPerEdit}-operation cap.");
    }

    if (!File.Exists(path))
    {
      return FailEdit(DocxErrorCodes.FileNotFound, $"File not found: '{path}'.");
    }

    FileInfo info = new(path);
    FileVersion current = new(info.Length, info.LastWriteTimeUtc);
    if (current != expectedVersion)
    {
      return FailEdit(DocxErrorCodes.StaleDocument,
        $"The document changed since it was read (expected {expectedVersion.Token}, current {current.Token}); re-read and retry.");
    }

    Result<string> sniff = DocxFormatSniffer.Inspect(path);
    if (!sniff.IsSuccess)
    {
      return Result.Failure<DocxEditOutcome>(sniff.Error);
    }

    try
    {
      using WordprocessingDocument doc = WordprocessingDocument.Open(path, true, new OpenSettings
      {
        MaxCharactersInPart = DocxLimits.MaxCharactersInPart,
      });
      if (doc.MainDocumentPart?.Document?.Body is null)
      {
        return FailEdit(DocxErrorCodes.CorruptDocument, $"'{path}' has no readable document body.");
      }

      List<ResolvedEdit> resolved = [];
      foreach (DocxOperation op in operations)
      {
        Result<IReadOnlyList<ResolvedEdit>> one = ResolveEdit(doc, op);
        if (!one.IsSuccess)
        {
          return Result.Failure<DocxEditOutcome>(one.Error); // atomic: nothing mutated yet
        }

        resolved.AddRange(one.Value);
      }

      HashSet<string> touched = [];
      foreach (ResolvedEdit edit in resolved)
      {
        edit.Apply();
        _ = touched.Add(edit.BlockKey);
      }

      if (touched.Count == 0)
      {
        return Result.Success(new DocxEditOutcome(expectedVersion, 0)); // pinned: zero matches skip the save
      }

      doc.Save();
      return Result.Success(new DocxEditOutcome(StatAfter(path), touched.Count));
    }
    catch (OpenXmlPackageException ex)
    {
      return FailEdit(DocxErrorCodes.CorruptDocument, $"'{path}' could not be opened as a Word package: {Clamp(ex.Message)}");
    }
    catch (IOException ex)
    {
      return FailEdit(DocxErrorCodes.CorruptDocument, $"'{path}' could not be opened as a Word package: {Clamp(ex.Message)}");
    }
  }

  private static FileVersion StatAfter(string path)
  {
    FileInfo after = new(path);
    return new FileVersion(after.Length, after.LastWriteTimeUtc);
  }

  private static Result<IReadOnlyList<ResolvedEdit>> ResolveEdit(WordprocessingDocument doc, DocxOperation op)
  {
    MainDocumentPart main = doc.MainDocumentPart ?? throw new InvalidOperationException("package has no main document part");
    Body body = main.Document.Body ?? throw new InvalidOperationException("document has no body");
    return op switch
    {
      ReplaceParagraphText text => ResolveParagraphOp(body, text.Address, "p", para => ReplaceText(para, text.Text)),
      SetParagraphStyle style => ResolveStyleOp(main, body, style),
      InsertParagraph insert => ResolveInsertParagraph(main, body, insert),
      DeleteParagraph delete => ResolveParagraphOp(body, delete.Address, "p", DeleteParagraphOp),
      SetCellText cell => ResolveCellOp(body, cell),
      InsertTableRow row => ResolveInsertRow(body, row),
      DeleteTableRow row => ResolveDeleteRow(body, row),
      ApplyRunFormatting format => ResolveFormattingOp(body, format),
      FindReplace replace => ResolveFindReplace(body, replace),
      _ => FailResolved(ToolErrorCodes.InvalidParameterValue, $"Unknown edit operation '{op.GetType().Name}'."),
    };
  }

  /// <summary>Resolves a body-paragraph operation: the address must parse to the
  ///     Paragraph kind and resolve to a Paragraph element.</summary>
  private static Result<IReadOnlyList<ResolvedEdit>> ResolveParagraphOp(Body body, string address, string keyPrefix, Action<Paragraph> apply)
  {
    Result<DocxAddress> parsed = DocxAddress.Parse(address);
    if (!parsed.IsSuccess)
    {
      return FailResolved(parsed.Error.Code, parsed.Error.Message);
    }

    if (parsed.Value.Kind != DocxAddressKind.Paragraph)
    {
      return FailResolved(ToolErrorCodes.InvalidParameterValue,
        $"Address '{address}' does not address a body paragraph (kind {parsed.Value.Kind}); header/footer paragraphs are separate layers, and cells are addressed through setCellText.");
    }

    if (TryBodyBlock(body, parsed.Value.BodyIndex) is not Paragraph para)
    {
      return FailResolved(DocxErrorCodes.AddressNotFound,
        $"Address '{address}' does not resolve to a body paragraph; {RangeHint(body)}.");
    }

    string key = keyPrefix + parsed.Value.BodyIndex;
    return Single(new ResolvedEdit(key, () => apply(para)));
  }

  private static Result<IReadOnlyList<ResolvedEdit>> ResolveStyleOp(MainDocumentPart main, Body body, SetParagraphStyle op)
  {
    Result<DocxAddress> parsed = DocxAddress.Parse(op.Address);
    if (!parsed.IsSuccess)
    {
      return FailResolved(parsed.Error.Code, parsed.Error.Message);
    }

    if (parsed.Value.Kind != DocxAddressKind.Paragraph)
    {
      return FailResolved(ToolErrorCodes.InvalidParameterValue,
        $"Address '{op.Address}' does not address a body paragraph (kind {parsed.Value.Kind}).");
    }

    if (TryBodyBlock(body, parsed.Value.BodyIndex) is not Paragraph para)
    {
      return FailResolved(DocxErrorCodes.AddressNotFound,
        $"Address '{op.Address}' does not resolve to a body paragraph; {RangeHint(body)}.");
    }

    Result<string?> styleId = ResolveStyleId(main, op.StyleName);
    if (!styleId.IsSuccess)
    {
      return Result.Failure<IReadOnlyList<ResolvedEdit>>(styleId.Error);
    }

    string? id = styleId.Value;
    return Single(new ResolvedEdit("p" + parsed.Value.BodyIndex, () => ApplyStyle(para, id)));
  }

  private static Result<IReadOnlyList<ResolvedEdit>> ResolveInsertParagraph(MainDocumentPart main, Body body, InsertParagraph op)
  {
    Result<string?> styleId = op.Style is null ? Result.Success((string?)null) : ResolveStyleId(main, op.Style);
    if (!styleId.IsSuccess)
    {
      return Result.Failure<IReadOnlyList<ResolvedEdit>>(styleId.Error);
    }

    string? id = styleId.Value;
    if (op.AfterAddress == "p0")
    {
      return Single(new ResolvedEdit("p0", () =>
      {
        Paragraph para = BuildParagraph(op.Text, id);
        _ = body.InsertAt(para, 0);
      }));
    }

    Result<DocxAddress> parsed = DocxAddress.Parse(op.AfterAddress);
    if (!parsed.IsSuccess)
    {
      return FailResolved(parsed.Error.Code, parsed.Error.Message);
    }

    if (parsed.Value.Kind != DocxAddressKind.Paragraph)
    {
      return FailResolved(ToolErrorCodes.InvalidParameterValue,
        $"Address '{op.AfterAddress}' does not address a body paragraph (kind {parsed.Value.Kind}).");
    }

    if (TryBodyBlock(body, parsed.Value.BodyIndex) is not Paragraph para)
    {
      return FailResolved(DocxErrorCodes.AddressNotFound,
        $"Address '{op.AfterAddress}' does not resolve to a body paragraph; {RangeHint(body)}.");
    }

    string key = "p" + parsed.Value.BodyIndex;
    return Single(new ResolvedEdit(key,
      () => body.InsertAfter(BuildParagraph(op.Text, id), para)));
  }

  private static Result<IReadOnlyList<ResolvedEdit>> ResolveCellOp(Body body, SetCellText op)
  {
    Result<DocxAddress> parsed = DocxAddress.Parse(op.Address);
    if (!parsed.IsSuccess)
    {
      return FailResolved(parsed.Error.Code, parsed.Error.Message);
    }

    if (parsed.Value.Kind != DocxAddressKind.TableCell)
    {
      return FailResolved(ToolErrorCodes.InvalidParameterValue,
        $"Address '{op.Address}' does not address a table cell (kind {parsed.Value.Kind}); cells address as t<N>.r<R>.c<C>.");
    }

    if (TryBodyBlock(body, parsed.Value.BodyIndex) is not Table table)
    {
      return FailResolved(DocxErrorCodes.AddressNotFound,
        $"Address '{op.Address}' does not resolve to a body table; {RangeHint(body)}.");
    }

    List<TableRow> rows = [.. table.Elements<TableRow>()];
    if (parsed.Value.Row < 1 || parsed.Value.Row > rows.Count)
    {
      return FailResolved(DocxErrorCodes.AddressNotFound,
        $"Address '{op.Address}': row {parsed.Value.Row} is outside the table's 1..{rows.Count} rows.");
    }

    List<TableCell> cells = [.. rows[parsed.Value.Row - 1].Elements<TableCell>()];
    if (parsed.Value.Col < 1 || parsed.Value.Col > cells.Count)
    {
      return FailResolved(DocxErrorCodes.AddressNotFound,
        $"Address '{op.Address}': column {parsed.Value.Col} is outside the row's 1..{cells.Count} cells.");
    }

    TableCell target = cells[parsed.Value.Col - 1];
    return Single(new ResolvedEdit(
      $"t{parsed.Value.BodyIndex}.r{parsed.Value.Row}.c{parsed.Value.Col}", () => ReplaceCellText(target, op.Text)));
  }

  private static Result<IReadOnlyList<ResolvedEdit>> ResolveInsertRow(Body body, InsertTableRow op)
  {
    Result<DocxAddress> parsed = DocxAddress.Parse(op.TableAddress);
    if (!parsed.IsSuccess)
    {
      return FailResolved(parsed.Error.Code, parsed.Error.Message);
    }

    if (parsed.Value.Kind != DocxAddressKind.Table)
    {
      return FailResolved(ToolErrorCodes.InvalidParameterValue,
        $"Address '{op.TableAddress}' does not address a body table (kind {parsed.Value.Kind}).");
    }

    if (TryBodyBlock(body, parsed.Value.BodyIndex) is not Table table)
    {
      return FailResolved(DocxErrorCodes.AddressNotFound,
        $"Address '{op.TableAddress}' does not resolve to a body table; {RangeHint(body)}.");
    }

    List<TableRow> rows = [.. table.Elements<TableRow>()];
    int count = rows.Count;
    if (op.AfterRowIndex < 0 || op.AfterRowIndex > count)
    {
      return FailResolved(DocxErrorCodes.AddressNotFound,
        $"Address '{op.TableAddress}': afterRow {op.AfterRowIndex} is outside the table's 0..{rows.Count} insertion points.");
    }

    TableRow newRow = BuildRowLike(rows[^1]);
    string key = "t" + parsed.Value.BodyIndex;
    return Single(new ResolvedEdit(key, () => InsertRowAt(table, rows, newRow, op.AfterRowIndex)));
  }

  private static Result<IReadOnlyList<ResolvedEdit>> ResolveDeleteRow(Body body, DeleteTableRow op)
  {
    Result<DocxAddress> parsed = DocxAddress.Parse(op.TableAddress);
    if (!parsed.IsSuccess)
    {
      return FailResolved(parsed.Error.Code, parsed.Error.Message);
    }

    if (parsed.Value.Kind != DocxAddressKind.Table)
    {
      return FailResolved(ToolErrorCodes.InvalidParameterValue,
        $"Address '{op.TableAddress}' does not address a body table (kind {parsed.Value.Kind}).");
    }

    if (TryBodyBlock(body, parsed.Value.BodyIndex) is not Table table)
    {
      return FailResolved(DocxErrorCodes.AddressNotFound,
        $"Address '{op.TableAddress}' does not resolve to a body table; {RangeHint(body)}.");
    }

    List<TableRow> rows = [.. table.Elements<TableRow>()];
    if (op.RowIndex < 1 || op.RowIndex > rows.Count)
    {
      return FailResolved(DocxErrorCodes.AddressNotFound,
        $"Address '{op.TableAddress}': row {op.RowIndex} is outside the table's 1..{rows.Count} rows.");
    }

    TableRow victim = rows[op.RowIndex - 1];
    return Single(new ResolvedEdit("t" + parsed.Value.BodyIndex, victim.Remove));
  }

  /// <summary>The nth block of the shared flat body numbering (1-based): the
  ///     Paragraph or Table element at that position, or null.</summary>
  private static OpenXmlElement? TryBodyBlock(Body body, int index)
    => body.ChildElements.Where(child => child is Paragraph or Table)
      .ElementAtOrDefault(index - 1);

  private static string RangeHint(Body body)
  {
    int count = body.ChildElements.Count(el => el is Paragraph or Table);
    return count == 0 ? "the body has no blocks" : $"the body has block 1..{count}";
  }

  /// <summary>Resolves a style NAME (what read surfaces) to its style id; an unknown
  ///     name fails StyleNotFound listing the available paragraph style names.</summary>
  private static Result<string?> ResolveStyleId(MainDocumentPart main, string styleName)
  {
    Styles? styles = main.StyleDefinitionsPart?.Styles;
    if (styles is null)
    {
      return Result.Failure<string?>(new DomainError(DocxErrorCodes.StyleNotFound,
        $"Style '{styleName}' was not found; the document has no styles part."));
    }

    List<Style> paragraphStyles = [.. styles.Elements<Style>().Where(s => s.Type is null || s.Type == StyleValues.Paragraph)];
    Style? match = paragraphStyles.FirstOrDefault(s => string.Equals(s.StyleName?.Val, styleName, StringComparison.Ordinal))
      ?? paragraphStyles.FirstOrDefault(s => string.Equals(s.StyleId?.Value, styleName, StringComparison.Ordinal));
    if (match is null)
    {
      List<string> available = [.. paragraphStyles
        .Select(s => s.StyleName?.Val?.Value ?? s.StyleId?.Value ?? "(unnamed)")
        .Distinct()];
      return Result.Failure<string?>(new DomainError(DocxErrorCodes.StyleNotFound,
        $"Style '{styleName}' was not found; available styles: {(available.Count == 0 ? "(none)" : string.Join(", ", available))}."));
    }

    return Result.Success(match.StyleId?.Value);
  }

  private static void ApplyStyle(Paragraph para, string? styleId)
  {
    ParagraphProperties properties = para.ParagraphProperties ?? new ParagraphProperties();
    if (para.ParagraphProperties is null)
    {
      _ = para.InsertAt(properties, 0);
    }

    properties.ParagraphStyleId = new ParagraphStyleId { Val = styleId };
  }

  /// <summary>Replaces a paragraph's text: the style is kept, the FIRST run's
  ///     formatting applies to the new text, run-level variance is lost (named
  ///     decisions). Comment range markers and bookmarks survive.</summary>
  private static void ReplaceText(Paragraph para, string text)
  {
    RunProperties? firstFormat = para.Descendants<Run>().FirstOrDefault()?.RunProperties?.CloneNode(true) as RunProperties;
    foreach (Run run in para.Elements<Run>().ToList())
    {
      run.Remove();
    }

    foreach (Hyperlink link in para.Elements<Hyperlink>().ToList())
    {
      link.Remove();
    }

    RunProperties? format = firstFormat;
    Run replacement = format is null
      ? new Run([new Text(text) { Space = SpaceProcessingModeValues.Preserve }])
      : new Run([format, new Text(text) { Space = SpaceProcessingModeValues.Preserve }]);
    para.Append(replacement);
  }

  private static void DeleteParagraphOp(Paragraph para) => para.Remove();

  /// <summary>Sets a cell's text: the first paragraph gets the replace treatment,
  ///     extra paragraphs are removed (a cell renders as one text block).</summary>
  private static void ReplaceCellText(TableCell cell, string text)
  {
    List<Paragraph> paragraphs = [.. cell.Elements<Paragraph>()];
    if (paragraphs.Count == 0)
    {
      cell.Append(new Paragraph([new Run([new Text(text) { Space = SpaceProcessingModeValues.Preserve }])]));
      return;
    }

    ReplaceText(paragraphs[0], text);
    foreach (Paragraph extra in paragraphs.Skip(1))
    {
      extra.Remove();
    }
  }

  private static Paragraph BuildParagraph(string text, string? styleId)
  {
    Paragraph para = new([new Run([new Text(text) { Space = SpaceProcessingModeValues.Preserve }])]);
    if (styleId is not null)
    {
      _ = para.InsertAt(new ParagraphProperties([new ParagraphStyleId { Val = styleId }]), 0);
    }

    return para;
  }

  /// <summary>Inserts a built row: before the first row at index 0, else after the
  ///     addressed row (the single-ternary shape keeps S3358 quiet).</summary>
  private static void InsertRowAt(Table table, List<TableRow> rows, TableRow newRow, int afterRowIndex)
    => _ = afterRowIndex == 0 ? table.PrependChild(newRow) : table.InsertAfter(newRow, rows[afterRowIndex - 1]);

  /// <summary>A new row shaped like the table's last row: one empty-text cell per
  ///     column (the pinned clone-last-row rule, without cloning content).</summary>
  private static TableRow BuildRowLike(TableRow template)
  {
    int cols = template.Elements<TableCell>().Count();
    List<TableCell> cells = [];
    for (int i = 0; i < cols; i++)
    {
      cells.Add(new TableCell([new Paragraph([new Run([new Text(string.Empty) { Space = SpaceProcessingModeValues.Preserve }])])]));
    }

    return new TableRow([.. cells]);
  }

  /// <summary>Resolves run formatting (task 7): the address must be a body
  ///     paragraph; the format applies to EVERY run in it (paragraph-granular).</summary>
  private static Result<IReadOnlyList<ResolvedEdit>> ResolveFormattingOp(Body body, ApplyRunFormatting op)
  {
    Result<DocxAddress> parsed = DocxAddress.Parse(op.Address);
    if (!parsed.IsSuccess)
    {
      return FailResolved(parsed.Error.Code, parsed.Error.Message);
    }

    if (parsed.Value.Kind != DocxAddressKind.Paragraph)
    {
      return FailResolved(ToolErrorCodes.InvalidParameterValue,
        $"Address '{op.Address}' does not address a body paragraph (kind {parsed.Value.Kind}).");
    }

    if (TryBodyBlock(body, parsed.Value.BodyIndex) is not Paragraph para)
    {
      return FailResolved(DocxErrorCodes.AddressNotFound,
        $"Address '{op.Address}' does not resolve to a body paragraph; {RangeHint(body)}.");
    }

    string key = "p" + parsed.Value.BodyIndex;
    return Single(new ResolvedEdit(key, () => ApplyRunFormat(para, op.Format)));
  }

  /// <summary>Applies the non-null format members to every run in the paragraph;
  ///     runs without properties get one; null members stay untouched.</summary>
  private static void ApplyRunFormat(Paragraph para, DocxRunFormat format)
  {
    foreach (Run run in para.Descendants<Run>().ToList())
    {
      RunProperties properties = run.RunProperties ?? new RunProperties();
      if (run.RunProperties is null)
      {
        _ = run.InsertAt(properties, 0);
      }

      if (format.Bold is not null)
      {
        properties.Bold = format.Bold.Value ? new Bold() : new Bold { Val = false };
      }

      if (format.Italic is not null)
      {
        properties.Italic = format.Italic.Value ? new Italic() : new Italic { Val = false };
      }

      if (format.Underline is not null)
      {
        properties.Underline = format.Underline.Value
          ? new Underline()
          : new Underline { Val = UnderlineValues.None };
      }

      if (format.FontSizeHalfPoints is not null)
      {
        properties.FontSize = new FontSize { Val = format.FontSizeHalfPoints.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) };
      }

      if (format.ColorHex is not null)
      {
        properties.Color = new Color { Val = format.ColorHex };
      }
    }
  }

  /// <summary>Resolves find/replace (task 7): literal, case-sensitive, body
  ///     paragraphs only; zero matches skips the save entirely (pinned decision -
  ///     the version stays unchanged).</summary>
  private static Result<IReadOnlyList<ResolvedEdit>> ResolveFindReplace(Body body, FindReplace op)
  {
    if (op.Find.Length == 0)
    {
      return FailResolved(ToolErrorCodes.InvalidParameterValue, "The find text must not be empty.");
    }

    // one resolved edit per matched paragraph (keyed by its flat body position), so
    // ChangedBlocks counts the paragraphs the find/replace touched; zero matches
    // yields no edits at all and the save is skipped (pinned decision)
    List<OpenXmlElement> flat = [.. body.ChildElements];
    List<ResolvedEdit> edits = [.. body.Elements<Paragraph>()
      .Where(para => ParagraphText(para).Contains(op.Find, StringComparison.Ordinal))
      .Select(para => new ResolvedEdit("p" + (flat.IndexOf(para) + 1), () => ApplyFindReplace(para, op)))];
    return Result.Success<IReadOnlyList<ResolvedEdit>>(edits);
  }

  private static string ParagraphText(Paragraph para)
    => string.Concat(para.Descendants<Text>().Select(t => t.Text));

  /// <summary>Rebuilds each matched paragraph's run list: segments outside matches
  ///     keep their run formatting; each match's replacement carries the FIRST
  ///     matched run's cloned formatting (named decision).</summary>
  private static void ApplyFindReplace(Paragraph para, FindReplace op)
  {
    List<Run> runs = [.. para.Elements<Run>().Where(run => run.Elements<Text>().Any())];
    string full = string.Concat(runs.Select(run => string.Concat(run.Elements<Text>().Select(t => t.Text))));
    List<(int Start, int End)> matches = [];
    int at = 0;
    while ((at = full.IndexOf(op.Find, at, StringComparison.Ordinal)) >= 0)
    {
      matches.Add((at, at + op.Find.Length));
      at += op.Find.Length;
    }

    List<(string Text, RunProperties? Format)> segments = [];
    int cursor = 0;
    foreach ((int start, int end) in matches)
    {
      if (start > cursor)
      {
        segments.Add((full[cursor..start], FormatAt(runs, cursor)));
      }

      segments.Add((op.Replace, FormatAt(runs, start)));
      cursor = end;
    }

    if (cursor < full.Length)
    {
      segments.Add((full[cursor..], FormatAt(runs, cursor)));
    }

    foreach (Run run in runs)
    {
      run.Remove();
    }

    foreach ((string text, RunProperties? format) in segments)
    {
      Run replacement = format is null
        ? new Run([new Text(text) { Space = SpaceProcessingModeValues.Preserve }])
        : new Run([format.CloneNode(true) as RunProperties ?? format, new Text(text) { Space = SpaceProcessingModeValues.Preserve }]);
      para.Append(replacement);
    }
  }

  /// <summary>The formatting of the run covering the concatenated-text offset.</summary>
  private static RunProperties? FormatAt(List<Run> runs, int offset)
  {
    int at = 0;
    foreach (Run run in runs)
    {
      int length = run.Elements<Text>().Sum(t => t.Text.Length);
      if (offset < at + length)
      {
        return run.RunProperties;
      }

      at += length;
    }

    return runs.Count > 0 ? runs[^1].RunProperties : null;
  }

  /// <summary>The shared mutation shape for comments/revisions (task 9+): stat
  ///     check, sniff, open editable, mutate through the delegate, save, re-stat.
  ///     A delegate failure refuses the mutation (nothing saved).</summary>
  private static Result<DocxSaveOutcome> MutateSave(string path, FileVersion expectedVersion, Func<WordprocessingDocument, Result<bool>> mutate)
  {
    if (!File.Exists(path))
    {
      return FailSave(DocxErrorCodes.FileNotFound, $"File not found: '{path}'.");
    }

    FileInfo info = new(path);
    FileVersion current = new(info.Length, info.LastWriteTimeUtc);
    if (current != expectedVersion)
    {
      return FailSave(DocxErrorCodes.StaleDocument,
        $"The document changed since it was read (expected {expectedVersion.Token}, current {current.Token}); re-read and retry.");
    }

    Result<string> sniff = DocxFormatSniffer.Inspect(path);
    if (!sniff.IsSuccess)
    {
      return Result.Failure<DocxSaveOutcome>(sniff.Error);
    }

    try
    {
      using WordprocessingDocument doc = WordprocessingDocument.Open(path, true, new OpenSettings
      {
        MaxCharactersInPart = DocxLimits.MaxCharactersInPart,
      });
      Result<bool> outcome = mutate(doc);
      if (!outcome.IsSuccess)
      {
        return Result.Failure<DocxSaveOutcome>(outcome.Error);
      }

      doc.Save();
      return Result.Success(new DocxSaveOutcome(StatAfter(path)));
    }
    catch (OpenXmlPackageException ex)
    {
      return FailSave(DocxErrorCodes.CorruptDocument, $"'{path}' could not be opened as a Word package: {Clamp(ex.Message)}");
    }
    catch (IOException ex)
    {
      return FailSave(DocxErrorCodes.CorruptDocument, $"'{path}' could not be opened as a Word package: {Clamp(ex.Message)}");
    }
  }

  private static Result<IReadOnlyList<DocxCommentInfo>> ListCommentsCore(string path)
  {
    if (!File.Exists(path))
    {
      return Result.Failure<IReadOnlyList<DocxCommentInfo>>(new DomainError(DocxErrorCodes.FileNotFound, $"File not found: '{path}'."));
    }

    Result<string> sniff = DocxFormatSniffer.Inspect(path);
    if (!sniff.IsSuccess)
    {
      return Result.Failure<IReadOnlyList<DocxCommentInfo>>(sniff.Error);
    }

    try
    {
      using WordprocessingDocument doc = WordprocessingDocument.Open(path, false, new OpenSettings
      {
        MaxCharactersInPart = DocxLimits.MaxCharactersInPart,
      });
      DocxMap map = DocxElementMap.Build(doc);
      List<DocxCommentInfo> comments = [];
      Comments? root = doc.MainDocumentPart?.WordprocessingCommentsPart?.Comments;
      if (root is not null)
      {
        foreach (Comment comment in root.Elements<Comment>())
        {
          int id = ParseCommentId(comment.Id?.Value);
          string author = comment.Author?.Value ?? string.Empty;
          string text = string.Concat(comment.Descendants<Text>().Select(t => t.Text));
          string anchor = AnchorOf(doc, map, id);
          comments.Add(new DocxCommentInfo(id, author, text, anchor));
        }
      }

      return Result.Success<IReadOnlyList<DocxCommentInfo>>(comments);
    }
    catch (OpenXmlPackageException ex)
    {
      return Result.Failure<IReadOnlyList<DocxCommentInfo>>(new DomainError(DocxErrorCodes.CorruptDocument,
        $"'{path}' could not be opened as a Word package: {Clamp(ex.Message)}"));
    }
    catch (IOException ex)
    {
      return Result.Failure<IReadOnlyList<DocxCommentInfo>>(new DomainError(DocxErrorCodes.CorruptDocument,
        $"'{path}' could not be opened as a Word package: {Clamp(ex.Message)}"));
    }
  }

  /// <summary>The anchor address of the comment's commentRangeStart: the first
  ///     mappable ancestor (paragraph, cell, header/footer paragraph).</summary>
  private static string AnchorOf(WordprocessingDocument doc, DocxMap map, int commentId)
  {
    string idText = commentId.ToString(System.Globalization.CultureInfo.InvariantCulture);
    MainDocumentPart? main = doc.MainDocumentPart;
    CommentRangeStart? marker = main?.Document?.Descendants<CommentRangeStart>()
      .FirstOrDefault(m => m.Id?.Value == idText)
      ?? main?.HeaderParts.SelectMany(h => h.Header.Descendants<CommentRangeStart>()).FirstOrDefault(m => m.Id?.Value == idText)
      ?? main?.FooterParts.SelectMany(fo => fo.Footer.Descendants<CommentRangeStart>()).FirstOrDefault(m => m.Id?.Value == idText);
    if (marker is null)
    {
      return string.Empty;
    }

    for (OpenXmlElement? at = marker; at is not null; at = at.Parent)
    {
      if (at is Paragraph or TableCell)
      {
        string? address = DocxElementMap.AddressOf(map, at);
        if (address is not null)
        {
          return address;
        }
      }
    }

    return string.Empty;
  }

  private static int ParseCommentId(string? id)
    => int.TryParse(id, System.Globalization.CultureInfo.InvariantCulture, out int value) ? value : 0;

  private static Result<bool> AddComment(WordprocessingDocument doc, string address, string author, string text)
  {
    MainDocumentPart main = doc.MainDocumentPart ?? throw new InvalidOperationException("package has no main document part");
    DocxMap map = DocxElementMap.Build(doc);
    OpenXmlElement? target = ResolveAddressElement(map, address);
    if (target is null)
    {
      return Result.Failure<bool>(new DomainError(DocxErrorCodes.AddressNotFound,
        $"Address '{address}' does not resolve to a block."));
    }

    if (target is Table)
    {
      return Result.Failure<bool>(new DomainError(ToolErrorCodes.InvalidParameterValue,
        $"Address '{address}' addresses a whole table; anchor a paragraph or a cell."));
    }

    Paragraph anchorPara = target switch
    {
      Paragraph para => para,
      TableCell cell => cell.Elements<Paragraph>().FirstOrDefault() ?? throw new InvalidOperationException("cell has no paragraph"),
      _ => throw new InvalidOperationException($"unexpected anchor element '{target.GetType().Name}'"),
    };

    WordprocessingCommentsPart commentsPart = main.WordprocessingCommentsPart ?? main.AddNewPart<WordprocessingCommentsPart>();
    commentsPart.Comments ??= new Comments();
    int nextId = commentsPart.Comments.Elements<Comment>()
      .Select(c => ParseCommentId(c.Id?.Value))
      .DefaultIfEmpty(0)
      .Max() + 1;
    string idText = nextId.ToString(System.Globalization.CultureInfo.InvariantCulture);
    commentsPart.Comments.Append(new Comment([new Paragraph([new Run([new Text(text) { Space = SpaceProcessingModeValues.Preserve }])])])
    {
      Id = idText,
      Author = author,
      Date = DateTime.UtcNow,
    });
    _ = anchorPara.InsertAt(new CommentRangeStart { Id = idText }, 0);
    anchorPara.Append(new CommentRangeEnd { Id = idText });
    anchorPara.Append(new Run([new CommentReference { Id = idText }]));
    return Result.Success(true);
  }

  private static Result<bool> DeleteComment(WordprocessingDocument doc, int commentId)
  {
    MainDocumentPart main = doc.MainDocumentPart ?? throw new InvalidOperationException("package has no main document part");
    Comments? root = main.WordprocessingCommentsPart?.Comments;
    string idText = commentId.ToString(System.Globalization.CultureInfo.InvariantCulture);
    Comment? comment = root?.Elements<Comment>().FirstOrDefault(c => c.Id?.Value == idText);
    if (comment is null)
    {
      return Result.Failure<bool>(new DomainError(DocxErrorCodes.CommentNotFound, $"No comment with id {commentId}."));
    }

    comment.Remove();
    foreach (CommentRangeStart start in main.Document.Descendants<CommentRangeStart>().Where(m => m.Id?.Value == idText).ToList())
    {
      start.Remove();
    }

    foreach (CommentRangeEnd end in main.Document.Descendants<CommentRangeEnd>().Where(m => m.Id?.Value == idText).ToList())
    {
      end.Remove();
    }

    foreach (CommentReference reference in main.Document.Descendants<CommentReference>().Where(r => r.Id?.Value == idText).ToList())
    {
      OpenXmlElement parent = reference.Parent ?? throw new InvalidOperationException("orphan comment reference");
      if (parent is Run run && run.Elements<CommentReference>().Any() && !run.Elements<Text>().Any())
      {
        run.Remove(); // the reference-only wrapper run goes with it
      }
      else
      {
        reference.Remove();
      }
    }

    return Result.Success(true);
  }

  /// <summary>Resolves an address to its ELEMENT through the map: body block,
  ///     header/footer paragraph, or table cell.</summary>
  private static OpenXmlElement? ResolveAddressElement(DocxMap map, string address)
  {
    Result<DocxAddress> parsed = DocxAddress.Parse(address);
    if (!parsed.IsSuccess)
    {
      return null;
    }

    DocxAddress a = parsed.Value;
    return a.Kind switch
    {
      DocxAddressKind.Paragraph when a.BodyIndex >= 1 && a.BodyIndex <= map.BodyBlocks.Count
        => map.BodyBlocks[a.BodyIndex - 1] is Paragraph ? map.BodyBlocks[a.BodyIndex - 1] : null,
      DocxAddressKind.Table when a.BodyIndex >= 1 && a.BodyIndex <= map.BodyBlocks.Count
        => map.BodyBlocks[a.BodyIndex - 1] is Table ? map.BodyBlocks[a.BodyIndex - 1] : null,
      DocxAddressKind.TableCell when a.BodyIndex >= 1 && a.BodyIndex <= map.BodyBlocks.Count
        => map.BodyBlocks[a.BodyIndex - 1] is Table table ? CellElement(table, a.Row, a.Col) : null,
      DocxAddressKind.HeaderParagraph or DocxAddressKind.FooterParagraph
        => map.ExtraParagraphs.FirstOrDefault(e => e.Address == address).Element,
      _ => null,
    };
  }

  private static TableCell? CellElement(Table table, int row, int col)
  {
    List<TableRow> rows = [.. table.Elements<TableRow>()];
    if (row < 1 || row > rows.Count)
    {
      return null;
    }

    List<TableCell> cells = [.. rows[row - 1].Elements<TableCell>()];
    return col >= 1 && col <= cells.Count ? cells[col - 1] : null;
  }

  /// <summary>Lists the document's tracked changes (task 10): document order,
  ///     author, type (Insertion|Deletion|Formatting), and the anchor address.</summary>
  public Task<Result<IReadOnlyList<DocxRevisionInfo>>> ListRevisionsAsync(string path, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(path);
    return Task.FromResult(ListRevisionsCore(path));
  }

  /// <summary>Accepts or rejects tracked changes matching the scope (task 10,
  ///     version-guarded): accept unwraps insertions and removes deletions; reject
  ///     removes insertions and unwraps deletions (delText converts to text).</summary>
  public Task<Result<DocxSaveOutcome>> ResolveRevisionsAsync(string path, FileVersion expectedVersion, bool accept, DocxRevisionScope scope, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(path);
    ArgumentNullException.ThrowIfNull(expectedVersion);
    ArgumentNullException.ThrowIfNull(scope);
    return Task.FromResult(MutateSave(path, expectedVersion, doc => ResolveRevisions(doc, accept, scope)));
  }

  private static Result<IReadOnlyList<DocxRevisionInfo>> ListRevisionsCore(string path)
  {
    if (!File.Exists(path))
    {
      return Result.Failure<IReadOnlyList<DocxRevisionInfo>>(new DomainError(DocxErrorCodes.FileNotFound, $"File not found: '{path}'."));
    }

    Result<string> sniff = DocxFormatSniffer.Inspect(path);
    if (!sniff.IsSuccess)
    {
      return Result.Failure<IReadOnlyList<DocxRevisionInfo>>(sniff.Error);
    }

    try
    {
      using WordprocessingDocument doc = WordprocessingDocument.Open(path, false, new OpenSettings
      {
        MaxCharactersInPart = DocxLimits.MaxCharactersInPart,
      });
      DocxMap map = DocxElementMap.Build(doc);
      List<DocxRevisionInfo> revisions = [];
      int index = 0;
      foreach (OpenXmlElement change in RevisionElements(doc))
      {
        index++;
        string type = change switch
        {
          InsertedRun => "Insertion",
          DeletedRun => "Deletion",
          _ => "Formatting",
        };
        string author = AuthorOf(change);
        string anchor = AnchorOfElement(map, change);
        revisions.Add(new DocxRevisionInfo(index, author, type, anchor));
      }

      return Result.Success<IReadOnlyList<DocxRevisionInfo>>(revisions);
    }
    catch (OpenXmlPackageException ex)
    {
      return Result.Failure<IReadOnlyList<DocxRevisionInfo>>(new DomainError(DocxErrorCodes.CorruptDocument,
        $"'{path}' could not be opened as a Word package: {Clamp(ex.Message)}"));
    }
    catch (IOException ex)
    {
      return Result.Failure<IReadOnlyList<DocxRevisionInfo>>(new DomainError(DocxErrorCodes.CorruptDocument,
        $"'{path}' could not be opened as a Word package: {Clamp(ex.Message)}"));
    }
  }

  /// <summary>Every tracked-change element in document order: insertions, deletions,
  ///     and formatting changes (rPrChange/pPrChange).</summary>
  private static IEnumerable<OpenXmlElement> RevisionElements(WordprocessingDocument doc)
    => doc.MainDocumentPart?.Document?.Descendants()
      .Where(el => el is InsertedRun or DeletedRun or PreviousRunProperties or PreviousParagraphProperties)
      ?? [];

  /// <summary>The change's author: w:ins/w:del carry w:author directly; formatting
  ///     changes (rPrChange/pPrChange) carry it as the same attribute on their
  ///     element - read through the qualified attribute name.</summary>
  private static string AuthorOf(OpenXmlElement change)
    => change switch
    {
      InsertedRun ins => ins.Author?.Value ?? string.Empty,
      DeletedRun del => del.Author?.Value ?? string.Empty,
      _ => change.GetAttributes().FirstOrDefault(a => a.LocalName == "author").Value ?? string.Empty,
    };

  /// <summary>The anchor of a change element: the first mappable ancestor.</summary>
  private static string AnchorOfElement(DocxMap map, OpenXmlElement change)
  {
    for (OpenXmlElement? at = change; at is not null; at = at.Parent)
    {
      if (at is Paragraph or TableCell)
      {
        string? address = DocxElementMap.AddressOf(map, at);
        if (address is not null)
        {
          return address;
        }
      }
    }

    return string.Empty;
  }

  private static Result<bool> ResolveRevisions(WordprocessingDocument doc, bool accept, DocxRevisionScope scope)
  {
    DocxMap map = DocxElementMap.Build(doc);
    List<OpenXmlElement> selected = [.. RevisionElements(doc).Where(change => ScopeMatches(map, change, scope))];
    if (selected.Count == 0)
    {
      return Result.Failure<bool>(new DomainError(DocxErrorCodes.RevisionNotFound,
        $"No tracked changes match the scope (all={scope.All}, author={scope.Author ?? "(none)"}, address={scope.Address ?? "(none)"})."));
    }

    foreach (OpenXmlElement change in selected)
    {
      ResolveOne(change, accept);
    }

    return Result.Success(true);
  }

  private static bool ScopeMatches(DocxMap map, OpenXmlElement change, DocxRevisionScope scope)
    => scope.All
      || (scope.Author is not null && string.Equals(AuthorOf(change), scope.Author, StringComparison.Ordinal))
      || (scope.Address is not null && string.Equals(AnchorOfElement(map, change), scope.Address, StringComparison.Ordinal));

  /// <summary>Resolves ONE change element: accept unwraps insertions (content stays)
  ///     and removes deletions (content stays deleted); reject removes insertions
  ///     and unwraps deletions converting delText to text; formatting changes are
  ///     removed either way.</summary>
  private static void ResolveOne(OpenXmlElement change, bool accept)
  {
    switch (change)
    {
      case InsertedRun:
        if (accept)
        {
          Unwrap(change);
        }
        else
        {
          change.Remove();
        }

        break;
      case DeletedRun:
        if (accept)
        {
          change.Remove();
        }
        else
        {
          UnwrapDeletion(change);
        }

        break;
      default:
        change.Remove(); // rPrChange / pPrChange: the change record goes either way
        break;
    }
  }

  private static void Unwrap(OpenXmlElement wrapper)
  {
    OpenXmlElement parent = wrapper.Parent ?? throw new InvalidOperationException("revision wrapper has no parent");
    OpenXmlElement at = wrapper;
    foreach (OpenXmlElement child in wrapper.ChildElements.ToList())
    {
      child.Remove(); // detach from the wrapper before re-attaching (the SDK refuses in-tree moves)
      at = parent.InsertAfter(child, at);
    }

    wrapper.Remove();
  }

  /// <summary>Rejecting a deletion restores the text: unwrap, then convert every
  ///     w:delText back to w:t (Space preserved).</summary>
  private static void UnwrapDeletion(OpenXmlElement wrapper)
  {
    OpenXmlElement parent = wrapper.Parent ?? throw new InvalidOperationException("revision wrapper has no parent");
    OpenXmlElement at = wrapper;
    foreach (OpenXmlElement child in wrapper.ChildElements.ToList())
    {
      child.Remove(); // detach from the wrapper before re-attaching
      at = parent.InsertAfter(child, at);
    }

    wrapper.Remove();
    foreach (DeletedText deleted in parent.Descendants<DeletedText>().ToList())
    {
      OpenXmlElement converted = new Text(deleted.Text) { Space = SpaceProcessingModeValues.Preserve };
      _ = deleted.Parent!.InsertAfter(converted, deleted);
      deleted.Remove();
    }
  }

  private static Result<DocxSnapshot> InvalidSnapshot(string message)
    => FailSnapshot(ToolErrorCodes.InvalidParameterValue, message);

  private static Result<DocxSnapshot> FailSnapshot(string code, string message)
    => Result.Failure<DocxSnapshot>(new DomainError(code, message));

  private static Result<DocxEditOutcome> FailEdit(string code, string message)
    => Result.Failure<DocxEditOutcome>(new DomainError(code, message));

  private static Result<DocxSaveOutcome> FailSave(string code, string message)
    => Result.Failure<DocxSaveOutcome>(new DomainError(code, message));

  private static Result<IReadOnlyList<ResolvedEdit>> FailResolved(string code, string message)
    => Result.Failure<IReadOnlyList<ResolvedEdit>>(new DomainError(code, message));

  private static Result<IReadOnlyList<ResolvedEdit>> Single(ResolvedEdit edit)
    => Result.Success<IReadOnlyList<ResolvedEdit>>([edit]);

  private static string Clamp(string message)
  {
    const int cap = 300;
    return message.Length <= cap ? message : message[..cap] + "...";
  }
}
