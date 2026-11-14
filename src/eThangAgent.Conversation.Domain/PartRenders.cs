using eThangAgent.ToolDomain;

namespace eThangAgent.ConversationDomain;

/// <summary>Projects a message's parts into the render vocabulary the shared parts renderer
///     consumes (<see cref="ImagePartMarker"/>): an inline image carrying its payload, a STORED
///     image naming its file, or text. One projection for every secondary parts consumer - the
///     compactor's LLM handoff render and the context list the context_edit tool shows - so the
///     two cannot render the same part two different ways.</summary>
public static class PartRenders
{
  /// <summary>The render projection of <paramref name="parts"/>, in order.</summary>
  /// <param name="parts">A message's content parts.</param>
  public static IReadOnlyList<PartRender> Of(IReadOnlyList<MessagePart> parts)
  {
    ArgumentNullException.ThrowIfNull(parts);
    List<PartRender> renders = [];
    foreach (MessagePart part in parts)
    {
      renders.Add(part switch
      {
        MessagePart.ImagePart image => PartRender.ForImage(image.MediaType, image.Base64Data),
        MessagePart.ImageReferencePart stored => PartRender.ForStoredImage(stored.MediaType, stored.Path),
        MessagePart.TextPart text => PartRender.ForText(text.Text),
        _ => throw new InvalidOperationException(
            $"message part type {part.GetType().Name} has no rendered form."),
      });
    }

    return renders;
  }
}
