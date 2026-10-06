using System.Globalization;

namespace eThangAgent.ToolDomain;

/// <summary>An inclusive 1-based line range [Start, End]. The freshness ledger's
///     unit of coverage.</summary>
public readonly record struct LineSpan(int Start, int End)
{
  public override string ToString() => $"{Start.ToString(CultureInfo.InvariantCulture)}-{End.ToString(CultureInfo.InvariantCulture)}";
}
