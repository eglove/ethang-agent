using System.Text;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.PDF.ACL.Tests;

public sealed class PdfFormatSnifferTests : IDisposable
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

  private string TrackDir(string dir)
  {
    _dirs.Add(dir);
    return dir;
  }

  private string TempFile(string name, byte[] bytes)
  {
    string dir = TrackDir(Path.Combine(Path.GetTempPath(), "pdf-tests-" + Guid.NewGuid().ToString("N")));
    _ = Directory.CreateDirectory(dir);
    string path = Path.Combine(dir, name);
    File.WriteAllBytes(path, bytes);
    return path;
  }

  private string TempFile(string name, string text)
    => TempFile(name, Encoding.ASCII.GetBytes(text));

  [Fact]
  public void Accepts_valid_pdf_header()
  {
    string path = TempFile("valid.pdf", "%PDF-1.4\n1 0 obj\n<<>>\nendobj\ntrailer\n<<>>\n%%EOF");
    Result<string> r = PdfFormatSniffer.Inspect(path);
    Assert.True(r.IsSuccess, r.Error?.Message);
    Assert.Equal("pdf", r.Value);
  }

  [Fact]
  public void Rejects_missing_file()
  {
    string path = Path.Combine(Path.GetTempPath(), "pdf-missing-" + Guid.NewGuid().ToString("N") + ".pdf");
    Result<string> r = PdfFormatSniffer.Inspect(path);
    Assert.False(r.IsSuccess);
    Assert.Equal(PdfErrorCodes.FileNotFound, r.Error.Code);
  }

  [Fact]
  public void Rejects_text_file()
  {
    string path = TempFile("plain.txt", "This is plain text, not a PDF.");
    Result<string> r = PdfFormatSniffer.Inspect(path);
    Assert.False(r.IsSuccess);
    Assert.Equal(PdfErrorCodes.NotAPdf, r.Error.Code);
  }

  [Fact]
  public void Rejects_file_shorter_than_header()
  {
    string path = TempFile("short.pdf", "%PDF");
    Result<string> r = PdfFormatSniffer.Inspect(path);
    Assert.False(r.IsSuccess);
    Assert.Equal(PdfErrorCodes.NotAPdf, r.Error.Code);
  }

  [Fact]
  public void Rejects_encrypted_pdf_with_trailer_encrypt_tag()
  {
    string header = "%PDF-1.4\n1 0 obj\n<<>>\nendobj\n";
    string trailer = "trailer\n<</Encrypt 2 0 R /Root 1 0 R>>\n%%EOF";
    string path = TempFile("encrypted.pdf", header + trailer);
    Result<string> r = PdfFormatSniffer.Inspect(path);
    Assert.False(r.IsSuccess);
    Assert.Equal(PdfErrorCodes.EncryptedPdf, r.Error.Code);
    Assert.Contains(path, r.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void Accepts_pdf_when_encrypt_is_outside_trailer_region()
  {
    // If /Encrypt appears in the body object earlier than 2 KB from the end, sniffer ignores it
    byte[] header = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj\n<</Title (/Encrypt in title)>>\nendobj\n");
    byte[] padding = new byte[3000];
    Array.Fill(padding, (byte)' ');
    byte[] trailer = Encoding.ASCII.GetBytes("\ntrailer\n<</Root 1 0 R>>\n%%EOF");

    byte[] full = new byte[header.Length + padding.Length + trailer.Length];
    Buffer.BlockCopy(header, 0, full, 0, header.Length);
    Buffer.BlockCopy(padding, 0, full, header.Length, padding.Length);
    Buffer.BlockCopy(trailer, 0, full, header.Length + padding.Length, trailer.Length);

    string path = TempFile("not-encrypted.pdf", full);
    Result<string> r = PdfFormatSniffer.Inspect(path);
    Assert.True(r.IsSuccess, r.Error?.Message);
    Assert.Equal("pdf", r.Value);
  }

  [Fact]
  public void Rejects_oversize_file()
  {
    string dir = TrackDir(Path.Combine(Path.GetTempPath(), "pdf-tests-" + Guid.NewGuid().ToString("N")));
    _ = Directory.CreateDirectory(dir);
    string path = Path.Combine(dir, "oversize.pdf");

    using (FileStream fs = new(path, FileMode.Create, FileAccess.Write, FileShare.None))
    {
      byte[] header = Encoding.ASCII.GetBytes("%PDF-1.4\n");
      fs.Write(header, 0, header.Length);
      // MaxFileBytes is 100 MB = 104,857,600 bytes
      fs.SetLength(PdfLimits.MaxFileBytes + 1);
    }

    Result<string> r = PdfFormatSniffer.Inspect(path);
    Assert.False(r.IsSuccess);
    Assert.Equal(PdfErrorCodes.OversizePdf, r.Error.Code);
  }
}
