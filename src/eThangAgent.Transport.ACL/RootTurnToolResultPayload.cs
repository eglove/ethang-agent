using System.Text.Json;

namespace eThangAgent.Transport.ACL;

/// <summary>The JSON payload of a <c>tool</c> result event (issue #245): the tool's name,
///     its ok|error status, the rich card title (null when the tool supplied none), the
///     short summary line, and the FULL result content — exactly what entered the
///     conversation as the tool message.
/// <para>WHY THE CONTENT TRAVELS: the legacy text shape carried only the summary line,
///     whose success form is the literal "ok" — the app's tool cards rendered every
///     result as OK and the user could not see what a tool did without opening logs. The
///     payload is a JSON object (not the legacy space-delimited text) because the content
///     is arbitrary text: newlines, quotes, and separators all appear in real tool output,
///     and a delimiter-split shape would corrupt on the first one.</para></summary>
public sealed record RootTurnToolResultPayload(string Name, string Status, string? Title,
    string Summary, string Content)
{
  public const string OkStatus = "ok";
  public const string ErrorStatus = "error";

  /// <summary>Serializes one payload — the host's emission shape, and the shape tests
  ///     script. The one writer of the result shape, beside <see cref="Parse"/>'s one
  ///     reader, so the wire vocabulary cannot drift between the two sides.</summary>
  public static string Serialize(string name, string status, string? title, string summary, string content)
      => JsonSerializer.Serialize(new RootTurnToolResultPayload(name, status, title, summary, content));

  /// <summary>Parses one JSON tool-result payload, or null when it does not decode,
  ///     names no tool, or carries an unknown status token. The one reader of the result
  ///     shape - the host serializes it, the app (and tests) decode it through here, so
  ///     the wire vocabulary cannot drift between the two sides.</summary>
  public static RootTurnToolResultPayload? Parse(string payload)
  {
    ArgumentNullException.ThrowIfNull(payload);
    RootTurnToolResultPayload? parsed;
    try
    {
      parsed = JsonSerializer.Deserialize<RootTurnToolResultPayload>(payload);
    }
    catch (JsonException)
    {
      return null;
    }

    return parsed is null || string.IsNullOrEmpty(parsed.Name)
        || (parsed.Status != OkStatus && parsed.Status != ErrorStatus)
        ? null
        : parsed;
  }

  /// <summary>True when the status token names a failed tool result. Unknown status
  ///     tokens are NOT errors (strict vocabulary: only the two documented tokens are
  ///     meaningful, and an unknown one refuses the payload at the parser).</summary>
  public bool IsError => string.Equals(Status, ErrorStatus, StringComparison.Ordinal);
}
