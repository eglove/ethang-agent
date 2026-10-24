using System.Text.Json;
using eThangAgent.CapabilityDomain;
using eThangAgent.Composition;
using eThangAgent.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop.Tests;

/// <summary>End-to-end proof of the trusted-directory wiring (issue #96) over the
/// PRODUCTION open path: an approval written to the workspace preference key reaches a
/// real session container opened through <see cref="AgentSessionFactory"/> - the same
/// path the launch dialog uses - and the ordinary tools reach the external root. The
/// inverse pins the no-approval behavior: nothing configured means the container
/// behaves exactly as it did before the capability existed.</summary>
[Collection("Desktop E2E")]
public sealed class TrustedDirectoryE2ETests
{
  [Fact]
  public async Task Session_WithApprovedRoot_ReadsItThroughTheReadTool()
  {
    DirectoryInfo ws = Directory.CreateTempSubdirectory("ethang-e2e-trusted-ws");
    DirectoryInfo vault = Directory.CreateTempSubdirectory("ethang-e2e-trusted-vault");
    try
    {
      string file = Path.Combine(vault.FullName, "notes.md");
      await File.WriteAllTextAsync(file, "trusted marker",
          TestContext.Current.CancellationToken);

      using E2E.HostHarness host = new();
      _ = await host.StartAsync(workspaceRoot: ws.FullName);

      // Configure the approval exactly the way the launch dialog persists it: the
      // workspace preference key in the SAME app database the factory reads on every
      // open. Read-only here - reading is what the approval is for.
      _ = await host.Store.SetAsync(
          TrustedDirectoryPreferences.WorkspaceKey(ws.FullName),
          TrustedDirectoryPreferences.Serialize(
              [new TrustedDirectoryEntry(vault.FullName, Enabled: true, Editable: false)]),
          TestContext.Current.CancellationToken);

      AgentSessionFactory factory = host.CreateResumeFactory();
      Result<AgentSession> opened = await factory.CreateAsync(
          ws.FullName, Providers.OpenRouter, ct: TestContext.Current.CancellationToken);
      Assert.True(opened.IsSuccess);
      AgentSession session = opened.Value;

      AgentToolsProvider tools = session.Services.GetRequiredService<AgentToolsProvider>();
      CapabilityInvocationResult read = await tools.InvokeAsync("read",
          JsonSerializer.Serialize(new { timeoutSeconds = 60, path = file, startLine = 1, endLine = 5 }),
          TestContext.Current.CancellationToken);

      Assert.False(read.IsError);
      Assert.Contains("trusted marker", read.Content, StringComparison.Ordinal);

      // The model is TOLD about the approval in the composed prompt: an approval it
      // cannot see is one it will never use, which is the whole defect #96 names.
      Assert.Contains(vault.FullName, session.SystemPrompt, StringComparison.Ordinal);

      await session.Services.DisposeAsync();
    }
    finally
    {
      ws.Delete(true);
      vault.Delete(true);
    }
  }

  [Fact]
  public async Task Session_WithoutApproval_RefusesTheSamePathAndDoesNotNameIt()
  {
    DirectoryInfo ws = Directory.CreateTempSubdirectory("ethang-e2e-trusted-none");
    DirectoryInfo vault = Directory.CreateTempSubdirectory("ethang-e2e-trusted-unapproved");
    try
    {
      string file = Path.Combine(vault.FullName, "notes.md");
      await File.WriteAllTextAsync(file, "unapproved marker",
          TestContext.Current.CancellationToken);

      using E2E.HostHarness host = new();
      _ = await host.StartAsync(workspaceRoot: ws.FullName);

      AgentSessionFactory factory = host.CreateResumeFactory();
      Result<AgentSession> opened = await factory.CreateAsync(
          ws.FullName, Providers.OpenRouter, ct: TestContext.Current.CancellationToken);
      Assert.True(opened.IsSuccess);
      AgentSession session = opened.Value;

      AgentToolsProvider tools = session.Services.GetRequiredService<AgentToolsProvider>();
      CapabilityInvocationResult write = await tools.InvokeAsync("write",
          JsonSerializer.Serialize(new { timeoutSeconds = 60, path = file, content = "no", overwrite = true }),
          TestContext.Current.CancellationToken);

      Assert.True(write.IsError);
      Assert.Contains("PathOutsideWorkspace", write.Content, StringComparison.Ordinal);
      // Byte-identical prompt: nothing approved renders nothing, so a session that
      // approves nothing reads exactly as it did before the capability existed.
      Assert.DoesNotContain("Trusted directories", session.SystemPrompt, StringComparison.Ordinal);

      await session.Services.DisposeAsync();
    }
    finally
    {
      ws.Delete(true);
      vault.Delete(true);
    }
  }
}
