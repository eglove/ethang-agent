using eThangAgent.ToolDomain;

namespace eThangAgent.FileSystem.ACL.Tests;

// Test helpers: sync temp-file IO and best-effort cleanup are deliberate.
#pragma warning disable CA1849 // Call async methods when in an async method
#pragma warning disable CA1031 // Do not catch general exception types

/// <summary>The workspace-majority fallback is a HEURISTIC, so it must be BOUNDED:
///     a convention resolver on the write path may never walk an arbitrarily large
///     tree. These pin the bound and the short-circuit, so a huge workspace costs a
///     fixed sample instead of a full scan.</summary>
public sealed class WorkspaceMajorityBoundTests : IDisposable
{
  private readonly string _root = Directory.CreateTempSubdirectory("ethang-eolb").FullName;

  public void Dispose()
  {
    try
    {
      Directory.Delete(_root, recursive: true);
    }
    catch (IOException)
    {
      // best-effort temp cleanup
    }
    catch (UnauthorizedAccessException)
    {
      // best-effort temp cleanup
    }

    GC.SuppressFinalize(this);
  }

  [Fact]
  public void MajorityScan_ExaminesAtMostTheDocumentedCap()
  {
    // A tree comfortably larger than the cap: the scan must stop at the cap, not
    // enumerate everything.
    const int files = WorkspaceTextFiles.MaxSampledFiles + 200;
    for (int i = 0; i < files; i++)
    {
      File.WriteAllText(Path.Combine(_root, $"f{i}.txt"), "line\r\n");
    }

    WorkspaceTextFiles.Sample sample = WorkspaceTextFiles.SampleMajority(_root);

    Assert.True(sample.Examined <= WorkspaceTextFiles.MaxSampledFiles,
        $"examined {sample.Examined}, cap {WorkspaceTextFiles.MaxSampledFiles}");
    Assert.Equal(LineEnding.CrLf, sample.Dominant);
  }

  [Fact]
  public void MajorityScan_ShortCircuitsOnceTheResultCannotChange()
  {
    // 200 LF files then 200 CRLF files, named so the LF ones are reached FIRST. Once the
    // LF lead exceeds the remaining sample budget the outcome is settled, so the scan must
    // stop long before it has seen all 400 files.
    for (int i = 0; i < 200; i++)
    {
      File.WriteAllText(Path.Combine(_root, $"a-lf{i:D3}.txt"), "line\n");
    }

    for (int i = 0; i < 200; i++)
    {
      File.WriteAllText(Path.Combine(_root, $"z-crlf{i:D3}.txt"), "line\r\n");
    }

    WorkspaceTextFiles.Sample sample = WorkspaceTextFiles.SampleMajority(_root);

    Assert.Equal(LineEnding.Lf, sample.Dominant);
    Assert.True(sample.Examined < 400, $"examined {sample.Examined} of 400 — no short-circuit");
  }

  [Fact]
  public void MajorityScan_EmptyTree_ReportsNoDominantConvention()
  {
    WorkspaceTextFiles.Sample sample = WorkspaceTextFiles.SampleMajority(_root);

    Assert.Equal(0, sample.Examined);
    Assert.Null(sample.Dominant);
  }
}
