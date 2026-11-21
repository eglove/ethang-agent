using System.Text.Json;

namespace eThangAgent.ToolDomain.Mcp;

/// <summary>The stored column shapes for a server's collection fields: args is a JSON
///     array of strings, env and headers are JSON objects of strings. One place renders
///     them so a spec and an edit cannot disagree about what the store will read back.</summary>
internal static class McpServerSpecJson
{
  /// <summary>The arguments list as the stored JSON array text.</summary>
  internal static string Array(IReadOnlyList<string> args) => JsonSerializer.Serialize(args);

  /// <summary>The entry map as the stored JSON object text.</summary>
  internal static string Object(IReadOnlyDictionary<string, string> entries) =>
      JsonSerializer.Serialize(entries);
}
