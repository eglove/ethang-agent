namespace eThangAgent.ToolDomain;

/// <summary>One <c>db_query</c> result page: the column names, up to the requested row
///     cap, and whether at least one more row existed beyond the cap.</summary>
public sealed record SelfQueryResult(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<SelfQueryCell>> Rows,
    bool Truncated);

/// <summary>One result cell. <see cref="Text"/> carries text and numeric values; it is
///     null exactly when the SQL value was NULL or a BLOB — BLOBs set
///     <see cref="BlobByteCount"/> to their byte length instead.</summary>
public sealed record SelfQueryCell(string? Text, int? BlobByteCount)
{
  public static SelfQueryCell Null { get; } = new(null, null);
}
