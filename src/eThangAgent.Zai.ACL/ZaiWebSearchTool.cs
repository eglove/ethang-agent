using System.Globalization;
using System.Text;
using System.Text.Json;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Zai.ACL;

/// <summary>Searches the live web through z.ai's search API.</summary>
public sealed class ZaiWebSearchTool(HttpClient http, ZaiConfiguration config) : ITool
{
  internal const int SnippetLimit = 800;

  private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
  private readonly ZaiConfiguration _config = config ?? throw new ArgumentNullException(nameof(config));

  public ToolDefinition Definition { get; } = new(
      "web_search",
      "Search the live web via z.ai. timeoutSeconds and query are mandatory; count optionally limits " +
      "results (1..50, default 10); recency optionally filters freshness (exactly one of oneDay, " +
      "oneWeek, oneMonth, oneYear, noLimit). Output begins with an annotation line " +
      "`[web_search '<query>': N result(s)]`; each result follows as a numbered block: `N. <title>`, " +
      "`   url: <link>`, then `   source: <media>` and `   published: <date>` lines when present, then " +
      "the content summary indented three spaces (snippets above 800 characters end with a visible " +
      "' [content truncated]' marker). Errors begin with `Error [Code]:`.",
      [
          new ToolParameter(ToolTimeout.ParameterName, ToolParameterType.WholeNumber, ToolTimeout.ParameterDescription, Minimum: 1),
            new ToolParameter("query", ToolParameterType.Text, "Non-empty search query."),
            new ToolParameter("count", ToolParameterType.WholeNumber, "Result count, 1..50. Default: 10.", Minimum: 1),
            new ToolParameter("recency", ToolParameterType.Text,
                "Freshness filter; exactly one of oneDay, oneWeek, oneMonth, oneYear, noLimit."),
      ],
      ["timeoutSeconds", "query"]);

  public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(input);
    Result<ToolCallEnvelope> envelope = ToolCallEnvelopeParser.Parse(input.Name, input.JsonArguments);
    if (!envelope.IsSuccess)
    {
      return Task.FromResult(ZaiToolHttp.Err(envelope.Error));
    }

    Result<ZaiWebSearchInput> parsed = ZaiWebSearchInput.Create(envelope.Value.Arguments);
    return !parsed.IsSuccess
      ? Task.FromResult(ZaiToolHttp.Err(parsed.Error))
      : ToolExecution.RunAsync(input.Name, envelope.Value.Timeout, token =>
        SearchAsync(parsed.Value, token), ct);
  }

  private async Task<ToolResult> SearchAsync(ZaiWebSearchInput v, CancellationToken ct)
  {
    Dictionary<string, object?> body = new()
    {
      ["search_engine"] = "search-prime",
      ["search_query"] = v.Query,
      ["count"] = v.Count,
    };
    if (v.Recency is not null)
    {
      body["search_recency_filter"] = v.Recency;
    }

    Result<JsonElement> response = await ZaiToolHttp.PostJsonAsync(
        _http, _config, ZaiToolHttp.WebSearchPath, body, ct).ConfigureAwait(false);
    if (!response.IsSuccess)
    {
      return ZaiToolHttp.Err(response.Error);
    }

    List<(string Title, string Link, string Content, string? Media, string? Published)> results = [];
    if (response.Value.TryGetProperty("search_result", out JsonElement items)
        && items.ValueKind == JsonValueKind.Array)
    {
      foreach (JsonElement item in items.EnumerateArray())
      {
        results.Add((
            Text(item, "title"), Text(item, "link"), Text(item, "content"),
            TextOrNull(item, "media"), TextOrNull(item, "publish_date")));
      }
    }

    if (results.Count == 0)
    {
      return new ToolResult(string.Create(CultureInfo.InvariantCulture, $"[web_search '{v.Query}': 0 result(s)]"), false);
    }

    StringBuilder sb = new();
    _ = sb.Append(CultureInfo.InvariantCulture, $"[web_search '{v.Query}': {results.Count} result(s)]");
    int index = 1;
    foreach ((string title, string link, string content, string? media, string? published) in results)
    {
      _ = sb.Append(CultureInfo.InvariantCulture, $"\n{index++}. {title}");
      _ = sb.Append(CultureInfo.InvariantCulture, $"\n   url: {link}");
      if (!string.IsNullOrEmpty(media))
      {
        _ = sb.Append(CultureInfo.InvariantCulture, $"\n   source: {media}");
      }
      if (!string.IsNullOrEmpty(published))
      {
        _ = sb.Append(CultureInfo.InvariantCulture, $"\n   published: {published}");
      }
      _ = sb.Append("\n   ").Append(content.Length > SnippetLimit
          ? content[..SnippetLimit] + " [content truncated]"
          : content);
    }
    return new ToolResult(sb.ToString(), false);
  }

  private static string Text(JsonElement parent, string name)
      => TextOrNull(parent, name) ?? "";

  private static string? TextOrNull(JsonElement parent, string name)
      => parent.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.String
          ? el.GetString()
          : null;
}
