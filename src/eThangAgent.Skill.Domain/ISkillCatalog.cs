using eThangAgent.SharedKernel;

namespace eThangAgent.SkillDomain;

/// <summary>Built-in skill seam. Built-ins are authoritative: learned skills
/// may never shadow these names.</summary>
public interface ISkillCatalog
{
  Task<Result<IReadOnlyList<SkillDefinition>>> ListAsync(CancellationToken ct = default);
  Task<Result<SkillDefinition>> GetAsync(string name, CancellationToken ct = default);
}
