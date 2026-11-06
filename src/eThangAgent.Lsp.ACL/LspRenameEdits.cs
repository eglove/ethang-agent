using System.Text.Json;

namespace eThangAgent.Lsp.ACL;

/// <summary>One text edit from a rename WorkspaceEdit. Line and character are 0-based,
/// exactly as the protocol reports them.</summary>
public sealed record LspTextEdit(int StartLine, int StartCharacter, int EndLine, int EndCharacter, string NewText);

/// <summary>Every text edit a rename applies to one document, keyed by the document URI.
/// The URI is the server's raw URI string; it is not normalized here.</summary>
#pragma warning disable CA1056 // Named decision: LSP file URIs are opaque strings on the wire; the ACL owns URI mapping.
public sealed record LspTextDocumentEdit(string Uri, IReadOnlyList<LspTextEdit> Edits);
#pragma warning restore CA1056

/// <summary>A parsed rename WorkspaceEdit. HasFileRenames reports whether the server asked
/// for a file move, which a text-edit applier cannot perform.</summary>
public sealed record LspRenameEditSet(IReadOnlyList<LspTextDocumentEdit> Documents, bool HasFileRenames);

/// <summary>Reads a rename WorkspaceEdit in either the changes or the documentChanges shape.</summary>
public static class LspRenameEdits
{
  private const string ChangesProperty = "changes";
  private const string DocumentChangesProperty = "documentChanges";
  private const string KindProperty = "kind";
  private const string RenameKind = "rename";
  private const string TextDocumentProperty = "textDocument";
  private const string UriProperty = "uri";
  private const string EditsProperty = "edits";
  private const string RangeProperty = "range";
  private const string StartProperty = "start";
  private const string EndProperty = "end";
  private const string LineProperty = "line";
  private const string CharacterProperty = "character";
  private const string NewTextProperty = "newText";

  /// <summary>Parses a WorkspaceEdit payload. A malformed payload throws <see cref="JsonException"/>.</summary>
  public static LspRenameEditSet Parse(string workspaceEditJson)
  {
    ArgumentNullException.ThrowIfNull(workspaceEditJson);
    using JsonDocument document = JsonDocument.Parse(workspaceEditJson);
    JsonElement root = document.RootElement;
    if (root.ValueKind != JsonValueKind.Object)
    {
      throw new JsonException("A WorkspaceEdit must be a JSON object.");
    }

    List<LspTextDocumentEdit> documents = [];
    bool hasFileRenames = false;

    if (root.TryGetProperty(ChangesProperty, out JsonElement changes))
    {
      if (changes.ValueKind != JsonValueKind.Object)
      {
        throw new JsonException("WorkspaceEdit 'changes' must be an object keyed by document URI.");
      }
      foreach (JsonProperty entry in changes.EnumerateObject())
      {
        documents.Add(new LspTextDocumentEdit(entry.Name, ReadEdits(entry.Value, entry.Name)));
      }
    }

    if (root.TryGetProperty(DocumentChangesProperty, out JsonElement documentChanges))
    {
      if (documentChanges.ValueKind != JsonValueKind.Array)
      {
        throw new JsonException("WorkspaceEdit 'documentChanges' must be an array.");
      }
      foreach (JsonElement entry in documentChanges.EnumerateArray())
      {
        if (entry.ValueKind != JsonValueKind.Object)
        {
          throw new JsonException("A 'documentChanges' entry must be a JSON object.");
        }
        if (entry.TryGetProperty(KindProperty, out JsonElement kind))
        {
          hasFileRenames |= kind.ValueKind == JsonValueKind.String && kind.GetString() == RenameKind;
          continue;
        }
        if (!entry.TryGetProperty(TextDocumentProperty, out JsonElement textDocument))
        {
          throw new JsonException("A 'documentChanges' entry must carry 'textDocument' or 'kind'.");
        }
        string uri = ReadString(textDocument, UriProperty, DocumentChangesProperty);
        if (!entry.TryGetProperty(EditsProperty, out JsonElement editList))
        {
          throw new JsonException($"The document change for '{uri}' must carry an 'edits' array.");
        }
        documents.Add(new LspTextDocumentEdit(uri, ReadEdits(editList, uri)));
      }
    }

    return new LspRenameEditSet(documents, hasFileRenames);
  }

  private static List<LspTextEdit> ReadEdits(JsonElement editList, string uri)
  {
    if (editList.ValueKind != JsonValueKind.Array)
    {
      throw new JsonException($"The edit list for '{uri}' must be an array.");
    }
    List<LspTextEdit> parsed = [];
    foreach (JsonElement edit in editList.EnumerateArray())
    {
      if (edit.ValueKind != JsonValueKind.Object)
      {
        throw new JsonException($"An edit for '{uri}' must be a JSON object.");
      }
      if (!edit.TryGetProperty(RangeProperty, out JsonElement range) || range.ValueKind != JsonValueKind.Object)
      {
        throw new JsonException($"An edit for '{uri}' must carry a 'range' object.");
      }
      JsonElement start = ReadObject(range, StartProperty, uri);
      JsonElement end = ReadObject(range, EndProperty, uri);
      parsed.Add(new LspTextEdit(
          ReadInt(start, LineProperty, uri),
          ReadInt(start, CharacterProperty, uri),
          ReadInt(end, LineProperty, uri),
          ReadInt(end, CharacterProperty, uri),
          ReadString(edit, NewTextProperty, uri)));
    }
    return parsed;
  }

  private static JsonElement ReadObject(JsonElement parent, string name, string uri)
  {
    if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Object)
    {
      throw new JsonException($"The range of an edit for '{uri}' must carry an object property '{name}'.");
    }
    return value;
  }

  private static string ReadString(JsonElement parent, string name, string uri)
  {
    if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String)
    {
      throw new JsonException($"An edit for '{uri}' must carry a string property '{name}'.");
    }
    return value.GetString()!;
  }

  private static int ReadInt(JsonElement parent, string name, string uri)
  {
    if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int parsed))
    {
      throw new JsonException($"A position in an edit for '{uri}' must carry an integer property '{name}'.");
    }
    return parsed;
  }
}
