using eThangAgent.ChildHost;
using eThangAgent.ModelDomain;
using eThangAgent.StateDomain;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Transport.ACL.Tests;

/// <summary>D (spawn-time workspace anchor): the settings JSON the app writes
///     carries the workspace root; SessionHost anchors its container there instead
///     of the settings-file directory. Absent root = legacy fallback (scratch dir),
///     named for backward compatibility; a relative root is a named startup error
///     (W1.2 strictness), never a silent resolution against a random cwd.</summary>
public class SessionHostWorkspaceAnchorTests
{
  private static string WriteSettings(string json)
  {
    string path = Path.Combine(Path.GetTempPath(), "ethang-anchor-" + Guid.NewGuid().ToString("N") + ".json");
    File.WriteAllText(path, json);
    return path;
  }

  private static string SettingsJson(string extra)
    => "{\"OpenRouter\":{\"ApiKey\":\"sk-test\",\"BaseUrl\":\"http://openrouter.test\"},\"Zai\":{\"ApiKey\":null,\"BaseUrl\":\"http://zai.test\"},\"SubAgents\":{\"MaxConcurrentAgents\":1}" + extra + "}";

  [Fact]
  public void SettingsJson_WithWorkspaceRoot_AnchorsTheContainerThere()
  {
    string root = Directory.CreateTempSubdirectory("ethang-anchor-ws").FullName;
    string path = WriteSettings(SettingsJson(",\"WorkspaceRoot\":\"" + root.Replace("\\", "\\\\", StringComparison.Ordinal) + "\""));
    try
    {
      SessionHost host = SessionHost.Create(path, Path.Combine(Path.GetTempPath(), "ethang-anchor-" + Guid.NewGuid().ToString("N") + ".db"));
      using IServiceScope scope = host.Services.CreateScope();
      Assert.Equal(root, scope.ServiceProvider.GetRequiredService<IWorkspaceContext>().WorkspaceId);
      string resolved = scope.ServiceProvider.GetRequiredService<IPathResolver>().Resolve("AGENTS.md").Value!;
      Assert.Equal(root, resolved[..root.Length], ignoreCase: true);
    }
    finally
    {
      File.Delete(path);
      Directory.Delete(root);
    }
  }

  [Fact]
  public void SettingsJson_WithRelativeWorkspaceRoot_IsANamedStartupError()
  {
    string path = WriteSettings(SettingsJson(",\"WorkspaceRoot\":\"relative/ws\""));
    try
    {
      InvalidOperationException error = Assert.Throws<InvalidOperationException>(
          () => SessionHost.Create(path, Path.Combine(Path.GetTempPath(), "x.db")));
      Assert.Contains("WorkspaceRoot must be an absolute path", error.Message, StringComparison.Ordinal);
    }
    finally
    {
      File.Delete(path);
    }
  }

  [Fact]
  public void SettingsJson_WithoutWorkspaceRoot_FallsBackToTheSettingsDirectory()
  {
    string path = WriteSettings(SettingsJson(""));
    try
    {
      SessionHost host = SessionHost.Create(path, Path.Combine(Path.GetTempPath(), "ethang-anchor-" + Guid.NewGuid().ToString("N") + ".db"));
      using IServiceScope scope = host.Services.CreateScope();
      Assert.Equal(Path.GetDirectoryName(path), scope.ServiceProvider.GetRequiredService<IWorkspaceContext>().WorkspaceId);
    }
    finally
    {
      File.Delete(path);
    }
  }

  [Fact]
  public void SettingsJson_WithWorkspaceRoot_InjectsWorkspaceDocsIntoTheSystemPrompt()
  {
    string root = Directory.CreateTempSubdirectory("ethang-anchor-ws2").FullName;
    File.WriteAllText(Path.Combine(root, "AGENTS.md"), "# anchor prompt parity probe");
    string escaped = root.Replace("\\", "\\\\", StringComparison.Ordinal);
    string path = WriteSettings(SettingsJson(",\"WorkspaceRoot\":\"" + escaped + "\""));
    try
    {
      SessionHost host = SessionHost.Create(path, Path.Combine(Path.GetTempPath(), "ethang-anchor-" + Guid.NewGuid().ToString("N") + ".db"));
      ISystemPromptProvider prompt =
          host.Services.GetRequiredService<ISystemPromptProvider>();
      Assert.Contains("# anchor prompt parity probe", prompt.Build(), StringComparison.Ordinal);
    }
    finally
    {
      File.Delete(path);
      Directory.Delete(root, recursive: true);
    }
  }
}
