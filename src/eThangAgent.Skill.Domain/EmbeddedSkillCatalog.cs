using System.Reflection;
using eThangAgent.SharedKernel;

namespace eThangAgent.SkillDomain;

/// <summary>Serves SKILL.md resources embedded in the Skill Domain assembly,
/// byte-verbatim from upstream. Parsing happens once, lazily, cached.</summary>
public sealed class EmbeddedSkillCatalog : ISkillCatalog
{
  private static readonly Lock Gate = new();
  private static IReadOnlyDictionary<string, SkillDefinition>? _cache;

  public async Task<Result<IReadOnlyList<SkillDefinition>>> ListAsync(CancellationToken ct = default)
  {
    IReadOnlyDictionary<string, SkillDefinition> all = await LoadAllAsync(ct).ConfigureAwait(false);
    return Result.Success<IReadOnlyList<SkillDefinition>>(
        [.. all.Values.OrderBy(s => s.Name, StringComparer.Ordinal)]);
  }

  public async Task<Result<SkillDefinition>> GetAsync(string name, CancellationToken ct = default)
  {
    IReadOnlyDictionary<string, SkillDefinition> all = await LoadAllAsync(ct).ConfigureAwait(false);
    return all.TryGetValue(name, out SkillDefinition? skill)
        ? Result.Success(skill)
        : Result.Failure<SkillDefinition>(new DomainError("SkillNotFound",
            $"No built-in skill named '{name}'. Use skill_list to see available skills."));
  }

  private static Task<IReadOnlyDictionary<string, SkillDefinition>> LoadAllAsync(CancellationToken ct)
  {
    lock (Gate)
    {
      if (_cache is not null)
      {
        return Task.FromResult(_cache)!;
      }
    }

    Assembly assembly = typeof(EmbeddedSkillCatalog).Assembly;
    string prefix = assembly.GetName().Name + ".skills.";
    Dictionary<string, SkillDefinition> byName = new(StringComparer.Ordinal);
    foreach (string? resourceName in assembly.GetManifestResourceNames()
                 .Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && n.EndsWith(".md", StringComparison.Ordinal)))
    {
      ct.ThrowIfCancellationRequested();
      using Stream stream = assembly.GetManifestResourceStream(resourceName)!;
      using StreamReader reader = new(stream);
      Result<ParsedSkill> parsed = SkillMarkdown.Parse(reader.ReadToEndAsync(ct).GetAwaiter().GetResult());
      if (!parsed.IsSuccess)
      {
        throw new InvalidOperationException(
            $"Embedded skill resource '{resourceName}' failed frontmatter parsing: " +
            parsed.Error!.Message);
      }

      SkillDefinition definition = new(
          parsed.Value!.Name, parsed.Value.Description, parsed.Value.Body,
          Version: 1, SkillSource.BuiltIn, ProvenanceSessionId: null,
          DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
      byName[definition.Name] = definition;
    }

    lock (Gate)
    {
      _cache = byName;
    }
    return Task.FromResult(_cache)!;
  }
}
