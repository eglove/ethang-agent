using System.Text.Json;
using eThangAgent.CapabilityDomain;
using eThangAgent.Composition;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using eThangAgent.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop.Tests;

/// <summary>End-to-end proof of the trusted-directory wiring (issue #96) over the
/// PRODUCTION open path: an approval written to the workspace preference key reaches a
/// real session container opened through <see cref="AgentSessionFactory"/> - the same
/// path the shell's New Agent entry uses - and the ordinary tools reach the external root. The
/// inverse pins the no-approval behavior: nothing configured means the container
/// behaves exactly as it did before the capability existed.</summary>
[Collection("Desktop E2E")]
[Trait("Category", "Process")]
public sealed class TrustedDirectoryE2ETests
{
  [Fact]
  public async Task Session_WithApprovedRoot_ReadsItThroughTheReadTool()
  {
    DirectoryInfo ws = TestTempRoot.CreateDirectory("ethang-e2e-trusted-ws");
    DirectoryInfo vault = TestTempRoot.CreateDirectory("ethang-e2e-trusted-vault");
    try
    {
      string file = Path.Combine(vault.FullName, "notes.md");
      await File.WriteAllTextAsync(file, "trusted marker",
          TestContext.Current.CancellationToken);

#pragma warning disable CA2007 // 'await using' cannot carry ConfigureAwait.
      await using E2E.HostHarness host = new();
#pragma warning restore CA2007
      host.ClaimHostsForCurrentTest();
      _ = await host.StartAsync(workspaceRoot: ws.FullName);

      // Configure the approval exactly the way the workspace settings save persists it: the
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

  /// <summary>The workspace settings SAVE is the path the approval now travels: a save that
  ///     approves the vault reaches a session the factory builds afterwards, so the window's
  ///     confirmation is what an agent's reach is made of.</summary>
  [Fact]
  public async Task WorkspaceSettingsSave_ApprovingARoot_ReachesTheNextSession()
  {
    DirectoryInfo ws = TestTempRoot.CreateDirectory("ethang-e2e-trusted-save-ws");
    DirectoryInfo vault = TestTempRoot.CreateDirectory("ethang-e2e-trusted-save-vault");
    try
    {
      string file = Path.Combine(vault.FullName, "notes.md");
      await File.WriteAllTextAsync(file, "saved marker",
          TestContext.Current.CancellationToken);

#pragma warning disable CA2007 // 'await using' cannot carry ConfigureAwait.
      await using E2E.HostHarness host = new();
#pragma warning restore CA2007
      host.ClaimHostsForCurrentTest();
      _ = await host.StartAsync(workspaceRoot: ws.FullName);

      MainViewModel shell = SettingsShell(host.Store, ws.FullName);
      Result<bool> saved = await shell.ApplyWorkspaceSettingsAsync(new WorkspaceSettingsSave(
          ws.FullName, [], [], [new TrustedDirectoryEntry(vault.FullName, Enabled: true, Editable: false)]));
      Assert.True(saved.IsSuccess);

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
      Assert.Contains("saved marker", read.Content, StringComparison.Ordinal);
      await session.Services.DisposeAsync();
    }
    finally
    {
      ws.Delete(true);
      vault.Delete(true);
    }
  }

  /// <summary>An empty trusted-directory list DELETES the workspace key, so the next session
  ///     in that workspace reaches no external root at all.</summary>
  [Fact]
  public async Task WorkspaceSettingsSave_EmptyList_DeletesTheKey_AndTheNextSessionReachesNothing()
  {
    DirectoryInfo ws = TestTempRoot.CreateDirectory("ethang-e2e-trusted-clear-ws");
    DirectoryInfo vault = TestTempRoot.CreateDirectory("ethang-e2e-trusted-clear-vault");
    try
    {
      string file = Path.Combine(vault.FullName, "notes.md");
      await File.WriteAllTextAsync(file, "cleared marker",
          TestContext.Current.CancellationToken);

#pragma warning disable CA2007 // 'await using' cannot carry ConfigureAwait.
      await using E2E.HostHarness host = new();
#pragma warning restore CA2007
      host.ClaimHostsForCurrentTest();
      _ = await host.StartAsync(workspaceRoot: ws.FullName);

      string key = TrustedDirectoryPreferences.WorkspaceKey(ws.FullName);
      _ = await host.Store.SetAsync(key,
          TrustedDirectoryPreferences.Serialize(
              [new TrustedDirectoryEntry(vault.FullName, Enabled: true, Editable: false)]),
          TestContext.Current.CancellationToken);

      MainViewModel shell = SettingsShell(host.Store, ws.FullName);
      Result<bool> saved = await shell.ApplyWorkspaceSettingsAsync(new WorkspaceSettingsSave(
          ws.FullName, [], [], []));
      Assert.True(saved.IsSuccess);
      Assert.Null(await host.Store.GetAsync(key, TestContext.Current.CancellationToken));

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
      Assert.DoesNotContain("Trusted directories", session.SystemPrompt, StringComparison.Ordinal);
      await session.Services.DisposeAsync();
    }
    finally
    {
      ws.Delete(true);
      vault.Delete(true);
    }
  }

  /// <summary>A shell bound to the workspace, over the host's own preference store - the same
  ///     store the factory reads. Its session source is never invoked: these tests persist
  ///     settings and then open through the factory directly.</summary>
  private static MainViewModel SettingsShell(IAppPreferenceStore preferences, string workspaceRoot)
      => new((_, _) => Task.FromResult(Result.Failure<AgentSession>(
              new DomainError("NotUsed", "this shell only persists settings."))),
          new MainViewModelOptions { Preferences = preferences, WorkspaceRoot = workspaceRoot });

  [Fact]
  public async Task Session_WithoutApproval_RefusesTheSamePathAndDoesNotNameIt()
  {
    DirectoryInfo ws = TestTempRoot.CreateDirectory("ethang-e2e-trusted-none");
    DirectoryInfo vault = TestTempRoot.CreateDirectory("ethang-e2e-trusted-unapproved");
    try
    {
      string file = Path.Combine(vault.FullName, "notes.md");
      await File.WriteAllTextAsync(file, "unapproved marker",
          TestContext.Current.CancellationToken);

#pragma warning disable CA2007 // 'await using' cannot carry ConfigureAwait.
      await using E2E.HostHarness host = new();
#pragma warning restore CA2007
      host.ClaimHostsForCurrentTest();
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
