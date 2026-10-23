using eThangAgent.SharedKernel;

namespace eThangAgent.Composition;

/// <summary>The compaction-handoff preference (issue #234): the key the handoff
///     enablement delegate reads live at compaction time, and its STRICT parser.
///     Absent means disabled; only the exact lowercase strings bind; anything else is a
///     TYPED FAILURE naming the key — never coerced, and never thrown across a seam:
///     the compactor consumes the failure through the Result seam and reports it as a
///     compaction failure (the existing '[Context compaction failed: ...]' notice path),
///     so a misconfigured preference is visible at the surface without an untyped fault.
///     bool.TryParse would accept any case, which is coercion, so it is not used.</summary>
public static class SessionHandoffPreferences
{
  /// <summary>The app-preference key enabling the compaction handoff.</summary>
  public const string Key = "compaction_handoff_enabled";

  /// <summary>Reads the stored preference value: null (absent) and the exact string
  ///     <c>"false"</c> bind false; the exact string <c>"true"</c> binds true; ANY
  ///     other value is a typed failure carrying <c>InvalidPreferenceValue</c> and a
  ///     message naming the key — nothing is coerced, defaulted, or clamped.</summary>
  public static Result<bool> TryParse(string? stored)
  {
    return stored switch
    {
      null => Result.Success(false),
      "true" => Result.Success(true),
      "false" => Result.Success(false),
      _ => Result.Failure<bool>(new DomainError("InvalidPreferenceValue",
          $"{Key} must be 'true' or 'false', got '{stored}'.")),
    };
  }
}
