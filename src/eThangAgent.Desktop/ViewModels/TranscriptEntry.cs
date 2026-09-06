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

internal sealed record ToolCallEntry(string Name, string Arguments, ToolElapsedHandle? Elapsed = null) : TranscriptEntry
{
  /// <summary>The elapsed line for logic and tests: the live handle's display
  ///     while the tool runs, empty on restored cards (no handle).</summary>
  public string ElapsedDisplay => Elapsed?.Display ?? "";

  public string Preview => ToolArgsFormatter.Preview(Arguments);

  public string ArgumentsFormatted => ToolArgsFormatter.Indent(Arguments);
}

// ElapsedDisplay carries the tool card's elapsed-time line (empty when unknown, so
// restored transcripts render unchanged): the call card counts up while the tool
// runs, the result card freezes the total; both render it in the card header.
internal sealed record ToolResultEntry(string Name, string Summary, string FullContent, bool IsError, string ElapsedDisplay = "") : TranscriptEntry
{
  public IBrush SummaryBrush => IsError ? Brushes.IndianRed : Brushes.Gray;
}

internal sealed record NoticeEntry(string Text) : TranscriptEntry;

/// <summary>A ! command the user ran: echoed as typed, before its result lands.
///     Local-only — a command entry never enters the conversation.</summary>
internal sealed record CommandRunEntry(string Command) : TranscriptEntry;

/// <summary>The captured output of one ! command run. Shows a tail of the output
///     (the full text lives in the database; command_output reads it back) plus the
///     exit/timeout line. Local-only, like the run entry.</summary>
internal sealed record CommandResultEntry(string Command, int RunId, int ExitCode, bool TimedOut, string OutputTail, bool Truncated) : TranscriptEntry
{
  public IBrush StatusBrush => TimedOut || ExitCode != 0 ? Brushes.IndianRed : Brushes.Gray;

  public string OutputDisplay => string.IsNullOrWhiteSpace(OutputTail) ? "(no output)" : OutputTail;

  public string StatusLine => TimedOut
      ? $"id {RunId} — TIMED OUT (partial output captured)"
      : $"id {RunId} — exit code {ExitCode}";
}
