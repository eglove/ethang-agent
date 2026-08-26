namespace eThangAgent.Desktop.ViewModels;

// Controller Ruling R4: entry variants are top-level records in this namespace
// (not nested types) so Avalonia XAML DataTemplates can reference them without
// nested-type syntax. Positional records give init-only properties, enabling
// non-destructive mutation with `with` when a block is extended.

internal abstract record TranscriptEntry;

internal sealed record UserMessageEntry(string Text) : TranscriptEntry;

internal sealed record AssistantTextEntry(string Text) : TranscriptEntry;

internal sealed record ReasoningEntry(string Text) : TranscriptEntry;

internal sealed record ToolCallEntry(string Name, string Arguments) : TranscriptEntry;

internal sealed record ToolResultEntry(string Name, string Summary) : TranscriptEntry;

internal sealed record NoticeEntry(string Text) : TranscriptEntry;
