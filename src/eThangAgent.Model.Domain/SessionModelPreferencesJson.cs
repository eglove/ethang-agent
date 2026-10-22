using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ModelDomain;

/// <summary>Codec for the session's live model preferences as they travel between
///     processes. The app owns the model picker; the conversation's agent lives in its own
///     host process, so the choice must CROSS the boundary or the host resolves from the
///     persisted pin alone.
///     <para>WHY THIS EXISTS (2026-10-22): the host container's
///     <see cref="SessionModelPreferences"/> is a fresh all-null singleton. A turn that did
///     not carry the app's choice therefore resolved from the persisted row — which the
///     bootstrap stamps with the routing id, i.e. "nothing selected" — and every turn
///     served <c>openrouter/auto</c> while the app displayed the user's pick.</para>
///     <para>The wire form is OPAQUE JSON: the transport carries the string and never
///     interprets it (the same doctrine as <c>ModelConfig.ProviderSettings</c>). A
///     malformed carrier is a typed failure, never an exception crossing the wire.</para></summary>
public static class SessionModelPreferencesJson
{
  /// <summary>Renders the preferences as their wire form.</summary>
  public static string Serialize(SessionModelPreferences preferences)
  {
    ArgumentNullException.ThrowIfNull(preferences);
    return JsonSerializer.Serialize(preferences);
  }

  /// <summary>Reads the wire form back, or fails with a named error when the carrier is
  ///     not a preferences document. Null/blank is a failure too: a caller that means
  ///     "no preferences" simply omits the carrier.</summary>
  public static Result<SessionModelPreferences> Deserialize(string json)
  {
    if (string.IsNullOrWhiteSpace(json))
    {
      return Result.Failure<SessionModelPreferences>(new DomainError("InvalidModelPreferences",
          "the model-preferences carrier was empty."));
    }

    try
    {
      SessionModelPreferences? parsed = JsonSerializer.Deserialize<SessionModelPreferences>(json);
      return parsed is null
          ? Result.Failure<SessionModelPreferences>(new DomainError("InvalidModelPreferences",
              "the model-preferences carrier deserialized to null."))
          : Result.Success(parsed);
    }
#pragma warning disable CA1031 // A malformed carrier is an EXPECTED condition: it becomes a named error.
    catch (JsonException ex)
#pragma warning restore CA1031
    {
      return Result.Failure<SessionModelPreferences>(new DomainError("InvalidModelPreferences",
          "the model-preferences carrier is not valid JSON: " + ex.Message));
    }
  }
}
