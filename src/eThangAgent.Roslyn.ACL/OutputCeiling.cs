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
///     produced 10 MB. The two streams keep their own heads (a stdout chunk never eats
///     stderr's room), but they draw from one shared room budget, and a stream that was
///     clipped — or dropped entirely once the budget ran out — carries the marker so a
///     clipped stream is never mistaken for a complete one.</para>
///     <para>Thread-safe by construction: the two pipe pumps append concurrently.</para>
///     <para>Named decision: the caps are measured in CHARACTERS of the decoded capture
///     (the pipes are already decoded to strings), not in wire bytes. The ceiling bounds
///     what the model would otherwise read, which is the decoded text — a UTF-8 byte
///     count would need a second, redundant accounting of the same buffer.</para></summary>
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

  /// <summary>True when any byte of the run's output was dropped. The head is what
  ///     survives, so a truncated result is never mistaken for a short one.</summary>
  public bool Truncated { get; private set; }

  /// <summary>True when the run produced more than the kill cap and must be stopped.</summary>
  public bool Exceeded { get; private set; }

  /// <summary>Appends one stdout chunk; returns false when the run must be stopped.</summary>
  public bool Append(string text) => Append(text, isErrorStream: false);

  /// <summary>Appends one chunk from either pipe; returns false when the run must be
  ///     stopped. The chunk is counted against the shared total BEFORE any room check,
  ///     so the kill cap sees the run's real output volume even when every byte of it
  ///     was clipped away.</summary>
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

      // The limit rides the marker so the model can size its next run against it. The
      // stream name is spelled out when the OTHER stream exists too, because a clipped
      // stderr under a complete stdout is otherwise easy to miss.
      string limit = TruncationMarker + " (limit " + HumanBytes(truncateBytes)
          + (isErrorStream ? ", stderr" : ", stdout") + ")";
      return body.Length == 0 ? limit : body + Environment.NewLine + limit;
    }
  }

  /// <summary>A byte count in the unit the ceiling contract speaks in, so a limit reads
  ///     as "64 KB" or "8 MB" rather than a unitless number.</summary>
  internal static string HumanBytes(long bytes) => bytes switch
  {
    >= 1024L * 1024L => (bytes / (double)(1024L * 1024L)).ToString("0.#", CultureInfo.InvariantCulture) + " MB",
    >= 1024L => (bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB",
    _ => bytes.ToString(CultureInfo.InvariantCulture) + " bytes",
  };
}
