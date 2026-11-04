using System.Text.Json;
using System.Text.Json.Nodes;

namespace eThangAgent.Lsp.ACL;

/// <summary>One watcher announcement on the daemon pipe (issue #362): the #351
/// wire format. Paths are slash-separated workspace-relative, sorted; generation
/// is +1 per changing pass; the timestamp is ISO-8601 UTC.</summary>
public sealed record WatcherAnnouncement(
    string WorkspaceId,
    IReadOnlyList<string> Paths,
    IReadOnlyList<string> DeletedPaths,
    long Generation,
    DateTimeOffset UtcTimestamp);

/// <summary>NDJSON codec for the watcher.announce notification envelope
/// {method, params:{workspaceId, paths, deletedPaths, generation, utcTimestamp}}.
/// Malformed lines decode to null so a bad frame never kills the daemon loop.</summary>
public static class WatcherAnnouncementCodec
{
  public const string MethodName = "watcher.announce";

  public static string Encode(WatcherAnnouncement announcement)
  {
    ArgumentNullException.ThrowIfNull(announcement);
    JsonArray paths = [];
    foreach (string p in announcement.Paths) paths.Add(p);
    JsonArray deleted = [];
    foreach (string p in announcement.DeletedPaths) deleted.Add(p);
    JsonObject envelope = new()
    {
      ["method"] = MethodName,
      ["params"] = new JsonObject
      {
        ["workspaceId"] = announcement.WorkspaceId,
        ["paths"] = paths,
        ["deletedPaths"] = deleted,
        ["generation"] = announcement.Generation,
        ["utcTimestamp"] = announcement.UtcTimestamp.ToString("o")
      }
    };
    return envelope.ToJsonString();
  }

  public static WatcherAnnouncement? TryDecode(string line)
  {
    if (string.IsNullOrWhiteSpace(line)) return null;
    try
    {
      using JsonDocument doc = JsonDocument.Parse(line);
      JsonElement root = doc.RootElement;
      if (!root.TryGetProperty("method", out JsonElement method) || method.GetString() != MethodName) return null;
      if (!root.TryGetProperty("params", out JsonElement p)) return null;
      string workspaceId = p.GetProperty("workspaceId").GetString() ?? string.Empty;
      if (string.IsNullOrWhiteSpace(workspaceId)) return null;
      List<string> paths = ReadPaths(p, "paths");
      List<string> deleted = ReadPaths(p, "deletedPaths");
      long generation = p.GetProperty("generation").GetInt64();
      DateTimeOffset timestamp = p.GetProperty("utcTimestamp").GetDateTimeOffset();
      return new WatcherAnnouncement(workspaceId, paths, deleted, generation, timestamp);
    }
    catch (JsonException) { return null; }
    catch (InvalidOperationException) { return null; }
    catch (FormatException) { return null; }
  }

  private static List<string> ReadPaths(JsonElement p, string name)
  {
    List<string> values = [];
    if (p.TryGetProperty(name, out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
    {
      foreach (JsonElement item in arr.EnumerateArray())
      {
        string? s = item.GetString();
        if (!string.IsNullOrWhiteSpace(s)) values.Add(s);
      }
    }
    return values;
  }
}
