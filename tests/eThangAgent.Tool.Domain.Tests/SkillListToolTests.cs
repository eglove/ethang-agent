using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;

namespace eThangAgent.ToolDomain.Tests;

public class SkillListToolTests
{
  private static SkillDefinition Def(string name, string description,
      int version = 1, SkillSource source = SkillSource.BuiltIn,
      bool manual = false, string? origin = null) =>
      new(name, description, "BODY", version, source,
          ProvenanceSessionId: null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
          manual, origin);

  private static SkillListTool MakeTool(
      IReadOnlyList<SkillDefinition>? builtIns = null,
      IReadOnlyList<SkillDefinition>? learned = null,
      bool failCatalogList = false,
      bool failStoreList = false) =>
      new(new FakeCatalog(builtIns ?? [], failCatalogList),
          new FakeLearnedStore(learned ?? [], failStoreList));

  // ---- Input strictness (zero-parameter tool) ----

  [Fact]
  public async Task EmptyObject_IsAccepted()
  {
    ToolResult result = await MakeTool().ExecuteAsync(new RawToolInput("skill_list", /*lang=json,strict*/ "{\"timeoutSeconds\":120}"), ct: TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
  }

  [Theory]
  [InlineData(/*lang=json,strict*/ """{"timeoutSeconds":120,"verbose":true}""")]
  [InlineData(/*lang=json,strict*/ """{"timeoutSeconds":120,"name":"brainstorming"}""")]
  public async Task AnyParameter_IsRejected(string args)
  {
    ToolResult result = await MakeTool().ExecuteAsync(new RawToolInput("skill_list", args), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("Unknown parameter", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task NonObjectArguments_AreRejected()
  {
    ToolResult result = await MakeTool().ExecuteAsync(new RawToolInput("skill_list", "[]"), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("JSON object", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task InvalidJsonArguments_AreRejected()
  {
    ToolResult result = await MakeTool().ExecuteAsync(new RawToolInput("skill_list", "{\"timeoutSeconds\":120,bad"), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("not valid JSON", result.Content, StringComparison.Ordinal);
  }

  // ---- Listing format ----

  [Fact]
  public async Task NoSkillsAnywhere_HeaderCountsZero()
  {
    ToolResult result = await MakeTool().ExecuteAsync(new RawToolInput("skill_list", /*lang=json,strict*/ "{\"timeoutSeconds\":120}"), ct: TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    Assert.Equal("[skills: 0 available]", result.Content);
  }

  [Fact]
  public async Task MergesBuiltInAndLearned_SortedByName_TruncatesLongDescriptions()
  {
    string longDescription = new('d', 61);
    SkillListTool tool = new(
        new FakeCatalog([Def("brainstorming", longDescription)]),
        new FakeLearnedStore(
            [Def("my-skill", "Remember deployment quirks", version: 3, source: SkillSource.Learned)]));

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("skill_list", /*lang=json,strict*/ "{\"timeoutSeconds\":120}"), ct: TestContext.Current.CancellationToken);

    string expected =
        "[skills: 2 available]\n" +
        $"{"brainstorming",-20} builtin v1  {new string('d', 60)}\u2026\n" +
        $"{"my-skill",-20} learned v3  Remember deployment quirks";
    Assert.False(result.IsError);
    Assert.Equal(expected, result.Content);
  }

  [Fact]
  public async Task Description_ExactlySixtyChars_IsNotTruncated()
  {
    string description = new('d', 60);
    ToolResult result = await MakeTool([Def("s", description)])
            .ExecuteAsync(new RawToolInput("skill_list", /*lang=json,strict*/ "{\"timeoutSeconds\":120}"), ct: TestContext.Current.CancellationToken);
    Assert.Equal("[skills: 1 available]\n" + $"{"s",-20} builtin v1  {description}",
        result.Content);
  }

  // ---- File skills, the manual marker, and catalog diagnostics ----

  [Fact]
  public async Task FileSkill_RendersFileLabel()
  {
    ToolResult result = await MakeTool(
            builtIns: [Def("alpha", "from a directory", source: SkillSource.File, origin: @"C:\skills\global")])
            .ExecuteAsync(new RawToolInput("skill_list", /*lang=json,strict*/ "{\"timeoutSeconds\":120}"), ct: TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    Assert.Equal("[skills: 1 available]\n" + $"{"alpha",-20} file v1  from a directory",
        result.Content);
  }

  [Fact]
  public async Task ManualMarker_OnlyForManualSkills()
  {
    ToolResult result = await MakeTool(builtIns:
            [
                Def("auto-one", "auto", source: SkillSource.File, origin: @"C:\skills"),
                Def("manual-one", "manual only", source: SkillSource.File, manual: true, origin: @"C:\skills"),
            ])
            .ExecuteAsync(new RawToolInput("skill_list", /*lang=json,strict*/ "{\"timeoutSeconds\":120}"), ct: TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    Assert.Equal(
        "[skills: 2 available]\n" +
        $"{"auto-one",-20} file v1  auto\n" +
        $"{"manual-one",-20} file v1 [manual]  manual only",
        result.Content);
  }

  [Fact]
  public async Task ManualMarker_IsSourceIndependent()
  {
    ToolResult result = await MakeTool(
            learned: [Def("learned-manual", "learned", version: 2, source: SkillSource.Learned, manual: true)])
            .ExecuteAsync(new RawToolInput("skill_list", /*lang=json,strict*/ "{\"timeoutSeconds\":120}"), ct: TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    Assert.Equal("[skills: 1 available]\n" +
        $"{"learned-manual",-20} learned v2 [manual]  learned",
        result.Content);
  }

  [Fact]
  public async Task Diagnostics_RenderAfterRows_CollisionVerbatim_OthersPrefixed()
  {
    SkillListTool tool = new(
        new FakeCatalogWithDiagnostics(
            [Def("alpha", "first", source: SkillSource.File, origin: @"C:\skills")],
            [
                "no SKILL.md in broken, skipped",
                "[collision] alpha (workspace directory) shadowed by built-in",
            ]),
        new FakeLearnedStore([]));

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("skill_list", /*lang=json,strict*/ "{\"timeoutSeconds\":120}"), ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    Assert.Equal(
        "[skills: 1 available]\n" +
        $"{"alpha",-20} file v1  first\n" +
        "[warning] no SKILL.md in broken, skipped\n" +
        "[collision] alpha (workspace directory) shadowed by built-in",
        result.Content);
  }

  [Fact]
  public async Task SourceWarnings_KeepPositions_AfterDiagnostics()
  {
    SkillListTool tool = new(
        new FakeCatalogWithDiagnostics([], ["directory load failed C:\\skills: disk on fire"]),
        new FakeLearnedStore([], failList: true));

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("skill_list", /*lang=json,strict*/ "{\"timeoutSeconds\":120}"), ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    Assert.Equal(
        "[skills: 0 available]\n" +
        "[warning] directory load failed C:\\skills: disk on fire\n" +
        "[warning] learned skills unavailable: store down",
        result.Content);
  }

  [Fact]
  public async Task DiagnosticsFailure_IsSkipped_ListingStillSucceeds()
  {
    SkillListTool tool = new(
        new FakeCatalogWithDiagnostics([Def("alpha", "first")], failDiagnostics: true),
        new FakeLearnedStore([]));

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("skill_list", /*lang=json,strict*/ "{\"timeoutSeconds\":120}"), ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    Assert.Equal("[skills: 1 available]\n" + $"{"alpha",-20} builtin v1  first",
        result.Content);
  }
  // ---- Degradation on source failure ----

  [Fact]
  public async Task LearnedStoreFailure_AppendsWarning_StillSucceeds()
  {
    ToolResult result = await MakeTool([Def("brainstorming", "short")], failStoreList: true)
            .ExecuteAsync(new RawToolInput("skill_list", /*lang=json,strict*/ "{\"timeoutSeconds\":120}"), ct: TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    Assert.Equal(
        "[skills: 1 available]\n" +
        $"{"brainstorming",-20} builtin v1  short\n" +
        "[warning] learned skills unavailable: store down",
        result.Content);
  }

  [Fact]
  public async Task CatalogFailure_OmitsBuiltIns_AppendsWarning_StillSucceeds()
  {
    ToolResult result = await MakeTool(
            learned: [Def("my-skill", "learned thing", source: SkillSource.Learned)],
            failCatalogList: true)
            .ExecuteAsync(new RawToolInput("skill_list", /*lang=json,strict*/ "{\"timeoutSeconds\":120}"), ct: TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    Assert.Equal(
        "[skills: 1 available]\n" +
        $"{"my-skill",-20} learned v1  learned thing\n" +
        "[warning] built-in skills unavailable: catalog down",
        result.Content);
  }

  private sealed class FakeCatalog(IReadOnlyList<SkillDefinition> skills, bool failList = false) : ISkillCatalog
  {
    public Task<Result<IReadOnlyList<SkillDefinition>>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult(failList
            ? Result.Failure<IReadOnlyList<SkillDefinition>>(
                new DomainError("CatalogUnavailable", "catalog down"))
            : Result.Success(skills));

    public Task<Result<SkillDefinition>> GetAsync(string name, CancellationToken ct = default)
    {
      SkillDefinition? match = skills.FirstOrDefault(s => s.Name == name);
      return Task.FromResult(match is not null
          ? Result.Success(match)
          : Result.Failure<SkillDefinition>(new DomainError("SkillNotFound",
              $"No built-in skill named '{name}'.")));
    }
  }

  /// <summary>A catalog fake that also carries pre-formatted diagnostics, the
  /// shape the composite skill catalog exposes through ISkillCatalogDiagnostics.</summary>
  private sealed class FakeCatalogWithDiagnostics(IReadOnlyList<SkillDefinition> skills,
      IReadOnlyList<string>? diagnostics = null, bool failDiagnostics = false)
      : ISkillCatalog, ISkillCatalogDiagnostics
  {
    public Task<Result<IReadOnlyList<SkillDefinition>>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult(Result.Success(skills));

    public Task<Result<SkillDefinition>> GetAsync(string name, CancellationToken ct = default)
    {
      SkillDefinition? match = skills.FirstOrDefault(s => s.Name == name);
      return Task.FromResult(match is not null
          ? Result.Success(match)
          : Result.Failure<SkillDefinition>(new DomainError("SkillNotFound",
              $"No built-in skill named '{name}'.")));
    }

    public Task<Result<IReadOnlyList<string>>> GetDiagnosticsAsync(CancellationToken ct = default) =>
        Task.FromResult(failDiagnostics
            ? Result.Failure<IReadOnlyList<string>>(new DomainError("DiagnosticsUnavailable", "diagnostics down"))
            : Result.Success(diagnostics ?? []));
  }
  private sealed class FakeLearnedStore(IReadOnlyList<SkillDefinition> skills, bool failList = false) : ILearnedSkillStore
  {
    public Task<Result<IReadOnlyList<SkillDefinition>>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult(failList
            ? Result.Failure<IReadOnlyList<SkillDefinition>>(
                new DomainError("StoreUnavailable", "store down"))
            : Result.Success(skills));

    public Task<Result<SkillDefinition?>> GetAsync(string name, CancellationToken ct = default) =>
        Task.FromResult(Result.Success(
            skills.FirstOrDefault(s => s.Name == name)));

    public Task<Result<int>> AppendUsageAsync(string name, DateTimeOffset viewedAt, CancellationToken ct = default) =>
        Task.FromResult(Result.Failure<int>(new DomainError("StoreUnavailable", "store down")));

    public Task<Result<SkillDefinition>> CreateAsync(SkillDefinition skill, CancellationToken ct = default) =>
        throw new NotSupportedException("Not exercised by these tests.");

    public Task<Result<SkillDefinition>> UpdateAsync(SkillDefinition updated, CancellationToken ct = default) =>
        throw new NotSupportedException("Not exercised by these tests.");

    public Task<Result<bool>> DeleteAsync(string name, CancellationToken ct = default) =>
        throw new NotSupportedException("Not exercised by these tests.");
  }
}
