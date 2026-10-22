using System.Diagnostics;

namespace eThangAgent.Composition.Tests;

/// <summary>The host process's WORKING DIRECTORY is its own SCRATCH folder under the
///     temp root - NEVER the session workspace. Two facts make this exact:
///     (1) the host inherited the APP's launch directory, so a relative path inside an
///     exec script resolved there instead of at the session's own workspace
///     (2026-10-22: a personal-vault session wrote p1.txt and doc161.pdf into the
///     ethang-agent repo, because the app had been launched from that repo); and
///     (2) a child process whose working directory sits INSIDE the workspace PINS that
///     directory, so deletes over it fail while the child lives - the documented git
///     -C hazard. The scratch folder is outside the workspace, so it fixes (1) without
///     causing (2).</summary>
public class RemoteHostWorkingDirectoryTests
{
  [Fact]
  public void StartInfo_WorkingDirectory_IsTheHostScratchFolder()
  {
    string scratch = Directory.CreateTempSubdirectory("ethang-host-wd").FullName;
    try
    {
      ProcessStartInfo psi = RemoteHostSupervisor.BuildStartInfo(
          "host.exe", "pipe", "settings.json", "app.db", scratch);

      Assert.Equal(scratch, psi.WorkingDirectory, StringComparer.OrdinalIgnoreCase);
    }
    finally
    {
      Directory.Delete(scratch, recursive: true);
    }
  }

  [Fact]
  public void StartInfo_MissingWorkingDirectory_LeavesItUnset()
  {
    // A folder that no longer exists must NOT fail the spawn: the working directory
    // is a convenience, not a requirement.
    string missing = Path.Combine(Path.GetTempPath(), "ethang-host-wd-missing-" + Guid.NewGuid().ToString("N"));

    ProcessStartInfo psi = RemoteHostSupervisor.BuildStartInfo(
        "host.exe", "pipe", "settings.json", "app.db", missing);

    Assert.True(string.IsNullOrEmpty(psi.WorkingDirectory));
  }

  [Fact]
  public void StartInfo_CarriesTheHostArguments()
  {
    ProcessStartInfo psi = RemoteHostSupervisor.BuildStartInfo(
        "host.exe", "pipe-1", "settings.json", "app.db", AppContext.BaseDirectory);

    Assert.Equal("host.exe", psi.FileName, StringComparer.Ordinal);
    Assert.Equal(3, psi.ArgumentList.Count);
    Assert.Equal("pipe-1", psi.ArgumentList[0], StringComparer.Ordinal);
    Assert.Equal("settings.json", psi.ArgumentList[1], StringComparer.Ordinal);
    Assert.Equal("app.db", psi.ArgumentList[2], StringComparer.Ordinal);
  }
}
