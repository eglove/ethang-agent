using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

// Best-effort temp-file cleanup in finally blocks is deliberate (CA1031).
#pragma warning disable CA1031, S108 // Do not catch general exception types

namespace eThangAgent.Composition.Tests;

/// <summary>The DEPLOYED app: it lives outside any repo, so the conversation host must be
///     the copy shipped NEXT TO THE APP. The 2026-10-21 crash: the published app walked
///     past the filesystem root looking for a repo, found none, and dereferenced the
///     null — a NullReferenceException that killed the whole app the moment a workspace
///     was opened. Two contracts are pinned here: the lookup resolves without faulting,
///     and a host that cannot start at all is a NAMED failure the shell can surface,
///     never an escaping exception.</summary>
[Collection("EnvironmentSensitive")]
public class DeployedHostResolutionTests
{
  /// <summary>The host exe name, spelled out here so the test pins the FILE the app
  ///     looks for - a rename on the app side must break this test, not the app.</summary>
  private const string HostExeName = "eThangAgent.ChildHost.exe";

  [Fact]
  public void DefaultHostExePath_Outside_A_Repo_Resolves_The_Host_Next_To_The_App()
  {
    // The published layout: the app's own directory, with no repo anywhere above it.
    string appDir = Directory.CreateTempSubdirectory("ethang-published-app").FullName;
    try
    {
      string fakeAssembly = Path.Combine(appDir, "eThangAgent.Composition.dll");

      string resolved = RemoteHostSupervisor.ResolveDefaultHostExePath(fakeAssembly, appDir);

      // The host that ships with the app - never a crash, never a repo path.
      Assert.Equal(Path.Combine(appDir, HostExeName), resolved);
    }
    finally
    {
      Directory.Delete(appDir, recursive: true);
    }
  }

  [Fact]
  public void DefaultHostExePath_Inside_A_Repo_Prefers_The_Repos_Build_Output()
  {
    // The dev layout keeps working for a caller that ships no host of its own: the
    // repo's build output is the fallback.
    string repo = Directory.CreateTempSubdirectory("ethang-repo-app").FullName;
    try
    {
      File.WriteAllText(Path.Combine(repo, "eThangAgent.slnx"), string.Empty);
      string hostDir = Path.Combine(repo, "src", "eThangAgent.ChildHost", "bin", "Debug", "net10.0-windows");
      _ = Directory.CreateDirectory(hostDir);
      string host = Path.Combine(hostDir, HostExeName);
      File.WriteAllText(host, string.Empty);

      string resolved = RemoteHostSupervisor.ResolveDefaultHostExePath(
          Path.Combine(repo, "bin", "Debug", "eThangAgent.Composition.dll"), repo);

      Assert.Equal(host, resolved);
    }
    finally
    {
      Directory.Delete(repo, recursive: true);
    }
  }

  [Fact]
  public void DefaultHostExePath_Inside_A_Repo_Prefers_The_Host_Shipped_With_The_App()
  {
    // The app and its host are ONE build: the copy shipped next to the app always
    // wins over the repo's output, which may hold a DIFFERENT (stale) configuration
    // of the host. A cross-build pair fails the wire protocol in ways that read as
    // product bugs - the 2026-10-22 case answered every root turn with "this
    // conversation host is not bound to a root session".
    string repo = Directory.CreateTempSubdirectory("ethang-repo-app").FullName;
    try
    {
      File.WriteAllText(Path.Combine(repo, "eThangAgent.slnx"), string.Empty);
      string hostDir = Path.Combine(repo, "src", "eThangAgent.ChildHost", "bin", "Debug", "net10.0-windows");
      _ = Directory.CreateDirectory(hostDir);
      File.WriteAllText(Path.Combine(hostDir, HostExeName), string.Empty);
      string appDir = Directory.CreateTempSubdirectory("ethang-repo-app-bin").FullName;
      try
      {
        string shipped = Path.Combine(appDir, HostExeName);
        File.WriteAllText(shipped, string.Empty);

        string resolved = RemoteHostSupervisor.ResolveDefaultHostExePath(
            Path.Combine(appDir, "eThangAgent.Composition.dll"), appDir);

        Assert.Equal(shipped, resolved);
      }
      finally
      {
        Directory.Delete(appDir, recursive: true);
      }
    }
    finally
    {
      Directory.Delete(repo, recursive: true);
    }
  }

  [Fact]
  public async Task CreateAsync_With_A_Host_That_Cannot_Start_Fails_Named()
  {
    // The host executable does not exist: the open must fail with a NAMED error the
    // shell surfaces in a dialog, never an exception that tears the process down
    // (the open path runs on the UI dispatcher, which has no handler for one).
    string dbPath = Path.Combine(Path.GetTempPath(), $"ethang-host-{Guid.NewGuid():N}.db");
    string missingHost = Path.Combine(
        Path.GetTempPath(), $"ethang-no-host-{Guid.NewGuid():N}", HostExeName);
    DirectoryInfo dir = Directory.CreateTempSubdirectory("ethang-ws-host");
    Environment.SetEnvironmentVariable("ETHANG_AGENT_DB", dbPath);
    try
    {
      AgentSessionFactory factory = new(
          new AgentSettings(
              new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
              new SubAgentOptions(null, 2)),
          hostExePath: () => missingHost);

      Result<AgentSession> result = await factory
          .CreateAsync(dir.FullName, Providers.OpenRouter, ct: TestContext.Current.CancellationToken)
          .ConfigureAwait(true);

      Assert.False(result.IsSuccess);
      Assert.Equal("HostUnavailable", result.Error.Code);
      // The message names WHERE the host was expected: the operator needs to know
      // which path to fix, not just that something went wrong.
      Assert.Contains(missingHost, result.Error.Message, StringComparison.Ordinal);
      // The factory disposes the container on this failure path; the explicit call
      // is the belt-and-braces that keeps a session from ever holding a host.
      await TestSession.DisposeAsync(result);
    }
    finally
    {
      Environment.SetEnvironmentVariable("ETHANG_AGENT_DB", null);
      dir.Delete(true);
      try
      {
        File.Delete(dbPath);
      }
      catch
      {
      }
    }
  }
}
