using eThangAgent.ToolDomain;

namespace eThangAgent.ConversationDomain;

/// <summary>Projects a message's parts into the render vocabulary the shared parts renderer
///     consumes (<see cref="ImagePartMarker"/>): an inline image carrying its payload, a STORED
///     image naming its file, or text. One projection for every secondary parts consumer - the
///     compactor's LLM handoff render and the context list the context_edit tool shows - so the
///     two cannot render the same part two different ways.
///     <para>AN IMAGE THE REQUEST DOES NOT RE-SEND PROJECTS AS TEXT, not as an image with a
///     payload: <see cref="ImageResendPlan"/> decides, and the answer it gives is the marker
///     <see cref="ImagePartMarker.MarkerForNotResent"/> spells. That is what makes the withheld
///     case identical at every consumer - the render vocabulary has no way to carry a picture the
///     wire is not sending, so no consumer can accidentally send one.</para></summary>
public static class PartRenders
{
  /// <summary>The render projection of <paramref name="parts"/>, in order, under the resend
  ///     decision <paramref name="plan"/> makes for the message at
  ///     <paramref name="messageIndex"/>.</summary>
  /// <param name="parts">A message's content parts.</param>
  /// <param name="plan">The resend decision for the transcript these parts belong to.</param>
  /// <param name="messageIndex">The index of this message in that transcript.</param>
  public static IReadOnlyList<PartRender> Of(IReadOnlyList<MessagePart> parts, ImageResendPlan plan,
      int messageIndex)
  {
    ArgumentNullException.ThrowIfNull(parts);
    List<PartRender> renders = [];
    foreach (MessagePart part in parts)
    {
      renders.Add(part switch
      {
        MessagePart.ImagePart image when plan.Sends(image, messageIndex) =>
            PartRender.ForImage(image.MediaType, image.Base64Data),
        MessagePart.ImagePart withheld => PartRender.ForText(ImagePartMarker.MarkerForNotResent(
            withheld.MediaType, ImagePartMarker.DecodedByteCount(withheld.Base64Data))),
        MessagePart.ImageReferencePart stored => PartRender.ForStoredImage(stored.MediaType, stored.Path),
        MessagePart.TextPart text => PartRender.ForText(text.Text),
        _ => throw new InvalidOperationException(
            $"message part type {part.GetType().Name} has no rendered form."),
      });
    }

    return renders;
  }
}
