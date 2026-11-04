using System.Text.Json;

namespace eThangAgent.Lsp.ACL;

public sealed partial class LspSymbolService
{
  public Task<LspSymbolResult> CallHierarchyIncomingAsync(string uri, string symbolName, int occurrence = 0, int depth = 1, CancellationToken ct = default)
    => CallHierarchyAsync(uri, symbolName, occurrence, depth, true, ct);

  public Task<LspSymbolResult> CallHierarchyOutgoingAsync(string uri, string symbolName, int occurrence = 0, int depth = 1, CancellationToken ct = default)
    => CallHierarchyAsync(uri, symbolName, occurrence, depth, false, ct);

  private async Task<LspSymbolResult> CallHierarchyAsync(string uri, string symbolName, int occurrence, int depth, bool incoming, CancellationToken ct)
  {
    if (_transport is null)
    {
      return Unavailable();
    }

    if (depth is < 1 or > 2)
    {
      return new LspSymbolResult(LspSymbolState.Unsupported, [], occurrence, 1, DepthMessage(depth));
    }

    if (!_capabilities.Supports("callHierarchy/prepare"))
    {
      return Unsupported("callHierarchy/prepare");
    }

    LspSymbolResolution? resolution = await ResolveAsync(uri, symbolName, occurrence, ct).ConfigureAwait(false);
    if (resolution is null)
    {
      return Unsupported("textDocument/documentSymbol");
    }

    if (!resolution.Found)
    {
      return ReferencedOnly(symbolName, uri);
    }

    string prepParams = BuildPrepParams(uri, resolution);
    LspRequestResult prep = await _transport.SendRequestAsync("callHierarchy/prepare", prepParams, ct).ConfigureAwait(false);
    if (!prep.IsSuccess)
    {
      if (prep.ErrorCode == "Unsupported")
      {
        return Unsupported("callHierarchy/prepare");
      }

      return new LspSymbolResult(LspSymbolState.Degraded, [], occurrence, resolution.TotalOccurrences, prep.ErrorMessage);
    }

    string? itemJson = FirstItemJson(prep.ResultJson);
    if (itemJson is null)
    {
      return new LspSymbolResult(LspSymbolState.Ready, [], occurrence, resolution.TotalOccurrences);
    }

    string method = incoming ? "callHierarchy/incoming" : "callHierarchy/outgoing";
    List<LspSymbolSite> all = [];
    List<string> frontier = [itemJson];
    for (int level = 0; level < depth; level++)
    {
      List<string> next = [];
      foreach (string item in frontier)
      {
        string paramsJson = "{\"item\":" + item + "}";
        LspRequestResult resp = await _transport.SendRequestAsync(method, paramsJson, ct).ConfigureAwait(false);
        if (!resp.IsSuccess)
        {
          if (resp.ErrorCode == "Unsupported")
          {
            return Unsupported(method);
          }

          return new LspSymbolResult(LspSymbolState.Degraded, all, occurrence, resolution.TotalOccurrences, resp.ErrorMessage);
        }

        List<LspSymbolSite> sites = ParseCallHierarchyItems(resp.ResultJson, incoming);
        all.AddRange(sites);
        if (level + 1 < depth)
        {
          next.AddRange(ExtractItems(resp.ResultJson, incoming));
        }
      }

      frontier = next;
    }

    return new LspSymbolResult(LspSymbolState.Ready, all, occurrence, resolution.TotalOccurrences);
  }

  private static string DepthMessage(int depth) => "depth must be 1 or 2 (got " + depth + ")";

  private static string BuildPrepParams(string uri, LspSymbolResolution resolution)
    => "{\"textDocument\":{\"uri\":\"" + uri + "\"},\"position\":{\"line\":" + resolution.Line + ",\"character\":" + resolution.Character + "}}";

  private static string? FirstItemJson(string? json)
  {
    if (string.IsNullOrWhiteSpace(json))
    {
      return null;
    }

    try
    {
      using JsonDocument doc = JsonDocument.Parse(json);
      JsonElement root = doc.RootElement;
      if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
      {
        return root[0].GetRawText();
      }

      if (root.ValueKind == JsonValueKind.Object)
      {
        return root.GetRawText();
      }
    }
    catch (JsonException)
    {
    }

    return null;
  }

  private static List<string> ExtractItems(string? json, bool incoming)
  {
    List<string> items = [];
    if (string.IsNullOrWhiteSpace(json))
    {
      return items;
    }

    try
    {
      using JsonDocument doc = JsonDocument.Parse(json);
      JsonElement root = doc.RootElement;
      if (root.ValueKind != JsonValueKind.Array)
      {
        return items;
      }

      foreach (JsonElement el in root.EnumerateArray())
      {
        if (incoming && el.TryGetProperty("from", out JsonElement from))
        {
          items.Add(from.GetRawText());
        }
        else if (!incoming && el.TryGetProperty("to", out JsonElement to))
        {
          items.Add(to.GetRawText());
        }
      }
    }
    catch (JsonException)
    {
    }

    return items;
  }

  private static List<LspSymbolSite> ParseCallHierarchyItems(string? json, bool incoming)
  {
    List<LspSymbolSite> sites = [];
    if (string.IsNullOrWhiteSpace(json))
    {
      return sites;
    }

    try
    {
      using JsonDocument doc = JsonDocument.Parse(json);
      JsonElement root = doc.RootElement;
      if (root.ValueKind != JsonValueKind.Array)
      {
        return sites;
      }

      foreach (JsonElement el in root.EnumerateArray())
      {
        JsonElement item = el;
        if (incoming && el.TryGetProperty("from", out JsonElement from))
        {
          item = from;
        }
        else if (!incoming && el.TryGetProperty("to", out JsonElement to))
        {
          item = to;
        }

        string uri = item.TryGetProperty("uri", out JsonElement u) ? u.GetString() ?? string.Empty : string.Empty;
        if (!item.TryGetProperty("range", out JsonElement range))
        {
          continue;
        }

        int line = range.GetProperty("start").GetProperty("line").GetInt32();
        int ch = range.GetProperty("start").GetProperty("character").GetInt32();
        string name = item.TryGetProperty("name", out JsonElement n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : string.Empty;
        sites.Add(new LspSymbolSite(uri, line, ch, name));
      }
    }
    catch (JsonException)
    {
    }

    return sites;
  }
}
