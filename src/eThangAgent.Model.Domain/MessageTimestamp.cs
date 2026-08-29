using eThangAgent.ConversationDomain;

namespace eThangAgent.ModelDomain;

/// <summary>
/// Renders the wall-clock time a conversation message was created as a compact prefix
/// prepended to its content in provider wire requests. Every model-bound message role
/// (system, user, assistant, tool) is stamped; per-request system prompts are not
/// messages and stay unstamped.
/// <para>
/// Format contract, verbatim: <c>[yyyy-MM-dd HH:mm:ssZ] </c> — UTC normalized, seconds
/// precision, trailing single space, prepended to the message's content exactly as-is.
/// Timestamps come from the persisted message fact, so repeated renders of the same
/// history are deterministic. Tool-call timing is message-level: an assistant
/// tool-call message's stamp is its dispatch time, a tool result message's stamp its
/// completion time.
/// </para>
/// </summary>
public static class MessageTimestamp
{
  private const string PrefixFormat = "[yyyy-MM-dd HH:mm:ssZ] ";

  /// <summary>Returns <paramref name="message"/>'s content with its UTC timestamp prefix prepended.</summary>
  public static string Stamp(Message message)
  {
    ArgumentNullException.ThrowIfNull(message);
    return message.Timestamp.ToUniversalTime().ToString(PrefixFormat, System.Globalization.CultureInfo.InvariantCulture)
        + message.Content;
  }
}
