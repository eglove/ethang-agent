namespace eThangAgent.ConversationDomain.Tests;

/// <summary>The ONE resend decision, and the context list that consults it. The wire encoder and
///     the compaction render are the other two consumers; they agree with this one because they
///     hold the same <see cref="ImageResendPlan"/>, not because three implementations were kept in
///     step by hand.
///     <para>THE TURN BOUNDARY IS THE LAST USER MESSAGE, which is where every turn begins
///     (<see cref="Conversation.AddUserMessage"/>). The checks below pin that reading, including
///     the transcript that holds no user message at all - the one case where the rule has no
///     boundary to point at and therefore withholds nothing.</para></summary>
public class ImageResendPlanTests
{
  private static readonly DateTimeOffset T = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

  private const string StoredPath = @"C:\images\ethang-agent\shot.png";

  private static Message User(string text, params MessagePart[] parts) =>
      new(Role.User, text, T, Parts: parts.Length == 0 ? null : parts);

  private static Message Assistant(string text) => new(Role.Assistant, text, T);

  // --- The boundary ---

  [Fact]
  public void TheTurnStartsAtTheLastUserMessage()
  {
    ImageResendPlan plan = ImageResendPlan.For(
    [
      User("first turn"),
      Assistant("ok"),
      User("second turn"),
      Assistant("ok again"),
    ]);

    Assert.Equal(2, plan.TurnStart);
  }

  [Fact]
  public void ATranscriptWithNoUserMessage_HasNoBoundaryToPointAt_AndWithholdsNothing()
  {
    ImageResendPlan plan = ImageResendPlan.For([Assistant("hello")]);

    Assert.Equal(0, plan.TurnStart);
  }

  [Fact]
  public void AnEmptyTranscript_IsAPlanThatWithholdsNothing() =>
      Assert.Equal(0, ImageResendPlan.For([]).TurnStart);

  // --- What the plan answers about a part ---

  [Fact]
  public void AnInlineImage_IsSentExactlyWhenItBelongsToTheCurrentTurn()
  {
    ImageResendPlan plan = ImageResendPlan.For([User("start"), Assistant("ok"), User("now")]);
    MessagePart.ImagePart image = new("image/png", "aGVsbG8=");

    Assert.False(plan.Sends(image, 0));
    Assert.False(plan.Sends(image, 1));
    Assert.True(plan.Sends(image, 2));
    Assert.True(plan.Sends(image, 3));
  }

  /// <summary>A stored image is never sent, whatever the turn: it has no bytes to send, and its
  ///     path is what the model reaches it through. A text part carries no image at all.</summary>
  [Fact]
  public void AStoredImage_IsNeverSent_AndATextPartCarriesNoImage()
  {
    ImageResendPlan plan = ImageResendPlan.For([User("now")]);

    Assert.False(plan.Sends(new MessagePart.ImageReferencePart(StoredPath, "image/png"), 0));
    Assert.False(plan.Sends(new MessagePart.TextPart("just words"), 0));
  }

  [Fact]
  public void ANegativeMessageIndex_IsAProgrammerError()
  {
    ImageResendPlan plan = ImageResendPlan.For([User("now")]);

    _ = Assert.Throws<ArgumentOutOfRangeException>(
        () => plan.Sends(new MessagePart.ImagePart("image/png", "aGVsbG8="), -1));
  }

  // --- The context list consults the same decision ---

  [Fact]
  public void List_MarksAnImageFromAnEarlierTurn_AsNotResent()
  {
    Conversation conversation = new();
    conversation.AddToolResult("c1", "computer: observe",
        [new MessagePart.ImagePart("image/png", "aGVsbG8=")]);
    conversation.AddUserMessage("carry on");

    string listing = new ConversationContextService(conversation).List();

    Assert.Contains("[image not re-sent: image/png, 5 bytes", listing, StringComparison.Ordinal);
    Assert.DoesNotContain("[image: image/png, 5 bytes]", listing, StringComparison.Ordinal);
  }

  [Fact]
  public void List_KeepsThePictureMarkerForAnImageFromTheCurrentTurn()
  {
    Conversation conversation = new();
    conversation.AddUserMessage("take a screenshot");
    conversation.AddToolResult("c1", "computer: observe",
        [new MessagePart.ImagePart("image/png", "aGVsbG8=")]);

    string listing = new ConversationContextService(conversation).List();

    Assert.Contains("[image: image/png, 5 bytes]", listing, StringComparison.Ordinal);
    Assert.DoesNotContain("not re-sent", listing, StringComparison.Ordinal);
  }

  [Fact]
  public void List_TextOnlyMessages_AreUnchanged()
  {
    Conversation conversation = new();
    conversation.AddUserMessage("plain text");
    conversation.AddAssistantMessage("the answer");

    string listing = new ConversationContextService(conversation).List();

    Assert.DoesNotContain("[image", listing, StringComparison.Ordinal);
    Assert.Contains("[1] [User] plain text", listing, StringComparison.Ordinal);
  }
}
