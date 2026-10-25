using System.Globalization;
using System.Text;

namespace eThangAgent.ToolDomain;

/// <summary>What one exec run actually changed in the workspace, as a fact rather than
///     advice (issue #283). The distinction that matters: this describes EFFECTS, not
///     the API used — a program that writes through <c>Tools.write</c> and one that
///     hand-rolls <c>File.WriteAllText</c> are reported identically, because both landed
///     the same bytes.
///     <para>A run that changed nothing is the case this exists for: a read-modify-write
///     whose anchor did not match writes the file back unchanged and used to report
///     success. It now reports <c>modified 0</c>, and the correction needs no judgment.</para></summary>
public sealed class WorkspaceChangeSet
{
  /// <summary>Most paths named in one report line. A refactor touching hundreds of
  ///     files must not flood the transcript; the remainder is counted, never dropped
  ///     silently.</summary>
  public const int MaxListedPaths = 12;

  public WorkspaceChangeSet(IReadOnlyList<string> added, IReadOnlyList<string> modified,
      IReadOnlyList<string> removed, bool isComplete = true)
  {
    ArgumentNullException.ThrowIfNull(added);
    ArgumentNullException.ThrowIfNull(modified);
    ArgumentNullException.ThrowIfNull(removed);
    Added = added;
    Modified = modified;
    Removed = removed;
    IsComplete = isComplete;
  }

  /// <summary>Paths that did not exist before the run.</summary>
  public IReadOnlyList<string> Added { get; }

  /// <summary>Paths whose content differs from before the run.</summary>
  public IReadOnlyList<string> Modified { get; }

  /// <summary>Paths that existed before the run and are gone after it.</summary>
  public IReadOnlyList<string> Removed { get; }

  /// <summary>False when either snapshot was truncated, so the account may be partial.
  ///     The rendered line says so rather than implying a complete walk.</summary>
  public bool IsComplete { get; }

  /// <summary>Nothing changed at all.</summary>
  public bool IsEmpty => Added.Count == 0 && Modified.Count == 0 && Removed.Count == 0;

  /// <summary>Total files touched, in any direction.</summary>
  public int Count => Added.Count + Modified.Count + Removed.Count;

  /// <summary>The model-facing annotation line. Two shapes, both verbatim contracts:
  ///     <code>[exec: modified 0 workspace files — the program completed but changed nothing]</code>
  ///     <code>[exec: modified 2 workspace files — src/a.cs, src/b.cs (added)]</code>
  ///     A truncated walk appends a note naming the limit, so the model never reads a
  ///     partial account as a complete one.</summary>
  public string Render()
  {
    if (IsEmpty)
    {
      return IsComplete
          ? "[exec: modified 0 workspace files — the program completed but changed nothing]"
          : "[exec: modified 0 workspace files — the program completed but changed nothing "
              + "(the workspace walk was truncated, so this is not a complete account)]";
    }

    StringBuilder sb = new();
    _ = sb.Append(CultureInfo.InvariantCulture,
        $"[exec: modified {Count} workspace file{(Count == 1 ? string.Empty : "s")} — ");
    AppendPaths(sb);
    _ = sb.Append(']');
    if (!IsComplete)
    {
      _ = sb.Append(CultureInfo.InvariantCulture,
          $"\n[exec: note — the workspace walk was truncated at {WorkspaceSnapshotLimits.MaxFiles} files, so paths beyond that limit are not reported.]");
    }

    return sb.ToString();
  }

  /// <summary>Lists changed paths in a stable order (modified, added, removed — the
  ///     order a reader cares about), marking the non-modified ones, and counts whatever
  ///     the listing cap left out.</summary>
  private void AppendPaths(StringBuilder sb)
  {
    List<string> rendered = [];
    rendered.AddRange(Modified);
    rendered.AddRange(Added.Select(p => p + " (added)"));
    rendered.AddRange(Removed.Select(p => p + " (removed)"));

    int listed = Math.Min(rendered.Count, MaxListedPaths);
    _ = sb.Append(string.Join(", ", rendered.Take(listed)));
    if (rendered.Count > listed)
    {
      _ = sb.Append(CultureInfo.InvariantCulture, $", and {rendered.Count - listed} more");
    }
  }
}
