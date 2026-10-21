using System.Globalization;
using System.Text;

namespace eThangAgent.Roslyn.ACL;

/// <summary>Bounds one run's captured output (spec D10). Two caps: past the truncate
///     cap the head is kept and an explicit marker names the limit and how to recover
///     the rest; past the kill cap the run is stopped, because a program producing
///     hundreds of megabytes of output is a runaway, not a report. Never silent: the
///     model must always be able to tell truncation from a short result.
///     <para>BOTH pipes feed ONE ceiling, so the bound is on the run's TOTAL output
///     rather than per stream — a program writing 5 MB to stdout and 5 MB to stderr has
///     produced 10 MB. There is ONE shared room for the whole run: whichever pump
///     appends first consumes it, so a chatty stream can take the entire room and leave
///     the other stream with nothing but its marker. Each stream keeps its own head
///     buffer and its own clipped flag, and a stream that was clipped — or dropped
///     entirely once the room ran out — carries the marker, so a clipped stream is never
///     mistaken for a complete one. A stream that fit is rendered verbatim with no
///     marker.</para>
///     <para>Thread-safe by construction: the two pipe pumps append concurrently.</para>
///     <para>UNIT — the single authoritative statement: both caps are counted in
///     CHARACTERS of the decoded capture, not in wire bytes. The pipes are already
///     decoded to strings, and the ceiling bounds what the model would otherwise READ,
///     which is the decoded text; a UTF-8 byte count would need a second, redundant
///     accounting of the same buffer. Every rendered limit says "characters" for this
///     reason. The <c>ScriptCeilings.OutputTruncateBytes</c>/<c>OutputKillBytes</c>
///     fields and this class's parameter names keep the word "Bytes" for contract
///     continuity with the plan that reserved them; the COUNT is characters.</para></summary>
internal sealed class OutputCeiling(long truncateBytes, long killBytes)
{
  /// <summary>The marker line appended to a clipped stream. Names the limit and the
  ///     recovery, so the model re-runs with a redirect instead of reading a silently
  ///     clipped result as complete.</summary>
  public const string TruncationMarker =
      "[output truncated: the capture limit was reached; redirect large output to a file and read a range]";

  private readonly Lock _gate = new();
  private readonly StringBuilder _stdout = new();
  private readonly StringBuilder _stderr = new();
  private long _total;
  private long _kept;
  private bool _stdoutClipped;
  private bool _stderrClipped;

  /// <summary>True when any character of the run's output was dropped. The head is what
  ///     survives, so a truncated result is never mistaken for a short one.</summary>
  public bool Truncated { get; private set; }

  /// <summary>True when the run produced more than the kill cap and must be stopped.</summary>
  public bool Exceeded { get; private set; }

  /// <summary>Appends one stdout chunk; returns false when the run must be stopped.</summary>
  public bool Append(string text) => Append(text, isErrorStream: false);

  /// <summary>Appends one chunk from either pipe; returns false when the run must be
  ///     stopped. The chunk is counted against the shared total BEFORE any room check,
  ///     so the kill cap sees the run's real output volume even when every character of
  ///     it was clipped away.</summary>
  public bool Append(string text, bool isErrorStream)
  {
    if (string.IsNullOrEmpty(text))
    {
      return true;
    }

    lock (_gate)
    {
      _total += text.Length;
      if (_total > killBytes)
      {
        Exceeded = true;
        return false;
      }

      StringBuilder target = isErrorStream ? _stderr : _stdout;
      long room = truncateBytes - _kept;
      if (room <= 0)
      {
        // The shared room is gone: this stream contributes nothing more, and it must
        // SAY so rather than render as a complete (short) stream.
        Truncated = true;
        Clip(isErrorStream);
        return true;
      }

      if (text.Length <= room)
      {
        _ = target.Append(text);
        _kept += text.Length;
        return true;
      }

      _ = target.Append(text, 0, (int)room);
      _kept += room;
      Truncated = true;
      Clip(isErrorStream);
      return true;
    }
  }

  /// <summary>The captured stdout head, with the truncation marker appended when this
  ///     stream was clipped.</summary>
  public string Render() => RenderStream(_stdout, _stdoutClipped, isErrorStream: false);

  /// <summary>The captured stderr head, with the truncation marker appended when this
  ///     stream was clipped.</summary>
  public string RenderStderr() => RenderStream(_stderr, _stderrClipped, isErrorStream: true);

  private void Clip(bool isErrorStream)
  {
    if (isErrorStream)
    {
      _stderrClipped = true;
    }
    else
    {
      _stdoutClipped = true;
    }
  }

  private string RenderStream(StringBuilder buffer, bool clipped, bool isErrorStream)
  {
    lock (_gate)
    {
      string body = buffer.ToString();
      if (!clipped)
      {
        return body;
      }

      // The limit rides the marker so the model can size its next run against it, and it
      // is rendered in the unit the cap actually counts (characters — see the class
      // summary's UNIT paragraph) rather than a rounded byte magnitude. The stream name
      // is spelled out because a clipped stderr under a complete stdout is otherwise easy
      // to miss.
      string limit = TruncationMarker + " (limit " + truncateBytes.ToString(CultureInfo.InvariantCulture)
          + " characters" + (isErrorStream ? ", stderr" : ", stdout") + ")";
      return body.Length == 0 ? limit : body + Environment.NewLine + limit;
    }
  }

  /// <summary>A cap rendered in the unit it counts (characters — see the class summary's
  ///     UNIT paragraph), with a parenthesised magnitude so a large cap still reads at a
  ///     glance: "8388608 characters (8 MB)".</summary>
  internal static string DescribeCap(long characters) =>
      characters.ToString(CultureInfo.InvariantCulture) + " characters (" + Magnitude(characters) + ")";

  /// <summary>The cap's magnitude for at-a-glance reading. A parenthetical aid only —
  ///     the authoritative unit is the character count beside it.</summary>
  private static string Magnitude(long characters) => characters switch
  {
    >= 1024L * 1024L => (characters / (double)(1024L * 1024L)).ToString("0.#", CultureInfo.InvariantCulture) + " MB",
    >= 1024L => (characters / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB",
    _ => characters.ToString(CultureInfo.InvariantCulture) + " characters",
  };
}
