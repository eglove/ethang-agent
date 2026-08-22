using System.Reflection;
using eThangAgent.SharedKernel;

namespace eThangAgent.SkillDomain;

/// <summary>Serves SKILL.md resources embedded in the Skill Domain assembly,
/// byte-verbatim from upstream. Parsing happens once, lazily, cached.</summary>
public sealed class EmbeddedSkillCatalog : ISkillCatalog
{
    private static readonly object Gate = new();
    private static IReadOnlyDictionary<string, SkillDefinition>? _cache;

    public async Task<Result<IReadOnlyList<SkillDefinition>>> ListAsync(CancellationToken ct = default)
    {
        var all = await LoadAllAsync(ct);
        return Result<IReadOnlyList<SkillDefinition>>.Success(
            all.Values.OrderBy(s => s.Name, StringComparer.Ordinal).ToList());
    }

    public async Task<Result<SkillDefinition>> GetAsync(string name, CancellationToken ct = default)
    {
        var all = await LoadAllAsync(ct);
        return all.TryGetValue(name, out var skill)
            ? Result<SkillDefinition>.Success(skill)
            : Result<SkillDefinition>.Failure(new Error("SkillNotFound",
                $"No built-in skill named '{name}'. Use skill_list to see available skills."));
    }

    private static Task<IReadOnlyDictionary<string, SkillDefinition>> LoadAllAsync(CancellationToken ct)
    {
        lock (Gate)
        {
            if (_cache is not null) return Task.FromResult(_cache)!;
        }

        var assembly = typeof(EmbeddedSkillCatalog).Assembly;
        var prefix = assembly.GetName().Name + ".skills.";
        var byName = new Dictionary<string, SkillDefinition>(StringComparer.Ordinal);
        foreach (var resourceName in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && n.EndsWith(".md", StringComparison.Ordinal)))
        {
            ct.ThrowIfCancellationRequested();
            using var stream = assembly.GetManifestResourceStream(resourceName)!;
            using var reader = new StreamReader(stream);
            var parsed = SkillMarkdown.Parse(reader.ReadToEndAsync(ct).GetAwaiter().GetResult());
            if (!parsed.IsSuccess)
                throw new InvalidOperationException(
                    $"Embedded skill resource '{resourceName}' failed frontmatter parsing: " +
                    parsed.Error!.Message);
            var definition = new SkillDefinition(
                parsed.Value!.Name, parsed.Value.Description, parsed.Value.Body,
                Version: 1, SkillSource.BuiltIn, ProvenanceSessionId: null,
                DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
            byName[definition.Name] = definition;
        }

        lock (Gate) { _cache = byName; }
        return Task.FromResult(_cache)!;
    }
}
