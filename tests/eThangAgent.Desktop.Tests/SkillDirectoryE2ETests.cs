using eThangAgent.Composition;
using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop.Tests;

/// <summary>End-to-end proof of the composite skill catalog wiring (skill-routing
///     Phase 1, Task 7): a skill directory configured through the durable workspace
///     preference key reaches a REAL session container — opened through the
///     production session factory over the SAME app database — and the resolved
///     ISkillCatalog lists the file skill beside the built-ins. Assertions use the
///     container-resolution form (no model turn runs): the composed catalog is the
///     exact seam the skill tools and the prompt provider consume. The composed
///     SystemPrompt must NOT carry the file skill — directory listing injection is
///     Task 11, the phase boundary this task must not cross. With no directories
///     configured the composite is behavior-identical: built-ins only.</summary>
[Collection("Desktop E2E")]
public class SkillDirectoryE2ETests
{
  private const string FileSkillName = "e2e-file-skill";

  [Fact]
  public async Task Session_WithConfiguredSkillDirectory_ListsFileSkillFromCompositeCatalog()
  {
    DirectoryInfo ws = Directory.CreateTempSubdirectory("ethang-e2e-skilldir-ws");
    DirectoryInfo skills = Directory.CreateTempSubdirectory("ethang-e2e-skilldir");
    try
    {
      // ONE valid file skill (agentskills.io shape: name/description frontmatter only)
      // inside its own folder of the configured directory.
      string skillFolder = Path.Combine(skills.FullName, FileSkillName);
      _ = Directory.CreateDirectory(skillFolder);
      string[] skillLines =
      [
        "---",
        $"name: {FileSkillName}",
        "description: End-to-end file skill for the composite catalog wiring test.",
        "---",
        "",
        $"Body of the {FileSkillName} file skill.",
        "",
      ];
      await File.WriteAllLinesAsync(Path.Combine(skillFolder, "SKILL.md"), skillLines,
        TestContext.Current.CancellationToken);

      using E2E.HostHarness host = new();
      _ = await host.StartAsync(workspaceRoot: ws.FullName);

      // Configure the directory exactly the way the settings surface persists it:
      // the workspace preference key in the SAME app database the session factory
      // reads on every open.
      _ = await host.Store.SetAsync(
        SkillDirectoryPreferences.WorkspaceKey(ws.FullName),
        SkillDirectoryPreferences.Serialize([new SessionFileEntry(skills.FullName, Enabled: true)]),
        TestContext.Current.CancellationToken);

      // The REAL production open path: the factory reads the stored lists, resolves
      // the directory, and builds the container that wires the composite catalog.
      AgentSessionFactory factory = host.CreateResumeFactory();
      Result<AgentSession> opened = await factory.CreateAsync(
        ws.FullName, Providers.OpenRouter, TestContext.Current.CancellationToken);
      Assert.True(opened.IsSuccess);
      AgentSession session = opened.Value;

      ISkillCatalog catalog = session.Services.GetRequiredService<ISkillCatalog>();
      Result<IReadOnlyList<SkillDefinition>> listed =
        await catalog.ListAsync(TestContext.Current.CancellationToken);
      Assert.True(listed.IsSuccess);

      // The file skill lists beside the built-ins, carrying its File source.
      SkillDefinition fileSkill = Assert.Single(listed.Value, s => s.Name == FileSkillName);
      Assert.Equal(SkillSource.File, fileSkill.Source);
      Assert.Contains(listed.Value, s => s.Name == "using-skills" && s.Source == SkillSource.BuiltIn);

      // Phase boundary: the composed SystemPrompt must NOT carry the file skill —
      // directory listing injection is Task 11.
      Assert.DoesNotContain(FileSkillName, session.SystemPrompt, StringComparison.Ordinal);

      await session.Services.DisposeAsync();
    }
    finally
    {
      ws.Delete(true);
      skills.Delete(true);
    }
  }

  [Fact]
  public async Task Session_WithoutConfiguredDirectories_ListsBuiltInsOnly()
  {
    DirectoryInfo ws = Directory.CreateTempSubdirectory("ethang-e2e-skilldir-empty");
    try
    {
      using E2E.HostHarness host = new();
      _ = await host.StartAsync(workspaceRoot: ws.FullName);

      // No skill-directory preference written anywhere: the composite must be
      // behavior-identical — built-ins only, no file skills.
      AgentSessionFactory factory = host.CreateResumeFactory();
      Result<AgentSession> opened = await factory.CreateAsync(
        ws.FullName, Providers.OpenRouter, TestContext.Current.CancellationToken);
      Assert.True(opened.IsSuccess);
      AgentSession session = opened.Value;

      ISkillCatalog catalog = session.Services.GetRequiredService<ISkillCatalog>();
      Result<IReadOnlyList<SkillDefinition>> listed =
        await catalog.ListAsync(TestContext.Current.CancellationToken);
      Assert.True(listed.IsSuccess);
      Assert.NotEmpty(listed.Value);
      Assert.All(listed.Value, s => Assert.Equal(SkillSource.BuiltIn, s.Source));
      Assert.DoesNotContain(listed.Value, s => s.Name == FileSkillName);
      Assert.DoesNotContain(FileSkillName, session.SystemPrompt, StringComparison.Ordinal);

      await session.Services.DisposeAsync();
    }
    finally
    {
      ws.Delete(true);
    }
  }
}
