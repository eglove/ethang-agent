using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;


// S3220/S3878 flag the OOXML element-constructor calls below; the element trees
// are built exactly this way by design (mirrors DirectDocxAccess/TestDocxFactory).
#pragma warning disable S3220, S3878
namespace eThangAgent.Documents.ACL;

/// <summary>Builds a complete new .docx package from the intermediate markdown
///     blocks (task 8): minimal styles part (Heading1-3, ListParagraph, Normal) and
///     a minimal numbering part (one bullet abstractNum + one decimal abstractNum);
///     tables carry direct tblBorders. The file must not exist (defense in depth -
///     the tool guards too).</summary>
public static class DocxDocumentBuilder
{
  public static void Build(string path, IReadOnlyList<DocxMarkdownBlock> blocks)
  {
    using WordprocessingDocument doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
    MainDocumentPart main = doc.AddMainDocumentPart();
    AddStyles(main);
    AddNumbering(main);
    main.Document = new Document(new Body([.. blocks.SelectMany(ElementsOf)]));
    doc.Save();
  }

  private static IEnumerable<OpenXmlElement> ElementsOf(DocxMarkdownBlock block)
  {
    switch (block)
    {
      case DocxMarkdownHeading heading:
        yield return new Paragraph(
        [
          new ParagraphProperties([new ParagraphStyleId { Val = "Heading" + heading.Level }]),
          new Run([new Text(heading.Text) { Space = SpaceProcessingModeValues.Preserve }]),
        ]);
        break;
      case DocxMarkdownListItem item:
        yield return new Paragraph(
        [
          new ParagraphProperties(
          [
            new ParagraphStyleId { Val = "ListParagraph" },
            new NumberingProperties(
            [
              new NumberingLevelReference { Val = 0 },
              new NumberingId { Val = item.Ordered ? 2 : 1 },
            ]),
          ]),
          new Run([new Text(item.Text) { Space = SpaceProcessingModeValues.Preserve }]),
        ]);
        break;
      case DocxMarkdownParagraph paragraph:
        yield return ParagraphOf(paragraph);
        break;
      case DocxMarkdownTable table:
        yield return TableOf(table);
        break;
      default:
        throw new InvalidOperationException($"unknown markdown block '{block.GetType().Name}'");
    }
  }

  private static Paragraph ParagraphOf(DocxMarkdownParagraph paragraph)
  {
    Paragraph para = new();
    foreach (DocxMarkdownSpan span in paragraph.Spans)
    {
      RunProperties? format = BuildFormat(span.Bold, span.Italic);
      Run run = format is null
        ? new Run([new Text(span.Text) { Space = SpaceProcessingModeValues.Preserve }])
        : new Run([format, new Text(span.Text) { Space = SpaceProcessingModeValues.Preserve }]);
      para.Append(run);
    }

    return para;
  }

  private static RunProperties? BuildFormat(bool bold, bool italic)
  {
    if (!bold && !italic)
    {
      return null;
    }

    RunProperties properties = new();
    if (bold)
    {
      properties.Bold = new Bold();
    }

    if (italic)
    {
      properties.Italic = new Italic();
    }

    return properties;
  }

  private static Table TableOf(DocxMarkdownTable table)
  {
    TableBorders borders = new(
    [
      new TopBorder { Val = BorderValues.Single },
      new LeftBorder { Val = BorderValues.Single },
      new BottomBorder { Val = BorderValues.Single },
      new RightBorder { Val = BorderValues.Single },
      new InsideHorizontalBorder { Val = BorderValues.Single },
      new InsideVerticalBorder { Val = BorderValues.Single },
    ]);
    Table result = new([new TableProperties([borders])]);
    foreach (IReadOnlyList<string> row in table.Rows)
    {
      List<TableCell> cells = [];
      foreach (string text in row)
      {
        cells.Add(new TableCell([new Paragraph([new Run([new Text(text) { Space = SpaceProcessingModeValues.Preserve }])])]));
      }

      result.Append(new TableRow([.. cells]));
    }

    return result;
  }

  private static void AddStyles(MainDocumentPart main)
  {
    StyleDefinitionsPart styles = main.AddNewPart<StyleDefinitionsPart>();
    styles.Styles = new Styles(
    [
      new Style([new StyleName { Val = "Normal" }]) { Type = StyleValues.Paragraph, StyleId = "Normal" },
      new Style([new StyleName { Val = "heading 1" }, new BasedOn { Val = "Normal" }, new PrimaryStyle()]) { Type = StyleValues.Paragraph, StyleId = "Heading1" },
      new Style([new StyleName { Val = "heading 2" }, new BasedOn { Val = "Normal" }, new PrimaryStyle()]) { Type = StyleValues.Paragraph, StyleId = "Heading2" },
      new Style([new StyleName { Val = "heading 3" }, new BasedOn { Val = "Normal" }, new PrimaryStyle()]) { Type = StyleValues.Paragraph, StyleId = "Heading3" },
      new Style([new StyleName { Val = "List Paragraph" }, new BasedOn { Val = "Normal" }]) { Type = StyleValues.Paragraph, StyleId = "ListParagraph" },
    ]);
  }

  private static void AddNumbering(MainDocumentPart main)
  {
    NumberingDefinitionsPart numbering = main.AddNewPart<NumberingDefinitionsPart>();
    numbering.Numbering = new Numbering(
    [
      new AbstractNum(
      [
        new Level(
        [
          new StartNumberingValue { Val = 1 },
          new NumberingFormat { Val = NumberFormatValues.Bullet },
          new LevelText { Val = "\u2022" },
          new LevelJustification { Val = LevelJustificationValues.Left },
        ]),
      ]) { AbstractNumberId = 1 },
      new AbstractNum(
      [
        new Level(
        [
          new StartNumberingValue { Val = 1 },
          new NumberingFormat { Val = NumberFormatValues.Decimal },
          new LevelText { Val = "%1." },
          new LevelJustification { Val = LevelJustificationValues.Left },
        ]),
      ]) { AbstractNumberId = 2 },
      new NumberingInstance(new AbstractNumId { Val = 1 }) { NumberID = 1 },
      new NumberingInstance(new AbstractNumId { Val = 2 }) { NumberID = 2 },
    ]);
  }
}
