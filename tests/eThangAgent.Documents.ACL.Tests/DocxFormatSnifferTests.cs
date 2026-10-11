using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Documents.ACL.Tests;

/// <summary>Format sniffing for the docx surface (task 4): zip magic, package
///     inspection, macro refusal, oversize refusal - all against SDK-generated
///     fixtures.</summary>
public sealed class DocxFormatSnifferTests : IDisposable
{
  private readonly List<string> _dirs = [];

  public void Dispose()
  {
    foreach (string dir in _dirs)
    {
      try
      {
        Directory.Delete(dir, true);
      }
      catch (IOException)
      {
        // best-effort temp cleanup
      }
      catch (UnauthorizedAccessException)
      {
        // best-effort temp cleanup
      }
    }

    GC.SuppressFinalize(this);
  }

  private string Track(string path)
  {
    string? dir = Path.GetDirectoryName(path);
    return dir is null ? path : TrackDir(dir);
  }

  private string TrackDir(string dir)
  {
    _dirs.Add(dir);
    return dir;
  }

  private string TempFile(string name, string content)
  {
    string dir = TrackDir(Path.Combine(Path.GetTempPath(), "docx-tests-" + Guid.NewGuid().ToString("N")));
    _ = Directory.CreateDirectory(dir);
    string path = Path.Combine(dir, name);
    File.WriteAllText(path, content);
    return path;
  }

  [Fact]
  public void Accepts_sdk_generated_docx()
  {
    string path = TestDocxFactory.CreateSimple("sniff-ok");
    _ = Track(path);
    Result<string> r = DocxFormatSniffer.Inspect(path);
    Assert.True(r.IsSuccess, r.Error?.Message);
    Assert.Equal("docx", r.Value);
  }

  [Fact]
  public void Rejects_text_file()
  {
    string path = TempFile("plain.txt", "hello");
    Result<string> r = DocxFormatSniffer.Inspect(path);
    Assert.False(r.IsSuccess);
    Assert.Equal(DocxErrorCodes.NotADocx, r.Error.Code);
  }

  [Fact]
  public void Rejects_missing_file()
  {
    string path = Path.Combine(Path.GetTempPath(), "docx-missing-" + Guid.NewGuid().ToString("N") + ".docx");
    Result<string> r = DocxFormatSniffer.Inspect(path);
    Assert.False(r.IsSuccess);
    Assert.Equal(DocxErrorCodes.FileNotFound, r.Error.Code);
  }

  [Fact]
  public void Rejects_zip_without_docx_parts()
  {
    string dir = TrackDir(Path.Combine(Path.GetTempPath(), "docx-tests-" + Guid.NewGuid().ToString("N")));
    string inner = Path.Combine(dir, "payload");
    _ = Directory.CreateDirectory(inner);
    File.WriteAllText(Path.Combine(inner, "x.txt"), "x");
    string zipPath = Path.Combine(dir, "plain.zip");
    System.IO.Compression.ZipFile.CreateFromDirectory(inner, zipPath);
    File.Move(zipPath, Path.Combine(dir, "plain.docx"));
    Result<string> r = DocxFormatSniffer.Inspect(Path.Combine(dir, "plain.docx"));
    Assert.False(r.IsSuccess);
    Assert.Equal(DocxErrorCodes.CorruptDocument, r.Error.Code);
  }

  [Fact]
  public void Rejects_macro_document()
  {
    string path = TestDocxFactory.CreateWithStubMacroPart("macro");
    _ = Track(path);
    Result<string> r = DocxFormatSniffer.Inspect(path);
    Assert.False(r.IsSuccess);
    Assert.Equal(DocxErrorCodes.MacroDocument, r.Error.Code);
  }

  [Fact]
  public void Rejects_oversize()
  {
    string dir = TrackDir(Path.Combine(Path.GetTempPath(), "docx-tests-" + Guid.NewGuid().ToString("N")));
    _ = Directory.CreateDirectory(dir);
    string path = Path.Combine(dir, "big.docx");
    using (FileStream fs = File.Create(path))
    {
      fs.SetLength(DocxLimits.MaxFileBytes + 1);
    }
    Result<string> r = DocxFormatSniffer.Inspect(path);
    Assert.False(r.IsSuccess);
    Assert.Equal(DocxErrorCodes.OversizeDocument, r.Error.Code);
  }
}
