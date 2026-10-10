namespace eThangAgent.ToolDomain;

/// <summary>One body table cell's text: the row/column coordinates (1-based, matching
///     the cell address grammar) and the cell's plain text.</summary>
public sealed record DocxCellText(int Row, int Col, string Text);

/// <summary>One numbered block of the flat body list. Paragraphs and tables share the
///     numbering — a table is ONE block regardless of its row count.</summary>
public abstract record DocxBlock(int Index);

/// <summary>A body paragraph block: its style name (empty when the paragraph carries no
///     explicit style) and its plain text.</summary>
public sealed record DocxParagraphBlock(int Index, string Style, string Text) : DocxBlock(Index);

/// <summary>A body table block: dimensions plus every cell's text. A table is one
///     block; its cells address through t&lt;N&gt;.r&lt;R&gt;.c&lt;C&gt;.</summary>
public sealed record DocxTableBlock(int Index, int Rows, int Cols, IReadOnlyList<DocxCellText> Cells) : DocxBlock(Index);

/// <summary>One section's header/footer paragraph layers (empty lists when the section
///     has no header/footer of its own).</summary>
public sealed record DocxSectionLayers(
    int Section,
    IReadOnlyList<DocxParagraphBlock> HeaderParagraphs,
    IReadOnlyList<DocxParagraphBlock> FooterParagraphs);

/// <summary>One embedded media entry: package-relative name, media type, byte count.</summary>
public sealed record DocxMediaInfo(string Name, string MediaType, long ByteCount);

/// <summary>The docx read snapshot: the file's content-true version at the read moment,
///     the flat body block count, the requested page of blocks, section/header/footer
///     layers, tracked-change and comment counts, embedded media, and the per-section
///     header/footer paragraphs. Read surfaces the version token; edits require it back.
///     </summary>
public sealed record DocxSnapshot(
    FileVersion Version,
    int TotalBlocks,
    int Offset,
    IReadOnlyList<DocxBlock> Blocks,
    int SectionCount,
    int TrackedChangeCount,
    int CommentCount,
    IReadOnlyList<DocxMediaInfo> Media,
    IReadOnlyList<DocxSectionLayers> Sections);

/// <summary>Run-level formatting to apply (paragraph-granular per the named decisions):
///     null members are left unchanged; a set member applies to every run in the
///     addressed paragraph. ColorHex is validated in the constructor - an invalid hex
///     string is a programmer error (the tool validates before constructing).</summary>
public sealed record DocxRunFormat
{
  public DocxRunFormat(bool? bold, bool? italic, bool? underline, int? fontSizeHalfPoints, string? colorHex)
  {
    if (colorHex is not null && (colorHex.Length != 6 || colorHex.Any(c => !Uri.IsHexDigit(c))))
    {
      throw new ArgumentException($"ColorHex must match ^[0-9A-Fa-f]{{6}}$; got '{colorHex}'.", nameof(colorHex));
    }

    Bold = bold;
    Italic = italic;
    Underline = underline;
    FontSizeHalfPoints = fontSizeHalfPoints;
    ColorHex = colorHex;
  }

  public bool? Bold { get; }

  public bool? Italic { get; }

  public bool? Underline { get; }

  public int? FontSizeHalfPoints { get; }

  public string? ColorHex { get; }
}

/// <summary>One edit operation inside an atomic batch. All operations are validated and
///     resolved BEFORE any mutation; the first invalid op refuses the whole batch. The
///     marker record is deliberately empty: it exists to type the closed operation
///     vocabulary (the ComputerCommand pattern), not to carry members.</summary>
#pragma warning disable S2094 // Named decision: deliberate empty marker record typing the closed op vocabulary.
public abstract record DocxOperation;
#pragma warning restore S2094

/// <summary>Replaces one paragraph's text. The paragraph style is kept; the first run's
///     formatting applies to the new text; run-level variance inside the paragraph is
///     lost (named decisions from the spec).</summary>
public sealed record ReplaceParagraphText(string Address, string Text) : DocxOperation;

/// <summary>Sets one paragraph's style by name (unknown names fail StyleNotFound, the
///     message lists the available styles).</summary>
public sealed record SetParagraphStyle(string Address, string StyleName) : DocxOperation;

/// <summary>Inserts a paragraph after the addressed block. AfterAddress "p0" inserts
///     before the first body block.</summary>
public sealed record InsertParagraph(string AfterAddress, string Text, string? Style) : DocxOperation;

/// <summary>Deletes one paragraph.</summary>
public sealed record DeleteParagraph(string Address) : DocxOperation;

/// <summary>Sets one table cell's text (replaces the cell's paragraph content).</summary>
public sealed record SetCellText(string Address, string Text) : DocxOperation;

/// <summary>Inserts a table row after the given row index (0 = before the first row).</summary>
public sealed record InsertTableRow(string TableAddress, int AfterRowIndex) : DocxOperation;

/// <summary>Deletes one table row.</summary>
public sealed record DeleteTableRow(string TableAddress, int RowIndex) : DocxOperation;

/// <summary>Applies run formatting to every run in the addressed paragraph
///     (paragraph-granular; null members left unchanged).</summary>
public sealed record ApplyRunFormatting(string Address, DocxRunFormat Format) : DocxOperation;

/// <summary>Literal (non-regex) whole-document find/replace over body paragraphs; the
///     replacement merges the first run's formatting over the match (named decision).</summary>
public sealed record FindReplace(string Find, string Replace) : DocxOperation;

/// <summary>The result of an atomic edit batch: the file's new content-true version and
///     how many blocks changed.</summary>
public sealed record DocxEditOutcome(FileVersion NewVersion, int ChangedBlocks);

/// <summary>The result of a create/comment/revision mutation: the file's new
///     content-true version.</summary>
public sealed record DocxSaveOutcome(FileVersion NewVersion);

/// <summary>One document comment: id, author, text, and the block address it anchors to.
///     </summary>
public sealed record DocxCommentInfo(int Id, string Author, string Text, string AnchorAddress);

/// <summary>One tracked change: index, author, type (Insertion|Deletion|Formatting), and
///     the block address it anchors to.</summary>
public sealed record DocxRevisionInfo(int Index, string Author, string Type, string AnchorAddress);

/// <summary>Revision resolution scope — exactly one selector: All=true, or Author
///     non-null, or Address non-null. More than one selector throws ArgumentException
///     (programmer error; the tool validates before constructing).</summary>
public sealed record DocxRevisionScope
{
  public DocxRevisionScope(bool all, string? author, string? address)
  {
    All = all;
    Author = author;
    Address = address;
    int selectors = (All ? 1 : 0) + (Author is not null ? 1 : 0) + (Address is not null ? 1 : 0);
    if (selectors != 1)
    {
      throw new ArgumentException($"Exactly one revision selector must be set: All=true, a non-null Author, or a non-null Address; got {selectors}.", nameof(all));
    }

    if (Author is not null && Author.Length == 0)
    {
      throw new ArgumentException("Author must be non-empty when given.", nameof(author));
    }

    if (Address is not null && Address.Length == 0)
    {
      throw new ArgumentException("Address must be non-empty when given.", nameof(address));
    }
  }

  public bool All { get; }

  public string? Author { get; }

  public string? Address { get; }
}
