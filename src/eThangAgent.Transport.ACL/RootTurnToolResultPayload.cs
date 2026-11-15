using System.Text;
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
///     and a delimiter-split shape would corrupt on the first one.</para>
/// <para>WHY THE IMAGES TRAVEL (issue #230): every conversation runs in a host process,
///     so an image attached to a tool result that does not ride this payload can never be
///     rendered — the computer screenshot path and the generated-image artifact both went
///     dark in production for exactly that reason. Absent on the wire means an empty list,
///     so a payload from an older host still parses.</para></summary>
public sealed record RootTurnToolResultPayload(string Name, string Status, string? Title,
    string Summary, string Content)
{
  public const string OkStatus = "ok";
  public const string ErrorStatus = "error";

  /// <summary>The images the tool attached to its result, empty when none. Never null:
  ///     an absent member (an older host's payload) reads as empty.</summary>
  public IReadOnlyList<RootTurnImage> Images { get; init; } = [];

  /// <summary>Serializes one payload — the host's emission shape, and the shape tests
  ///     script. The one writer of the result shape, beside <see cref="Parse"/>'s one
  ///     reader, so the wire vocabulary cannot drift between the two sides.
  /// <para>The result is held to <see cref="FramedPayloadBudget.MaxPayloadBytes"/> because
  ///     the app and the host are joined by a frame seam that refuses a larger payload
  ///     outright: text is shortened first (content, then summary, then title, to
  ///     <see cref="FramedPayloadBudget.MaxTextBytes"/> together), and any image that still
  ///     does not fit is dropped whole from the end, its count carried in the summary. The
  ///     members are those of the record built here and no others, so every reader
  ///     deserializes the same shape whether or not the budget ran. A payload within the
  ///     budget is serialized from the values it arrived with, byte for byte.</para></summary>
  public static string Serialize(string name, string status, string? title, string summary, string content,
      IReadOnlyList<RootTurnImage>? images = null)
  {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(status);
    ArgumentNullException.ThrowIfNull(summary);
    ArgumentNullException.ThrowIfNull(content);
    (string framedContent, string framedSummary, string? framedTitle) =
        FramedPayloadBudget.FrameText(content, summary, title);
    IReadOnlyList<RootTurnImage> framed = images ?? [];
    int kept = framed.Count;
    while (true)
    {
      int dropped = framed.Count - kept;
      string noticeSummary = FramedPayloadBudget.WithImageNotice(
          framedContent, framedSummary, framedTitle, dropped);
      // The measured string IS the result: a payload within the budget is serialized once,
      // exactly as it was before the budget existed.
      string json = JsonSerializer.Serialize(
          new RootTurnToolResultPayload(name, status, framedTitle, noticeSummary, framedContent)
          {
            Images = [.. framed.Take(kept)],
          });
      if (kept == 0
          || Encoding.UTF8.GetByteCount(json) <= FramedPayloadBudget.MaxPayloadBytes)
      {
        return json;
      }

      kept--;
    }
  }

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
