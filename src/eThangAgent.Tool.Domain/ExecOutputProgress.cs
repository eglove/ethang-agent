using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace eThangAgent.ToolDomain;

/// <summary>The lines a running program has printed so far, published while it runs.
///     A run that never completes still has something to say: when the budget elapses the
///     program is abandoned mid-flight and its collected output is the only account of how
///     far it got, so the timeout result carries these lines instead of losing them.
///     <para>Every line is published as it is printed, so the record is complete up to the
///     instant the budget elapsed — not up to some earlier flush.</para></summary>
public sealed class ExecOutputProgress
{
  /// <summary>How much of the record a timeout result carries. A program that printed
  ///     megabytes before overrunning must not turn one failure into an unbounded
  ///     message, so the tail is kept and the omission is stated.</summary>
  public const int MaxChars = 4000;

  private readonly ConcurrentQueue<string> _lines = new();

  /// <summary>The lines printed so far, in order.</summary>
  public IReadOnlyList<string> Lines => [.. _lines];

  /// <summary>Records one printed line. Called from the program's own threads, so it is
  ///     safe to call concurrently and never blocks.</summary>
  public void Add(string line) => _lines.Enqueue(line ?? string.Empty);

  /// <summary>The record as it enters a timeout result: the last
  ///     <see cref="MaxChars"/> characters, prefixed by a named omission when earlier
  ///     lines were dropped. Empty when the program printed nothing.</summary>
  public string Render()
  {
    string text = string.Join('\n', _lines);
    if (text.Length <= MaxChars)
    {
      return text;
    }

    int omitted = text.Length - MaxChars;
    StringBuilder sb = new();
    _ = sb.Append(CultureInfo.InvariantCulture,
        $"[exec: {omitted.ToString(CultureInfo.InvariantCulture)} earlier character(s) of output omitted — the last {MaxChars.ToString(CultureInfo.InvariantCulture)} are shown]");
    _ = sb.Append('\n');
    _ = sb.Append(text[^MaxChars..]);
    return sb.ToString();
  }
}
