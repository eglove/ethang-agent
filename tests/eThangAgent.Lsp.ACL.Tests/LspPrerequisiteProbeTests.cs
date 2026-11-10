using eThangAgent.Lsp.ACL;
using eThangAgent.TestSupport;

namespace eThangAgent.Lsp.ACL.Tests;

public class LspPrerequisiteProbeTests
{
  [Fact]
  public void Probe_Found_ReportsPresentWithTheAbsolutePath()
  {
    LspPrerequisiteProbe probe = new(_ => PrerequisiteProbeResult.Found("C:\\nodejs\\node.exe"));
    PrerequisiteProbeResult result = probe.Probe("node");
    Assert.Equal(PrerequisiteStatus.Present, result.Status);
    Assert.Equal("C:\\nodejs\\node.exe", result.ExecutablePath);
  }

  [Fact]
  public void Probe_Missing_ReportsAbsentWithNoPath()
  {
    LspPrerequisiteProbe probe = new(_ => PrerequisiteProbeResult.Missing);
    PrerequisiteProbeResult result = probe.Probe("go");
    Assert.Equal(PrerequisiteStatus.Absent, result.Status);
    Assert.Null(result.ExecutablePath);
  }

  [Fact]
  public void Probe_Unsearched_ReportsUnknown()
  {
    LspPrerequisiteProbe probe = new(_ => PrerequisiteProbeResult.Unsearched);
    Assert.Equal(PrerequisiteStatus.Unknown, probe.Probe("gem").Status);
  }

  [Fact]
  public void Probe_NoExecutableNamed_ReportsUnknownWithoutSearching()
  {
    int searches = 0;
    LspPrerequisiteProbe probe = new(_ => { searches++; return PrerequisiteProbeResult.Missing; });
    Assert.Equal(PrerequisiteStatus.Unknown, probe.Probe("  ").Status);
    Assert.Equal(0, searches);
  }

  [Fact]
  public void DefaultProbe_FindsAnExecutableThatIsOnDisk()
  {
    DirectoryInfo directory = TestTempRoot.CreateDirectory("lspprobe");
    try
    {
      string executable = Path.Combine(directory.FullName, "tool.exe");
      File.WriteAllText(executable, string.Empty);
      PrerequisiteProbeResult result = LspPrerequisiteProbe.CreateDefault()
          .Probe(Path.Combine(directory.FullName, "tool"));
      Assert.Equal(PrerequisiteStatus.Present, result.Status);
      Assert.Equal(executable, result.ExecutablePath, ignoreCase: true);
      Assert.True(Path.IsPathFullyQualified(result.ExecutablePath!), "the probe reports an absolute path");
    }
    finally { directory.Delete(true); }
  }

  [Fact]
  public void DefaultProbe_ExecutableNotOnDisk_ReportsAbsent()
  {
    DirectoryInfo directory = TestTempRoot.CreateDirectory("lspprobe");
    try
    {
      PrerequisiteProbeResult result = LspPrerequisiteProbe.CreateDefault()
          .Probe(Path.Combine(directory.FullName, "absent-tool"));
      Assert.Equal(PrerequisiteStatus.Absent, result.Status);
      Assert.Null(result.ExecutablePath);
    }
    finally { directory.Delete(true); }
  }
}
