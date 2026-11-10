using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.Composition;

/// <summary>One language server the user installed: the language it serves and the pinned
///     version that was installed.</summary>
public sealed record InstalledLanguageServer(string LanguageId, string Version);

/// <summary>Strict parsing and the durable keys for the installed language servers
///     (multi-language LSP support). The stored value is a JSON array of
///     {languageId, version} objects. Anything that is not exactly that shape is a named
///     failure: nothing is silently coerced, defaulted, or dropped. Null/blank means nothing
///     is installed and parses to an empty list.
///     <para>The preference store is the ONLY persistence for install state. There is no
///     installed-languages file, marker file, or cache manifest: the app's own database is
///     the only artifact.</para></summary>
public static class LanguageServerPreferences
{
  /// <summary>App-preference key holding the installed language-server list.</summary>
  public const string InstalledKey = "languageservers/installed";

  /// <summary>App-preference key holding the user's choice not to be asked again about a
  ///     workspace's missing language servers. The stored value is the string
  ///     <c>true</c>.</summary>
  public const string PromptDisabledKey = "languageservers/promptDisabled";

  /// <summary>The named failure code every parse error carries.</summary>
  private const string ErrorCode = "InvalidInstalledLanguageServers";

  /// <summary>The noun the parse errors name this list by.</summary>
  private const string EntryNoun = "installed language server";

  /// <summary>Parses a stored preference value. Success carries the entries in stored
  ///     order; Failure carries the named error, whose message names the 1-based entry
  ///     index.</summary>
  public static Result<IReadOnlyList<InstalledLanguageServer>> Parse(string? stored)
  {
    if (string.IsNullOrWhiteSpace(stored))
    {
      return Result.Success<IReadOnlyList<InstalledLanguageServer>>([]);
    }

    try
    {
      using JsonDocument document = JsonDocument.Parse(stored);
      return ReadEntries(document.RootElement);
    }
    catch (JsonException ex)
    {
      return Result.Failure<IReadOnlyList<InstalledLanguageServer>>(new DomainError(ErrorCode,
          EntryNoun + " preference is not valid JSON: " + ex.Message));
    }
  }

  /// <summary>Serializes entries back to the stored JSON shape: camelCase property names,
  ///     which is what the stored contract names.</summary>
  public static string Serialize(IReadOnlyList<InstalledLanguageServer> entries)
  {
    ArgumentNullException.ThrowIfNull(entries);
    return JsonSerializer.Serialize(entries, StoredShape);
  }

  /// <summary>The stored shape's serializer options.</summary>
  private static readonly JsonSerializerOptions StoredShape = new()
  {
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
  };

  /// <summary>The language ids the stored value names, in stored order. A parse failure
  ///     resolves NOTHING: a value this app cannot read never half-claims an install, so the
  ///     page reports NotInstalled rather than a set it only partly understood.</summary>
  public static IReadOnlyList<string> InstalledLanguageIds(string? stored) =>
      Parse(stored) is { IsSuccess: true } parsed
          ? [.. parsed.Value.Select(entry => entry.LanguageId)]
          : [];

  /// <summary>Reads every entry of the root array, failing on the first bad one.</summary>
  private static Result<IReadOnlyList<InstalledLanguageServer>> ReadEntries(JsonElement root)
  {
    if (root.ValueKind is not JsonValueKind.Array)
    {
      return Result.Failure<IReadOnlyList<InstalledLanguageServer>>(new DomainError(ErrorCode,
          EntryNoun + " preference must be a JSON array of {languageId, version} entries."));
    }

    List<InstalledLanguageServer> entries = [];
    int index = 0;
    foreach (JsonElement element in root.EnumerateArray())
    {
      index++;
      Result<InstalledLanguageServer> entry = ReadEntry(element, index);
      if (!entry.IsSuccess)
      {
        return Result.Failure<IReadOnlyList<InstalledLanguageServer>>(entry.Error);
      }

      entries.Add(entry.Value);
    }

    return Result.Success<IReadOnlyList<InstalledLanguageServer>>(entries);
  }

  /// <summary>Reads one entry, naming its 1-based index in any failure.</summary>
  private static Result<InstalledLanguageServer> ReadEntry(JsonElement element, int index)
  {
    (JsonElement? languageId, JsonElement? version) = MatchEntryProperties(element);
    if (languageId is not { } languageValue || version is not { } versionValue)
    {
      return Result.Failure<InstalledLanguageServer>(new DomainError(ErrorCode,
          $"{EntryNoun} entry {index} must be an object with a string 'languageId' and a string 'version'."));
    }

    string languageText = languageValue.GetString()!;
    string versionText = versionValue.GetString()!;
    return string.IsNullOrWhiteSpace(languageText)
        ? Result.Failure<InstalledLanguageServer>(new DomainError(ErrorCode,
            $"{EntryNoun} entry {index} has a blank languageId."))
        : ReadVersion(languageText, versionText, index);
  }

  /// <summary>Reads the version of one entry, naming its 1-based index in any failure.</summary>
  private static Result<InstalledLanguageServer> ReadVersion(string languageText, string versionText, int index)
  {
    return string.IsNullOrWhiteSpace(versionText)
        ? Result.Failure<InstalledLanguageServer>(new DomainError(ErrorCode,
            $"{EntryNoun} entry {index} has a blank version."))
        : Result.Success(new InstalledLanguageServer(languageText, versionText));
  }

  /// <summary>Collects the entry's typed properties (case-insensitive names). A non-object
  ///     element matches nothing, which the caller reports as a shape failure, and a property
  ///     typed wrongly is ignored rather than coerced.</summary>
  private static (JsonElement? LanguageId, JsonElement? Version) MatchEntryProperties(JsonElement element)
  {
    JsonElement? languageMatched = null;
    JsonElement? versionMatched = null;
    if (element.ValueKind is not JsonValueKind.Object)
    {
      return (null, null);
    }

    foreach (JsonProperty property in element.EnumerateObject())
    {
      languageMatched = Match(languageMatched, property, "LanguageId", JsonValueKind.String);
      versionMatched = Match(versionMatched, property, "Version", JsonValueKind.String);
    }

    return (languageMatched, versionMatched);
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
