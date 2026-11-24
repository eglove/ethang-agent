namespace eThangAgent.Presentation.ACL.Tests;

/// <summary>The raw part XML every fixture deck is assembled from. Parts are fed as text
///     rather than built from element constructors: a fixture states the markup it wants
///     and nothing else, and the SDK parses it on access.</summary>
internal static class TestPptxXml
{
  internal const string NsA = "http://schemas.openxmlformats.org/drawingml/2006/main";
  internal const string NsR = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
  internal const string NsP = "http://schemas.openxmlformats.org/presentationml/2006/main";
  internal const string NsC = "http://schemas.openxmlformats.org/drawingml/2006/chart";

  internal const string TableGraphicUri = "http://schemas.openxmlformats.org/drawingml/2006/table";
  internal const string ChartGraphicUri = NsC;

  private const string Header = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>";

  /// <summary>The three markup slots a text shape can state: its shape properties, its
  ///     run properties and its paragraph properties. Plain is the unformatted default.
  ///     </summary>
  internal sealed record TextShapeMarkup(string ShapeProperties, string RunProperties, string ParagraphProperties)
  {
    internal static readonly TextShapeMarkup Plain = new("<p:spPr/>", "<a:rPr lang=\"en-US\"/>", string.Empty);
  }

  private const string Root = "xmlns:a=\"" + NsA + "\" xmlns:r=\"" + NsR + "\" xmlns:p=\"" + NsP + "\"";

  private const string GroupProperties =
      "<p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>"
      + "<p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/>"
      + "<a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"0\" cy=\"0\"/></a:xfrm></p:grpSpPr>";

  /// <summary>A slide part: the given shapes in document order, optionally hidden, with
  ///     an optional timing element.</summary>
  internal static string Slide(string shapes, bool hidden, string? timing)
  {
    string show = hidden ? " show=\"0\"" : string.Empty;
    return Header
        + "<p:sld " + Root + show + ">"
        + "<p:cSld><p:spTree>" + GroupProperties + shapes + "</p:spTree></p:cSld>"
        + "<p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr>"
        + (timing ?? string.Empty)
        + "</p:sld>";
  }

  /// <summary>A title placeholder carrying the given text, one paragraph per line.
  ///     </summary>
  internal static string TitleShape(int id, string text)
    => TextShape(id, "Title " + id, "title", null, text);

  /// <summary>A title placeholder whose run carries explicit character formatting and
  ///     whose shape carries an explicit position and size, as a deck authored in
  ///     PowerPoint does.</summary>
  internal static string FormattedTitleShape(int id, string text)
    => TextShape(id, "Title " + id, "title", null, text, FormattedTitleMarkup);

  /// <summary>The markup a formatted title carries: an explicit position and size, 24pt
  ///     bold character formatting and centred paragraphs.</summary>
  private static readonly TextShapeMarkup FormattedTitleMarkup = new(
      "<p:spPr><a:xfrm><a:off x=\"838200\" y=\"365125\"/>"
      + "<a:ext cx=\"10515600\" cy=\"1325563\"/></a:xfrm>"
      + "<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></p:spPr>",
      "<a:rPr lang=\"en-US\" sz=\"2400\" b=\"1\"/>",
      "<a:pPr algn=\"ctr\"/>");

  /// <summary>A body placeholder carrying the given text, one paragraph per line.
  ///     </summary>
  internal static string BodyShape(int id, string text)
    => TextShape(id, "Content " + id, "body", "1", text);

  /// <summary>A plain text box whose single paragraph carries an explicit line break
  ///     between two runs.</summary>
  internal static string BreakShape(int id, string first, string second)
    => "<p:sp><p:nvSpPr><p:cNvPr id=\"" + id + "\" name=\"Break " + id + "\"/>"
       + "<p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr><p:nvPr/></p:nvSpPr>"
       + "<p:spPr/><p:txBody><a:bodyPr/><a:lstStyle/><a:p>"
       + "<a:r><a:rPr lang=\"en-US\"/><a:t>" + Escape(first) + "</a:t></a:r>"
       + "<a:br/>"
       + "<a:r><a:rPr lang=\"en-US\"/><a:t>" + Escape(second) + "</a:t></a:r>"
       + "</a:p></p:txBody></p:sp>";

  /// <summary>A plain text box with five runs in one paragraph, the second and fourth of
  ///     them empty, as a real deck's authoring leaves behind.</summary>
  internal static string EmptyRunsShape(int id)
    => "<p:sp><p:nvSpPr><p:cNvPr id=\"" + id + "\" name=\"Runs " + id + "\"/>"
       + "<p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr><p:nvPr/></p:nvSpPr>"
       + "<p:spPr/><p:txBody><a:bodyPr/><a:lstStyle/><a:p>"
       + "<a:r><a:rPr lang=\"en-US\"/><a:t>Alpha</a:t></a:r>"
       + "<a:r><a:rPr lang=\"en-US\"/><a:t></a:t></a:r>"
       + "<a:r><a:rPr lang=\"en-US\"/><a:t>Beta</a:t></a:r>"
       + "<a:r><a:rPr lang=\"en-US\"/><a:t></a:t></a:r>"
       + "<a:r><a:rPr lang=\"en-US\"/><a:t>Gamma</a:t></a:r>"
       + "</a:p></p:txBody></p:sp>";

  /// <summary>A timing element with no effect in it: a main sequence that carries no
  ///     effect node, which is what a deck with no animation writes.</summary>
  internal static string EmptyTiming()
    => "<p:timing><p:tnLst><p:par><p:cTn id=\"1\" dur=\"indefinite\" restart=\"never\" nodeType=\"tmRoot\">"
       + "<p:childTnLst><p:seq concurrent=\"1\" nextAc=\"seek\"><p:cTn id=\"2\" dur=\"indefinite\" nodeType=\"mainSeq\"/>"
       + "</p:seq></p:childTnLst></p:cTn></p:par></p:tnLst></p:timing>";

  /// <summary>A legacy comment part: one comment anchored at the slide's first shape, by
  ///     author id 1.</summary>
  internal static string LegacyComments(string text)
    => Header
       + "<p:cmLst " + Root + ">"
       + "<p:cm authorId=\"1\" dt=\"2024-01-01T00:00:00\" idx=\"1\">"
       + "<p:pos x=\"10\" y=\"10\"/>"
       + "<p:text>" + Escape(text) + "</p:text></p:cm>"
       + "</p:cmLst>";

  /// <summary>The legacy author list: author id 1 is 'Reviewer'.</summary>
  internal static string CommentAuthors()
    => Header
       + "<p:cmAuthorLst " + Root + ">"
       + "<p:cmAuthor id=\"1\" name=\"Reviewer\" initials=\"R\" lastIdx=\"1\" clrIdx=\"0\"/>"
       + "</p:cmAuthorLst>";

  /// <summary>A plain text box that is not a placeholder.</summary>
  internal static string PlainTextBox(int id, string text)
    => TextShape(id, "TextBox " + id, null, null, text);

  private static string TextShape(int id, string name, string? placeholderType, string? placeholderIndex, string text,
      TextShapeMarkup? markup = null)
  {
    TextShapeMarkup shape = markup ?? TextShapeMarkup.Plain;
    string index = placeholderIndex is null ? string.Empty : " idx=\"" + placeholderIndex + "\"";
    string placeholder = placeholderType is null
        ? "<p:nvPr/>"
        : "<p:nvPr><p:ph type=\"" + placeholderType + "\""
          + index
          + "/></p:nvPr>";
    return "<p:sp><p:nvSpPr><p:cNvPr id=\"" + id + "\" name=\"" + name + "\"/>"
        + "<p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr>" + placeholder + "</p:nvSpPr>"
        + shape.ShapeProperties
        + "<p:txBody><a:bodyPr/><a:lstStyle/>" + Paragraphs(text, shape) + "</p:txBody></p:sp>";
  }

  /// <summary>One paragraph per line of text; an empty text is a single empty paragraph.
  ///     </summary>
  internal static string Paragraphs(string text)
    => Paragraphs(text, TextShapeMarkup.Plain);

  private static string Paragraphs(string text, TextShapeMarkup markup)
  {
    string[] lines = text.Length == 0 ? [string.Empty] : text.Split('\n');
    return string.Concat(lines.Select(line =>
        "<a:p>" + markup.ParagraphProperties
        + "<a:r>" + markup.RunProperties
        + "<a:t>" + Escape(line) + "</a:t></a:r></a:p>"));
  }

  /// <summary>A picture shape displaying the media part reached by the given relationship
  ///     id.</summary>
  internal static string PictureShape(int id, string embedRelationshipId)
    => "<p:pic><p:nvPicPr><p:cNvPr id=\"" + id + "\" name=\"Picture " + id + "\"/>"
       + "<p:cNvPicPr><a:picLocks noChangeAspect=\"1\"/></p:cNvPicPr><p:nvPr/></p:nvPicPr>"
       + "<p:blipFill><a:blip r:embed=\"" + embedRelationshipId + "\"/>"
       + "<a:stretch><a:fillRect/></a:stretch></p:blipFill>"
       + "<p:spPr><a:xfrm><a:off x=\"914400\" y=\"914400\"/><a:ext cx=\"914400\" cy=\"914400\"/></a:xfrm>"
       + "<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></p:spPr></p:pic>";

  /// <summary>A table graphic frame with the given cell text, row by row.</summary>
  internal static string TableShape(int id, IReadOnlyList<IReadOnlyList<string>> cells)
  {
    int cols = cells.Count == 0 ? 1 : cells[0].Count;
    string grid = string.Concat(Enumerable.Repeat("<a:gridCol w=\"304800\"/>", cols));
    string rows = string.Concat(cells.Select(row =>
        "<a:tr h=\"370840\">" + string.Concat(row.Select(cell =>
            "<a:tc><a:txBody><a:bodyPr/><a:lstStyle/>" + Paragraphs(cell) + "</a:txBody><a:tcPr/></a:tc>"))
        + "</a:tr>"));
    return GraphicFrame(id, "Table " + id, TableGraphicUri,
        "<a:tbl><a:tblPr firstRow=\"1\" bandRow=\"1\"/><a:tblGrid>" + grid + "</a:tblGrid>" + rows + "</a:tbl>");
  }

  /// <summary>A chart graphic frame referencing the chart part reached by the given
  ///     relationship id.</summary>
  internal static string ChartShape(int id, string chartRelationshipId)
    => GraphicFrame(id, "Chart " + id, ChartGraphicUri,
        "<c:chart xmlns:c=\"" + NsC + "\" xmlns:r=\"" + NsR + "\" r:id=\"" + chartRelationshipId + "\"/>");

  private static string GraphicFrame(int id, string name, string graphicUri, string payload)
    => "<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"" + id + "\" name=\"" + name + "\"/>"
       + "<p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr>"
       + "<p:xfrm><a:off x=\"914400\" y=\"1828800\"/><a:ext cx=\"3657600\" cy=\"2743200\"/></p:xfrm>"
       + "<a:graphic><a:graphicData uri=\"" + graphicUri + "\">" + payload + "</a:graphicData></a:graphic>"
       + "</p:graphicFrame>";

  /// <summary>A group shape holding two plain text boxes.</summary>
  internal static string GroupShape(int id, string firstText, string secondText)
    => "<p:grpSp><p:nvGrpSpPr><p:cNvPr id=\"" + id + "\" name=\"Group " + id + "\"/>"
       + "<p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>"
       + "<p:grpSpPr><a:xfrm><a:off x=\"914400\" y=\"914400\"/><a:ext cx=\"3657600\" cy=\"1828800\"/>"
       + "<a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"3657600\" cy=\"1828800\"/></a:xfrm></p:grpSpPr>"
       + PlainTextBox(id + 1, firstText) + PlainTextBox(id + 2, secondText)
       + "</p:grpSp>";

  /// <summary>A timing element carrying the given number of entrance effects on the given
  ///     shape id, each as its own click step. The Nth effect runs for 100N milliseconds, so a
  ///     test can tell the effects apart by duration after one of them is removed.</summary>
  internal static string Timing(int shapeId, int effects)
    => "<p:timing><p:tnLst><p:par><p:cTn id=\"1\" dur=\"indefinite\" restart=\"never\" nodeType=\"tmRoot\">"
       + "<p:childTnLst><p:seq concurrent=\"1\" nextAc=\"seek\"><p:cTn id=\"2\" dur=\"indefinite\" nodeType=\"mainSeq\">"
       + "<p:childTnLst>"
       + string.Concat(Enumerable.Range(0, effects).Select(step =>
           Effect(shapeId, 3 + (step * 4), (step + 1) * 100)))
       + "</p:childTnLst></p:cTn></p:seq></p:childTnLst></p:cTn></p:par></p:tnLst></p:timing>";

  /// <summary>One click step: the node PowerPoint waits on, the step inside it, and the
  ///     effect itself with the visibility behaviour it animates by.</summary>
  private static string Effect(int shapeId, int firstId, int duration)
  {
    string group = firstId.ToString(System.Globalization.CultureInfo.InvariantCulture);
    string step = (firstId + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
    string effect = (firstId + 2).ToString(System.Globalization.CultureInfo.InvariantCulture);
    string visibility = (firstId + 3).ToString(System.Globalization.CultureInfo.InvariantCulture);
    return "<p:par><p:cTn id=\"" + group + "\" fill=\"hold\"><p:stCondLst><p:cond delay=\"indefinite\"/></p:stCondLst>"
        + "<p:childTnLst><p:par><p:cTn id=\"" + step + "\" fill=\"hold\"><p:stCondLst><p:cond delay=\"0\"/></p:stCondLst>"
        + "<p:childTnLst><p:par><p:cTn id=\"" + effect + "\" presetID=\"10\" presetClass=\"entr\" presetSubtype=\"0\" fill=\"hold\" nodeType=\"clickEffect\" dur=\""
        + duration.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\">"
        + "<p:stCondLst><p:cond delay=\"0\"/></p:stCondLst><p:childTnLst>"
        + "<p:set><p:cBhvr><p:cTn id=\"" + visibility + "\" dur=\"1\" fill=\"hold\"><p:stCondLst><p:cond delay=\"0\"/></p:stCondLst></p:cTn>"
        + "<p:tgtEl><p:spTgt spid=\"" + shapeId + "\"/></p:tgtEl><p:attrNameLst><p:attrName>style.visibility</p:attrName></p:attrNameLst>"
        + "</p:cBhvr><p:to><p:strVal val=\"visible\"/></p:to></p:set>"
        + "</p:childTnLst></p:cTn></p:par></p:childTnLst></p:cTn></p:par></p:childTnLst></p:cTn></p:par>";
  }

  /// <summary>A notes slide part carrying the given text in its body placeholder.
  ///     </summary>
  internal static string Notes(string text)
    => Header
       + "<p:notes " + Root + ">"
       + "<p:cSld><p:spTree>" + GroupProperties
       + "<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"Notes Placeholder 2\"/>"
       + "<p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr><p:nvPr><p:ph type=\"body\" idx=\"1\"/></p:nvPr></p:nvSpPr>"
       + "<p:spPr/><p:txBody><a:bodyPr/><a:lstStyle/>" + Paragraphs(text) + "</p:txBody></p:sp>"
       + "</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:notes>";

  /// <summary>A layout part with the given display name and a title plus body
  ///     placeholder.</summary>
  internal static string Layout(string name) => name switch
  {
    "Title Slide" => KindLayout(name, "title", TitleSlidePlaceholders),
    "Title Only" => KindLayout(name, "titleOnly", TitleOnlyPlaceholders),
    _ => DefaultLayout(name),
  };

  /// <summary>A layout part of the given type, with the placeholders the given builder
  ///     states.</summary>
  private static string KindLayout(string name, string type, Func<string> placeholders)
    => Header
       + "<p:sldLayout " + Root + " type=\"" + type + "\" preserve=\"1\">"
       + "<p:cSld name=\"" + Escape(name) + "\"><p:spTree>" + GroupProperties
       + placeholders()
       + "</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>";

  /// <summary>A title slide's placeholders: a centre title and a subtitle.</summary>
  private static string TitleSlidePlaceholders()
    => Placeholder(2, "Title 1", "ctrTitle", null, null, TitleGeometry)
       + Placeholder(3, "Subtitle 2", "subTitle", "1", null, BodyGeometry);

  /// <summary>A title-only layout's placeholders: a title and nothing else.</summary>
  private static string TitleOnlyPlaceholders()
    => Placeholder(2, "Title 1", "title", null, null, TitleGeometry);

  /// <summary>A placeholder shape with the given identity, index, text and geometry.
  ///     </summary>
  private static string Placeholder(int id, string name, string? type, string? index, string? text, string geometry)
  {
    string typeAttribute = type is null ? string.Empty : " type=\"" + type + "\"";
    string indexAttribute = index is null ? string.Empty : " idx=\"" + index + "\"";
    string body = text is null ? "<a:p><a:endParaRPr lang=\"en-US\"/></a:p>" : Paragraphs(text);
    return "<p:sp><p:nvSpPr><p:cNvPr id=\"" + id + "\" name=\"" + name + "\"/>"
        + "<p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr>"
        + "<p:nvPr><p:ph" + typeAttribute + indexAttribute + "/></p:nvPr></p:nvSpPr>"
        + geometry
        + "<p:txBody><a:bodyPr/><a:lstStyle/>" + body + "</p:txBody></p:sp>";
  }

  /// <summary>The position and size a layout gives its title placeholder.</summary>
  private const string TitleGeometry =
      "<p:spPr><a:xfrm><a:off x=\"838200\" y=\"365125\"/><a:ext cx=\"10515600\" cy=\"1325563\"/></a:xfrm>"
      + "<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></p:spPr>";

  /// <summary>The position and size a layout gives its body placeholder.</summary>
  private const string BodyGeometry =
      "<p:spPr><a:xfrm><a:off x=\"838200\" y=\"1825625\"/><a:ext cx=\"10515600\" cy=\"4351338\"/></a:xfrm>"
      + "<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></p:spPr>";

  /// <summary>A slide's placeholders, matching the layout kind it is built on. The ids run
  ///     from 2 so the shape tree's own group id stays 1.</summary>
  internal static string SlidePlaceholders(string layoutName, string title, string body, bool formattedTitle)
  {
    if (layoutName == "Title Slide")
    {
      return TextShape(2, "Title 2", "ctrTitle", null, title, formattedTitle ? FormattedTitleMarkup : null)
          + TextShape(3, "Subtitle 3", "subTitle", "1", body);
    }

    string titleShape = formattedTitle ? FormattedTitleShape(2, title) : TitleShape(2, title);
    return layoutName == "Title Only" ? titleShape : titleShape + BodyShape(3, body);
  }

  /// <summary>A layout part with the given display name and a title plus body
  ///     placeholder.</summary>
  private static string DefaultLayout(string name)
    => KindLayout(name, "titleAndContent", DefaultPlaceholders);

  /// <summary>A title-and-content layout's placeholders: a title and an untyped object
  ///     placeholder at index 1, which is what PowerPoint writes for a content box.</summary>
  private static string DefaultPlaceholders()
    => Placeholder(2, "Title 1", "title", null, null, TitleGeometry)
       + Placeholder(3, "Content Placeholder 2", null, "1", null, BodyGeometry);


  /// <summary>The slide master, naming the layout relationship ids it owns.</summary>
  internal static string Master(IReadOnlyList<string> layoutRelationshipIds)
  {
    string layouts = string.Concat(layoutRelationshipIds.Select((id, index) =>
        "<p:sldLayoutId id=\"" + (2147483649 + index) + "\" r:id=\"" + id + "\"/>"));
    return Header
        + "<p:sldMaster " + Root + ">"
        + "<p:cSld><p:spTree>" + GroupProperties + "</p:spTree></p:cSld>"
        + "<p:clrMap bg1=\"lt1\" tx1=\"dk1\" bg2=\"lt2\" tx2=\"dk2\" accent1=\"accent1\" accent2=\"accent2\""
        + " accent3=\"accent3\" accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\""
        + " hlink=\"hlink\" folHlink=\"folHlink\"/>"
        + "<p:sldLayoutIdLst>" + layouts + "</p:sldLayoutIdLst>"
        + "</p:sldMaster>";
  }

  /// <summary>The presentation part: the master relationship and the slide-id list, in
  ///     the order the caller states.</summary>
  internal static string Presentation(IReadOnlyList<string> slideRelationshipIds, string masterRelationshipId)
  {
    string slides = string.Concat(slideRelationshipIds.Select((id, index) =>
        "<p:sldId id=\"" + (256 + index) + "\" r:id=\"" + id + "\"/>"));
    return Header
        + "<p:presentation " + Root + " saveSubsetFonts=\"1\">"
        + "<p:sldMasterIdLst><p:sldMasterId id=\"2147483648\" r:id=\"" + masterRelationshipId + "\"/></p:sldMasterIdLst>"
        + "<p:sldIdLst>" + slides + "</p:sldIdLst>"
        + "<p:sldSz cx=\"12192000\" cy=\"6858000\"/>"
        + "<p:notesSz cx=\"6858000\" cy=\"9144000\"/>"
        + "</p:presentation>";
  }

  /// <summary>A bar chart part: one series over two categories, with cached values.
  ///     </summary>
  internal static string Chart()
    => Header
       + "<c:chartSpace xmlns:c=\"" + NsC + "\" xmlns:a=\"" + NsA + "\" xmlns:r=\"" + NsR + "\">"
       + "<c:chart><c:plotArea><c:layout/><c:barChart><c:barDir val=\"col\"/><c:grouping val=\"clustered\"/>"
       + "<c:varyColors val=\"0\"/><c:ser><c:idx val=\"0\"/><c:order val=\"0\"/>"
       + "<c:tx><c:strRef><c:f>Sheet1!$B$1</c:f><c:strCache><c:ptCount val=\"1\"/>"
       + "<c:pt idx=\"0\"><c:v>Series 1</c:v></c:pt></c:strCache></c:strRef></c:tx>"
       + "<c:cat><c:strRef><c:f>Sheet1!$A$2:$A$3</c:f><c:strCache><c:ptCount val=\"2\"/>"
       + "<c:pt idx=\"0\"><c:v>Cat A</c:v></c:pt><c:pt idx=\"1\"><c:v>Cat B</c:v></c:pt></c:strCache></c:strRef></c:cat>"
       + "<c:val><c:numRef><c:f>Sheet1!$B$2:$B$3</c:f><c:numCache><c:formatCode>General</c:formatCode>"
       + "<c:ptCount val=\"2\"/><c:pt idx=\"0\"><c:v>1</c:v></c:pt><c:pt idx=\"1\"><c:v>2</c:v></c:pt></c:numCache></c:numRef></c:val>"
       + "</c:ser><c:axId val=\"111111111\"/><c:axId val=\"222222222\"/></c:barChart>"
       + "<c:catAx><c:axId val=\"111111111\"/><c:scaling><c:orientation val=\"minMax\"/></c:scaling>"
       + "<c:delete val=\"0\"/><c:axPos val=\"b\"/><c:crossAx val=\"222222222\"/></c:catAx>"
       + "<c:valAx><c:axId val=\"222222222\"/><c:scaling><c:orientation val=\"minMax\"/></c:scaling>"
       + "<c:delete val=\"0\"/><c:axPos val=\"l\"/><c:crossAx val=\"111111111\"/></c:valAx>"
       + "</c:plotArea><c:plotVisOnly val=\"1\"/></c:chart></c:chartSpace>";

  /// <summary>A chart part of the named family carrying the given series, categories and
  ///     values as cached data, and referencing an embedded workbook when a relationship id
  ///     is given. The family names are the ones the read reports: bar, column, line, pie
  ///     and radar.</summary>
  internal static string ChartPartXml(string family, IReadOnlyList<string> seriesNames,
      IReadOnlyList<string> categories, IReadOnlyList<IReadOnlyList<double>> values,
      string? workbookRelationshipId)
  {
    string series = string.Concat(seriesNames.Select((name, position) =>
        Series(name, position, categories, values[position])));
    return Header
        + "<c:chartSpace xmlns:c=\"" + NsC + "\" xmlns:a=\"" + NsA + "\" xmlns:r=\"" + NsR + "\">"
        + "<c:chart><c:plotArea><c:layout/>" + Family(family, series) + Axes(family)
        + "</c:plotArea><c:plotVisOnly val=\"1\"/></c:chart>"
        + ExternalData(workbookRelationshipId)
        + "</c:chartSpace>";
  }

  private static string Family(string family, string series) => family switch
  {
    "line" => "<c:lineChart><c:grouping val=\"standard\"/><c:varyColors val=\"0\"/>" + series
        + "<c:marker val=\"1\"/><c:axId val=\"111111111\"/><c:axId val=\"222222222\"/></c:lineChart>",
    "pie" => "<c:pieChart><c:varyColors val=\"1\"/>" + series + "</c:pieChart>",
    "radar" => "<c:radarChart><c:radarStyle val=\"marker\"/><c:varyColors val=\"0\"/>" + series
        + "<c:axId val=\"111111111\"/><c:axId val=\"222222222\"/></c:radarChart>",
    "bar" => "<c:barChart><c:barDir val=\"bar\"/><c:grouping val=\"clustered\"/><c:varyColors val=\"0\"/>"
        + series + "<c:axId val=\"111111111\"/><c:axId val=\"222222222\"/></c:barChart>",
    _ => "<c:barChart><c:barDir val=\"col\"/><c:grouping val=\"clustered\"/><c:varyColors val=\"0\"/>"
        + series + "<c:axId val=\"111111111\"/><c:axId val=\"222222222\"/></c:barChart>",
  };

  private static string Axes(string family) => family == "pie"
      ? string.Empty
      : "<c:catAx><c:axId val=\"111111111\"/><c:scaling><c:orientation val=\"minMax\"/></c:scaling>"
        + "<c:delete val=\"0\"/><c:axPos val=\"b\"/><c:crossAx val=\"222222222\"/></c:catAx>"
        + "<c:valAx><c:axId val=\"222222222\"/><c:scaling><c:orientation val=\"minMax\"/></c:scaling>"
        + "<c:delete val=\"0\"/><c:axPos val=\"l\"/><c:crossAx val=\"111111111\"/></c:valAx>";

  private static string Series(string name, int position, IReadOnlyList<string> categories,
      IReadOnlyList<double> values)
  {
    string column = ((char)('B' + position)).ToString();
    string last = (categories.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
    return "<c:ser><c:idx val=\"" + position + "\"/><c:order val=\"" + position + "\"/>"
        + "<c:tx><c:strRef><c:f>Sheet1!$" + column + "$1</c:f><c:strCache><c:ptCount val=\"1\"/>"
        + "<c:pt idx=\"0\"><c:v>" + name + "</c:v></c:pt></c:strCache></c:strRef></c:tx>"
        + "<c:cat><c:strRef><c:f>Sheet1!$A$2:$A$" + last + "</c:f><c:strCache>"
        + "<c:ptCount val=\"" + categories.Count + "\"/>" + Points(categories) + "</c:strCache></c:strRef></c:cat>"
        + "<c:val><c:numRef><c:f>Sheet1!$" + column + "$2:$" + column + "$" + last + "</c:f><c:numCache>"
        + "<c:formatCode>General</c:formatCode><c:ptCount val=\"" + values.Count + "\"/>"
        + NumberPoints(values) + "</c:numCache></c:numRef></c:val></c:ser>";
  }

  private static string Points(IReadOnlyList<string> points)
    => string.Concat(points.Select((point, position) =>
        "<c:pt idx=\"" + position + "\"><c:v>" + point + "</c:v></c:pt>"));

  private static string NumberPoints(IReadOnlyList<double> points)
    => string.Concat(points.Select((point, position) =>
        "<c:pt idx=\"" + position + "\"><c:v>"
        + point.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "</c:v></c:pt>"));

  private static string ExternalData(string? workbookRelationshipId)
    => workbookRelationshipId is null
        ? string.Empty
        : "<c:externalData xmlns:r=\"" + NsR + "\" r:id=\"" + workbookRelationshipId + "\">"
          + "<c:autoUpdate val=\"0\"/></c:externalData>";

  /// <summary>The theme a deck needs to open with a design rather than a blank page.
  ///     </summary>
  internal static string Theme()
    => Header
       + "<a:theme xmlns:a=\"" + NsA + "\" name=\"Office Theme\"><a:themeElements>"
       + "<a:clrScheme name=\"Office\">"
       + "<a:dk1><a:sysClr val=\"windowText\" lastClr=\"000000\"/></a:dk1>"
       + "<a:lt1><a:sysClr val=\"window\" lastClr=\"FFFFFF\"/></a:lt1>"
       + "<a:dk2><a:srgbClr val=\"44546A\"/></a:dk2><a:lt2><a:srgbClr val=\"E7E6E6\"/></a:lt2>"
       + "<a:accent1><a:srgbClr val=\"4472C4\"/></a:accent1><a:accent2><a:srgbClr val=\"ED7D31\"/></a:accent2>"
       + "<a:accent3><a:srgbClr val=\"A5A5A5\"/></a:accent3><a:accent4><a:srgbClr val=\"FFC000\"/></a:accent4>"
       + "<a:accent5><a:srgbClr val=\"5B9BD5\"/></a:accent5><a:accent6><a:srgbClr val=\"70AD47\"/></a:accent6>"
       + "<a:hlink><a:srgbClr val=\"0563C1\"/></a:hlink><a:folHlink><a:srgbClr val=\"954F72\"/></a:folHlink>"
       + "</a:clrScheme>"
       + "<a:fontScheme name=\"Office\">"
       + "<a:majorFont><a:latin typeface=\"Calibri Light\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:majorFont>"
       + "<a:minorFont><a:latin typeface=\"Calibri\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:minorFont>"
       + "</a:fontScheme>"
       + "<a:formatScheme name=\"Office\">"
       + "<a:fillStyleLst>"
       + "<a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill>"
       + "<a:gradFill rotWithShape=\"1\"><a:gsLst><a:gs pos=\"0\"><a:schemeClr val=\"phClr\"><a:tint val=\"50000\"/>"
       + "<a:satMod val=\"300000\"/></a:schemeClr></a:gs><a:gs pos=\"100000\"><a:schemeClr val=\"phClr\">"
       + "<a:shade val=\"50000\"/><a:satMod val=\"200000\"/></a:schemeClr></a:gs></a:gsLst>"
       + "<a:lin ang=\"16200000\" scaled=\"1\"/></a:gradFill>"
       + "<a:gradFill rotWithShape=\"1\"><a:gsLst><a:gs pos=\"0\"><a:schemeClr val=\"phClr\"><a:tint val=\"100000\"/>"
       + "<a:shade val=\"100000\"/><a:satMod val=\"130000\"/></a:schemeClr></a:gs><a:gs pos=\"100000\">"
       + "<a:schemeClr val=\"phClr\"><a:tint val=\"50000\"/><a:shade val=\"100000\"/><a:satMod val=\"350000\"/>"
       + "</a:schemeClr></a:gs></a:gsLst><a:lin ang=\"16200000\" scaled=\"0\"/></a:gradFill>"
       + "</a:fillStyleLst>"
       + "<a:lnStyleLst>"
       + "<a:ln w=\"6350\" cap=\"flat\" cmpd=\"sng\" algn=\"ctr\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill>"
       + "<a:prstDash val=\"solid\"/><a:miter lim=\"800000\"/></a:ln>"
       + "<a:ln w=\"12700\" cap=\"flat\" cmpd=\"sng\" algn=\"ctr\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill>"
       + "<a:prstDash val=\"solid\"/><a:miter lim=\"800000\"/></a:ln>"
       + "<a:ln w=\"19050\" cap=\"flat\" cmpd=\"sng\" algn=\"ctr\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill>"
       + "<a:prstDash val=\"solid\"/><a:miter lim=\"800000\"/></a:ln>"
       + "</a:lnStyleLst>"
       + "<a:effectStyleLst><a:effectStyle><a:effectLst/></a:effectStyle>"
       + "<a:effectStyle><a:effectLst/></a:effectStyle>"
       + "<a:effectStyle><a:effectLst><a:outerShdw blurRad=\"57150\" dist=\"19050\" dir=\"5400000\" algn=\"ctr\""
       + " rotWithShape=\"0\"><a:srgbClr val=\"000000\"><a:alpha val=\"63000\"/></a:srgbClr></a:outerShdw></a:effectLst></a:effectStyle>"
       + "</a:effectStyleLst>"
       + "<a:bgFillStyleLst><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill>"
       + "<a:solidFill><a:schemeClr val=\"phClr\"><a:tint val=\"95000\"/><a:satMod val=\"170000\"/></a:schemeClr></a:solidFill>"
       + "<a:gradFill rotWithShape=\"1\"><a:gsLst><a:gs pos=\"0\"><a:schemeClr val=\"phClr\"><a:tint val=\"93000\"/>"
       + "<a:satMod val=\"150000\"/><a:shade val=\"98000\"/><a:lumMod val=\"102000\"/></a:schemeClr></a:gs>"
       + "<a:gs pos=\"50000\"><a:schemeClr val=\"phClr\"><a:tint val=\"98000\"/><a:satMod val=\"130000\"/>"
       + "<a:shade val=\"90000\"/><a:lumMod val=\"103000\"/></a:schemeClr></a:gs>"
       + "<a:gs pos=\"100000\"><a:schemeClr val=\"phClr\"><a:shade val=\"63000\"/><a:satMod val=\"120000\"/>"
       + "</a:schemeClr></a:gs></a:gsLst><a:lin ang=\"16200000\" scaled=\"0\"/></a:gradFill></a:bgFillStyleLst>"
       + "</a:formatScheme></a:themeElements></a:theme>";

  private static string Escape(string value)
    => value.Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal);
}
