using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;

namespace eThangAgent.Composition.Tests;

/// <summary>The bootstrap and the skills listing must name their actions as exec-bridge
///     calls: the loop's tool registry carries only exec, so a model that calls skill_view
///     directly gets Error [UnknownTool] and burns a turn recovering through Tools.Invoke
///     (observed in the field, 2026-09-26).
///     <para>The mapping's rows are checked against actions that EXIST. It used to pin a
///     <c>Tools.Invoke("spawn"</c> row, which was correct then and is exactly what issue
///     #308 removed — a bridge-call test naming a retired action would pin the staleness the
///     mechanical guard exists to catch.</para></summary>
public class BridgeCallWordingTests
{
  [Fact]
  public async Task SkillsListingHeader_NamesSkillViewAsExecBridgeCall()
  {
    string text = await new SkillsListingPromptProvider(
        new EmptyCatalog(), new EmptyLearned(), []).BuildAsync();

    Assert.StartsWith(
        "[skills listing — prefer a matching skill over improvising; load bodies with skill_view (an exec-bridge call: Tools.Invoke(\"skill_view\", new { name = \"<name>\" }) inside exec)]",
        text, StringComparison.Ordinal);
  }

  [Fact]
  public void BootstrapToolsMapping_NamesItsActionsAsExecBridgeCalls()
  {
    string output = new SkillsBootstrapPromptProvider(new EmbeddedSkillCatalog()).Build();

    // The mapping's binding rows carry the bridge form verbatim, for actions the harness
    // really has.
    Assert.Contains("Tools.Invoke(\"skill_view\"", output, StringComparison.Ordinal);
    Assert.Contains("Tools.List()", output, StringComparison.Ordinal);
  }

  private sealed class EmptyCatalog : ISkillCatalog
  {
    public Task<Result<IReadOnlyList<SkillDefinition>>> ListAsync(CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<SkillDefinition>>([One()]));

    public Task<Result<SkillDefinition>> GetAsync(string name, CancellationToken ct = default)
        => Task.FromResult(Result.Success(One()));

    private static SkillDefinition One() => new(
        "sample", "sample description", "sample body", 1, SkillSource.BuiltIn,
        null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
  }

  private sealed class EmptyLearned : ILearnedSkillStore
  {
    public Task<Result<IReadOnlyList<SkillDefinition>>> ListAsync(CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<SkillDefinition>>([]));

    public Task<Result<SkillDefinition?>> GetAsync(string name, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<SkillDefinition?>(new DomainError("SkillNotFound", name)));

    public Task<Result<SkillDefinition>> CreateAsync(SkillDefinition skill, CancellationToken ct = default)
        => Task.FromResult(Result.Success(skill));

    public Task<Result<SkillDefinition>> UpdateAsync(SkillDefinition updated, CancellationToken ct = default)
        => Task.FromResult(Result.Success(updated));

    public Task<Result<bool>> DeleteAsync(string name, CancellationToken ct = default)
        => Task.FromResult(Result.Success(true));

    public Task<Result<int>> AppendUsageAsync(string name, DateTimeOffset viewedAt, CancellationToken ct = default)
        => Task.FromResult(Result.Success(0));
  }
}
