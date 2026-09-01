using System.Text.Json;

namespace eThangAgent.AgentDomain;

/// <summary>Dependency-free JSON-schema subset validation for structured child results
///     (source step 10): top-level type object/array/string/number/boolean, object
///     properties, and required. Deliberately NOT a full JSON-schema engine — the seam
///     exists so the repair-round policy (approved D3) has a well-defined error to feed back.
///     Malformed schema JSON is an infrastructure fault (throws); malformed REPORT JSON is a
///     validation failure, not an exception.</summary>
public static class ResultSchemaValidator
{
  public static SchemaValidation Validate(string schemaJson, string reportJson)
  {
    using JsonDocument schema = JsonDocument.Parse(schemaJson);
    JsonElement report;
    try
    {
      report = JsonDocument.Parse(reportJson).RootElement;
    }
    catch (JsonException ex)
    {
      return new SchemaValidation(false, $"report is not valid JSON: {ex.Message}", null);
    }

    JsonElement schemaRoot = schema.RootElement;
    if (schemaRoot.TryGetProperty("type", out JsonElement typeElement))
    {
      string expected = typeElement.GetString() ?? "";
      if (!TypeMatches(expected, report.ValueKind))
      {
        return new SchemaValidation(false, $"expected type '{expected}' but report was '{report.ValueKind}'.", null);
      }
    }

    if (schemaRoot.TryGetProperty("properties", out JsonElement properties) && report.ValueKind is JsonValueKind.Object)
    {
      foreach (JsonProperty property in properties.EnumerateObject())
      {
        if (report.TryGetProperty(property.Name, out JsonElement value)
            && property.Value.TryGetProperty("type", out JsonElement propertyType)
            && !TypeMatches(propertyType.GetString() ?? "", value.ValueKind))
        {
          return new SchemaValidation(false,
              $"property '{property.Name}' expected type '{propertyType.GetString()}' but was '{value.ValueKind}'.", null);
        }
      }
    }

    if (schemaRoot.TryGetProperty("required", out JsonElement required) && report.ValueKind is JsonValueKind.Object)
    {
      List<string> missing = [.. required.EnumerateArray()
          .Where(entry => entry.ValueKind is JsonValueKind.String)
          .Select(entry => entry.GetString()!)
          .Where(name => !report.TryGetProperty(name, out _))];
      if (missing.Count > 0)
      {
        return new SchemaValidation(false, "missing required properties: " + string.Join(", ", missing), null);
      }
    }

    return new SchemaValidation(true, null, reportJson);
  }

  private static bool TypeMatches(string expected, JsonValueKind kind)
      => expected switch
      {
        "object" => kind is JsonValueKind.Object,
        "array" => kind is JsonValueKind.Array,
        "string" => kind is JsonValueKind.String,
        "number" => kind is JsonValueKind.Number,
        "boolean" => kind is JsonValueKind.True or JsonValueKind.False,
        _ => true, // unknown declared type: the subset does not enforce it
      };
}

/// <summary>The outcome of one validation pass. NormalizedJson carries the report only on
///     success; Error carries the exact feedback for the repair round (approved D3).</summary>
public sealed record SchemaValidation(bool IsValid, string? Error, string? NormalizedJson);
