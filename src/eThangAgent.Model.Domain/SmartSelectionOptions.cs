using System.Text.Json;

namespace eThangAgent.ModelDomain;

/// <summary>One sort criterion for smart selection: how the best pair is chosen
/// among the candidates that pass every constraint.</summary>
public enum SmartSortCriterion
{
  Price,
  Intelligence,
  Coding,
  Agentic,
}

/// <summary>User constraints for smart selection, persisted per workspace + provider.
/// Prices are $/M tokens (converted to per-token at comparison); score floors are
/// Artificial Analysis indices; every nullable member is unconstrained when null.
/// Immutable: a session reads one snapshot at container build; the Desktop preview
/// passes its live draft per call.</summary>
public sealed record SmartSelectionOptions(
    decimal? MaxInputPerM,
    decimal? MaxOutputPerM,
    double? MinIntelligence,
    double? MinCoding,
    double? MinAgentic,
    int? MinContextWindow,
    SmartSortCriterion Sort)
{
  public static SmartSelectionOptions Default { get; } =
      new(null, null, null, null, null, null, SmartSortCriterion.Price);

  /// <summary>Serializes every member (nulls included) so a round-trip preserves
  /// the exact shape.</summary>
  public string ToJson()
  {
    var payload = new
    {
      maxInputPerM = MaxInputPerM,
      maxOutputPerM = MaxOutputPerM,
      minIntelligence = MinIntelligence,
      minCoding = MinCoding,
      minAgentic = MinAgentic,
      minContextWindow = MinContextWindow,
      sort = Sort.ToString(),
    };
    return JsonSerializer.Serialize(payload);
  }

  /// <summary>Parses a stored payload. Missing members are unconstrained / Price;
  /// malformed JSON or an unknown sort value returns false - the caller degrades
  /// to <see cref="Default"/> and logs (the corrupt-to-unset named decision).</summary>
  public static bool TryFromJson(string json, out SmartSelectionOptions options)
  {
    options = Default;
    if (string.IsNullOrWhiteSpace(json))
    {
      return false;
    }

    try
    {
      using JsonDocument doc = JsonDocument.Parse(json);
      JsonElement root = doc.RootElement;
      if (root.ValueKind != JsonValueKind.Object)
      {
        return false;
      }

      decimal? maxIn = GetDecimalOrNull(root, "maxInputPerM");
      decimal? maxOut = GetDecimalOrNull(root, "maxOutputPerM");
      double? minI = GetDoubleOrNull(root, "minIntelligence");
      double? minC = GetDoubleOrNull(root, "minCoding");
      double? minA = GetDoubleOrNull(root, "minAgentic");
      int? minCtx = GetIntOrNull(root, "minContextWindow");
      SmartSortCriterion sort = SmartSortCriterion.Price;
      if (root.TryGetProperty("sort", out JsonElement sortEl) && sortEl.ValueKind == JsonValueKind.String
          && !Enum.TryParse(sortEl.GetString(), ignoreCase: true, out sort))
      {
        return false;
      }

      options = new SmartSelectionOptions(maxIn, maxOut, minI, minC, minA, minCtx, sort);
      return true;
    }
    catch (JsonException)
    {
      return false;
    }
  }

  private static decimal? GetDecimalOrNull(JsonElement parent, string name)
      => parent.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.Number && el.TryGetDecimal(out decimal d) ? d : null;

  private static double? GetDoubleOrNull(JsonElement parent, string name)
      => parent.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out double d) ? d : null;

  private static int? GetIntOrNull(JsonElement parent, string name)
      => parent.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int i) ? i : null;
}
