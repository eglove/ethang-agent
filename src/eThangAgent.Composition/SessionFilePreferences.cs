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

  /// <summary>The named failure code every parse error carries.</summary>
  private const string ErrorCode = "InvalidSessionFiles";

  /// <summary>The noun the parse errors name this list by.</summary>
  private const string EntryNoun = "session file";

  public static string WorkspaceKey(string workspaceRoot) => WorkspacePrefix + workspaceRoot;

  /// <summary>Parses a stored preference value. Success carries the entries in
  ///     stored order; Failure carries a named error naming the offending entry.</summary>
  public static Result<IReadOnlyList<SessionFileEntry>> Parse(string? stored) =>
      PreferenceEntryListParser.Parse(stored, ErrorCode, EntryNoun);

  /// <summary>Serializes entries back to the stored JSON shape.</summary>
  public static string Serialize(IReadOnlyList<SessionFileEntry> entries) =>
      JsonSerializer.Serialize(entries);
}

/// <summary>The strict {path, enabled} entry-list parser both preference lists share:
///     the session-file lists and the skill-directory lists store the identical JSON
///     shape and differ only in the failure code and the noun their messages name.
///     Anything that is not exactly that shape is a named failure: nothing is silently
///     coerced, defaulted, or dropped. Null/blank parses to an empty list.</summary>
internal static class PreferenceEntryListParser
{
  /// <summary>Parses a stored value into entries, in stored order.</summary>
  public static Result<IReadOnlyList<SessionFileEntry>> Parse(string? stored, string errorCode, string entryNoun)
  {
    if (string.IsNullOrWhiteSpace(stored))
    {
      return Result.Success<IReadOnlyList<SessionFileEntry>>([]);
    }

    try
    {
      using JsonDocument document = JsonDocument.Parse(stored);
      return ReadEntries(document.RootElement, errorCode, entryNoun);
    }
    catch (JsonException ex)
    {
      return Result.Failure<IReadOnlyList<SessionFileEntry>>(new DomainError(errorCode,
          entryNoun + " preference is not valid JSON: " + ex.Message));
    }
  }

  /// <summary>Reads every entry of the root array, failing on the first bad one.</summary>
  private static Result<IReadOnlyList<SessionFileEntry>> ReadEntries(
      JsonElement root, string errorCode, string entryNoun)
  {
    if (root.ValueKind is not JsonValueKind.Array)
    {
      return Result.Failure<IReadOnlyList<SessionFileEntry>>(new DomainError(errorCode,
          entryNoun + " preference must be a JSON array of {path, enabled} entries."));
    }

    List<SessionFileEntry> entries = [];
    int index = 0;
    foreach (JsonElement element in root.EnumerateArray())
    {
      index++;
      Result<SessionFileEntry> entry = ReadEntry(element, index, errorCode, entryNoun);
      if (!entry.IsSuccess)
      {
        return Result.Failure<IReadOnlyList<SessionFileEntry>>(entry.Error);
      }

      entries.Add(entry.Value);
    }

    return Result.Success<IReadOnlyList<SessionFileEntry>>(entries);
  }

  /// <summary>Reads one entry, naming its 1-based index in any failure.</summary>
  private static Result<SessionFileEntry> ReadEntry(
      JsonElement element, int index, string errorCode, string entryNoun)
  {
    (JsonElement? pathMatched, JsonElement? enabledMatched) = MatchEntryProperties(element);
    if (pathMatched is not { } path || enabledMatched is not { } enabled)
    {
      return Result.Failure<SessionFileEntry>(new DomainError(errorCode,
          $"{entryNoun} entry {index} must be an object with a string 'path' and a boolean 'enabled'."));
    }

    string pathText = path.GetString()!;
    return string.IsNullOrWhiteSpace(pathText)
        ? Result.Failure<SessionFileEntry>(new DomainError(errorCode,
            $"{entryNoun} entry {index} has a blank path."))
        : Result.Success(new SessionFileEntry(pathText, enabled.GetBoolean()));
  }

  /// <summary>Collects the first string 'path' and boolean 'enabled' property of one
  ///     element (case-insensitive names, last match wins); a non-object element
  ///     matches nothing, which the caller reports as a shape failure.</summary>
  private static (JsonElement? Path, JsonElement? Enabled) MatchEntryProperties(JsonElement element)
  {
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

    return (pathMatched, enabledMatched);
  }
}
