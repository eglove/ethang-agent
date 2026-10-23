using System.Text.Json;
using eThangAgent.Transport.ACL;

namespace eThangAgent.Desktop.ViewModels;

/// <summary>One parsed `tool` wire payload from a conversation host (spec D6). The host's
///     tool kind carries two shapes, documented verbatim by RootRunHost:
///     <c>call &lt;name&gt; &lt;n&gt;/&lt;total&gt; &lt;arguments&gt;</c> and a JSON
///     <see cref="RootTurnToolResultPayload"/> object. Parsing lives here so the
///     view-model's event mapping stays a switch and the wire vocabulary has exactly one
///     reader.
/// <para>The result shape is JSON (issue #245): the legacy text shape carried only the
///     summary line, so the app showed "ok" instead of what the tool did. The parser
///     still accepts the legacy text result shape so an older host cannot crash the
///     session — its summary renders as before, with no content.</para></summary>
/// <param name="Name">The tool name.</param>
/// <param name="Arguments">The call's arguments JSON; empty for a result.</param>
/// <param name="Summary">The result's summary line; empty for a call.</param>
/// <param name="Content">The result's full content; null for a call or a legacy payload
///     that carried none.</param>
/// <param name="Title">The rich card title; null when the tool supplied none.</param>
/// <param name="IsResult">True for a result payload, false for a call payload.</param>
/// <param name="IsError">True when the result reports a tool failure.</param>
internal sealed record RootTurnToolPayload(string Name, string Arguments, string Summary,
    bool IsResult, bool IsError, string? Content = null, string? Title = null)
{
  private const string CallPrefix = "call ";
  private const string ResultPrefix = "result ";

  /// <summary>Parses one tool payload, or returns null when it matches neither documented
  ///     shape. Null is NOT an error the caller swallows: an unreadable tool event is
  ///     surfaced as a notice, because dropping it would hide a whole tool step.</summary>
  public static RootTurnToolPayload? Parse(string payload)
  {
    ArgumentNullException.ThrowIfNull(payload);
    // The JSON result shape starts with '{': try it first, so a tool named 'result' or
    // 'call' cannot be misread by the legacy text branches.
    return payload.StartsWith('{')
        ? ParseJsonResult(payload)
        : payload switch
        {
          _ when payload.StartsWith(CallPrefix, StringComparison.Ordinal)
              => ParseCall(payload[CallPrefix.Length..]),
          _ when payload.StartsWith(ResultPrefix, StringComparison.Ordinal)
              => ParseResult(payload[ResultPrefix.Length..]),
          _ => null,
        };
  }

  /// <summary>&lt;name&gt; &lt;n&gt;/&lt;total&gt; &lt;arguments&gt; - the arguments are
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

  /// <summary>The JSON result shape: a RootTurnToolResultPayload object. A payload that
  ///     does not decode, or whose status token is neither documented value, is null —
  ///     the caller surfaces it as a notice rather than rendering a wrong card.</summary>
  private static RootTurnToolPayload? ParseJsonResult(string payload)
  {
    RootTurnToolResultPayload? parsed;
    try
    {
      parsed = JsonSerializer.Deserialize<RootTurnToolResultPayload>(payload);
    }
    catch (JsonException)
    {
      return null;
    }

    return parsed is null
        || string.IsNullOrEmpty(parsed.Name)
        || (parsed.Status != RootTurnToolResultPayload.OkStatus
            && parsed.Status != RootTurnToolResultPayload.ErrorStatus)
        ? null
        : new RootTurnToolPayload(parsed.Name, string.Empty, parsed.Summary,
            IsResult: true, IsError: parsed.IsError, Content: parsed.Content, Title: parsed.Title);
  }

  /// <summary>The LEGACY text result shape &lt;name&gt; ok|error &lt;summary&gt; - a summary
  ///     may be empty. Kept so an older host still renders (summary only, no content).
  ///     A payload whose name is itself 'result'-prefixed JSON is unreachable: the JSON
  ///     branch runs first.</summary>
  private static RootTurnToolPayload? ParseResult(string body)
  {
    int nameEnd = body.IndexOf(' ', StringComparison.Ordinal);
    if (nameEnd <= 0)
    {
      return null;
    }

    int statusEnd = body.IndexOf(' ', nameEnd + 1);
    string status = statusEnd < 0 ? body[(nameEnd + 1)..] : body[(nameEnd + 1)..statusEnd];
    bool isError = string.Equals(status, "error", StringComparison.Ordinal);
    if (!isError && !string.Equals(status, "ok", StringComparison.Ordinal))
    {
      return null;
    }

    string summary = statusEnd < 0 ? string.Empty : body[(statusEnd + 1)..];
    return new RootTurnToolPayload(body[..nameEnd], string.Empty, summary,
        IsResult: true, IsError: isError);
  }
}
