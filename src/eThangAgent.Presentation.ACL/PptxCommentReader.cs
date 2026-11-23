using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using eThangAgent.ToolDomain;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using P188 = DocumentFormat.OpenXml.Office2021.PowerPoint.Comment;

namespace eThangAgent.Presentation.ACL;

/// <summary>Reads a slide's comments from both shapes PowerPoint has written them in: the
///     legacy per-slide comment part (p:cmLst, whose authors resolve through the
///     presentation's legacy author list) and the current comment part (p188:cmLst, whose
///     authors resolve through the presentation's current author list). A deck carrying
///     both lists all of them.</summary>
internal static class PptxCommentReader
{
  /// <summary>The slide's comments: legacy comments first, then current ones, each with
  ///     the address of the shape it anchors to. A slide with no comment part has none.
  ///     </summary>
  /// <param name="presentationPart">The presentation part, which owns the author lists.</param>
  /// <param name="slidePart">The slide part whose comments are read.</param>
  /// <param name="slide">The slide's 1-based number.</param>
  /// <param name="addressesByShapeId">The address of every shape that carries a drawing id.</param>
  internal static IReadOnlyList<PptxCommentInfo> Read(PresentationPart presentationPart, SlidePart slidePart,
      int slide, IReadOnlyDictionary<uint, string> addressesByShapeId)
  {
    ArgumentNullException.ThrowIfNull(presentationPart);
    ArgumentNullException.ThrowIfNull(slidePart);
    ArgumentNullException.ThrowIfNull(addressesByShapeId);
    Anchor anchor = new(slide, addressesByShapeId);
    List<PptxCommentInfo> comments = [.. Legacy(presentationPart, slidePart, anchor)];
    comments.AddRange(Current(slidePart, anchor));
    return comments;
  }

  private static List<PptxCommentInfo> Legacy(PresentationPart presentationPart, SlidePart slidePart, Anchor anchor)
  {
    P.CommentList? list = slidePart.SlideCommentsPart?.CommentList;
    if (list is null)
    {
      return [];
    }

    Dictionary<uint, string> authors = LegacyAuthors(presentationPart);
    List<PptxCommentInfo> comments = [];
    foreach (P.Comment comment in list.Elements<P.Comment>())
    {
      comments.Add(new PptxCommentInfo(
          (int)(comment.Index?.Value ?? 0),
          authors.GetValueOrDefault(comment.AuthorId?.Value ?? 0, string.Empty),
          comment.Text?.InnerText ?? string.Empty,
          anchor.Of(ShapeIdIn(comment.Position)),
          true));
    }

    return comments;
  }

  private static List<PptxCommentInfo> Current(SlidePart slidePart, Anchor anchor)
  {
    P188.CommentList? list = CurrentPart(slidePart)?.CommentList;
    if (list is null)
    {
      return [];
    }

    Dictionary<string, string> authors = CurrentAuthors(slidePart);
    List<PptxCommentInfo> comments = [];
    foreach (P188.Comment comment in list.Elements<P188.Comment>())
    {
      string authorId = comment.AuthorId?.InnerText ?? string.Empty;
      comments.Add(new PptxCommentInfo(
          CommentId(comment.Id?.InnerText),
          authors.GetValueOrDefault(authorId, authorId),
          TextOf(comment.GetFirstChild<P188.TextBodyType>()),
          anchor.Of(ShapeIdIn(comment)),
          false));
    }

    return comments;
  }

  private static int CommentId(string? text)
    => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) ? id : 0;

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

  private static Dictionary<string, string> CurrentAuthors(SlidePart slidePart)
  {
    Dictionary<string, string> authors = [];
    PowerPointAuthorsPart? part = AuthorsPart(slidePart);
    foreach (P188.Author author in part?.AuthorList?.Elements<P188.Author>() ?? [])
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
  private static PowerPointAuthorsPart? AuthorsPart(SlidePart slidePart)
    => slidePart.OpenXmlPackage is PresentationDocument document
        ? document.PresentationPart?.Parts.Select(pair => pair.OpenXmlPart).OfType<PowerPointAuthorsPart>().FirstOrDefault()
        : null;

  /// <summary>A comment's target shape id: the first spid attribute in the comment's
  ///     anchor element. Both comment shapes name the shape they are attached to that way,
  ///     through different moniker lists, so the attribute is read rather than the type.
  ///     </summary>
  private static uint? ShapeIdIn(OpenXmlElement? element)
  {
    if (element is null)
    {
      return null;
    }

    foreach (OpenXmlElement candidate in element.Descendants())
    {
      if (candidate.GetAttributes().FirstOrDefault(attribute => attribute.LocalName == "spid") is { } value
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
