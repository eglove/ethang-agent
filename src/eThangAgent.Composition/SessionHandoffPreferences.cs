namespace eThangAgent.Composition;

/// <summary>The compaction-handoff preference (issue #234): the key the handoff
///     enablement delegate reads live at compaction time, and its STRICT parser.
///     Absent means disabled; only the exact lowercase strings bind; anything else is
///     a named error, never coerced — the AGENTS.md strict-boundary convention
///     (bool.TryParse would accept any case, which is coercion, so it is not used).</summary>
public static class SessionHandoffPreferences
{
  /// <summary>The app-preference key enabling the compaction handoff.</summary>
  public const string Key = "compaction_handoff_enabled";

  /// <summary>Binds the stored preference value: null (absent) and the exact string
  ///     <c>"false"</c> bind false; the exact string <c>"true"</c> binds true; ANY
  ///     other value throws naming the key — nothing is coerced, defaulted, or clamped.</summary>
  public static bool Parse(string? stored)
  {
    return stored switch
    {
      null => false,
      "true" => true,
      "false" => false,
      _ => throw new InvalidOperationException(
          $"{Key} must be 'true' or 'false', got '{stored}'."),
    };
  }
}
