namespace eThangAgent.ConversationDomain;

/// <summary>THE ONE PLACE THAT DECIDES whether an image part's bytes are SENT to the provider.
///     <para>A picture is worth re-sending on the turn that produced it and worth nothing on any
///     later turn: the model has already looked at it, and every later request that carries it
///     pays for the same megabytes again. So the rule is about the TURN, not about the image's
///     size or its form.</para>
///     <para>THE TURN BOUNDARY IS READ FROM THE TRANSCRIPT, not plumbed through the request.
///     Every turn begins with <see cref="Conversation.AddUserMessage"/>, so the LAST User-role
///     message marks where the current turn starts and everything at or after it is current. A
///     transcript with no user message at all has no boundary to find and sends everything, which
///     is the pre-existing behaviour - the rule withholds an image only when it can point at the
///     user message that began the turn the image is not part of.</para>
///     <para>WHY IT IS A VALUE AND NOT A PREDICATE WITH THREE ARGUMENTS: a caller that computed
///     the boundary itself could compute it differently, and then the wire encoder, the context
///     list and the compaction render would disagree about which pictures the model sees. They all
///     hold one of these instead, so <see cref="Sends"/> is the only answer any of them can
///     reach.</para></summary>
public readonly record struct ImageResendPlan
{
  private ImageResendPlan(int turnStart) => TurnStart = turnStart;

  /// <summary>The index of the message that begins the current turn: the LAST User-role message,
  ///     or zero when the transcript holds none.</summary>
  public int TurnStart { get; }

  /// <summary>The plan for <paramref name="messages"/>: the boundary is the last User-role
  ///     message, so an image on that message or on anything after it belongs to the turn in
  ///     flight.</summary>
  /// <param name="messages">The transcript the request is built from, in order.</param>
  public static ImageResendPlan For(IReadOnlyList<Message> messages)
  {
    ArgumentNullException.ThrowIfNull(messages);
    for (int i = messages.Count - 1; i >= 0; i--)
    {
      if (messages[i].Role is Role.User)
      {
        return new ImageResendPlan(i);
      }
    }

    return new ImageResendPlan(0);
  }

  /// <summary>True when the request carries the bytes of <paramref name="part"/>, the part of the
  ///     message at <paramref name="messageIndex"/>. An inline image is carried exactly when it
  ///     belongs to the current turn; a STORED image is never carried, whatever the turn, because
  ///     a pointer has no bytes to send - the model reaches the file through its path instead. A
  ///     text part carries no image bytes and answers false.</summary>
  /// <param name="part">One content part of the message at <paramref name="messageIndex"/>.</param>
  /// <param name="messageIndex">That message's index in the list the plan was built from.</param>
  public bool Sends(MessagePart part, int messageIndex)
  {
    ArgumentNullException.ThrowIfNull(part);
    ArgumentOutOfRangeException.ThrowIfNegative(messageIndex);
    return part switch
    {
      MessagePart.ImagePart => messageIndex >= TurnStart,
      MessagePart.ImageReferencePart => false,
      _ => false,
    };
  }
}
