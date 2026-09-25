using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;

namespace eThangAgent.Composition.Tests;

/// <summary>Hot reload (spec #26): Invalidate clears the memoized render so the
/// next Build re-reads the catalog; without a change, memoization still holds.</summary>
public class SkillsListingInvalidationTests
{
  [Fact]
  public async Task Build_Memoized_SecondCallDoesNotReenumerate()
  {
    CountingCatalog catalog = new();
    SkillsListingPromptProvider provider = new(catalog, new FakeLearned(), []);

    string first = await provider.BuildAsync();
    string second = await provider.BuildAsync();

    Assert.Equal(1, catalog.ListCalls);
    Assert.Equal(first, second);
  }

  [Fact]
  public async Task Invalidate_NextBuildReEnumeratesAndShowsTheNewSkill()
  {
    CountingCatalog catalog = new();
    SkillsListingPromptProvider provider = new(catalog, new FakeLearned(), []);

    string first = await provider.BuildAsync();
    Assert.DoesNotContain("fresh-skill", first, StringComparison.Ordinal);

    catalog.Add(Mk("fresh-skill"));
    provider.Invalidate();
    string second = await provider.BuildAsync();

    Assert.Equal(2, catalog.ListCalls);
    Assert.Contains("fresh-skill", second, StringComparison.Ordinal);
    Assert.NotEqual(first, second);
  }

  [Fact]
  public async Task Invalidate_ThenTwoBuilds_ReMemoizesAfterOneReload()
  {
    CountingCatalog catalog = new();
    SkillsListingPromptProvider provider = new(catalog, new FakeLearned(), []);

    _ = await provider.BuildAsync();
    catalog.Add(Mk("s2"));
    provider.Invalidate();
    _ = await provider.BuildAsync();
    _ = await provider.BuildAsync();

    Assert.Equal(2, catalog.ListCalls);
  }

  private static SkillDefinition Mk(string name) => new(
      name, "desc " + name, "body " + name, Version: 1, SkillSource.File,
      ProvenanceSessionId: null, CreatedAt: DateTimeOffset.UnixEpoch,
      UpdatedAt: DateTimeOffset.UnixEpoch, Manual: false, Origin: "C:\\skills\\global");

  private sealed class CountingCatalog : ISkillCatalog
  {
    private readonly List<SkillDefinition> _skills =
        [Mk("base-skill")];
    public int ListCalls { get; private set; }

    public void Add(SkillDefinition skill) => _skills.Add(skill);

    public Task<Result<IReadOnlyList<SkillDefinition>>> ListAsync(CancellationToken ct = default)
    {
      ListCalls++;
      return Task.FromResult(Result.Success<IReadOnlyList<SkillDefinition>>([.. _skills]));
    }

    public Task<Result<SkillDefinition>> GetAsync(string name, CancellationToken ct = default) =>
        Task.FromResult(Result.Failure<SkillDefinition>(
            new DomainError("SkillNotFound", "not found")));
  }

  private sealed class FakeLearned : ILearnedSkillStore
  {
    public Task<Result<IReadOnlyList<SkillDefinition>>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult(Result.Success<IReadOnlyList<SkillDefinition>>([]));
    public Task<Result<SkillDefinition?>> GetAsync(string name, CancellationToken ct = default) =>
        Task.FromResult(Result.Failure<SkillDefinition?>(new DomainError("SkillNotFound", "x")));
    // Remaining members are unreachable from the listing provider's read path;
    // throw to surface any unexpected use in tests.
    public Task<Result<SkillDefinition>> CreateAsync(SkillDefinition skill, CancellationToken ct = default) =>
        throw new NotSupportedException();
    public Task<Result<SkillDefinition>> UpdateAsync(SkillDefinition updated, CancellationToken ct = default) =>
        throw new NotSupportedException();
    public Task<Result<bool>> DeleteAsync(string name, CancellationToken ct = default) =>
        throw new NotSupportedException();
    public Task<Result<int>> AppendUsageAsync(string name, DateTimeOffset viewedAt, CancellationToken ct = default) =>
        throw new NotSupportedException();
  }
}
