using eThangAgent.ModelDomain;

namespace eThangAgent.Cloudflare.ACL;

/// <summary>The provider-neutral finish reason for Cloudflare's own vocabulary.
///     <para>ONE TRANSLATION FOR BOTH PATHS: the streamed frames and the single body state the
///     same words, so they read them here and cannot disagree about what a response did.
///     </para>
///     <para>AN UNRECOGNISED WORD IS <see cref="FinishReason.Unknown"/>, never a guess: the
///     loop treats an unknown finish as a completed turn, which is the honest reading of a
///     reason this build does not know.</para></summary>
internal static class CloudflareFinishReason
{
  /// <summary>The finish reason a wire value states, or <see cref="FinishReason.Unknown"/> for a
  ///     word this build does not know.</summary>
  /// <param name="wire">The wire value, or null.</param>
  public static FinishReason Of(string? wire) => wire switch
  {
    "stop" => FinishReason.Stop,
    "length" => FinishReason.Length,
    "tool_calls" => FinishReason.ToolCalls,
    "content_filter" => FinishReason.ContentFilter,
    _ => FinishReason.Unknown,
  };
}
