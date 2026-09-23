using eThangAgent.SharedKernel;

namespace eThangAgent.SkillDomain;

public interface ISkillDirectorySource
{
  Task<Result<SkillDirectoryLoad>> ListAsync(string directoryPath, CancellationToken ct = default);
}
