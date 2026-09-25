// Test fixture: sync temp-dir IO and best-effort cleanup are deliberate;
// the normalizer reads small local directories.
#pragma warning disable CA1849 // Call async methods when in an async method
#pragma warning disable CA2000 // Call IDisposable.Dispose on object created by
#pragma warning disable CA1031 // Do not catch general exception types

using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;

namespace eThangAgent.Skill.Domain.Tests;

/// <summary>Install layout rules (spec #27 task 3): skills/ subfolders win,
/// a root SKILL.md makes the repo a single skill, a sub-skill address yields
/// exactly that folder, and invalid names fail loudly — never renamed.</summary>
public sealed class RegistryLayoutNormalizerTests : IDisposable
{
  private readonly string _root;

  public RegistryLayoutNormalizerTests()
  {
    _root = Path.Combine(Path.GetTempPath(), "normtests-" + Guid.NewGuid().ToString("N"));
    _ = Directory.CreateDirectory(_root);
  }

  public void Dispose()
  {
    try
    {
      Directory.Delete(_root, true);
    }
    catch (IOException)
    {
      // best-effort temp cleanup
    }
    catch (UnauthorizedAccessException)
    {
      // best-effort temp cleanup
    }

    GC.SuppressFinalize(this);
  }

  private string WriteSkill(string relativeDir, string skillName = "sample")
  {
    string dir = Path.GetFullPath(Path.Combine(_root, relativeDir));
    _ = Directory.CreateDirectory(dir);
    File.WriteAllText(Path.Combine(dir, "SKILL.md"),
        "---\nname: " + skillName + "\ndescription: d\n---\nbody");
    return dir;
  }

  private string WriteFile(string relativePath, string content = "x")
  {
    string dir = Path.GetDirectoryName(Path.Combine(_root, relativePath))!;
    _ = Directory.CreateDirectory(dir);
    File.WriteAllText(Path.Combine(_root, relativePath), content);
    return Path.Combine(_root, relativePath);
  }

  [Fact]
  public void SkillsSubfolders_OneStagedSkillPerValidFolder()
  {
    string a = WriteSkill("skills/alpha", "alpha");
    string b = WriteSkill("skills/beta-tools", "beta-tools");
    _ = WriteFile("skills/empty-folder/readme.txt");

    Result<IReadOnlyList<StagedSkill>> r = RegistryLayoutNormalizer.Normalize(_root, null, "repo");
    Assert.True(r.IsSuccess);
    Assert.Equal(
        [("alpha", a), ("beta-tools", b)],
        [.. r.Value.Select(s => (s.Name, s.SourcePath))]);
  }

  [Fact]
  public void RootSkillMd_SingleSkillNamedAfterRepo()
  {
    _ = WriteFile("SKILL.md", "---\nname: whatever\ndescription: d\n---\nbody");
    _ = WriteFile("references/guide.md");

    Result<IReadOnlyList<StagedSkill>> r = RegistryLayoutNormalizer.Normalize(_root, null, "avoid-ai-writing");
    Assert.True(r.IsSuccess);
    StagedSkill one = Assert.Single(r.Value);
    Assert.Equal("avoid-ai-writing", one.Name);
    Assert.Equal(_root, one.SourcePath);
  }

  [Theory]
  [InlineData("target", "skills/target")]
  [InlineData("target", "target")]
  public void SubSkill_YieldsExactlyThatFolder(string sub, string layoutDir)
  {
    _ = WriteSkill("skills/alpha", "alpha");
    _ = WriteSkill("skills/" + sub, "target");
    _ = WriteSkill(layoutDir == "target" ? "target" : "skills/other", "other");

    Result<IReadOnlyList<StagedSkill>> r = RegistryLayoutNormalizer.Normalize(_root, "target", "repo");
    Assert.True(r.IsSuccess);
    StagedSkill one = Assert.Single(r.Value);
    Assert.Equal("target", one.Name);
    Assert.Equal(Path.Combine(_root, "skills", "target"), one.SourcePath);
  }

  [Fact]
  public void SubSkill_MissingFolder_FailsSkillFolderNotFound()
  {
    _ = WriteSkill("skills/alpha", "alpha");
    Result<IReadOnlyList<StagedSkill>> r = RegistryLayoutNormalizer.Normalize(_root, "nope", "repo");
    Assert.False(r.IsSuccess);
    Assert.Equal("SkillFolderNotFound", r.Error.Code);
  }

  [Fact]
  public void BothLayouts_SkillsDirWins_RootIgnored()
  {
    _ = WriteSkill("skills/alpha", "alpha");
    _ = WriteFile("SKILL.md", "---\nname: root-skill\ndescription: d\n---\nbody");

    Result<IReadOnlyList<StagedSkill>> r = RegistryLayoutNormalizer.Normalize(_root, null, "repo");
    Assert.True(r.IsSuccess);
    StagedSkill one = Assert.Single(r.Value);
    Assert.Equal("alpha", one.Name);
  }

  [Fact]
  public void InvalidFolderName_FailsInvalidLayout_NamesTheFolder()
  {
    _ = WriteSkill("skills/Not_Valid", "Not_Valid");
    _ = WriteSkill("skills/good-one", "good-one");

    Result<IReadOnlyList<StagedSkill>> r = RegistryLayoutNormalizer.Normalize(_root, null, "repo");
    Assert.False(r.IsSuccess);
    Assert.Equal("InvalidLayout", r.Error.Code);
    Assert.Contains("Not_Valid", r.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void EmptyStagedRoot_FailsInvalidLayout()
  {
    Result<IReadOnlyList<StagedSkill>> r = RegistryLayoutNormalizer.Normalize(_root, null, "repo");
    Assert.False(r.IsSuccess);
    Assert.Equal("InvalidLayout", r.Error.Code);
  }

  [Fact]
  public void MissingStagedRoot_FailsInvalidLayout()
  {
    string missing = Path.Combine(_root, "does-not-exist");
    Result<IReadOnlyList<StagedSkill>> r = RegistryLayoutNormalizer.Normalize(missing, null, "repo");
    Assert.False(r.IsSuccess);
    Assert.Equal("InvalidLayout", r.Error.Code);
  }

  [Fact]
  public void SkillCaseSensitiveFileNamings_FolderWithoutExactSkillMd_IsSkipped()
  {
    _ = WriteSkill("skills/alpha", "alpha");
    string dir = Path.Combine(_root, "skills", "wrongcase");
    _ = Directory.CreateDirectory(dir);
    File.WriteAllText(Path.Combine(dir, "skill.md"), "---\nname: w\ndescription: d\n---\nbody");

    Result<IReadOnlyList<StagedSkill>> r = RegistryLayoutNormalizer.Normalize(_root, null, "repo");
    Assert.True(r.IsSuccess);
    _ = Assert.Single(r.Value);
  }
}