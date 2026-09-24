using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.Composition;

/// <summary>Strict parsing and durable keys for the configured skill directories
///     (skill-routing rework, Phase 1 Task 6). The stored value is a JSON array of
///     {path, enabled} objects - the SAME stored shape as the session-file lists
///     (<see cref="SessionFilePreferences"/>), reusing <see cref="SessionFileEntry"/>.
///     Anything that is not exactly that shape is a named failure: nothing is
///     silently coerced, defaulted, or dropped. Null/blank means unconfigured and
///     parses to an empty list.</summary>
public static class SkillDirectoryPreferences
{
  /// <summary>App-preference key holding the global skill directory list.</summary>
  public const string GlobalKey = "skill_directories:global";

  /// <summary>App-preference key prefix holding one workspace's skill directory list.
  ///     The full key is <c>skill_directories:ws:{workspaceRoot}</c> - the same
  ///     workspace-scoped shape the session-file lists use.</summary>
  public const string WorkspacePrefix = "skill_directories:ws:";

  public static string WorkspaceKey(string workspaceRoot) => WorkspacePrefix + workspaceRoot;

  /// <summary>Parses a stored preference value. Success carries the entries in
  ///     stored order; Failure carries the named <c>InvalidSkillDirectories</c>
  ///     error, whose message names the 1-based entry index. The parse validates
  ///     SHAPE only: duplicate-path resolution (a workspace path equal,
  ///     case-insensitively, to a global path) lives at the factory's resolution
  ///     site, where the global entry wins and the workspace duplicate is skipped.</summary>
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
        return Result.Failure<IReadOnlyList<SessionFileEntry>>(new DomainError("InvalidSkillDirectories",
            "skill directory preference must be a JSON array of {path, enabled} entries."));
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
          return Result.Failure<IReadOnlyList<SessionFileEntry>>(new DomainError("InvalidSkillDirectories",
              $"skill directory entry {index} must be an object with a string 'path' and a boolean 'enabled'."));
        }

        string pathText = path.GetString()!;
        if (string.IsNullOrWhiteSpace(pathText))
        {
          return Result.Failure<IReadOnlyList<SessionFileEntry>>(new DomainError("InvalidSkillDirectories",
              $"skill directory entry {index} has a blank path."));
        }

        entries.Add(new SessionFileEntry(pathText, enabled.GetBoolean()));
      }
    }
    catch (JsonException ex)
    {
      return Result.Failure<IReadOnlyList<SessionFileEntry>>(new DomainError("InvalidSkillDirectories",
          "skill directory preference is not valid JSON: " + ex.Message));
    }

    return Result.Success<IReadOnlyList<SessionFileEntry>>(entries);
  }

  /// <summary>Serializes entries back to the stored JSON shape.</summary>
  public static string Serialize(IReadOnlyList<SessionFileEntry> entries) =>
      JsonSerializer.Serialize(entries);
}
