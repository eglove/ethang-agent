using eThangAgent.SharedKernel;

namespace eThangAgent.SkillDomain;

/// <summary>Persistence for agent-created skills and their version history.
/// Global scope by design: methodology knowledge transcends workspaces.
/// Single-writer CLI, so updates are last-write-wins; history preserves audit.</summary>
public interface ILearnedSkillStore
{
    Task<Result<SkillDefinition>> CreateAsync(SkillDefinition skill, CancellationToken ct = default);
    Task<Result<SkillDefinition?>> GetAsync(string name, CancellationToken ct = default);
    Task<Result<IReadOnlyList<SkillDefinition>>> ListAsync(CancellationToken ct = default);
    /// <summary>Writes the new current row AND a history row at the definition's version.</summary>
    Task<Result<SkillDefinition>> UpdateAsync(SkillDefinition updated, CancellationToken ct = default);
    /// <summary>Removes current + history rows. Usage rows survive (analytics only).</summary>
    Task<Result<bool>> DeleteAsync(string name, CancellationToken ct = default);
    Task<Result<int>> AppendUsageAsync(string name, DateTimeOffset viewedAt, CancellationToken ct = default);
}
