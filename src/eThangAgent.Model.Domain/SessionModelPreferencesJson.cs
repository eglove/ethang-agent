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
///     <para>THE VENDOR TRAVELS WITH EACH ENTRY, because the host resolves a provider per
///     attempt: a carrier that carried only ids would lose which vendor serves a model, and the
///     host would serve the wrong one. A carrier whose entries name no vendor is written in the
///     ids-only shape earlier builds produced, so nothing that reads it has to change.</para>
///     <para>The wire form is OPAQUE JSON: the transport carries the string and never
///     interprets it (the same doctrine as <c>ModelConfig.ProviderSettings</c>). A malformed
///     carrier is a typed failure, never an exception crossing the wire.</para></summary>
public static class SessionModelPreferencesJson
{
  private const string InvalidPreferencesCode = "InvalidModelPreferences";

  /// <summary>Renders the preferences as their wire form.</summary>
  /// <param name="preferences">The preferences to render.</param>
  public static string Serialize(SessionModelPreferences preferences)
  {
    ArgumentNullException.ThrowIfNull(preferences);
    return JsonSerializer.Serialize(preferences);
  }

  /// <summary>Reads the wire form back, or fails with a named error when the carrier is
  ///     not a preferences document. Null/blank is a failure too: a caller that means
  ///     "no preferences" simply omits the carrier.
  ///     <para>A DOCUMENT WITH NO MODEL LIST DESERIALIZES WITH A NULL LIST, which is the same
  ///     statement as a preference the sender never made. Absence is not a failure here.</para>
  ///     <para>AN ENTRY THAT NAMES NO MODEL IS A FAILURE, never a list quietly shortened: the
  ///     list is what the request is built from, so a compacted list would put an order on the
  ///     wire that the carrier never stated.</para>
  ///     <para>AN ENTRY THAT NAMES AN UNKNOWN VENDOR IS A FAILURE naming its position and the
  ///     id: substituting a vendor would send the turn to a provider the sender did not name.
  ///     </para>
  ///     <para>A CARRIER CARRYING BOTH A MODEL-ID LIST AND AN ENTRY LIST IS AMBIGUOUS and is
  ///     refused: this harness writes exactly one of them, so a carrier holding both is input no
  ///     sender produced. The two members are read from the raw document because the carrier
  ///     type writes only one of them.</para></summary>
  /// <param name="json">The carrier text.</param>
  public static Result<SessionModelPreferences> Deserialize(string json)
  {
    if (string.IsNullOrWhiteSpace(json))
    {
      return Result.Failure<SessionModelPreferences>(new DomainError(InvalidPreferencesCode,
          "the model-preferences carrier was empty."));
    }

    try
    {
      using JsonDocument document = JsonDocument.Parse(json);
      if (document.RootElement.ValueKind != JsonValueKind.Object)
      {
        return Result.Failure<SessionModelPreferences>(new DomainError(InvalidPreferencesCode,
            "the model-preferences carrier is not a preferences document."));
      }

      bool bothShapes = document.RootElement.TryGetProperty(EntriesMember, out _)
          && document.RootElement.TryGetProperty(ModelIdsMember, out _);
      if (bothShapes)
      {
        return Result.Failure<SessionModelPreferences>(AmbiguousCarrier());
      }

      SessionModelPreferences? parsed = JsonSerializer.Deserialize<SessionModelPreferences>(json);
      return Read(parsed);
    }
    catch (JsonException ex)
    {
      return Result.Failure<SessionModelPreferences>(new DomainError(InvalidPreferencesCode,
          "the model-preferences carrier is not valid JSON: " + ex.Message));
    }
  }

  /// <summary>The carrier a successful parse produced, or the named failure its entries earn.
  ///     </summary>
  /// <param name="parsed">The deserialized carrier, or null.</param>
  private static Result<SessionModelPreferences> Read(SessionModelPreferences? parsed)
  {
    if (parsed is null)
    {
      return Result.Failure<SessionModelPreferences>(new DomainError(InvalidPreferencesCode,
          "the model-preferences carrier deserialized to null."));
    }

    DomainError? refusal = EntryRefusal(parsed.Entries);
    return refusal is not null
        ? Result.Failure<SessionModelPreferences>(refusal)
        : Result.Success(parsed);
  }

  private const string ModelIdsMember = "ModelIds";
  private const string EntriesMember = "Entries";

  /// <summary>The refusal for a carrier that states its model list twice.</summary>
  private static DomainError AmbiguousCarrier()
      => new(InvalidPreferencesCode,
          "the model-preferences carrier names its model list twice; this harness writes "
          + "either the ids or the entries, never both.");

  /// <summary>The refusal for an entry list naming no model, or an unknown vendor, or null when
  ///     every entry is usable.</summary>
  /// <param name="entries">The carrier's entries, or null.</param>
  private static DomainError? EntryRefusal(IReadOnlyList<ModelSelectionEntry>? entries)
  {
    if (entries is null)
    {
      return null;
    }

    int blank = Enumerable.Range(0, entries.Count).FirstOrDefault(
        position => string.IsNullOrWhiteSpace(entries[position].ModelId), -1);
    if (blank >= 0)
    {
      return new DomainError(InvalidPreferencesCode,
          $"the model-preferences carrier names no model at position {blank + 1}; every entry "
          + "must name a model.");
    }

    int unknown = Enumerable.Range(0, entries.Count).FirstOrDefault(
        position => entries[position].Vendor is { } vendor && !ModelVendors.IsKnown(vendor), -1);
    return unknown < 0
        ? null
        : new DomainError(InvalidPreferencesCode,
            $"the model-preferences carrier names the unknown provider "
            + $"'{entries[unknown].Vendor}' at position {unknown + 1}; known providers are "
            + $"{string.Join(", ", ModelVendors.All)}.");
  }
}
