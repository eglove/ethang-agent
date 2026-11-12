using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

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

  /// <summary>Writes a one-page PDF carrying an AcroForm with a text field, a
  ///     checkbox, a combo box with the given options, and a push button. The page also
  ///     carries body text, so a flatten can be shown to preserve the page's content.
  ///     </summary>
  public static void CreateForm(string path, params string[] options)
  {
    string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
    if (dir is not null)
    {
      _ = Directory.CreateDirectory(dir);
    }

    using PdfDocument doc = new();
    PdfPage page = doc.AddPage();
    using (XGraphics gfx = XGraphics.FromPdfPage(page))
    {
      gfx.DrawString("Body text", new XFont("Arial", 12), XBrushes.Black, new XPoint(50, 60));
    }

    PdfDictionary name = Widget(doc, page, new XRect(50, 90, 200, 20), "/Tx", "fullName");
    PdfDictionary agree = Widget(doc, page, new XRect(50, 130, 20, 20), "/Btn", "agree");
    PdfDictionary country = Widget(doc, page, new XRect(50, 170, 200, 20), "/Ch", "country");
    PdfDictionary submit = Widget(doc, page, new XRect(50, 210, 100, 20), "/Btn", "submit");

    agree.Elements.SetName("/V", "/Off");
    agree.Elements.SetName("/AS", "/Off");

    PdfArray choices = new(doc);
    foreach (string option in options)
    {
      choices.Elements.Add(new PdfString(option));
    }

    country.Elements["/Opt"] = choices;

    // Bit 17 (65536) marks a push button, which carries a caption rather than a value.
    submit.Elements.SetInteger("/Ff", 65536);

    PdfItem[] widgets = [.. new[] { name, agree, country, submit }.Select(w => (PdfItem)PdfInternals.GetReference(w)!)];
    page.Elements["/Annots"] = new PdfArray(doc, [.. widgets]);

    PdfDictionary acro = new(doc);
    acro.Elements["/Fields"] = new PdfArray(doc, [.. widgets]);
    acro.Elements.SetName("/DA", "/Helv 0 Tf 0 g");
    doc.Internals.AddObject(acro);
    doc.Internals.Catalog.Elements["/AcroForm"] = PdfInternals.GetReference(acro);
    doc.Save(path);
  }

  /// <summary>Builds one widget annotation for <see cref="CreateForm"/>, already
  ///     registered as an indirect object and bound to its page.</summary>
  private static PdfDictionary Widget(PdfDocument doc, PdfPage page, XRect rect, string fieldType, string name)
  {
    PdfDictionary widget = new(doc);
    widget.Elements.SetName("/Type", "/Annot");
    widget.Elements.SetName("/Subtype", "/Widget");
    widget.Elements.SetRectangle("/Rect", new PdfRectangle(rect));
    widget.Elements.SetName("/FT", fieldType);
    widget.Elements.SetString("/T", name);
    widget.Elements.SetInteger("/Ff", 0);
    widget.Elements.SetInteger("/F", 4);
    widget.Elements.SetName("/DA", "/Helv 0 Tf 0 g");
    doc.Internals.AddObject(widget);
    widget.Elements["/P"] = PdfInternals.GetReference(page);
    return widget;
  }

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
