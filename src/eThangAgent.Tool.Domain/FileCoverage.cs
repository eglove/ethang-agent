namespace eThangAgent.ToolDomain;

/// <summary>One file's freshness verdict for a requested range: which spans of it
///     the model already holds (delivered earlier at the same version).</summary>
/// <param name="Elided">The held spans inside the requested range, ordered and
///     merged (adjacent spans are one span).</param>
public sealed record FileCoverage(IReadOnlyList<LineSpan> Elided)
{
  /// <summary>True when nothing in the requested range is already held.</summary>
  public bool IsEmpty => Elided.Count == 0;
}
