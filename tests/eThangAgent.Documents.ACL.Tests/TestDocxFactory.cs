using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;


// S3220 flags every single-element params-ctor call below; OOXML element trees are
// built exactly this way by design (a child element argument to the params ctor is
// the intended shape, not a partial-match hazard). Named deviation, scoped to this
// fixture factory only.
#pragma warning disable S3220, S3878
namespace eThangAgent.Documents.ACL.Tests;

/// <summary>In-test .docx fixture generator (task 4): every fixture is created with
///     the SDK itself - no binary fixtures are committed. Each fixture lives in its
///     own temp directory the caller deletes. Single-child SDK ctor calls pass an
///     explicit OpenXmlElement[] (OpenXmlElement implements IEnumerable&lt;OpenXmlElement&gt;,
///     so a bare element argument is ambiguous between params-wrap and child-adoption).</summary>
public static class TestDocxFactory
{
  /// <summary>2 paragraphs + a 1x2 table.</summary>
  public static string CreateSimple(string name)
  {
    string path = NewPath(name);
    using (WordprocessingDocument doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
    {
      MainDocumentPart main = doc.AddMainDocumentPart();
      main.Document = new Document(new Body(Para("First paragraph"), Para("Second paragraph"), BuildTable(1, 2, "a", "b")));
      doc.Save();
    }
    return path;
  }

  /// <summary>Simple doc + StyleDefinitionsPart with Heading1 + Normal styles.</summary>
  public static string CreateWithStyles(string name)
  {
    string path = NewPath(name);
    using (WordprocessingDocument doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
    {
      MainDocumentPart main = doc.AddMainDocumentPart();
      Paragraph styled = new([new ParagraphProperties(new ParagraphStyleId { Val = "Heading1" }), BoldRun("Title")]);
      main.Document = new Document(new Body(styled, Para("Body text")));
      StyleDefinitionsPart styles = main.AddNewPart<StyleDefinitionsPart>();
      styles.Styles = new Styles(
      [
        new Style([new StyleName { Val = "heading 1" }, new PrimaryStyle()]) { Type = StyleValues.Paragraph, StyleId = "Heading1" },
        new Style([new StyleName { Val = "Normal" }]) { Type = StyleValues.Paragraph, StyleId = "Normal" },
      ]);
      doc.Save();
    }
    return path;
  }

  /// <summary>Valid docx + a vbaProject part with bytes (macro refusal fixture).</summary>
  public static string CreateWithStubMacroPart(string name)
  {
    string path = NewPath(name);
    using (WordprocessingDocument doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
    {
      MainDocumentPart main = doc.AddMainDocumentPart();
      main.Document = new Document(new Body(Para("macro doc")));
      VbaProjectPart vba = main.AddNewPart<VbaProjectPart>("application/vnd.ms-office.vbaProject", "vbaProject1");
      using (Stream s = vba.GetStream(FileMode.Create))
      {
        s.Write([0x01, 0x02], 0, 2);
      }
      doc.Save();
    }
    return path;
  }

  /// <summary>One insertion and one deletion tracked change by the given author.</summary>
  public static string CreateWithTrackedChanges(string name, string author)
  {
    string path = NewPath(name);
    using (WordprocessingDocument doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
    {
      MainDocumentPart main = doc.AddMainDocumentPart();
      InsertedRun insertion = new([RunOf("Added")]) { Id = "1", Author = author, Date = DateTime.UtcNow };
      DeletedRun deletion = new([new Run([new DeletedText("Gone") { Space = SpaceProcessingModeValues.Preserve }])]) { Id = "2", Author = author, Date = DateTime.UtcNow };
      main.Document = new Document(new Body(new Paragraph([insertion]), new Paragraph([deletion])));
      doc.Save();
    }
    return path;
  }

  /// <summary>One comment (id 1) anchored on the first paragraph.</summary>
  public static string CreateWithComments(string name, string author, string text)
  {
    string path = NewPath(name);
    using (WordprocessingDocument doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
    {
      MainDocumentPart main = doc.AddMainDocumentPart();
      WordprocessingCommentsPart comments = main.AddNewPart<WordprocessingCommentsPart>();
      comments.Comments = new Comments(
      [
        new Comment([new Paragraph([RunOf(text)])]) { Id = "1", Author = author, Date = DateTime.UtcNow },
        ]);
      Paragraph anchored = new(
      [
        new CommentRangeStart { Id = "1" },
          RunOf("Commented text"),
          new CommentRangeEnd { Id = "1" },
          new Run([new CommentReference { Id = "1" }]),
        ]);
      main.Document = new Document(new Body(anchored));
      doc.Save();
    }
    return path;
  }

  /// <summary>Simple doc + one embedded PNG image part.</summary>
  public static string CreateWithImage(string name, byte[] pngBytes)
  {
    ArgumentNullException.ThrowIfNull(pngBytes);
    string path = NewPath(name);
    using (WordprocessingDocument doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
    {
      MainDocumentPart main = doc.AddMainDocumentPart();
      main.Document = new Document(new Body(Para("with image")));
      ImagePart image = main.AddNewPart<ImagePart>("image/png", "img1");
      using (Stream s = image.GetStream(FileMode.Create))
      {
        s.Write(pngBytes, 0, pngBytes.Length);
      }
      doc.Save();
    }
    return path;
  }

  /// <summary>Body paragraph + a default header/footer, each with one paragraph.</summary>
  public static string CreateWithHeaderFooter(string name)
  {
    string path = NewPath(name);
    using (WordprocessingDocument doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
    {
      MainDocumentPart main = doc.AddMainDocumentPart();
      HeaderPart header = main.AddNewPart<HeaderPart>();
      header.Header = new Header(Para("Header text"));
      FooterPart footer = main.AddNewPart<FooterPart>();
      footer.Footer = new Footer(Para("Footer text"));
      SectionProperties sectPr = new(
      [
        new HeaderReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(header) },
          new FooterReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(footer) },
        ]);
      main.Document = new Document(new Body(Para("Body text"), sectPr));
      doc.Save();
    }
    return path;
  }

  private static string NewPath(string name)
  {
    string dir = Path.Combine(Path.GetTempPath(), "docx-tests-" + Guid.NewGuid().ToString("N"));
    _ = Directory.CreateDirectory(dir);
    return Path.Combine(dir, name + ".docx");
  }

  private static Paragraph Para(string text) => new([RunOf(text)]);

  private static Run BoldRun(string text)
  {
    RunProperties rp = new([new Bold()]);
    Run run = new([rp, new Text(text) { Space = SpaceProcessingModeValues.Preserve }]);
    return run;
  }

  private static Run RunOf(string text)
    => new([new Text(text) { Space = SpaceProcessingModeValues.Preserve }]);

  private static Table BuildTable(int rows, int cols, params string[] cells)
  {
    List<TableRow> trs = [];
    for (int r = 0; r < rows; r++)
    {
      List<TableCell> tcs = [];
      for (int c = 0; c < cols; c++)
      {
        tcs.Add(new TableCell(Para(cells[(r * cols) + c])));
      }
      trs.Add(new TableRow(tcs));
    }
    return new Table(trs);
  }
}
