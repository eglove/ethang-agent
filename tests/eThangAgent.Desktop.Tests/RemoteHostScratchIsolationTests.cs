using eThangAgent.Composition;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;

namespace eThangAgent.Desktop.Tests;

/// <summary>D5 (scratch isolation): the remote supervisor's settings JSON and scratch
///     live OUTSIDE the user's workspace. A Path.Combine(temp-root, workspaceId) where
///     workspaceId is itself an ABSOLUTE path silently discards the temp root and
///     dropped childhost-settings.json INTO the user's project directory (observed:
///     every remote-mode session polluted the workspace). The anchor test proves the
///     feature; this proves the file never lands where the user works.</summary>
[Collection("Desktop E2E")]
public class RemoteHostScratchIsolationTests
{
  [Fact]
  public async Task RemoteSessionOpen_NeverWritesHostSettingsIntoTheWorkspace()
  {
    using E2E.HostHarness host = new();
    _ = await host.StartAsync();
    string ws = Directory.CreateTempSubdirectory("ethang-scratch-iso").FullName;
    try
    {
      AgentSessionFactory factory = new(
          host.BuildSettings(),
          new AppDatabase(host.DatabasePath));
      Result<AgentSession> opened = await factory.CreateAsync(
          ws, Providers.OpenRouter,
          ct: TestContext.Current.CancellationToken);
      Assert.True(opened.IsSuccess, opened.Error?.Message);
      try
      {
        _ = opened.Value;
      }
      finally
      {
        await opened.Value.Services.DisposeAsync();
      }

      Assert.False(File.Exists(Path.Combine(ws, "childhost-settings.json")),
          "the host settings JSON leaked into the workspace");

      // The workspace is EMPTY: the harness adds no artifacts to the user's machine outside
      // its database, so neither its scratch (host settings, logs) nor an identity marker lands
      // where the user works.
      string[] unexpected = [.. Directory
          .EnumerateFiles(ws, "*", SearchOption.AllDirectories)
          .Select(path => Path.GetRelativePath(ws, path).Replace('\\', '/'))];
      Assert.Empty(unexpected);
    }
    finally
    {
      try
      {
        Directory.Delete(ws, recursive: true);
      }
      catch (IOException)
      {
        // best effort: the workspace may be pinned by the host
      }
    }
  }
}
