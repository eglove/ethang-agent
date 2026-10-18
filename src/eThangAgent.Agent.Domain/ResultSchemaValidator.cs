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
    return ValidateType(schemaRoot, report) ?? ValidateProperties(schemaRoot, report)
        ?? ValidateRequired(schemaRoot, report, reportJson);
  }

  /// <summary>The top-level type check, when the schema declares one.</summary>
  private static SchemaValidation? ValidateType(JsonElement schemaRoot, JsonElement report)
  {
    if (!schemaRoot.TryGetProperty("type", out JsonElement typeElement))
    {
      return null;
    }

    string expected = typeElement.GetString() ?? "";
    return TypeMatches(expected, report.ValueKind)
        ? null
        : new SchemaValidation(false, $"expected type '{expected}' but report was '{report.ValueKind}'.", null);
  }

  /// <summary>Per-property type checks, when the schema declares properties and the
  ///     report is an object.</summary>
  private static SchemaValidation? ValidateProperties(JsonElement schemaRoot, JsonElement report)
  {
    if (!schemaRoot.TryGetProperty("properties", out JsonElement properties)
        || report.ValueKind is not JsonValueKind.Object)
    {
      return null;
    }

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

    return null;
  }

  /// <summary>The required-properties check, when the schema declares it and the
  ///     report is an object.</summary>
  private static SchemaValidation ValidateRequired(JsonElement schemaRoot, JsonElement report, string reportJson)
  {
    if (!schemaRoot.TryGetProperty("required", out JsonElement required)
        || report.ValueKind is not JsonValueKind.Object)
    {
      return new SchemaValidation(true, null, reportJson);
    }

    List<string> missing = [.. required.EnumerateArray()
        .Where(entry => entry.ValueKind is JsonValueKind.String)
        .Select(entry => entry.GetString()!)
        .Where(name => !report.TryGetProperty(name, out _))];
    return missing.Count == 0
        ? new SchemaValidation(true, null, reportJson)
        : new SchemaValidation(false, "missing required properties: " + string.Join(", ", missing), null);
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
