namespace eThangAgent.ToolDomain;

/// <summary>A tool whose arguments carry transport metadata alongside the semantic
/// payload (issue #124, 2026-10-15 incident: exec's title/timeoutSeconds envelope). The
/// repeat guard keys call identity on the payload this returns, so envelope variation
/// cannot defeat the identical-call streaks; null falls back to the raw arguments.</summary>
public interface IToolCallIdentifiable
{
  /// <summary>The stable call identity inside <paramref name="jsonArguments"/>, or
  /// null when the arguments cannot be interpreted (the caller keeps raw arguments).</summary>
  string? CallIdentity(string jsonArguments);
}
