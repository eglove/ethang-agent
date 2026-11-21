using System.Text.Json;

namespace eThangAgent.Cloudflare.ACL;

/// <summary>How Cloudflare words a refusal, read from its own error envelope.
///     <para>THE ENVELOPE IS THE MESSAGE: Cloudflare answers a failed call with
///     <c>{"success":false,"errors":[{"code":…,"message":"…"}]}</c>, and those messages name the
///     fault the user has to act on — a token that was not accepted, an allocation that is spent,
///     a model the account cannot run. A surface that showed a status code instead would tell the
///     user nothing they can act on.</para>
///     <para>ONE COPY PER ACL: the catalog and the chat provider both quote a refusal, and they
///     must quote it the same way and cap it at the same length, so both read it here.</para>
///     <para>A BODY THAT IS NOT THE ENVELOPE IS QUOTED AS IT ARRIVED, trimmed and capped: a
///     provider's own text is still the message even when it is not the shape we expected.</para>
///     </summary>
internal static class CloudflareErrorText
{
  /// <summary>The longest error text quoted back, so a long response cannot flood a transcript.
  ///     </summary>
  public const int MaxCharacters = 512;

  /// <summary>The text Cloudflare's error envelope carries, or the body itself when it carries
  ///     none, capped.</summary>
  /// <param name="body">The response body.</param>
  public static string Of(string body)
  {
    ArgumentNullException.ThrowIfNull(body);
    string text = Messages(body) ?? body;
    string trimmed = text.Trim();
    return trimmed.Length <= MaxCharacters ? trimmed : trimmed[..MaxCharacters] + "…";
  }

  /// <summary>The joined <c>errors[].message</c> texts, or null when the body carries none.
  ///     </summary>
  /// <param name="body">The response body.</param>
  private static string? Messages(string body)
  {
    try
    {
      using JsonDocument document = JsonDocument.Parse(body);
      if (document.RootElement.ValueKind != JsonValueKind.Object
          || !document.RootElement.TryGetProperty("errors", out JsonElement errors)
          || errors.ValueKind != JsonValueKind.Array)
      {
        return null;
      }

      string[] messages =
      [
        .. errors.EnumerateArray()
          .Select(Message)
          .Where(message => message is not null)
          .Select(message => message!),
      ];
      return messages.Length > 0 ? string.Join("; ", messages) : null;
    }
    catch (JsonException)
    {
      return null;
    }
  }

  /// <summary>One envelope error's message text, or null when it carries none.</summary>
  /// <param name="error">One element of the envelope's <c>errors</c> array.</param>
  private static string? Message(JsonElement error)
      => error.ValueKind == JsonValueKind.Object
          && error.TryGetProperty("message", out JsonElement message)
          && message.ValueKind == JsonValueKind.String
          && message.GetString() is { } text
          && !string.IsNullOrWhiteSpace(text)
              ? text
              : null;
}
