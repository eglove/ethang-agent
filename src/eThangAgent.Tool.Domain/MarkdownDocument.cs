namespace eThangAgent.ToolDomain;

/// <summary>Marker for one block of a structured markdown document.</summary>
public interface IMarkdownBlock;

/// <summary>GFM alert variants rendered as > [!TYPE] blockquotes.</summary>
public enum AlertType { Caution, Important, Note, Tip, Warning }

public enum ListKind { Unordered, Numbered }

public enum TableAlign { Left, Center, Right }

public sealed record TextBlock(string Text) : IMarkdownBlock;

public sealed record HeaderBlock(int Level, string Text) : IMarkdownBlock;

public sealed record QuoteBlock(string Text) : IMarkdownBlock;

public sealed record AlertBlock(AlertType Alert, string Text) : IMarkdownBlock;

public sealed record CodeBlock(string Code, string? Language = null) : IMarkdownBlock;

public sealed record SpaceBlock(int Count = 1) : IMarkdownBlock;

public sealed record ListItem(string Text, IReadOnlyList<ListItem>? Children = null);

public sealed record ListBlock(ListKind Kind, IReadOnlyList<ListItem> Items) : IMarkdownBlock;

public sealed record TaskListItem(bool IsComplete, string Label);

public sealed record TaskListBlock(IReadOnlyList<TaskListItem> Items) : IMarkdownBlock;

public sealed record TableHeader(string Text, TableAlign? Align = null);

public sealed record TableBlock(
    IReadOnlyList<TableHeader> Headers,
    IReadOnlyList<IReadOnlyList<string>> Rows) : IMarkdownBlock;

/// <summary>A structured markdown document: ordered blocks (null entries are skipped,
/// mirroring the reference generator) plus optional YAML frontmatter whose values are
/// limited to string / bool / double.</summary>
public sealed record MarkdownDocument(
    IReadOnlyList<IMarkdownBlock?> Blocks,
    IReadOnlyDictionary<string, object>? FrontMatter = null);
