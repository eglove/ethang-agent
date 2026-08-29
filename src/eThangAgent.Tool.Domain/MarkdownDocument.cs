namespace eThangAgent.ToolDomain;

/// <summary>Base type for one block of a structured markdown document. Abstract record
/// rather than an empty interface (CA1040): blocks are data, and the base carries no
/// members of its own.</summary>
#pragma warning disable S2094 // Deliberate empty base — blocks are data, see doc comment.
public abstract record MarkdownBlock;
#pragma warning restore S2094

/// <summary>GFM alert variants rendered as > [!TYPE] blockquotes.</summary>
public enum AlertType { Caution, Important, Note, Tip, Warning }

public enum ListKind { Unordered, Numbered }

public enum TableAlign { Left, Center, Right }

public sealed record TextBlock(string Text) : MarkdownBlock;

public sealed record HeaderBlock(int Level, string Text) : MarkdownBlock;

public sealed record QuoteBlock(string Text) : MarkdownBlock;

public sealed record AlertBlock(AlertType Alert, string Text) : MarkdownBlock;

public sealed record CodeBlock(string Code, string? Language = null) : MarkdownBlock;

public sealed record SpaceBlock(int Count = 1) : MarkdownBlock;

public sealed record ListItem(string Text, IReadOnlyList<ListItem>? Children = null);

public sealed record ListBlock(ListKind Kind, IReadOnlyList<ListItem> Items) : MarkdownBlock;

public sealed record TaskListItem(bool IsComplete, string Label);

public sealed record TaskListBlock(IReadOnlyList<TaskListItem> Items) : MarkdownBlock;

public sealed record TableHeader(string Text, TableAlign? Align = null);

public sealed record TableBlock(
    IReadOnlyList<TableHeader> Headers,
    IReadOnlyList<IReadOnlyList<string>> Rows) : MarkdownBlock;

/// <summary>A structured markdown document: ordered blocks (null entries are skipped,
/// mirroring the reference generator) plus optional YAML frontmatter whose values are
/// limited to string / bool / double.</summary>
public sealed record MarkdownDocument(
    IReadOnlyList<MarkdownBlock?> Blocks,
    IReadOnlyDictionary<string, object>? FrontMatter = null);
