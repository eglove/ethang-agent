using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using eThangAgent.ToolDomain;

namespace eThangAgent.Documents.ACL;

/// <summary>Walks a document into the neutral block model (task 5). Body paragraphs
///     and tables interleave in document order and SHARE the flat 1-based numbering
///     (the third block is p3 or t3 by its kind - DocxRecords pins the shared
///     numbering; the flat list position is the pagination order). Headers and
///     footers walk per section reference into their own paragraph lists. Named
///     decisions: a paragraph without an explicit style reads as an empty style name;
///     an existing-but-unknown style id renders as the raw id (never fabricated);
///     deleted text (w:delText) renders in paragraph text because Word shows it
///     (strikethrough) - the revision list is the change surface.</summary>
public static class DocxBlockWalker
{
  /// <summary>Walks the whole document into a snapshot paginated to
  ///     blocks[offset..offset+maxBlocks]. Callers validate offset/maxBlocks and the
  ///     package (sniffer) before calling; the version is the stat-first token the
  ///     access captured before opening.</summary>
  public static DocxSnapshot Walk(WordprocessingDocument doc, FileVersion version, int offset, int maxBlocks)
  {
    ArgumentNullException.ThrowIfNull(doc);
    MainDocumentPart main = doc.MainDocumentPart ?? throw new InvalidOperationException("package has no main document part");
    Body body = main.Document.Body ?? throw new InvalidOperationException("document has no body");
    List<DocxBlock> blocks = [];
    foreach (OpenXmlElement child in body.ChildElements)
    {
      if (child is Paragraph para)
      {
        blocks.Add(new DocxParagraphBlock(blocks.Count + 1, StyleNameOf(para, main), TextOf(para)));
      }
      else if (child is Table tbl)
      {
        blocks.Add(TableBlockOf(blocks.Count + 1, tbl));
      }
    }

    Document document = main.Document;
    int tracked = document.Descendants<InsertedRun>().Count()
      + document.Descendants<DeletedRun>().Count()
      + document.Descendants<PreviousRunProperties>().Count()
      + document.Descendants<PreviousParagraphProperties>().Count();
    int comments = main.WordprocessingCommentsPart?.Comments?.Elements<Comment>().Count() ?? 0;
    List<DocxMediaInfo> media = [.. main.GetPartsOfType<ImagePart>()
      .Select(part => new DocxMediaInfo(NameOf(part.Uri), part.ContentType, PartBytes(part)))];
    List<DocxSectionLayers> sections = SectionsOf(body, main);
    List<DocxBlock> page = [.. blocks.Skip(offset).Take(maxBlocks)];
    return new DocxSnapshot(version, blocks.Count, offset, page, sections.Count, tracked, comments, media, sections);
  }

  private static string NameOf(Uri uri)
  {
    string raw = uri.OriginalString;
    int slash = raw.LastIndexOf('/');
    return slash < 0 ? raw : raw[(slash + 1)..];
  }

  private static long PartBytes(ImagePart part)
  {
    using Stream s = part.GetStream();
    return s.Length;
  }

  private static string StyleNameOf(Paragraph para, MainDocumentPart main)
  {
    string? id = para.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
    if (id is null)
    {
      return string.Empty;
    }

    Styles? styles = main.StyleDefinitionsPart?.Styles;
    return styles is null ? id : StyleDisplayNameOf(styles, id);
  }

  /// <summary>The style's display name: the named style's name, or the raw id when
  ///     the id exists but the style does not (never fabricated).</summary>
  private static string StyleDisplayNameOf(Styles styles, string id)
    => styles.Elements<Style>()
      .FirstOrDefault(s => string.Equals(s.StyleId?.Value, id, StringComparison.Ordinal))
      ?.StyleName?.Val?.Value ?? id;

  private static string TextOf(OpenXmlElement container)
    => string.Concat(FragmentsOf(container));

  /// <summary>The text fragments of one element: w:t text, w:tab as tab, w:br as
  ///     newline, and w:delText (deleted text renders because Word shows it
  ///     strikethrough - the revision list is the change surface).</summary>
  private static IEnumerable<string> FragmentsOf(OpenXmlElement container)
  {
    foreach (OpenXmlElement el in container.Descendants())
    {
      if (el is Text text)
      {
        yield return text.Text;
      }
      else if (el is TabChar)
      {
        yield return "\t";
      }
      else if (el is Break)
      {
        yield return "\n";
      }
      else if (el is DeletedText deleted)
      {
        yield return deleted.Text;
      }
    }
  }

  private static DocxTableBlock TableBlockOf(int index, Table tbl)
  {
    List<TableRow> rows = [.. tbl.Elements<TableRow>()];
    int cols = rows.Count == 0 ? 0 : rows.Max(r => r.Elements<TableCell>().Count());
    List<DocxCellText> cells = [];
    for (int r = 0; r < rows.Count; r++)
    {
      List<TableCell> rowCells = [.. rows[r].Elements<TableCell>()];
      for (int c = 0; c < rowCells.Count; c++)
      {
        cells.Add(new DocxCellText(r + 1, c + 1, CellTextOf(rowCells[c])));
      }
    }

    return new DocxTableBlock(index, rows.Count, cols, cells);
  }

  private static string CellTextOf(TableCell cell)
    => string.Join("\n", cell.Elements<Paragraph>().Select(TextOf));

  private static List<DocxSectionLayers> SectionsOf(Body body, MainDocumentPart main)
  {
    List<SectionProperties> sectPrs = [];
    foreach (OpenXmlElement child in body.ChildElements)
    {
      if (child is Paragraph para)
      {
        SectionProperties? paraSect = para.ParagraphProperties?.GetFirstChild<SectionProperties>();
        if (paraSect is not null)
        {
          sectPrs.Add(paraSect);
        }
      }
      else if (child is SectionProperties bodySect)
      {
        sectPrs.Add(bodySect);
      }
    }

    List<DocxSectionLayers> sections = new(sectPrs.Count);
    for (int i = 0; i < sectPrs.Count; i++)
    {
      sections.Add(LayersOf(i + 1, sectPrs[i], main));
    }

    return sections;
  }

  private static DocxSectionLayers LayersOf(int number, SectionProperties sectPr, MainDocumentPart main)
  {
    List<DocxParagraphBlock> headerParagraphs = [];
    List<DocxParagraphBlock> footerParagraphs = [];
    int hp = 0;
    int fp = 0;
    foreach (HeaderReference reference in sectPr.Elements<HeaderReference>())
    {
      if (TryPart<HeaderPart>(main, reference.Id) is not HeaderPart part)
      {
        continue;
      }

      foreach (Paragraph para in part.Header.Elements<Paragraph>())
      {
        hp++;
        headerParagraphs.Add(new DocxParagraphBlock(hp, StyleNameOf(para, main), TextOf(para)));
      }
    }

    foreach (FooterReference reference in sectPr.Elements<FooterReference>())
    {
      if (TryPart<FooterPart>(main, reference.Id) is not FooterPart part)
      {
        continue;
      }

      foreach (Paragraph para in part.Footer.Elements<Paragraph>())
      {
        fp++;
        footerParagraphs.Add(new DocxParagraphBlock(fp, StyleNameOf(para, main), TextOf(para)));
      }
    }

    return new DocxSectionLayers(number, headerParagraphs, footerParagraphs);
  }

  private static T? TryPart<T>(MainDocumentPart main, string? relationshipId)
    where T : OpenXmlPart
  {
    if (relationshipId is null)
    {
      return null;
    }

    try
    {
      return main.GetPartById(relationshipId) as T;
    }
    catch (KeyNotFoundException)
    {
      return null; // dangling header/footer reference: skip the layer, never crash the read
    }
  }
}
