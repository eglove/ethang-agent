using System.Text.Json;
using eThangAgent.ModelDomain;

namespace eThangAgent.Provider.Wire;

/// <summary>Extracts a server-tool call from one output item: the wire type is
///     "&lt;tool&gt;_call" (web_search_call, web_fetch_call, image_generation_call, ...)
///     and the query/url detail rides "action.query" / "action.url" when present.
///     Unknown item types parse as false — the tolerance contract.</summary>
public static class ServerToolCallItem
{
  public static bool TryParse(string itemType, JsonElement item, out ServerToolCall call)
  {
    call = new ServerToolCall("?", null);
    if (itemType is null)
    {
      return false;
    }

    if (!itemType.EndsWith("_call", StringComparison.Ordinal) || itemType.Length <= "_call".Length)
    {
      return false;
    }

    string tool = itemType[..^"_call".Length];
    string? detail = null;
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
    }

    call = new ServerToolCall(tool, detail);
    return true;
  }
}

