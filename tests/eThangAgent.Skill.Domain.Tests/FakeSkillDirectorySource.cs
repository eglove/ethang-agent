using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;

namespace eThangAgent.Skill.Domain.Tests;

/// <summary>A directory-source fake: routes requests to handlers by directory
/// path; unlisted paths fail. Counts every <see cref="ISkillDirectorySource.ListAsync" />
/// invocation for memoization assertions.</summary>
internal sealed class FakeSkillDirectorySource
{
  public int ListCalls { get; private set; }

  private readonly Dictionary<string, Func<SkillDirectoryLoad>> _handlers = new(StringComparer.Ordinal);

  public void LoadReturns(string directoryPath, params SkillDefinition[] skills) =>
      _handlers[directoryPath] = () => new SkillDirectoryLoad(skills, []);

  public void LoadDiagnostics(string directoryPath, params string[] diagnostics) =>
      _handlers[directoryPath] = () => new SkillDirectoryLoad([], diagnostics);

  public void LoadFails(string directoryPath, DomainError error) =>
      _handlers[directoryPath] = () => throw new InvalidOperationException(error.Message);

  public ISkillDirectorySource AsSource() => new Backing(this);

  private sealed class Backing(FakeSkillDirectorySource owner) : ISkillDirectorySource
  {
    public Task<Result<SkillDirectoryLoad>> ListAsync(string directoryPath, CancellationToken ct = default)
    {
      owner.ListCalls++;
      if (!owner._handlers.TryGetValue(directoryPath, out Func<SkillDirectoryLoad>? handler))
      {
        return Task.FromResult(Result.Failure<SkillDirectoryLoad>(
            new DomainError("DirectoryMissing", "no fake load for " + directoryPath)));
      }

      try
      {
        return Task.FromResult(Result.Success(handler()));
      }
      catch (InvalidOperationException ex)
      {
        return Task.FromResult(Result.Failure<SkillDirectoryLoad>(
            new DomainError("DirectoryReadFailed", ex.Message)));
      }
    }
  }
}
