using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;

namespace eThangAgent.ToolDomain.Tests;

/// <summary>skill_lint: the deterministic description linter over a catalog skill.
///     Read-only — it resolves a name, lints the description, and writes nothing.</summary>
public class SkillLintToolTests
{
  private static SkillDefinition Def(string name, string description) =>
      new(name, description, "BODY", 1, SkillSource.BuiltIn);

  private static SkillLintTool MakeTool(params SkillDefinition[] skills) =>
      new(new FakeCatalog(skills));

  private static Task<ToolResult> Lint(SkillLintTool tool, string name) =>
      tool.ExecuteAsync(new RawToolInput("skill_lint",
          $"{{\"timeoutSeconds\":120,\"name\":\"{name}\"}}"), ct: TestContext.Current.CancellationToken);

  [Fact]
  public async Task CleanDescription_ReportsClean()
  {
    ToolResult result = await Lint(MakeTool(Def("clean-skill",
        "Reviews code for defects. Use when reviewing a pull request.")), "clean-skill");

    Assert.False(result.IsError);
    Assert.Equal("[skill-lint] 'clean-skill' clean", result.Content);
  }

  [Fact]
  public async Task FirstPersonAndMissingTrigger_ReportEveryFinding()
  {
    ToolResult result = await Lint(MakeTool(Def("my-skill", "I help review code.")), "my-skill");

    Assert.False(result.IsError);
    Assert.Contains("[skill-lint] 'my-skill' has 2 finding(s):", result.Content, StringComparison.Ordinal);
    Assert.Contains("[lint] ThirdPerson: ", result.Content, StringComparison.Ordinal);
    Assert.Contains("[lint] WhatAndWhen: ", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task UnknownName_SkillNotFound()
  {
    ToolResult result = await Lint(MakeTool(), "no-such-skill-anywhere");

    Assert.True(result.IsError);
    Assert.Contains("Error [SkillNotFound]", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task MissingName_MissingParameter()
  {
    ToolResult result = await MakeTool().ExecuteAsync(
        new RawToolInput("skill_lint", /*lang=json,strict*/ "{\"timeoutSeconds\":120}"),
        ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.Contains("MissingParameter", result.Content, StringComparison.Ordinal);
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
              $"No skill named '{name}'.")));
    }
  }
}
