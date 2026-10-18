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

    string? tool = ToolNameOf(itemType);
    if (tool is null)
    {
      return false;
    }

    call = ParseAction(item, tool);
    return true;
  }

  /// <summary>The tool name stripped of its wire spelling: "&lt;tool&gt;_call" and
  ///     "openrouter:&lt;tool&gt;" both yield "&lt;tool&gt;"; any other shape (or a
  ///     bare prefix/suffix with no tool name) yields null — the tolerance contract.</summary>
  private static string? ToolNameOf(string itemType)
    => itemType.EndsWith(CallSuffix, StringComparison.Ordinal) && itemType.Length > CallSuffix.Length
        ? itemType[..^CallSuffix.Length]
        : StripOpenRouterPrefix(itemType);

  private static string? StripOpenRouterPrefix(string itemType)
    => itemType.StartsWith(OpenRouterPrefix, StringComparison.Ordinal) && itemType.Length > OpenRouterPrefix.Length
        ? itemType[OpenRouterPrefix.Length..] : null;

  /// <summary>The call's detail and sources from the item's "action" object: query
  ///     wins over url for the detail, and the result URLs ride "sources" — all
  ///     best-effort, never a parse failure.</summary>
  private static ServerToolCall ParseAction(JsonElement item, string tool)
  {
    string? detail = null;
    IReadOnlyList<string> sources = [];
    if (item.TryGetProperty("action", out JsonElement action) && action.ValueKind == JsonValueKind.Object)
    {
      detail = DetailOf(action);
      sources = ParseSources(action);
    }

    return new ServerToolCall(tool, detail, sources);
  }

  /// <summary>The action's detail string: its "query" when present, else its "url"
  ///     when present, else null.</summary>
  private static string? DetailOf(JsonElement action)
  {
    string? query = StringMemberOf(action, "query");
    return query is not null ? query : StringMemberOf(action, "url");
  }

  /// <summary>The parent's string member when present and well-typed, else null.</summary>
  private static string? StringMemberOf(JsonElement parent, string name)
    => parent.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.String
        ? el.GetString() : null;

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
