using System.Text;
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
  private static readonly TimeSpan NoWait = TimeSpan.Zero;
  private static readonly TimeSpan LoadWait = TimeSpan.FromSeconds(60);

  private readonly LspJsonRpcTransport? _transport;
  private readonly LspCapabilities _capabilities;
  private readonly ILspWorkspaceModel? _workspace;

  public LspSymbolService(LspJsonRpcTransport? transport, LspCapabilities capabilities, ILspWorkspaceModel? workspace = null)
    => (_transport, _capabilities, _workspace) = (transport, capabilities ?? throw new ArgumentNullException(nameof(capabilities)), workspace);

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
        && NamesMatch(nameEl.GetString()!, symbolName))
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

  /// <summary>True when a document symbol's name denotes the queried symbol. A server
  /// reports a member's name together with its signature ("GetStatus(AgentId) : Task"),
  /// so the declared name is compared as well as the raw string.</summary>
  private static bool NamesMatch(string declaredName, string symbolName)
  {
    if (string.Equals(declaredName, symbolName, StringComparison.Ordinal))
    {
      return true;
    }

    return string.Equals(DeclaredName(declaredName), DeclaredName(symbolName), StringComparison.Ordinal)
        && DeclaredName(declaredName).Length > 0;
  }

  /// <summary>The name a symbol declares, with any signature or type annotation removed.</summary>
  private static string DeclaredName(string raw)
  {
    int cut = raw.IndexOf('(', StringComparison.Ordinal);
    int colon = raw.IndexOf(" : ", StringComparison.Ordinal);
    if (colon >= 0 && (cut < 0 || colon < cut))
    {
      cut = colon;
    }

    return (cut < 0 ? raw : raw[..cut]).Trim();
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

  private const string NotLoadedMessage =
    "the language server has not loaded this workspace yet, so a symbol query has no compilation behind it; retry, or call the workspace-load action to wait for the load";

  /// <summary>Answers Starting when the workspace has no compilation behind it. Roslyn
  ///     answers a symbol query against an unloaded workspace from its file-based project
  ///     system, which reports a type's own declaration and none of its cross-file uses -
  ///     an empty Ready that reads as truth. The load is requested here and not awaited:
  ///     it takes far longer than a query budget, so the caller gets Starting now and a
  ///     real answer on a later call.</summary>
  private async Task<LspSymbolResult?> WorkspaceGateAsync(CancellationToken ct)
  {
    if (_workspace is null || _workspace.IsLoaded)
    {
      return null;
    }

    await _workspace.EnsureLoadedAsync(NoWait, ct).ConfigureAwait(false);
    return new LspSymbolResult(LspSymbolState.Starting, [], 0, 1, NotLoadedMessage);
  }

  /// <summary>Requests the workspace load and waits for it to finish. The one operation
  ///     that blocks on the load, because waiting is the whole of what it asks for.</summary>
  public async Task<LspSymbolResult> LoadWorkspaceAsync(CancellationToken ct = default)
  {
    if (_transport is null)
    {
      return Unavailable();
    }

    if (_workspace is null)
    {
      return new LspSymbolResult(LspSymbolState.Ready, [], 0, 1, "this workspace has no solution to load");
    }

    bool loaded = await _workspace.EnsureLoadedAsync(LoadWait, ct).ConfigureAwait(false);
    return loaded
        ? new LspSymbolResult(LspSymbolState.Ready, [], 0, 1)
        : new LspSymbolResult(LspSymbolState.Starting, [], 0, 1, NotLoadedMessage);
  }

  private static LspSymbolResult Unsupported(string method) => new(LspSymbolState.Unsupported, [], 0, 1, "capability not advertised for " + method);

  private static LspSymbolResult ReferencedOnly(string symbolName, string uri)
    => new(LspSymbolState.Unsupported, [], 0, 1, "symbol '" + symbolName + "' is only referenced, not defined, in " + uri + "; position cannot be resolved");

  /// <summary>Why a symbol name did not resolve to a position. A name absent from the file
  ///     and an occurrence index past the last definition are different mistakes, so they
  ///     never share a message.</summary>
  private static LspSymbolResult NotResolved(string symbolName, string uri, int occurrence, LspSymbolResolution resolution)
    => resolution.TotalOccurrences == 0
        ? ReferencedOnly(symbolName, uri)
        : new(LspSymbolState.Unsupported, [], occurrence, resolution.TotalOccurrences,
            "occurrence " + occurrence + " is out of range: '" + symbolName + "' is defined " + resolution.TotalOccurrences + " time(s) in " + uri + "; pass an occurrence from 0 to " + (resolution.TotalOccurrences - 1));

  private async Task<LspSymbolResult> QueryAsync(string uri, string symbolName, int occurrence, string method, Func<string, string, LspSymbolResolution, string> buildParams, CancellationToken ct)
  {
    if (_transport is null)
    {
      return Unavailable();
    }

    LspSymbolResult? gate = await WorkspaceGateAsync(ct).ConfigureAwait(false);
    if (gate is not null)
    {
      return gate;
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
      return NotResolved(symbolName, uri, occurrence, resolution);
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

    List<LspSymbolSite> sites = method == "textDocument/hover"
        ? HoverSites(response.ResultJson, uri, symbolName, resolution)
        : ParseLocations(response.ResultJson, symbolName);
    string? message = method == "textDocument/hover" ? HoverText(response.ResultJson) : null;
    return new LspSymbolResult(LspSymbolState.Ready, sites, occurrence, resolution.TotalOccurrences, message);
  }

  /// <summary>The text a hover response carries. The protocol allows a string, a
  /// MarkupContent object, or an array of either.</summary>
  private static string? HoverText(string? json)
  {
    if (string.IsNullOrWhiteSpace(json))
    {
      return null;
    }

    try
    {
      using JsonDocument doc = JsonDocument.Parse(json);
      if (doc.RootElement.ValueKind != JsonValueKind.Object
          || !doc.RootElement.TryGetProperty("contents", out JsonElement contents))
      {
        return null;
      }

      StringBuilder text = new();
      AppendHoverContents(contents, text);
      string rendered = text.ToString().Trim();
      return rendered.Length == 0 ? null : rendered;
    }
    catch (JsonException)
    {
      return null;
    }
  }

  /// <summary>The site a hover answers for. A hover response carries contents and an
  ///     optional range, never a location, so the position comes from the range the server
  ///     reported and falls back to the resolved declaration; the document is the one asked
  ///     about.</summary>
  private static List<LspSymbolSite> HoverSites(string? json, string uri, string symbolName, LspSymbolResolution resolution)
  {
    (int line, int character) = (resolution.Line, resolution.Character);
    if (!string.IsNullOrWhiteSpace(json))
    {
      try
      {
        using JsonDocument doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("range", out JsonElement range)
            && range.ValueKind == JsonValueKind.Object)
        {
          JsonElement start = range.GetProperty("start");
          line = start.GetProperty("line").GetInt32();
          character = start.GetProperty("character").GetInt32();
        }
      }
      catch (JsonException)
      {
      }
    }

    return [new LspSymbolSite(uri, line, character, symbolName)];
  }

  private static void AppendHoverContents(JsonElement contents, StringBuilder text)
  {
    if (contents.ValueKind == JsonValueKind.String)
    {
      text.Append(contents.GetString());
      return;
    }

    if (contents.ValueKind == JsonValueKind.Object)
    {
      if (contents.TryGetProperty("value", out JsonElement value) && value.ValueKind == JsonValueKind.String)
      {
        text.Append(value.GetString());
      }

      return;
    }

    if (contents.ValueKind == JsonValueKind.Array)
    {
      foreach (JsonElement part in contents.EnumerateArray())
      {
        if (text.Length > 0)
        {
          text.Append('\n');
        }

        AppendHoverContents(part, text);
      }
    }
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

    return Dedupe(sites);
  }

  /// <summary>Collapses sites that name the same position. A server reports a type and the
  ///     primary constructor it declares as separate symbols over one range, so a reference
  ///     query answers with that declaration twice.</summary>
  private static List<LspSymbolSite> Dedupe(List<LspSymbolSite> sites)
  {
    List<LspSymbolSite> unique = [];
    HashSet<(string Uri, int Line, int Character, string SymbolName)> seen = [];
    foreach (LspSymbolSite site in sites)
    {
      if (seen.Add((site.Uri, site.Line, site.Character, site.SymbolName)))
      {
        unique.Add(site);
      }
    }

    return unique;
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

  /// <summary>Asks the server to compute a rename of the named symbol and reports the edit
  ///     set it answers with, unapplied, as the result message.</summary>
  public Task<LspSymbolResult> RenameAsync(string uri, string symbolName, int occurrence, string newName, CancellationToken ct = default)
    => RenameCoreAsync(uri, symbolName, occurrence, newName, ct);

  public async Task<LspSymbolResult> DocumentSymbolsAsync(string uri, CancellationToken ct = default)
  {
    if (_transport is null)
    {
      return Unavailable();
    }

    LspSymbolResult? gate = await WorkspaceGateAsync(ct).ConfigureAwait(false);
    if (gate is not null)
    {
      return gate;
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

    List<LspSymbolSite> sites = ParseDocumentSymbols(response.ResultJson, uri);
    return new LspSymbolResult(LspSymbolState.Ready, sites, 0, sites.Count);
  }

  private static List<LspSymbolSite> ParseDocumentSymbols(string? json, string uri)
  {
    List<LspSymbolSite> sites = [];
    if (string.IsNullOrWhiteSpace(json))
    {
      return sites;
    }

    try
    {
      using JsonDocument doc = JsonDocument.Parse(json);
      CollectDocumentSymbols(doc.RootElement, uri, sites);
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

  /// <summary>Parses a workspace/symbol payload. Both shapes the protocol allows are
  /// accepted: SymbolInformation carries a nested location, WorkspaceSymbol carries
  /// the uri and range inline.</summary>
  private static List<LspSymbolSite> ParseWorkspaceSymbols(string? json)
  {
    List<LspSymbolSite> sites = [];
    if (string.IsNullOrWhiteSpace(json))
    {
      return sites;
    }

    try
    {
      using JsonDocument doc = JsonDocument.Parse(json);
      if (doc.RootElement.ValueKind != JsonValueKind.Array)
      {
        return sites;
      }

      foreach (JsonElement item in doc.RootElement.EnumerateArray())
      {
        if (item.ValueKind != JsonValueKind.Object)
        {
          continue;
        }

        JsonElement located = item.TryGetProperty("location", out JsonElement loc) && loc.ValueKind == JsonValueKind.Object ? loc : item;
        if (!located.TryGetProperty("range", out _))
        {
          continue;
        }

        string name = item.TryGetProperty("name", out JsonElement n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : string.Empty;
        sites.Add(ParseLocation(located, name));
      }
    }
    catch (JsonException)
    {
    }

    return sites;
  }

  private static string NoWorkspaceSymbolsMessage(string query)
    => "the language server returned no workspace symbols for '" + query + "'; its workspace index may not be loaded yet";

  public async Task<LspSymbolResult> WorkspaceSymbolsAsync(string query, CancellationToken ct = default)
  {
    if (_transport is null)
    {
      return Unavailable();
    }

    LspSymbolResult? gate = await WorkspaceGateAsync(ct).ConfigureAwait(false);
    if (gate is not null)
    {
      return gate;
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

    List<LspSymbolSite> sites = ParseWorkspaceSymbols(response.ResultJson);
    return sites.Count == 0
        ? new LspSymbolResult(LspSymbolState.Degraded, [], 0, 0, NoWorkspaceSymbolsMessage(query))
        : new LspSymbolResult(LspSymbolState.Ready, sites, 0, sites.Count);
  }
}

internal static class LspSymbolResolutionExtensions
{
  public static int OccurrenceUsedOr(this LspSymbolResolution r, int fallback) => fallback;
}
