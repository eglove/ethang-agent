using eThangAgent.SkillDomain;

namespace eThangAgent.Skill.Domain.Tests;

/// <summary>The description linter (vault move 6b): deterministic checks for the
/// documented trigger rules - third person, what+when-to-use, front-loaded key
/// terms. Clean descriptions yield no findings.</summary>
public class SkillDescriptionLinterTests
{
  [Fact]
  public void Lint_CleanDescription_NoFindings()
  {
    IReadOnlyList<SkillDescriptionFinding> findings = SkillDescriptionLinter.Lint(
        "Execute a stored implementation plan task by task with per-task review. Use when a plan record exists and the user asks to execute.");

    Assert.Empty(findings);
  }

  [Fact]
  public void Lint_FirstPerson_ThirdPersonFinding()
  {
    IReadOnlyList<SkillDescriptionFinding> findings = SkillDescriptionLinter.Lint(
        "I help you review code. Use when reviewing a pull request.");

    SkillDescriptionFinding finding = Assert.Single(findings);
    Assert.Equal("ThirdPerson", finding.Rule);
  }

  [Fact]
  public void Lint_NoWhenClause_WhatAndWhenFinding()
  {
    IReadOnlyList<SkillDescriptionFinding> findings = SkillDescriptionLinter.Lint(
        "Reviews code for defects and style problems.");

    SkillDescriptionFinding finding = Assert.Single(findings);
    Assert.Equal("WhatAndWhen", finding.Rule);
  }

  [Fact]
  public void Lint_BoilerplateOpener_FrontLoadFinding()
  {
    IReadOnlyList<SkillDescriptionFinding> findings = SkillDescriptionLinter.Lint(
        "A skill for reviewing code. Use when reviewing a pull request.");

    SkillDescriptionFinding finding = Assert.Single(findings);
    Assert.Equal("FrontLoad", finding.Rule);
  }

  [Fact]
  public void Lint_MultipleProblems_AllReported()
  {
    IReadOnlyList<SkillDescriptionFinding> findings = SkillDescriptionLinter.Lint(
        "This skill helps me review things.");

    Assert.Equal(["ThirdPerson", "WhatAndWhen", "FrontLoad"],
        [.. findings.Select(f => f.Rule)]);
  }

  [Fact]
  public void Lint_EmptyDescription_SingleEmptyFinding()
  {
    IReadOnlyList<SkillDescriptionFinding> findings = SkillDescriptionLinter.Lint("   ");

    SkillDescriptionFinding finding = Assert.Single(findings);
    Assert.Equal("Empty", finding.Rule);
  }
}
