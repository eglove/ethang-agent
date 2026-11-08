using eThangAgent.SkillDomain;

namespace eThangAgent.Skill.Domain.Tests;

/// <summary>Builds file-sourced skill definitions for catalog tests;
/// <c>Origin</c> mirrors the directory a file skill was loaded from.</summary>
internal static class SkillFactory
{
  public static SkillDefinition File(string name, string origin) => new(
      name, "desc " + name, "body " + name, Version: 1, SkillSource.File,
      Manual: false, Origin: origin);

  public static SkillDefinition File(string name, string origin, bool manual) => new(
      name, "desc " + name, "body " + name, Version: 1, SkillSource.File,
      Manual: manual, Origin: origin);
}
