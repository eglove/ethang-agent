using Avalonia.Media;

namespace eThangAgent.Desktop.ViewModels;

// Controller Ruling R4: entry variants are top-level records in this namespace
// (not nested types) so Avalonia XAML DataTemplates can reference them without
// nested-type syntax. Positional records give init-only properties, enabling
// non-destructive mutation with `with` when a block is extended.

#pragma warning disable S2094 // Deliberate empty base: entry variants are data for DataTemplates.
internal abstract record TranscriptEntry;
#pragma warning restore S2094

internal sealed record UserMessageEntry(string Text) : TranscriptEntry;

internal sealed record AssistantTextEntry(string Text, bool IsOpen) : TranscriptEntry;

internal sealed record ReasoningEntry(string Text, bool IsOpen) : TranscriptEntry;

internal sealed record ToolCallEntry(string Name, string Arguments) : TranscriptEntry
{
  public string Preview => ToolArgsFormatter.Preview(Arguments);

  public string ArgumentsFormatted => ToolArgsFormatter.Indent(Arguments);
}

internal sealed record ToolResultEntry(string Name, string Summary, string FullContent, bool IsError) : TranscriptEntry
{
  public IBrush SummaryBrush => IsError ? Brushes.IndianRed : Brushes.Gray;
}

internal sealed record NoticeEntry(string Text) : TranscriptEntry;
