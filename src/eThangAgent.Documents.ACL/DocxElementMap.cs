using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using eThangAgent.ToolDomain;

namespace eThangAgent.Documents.ACL;

/// <summary>The shared element-to-address mapping (task 9, extracted from the task 5
///     walker): the flat body block list, per-section header/footer paragraph lists,
///     and the address of ANY paragraph element the document carries. Read anchors
///     and edit/comment resolution agree BY CONSTRUCTION because both sides ask
///     this map.</summary>
public static class DocxElementMap
{
  /// <summary>Builds the map over an open document: body blocks in document order
  ///     (shared flat numbering), header/footer paragraph layers per section.</summary>
  public static DocxMap Build(WordprocessingDocument doc)
  {
    ArgumentNullException.ThrowIfNull(doc);
    MainDocumentPart main = doc.MainDocumentPart ?? throw new InvalidOperationException("package has no main document part");
    Body body = main.Document.Body ?? throw new InvalidOperationException("document has no body");
    List<OpenXmlElement> blocks = [.. body.ChildElements.Where(child => child is Paragraph or Table)];

    List<SectionProperties> sectPrs =
      [.. body.ChildElements
        .Select(child => child switch
        {
          Paragraph para => para.ParagraphProperties?.GetFirstChild<SectionProperties>(),
          SectionProperties bodySect => bodySect,
          _ => null,
        })
        .Where(sect => sect is not null)
        .Select(sect => sect!)];

    List<DocxSectionLayers> layers = [];
    List<(OpenXmlElement Element, string Address)> extras = [];
    for (int i = 0; i < sectPrs.Count; i++)
    {
      (DocxSectionLayers layer, List<(OpenXmlElement, string)> layerExtras) = LayersOf(i + 1, sectPrs[i], main);
      layers.Add(layer);
      extras.AddRange(layerExtras);
    }

    return new DocxMap(blocks, layers, extras);
  }

  /// <summary>The address of a paragraph element: its body block position, or its
  ///     header/footer layer position; null when the element is unknown to the map
  ///     (e.g. inside a comment the map does not carry).</summary>
  public static string? AddressOf(DocxMap map, OpenXmlElement element)
  {
    ArgumentNullException.ThrowIfNull(map);
    ArgumentNullException.ThrowIfNull(element);
    int idx = IndexOfElement(map.BodyBlocks, element);
    if (idx >= 0)
    {
      return element is Table ? "t" + (idx + 1) : "p" + (idx + 1);
    }

    foreach ((OpenXmlElement extra, string address) in map.ExtraParagraphs)
    {
      if (extra == element)
      {
        return address;
      }
    }

    // inside a table cell: walk up to the table block, compute r/c
    for (OpenXmlElement? at = element.Parent; at is not null; at = at.Parent)
    {
      if (at is Table table)
      {
        int tIdx = IndexOfElement(map.BodyBlocks, table);
        if (tIdx >= 0 && element.Ancestors<TableCell>().FirstOrDefault() is TableCell cell)
        {
          (int row, int col) = CellCoords(table, cell);
          return $"t{tIdx + 1}.r{row}.c{col}";
        }
      }
    }

    return null;
  }

  /// <summary>Reference equality index over the block list (OpenXmlElement
  ///     identity, not value equality).</summary>
  private static int IndexOfElement(IReadOnlyList<OpenXmlElement> blocks, OpenXmlElement element)
  {
    for (int i = 0; i < blocks.Count; i++)
    {
      if (ReferenceEquals(blocks[i], element))
      {
        return i;
      }
    }

    return -1;
  }

  private static (int Row, int Col) CellCoords(Table table, TableCell cell)
  {
    List<TableRow> rows = [.. table.Elements<TableRow>()];
    for (int r = 0; r < rows.Count; r++)
    {
      List<TableCell> cells = [.. rows[r].Elements<TableCell>()];
      for (int c = 0; c < cells.Count; c++)
      {
        if (cells[c] == cell)
        {
          return (r + 1, c + 1);
        }
      }
    }

    return (0, 0);
  }

  private static (DocxSectionLayers Layer, List<(OpenXmlElement, string)> Extras) LayersOf(int number, SectionProperties sectPr, MainDocumentPart main)
  {
    List<DocxParagraphBlock> headerParagraphs = [];
    List<DocxParagraphBlock> footerParagraphs = [];
    List<(OpenXmlElement, string)> extras = [];
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
        string address = $"sec{number}.header.p{hp}";
        headerParagraphs.Add(new DocxParagraphBlock(hp, string.Empty, TextOf(para)));
        extras.Add((para, address));
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
        string address = $"sec{number}.footer.p{fp}";
        footerParagraphs.Add(new DocxParagraphBlock(fp, string.Empty, TextOf(para)));
        extras.Add((para, address));
      }
    }

    return (new DocxSectionLayers(number, headerParagraphs, footerParagraphs), extras);
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
      return null; // dangling reference: skip the layer, never crash the walk
    }
  }

  private static string TextOf(OpenXmlElement container)
    => string.Concat(container.Descendants<Text>().Select(t => t.Text));
}

/// <summary>The built map: body blocks (document order), section layers, and the
///     header/footer paragraph elements with their addresses.</summary>
public sealed record DocxMap(
    IReadOnlyList<OpenXmlElement> BodyBlocks,
    IReadOnlyList<DocxSectionLayers> Layers,
    IReadOnlyList<(OpenXmlElement Element, string Address)> ExtraParagraphs);
