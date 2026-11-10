using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace FakeLspServer;

/// <summary>A minimal Language Server: initialize, textDocument/documentSymbol, and
///     textDocument/definition, answered from the file on disk. Everything else is answered
///     with the JSON-RPC method-not-found error.
///     <para>IT IS LANGUAGE-AGNOSTIC ON PURPOSE: a callable is any identifier immediately
///     followed by <c>(</c>, and a definition is the FIRST line in the file naming the
///     queried word. A fixture therefore declares before it calls, in whatever syntax its
///     language uses.</para></summary>
internal static class FakeServer
{
  /// <summary>Serves requests until the client sends exit or closes the stream.</summary>
  public static async Task<int> RunAsync(Stream input, Stream output)
  {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    using SemaphoreSlim writeGate = new(1, 1);
    while (true)
    {
      string? message = await ReadMessageAsync(input).ConfigureAwait(false);
      if (message is null)
      {
        return 0;
      }

      JsonNode? root = JsonNode.Parse(message);
      if (root is null)
      {
        continue;
      }

      if (await HandleAsync(root, output, writeGate).ConfigureAwait(false))
      {
        return 0;
      }
    }
  }

  /// <summary>Answers one message. True means the client asked the server to exit.
  ///     </summary>
  private static async Task<bool> HandleAsync(JsonNode root, Stream output, SemaphoreSlim gate)
  {
    string? method = root["method"]?.GetValue<string>();
    if (method is null)
    {
      return false;
    }

    if (string.Equals(method, "exit", StringComparison.Ordinal))
    {
      return true;
    }

    JsonNode? id = root["id"];
    if (id is null)
    {
      return false;
    }

    await WriteAsync(output, gate, BuildReply(method, root, id)).ConfigureAwait(false);
    return false;
  }

  private static JsonNode BuildReply(string method, JsonNode root, JsonNode id) => method switch
  {
    "initialize" => Reply(id, new JsonObject
    {
      ["capabilities"] = new JsonObject
      {
        ["definitionProvider"] = true,
        ["documentSymbolProvider"] = true,
        ["referencesProvider"] = true,
        ["hoverProvider"] = true,
      },
      ["serverInfo"] = new JsonObject { ["name"] = "ethang-fake-lsp", ["version"] = "1.0.0" },
    }),
    "textDocument/documentSymbol" => Reply(id, DocumentSymbols(PathOf(root))),
    "textDocument/definition" => Reply(id, Definition(PathOf(root), Position(root, "line"), Position(root, "character"))),
    _ => new JsonObject
    {
      ["jsonrpc"] = "2.0",
      ["id"] = id.DeepClone(),
      ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "method not found" },
    },
  };

  private static JsonObject Reply(JsonNode id, JsonNode result) => new()
  {
    ["jsonrpc"] = "2.0",
    ["id"] = id.DeepClone(),
    ["result"] = result,
  };

  private static string PathOf(JsonNode root)
  {
    string uri = root["params"]?["textDocument"]?["uri"]?.GetValue<string>() ?? string.Empty;
    return uri.Length == 0 ? string.Empty : new Uri(uri).LocalPath;
  }

  private static int Position(JsonNode root, string name) =>
      root["params"]?["position"]?[name]?.GetValue<int>() ?? 0;

  /// <summary>Every callable name the file declares, one symbol per line. The service
  ///     resolves a queried name to the FIRST of them, which is the declaration in a
  ///     fixture that declares before it calls.</summary>
  private static JsonArray DocumentSymbols(string path)
  {
    JsonArray symbols = [];
    if (!File.Exists(path))
    {
      return symbols;
    }

    string[] lines = File.ReadAllLines(path);
    for (int i = 0; i < lines.Length; i++)
    {
      foreach ((string name, int at) in Callables(lines[i]))
      {
        symbols.Add(new JsonObject
        {
          ["name"] = name,
          ["kind"] = 12,
          ["range"] = Range(i, 0, i, lines[i].Length),
          ["selectionRange"] = Range(i, at, i, at + name.Length),
        });
      }
    }

    return symbols;
  }

  private static JsonArray Definition(string path, int line, int character)
  {
    JsonArray locations = [];
    if (!File.Exists(path))
    {
      return locations;
    }

    string[] lines = File.ReadAllLines(path);
    if (line < 0 || line >= lines.Length)
    {
      return locations;
    }

    string word = WordAt(lines[line], character);
    if (word.Length == 0)
    {
      return locations;
    }

    int at = FirstLineNaming(lines, word);
    if (at >= 0)
    {
      locations.Add(new JsonObject
      {
        ["uri"] = new Uri(path).AbsoluteUri,
        ["range"] = Range(at, IndexOfWord(lines[at], word), at, IndexOfWord(lines[at], word) + word.Length),
      });
    }

    return locations;
  }

  private static int FirstLineNaming(string[] lines, string word)
  {
    for (int i = 0; i < lines.Length; i++)
    {
      if (IndexOfWord(lines[i], word) >= 0)
      {
        return i;
      }
    }

    return -1;
  }

  private static JsonObject Range(int startLine, int startCharacter, int endLine, int endCharacter) => new()
  {
    ["start"] = new JsonObject { ["line"] = startLine, ["character"] = startCharacter },
    ["end"] = new JsonObject { ["line"] = endLine, ["character"] = endCharacter },
  };

  private static List<(string Name, int At)> Callables(string line)
  {
    List<(string Name, int At)> found = [];
    int i = 0;
    while (i < line.Length)
    {
      if (!IsIdentifierStart(line[i]))
      {
        i++;
        continue;
      }

      int start = i;
      while (i < line.Length && IsIdentifierPart(line[i]))
      {
        i++;
      }

      if (i < line.Length && line[i] == '(')
      {
        found.Add((line[start..i], start));
      }
    }

    return found;
  }

  private static string WordAt(string line, int character)
  {
    if (character < 0 || character > line.Length)
    {
      return string.Empty;
    }

    int start = character;
    while (start > 0 && IsIdentifierPart(line[start - 1]))
    {
      start--;
    }

    int end = character;
    while (end < line.Length && IsIdentifierPart(line[end]))
    {
      end++;
    }

    return line[start..end];
  }

  private static int IndexOfWord(string line, string word)
  {
    int from = 0;
    while (from <= line.Length - word.Length)
    {
      int at = line.IndexOf(word, from, StringComparison.Ordinal);
      if (at < 0)
      {
        return -1;
      }

      if (IsWholeWord(line, at, word.Length))
      {
        return at;
      }

      from = at + 1;
    }

    return -1;
  }

  private static bool IsWholeWord(string line, int at, int length)
  {
    bool beforeOk = at == 0 || !IsIdentifierPart(line[at - 1]);
    int after = at + length;
    bool afterOk = after >= line.Length || !IsIdentifierPart(line[after]);
    return beforeOk && afterOk;
  }

  private static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_';

  private static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c == '_';

  private static async Task<string?> ReadMessageAsync(Stream source)
  {
    string? header = await ReadHeaderAsync(source).ConfigureAwait(false);
    if (header is null)
    {
      return null;
    }

    int length = ContentLength(header);
    byte[] body = new byte[length];
    int filled = 0;
    while (filled < length)
    {
      int read = await source.ReadAsync(body.AsMemory(filled, length - filled)).ConfigureAwait(false);
      if (read == 0)
      {
        return null;
      }

      filled += read;
    }

    return Encoding.UTF8.GetString(body);
  }

  private static async Task<string?> ReadHeaderAsync(Stream source)
  {
    StringBuilder header = new();
    byte[] one = new byte[1];
    while (true)
    {
      int read = await source.ReadAsync(one.AsMemory(0, 1)).ConfigureAwait(false);
      if (read == 0)
      {
        return null;
      }

      header.Append((char)one[0]);
      if (header.Length >= 4 && header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
      {
        return header.ToString();
      }
    }
  }

  private static int ContentLength(string header)
  {
    foreach (string line in header.Split("\r\n"))
    {
      if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
      {
        return int.Parse(line["Content-Length:".Length..].Trim(), CultureInfo.InvariantCulture);
      }
    }

    return 0;
  }

  private static async Task WriteAsync(Stream destination, SemaphoreSlim gate, JsonNode message)
  {
    byte[] body = Encoding.UTF8.GetBytes(message.ToJsonString());
    byte[] header = Encoding.ASCII.GetBytes(
        "Content-Length: " + body.Length.ToString(CultureInfo.InvariantCulture) + "\r\n\r\n");
    await gate.WaitAsync().ConfigureAwait(false);
    try
    {
      await destination.WriteAsync(header).ConfigureAwait(false);
      await destination.WriteAsync(body).ConfigureAwait(false);
      await destination.FlushAsync().ConfigureAwait(false);
    }
    finally
    {
      gate.Release();
    }
  }
}
