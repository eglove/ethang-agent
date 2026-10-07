using System.Text.Json;
using eThangAgent.ToolDomain;

#pragma warning disable CA2213 // _http is injected (DI-owned)
namespace eThangAgent.OpenRouter.ACL;

/// <summary>Translates the Tool Domain's closed management-command set onto the
///     OpenRouter Management API wire for DEBUGGING: generation lookups
///     (GET /api/v1/generation and /generation/content), credits
///     (GET /api/v1/credits), activity (GET /api/v1/activity), and analytics
///     (GET /api/v1/analytics/meta, POST /api/v1/analytics/query) - all
///     Bearer-authenticated with the MANAGEMENT key (never the model key).
///     Every HTTP failure is a typed outcome carrying the capped error body
///     (the provider's error-is-information rule); parse failures are their own
///     outcome, never exceptions.</summary>
public sealed class OpenRouterManagementClient(HttpClient http, OpenRouterConfiguration config)
    : IOpenRouterManagementAccess
{
  private const int MaxErrorBodyCharacters = 300;

  private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
  private readonly OpenRouterConfiguration _config = config ?? throw new ArgumentNullException(nameof(config));

  /// <summary>Internal pipeline result: either a parsed JSON root or the typed
  ///     failure to surface verbatim. Never both, never neither.</summary>
  private sealed record Wire(JsonElement? Root, OpenRouterManagementOutcome? Failure)
  {
    public static Wire Ok(JsonElement root) => new(root, null);
    public static Wire Fail(OpenRouterManagementOutcome failure) => new(null, failure);
  }

  public async Task<OpenRouterManagementOutcome> ExecuteAsync(
      OpenRouterManagementCommand command, CancellationToken ct = default)
  {
    if (string.IsNullOrWhiteSpace(_config.ManagementKey))
    {
      return new OpenRouterManagementOutcome.Failure("ManagementUnavailable",
          "No OpenRouter management key is configured. Add one under Settings, API Keys.");
    }

    try
    {
      return command switch
      {
        OpenRouterManagementCommand.GetGeneration get => await GenerationAsync(get.Id, ct).ConfigureAwait(false),
        OpenRouterManagementCommand.GetGenerationContent get => await GenerationContentAsync(get.Id, ct).ConfigureAwait(false),
        OpenRouterManagementCommand.GetCredits => await CreditsAsync(ct).ConfigureAwait(false),
        OpenRouterManagementCommand.GetActivity activity => await ActivityAsync(activity, ct).ConfigureAwait(false),
        OpenRouterManagementCommand.GetAnalyticsMeta => await AnalyticsMetaAsync(ct).ConfigureAwait(false),
        OpenRouterManagementCommand.QueryAnalytics query => await AnalyticsQueryAsync(query, ct).ConfigureAwait(false),
        _ => new OpenRouterManagementOutcome.Failure("ManagementError", $"Unknown command: {command.GetType().Name}."),
      };
    }
    catch (HttpRequestException ex)
    {
      return new OpenRouterManagementOutcome.Failure("ManagementError", $"OpenRouter management request failed: {ex.Message}");
    }
    catch (TaskCanceledException) when (!ct.IsCancellationRequested)
    {
      return new OpenRouterManagementOutcome.Failure("ManagementError", "OpenRouter management request timed out.");
    }
  }

  private async Task<OpenRouterManagementOutcome> GenerationAsync(string id, CancellationToken ct)
  {
    using HttpRequestMessage request = new(HttpMethod.Get,
        new Uri(_config.BaseUrl, $"/api/v1/generation?id={Uri.EscapeDataString(id)}"));
    Wire wire = await SendAsync(request, ct).ConfigureAwait(false);
    return Record(wire, data => new OpenRouterManagementOutcome.Generation(ParseGeneration(data)));
  }

  private async Task<OpenRouterManagementOutcome> GenerationContentAsync(string id, CancellationToken ct)
  {
    using HttpRequestMessage request = new(HttpMethod.Get,
        new Uri(_config.BaseUrl, $"/api/v1/generation/content?id={Uri.EscapeDataString(id)}"));
    Wire wire = await SendAsync(request, ct).ConfigureAwait(false);
    return Record(wire, data => new OpenRouterManagementOutcome.GenerationContent(ParseContent(data)));
  }

  private async Task<OpenRouterManagementOutcome> CreditsAsync(CancellationToken ct)
  {
    using HttpRequestMessage request = new(HttpMethod.Get, _config.Endpoint("/api/v1/credits"));
    Wire wire = await SendAsync(request, ct).ConfigureAwait(false);
    return Record(wire, data => new OpenRouterManagementOutcome.Credits(new OpenRouterCreditsItem(
        DoubleOf(data, "total_credits") ?? 0, DoubleOf(data, "total_usage") ?? 0)));
  }

  private async Task<OpenRouterManagementOutcome> AnalyticsMetaAsync(CancellationToken ct)
  {
    using HttpRequestMessage request = new(HttpMethod.Get, _config.Endpoint("/api/v1/analytics/meta"));
    Wire wire = await SendAsync(request, ct).ConfigureAwait(false);
    return Record(wire, data => new OpenRouterManagementOutcome.AnalyticsMeta(
        ParseFields(data, "metrics"), ParseFields(data, "dimensions"), ParseFields(data, "granularities")));
  }

  /// <summary>Folds one wire result into its record outcome: the typed failure
  ///     wins, a missing/ill-typed 'data' object is a parse error, otherwise the
  ///     builder runs over the record element. One shared shape for every
  ///     single-record route.</summary>
  private static OpenRouterManagementOutcome Record(
      Wire wire, Func<JsonElement, OpenRouterManagementOutcome> build)
  {
    return wire switch
    {
      _ when wire.Failure is { } failure => failure,
      _ when !wire.Root!.Value.TryGetProperty("data", out JsonElement data)
          || data.ValueKind != JsonValueKind.Object => NoRecord(),
      _ => build(wire.Root.Value.GetProperty("data")),
    };
  }

  private async Task<OpenRouterManagementOutcome> ActivityAsync(
      OpenRouterManagementCommand.GetActivity activity, CancellationToken ct)
  {
    // Filters ride the query string; Endpoint() escapes '?' as path, so the URI is
    // built directly over the base URL (the same named decision as list pagination).
    List<string> filters = [];
    if (activity.Date is { } date)
    {
      filters.Add($"date={Uri.EscapeDataString(date)}");
    }

    if (activity.GroupBy is { } groupBy)
    {
      filters.Add($"group_by={Uri.EscapeDataString(groupBy)}");
    }

    string query = filters.Count == 0 ? string.Empty : "?" + string.Join("&", filters);
    using HttpRequestMessage request = new(HttpMethod.Get,
        new Uri(_config.BaseUrl, $"/api/v1/activity{query}"));
    Wire wire = await SendAsync(request, ct).ConfigureAwait(false);
    if (wire.Failure is { } failure)
    {
      return failure;
    }

    List<OpenRouterActivityRow> rows = [];
    if (wire.Root!.Value.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Array)
    {
      rows.AddRange(data.EnumerateArray().Select(ParseActivityRow));
    }

    return new OpenRouterManagementOutcome.Activity(rows);
  }

  private async Task<OpenRouterManagementOutcome> AnalyticsQueryAsync(
      OpenRouterManagementCommand.QueryAnalytics query, CancellationToken ct)
  {
    Dictionary<string, object?> fields = new() { ["metrics"] = query.Metrics };
    if (query.Dimensions is { } dimensions)
    {
      fields["dimensions"] = dimensions;
    }

    if (query.Granularity is { } granularity)
    {
      fields["granularity"] = granularity;
    }

    if (query.TimeRangeStart is { } start)
    {
      fields["time_range_start"] = start;
    }

    if (query.TimeRangeEnd is { } end)
    {
      fields["time_range_end"] = end;
    }

    if (query.GroupLimit is { } groupLimit)
    {
      fields["group_limit"] = groupLimit;
    }

    if (query.Limit is { } limit)
    {
      fields["limit"] = limit;
    }

    using HttpRequestMessage request = new(HttpMethod.Post, _config.Endpoint("/api/v1/analytics/query"))
    {
      Content = new StringContent(JsonSerializer.Serialize(fields), System.Text.Encoding.UTF8, "application/json"),
    };
    Wire wire = await SendAsync(request, ct).ConfigureAwait(false);
    if (wire.Failure is { } failure)
    {
      return failure;
    }

    if (!wire.Root!.Value.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Object)
    {
      return NoRecord();
    }

    List<Dictionary<string, string>> rows = [];
    if (data.TryGetProperty("rows", out JsonElement rowsEl) && rowsEl.ValueKind == JsonValueKind.Array)
    {
      foreach (JsonElement row in rowsEl.EnumerateArray())
      {
        Dictionary<string, string> values = [];
        foreach (JsonProperty property in row.EnumerateObject())
        {
          values[property.Name] = property.Value.ValueKind == JsonValueKind.String
              ? property.Value.GetString()!
              : property.Value.GetRawText();
        }
        rows.Add(values);
      }
    }

    return new OpenRouterManagementOutcome.Analytics(new OpenRouterAnalyticsResult(
        rows,
        LongOf(data, "row_count") ?? rows.Count,
        data.TryGetProperty("truncated", out JsonElement truncated) && truncated.ValueKind == JsonValueKind.True,
        LongOf(data, "next_offset")));
  }

  private async Task<Wire> SendAsync(HttpRequestMessage request, CancellationToken ct)
  {
    Authorize(request);
    using HttpResponseMessage response = await _http.SendAsync(request, ct).ConfigureAwait(false);
    string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    if (!response.IsSuccessStatusCode)
    {
      string detail = body.Length > MaxErrorBodyCharacters ? body[..MaxErrorBodyCharacters] + "…" : body;
      return Wire.Fail(new OpenRouterManagementOutcome.Failure("ManagementError",
          $"OpenRouter management API returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}"
          + (detail.Length == 0 ? "." : $": {detail}")));
    }

    try
    {
      using JsonDocument doc = JsonDocument.Parse(body);
      return Wire.Ok(doc.RootElement.Clone());
    }
    catch (JsonException ex)
    {
      return Wire.Fail(new OpenRouterManagementOutcome.Failure("ManagementParseError",
          $"Failed to parse OpenRouter management response: {ex.Message}"));
    }
  }

  private static OpenRouterManagementOutcome.Failure NoRecord() =>
      new("ManagementParseError", "OpenRouter management response carried no record.");

  private void Authorize(HttpRequestMessage request)
  {
    request.Headers.Authorization =
        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _config.ManagementKey);
  }

  private static OpenRouterGenerationRecord ParseGeneration(JsonElement el) => new(
      Optional(el, "id") ?? string.Empty,
      Optional(el, "model"),
      Optional(el, "provider"),
      Optional(el, "route"),
      Optional(el, "created"),
      BoolOf(el, "streamed"),
      BoolOf(el, "cancelled"),
      Optional(el, "finish_reason"),
      Optional(el, "native_finish_reason"),
      Optional(el, "strategy"),
      LongOf(el, "tokens_prompt"),
      LongOf(el, "tokens_completion"),
      LongOf(el, "tokens_reasoning"),
      LongOf(el, "tokens_cached"),
      DoubleOf(el, "cost"),
      DoubleOf(el, "usage"),
      LongOf(el, "latency"),
      LongOf(el, "moderation_latency"),
      LongOf(el, "generation_time"),
      BoolOf(el, "upstream_error"),
      Optional(el, "upstream_id"),
      Optional(el, "request_id"),
      LongOf(el, "seed"),
      Optional(el, "external_user"),
      Optional(el, "endpoint"),
      LongOf(el, "upstream_transport"));

  private static OpenRouterGenerationContent ParseContent(JsonElement el)
  {
    // input/output are objects ({"content": ...} or {"url": ...}) or strings; the
    // content text wins, then a signed URL, then the raw JSON.
    return new OpenRouterGenerationContent(
        Optional(el, "error"),
        ContentOf(el, "input"),
        ContentOf(el, "output"),
        Optional(el, "error_message"));
  }

  private static string? ContentOf(JsonElement el, string name) =>
      !el.TryGetProperty(name, out JsonElement v) ? null : ValueOf(v);

  private static string? ValueOf(JsonElement v) =>
      v.ValueKind switch
      {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.Object => ObjectContentOf(v),
        JsonValueKind.Undefined or JsonValueKind.Array or JsonValueKind.Null
            or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.GetRawText(),
        _ => throw new InvalidOperationException("unreachable: ValueKind is a validated enum"),
      };

  private static string ObjectContentOf(JsonElement v) =>
      StringMember(v, "content") ?? StringMember(v, "url") ?? v.GetRawText();

  private static string? StringMember(JsonElement v, string name)
  {
    return v.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.String
        ? el.GetString()
        : null;
  }

  private static OpenRouterActivityRow ParseActivityRow(JsonElement el) => new(
      Optional(el, "date") ?? string.Empty,
      Optional(el, "model"),
      Optional(el, "provider_name"),
      LongOf(el, "request_count") ?? 0,
      LongOf(el, "tokens_prompt") ?? 0,
      LongOf(el, "tokens_completion") ?? 0,
      LongOf(el, "tokens_reasoning") ?? 0,
      LongOf(el, "tokens_cached") ?? 0,
      DoubleOf(el, "usage") ?? 0,
      DoubleOf(el, "byok_usage"));

  private static List<OpenRouterAnalyticsField> ParseFields(JsonElement data, string name)
  {
    if (!data.TryGetProperty(name, out JsonElement el) || el.ValueKind != JsonValueKind.Array)
    {
      return [];
    }

    List<OpenRouterAnalyticsField> fields = [];
    foreach (JsonElement item in el.EnumerateArray())
    {
      fields.Add(new OpenRouterAnalyticsField(
          Optional(item, "name") ?? string.Empty,
          Optional(item, "label") ?? string.Empty,
          Optional(item, "type")));
    }
    return fields;
  }

  private static string? Optional(JsonElement el, string name) =>
      el.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

  private static bool BoolOf(JsonElement el, string name) =>
      el.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.True;

  private static double? DoubleOf(JsonElement el, string name) =>
      el.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d)
          ? d
          : null;

  private static long? LongOf(JsonElement el, string name) =>
      el.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long l)
          ? l
          : null;
}
