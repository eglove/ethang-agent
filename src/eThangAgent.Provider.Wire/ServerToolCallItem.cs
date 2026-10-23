using System.Text.Json;
using eThangAgent.ModelDomain;

namespace eThangAgent.Provider.Wire;

/// <summary>Extracts a server-tool call from one output item. Two wire spellings are
///     accepted (both verified live 2026-09): the OpenAI Responses shape whose item
///     type is "&lt;tool&gt;_call" (web_search_call, web_fetch_call, ...) and
///     OpenRouter's own shape whose item type IS "openrouter:&lt;tool&gt;"
///     (openrouter:web_search, ...). The call detail rides "action.query" /
///     "action.url" and the result URLs ride "action.sources[].url" when present.
///     An image-generation item carries its artifact OUTSIDE "action" — under
///     "result" / "imageUrl" as a data:&lt;media&gt;;base64,&lt;payload&gt; URL (verified live
///     2026-10) — so the image is read from there. Unknown item types parse as false,
///     and any malformed image payload parses as a call with no image: the tolerance
///     contract, never a parse failure.</summary>
public static class ServerToolCallItem
{
  private const string CallSuffix = "_call";
  private const string OpenRouterPrefix = "openrouter:";
  private const string DataUrlPrefix = "data:";
  private const string Base64Marker = ";base64,";

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

    call = ParseItem(item, tool);
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

  /// <summary>The call's detail, sources, and image: the action object when present
  ///     (query/url/sources) and the generated image when the item carries one — all
  ///     best-effort, never a parse failure.</summary>
  private static ServerToolCall ParseItem(JsonElement item, string tool)
  {
    string? detail = null;
    IReadOnlyList<string> sources = [];
    if (item.TryGetProperty("action", out JsonElement action) && action.ValueKind == JsonValueKind.Object)
    {
      detail = DetailOf(action);
      sources = ParseSources(action);
    }

    return new ServerToolCall(tool, detail, sources, ParseImage(item));
  }

  /// <summary>The generated image, when the item carries a well-formed one. The payload
  ///     rides "result" (the live spelling) or "imageUrl" as a data URL; anything else —
  ///     a missing member, a non-string, a remote https URL, an unsupported media type,
  ///     invalid base64 — yields null. Null is the caller's no-image signal, never an
  ///     error: the tolerance contract the rest of this parser documents.</summary>
  private static ServerToolCallImage? ParseImage(JsonElement item)
  {
    string? url = StringMemberOf(item, "result") ?? StringMemberOf(item, "imageUrl");
    return url is null ? null : ImageFromDataUrl(url);
  }

  /// <summary>Decodes one data:&lt;media&gt;;base64,&lt;payload&gt; URL into an image carrier, or
  ///     null when the URL is not a base64 data URL in a supported image format. The
  ///     media type is validated against the same image/png | image/jpeg rule every other
  ///     image carrier enforces, and the constructor re-validates the base64.</summary>
  private static ServerToolCallImage? ImageFromDataUrl(string url)
  {
    if (!url.StartsWith(DataUrlPrefix, StringComparison.Ordinal))
    {
      return null;
    }

    int marker = url.IndexOf(Base64Marker, StringComparison.Ordinal);
    if (marker < 0)
    {
      return null;
    }

    string mediaType = url[DataUrlPrefix.Length..marker];
    string payload = url[(marker + Base64Marker.Length)..];
    if (mediaType is not ("image/png" or "image/jpeg") || payload.Length == 0)
    {
      return null;
    }

    try
    {
      return new ServerToolCallImage(mediaType, payload);
    }
    catch (ArgumentException)
    {
      // Invalid base64: tolerance, not a parse failure.
      return null;
    }
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
