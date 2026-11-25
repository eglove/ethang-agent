using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using eThangAgent.ToolDomain;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace eThangAgent.Presentation.ACL;

/// <summary>The families a placeholder is matched across when a slide changes layout.
///     PowerPoint matches a placeholder by its family and its index, never by name: one
///     layout can carry two body placeholders, so a name is not an identity.
///     </summary>
internal enum PptxPlaceholderFamily
{
  /// <summary>A title or centre-title placeholder.</summary>
  Title,

  /// <summary>A body, subtitle or object placeholder: the shape that holds prose.</summary>
  Body,

  /// <summary>A picture placeholder.</summary>
  Picture,

  /// <summary>A chart, table, clip-art, diagram or media placeholder.</summary>
  Content,

  /// <summary>A date, slide-number, footer or header placeholder. These are the layout's
  ///     own decorations rather than the slide's content, so one is left in place when the
  ///     target layout has no counterpart for it.</summary>
  Decoration,
}

/// <summary>One placeholder a target layout offers: the identity a slide shape is matched
///     by, the geometry a matched shape adopts, and the placeholder declaration the layout
///     states. <paramref name="Type"/> is null when the layout omits the type attribute,
///     which the schema reads as an object placeholder.</summary>
internal sealed record PptxPlaceholderTarget(PptxPlaceholderFamily Family, int Index,
    string Name, A.Transform2D? Transform, P.PlaceholderValues? Type);

/// <summary>What one slide shape becomes under a target layout.</summary>
internal sealed record PptxLayoutMapping(int Index, string Address, bool IsPlaceholder,
    PptxPlaceholderFamily Family, PptxPlaceholderTarget? Target)
{
  /// <summary>Whether this shape would be left with no position on the slide: a content
  ///     placeholder the target layout has no counterpart for. A shape that is not a
  ///     placeholder, and a decoration placeholder, keep their own geometry and are never
  ///     stranded.</summary>
  internal bool IsStranded => IsPlaceholder && Target is null && Family != PptxPlaceholderFamily.Decoration;
}

/// <summary>Maps a slide's shapes onto a target layout's placeholders. A slide shape that
///     is not a placeholder keeps its own position and stays visible; a placeholder is
///     matched to the target placeholder of the same family and index, and a content
///     placeholder with no counterpart is reported as stranded rather than dropped.
///     </summary>
internal static class PptxLayoutMapper
{
  /// <summary>Every placeholder type the schema declares, with the family it is matched in.
  ///     The order is the order a match is looked up in; each type appears once.</summary>
  private static readonly (P.PlaceholderValues Type, PptxPlaceholderFamily Family)[] Families =
  [
    (P.PlaceholderValues.Title, PptxPlaceholderFamily.Title),
    (P.PlaceholderValues.CenteredTitle, PptxPlaceholderFamily.Title),
    (P.PlaceholderValues.Body, PptxPlaceholderFamily.Body),
    (P.PlaceholderValues.SubTitle, PptxPlaceholderFamily.Body),
    (P.PlaceholderValues.Object, PptxPlaceholderFamily.Body),
    (P.PlaceholderValues.Picture, PptxPlaceholderFamily.Picture),
    (P.PlaceholderValues.Chart, PptxPlaceholderFamily.Content),
    (P.PlaceholderValues.Table, PptxPlaceholderFamily.Content),
    (P.PlaceholderValues.ClipArt, PptxPlaceholderFamily.Content),
    (P.PlaceholderValues.Diagram, PptxPlaceholderFamily.Content),
    (P.PlaceholderValues.Media, PptxPlaceholderFamily.Content),
    (P.PlaceholderValues.SlideImage, PptxPlaceholderFamily.Content),
    (P.PlaceholderValues.DateAndTime, PptxPlaceholderFamily.Decoration),
    (P.PlaceholderValues.SlideNumber, PptxPlaceholderFamily.Decoration),
    (P.PlaceholderValues.Footer, PptxPlaceholderFamily.Decoration),
    (P.PlaceholderValues.Header, PptxPlaceholderFamily.Decoration),
  ];

  /// <summary>Reports, for every top-level shape of the slide in document order, the
  ///     placeholder it maps to under the target layout. Nothing is mutated.</summary>
  /// <param name="slidePart">The slide whose shapes are mapped.</param>
  /// <param name="slide">The slide's 1-based number, used to build every address.</param>
  /// <param name="targetLayout">The layout the slide would move onto.</param>
  internal static IReadOnlyList<PptxLayoutMapping> Map(SlidePart slidePart, int slide,
      SlideLayoutPart targetLayout)
  {
    ArgumentNullException.ThrowIfNull(slidePart);
    ArgumentNullException.ThrowIfNull(targetLayout);
    List<PptxPlaceholderTarget> targets = Placeholders(targetLayout);
    List<OpenXmlElement> shapes = PptxShapeWalker.TopLevelShapes(slidePart);
    return [.. shapes.Select((shape, position) => MapOne(shape, position + 1, slide, targets))];
  }

  /// <summary>The placeholders a layout offers, in document order. A layout with no shape
  ///     tree offers none.</summary>
  /// <param name="layout">The layout to read.</param>
  internal static List<PptxPlaceholderTarget> Placeholders(SlideLayoutPart layout)
  {
    ArgumentNullException.ThrowIfNull(layout);
    P.ShapeTree? tree = layout.SlideLayout?.CommonSlideData?.ShapeTree;
    return tree is null ? [] : [.. tree.Elements<P.Shape>().Select(Target).OfType<PptxPlaceholderTarget>()];
  }

  /// <summary>Moves the slide's content onto the target layout: every mapped shape adopts
  ///     the target placeholder's identity and geometry, and the slide's layout
  ///     relationship is re-pointed. Shapes that do not map are not touched.</summary>
  /// <param name="slidePart">The slide to change.</param>
  /// <param name="targetLayout">The layout the slide moves onto.</param>
  /// <param name="mappings">The mapping this class produced for the slide.</param>
  internal static void ApplyLayoutChange(SlidePart slidePart, SlideLayoutPart targetLayout,
      IReadOnlyList<PptxLayoutMapping> mappings)
  {
    ArgumentNullException.ThrowIfNull(slidePart);
    ArgumentNullException.ThrowIfNull(targetLayout);
    ArgumentNullException.ThrowIfNull(mappings);
    List<OpenXmlElement> shapes = PptxShapeWalker.TopLevelShapes(slidePart);
    foreach (PptxLayoutMapping mapping in mappings.Where(mapping => mapping.Target is not null && mapping.Index <= shapes.Count))
    {
      OpenXmlElement shape = shapes[mapping.Index - 1];
      AdoptIdentity(shape, mapping.Target!);
      AdoptGeometry(shape, mapping.Target!.Transform);
    }

    Rebind(slidePart, targetLayout);
  }

  /// <summary>The placeholder family a declared placeholder type belongs to. A placeholder
  ///     that states no type is an object placeholder, which is the body family.
  ///     </summary>
  /// <param name="type">The declared placeholder type.</param>
  internal static PptxPlaceholderFamily FamilyOf(P.PlaceholderValues type)
    => Families.FirstOrDefault(entry => entry.Type == type).Family;

  /// <summary>The placeholder declaration a shape carries, or null when the shape is not a
  ///     placeholder. A group is never a placeholder, so its children are not consulted.
  ///     </summary>
  /// <param name="shape">The shape to read.</param>
  internal static P.PlaceholderShape? PlaceholderOf(OpenXmlElement shape)
    => NonVisual(shape)?.PlaceholderShape;

  /// <summary>A shape's non-visual drawing properties, which carry its placeholder
  ///     declaration. A group carries none, so a group is never a placeholder.</summary>
  private static P.ApplicationNonVisualDrawingProperties? NonVisual(OpenXmlElement shape) => shape switch
  {
    P.Shape text => text.NonVisualShapeProperties?.ApplicationNonVisualDrawingProperties,
    P.Picture picture => picture.NonVisualPictureProperties?.ApplicationNonVisualDrawingProperties,
    P.GraphicFrame frame => frame.NonVisualGraphicFrameProperties?.ApplicationNonVisualDrawingProperties,
    P.ConnectionShape connection => connection.NonVisualConnectionShapeProperties?.ApplicationNonVisualDrawingProperties,
    _ => null,
  };

  private static PptxLayoutMapping MapOne(OpenXmlElement shape, int index, int slide,
      IReadOnlyList<PptxPlaceholderTarget> targets)
  {
    string address = PptxAddress.ForShape(slide, [index]).Render();
    P.PlaceholderShape? placeholder = PlaceholderOf(shape);
    return placeholder is null
        ? new PptxLayoutMapping(index, address, false, PptxPlaceholderFamily.Content, null)
        : Mapped(index, address, placeholder, targets);
  }

  private static PptxLayoutMapping Mapped(int index, string address, P.PlaceholderShape placeholder,
      IReadOnlyList<PptxPlaceholderTarget> targets)
  {
    PptxPlaceholderFamily family = FamilyOf(placeholder.Type?.Value ?? P.PlaceholderValues.Object);
    int placeholderIndex = PlaceholderIndex(placeholder);
    PptxPlaceholderTarget? target = targets.FirstOrDefault(candidate =>
        candidate.Family == family && candidate.Index == placeholderIndex);
    return new PptxLayoutMapping(index, address, true, family, target);
  }

  /// <summary>A placeholder's index. An omitted index is 0, which is the index the schema
  ///     gives a placeholder that does not state one.</summary>
  private static int PlaceholderIndex(P.PlaceholderShape placeholder)
    => (int)(placeholder.Index?.Value ?? 0U);

  /// <summary>A layout placeholder as a match target, or null when the shape is not a
  ///     placeholder.</summary>
  private static PptxPlaceholderTarget? Target(P.Shape shape)
  {
    P.PlaceholderShape? placeholder = shape.NonVisualShapeProperties
        ?.ApplicationNonVisualDrawingProperties?.PlaceholderShape;
    return placeholder is null ? null : Target(shape, placeholder);
  }

  private static PptxPlaceholderTarget Target(P.Shape shape, P.PlaceholderShape placeholder)
    => new(FamilyOf(placeholder.Type?.Value ?? P.PlaceholderValues.Object),
        PlaceholderIndex(placeholder),
        shape.NonVisualShapeProperties?.NonVisualDrawingProperties?.Name?.Value ?? string.Empty,
        shape.ShapeProperties?.Transform2D?.CloneNode(true) as A.Transform2D,
        placeholder.Type?.Value);

  /// <summary>Gives a shape the target placeholder's identity. The declared type is written
  ///     only when the layout states one, so a shape keeps its own family spelling when the
  ///     layout's placeholder is the untyped object form.</summary>
  private static void AdoptIdentity(OpenXmlElement shape, PptxPlaceholderTarget target)
  {
    P.PlaceholderShape? placeholder = PlaceholderOf(shape);
    if (placeholder is null)
    {
      return;
    }

    AdoptType(placeholder, target.Type);
    placeholder.Index = target.Index == 0 ? null : (uint)target.Index;
  }

  private static void AdoptType(P.PlaceholderShape placeholder, P.PlaceholderValues? declared)
  {
    if (declared is { } type)
    {
      placeholder.Type = type;
    }
  }

  /// <summary>Gives a shape the target placeholder's position and size. A shape with no
  ///     geometry of its own is left alone rather than given a position the layout does not
  ///     state.</summary>
  private static void AdoptGeometry(OpenXmlElement shape, A.Transform2D? transform)
  {
    if (transform is null)
    {
      return;
    }

    if (shape is P.GraphicFrame frame)
    {
      frame.Transform = FrameTransform(transform);
      return;
    }

    ShapePropertiesOf(shape)?.Transform2D = transform.CloneNode(true) as A.Transform2D;
  }

  private static P.ShapeProperties? ShapePropertiesOf(OpenXmlElement shape) => shape switch
  {
    P.Shape text => text.ShapeProperties,
    P.Picture picture => picture.ShapeProperties,
    P.ConnectionShape connection => connection.ShapeProperties,
    _ => null,
  };

  /// <summary>A graphic frame carries its geometry as p:xfrm rather than a:xfrm, so the
  ///     layout's a:xfrm is restated in the frame's own element.</summary>
  private static P.Transform FrameTransform(A.Transform2D transform)
  {
    P.Transform frame = new();
    if (transform.Offset is { } offset)
    {
      frame.Offset = new A.Offset { X = offset.X?.Value, Y = offset.Y?.Value };
    }

    if (transform.Extents is { } extents)
    {
      frame.Extents = new A.Extents { Cx = extents.Cx?.Value, Cy = extents.Cy?.Value };
    }

    return frame;
  }

  /// <summary>Re-points the slide's layout relationship. A slide part carries exactly one
  ///     layout relationship, so the old one is removed before the new one is added; the
  ///     old layout part itself stays in the master, where any other slide still uses it.
  ///     </summary>
  private static void Rebind(SlidePart slidePart, SlideLayoutPart targetLayout)
  {
    SlideLayoutPart? current = slidePart.SlideLayoutPart;
    if (current is not null && current.Uri.OriginalString == targetLayout.Uri.OriginalString)
    {
      return;
    }

    if (current is not null)
    {
      _ = slidePart.DeletePart(current);
    }

    _ = slidePart.AddPart(targetLayout);
  }
}
