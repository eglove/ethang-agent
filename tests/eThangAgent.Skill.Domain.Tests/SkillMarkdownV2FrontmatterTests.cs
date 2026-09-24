using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;

namespace eThangAgent.Skill.Domain.Tests;

/// <summary>agentskills.io v2 frontmatter rules: the manual-invocation flag,
/// metadata.version, known-but-unapplied keys, unknown-key warnings, and quoted
/// values — warnings never fail the parse.</summary>
public class SkillMarkdownV2FrontmatterTests
{
  [Fact]
  public void Parse_ManualFlag_TrueValueParsesFalseMissing()
  {
    Result<ParsedSkill> with = SkillMarkdown.Parse("---\nname: a\ndescription: b\ndisable-model-invocation: true\n---\nbody");
    Assert.True(with.IsSuccess);
    Assert.True(with.Value.Manual);

    Result<ParsedSkill> without = SkillMarkdown.Parse("---\nname: a\ndescription: b\n---\nbody");
    Assert.True(without.IsSuccess);
    Assert.False(without.Value.Manual);
  }

  [Fact]
  public void Parse_ManualFlag_NonBoolValueWarnsAndDefaultsFalse()
  {
    Result<ParsedSkill> r = SkillMarkdown.Parse("---\nname: a\ndescription: b\ndisable-model-invocation: sometimes\n---\nbody");
    Assert.True(r.IsSuccess);
    Assert.False(r.Value.Manual);
    Assert.Equal("unknown value for disable-model-invocation", Assert.Single(r.Value.Warnings));

    Result<ParsedSkill> mixedCaseFalse = SkillMarkdown.Parse("---\nname: a\ndescription: b\ndisable-model-invocation: False\n---\nbody");
    Assert.True(mixedCaseFalse.IsSuccess);
    Assert.False(mixedCaseFalse.Value.Manual);
    Assert.Empty(mixedCaseFalse.Value.Warnings);
  }

  [Fact]
  public void Parse_MetadataVersion_IntParsesNonIntWarns()
  {
    Result<ParsedSkill> intVersion = SkillMarkdown.Parse("---\nname: a\ndescription: b\nmetadata:\n  version: 3\n---\nbody");
    Assert.True(intVersion.IsSuccess);
    Assert.Equal(3, intVersion.Value.Version);

    Result<ParsedSkill> nonInt = SkillMarkdown.Parse("---\nname: a\ndescription: b\nmetadata:\n  version: three\n---\nbody");
    Assert.True(nonInt.IsSuccess);
    Assert.Null(nonInt.Value.Version);
    Assert.Equal("metadata.version is not an integer", Assert.Single(nonInt.Value.Warnings));
  }

  [Fact]
  public void Parse_MetadataAbsent_VersionNull()
  {
    Result<ParsedSkill> absent = SkillMarkdown.Parse("---\nname: a\ndescription: b\n---\nbody");
    Assert.True(absent.IsSuccess);
    Assert.Null(absent.Value.Version);
    Assert.Empty(absent.Value.Warnings);

    Result<ParsedSkill> blockWithoutVersion = SkillMarkdown.Parse("---\nname: a\ndescription: b\nmetadata:\n  owner: tooling\n---\nbody");
    Assert.True(blockWithoutVersion.IsSuccess);
    Assert.Null(blockWithoutVersion.Value.Version);
    Assert.Empty(blockWithoutVersion.Value.Warnings);
  }

  [Fact]
  public void Parse_KnownKeys_AllowedToolsLicenseCompatibility_WarnIgnored()
  {
    Result<ParsedSkill> r = SkillMarkdown.Parse("---\nname: a\ndescription: b\nallowed-tools: Bash\nlicense: MIT\ncompatibility: >=1.0\n---\nbody");
    Assert.True(r.IsSuccess);
    Assert.Equal((string[])
    [
      "ignored key allowed-tools (known but unapplied)",
      "ignored key license (known but unapplied)",
      "ignored key compatibility (known but unapplied)",
    ], r.Value.Warnings);
  }

  [Fact]
  public void Parse_KnownKeyRepeated_WarnsOncePerSkill()
  {
    // Cosmetics fix (rework final review): each known-but-unapplied key warns
    // once per skill, not once per occurrence.
    Result<ParsedSkill> r = SkillMarkdown.Parse("---\nname: a\ndescription: b\nallowed-tools: Bash\nallowed-tools: Web\nlicense: MIT\nlicense: Apache\n---\nbody");
    Assert.True(r.IsSuccess);
    Assert.Equal((string[])
    [
      "ignored key allowed-tools (known but unapplied)",
      "ignored key license (known but unapplied)",
    ], r.Value.Warnings);
  }
  [Fact]
  public void Parse_UnknownKey_Warns()
  {
    Result<ParsedSkill> r = SkillMarkdown.Parse("---\nname: a\ndescription: b\nfrobnicate: yes\n---\nbody");
    Assert.True(r.IsSuccess);
    Assert.Equal("unknown frontmatter key frobnicate", Assert.Single(r.Value.Warnings));
  }

  [Fact]
  public void Parse_UnknownKeyRepeated_WarnsOncePerSkill()
  {
    Result<ParsedSkill> r = SkillMarkdown.Parse("---\nname: a\ndescription: b\nfrobnicate: 1\nfrobnicate: 2\n---\nbody");
    Assert.True(r.IsSuccess);
    Assert.Equal("unknown frontmatter key frobnicate", Assert.Single(r.Value.Warnings));
  }
  [Fact]
  public void Parse_Warnings_DoNotFailParse()
  {
    Result<ParsedSkill> r = SkillMarkdown.Parse("---\nname: a\ndescription: b\nmystery: 1\nlicense: MIT\nmetadata:\n  version: x\n---\n\n# Body");
    Assert.True(r.IsSuccess);
    Assert.Equal(3, r.Value.Warnings.Count);
    Assert.Contains("unknown frontmatter key mystery", r.Value.Warnings);
    Assert.Contains("ignored key license (known but unapplied)", r.Value.Warnings);
    Assert.Contains("metadata.version is not an integer", r.Value.Warnings);
    Assert.Equal("# Body", r.Value.Body);
  }

  [Fact]
  public void Parse_QuotedValues_Stripped()
  {
    Result<ParsedSkill> single = SkillMarkdown.Parse("---\nname: 'a'\ndescription: 'does things'\n---\nbody");
    Assert.True(single.IsSuccess);
    Assert.Equal("a", single.Value.Name);
    Assert.Equal("does things", single.Value.Description);

    Result<ParsedSkill> doubleQuoted = SkillMarkdown.Parse("---\nname: \"a\"\ndescription: \"b\"\n---\nbody");
    Assert.True(doubleQuoted.IsSuccess);
    Assert.Equal("a", doubleQuoted.Value.Name);
    Assert.Equal("b", doubleQuoted.Value.Description);

    Result<ParsedSkill> unmatched = SkillMarkdown.Parse("---\nname: 'a\ndescription: b\n---\nbody");
    Assert.True(unmatched.IsSuccess);
    Assert.Equal("'a", unmatched.Value.Name);
  }

  [Fact]
  public void Parse_MetadataBlankLinesInsideBlock_VersionStillHarvested()
  {
    Result<ParsedSkill> r = SkillMarkdown.Parse("---\nname: a\ndescription: b\nmetadata:\n\n  version: 5\n\n---\nbody");
    Assert.True(r.IsSuccess);
    Assert.Equal(5, r.Value.Version);
    Assert.Empty(r.Value.Warnings);
  }

  [Fact]
  public void Parse_ExistingRequiredKeyRules_Unchanged()
  {
    Result<ParsedSkill> missingOpenFence = SkillMarkdown.Parse("name: n\ndescription: d\n---\nb");
    Assert.False(missingOpenFence.IsSuccess);
    Assert.Equal("MissingFrontmatter", missingOpenFence.Error.Code);

    Result<ParsedSkill> missingCloseFence = SkillMarkdown.Parse("---\nname: n\ndescription: d");
    Assert.False(missingCloseFence.IsSuccess);
    Assert.Equal("MissingFrontmatter", missingCloseFence.Error.Code);

    Result<ParsedSkill> missingName = SkillMarkdown.Parse("---\ndescription: d\n---\nb");
    Assert.False(missingName.IsSuccess);
    Assert.Equal("MissingKey", missingName.Error.Code);

    Result<ParsedSkill> missingDescription = SkillMarkdown.Parse("---\nname: n\n---\nb");
    Assert.False(missingDescription.IsSuccess);
    Assert.Equal("MissingKey", missingDescription.Error.Code);

    Result<ParsedSkill> emptyDescription = SkillMarkdown.Parse("---\nname: n\ndescription:\n---\nb");
    Assert.False(emptyDescription.IsSuccess);
    Assert.Equal("EmptyDescription", emptyDescription.Error.Code);
  }
}
