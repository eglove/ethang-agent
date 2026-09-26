using System.Text.Json;
using eThangAgent.ModelDomain;

namespace eThangAgent.Provider.Wire;

/// <summary>Extracts a server-tool call from one output item. Two wire spellings are
///     accepted (both verified live 2026-09): the OpenAI Responses shape whose item
///     type is "&lt;tool&gt;_call" (web_search_call, web_fetch_call, ...) and
///     OpenRouter's own shape whose item type IS "openrouter:&lt;tool&gt;"
///     (openrouter:web_search, ...). The call detail rides "action.query" /
///     "action.url" and the result URLs ride "action.sources[].url" when present.
///     Unknown item types parse as false — the tolerance contract.</summary>
public static class ServerToolCallItem
{
  private const string CallSuffix = "_call";
  private const string OpenRouterPrefix = "openrouter:";

  public static bool TryParse(string itemType, JsonElement item, out ServerToolCall call)
  {
    call = new ServerToolCall("?", null);
    if (itemType is null)
    {
      return false;
    }

    string? tool;
    if (itemType.EndsWith(CallSuffix, StringComparison.Ordinal) && itemType.Length > CallSuffix.Length)
    {
      tool = itemType[..^CallSuffix.Length];
    }
    else if (itemType.StartsWith(OpenRouterPrefix, StringComparison.Ordinal) && itemType.Length > OpenRouterPrefix.Length)
    {
      tool = itemType[OpenRouterPrefix.Length..];
    }
    else
    {
      return false;
    }

    string? detail = null;
    IReadOnlyList<string> sources = [];
    if (item.TryGetProperty("action", out JsonElement action) && action.ValueKind == JsonValueKind.Object)
    {
      if (action.TryGetProperty("query", out JsonElement query) && query.ValueKind == JsonValueKind.String)
      {
        detail = query.GetString();
      }
      else if (action.TryGetProperty("url", out JsonElement url) && url.ValueKind == JsonValueKind.String)
      {
        detail = url.GetString();
      }

      sources = ParseSources(action);
    }

    call = new ServerToolCall(tool, detail, sources);
    return true;
  }

  /// <summary>The action's result URLs: "sources" entries that are objects carrying a
  ///     string "url" member (the live web_search shape). Anything else — missing
  ///     member, non-array, foreign entry shapes — yields an empty list: detail is
  ///     best-effort, never a parse failure.</summary>
  private static List<string> ParseSources(JsonElement action)
  {
    if (!action.TryGetProperty("sources", out JsonElement sources) || sources.ValueKind != JsonValueKind.Array)
    {
      return [];
    }

    List<string> urls = [];
    foreach (JsonElement entry in sources.EnumerateArray())
    {
      if (entry.ValueKind == JsonValueKind.Object
          && entry.TryGetProperty("url", out JsonElement url)
          && url.ValueKind == JsonValueKind.String
          && url.GetString() is { Length: > 0 } parsed)
      {
        urls.Add(parsed);
      }
    }

    return urls;
  }
}
