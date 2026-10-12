using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;

namespace eThangAgent.PDF.ACL.Tests;

/// <summary>Builds PDF fixtures in-test with PDFsharp (no binary fixtures on disk).
///     One page per supplied string; text drawn with a platform font so PdfPig can
///     extract it. Encrypted and metadata variants support the read-path tests.</summary>
internal static class TestPdfFactory
{
  // Windows-only harness: the Core build resolves Arial/Times from C:\Windows\Fonts.
  static TestPdfFactory() => GlobalFontSettings.UseWindowsFontsUnderWindows = true;

  /// <summary>Writes a PDF with one page per string.</summary>
  public static void Create(string path, params string[] pageTexts)
    => Write(path, null, null, null, pageTexts);

  /// <summary>Writes a PDF carrying Title/Author document information.</summary>
  public static void CreateWithMetadata(string path, string title, string author, params string[] pageTexts)
    => Write(path, title, author, null, pageTexts);

  /// <summary>Writes a user-password-encrypted PDF (the sniffer's /Encrypt gate).</summary>
  public static void CreateEncrypted(string path, string userPassword, params string[] pageTexts)
    => Write(path, null, null, userPassword, pageTexts);

  private static void Write(string path, string? title, string? author, string? userPassword, string[] pageTexts)
  {
    string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
    if (dir is not null)
    {
      _ = Directory.CreateDirectory(dir);
    }

    using PdfDocument doc = new();
    if (title is not null)
    {
      doc.Info.Title = title;
    }

    if (author is not null)
    {
      doc.Info.Author = author;
    }

    foreach (string text in pageTexts)
    {
      PdfPage page = doc.AddPage();
      using XGraphics gfx = XGraphics.FromPdfPage(page);
      XFont font = new("Arial", 12);
      gfx.DrawString(text, font, XBrushes.Black, new XPoint(50, 100));
    }

    if (userPassword is not null)
    {
      doc.SecuritySettings.UserPassword = userPassword;
    }

    doc.Save(path);
  }
}
