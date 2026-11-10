using System.Text.Json;

namespace eThangAgent.Lsp.ACL;

public sealed partial class LspSymbolService
{
  /// <summary>Asks the server to compute a rename at the resolved declaration and reports
  ///     the WorkspaceEdit it answers with, unapplied, as the result message.</summary>
  private async Task<LspSymbolResult> RenameCoreAsync(string uri, string symbolName, int occurrence, string newName, CancellationToken ct)
  {
    if (_transport is null)
    {
      return Unavailable();
    }

    if (!_capabilities.Supports("textDocument/rename"))
    {
      return Unsupported("rename");
    }

    LspSymbolResolution? resolution = await ResolveAsync(uri, symbolName, occurrence, ct).ConfigureAwait(false);
    if (resolution is null)
    {
      return Unsupported("rename", DocumentSymbolGap);
    }

    if (!resolution.Found)
    {
      return NotResolved(symbolName, uri, occurrence, resolution);
    }

    string paramsJson = BuildRenameParams(uri, resolution, newName);
    LspRequestResult response = await _transport.SendRequestAsync("textDocument/rename", paramsJson, ct).ConfigureAwait(false);
    if (!response.IsSuccess)
    {
      if (response.ErrorCode == "Unsupported")
      {
        return Unsupported("rename");
      }

      return Result(LspSymbolState.Degraded, [], occurrence, resolution.TotalOccurrences, response.ErrorMessage);
    }

    return IsEmptyEditSet(response.ResultJson)
        ? Result(LspSymbolState.Ready, [], occurrence, resolution.TotalOccurrences, NoRenameEditsMessage)
        : Result(LspSymbolState.Ready, [], occurrence, resolution.TotalOccurrences, response.ResultJson);
  }

  private const string NoRenameEditsMessage = "the server returned no edits for this rename";

  /// <summary>True when the payload carries no text edits and no file operation, which is
  ///     what a server answers when the symbol has nothing to rename. A payload that does
  ///     not parse is not empty: it is reported verbatim so the caller can surface it.</summary>
  private static bool IsEmptyEditSet(string? resultJson)
  {
    if (string.IsNullOrWhiteSpace(resultJson))
    {
      return true;
    }

    try
    {
      LspRenameEditSet edits = LspRenameEdits.Parse(resultJson);
      return edits.Documents.Count == 0 && !edits.HasFileRenames;
    }
    catch (JsonException)
    {
      return false;
    }
  }

  /// <summary>The rename request parameters. The new name is serialized rather than
  ///     concatenated so a name carrying quotes or escapes reaches the server intact.</summary>
  private static string BuildRenameParams(string uri, LspSymbolResolution resolution, string newName)
    => "{\"textDocument\":{\"uri\":\"" + uri + "\"},\"position\":{\"line\":" + resolution.Line + ",\"character\":" + resolution.Character + "},\"newName\":" + JsonSerializer.Serialize(newName) + "}";
};
