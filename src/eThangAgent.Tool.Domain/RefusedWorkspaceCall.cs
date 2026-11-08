namespace eThangAgent.ToolDomain;

/// <summary>One mutating file-tool call the harness REFUSED during an exec run, as a fact
///     rather than advice (issue #375). The change report describes effects, so a refused
///     write and a deliberate no-op render identically; this record is the intent the
///     effects cannot show. A run that issued one is not a clean completion, whatever the
///     workspace diff says.</summary>
public sealed record RefusedWorkspaceCall(string Tool, string ErrorCode, string Message)
{
  /// <summary>Longest part of the tool's own message the annotation quotes. A refusal's
  ///     first line is the actionable part (AnchorNotFound quotes a whole region after
  ///     it), and the annotation flags the refusal rather than reproducing it.</summary>
  public const int MaxMessageChars = 200;

  /// <summary>The model-facing line, one per refused call:
  ///     <code>[exec: write was refused (Error [FileExists]: ...) — nothing was written]</code>
  ///     Every mutating tool writes its file, so the closing clause is true of all of them.</summary>
  public string Render() =>
      $"[exec: {Tool} was refused (Error [{ErrorCode}]: {Message}) — nothing was written]";

  /// <summary>The refusal a tool result carries, or null when the result is not an
  ///     <c>Error [Code]: message</c> refusal at all. Only the first line of the message
  ///     is kept, bounded by <see cref="MaxMessageChars"/>.</summary>
  public static RefusedWorkspaceCall? FromResult(string tool, string content)
  {
    ArgumentNullException.ThrowIfNull(tool);
    ArgumentNullException.ThrowIfNull(content);
    const string Prefix = "Error [";
    int close = content.IndexOf("]: ", StringComparison.Ordinal);
    return !content.StartsWith(Prefix, StringComparison.Ordinal) || close < 0
        ? null
        : new RefusedWorkspaceCall(tool, content[Prefix.Length..close], FirstLine(content[(close + 3)..]));
  }

  private static string FirstLine(string message)
  {
    int newline = message.IndexOfAny(['\r', '\n']);
    string line = newline < 0 ? message : message[..newline];
    return line.Length <= MaxMessageChars ? line : line[..MaxMessageChars] + "…";
  }
}
