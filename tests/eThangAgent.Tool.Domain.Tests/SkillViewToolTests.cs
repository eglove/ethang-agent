using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;

namespace eThangAgent.ToolDomain.Tests;

public class SkillViewToolTests
{
  private static SkillDefinition Def(string name, string body,
      int version = 1, SkillSource source = SkillSource.BuiltIn,
      bool manual = false, string? origin = null) =>
      new(name, "description", body, version, source, manual, origin);

  private static SkillViewTool MakeTool(IReadOnlyList<SkillDefinition>? builtIns = null) =>
      new(new FakeCatalog(builtIns ?? []));

  // ---- Input strictness ----

  [Fact]
  public async Task MissingName_MissingParameter()
  {
    SkillViewTool tool = MakeTool();
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("skill_view", /*lang=json,strict*/ "{\"timeoutSeconds\":120}"), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("MissingParameter", result.Content, StringComparison.Ordinal);
    Assert.Contains("'name'", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task UnknownParameter_Rejected()
  {
    SkillViewTool tool = MakeTool();
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
    SkillViewTool tool = MakeTool();
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
    SkillViewTool tool = MakeTool();
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("skill_view",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"name":""}"""), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("non-empty", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task InvalidJsonArguments_Rejected()
  {
    SkillViewTool tool = MakeTool();
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("skill_view", "{\"timeoutSeconds\":120,bad"), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("not valid JSON", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task NonObjectArguments_Rejected()
  {
    SkillViewTool tool = MakeTool();
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("skill_view", "[]"), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("JSON object", result.Content, StringComparison.Ordinal);
  }

  // ---- Resolution & output format ----

  [Fact]
  public async Task BuiltInHit_AnnotationExact_BodyVerbatim()
  {
    SkillViewTool tool = MakeTool(builtIns: [Def("sample-skill", "First line.\nSecond line.")]);

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("skill_view",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"name":"sample-skill"}"""), ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    Assert.Equal("[skill sample-skill | builtin | v1]\nFirst line.\nSecond line.",
        result.Content);
  }

  [Fact]
  public async Task UnknownName_SkillNotFound()
  {
    SkillViewTool tool = MakeTool();
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("skill_view",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"name":"nope"}"""), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("Error [SkillNotFound]", result.Content, StringComparison.Ordinal);
    Assert.Contains("nope", result.Content, StringComparison.Ordinal);
  }

  // ---- File skills: origin annotation ----

  [Fact]
  public async Task FileHit_AnnotationCarriesOrigin()
  {
    SkillViewTool tool = MakeTool(
        builtIns: [Def("deploy-runbook", "Roll out.", source: SkillSource.File,
            manual: false, origin: @"C:\skills\global\deploy-runbook")]);

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("skill_view",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"name":"deploy-runbook"}"""), ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    Assert.Equal("[skill deploy-runbook | file | v1 | C:\\skills\\global\\deploy-runbook]\nRoll out.",
        result.Content);
  }

  [Fact]
  public async Task FileHit_ManualFlag_DoesNotAlterAnnotation()
  {
    SkillViewTool tool = MakeTool(
        builtIns: [Def("manual-skill", "Body.", source: SkillSource.File,
            manual: true, origin: @"C:\skills\manual-skill")]);

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("skill_view",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"name":"manual-skill"}"""), ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    Assert.Equal("[skill manual-skill | file | v1 | C:\\skills\\manual-skill]\nBody.",
        result.Content);
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
}
