using System.Globalization;
using System.Text.Json;
using eThangAgent.ModelDomain;

namespace eThangAgent.Cloudflare.ACL;

/// <summary>One model as Cloudflare's account-scoped search endpoint describes it, translated
///     into the harness's provider-neutral entry.
///     <para>EVERY FACT HERE IS READ FROM THE RESPONSE AND NOTHING IS INFERRED. Cloudflare
///     publishes a model's task, its context window, its function-calling support, a plan
///     requirement, and a price list as named PROPERTIES; a property that is absent means the
///     source said nothing, and the entry then carries the absence rather than a zero.</para>
///     <para>THE TASK DECIDES ELIGIBILITY, and it is read from the model's own task name so a
///     task Cloudflare adds later is excluded by default instead of being admitted as chat.
///     </para>
///     <para>PRICES ARRIVE PER MILLION TOKENS and are divided down to a per-token rate, because
///     every consumer of <see cref="ModelProviderEntry"/> prices a call by multiplying tokens by
///     a per-token rate.</para></summary>
internal static class CloudflareModelFacts
{
  /// <summary>The only task name the harness serves: a model whose task is anything else cannot
  ///     take a chat turn.</summary>
  private const string TextGenerationTask = "Text Generation";

  /// <summary>The property names Cloudflare publishes facts under.</summary>
  private const string ContextWindowProperty = "context_window";
  private const string MaxInputTokensProperty = "max_input_tokens";
  private const string FunctionCallingProperty = "function_calling";
  private const string VisionProperty = "vision";
  private const string PriceProperty = "price";
  private const string PaidPlanProperty = "require_workers_paid";

  /// <summary>The price unit a per-million-input rate is stated in.</summary>
  private const string InputUnit = "per M input tokens";

  /// <summary>The price unit a per-million-output rate is stated in.</summary>
  private const string OutputUnit = "per M output tokens";

  private const decimal TokensPerPublishedUnit = 1_000_000m;

  /// <summary>True when the model's task is the one a chat turn can use.</summary>
  /// <param name="taskName">The task name Cloudflare states, or null.</param>
  public static bool IsChatModel(string? taskName)
      => string.Equals(taskName, TextGenerationTask, StringComparison.OrdinalIgnoreCase);

  /// <summary>The provider-neutral entry for one Cloudflare model, or null when the row names
  ///     no model at all.
  ///     <para>A ROW WITH NO NAME IS NOT A MODEL: it cannot be selected, so it contributes
  ///     nothing rather than an entry with an empty id.</para></summary>
  /// <param name="model">One element of the response's <c>result</c> array.</param>
  public static ModelProviderEntry? ToEntry(JsonElement model)
  {
    string? modelId = StringMember(model, "name") ?? StringMember(model, "id");
    if (string.IsNullOrWhiteSpace(modelId))
    {
      return null;
    }

    Dictionary<string, JsonElement> properties = Properties(model);
    (decimal? prompt, decimal? completion) = Prices(properties);
    return new ModelProviderEntry(
        modelId.Trim(),
        ModelVendors.Cloudflare,
        prompt,
        null,
        completion,
        ContextLength(properties),
        MaxCompletionTokens: 0,
        SupportsToolUse: Flag(properties, FunctionCallingProperty),
        SupportsVision: Flag(properties, VisionProperty),
        SupportsTextOutput: true,
        IntelligenceScore: null,
        CodingScore: null,
        AgenticScore: null,
        LatencyMs: null,
        ThroughputTokensPerSec: null,
        Description: StringMember(model, "description"))
    {
      RequiresPaidPlan = PaidPlan(properties),
    };
  }

  /// <summary>The model's published context window, or 0 when it published none.
  ///     <para>ZERO IS THE ENTRY'S WAY OF SAYING 'NOT PUBLISHED', which is why the read tries the
  ///     wider window property before the input-token one and falls back to zero rather than to a
  ///     guess.</para></summary>
  /// <param name="properties">The model's properties, keyed by property id.</param>
  private static int ContextLength(Dictionary<string, JsonElement> properties)
  {
    int stated = Integer(properties, ContextWindowProperty);
    return stated > 0 ? stated : Integer(properties, MaxInputTokensProperty);
  }

  /// <summary>The per-token prices the model publishes, each null when it publishes none.
  ///     <para>A PRICE IS NULL, NEVER ZERO, WHEN ABSENT: a model with no published price is not a
  ///     free model, and coercing the absence to zero would tell the user it costs nothing.
  ///     </para>
  ///     <para>A UNIT THE HARNESS DOES NOT PRICE (per audio minute, per image tile) contributes
  ///     nothing to either rate, because it is not a token price.</para></summary>
  /// <param name="properties">The model's properties, keyed by property id.</param>
  private static (decimal? Prompt, decimal? Completion) Prices(
      Dictionary<string, JsonElement> properties)
  {
    if (!properties.TryGetValue(PriceProperty, out JsonElement price)
        || price.ValueKind != JsonValueKind.Array)
    {
      return (null, null);
    }

    decimal? prompt = null;
    decimal? completion = null;
    foreach (JsonElement line in price.EnumerateArray())
    {
      string? unit = StringMember(line, "unit");
      decimal? amount = DecimalMember(line, "price");
      if (amount is not { } published)
      {
        continue;
      }

      if (string.Equals(unit, InputUnit, StringComparison.OrdinalIgnoreCase))
      {
        prompt = published / TokensPerPublishedUnit;
      }
      else if (string.Equals(unit, OutputUnit, StringComparison.OrdinalIgnoreCase))
      {
        completion = published / TokensPerPublishedUnit;
      }
    }

    return (prompt, completion);
  }

  /// <summary>Whether the model states it needs a paid plan: true when it says so, false when it
  ///     says it does not, and null when it says nothing.
  ///     <para>THE MARKER IS A PROPERTY, and its value is read as a boolean whatever spelling the
  ///     response uses, because a marker written as the string "true" and one written as the
  ///     boolean true state the same fact.</para></summary>
  /// <param name="properties">The model's properties, keyed by property id.</param>
  private static bool? PaidPlan(Dictionary<string, JsonElement> properties)
      => properties.TryGetValue(PaidPlanProperty, out JsonElement marker)
          ? BooleanValue(marker)
          : null;

  /// <summary>Whether a named property states true. An absent property states nothing, which is
  ///     false here because a capability nobody published is not a capability the model has.
  ///     </summary>
  /// <param name="properties">The model's properties, keyed by property id.</param>
  /// <param name="name">The property id.</param>
  private static bool Flag(Dictionary<string, JsonElement> properties, string name)
      => properties.TryGetValue(name, out JsonElement value) && BooleanValue(value) is true;

  /// <summary>A property's value read as a boolean, or null when it states neither true nor
  ///     false. A property whose value is an object or an array states no boolean.</summary>
  /// <param name="value">The property's value.</param>
  private static bool? BooleanValue(JsonElement value)
      => value.ValueKind is JsonValueKind.True or JsonValueKind.False
          ? value.GetBoolean()
          : ParsedBoolean(value);

  /// <summary>A value read as the boolean its text states, or null when it states none.
  ///     </summary>
  /// <param name="value">The value to read.</param>
  private static bool? ParsedBoolean(JsonElement value)
      => value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out bool parsed)
          ? parsed
          : null;

  /// <summary>A numeric property read as an integer, or 0 when it is absent or not a number.
  ///     </summary>
  /// <param name="properties">The model's properties, keyed by property id.</param>
  /// <param name="name">The property id.</param>
  private static int Integer(Dictionary<string, JsonElement> properties, string name)
      => properties.TryGetValue(name, out JsonElement value) ? IntegerValue(value) : 0;

  /// <summary>A value read as an integer, or 0 when it is not one. Cloudflare publishes numeric
  ///     properties as STRINGS, so a quoted number is read as the number it states.</summary>
  /// <param name="value">The value to read.</param>
  private static int IntegerValue(JsonElement value)
  {
    int number = 0;
    if (value.ValueKind == JsonValueKind.Number)
    {
      _ = value.TryGetInt32(out number);
    }
    else if (value.ValueKind == JsonValueKind.String)
    {
      _ = int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture,
          out number);
    }

    return number;
  }

  /// <summary>The model's properties keyed by property id. A later duplicate wins, because the
  ///     response is a list and the last statement about a property is the one it makes.
  ///     </summary>
  /// <param name="model">The model element.</param>
  private static Dictionary<string, JsonElement> Properties(JsonElement model)
  {
    Dictionary<string, JsonElement> properties = [with(StringComparer.Ordinal)];
    if (!model.TryGetProperty("properties", out JsonElement stated)
        || stated.ValueKind != JsonValueKind.Array)
    {
      return properties;
    }

    foreach (JsonElement property in stated.EnumerateArray())
    {
      string? id = StringMember(property, "property_id");
      if (!string.IsNullOrWhiteSpace(id) && property.TryGetProperty("value", out JsonElement value))
      {
        properties[id] = value;
      }
    }

    return properties;
  }

  /// <summary>A string member of an element, or null when it is absent or not a string.
  ///     </summary>
  /// <param name="element">The element to read from.</param>
  /// <param name="name">The member name.</param>
  private static string? StringMember(JsonElement element, string name)
      => element.ValueKind == JsonValueKind.Object
          && element.TryGetProperty(name, out JsonElement value)
          && value.ValueKind == JsonValueKind.String
              ? value.GetString()
              : null;

  /// <summary>A numeric member of an element, or null when it is absent or not a number.
  ///     </summary>
  /// <param name="element">The element to read from.</param>
  /// <param name="name">The member name.</param>
  private static decimal? DecimalMember(JsonElement element, string name)
      => element.ValueKind == JsonValueKind.Object
          && element.TryGetProperty(name, out JsonElement value)
          && value.ValueKind == JsonValueKind.Number
              && value.TryGetDecimal(out decimal number)
                  ? number
                  : null;
}
