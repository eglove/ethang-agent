using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;

namespace eThangAgent.Skill.Domain.Tests;

/// <summary>A built-in catalog fake: succeeds with the given skills, or fails
/// with <see cref="Failing" /> when an error is supplied.</summary>
internal sealed class FakeBuiltInCatalog(params SkillDefinition[] skills)
{
  private readonly SkillDefinition[] _skills = skills;

  public DomainError? Failing { get; init; }

  public int ListCalls { get; private set; }

  public ISkillCatalog AsCatalog() => new Backing(this);

  public static SkillDefinition Make(string name) => new(
      name, "desc " + name, "body " + name, Version: 1, SkillSource.BuiltIn,
      ProvenanceSessionId: null, CreatedAt: DateTimeOffset.UnixEpoch,
      UpdatedAt: DateTimeOffset.UnixEpoch, Manual: false);

  private sealed class Backing(FakeBuiltInCatalog owner) : ISkillCatalog
  {
    public Task<Result<IReadOnlyList<SkillDefinition>>> ListAsync(CancellationToken ct = default)
    {
      owner.ListCalls++;
      return owner.Failing is not null
          ? Task.FromResult(Result.Failure<IReadOnlyList<SkillDefinition>>(owner.Failing))
          : Task.FromResult(Result.Success<IReadOnlyList<SkillDefinition>>([.. owner._skills]));
    }

    public async Task<Result<SkillDefinition>> GetAsync(string name, CancellationToken ct = default)
    {
      Result<IReadOnlyList<SkillDefinition>> listed = await ListAsync(ct).ConfigureAwait(false);
      if (!listed.IsSuccess)
      {
        return Result.Failure<SkillDefinition>(listed.Error);
      }

      SkillDefinition? found = null;
      foreach (SkillDefinition skill in listed.Value)
      {
        if (string.Equals(skill.Name, name, StringComparison.Ordinal))
        {
          found = skill;
          break;
        }
      }

      return found is not null
          ? Result.Success(found)
          : Result.Failure<SkillDefinition>(new DomainError("SkillNotFound", "no skill named " + name));
    }
  }
}
