using System.Text.Json;

namespace eThangAgent.Lsp.ACL;

/// <summary>Explicit diagnostics state (issue #354). Empty is never clean: a
/// file with no diagnostics yet reports Starting, never Ready with zero items
/// unless the server has actually reported for it.</summary>
public enum LspDiagnosticsState
{
  Ready,
  Starting,
  Degraded,
  Unavailable,
}

/// <summary>One diagnostic with its range, code, and message. Identity for
/// dedup is file + range + code + message.</summary>
#pragma warning disable CA1056 // Named decision: LSP file URIs are opaque strings on the wire; the ACL owns URI mapping.
public sealed record LspDiagnostic(
    string FileUri,
    int StartLine,
    int StartCharacter,
    int EndLine,
    int EndCharacter,
    string? Code,
    string Message,
    int? Severity);

public sealed record LspDiagnosticsResult(LspDiagnosticsState State, IReadOnlyList<LspDiagnostic> Diagnostics);

/// <summary>Merges push (publishDiagnostics) and pull (textDocument/diagnostic)
/// diagnostics by identity. Push is primary for C# (OmniSharp is push-only per
/// #355); pull is secondary and must not be assumed from advertisement. Both
/// paths merge by identity (file + range + code + message): a diagnostic
/// reported by both appears once, distinct diagnostics are kept. A later
/// report for a file replaces that path's earlier set for the file. The
/// union of both paths is deduplicated by identity.</summary>
public sealed class LspDiagnosticStore
{
  private readonly Dictionary<string, List<LspDiagnostic>> _pushByUri = new(StringComparer.Ordinal);
  private readonly Dictionary<string, int> _pushVersionByUri = new(StringComparer.Ordinal);
  private readonly Dictionary<string, List<LspDiagnostic>> _pullByUri = new(StringComparer.Ordinal);
  private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
  private readonly Lock _gate = new();

  private readonly Dictionary<string, string> _resultIdByUri = new(StringComparer.Ordinal);

  public string? LastResultId => _resultIdByUri.Count > 0 ? _resultIdByUri.Values.Last() : null;

  public string? GetResultId(string uri)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(uri);
    lock (_gate) { return _resultIdByUri.TryGetValue(uri, out string? id) ? id : null; }
  }

  public void OnPublishDiagnostics(string paramsJson)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(paramsJson);
    (string uri, List<LspDiagnostic> diags, int? version) = Parse(paramsJson);
    lock (_gate)
    {
      _pushByUri[uri] = diags;
      _reported.Add(uri);
      if (version is not null)
      {
        _pushVersionByUri[uri] = version.Value;
      }
    }
  }

  /// <summary>The document version the last push report for <paramref name="uri"/>
  ///     described, or null when the server sent no version (it may omit one, and a
  ///     server that never received <c>versionSupport</c> always will).</summary>
  public int? GetPushVersion(string uri)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(uri);
    lock (_gate)
    {
      return _pushVersionByUri.TryGetValue(uri, out int version) ? version : null;
    }
  }

  public void OnPullDiagnostics(string uri, string resultJson)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(uri);
    ArgumentException.ThrowIfNullOrWhiteSpace(resultJson);
    (string parsedUri, List<LspDiagnostic> diags, string? resultId) = ParseWithResultId(resultJson);
    lock (_gate)
    {
      _pullByUri[uri] = diags;
      _reported.Add(uri);
      if (resultId is not null) _resultIdByUri[uri] = resultId;
      _ = parsedUri;
    }
  }

  /// <summary>The diagnostics for <paramref name="uri"/> as of the document version the
  ///     caller currently holds, or <c>Starting</c> when the only report in hand describes
  ///     different content.
  ///     <para>WHY A VERSION MISMATCH IS NOT A RESULT: a server publishes diagnostics for the
  ///     snapshot it analyzed, and it publishes them on its own schedule - often before the
  ///     analysis that would make them meaningful has run. A report whose version is not the
  ///     caller's version describes text the caller is no longer editing, so its claims are
  ///     answers to a question nobody asked. Reporting them as evidence is how an editor is
  ///     told to delete a line that is load-bearing in the content it actually holds.</para>
  ///     <para>The rule is the protocol's own and names no language: LSP carries
  ///     <c>version</c> on a publish only when the client advertised
  ///     <c>versionSupport</c>, so the comparison is available wherever a server honours it.
  ///     A server that sends no version yields no comparison, and the report stands as it
  ///     always did.</para></summary>
  /// <param name="uri">The document.</param>
  /// <param name="currentVersion">The version the caller last sent, or null when unknown.</param>
  public LspDiagnosticsResult GetForVersion(string uri, int? currentVersion)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(uri);
    if (currentVersion is null)
    {
      return Get(uri);
    }

    lock (_gate)
    {
      if (!_reported.Contains(uri))
      {
        return new LspDiagnosticsResult(LspDiagnosticsState.Starting, []);
      }

      bool describesCurrentContent = !_pushVersionByUri.TryGetValue(uri, out int reported)
          || reported == currentVersion.Value;
      return describesCurrentContent
          ? new LspDiagnosticsResult(LspDiagnosticsState.Ready, Merge(uri))
          : new LspDiagnosticsResult(LspDiagnosticsState.Starting, []);
    }
  }

  public LspDiagnosticsResult Get(string uri)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(uri);
    lock (_gate)
    {
      if (!_reported.Contains(uri))
      {
        return new LspDiagnosticsResult(LspDiagnosticsState.Starting, []);
      }
      List<LspDiagnostic> merged = Merge(uri);
      return new LspDiagnosticsResult(LspDiagnosticsState.Ready, merged);
    }
  }

  private List<LspDiagnostic> Merge(string uri)
  {
    HashSet<string> seen = new(StringComparer.Ordinal);
    List<LspDiagnostic> merged = [];
    if (_pushByUri.TryGetValue(uri, out List<LspDiagnostic>? push))
    {
      foreach (LspDiagnostic d in push)
      {
        if (seen.Add(Identity(d))) merged.Add(d);
      }
    }
    if (_pullByUri.TryGetValue(uri, out List<LspDiagnostic>? pull))
    {
      foreach (LspDiagnostic d in pull)
      {
        if (seen.Add(Identity(d))) merged.Add(d);
      }
    }
    return merged;
  }

  private static string Identity(LspDiagnostic d)
    => d.FileUri + "|" + d.StartLine + ":" + d.StartCharacter + "-" + d.EndLine + ":" + d.EndCharacter + "|" + (d.Code ?? string.Empty) + "|" + d.Message;

  /// <summary>Pull parameters for one document. No <c>previousResultId</c> is sent:
  /// the server's result id identifies the diagnostic KIND, not the document's content,
  /// so echoing it back makes the server answer "unchanged" for a file that has since
  /// changed - and the answer then carries no items at all.</summary>
  public static string BuildPullParams(string uri)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(uri);
    return "{\"textDocument\":{\"uri\":\"" + uri + "\"}}";
  }

  private static (string Uri, List<LspDiagnostic> Diags, int? Version) Parse(string json)
  {
    (string uri, List<LspDiagnostic> diags, string? _) = ParseWithResultId(json);
    using JsonDocument doc = JsonDocument.Parse(json);
    int? version = doc.RootElement.TryGetProperty("version", out JsonElement v)
        && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
    return (uri, diags, version);
  }

  private static (string Uri, List<LspDiagnostic> Diags, string? ResultId) ParseWithResultId(string json)
  {
    using JsonDocument doc = JsonDocument.Parse(json);
    JsonElement root = doc.RootElement;
    string uri = root.TryGetProperty("uri", out JsonElement u) ? u.GetString() ?? string.Empty : string.Empty;
    string? resultId = root.TryGetProperty("resultId", out JsonElement rid) ? rid.GetString() : null;
    // A pull response carries its diagnostics under "items"; a push notification
    // carries them under "diagnostics". Both shapes reach this parser, so it reads
    // whichever is present.
    List<LspDiagnostic> diags = [];
    if (TryGetArray(root, "items", out JsonElement items) || TryGetArray(root, "diagnostics", out items))
    {
      foreach (JsonElement item in items.EnumerateArray())
      {
        diags.Add(ParseOne(uri, item));
      }
    }
    return (uri, diags, resultId);
  }

  private static bool TryGetArray(JsonElement root, string name, out JsonElement value)
  {
    if (root.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Array)
    {
      return true;
    }

    value = default;
    return false;
  }

  private static LspDiagnostic ParseOne(string uri, JsonElement item)
  {
    JsonElement range = item.GetProperty("range");
    int sl = range.GetProperty("start").GetProperty("line").GetInt32();
    int sc = range.GetProperty("start").GetProperty("character").GetInt32();
    int el = range.GetProperty("end").GetProperty("line").GetInt32();
    int ec = range.GetProperty("end").GetProperty("character").GetInt32();
    string? code = null;
    if (item.TryGetProperty("code", out JsonElement c))
    {
      code = c.ValueKind == JsonValueKind.String ? c.GetString() : c.GetRawText();
    }
    string message = item.TryGetProperty("message", out JsonElement m) ? m.GetString() ?? string.Empty : string.Empty;
    int? severity = item.TryGetProperty("severity", out JsonElement s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : null;
    return new LspDiagnostic(uri, sl, sc, el, ec, code, message, severity);
  }
}
