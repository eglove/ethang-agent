using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using eThangAgent.SharedKernel;

namespace eThangAgent.MemoryDomain;

/// <summary>The curated bucket a durable memory belongs to.</summary>
public enum MemoryCategory
{
  Convention,
  Preference,
  Insight,
  Failure,
  Reference
}

/// <summary>Visibility of a memory row: one workspace or every workspace.</summary>
public enum MemoryScope
{
  Workspace,
  Global
}

/// <summary>
/// A human-curated durable memory row: a categorized, tagged note with provenance.
/// Pure data — validation and normalization rules live in
/// <see cref="CuratedMemorySpecifications"/>; persistence lives behind
/// <see cref="ICuratedMemoryStore"/>.
/// </summary>
public sealed record CuratedMemory(
    Guid Id,
    string WorkspaceId,          // empty string ⇒ Global scope row
    MemoryCategory Category,
    IReadOnlyList<string> Tags,
    string Content,
    string? UsageHint,
    MemoryScope Scope,
    string? ProvenanceSession,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Domain rules for <see cref="CuratedMemory"/>: tag shape, content budget,
/// and strict wire-form parsing for the enums. Parsing is exact-lowercase —
/// nothing is silently coerced; unknown input fails with the allowed values named.
/// </summary>
public static partial class CuratedMemorySpecifications
{
  /// <summary>Hard cap on stored content length, in characters.</summary>
  public const int MaxContentChars = 4000;

  /// <summary>
  /// A tag is 1–32 characters: it starts with [a-z0-9] and continues with
  /// [a-z0-9], '-', or '_'. Lowercase only; case is never folded.
  /// </summary>
  [GeneratedRegex(@"^[a-z0-9][a-z0-9-_]{0,31}$", RegexOptions.CultureInvariant)]
  private static partial Regex ValidTagRegex();

  public static bool ValidTag([NotNullWhen(true)] string? tag)
      => tag is not null && ValidTagRegex().IsMatch(tag);

  /// <summary>
  /// Returns the tags deduplicated by ordinal comparison in first-seen order.
  /// Throws <see cref="ArgumentException"/> on any entry that is not a valid
  /// tag — callers validate first; this is the last line of defense, so an
  /// invalid entry is programmer error, not a coerced value.
  /// </summary>
  public static IReadOnlyList<string> NormalizeTags(IEnumerable<string> tags)
  {
    ArgumentNullException.ThrowIfNull(tags);

    List<string> normalized = [];
    HashSet<string> seen = new(StringComparer.Ordinal);
    foreach (string tag in tags)
    {
      if (!ValidTag(tag))
      {
        throw new ArgumentException(
            $"Invalid tag '{tag}': tags must match ^[a-z0-9][a-z0-9-_]{{0,31}}$.",
            nameof(tags));
      }

      if (seen.Add(tag))
      {
        normalized.Add(tag);
      }
    }

    return normalized;
  }

  /// <summary>Parses the exact-lowercase wire form of <see cref="MemoryCategory"/>.</summary>
  public static Result<MemoryCategory> ParseCategory(string? raw) => raw switch
  {
    "convention" => Result.Success<MemoryCategory>(MemoryCategory.Convention),
    "preference" => Result.Success<MemoryCategory>(MemoryCategory.Preference),
    "insight" => Result.Success<MemoryCategory>(MemoryCategory.Insight),
    "failure" => Result.Success<MemoryCategory>(MemoryCategory.Failure),
    "reference" => Result.Success<MemoryCategory>(MemoryCategory.Reference),
    _ => Result.Failure<MemoryCategory>(new DomainError(
        "InvalidCategory",
        $"Unknown category '{raw}'. Valid categories: convention | preference | insight | failure | reference.")),
  };

  /// <summary>Parses the exact-lowercase wire form of <see cref="MemoryScope"/>.</summary>
  public static Result<MemoryScope> ParseScope(string? raw) => raw switch
  {
    "workspace" => Result.Success<MemoryScope>(MemoryScope.Workspace),
    "global" => Result.Success<MemoryScope>(MemoryScope.Global),
    _ => Result.Failure<MemoryScope>(new DomainError(
        "InvalidScope",
        $"Unknown scope '{raw}'. Valid scopes: workspace | global.")),
  };
}
