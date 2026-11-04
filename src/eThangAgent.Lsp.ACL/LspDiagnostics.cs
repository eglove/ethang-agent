using System.Text.Json;

namespace eThangAgent.Lsp.ACL;

/// <summary>Explicit diagnostics state (issue #354). Empty is never clean: a
/// file with no diagnostics yet reports Starting, never Ready with zero items
/// unless the server has actually reported for it.</summary>
public enum LspDiagnosticsState
{
  Ready,
  Starting,
  Stale,
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
/// report for a file replaces that path's earlier set for the file. A pull
/// result is authoritative for the file at pull time, so it replaces the push
/// set; a push replaces the pull set only when newer. The union is then
/// deduplicated by identity.</summary>
public sealed class LspDiagnosticStore
{
  private readonly Dictionary<string, List<LspDiagnostic>> _pushByUri = new(StringComparer.Ordinal);
  private readonly Dictionary<string, List<LspDiagnostic>> _pullByUri = new(StringComparer.Ordinal);
  private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
  private readonly Lock _gate = new();

  public string? LastResultId { get; private set; }

  public void OnPublishDiagnostics(string paramsJson)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(paramsJson);
    (string uri, List<LspDiagnostic> diags) = Parse(paramsJson);
    lock (_gate)
    {
      _pushByUri[uri] = diags;
      _reported.Add(uri);
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
      _pushByUri.Remove(uri);
      _reported.Add(uri);
      if (resultId is not null) LastResultId = resultId;
      _ = parsedUri;
    }
  }

  public LspDiagnosticsResult Get(string uri, long knownGeneration = -1, long lastAppliedGeneration = -1)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(uri);
    lock (_gate)
    {
      if (knownGeneration >= 0 && lastAppliedGeneration >= 0 && knownGeneration < lastAppliedGeneration)
      {
        return new LspDiagnosticsResult(LspDiagnosticsState.Stale, []);
      }
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

  public string BuildPullParams(string uri)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(uri);
    string? previous = LastResultId;
    if (previous is null)
    {
      return "{\"textDocument\":{\"uri\":\"" + uri + "\"}}";
    }
    return "{\"textDocument\":{\"uri\":\"" + uri + "\"},\"previousResultId\":\"" + previous + "\"}";
  }

  private static (string Uri, List<LspDiagnostic> Diags) Parse(string json)
  {
    (string uri, List<LspDiagnostic> diags, string? _) = ParseWithResultId(json);
    return (uri, diags);
  }

  private static (string Uri, List<LspDiagnostic> Diags, string? ResultId) ParseWithResultId(string json)
  {
    using JsonDocument doc = JsonDocument.Parse(json);
    JsonElement root = doc.RootElement;
    string uri = root.TryGetProperty("uri", out JsonElement u) ? u.GetString() ?? string.Empty : string.Empty;
    string? resultId = root.TryGetProperty("resultId", out JsonElement rid) ? rid.GetString() : null;
    List<LspDiagnostic> diags = [];
    if (root.TryGetProperty("diagnostics", out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
    {
      foreach (JsonElement item in arr.EnumerateArray())
      {
        diags.Add(ParseOne(uri, item));
      }
    }
    return (uri, diags, resultId);
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
