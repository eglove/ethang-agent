using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;

namespace eThangAgent.ToolDomain.Tests;

public class SkillViewToolTests
{
  private static SkillDefinition Def(string name, string body,
      int version = 1, SkillSource source = SkillSource.BuiltIn) =>
      new(name, "description", body, version, source,
          ProvenanceSessionId: null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

  private static (SkillViewTool Tool, FakeLearnedStore Store) MakeTool(
      IReadOnlyList<SkillDefinition>? builtIns = null,
      IReadOnlyList<SkillDefinition>? learned = null,
      bool failAppendUsage = false)
  {
    FakeLearnedStore store = new(learned ?? [], failAppendUsage);
    return (new SkillViewTool(new FakeCatalog(builtIns ?? []), store), store);
  }

  // ---- Input strictness ----

  [Fact]
  public async Task MissingName_MissingParameter()
  {
    (SkillViewTool? tool, FakeLearnedStore _) = MakeTool();
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("skill_view", /*lang=json,strict*/ "{\"timeoutSeconds\":120}"), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("MissingParameter", result.Content, StringComparison.Ordinal);
    Assert.Contains("'name'", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task UnknownParameter_Rejected()
  {
    (SkillViewTool? tool, FakeLearnedStore _) = MakeTool();
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("skill_view",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"name":"a","encoding":"utf16"}"""), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("Unknown parameter", result.Content, StringComparison.Ordinal);
    Assert.Contains("encoding", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Name_MustBeString_NumberRejected()
  {
    (SkillViewTool? tool, FakeLearnedStore _) = MakeTool();
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("skill_view",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"name":42}"""), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("InvalidParameterType", result.Content, StringComparison.Ordinal);
    Assert.Contains("string", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Name_MustBeNonEmpty()
  {
    (SkillViewTool? tool, FakeLearnedStore _) = MakeTool();
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("skill_view",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"name":""}"""), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("non-empty", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task InvalidJsonArguments_Rejected()
  {
    (SkillViewTool? tool, FakeLearnedStore _) = MakeTool();
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("skill_view", "{\"timeoutSeconds\":120,bad"), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("not valid JSON", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task NonObjectArguments_Rejected()
  {
    (SkillViewTool? tool, FakeLearnedStore _) = MakeTool();
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("skill_view", "[]"), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("JSON object", result.Content, StringComparison.Ordinal);
  }

  // ---- Resolution & output format ----

  [Fact]
  public async Task BuiltInHit_AnnotationExact_BodyVerbatim_RecordsUsage()
  {
    (SkillViewTool? tool, FakeLearnedStore? store) = MakeTool(builtIns: [Def("brainstorming", "First line.\nSecond line.")]);

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("skill_view",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"name":"brainstorming"}"""), ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    Assert.Equal("[skill brainstorming | builtin | v1]\nFirst line.\nSecond line.",
        result.Content);

    FakeLearnedStore.UsageCall call = Assert.Single(store.UsageCalls);
    Assert.Equal("brainstorming", call.Name);
    Assert.True(call.ViewedAt >= DateTimeOffset.UtcNow.AddMinutes(-1),
        "usage timestamp should be roughly now");
  }

  [Fact]
  public async Task LearnedHit_WhenCatalogMisses_RecordsUsage()
  {
    (SkillViewTool? tool, FakeLearnedStore? store) = MakeTool(learned: [Def("my-skill", "Learned body.", version: 3,
            source: SkillSource.Learned)]);

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("skill_view",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"name":"my-skill"}"""), ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    Assert.Equal("[skill my-skill | learned | v3]\nLearned body.", result.Content);
    _ = Assert.Single(store.UsageCalls);
  }

  [Fact]
  public async Task BuiltInWins_OverSameNamedLearned()
  {
    (SkillViewTool? tool, FakeLearnedStore _) = MakeTool(
        builtIns: [Def("shared", "Built-in body.", version: 1)],
        learned: [Def("shared", "Learned body.", version: 7, source: SkillSource.Learned)]);

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("skill_view",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"name":"shared"}"""), ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    Assert.Equal("[skill shared | builtin | v1]\nBuilt-in body.", result.Content);
  }

  [Fact]
  public async Task UnknownName_SkillNotFound()
  {
    (SkillViewTool? tool, FakeLearnedStore _) = MakeTool();
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("skill_view",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"name":"nope"}"""), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("Error [SkillNotFound]", result.Content, StringComparison.Ordinal);
    Assert.Contains("nope", result.Content, StringComparison.Ordinal);
  }

  // ---- Best-effort usage recording ----

  [Fact]
  public async Task UsageRecordingFailure_WarningAppended_ViewStillSucceeds()
  {
    (SkillViewTool? tool, FakeLearnedStore _) = MakeTool(builtIns: [Def("brainstorming", "The body.")],
        failAppendUsage: true);

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("skill_view",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"name":"brainstorming"}"""), ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    Assert.Equal("[skill brainstorming | builtin | v1]\nThe body." +
                 "\n[warning] usage not recorded", result.Content);
  }

  private sealed class FakeCatalog(IReadOnlyList<SkillDefinition> skills) : ISkillCatalog
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
  }

  private sealed class FakeLearnedStore(IReadOnlyList<SkillDefinition> skills, bool failAppendUsage)
      : ILearnedSkillStore
  {
    internal sealed record UsageCall(string Name, DateTimeOffset ViewedAt);

    public List<UsageCall> UsageCalls { get; } = [];

    public Task<Result<IReadOnlyList<SkillDefinition>>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult(Result.Success(skills));

    public Task<Result<SkillDefinition?>> GetAsync(string name, CancellationToken ct = default) =>
        Task.FromResult(Result.Success(
            skills.FirstOrDefault(s => s.Name == name)));

    public Task<Result<int>> AppendUsageAsync(string name, DateTimeOffset viewedAt, CancellationToken ct = default)
    {
      UsageCalls.Add(new UsageCall(name, viewedAt));
      return Task.FromResult(failAppendUsage
          ? Result.Failure<int>(new DomainError("StoreUnavailable", "store down"))
          : Result.Success(1));
    }

    public Task<Result<SkillDefinition>> CreateAsync(SkillDefinition skill, CancellationToken ct = default) =>
        throw new NotSupportedException("Not exercised by these tests.");

    public Task<Result<SkillDefinition>> UpdateAsync(SkillDefinition updated, CancellationToken ct = default) =>
        throw new NotSupportedException("Not exercised by these tests.");

    public Task<Result<bool>> DeleteAsync(string name, CancellationToken ct = default) =>
        throw new NotSupportedException("Not exercised by these tests.");
  }
}
