using System.Text.Json;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Composition;

/// <summary>One configured trusted-directory row: the external root, whether the user
/// enabled it, and whether mutating tools may write inside it. Read access is implied by
/// enabling the row - that is what approving a trusted directory grants.</summary>
public sealed record TrustedDirectoryEntry(string Path, bool Enabled, bool Editable);

/// <summary>Strict parsing and the durable key for the trusted-directory list
/// (issue #96). The stored value is a JSON array of {path, enabled, editable} objects -
/// the same checkbox-bearing shape the session-file and skill-directory lists use, with
/// the extra write permission the trusted-directory capability is built around.
/// <para>WORKSPACE SCOPE ONLY, by design: an approval says 'in THIS workspace, the agent
/// may reach this external root'. A global scope would silently widen every other
/// workspace's reach the moment a root is approved in one, which is precisely the
/// decision the user should be making deliberately each time.</para>
/// <para>Anything that is not exactly that shape is a named failure - nothing is
/// silently coerced, defaulted, or dropped. Null/blank means unconfigured and parses to
/// an empty list.</para></summary>
public static class TrustedDirectoryPreferences
{
  /// <summary>App-preference key prefix holding one workspace's trusted-directory list.
  ///     The full key is <c>trusted_directories:ws:{workspaceRoot}</c> - the workspace-
  ///     scoped shape the session-file and skill-directory lists use.</summary>
  public const string WorkspacePrefix = "trusted_directories:ws:";

  /// <summary>The named failure code every parse error carries.</summary>
  private const string ErrorCode = "InvalidTrustedDirectories";

  /// <summary>The noun the parse errors name this list by.</summary>
  private const string EntryNoun = "trusted directory";

  public static string WorkspaceKey(string workspaceRoot) => WorkspacePrefix + workspaceRoot;

  /// <summary>Parses a stored preference value. Success carries the entries in stored
  ///     order; Failure carries the named <c>InvalidTrustedDirectories</c> error, whose
  ///     message names the 1-based entry index. Shape is validated strictly; whether a
  ///     path exists is a load-time concern, not a parse concern.</summary>
  public static Result<IReadOnlyList<TrustedDirectoryEntry>> Parse(string? stored)
  {
    if (string.IsNullOrWhiteSpace(stored))
    {
      return Result.Success<IReadOnlyList<TrustedDirectoryEntry>>([]);
    }

    try
    {
      using JsonDocument document = JsonDocument.Parse(stored);
      return ReadEntries(document.RootElement);
    }
    catch (JsonException ex)
    {
      return Result.Failure<IReadOnlyList<TrustedDirectoryEntry>>(new DomainError(ErrorCode,
          EntryNoun + " preference is not valid JSON: " + ex.Message));
    }
  }

  /// <summary>Serializes entries back to the stored JSON shape.</summary>
  public static string Serialize(IReadOnlyList<TrustedDirectoryEntry> entries) =>
      JsonSerializer.Serialize(entries);

  /// <summary>Resolves the stored list to the enabled roots the resolver consumes,
  ///     stored order kept. A parse FAILURE resolves NOTHING (named decision: a bad
  ///     stored value never half-grants a partially understood set of external roots -
  ///     the safe direction is no reach at all) and its diagnostic is dropped here
  ///     because the loaders that need it surface the parse result themselves.</summary>
  public static IReadOnlyList<TrustedDirectory> Resolve(string? stored) =>
      Parse(stored) is { IsSuccess: true } parsed
          ? [.. parsed.Value.Where(e => e.Enabled).Select(e => new TrustedDirectory(e.Path, e.Editable))]
          : [];

  /// <summary>Reads every entry of the root array, failing on the first bad one.</summary>
  private static Result<IReadOnlyList<TrustedDirectoryEntry>> ReadEntries(JsonElement root)
  {
    if (root.ValueKind is not JsonValueKind.Array)
    {
      return Result.Failure<IReadOnlyList<TrustedDirectoryEntry>>(new DomainError(ErrorCode,
          EntryNoun + " preference must be a JSON array of {path, enabled, editable} entries."));
    }

    List<TrustedDirectoryEntry> entries = [];
    int index = 0;
    foreach (JsonElement element in root.EnumerateArray())
    {
      index++;
      Result<TrustedDirectoryEntry> entry = ReadEntry(element, index);
      if (!entry.IsSuccess)
      {
        return Result.Failure<IReadOnlyList<TrustedDirectoryEntry>>(entry.Error);
      }

      entries.Add(entry.Value);
    }

    return Result.Success<IReadOnlyList<TrustedDirectoryEntry>>(entries);
  }

  /// <summary>Reads one entry, naming its 1-based index in any failure. 'editable' is
  ///     OPTIONAL and defaults to false: an entry written by a build that predates the
  ///     permission carries no field, and read-only is the safe reading of silence.</summary>
  private static Result<TrustedDirectoryEntry> ReadEntry(JsonElement element, int index)
  {
    (JsonElement? path, JsonElement? enabled, JsonElement? editable) = MatchEntryProperties(element);
    if (path is not { } pathValue || enabled is not { } enabledValue)
    {
      return Result.Failure<TrustedDirectoryEntry>(new DomainError(ErrorCode,
          $"{EntryNoun} entry {index} must be an object with a string 'path' and a boolean 'enabled'."));
    }

    string pathText = pathValue.GetString()!;
    return string.IsNullOrWhiteSpace(pathText)
        ? Result.Failure<TrustedDirectoryEntry>(new DomainError(ErrorCode,
            $"{EntryNoun} entry {index} has a blank path."))
        : Result.Success(new TrustedDirectoryEntry(pathText, enabledValue.GetBoolean(),
            editable is { } editableValue && editableValue.GetBoolean()));
  }

  /// <summary>Collects the entry's typed properties (case-insensitive names). A
  ///     non-object element matches nothing, which the caller reports as a shape failure.</summary>
  private static (JsonElement? Path, JsonElement? Enabled, JsonElement? Editable) MatchEntryProperties(
      JsonElement element)
  {
    JsonElement? pathMatched = null;
    JsonElement? enabledMatched = null;
    JsonElement? editableMatched = null;
    // A non-object element matches nothing (the caller reports the shape failure), and a
    // property typed wrongly is IGNORED rather than coerced - the same strictness the
    // session-file parser applies, so a number or a bare string never becomes a path.
    if (element.ValueKind is not JsonValueKind.Object)
    {
      return (null, null, null);
    }

    // A property typed wrongly is IGNORED rather than coerced - the same strictness the
    // session-file parser applies, so a number or a bare string never becomes a path.
    foreach (JsonProperty property in element.EnumerateObject())
    {
      pathMatched = Match(pathMatched, property, "Path", JsonValueKind.String);
      enabledMatched = Match(enabledMatched, property, "Enabled", JsonValueKind.True, JsonValueKind.False);
      editableMatched = Match(editableMatched, property, "Editable", JsonValueKind.True, JsonValueKind.False);
    }

    return (pathMatched, enabledMatched, editableMatched);
  }

  /// <summary>Returns <paramref name="current"/> unless this property is the named one with
  ///     one of the allowed value kinds, in which case its value replaces it (last match
  ///     wins, case-insensitive name).</summary>
  private static JsonElement? Match(JsonElement? current, JsonProperty property, string name,
      params JsonValueKind[] allowed) =>
      property.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && allowed.Contains(property.Value.ValueKind)
          ? property.Value
          : current;
}
