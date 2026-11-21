using System.Text.Json;
using eThangAgent.ModelDomain;

namespace eThangAgent.Cloudflare.ACL;

/// <summary>The token accounting a Cloudflare response reports, translated into the harness's
///     usage value.
///     <para>UNREPORTED IS ABSENT, NEVER ZERO. Cloudflare reports <c>prompt_tokens</c> and
///     <c>completion_tokens</c> under <c>usage</c>, and a response that reports neither has said
///     nothing about what it cost — a fabricated zero would record a free call that was not
///     free. So a usage object that carries neither count yields null rather than a zeroed
///     value.</para>
///     <para>NO COST IS READ HERE: Cloudflare does not bill in the response, so
///     <see cref="TokenUsage.ReportedCost"/> stays null and the harness prices the call from
///     the catalog, exactly as it does for a model whose provider reports no charge.</para>
///     </summary>
internal static class CloudflareUsage
{
  /// <summary>The usage the object reports, or null when it reports no token count.
  ///     </summary>
  /// <param name="usage">The response's <c>usage</c> object.</param>
  public static TokenUsage? Read(JsonElement usage)
  {
    int? prompt = Count(usage, "prompt_tokens");
    int? completion = Count(usage, "completion_tokens");
    return prompt is null && completion is null
        ? null
        : new TokenUsage(prompt ?? 0, completion ?? 0);
  }

  /// <summary>A whole-number count member, or null when the member is absent or not a number.
  ///     </summary>
  /// <param name="usage">The usage object.</param>
  /// <param name="member">The count's member name.</param>
  private static int? Count(JsonElement usage, string member)
      => usage.TryGetProperty(member, out JsonElement value)
          && value.ValueKind == JsonValueKind.Number
          && value.TryGetInt32(out int count)
              ? count
              : null;
}
