using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.Composition;

/// <summary>One session-start file entry: the path the user configured and the
///     checkbox state that decides whether it loads.</summary>
public sealed record SessionFileEntry(string Path, bool Enabled);

/// <summary>Strict parsing and durable keys for the session-start file lists (E).
///     The stored value is a JSON array of {path, enabled} objects - checkboxes are
///     part of the data, not a separate surface. Anything that is not exactly that
///     shape is a named failure: nothing is silently coerced, defaulted, or dropped.
///     Null/blank means unconfigured and parses to an empty list.</summary>
public static class SessionFilePreferences
{
  /// <summary>App-preference key holding the global session file list.</summary>
  public const string GlobalKey = "session_files:global";

  /// <summary>App-preference key prefix holding one workspace's session file list.
  ///     The full key is <c>session_files:ws:{workspaceRoot}</c> - the same
  ///     workspace-scoped shape the model picker and compaction resolver use.</summary>
  public const string WorkspacePrefix = "session_files:ws:";

  public static string WorkspaceKey(string workspaceRoot) => WorkspacePrefix + workspaceRoot;

  /// <summary>Parses a stored preference value. Success carries the entries in
  ///     stored order; Failure carries a named error naming the offending entry.</summary>
  public static Result<IReadOnlyList<SessionFileEntry>> Parse(string? stored)
  {
    if (string.IsNullOrWhiteSpace(stored))
    {
      return Result.Success<IReadOnlyList<SessionFileEntry>>([]);
    }

    List<SessionFileEntry> entries = [];
    try
    {
      using JsonDocument document = JsonDocument.Parse(stored);
      if (document.RootElement.ValueKind is not JsonValueKind.Array)
      {
        return Result.Failure<IReadOnlyList<SessionFileEntry>>(new DomainError("InvalidSessionFiles",
            "session file preference must be a JSON array of {path, enabled} entries."));
      }

      int index = 0;
      foreach (JsonElement element in document.RootElement.EnumerateArray())
      {
        index++;
        JsonElement? pathMatched = null;
        JsonElement? enabledMatched = null;
        if (element.ValueKind is JsonValueKind.Object)
        {
          foreach (JsonProperty property in element.EnumerateObject())
          {
            bool isPath = property.Name.Equals("Path", StringComparison.OrdinalIgnoreCase);
            bool isEnabled = property.Name.Equals("Enabled", StringComparison.OrdinalIgnoreCase);
            if (isPath && property.Value.ValueKind is JsonValueKind.String)
            {
              pathMatched = property.Value;
            }
            else if (isEnabled && property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
              enabledMatched = property.Value;
            }
          }
        }

        if (pathMatched is not { } path || enabledMatched is not { } enabled)
        {
          return Result.Failure<IReadOnlyList<SessionFileEntry>>(new DomainError("InvalidSessionFiles",
              $"session file entry {index} must be an object with a string 'path' and a boolean 'enabled'."));
        }

        string pathText = path.GetString()!;
        if (string.IsNullOrWhiteSpace(pathText))
        {
          return Result.Failure<IReadOnlyList<SessionFileEntry>>(new DomainError("InvalidSessionFiles",
              $"session file entry {index} has a blank path."));
        }

        entries.Add(new SessionFileEntry(pathText, enabled.GetBoolean()));
      }
    }
    catch (JsonException ex)
    {
      return Result.Failure<IReadOnlyList<SessionFileEntry>>(new DomainError("InvalidSessionFiles",
          "session file preference is not valid JSON: " + ex.Message));
    }

    return Result.Success<IReadOnlyList<SessionFileEntry>>(entries);
  }

  /// <summary>Serializes entries back to the stored JSON shape.</summary>
  public static string Serialize(IReadOnlyList<SessionFileEntry> entries) =>
      JsonSerializer.Serialize(entries);
}
