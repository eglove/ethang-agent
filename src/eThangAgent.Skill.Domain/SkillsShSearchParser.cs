using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.SkillDomain;

/// <summary>One skills.sh directory entry (plan #29 task 4): the display
/// name, the install count, and the install address in owner/repo[/skill]
/// form.</summary>
public sealed record SkillsShEntry(string Name, long Installs, string Address);

/// <summary>Parses the skills.sh public JSON search API (verified live
/// 2026-09-25, GET https://www.skills.sh/api/search?q=&lt;query&gt;): a
/// skills array of {id, source, skillId, name, installs} objects. The id
/// becomes the install address verbatim; duplicates dedupe preserving
/// order; entries missing id or name are skipped quietly.</summary>
public static class SkillsShSearchParser
{
  public static Result<IReadOnlyList<SkillsShEntry>> ParseSearch(string json)
  {
    try
    {
      using JsonDocument doc = JsonDocument.Parse(json);
      return doc.RootElement.TryGetProperty("skills", out JsonElement skills)
          && skills.ValueKind == JsonValueKind.Array
              ? ParseSkills(skills)
              : Result.Failure<IReadOnlyList<SkillsShEntry>>(new DomainError("ParseFailed", "skills.sh response is missing the skills array."));
    }
    catch (JsonException ex)
    {
      return Result.Failure<IReadOnlyList<SkillsShEntry>>(new DomainError("ParseFailed", $"skills.sh response is not valid JSON: {ex.Message}"));
    }
  }

  private static Result<IReadOnlyList<SkillsShEntry>> ParseSkills(JsonElement skills)
  {
    List<SkillsShEntry> entries = [];
    HashSet<string> seen = [];
    foreach (JsonElement e in skills.EnumerateArray())
    {
      SkillsShEntry? entry = ParseEntry(e);
      if (entry is not null && seen.Add(entry.Address))
      {
        entries.Add(entry);
      }
    }

    return Result.Success<IReadOnlyList<SkillsShEntry>>(entries);
  }

  private static SkillsShEntry? ParseEntry(JsonElement e) =>
      e.ValueKind == JsonValueKind.Object
          && e.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.String
          && e.TryGetProperty("name", out JsonElement name) && name.ValueKind == JsonValueKind.String
          && e.TryGetProperty("installs", out JsonElement installs) && installs.TryGetInt64(out long count)
              ? new SkillsShEntry(name.GetString()!, count, id.GetString()!)
              : null;
}