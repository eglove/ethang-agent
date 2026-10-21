namespace eThangAgent.Desktop.ViewModels;

/// <summary>One parsed `tool` wire payload from a conversation host (spec D6). The host's
///     tool kind carries two shapes, documented verbatim by RootRunHost:
///     <c>call &lt;name&gt; &lt;n&gt;/&lt;total&gt; &lt;arguments&gt;</c> and
///     <c>result &lt;name&gt; ok|error &lt;summary&gt;</c>. Parsing lives here so the view-model's
///     event mapping stays a switch and the wire vocabulary has exactly one reader.</summary>
/// <param name="Name">The tool name.</param>
/// <param name="Arguments">The call's arguments JSON; empty for a result.</param>
/// <param name="Summary">The result's summary line; empty for a call.</param>
/// <param name="IsResult">True for a result payload, false for a call payload.</param>
/// <param name="IsError">True when the result reports a tool failure.</param>
internal sealed record RootTurnToolPayload(string Name, string Arguments, string Summary,
    bool IsResult, bool IsError)
{
  private const string CallPrefix = "call ";
  private const string ResultPrefix = "result ";
  private const string OkStatus = "ok";
  private const string ErrorStatus = "error";

  /// <summary>Parses one tool payload, or returns null when it matches neither documented
  ///     shape. Null is NOT an error the caller swallows: an unreadable tool event is
  ///     surfaced as a notice, because dropping it would hide a whole tool step.</summary>
  public static RootTurnToolPayload? Parse(string payload)
  {
    ArgumentNullException.ThrowIfNull(payload);
    return payload switch
    {
      _ when payload.StartsWith(CallPrefix, StringComparison.Ordinal)
          => ParseCall(payload[CallPrefix.Length..]),
      _ when payload.StartsWith(ResultPrefix, StringComparison.Ordinal)
          => ParseResult(payload[ResultPrefix.Length..]),
      _ => null,
    };
  }

  /// <summary><c>&lt;name&gt; &lt;n&gt;/&lt;total&gt; &lt;arguments&gt;</c> — the arguments are
  ///     everything after the position token, spaces included, because they are JSON.
  ///     The position token is the structural marker separating name from arguments, so a
  ///     payload without one is not this shape.</summary>
  private static RootTurnToolPayload? ParseCall(string body)
  {
    int nameEnd = body.IndexOf(' ', StringComparison.Ordinal);
    int positionEnd = nameEnd < 0 ? -1 : body.IndexOf(' ', nameEnd + 1);
    if (nameEnd <= 0 || positionEnd < 0
        || !body[(nameEnd + 1)..positionEnd].Contains('/', StringComparison.Ordinal))
    {
      return null;
    }

    string arguments = body[(positionEnd + 1)..];
    return new RootTurnToolPayload(body[..nameEnd], arguments, "",
        IsResult: false, IsError: false);
  }

  /// <summary><c>&lt;name&gt; ok|error &lt;summary&gt;</c> — a summary may be empty.</summary>
  private static RootTurnToolPayload? ParseResult(string body)
  {
    int nameEnd = body.IndexOf(' ', StringComparison.Ordinal);
    if (nameEnd <= 0)
    {
      return null;
    }

    int statusEnd = body.IndexOf(' ', nameEnd + 1);
    string status = statusEnd < 0 ? body[(nameEnd + 1)..] : body[(nameEnd + 1)..statusEnd];
    bool isError = string.Equals(status, ErrorStatus, StringComparison.Ordinal);
    if (!isError && !string.Equals(status, OkStatus, StringComparison.Ordinal))
    {
      return null;
    }

    string summary = statusEnd < 0 ? string.Empty : body[(statusEnd + 1)..];
    return new RootTurnToolPayload(body[..nameEnd], string.Empty, summary,
        IsResult: true, IsError: isError);
  }
}
