using eThangAgent.ConversationDomain;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>The compaction handoff render is the SECOND of the three consumers of the resend
///     decision (<see cref="ImageResendPlan"/>); the wire encoder and the context list are the
///     other two. Each check here pins the same two facts the wire suite pins: an image from an
///     earlier turn renders as the marker rather than as a picture, and an image from the current
///     turn still renders as one.
///     <para>WHY BOTH RENDERERS NEED THIS. Compaction is not the request path: the summarizer is a
///     different model reading a different render, so a rule that lived only in the wire encoder
///     would leave this one describing pictures the request no longer carries. The two agree
///     because they consult one plan, not because two implementations were kept in step.</para></summary>
public class ImageResendRenderTests
{
  private static readonly DateTimeOffset T = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

  private const string StoredPath = @"C:\images\ethang-agent\shot.png";

  private static Message Tool(string text, params MessagePart[] parts) =>
      new(Role.Tool, text, T, ToolCallId: "c1", Parts: parts);

  private static Message User(string text, params MessagePart[] parts) =>
      new(Role.User, text, T, Parts: parts.Length == 0 ? null : parts);

  // --- An earlier turn's image: the marker, not the picture ---

  [Fact]
  public void CompactorRender_ImageFromAnEarlierTurn_BecomesTheNotResentMarker()
  {
    string rendered = DefaultContextCompactor.Render(
    [
      Tool("computer: observe", new MessagePart.ImagePart("image/png", "b2xkZXI=")),
      User("carry on"),
    ],
    []);

    Assert.Contains("[image not re-sent: image/png, 5 bytes", rendered, StringComparison.Ordinal);
    Assert.DoesNotContain("aGVsbG8=", rendered, StringComparison.Ordinal);
    Assert.DoesNotContain("[image: image/png, 5 bytes]", rendered, StringComparison.Ordinal);
  }

  // --- The current turn's image: still a picture ---

  [Fact]
  public void CompactorRender_ImageFromTheCurrentTurn_KeepsItsPictureMarker()
  {
    string rendered = DefaultContextCompactor.Render(
    [
      User("take a screenshot"),
      Tool("computer: observe", new MessagePart.ImagePart("image/png", "aGVsbG8=")),
    ],
    []);

    Assert.Contains("[image: image/png, 5 bytes]", rendered, StringComparison.Ordinal);
    Assert.DoesNotContain("not re-sent", rendered, StringComparison.Ordinal);
  }

  /// <summary>The KEPT TAIL is offset into the same plan as the evicted prefix: a kept message's
  ///     index is its index in the concatenation the plan was built over, so an image that moved
  ///     from the prefix to the tail cannot change its answer depending on which list it is
  ///     rendered from.</summary>
  [Fact]
  public void CompactorRender_JudgesTheKeptTailAtItsOwnIndex_NotFromZero()
  {
    // The kept tail is what remains AFTER the last user message, so its image is current.
    string rendered = DefaultContextCompactor.Render(
    [User("older")],
    [
      User("take a screenshot"),
      Tool("computer: observe", new MessagePart.ImagePart("image/png", "aGVsbG8=")),
    ]);

    Assert.Contains("[image: image/png, 5 bytes]", rendered, StringComparison.Ordinal);
  }

  /// <summary>THE DISCRIMINATING CASE: an image sits BETWEEN two user messages, so the answer
  ///     depends on WHICH user message begins the turn. The last one does, so the picture is from
  ///     an earlier turn and is withheld; a renderer that picked any other boundary would send it.
  ///     <para>Without a case shaped like this the compactor's checks would pass under a wrong
  ///     boundary too - they would be describing the marker, not the decision.</para></summary>
  [Fact]
  public void CompactorRender_ImageBetweenTwoUserMessages_IsWithheld()
  {
    string rendered = DefaultContextCompactor.Render(
    [
      User("first turn"),
      Tool("computer: observe", new MessagePart.ImagePart("image/png", "aGVsbG8=")),
    ],
    [User("second turn")]);

    Assert.Contains("[image not re-sent: image/png, 5 bytes", rendered, StringComparison.Ordinal);
    Assert.DoesNotContain("[image: image/png, 5 bytes]", rendered, StringComparison.Ordinal);
  }

  // --- A stored reference is a pointer at every renderer ---

  [Fact]
  public void CompactorRender_StoredReferenceOnTheCurrentTurn_StillNamesItsPath()
  {
    string rendered = DefaultContextCompactor.Render(
    [
      User("carry on"),
      Tool("computer: observe", new MessagePart.ImageReferencePart(StoredPath, "image/png")),
    ],
    []);

    Assert.Contains("[image: image/png, " + StoredPath + "]", rendered, StringComparison.Ordinal);
  }

  // --- A transcript with no images renders exactly as it did ---

  [Fact]
  public void CompactorRender_TextOnlyMessages_AreUnchanged()
  {
    string rendered = DefaultContextCompactor.Render(
    [User("do the thing"), Tool("file contents")],
    [User("next")]);

    Assert.Contains("[User] do the thing", rendered, StringComparison.Ordinal);
    Assert.Contains("[Tool] file contents [answers c1]", rendered, StringComparison.Ordinal);
    Assert.DoesNotContain("[image", rendered, StringComparison.Ordinal);
  }
}
