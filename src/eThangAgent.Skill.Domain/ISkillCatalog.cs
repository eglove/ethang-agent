using eThangAgent.SharedKernel;

namespace eThangAgent.SkillDomain;

/// <summary>The skill catalog seam: built-ins and the skills loaded from the
/// configured directories.</summary>
public interface ISkillCatalog
{
  Task<Result<IReadOnlyList<SkillDefinition>>> ListAsync(CancellationToken ct = default);
  Task<Result<SkillDefinition>> GetAsync(string name, CancellationToken ct = default);
}
