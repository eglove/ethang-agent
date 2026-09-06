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

  // Exec call shape (grand-plan tool-output contract), derived from the args JSON so
  // live and restored cards render identically. Non-exec (or unparseable) cards keep
  // the legacy name+preview header and JSON body.
  private ExecCallShape? Shape => IsExec
      ? ExecCallShape.TryParse(Arguments)
      : null;

  private bool IsExec => string.Equals(Name, "exec", StringComparison.Ordinal);

  /// <summary>True when this card is an exec call whose args parsed: the header
  ///     becomes [gear][title], the body becomes the fenced program.</summary>
  public bool IsExecCall => Shape is not null;

  /// <summary>Header title: the exec call's required title input, falling back to
  ///     the tool name when a legacy/odd exec card carries none.</summary>
  public string HeaderTitle => Shape?.Title ?? Name;

  /// <summary>The program text fenced as C# for the markdown renderer; null keeps
  ///     the legacy JSON arguments body.</summary>
  public string? ProgramBody => Shape is { } shape ? $"```csharp\n{shape.Program}\n```" : null;

  /// <summary>Right header slot: live "elapsed / budget" while running, "budget"
  ///     alone on restored cards, bare elapsed for non-exec tools.</summary>
  public string HeaderTimeDisplay => Shape is not { } shape
      ? ElapsedDisplay
      : HeaderTime(Elapsed, shape);

  private static string HeaderTime(ToolElapsedHandle? elapsed, ExecCallShape shape) =>
      elapsed is null ? shape.BudgetDisplay : $"{elapsed.Display} / {shape.BudgetDisplay}";
}

/// <summary>The parsed exec-call arguments the card UI renders from: title (optional
///     on legacy cards), program, and the configured timeout budget.</summary>
internal sealed record ExecCallShape(string? Title, string Program, string BudgetDisplay)
{
  public static ExecCallShape? TryParse(string rawArguments)
  {
    if (string.IsNullOrWhiteSpace(rawArguments))
    {
      return null;
    }

    try
    {
      using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(rawArguments);
      System.Text.Json.JsonElement root = document.RootElement;
      if (root.ValueKind != System.Text.Json.JsonValueKind.Object
          || !root.TryGetProperty("program", out System.Text.Json.JsonElement programEl)
          || programEl.ValueKind != System.Text.Json.JsonValueKind.String
          || !root.TryGetProperty("timeoutSeconds", out System.Text.Json.JsonElement budgetEl)
          || budgetEl.ValueKind != System.Text.Json.JsonValueKind.Number)
      {
        return null;
      }

      string? title = root.TryGetProperty("title", out System.Text.Json.JsonElement titleEl)
          && titleEl.ValueKind == System.Text.Json.JsonValueKind.String
          ? titleEl.GetString()
          : null;
      return new ExecCallShape(title, programEl.GetString()!, $"{budgetEl.GetDouble()}s");
    }
    catch (System.Text.Json.JsonException)
    {
      return null;
    }
  }
}

// ElapsedDisplay carries the tool card's elapsed-time line (empty when unknown, so
// restored transcripts render unchanged): the call card counts up while the tool
// runs, the result card freezes the total; both render it in the card header.
internal sealed record ToolResultEntry(string Name, string Summary, string FullContent, bool IsError, string ElapsedDisplay = "",
    string? Title = null) : TranscriptEntry
{
  public IBrush SummaryBrush => IsError ? Brushes.IndianRed : Brushes.Gray;

  /// <summary>Rich card title for the header row; empty when the tool supplied none
  ///     (the name-plus-summary legacy header renders instead).</summary>
  public string HeaderTitle => Title ?? "";


}

internal sealed record NoticeEntry(string Text) : TranscriptEntry;

/// <summary>A system message the agent loop appended to the conversation mid-turn
///     (nudges, continuation prompts, compaction-failure notices). Loop-voice, unlike
///     a transient host notice: it lives in the persisted conversation, and restore
///     maps persisted System messages onto this kind.</summary>
internal sealed record SystemMessageEntry(string Text) : TranscriptEntry;

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
