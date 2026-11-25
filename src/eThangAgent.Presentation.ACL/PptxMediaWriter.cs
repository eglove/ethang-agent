using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

// S3220/S3878 flag every OOXML element-constructor call below: each element tree is
// built by handing its children to the constructor, which is the overload the SDK
// declares for it. S1075 flags the table graphic-data URI, which is the schema's own
// namespace identifier rather than a machine-local path.
#pragma warning disable S3220, S3878, S1075
namespace eThangAgent.Presentation.ACL;

/// <summary>One image file read for an insert or a replace: its bytes and the media type
///     those bytes sniff to.</summary>
/// <param name="Bytes">The file's bytes.</param>
/// <param name="MediaType">Exactly image/png or image/jpeg.</param>
internal sealed record PptxImageSource(byte[] Bytes, string MediaType);

/// <summary>An address resolved to the slide that carries it and the element it names.
///     </summary>
/// <param name="Slide">The slide part the address belongs to.</param>
/// <param name="Address">The parsed address.</param>
/// <param name="Element">The element the address reaches, or null when it reaches none.</param>
internal sealed record PptxSlideTarget(SlidePart Slide, PptxAddress Address, OpenXmlElement? Element);

/// <summary>The media and content-insertion edits: swapping a picture's bytes, adding a
///     picture, a table and a text box, and extracting the deck's media as attachable
///     images. A picture's bytes and the relationship that reaches them are written
///     together - a media part with no relationship is a part PowerPoint cannot find -
///     and a media part another slide also displays is copied rather than overwritten, so
///     replacing one picture never changes a picture elsewhere in the deck.
///     </summary>
internal static class PptxMediaWriter
{
  /// <summary>The graphic-data namespace a table is carried in.</summary>
  private const string TableGraphicDataUri = "http://schemas.openxmlformats.org/drawingml/2006/table";

  /// <summary>Resolves one of the four media and content operations, or null when the
  ///     operation belongs to another task. The ordinal is the operation's position in the
  ///     batch, which is what keeps two insertions on one slide from sharing a key: an
  ///     inserted shape does not exist while the batch is being resolved, so its key is the
  ///     address it will take.</summary>
  /// <param name="slides">The deck's slides in presentation order.</param>
  /// <param name="operation">The operation to resolve.</param>
  /// <param name="ordinal">The operation's 0-based position in the batch.</param>
  internal static Result<PptxResolvedEdit>? Resolve(IReadOnlyList<SlidePart> slides, PptxOperation operation, int ordinal) => operation switch
  {
    ReplacePicture replace => ResolveReplacePicture(slides, replace),
    InsertPicture insert => ResolveInsertPicture(slides, insert, ordinal),
    InsertTable table => ResolveInsertTable(slides, table, ordinal),
    InsertTextBox text => ResolveInsertTextBox(slides, text, ordinal),
    _ => null,
  };

  /// <summary>Extracts the deck's media as attachable images. A name filter selects a
  ///     subset and null selects every part; a listed name the deck does not carry, and a
  ///     media part that is neither PNG nor JPEG, are both ImageNotAttachable.</summary>
  /// <param name="path">The deck's path.</param>
  /// <param name="names">The media names to return, or null for every part.</param>
  internal static Result<IReadOnlyList<ToolResultImage>> Extract(string path, IReadOnlyList<string>? names)
  {
    ArgumentNullException.ThrowIfNull(path);
    if (!File.Exists(path))
    {
      return Failure(PptxErrorCodes.FileNotFound, $"File not found: '{path}'.");
    }

    Result<string> sniff = PptxFormatSniffer.Inspect(path);
    if (!sniff.IsSuccess)
    {
      return Result.Failure<IReadOnlyList<ToolResultImage>>(sniff.Error);
    }

    try
    {
      using PresentationDocument doc = PresentationDocument.Open(path, false, new OpenSettings
      {
        MaxCharactersInPart = PptxLimits.MaxCharactersInPart,
      });
      if (doc.PresentationPart is not { } presentationPart)
      {
        return Failure(PptxErrorCodes.CorruptPresentation,
            $"The package has no readable {PptxFormatSniffer.PresentationPartName} part.");
      }

      Result<List<ImagePart>> selected = Select(PptxOutlineReader.ImageParts(presentationPart), names);
      return selected.IsSuccess
          ? Collect(selected.Value)
          : Result.Failure<IReadOnlyList<ToolResultImage>>(selected.Error);
    }
    catch (OpenXmlPackageException ex)
    {
      return Failure(PptxErrorCodes.CorruptPresentation, Corrupt(path, ex.Message));
    }
    catch (InvalidDataException ex)
    {
      return Failure(PptxErrorCodes.CorruptPresentation, Corrupt(path, ex.Message));
    }
    catch (IOException ex)
    {
      return Failure(PptxErrorCodes.CorruptPresentation, Corrupt(path, ex.Message));
    }
  }

  /// <summary>Turns the selected media parts into attachable images. A part whose bytes
  ///     are neither PNG nor JPEG is refused rather than silently skipped: the caller
  ///     asked for it by name, so a missing entry would be a lie.</summary>
  private static Result<IReadOnlyList<ToolResultImage>> Collect(List<ImagePart> parts)
  {
    List<ToolResultImage> images = [];
    foreach (ImagePart part in parts)
    {
      Result<ToolResultImage> image = Attach(part);
      if (!image.IsSuccess)
      {
        return Result.Failure<IReadOnlyList<ToolResultImage>>(image.Error);
      }

      images.Add(image.Value);
    }

    return Result.Success<IReadOnlyList<ToolResultImage>>(images);
  }

  /// <summary>One media part as an attachable image. A part that is not PNG or JPEG is
  ///     ImageNotAttachable and the message names the part and its content type.</summary>
  private static Result<ToolResultImage> Attach(ImagePart part)
  {
    byte[] bytes = Bytes(part);
    string? mediaType = ImageFormats.SniffMediaType(bytes);
    return mediaType is null
        ? Result.Failure<ToolResultImage>(new DomainError(PptxErrorCodes.ImageNotAttachable,
            $"'{part.Uri.OriginalString}' is {part.ContentType}, which cannot be attached; only PNG and JPEG media return as images."))
        : Result.Success(new ToolResultImage(mediaType, Convert.ToBase64String(bytes)));
  }

  /// <summary>The parts the requested names select. A name matches a part's package name
  ///     or its last segment, so both '/ppt/media/image3.png' and 'image3.png' address the
  ///     same part. A name the deck does not carry is ImageNotAttachable and the message
  ///     lists every name it does carry.</summary>
  private static Result<List<ImagePart>> Select(List<ImagePart> parts, IReadOnlyList<string>? names)
  {
    if (names is null || names.Count == 0)
    {
      return Result.Success(parts);
    }

    List<string> unknown = [.. names.Where(name => !parts.Any(part => Matches(part, name)))];
    if (unknown.Count > 0)
    {
      string available = string.Join(", ", parts.Select(part => part.Uri.OriginalString));
      return Result.Failure<List<ImagePart>>(new DomainError(PptxErrorCodes.ImageNotAttachable,
          $"The deck carries no media named {string.Join(", ", unknown.Select(name => "'" + name + "'"))}; its media is: {available}."));
    }

    return Result.Success<List<ImagePart>>([.. parts.Where(part => names.Any(name => Matches(part, name)))]);
  }

  private static bool Matches(ImagePart part, string name)
    => part.Uri.OriginalString.Equals(name, StringComparison.OrdinalIgnoreCase)
        || Path.GetFileName(part.Uri.OriginalString).Equals(name, StringComparison.OrdinalIgnoreCase);

  /// <summary>A media part's bytes.</summary>
  private static byte[] Bytes(ImagePart part)
  {
    using MemoryStream buffer = new();
    using (Stream stream = part.GetStream())
    {
      stream.CopyTo(buffer);
    }

    return buffer.ToArray();
  }

  /// <summary>Resolves a picture replacement: the addressed shape must be a picture whose
  ///     media relationship resolves, and the new file must be PNG or JPEG. The picture's
  ///     own properties are never touched, so position, size, crop and effects survive.
  ///     </summary>
  private static Result<PptxResolvedEdit> ResolveReplacePicture(IReadOnlyList<SlidePart> slides, ReplacePicture operation)
  {
    Result<PptxSlideTarget> target = Target(slides, operation.Address);
    if (!target.IsSuccess)
    {
      return Result.Failure<PptxResolvedEdit>(target.Error);
    }

    PptxSlideTarget resolved = target.Value;
    if (resolved.Element is not P.Picture picture || picture.BlipFill?.Blip is not { } blip
        || blip.Embed?.Value is not { } relationshipId)
    {
      return Failed(PptxErrorCodes.AddressNotFound,
          $"Address '{operation.Address}' resolves to no picture on slide {resolved.Address.Slide}.");
    }

    if (resolved.Slide.Parts.FirstOrDefault(pair => pair.RelationshipId == relationshipId).OpenXmlPart is not ImagePart existing)
    {
      return Failed(PptxErrorCodes.AddressNotFound,
          $"Address '{operation.Address}' names a picture whose media part the deck does not carry.");
    }

    Result<PptxImageSource> source = Read(operation.ImagePath);
    if (!source.IsSuccess)
    {
      return Result.Failure<PptxResolvedEdit>(source.Error);
    }

    PptxImageSource image = source.Value;
    return Result.Success(new PptxResolvedEdit(resolved.Address.Render(),
        () => ReplaceBytes(resolved.Slide, blip, existing, relationshipId, image)));
  }

  /// <summary>Writes the new bytes behind a picture. A media part another part also
  ///     displays is left alone and a fresh part is written instead, so a picture on
  ///     another slide keeps its bytes; the relationship is repointed and the old one is
  ///     dropped when this slide no longer uses it. A part whose content type differs from
  ///     the new bytes is replaced rather than overwritten, because the part's extension
  ///     and content type are what PowerPoint reads the format from.</summary>
  private static void ReplaceBytes(SlidePart slide, A.Blip blip, ImagePart existing, string relationshipId,
      PptxImageSource source)
  {
    bool shared = IsShared(slide, relationshipId, existing);
    if (!shared && existing.ContentType == source.MediaType)
    {
      existing.FeedData(new MemoryStream(source.Bytes));
      return;
    }

    ImagePart fresh = slide.AddImagePart(PartTypeOf(source.MediaType));
    fresh.FeedData(new MemoryStream(source.Bytes));
    blip.Embed = slide.GetIdOfPart(fresh);
    if (!shared && !Uses(slide, relationshipId))
    {
      _ = slide.DeletePart(existing);
    }
  }

  /// <summary>True when the media part is displayed from more than one place: another
  ///     part references it, or another picture on this slide shares its relationship.
  ///     </summary>
  private static bool IsShared(SlidePart slide, string relationshipId, ImagePart part)
    => part.GetParentParts().Skip(1).Any() || PicturesUsing(slide, relationshipId) > 1;

  /// <summary>Whether any picture on the slide still displays the given media
  ///     relationship.</summary>
  private static bool Uses(SlidePart slide, string relationshipId)
    => PicturesUsing(slide, relationshipId) > 0;

  /// <summary>How many pictures on the slide display the given media relationship.
  ///     </summary>
  private static int PicturesUsing(SlidePart slide, string relationshipId)
    => slide.Slide?.Descendants<A.Blip>().Count(candidate => candidate.Embed?.Value == relationshipId) ?? 0;

  /// <summary>Resolves a picture insertion: the file must be PNG or JPEG and the slide
  ///     must have a shape tree for the picture to join.</summary>
  private static Result<PptxResolvedEdit> ResolveInsertPicture(IReadOnlyList<SlidePart> slides, InsertPicture operation, int ordinal)
  {
    Result<SlidePart> slide = SlideAt(slides, operation.Slide);
    if (!slide.IsSuccess)
    {
      return Result.Failure<PptxResolvedEdit>(slide.Error);
    }

    Result<bool> sized = Sized(operation.Width, operation.Height);
    if (!sized.IsSuccess)
    {
      return Result.Failure<PptxResolvedEdit>(sized.Error);
    }

    Result<PptxImageSource> source = Read(operation.ImagePath);
    if (!source.IsSuccess)
    {
      return Result.Failure<PptxResolvedEdit>(source.Error);
    }

    PptxImageSource image = source.Value;
    Result<string> key = AppendedKey(slide.Value, operation.Slide, ordinal);
    return key.IsSuccess
        ? Result.Success(new PptxResolvedEdit(key.Value, () => AddPicture(slide.Value, operation, image)))
        : Result.Failure<PptxResolvedEdit>(key.Error);
  }

  /// <summary>Resolves a table insertion: the cell grid must be Rows by Cols, and Rows and
  ///     Cols must be positive. A grid that does not match is InvalidAddress.</summary>
  private static Result<PptxResolvedEdit> ResolveInsertTable(IReadOnlyList<SlidePart> slides, InsertTable operation, int ordinal)
  {
    Result<SlidePart> slide = SlideAt(slides, operation.Slide);
    if (!slide.IsSuccess)
    {
      return Result.Failure<PptxResolvedEdit>(slide.Error);
    }

    Result<bool> sized = Sized(operation.Width, operation.Height);
    if (!sized.IsSuccess)
    {
      return Result.Failure<PptxResolvedEdit>(sized.Error);
    }

    Result<string> grid = Grid(operation);
    if (!grid.IsSuccess)
    {
      return Failed(PptxErrorCodes.InvalidAddress, grid.Error.Message);
    }

    Result<string> key = AppendedKey(slide.Value, operation.Slide, ordinal);
    return key.IsSuccess
        ? Result.Success(new PptxResolvedEdit(key.Value, () => AddTable(slide.Value, operation)))
        : Result.Failure<PptxResolvedEdit>(key.Error);
  }

  /// <summary>Resolves a text-box insertion. The text is written as one paragraph per
  ///     line, and an empty text is one empty paragraph.</summary>
  private static Result<PptxResolvedEdit> ResolveInsertTextBox(IReadOnlyList<SlidePart> slides, InsertTextBox operation, int ordinal)
  {
    Result<SlidePart> slide = SlideAt(slides, operation.Slide);
    if (!slide.IsSuccess)
    {
      return Result.Failure<PptxResolvedEdit>(slide.Error);
    }

    Result<bool> sized = Sized(operation.Width, operation.Height);
    if (!sized.IsSuccess)
    {
      return Result.Failure<PptxResolvedEdit>(sized.Error);
    }

    Result<string> key = AppendedKey(slide.Value, operation.Slide, ordinal);
    return key.IsSuccess
        ? Result.Success(new PptxResolvedEdit(key.Value, () => AddTextBox(slide.Value, operation)))
        : Result.Failure<PptxResolvedEdit>(key.Error);
  }

  /// <summary>A cell grid that does not hold Rows rows of Cols cells is refused with the
  ///     dimensions the operation stated and the dimensions its cells actually have.
  ///     </summary>
  private static Result<string> Grid(InsertTable operation)
  {
    if (operation.Rows < 1 || operation.Cols < 1)
    {
      return Result.Failure<string>(new DomainError(PptxErrorCodes.InvalidAddress,
          $"A table needs at least one row and one column; the operation stated {operation.Rows} by {operation.Cols}."));
    }

    int rows = operation.Cells.Count;
    int ragged = operation.Cells.Count(row => row.Count != operation.Cols);
    if (rows == operation.Rows && ragged == 0)
    {
      return Result.Success(string.Empty);
    }

    string width = ragged == 0 ? string.Empty : $", {ragged} of them not {operation.Cols} wide";
    return Result.Failure<string>(new DomainError(PptxErrorCodes.InvalidAddress,
        $"The cells do not match the table's dimensions: {operation.Rows} row(s) of {operation.Cols} cell(s) were stated, and the cells hold {rows} row(s){width}."));
  }

  /// <summary>The width and height an insertion needs: both must be positive, because a
  ///     shape with no extent is invisible on the slide.</summary>
  private static Result<bool> Sized(long width, long height)
    => width > 0 && height > 0
        ? Result.Success(true)
        : Result.Failure<bool>(new DomainError(ToolErrorCodes.InvalidParameterValue,
            $"A shape needs a positive width and height in English Metric Units; {width} by {height} was stated."));

  /// <summary>The address an appended shape takes: the slide's shape tree gains it after
  ///     every shape already there, offset by the operation's position so two insertions in
  ///     one batch count as two changed shapes.</summary>
  private static Result<string> AppendedKey(SlidePart slide, int number, int ordinal)
  {
    P.ShapeTree? tree = slide.Slide?.CommonSlideData?.ShapeTree;
    return tree is null
        ? Result.Failure<string>(new DomainError(PptxErrorCodes.CorruptPresentation,
            $"Slide {number} has no shape tree to add a shape to."))
        : Result.Success(PptxAddress.ForShape(number, [PptxShapeWalker.TopLevelShapes(slide).Count + 1 + ordinal]).Render());
  }

  /// <summary>Reads an image file for an insert or a replace. A missing file is
  ///     FileNotFound; bytes that are neither PNG nor JPEG are ImageNotAttachable.
  ///     </summary>
  private static Result<PptxImageSource> Read(string path)
  {
    ArgumentNullException.ThrowIfNull(path);
    if (!File.Exists(path))
    {
      return Result.Failure<PptxImageSource>(new DomainError(PptxErrorCodes.FileNotFound, $"Image not found: '{path}'."));
    }

    byte[] bytes = File.ReadAllBytes(path);
    string? mediaType = ImageFormats.SniffMediaType(bytes);
    return mediaType is null
        ? Result.Failure<PptxImageSource>(new DomainError(PptxErrorCodes.ImageNotAttachable,
            $"'{path}' is not a PNG or JPEG image; a picture must be one of those two formats."))
        : Result.Success(new PptxImageSource(bytes, mediaType));
  }

  private static void AddPicture(SlidePart slide, InsertPicture operation, PptxImageSource source)
  {
    P.ShapeTree tree = TreeOf(slide);
    ImagePart part = slide.AddImagePart(PartTypeOf(source.MediaType));
    part.FeedData(new MemoryStream(source.Bytes));
    tree.Append(Picture(NextShapeId(tree), slide.GetIdOfPart(part), operation));
  }

  private static void AddTable(SlidePart slide, InsertTable operation)
  {
    P.ShapeTree tree = TreeOf(slide);
    tree.Append(TableFrame(NextShapeId(tree), operation));
  }

  private static void AddTextBox(SlidePart slide, InsertTextBox operation)
  {
    P.ShapeTree tree = TreeOf(slide);
    tree.Append(TextBox(NextShapeId(tree), operation));
  }

  private static P.ShapeTree TreeOf(SlidePart slide)
    => slide.Slide?.CommonSlideData?.ShapeTree
        ?? throw new InvalidOperationException("the slide has no shape tree; the insertion was resolved against one that existed");

  /// <summary>A picture shape: the media relationship it displays and the offset and size
  ///     it occupies, in English Metric Units.</summary>
  private static P.Picture Picture(uint id, string relationshipId, InsertPicture operation)
    => new(
    [
      new P.NonVisualPictureProperties(
      [
        new P.NonVisualDrawingProperties { Id = id, Name = "Picture " + Name(id) },
        new P.NonVisualPictureDrawingProperties(new A.PictureLocks { NoChangeAspect = true }),
        new P.ApplicationNonVisualDrawingProperties(),
      ]),
      new P.BlipFill(new A.Blip { Embed = relationshipId }, new A.Stretch(new A.FillRectangle())),
      new P.ShapeProperties(
      [
        Transform(operation.X, operation.Y, operation.Width, operation.Height),
        new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle },
      ]),
    ]);

  /// <summary>A text box: an explicit rectangle with no fill, holding one paragraph per
  ///     line of the text.</summary>
  private static P.Shape TextBox(uint id, InsertTextBox operation)
    => new(
    [
      new P.NonVisualShapeProperties(
      [
        new P.NonVisualDrawingProperties { Id = id, Name = "TextBox " + Name(id) },
        new P.NonVisualShapeDrawingProperties(new A.ShapeLocks { NoGrouping = true }) { TextBox = true },
        new P.ApplicationNonVisualDrawingProperties(),
      ]),
      new P.ShapeProperties(
      [
        Transform(operation.X, operation.Y, operation.Width, operation.Height),
        new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle },
        new A.NoFill(),
      ]),
      new P.TextBody(
      [
        new A.BodyProperties { Wrap = A.TextWrappingValues.Square },
        new A.ListStyle(),
        .. Paragraphs(operation.Text),
      ]),
    ]);

  /// <summary>A table graphic frame: the grid's columns share the frame's width and its
  ///     rows share the frame's height, so the table fills the geometry it was given.
  ///     </summary>
  private static P.GraphicFrame TableFrame(uint id, InsertTable operation)
    => new(
    [
      new P.NonVisualGraphicFrameProperties(
      [
        new P.NonVisualDrawingProperties { Id = id, Name = "Table " + Name(id) },
        new P.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoGrouping = true }),
        new P.ApplicationNonVisualDrawingProperties(),
      ]),
      new P.Transform(
      [
        new A.Offset { X = operation.X, Y = operation.Y },
        new A.Extents { Cx = operation.Width, Cy = operation.Height },
      ]),
      new A.Graphic(new A.GraphicData(Table(operation)) { Uri = TableGraphicDataUri }),
    ]);

  private static A.Table Table(InsertTable operation)
  {
    A.TableGrid grid = new();
    for (int column = 0; column < operation.Cols; column++)
    {
      grid.Append(new A.GridColumn { Width = operation.Width / operation.Cols });
    }

    A.Table table = new(new A.TableProperties { FirstRow = true, BandRow = true }, grid);
    foreach (IReadOnlyList<string> row in operation.Cells)
    {
      table.Append(TableRow(row, operation.Height / operation.Rows));
    }

    return table;
  }

  private static A.TableRow TableRow(IReadOnlyList<string> cells, long height)
  {
    A.TableRow row = new() { Height = height };
    foreach (string cell in cells)
    {
      row.Append(Cell(cell));
    }

    return row;
  }

  private static A.TableCell Cell(string text)
    => new(
    [
      new A.TextBody(
      [
        new A.BodyProperties(),
        new A.ListStyle(),
        .. Paragraphs(text),
      ]),
      new A.TableCellProperties(),
    ]);

  /// <summary>The paragraphs a text is written as: one per line, and an empty text is one
  ///     empty paragraph.</summary>
  private static A.Paragraph[] Paragraphs(string text)
    => [.. (text.Length == 0 ? [string.Empty] : text.Split('\n')).Select(Paragraph)];

  private static A.Paragraph Paragraph(string line)
  {
    A.Paragraph paragraph = new();
    if (line.Length > 0)
    {
      paragraph.Append(new A.Run(new A.Text(line)));
    }

    return paragraph;
  }

  private static A.Transform2D Transform(long x, long y, long width, long height)
    => new(new A.Offset { X = x, Y = y }, new A.Extents { Cx = width, Cy = height });

  /// <summary>The image part type the sniffed media type writes as: the part's extension
  ///     and content type both follow it.</summary>
  private static PartTypeInfo PartTypeOf(string mediaType)
    => mediaType == "image/png" ? ImagePartType.Png : ImagePartType.Jpeg;

  private static string Name(uint id) => id.ToString(CultureInfo.InvariantCulture);

  private static uint NextShapeId(P.ShapeTree tree)
    => tree.Descendants<P.NonVisualDrawingProperties>().Select(properties => properties.Id?.Value ?? 0U).DefaultIfEmpty(1U).Max() + 1U;

  /// <summary>The slide part an address names, or the refusal that stops the operation.
  ///     </summary>
  private static Result<SlidePart> SlideAt(IReadOnlyList<SlidePart> slides, int slide)
    => slide >= 1 && slide <= slides.Count
        ? Result.Success(slides[slide - 1])
        : Result.Failure<SlidePart>(new DomainError(PptxErrorCodes.SlideNotFound,
            $"Slide {slide} does not exist: the deck has {slides.Count} slide(s), numbered 1 to {slides.Count}."));

  /// <summary>The slide and element an address reaches. An address that does not parse is
  ///     InvalidAddress, one naming a slide the deck does not have is SlideNotFound, and
  ///     one that does not name a shape is InvalidAddress.</summary>
  private static Result<PptxSlideTarget> Target(IReadOnlyList<SlidePart> slides, string address)
  {
    Result<PptxAddress> parsed = PptxAddress.Parse(address);
    if (!parsed.IsSuccess)
    {
      return Result.Failure<PptxSlideTarget>(parsed.Error);
    }

    if (parsed.Value.Kind != PptxAddressKind.Shape)
    {
      return Result.Failure<PptxSlideTarget>(new DomainError(PptxErrorCodes.InvalidAddress,
          $"Address '{address}' does not name a shape; a picture is addressed as s<N>.sh<M>."));
    }

    Result<SlidePart> slide = SlideAt(slides, parsed.Value.Slide);
    return slide.IsSuccess
        ? Result.Success(new PptxSlideTarget(slide.Value, parsed.Value,
            PptxShapeWalker.ElementAt(slide.Value, parsed.Value.ShapePath)))
        : Result.Failure<PptxSlideTarget>(slide.Error);
  }

  private static string Corrupt(string path, string message)
    => $"'{path}' could not be opened as a presentation: {Clamp(message)}";

  private static string Clamp(string message)
  {
    const int cap = 300;
    return message.Length <= cap ? message : message[..cap] + "...";
  }

  private static Result<PptxResolvedEdit> Failed(string code, string message)
    => Result.Failure<PptxResolvedEdit>(new DomainError(code, message));

  private static Result<IReadOnlyList<ToolResultImage>> Failure(string code, string message)
    => Result.Failure<IReadOnlyList<ToolResultImage>>(new DomainError(code, message));
}
