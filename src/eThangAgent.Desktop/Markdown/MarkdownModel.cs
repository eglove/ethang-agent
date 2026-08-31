namespace eThangAgent.Desktop.Markdown;

// Chat-markdown object model. Records give init-only shape for the renderer;
// base variants exist only to group data (see TranscriptEntry for the same ruling).

#pragma warning disable S2094 // Deliberate empty base records: variants are data for rendering.
internal abstract record Inline;
internal abstract record Block;
#pragma warning restore S2094

internal sealed record TextSpan(string Text) : Inline;

internal sealed record BoldSpan(IReadOnlyList<Inline> Children) : Inline;

internal sealed record ItalicSpan(IReadOnlyList<Inline> Children) : Inline;

internal sealed record CodeSpan(string Code) : Inline;

internal sealed record LinkSpan(string Text, string Url) : Inline;

internal sealed record HeadingBlock(int Level, IReadOnlyList<Inline> Inlines) : Block;

internal sealed record ParagraphBlock(IReadOnlyList<Inline> Inlines) : Block;

internal sealed record CodeBlock(string Language, string Text) : Block;

internal sealed record ListBlock(bool Ordered, IReadOnlyList<IReadOnlyList<Inline>> Items) : Block;

/// <summary>One table cell: the inlines rendered inside it.</summary>
internal sealed record TableCell(IReadOnlyList<Inline> Inlines);

/// <summary>One table row: its cells, top-aligned by the renderer.</summary>
internal sealed record TableRow(IReadOnlyList<TableCell> Cells);

/// <summary>Pipe table. Cells reuse the inline model; the header row is mandatory
///     (a pipe sequence without a delimiter row is not a table).</summary>
internal sealed record TableBlock(TableRow Header, IReadOnlyList<TableRow> Rows) : Block;

internal sealed record MarkdownDocument(IReadOnlyList<Block> Blocks);
