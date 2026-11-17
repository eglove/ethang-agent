using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.Composition;

/// <summary>Strict parsing and the durable key for the list of command-line tools the
/// agent is told about. The stored value is a JSON array of bare tool names - no
/// versions, no descriptions, no per-tool annotation.
/// <para>APP SCOPE ONLY, with no workspace variant: which tools a machine has is a
/// property of the machine, not of the directory being worked in, so a per-workspace
/// copy would be a second source of truth for one fact.</para>
/// <para>Anything that is not exactly that shape is a named failure - nothing is
/// silently coerced, defaulted, or dropped. Null/blank means nothing is ticked and
/// parses to an empty list.</para></summary>
public static class PathToolPreferences
{
  /// <summary>The app-preference key holding the ticked tool names.</summary>
  public const string Key = "path_tools";

  /// <summary>The named failure code every parse error carries.</summary>
  private const string ErrorCode = "InvalidPathTools";

  /// <summary>The noun the parse errors name this list by.</summary>
  private const string EntryNoun = "path tool";

  /// <summary>The executable extensions a stored name may carry and that are stripped
  ///     on parse, so what is stored and what is announced are both bare names.</summary>
  private static readonly string[] ExecutableExtensions = [".exe", ".cmd", ".bat"];

  /// <summary>Parses a stored preference value. Success carries the bare names in stored
  ///     order, with duplicates removed case-insensitively (the first spelling wins).
  ///     Failure carries the named <c>InvalidPathTools</c> error, whose message names the
  ///     1-based element index where one applies.</summary>
  public static Result<IReadOnlyList<string>> Parse(string? stored)
  {
    if (string.IsNullOrWhiteSpace(stored))
    {
      return Result.Success<IReadOnlyList<string>>([]);
    }

    try
    {
      using JsonDocument document = JsonDocument.Parse(stored);
      return ReadNames(document.RootElement);
    }
    catch (JsonException ex)
    {
      return Result.Failure<IReadOnlyList<string>>(new DomainError(ErrorCode,
          EntryNoun + " preference is not valid JSON: " + ex.Message));
    }
  }

  /// <summary>Serializes names back to the stored JSON shape, applying the same
  ///     normalization <see cref="Parse"/> applies (bare names, first spelling of each
  ///     duplicate kept) so a serialized value always parses back to what was written.
  ///     A blank name is a caller defect, not stored input, and throws.</summary>
  public static string Serialize(IReadOnlyList<string> names)
  {
    ArgumentNullException.ThrowIfNull(names);

    List<string> bare = [];
    HashSet<string> seen = [with(StringComparer.OrdinalIgnoreCase)];
    foreach (string name in names)
    {
      string stripped = StripExtension(name);
      if (string.IsNullOrWhiteSpace(stripped) || stripped != stripped.Trim())
      {
        throw new ArgumentException(
            "A path tool name must be non-blank and carry no surrounding whitespace.", nameof(names));
      }

      if (seen.Add(stripped))
      {
        bare.Add(stripped);
      }
    }

    return JsonSerializer.Serialize(bare);
  }

  /// <summary>Reads every element of the root array, failing on the first bad one.</summary>
  private static Result<IReadOnlyList<string>> ReadNames(JsonElement root)
  {
    if (root.ValueKind is not JsonValueKind.Array)
    {
      return Result.Failure<IReadOnlyList<string>>(new DomainError(ErrorCode,
          EntryNoun + " preference must be a JSON array of tool name strings."));
    }

    List<string> names = [];
    HashSet<string> seen = [with(StringComparer.OrdinalIgnoreCase)];
    int index = 0;
    foreach (JsonElement element in root.EnumerateArray())
    {
      index++;
      Result<string> name = ReadName(element, index);
      if (!name.IsSuccess)
      {
        return Result.Failure<IReadOnlyList<string>>(name.Error);
      }

      if (seen.Add(name.Value))
      {
        names.Add(name.Value);
      }
    }

    return Result.Success<IReadOnlyList<string>>(names);
  }

  /// <summary>Reads one element as a bare tool name, naming its 1-based index in any
  ///     failure. Surrounding whitespace is rejected rather than trimmed: trimming would
  ///     coerce a value nobody wrote, and storing a name that cannot resolve is the
  ///     failure this preference exists to avoid.</summary>
  private static Result<string> ReadName(JsonElement element, int index)
  {
    if (element.ValueKind is not JsonValueKind.String)
    {
      return Result.Failure<string>(new DomainError(ErrorCode,
          $"{EntryNoun} entry {index} must be a string tool name."));
    }

    string text = element.GetString()!;
    if (string.IsNullOrWhiteSpace(text))
    {
      return Result.Failure<string>(new DomainError(ErrorCode,
          $"{EntryNoun} entry {index} is blank."));
    }

    if (text != text.Trim())
    {
      return Result.Failure<string>(new DomainError(ErrorCode,
          $"{EntryNoun} entry {index} has surrounding whitespace."));
    }

    string bare = StripExtension(text);
    return string.IsNullOrWhiteSpace(bare)
        ? Result.Failure<string>(new DomainError(ErrorCode,
            $"{EntryNoun} entry {index} is only an executable extension."))
        : Result.Success(bare);
  }

  /// <summary>Removes one trailing <c>.exe</c>, <c>.cmd</c>, or <c>.bat</c>
  ///     (case-insensitive). Any other extension is part of the name.</summary>
  private static string StripExtension(string name)
  {
    string? matched = ExecutableExtensions.FirstOrDefault(
        extension => name.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
    return matched is null ? name : name[..^matched.Length];
  }
}
