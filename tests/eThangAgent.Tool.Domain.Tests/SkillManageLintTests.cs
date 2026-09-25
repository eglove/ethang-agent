
using eThangAgent.SkillDomain;

namespace eThangAgent.ToolDomain.Tests;

/// <summary>skill_manage Lint action (vault move 6b): deterministic description
/// linting over any catalog or learned skill - read-only, no store writes.</summary>
public class SkillManageLintTests
{
  private static readonly DateTimeOffset ClockNow = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

  private static SkillDefinition Def(string name, string description, SkillSource source) =>
      new(name, description, "body", 1, source, null, DateTimeOffset.UnixEpoch,
          DateTimeOffset.UnixEpoch, false, null);

  private static (SkillManageTool Tool, SkillManageToolTests.FakeCatalog Catalog, SkillManageToolTests.FakeLearnedStore Store) MakeTool(
      IReadOnlyList<SkillDefinition>? builtIns = null,
      IReadOnlyList<SkillDefinition>? learned = null)
  {
    SkillManageToolTests.FakeCatalog catalog = new(builtIns ?? []);
    SkillManageToolTests.FakeLearnedStore store = new(learned ?? []);
    return (new SkillManageTool(catalog, store, () => ClockNow), catalog, store);
  }

  [Fact]
  public async Task Lint_BuiltInWithCleanDescription_ReportsClean()
  {
    (SkillManageTool? tool, _, _) = MakeTool(
        builtIns: [Def("clean-skill", "Reviews code for defects. Use when reviewing a pull request.", SkillSource.BuiltIn)]);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("skill_manage",
        /*lang=json,strict*/ "{\"timeoutSeconds\":120,\"action\":\"Lint\",\"name\":\"clean-skill\"}"),
        ct: TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    Assert.Contains("[skill-lint] 'clean-skill' clean", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Lint_FirstPersonDescription_ReportsThirdPersonRule()
  {
    (SkillManageTool? tool, _, _) = MakeTool(
        builtIns: [Def("my-skill", "I help review code. Use when reviewing.", SkillSource.BuiltIn)]);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("skill_manage",
        /*lang=json,strict*/ "{\"timeoutSeconds\":120,\"action\":\"Lint\",\"name\":\"my-skill\"}"),
        ct: TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    Assert.Contains("[lint] ThirdPerson", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Lint_LearnedSkill_LintsTheLearnedDescription()
  {
    (SkillManageTool? tool, _, _) = MakeTool(
        learned: [Def("learned-one", "Reviews things.", SkillSource.Learned)]);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("skill_manage",
        /*lang=json,strict*/ "{\"timeoutSeconds\":120,\"action\":\"Lint\",\"name\":\"learned-one\"}"),
        ct: TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    Assert.Contains("[lint] WhatAndWhen", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Lint_UnknownName_SkillNotFound()
  {
    (SkillManageTool? tool, _, _) = MakeTool();
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("skill_manage",
        /*lang=json,strict*/ "{\"timeoutSeconds\":120,\"action\":\"Lint\",\"name\":\"nope\"}"),
        ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("SkillNotFound", result.Content, StringComparison.Ordinal);
  }
}
