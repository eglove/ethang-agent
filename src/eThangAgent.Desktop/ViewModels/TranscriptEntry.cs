namespace eThangAgent.Desktop.ViewModels;

// Controller Ruling R4: entry variants are top-level records in this namespace
// (not nested types) so Avalonia XAML DataTemplates can reference them without
// nested-type syntax. Positional records give init-only properties, enabling
// non-destructive mutation with `with` when a block is extended.

public abstract record TranscriptEntry;

public sealed record UserMessageEntry(string Text) : TranscriptEntry;

public sealed record AssistantTextEntry(string Text) : TranscriptEntry;

public sealed record ReasoningEntry(string Text) : TranscriptEntry;

public sealed record ToolCallEntry(string Name, string Arguments) : TranscriptEntry;

public sealed record ToolResultEntry(string Name, string Summary) : TranscriptEntry;

public sealed record NoticeEntry(string Text) : TranscriptEntry;
