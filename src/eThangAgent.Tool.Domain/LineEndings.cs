using System.Text;

namespace eThangAgent.ToolDomain;

/// <summary>Mechanical line-ending primitives. No IO, no project knowledge — the
/// policy seam (<see cref="ILineEndingPolicy"/>) decides WHICH convention applies;
/// this type only counts and rewrites terminators.</summary>
public static class LineEndings
{
  /// <summary>The convention an existing text uses, or null when no convention is
  /// dominant. A bare CR counts as a CR-terminated line (it is not an LF), and a
  /// tie is null: a file split evenly between conventions has no dominant style,
  /// so the caller falls back to the project's convention rather than a coin flip.</summary>
  public static LineEnding? DetectDominant(string text)
  {
    ArgumentNullException.ThrowIfNull(text);
    int crTerminated = 0;
    int lfTerminated = 0;
    int i = 0;
    while (i < text.Length)
    {
      (int consumed, bool carriage) = ScanTerminator(text, i);
      if (carriage)
      {
        crTerminated++;
      }
      else if (consumed > 0)
      {
        lfTerminated++;
      }

      i += consumed == 0 ? 1 : consumed;
    }

    if (crTerminated == lfTerminated)
    {
      return null; // no dominant convention (includes text with no terminator at all)
    }

    return crTerminated > lfTerminated ? LineEnding.CrLf : LineEnding.Lf;
  }

  /// <summary>Rewrites every line terminator in <paramref name="text"/> to
  /// <paramref name="target"/>. Terminators are LF, CRLF, and bare CR; nothing else
  /// changes, and a missing trailing terminator stays missing (normalizing line
  /// endings never adds or removes lines).</summary>
  public static string Normalize(string text, LineEnding target)
  {
    ArgumentNullException.ThrowIfNull(text);
    string terminator = Terminator(target);
    StringBuilder sb = new(text.Length);
    int i = 0;
    while (i < text.Length)
    {
      (int consumed, _) = ScanTerminator(text, i);
      if (consumed == 0)
      {
        _ = sb.Append(text[i]);
        i++;
      }
      else
      {
        _ = sb.Append(terminator);
        i += consumed;
      }
    }

    return sb.ToString();
  }

  /// <summary>The terminator text for a convention.</summary>
  public static string Terminator(LineEnding ending) =>
      ending switch
      {
        LineEnding.Lf => "\n",
        LineEnding.CrLf => "\r\n",
        _ => throw new ArgumentOutOfRangeException(nameof(ending), ending, "unknown line ending"),
      };

  /// <summary>The name used in model-facing annotation lines.</summary>
  public static string Label(LineEnding ending) =>
      ending switch
      {
        LineEnding.Lf => "LF",
        LineEnding.CrLf => "CRLF",
        _ => throw new ArgumentOutOfRangeException(nameof(ending), ending, "unknown line ending"),
      };

  /// <summary>The line terminator starting at <paramref name="index"/>, if any: how
  /// many characters it spans (0 when the character is ordinary text) and whether it
  /// is carriage-return-flavoured (CRLF or a bare CR) rather than a lone LF.</summary>
  private static (int Consumed, bool CarriageReturn) ScanTerminator(string text, int index)
  {
    char c = text[index];
    bool more = index + 1 < text.Length;
    return c switch
    {
      '\r' => (more && text[index + 1] == '\n' ? 2 : 1, true),
      '\n' => (1, false),
      _ => (0, false),
    };
  }
}
