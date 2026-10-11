using System.Globalization;
using System.Text.Json;

namespace eThangAgent.ModelDomain;

/// <summary>A workspace-scoped prior of a session's real output/input token mix, persisted
///     per provider + workspace. Later tasks seed <see cref="MeasuredMixPolicy"/> from
///     <see cref="SeedWeight"/> so a fresh session starts near the mix this workspace has
///     actually shown. Immutable value; the JSON shape is the persisted contract.</summary>
public sealed record MeasuredMixPrior(long TotalInput, long TotalOutput, int TurnCount, DateTimeOffset UpdatedAt)
{
  /// <summary>The seed weight for a new policy: the accumulated output/input ratio, clamped
  ///     to a sane maximum (a handful of pathological turns must not steer the mix), and
  ///     <see cref="MeasuredMixPolicy.DefaultWeight"/> when no input has been observed yet.</summary>
  public decimal SeedWeight => TotalInput == 0
      ? MeasuredMixPolicy.DefaultWeight
      : Math.Min((decimal)TotalOutput / TotalInput, 50m);

  /// <summary>Serializes the persisted shape (camelCase members) so a round-trip preserves
  ///     every member.</summary>
  public string ToJson()
  {
    var payload = new
    {
      totalInput = TotalInput,
      totalOutput = TotalOutput,
      turnCount = TurnCount,
      updatedAt = UpdatedAt,
    };
    return JsonSerializer.Serialize(payload);
  }

  /// <summary>Parses a stored payload. Malformed or corrupt JSON returns false and yields a
  ///     default prior (zero totals) - the caller degrades to no prior and logs (the
  ///     corrupt-to-unset named decision, mirroring <see cref="SmartSelectionOptions.TryFromJson"/>).</summary>
  public static bool TryFromJson(string json, out MeasuredMixPrior prior)
  {
    prior = new MeasuredMixPrior(0L, 0L, 0, DateTimeOffset.MinValue);
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

      if (!root.TryGetProperty("totalInput", out JsonElement totalInEl) || !totalInEl.TryGetInt64(out long totalInput)
          || !root.TryGetProperty("totalOutput", out JsonElement totalOutEl) || !totalOutEl.TryGetInt64(out long totalOutput)
          || !root.TryGetProperty("turnCount", out JsonElement turnsEl) || !turnsEl.TryGetInt32(out int turnCount)
          || !root.TryGetProperty("updatedAt", out JsonElement updatedEl) || !updatedEl.TryGetDateTimeOffset(out DateTimeOffset updatedAt))
      {
        return false;
      }

      prior = new MeasuredMixPrior(totalInput, totalOutput, turnCount, updatedAt);
      return true;
    }
    catch (JsonException)
    {
      return false;
    }
  }

  /// <summary>The workspace-scoped state key holding this prior for one provider.</summary>
  public static string StateKey(string providerName, string workspaceRoot)
      => string.Create(CultureInfo.InvariantCulture, $"measured_mix:{providerName}:{workspaceRoot}");
}
