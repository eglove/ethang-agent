using System.Text.Json;
using System.Text.Json.Serialization;

namespace eThangAgent.OpenRouter.ACL;

/// <summary>Typed OpenRouter-side request settings: provider routing, the twelve
///     server-side tools, plugins, and server-tool budgets, as one record the ACL
///     serializes to the opaque ProviderSettings JSON the Model Domain carries
///     (never exposed as a type outside this project). All members are optional;
///     disabled toggles and unset members are omitted, so the serialized form of an
///     all-default instance is the empty JSON object.</summary>
/// <remarks>The section records (Routing, ServerTools, Plugins) live beside this
///     record at namespace level rather than nested inside it: C# forbids a nested
///     type and a same-named property in one declaring type, and the outer record's
///     Routing / ServerTools / Plugins properties and the type names are both part
///     of this record's fixed surface.</remarks>
[JsonConverter(typeof(WireConverter))]
public sealed record OpenRouterRequestSettings
{
  /// <summary>Routing preferences for OpenRouter's provider routing: ordering,
  ///     inclusion, exclusion, fallback, sort, quantization, parameter,
  ///     data-collection, model, and route controls.</summary>
  [JsonIgnore]
  public Routing Routing { get; init; } = new();

  /// <summary>The twelve OpenRouter server-side tools; an enabled tool runs on the
  ///     provider side and is keyed by its wire type string when serialized.</summary>
  [JsonIgnore]
  public ServerTools ServerTools { get; init; } = new();

  /// <summary>OpenRouter plugins: web grounding (with optional engine override) and
  ///     response healing.</summary>
  [JsonIgnore]
  public Plugins Plugins { get; init; } = new();

  /// <summary>Serializes to the compact opaque JSON stored in ProviderSettings.
  ///     Null settings serialize to null.</summary>
  public static string? Serialize(OpenRouterRequestSettings? settings)
      => settings is null ? null : JsonSerializer.Serialize(settings, Options);

  /// <summary>Parses persisted ProviderSettings JSON. Null or whitespace yields null.
  ///     Malformed JSON — including JSON null — throws JsonException: corrupt
  ///     preference storage is an infrastructure fault, never silently dropped.</summary>
  public static OpenRouterRequestSettings? Parse(string? json)
      => string.IsNullOrWhiteSpace(json)
          ? null
          : JsonSerializer.Deserialize<OpenRouterRequestSettings>(json, Options)
              ?? throw new JsonException("OpenRouterRequestSettings payload deserialized to null.");

  private static readonly JsonSerializerOptions Options = new();

  /// <summary>Writes only the sections whose record is not the default one, so an
  ///     all-default instance serializes to the empty object. Unknown members are
  ///     skipped on read so the surface can grow server-side without breaking old
  ///     readers of persisted settings.</summary>
#pragma warning disable CA1812 // Instantiated by System.Text.Json through the JsonConverter attribute.
  private sealed class WireConverter : JsonConverter<OpenRouterRequestSettings>
  {
    private static readonly Routing EmptyRouting = new();
    private static readonly ServerTools EmptyServerTools = new();
    private static readonly Plugins EmptyPlugins = new();

    public override OpenRouterRequestSettings Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
      if (reader.TokenType != JsonTokenType.StartObject)
      {
        throw new JsonException("Expected an object for OpenRouterRequestSettings.");
      }

      Routing? routing = null;
      ServerTools? serverTools = null;
      Plugins? plugins = null;
      while (reader.Read())
      {
        if (reader.TokenType == JsonTokenType.EndObject)
        {
          return new OpenRouterRequestSettings { Routing = routing ?? new(), ServerTools = serverTools ?? new(), Plugins = plugins ?? new() };
        }

        string? name = reader.GetString();
        _ = reader.Read();
        switch (name)
        {
          case "routing": routing = JsonSerializer.Deserialize<Routing>(ref reader, options); break;
          case "server_tools": serverTools = JsonSerializer.Deserialize<ServerTools>(ref reader, options); break;
          case "plugins": plugins = JsonSerializer.Deserialize<Plugins>(ref reader, options); break;
          default: reader.Skip(); break;
        }
      }

      throw new JsonException("Unterminated OpenRouterRequestSettings object.");
    }

    public override void Write(Utf8JsonWriter writer, OpenRouterRequestSettings value, JsonSerializerOptions options)
    {
      writer.WriteStartObject();
      if (value.Routing != EmptyRouting)
      {
        writer.WritePropertyName("routing");
        JsonSerializer.Serialize(writer, value.Routing, options);
      }

      if (value.ServerTools != EmptyServerTools)
      {
        writer.WritePropertyName("server_tools");
        JsonSerializer.Serialize(writer, value.ServerTools, options);
      }

      if (value.Plugins != EmptyPlugins)
      {
        writer.WritePropertyName("plugins");
        JsonSerializer.Serialize(writer, value.Plugins, options);
      }

      writer.WriteEndObject();
    }
  }
#pragma warning restore CA1812
}

/// <summary>OpenRouter routing preferences; every member is optional and null
///     members are omitted from the serialized form. Array members compare by
///     element sequence, so a round-trip through Serialize and Parse is
///     value-equal.</summary>
/// <param name="Order">Preferred provider order. Wire: order.</param>
/// <param name="Only">Restrict routing to these providers. Wire: only.</param>
/// <param name="Ignore">Exclude these providers from routing. Wire: ignore.</param>
/// <param name="AllowFallbacks">Whether other providers may serve the request.
///     Wire: allow_fallbacks.</param>
/// <param name="Sort">Routing sort key, e.g. price or throughput. Wire: sort.</param>
/// <param name="Quantizations">Acceptable quantization levels. Wire: quantizations.</param>
/// <param name="RequireParameters">Whether the provider must support all request
///     parameters. Wire: require_parameters.</param>
/// <param name="DataCollection">Provider data-collection policy filter, e.g. deny.
///     Wire: data_collection.</param>
/// <param name="Models">Restrict routing to these models. Wire: models.</param>
/// <param name="Route">Explicit route strategy, e.g. fallback. Wire: route.</param>
#pragma warning disable CA1819 // By-design wire data-carrier: the OpenRouter API's own members are arrays; this record is data, not a mutable surface.
public sealed record Routing(
  [property: JsonPropertyName("order")]
  [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  string[]? Order = null,
  [property: JsonPropertyName("only")]
  [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  string[]? Only = null,
  [property: JsonPropertyName("ignore")]
  [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  string[]? Ignore = null,
  [property: JsonPropertyName("allow_fallbacks")]
  [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  bool? AllowFallbacks = null,
  [property: JsonPropertyName("sort")]
  [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  string? Sort = null,
  [property: JsonPropertyName("quantizations")]
  [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  string[]? Quantizations = null,
  [property: JsonPropertyName("require_parameters")]
  [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  bool? RequireParameters = null,
  [property: JsonPropertyName("data_collection")]
  [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  string? DataCollection = null,
  [property: JsonPropertyName("models")]
  [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  string[]? Models = null,
  [property: JsonPropertyName("route")]
  [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  string? Route = null)
{
  /// <summary>Sequence equality over the array members — the default record
  ///     equality compares arrays by reference, which would break
  ///     Parse(Serialize(x)) equal to x.</summary>
  public bool Equals(Routing? other)
      => other is not null
          && NullableSequenceEqual(Order, other.Order)
          && NullableSequenceEqual(Only, other.Only)
          && NullableSequenceEqual(Ignore, other.Ignore)
          && AllowFallbacks == other.AllowFallbacks
          && Sort == other.Sort
          && NullableSequenceEqual(Quantizations, other.Quantizations)
          && RequireParameters == other.RequireParameters
          && DataCollection == other.DataCollection
          && NullableSequenceEqual(Models, other.Models)
          && Route == other.Route;

  /// <summary>Sequence hash consistent with Equals.</summary>
  public override int GetHashCode()
  {
    HashCode hash = new();
    AddSequence(ref hash, Order);
    AddSequence(ref hash, Only);
    AddSequence(ref hash, Ignore);
    hash.Add(AllowFallbacks);
    hash.Add(Sort);
    AddSequence(ref hash, Quantizations);
    hash.Add(RequireParameters);
    hash.Add(DataCollection);
    AddSequence(ref hash, Models);
    hash.Add(Route);
    return hash.ToHashCode();
  }

  private static bool NullableSequenceEqual(string[]? left, string[]? right)
      => left is null ? right is null : right is not null && left.SequenceEqual(right);

  private static void AddSequence(ref HashCode hash, string[]? values)
  {
    if (values is null)
    {
      return;
    }

    foreach (string value in values)
    {
      hash.Add(value);
    }
  }
}
#pragma warning restore CA1819

/// <summary>The twelve OpenRouter server-side tools and the server-tool budgets.
///     Enabled toggles serialize as the tool's wire type string; disabled toggles
///     and unset budgets are omitted entirely. The array budget member compares by
///     element sequence, so a round-trip through Serialize and Parse is
///     value-equal.</summary>
/// <param name="WebSearch">Web search. Wire type: openrouter:web_search.</param>
/// <param name="WebFetch">Web page fetch. Wire type: openrouter:web_fetch.</param>
/// <param name="Datetime">Current date and time. Wire type: openrouter:datetime.</param>
/// <param name="ImageGeneration">Image generation. Wire type: openrouter:image_generation.</param>
/// <param name="Shell">Command shell. Wire type: openrouter:shell.</param>
/// <param name="ApplyPatch">Patch application. Wire type: openrouter:apply_patch.</param>
/// <param name="Bash">Bash shell. Wire type: openrouter:bash.</param>
/// <param name="Fusion">Fusion retrieval. Wire type: openrouter:fusion.</param>
/// <param name="Advisor">Advisor. Wire type: openrouter:advisor.</param>
/// <param name="Subagent">Sub-agent delegation. Wire type: openrouter:subagent.</param>
/// <param name="SearchModels">Model search. Wire type:
///     openrouter:experimental__search_models.</param>
/// <param name="ToolSearch">Tool search. Wire type: openrouter:tool_search.</param>
/// <param name="MaxToolCalls">Budget: maximum server-tool calls per turn; null is
///     unbounded and omitted. Wire: max_tool_calls.</param>
/// <param name="StopServerToolsWhen">Budget: conditions that stop server tools;
///     null is omitted. Wire: stop_server_tools_when.</param>
#pragma warning disable CA1819 // By-design wire data-carrier: the OpenRouter API's own budget member is an array; this record is data, not a mutable surface.
public sealed record ServerTools(
  [property: JsonIgnore]
  bool WebSearch = false,
  [property: JsonIgnore]
  bool WebFetch = false,
  [property: JsonIgnore]
  bool Datetime = false,
  [property: JsonIgnore]
  bool ImageGeneration = false,
  [property: JsonIgnore]
  bool Shell = false,
  [property: JsonIgnore]
  bool ApplyPatch = false,
  [property: JsonIgnore]
  bool Bash = false,
  [property: JsonIgnore]
  bool Fusion = false,
  [property: JsonIgnore]
  bool Advisor = false,
  [property: JsonIgnore]
  bool Subagent = false,
  [property: JsonIgnore]
  bool SearchModels = false,
  [property: JsonIgnore]
  bool ToolSearch = false,
  [property: JsonPropertyName("max_tool_calls")]
  [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  int? MaxToolCalls = null,
  [property: JsonPropertyName("stop_server_tools_when")]
  [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  string[]? StopServerToolsWhen = null)
{
  /// <summary>Wire view of WebSearch: serialized as its wire type string only when
  ///     the tool is enabled.</summary>
  [JsonPropertyName("openrouter:web_search")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  public bool? WebSearchWire { get => WebSearch ? true : null; init => WebSearch = value.GetValueOrDefault(); }

  /// <summary>Wire view of WebFetch: serialized as its wire type string only when
  ///     the tool is enabled.</summary>
  [JsonPropertyName("openrouter:web_fetch")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  public bool? WebFetchWire { get => WebFetch ? true : null; init => WebFetch = value.GetValueOrDefault(); }

  /// <summary>Wire view of Datetime: serialized as its wire type string only when
  ///     the tool is enabled.</summary>
  [JsonPropertyName("openrouter:datetime")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  public bool? DatetimeWire { get => Datetime ? true : null; init => Datetime = value.GetValueOrDefault(); }

  /// <summary>Wire view of ImageGeneration: serialized as its wire type string only
  ///     when the tool is enabled.</summary>
  [JsonPropertyName("openrouter:image_generation")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  public bool? ImageGenerationWire { get => ImageGeneration ? true : null; init => ImageGeneration = value.GetValueOrDefault(); }

  /// <summary>Wire view of Shell: serialized as its wire type string only when the
  ///     tool is enabled.</summary>
  [JsonPropertyName("openrouter:shell")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  public bool? ShellWire { get => Shell ? true : null; init => Shell = value.GetValueOrDefault(); }

  /// <summary>Wire view of ApplyPatch: serialized as its wire type string only when
  ///     the tool is enabled.</summary>
  [JsonPropertyName("openrouter:apply_patch")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  public bool? ApplyPatchWire { get => ApplyPatch ? true : null; init => ApplyPatch = value.GetValueOrDefault(); }

  /// <summary>Wire view of Bash: serialized as its wire type string only when the
  ///     tool is enabled.</summary>
  [JsonPropertyName("openrouter:bash")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  public bool? BashWire { get => Bash ? true : null; init => Bash = value.GetValueOrDefault(); }

  /// <summary>Wire view of Fusion: serialized as its wire type string only when the
  ///     tool is enabled.</summary>
  [JsonPropertyName("openrouter:fusion")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  public bool? FusionWire { get => Fusion ? true : null; init => Fusion = value.GetValueOrDefault(); }

  /// <summary>Wire view of Advisor: serialized as its wire type string only when the
  ///     tool is enabled.</summary>
  [JsonPropertyName("openrouter:advisor")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  public bool? AdvisorWire { get => Advisor ? true : null; init => Advisor = value.GetValueOrDefault(); }

  /// <summary>Wire view of Subagent: serialized as its wire type string only when
  ///     the tool is enabled.</summary>
  [JsonPropertyName("openrouter:subagent")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  public bool? SubagentWire { get => Subagent ? true : null; init => Subagent = value.GetValueOrDefault(); }

  /// <summary>Wire view of SearchModels: serialized as its wire type string only
  ///     when the tool is enabled.</summary>
  [JsonPropertyName("openrouter:experimental__search_models")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  public bool? SearchModelsWire { get => SearchModels ? true : null; init => SearchModels = value.GetValueOrDefault(); }

  /// <summary>Wire view of ToolSearch: serialized as its wire type string only when
  ///     the tool is enabled.</summary>
  [JsonPropertyName("openrouter:tool_search")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  public bool? ToolSearchWire { get => ToolSearch ? true : null; init => ToolSearch = value.GetValueOrDefault(); }

  /// <summary>Sequence equality over the array budget member — the default record
  ///     equality compares arrays by reference, which would break
  ///     Parse(Serialize(x)) equal to x.</summary>
  public bool Equals(ServerTools? other)
      => other is not null
          && WebSearch == other.WebSearch
          && WebFetch == other.WebFetch
          && Datetime == other.Datetime
          && ImageGeneration == other.ImageGeneration
          && Shell == other.Shell
          && ApplyPatch == other.ApplyPatch
          && Bash == other.Bash
          && Fusion == other.Fusion
          && Advisor == other.Advisor
          && Subagent == other.Subagent
          && SearchModels == other.SearchModels
          && ToolSearch == other.ToolSearch
          && MaxToolCalls == other.MaxToolCalls
          && NullableSequenceEqual(StopServerToolsWhen, other.StopServerToolsWhen);

  /// <summary>Sequence hash consistent with Equals.</summary>
  public override int GetHashCode()
  {
    HashCode hash = new();
    hash.Add(WebSearch);
    hash.Add(WebFetch);
    hash.Add(Datetime);
    hash.Add(ImageGeneration);
    hash.Add(Shell);
    hash.Add(ApplyPatch);
    hash.Add(Bash);
    hash.Add(Fusion);
    hash.Add(Advisor);
    hash.Add(Subagent);
    hash.Add(SearchModels);
    hash.Add(ToolSearch);
    hash.Add(MaxToolCalls);
    if (StopServerToolsWhen is not null)
    {
      hash.Add(StopServerToolsWhen.Length);
      foreach (string value in StopServerToolsWhen)
      {
        hash.Add(value);
      }
    }

    return hash.ToHashCode();
  }

  private static bool NullableSequenceEqual(string[]? left, string[]? right)
      => left is null ? right is null : right is not null && left.SequenceEqual(right);
}
#pragma warning restore CA1819

/// <summary>OpenRouter plugins. Enabled plugins serialize as entries keyed by the
///     plugin id; disabled plugins are omitted entirely.</summary>
/// <param name="WebGrounding">Web grounding plugin. Plugin id: web.</param>
/// <param name="ResponseHealing">Response healing plugin. Plugin id: response-healing.</param>
/// <param name="WebGroundingEngine">Optional search engine override for web
///     grounding; null uses the provider default. C#-only member — never on
///     the wire directly; the engine travels only inside the web entry.</param>
public sealed record Plugins(
  [property: JsonIgnore]
  bool WebGrounding = false,
  [property: JsonIgnore]
  bool ResponseHealing = false,
  [property: JsonIgnore]
  string? WebGroundingEngine = null)
{
  /// <summary>Wire entry for the web grounding plugin: serialized only when the
  ///     plugin is enabled, carrying the optional engine override.</summary>
  [JsonPropertyName("web")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  public WebGroundingPlugin? Web
  {
    get => WebGrounding ? new WebGroundingPlugin(WebGroundingEngine) : null;
    init
    {
      WebGrounding = value is not null;
      WebGroundingEngine = value?.Engine;
    }
  }

  /// <summary>Wire entry for the response healing plugin: serialized only when the
  ///     plugin is enabled.</summary>
  [JsonPropertyName("response-healing")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  public ResponseHealingPlugin? ResponseHealingEntry
  {
    get => ResponseHealing ? new ResponseHealingPlugin() : null;
    init => ResponseHealing = value is not null;
  }
}

/// <summary>Wire shape of the web grounding plugin entry: the optional engine
///     override; a null engine is omitted so the entry serializes as the empty
///     object.</summary>
/// <param name="Engine">Optional search engine override, e.g. exa. Wire: engine.</param>
public sealed record WebGroundingPlugin(
  [property: JsonPropertyName("engine")]
  [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  string? Engine = null);

/// <summary>Wire shape of the response healing plugin entry: the plugin id carries
///     no settings today; the record keeps the shape extensible.</summary>
#pragma warning disable S2094 // Deliberate empty record: presence of the entry IS the plugin switch; settings extend it later.
public sealed record ResponseHealingPlugin();
#pragma warning restore S2094
