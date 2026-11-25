using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using eThangAgent.ToolDomain;
using A = DocumentFormat.OpenXml.Drawing;
using C = DocumentFormat.OpenXml.Office2016.Drawing.Command;
using P = DocumentFormat.OpenXml.Presentation;
using P188 = DocumentFormat.OpenXml.Office2021.PowerPoint.Comment;

// S3220/S3878 flag every OOXML element-constructor call below: each element tree is
// built by handing its children to the constructor, which is the overload the SDK
// declares for it.
#pragma warning disable S3220, S3878
namespace eThangAgent.Presentation.ACL;

/// <summary>Reads, adds and deletes a deck's comments across both shapes PowerPoint has
///     written them in: the legacy per-slide comment part (p:cmLst, whose authors resolve
///     through the presentation's legacy author list) and the current comment part
///     (p188:cmLst, whose authors resolve through the presentation's current author
///     list). A deck carrying both lists all of them; a comment is always WRITTEN in the
///     current shape, and a delete removes one from whichever shape holds it.</summary>
internal static class PptxCommentReader
{
  /// <summary>The element a current comment names its target shape with.</summary>
  private const string ShapeMoniker = "spMk";

  /// <summary>The attribute a legacy comment names its target shape with.</summary>
  private const string LegacyShapeId = "spid";

  /// <summary>The attribute a shape moniker carries the drawing id in.</summary>
  private const string MonikerId = "id";

  /// <summary>The slide's comments: legacy comments first, then current ones, each with
  ///     the address of the shape it anchors to. Ids are the comment's 1-based position
  ///     in the deck-wide list, so an id names one comment unambiguously across both
  ///     shapes.</summary>
  /// <param name="presentationPart">The presentation part, which owns the author lists.</param>
  /// <param name="slidePart">The slide part whose comments are read.</param>
  /// <param name="slide">The slide's 1-based number.</param>
  /// <param name="addressesByShapeId">The address of every shape that carries a drawing id.</param>
  /// <param name="idOffset">How many comments the deck's earlier slides carry.</param>
  internal static IReadOnlyList<PptxCommentInfo> Read(PresentationPart presentationPart, SlidePart slidePart,
      int slide, IReadOnlyDictionary<uint, string> addressesByShapeId, int idOffset)
  {
    ArgumentNullException.ThrowIfNull(presentationPart);
    ArgumentNullException.ThrowIfNull(slidePart);
    ArgumentNullException.ThrowIfNull(addressesByShapeId);
    Anchor anchor = new(slide, addressesByShapeId);
    List<PptxCommentInfo> comments = [];
    int id = idOffset;
    Dictionary<uint, string> legacyAuthors = LegacyAuthors(presentationPart);
    foreach (P.Comment comment in LegacyComments(slidePart))
    {
      id++;
      comments.Add(new PptxCommentInfo(
          id,
          legacyAuthors.GetValueOrDefault(comment.AuthorId?.Value ?? 0, string.Empty),
          comment.Text?.InnerText ?? string.Empty,
          anchor.Of(LegacyShapeIdIn(comment)),
          true));
    }

    Dictionary<string, string> currentAuthors = CurrentAuthors(presentationPart);
    foreach (P188.Comment comment in CurrentComments(slidePart))
    {
      id++;
      string authorId = comment.AuthorId?.InnerText ?? string.Empty;
      comments.Add(new PptxCommentInfo(
          id,
          currentAuthors.GetValueOrDefault(authorId, authorId),
          TextOf(comment.GetFirstChild<P188.TextBodyType>()),
          anchor.Of(MonikerShapeId(comment)),
          false));
    }

    return comments;
  }

  /// <summary>How many comments one slide carries, across both shapes.</summary>
  /// <param name="slidePart">The slide part whose comment parts are counted.</param>
  internal static int Count(SlidePart slidePart)
  {
    ArgumentNullException.ThrowIfNull(slidePart);
    return LegacyComments(slidePart).Count + CurrentComments(slidePart).Count;
  }

  /// <summary>How many comments the slides before <paramref name="slide"/> carry, which
  ///     is the offset the slide's own ids start at.</summary>
  /// <param name="slides">The deck's slides in presentation order.</param>
  /// <param name="slide">The slide's 1-based number.</param>
  internal static int Offset(IReadOnlyList<SlidePart> slides, int slide)
  {
    ArgumentNullException.ThrowIfNull(slides);
    int offset = 0;
    for (int index = 0; index < slide - 1 && index < slides.Count; index++)
    {
      offset += Count(slides[index]);
    }

    return offset;
  }

  /// <summary>Appends a comment in the current shape, anchored at the shape that carries
  ///     <paramref name="targetShapeId"/>. The deck's author list gains the author when it
  ///     does not already carry it, and the slide gains a current comment part when it has
  ///     none. Legacy comments on the slide are left exactly as they are.</summary>
  /// <param name="presentationPart">The presentation part, which owns the author list.</param>
  /// <param name="slidePart">The slide part the comment is attached to.</param>
  /// <param name="targetShapeId">The drawing id of the shape the comment anchors at.</param>
  /// <param name="author">The comment's author.</param>
  /// <param name="text">The comment's text.</param>
  internal static void Add(PresentationPart presentationPart, SlidePart slidePart, uint targetShapeId,
      string author, string text)
  {
    ArgumentNullException.ThrowIfNull(presentationPart);
    ArgumentNullException.ThrowIfNull(slidePart);
    ArgumentNullException.ThrowIfNull(author);
    ArgumentNullException.ThrowIfNull(text);
    string authorId = AuthorId(presentationPart, author);
    PowerPointCommentPart part = CurrentPart(slidePart) ?? slidePart.AddNewPart<PowerPointCommentPart>();
    part.CommentList ??= new P188.CommentList();
    part.CommentList.Append(Comment(authorId, targetShapeId, text));
  }

  /// <summary>Removes the comment whose deck-wide id is <paramref name="commentId"/>, from
  ///     whichever shape holds it, and reports whether one was found.</summary>
  /// <param name="slides">The deck's slides in presentation order.</param>
  /// <param name="commentId">The 1-based deck-wide comment id.</param>
  internal static bool Delete(IReadOnlyList<SlidePart> slides, int commentId)
  {
    ArgumentNullException.ThrowIfNull(slides);
    int id = 0;
    foreach (SlidePart slidePart in slides)
    {
      foreach (P.Comment comment in LegacyComments(slidePart))
      {
        id++;
        if (id == commentId)
        {
          comment.Remove();
          return true;
        }
      }

      foreach (P188.Comment comment in CurrentComments(slidePart))
      {
        id++;
        if (id == commentId)
        {
          comment.Remove();
          return true;
        }
      }
    }

    return false;
  }

  /// <summary>The comment a new one is written as: a fresh id, the deck's author id, the
  ///     creation time, a marker position and the shape it anchors at.</summary>
  private static P188.Comment Comment(string authorId, uint targetShapeId, string text)
  {
    P188.Comment comment = new()
    {
      Id = Guid.NewGuid().ToString("B", CultureInfo.InvariantCulture),
      AuthorId = authorId,
      Created = new DateTimeValue(DateTime.UtcNow),
    };
    comment.Append(new P188.Point2DType { X = 0L, Y = 0L });
    comment.Append(new P188.TextBodyType(new A.Paragraph(new A.Run(new A.Text(text)))));
    comment.Append(new C.DeMkLstDrawingElementMonikerList(new C.ShapeMoniker { Id = targetShapeId }));
    return comment;
  }

  /// <summary>The deck's author id for a name: the existing entry's id when the author
  ///     list already carries the name, otherwise a freshly appended entry.</summary>
  private static string AuthorId(PresentationPart presentationPart, string author)
  {
    PowerPointAuthorsPart part = AuthorsPart(presentationPart) ?? presentationPart.AddNewPart<PowerPointAuthorsPart>();
    part.AuthorList ??= new P188.AuthorList();
    P188.Author? existing = part.AuthorList.Elements<P188.Author>()
        .FirstOrDefault(candidate => string.Equals(candidate.Name?.Value, author, StringComparison.Ordinal));
    if (existing?.Id?.Value is { } id)
    {
      return id;
    }

    string created = Guid.NewGuid().ToString("B", CultureInfo.InvariantCulture);
    part.AuthorList.Append(new P188.Author { Id = created, Name = author, Initials = Initials(author) });
    return created;
  }

  private static string Initials(string author)
    => author.Length == 0 ? string.Empty : author[..1].ToUpperInvariant();

  private static List<P.Comment> LegacyComments(SlidePart slidePart)
    => [.. slidePart.SlideCommentsPart?.CommentList?.Elements<P.Comment>() ?? []];

  private static List<P188.Comment> CurrentComments(SlidePart slidePart)
    => [.. CurrentPart(slidePart)?.CommentList?.Elements<P188.Comment>() ?? []];

  /// <summary>The slide's current comment part, reached through the slide's comment
  ///     relationship: the SDK exposes it as the slide's typed comment parts, not as a
  ///     named property.</summary>
  private static PowerPointCommentPart? CurrentPart(SlidePart slidePart)
    => slidePart.commentParts.OfType<PowerPointCommentPart>().FirstOrDefault();

  private static Dictionary<uint, string> LegacyAuthors(PresentationPart presentationPart)
  {
    Dictionary<uint, string> authors = [];
    foreach (P.CommentAuthor author in presentationPart.CommentAuthorsPart?.CommentAuthorList?.Elements<P.CommentAuthor>()
        ?? [])
    {
      authors[author.Id?.Value ?? 0] = author.Name?.Value ?? string.Empty;
    }

    return authors;
  }

  private static Dictionary<string, string> CurrentAuthors(PresentationPart presentationPart)
  {
    Dictionary<string, string> authors = [];
    foreach (P188.Author author in AuthorsPart(presentationPart)?.AuthorList?.Elements<P188.Author>() ?? [])
    {
      if (author.Id?.Value is { } id)
      {
        authors[id] = author.Name?.Value ?? string.Empty;
      }
    }

    return authors;
  }

  /// <summary>The deck's current author list part, reached from the presentation's own
  ///     parts: the SDK exposes it as a typed part on the container, not as a named
  ///     property.</summary>
  private static PowerPointAuthorsPart? AuthorsPart(PresentationPart presentationPart)
    => presentationPart.Parts.Select(pair => pair.OpenXmlPart).OfType<PowerPointAuthorsPart>().FirstOrDefault();

  /// <summary>A legacy comment's target shape id: the first spid attribute in the
  ///     comment, or the shape moniker its extension list carries when it has one.
  ///     </summary>
  private static uint? LegacyShapeIdIn(OpenXmlElement? comment)
    => IdAttribute(comment, LegacyShapeId) ?? MonikerShapeId(comment);

  /// <summary>A current comment's target shape id: the id its shape moniker carries. The
  ///     moniker is matched by element name, because the SDK parses it as an unknown
  ///     element even when the same markup is written through its own type.</summary>
  private static uint? MonikerShapeId(OpenXmlElement? comment)
    => comment?.Descendants().FirstOrDefault(element => element.LocalName == ShapeMoniker) is { } moniker
        ? IdAttribute(moniker, MonikerId)
        : null;

  private static uint? IdAttribute(OpenXmlElement? element, string name)
  {
    if (element is null)
    {
      return null;
    }

    foreach (OpenXmlElement candidate in element.Descendants().Prepend(element))
    {
      if (candidate.GetAttributes().FirstOrDefault(attribute => attribute.LocalName == name) is { } value
          && uint.TryParse(value.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint id))
      {
        return id;
      }
    }

    return null;
  }

  private static string TextOf(P188.TextBodyType? body)
    => body is null
        ? string.Empty
        : string.Join("\n", body.Descendants<A.Paragraph>().Select(paragraph =>
            string.Concat(paragraph.Descendants<A.Text>().Select(run => run.Text))));

  /// <summary>Resolves a target shape id to the address that reaches it, falling back to
  ///     the slide itself when the id names no shape on this slide.</summary>
  private sealed record Anchor(int Slide, IReadOnlyDictionary<uint, string> AddressesByShapeId)
  {
    internal string Of(uint? shapeId)
      => shapeId is { } id && AddressesByShapeId.TryGetValue(id, out string? address)
          ? address
          : PptxAddress.ForSlide(Slide).Render();
  }
}
