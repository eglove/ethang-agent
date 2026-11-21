using System.Text.Json;

namespace eThangAgent.Transport.ACL;

/// <summary>The JSON payload of a <c>model-dropped</c> event: one model the turn's walk
///     removed from the session's surviving list, with the reason the provider gave.
///     <para>WHY IT TRAVELS: the walk runs in the HOST process, so the app's own surviving
///     list would never learn that a model was dropped — the model that refused would keep
///     being sent on later turns. The kind set is closed and strict, and app and host ship as
///     one build, so extending it on both sides needs no negotiation.</para>
///     <para>EVERY MEMBER IS A STRING OR NULL, and the app states the drop in its own words:
///     the wire carries the facts, never a rendered line.</para></summary>
/// <param name="ModelId">The model the walk dropped.</param>
/// <param name="Vendor">The vendor that served it, or null when none was named.</param>
/// <param name="Code">The domain error code the attempt failed with.</param>
/// <param name="Reason">The provider's own message.</param>
public sealed record RootTurnModelDropPayload(
    string? ModelId = null,
    string? Vendor = null,
    string? Code = null,
    string? Reason = null)
{
  /// <summary>Serializes one drop for a <c>model-dropped</c> event.</summary>
  /// <param name="modelId">The model the walk dropped.</param>
  /// <param name="vendor">The vendor that served it, or null.</param>
  /// <param name="code">The code it failed with.</param>
  /// <param name="reason">Its own message.</param>
  public static string Serialize(string modelId, string? vendor, string code, string reason) =>
      JsonSerializer.Serialize(new RootTurnModelDropPayload(modelId, vendor, code, reason));

  /// <summary>Parses one JSON drop payload, or null when it does not decode or names no model.
  ///     The one reader of the shape: the host writes it through <see cref="Serialize"/>, the
  ///     app (and tests) read it here.</summary>
  /// <param name="payload">The event's payload.</param>
  public static RootTurnModelDropPayload? Parse(string payload)
  {
    ArgumentNullException.ThrowIfNull(payload);
    try
    {
      RootTurnModelDropPayload? parsed = JsonSerializer.Deserialize<RootTurnModelDropPayload>(payload);
      return string.IsNullOrWhiteSpace(parsed?.ModelId) ? null : parsed;
    }
    catch (JsonException)
    {
      return null;
    }
  }
}
