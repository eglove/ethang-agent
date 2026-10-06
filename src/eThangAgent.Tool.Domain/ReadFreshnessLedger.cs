namespace eThangAgent.ToolDomain;

/// <summary>Turn-local read memory for the freshness guard: per resolved path, one
///     entry holding the file's version (the (Length, LastWriteTimeUtc) pair the ACL
///     captured at the read moment) and the line ranges delivered at that version.
///     A read whose requested range is already covered — same version, stored ranges
///     cover it — elides; a version change wipes the path's entry wholesale (the
///     pair IS the invalidation protocol); Reset() clears everything at turn start.
///     All operations are thread-safe (the parallel-call hazard: sibling tool calls
///     read the same file concurrently) — one lock over the entry map.</summary>
public sealed class ReadFreshnessLedger
{
  private readonly Lock _lock = new();
  private readonly Dictionary<string, (FileVersion Version, List<LineSpan> Ranges)> _entries = new(StringComparer.Ordinal);

  /// <summary>Which spans of [startLine, endLine] the model already holds at
  ///     <paramref name="version"/>. A null version (legacy read) never covers.</summary>
  public FileCoverage Check(string path, FileVersion? version, int startLine, int endLine)
  {
    if (version is null)
    {
      return new FileCoverage([]);
    }

    lock (_lock)
    {
      return _entries.TryGetValue(path, out (FileVersion Version, List<LineSpan> Ranges) entry)
              && entry.Version == version
          ? new FileCoverage(Intersect(entry.Ranges, startLine, endLine))
          : new FileCoverage([]);
    }
  }

  /// <summary>Records that [startLine, endLine] was delivered at
  ///     <paramref name="version"/>. A version change replaces the path's entry
  ///     wholesale; same version unions (and merges adjacent ranges). First writer
  ///     wins on a concurrent version bump — a miss never elides, the safe direction.</summary>
  public void Record(string path, FileVersion? version, int startLine, int endLine)
  {
    if (version is null)
    {
      return;
    }

    lock (_lock)
    {
      if (_entries.TryGetValue(path, out (FileVersion Version, List<LineSpan> Ranges) entry)
          && entry.Version == version)
      {
        entry.Ranges.Add(new LineSpan(startLine, endLine));
        entry.Ranges.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.End.CompareTo(b.End));
        _ = Merge(entry.Ranges);
      }
      else
      {
        _entries[path] = (version, [new LineSpan(startLine, endLine)]);
      }
    }
  }

  /// <summary>Clears all entries (turn start).</summary>
  public void Reset()
  {
    lock (_lock)
    {
      _entries.Clear();
    }
  }

  /// <summary>The spans of the stored ranges inside [startLine, endLine], sorted and merged.</summary>
  private static List<LineSpan> Intersect(List<LineSpan> stored, int startLine, int endLine)
  {
    List<LineSpan> result = [];
    foreach (LineSpan s in stored)
    {
      int from = Math.Max(s.Start, startLine);
      int to = Math.Min(s.End, endLine);
      if (from <= to)
      {
        result.Add(new LineSpan(from, to));
      }
    }

    return Merge(result);
  }

  /// <summary>Merges overlapping and adjacent spans in place; the list stays sorted.</summary>
  private static List<LineSpan> Merge(List<LineSpan> spans)
  {
    for (int i = 0; i < spans.Count - 1;)
    {
      if (spans[i].End + 1 >= spans[i + 1].Start)
      {
        spans[i] = new LineSpan(spans[i].Start, Math.Max(spans[i].End, spans[i + 1].End));
        spans.RemoveAt(i + 1);
      }
      else
      {
        i++;
      }
    }

    return spans;
  }
}
