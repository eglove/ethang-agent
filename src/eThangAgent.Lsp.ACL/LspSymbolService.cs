using System.Text.Json;

namespace eThangAgent.Lsp.ACL;

/// <summary>Symbol navigation state (issue #357), matching the diagnostics
/// honesty labels plus Unsupported for unadvertised capabilities.</summary>
public enum LspSymbolState
{
  Ready,
  Starting,
  Stale,
  Degraded,
  Unavailable,
  Unsupported,
}

/// <summary>One site returned by a symbol query.</summary>
#pragma warning disable CA1056 // Named decision: LSP file URIs are opaque strings on the wire; the ACL owns URI mapping.
public sealed record LspSymbolSite(string Uri, int Line, int Character, string SymbolName);
#pragma warning restore CA1056

/// <summary>Result of a symbol query.</summary>
public sealed record LspSymbolResult(LspSymbolState State, IReadOnlyList<LspSymbolSite> Sites, int OccurrenceUsed = 0, int TotalOccurrences = 1, string? Message = null);

/// <summary>Outcome of resolving a symbol name against a document-symbols payload.</summary>
public sealed record LspSymbolResolution(bool Found, int Line, int Character, int TotalOccurrences);

/// <summary>Production symbol navigation service (issue #357): resolves a symbol
/// name to a position via textDocument/documentSymbol, then issues the requested
/// operation. Capability-gated: an unadvertised capability returns Unsupported
/// without touching the wire. A symbol only referenced (not defined) in the named
/// file resolves to Unsupported, never a guessed position.</summary>
public sealed partial class LspSymbolService
{
  private readonly LspJsonRpcTransport? _transport;
  private readonly LspCapabilities _capabilities;

  public LspSymbolService(LspJsonRpcTransport? transport, LspCapabilities capabilities)
    => (_transport, _capabilities) = (transport, capabilities ?? throw new ArgumentNullException(nameof(capabilities)));

  public static LspSymbolResolution ResolveSymbol(string documentSymbolsJson, string symbolName, int occurrence)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(documentSymbolsJson);
    ArgumentException.ThrowIfNullOrWhiteSpace(symbolName);
    List<(int Line, int Character)> matches = [];
    try
    {
      using JsonDocument doc = JsonDocument.Parse(documentSymbolsJson);
      CollectMatches(doc.RootElement, symbolName, matches);
    }
    catch (JsonException)
    {
      return new LspSymbolResolution(false, -1, -1, 0);
    }

    if (occurrence < 0 || occurrence >= matches.Count)
    {
      return new LspSymbolResolution(false, -1, -1, matches.Count);
    }

    (int line, int ch) = matches[occurrence];
    return new LspSymbolResolution(true, line, ch, matches.Count);
  }

  private static void CollectMatches(JsonElement el, string symbolName, List<(int Line, int Character)> matches)
  {
    if (el.ValueKind == JsonValueKind.Array)
    {
      foreach (JsonElement item in el.EnumerateArray())
      {
        CollectMatches(item, symbolName, matches);
      }

      return;
    }

    if (el.ValueKind != JsonValueKind.Object)
    {
      return;
    }

    if (el.TryGetProperty("name", out JsonElement nameEl)
        && nameEl.ValueKind == JsonValueKind.String
        && string.Equals(nameEl.GetString(), symbolName, StringComparison.Ordinal))
    {
      JsonElement range = el.TryGetProperty("selectionRange", out JsonElement sel) ? sel : el.GetProperty("range");
      int line = range.GetProperty("start").GetProperty("line").GetInt32();
      int ch = range.GetProperty("start").GetProperty("character").GetInt32();
      matches.Add((line, ch));
    }

    if (el.TryGetProperty("children", out JsonElement children))
    {
      CollectMatches(children, symbolName, matches);
    }
  }

  private async Task<LspSymbolResolution?> ResolveAsync(string uri, string symbolName, int occurrence, CancellationToken ct)
  {
    if (_transport is null)
    {
      return null;
    }

    if (!_capabilities.Supports("textDocument/documentSymbol"))
    {
      return null;
    }

    string paramsJson = "{\"textDocument\":{\"uri\":\"" + uri + "\"}}";
    LspRequestResult response = await _transport.SendRequestAsync("textDocument/documentSymbol", paramsJson, ct).ConfigureAwait(false);
    if (!response.IsSuccess || string.IsNullOrWhiteSpace(response.ResultJson))
    {
      return null;
    }

    return ResolveSymbol(response.ResultJson, symbolName, occurrence);
  }

  private static LspSymbolResult Unavailable() => new(LspSymbolState.Unavailable, [], 0, 1, "no language server for this workspace");

  private static LspSymbolResult Unsupported(string method) => new(LspSymbolState.Unsupported, [], 0, 1, "capability not advertised for " + method);

  private static LspSymbolResult ReferencedOnly(string symbolName, string uri)
    => new(LspSymbolState.Unsupported, [], 0, 1, "symbol '" + symbolName + "' is only referenced, not defined, in " + uri + "; position cannot be resolved");

  private async Task<LspSymbolResult> QueryAsync(string uri, string symbolName, int occurrence, string method, Func<string, string, LspSymbolResolution, string> buildParams, CancellationToken ct)
  {
    if (_transport is null)
    {
      return Unavailable();
    }

    if (!_capabilities.Supports(method))
    {
      return Unsupported(method);
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

    string paramsJson = buildParams(uri, symbolName, resolution);
    LspRequestResult response = await _transport.SendRequestAsync(method, paramsJson, ct).ConfigureAwait(false);
    if (!response.IsSuccess)
    {
      if (response.ErrorCode == "Unsupported")
      {
        return Unsupported(method);
      }

      return new LspSymbolResult(LspSymbolState.Degraded, [], resolution.OccurrenceUsedOr(occurrence), resolution.TotalOccurrences, response.ErrorMessage);
    }

    List<LspSymbolSite> sites = ParseLocations(response.ResultJson, symbolName);
    return new LspSymbolResult(LspSymbolState.Ready, sites, occurrence, resolution.TotalOccurrences);
  }

  private static List<LspSymbolSite> ParseLocations(string? json, string symbolName)
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
      if (root.ValueKind == JsonValueKind.Array)
      {
        foreach (JsonElement item in root.EnumerateArray())
        {
          sites.Add(ParseLocation(item, symbolName));
        }
      }
      else if (root.ValueKind == JsonValueKind.Object)
      {
        sites.Add(ParseLocation(root, symbolName));
      }
    }
    catch (JsonException)
    {
    }

    return sites;
  }

  private static LspSymbolSite ParseLocation(JsonElement item, string symbolName)
  {
    string uri = item.TryGetProperty("uri", out JsonElement u) ? u.GetString() ?? string.Empty : string.Empty;
    JsonElement range = item.GetProperty("range");
    int line = range.GetProperty("start").GetProperty("line").GetInt32();
    int ch = range.GetProperty("start").GetProperty("character").GetInt32();
    string name = item.TryGetProperty("name", out JsonElement n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : symbolName;
    return new LspSymbolSite(uri, line, ch, name);
  }

  public Task<LspSymbolResult> DefinitionAsync(string uri, string symbolName, int occurrence = 0, CancellationToken ct = default)
    => QueryAsync(uri, symbolName, occurrence, "textDocument/definition",
        (u, _, r) => "{\"textDocument\":{\"uri\":\"" + u + "\"},\"position\":{\"line\":" + r.Line + ",\"character\":" + r.Character + "}}", ct);

  public Task<LspSymbolResult> ReferencesAsync(string uri, string symbolName, int occurrence = 0, CancellationToken ct = default)
    => QueryAsync(uri, symbolName, occurrence, "textDocument/references",
        (u, _, r) => "{\"textDocument\":{\"uri\":\"" + u + "\"},\"position\":{\"line\":" + r.Line + ",\"character\":" + r.Character + "},\"context\":{\"includeDeclaration\":true}}", ct);

  public Task<LspSymbolResult> HoverAsync(string uri, string symbolName, int occurrence = 0, CancellationToken ct = default)
    => QueryAsync(uri, symbolName, occurrence, "textDocument/hover",
        (u, _, r) => "{\"textDocument\":{\"uri\":\"" + u + "\"},\"position\":{\"line\":" + r.Line + ",\"character\":" + r.Character + "}}", ct);

  public Task<LspSymbolResult> ImplementationAsync(string uri, string symbolName, int occurrence = 0, CancellationToken ct = default)
    => QueryAsync(uri, symbolName, occurrence, "textDocument/implementation",
        (u, _, r) => "{\"textDocument\":{\"uri\":\"" + u + "\"},\"position\":{\"line\":" + r.Line + ",\"character\":" + r.Character + "}}", ct);

  public async Task<LspSymbolResult> DocumentSymbolsAsync(string uri, CancellationToken ct = default)
  {
    if (_transport is null)
    {
      return Unavailable();
    }

    if (!_capabilities.Supports("textDocument/documentSymbol"))
    {
      return Unsupported("textDocument/documentSymbol");
    }

    string paramsJson = "{\"textDocument\":{\"uri\":\"" + uri + "\"}}";
    LspRequestResult response = await _transport.SendRequestAsync("textDocument/documentSymbol", paramsJson, ct).ConfigureAwait(false);
    if (!response.IsSuccess)
    {
      return new LspSymbolResult(LspSymbolState.Degraded, [], 0, 1, response.ErrorMessage);
    }

    List<LspSymbolSite> sites = ParseDocumentSymbols(response.ResultJson);
    return new LspSymbolResult(LspSymbolState.Ready, sites, 0, sites.Count);
  }

  private static List<LspSymbolSite> ParseDocumentSymbols(string? json)
  {
    List<LspSymbolSite> sites = [];
    if (string.IsNullOrWhiteSpace(json))
    {
      return sites;
    }

    try
    {
      using JsonDocument doc = JsonDocument.Parse(json);
      CollectDocumentSymbols(doc.RootElement, string.Empty, sites);
    }
    catch (JsonException)
    {
    }

    return sites;
  }

  private static void CollectDocumentSymbols(JsonElement el, string uri, List<LspSymbolSite> sites)
  {
    if (el.ValueKind == JsonValueKind.Array)
    {
      foreach (JsonElement item in el.EnumerateArray())
      {
        CollectDocumentSymbols(item, uri, sites);
      }

      return;
    }

    if (el.ValueKind != JsonValueKind.Object)
    {
      return;
    }

    if (el.TryGetProperty("name", out JsonElement nameEl) && nameEl.ValueKind == JsonValueKind.String)
    {
      JsonElement range = el.TryGetProperty("selectionRange", out JsonElement sel) ? sel : el.GetProperty("range");
      int line = range.GetProperty("start").GetProperty("line").GetInt32();
      int ch = range.GetProperty("start").GetProperty("character").GetInt32();
      sites.Add(new LspSymbolSite(uri, line, ch, nameEl.GetString()!));
    }

    if (el.TryGetProperty("children", out JsonElement children))
    {
      CollectDocumentSymbols(children, uri, sites);
    }
  }

  public async Task<LspSymbolResult> WorkspaceSymbolsAsync(string query, CancellationToken ct = default)
  {
    if (_transport is null)
    {
      return Unavailable();
    }

    if (!_capabilities.Supports("workspace/symbol"))
    {
      return Unsupported("workspace/symbol");
    }

    string paramsJson = "{\"query\":\"" + query + "\"}";
    LspRequestResult response = await _transport.SendRequestAsync("workspace/symbol", paramsJson, ct).ConfigureAwait(false);
    if (!response.IsSuccess)
    {
      return new LspSymbolResult(LspSymbolState.Degraded, [], 0, 1, response.ErrorMessage);
    }

    List<LspSymbolSite> sites = ParseLocations(response.ResultJson, query);
    return new LspSymbolResult(LspSymbolState.Ready, sites, 0, sites.Count);
  }
}

internal static class LspSymbolResolutionExtensions
{
  public static int OccurrenceUsedOr(this LspSymbolResolution r, int fallback) => fallback;
}
