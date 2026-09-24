using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;

namespace eThangAgent.Skill.Domain.Tests;

/// <summary>Composite catalog over built-ins plus configured skill directories:
/// precedence BuiltIn &gt; global directory &gt; workspace directory, shadowed
/// names kept for diagnostics only, loads memoized per instance.</summary>
public class CompositeSkillCatalogTests
{
  private const string GlobalDir = "C:\\skills\\global";
  private const string WorkspaceDir = "C:\\proj\\.agents\\skills";

  [Fact]
  public async Task List_Priority_BuiltInBeatsFileAndGlobalBeatsWorkspace()
  {
    FakeBuiltInCatalog builtIns = new(FakeBuiltInCatalog.Make("alpha"));
    FakeSkillDirectorySource source = new();
    source.LoadReturns(GlobalDir, SkillFactory.File("alpha", GlobalDir), SkillFactory.File("beta", GlobalDir));
    source.LoadReturns(WorkspaceDir,
        SkillFactory.File("alpha", WorkspaceDir), SkillFactory.File("beta", WorkspaceDir),
        SkillFactory.File("gamma", WorkspaceDir));

    CompositeSkillCatalog catalog = new(builtIns.AsCatalog(), source.AsSource(),
        [new SkillDirectory(GlobalDir, SkillDirectoryScope.Global),
             new SkillDirectory(WorkspaceDir, SkillDirectoryScope.Workspace)]);

    Result<IReadOnlyList<SkillDefinition>> r = await catalog.ListAsync(TestContext.Current.CancellationToken);

    Assert.True(r.IsSuccess);
    Assert.Equal([("alpha", SkillSource.BuiltIn, null),
                      ("beta", SkillSource.File, GlobalDir),
                      ("gamma", SkillSource.File, WorkspaceDir)],
        [.. r.Value.Select(s => (s.Name, s.Source, s.Origin))]);
  }

  [Fact]
  public async Task List_ShadowedEntries_ExcludedFromListButInDiagnostics()
  {
    FakeBuiltInCatalog builtIns = new(FakeBuiltInCatalog.Make("alpha"));
    FakeSkillDirectorySource source = new();
    source.LoadReturns(GlobalDir, SkillFactory.File("alpha", GlobalDir), SkillFactory.File("beta", GlobalDir));
    source.LoadReturns(WorkspaceDir, SkillFactory.File("alpha", WorkspaceDir), SkillFactory.File("beta", WorkspaceDir));

    CompositeSkillCatalog catalog = new(builtIns.AsCatalog(), source.AsSource(),
        [new SkillDirectory(GlobalDir, SkillDirectoryScope.Global),
             new SkillDirectory(WorkspaceDir, SkillDirectoryScope.Workspace)]);

    Result<IReadOnlyList<SkillDefinition>> r = await catalog.ListAsync(TestContext.Current.CancellationToken);

    Assert.True(r.IsSuccess);
    Assert.Equal(["alpha", "beta"], [.. r.Value.Select(s => s.Name)]);

    Result<IReadOnlyList<string>> diagnostics = await catalog.GetDiagnosticsAsync(TestContext.Current.CancellationToken);
    Assert.True(diagnostics.IsSuccess);
    Assert.Equal(
        [
            "[collision] alpha (global directory) shadowed by built-in",
                "[collision] beta (workspace directory) shadowed by global directory",
            ],
        diagnostics.Value);
  }

  [Fact]
  public async Task Get_ResolvesByPrecedence()
  {
    FakeBuiltInCatalog builtIns = new(FakeBuiltInCatalog.Make("alpha"));
    FakeSkillDirectorySource source = new();
    source.LoadReturns(GlobalDir, SkillFactory.File("alpha", GlobalDir), SkillFactory.File("beta", GlobalDir));
    source.LoadReturns(WorkspaceDir, SkillFactory.File("beta", WorkspaceDir), SkillFactory.File("gamma", WorkspaceDir));

    CompositeSkillCatalog catalog = new(builtIns.AsCatalog(), source.AsSource(),
        [new SkillDirectory(GlobalDir, SkillDirectoryScope.Global),
             new SkillDirectory(WorkspaceDir, SkillDirectoryScope.Workspace)]);

    Result<SkillDefinition> alpha = await catalog.GetAsync("alpha", TestContext.Current.CancellationToken);
    Assert.True(alpha.IsSuccess);
    Assert.Equal(SkillSource.BuiltIn, alpha.Value.Source);

    Result<SkillDefinition> beta = await catalog.GetAsync("beta", TestContext.Current.CancellationToken);
    Assert.True(beta.IsSuccess);
    Assert.Equal(GlobalDir, beta.Value.Origin);

    Result<SkillDefinition> gamma = await catalog.GetAsync("gamma", TestContext.Current.CancellationToken);
    Assert.True(gamma.IsSuccess);
    Assert.Equal(WorkspaceDir, gamma.Value.Origin);

    Result<SkillDefinition> missing = await catalog.GetAsync("missing", TestContext.Current.CancellationToken);
    Assert.False(missing.IsSuccess);
  }

  [Fact]
  public async Task List_EmptyDirectories_BuiltInsOnly()
  {
    FakeBuiltInCatalog builtIns = new(FakeBuiltInCatalog.Make("m"), FakeBuiltInCatalog.Make("k"));
    FakeSkillDirectorySource source = new();

    CompositeSkillCatalog catalog = new(builtIns.AsCatalog(), source.AsSource(), []);

    Result<IReadOnlyList<SkillDefinition>> r = await catalog.ListAsync(TestContext.Current.CancellationToken);

    Assert.True(r.IsSuccess);
    Assert.Equal(["k", "m"], [.. r.Value.Select(s => s.Name)]);
    Assert.Equal(0, source.ListCalls);
  }

  [Fact]
  public async Task List_SourceFailure_DegradesToDiagnostic()
  {
    FakeBuiltInCatalog builtIns = new(FakeBuiltInCatalog.Make("k"));
    FakeSkillDirectorySource source = new();
    source.LoadFails(GlobalDir, new DomainError("DirectoryReadFailed", "disk on fire"));
    source.LoadReturns(WorkspaceDir, SkillFactory.File("gamma", WorkspaceDir));

    CompositeSkillCatalog catalog = new(builtIns.AsCatalog(), source.AsSource(),
        [new SkillDirectory(GlobalDir, SkillDirectoryScope.Global),
             new SkillDirectory(WorkspaceDir, SkillDirectoryScope.Workspace)]);

    Result<IReadOnlyList<SkillDefinition>> r = await catalog.ListAsync(TestContext.Current.CancellationToken);

    Assert.True(r.IsSuccess);
    Assert.Equal([FakeBuiltInCatalog.Make("k").Name, "gamma"], [.. r.Value.Select(s => s.Name)]);

    Result<IReadOnlyList<string>> diagnostics = await catalog.GetDiagnosticsAsync(TestContext.Current.CancellationToken);
    Assert.True(diagnostics.IsSuccess);
    Assert.Equal([$"directory load failed {GlobalDir}: disk on fire"], diagnostics.Value);
  }

  [Fact]
  public async Task List_BuiltInFailure_DegradesToDiagnostic()
  {
    FakeBuiltInCatalog builtIns = new(FakeBuiltInCatalog.Make("a"))
    {
      Failing = new DomainError("CatalogUnavailable", "catalog exploded"),
    };
    FakeSkillDirectorySource source = new();
    source.LoadReturns(GlobalDir, SkillFactory.File("alpha", GlobalDir));

    CompositeSkillCatalog catalog = new(builtIns.AsCatalog(), source.AsSource(),
        [new SkillDirectory(GlobalDir, SkillDirectoryScope.Global)]);

    Result<IReadOnlyList<SkillDefinition>> r = await catalog.ListAsync(TestContext.Current.CancellationToken);

    Assert.True(r.IsSuccess);
    Assert.Equal([SkillFactory.File("alpha", GlobalDir).Name], [.. r.Value.Select(s => s.Name)]);

    Result<IReadOnlyList<string>> diagnostics = await catalog.GetDiagnosticsAsync(TestContext.Current.CancellationToken);
    Assert.True(diagnostics.IsSuccess);
    Assert.Equal([$"built-in catalog unavailable: catalog exploded"], diagnostics.Value);
  }

  [Fact]
  public async Task List_IncludesManualSkills()
  {
    FakeBuiltInCatalog builtIns = new();
    FakeSkillDirectorySource source = new();
    source.LoadReturns(GlobalDir,
        SkillFactory.File("manual-one", GlobalDir, manual: true),
        SkillFactory.File("plain", GlobalDir));

    CompositeSkillCatalog catalog = new(builtIns.AsCatalog(), source.AsSource(),
        [new SkillDirectory(GlobalDir, SkillDirectoryScope.Global)]);

    Result<IReadOnlyList<SkillDefinition>> r = await catalog.ListAsync(TestContext.Current.CancellationToken);

    Assert.True(r.IsSuccess);
    Assert.Equal(["manual-one", "plain"], [.. r.Value.Select(s => s.Name)]);
    Assert.Contains(r.Value, s => s.Name == "manual-one" && s.Manual);
  }

  [Fact]
  public async Task List_SortedByNameWithinSourceOrder()
  {
    FakeBuiltInCatalog builtIns = new(FakeBuiltInCatalog.Make("m"), FakeBuiltInCatalog.Make("k"));
    FakeSkillDirectorySource source = new();
    source.LoadReturns(GlobalDir, SkillFactory.File("z", GlobalDir), SkillFactory.File("a", GlobalDir));
    source.LoadReturns(WorkspaceDir, SkillFactory.File("c", WorkspaceDir));

    CompositeSkillCatalog catalog = new(builtIns.AsCatalog(), source.AsSource(),
        [new SkillDirectory(GlobalDir, SkillDirectoryScope.Global),
             new SkillDirectory(WorkspaceDir, SkillDirectoryScope.Workspace)]);

    Result<IReadOnlyList<SkillDefinition>> r = await catalog.ListAsync(TestContext.Current.CancellationToken);

    Assert.True(r.IsSuccess);
    Assert.Equal([
        FakeBuiltInCatalog.Make("k").Name,
            FakeBuiltInCatalog.Make("m").Name,
            "a", "c", "z",
        ], [.. r.Value.Select(s => s.Name)]);
  }

  [Fact]
  public async Task GetDiagnostics_Memoized()
  {
    FakeBuiltInCatalog builtIns = new(FakeBuiltInCatalog.Make("k"));
    FakeSkillDirectorySource source = new();
    source.LoadReturns(GlobalDir, SkillFactory.File("alpha", GlobalDir));

    CompositeSkillCatalog catalog = new(builtIns.AsCatalog(), source.AsSource(),
        [new SkillDirectory(GlobalDir, SkillDirectoryScope.Global)]);

    Result<IReadOnlyList<string>> first = await catalog.GetDiagnosticsAsync(TestContext.Current.CancellationToken);
    Result<IReadOnlyList<string>> second = await catalog.GetDiagnosticsAsync(TestContext.Current.CancellationToken);
    Result<IReadOnlyList<SkillDefinition>> listed = await catalog.ListAsync(TestContext.Current.CancellationToken);

    Assert.True(first.IsSuccess);
    Assert.True(second.IsSuccess);
    Assert.True(listed.IsSuccess);
    Assert.Equal(first.Value, second.Value);
    Assert.Equal(1, source.ListCalls);
    Assert.Equal(1, builtIns.ListCalls);
  }
}
