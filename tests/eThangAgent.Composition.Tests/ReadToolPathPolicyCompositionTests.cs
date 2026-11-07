using eThangAgent.AgentDomain;
using eThangAgent.CapabilityDomain;
using eThangAgent.ModelDomain;
using eThangAgent.TestSupport;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

/// <summary>The read tool's path policy at the composition level: a real
/// session's read accepts an absolute path OUTSIDE the workspace (read is
/// read-only) while its relative paths stay bound to the workspace root.
/// Mutating tools keep strict containment — write still refuses the same
/// absolute path.</summary>
[Trait("Category", "Process")]
public sealed class ReadToolPathPolicyCompositionTests
{
  [Fact]
  public async Task Read_Accepts_AbsolutePath_OutsideWorkspace()
  {
    string outside = Path.Combine(TestTempRoot.Root, $"ethang-read-out-{Guid.NewGuid():N}.txt");
    await File.WriteAllTextAsync(outside, "outside marker", TestContext.Current.CancellationToken);
    try
    {
      using TestAppDatabase db = TestAppDatabase.Create();
      using ServiceProvider services = new ServiceCollection()
          .AddEThangAgentCore(Settings(), Providers.OpenRouter,
              ModelConfig.Create("m", 512, 0.5f, 8192).Value!,
              new AgentHostOptions(
                  FixedWorkspaceContext.ForPath(TestTempRoot.Root), new WorkspacePathResolver(TestTempRoot.Root)),
              db.Database)
          .BuildServiceProvider();

      AgentToolsProvider tools = services.GetRequiredService<AgentToolsProvider>();
      string args = System.Text.Json.JsonSerializer.Serialize(
          new { timeoutSeconds = 60, path = outside, startLine = 1, endLine = 5 });
      CapabilityInvocationResult result = await tools.InvokeAsync("read", args, TestContext.Current.CancellationToken);

      Assert.False(result.IsError);
      Assert.Contains("outside marker", result.Content, StringComparison.Ordinal);
    }
    finally
    {
      try
      {
        File.Delete(outside);
      }
#pragma warning disable CA1031 // Best-effort cleanup of the temp file.
#pragma warning disable S108 // Empty catch block is the best-effort cleanup.
      catch
      {
      }
#pragma warning restore S108
#pragma warning restore CA1031
    }
  }

  [Fact]
  public async Task Write_Still_Rejects_AbsolutePath_OutsideWorkspace()
  {
    using TestAppDatabase db = TestAppDatabase.Create();
    using ServiceProvider services = new ServiceCollection()
        .AddEThangAgentCore(Settings(), Providers.OpenRouter,
            ModelConfig.Create("m", 512, 0.5f, 8192).Value!,
            new AgentHostOptions(
                FixedWorkspaceContext.ForPath(TestTempRoot.Root), new WorkspacePathResolver(TestTempRoot.Root)),
            db.Database)
        .BuildServiceProvider();

    AgentToolsProvider tools = services.GetRequiredService<AgentToolsProvider>();
    string args = System.Text.Json.JsonSerializer.Serialize(
        new { timeoutSeconds = 60, path = "C:\\definitely-not-the-workspace\\x.txt", content = "no" });
    CapabilityInvocationResult result = await tools.InvokeAsync("write", args, TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.Contains("PathOutsideWorkspace", result.Content, StringComparison.Ordinal);
  }

  private static AgentSettings Settings() => new(
      new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
      new AgentRunOptions(null, 2));
}
