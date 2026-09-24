using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;

namespace eThangAgent.FileSystem.ACL.Tests;

// Test helpers: sync temp-file IO and best-effort cleanup are deliberate;
// real temp directories are recreated per test instance and removed in Dispose.
#pragma warning disable CA1849 // Call async methods when in an async method
#pragma warning disable CA2000 // Call IDisposable.Dispose on object created by
#pragma warning disable CA1031 // Do not catch general exception types
#pragma warning disable S6966 // Use await instead of a blocking task (sync temp-file helpers)
public sealed class DirectorySkillSourceTests : IDisposable
{
  private const string SkillFileName = "SKILL.md";

  private const string ValidAlpha = "---\nname: alpha\ndescription: Alpha does things.\n---\nAlpha body.";
  private const string ValidBeta = "---\nname: beta\ndescription: Beta does other things.\n---\nBeta body.";

  private readonly string _root = Directory.CreateTempSubdirectory("ethang-skilldir").FullName;
  private readonly DirectorySkillSource _source = new();

  public void Dispose()
  {
    try
    {
      Directory.Delete(_root, recursive: true);
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

  [Fact]
  public async Task List_TwoSkillFolders_ReturnsWithOriginPaths()
  {
    string alphaFolder = WriteSkillFolder("alpha", ValidAlpha);
    string betaFolder = WriteSkillFolder("beta", ValidBeta);

    Result<SkillDirectoryLoad> result = await _source.ListAsync(_root, TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal(2, result.Value.Skills.Count);
    Assert.Equal("alpha", result.Value.Skills[0].Name);
    Assert.Equal("beta", result.Value.Skills[1].Name);
    Assert.Equal(alphaFolder, result.Value.Skills[0].Origin);
    Assert.Equal(betaFolder, result.Value.Skills[1].Origin);
    foreach (SkillDefinition skill in result.Value.Skills)
    {
      Assert.Equal(SkillSource.File, skill.Source);
      Assert.Null(skill.ProvenanceSessionId);
      Assert.Equal(1, skill.Version);
      Assert.False(skill.Manual);
      Assert.Equal(skill.CreatedAt, skill.UpdatedAt);
    }

    Assert.Empty(result.Value.Diagnostics);
  }

  [Fact]
  public async Task List_RootLevelSkillMd_SingleSkill()
  {
    File.WriteAllText(Path.Combine(_root, SkillFileName), ValidAlpha);

    Result<SkillDirectoryLoad> result = await _source.ListAsync(_root, TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    SkillDefinition skill = Assert.Single(result.Value.Skills);
    Assert.Equal("alpha", skill.Name);
    Assert.Equal(_root, skill.Origin);
    Assert.Empty(result.Value.Diagnostics);
  }

  [Fact]
  public async Task List_FolderWithoutSkillMd_DiagnosticSkipped()
  {
    string folder = Path.Combine(_root, "not-a-skill");
    _ = Directory.CreateDirectory(folder);
    File.WriteAllText(Path.Combine(folder, "README.md"), "not a skill");

    Result<SkillDirectoryLoad> result = await _source.ListAsync(_root, TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Empty(result.Value.Skills);
    Assert.Equal("no SKILL.md in not-a-skill, skipped", Assert.Single(result.Value.Diagnostics));
  }

  [Fact]
  public async Task List_InvalidFrontmatter_DiagnosticNamesFile()
  {
    string folder = WriteSkillFolder("broken", "---\ndescription: no name key\n---\nbody");

    Result<SkillDirectoryLoad> result = await _source.ListAsync(_root, TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Empty(result.Value.Skills);
    string diagnostic = Assert.Single(result.Value.Diagnostics);
    Assert.StartsWith($"invalid skill file {Path.Combine(folder, SkillFileName)}:", diagnostic, StringComparison.Ordinal);
    Assert.Contains("Frontmatter requires a 'name:' key.", diagnostic, StringComparison.Ordinal);
  }

  [Fact]
  public async Task List_UnknownKeyWarning_PrefixedWithSkillName()
  {
    _ = WriteSkillFolder("gamma", "---\nname: gamma\ndescription: Gamma skill.\ncustom-key: free text\n---\nGamma body.");

    Result<SkillDirectoryLoad> result = await _source.ListAsync(_root, TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    _ = Assert.Single(result.Value.Skills);
    Assert.Equal("gamma: unknown frontmatter key custom-key", Assert.Single(result.Value.Diagnostics));
  }

  [Fact]
  public async Task List_MissingDirectory_FailsDirectoryNotFound()
  {
    string missing = Path.Combine(_root, "missing");

    Result<SkillDirectoryLoad> result = await _source.ListAsync(missing, TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal("DirectoryNotFound", result.Error.Code);
    Assert.Contains(missing, result.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task List_EmptyDirectory_SucceedsWithNoSkills()
  {
    Result<SkillDirectoryLoad> result = await _source.ListAsync(_root, TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Empty(result.Value.Skills);
    Assert.Empty(result.Value.Diagnostics);
  }

  [Fact]
  public async Task List_CrLf_File_ParsesCorrectly()
  {
    _ = WriteSkillFolder("crlf", "---\r\nname: crlf-skill\r\ndescription: Written with CRLF endings.\r\n---\r\nCRLF body line.");

    Result<SkillDirectoryLoad> result = await _source.ListAsync(_root, TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    SkillDefinition skill = Assert.Single(result.Value.Skills);
    Assert.Equal("crlf-skill", skill.Name);
    Assert.Equal("Written with CRLF endings.", skill.Description);
    Assert.Equal("CRLF body line.", skill.Body);
  }

  [Fact]
  public async Task List_HiddenFolder_SkippedSilently()
  {
    _ = WriteSkillFolder(".hidden", ValidAlpha);

    Result<SkillDirectoryLoad> result = await _source.ListAsync(_root, TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Empty(result.Value.Skills);
    Assert.Empty(result.Value.Diagnostics);
  }

  [Fact]
  public async Task List_LowercaseSkillMd_NotTreatedAsSkill()
  {
    string folder = Path.Combine(_root, "wrongcase");
    _ = Directory.CreateDirectory(folder);
    File.WriteAllText(Path.Combine(folder, "skill.md"), "---\nname: wrong\ndescription: wrong case\n---\nbody");

    Result<SkillDirectoryLoad> result = await _source.ListAsync(_root, TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Empty(result.Value.Skills);
    Assert.Equal("no SKILL.md in wrongcase, skipped", Assert.Single(result.Value.Diagnostics));
  }

  [Fact]
  public async Task List_MetadataVersionAndManual_FlowIntoDefinition()
  {
    _ = WriteSkillFolder("configured",
        "---\nname: configured\ndescription: Configured skill.\nmetadata:\n  version: 7\ndisable-model-invocation: true\n---\nBody.");

    Result<SkillDirectoryLoad> result = await _source.ListAsync(_root, TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    SkillDefinition skill = Assert.Single(result.Value.Skills);
    Assert.Equal(7, skill.Version);
    Assert.True(skill.Manual);
  }

  private string WriteSkillFolder(string folderName, string text)
  {
    string folder = Path.Combine(_root, folderName);
    _ = Directory.CreateDirectory(folder);
    File.WriteAllText(Path.Combine(folder, SkillFileName), text);
    return folder;
  }
}
