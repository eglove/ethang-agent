using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;

namespace eThangAgent.Skill.Domain.Tests;

/// <summary>Hot reload (spec #26): ReloadAsync re-performs the composite load and
/// diffs the visible sets by name. Changed = description, body, version, manual, or
/// source-label change; manual skills never appear in the diff; the stored load is
/// replaced so subsequent List/Get serve the fresh view; reload is single-flight.</summary>
public class CompositeSkillCatalogReloadTests
{
  private const string GlobalDir = "C:\\skills\\global";

  [Fact]
  public async Task Reload_SkillAdded_AppearsInDiffAndInList()
  {
    FakeBuiltInCatalog builtIns = new();
    FakeSkillDirectorySource source = new();
    source.LoadReturns(GlobalDir, SkillFactory.File("alpha", GlobalDir));
    CompositeSkillCatalog catalog = new(builtIns.AsCatalog(), source.AsSource(),
        [new SkillDirectory(GlobalDir, SkillDirectoryScope.Global)]);

    _ = await catalog.ListAsync(TestContext.Current.CancellationToken);
    source.LoadReturns(GlobalDir, SkillFactory.File("alpha", GlobalDir), SkillFactory.File("beta", GlobalDir));

    Result<SkillReloadDiff> diff = await catalog.ReloadAsync(TestContext.Current.CancellationToken);

    Assert.True(diff.IsSuccess);
    Assert.Equal([("+", "beta")], [.. diff.Value.Added.Select(s => ("+", s.Name))]);
    Assert.Empty(diff.Value.Removed);
    Assert.Empty(diff.Value.Changed);

    Result<IReadOnlyList<SkillDefinition>> listed = await catalog.ListAsync(TestContext.Current.CancellationToken);
    Assert.True(listed.IsSuccess);
    Assert.Equal(["alpha", "beta"], [.. listed.Value.Select(s => s.Name)]);
  }

  [Fact]
  public async Task Reload_SkillRemoved_AppearsInDiffAndLeavesList()
  {
    FakeBuiltInCatalog builtIns = new(FakeBuiltInCatalog.Make("gone"));
    FakeSkillDirectorySource source = new();
    source.LoadReturns(GlobalDir, SkillFactory.File("alpha", GlobalDir));
    CompositeSkillCatalog catalog = new(builtIns.AsCatalog(), source.AsSource(),
        [new SkillDirectory(GlobalDir, SkillDirectoryScope.Global)]);

    _ = await catalog.ListAsync(TestContext.Current.CancellationToken);
    source.LoadReturns(GlobalDir);

    Result<SkillReloadDiff> diff = await catalog.ReloadAsync(TestContext.Current.CancellationToken);

    Assert.True(diff.IsSuccess);
    // The built-in never disappears; the vanishing file skill is the removal.
    Assert.Equal(["alpha"], [.. diff.Value.Removed.Select(s => s.Name)]);
    Assert.Empty(diff.Value.Added);
    Assert.Empty(diff.Value.Changed);
    Result<IReadOnlyList<SkillDefinition>> listed = await catalog.ListAsync(TestContext.Current.CancellationToken);
    Assert.True(listed.IsSuccess);
    Assert.DoesNotContain("alpha", listed.Value.Select(s => s.Name));
  }

  [Fact]
  public async Task Reload_SkillChanged_BodyOrManualCountsAsChanged()
  {
    FakeBuiltInCatalog builtIns = new();
    FakeSkillDirectorySource source = new();
    source.LoadReturns(GlobalDir, SkillFactory.File("alpha", GlobalDir));
    CompositeSkillCatalog catalog = new(builtIns.AsCatalog(), source.AsSource(),
        [new SkillDirectory(GlobalDir, SkillDirectoryScope.Global)]);

    _ = await catalog.ListAsync(TestContext.Current.CancellationToken);
    source.LoadReturns(GlobalDir, SkillFactory.File("alpha", GlobalDir) with { Body = "body v2" });

    Result<SkillReloadDiff> diff = await catalog.ReloadAsync(TestContext.Current.CancellationToken);

    Assert.True(diff.IsSuccess);
    SkillDefinition changed = Assert.Single(diff.Value.Changed);
    Assert.Equal("alpha", changed.Name);
    Assert.Empty(diff.Value.Added);
    Assert.Empty(diff.Value.Removed);
  }

  [Fact]
  public async Task Reload_ManualFlagToggles_NeverAnnouncedAsAddedOrRemoved()
  {
    // Manual never appears in the listing (or the announcement): a skill that
    // flips to manual behaves as REMOVED for announcement purposes; one that
    // flips from manual is ADDED. The diff exposes only the announcement view.
    FakeBuiltInCatalog builtIns = new();
    FakeSkillDirectorySource source = new();
    source.LoadReturns(GlobalDir, SkillFactory.File("alpha", GlobalDir, manual: false));
    CompositeSkillCatalog catalog = new(builtIns.AsCatalog(), source.AsSource(),
        [new SkillDirectory(GlobalDir, SkillDirectoryScope.Global)]);

    _ = await catalog.ListAsync(TestContext.Current.CancellationToken);
    source.LoadReturns(GlobalDir, SkillFactory.File("alpha", GlobalDir, manual: true));

    Result<SkillReloadDiff> diff = await catalog.ReloadAsync(TestContext.Current.CancellationToken);

    Assert.True(diff.IsSuccess);
    Assert.Equal(["alpha"], [.. diff.Value.Removed.Select(s => s.Name)]);
    Assert.Empty(diff.Value.Added);
    Assert.Empty(diff.Value.Changed);
  }

  [Fact]
  public async Task Reload_NoChanges_DiffIsEmpty()
  {
    FakeBuiltInCatalog builtIns = new(FakeBuiltInCatalog.Make("same"));
    FakeSkillDirectorySource source = new();
    source.LoadReturns(GlobalDir, SkillFactory.File("alpha", GlobalDir));
    CompositeSkillCatalog catalog = new(builtIns.AsCatalog(), source.AsSource(),
        [new SkillDirectory(GlobalDir, SkillDirectoryScope.Global)]);

    _ = await catalog.ListAsync(TestContext.Current.CancellationToken);

    Result<SkillReloadDiff> diff = await catalog.ReloadAsync(TestContext.Current.CancellationToken);

    Assert.True(diff.IsSuccess);
    Assert.Empty(diff.Value.Added);
    Assert.Empty(diff.Value.Removed);
    Assert.Empty(diff.Value.Changed);
  }

  [Fact]
  public async Task Reload_CollisionResolved_IsAnnouncedAsChange()
  {
    // The winner for a name moves when the higher-precedence copy disappears:
    // global alpha serves the listing first; when the global copy is gone, the
    // workspace copy takes over - same name, different source label = CHANGED.
    FakeBuiltInCatalog builtIns = new();
    FakeSkillDirectorySource source = new();
    source.LoadReturns(GlobalDir, SkillFactory.File("alpha", GlobalDir));
    source.LoadReturns("C:\\proj\\ws", SkillFactory.File("alpha", "C:\\proj\\ws"));
    CompositeSkillCatalog catalog = new(builtIns.AsCatalog(), source.AsSource(),
        [new SkillDirectory(GlobalDir, SkillDirectoryScope.Global),
             new SkillDirectory("C:\\proj\\ws", SkillDirectoryScope.Workspace)]);

    _ = await catalog.ListAsync(TestContext.Current.CancellationToken);
    source.LoadReturns(GlobalDir);

    Result<SkillReloadDiff> diff = await catalog.ReloadAsync(TestContext.Current.CancellationToken);

    Assert.True(diff.IsSuccess);
    Assert.Equal(["alpha"], [.. diff.Value.Changed.Select(s => s.Name)]);
    Assert.Empty(diff.Value.Added);
    Assert.Empty(diff.Value.Removed);
  }

  [Fact]
  public async Task List_AfterReload_MemoizationServesNewLoad()
  {
    FakeBuiltInCatalog builtIns = new();
    FakeSkillDirectorySource source = new();
    source.LoadReturns(GlobalDir, SkillFactory.File("alpha", GlobalDir));
    CompositeSkillCatalog catalog = new(builtIns.AsCatalog(), source.AsSource(),
        [new SkillDirectory(GlobalDir, SkillDirectoryScope.Global)]);

    _ = await catalog.ListAsync(TestContext.Current.CancellationToken);
    int callsAfterFirst = source.ListCalls;
    source.LoadReturns(GlobalDir, SkillFactory.File("alpha", GlobalDir), SkillFactory.File("new1", GlobalDir));

    _ = await catalog.ReloadAsync(TestContext.Current.CancellationToken);
    _ = await catalog.ListAsync(TestContext.Current.CancellationToken);
    _ = await catalog.ListAsync(TestContext.Current.CancellationToken);

    Assert.Equal(callsAfterFirst + 1, source.ListCalls);
  }
}
