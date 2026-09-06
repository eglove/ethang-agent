using eThangAgent.AgentDomain;

namespace eThangAgent.Composition.Tests;

/// <summary>D (spawn-time workspace anchor): the supervisor serializes the WHOLE
///     AgentSettings into the host's settings JSON - so once the record carries the
///     workspace root, the host receives it without a second carrier. This pins
///     that contract: a root overlaid on the settings reaches childhost-settings.json.</summary>
public class RemoteHostSupervisorSettingsTests
{
  [Fact]
  public async Task Supervisor_WritesTheWorkspaceRootIntoTheHostSettingsJson()
  {
    string scratch = Directory.CreateTempSubdirectory("ethang-sup-ws").FullName;
    try
    {
      string root = Directory.CreateTempSubdirectory("ethang-sup-root").FullName;
      AgentSettings settings = new AgentSettings(
          new OpenRouterSettings("sk-test", new Uri("https://openrouter.test")),
          new ZaiSettings(null, new Uri("https://zai.test")),
          new SubAgentOptions(null, 2)).WithWorkspaceRoot(root);
      RemoteHostSupervisor supervisor = new("ws-id", scratch,
          settings, Path.Combine(scratch, "app.db"), _ => { });
      try
      {
        string json = await File.ReadAllTextAsync(Path.Combine(scratch, "childhost-settings.json"),
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Contains("WorkspaceRoot", json, StringComparison.Ordinal);
        Assert.Contains(root.Replace("\\", "\\\\", StringComparison.Ordinal), json, StringComparison.Ordinal);
      }
      finally
      {
        await supervisor.DisposeAsync().ConfigureAwait(true);
      }
    }
    finally
    {
      Directory.Delete(scratch, recursive: true);
    }
  }
}
